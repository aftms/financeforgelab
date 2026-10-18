using System.Text;
using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed record AssistantActionReceipt(string Message,long[] TransactionIds,long[] EvidenceIds,long[] ImportIds,string Url)
{
    public long[] AttachmentIds{get;init;}=[];
    public ReviewSnapshot? Review{get;init;}
    public List<ReviewNote> ReviewNotes{get;init;}=[];
}

public sealed class AssistantActions(Func<FinanceDb> open,FinanceService finance,BankImportService imports,ReimbursementService reimbursements,AssistantService assistant,IAssistantSourceReader? sourceReader=null)
{
    public static readonly HashSet<string> Types=["purchase","bank_stage","bank_confirm","movement","classify","allocation","advance","reference","source_fetch","source_record","source_cursor","review_note","evidence"];
    private static T Data<T>(AssistantAction a)=>a.Data.Deserialize<T>(AssistantService.Json)??throw new FinanceRuleException("Dados da operação em falta.");
    public async Task<AssistantActionReceipt> ExecuteAsync(string requestId,int round,AssistantAction action)
    {
        if(action.Key.StartsWith("system:",StringComparison.Ordinal)||!Types.Contains(action.Type))throw new FinanceRuleException("Operação reservada ou não suportada.");
        var payload=action.Data.GetRawText();AssistantOperation operation;
        await using(var db=open())
        {
            operation=await db.AssistantOperations.SingleOrDefaultAsync(x=>x.RequestId==requestId&&x.Key==action.Key)??new(){RequestId=requestId,Round=round,Key=action.Key,Type=action.Type,PayloadJson=payload};
            if(operation.Type!=action.Type||operation.PayloadJson!=payload)throw new FinanceRuleException("Uma chave de operação foi reutilizada com dados diferentes.");
            if(operation.Status=="Completed")return JsonSerializer.Deserialize<AssistantActionReceipt>(operation.ResultJson,AssistantService.Json)!;
            if(operation.Status=="Rejected")throw new FinanceRuleException(operation.Message);
            if(operation.Id==0){db.Add(operation);await db.SaveChangesAsync();}
        }
        AssistantActionReceipt result;
        try {result=await ApplyAsync(requestId,operation,action);}
        catch(FinanceRuleException ex)
        {
            await using var db=open();var stored=await db.AssistantOperations.SingleAsync(x=>x.Id==operation.Id);stored.Status="Rejected";stored.Message=ex.Message;await db.SaveChangesAsync();throw;
        }
        await using(var db=open())
        {
            var stored=await db.AssistantOperations.SingleAsync(x=>x.Id==operation.Id);stored.Status="Completed";stored.Message=result.Message;stored.ResultJson=JsonSerializer.Serialize(result,AssistantService.Json);
            db.Add(new AuditChange{Entity="AssistantRequest",EntityId=operation.Id,Action="ApplyOperation",Actor="Owner/Assistant",CreatedUtc=DateTime.UtcNow,DetailJson=JsonSerializer.Serialize(new{requestId,operation.Key,operation.Type,result})});
            await db.SaveChangesAsync();
        }
        return result;
    }
    private async Task<AssistantActionReceipt> ApplyAsync(string requestId,AssistantOperation op,AssistantAction action)
    {
        switch(action.Type)
        {
            case "review_note":
            {
                var d=Data<ReviewNotesAction>(action);var detail=await assistant.DetailAsync(requestId);
                if(detail.Request.Workflow!="review"||d.Items is null||d.Items.Count is 0 or >200)throw new FinanceRuleException("Notas de revisão inválidas.");
                var inventory=AssistantService.LatestReview(detail.Operations)?.Issues.Select(i=>i.Key).ToHashSet()??[];
                if(d.Items.Any(n=>n is null||n.Key is null||!inventory.Contains(n.Key)||string.IsNullOrWhiteSpace(n.Reason)||n.Reason.Length>2000))throw new FinanceRuleException("Indica uma pendência do inventário e o motivo concreto por resolver.");
                return new($"{d.Items.Count} pendências analisadas; motivos guardados, sem marcar os dados como corrigidos.",[],[],[],"/assistente/"+requestId){ReviewNotes=d.Items};
            }
            case "evidence":
            {
                var d=Data<EvidenceAction>(action);await using var db=open();
                var e=await db.PurchaseEvidence.AsNoTracking().SingleOrDefaultAsync(e=>e.Id==d.EvidenceId)??throw new FinanceRuleException("Compra não encontrada.");
                if(e.TransactionId is {} recorded)
                {
                    if(d.ExistingTransactionId is {} other&&other!=recorded)throw new FinanceRuleException("A compra já está associada a outro movimento.");
                    if(!await db.Transactions.AnyAsync(t=>t.Id==recorded&&t.Status=="Confirmed")||e.HasSourceConflict)throw new FinanceRuleException("A associação existente precisa de revisão; o movimento foi anulado ou as fontes divergem.");
                    if(d.AccountId is {} known&&e.AccountId!=known)throw new FinanceRuleException("A conta da compra existente não corresponde. Confere a ligação bancária.");
                    return new($"Compra #{e.Id} já associada ao movimento #{recorded}; não foi criada outra despesa.",[recorded],[e.Id],[],"/compras");
                }
                if(e.Version!=d.Version)throw new FinanceRuleException("A compra foi atualizada. Consulta a versão atual.");
                if(e.HasSourceConflict)throw new FinanceRuleException("As fontes da compra divergem; esclarece o conflito antes de confirmar.");
                long transaction;
                if(d.ExistingTransactionId is {} existing)
                {
                    if(d.AccountId is {} account&&await db.Postings.AnyAsync(p=>p.TransactionId==existing&&p.AccountId!=null&&p.AccountId!=account))throw new FinanceRuleException("O movimento pertence a outra conta.");
                    await imports.LinkEvidenceAsync(e.Id,e.Version,existing);transaction=existing;
                }
                else
                {
                    var matches=(await imports.EvidenceMatchesAsync([e.Id])).GetValueOrDefault(e.Id)??[];
                    if(matches.Count>0)throw new FinanceRuleException("Há correspondências para a compra; confere antes de criar outra despesa.");
                    var parts=PurchaseAllocationMath.Read(e.CategoryAllocationsJson,e.Units??0,e.Units??0);
                    transaction=await imports.ConfirmEvidenceMovementAsync(e.Id,e.Version,d.AccountId,d.CategoryId??e.CategoryId??parts.FirstOrDefault()?.CategoryId??0,true,false);
                }
                return new($"Compra #{e.Id} associada ao movimento #{transaction}.",[transaction],[e.Id],[],"/compras");
            }
            case "source_fetch":
            {
                if(sourceReader is null)throw new FinanceRuleException("Leitura das fontes não configurada.");
                var source=await sourceReader.ReadAsync(Data<AssistantSourceInput>(action));
                var fileId=await assistant.AddSourceAsync(requestId,op.Round,source);
                return new($"Fonte guardada para análise: anexo #{fileId}. Consulta-o na próxima etapa.",[],[],[], $"/api/v1/assistant/{requestId}/attachments/{fileId}"){AttachmentIds=[fileId]};
            }
            case "source_record":
            {
                var d=Data<SourceRecordAction>(action);await assistant.RecordSourceAsync(requestId,d.AttachmentId,d.Status,d.Note,d.OperationKeys??[],d.EvidenceIds??[],d.ImportIds??[]);
                return new("Memória da fonte atualizada: "+d.Note,[],[],[],"/assistente");
            }
            case "source_cursor":
            {
                var d=Data<SourceCursorAction>(action);await assistant.SaveCursorAsync(requestId,d.Kind,d.Scope,d.Cursor);
                return new("Ponto de retoma guardado para "+d.Scope,[],[],[],"/assistente");
            }
            case "purchase":
            {
                var d=Data<PurchaseAction>(action);var e=d.Evidence??throw new FinanceRuleException("Faltam os dados da compra.");
                // Source identity belongs to the application, never to the model.
                string source;
                if(d.AttachmentId is {} fileId){var file=await assistant.FileAsync(requestId,fileId);source=file.Sha256;}
                else
                {
                    var detail=await assistant.DetailAsync(requestId);
                    source=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',detail.Turns.Select(t=>t.UserText))))).ToLowerInvariant();
                }
                if(string.IsNullOrWhiteSpace(d.ItemKey)||d.ItemKey.Length>80)throw new FinanceRuleException("Falta uma referência estável do artigo/documento.");
                e.SourceKey=$"assistant:{source}:{d.ItemKey}";e.Source=$"Assistente · pedido {requestId}";e.Email=null;
                if(d.AttachmentId is {} sourceFile)
                {var file=await assistant.FileAsync(requestId,sourceFile);if(file.SourceCacheId is {} cacheId){await using var originDb=open();var origin=await originDb.AssistantSources.SingleAsync(s=>s.Id==cacheId);e.Source=$"{(origin.Kind=="email"?"Email":"Documento")} · {origin.Scope} · {origin.SourceId}";if(origin.Kind=="email")e.Email=EmailProvenance.FromRetrieved(file.Content);}}
                var id=(await imports.StageEvidenceAsync([e])).Single();
                if(d.AttachmentId is {} attachment)
                {var file=await assistant.FileAsync(requestId,attachment);if(file.ContentType=="application/pdf")await imports.AttachEvidencePdfAsync(e.SourceKey,file.Name,file.Content);}
                await using var db=open();var current=await db.PurchaseEvidence.AsNoTracking().SingleAsync(x=>x.Id==id);
                if(current.HasSourceConflict)throw new FinanceRuleException($"A compra #{id} ficou guardada, mas os documentos divergem. É preciso esclarecer os dados.");
                long? transaction=current.TransactionId;
                if(d.AccountId is {} knownAccount&&transaction is {} knownTransaction&&await db.Postings.AnyAsync(p=>p.TransactionId==knownTransaction&&p.AccountId!=null&&p.AccountId!=knownAccount))
                    throw new FinanceRuleException("A compra existente pertence a outra conta. Confere o titular e os documentos.");
                if(d.ExistingTransactionId is {} existing)
                {
                    if(d.AccountId is {} account&&await db.Postings.AnyAsync(p=>p.TransactionId==existing&&p.AccountId!=null&&p.AccountId!=account))throw new FinanceRuleException("O movimento indicado pertence a outra conta.");
                    if(transaction!=existing)await imports.LinkEvidenceAsync(id,current.Version,existing);
                    transaction=existing;
                }
                else if(transaction is null&&(current.FinancialUnits??current.Units)>0&&d.CreateMovement)
                {
                    var matches=(await imports.EvidenceMatchesAsync([id])).GetValueOrDefault(id)??[];
                    if(matches.Count>0)throw new FinanceRuleException($"Compra #{id} guardada. Há correspondências a conferir antes de criar outra despesa.");
                    transaction=await imports.ConfirmEvidenceMovementAsync(id,current.Version,d.AccountId,current.CategoryId??0,true,false);
                }
                return new(transaction is {} t?$"Compra #{id} associada ao movimento #{t}.":$"Compra #{id} guardada sem novo movimento financeiro.",transaction is {} tx?[tx]:[],[id],[],"/compras");
            }
            case "bank_stage":
            {
                var d=Data<StageAction>(action);List<long> ids;
                if(d.AttachmentId is {} fileId)
                {
                    var file=await assistant.FileAsync(requestId,fileId);
                    ids=file.ContentType=="application/pdf"?await imports.StageAsync(file.Name,file.Content):file.ContentType.StartsWith("text/")?await imports.StageOnlineCopyAsync(file.Name,file.Content):throw new FinanceRuleException("Um extrato em imagem precisa de leitura e conferência adicionais; não foi importado como PDF.");
                }
                else
                {
                    var detail=await assistant.DetailAsync(requestId);var text=detail.Turns.Single(t=>t.Round==d.TextRound).UserText;
                    ids=await imports.StageOnlineCopyAsync($"copia-{requestId}-{d.TextRound}.txt",Encoding.UTF8.GetBytes(text));
                }
                return new($"Extrato preparado: {string.Join(", ",ids.Select(id=>$"#{id}"))}. Os movimentos ainda precisam de confirmação.",[],[],ids.ToArray(),"/extratos");
            }
            case "bank_confirm":
            {
                var d=Data<BankConfirmAction>(action);await using var db=open();
                var entries=await db.BankImportEntries.Where(x=>x.ImportId==d.ImportId&&d.Rows.Select(r=>r.Ordinal).Contains(x.Ordinal)).ToListAsync();
                if(d.Rows.Count>0&&entries.Select(x=>x.Ordinal).Distinct().Count()==d.Rows.Count)
                    return new("Linhas já associadas; não foram criados novos movimentos.",entries.Select(x=>x.TransactionId).Distinct().ToArray(),[],[d.ImportId],"/extratos");
                var review=await imports.PrepareReviewAsync(d.ImportId);
                if(review.Version!=d.Version)throw new FinanceRuleException("O extrato foi atualizado. Analisa a revisão atual antes de confirmar.");
                if(d.Rows.Count is 0 or >100||d.Rows.Any(x=>x.AcknowledgeDuplicate||x.Decision is not ("Import" or "Link")))throw new FinanceRuleException("Aprovação automática inválida: os duplicados precisam de uma associação concreta.");
                foreach(var row in d.Rows){var index=review.Rows.FindIndex(x=>x.Ordinal==row.Ordinal);if(index<0)throw new FinanceRuleException("Linha não encontrada.");review.Rows[index]=row;}
                var result=await imports.ConfirmRowsAsync(review,d.Rows.Select(x=>x.Ordinal));
                var ids=await db.BankImportEntries.Where(x=>x.ImportId==d.ImportId&&d.Rows.Select(r=>r.Ordinal).Contains(x.Ordinal)).Select(x=>x.TransactionId).ToArrayAsync();
                return new($"Extrato #{d.ImportId}: {result.Created} movimentos criados e {result.Linked} associados.",ids,[],[d.ImportId],"/extratos");
            }
            case "movement":
            {
                var d=Data<MovementForm>(action);if(d.Id!=0)throw new FinanceRuleException("Usa a classificação para alterar um movimento existente.");
                d.RequestId=op.CommandId;var id=await finance.SaveMovementAsync(d,true);
                return new($"Movimento #{id} registado.",[id],[],[],"/movimentos");
            }
            case "classify":
            {
                var d=Data<ClassificationAction>(action);await using var db=open();
                if(await db.PurchaseEvidence.AnyAsync(x=>x.TransactionId==d.TransactionId&&x.CategoryAllocationsJson!="[]"))throw new FinanceRuleException("Este movimento tem repartição por artigos; altera a repartição da compra.");
                var form=await finance.MovementAsync(d.TransactionId);
                if(form.Version!=d.Version)
                {if(await db.Commands.AnyAsync(x=>x.Id==op.CommandId.ToString("N")))return new($"Movimento #{d.TransactionId} já classificado.",[d.TransactionId],[],[],"/movimentos");throw new FinanceRuleException("O movimento foi alterado entretanto.");}
                form.CategoryId=d.CategoryId;form.RequestId=op.CommandId;await finance.SaveMovementAsync(form);
                return new($"Classificação do movimento #{d.TransactionId} atualizada.",[d.TransactionId],[],[],"/movimentos");
            }
            case "allocation":
            {
                var d=Data<AllocationAction>(action);await using var db=open();var e=await db.PurchaseEvidence.SingleAsync(x=>x.Id==d.EvidenceId);
                if(e.CategoryAllocationsJson!=JsonSerializer.Serialize(d.Parts))await imports.SaveEvidenceAllocationsAsync(d.EvidenceId,d.Version,d.Parts);
                return new($"Repartição da compra #{e.Id} guardada.",e.TransactionId is {} id?[id]:[],[e.Id],[],"/compras");
            }
            case "advance":
            {
                var d=Data<AdvanceAction>(action);var id=await reimbursements.RegisterAsync(d.TransactionId,d.Debtor);
                return new($"Adiantamento #{id} registado para acompanhar o reembolso.",[d.TransactionId],[],[],"/reembolsos");
            }
            case "reference":
            {
                var d=Data<BankReferenceForm>(action);if(d.Id!=0)throw new FinanceRuleException("As referências existentes não são substituídas automaticamente.");
                var old=(await imports.ReferencesAsync(d.AccountId)).FirstOrDefault(x=>x.Active&&Fold(x.Merchant)==Fold(d.Merchant)&&Fold(x.Code)==Fold(d.Code)&&x.CategoryId==d.CategoryId&&x.Description==d.Description&&x.PurchaseDate==d.PurchaseDate&&x.AmountUnits==(d.Amount is {} a?Money.ToUnits(a):null));
                var id=old?.Id??await imports.SaveReferenceAsync(d);
                return new($"Referência #{id} memorizada.",[],[],[],"/extratos");
            }
            default:throw new FinanceRuleException("Operação não suportada.");
        }
    }
    public sealed record PurchaseAction(PurchaseEvidence Evidence,string ItemKey,long? AttachmentId,long? AccountId,long? ExistingTransactionId,bool CreateMovement);
    public sealed record StageAction(long? AttachmentId,int TextRound);
    public sealed record BankConfirmAction(long ImportId,int Version,List<BankRowDecision> Rows);
    public sealed record ClassificationAction(long TransactionId,int Version,long CategoryId);
    public sealed record AllocationAction(long EvidenceId,int Version,List<PurchaseCategoryAllocation> Parts);
    public sealed record AdvanceAction(long TransactionId,string Debtor);
    public sealed record SourceRecordAction(long AttachmentId,string Status,string Note,string[]? OperationKeys,long[]? EvidenceIds,long[]? ImportIds);
    public sealed record SourceCursorAction(string Kind,string Scope,JsonElement Cursor);
    public sealed record ReviewNotesAction(List<ReviewNote> Items);
    public sealed record EvidenceAction(long EvidenceId,int Version,long? AccountId,long? CategoryId,long? ExistingTransactionId);
    private static string Fold(string text)=>new(text.Normalize(NormalizationForm.FormD).Where(c=>System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)!=System.Globalization.UnicodeCategory.NonSpacingMark).Select(char.ToUpperInvariant).Where(char.IsLetterOrDigit).ToArray());
}

using System.Security.Cryptography;
using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed partial class BankImportService
{
    // Explicit correction after the owner and both bank sources identify the transfer.
    // Dates belong to each bank leg; settling later does not turn the credit into income.
    public async Task ReclassifyBankTransferAsync(long transactionId,int version,long outgoingImportId,int outgoingOrdinal,
        long incomingImportId,int incomingOrdinal,long destinationAccountId,string reason,bool acknowledgeIncomingTotalWarning=false)
    {
        if(string.IsNullOrWhiteSpace(reason)||reason.Length>2000)throw new FinanceRuleException("Indica a identificação da transferência e das duas origens.");
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var movement=await db.Transactions.SingleAsync(t=>t.Id==transactionId);
        var outgoing=await db.BankImports.SingleAsync(b=>b.Id==outgoingImportId);
        var incoming=await db.BankImports.SingleAsync(b=>b.Id==incomingImportId);
        var sourceEntry=await db.BankImportEntries.SingleAsync(e=>e.ImportId==outgoingImportId&&e.Ordinal==outgoingOrdinal);
        var destinationEntry=await db.BankImportEntries.SingleOrDefaultAsync(e=>e.ImportId==incomingImportId&&e.Ordinal==incomingOrdinal);
        var posts=await db.Postings.Where(p=>p.TransactionId==transactionId).ToListAsync();
        if(movement.Kind==MovementKind.Transfer&&movement.Status=="Confirmed"&&sourceEntry.TransactionId==transactionId&&destinationEntry?.TransactionId==transactionId&&
            posts.Count==2&&posts.Sum(p=>p.AmountUnits)==0&&posts.Any(p=>p.AccountId==destinationAccountId&&p.AmountUnits>0))return;
        if(movement.Version!=version||movement.Kind!=MovementKind.Expense||movement.Status!="Confirmed"||movement.Origin!="BankImport"||
            sourceEntry.TransactionId!=transactionId||!sourceEntry.Created||sourceEntry.TransactionVersion!=version||destinationEntry is not null||
            outgoing.Status!="Draft"||incoming.Status!="Draft"||outgoing.AccountId is null||outgoing.AccountId==destinationAccountId||
            incoming.AccountId is {} assigned&&assigned!=destinationAccountId||posts.Count!=1||posts[0].AccountId!=outgoing.AccountId)
            throw new FinanceRuleException("Confere o débito existente, as versões e as duas contas antes de corrigir a transferência.");
        var destination=await db.Accounts.SingleOrDefaultAsync(a=>a.Id==destinationAccountId&&a.Active&&a.Currency=="EUR"&&a.HouseholdId==movement.HouseholdId)
            ??throw new FinanceRuleException("Escolhe a conta de destino em EUR da mesma família.");
        if(!await db.BankImports.AnyAsync(b=>b.SourceReference==incoming.SourceReference&&b.AccountId==destination.Id)||
            await db.BankImports.AnyAsync(b=>b.SourceReference==incoming.SourceReference&&b.AccountId!=null&&b.AccountId!=destination.Id))
            throw new FinanceRuleException("A origem bancária não identifica inequivocamente esta conta de destino.");
        var outgoingDocument=await db.BankDocuments.SingleAsync(d=>d.Id==outgoing.DocumentId);
        var incomingDocument=await db.BankDocuments.SingleAsync(d=>d.Id==incoming.DocumentId);
        if(Convert.ToHexString(SHA256.HashData(outgoingDocument.Content))!=outgoingDocument.Id||Convert.ToHexString(SHA256.HashData(incomingDocument.Content))!=incomingDocument.Id)
            throw new FinanceRuleException("Um comprovativo original não confere.");
        var outgoingStatement=JsonSerializer.Deserialize<BankStatement>(outgoingDocument.StatementJson)!;
        var incomingStatement=JsonSerializer.Deserialize<BankStatement>(incomingDocument.StatementJson)!;
        var source=outgoingStatement.Accounts[outgoing.SectionIndex];var target=incomingStatement.Accounts[incoming.SectionIndex];
        const string totalWarning="Os movimentos Revolut não conferem com os totais e o saldo final.";
        if(outgoingStatement.Errors.Count>0||source.Errors.Count>0||incomingStatement.Errors.Count>0||
            target.Errors.Any(e=>e!=totalWarning)||target.Errors.Count>0&&!acknowledgeIncomingTotalWarning)
            throw new FinanceRuleException("Confere a leitura das linhas e os avisos do extrato antes de associar.");
        var debit=source.Rows.Single(r=>r.Ordinal==outgoingOrdinal);var credit=target.Rows.Single(r=>r.Ordinal==incomingOrdinal);
        var elapsed=credit.Date.DayNumber-debit.Date.DayNumber;
        var preceding=target.Rows.TakeWhile(r=>r.Ordinal!=incomingOrdinal).LastOrDefault()?.BalanceUnits??target.OpeningUnits;
        if(debit.Units>=0||credit.Units<=0||debit.Units!=-credit.Units||elapsed is <0 or >3||
            posts[0].AmountUnits!=debit.Units||movement.Date!=debit.Date||posts[0].Date!=debit.Date||
            preceding is null||(decimal)preceding+credit.Units!=credit.BalanceUnits)
            throw new FinanceRuleException("As duas linhas não conferem por valor, datas ou saldo sucessivo.");
        if(await db.PurchaseEvidence.AnyAsync(e=>e.TransactionId==transactionId)||await db.IndividualMovements.AnyAsync(m=>m.PaymentTransactionId==transactionId)||
            await db.ReimbursementClaims.AnyAsync(r=>r.ExpenseTransactionId==transactionId)||await db.ReimbursementAllocations.AnyAsync(r=>r.ReceiptTransactionId==transactionId)||
            await db.BankImportEntries.CountAsync(e=>e.TransactionId==transactionId)!=1)
            throw new FinanceRuleException("O débito tem outras associações que exigem reconciliação.");
        var first=debit.Date;var last=credit.Date;
        if(await (from p in db.Postings join t in db.Transactions on p.TransactionId equals t.Id
            where t.Status=="Confirmed"&&p.AccountId==destination.Id&&p.AmountUnits==credit.Units&&p.Date>=first&&p.Date<=last select p.Id).AnyAsync())
            throw new FinanceRuleException("Já existe uma entrada correspondente no destino. Reconcilia-a sem duplicar.");
        var before=JsonSerializer.SerializeToElement(new{Movement=movement,Postings=posts});
        movement.Kind=MovementKind.Transfer;movement.Notes=reason;movement.Version++;posts[0].CategoryId=null;
        var destinationPost=new Posting{TransactionId=transactionId,AccountId=destination.Id,Date=credit.Date,ValueDate=credit.ValueDate,AmountUnits=credit.Units};
        db.Add(destinationPost);sourceEntry.TransactionVersion=movement.Version;
        db.Add(new BankImportEntry{ImportId=incoming.Id,Ordinal=credit.Ordinal,TransactionId=transactionId,TransactionVersion=movement.Version,Created=false});
        void UpdateReview(BankImport batch,int ordinal,long accountId,long otherAccountId)
        {
            var review=JsonSerializer.Deserialize<BankImportReview>(batch.ReviewJson)??throw new FinanceRuleException("A revisão do extrato não está disponível.");
            var choice=review.Rows.Single(r=>r.Ordinal==ordinal);
            choice.Decision="Link";choice.Kind=MovementKind.Transfer;choice.CategoryId=null;choice.CounterpartyAccountId=otherAccountId;
            choice.ExistingTransactionId=transactionId;choice.ExistingVersion=movement.Version;choice.ClassificationEdited=true;choice.SuggestionAccepted=false;choice.Detail=reason;
            batch.AccountId=accountId;batch.Version++;batch.ReviewHash="";review.AccountId=accountId;review.Version=batch.Version;batch.ReviewJson=JsonSerializer.Serialize(review);
            db.Add(new AuditChange{Entity="BankImport",EntityId=batch.Id,Action="LinkInternalTransfer",Actor="Owner",CreatedUtc=DateTime.UtcNow,
                DetailJson=JsonSerializer.Serialize(new{TransactionId=transactionId,Ordinal=ordinal,AccountId=accountId,OtherAccountId=otherAccountId})});
        }
        UpdateReview(outgoing,debit.Ordinal,outgoing.AccountId.Value,destination.Id);
        UpdateReview(incoming,credit.Ordinal,destination.Id,outgoing.AccountId.Value);
        db.Add(new AuditChange{Entity="Transaction",EntityId=transactionId,Action="ReclassifyInternalTransfer",Actor="Owner",CreatedUtc=DateTime.UtcNow,
            DetailJson=JsonSerializer.Serialize(new{Before=before,After=new{Movement=movement,Source=posts[0],Destination=destinationPost},OutgoingImportId=outgoing.Id,OutgoingOrdinal=debit.Ordinal,
                IncomingImportId=incoming.Id,IncomingOrdinal=credit.Ordinal,PreservedIncomingWarnings=target.Errors,Reason=reason})});
        await db.SaveChangesAsync();await tx.CommitAsync();
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed class RecurringExpenseForm
{
    public long Id { get; set; }
    public int Version { get; set; }
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public long? CategoryId { get; set; }
    public long? AccountId { get; set; }
    public string ContainsText { get; set; } = "";
    public string Frequency { get; set; } = "Auto";
    public bool Included { get; set; } = true;
}
public sealed record RecurringExpenseLine(RecurringExpense Rule,string Category,string Frequency,List<MovementRow> Movements)
{
    public long TotalUnits=>-Movements.Sum(m=>m.Units);
    public long MonthUnits(int month)=>-Movements.Where(m=>m.Date.Month==month).Sum(m=>m.Units);
    public int MonthCount(int month)=>Movements.Count(m=>m.Date.Month==month);
}
public sealed record RecurringExpenseReport(int Year,List<int> Years,List<RecurringExpenseLine> Lines)
{
    public List<RecurringExpenseLine> Included=>Lines.Where(l=>l.Rule.Included).ToList();
    public long TotalUnits=>Included.Sum(l=>l.TotalUnits);
    public long MonthUnits(int month)=>Included.Sum(l=>l.MonthUnits(month));
    public int MonthCount(int month)=>Included.Sum(l=>l.MonthCount(month));
}

public sealed partial class FinanceService
{
    public static readonly IReadOnlyDictionary<string,string> RecurringFrequencies=new Dictionary<string,string>{
        ["Auto"]="Identificar pelo histórico",["Monthly"]="Mensal",["Bimonthly"]="Bimestral",["Quarterly"]="Trimestral",["HalfYearly"]="Semestral",["Annual"]="Anual",["Variable"]="Outra / irregular"};
    private static string RecurringFold(string? text)=>string.Join(' ',new string((text??"").Normalize(NormalizationForm.FormD)
        .Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark).Select(c=>char.IsLetterOrDigit(c)?char.ToUpperInvariant(c):' ').ToArray()).Split(' ',StringSplitOptions.RemoveEmptyEntries));
    private static string RecurringKey(long? category,long? account,string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{category}:{account}:{RecurringFold(text)}")));
    private static bool RecurringMatches(RecurringExpense rule,MovementRow row,Dictionary<long,Category> categories)=>
        (rule.AccountId is null||rule.AccountId==row.AccountId)&&
        (rule.CategoryId is null||rule.CategoryId==row.CategoryId||row.CategoryId is {} id&&categories.GetValueOrDefault(id)?.ParentId==rule.CategoryId)&&
        (rule.ContainsText.Length==0||RecurringFold(row.Description+" "+row.Merchant).Contains(RecurringFold(rule.ContainsText),StringComparison.Ordinal));

    public async Task<long> SaveRecurringExpenseAsync(RecurringExpenseForm form)
    {
        if(string.IsNullOrWhiteSpace(form.Name)||form.Name.Length>120||form.ContainsText.Length>150||!RecurringFrequencies.ContainsKey(form.Frequency))throw new FinanceRuleException("Indica o nome e uma periodicidade válida.");
        if(form.CategoryId is null&&RecurringFold(form.ContainsText).Length<3)throw new FinanceRuleException("Escolhe uma categoria ou pelo menos três letras da descrição para identificar o compromisso.");
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var hash=Payload(form);if(await Receipt(db,form.RequestId,"SaveRecurringExpense",hash) is {} saved)return saved;
        var household=await Household(db);
        if(form.CategoryId is {} category&&!await db.Categories.AnyAsync(c=>c.Id==category&&c.HouseholdId==household&&c.Kind==MovementKind.Expense))throw new FinanceRuleException("Escolhe uma categoria de despesa.");
        if(form.AccountId is {} account&&!await db.Accounts.AnyAsync(a=>a.Id==account&&a.HouseholdId==household))throw new FinanceRuleException("Conta inválida.");
        var key=RecurringKey(form.CategoryId,form.AccountId,form.ContainsText);
        if(await db.RecurringExpenses.AnyAsync(r=>r.HouseholdId==household&&r.MatchKey==key&&r.Id!=form.Id))throw new FinanceRuleException("Este conjunto de movimentos já tem um compromisso. Edita o existente.");
        RecurringExpense rule;object? before=null;
        if(form.Id==0){rule=new(){HouseholdId=household};db.Add(rule);}
        else{rule=await db.RecurringExpenses.SingleOrDefaultAsync(r=>r.Id==form.Id&&r.HouseholdId==household)??throw new FinanceRuleException("Compromisso não encontrado.");if(rule.Version!=form.Version)throw new FinanceRuleException("O compromisso foi alterado. Atualiza a página.");before=JsonSerializer.SerializeToElement(rule);rule.Version++;}
        rule.Name=form.Name.Trim();rule.CategoryId=form.CategoryId;rule.AccountId=form.AccountId;rule.ContainsText=form.ContainsText.Trim();rule.MatchKey=key;rule.Frequency=form.Frequency;rule.Included=form.Included;
        await db.SaveChangesAsync();AddAudit(db,"RecurringExpense",rule.Id,"Save",before,rule);AddReceipt(db,form.RequestId,"SaveRecurringExpense",hash,rule.Id);await db.SaveChangesAsync();await tx.CommitAsync();return rule.Id;
    }

    public async Task<int> IdentifyRecurringExpensesAsync()
    {
        var categories=await CategoriesAsync();var byId=categories.ToDictionary(c=>c.Id);
        await using var db=open();var existing=(await db.RecurringExpenses.AsNoTracking().ToListAsync()).Select(r=>r.MatchKey).ToHashSet();
        var names=new HashSet<string>{"AGUA","ELETRICIDADE","GAS","CONDOMINIO","RENDA","RENDA DA CASA","ARRENDAMENTO","CREDITO A HABITACAO","CREDITO PESSOAL","CREDITO AUTOMOVEL E DIVIDAS DE VEICULOS","INTERNET TELEVISAO E TELEFONE","TELEMOVEIS","STREAMING","GINASIO","QUOTAS DE CLUBES E ASSOCIACOES","ASSOCIACOES DE CONSUMIDORES","PROPINAS","MENSALIDADES","IUC","IMI"};
        int added=0;
        foreach(var c in categories.Where(c=>c.Active&&c.Kind==MovementKind.Expense&&c.ParentId!=null)){
            var root=byId.GetValueOrDefault(c.ParentId!.Value)?.Name??"";
            if(!names.Contains(RecurringFold(c.Name))&&RecurringFold(root)!="SEGUROS")continue;
            if(existing.Contains(RecurringKey(c.Id,null,"")))continue;
            await SaveRecurringExpenseAsync(new(){Name=$"{root} · {c.Name}",CategoryId=c.Id,Frequency=c.Name is "IUC" or "IMI"?"Annual":"Auto"});added++;
        }
        // Recognizable suppliers also cover bills still assigned to a broad
        // category, without changing their financial classification.
        var rows=await EntryRowsAsync(db,new(){Kind=MovementKind.Expense});
        var suppliers=new[]{("Energia · Gold Energy","GOLD ENERGY",1),("Energia · Goldenergy","GOLDENERGY",1),("Energia · EDP","EDP",1),("Energia · Endesa","ENDESA",1),("Energia · Iberdrola","IBERDROLA",1),("Renda da casa","RENDA DA CASA",1),("Água · Aquaservice","AQUASERVICE",1),("Serviços digitais · Render","RENDER",2),("Serviços digitais · GitHub","GITHUB",2),("Serviços digitais · OpenAI","OPENAI",2),("Subscrição · Público","PUBLICO",2)};
        foreach(var (name,text,minimumMonths) in suppliers){
            if(existing.Contains(RecurringKey(null,null,text)))continue;
            var candidate=new RecurringExpense{ContainsText=text};
            if(rows.Where(r=>RecurringMatches(candidate,r,byId)).Select(r=>(r.Date.Year,r.Date.Month)).Distinct().Count()<minimumMonths)continue;
            await SaveRecurringExpenseAsync(new(){Name=name,ContainsText=text});added++;
        }
        return added;
    }

    public async Task<RecurringExpenseReport> RecurringExpensesAsync(int year)
    {
        if(year is <1 or >9999)throw new FinanceRuleException("Ano inválido.");
        await using var db=open();var categories=await db.Categories.AsNoTracking().ToDictionaryAsync(c=>c.Id);
        var rules=await db.RecurringExpenses.AsNoTracking().OrderBy(r=>r.Name).ToListAsync();
        // Reuse the effective ledger, including category splits and individual edits;
        // the parent payment, sources and bank copies are not additional expenses.
        var history=await EntryRowsAsync(db,new(){From=new(Math.Max(1,year-2),1,1),To=new(year,12,31),Kind=MovementKind.Expense});
        var assigned=rules.ToDictionary(r=>r.Id,_=>new List<MovementRow>());
        foreach(var row in history.Where(r=>r.Status=="Confirmed"&&r.Kind==MovementKind.Expense)){
            var rule=rules.Where(r=>RecurringMatches(r,row,categories))
                .OrderByDescending(r=>r.ContainsText.Length>0).ThenByDescending(r=>r.AccountId!=null)
                .ThenByDescending(r=>r.CategoryId is {} id&&categories.GetValueOrDefault(id)?.ParentId!=null)
                .ThenByDescending(r=>r.ContainsText.Length).ThenBy(r=>r.Id).FirstOrDefault();
            // An excluded specific rule also reserves its matches, so a broader
            // included category cannot silently add them back to the totals.
            if(rule!=null)assigned[rule.Id].Add(row);
        }
        string Frequency(RecurringExpense rule,List<MovementRow> entries){
            if(rule.Frequency!="Auto")return RecurringFrequencies[rule.Frequency];
            var months=entries.Select(m=>m.Date.Year*12+m.Date.Month).Distinct().Order().ToList();
            if(months.Count<2)return "Por confirmar";
            var gaps=months.Zip(months.Skip(1),(a,b)=>b-a).Order().ToList();var gap=gaps[gaps.Count/2];
            var label=gap switch{1=>"Mensal",2=>"Bimestral",3=>"Trimestral",6=>"Semestral",>=11 and <=13=>"Anual",_=>"Irregular"};
            return label+" · estimada";
        }
        var lines=rules.Select(r=>new RecurringExpenseLine(r,r.CategoryId is {} id?categories.GetValueOrDefault(id)?.Name??"":"Descrição",Frequency(r,assigned[r.Id]),assigned[r.Id].Where(m=>m.Date.Year==year).OrderByDescending(m=>m.Date).ToList())).ToList();
        return new(year,await MovementYearsAsync(),lines);
    }
}

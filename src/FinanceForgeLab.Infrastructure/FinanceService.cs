using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed partial class FinanceService(Func<FinanceDb> open)
{
    public async Task<List<Category>> CategoriesAsync() { await using var db = open(); return await db.Categories.AsNoTracking().OrderBy(x => x.Name).ToListAsync(); }
    public async Task<List<Person>> PeopleAsync() { await using var db = open(); return await db.People.AsNoTracking().OrderBy(x => x.Name).ToListAsync(); }
    public async Task<List<int>> MovementYearsAsync() { await using var db = open(); return await db.Transactions.AsNoTracking().Select(t => t.Date.Year).Union(db.IndividualMovements.Select(m=>m.Date.Year)).Distinct().OrderByDescending(y => y).ToListAsync(); }

    public async Task<Dictionary<long, int>> InactiveCategoryUsageAsync()
    {
        await using var db = open();
        return await (from p in db.Postings
                      join t in db.Transactions on p.TransactionId equals t.Id
                      join c in db.Categories on p.CategoryId equals c.Id
                      where !c.Active && t.Status == "Confirmed"
                      group t.Id by c.Id into g
                      select new { Id = g.Key, Count = g.Distinct().Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count);
    }

    private static IQueryable<MovementRow> Query(FinanceDb db, MovementFilter f)
    {
        if (f.From > f.To) throw new FinanceRuleException("A data inicial deve ser anterior à data final.");
        var transactions = string.IsNullOrWhiteSpace(f.Search) ? db.Transactions.AsQueryable() : db.Transactions.FromSqlInterpolated(
            $"SELECT * FROM \"Transactions\" WHERE ffl_contains(\"Description\", {f.Search.Trim()}) OR ffl_contains(\"Notes\", {f.Search.Trim()})");
        var query = from t in transactions.AsNoTracking()
                    join p in db.Postings.AsNoTracking() on t.Id equals p.TransactionId
                    join a in db.Accounts on p.AccountId equals a.Id into accountGroup from a in accountGroup.DefaultIfEmpty()
                    join c in db.Categories on p.CategoryId equals c.Id into categoryGroup from c in categoryGroup.DefaultIfEmpty()
                    join root in db.Categories on c.ParentId equals root.Id into parentGroup from root in parentGroup.DefaultIfEmpty()
                    join person in db.People on t.PersonId equals person.Id into personGroup from person in personGroup.DefaultIfEmpty()
                    where (t.Kind != MovementKind.Transfer || p.AmountUnits < 0)&&!db.IndividualMovements.Any(m=>m.PaymentTransactionId==t.Id)
                    select new MovementRow { Id=t.Id, Version=t.Version, Date=t.Date, Kind=t.Kind, Description=t.Description, Notes=t.Notes, AccountId=p.AccountId, Account=a.Name,
                        CategoryId=p.CategoryId, Category=c.Name, ParentCategory=root.Name, PersonId=t.PersonId, Person=person.Name, Units=p.AmountUnits, Origin=t.Origin, Status=t.Status };
        if (!f.IncludeVoided) query = query.Where(x => x.Status == "Confirmed");
        if(f.TransactionId is {} transactionId)query=query.Where(x=>x.Id==transactionId);
        if (f.From is not null) query = query.Where(x => x.Date >= f.From);
        if (f.To is not null) query = query.Where(x => x.Date <= f.To);
        if (f.Kind is not null) query = query.Where(x => x.Kind == f.Kind);
        if (f.PersonId is > 0) query = query.Where(x => x.PersonId == f.PersonId);
        if (f.AccountId == -1) query = query.Where(x => x.AccountId == null);
        else if (f.AccountId is > 0) query = query.Where(x => db.Postings.Any(p => p.TransactionId == x.Id && p.AccountId == f.AccountId));
        // Category zero is filtered after invoice allocations and individual movements are expanded.
        if (f.CategoryId is > 0) query = query.Where(x =>
            !db.PurchaseEvidence.Any(e=>e.TransactionId==x.Id&&!e.HasSourceConflict&&e.Units!=null&&(e.FinancialUnits??e.Units)==-x.Units&&PurchaseAllocationMath.ContainsCategory(e.CategoryAllocationsJson,0,e.Units.Value,-x.Units))
                && (x.CategoryId == f.CategoryId || db.Categories.Any(c => c.Id == x.CategoryId && c.ParentId == f.CategoryId))
            || db.PurchaseEvidence.Any(e=>e.TransactionId==x.Id&&!e.HasSourceConflict&&e.Units!=null&&(e.FinancialUnits??e.Units)==-x.Units&&
                (PurchaseAllocationMath.ContainsCategory(e.CategoryAllocationsJson,f.CategoryId.Value,e.Units.Value,-x.Units)||db.Categories.Any(c=>c.ParentId==f.CategoryId&&PurchaseAllocationMath.ContainsCategory(e.CategoryAllocationsJson,c.Id,e.Units.Value,-x.Units)))));
        return query;
    }
    public async Task<MovementPage> MovementsAsync(MovementFilter f)
    {
        await using var db = open();
        var rows=await EntryRowsAsync(db,f);var total=rows.Count;
        var size=Math.Clamp(f.PageSize,10,100);var page=Math.Clamp(f.Page,1,Math.Max(1,(int)Math.Ceiling(total/(double)size)));
        return new(await WithSourcesAsync(db,rows.Skip((page-1)*size).Take(size).ToList()),total,page,size);
    }
    private static async Task<List<MovementRow>> WithOriginalClassificationAsync(FinanceDb db, List<MovementRow> rows)
    {
        rows=await WithPurchaseClassificationAsync(db,rows);
        var ids = rows.Where(r => r.Origin == "Legacy").Select(r => r.Id).ToList();
        if (ids.Count == 0) return rows;
        var sources = await (from i in db.ImportRows.AsNoTracking()
                             join s in db.SourceRecords.AsNoTracking() on i.SourceRecordId equals s.Id
                             where i.TransactionId != null && ids.Contains(i.TransactionId.Value) && s.Table == "Movimento"
                             orderby i.Id
                             select new { TransactionId = i.TransactionId!.Value, s.SnapshotId, s.RawJson }).ToListAsync();
        var snapshots = sources.Select(s => s.SnapshotId).Distinct().ToList();
        var definitions = await db.SourceRecords.AsNoTracking().Where(s => snapshots.Contains(s.SnapshotId) && (s.Table == "Categoria" || s.Table == "SubCategoria")).ToListAsync();
        var names = definitions.ToDictionary(s => (s.SnapshotId, s.Table, s.LegacyId), s => JsonSerializer.Deserialize<Dictionary<string, string?>>(s.RawJson)!.GetValueOrDefault("Descricao"));
        var originals = sources.GroupBy(s => s.TransactionId).ToDictionary(g => g.Key, g => g.First());
        return rows.Select(row =>
        {
            if (!originals.TryGetValue(row.Id, out var source)) return row;
            var fields = JsonSerializer.Deserialize<Dictionary<string, string?>>(source.RawJson)!;
            return row with
            {
                OriginalCategory = names.GetValueOrDefault((source.SnapshotId, "Categoria", fields.GetValueOrDefault("IdCategoria") ?? "")),
                OriginalSubcategory = names.GetValueOrDefault((source.SnapshotId, "SubCategoria", fields.GetValueOrDefault("IdSubCategoria") ?? ""))
            };
        }).ToList();
    }
    public async Task<MovementForm> MovementAsync(long id)
    {
        await using var db = open(); var t = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == id);
        if (t.Kind is not (MovementKind.Income or MovementKind.Expense or MovementKind.Transfer)) throw new FinanceRuleException("Este movimento é gerido na conta.");
        var posts = await db.Postings.Where(x => x.TransactionId == id).ToListAsync();
        var from = t.Kind == MovementKind.Transfer ? posts.Single(x => x.AmountUnits < 0) : posts.Single();
        return new MovementForm { Id = id, Version = t.Version, Date = t.Date, Kind = t.Kind, Description = t.Description ?? "", Notes = t.Notes, PersonId = t.PersonId,
            AccountId = from.AccountId, CategoryId = from.CategoryId, Amount = t.Kind == MovementKind.Income ? Money.FromUnits(from.AmountUnits) : -Money.FromUnits(from.AmountUnits),
            DestinationAccountId = t.Kind == MovementKind.Transfer ? posts.Single(x => x.AmountUnits > 0).AccountId : null };
    }
    public async Task<long> SaveMovementAsync(MovementForm input, bool preventPossibleDuplicates = false)
    {
        if(input.IndividualId!=null||input.SourceCategoryId!=null)return await SaveIndividualAsync(input);
        Validate(input); var units = Units(input.Amount);
        if (units == 0) throw new FinanceRuleException("O montante deve ser diferente de zero.");
        if (units == long.MinValue && input.Kind != MovementKind.Income) throw new FinanceRuleException("O montante excede o limite permitido.");
        if (input.Kind is not (MovementKind.Income or MovementKind.Expense or MovementKind.Transfer)) throw new FinanceRuleException("Tipo de movimento inválido.");
        if (input.Kind == MovementKind.Transfer && units <= 0) throw new FinanceRuleException("A transferência deve ter um montante positivo.");
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var action = "SaveMovement"; var hash = Payload(input);
        if (await Receipt(db, input.RequestId, action, hash) is long existing) return existing;
        if (preventPossibleDuplicates && input.Id == 0)
        {
            var firstDate=DateOnly.FromDayNumber(Math.Max(0,input.Date.DayNumber-7));var lastDate=DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber,input.Date.DayNumber+7));
            var signed=input.Kind==MovementKind.Income?units:-units;
            if(await (from t0 in db.Transactions join p0 in db.Postings on t0.Id equals p0.TransactionId
                where t0.Status=="Confirmed"&&t0.Date>=firstDate&&t0.Date<=lastDate&&p0.AmountUnits==signed
                &&(p0.AccountId==input.AccountId||p0.AccountId==null||input.AccountId==null)
                select t0.Id).AnyAsync())throw new FinanceRuleException("Existe um possível movimento correspondente por valor, conta e data próxima. Associa a fonte ou pede confirmação antes de criar outro.");
        }
        FinancialTransaction t;
        object? before = null;
        List<Posting> posts;
        if (input.Id == 0)
        {
            if (string.IsNullOrWhiteSpace(input.Description)) throw new FinanceRuleException("Indica uma descrição.");
            t = new FinancialTransaction { HouseholdId = await Household(db), Origin = "Manual" }; db.Add(t); posts = [];
        }
        else
        {
            t = await db.Transactions.SingleOrDefaultAsync(x => x.Id == input.Id) ?? throw new FinanceRuleException("Movimento não encontrado.");
            if (t.Version != input.Version) throw new FinanceRuleException("O movimento foi alterado noutra sessão. Volta a abri-lo.");
            if (t.Status != "Confirmed" || t.Kind is MovementKind.Opening or MovementKind.Adjustment) throw new FinanceRuleException("Este movimento não pode ser editado aqui.");
            if(await db.IndividualMovements.AnyAsync(m=>m.PaymentTransactionId==t.Id)||await db.PurchaseEvidence.AnyAsync(e=>e.TransactionId==t.Id&&e.CategoryAllocationsJson!="[]"&&e.CategoryAllocationsJson!=""))throw new FinanceRuleException("Abre o movimento individual na lista para editar apenas essa linha.");
            posts = await db.Postings.Where(x => x.TransactionId == t.Id).ToListAsync();
            before = new { Transaction = JsonSerializer.SerializeToElement(t), Postings = JsonSerializer.SerializeToElement(posts) };
            t.Version++;
        }
        async Task<Account?> CheckAccount(long? id)
        {
            if (id is null) return null;
            var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id && x.HouseholdId == t.HouseholdId) ?? throw new FinanceRuleException("Conta inválida.");
            if (!account.Active && !posts.Any(x => x.AccountId == id)) throw new FinanceRuleException("A conta está inativa.");
            if (account.Currency != "EUR") throw new FinanceRuleException("O MVP permite movimentos em EUR.");
            return account;
        }
        await CheckAccount(input.AccountId);
        if (input.AccountId is null && (t.Origin is not ("Legacy" or "Purchase") || input.Kind == MovementKind.Transfer)) throw new FinanceRuleException("Seleciona uma conta.");
        if (input.PersonId is not null && !await db.People.AnyAsync(x => x.Id == input.PersonId && x.HouseholdId == t.HouseholdId)) throw new FinanceRuleException("Pessoa inválida.");
        Category? category = null;
        if (input.Kind == MovementKind.Transfer)
        {
            if (input.DestinationAccountId is null || input.DestinationAccountId == input.AccountId) throw new FinanceRuleException("Seleciona duas contas distintas.");
            await CheckAccount(input.DestinationAccountId);
        }
        else
        {
            category = await db.Categories.SingleOrDefaultAsync(x => x.Id == input.CategoryId && x.HouseholdId == t.HouseholdId)
                ?? throw new FinanceRuleException("Seleciona uma categoria.");
            if (category.Kind != input.Kind || (!category.Active && !posts.Any(x => x.CategoryId == category.Id))) throw new FinanceRuleException("A categoria não está disponível para este tipo.");
            if (category.ParentId is long parentId && !await db.Categories.AnyAsync(x => x.Id == parentId && x.Active) && !posts.Any(x => x.CategoryId == category.Id))
                throw new FinanceRuleException("A categoria de raiz está inativa.");
        }
        var preserveTransferDates = t.Kind == MovementKind.Transfer && input.Kind == MovementKind.Transfer && t.Date == input.Date &&
            posts.Count == 2 && posts[0].AccountId == input.AccountId && posts[1].AccountId == input.DestinationAccountId;
        t.Date = input.Date; t.Kind = input.Kind; t.Description = input.Description.Trim(); t.Notes = input.Notes; t.PersonId = input.PersonId;
        await db.SaveChangesAsync();
        // Keep existing posting IDs during edits so external references remain stable.
        var required = input.Kind == MovementKind.Transfer ? 2 : 1;
        while (posts.Count < required) { var post = new Posting { TransactionId = t.Id }; posts.Add(post); db.Add(post); }
        if (posts.Count > required) { db.RemoveRange(posts.Skip(required)); posts = posts.Take(required).ToList(); }
        for (var i = 0; i < posts.Count; i++)
        {
            var post = posts[i]; if (!preserveTransferDates) post.Date = input.Date; post.Currency = "EUR";
            post.AccountId = i == 0 ? input.AccountId : input.DestinationAccountId;
            post.CategoryId = input.Kind == MovementKind.Transfer ? null : category!.Id;
            post.AmountUnits = input.Kind == MovementKind.Income ? units : i == 0 ? checked(-units) : units;
        }
        if (input.Kind == MovementKind.Transfer && posts.Sum(p => (decimal)p.AmountUnits) != 0) throw new FinanceRuleException("Transferência desequilibrada.");
        AddAudit(db, "Transaction", t.Id, action, before, new { Transaction = t, Postings = posts });
        AddReceipt(db, input.RequestId, action, hash, t.Id);
        await db.SaveChangesAsync(); await tx.CommitAsync(); return t.Id;
    }
    public async Task VoidMovementAsync(long id, int version, Guid requestId)
    {
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var hash = Payload(new { id, version }); if (await Receipt(db, requestId, "VoidMovement", hash) is not null) return;
        var t = await db.Transactions.SingleOrDefaultAsync(x => x.Id == id) ?? throw new FinanceRuleException("Movimento inexistente.");
        if (t.Version != version || t.Status != "Confirmed") throw new FinanceRuleException("O movimento foi alterado. Atualiza a lista.");
        if (t.Kind == MovementKind.Opening) throw new FinanceRuleException("A abertura é gerida na conta.");
        if(await db.IndividualMovements.AnyAsync(m=>m.PaymentTransactionId==t.Id)||await db.PurchaseEvidence.AnyAsync(e=>e.TransactionId==t.Id&&e.CategoryAllocationsJson!="[]"&&e.CategoryAllocationsJson!=""))throw new FinanceRuleException("Escolhe o movimento individual na lista para anular apenas essa linha.");
        var before = JsonSerializer.SerializeToElement(t); t.Status = "Voided"; t.Version++;
        AddAudit(db, "Transaction", id, "Void", before, t); AddReceipt(db, requestId, "VoidMovement", hash, id);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task<List<AccountSummary>> AccountsAsync()
    {
        await using var db = open(); var accounts = await db.Accounts.AsNoTracking().OrderBy(x => x.Name).ToListAsync(); var result = new List<AccountSummary>();
        var individual=await IndividualRowsAsync(db);
        foreach (var account in accounts)
        {
            var posts = from p in db.Postings join t in db.Transactions on p.TransactionId equals t.Id where p.AccountId == account.Id && t.Status == "Confirmed"&&!db.IndividualMovements.Any(m=>m.PaymentTransactionId==t.Id) select p;
            var values = await posts.Where(x => account.BalanceStartDate != null && x.Date >= account.BalanceStartDate).Select(x => x.AmountUnits).ToListAsync();
            var related=individual.Where(r=>r.Status=="Confirmed"&&(r.AccountId==account.Id||r.DestinationAccountId==account.Id)).ToList();
            values.AddRange(related.Where(r=>account.BalanceStartDate!=null&&r.Date>=account.BalanceStartDate).Select(r=>r.AccountId==account.Id?r.Units:-r.Units));
            result.Add(new(account, Sum(values), await posts.CountAsync(x => account.BalanceStartDate != null && x.Date < account.BalanceStartDate)+related.Count(r=>account.BalanceStartDate!=null&&r.Date<account.BalanceStartDate)));
        }
        return result;
    }
    public async Task<long> SaveAccountAsync(AccountForm input)
    {
        Validate(input); var openingUnits = Units(input.OpeningBalance);
        if (input.Type is not ("Bank" or "Cash" or "Savings" or "Other")) throw new FinanceRuleException("Tipo de conta inválido.");
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var hash = Payload(input); if (await Receipt(db, input.RequestId, "SaveAccount", hash) is long existing) return existing;
        Account a; object? before = null;
        if (input.Id == 0) { a = new Account { HouseholdId = await Household(db), BalanceStartDate = input.BalanceStartDate }; db.Add(a); }
        else
        {
            a = await db.Accounts.SingleAsync(x => x.Id == input.Id);
            if (a.Version != input.Version) throw new FinanceRuleException("A conta foi alterada. Volta a abri-la.");
            if (a.BalanceStartDate != input.BalanceStartDate) throw new FinanceRuleException("A data de abertura exige reconciliação; não pode ser alterada aqui.");
            if (input.OpeningBalance != 0) throw new FinanceRuleException("A alteração do saldo de abertura exige reconciliação e não é feita neste formulário.");
            before = JsonSerializer.SerializeToElement(a); a.Version++;
        }
        if ((await db.Accounts.Where(x=>x.Id!=input.Id).Select(x=>x.Name).ToListAsync()).Any(name=>string.Equals(name,input.Name.Trim(),StringComparison.OrdinalIgnoreCase))) throw new FinanceRuleException("Já existe uma conta com este nome.");
        a.Name = input.Name.Trim(); a.Type = input.Type; a.Active = input.Active;
        await db.SaveChangesAsync();
        if (input.Id == 0 && openingUnits != 0)
        {
            var t = new FinancialTransaction { HouseholdId = a.HouseholdId, Date = input.BalanceStartDate, Kind = MovementKind.Opening, Description = "Saldo de abertura", Origin = "Manual" };
            db.Add(t); await db.SaveChangesAsync(); db.Add(new Posting { TransactionId = t.Id, AccountId = a.Id, Date = t.Date, AmountUnits = openingUnits });
        }
        AddAudit(db, "Account", a.Id, "SaveAccount", before, a); AddReceipt(db, input.RequestId, "SaveAccount", hash, a.Id);
        await db.SaveChangesAsync(); await tx.CommitAsync(); return a.Id;
    }
    public async Task<long> SaveCategoryAsync(CategoryForm input)
    {
        Validate(input); if (input.Kind is not (MovementKind.Income or MovementKind.Expense)) throw new FinanceRuleException("Tipo de categoria inválido.");
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var hash = Payload(input); if (await Receipt(db, input.RequestId, "SaveCategory", hash) is long existing) return existing;
        var parent = input.ParentId is null ? null : await db.Categories.SingleOrDefaultAsync(x => x.Id == input.ParentId);
        if (input.ParentId is not null && (parent is null || parent.ParentId is not null || parent.Kind != input.Kind || !parent.Active || parent.Id == input.Id)) throw new FinanceRuleException("Seleciona uma categoria de raiz ativa do mesmo tipo.");
        Category c; object? before = null;
        if (input.Id == 0) { c = new Category { HouseholdId = await Household(db) }; db.Add(c); }
        else
        {
            c = await db.Categories.SingleAsync(x => x.Id == input.Id);
            if (c.Version != input.Version) throw new FinanceRuleException("A categoria foi alterada. Volta a abri-la.");
            if ((c.Kind != input.Kind || c.ParentId != input.ParentId) && (await db.Postings.AnyAsync(x => x.CategoryId == c.Id) || await db.Categories.AnyAsync(x => x.ParentId == c.Id)))
                throw new FinanceRuleException("Uma categoria utilizada conserva o tipo e a hierarquia. Podes alterar o nome ou desativá-la.");
            before = JsonSerializer.SerializeToElement(c); c.Version++;
        }
        if ((await db.Categories.Where(x=>x.Id!=input.Id&&x.Kind==input.Kind&&x.ParentId==input.ParentId).Select(x=>x.Name).ToListAsync()).Any(name=>string.Equals(name,input.Name.Trim(),StringComparison.OrdinalIgnoreCase))) throw new FinanceRuleException("Já existe uma categoria com este nome neste nível.");
        c.Name = input.Name.Trim(); c.Kind = input.Kind; c.ParentId = input.ParentId; c.Active = input.Active;
        await db.SaveChangesAsync(); AddAudit(db, "Category", c.Id, "SaveCategory", before, c);
        AddReceipt(db, input.RequestId, "SaveCategory", hash, c.Id); await db.SaveChangesAsync(); await tx.CommitAsync(); return c.Id;
    }
    // Moving a leaf preserves its identity and all payment/document references.
    public async Task MoveSubcategoryAsync(long id,long parentId,int version,Guid requestId)
    {
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var hash=Payload(new{id,parentId,version});
        if(await Receipt(db,requestId,"MoveSubcategory",hash) is not null)return;
        var household=await Household(db);
        var category=await db.Categories.SingleOrDefaultAsync(c=>c.Id==id&&c.HouseholdId==household)??throw new FinanceRuleException("Subcategoria não encontrada.");
        var parent=await db.Categories.SingleOrDefaultAsync(c=>c.Id==parentId&&c.HouseholdId==household);
        if(category.Version!=version)throw new FinanceRuleException("A categoria foi alterada. Volta a abri-la.");
        if(category.ParentId is null||await db.Categories.AnyAsync(c=>c.ParentId==id)||parent is null||parent.ParentId is not null||!parent.Active||parent.Kind!=category.Kind)
            throw new FinanceRuleException("Escolhe uma categoria principal ativa do mesmo tipo para esta subcategoria.");
        if((await db.Categories.Where(c=>c.Id!=id&&c.ParentId==parentId).Select(c=>c.Name).ToListAsync()).Any(name=>string.Equals(name,category.Name,StringComparison.OrdinalIgnoreCase)))
            throw new FinanceRuleException("Já existe uma subcategoria com este nome no destino.");
        var before=JsonSerializer.SerializeToElement(category);
        category.ParentId=parentId;category.Version++;
        await db.SaveChangesAsync();AddAudit(db,"Category",id,"MoveSubcategory",before,category);
        AddReceipt(db,requestId,"MoveSubcategory",hash,id);await db.SaveChangesAsync();await tx.CommitAsync();
    }

    public async Task<DashboardSummary> DashboardAsync(int? year)
    {
        await using var db = open();
        var years = await db.Transactions.Where(x => x.Status == "Confirmed" && (x.Kind == MovementKind.Income || x.Kind == MovementKind.Expense)).Select(x => x.Date.Year).Union(db.IndividualMovements.Where(m=>m.Status=="Confirmed").Select(m=>m.Date.Year)).Distinct().OrderByDescending(x => x).ToListAsync();
        var selected = year ?? years.FirstOrDefault(DateTime.Today.Year);
        if (!years.Contains(selected)) { years.Add(selected); years.Sort((a, b) => b.CompareTo(a)); }
        var rows = await EntryRowsAsync(db, new MovementFilter { From = new(selected, 1, 1), To = new(selected, 12, 31) });
        var financial = rows.Where(x => x.Kind is MovementKind.Income or MovementKind.Expense).ToList();
        var months = Enumerable.Range(1, 12).Select(m => new MonthSummary(m, Sum(financial.Where(x => x.Date.Month == m && x.Kind == MovementKind.Income).Select(x => x.Units)),
            checked(-Sum(financial.Where(x => x.Date.Month == m && x.Kind == MovementKind.Expense).Select(x => x.Units))), financial.Count(x => x.Date.Month == m))).ToList();
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c=>c.Id);
        (Category? Root, Category? Subcategory, long Units) CategoryPart(long? id, long units)
        {
            var category = id is {} value ? categories.GetValueOrDefault(value) : null;
            var root = category?.ParentId is {} parent ? categories.GetValueOrDefault(parent) : category;
            return (root, category?.ParentId is not null ? category : null, units);
        }
        var expense = financial.Where(x => x.Kind == MovementKind.Expense).SelectMany(x=>x.CategoryAllocations.Count>0?x.CategoryAllocations.Select(p=>CategoryPart(p.CategoryId,p.Units)):[CategoryPart(x.CategoryId,checked(-x.Units))])
            .GroupBy(x=>new{CategoryId=x.Root?.Id,Name=x.Root?.Name??"Por classificar"}).Select(g=>new CategorySummary(g.Key.Name,Sum(g.Select(x=>x.Units))) {
                CategoryId=g.Key.CategoryId, DirectUnits=Sum(g.Where(x=>x.Subcategory is null).Select(x=>x.Units)),
                Subcategories=g.Where(x=>x.Subcategory is not null).GroupBy(x=>new{x.Subcategory!.Id,x.Subcategory!.Name})
                    .Select(s=>new CategorySummary(s.Key.Name,Sum(s.Select(x=>x.Units))){CategoryId=s.Key.Id}).OrderByDescending(s=>s.Units).ThenBy(s=>s.Name).ToList()
            }).OrderByDescending(x=>x.Units).ToList();
        var unassigned = await (from p in db.Postings join t in db.Transactions on p.TransactionId equals t.Id where p.AccountId == null && t.Status == "Confirmed"&&!db.IndividualMovements.Any(m=>m.PaymentTransactionId==t.Id) select p.Id).CountAsync();
        unassigned+=(await IndividualRowsAsync(db)).Count(m=>m.Status=="Confirmed"&&m.AccountId==null);
        var coverage = await BankCoverageAsync(db, selected);
        var pendingPurchases=PendingPurchases(db);
        var yearFrom=new DateOnly(selected,1,1);var yearTo=new DateOnly(selected,12,31);
        return new(selected, years, Sum(months.Select(x => x.IncomeUnits)), Sum(months.Select(x => x.ExpenseUnits)), financial.Count, unassigned, months, expense, await AccountsAsync())
            { PendingBankRows = coverage.Pending, BankThrough = coverage.Through, StatementInconsistencies = coverage.Inconsistencies,
              PendingPurchases=await pendingPurchases.CountAsync(e=>e.Date>=yearFrom&&e.Date<=yearTo),
              UndatedPurchases=await pendingPurchases.CountAsync(e=>e.Date==null),
              UnassignedExpenses=financial.Where(r=>r.Kind==MovementKind.Expense&&r.AccountId==null).Select(r=>r.EntryKey).Distinct().Count() };
    }
    private static async Task<(int Pending, DateOnly? Through, List<PendingStatementInconsistency> Inconsistencies)> BankCoverageAsync(FinanceDb db, int year)
    {
        var batches = await db.BankImports.AsNoTracking().Where(b => b.Status != "Voided").ToListAsync();
        var documents = await db.BankDocuments.AsNoTracking().Select(d => new { d.Id, d.StatementJson }).ToDictionaryAsync(d => d.Id, d => JsonSerializer.Deserialize<BankStatement>(d.StatementJson)!);
        var documentNames = await db.BankDocuments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name);
        var accountNames = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
        var entries = (await db.BankImportEntries.AsNoTracking().Select(e => new { e.ImportId, e.Ordinal }).ToListAsync()).Select(e => (e.ImportId, e.Ordinal)).ToHashSet();
        var confirmed = await db.Transactions.AsNoTracking().Where(t => t.Status == "Confirmed").Select(t => new { t.Id, t.Version }).ToDictionaryAsync(t => t.Id, t => t.Version);
        var pending = 0; DateOnly? through = null; var inconsistencies = new List<PendingStatementInconsistency>();
        foreach (var batch in batches.GroupBy(b => (b.DocumentId, b.SectionIndex)).Select(g => g.MaxBy(b => b.Id)!))
        {
            var document = documents[batch.DocumentId]; var section = document.Accounts[batch.SectionIndex];
            if (section.From.Year <= year && section.To.Year >= year && BankStatementConformance.Inconsistency(document, section) is {} inconsistency)
                inconsistencies.Add(new(batch.Id, documentNames[batch.DocumentId], batch.AccountId is {} account ? accountNames.GetValueOrDefault(account, section.DisplayReference) : section.DisplayReference, section.From, section.To, inconsistency));
            if (section.Rows.Any(r => r.Date.Year == year) && BankStatementConformance.CanImport(document, section))
                through = through is null || section.To > through ? section.To : through;
            if (batch.Status != "Draft") continue;
            var choices = string.IsNullOrEmpty(batch.ReviewJson) ? [] : JsonSerializer.Deserialize<BankImportReview>(batch.ReviewJson)!.Rows;
            foreach (var row in section.Rows.Where(r => r.Date.Year == year && r.Units != 0))
            {
                var choice = choices.SingleOrDefault(c => c.Ordinal == row.Ordinal);
                if (entries.Contains((batch.Id, row.Ordinal)) || choice?.Decision == "Exclude") continue;
                if (choice?.Decision == "Link" && choice.ExistingTransactionId is {} id && confirmed.TryGetValue(id, out var version) && version == choice.ExistingVersion) continue;
                pending++;
            }
        }
        return (pending, through, inconsistencies.OrderByDescending(i => i.To).ToList());
    }
    public async Task<string> ExportCsvAsync(MovementFilter filter)
    {
        await using var db = open();var rows=await EntryRowsAsync(db,filter);
        var csv = new StringBuilder("Data;Tipo;Descricao;Conta;Pessoa;Categoria;Subcategoria;MontanteEUR;Estado\r\n");
        static string Cell(string? text)
        {
            text ??= ""; var trimmed = text.TrimStart();
            if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        foreach (var r in rows) csv.AppendLine(string.Join(';', r.Date.ToString("yyyy-MM-dd"), Cell(r.Kind.ToString()), Cell(r.Description), Cell(r.Account ?? "Por associar"), Cell(r.Person), Cell(r.ParentCategory ?? r.Category), Cell(r.ParentCategory is null ? "" : r.Category), Money.FromUnits(r.Units).ToString("0.0000", CultureInfo.InvariantCulture), Cell(r.Status)));
        return csv.ToString();
    }
    private static long Sum(IEnumerable<long> values) { long sum = 0; foreach (var value in values) sum = checked(sum + value); return sum; }
    private static long Units(decimal value) { try { return Money.ToUnits(value); } catch (Exception e) when (e is OverflowException or ArgumentException) { throw new FinanceRuleException("Montante inválido ou com mais de quatro casas decimais."); } }
    private static void Validate(object form) { var results = new List<ValidationResult>(); if (!Validator.TryValidateObject(form, new(form), results, true)) throw new FinanceRuleException(results[0].ErrorMessage ?? "Dados inválidos."); }
    private static async Task<long> Household(FinanceDb db) => await db.Households.Select(x => x.Id).SingleAsync();
    private static string Payload(object data) => LegacyPackage.Hash(JsonSerializer.Serialize(data));
    private static async Task<long?> Receipt(FinanceDb db, Guid id, string action, string hash)
    {
        if (id == Guid.Empty) throw new FinanceRuleException("Identificador de operação inválido.");
        var receipt = await db.Commands.FindAsync(id.ToString("N"));
        if (receipt is null) return null;
        if (receipt.Action != action || receipt.PayloadHash != hash) throw new FinanceRuleException("Esta operação já foi usada com outros dados. Reabre o formulário.");
        return receipt.EntityId;
    }
    private static void AddReceipt(FinanceDb db, Guid id, string action, string hash, long entityId) => db.Add(new CommandReceipt { Id = id.ToString("N"), Action = action, PayloadHash = hash, EntityId = entityId });
    private static void AddAudit(FinanceDb db, string entity, long id, string action, object? before, object after) => db.Add(new AuditChange {
        Entity = entity, EntityId = id, Action = action, Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(new { Before = before, After = after }) });
}

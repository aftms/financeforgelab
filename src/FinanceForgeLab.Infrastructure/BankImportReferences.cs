using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed partial class BankImportService
{
    public async Task<List<BankReference>> ReferencesAsync(long accountId)
    {
        await using var db = open();
        return await db.BankReferences.AsNoTracking().Where(r => r.AccountId == accountId && r.Active)
            .OrderBy(r => r.Merchant).ThenBy(r => r.Code).ThenByDescending(r => r.PurchaseDate).ToListAsync();
    }

    public async Task<long> SaveReferenceAsync(BankReferenceForm form)
    {
        await using var db = open();
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == form.AccountId && a.Active)
            ?? throw new FinanceRuleException("Escolhe uma conta ativa para guardar esta referência.");
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == form.CategoryId && c.HouseholdId == account.HouseholdId && c.Active)
            ?? throw new FinanceRuleException("Escolhe a categoria da referência.");
        if (category.ParentId is long parent && !await db.Categories.AnyAsync(c => c.Id == parent && c.Active))
            throw new FinanceRuleException("A categoria principal está inativa.");
        var merchant = Normalize(form.Merchant); var code = Normalize(form.Code); var description = form.Description.Trim();
        if (merchant.Length > 150 || code.Length > 150 || description.Length is < 2 or > 500 || (merchant.Length < 3 && code.Length < 4))
            throw new FinanceRuleException("Indica uma entidade ou um código específico e uma descrição (até 500 caracteres).");
        if ((form.IsPurchase && (form.PurchaseDate is null || form.Amount is null)) || (form.PurchaseDate is null) != (form.Amount is null) || form.Amount is <= 0)
            throw new FinanceRuleException("Para uma compra pontual, indica a data e o montante positivo cobrado, em EUR.");
        if (form.PurchaseDate is null && code.Length == 0 && IsPaymentPlatform(merchant))
            throw new FinanceRuleException("Amazon e PayPal podem incluir compras diferentes. Indica um código de cobrança ou a data e o montante da compra.");
        long? units = null;
        if (form.Amount is decimal amount)
        {
            try { units = Money.ToUnits(amount); }
            catch (Exception e) when (e is ArgumentException or OverflowException) { throw new FinanceRuleException("Montante inválido; usa até quatro casas decimais."); }
        }
        var reference = form.Id == 0 ? new BankReference { AccountId = account.Id } :
            await db.BankReferences.SingleOrDefaultAsync(r => r.Id == form.Id && r.AccountId == account.Id && r.Active)
                ?? throw new FinanceRuleException("Referência não encontrada.");
        if (form.Id != 0 && reference.Version != form.Version) throw new FinanceRuleException("Esta referência foi alterada. Volta a abri-la.");
        var before = form.Id == 0 ? null : JsonSerializer.Serialize(reference);
        reference.Merchant = merchant; reference.Code = code; reference.Description = description; reference.CategoryId = category.Id;
        reference.PurchaseDate = form.PurchaseDate; reference.AmountUnits = units;
        if (form.Id == 0) db.Add(reference); else reference.Version++;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.SaveChangesAsync();
        db.Add(new AuditChange { Entity = "BankReference", EntityId = reference.Id, Action = "Save", Actor = "Owner", CreatedUtc = DateTime.UtcNow,
            DetailJson = JsonSerializer.Serialize(new { Before = before, After = reference }) });
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return reference.Id;
    }

    public async Task ArchiveReferenceAsync(long id, int version)
    {
        await using var db = open();
        var reference = await db.BankReferences.SingleOrDefaultAsync(r => r.Id == id) ?? throw new FinanceRuleException("Referência não encontrada.");
        if (reference.Version != version) throw new FinanceRuleException("Esta referência foi alterada. Atualiza a lista.");
        reference.Active = false; reference.Version++;
        db.Add(new AuditChange { Entity = "BankReference", EntityId = id, Action = "Archive", Actor = "Owner", CreatedUtc = DateTime.UtcNow,
            DetailJson = JsonSerializer.Serialize(reference) });
        await db.SaveChangesAsync();
    }

    private static bool IsPaymentPlatform(string text) => ContainsWords(text, "PAYPAL") ||
        IsAmazon(text);

    private static bool IsAmazon(string text) => System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(?:AMAZON|AMZN|AMZNMKTP[A-Z]{0,3}|AMAZON(?:EU|ES|DE|FR|IT|UK|COM))\b");

    private static bool ReferenceMatches(BankReference reference, StatementMovement row, List<Category> categories)
    {
        var category = categories.SingleOrDefault(c => c.Id == reference.CategoryId);
        if (category is null || (row.Units < 0 ? MovementKind.Expense : MovementKind.Income) != category.Kind) return false;
        var description = Normalize(row.Description);
        // Amazon bank descriptors can use AMZN instead of AMAZON.
        var merchant = reference.Merchant;
        var matchesMerchant = merchant.Length == 0 || ContainsWords(description, merchant) ||
            (merchant == "AMAZON" && IsAmazon(description));
        if (!matchesMerchant || (reference.Code.Length > 0 && !ContainsWords(description, reference.Code))) return false;
        return reference.PurchaseDate is not DateOnly date ||
            (row.Units != long.MinValue && Math.Abs(row.Units) == reference.AmountUnits &&
                row.Date.DayNumber >= date.DayNumber && row.Date.DayNumber <= date.DayNumber + 7);
    }
}

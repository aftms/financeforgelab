using System.ComponentModel.DataAnnotations;
using FinanceForgeLab.Core;

namespace FinanceForgeLab.Infrastructure;

public sealed class MovementForm
{
    public long? IndividualId { get; set; }
    public long? SourceCategoryId { get; set; }
    public int EvidenceVersion { get; set; }
    public long Id { get; set; }
    public int Version { get; set; }
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public MovementKind Kind { get; set; } = MovementKind.Expense;
    [StringLength(500)] public string Description { get; set; } = "";
    [StringLength(2000)] public string? Notes { get; set; }
    public decimal Amount { get; set; }
    public long? AccountId { get; set; }
    public long? DestinationAccountId { get; set; }
    public long? CategoryId { get; set; }
    public long? PersonId { get; set; }
}
public sealed class AccountForm
{
    public long Id { get; set; }
    public int Version { get; set; }
    public Guid RequestId { get; set; } = Guid.NewGuid();
    [Required(ErrorMessage = "Indica o nome da conta."), StringLength(100)] public string Name { get; set; } = "";
    public string Type { get; set; } = "Bank";
    public bool Active { get; set; } = true;
    public DateOnly BalanceStartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public decimal OpeningBalance { get; set; }
}
public sealed class CategoryForm
{
    public long Id { get; set; }
    public int Version { get; set; }
    public Guid RequestId { get; set; } = Guid.NewGuid();
    [Required(ErrorMessage = "Indica o nome."), StringLength(100)] public string Name { get; set; } = "";
    public MovementKind Kind { get; set; } = MovementKind.Expense;
    public long? ParentId { get; set; }
    public bool Active { get; set; } = true;
}
public enum MovementSort { Date, Description, Category, Account, Kind, Amount }
public sealed class MovementFilter
{
    public long? TransactionId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public long? AccountId { get; set; } // -1 explicitly selects historical postings without an account.
    public long? CategoryId { get; set; }
    public long? PersonId { get; set; }
    public MovementKind? Kind { get; set; }
    public string? Search { get; set; }
    public bool IncludeVoided { get; set; }
    public MovementSort Sort { get; set; } = MovementSort.Date;
    public bool Descending { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 40;
}
public sealed record MovementRow
{
    public long? IndividualId { get; init; }
    public long? SourceCategoryId { get; init; }
    public int EvidenceVersion { get; init; }
    public string? ReconciliationWarning { get; init; }
    public long? DestinationAccountId { get; init; }
    public string DisplayDescription => !string.IsNullOrWhiteSpace(Description) ? Description : OriginalSubcategory ?? Category ?? "Sem detalhe registado";
    public long Id { get; init; }
    public int Version { get; init; }
    public DateOnly Date { get; init; }
    public MovementKind Kind { get; init; }
    public string? Description { get; init; }
    public string? Notes { get; init; }
    public long? AccountId { get; init; }
    public string? Account { get; init; }
    public long? CategoryId { get; init; }
    public string? Category { get; init; }
    public string? ParentCategory { get; init; }
    public string? OriginalCategory { get; init; }
    public string? OriginalSubcategory { get; init; }
    public long? PersonId { get; init; }
    public string? Person { get; init; }
    public long Units { get; init; }
    public string Origin { get; init; } = "";
    public string Status { get; init; } = "";
    public List<MovementCategoryPart> CategoryAllocations { get; init; } = [];
    public long? EvidenceId { get; init; }
    public List<MovementSource> Sources { get; init; } = [];
    public string? Merchant { get; init; }
    public long? PaymentTotalUnits { get; init; }
    public bool IsCategoryEntry => PaymentTotalUnits is not null;
    public string EntryKey => IndividualId is {} individual?$"movement:{individual}":IsCategoryEntry ? $"{Id}:category:{CategoryId}" : Id.ToString();
}
public sealed record MovementCategoryPart(long CategoryId,string Category,string Root,long Units);
public sealed record MovementSource(string Kind,string Description,string Url,bool Download=false)
{
    public EmailSourceReference? Email { get; init; }
}
public sealed record MovementPage(List<MovementRow> Rows, int Total, int Page, int PageSize);
public sealed record AccountSummary(Account Account, long BalanceUnits, int HistoricalBeforeOpening);
public sealed record MonthSummary(int Month, long IncomeUnits, long ExpenseUnits, int Count);
public sealed record CategorySummary(string Name, long Units)
{
    public long? CategoryId { get; init; }
}
public sealed record DashboardSummary(int Year, List<int> Years, long IncomeUnits, long ExpenseUnits, int Count,
    int UnassignedCount, List<MonthSummary> Months, List<CategorySummary> Expenses, List<AccountSummary> Accounts)
{
    public int PendingBankRows { get; init; }
    public int PendingPurchases { get; init; }
    public int UndatedPurchases { get; init; }
    public int UnassignedExpenses { get; init; }
    public List<PendingStatementInconsistency> StatementInconsistencies { get; init; } = [];
    public bool HasReviewPending=>PendingBankRows>0||PendingPurchases>0||UndatedPurchases>0||UnassignedExpenses>0||StatementInconsistencies.Count>0;
    public DateOnly? BankThrough { get; init; }
}
public sealed class FinanceRuleException(string message) : Exception(message);

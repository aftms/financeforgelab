using FinanceForgeLab.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FinanceForgeLab.Infrastructure;

public sealed class FinanceDb(DbContextOptions<FinanceDb> options) : DbContext(options)
{
    public DbSet<Household> Households => Set<Household>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<RecurringExpense> RecurringExpenses => Set<RecurringExpense>();
    public DbSet<FinancialTransaction> Transactions => Set<FinancialTransaction>();
    public DbSet<Posting> Postings => Set<Posting>();
    public DbSet<IndividualMovement> IndividualMovements => Set<IndividualMovement>();
    public DbSet<SourceDataset> SourceDatasets => Set<SourceDataset>();
    public DbSet<SourceSnapshot> SourceSnapshots => Set<SourceSnapshot>();
    public DbSet<SourceRecord> SourceRecords => Set<SourceRecord>();
    public DbSet<LegacyMap> LegacyMaps => Set<LegacyMap>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();
    public DbSet<AuditChange> AuditChanges => Set<AuditChange>();
    public DbSet<OwnerCredential> Owners => Set<OwnerCredential>();
    public DbSet<CommandReceipt> Commands => Set<CommandReceipt>();
    public DbSet<BankDocument> BankDocuments => Set<BankDocument>();
    public DbSet<BankImport> BankImports => Set<BankImport>();
    public DbSet<BankImportEntry> BankImportEntries => Set<BankImportEntry>();
    public DbSet<BankReference> BankReferences => Set<BankReference>();
    public DbSet<PurchaseEvidence> PurchaseEvidence => Set<PurchaseEvidence>();
    public DbSet<PurchaseEvidenceSource> PurchaseEvidenceSources => Set<PurchaseEvidenceSource>();
    public DbSet<ReimbursementClaim> ReimbursementClaims => Set<ReimbursementClaim>();
    public DbSet<ReimbursementAllocation> ReimbursementAllocations => Set<ReimbursementAllocation>();
    public DbSet<AssistantRequest> AssistantRequests => Set<AssistantRequest>();
    public DbSet<AssistantTurn> AssistantTurns => Set<AssistantTurn>();
    public DbSet<AssistantAttachment> AssistantAttachments => Set<AssistantAttachment>();
    public DbSet<AssistantOperation> AssistantOperations => Set<AssistantOperation>();
    public DbSet<AssistantSourceCache> AssistantSources => Set<AssistantSourceCache>();

    public static FinanceDb Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), ForeignKeys = true, Pooling = false }.ToString());
        connection.CreateFunction<string?, string, bool>("ffl_contains", (text, term) => Fold(text).Contains(Fold(term), StringComparison.Ordinal), isDeterministic: true);
        connection.CreateFunction<string?,long,long,long,bool>("ffl_purchase_category",PurchaseAllocationMath.ContainsCategory,isDeterministic:true);
        return new(new DbContextOptionsBuilder<FinanceDb>().UseSqlite(connection, contextOwnsConnection: true).Options);
    }
    private static string Fold(string? text) => new string((text ?? "").Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<RecurringExpense>().HasOne<Household>().WithMany().HasForeignKey(x=>x.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<RecurringExpense>().HasOne<Category>().WithMany().HasForeignKey(x=>x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<RecurringExpense>().HasOne<Account>().WithMany().HasForeignKey(x=>x.AccountId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<RecurringExpense>().HasIndex(x=>new{x.HouseholdId,x.MatchKey}).IsUnique();
        m.Entity<RecurringExpense>().Property(x=>x.Version).IsConcurrencyToken();
        m.Entity<IndividualMovement>().HasOne<FinancialTransaction>().WithMany().HasForeignKey(x=>x.PaymentTransactionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<IndividualMovement>().HasOne<Category>().WithMany().HasForeignKey(x=>x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<IndividualMovement>().HasOne<Account>().WithMany().HasForeignKey(x=>x.AccountId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<IndividualMovement>().HasOne<Account>().WithMany().HasForeignKey(x=>x.DestinationAccountId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<IndividualMovement>().HasOne<Person>().WithMany().HasForeignKey(x=>x.PersonId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<IndividualMovement>().HasIndex(x=>new{x.PaymentTransactionId,x.SourceCategoryId}).IsUnique();
        m.Entity<IndividualMovement>().Property(x=>x.Version).IsConcurrencyToken();
        m.Entity<AssistantRequest>().HasIndex(x=>x.ClientId).IsUnique();
        m.Entity<AssistantRequest>().HasIndex(x=>x.Workflow);
        m.Entity<AssistantSourceCache>().HasIndex(x=>new{x.Kind,x.Scope,x.SourceId,x.Fingerprint}).IsUnique();
        m.Entity<AssistantAttachment>().HasOne<AssistantSourceCache>().WithMany().HasForeignKey(x=>x.SourceCacheId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<AssistantRequest>().Property(x=>x.Version).IsConcurrencyToken();
        m.Entity<AssistantTurn>().HasOne<AssistantRequest>().WithMany().HasForeignKey(x=>x.RequestId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<AssistantTurn>().HasIndex(x=>new{x.RequestId,x.Round}).IsUnique();
        m.Entity<AssistantTurn>().HasIndex(x=>x.ClientId).IsUnique();
        m.Entity<AssistantAttachment>().HasOne<AssistantRequest>().WithMany().HasForeignKey(x=>x.RequestId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<AssistantAttachment>().HasIndex(x=>new{x.RequestId,x.Sha256}).IsUnique();
        m.Entity<AssistantOperation>().HasOne<AssistantRequest>().WithMany().HasForeignKey(x=>x.RequestId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<AssistantOperation>().HasIndex(x=>new{x.RequestId,x.Key}).IsUnique();
        m.HasDbFunction(typeof(PurchaseAllocationMath).GetMethod(nameof(PurchaseAllocationMath.ContainsCategory))!).HasName("ffl_purchase_category");
        m.Entity<ReimbursementClaim>().HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<ReimbursementClaim>().HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => x.ExpenseTransactionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<ReimbursementClaim>().HasIndex(x => x.ExpenseTransactionId).IsUnique();
        m.Entity<ReimbursementClaim>().Property(x => x.Version).IsConcurrencyToken();
        m.Entity<ReimbursementClaim>().ToTable("ReimbursementClaims", t => t.HasCheckConstraint("CK_ReimbursementClaim_Amount", "AmountUnits > 0"));
        m.Entity<ReimbursementAllocation>().HasOne<ReimbursementClaim>().WithMany().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<ReimbursementAllocation>().HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => x.ReceiptTransactionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<ReimbursementAllocation>().HasIndex(x => new { x.ClaimId, x.ReceiptTransactionId }).IsUnique();
        m.Entity<ReimbursementAllocation>().ToTable("ReimbursementAllocations", t => t.HasCheckConstraint("CK_ReimbursementAllocation_Amount", "AmountUnits > 0"));
        m.Entity<PurchaseEvidence>().HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PurchaseEvidence>().HasOne<PurchaseEvidence>().WithMany().HasForeignKey(x => x.ParentEvidenceId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PurchaseEvidence>().HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PurchaseEvidence>().HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PurchaseEvidence>().HasOne<BankReference>().WithMany().HasForeignKey(x => x.BankReferenceId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PurchaseEvidence>().HasIndex(x => new { x.HouseholdId, x.SourceKey }).IsUnique();
        m.Entity<PurchaseEvidence>().Property(x => x.Version).IsConcurrencyToken();
        m.Entity<PurchaseEvidence>().Property(x=>x.CategoryAllocationsJson).HasDefaultValue("[]");
        m.Entity<PurchaseEvidence>().HasIndex(x => new { x.HouseholdId, x.OrderIdentity }).IsUnique();
        m.Entity<PurchaseEvidence>().HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PurchaseEvidence>().HasIndex(x => x.TransactionId).IsUnique();
        m.Entity<PurchaseEvidenceSource>().HasOne<PurchaseEvidence>().WithMany().HasForeignKey(x => x.EvidenceId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PurchaseEvidenceSource>().HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<PurchaseEvidenceSource>().HasIndex(x => new { x.HouseholdId, x.SourceKey }).IsUnique();
        m.Entity<BankReference>().HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<BankReference>().HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<BankReference>().Property(x => x.Version).IsConcurrencyToken();
        m.Entity<BankReference>().HasIndex(x => new { x.AccountId, x.Active });
        m.Entity<BankImport>().HasOne<BankDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<BankImport>().HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<BankImport>().HasOne<Person>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<BankImport>().Property(x => x.Version).IsConcurrencyToken();
        m.Entity<BankImport>().HasIndex(x => new { x.DocumentId, x.SectionIndex }).IsUnique().HasFilter("Status <> 'Voided'");
        m.Entity<BankImport>().HasIndex(x => x.SourceReference);
        m.Entity<BankImport>().ToTable("BankImports", t => t.HasCheckConstraint("CK_BankImport_Status", "Status IN ('Draft','Imported','Voided')"));
        m.Entity<BankImportEntry>().HasOne<BankImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<BankImportEntry>().HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<BankImportEntry>().HasIndex(x => new { x.ImportId, x.Ordinal }).IsUnique();
        m.Entity<Household>().ToTable("Households", t => t.HasCheckConstraint("CK_Household_Currency", "length(Currency) = 3"));
        m.Entity<Person>().HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Category>().HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Category>().HasOne<Category>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Category>().Property(x => x.Version).IsConcurrencyToken();
        m.Entity<Account>().Property(x => x.Version).IsConcurrencyToken();
        m.Entity<OwnerCredential>().ToTable("Owners", t => t.HasCheckConstraint("CK_Owner_Single", "Id = 1"));
        m.Entity<Category>().ToTable("Categories", t => {
            t.HasCheckConstraint("CK_Category_Kind", "Kind IN (1,2)");
            t.HasCheckConstraint("CK_Category_Parent", "ParentId IS NULL OR ParentId <> Id");
        });
        m.Entity<Account>().HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Account>().ToTable("Accounts", t => t.HasCheckConstraint("CK_Account_Currency", "length(Currency) = 3"));
        m.Entity<FinancialTransaction>().ToTable("Transactions", t => {
            t.HasCheckConstraint("CK_Transaction_Kind", "Kind BETWEEN 1 AND 5");
            t.HasCheckConstraint("CK_Transaction_Status", "Status IN ('Confirmed','Voided')");
        });
        m.Entity<FinancialTransaction>().HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<FinancialTransaction>().HasOne<Person>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<FinancialTransaction>().Property(x => x.Version).IsConcurrencyToken();
        m.Entity<FinancialTransaction>().HasIndex(x => x.Date);
        m.Entity<Posting>().HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Posting>().HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Posting>().HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Posting>().ToTable("Postings", t => {
            t.HasCheckConstraint("CK_Posting_Amount", "AmountUnits <> 0");
            t.HasCheckConstraint("CK_Posting_Currency", "length(Currency) = 3");
        });
        m.Entity<Posting>().HasIndex(x => new { x.AccountId, x.Date });
        m.Entity<Posting>().HasIndex(x => new { x.CategoryId, x.Date });
        m.Entity<SourceDataset>().HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<SourceSnapshot>().HasOne<SourceDataset>().WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<SourceSnapshot>().HasIndex(x => new { x.DatasetId, x.PackageSha256 }).IsUnique();
        m.Entity<SourceRecord>().HasOne<SourceSnapshot>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<SourceRecord>().HasIndex(x => new { x.SnapshotId, x.Table, x.LegacyId }).IsUnique();
        m.Entity<LegacyMap>().HasOne<SourceDataset>().WithMany().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<LegacyMap>().HasIndex(x => new { x.DatasetId, x.Table, x.LegacyId }).IsUnique();
        m.Entity<ImportBatch>().HasOne<SourceSnapshot>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<ImportRow>().HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<ImportRow>().HasOne<SourceRecord>().WithMany().HasForeignKey(x => x.SourceRecordId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<ImportRow>().HasOne<FinancialTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<ImportRow>().HasIndex(x => new { x.BatchId, x.SourceRecordId }).IsUnique();
    }
}

// Design-time context does not open or update any database.
public sealed class FinanceDbFactory : IDesignTimeDbContextFactory<FinanceDb>
{
    public FinanceDb CreateDbContext(string[] args) => FinanceDb.Open("design-time.sqlite");
}

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
    public DbSet<FinancialTransaction> Transactions => Set<FinancialTransaction>();
    public DbSet<Posting> Postings => Set<Posting>();
    public DbSet<SourceDataset> SourceDatasets => Set<SourceDataset>();
    public DbSet<SourceSnapshot> SourceSnapshots => Set<SourceSnapshot>();
    public DbSet<SourceRecord> SourceRecords => Set<SourceRecord>();
    public DbSet<LegacyMap> LegacyMaps => Set<LegacyMap>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();
    public DbSet<AuditChange> AuditChanges => Set<AuditChange>();
    public DbSet<OwnerCredential> Owners => Set<OwnerCredential>();
    public DbSet<CommandReceipt> Commands => Set<CommandReceipt>();

    public static FinanceDb Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), ForeignKeys = true, Pooling = false }.ToString());
        connection.CreateFunction<string?, string, bool>("ffl_contains", (text, term) => Fold(text).Contains(Fold(term), StringComparison.Ordinal), isDeterministic: true);
        return new(new DbContextOptionsBuilder<FinanceDb>().UseSqlite(connection, contextOwnsConnection: true).Options);
    }
    private static string Fold(string? text) => new string((text ?? "").Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();

    protected override void OnModelCreating(ModelBuilder m)
    {
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

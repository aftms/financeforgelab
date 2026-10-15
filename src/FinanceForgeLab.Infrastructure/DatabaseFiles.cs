using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FinanceForgeLab.Infrastructure;

public static class DatabaseFiles
{
    // SQLite's backup API includes committed WAL state; an ordinary file copy would not.
    public static void Backup(string source, string destination)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Base de dados inexistente.");
        using (new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) { }
        using var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(source), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(destination), Pooling = false }.ToString());
        from.Open(); to.Open(); from.BackupDatabase(to);
    }

    public static async Task<bool> CheckIntegrityAsync(FinanceDb db, CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = "PRAGMA integrity_check";
        if (!Equals(await cmd.ExecuteScalarAsync(ct), "ok")) return false;
        cmd.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return !await reader.ReadAsync(ct);
    }
}

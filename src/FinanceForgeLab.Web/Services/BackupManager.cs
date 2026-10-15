using FinanceForgeLab.Infrastructure;

namespace FinanceForgeLab.Web.Services;

public sealed class BackupManager(string databasePath, string directory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string> CreateAsync(string prefix = "manual")
    {
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite");
            DatabaseFiles.Backup(databasePath, path);
            await using var check = FinanceDb.Open(path);
            if (!await DatabaseFiles.CheckIntegrityAsync(check)) throw new IOException("BackupIntegrityFailed");
            return path;
        }
        finally { gate.Release(); }
    }
    public List<(string Name, DateTime Date, long Length)> List() => Directory.Exists(directory)
        ? new DirectoryInfo(directory).GetFiles("*.sqlite").OrderByDescending(x => x.LastWriteTimeUtc).Select(x => (x.Name, x.LastWriteTimeUtc, x.Length)).ToList() : [];
    public void ApplyRetention()
    {
        if (!Directory.Exists(directory)) return;
        // These paths are enumerated only in the configured backup folder, never supplied by a request.
        foreach (var group in new[] { ("daily-*.sqlite", 7), ("weekly-*.sqlite", 4) })
        foreach (var file in new DirectoryInfo(directory).GetFiles(group.Item1).OrderByDescending(x => x.LastWriteTimeUtc).Skip(group.Item2)) file.Delete();
    }
}
public sealed class DailyBackup(BackupManager backups, ILogger<DailyBackup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var files = backups.List();
                if (!files.Any(x => x.Name.StartsWith("daily-", StringComparison.Ordinal) && x.Date.Date == DateTime.UtcNow.Date)) await backups.CreateAsync("daily");
                if (!files.Any(x => x.Name.StartsWith("weekly-", StringComparison.Ordinal) && x.Date > DateTime.UtcNow.AddDays(-7))) await backups.CreateAsync("weekly");
                backups.ApplyRetention();
            }
            catch (Exception e) { logger.LogError("Não foi possível criar a cópia automática ({Type}).", e.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}

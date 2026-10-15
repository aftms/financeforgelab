using System.Text.Json;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("FinanceForgeLab migration v1\n" +
            "simulate --package <pasta> --output <nova-pasta> [--baseline <sqlite>]\n" +
            "apply --package <pasta> --output <nova-pasta-de-relatorio> --target <sqlite> --currency EUR\n" +
            "backup --database <sqlite> --output <novo-ficheiro>\n" +
            "restore --database <backup-sqlite> --output <novo-ficheiro>\n" +
            "decrypt --database <ficheiro.fflbackup> --output <novo-ficheiro.sqlite> (palavra-passe pedida na consola)\n" +
            "check --database <sqlite> (verificações de leitura e resumo sem dados pessoais)\n" +
            "inspect-statement --file <extrato-pdf> --output <novo-relatorio-json-privado>\n" +
            "Os relatórios detalhados contêm valores financeiros. Guardar numa pasta privada.");
        return 0;
    }
    string? reportPath = null;
    MigrationReport? report = null;
    try
    {
        var mode = args[0];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Argumentos inválidos ou repetidos.");
        string Required(string key) => options.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? Path.GetFullPath(v) : throw new ArgumentException($"Falta {key}.");
        if (mode == "inspect-statement")
        {
            if (options.Keys.Any(k => k is not ("--file" or "--output"))) throw new ArgumentException("Opção desconhecida.");
            var statementOutput = Required("--output");
            if (File.Exists(statementOutput)) throw new IOException("Escolhe um relatório novo.");
            var statement = NovoBancoStatementReader.Read(Required("--file"));
            await using (var file = new FileStream(statementOutput, FileMode.CreateNew, FileAccess.Write)) await JsonSerializer.SerializeAsync(file, statement, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(JsonSerializer.Serialize(new { statement.Valid, statement.Pages, Accounts = statement.Accounts.Count, Rows = statement.Accounts.Sum(x => x.Rows.Count), Errors = statement.Errors.Count + statement.Accounts.Sum(x => x.Errors.Count) }));
            return statement.Valid ? 0 : 2;
        }
        if (mode == "check")
        {
            if (options.Keys.Any(k => k != "--database")) throw new ArgumentException("Opção desconhecida.");
            var path = Required("--database"); if (!File.Exists(path)) throw new FileNotFoundException();
            var service = new FinanceService(() => FinanceDb.Open(path)); var dashboard = await service.DashboardAsync(null);
            var page = await service.MovementsAsync(new()); await using var db = FinanceDb.Open(path);
            Console.WriteLine(JsonSerializer.Serialize(new { Movements = page.Total, dashboard.Years, dashboard.UnassignedCount,
                Categories = await db.Categories.CountAsync(), Accounts = await db.Accounts.CountAsync(), Owners = await db.Owners.CountAsync(),
                SourceRows = await db.SourceRecords.CountAsync(), Integrity = await DatabaseFiles.CheckIntegrityAsync(db),
                SchemaCurrent = !(await db.Database.GetPendingMigrationsAsync()).Any() })); return 0;
        }
        if (mode == "decrypt")
        {
            if (options.Keys.Any(k => k is not ("--database" or "--output"))) throw new ArgumentException("Opção desconhecida.");
            var destination = Required("--output");
            if (File.Exists(destination)) throw new IOException("Escolhe um ficheiro novo.");
            Console.Write("Palavra-passe da cópia: ");
            var password = "";
            if (Console.IsInputRedirected) password = Console.ReadLine() ?? "";
            else { ConsoleKeyInfo key; while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter) { if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password = password[..^1]; } else if (!char.IsControl(key.KeyChar)) password += key.KeyChar; } Console.WriteLine(); }
            var content = BackupCrypto.Decrypt(await File.ReadAllBytesAsync(Required("--database")), password);
            await using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) await file.WriteAsync(content);
            await using var restored = FinanceDb.Open(destination);
            if (!await DatabaseFiles.CheckIntegrityAsync(restored)) throw new IOException("IntegrityCheckFailed");
            Console.WriteLine("Cópia decifrada num novo ficheiro e integridade verificada."); return 0;
        }
        if (mode is "backup" or "restore")
        {
            if (options.Keys.Any(k => k is not ("--database" or "--output"))) throw new ArgumentException("Opção desconhecida.");
            var source = Required("--database"); var destination = Required("--output");
            DatabaseFiles.Backup(source, destination);
            await using var restored = FinanceDb.Open(destination);
            if (!await DatabaseFiles.CheckIntegrityAsync(restored)) throw new InvalidOperationException("IntegrityCheckFailed");
            Console.WriteLine("Cópia SQLite criada e integridade verificada."); return 0;
        }
        if (mode is not ("simulate" or "apply")) throw new ArgumentException("Comando desconhecido.");
        if (options.Keys.Any(k => k is not ("--package" or "--output" or "--baseline" or "--target" or "--currency"))) throw new ArgumentException("Opção desconhecida.");
        if (mode == "simulate" && options.ContainsKey("--target") || mode == "apply" && options.ContainsKey("--baseline")) throw new ArgumentException("Opção incompatível com o modo.");
        if (mode == "apply" && options.GetValueOrDefault("--currency") != "EUR") throw new ArgumentException("A execução requer --currency EUR explícito.");
        if (options.ContainsKey("--currency") && options["--currency"] != "EUR") throw new ArgumentException("A origem atual foi confirmada apenas em EUR.");
        var packageDir = Required("--package");
        var output = Required("--output");
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("A pasta de resultado já existe; escolher uma pasta nova.");
        var loaded = await PackageFiles.LoadAsync(packageDir);
        var package = loaded.Package; var backupPath = loaded.BackupPath; var packageHash = loaded.PackageSha256;
        report = LegacyValidation.Validate(package);
        report.Mode = mode == "simulate" ? "Simulation" : "Apply";
        report.PackageSha256 = packageHash;
        report.BackupVerified = true;
        Directory.CreateDirectory(output);
        reportPath = Path.Combine(output, "validation-private.json");
        if (report.HasErrors) { report.Status = "Blocked"; await WriteReportAsync(reportPath, report); return 2; }

        string target;
        if (mode == "simulate")
        {
            target = Path.Combine(output, "simulation.sqlite");
            if (options.ContainsKey("--baseline")) DatabaseFiles.Backup(Required("--baseline"), target);
        }
        else
        {
            target = Required("--target");
            if (!string.Equals(Path.GetExtension(target), ".sqlite", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("O destino deve ter extensão .sqlite.");
            if (File.Exists(target)) DatabaseFiles.Backup(target, Path.Combine(output, "before.sqlite"));
        }
        await using (var db = FinanceDb.Open(target))
        {
            // Explicit CLI maintenance only. The future web server must not run migrations at startup.
            await db.Database.MigrateAsync();
            report = await new LegacyMigrator(db).RunAsync(package, packageHash, backupPath, report.Mode);
        }
        await WriteReportAsync(reportPath, report);
        if (report.Status == "Validated")
        {
            var recovery = Path.Combine(output, "recovery-check.sqlite");
            DatabaseFiles.Backup(target, recovery);
            await using var a = FinanceDb.Open(target);
            await using var b = FinanceDb.Open(recovery);
            var restored = await DatabaseFiles.CheckIntegrityAsync(b)
                && await a.Transactions.CountAsync() == await b.Transactions.CountAsync()
                && await a.SourceRecords.CountAsync() == await b.SourceRecords.CountAsync()
                && (await a.Postings.OrderBy(x => x.Id).Select(x => x.AmountUnits).ToListAsync())
                    .SequenceEqual(await b.Postings.OrderBy(x => x.Id).Select(x => x.AmountUnits).ToListAsync());
            if (!restored) { report.Status = "Failed"; report.ErrorCode = "RecoveryCheckFailed"; }
            await File.WriteAllTextAsync(Path.Combine(output, "recovery-check.json"), JsonSerializer.Serialize(new { Verified = restored }));
            await WriteReportAsync(reportPath, report);
        }
        Console.WriteLine($"Estado: {report.Status}; inseridos: {report.Inserted.Values.Sum()}; ignorados: {report.Skipped.Values.Sum()}; " +
            $"avisos: {report.Issues.Count(x => x.Severity == "Warning")}; erros: {report.Issues.Count(x => x.Severity == "Error")}.");
        return report.Status == "Validated" ? 0 : 2;
    }
    catch (Exception ex)
    {
        // Exception messages from providers can contain paths or source content. Keep console output technical and minimal.
        Console.Error.WriteLine($"Operação interrompida ({ex.GetType().Name}). Nenhum sucesso de migração foi declarado.");
        if (reportPath is not null && report is not null)
        {
            report.Status = "Failed"; report.ErrorCode = ex.GetType().Name;
            await WriteReportAsync(reportPath, report);
        }
        return 1;
    }
}

static async Task WriteReportAsync(string path, MigrationReport report)
{
    report.FinishedUtc ??= DateTime.UtcNow;
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}

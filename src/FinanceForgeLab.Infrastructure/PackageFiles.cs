using System.Security.Cryptography;
using System.Text.Json;

namespace FinanceForgeLab.Infrastructure;

public sealed record LoadedPackage(LegacyPackage Package, string PackageSha256, string BackupPath);

public static class PackageFiles
{
    private sealed record Manifest(string SourceSha256, string PackageSha256, string DatasetId, int Tables, int Records);

    public static async Task<LoadedPackage> LoadAsync(string directory)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "package.json"));
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json")))
            ?? throw new FormatException("Manifesto ausente.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(hash, manifest.PackageSha256, StringComparison.Ordinal)) throw new FormatException("PackageHashMismatch");
        var package = JsonSerializer.Deserialize<LegacyPackage>(bytes) ?? throw new FormatException("Pacote vazio.");
        if (package.DatasetId != manifest.DatasetId || package.SourceSha256 != manifest.SourceSha256
            || package.Tables.Count != manifest.Tables || package.Tables.Sum(t => t.Value.Count) != manifest.Records)
            throw new FormatException("ManifestMetadataMismatch");
        var backupPath = Path.Combine(Path.GetFullPath(directory), "source.accdb");
        await using var source = File.OpenRead(backupPath);
        if (source.Length != package.SourceLength || Convert.ToHexString(await SHA256.HashDataAsync(source)) != package.SourceSha256)
            throw new FormatException("SourceBackupMismatch");
        return new(package, hash, backupPath);
    }
}

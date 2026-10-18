using System.Diagnostics;
using System.Text;
using FinanceForgeLab.Infrastructure;

namespace FinanceForgeLab.Web.Services;

public static class PurchaseEvidenceReader
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public static async Task<string> ReadAsync(string name, byte[] content)
    {
        if (content.Length is 0 or > MaxBytes) throw new FinanceRuleException("Escolhe um ficheiro até 10 MB.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is ".csv" or ".txt" or ".tsv")
        {
            try { return Limit(new UTF8Encoding(false, true).GetString(content).TrimStart('\uFEFF')); }
            catch (DecoderFallbackException) { throw new FinanceRuleException("Guarda a lista em UTF-8 e volta a carregá-la."); }
        }
        var png = content.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpg = content.Length > 3 && content[0] == 255 && content[1] == 216 && content[2] == 255;
        if (!(png && extension == ".png") && !(jpg && extension is ".jpg" or ".jpeg"))
            throw new FinanceRuleException("Usa uma imagem PNG/JPG ou uma lista TXT/CSV/TSV.");
        var executable = Environment.GetEnvironmentVariable("FFL_TESSERACT") ??
            (OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe") : "tesseract");
        var input = Path.Combine(Path.GetTempPath(), "ffl-purchase-" + Guid.NewGuid().ToString("N") + (png ? ".png" : ".jpg"));
        try
        {
            await File.WriteAllBytesAsync(input, content);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var argument in new[] { input, "stdout", "-l", "eng", "--psm", "3" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new FinanceRuleException("Não foi possível iniciar a leitura da imagem.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, errors); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw new FinanceRuleException("A leitura demorou demasiado. Usa uma imagem mais pequena ou cola o texto."); }
            if (process.ExitCode != 0) throw new FinanceRuleException("Não foi possível ler esta imagem. Experimenta uma captura mais nítida.");
            return Limit(await output);
        }
        catch (System.ComponentModel.Win32Exception) { throw new FinanceRuleException("O leitor de imagens Tesseract não está disponível. Podes colar a lista ou enviar a imagem na conversa."); }
        finally { if (File.Exists(input)) File.Delete(input); }
    }
    private static string Limit(string text) => text.Length > 100_000 ? throw new FinanceRuleException("A lista é demasiado longa. Divide-a em ficheiros menores.") : text;
}

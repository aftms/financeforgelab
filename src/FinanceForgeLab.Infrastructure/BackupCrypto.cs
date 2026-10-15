using System.Security.Cryptography;
using System.Text;

namespace FinanceForgeLab.Infrastructure;

public static class BackupCrypto
{
    private static readonly byte[] Header = "FFLB1"u8.ToArray();
    public static byte[] Encrypt(byte[] content, string password)
    {
        if (password.Length < 12) throw new FinanceRuleException("Usa uma palavra-passe de cópia com pelo menos 12 caracteres.");
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var encrypted = new byte[content.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 210_000, HashAlgorithmName.SHA256, 32);
        try { using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, content, encrypted, tag, Header); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return [..Header, ..salt, ..nonce, ..tag, ..encrypted];
    }
    public static byte[] Decrypt(byte[] content, string password)
    {
        if (content.Length < 49 || !content.AsSpan(0, 5).SequenceEqual(Header)) throw new FormatException("Formato de cópia inválido.");
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), content.AsSpan(5, 16), 210_000, HashAlgorithmName.SHA256, 32);
        var plain = new byte[content.Length - 49];
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(content.AsSpan(21, 12), content.AsSpan(49), content.AsSpan(33, 16), plain, Header); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return plain;
    }
}

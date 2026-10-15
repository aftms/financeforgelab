using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Web.Services;

public sealed class OwnerAuth(Func<FinanceDb> open)
{
    private readonly PasswordHasher<OwnerCredential> hasher = new();
    public async Task<bool> ExistsAsync() { await using var db = open(); return await db.Owners.AnyAsync(); }
    public async Task<bool> SetupAsync(string password, string confirm)
    {
        if (password.Length < 12 || password.Length > 200 || password != confirm) return false;
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        if (await db.Owners.AnyAsync()) return false;
        var owner = new OwnerCredential(); owner.PasswordHash = hasher.HashPassword(owner, password); db.Add(owner);
        await db.SaveChangesAsync(); await tx.CommitAsync(); return true;
    }
    public async Task<bool> VerifyAsync(string password)
    {
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var owner = await db.Owners.SingleOrDefaultAsync();
        if (owner is null || owner.LockoutUntilUtc > DateTime.UtcNow) return false;
        if (password.Length > 200 || hasher.VerifyHashedPassword(owner, owner.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            owner.FailedAttempts++;
            if (owner.FailedAttempts >= 5) { owner.LockoutUntilUtc = DateTime.UtcNow.AddMinutes(5); owner.FailedAttempts = 0; }
            await db.SaveChangesAsync(); await tx.CommitAsync(); return false;
        }
        owner.FailedAttempts = 0; owner.LockoutUntilUtc = null;
        await db.SaveChangesAsync(); await tx.CommitAsync(); return true;
    }
}

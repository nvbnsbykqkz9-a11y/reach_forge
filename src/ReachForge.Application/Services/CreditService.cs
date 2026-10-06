using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;

namespace ReachForge.Application.Services;

public interface ICreditService
{
    Task<CreditAccount> GetAccountAsync(CancellationToken ct);

    /// <summary>推定消費をホールドする。確定しないまま破棄すると解放される（失敗・ブロック時はクレジットを消費しない）。</summary>
    Task<CreditHold> HoldAsync(int estimate, CancellationToken ct);
}

public sealed class CreditHold(CreditService owner, int amount) : IAsyncDisposable
{
    private bool _settled;

    public int Amount { get; } = amount;

    public async Task<int> CommitAsync(int actual, CancellationToken ct)
    {
        if (_settled) return 0;
        _settled = true;
        return await owner.CommitAsync(Amount, actual, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_settled) return;
        _settled = true;
        await owner.ReleaseAsync(Amount, CancellationToken.None);
    }
}

public sealed class CreditService(IAppDbContext db, ITenantContext tenant, TimeProvider clock) : ICreditService
{
    public const int DefaultMonthlyGrant = 1500;

    public async Task<CreditAccount> GetAccountAsync(CancellationToken ct)
    {
        var account = await db.CreditAccounts.FirstOrDefaultAsync(a => a.TenantId == tenant.TenantId, ct);
        if (account is not null) return account;

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        account = CreditAccount.Open(tenant.TenantId, DefaultMonthlyGrant, new DateOnly(today.Year, today.Month, 1));
        db.CreditAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<CreditHold> HoldAsync(int estimate, CancellationToken ct)
    {
        await MutateAsync(a => a.Hold(estimate), ct);
        return new CreditHold(this, estimate);
    }

    internal async Task<int> CommitAsync(int held, int actual, CancellationToken ct)
    {
        var charged = 0;
        await MutateAsync(a => charged = a.Commit(held, actual), ct);
        return charged;
    }

    internal Task ReleaseAsync(int held, CancellationToken ct) => MutateAsync(a => a.Release(held), ct);

    /// <summary>楽観排他で競合したら再読込して再試行する。</summary>
    private async Task MutateAsync(Action<CreditAccount> mutate, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var account = await GetAccountAsync(ct);
            mutate(account);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < 3)
            {
                foreach (var entry in ex.Entries) await entry.ReloadAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new DomainException(ErrorCodes.SysUnexpected, "クレジットの更新が混み合っています。もう一度お試しください。");
            }
        }
    }
}

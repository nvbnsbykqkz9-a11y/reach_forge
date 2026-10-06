using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Guardrails;

namespace ReachForge.Application.Services;

/// <summary>生成・検査に注入するブランド知識（ブランドプロファイル＋商品＋キャンペーン）。</summary>
public sealed record BrandContext(BrandProfile Profile, IReadOnlyList<Product> Products, Campaign? Campaign)
{
    public GuardrailContext ToGuardrailContext() => new()
    {
        Industry = Profile.Industry,
        NgWords = Profile.NgWords,
        MustPhrases = Profile.MustPhrases,
        IsAdvertisement = Campaign?.IsAdvertisement ?? false,
        KnownPrices = Products.Where(p => p.Price is not null).Select(p => p.Price!.Value).ToList(),
    };
}

public interface IBrandContextProvider
{
    Task<BrandContext> BuildAsync(Guid workspaceId, IReadOnlyCollection<Guid>? productIds, Guid? campaignId,
        CancellationToken ct);
}

public sealed class BrandContextProvider(IAppDbContext db) : IBrandContextProvider
{
    public async Task<BrandContext> BuildAsync(Guid workspaceId, IReadOnlyCollection<Guid>? productIds, Guid? campaignId,
        CancellationToken ct)
    {
        var profile = await db.BrandProfiles.FirstOrDefaultAsync(b => b.WorkspaceId == workspaceId, ct)
                      ?? new BrandProfile { WorkspaceId = workspaceId, BrandName = "" };

        // 商品が指定されていない場合も、価格の事実性チェックのためワークスペースの全商品を参照する
        var products = await db.Products.Where(p => p.WorkspaceId == workspaceId).ToListAsync(ct);
        if (productIds is { Count: > 0 })
        {
            products = [.. products.OrderByDescending(p => productIds.Contains(p.Id))];
        }

        var campaign = campaignId is null ? null : await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct);
        return new BrandContext(profile, products, campaign);
    }
}

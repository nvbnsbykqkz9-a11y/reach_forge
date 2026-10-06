using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Security;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Services;

public sealed record UsageBreakdown(string Label, int Credits);

public sealed record UsageSummary(CreditAccount Account, IReadOnlyList<UsageBreakdown> ByFeature, int XPostsThisPeriod,
    decimal EstimatedSnsCostUsd, DateOnly? ProjectedExhaustion);

/// <summary>ワークスペース・ブランド設定・利用量（F-02 / F-13 / SCR-04 / SCR-14）。</summary>
public sealed class WorkspaceService(IAppDbContext db, ITenantContext tenant, ICreditService credits, TimeProvider clock)
{
    public Task<Workspace> CurrentAsync(CancellationToken ct) =>
        db.Workspaces.FirstAsync(w => w.Id == tenant.WorkspaceId, ct);

    public Task<Tenant> TenantAsync(CancellationToken ct) => db.Tenants.FirstAsync(t => t.Id == tenant.TenantId, ct);

    public async Task<BrandProfile> GetBrandAsync(CancellationToken ct) =>
        await db.BrandProfiles.FirstOrDefaultAsync(b => b.WorkspaceId == tenant.WorkspaceId, ct)
        ?? new BrandProfile { TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, BrandName = "" };

    /// <summary>ブランドプロファイルを保存する。保存のたびに版を上げる。</summary>
    public async Task<BrandProfile> SaveBrandAsync(BrandProfile input, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageBrand);
        var profile = await db.BrandProfiles.FirstOrDefaultAsync(b => b.WorkspaceId == tenant.WorkspaceId, ct);
        if (profile is null)
        {
            profile = new BrandProfile { TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, BrandName = input.BrandName };
            db.BrandProfiles.Add(profile);
        }
        else
        {
            profile.Version++;
        }

        profile.BrandName = input.BrandName;
        profile.Industry = input.Industry;
        profile.WebsiteUrl = input.WebsiteUrl;
        profile.Tone = input.Tone;
        profile.Personas = [.. input.Personas];
        profile.NgWords = Clean(input.NgWords);
        profile.MustPhrases = Clean(input.MustPhrases);
        profile.PreferredHashtags = Clean(input.PreferredHashtags);
        db.Record(tenant, "brand.updated", nameof(BrandProfile), profile.Id, $"v{profile.Version}");
        await db.SaveChangesAsync(ct);
        return profile;

        static List<string> Clean(IEnumerable<string> xs) =>
            [.. xs.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct()];
    }

    public async Task<IReadOnlyList<Product>> ProductsAsync(CancellationToken ct) =>
        await db.Products.Where(p => p.WorkspaceId == tenant.WorkspaceId).OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<Product> SaveProductAsync(Product input, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageBrand);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == input.Id, ct);
        if (product is null)
        {
            product = new Product { TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Name = input.Name };
            db.Products.Add(product);
        }
        product.Name = input.Name;
        product.Description = input.Description;
        product.Price = input.Price;
        product.AvailableFrom = input.AvailableFrom;
        product.AvailableUntil = input.AvailableUntil;
        product.Url = input.Url;
        await db.SaveChangesAsync(ct);
        return product;
    }

    public async Task<IReadOnlyList<Campaign>> CampaignsAsync(CancellationToken ct) =>
        await db.Campaigns.Where(c => c.WorkspaceId == tenant.WorkspaceId).OrderBy(c => c.Name).ToListAsync(ct);

    public async Task<UsageSummary> UsageAsync(CancellationToken ct)
    {
        var account = await credits.GetAccountAsync(ct);
        var since = new DateTimeOffset(account.PeriodStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var generations = await db.AiGenerations
            .Where(g => g.CreatedAt >= since && g.Status == AiGenerationStatus.Succeeded)
            .Select(g => new { g.TaskType, g.Credits })
            .ToListAsync(ct);
        var byFeature = generations
            .GroupBy(g => g.TaskType)
            .Select(g => new UsageBreakdown(TaskLabel(g.Key), g.Sum(x => x.Credits)))
            .OrderByDescending(x => x.Credits)
            .ToList();

        var xPosts = await db.PostVariants
            .Where(v => v.Platform == SocialPlatform.X && v.Status == VariantStatus.Published && v.PublishedAt >= since)
            .Select(v => new { v.Body, v.UrlCostAcknowledged })
            .ToListAsync(ct);
        var x = Domain.Platforms.PlatformCatalog.Get(SocialPlatform.X);
        var snsCost = xPosts.Sum(p => Domain.Platforms.PostText.ContainsUrl(p.Body) ? x.CostPerPostWithUrlUsd!.Value : x.CostPerPostUsd!.Value);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return new UsageSummary(account, byFeature, xPosts.Count, snsCost, account.ProjectedExhaustionDate(today));
    }

    public static string TaskLabel(AiTaskType t) => t switch
    {
        AiTaskType.Ideation => "企画",
        AiTaskType.Copy => "投稿文",
        AiTaskType.Variant => "SNS別の変換",
        AiTaskType.Image => "画像",
        AiTaskType.ImageEdit => "画像編集",
        AiTaskType.Video => "動画",
        AiTaskType.Tts => "ナレーション",
        AiTaskType.Reply => "返信案",
        AiTaskType.Report => "レポート",
        AiTaskType.Classify => "分類",
        AiTaskType.Judge => "品質評価",
        AiTaskType.Summarize => "要約",
        _ => t.ToString(),
    };
}

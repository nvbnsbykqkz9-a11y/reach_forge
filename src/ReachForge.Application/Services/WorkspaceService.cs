using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Security;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Services;

/// <summary>ワークスペース・ブランド設定（F-02 / SCR-04）。</summary>
public sealed class WorkspaceService(IAppDbContext db, ITenantContext tenant)
{
    public Task<Workspace> CurrentAsync(CancellationToken ct) =>
        db.Workspaces.FirstAsync(w => w.Id == tenant.WorkspaceId, ct);

    public Task<Tenant> TenantAsync(CancellationToken ct) => db.Tenants.FirstAsync(t => t.Id == tenant.TenantId, ct);

    /// <summary>テナントのタイムゾーン（画面の日時の表示に使う）。</summary>
    public async Task<TimeZoneInfo> TenantTimeZoneAsync(CancellationToken ct)
    {
        var id = await db.Tenants.Where(t => t.Id == tenant.TenantId).Select(t => t.TimeZoneId).FirstOrDefaultAsync(ct);
        return TimeZones.Find(id);
    }

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
        profile.BrandColors = [.. Clean(input.BrandColors).Select(c => c.ToUpperInvariant())
            .Where(c => System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9A-F]{6}$")).Take(5)];
        // お手本（Few-shot）は A/B テストの勝ちパターンから登録される。画面では有効・無効と削除だけ行う
        profile.FewShotExamples = [.. input.FewShotExamples
            .Where(e => !string.IsNullOrWhiteSpace(e.Text))
            .Select(e => new FewShotExample { Id = e.Id, Text = e.Text.Trim(), Reason = e.Reason, SourceAbTestId = e.SourceAbTestId, Enabled = e.Enabled })
            .Take(20)];
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
}

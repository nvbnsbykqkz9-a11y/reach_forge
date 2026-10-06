using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Application.Tests;

public class MetricsTests
{
    [Fact]
    public async Task Collects_at_checkpoints_and_account_followers_daily()
    {
        await using var f = await AppFixture.CreateAsync();
        Guid variantId;
        await using (var scope = f.Scope())
        {
            var db = f.Get<IAppDbContext>(scope);
            var channel = await db.Channels.FirstAsync(c => c.Platform == SocialPlatform.Threads);
            var master = new MasterPost
            {
                TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, Title = "t", CoreMessage = "秋のお知らせです。",
            };
            var v = PostVariant.Create(master, channel, master.CoreMessage, ["cafe"]);
            v.Schedule(f.Clock.GetUtcNow().AddMinutes(1), f.Clock.GetUtcNow(), requiresApproval: false);
            db.MasterPosts.Add(master);
            db.PostVariants.Add(v);
            await db.SaveChangesAsync();
            variantId = v.Id;
        }
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await using (var worker = f.Scope(c => c.IsSystem = true))
        {
            Assert.Equal(VariantStatus.Published,
                await f.Get<PublishingService>(worker).PublishOneAsync(variantId, CancellationToken.None));
        }

        async Task<MetricsRunResult> RunAsync()
        {
            await using var worker = f.Scope(c => c.IsSystem = true);
            return await f.Get<MetricsCollectionService>(worker).CollectDueAsync(CancellationToken.None);
        }
        async Task<int> CountAsync()
        {
            await using var scope = f.Scope();
            return await f.Get<IAppDbContext>(scope).PostMetrics.CountAsync(m => m.PostVariantId == variantId);
        }

        var first = await RunAsync();
        Assert.Equal(0, await CountAsync()); // 1時間たっていない
        Assert.True(first.Accounts > 0);     // アカウント指標はその日1回

        f.Clock.Advance(TimeSpan.FromHours(1));
        var second = await RunAsync();
        Assert.Equal(1, await CountAsync());
        Assert.Equal(0, second.Accounts);

        await RunAsync();
        Assert.Equal(1, await CountAsync()); // 同じチェックポイントでは取り直さない

        f.Clock.Advance(TimeSpan.FromHours(23));
        await RunAsync();
        await using (var scope = f.Scope())
        {
            var metrics = await f.Get<IAppDbContext>(scope).PostMetrics.Where(m => m.PostVariantId == variantId).ToListAsync();
            Assert.Equal(2, metrics.Count);
            var ordered = metrics.OrderBy(m => m.CapturedAt).ToList();
            Assert.True(ordered[1].Impressions > ordered[0].Impressions); // 時間とともに増える
        }
    }
}

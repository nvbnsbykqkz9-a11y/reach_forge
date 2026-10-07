using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Media;

namespace ReachForge.Application.Tests;

/// <summary>有料広告（お試しの広告アカウント）：連携 → AI の広告文 → 出稿 → 審査 → 配信 → 一時停止 → 終了。</summary>
public class AdTests
{
    private static async Task<MediaAsset> ImageAsync(AppFixture f, AsyncServiceScope scope)
    {
        var bytes = (await new ImageSharpProcessor().RenderPlaceholderAsync(1080, 1080, 7, ["#B45309", "#FDE68A"], CancellationToken.None)).Bytes;
        return await f.Get<MediaService>(scope).UploadAsync("ad.png", "image/png", new MemoryStream(bytes), bytes.Length, CancellationToken.None);
    }

    private static AdDraft Draft(AppFixture f, Guid accountId, Guid? mediaId, decimal budget = 1000) => new()
    {
        Platform = SocialPlatform.X, AdAccountId = accountId, Objective = AdObjective.Traffic, DailyBudget = budget,
        StartAt = f.Clock.GetUtcNow().AddMinutes(15), EndAt = f.Clock.GetUtcNow().AddDays(7),
        Creative = new AdCreative { PrimaryText = "秋限定のさつまいもラテ", LinkUrl = "https://shop.example/latte", MediaAssetId = mediaId },
    };

    [Fact]
    public async Task Demo_ad_runs_from_connection_to_completion()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var ads = f.Get<AdService>(scope);

        var adapter = ads.AdapterFor(SocialPlatform.X)!;
        Assert.True(adapter.IsSimulation);
        Assert.Null(await ads.BeginConnectAsync(SocialPlatform.X, "https://app/cb", CancellationToken.None));
        var account = Assert.Single(await ads.AccountsAsync(AdNetwork.X, CancellationToken.None));
        Assert.True(account.IsDemo);

        var copies = await ads.SuggestCopyAsync(new AdCopyRequest(SocialPlatform.X, AdObjective.Traffic, "秋限定のさつまいもラテ", null), CancellationToken.None);
        Assert.Equal(3, copies.Count);
        Assert.All(copies, c => Assert.InRange(c.PrimaryText.Length, 1, AdCopyLimits.For(SocialPlatform.X).PrimaryText));
        Assert.All(copies, c => Assert.NotNull(c.Guardrail));
        Assert.True((await f.Get<IAppDbContext>(scope).CreditAccounts.SingleAsync()).Balance < 1500);

        var image = await ImageAsync(f, scope);
        await Assert.ThrowsAsync<DomainException>(() => ads.SubmitAsync(Draft(f, account.Id, image.Id), chargesConfirmed: false, CancellationToken.None));
        var low = await Assert.ThrowsAsync<DomainException>(() => ads.SubmitAsync(Draft(f, account.Id, image.Id, budget: 10), true, CancellationToken.None));
        Assert.Contains("1日の予算", low.Message);
        await Assert.ThrowsAsync<DomainException>(() => ads.SubmitAsync(Draft(f, account.Id, null), true, CancellationToken.None));

        var campaign = await ads.SubmitAsync(Draft(f, account.Id, image.Id), true, CancellationToken.None);
        Assert.Equal(AdStatus.InReview, campaign.Status);
        Assert.Equal(7000, campaign.MaxTotalSpend);
        Assert.StartsWith("X ", campaign.Name);
        Assert.Contains(await f.Get<IAppDbContext>(scope).AuditLogs.Select(a => a.Action).ToListAsync(), a => a == "ads.submitted");

        // 審査のあと配信が始まり、成果が増える（定期処理はシステムコンテキストで動く）
        f.Clock.Advance(TimeSpan.FromHours(6));
        await using (var worker = f.Scope(c => c.IsSystem = true))
        {
            var r = await f.Get<AdService>(worker).SyncDueAsync(CancellationToken.None);
            Assert.Equal((1, 0), (r.Synced, r.Failed));
        }
        await using (var check = f.Scope())
        {
            var live = Assert.Single(await f.Get<AdService>(check).ListAsync(SocialPlatform.X, CancellationToken.None));
            Assert.Equal(AdStatus.Active, live.Status);
            Assert.InRange(live.Results!.Spend, 1, live.DailyBudget);

            var paused = await f.Get<AdService>(check).SetPausedAsync(live.Id, true, CancellationToken.None);
            Assert.Equal(AdStatus.Paused, paused.Status);
            await f.Get<AdService>(check).SetPausedAsync(live.Id, false, CancellationToken.None);
        }

        f.Clock.Advance(TimeSpan.FromDays(8));
        await using (var worker = f.Scope(c => c.IsSystem = true))
        {
            await f.Get<AdService>(worker).SyncDueAsync(CancellationToken.None);
        }
        await using (var check = f.Scope())
        {
            var done = Assert.Single(await f.Get<AdService>(check).RecentAsync(5, CancellationToken.None));
            Assert.Equal(AdStatus.Completed, done.Status);
            Assert.False(done.IsLive);
        }
    }

    [Fact]
    public async Task Line_and_threads_have_no_ad_api()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var ads = f.Get<AdService>(scope);
        Assert.Null(ads.AdapterFor(SocialPlatform.Line));
        Assert.Null(ads.AdapterFor(SocialPlatform.Threads));
        await Assert.ThrowsAsync<DomainException>(() => ads.BeginConnectAsync(SocialPlatform.Line, "https://app/cb", CancellationToken.None));
    }

    [Fact]
    public async Task Without_app_settings_and_demo_ads_are_unavailable()
    {
        await using var f = await AppFixture.CreateAsync(new() { ["Social:UseMock"] = "false" });
        await using var scope = f.Scope();
        Assert.Null(f.Get<AdService>(scope).AdapterFor(SocialPlatform.Instagram));
    }

    [Fact]
    public async Task Viewer_cannot_connect_or_submit_and_state_is_single_use()
    {
        await using var f = await AppFixture.CreateAsync();
        await using (var viewer = f.Scope(c => c.Role = Role.Viewer))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => f.Get<AdService>(viewer).BeginConnectAsync(SocialPlatform.X, "https://app/cb", CancellationToken.None));
        }
        await using var scope = f.Scope();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            f.Get<AdService>(scope).CompleteConnectAsync(AdNetwork.Meta, "code", "ad_unknown", null, CancellationToken.None));
        Assert.Equal(ErrorCodes.SnsAuthCanceled, ex.ErrorCode);
    }
}

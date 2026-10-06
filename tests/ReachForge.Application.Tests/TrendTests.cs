using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;

namespace ReachForge.Application.Tests;

public class TrendTests
{
    private sealed class NewsSource : ITrendSource
    {
        public Task<IReadOnlyList<TrendCandidate>> CollectAsync(DateOnly today, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TrendCandidate>>(
            [
                new("大型台風の被害", today.AddDays(1), TrendSource.Web),   // 便乗しない
                new("秋のコーヒーフェス", today.AddDays(10), TrendSource.Web),
            ]);
    }

    [Fact]
    public async Task Refresh_scores_filters_sensitive_and_keeps_top_ten()
    {
        await using var f = await AppFixture.CreateAsync(configure: s => s.AddSingleton<ITrendSource, NewsSource>());
        await using var scope = f.Scope();
        var trends = f.Get<TrendService>(scope);
        var count = await trends.RefreshAsync(CancellationToken.None);
        Assert.InRange(count, 1, TrendService.KeepTop);

        var ideas = await trends.ListAsync(CancellationToken.None);
        Assert.DoesNotContain(ideas, i => i.Topic.Contains("台風"));
        var coffee = ideas.Single(i => i.Topic == "秋のコーヒーフェス");
        Assert.Equal(3, coffee.Angles.Count);
        Assert.True(coffee.RecommendedDate >= DateOnly.FromDateTime(f.Clock.GetUtcNow().UtcDateTime));
        Assert.True(ideas.SequenceEqual(ideas.OrderBy(i => i.RecommendedDate).ThenByDescending(i => i.Relevance)));

        // 使ったネタは次回の更新で出てこない
        await trends.SetStatusAsync(coffee.Id, IdeaStatus.Used, CancellationToken.None);
        await trends.RefreshAsync(CancellationToken.None);
        Assert.DoesNotContain(await trends.ListAsync(CancellationToken.None), i => i.Topic == "秋のコーヒーフェス");
    }

    [Fact]
    public async Task Daily_job_runs_once_per_day_after_six_local()
    {
        await using var f = await AppFixture.CreateAsync();
        // 2026-10-06 00:00Z = 09:00 JST
        await using (var worker = f.Scope(c => c.IsSystem = true))
        {
            Assert.True(await f.Get<TrendService>(worker).RefreshDueAsync(CancellationToken.None) > 0);
            Assert.Equal(0, await f.Get<TrendService>(worker).RefreshDueAsync(CancellationToken.None));
        }
        await using var scope = f.Scope();
        Assert.True(await f.Get<IAppDbContext>(scope).TrendIdeas.AnyAsync());
    }
}

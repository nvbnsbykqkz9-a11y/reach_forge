using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Application.Tests;

public class CampaignTests
{
    [Fact]
    public async Task Campaign_validation_and_kpi_progress()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var campaigns = f.Get<CampaignService>(scope);

        await Assert.ThrowsAsync<DomainException>(() => campaigns.SaveAsync(new SaveCampaign { Name = "x", Code = "Bad Code!" }, CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => campaigns.SaveAsync(new SaveCampaign { Name = "x", Code = "autumn2026" }, CancellationToken.None)); // 重複
        await Assert.ThrowsAsync<DomainException>(() => campaigns.SaveAsync(new SaveCampaign
        {
            Name = "x", Code = "winter", StartsOn = new DateOnly(2026, 12, 10), EndsOn = new DateOnly(2026, 12, 1),
        }, CancellationToken.None));

        var created = await campaigns.SaveAsync(new SaveCampaign
        {
            Name = "冬のギフト", Code = "winter-gift", Kpi = CampaignKpi.Impressions, KpiTarget = 5000,
            Platforms = [SocialPlatform.Instagram], StartsOn = new DateOnly(2026, 11, 1),
        }, CancellationToken.None);
        var list = await campaigns.ListAsync(CancellationToken.None);
        Assert.Contains(list, c => c.Campaign.Id == created.Id);
        var autumn = list.Single(c => c.Campaign.Code == "autumn2026");
        Assert.Equal(CampaignKpi.LinkClicks, autumn.Campaign.Kpi);

        await campaigns.DeleteAsync(created.Id, CancellationToken.None); // 投稿がないので削除できる
        Assert.DoesNotContain(await campaigns.ListAsync(CancellationToken.None), c => c.Campaign.Id == created.Id);
    }

    private static async Task<PostVariant> DraftVariantAsync(AppFixture f, SocialPlatform platform)
    {
        await using var scope = f.Scope();
        var studio = f.Get<StudioService>(scope);
        var copies = await studio.GenerateCopiesAsync(new CopyRequest
        {
            WorkspaceId = DemoSeeder.WorkspaceId, Objective = PostObjective.Engagement, Theme = "週末のラテアート体験",
        }, CancellationToken.None);
        var best = copies.Candidates[0];
        var post = await studio.SaveMasterPostAsync(new SaveMasterPost
        {
            Title = best.Headline, Objective = PostObjective.Engagement, CoreMessage = best.Body, Cta = best.Cta, Hashtags = best.Hashtags,
            AiGenerationId = copies.GenerationId,
        }, CancellationToken.None);
        var channel = (await f.Get<ChannelService>(scope).ListAsync(CancellationToken.None)).Single(c => c.Platform == platform);
        return (await studio.GenerateVariantsAsync(post.Id, [channel.Id], new VariantOptions(null), CancellationToken.None)).Single();
    }

    [Fact]
    public async Task Ab_test_staggers_b_one_week_later_and_registers_winner()
    {
        await using var f = await AppFixture.CreateAsync();
        var a = await DraftVariantAsync(f, SocialPlatform.Threads);
        AbTest test;
        await using (var scope = f.Scope())
        {
            var before = (await f.Get<ICreditService>(scope).GetAccountAsync(CancellationToken.None)).Balance;
            test = await f.Get<AbTestService>(scope).CreateAsync(new CreateAbTest { VariantAId = a.Id, Variable = AbVariable.Hook },
                CancellationToken.None);
            Assert.Equal(before - 1, (await f.Get<ICreditService>(scope).GetAccountAsync(CancellationToken.None)).Balance);
            var view = await f.Get<AbTestService>(scope).GetAsync(test.Id, CancellationToken.None);
            Assert.StartsWith("知っていましたか？", view.B.Body);
            Assert.Equal(("A", "B"), (view.A.AbGroup, view.B.AbGroup));
            // 同じ投稿で2つ目のテストは作れない
            await Assert.ThrowsAsync<DomainException>(() => f.Get<AbTestService>(scope)
                .CreateAsync(new CreateAbTest { VariantAId = a.Id, Variable = AbVariable.Cta }, CancellationToken.None));
        }

        var startAt = f.Clock.GetUtcNow().AddHours(2);
        await using (var editor = f.Scope(c => c.Role = Role.Editor))
        {
            await f.Get<AbTestService>(editor).StartAsync(test.Id, startAt, null, CancellationToken.None);
        }
        await using (var approver = f.Scope(c => { c.Role = Role.Approver; c.UserName = "高橋"; }))
        {
            foreach (var id in new[] { test.VariantAId, test.VariantBId })
            {
                await f.Get<ApprovalService>(approver).ApproveAsync(id, null, null, CancellationToken.None);
            }
        }
        await using (var scope = f.Scope())
        {
            var db = f.Get<IAppDbContext>(scope);
            var b = await db.PostVariants.SingleAsync(v => v.Id == test.VariantBId);
            Assert.Equal(startAt.AddDays(7), b.ScheduledAt); // 1週間後の同じ曜日・時刻
        }

        // 公開（A → 1週間後に B）
        async Task PublishAsync()
        {
            await using var worker = f.Scope(c => c.IsSystem = true);
            await f.Get<PublishingService>(worker).RunDueAsync(CancellationToken.None);
        }
        f.Clock.Advance(TimeSpan.FromHours(2));
        await PublishAsync();
        f.Clock.Advance(TimeSpan.FromDays(7));
        await PublishAsync();

        // 72時間後の指標（B の反応が明らかに高い）
        await using (var scope = f.Scope())
        {
            var db = f.Get<IAppDbContext>(scope);
            foreach (var (id, likes) in new[] { (test.VariantAId, 40), (test.VariantBId, 120) })
            {
                var v = await db.PostVariants.SingleAsync(x => x.Id == id);
                db.PostMetrics.Add(new PostMetric
                {
                    TenantId = DemoSeeder.TenantId, PostVariantId = id, Platform = v.Platform, PostedAt = v.PublishedAt!.Value,
                    CapturedAt = v.PublishedAt!.Value.AddHours(72), Impressions = 3000, Likes = likes,
                });
            }
            await db.SaveChangesAsync();
        }
        await using (var worker = f.Scope(c => c.IsSystem = true))
        {
            Assert.Equal(0, await f.Get<AbTestService>(worker).EvaluateDueAsync(CancellationToken.None)); // まだ72時間たっていない
        }
        f.Clock.Advance(TimeSpan.FromHours(73));
        await using (var worker = f.Scope(c => c.IsSystem = true))
        {
            Assert.Equal(1, await f.Get<AbTestService>(worker).EvaluateDueAsync(CancellationToken.None));
        }

        await using (var scope = f.Scope())
        {
            var view = await f.Get<AbTestService>(scope).GetAsync(test.Id, CancellationToken.None);
            Assert.Equal(AbTestStatus.Completed, view.Test.Status);
            Assert.Equal("BWins", view.Test.Verdict);
            Assert.StartsWith("Bの方が反応が 3.0倍", view.Status);
            var brand = await f.Get<IAppDbContext>(scope).BrandProfiles.SingleAsync();
            var example = Assert.Single(brand.FewShotExamples);
            Assert.False(example.Enabled); // 候補として登録し、担当者が有効にする
            Assert.Contains("書き出し", example.Reason);
        }
    }

    [Fact]
    public async Task Cross_channel_test_publishes_at_the_same_time()
    {
        await using var f = await AppFixture.CreateAsync(new() { });
        var a = await DraftVariantAsync(f, SocialPlatform.Threads);
        await using var scope = f.Scope();
        var channels = await f.Get<ChannelService>(scope).ListAsync(CancellationToken.None);
        var tests = f.Get<AbTestService>(scope);
        await Assert.ThrowsAsync<DomainException>(() => tests.CreateAsync(new CreateAbTest
        {
            VariantAId = a.Id, Variable = AbVariable.Cta, Mode = AbMode.CrossChannel, ChannelBId = a.ChannelId,
        }, CancellationToken.None));
        var test = await tests.CreateAsync(new CreateAbTest
        {
            VariantAId = a.Id, Variable = AbVariable.Cta, Mode = AbMode.CrossChannel,
            ChannelBId = channels.Single(c => c.Platform == SocialPlatform.Line).Id,
        }, CancellationToken.None);
        var view = await tests.GetAsync(test.Id, CancellationToken.None);
        Assert.Equal(SocialPlatform.Line, view.B.Platform);
        Assert.Contains("今週末までに", view.B.Body);
        await tests.StartAsync(test.Id, f.Clock.GetUtcNow().AddDays(1), null, CancellationToken.None);
        var b = await f.Get<IAppDbContext>(scope).PostVariants.AsNoTracking().SingleAsync(v => v.Id == test.VariantBId);
        Assert.Equal(f.Clock.GetUtcNow().AddDays(1), b.RequestedPublishAt);
    }
}

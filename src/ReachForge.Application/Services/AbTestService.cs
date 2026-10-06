using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Analytics;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

public sealed record CreateAbTest
{
    public required Guid VariantAId { get; init; }
    public required AbVariable Variable { get; init; }
    public AbMode Mode { get; init; } = AbMode.SameChannelStaggered;

    /// <summary>異なるチャネル間で比べる場合の B のチャネル。</summary>
    public Guid? ChannelBId { get; init; }

    /// <summary>B 案の本文（省略時は AI が A 案から指定の要素だけを変えて作る）。</summary>
    public string? BodyB { get; init; }

    /// <summary>画像のテストで B に使う画像（メディアライブラリ）。</summary>
    public Guid? ImageAssetIdB { get; init; }
    public string? Name { get; init; }
}

/// <summary>画面表示用：テストと A・B の投稿、途中経過（または判定結果）。</summary>
public sealed record AbTestView(AbTest Test, PostVariant A, PostVariant B, string ChannelA, string ChannelB,
    long ImpressionsA, long ImpressionsB, string Status);

/// <summary>
/// A/B テスト（F-11）：変える要素を1つ指定 → B 案（AI）→ 配信（同じチャネルで1週間後の同じ曜日・時刻、または別チャネルで同時）
/// → 72時間後に二項比率の検定で判定（サンプル不足は保留）→ 勝ちパターンをブランドの Few-shot 候補に登録。
/// </summary>
public sealed class AbTestService(
    IAppDbContext db,
    ITenantContext tenant,
    IAbVariantGenerator generator,
    IBrandContextProvider brand,
    ICreditService credits,
    MediaService media,
    ApprovalService approvals,
    SchedulingService scheduling,
    TimeProvider clock,
    ILogger<AbTestService> log)
{
    public static readonly TimeSpan StaggerInterval = TimeSpan.FromDays(7);

    public async Task<AbTest> CreateAsync(CreateAbTest cmd, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var a = await db.PostVariants.FirstOrDefaultAsync(v => v.Id == cmd.VariantAId && v.WorkspaceId == tenant.WorkspaceId, ct)
                ?? throw new NotFoundException("投稿");
        if (a.Status is VariantStatus.Published or VariantStatus.Publishing or VariantStatus.Canceled or VariantStatus.Failed)
        {
            throw new DomainException(ErrorCodes.Validation, "公開前の投稿を A 案に選んでください。");
        }
        if (a.AbGroup is not null || await db.AbTests.AnyAsync(t => t.VariantAId == a.Id || t.VariantBId == a.Id, ct))
        {
            throw new DomainException(ErrorCodes.Validation, "この投稿はすでに A/B テストに使われています。");
        }
        if (cmd.Mode == AbMode.CrossChannel && cmd.Variable == AbVariable.TimeSlot)
        {
            throw new DomainException(ErrorCodes.Validation, "投稿時間のテストは同じSNSで行ってください。");
        }

        var post = await db.MasterPosts.FirstAsync(p => p.Id == a.MasterPostId, ct);
        var channelB = cmd.Mode == AbMode.CrossChannel
            ? await db.Channels.FirstOrDefaultAsync(c => c.Id == cmd.ChannelBId && c.WorkspaceId == tenant.WorkspaceId, ct)
              ?? throw new DomainException(ErrorCodes.Validation, "B 案を投稿するSNSを選んでください。")
            : await db.Channels.FirstAsync(c => c.Id == a.ChannelId, ct);
        if (cmd.Mode == AbMode.CrossChannel && channelB.Id == a.ChannelId)
        {
            throw new DomainException(ErrorCodes.Validation, "別のSNSを選んでください（同じSNSで比べる場合は「同じSNSで日時をずらす」）。");
        }

        var ctx = await brand.BuildAsync(post.WorkspaceId, post.ProductIds, post.CampaignId, ct);

        // 別チャネルで比べる場合は、そのチャネル向けの文面（あれば）を土台にし、SNS の制約に合わせる
        var baseBody = a.Body;
        IReadOnlyList<string> hashtags = a.Hashtags;
        if (channelB.Id != a.ChannelId)
        {
            var sibling = await db.PostVariants.AsNoTracking()
                .FirstOrDefaultAsync(v => v.MasterPostId == post.Id && v.ChannelId == channelB.Id && v.AbGroup == null, ct);
            if (sibling is not null)
            {
                baseBody = sibling.Body;
                hashtags = sibling.Hashtags;
            }
            var limit = PlatformCatalog.Get(channelB.Platform).MaxHashtags;
            if (limit is { } max) hashtags = hashtags.Take(max).ToList();
        }

        string bodyB;
        if (!string.IsNullOrWhiteSpace(cmd.BodyB))
        {
            bodyB = cmd.BodyB.Trim();
        }
        else if (cmd.Variable is AbVariable.Hook or AbVariable.Cta)
        {
            var cost = CreditTable.Cost(CreditOperation.CopyPartialRegeneration);
            await using var hold = await credits.HoldAsync(cost, ct);
            bodyB = await generator.GenerateAsync(baseBody, cmd.Variable, ctx, ct);
            await hold.CommitAsync(cost, ct);
        }
        else
        {
            bodyB = baseBody;
        }
        if (cmd.Variable is AbVariable.Hook or AbVariable.Cta && bodyB == baseBody)
        {
            throw new DomainException(ErrorCodes.Validation, "B 案が A 案と同じです。変える部分を書き換えてください。");
        }

        var b = PostVariant.Create(post, channelB, bodyB, hashtags, a.Title, a.AiGenerationId);
        b.AspectMethod = a.AspectMethod is AspectMethod.Outpaint ? AspectMethod.SmartCrop : a.AspectMethod;
        b.UrlCostAcknowledged = a.UrlCostAcknowledged;
        if (cmd.Variable == AbVariable.Image)
        {
            var source = await media.GetAsync(cmd.ImageAssetIdB ?? throw new DomainException(ErrorCodes.Validation, "B 案の画像を選んでください。"), ct);
            if (a.MediaAssetIds.Contains(source.Id)) throw new DomainException(ErrorCodes.Validation, "A 案と違う画像を選んでください。");
            var derived = await media.DeriveForPlatformAsync(source, channelB.Platform, b.AspectMethod, ct);
            b.SetMedia([derived.Id], requiresApproval: false);
        }
        else if (channelB.Platform == a.Platform)
        {
            b.SetMedia(a.MediaAssetIds, requiresApproval: false);
        }
        else if (post.MediaAssetIds.Count > 0)
        {
            var derived = await media.DeriveForPlatformAsync(await media.GetAsync(post.MediaAssetIds[0], ct), channelB.Platform, b.AspectMethod, ct);
            b.SetMedia([derived.Id], requiresApproval: false);
        }
        b.ApplyGuardrail(StudioService.CheckVariant(b, ctx.ToGuardrailContext()));
        a.AbGroup = "A";
        b.AbGroup = "B";
        db.PostVariants.Add(b);

        var test = new AbTest
        {
            TenantId = tenant.TenantId,
            WorkspaceId = tenant.WorkspaceId,
            CampaignId = post.CampaignId,
            Name = string.IsNullOrWhiteSpace(cmd.Name) ? $"{post.Title}（{cmd.Variable.ToLabel()}）" : cmd.Name.Trim(),
            Variable = cmd.Variable,
            Mode = cmd.Mode,
            VariantAId = a.Id,
            VariantBId = b.Id,
            CreatedBy = tenant.UserName,
        };
        db.AbTests.Add(test);
        db.Record(tenant, "abtest.created", nameof(AbTest), test.Id, cmd.Variable.ToString());
        await db.SaveChangesAsync(ct);
        return test;
    }

    /// <summary>
    /// テストを始める。同じチャネルでは B を1週間後の同じ曜日・時刻に（投稿時間のテストは指定の時刻に）、
    /// 別チャネルでは同時に配信する。承認が必要なワークスペースでは、希望日時付きで承認を依頼する。
    /// </summary>
    public async Task<AbTest> StartAsync(Guid testId, DateTimeOffset startAt, DateTimeOffset? timeB, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Schedule);
        var test = await Find(testId, ct);
        if (test.Status != AbTestStatus.Draft) throw new DomainException(ErrorCodes.Validation, "このテストはすでに始まっています。");
        var now = clock.GetUtcNow();
        if (startAt <= now) throw new DomainException(ErrorCodes.PubScheduleInPast, "過去の日時には予約できません。日時を選び直してください。");
        var atB = test.Variable == AbVariable.TimeSlot
            ? timeB ?? throw new DomainException(ErrorCodes.Validation, "B 案の投稿時間を選んでください。")
            : test.Mode == AbMode.CrossChannel ? startAt : startAt + StaggerInterval;
        if (test.Variable == AbVariable.TimeSlot && (atB <= now || atB == startAt))
        {
            throw new DomainException(ErrorCodes.Validation, "B 案は A 案と違う、未来の時刻を選んでください。");
        }

        var pair = await db.PostVariants.AsNoTracking().Where(v => v.Id == test.VariantAId || v.Id == test.VariantBId).ToListAsync(ct);
        if (pair.FirstOrDefault(v => v.HasGuardrailErrors) is { } blocked)
        {
            throw new DomainException(ErrorCodes.AprBlockedByGuardrail,
                $"{(blocked.Id == test.VariantAId ? "A" : "B")}案の確認結果にエラーがあります。修正してから始めてください。");
        }
        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == test.WorkspaceId, ct);
        foreach (var (variantId, at) in new[] { (test.VariantAId, startAt), (test.VariantBId, atB) })
        {
            var v = await db.PostVariants.AsNoTracking().FirstAsync(x => x.Id == variantId, ct);
            if (workspace.RequiresApproval && v.Status is VariantStatus.Draft)
            {
                await approvals.SubmitAsync([variantId], at, "A/Bテストの投稿です", ct);
            }
            else if (!workspace.RequiresApproval || v.Status is VariantStatus.Approved or VariantStatus.Scheduled)
            {
                await scheduling.ScheduleAsync(variantId, at, ct);
            }
        }
        test.Status = AbTestStatus.Running;
        test.StartAt = startAt;
        db.Record(tenant, "abtest.started", nameof(AbTest), test.Id);
        await db.SaveChangesAsync(ct);
        return test;
    }

    public async Task CancelAsync(Guid testId, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Schedule);
        var test = await Find(testId, ct);
        if (test.Status is AbTestStatus.Completed or AbTestStatus.Canceled) return;
        test.Status = AbTestStatus.Canceled;
        db.Record(tenant, "abtest.canceled", nameof(AbTest), test.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AbTestView>> ListAsync(Guid? campaignId, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewAnalytics);
        var tests = await db.AbTests.AsNoTracking()
            .Where(t => t.WorkspaceId == tenant.WorkspaceId && (campaignId == null || t.CampaignId == campaignId))
            .ToListAsync(ct);
        var views = new List<AbTestView>();
        foreach (var t in tests.OrderByDescending(t => t.CreatedAt)) views.Add(await ViewAsync(t, ct));
        return views;
    }

    public async Task<AbTestView> GetAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ViewAnalytics);
        return await ViewAsync(await db.AbTests.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.WorkspaceId == tenant.WorkspaceId, ct)
                               ?? throw new NotFoundException("A/Bテスト"), ct);
    }

    /// <summary>判定（14章：1時間ごと、システムコンテキスト）。両方の公開から72時間後に判定する。</summary>
    public async Task<int> EvaluateDueAsync(CancellationToken ct)
    {
        if (!tenant.IsSystem) throw new InvalidOperationException("EvaluateDueAsync はシステムコンテキストで実行してください。");
        var now = clock.GetUtcNow();
        var running = await db.AbTests.Where(t => t.Status == AbTestStatus.Running).ToListAsync(ct);
        var evaluated = 0;
        foreach (var test in running)
        {
            var a = await db.PostVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Id == test.VariantAId, ct);
            var b = await db.PostVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Id == test.VariantBId, ct);
            if (a is null || b is null || a.Status is VariantStatus.Canceled or VariantStatus.Failed || b.Status is VariantStatus.Canceled or VariantStatus.Failed)
            {
                test.Status = AbTestStatus.Canceled;
                test.Summary = "投稿が取り消された、または公開に失敗したため中止しました";
                continue;
            }
            if (a.PublishedAt is not { } pa || b.PublishedAt is not { } pb) continue;
            var last = pa > pb ? pa : pb;
            if (now < last + AbTest.EvaluateAfter) continue;

            var (ma, mb) = (await MetricAt(a, ct), await MetricAt(b, ct));
            var result = AbTestEvaluator.Evaluate(Imp(ma), Eng(ma), Imp(mb), Eng(mb));
            test.RateA = result.RateA;
            test.RateB = result.RateB;
            test.PValue = result.PValue;
            test.EvaluatedAt = now;
            if (result.Verdict == AbVerdict.Pending && now < last + AbTest.GiveUpAfter)
            {
                var days = AbTestEvaluator.DaysUntilDecidable(Imp(ma), Imp(mb), (now - last).TotalDays);
                test.Summary = days is { } d and > 0 ? $"まだ判断できません（あと約{d}日）" : result.Summary;
                continue;
            }
            test.Verdict = result.Verdict.ToString();
            test.Summary = result.Verdict == AbVerdict.Pending ? "表示回数が足りず、判断できませんでした" : result.Summary;
            test.Status = AbTestStatus.Completed;
            evaluated++;
            if (result.Verdict is AbVerdict.AWins or AbVerdict.BWins)
            {
                await RegisterWinnerAsync(test, result.Verdict == AbVerdict.AWins ? a : b, result.Summary, ct);
            }
            log.LogInformation("A/B test {TestId} evaluated: {Verdict}", test.Id, result.Verdict);
        }
        await db.SaveChangesAsync(ct);
        return evaluated;
    }

    /// <summary>勝ちパターンをブランドプロファイルの Few-shot 候補に登録する（担当者が確認して有効にする）。</summary>
    private async Task RegisterWinnerAsync(AbTest test, PostVariant winner, string summary, CancellationToken ct)
    {
        var profile = await db.BrandProfiles.FirstOrDefaultAsync(p => p.WorkspaceId == test.WorkspaceId, ct);
        if (profile is null || profile.FewShotExamples.Any(e => e.SourceAbTestId == test.Id)) return;
        profile.FewShotExamples =
        [
            .. profile.FewShotExamples,
            new FewShotExample
            {
                Text = PostText.Truncate(winner.Body, 300),
                Reason = $"{test.Variable.ToLabel()}のテスト：{summary}",
                SourceAbTestId = test.Id,
            },
        ];
        test.WinnerRegistered = true;
    }

    /// <summary>72時間時点に最も近い指標（なければ最新）。</summary>
    private async Task<PostMetric?> MetricAt(PostVariant v, CancellationToken ct)
    {
        var target = v.PublishedAt!.Value + AbTest.EvaluateAfter;
        var metrics = await db.PostMetrics.AsNoTracking().Where(m => m.PostVariantId == v.Id).ToListAsync(ct);
        return metrics.Where(m => m.CapturedAt <= target + TimeSpan.FromHours(12)).MaxBy(m => m.CapturedAt)
               ?? metrics.MaxBy(m => m.CapturedAt);
    }

    private static long Imp(PostMetric? m) => m is null ? 0 : m.Impressions > 0 ? m.Impressions : m.Reach > 0 ? m.Reach : m.Views;
    private static long Eng(PostMetric? m) => m is null ? 0 : MetricsCalculator.Engagements(m);

    private async Task<AbTestView> ViewAsync(AbTest t, CancellationToken ct)
    {
        var a = await db.PostVariants.AsNoTracking().FirstAsync(v => v.Id == t.VariantAId, ct);
        var b = await db.PostVariants.AsNoTracking().FirstAsync(v => v.Id == t.VariantBId, ct);
        var channels = await db.Channels.AsNoTracking().Where(c => c.Id == a.ChannelId || c.Id == b.ChannelId)
            .ToDictionaryAsync(c => c.Id, c => c.DisplayName, ct);
        var ia = Imp(await LatestAsync(a.Id, ct));
        var ib = Imp(await LatestAsync(b.Id, ct));
        var status = t.Status switch
        {
            AbTestStatus.Draft => "開始前",
            AbTestStatus.Canceled => t.Summary ?? "中止しました",
            AbTestStatus.Completed => t.Summary ?? "判定済み",
            _ when a.PublishedAt is null || b.PublishedAt is null => "配信待ち（両方の投稿が公開されると判定を始めます）",
            _ when t.Summary is not null => t.Summary,
            _ => $"結果待ち（公開から72時間後に判定します）",
        };
        return new AbTestView(t, a, b, channels.GetValueOrDefault(a.ChannelId, ""), channels.GetValueOrDefault(b.ChannelId, ""), ia, ib, status);
    }

    private async Task<PostMetric?> LatestAsync(Guid variantId, CancellationToken ct) =>
        (await db.PostMetrics.AsNoTracking().Where(m => m.PostVariantId == variantId).ToListAsync(ct)).MaxBy(m => m.CapturedAt);

    private async Task<AbTest> Find(Guid id, CancellationToken ct) =>
        await db.AbTests.FirstOrDefaultAsync(t => t.Id == id && t.WorkspaceId == tenant.WorkspaceId, ct) ?? throw new NotFoundException("A/Bテスト");
}

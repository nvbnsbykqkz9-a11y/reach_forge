using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

/// <summary>LP からつくる内容（SNS・使う LP の画像・動画をつくるか）。</summary>
public sealed record LpProjectRequest
{
    public required string Url { get; init; }
    public required IReadOnlyList<SocialPlatform> Platforms { get; init; }

    /// <summary>使う LP の画像（URL）。広告用の画像と動画の素材になる。</summary>
    public IReadOnlyList<string> ImageUrls { get; init; } = [];

    /// <summary>LP の画像を広告・動画に使う権利があることを利用者が確認した。</summary>
    public bool RightsConfirmed { get; init; }

    public bool MakeVideo { get; init; } = true;
    public int VideoSeconds { get; init; } = 20;
    public bool Narration { get; init; } = true;
}

/// <summary>
/// LP から、選んだ SNS 向けの広告文（3案）・投稿文・ハッシュタグ・SNS のサイズの画像と、縦型の動画（ジョブ）をまとめてつくる。
/// SNS とはつながず、できたものは利用者がダウンロード・コピーして、各 SNS や広告マネージャーに手動でアップロードする。
/// </summary>
public sealed class LpStudioService(
    IAppDbContext db,
    ITenantContext tenant,
    IWebPageFetcher fetcher,
    MediaService media,
    VideoService videos,
    ILpCreativeWriter writer,
    IBrandContextProvider brand,
    ICreditService credits,
    ILogger<LpStudioService> log)
{
    /// <summary>つくれる SNS（画面の並び順）。</summary>
    public static readonly IReadOnlyList<SocialPlatform> Supported =
    [
        SocialPlatform.Instagram, SocialPlatform.X, SocialPlatform.Facebook, SocialPlatform.Threads,
        SocialPlatform.TikTok, SocialPlatform.YouTube, SocialPlatform.Line,
    ];

    /// <summary>使える LP の画像の数（動画の素材の上限と同じ）。</summary>
    public const int MaxImages = 4;

    /// <summary>LP を読み込む（タイトル・説明・画像の候補。クレジットは使わない）。</summary>
    public Task<WebPage> PreviewAsync(string url, CancellationToken ct) => videos.PreviewLandingPageAsync(url, ct);

    /// <summary>使うクレジットの見込み（文章は SNS ごと、動画は1本）。</summary>
    public static int EstimateCredits(LpProjectRequest r) =>
        CreditTable.Cost(CreditOperation.CopyGeneration) * r.Platforms.Distinct().Count()
        + (r.MakeVideo ? VideoService.EstimateCredits(VideoRequest(r)) : 0);

    /// <summary>この SNS に画像をつくるか（YouTube は動画だけ）。</summary>
    public static bool MakesImages(SocialPlatform p) => !PlatformCatalog.Get(p).VideoOnly;

    public async Task<IReadOnlyList<LpProject>> ListAsync(int count, CancellationToken ct) =>
        (await db.LpProjects.Where(p => p.WorkspaceId == tenant.WorkspaceId).ToListAsync(ct))
        .OrderByDescending(p => p.CreatedAt).Take(count).ToList();

    public async Task<LpProject> GetAsync(Guid id, CancellationToken ct) =>
        await db.LpProjects.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == tenant.WorkspaceId, ct)
        ?? throw new NotFoundException("つくったもの");

    /// <summary>動画のジョブ（つくらない場合は null）。</summary>
    public async Task<AiJob?> VideoJobAsync(LpProject project, CancellationToken ct) =>
        project.VideoJobId is { } id ? await db.AiJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) : null; // 別のプロセスの更新を読む

    /// <summary>つくる。文章と画像はその場でつくり、動画はジョブとして裏でつくる（できあがりは画面で知らせる）。</summary>
    public async Task<LpProject> CreateAsync(LpProjectRequest request, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var platforms = request.Platforms.Distinct().ToList();
        if (platforms.Count == 0) throw new DomainException(ErrorCodes.Validation, "つくる SNS を1つ以上選んでください。");
        if (platforms.Any(p => !Supported.Contains(p))) throw new DomainException(ErrorCodes.Validation, "選べない SNS が含まれています。");
        var imageUrls = request.ImageUrls.Distinct().ToList();
        if (imageUrls.Count > MaxImages) throw new DomainException(ErrorCodes.Validation, $"LP の画像は{MaxImages}枚まで選べます。");
        if (imageUrls.Count > 0 && !request.RightsConfirmed)
        {
            throw new DomainException(ErrorCodes.Validation, "LP の画像を広告・動画に使う権利があることを確認してください。");
        }
        if (imageUrls.Count == 0 && platforms.Any(MakesImages) && !request.MakeVideo)
        {
            throw new DomainException(ErrorCodes.Validation, "LP の画像を1枚以上選ぶか、動画をつくるようにしてください。");
        }

        var page = await fetcher.FetchAsync(request.Url, ct);
        var project = new LpProject
        {
            TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Url = page.Url.ToString(),
            Title = PostText.Truncate(page.Title is { Length: > 0 } t ? t : page.Url.Host, 200), Platforms = platforms, CreatedBy = tenant.UserName,
        };
        db.LpProjects.Add(project);
        db.Record(tenant, "lp.create", nameof(LpProject), project.Id, $"{string.Join(",", platforms)} video={request.MakeVideo}");
        await db.SaveChangesAsync(ct);

        try
        {
            // LP の画像を取り込む（取れなかった画像は飛ばす）
            var sources = new List<MediaAsset>();
            foreach (var url in imageUrls)
            {
                var web = page.ImageList.FirstOrDefault(i => i.Url.ToString() == url) ?? new WebImage(new Uri(url), null);
                try
                {
                    sources.Add(await media.ImportFromWebAsync(await fetcher.FetchImageAsync(web.Url, ct), web, ct));
                }
                catch (DomainException ex)
                {
                    log.LogWarning(ex, "LP image {Url} was skipped", url);
                }
            }
            project.SourceImageAssetIds = sources.Select(s => s.Id).ToList();

            var ctx = await brand.BuildAsync(tenant.WorkspaceId, [], ct);
            var cost = CreditTable.Cost(CreditOperation.CopyGeneration);
            var outputs = new Dictionary<SocialPlatform, LpPlatformOutput>();
            foreach (var platform in platforms)
            {
                await using var hold = await credits.HoldAsync(cost, ct);
                var creative = await writer.WriteAsync(ctx, page, platform, ct);
                await hold.CommitAsync(cost, ct);
                project.CreditsUsed += cost;

                var images = new List<Guid>();
                if (MakesImages(platform))
                {
                    foreach (var source in sources)
                    {
                        images.Add((await media.DeriveForPlatformAsync(source, platform, AspectMethod.SmartCrop, ct)).Id);
                    }
                }
                outputs[platform] = new LpPlatformOutput
                {
                    AdCopies = creative.AdCopies, PostText = creative.PostText, Hashtags = creative.Hashtags, ImageAssetIds = images,
                };
            }
            project.Outputs = outputs;

            if (request.MakeVideo)
            {
                var job = await videos.EnqueueAsync(VideoRequest(request with { Url = project.Url, ImageUrls = sources.Count == 0 ? [] : imageUrls }), ct);
                project.VideoJobId = job.Id;
                project.CreditsUsed += job.CreditsHeld;
            }
            project.Status = LpProjectStatus.Ready;
        }
        catch (Exception ex) when (ex is DomainException or AiUnavailableException)
        {
            project.Status = LpProjectStatus.Failed;
            project.Error = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        await db.SaveChangesAsync(ct);
        return project;
    }

    /// <summary>一覧から消す（画像・動画のファイルは残る）。</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var project = await GetAsync(id, ct);
        db.LpProjects.Remove(project);
        db.Record(tenant, "lp.delete", nameof(LpProject), project.Id, null);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>ボタンの表示名（各社の広告マネージャーでの呼び名）。</summary>
    public static string CtaLabel(string cta) => cta switch
    {
        "SHOP_NOW" => "購入する（Shop Now）",
        "SIGN_UP" => "登録する（Sign Up）",
        "CONTACT_US" => "お問い合わせ（Contact Us）",
        "BOOK_NOW" => "予約する（Book Now）",
        _ => "詳しくはこちら（Learn More）",
    };

    /// <summary>投稿文とハッシュタグ（そのまま貼り付けられる形）。</summary>
    public static string PostTextOf(LpPlatformOutput o) => PostText.Compose(o.PostText, o.Hashtags);

    /// <summary>広告文の案（広告マネージャーの入力欄ごと）。</summary>
    public static string AdCopyText(LpAdCopy c)
    {
        var lines = new List<string>();
        if (c.Headline.Length > 0) lines.Add($"見出し：{c.Headline}");
        lines.Add($"本文：{c.PrimaryText}");
        if (c.Description.Length > 0) lines.Add($"説明：{c.Description}");
        lines.Add($"ボタン：{CtaLabel(c.CallToAction)}");
        return string.Join("\n", lines);
    }

    /// <summary>1つの SNS の文章をまとめたテキスト（ダウンロード用）。</summary>
    public static string TextFile(SocialPlatform platform, LpPlatformOutput o, string url)
    {
        var name = PlatformCatalog.Get(platform).DisplayName;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{name} 用の文章（リンク先：{url}）").AppendLine();
        sb.AppendLine("■ 投稿文").AppendLine(PostTextOf(o)).AppendLine();
        foreach (var (copy, i) in o.AdCopies.Select((c, i) => (c, i + 1)))
        {
            sb.AppendLine($"■ 広告文 案{i}").AppendLine(AdCopyText(copy)).AppendLine();
        }
        return sb.ToString();
    }

    private static VideoJobRequest VideoRequest(LpProjectRequest r) => new(
        "", [], r.Narration, r.VideoSeconds, VideoMode.LandingPage, null, r.Url, r.ImageUrls, false, r.RightsConfirmed);
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

/// <summary>LP からつくる内容（SNS・使う LP の画像・動画をつくるか）。</summary>
public sealed record LpProjectRequest
{
    public required string Url { get; init; }
    public required IReadOnlyList<SocialPlatform> Platforms { get; init; }

    /// <summary>使う LP の画像（URL）。AI が広告の写真・動画をつくるときの素材（商品・被写体）になる。</summary>
    public IReadOnlyList<string> ImageUrls { get; init; } = [];

    /// <summary>画像に何が写っているか（URL ごと。読み込み時に AI が選んだときの説明）。</summary>
    public IReadOnlyDictionary<string, string> ImageDescriptions { get; init; } = new Dictionary<string, string>();

    /// <summary>LP の画像を広告・動画に使う権利があることを利用者が確認した。</summary>
    public bool RightsConfirmed { get; init; }

    public bool MakeVideo { get; init; } = true;
    public int VideoSeconds { get; init; } = 20;
    public bool Narration { get; init; } = true;
}

/// <summary>LP の画像の候補（読み込み時に表示する）。<paramref name="Recommended"/> は AI のおすすめ（順位）。</summary>
public sealed record LpImageOption(string Url, int Width, int Height, string? Alt, int? Recommended, string? Description);

/// <summary>LP を読み込んだ結果：ページの内容と、使える画像の候補（おすすめの順）。</summary>
public sealed record LpPreview(WebPage Page, IReadOnlyList<LpImageOption> Images)
{
    public IEnumerable<LpImageOption> Recommended => Images.Where(i => i.Recommended is not null).OrderBy(i => i.Recommended);
}

/// <summary>つくる途中の進み具合。<see cref="LpStudioService.CreateSteps"/> の何番目をしているか（Detail は「2/4枚」など）。</summary>
public sealed record LpCreateProgress(int StepIndex, string? Detail = null);

/// <summary>
/// LP から、選んだ SNS 向けの広告文（3案）・投稿文・ハッシュタグと、SNS の形式ごとの広告画像・動画をまとめてつくる。
/// 文章はその場でつくり、画像と動画はジョブ（<see cref="LpMediaService"/>）で裏でつくる。
/// SNS とはつながず、できたものは利用者がダウンロード・コピーして、各 SNS や広告マネージャーに手動でアップロードする。
/// </summary>
public sealed class LpStudioService(
    IAppDbContext db,
    ITenantContext tenant,
    IWebPageFetcher fetcher,
    MediaService media,
    VideoService videos,
    ILpCreativeWriter writer,
    ILpImageCurator curator,
    IImageProcessor images,
    IBrandContextProvider brand,
    IWorkQueue queue,
    ILogger<LpStudioService> log)
{
    /// <summary>つくれる SNS（画面の並び順）。</summary>
    public static readonly IReadOnlyList<SocialPlatform> Supported =
    [
        SocialPlatform.Instagram, SocialPlatform.X, SocialPlatform.Facebook, SocialPlatform.Threads,
        SocialPlatform.TikTok, SocialPlatform.YouTube, SocialPlatform.Line,
    ];

    /// <summary>素材にできる LP の画像の数（1枚ごとに広告の写真をつくる）。</summary>
    public const int MaxImages = 4;

    /// <summary>AI がおすすめする枚数。</summary>
    public const int RecommendCount = 3;

    /// <summary>AI に見せる候補の上限と、素材にできる画像の最小の大きさ（短い辺）。</summary>
    public const int MaxCandidates = 16;
    public const int MinSourceSide = 300;

    /// <summary>
    /// LP を読み込み、画像の候補をそろえる。小さい画像（アイコン等）・細長いバナーは除き、AI が商品・サービスの特色が伝わる画像を選んで
    /// おすすめの順に並べる（AI が使えないときは大きい順）。
    /// </summary>
    public async Task<LpPreview> PreviewAsync(string url, CancellationToken ct)
    {
        var page = await videos.PreviewLandingPageAsync(url, ct);
        var candidates = new List<(WebImage Web, LpImageCandidate Candidate)>();
        foreach (var batch in page.ImageList.DistinctBy(i => i.Url).Take(MaxCandidates).Chunk(4))
        {
            var loaded = await Task.WhenAll(batch.Select(async web =>
            {
                try
                {
                    var image = await fetcher.FetchImageAsync(web.Url, ct);
                    var (w, h) = await images.MeasureAsync(image.Bytes, ct);
                    if (Math.Min(w, h) < MinSourceSide || Math.Max(w, h) > 3 * Math.Min(w, h)) return default;
                    var thumb = await images.ThumbnailAsync(image.Bytes, 512, ct);
                    var jpeg = await images.EncodeJpegAsync(thumb.Bytes, 300_000, ct);
                    return (web, new LpImageCandidate(0, jpeg.Bytes, jpeg.Mime, w, h, web.Alt));
                }
                catch (Exception ex) when (ex is DomainException or InvalidDataException or HttpRequestException)
                {
                    log.LogInformation("LP image {Url} is not usable: {Reason}", web.Url, ex.Message);
                    return default;
                }
            }));
            candidates.AddRange(loaded.Where(x => x.Item1 is not null).Select(x => (x.Item1!, x.Item2!)));
        }
        candidates = [.. candidates.Select((c, i) => (c.Web, c.Candidate with { Index = i }))];

        IReadOnlyList<LpImagePick> picks;
        try
        {
            picks = await curator.PickAsync(page, [.. candidates.Select(c => c.Candidate)], RecommendCount, ct);
        }
        catch (AiUnavailableException ex)
        {
            log.LogWarning(ex, "Image curation failed; recommending the largest images");
            picks = [.. candidates.OrderByDescending(c => (long)c.Candidate.Width * c.Candidate.Height).Take(RecommendCount)
                .Select(c => new LpImagePick(c.Candidate.Index, c.Web.Alt ?? ""))];
        }
        var rank = picks.Select((p, i) => (p, i)).ToDictionary(x => x.p.Index, x => (Rank: x.i + 1, x.p.Description));
        var options = candidates
            .Select(c => new LpImageOption(c.Web.Url.ToString(), c.Candidate.Width, c.Candidate.Height, c.Web.Alt,
                rank.TryGetValue(c.Candidate.Index, out var r) ? r.Rank : null,
                rank.TryGetValue(c.Candidate.Index, out var d) && d.Description.Length > 0 ? d.Description : null))
            .OrderBy(o => o.Recommended ?? int.MaxValue)
            .ThenByDescending(o => (long)o.Width * o.Height)
            .ToList();
        return new LpPreview(page with { Images = [.. options.Select(o => page.ImageList.First(i => i.Url.ToString() == o.Url))] }, options);
    }

    /// <summary>つくるときの手順（画面に進み具合として出す。<see cref="CreateAsync"/> はこの順に進める）。</summary>
    public static IReadOnlyList<string> CreateSteps(LpProjectRequest r)
    {
        var steps = new List<string> { "LP を読み込む" };
        if (r.ImageUrls.Distinct().Any()) steps.Add("LP の画像を取り込む");
        steps.AddRange(r.Platforms.Distinct().Select(p => $"{PlatformCatalog.Get(p).DisplayName}：広告文・投稿文をつくる"));
        steps.Add(r.MakeVideo ? "広告の画像と動画づくりを始める" : "広告の画像づくりを始める");
        return steps;
    }

    /// <summary>この SNS に画像をつくるか（YouTube は動画だけだが、サムネイルはつくる）。</summary>
    public static bool MakesImages(SocialPlatform p) => PlatformCatalog.Get(p).ImageFormats.Count > 0;

    public async Task<IReadOnlyList<LpProject>> ListAsync(int count, CancellationToken ct) =>
        (await db.LpProjects.Where(p => p.WorkspaceId == tenant.WorkspaceId).ToListAsync(ct))
        .OrderByDescending(p => p.CreatedAt).Take(count).ToList();

    public async Task<LpProject> GetAsync(Guid id, CancellationToken ct) =>
        await db.LpProjects.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == tenant.WorkspaceId, ct)
        ?? throw new NotFoundException("つくったもの");

    /// <summary>つくったものを読み直す（画像・動画は別のプロセスのジョブが書き込むため、追跡せずに読む）。</summary>
    public async Task<LpProject> ReloadAsync(Guid id, CancellationToken ct) =>
        await db.LpProjects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == tenant.WorkspaceId, ct)
        ?? throw new NotFoundException("つくったもの");

    /// <summary>画像と動画をつくるジョブ。</summary>
    public async Task<AiJob?> MediaJobAsync(LpProject project, CancellationToken ct) =>
        project.MediaJobId is { } id ? await db.AiJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) : null; // 別のプロセスの更新を読む

    /// <summary>つくる。文章はその場でつくり、画像と動画はジョブとして裏でつくる（進み具合とできあがりは画面で知らせる）。</summary>
    public async Task<LpProject> CreateAsync(LpProjectRequest request, CancellationToken ct, IProgress<LpCreateProgress>? progress = null)
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

        var step = 0;
        progress?.Report(new(step));
        var page = await fetcher.FetchAsync(request.Url, ct);
        var project = new LpProject
        {
            TenantId = tenant.TenantId, WorkspaceId = tenant.WorkspaceId, Url = page.Url.ToString(),
            Title = PostText.Truncate(page.Title is { Length: > 0 } t ? t : page.Url.Host, 200), Platforms = platforms, CreatedBy = tenant.UserName,
            MakeVideo = request.MakeVideo,
        };
        db.LpProjects.Add(project);
        db.Record(tenant, "lp.create", nameof(LpProject), project.Id, $"{string.Join(",", platforms)} video={request.MakeVideo}");
        await db.SaveChangesAsync(ct);

        try
        {
            // LP の画像を取り込む（取れなかった画像は飛ばす）
            var sources = new List<LpSourceImage>();
            if (imageUrls.Count > 0) step++;
            foreach (var (url, n) in imageUrls.Select((u, n) => (u, n)))
            {
                progress?.Report(new(step, $"{n + 1}/{imageUrls.Count}枚"));
                var web = page.ImageList.FirstOrDefault(i => i.Url.ToString() == url) ?? new WebImage(new Uri(url), null);
                try
                {
                    var asset = await media.ImportFromWebAsync(await fetcher.FetchImageAsync(web.Url, ct), web, ct);
                    var description = request.ImageDescriptions.GetValueOrDefault(url) is { Length: > 0 } d ? d : web.Alt ?? "";
                    sources.Add(new LpSourceImage(asset.Id, url, PostText.Truncate(description, 80)));
                }
                catch (DomainException ex)
                {
                    log.LogWarning(ex, "LP image {Url} was skipped", url);
                }
            }
            project.Sources = sources;
            project.SourceImageAssetIds = [.. sources.Select(s => s.AssetId)];

            var ctx = await brand.BuildAsync(tenant.WorkspaceId, [], ct);
            var outputs = new Dictionary<SocialPlatform, LpPlatformOutput>();
            foreach (var platform in platforms)
            {
                progress?.Report(new(++step));
                var creative = await writer.WriteAsync(ctx, page, platform, ct);
                outputs[platform] = new LpPlatformOutput { AdCopies = creative.AdCopies, PostText = creative.PostText, Hashtags = creative.Hashtags };
            }
            project.Outputs = outputs;

            // 画像と動画は裏でつくる（AI の画像生成・動画生成は数分かかる）
            progress?.Report(new(++step));
            var job = new AiJob
            {
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                TaskType = AiTaskType.LpMedia,
                RequestJson = LpMediaService.Serialize(new LpMediaRequest(project.Id, request.MakeVideo,
                    Math.Clamp(request.VideoSeconds, VideoJobRequest.MinSeconds, VideoJobRequest.MaxSeconds), request.Narration)),
                RequestedBy = tenant.UserName,
            };
            db.AiJobs.Add(job);
            project.MediaJobId = job.Id;
            project.Status = LpProjectStatus.Ready;
            db.Record(tenant, "ai.job_queued", nameof(AiJob), job.Id, AiTaskType.LpMedia.ToString());
            await db.SaveChangesAsync(ct);
            await queue.NotifyAiJobAsync(job.Id, ct);
        }
        catch (Exception ex) when (ex is DomainException or AiUnavailableException)
        {
            project.Status = LpProjectStatus.Failed;
            project.Error = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        progress?.Report(new(++step)); // すべて終わった
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
        if (o.Images.Count > 0)
        {
            sb.AppendLine("■ 画像（このフォルダー）");
            foreach (var g in o.Images.GroupBy(x => x.Key)) sb.AppendLine($"・{g.First().Description}：{g.Count()}枚");
            sb.AppendLine();
        }
        if (o.Videos.Count > 0)
        {
            sb.AppendLine("■ 動画（ZIP の一番上のフォルダー）");
            foreach (var v in o.Videos) sb.AppendLine($"・{v.Label}：{(v.Width > v.Height ? "横型" : "縦型")}（{v.Width}×{v.Height}）の動画を使う");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

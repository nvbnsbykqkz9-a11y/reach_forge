using System.Text.Json;
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

/// <summary>LP の広告の画像・動画をつくるジョブの内容。</summary>
public sealed record LpMediaRequest(Guid ProjectId, bool MakeVideo, int VideoSeconds, bool Narration, string? BgmTrackId = "calm");

/// <summary>
/// LP から広告の画像・動画をつくる（ジョブとして裏で実行する）。
/// ① LP の内容から、AI が広告の世界観（雰囲気・色・背景の模様・映像の舞台）と、選んだ画像ごとのビジュアル案をつくる
/// ② 写真は、画像生成 AI が素材の商品・被写体を使って向き（正方形・縦長・横長）ごとの広告写真（キービジュアル）をつくり、
///    画像理解 AI が出来を確かめる（商品が変わった・文字が崩れたなどは、指示を足して1回つくり直す）
///    サービスの画面（スクリーンショット）は描き直さず、世界観に合わせた背景の上で、パソコン・スマートフォンの枠に入れる
/// ③ SNS の形式（Instagram フィード 1080×1350 など）ちょうどに仕上げ、見出しを正確な日本語の文字で入れる
/// ④ 動画は向き（縦型 9:16・横型 16:9）ごとに、動画生成 AI で動きのあるシーン（画面のシーンは背景の映像の上に画面を重ねる）をつくり、
///    テロップ・ナレーション・BGM を重ねて書き出す（動画生成ができないときは、キービジュアルにゆっくりズームをかける）
/// AI でつくれない場合（API キーがないなど）も、無地にはしない：写真は切らずに枠に収め、背景は LP の色で模様を描く。
/// </summary>
public sealed class LpMediaService(
    IAppDbContext db,
    MediaService media,
    IImageProcessor images,
    IImageGenerationService imageGenerator,
    IImageSafetyChecker safety,
    ILpVisualPlanner visualPlanner,
    ILpVisualReviewer reviewer,
    ILandingPageVideoPlanner videoPlanner,
    ITextToSpeech tts,
    IVideoComposer composer,
    IVideoGenerationService videoGenerator,
    IWebPageFetcher fetcher,
    IBrandContextProvider brand,
    TimeProvider clock,
    ILogger<LpMediaService> log)
{
    /// <summary>同時に依頼する生成の数（待ち時間を短くしつつ、各社の同時実行の上限に当たらないように）。</summary>
    public const int MaxParallel = 3;

    /// <summary>動画1本あたり、動画生成 AI で動かすシーンの数（費用と待ち時間の上限）。</summary>
    public const int MaxGeneratedClips = 4;

    /// <summary>画面（スクリーンショット）を入れるキービジュアルの大きさ（向きごと）。SNS の形式へは、背景から形式ちょうどに組み直す。</summary>
    public static (int Width, int Height) ScreenCanvas(MediaOrientation o) => o switch
    {
        MediaOrientation.Portrait => (1440, 2160),
        MediaOrientation.Landscape => (2160, 1440),
        _ => (1800, 1800),
    };

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>向きごとのキービジュアルの比率（画像生成 AI が得意な大きさ）。SNS の形式へは、ここから切り出す。</summary>
    public static AspectRatio KeyVisualAspect(MediaOrientation o) => o switch
    {
        MediaOrientation.Portrait => new(2, 3),
        MediaOrientation.Landscape => new(3, 2),
        _ => new(1, 1),
    };

    /// <summary>縦型（9:16 前後）は、下にアプリのボタン・説明文が重なるので、見出し・テロップを上げる。</summary>
    public static float SafeBottom(int width, int height) => (double)width / height < 0.7 ? TextOverlay.VerticalSafeBottom : 0;

    /// <summary>向きごとの動画の大きさ。</summary>
    public static (int Width, int Height) VideoSize(MediaOrientation o) => o == MediaOrientation.Landscape ? (1920, 1080) : (1080, 1920);

    /// <summary>この SNS の組み合わせでつくる画像・動画の形式（重複なし）。</summary>
    public static (IReadOnlyList<(SocialPlatform Platform, MediaFormat Format)> Images, IReadOnlyList<(SocialPlatform Platform, MediaFormat Format)> Videos)
        FormatsFor(IEnumerable<SocialPlatform> platforms, bool makeVideo)
    {
        var list = platforms.Distinct().Select(PlatformCatalog.Get).ToList();
        return ([.. list.SelectMany(c => c.ImageFormats.Select(f => (c.Platform, f)))],
            makeVideo ? [.. list.SelectMany(c => c.VideoFormats.Select(f => (c.Platform, f)))] : []);
    }

    public static LpMediaRequest Read(AiJob job) => JsonSerializer.Deserialize<LpMediaRequest>(job.RequestJson, s_json)!;

    public static string Serialize(LpMediaRequest request) => JsonSerializer.Serialize(request, s_json);

    /// <summary>素材の画像（取り込んだファイル・中身・何が写っているか・写真か画面か）。</summary>
    private sealed record Source(MediaAsset Asset, byte[] Bytes, string Description, LpSourceKind Kind);

    /// <summary>
    /// キービジュアル。画面の案では <paramref name="Backdrop"/>（端末を置く前の背景）を持ち、SNS の形式・動画の大きさごとに組み直す。
    /// <paramref name="Fallback"/>：AI でつくれなかった（写真を枠に収めた・背景を模様で描いた）。
    /// </summary>
    private sealed record KeyVisual(MediaAsset Asset, byte[] Bytes, bool Fallback, bool Retried, byte[]? Backdrop = null);

    /// <summary>ジョブを実行する（AiJobProcessor から、ジョブのテナント・ワークスペースのコンテキストで呼ぶ）。つくったファイルを返す。</summary>
    public async Task<IReadOnlyList<Guid>> ProcessAsync(AiJob job, CancellationToken ct)
    {
        var request = Read(job);
        var project = await db.LpProjects.FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct) ?? throw new NotFoundException("つくったもの");
        var ctx = await brand.BuildAsync(project.WorkspaceId, [], ct);

        await ReportAsync(job, AiJobStage.Generating, 2, "LP を読み込んでいます", ct);
        var page = await fetcher.FetchAsync(project.Url, ct);
        var sources = new List<Source>();
        foreach (var source in project.Sources)
        {
            var asset = await media.GetAsync(source.AssetId, ct);
            sources.Add(new Source(asset, await media.ReadAsync(asset, ct), source.Description, source.Kind));
        }

        // ① 世界観とビジュアル案（素材画像ごとに1案。素材がなければ LP の内容から1案）
        await ReportAsync(job, AiJobStage.Generating, 6, "AI が LP の内容から、広告の世界観（雰囲気・色・背景）とビジュアルを考えています", ct);
        IReadOnlyList<LpSourceBrief> briefs = sources.Count > 0
            ? [.. sources.Select(s => new LpSourceBrief(string.IsNullOrWhiteSpace(s.Description) ? "LP の画像" : s.Description, s.Kind))]
            : [new LpSourceBrief("（素材画像なし）LP の内容から商品・サービスのイメージをつくる", LpSourceKind.Photo)];
        var plan = await visualPlanner.PlanAsync(ctx, page, briefs, ct);
        var concepts = plan.Concepts;
        var direction = plan.Direction;
        project.Direction = direction;

        var (imageFormats, videoFormats) = FormatsFor(project.Platforms, request.MakeVideo);
        var orientations = imageFormats.Select(x => x.Format.Orientation)
            .Concat(videoFormats.Select(x => x.Format.Orientation)).Distinct().OrderBy(o => o).ToList();

        // ② キービジュアル（案 × 向き）
        var keyVisuals = await KeyVisualsAsync(job, ctx, project, direction, concepts, sources, orientations, ct);
        project.Visuals = [.. concepts.Select((c, i) => new LpVisual
        {
            SourceIndex = c.SourceIndex, Angle = c.Angle, Headline = c.Headline, ImagePrompt = c.ImagePrompt, MotionPrompt = c.MotionPrompt,
            Kind = IsScreen(c, sources) ? LpSourceKind.Screen : LpSourceKind.Photo, BackdropPrompt = c.BackdropPrompt,
            KeyVisuals = orientations.Where(o => keyVisuals.ContainsKey((i, o))).ToDictionary(o => o, o => keyVisuals[(i, o)].Asset.Id),
            Fallback = keyVisuals.Where(k => k.Key.Concept == i).Any(k => k.Value.Fallback),
            Retried = keyVisuals.Where(k => k.Key.Concept == i).Any(k => k.Value.Retried),
        })];
        await db.SaveChangesAsync(ct);

        // ③ SNS の形式ごとの画像
        var created = keyVisuals.Values.Select(k => k.Asset.Id).ToList();
        var outputs = project.Outputs.ToDictionary(o => o.Key, o => o.Value);
        var done = 0;
        foreach (var group in imageFormats.GroupBy(x => x.Platform))
        {
            var list = new List<LpMedia>();
            foreach (var (platform, format) in group)
            {
                await ReportAsync(job, AiJobStage.Generating, 45 + 10 * done++ / Math.Max(1, imageFormats.Count),
                    $"{PlatformCatalog.Get(platform).DisplayName} の{format.Label}（{format.Width}×{format.Height}）の画像に仕上げています", ct);
                for (var i = 0; i < concepts.Count; i++)
                {
                    var asset = await PlatformImageAsync(project, platform, format, concepts[i], i, keyVisuals[(i, format.Orientation)],
                        ScreenOf(concepts[i], sources), ctx, direction, ct);
                    list.Add(new LpMedia(format.Key, format.Label, format.Width, format.Height, asset.Id));
                    created.Add(asset.Id);
                }
            }
            outputs[group.Key] = (outputs.GetValueOrDefault(group.Key) ?? new LpPlatformOutput()) with { Images = list };
        }
        project.Outputs = outputs;
        await db.SaveChangesAsync(ct);

        // ④ 動画（向きごと）
        if (videoFormats.Count > 0)
        {
            var videos = await VideosAsync(job, request, ctx, page, project, direction, concepts, sources, keyVisuals,
                [.. videoFormats.Select(x => x.Format.Orientation).Distinct().OrderBy(o => o)], ct);
            project.Videos = videos.ToDictionary(v => v.Key, v => v.Value.Id);
            created.AddRange(videos.Values.Select(v => v.Id));
            foreach (var group in videoFormats.GroupBy(x => x.Platform))
            {
                outputs[group.Key] = (outputs.GetValueOrDefault(group.Key) ?? new LpPlatformOutput()) with
                {
                    Videos = [.. group.Where(x => videos.ContainsKey(x.Format.Orientation))
                        .Select(x => new LpMedia(x.Format.Key, x.Format.Label, x.Format.Width, x.Format.Height, videos[x.Format.Orientation].Id))],
                };
            }
            project.Outputs = outputs.ToDictionary(o => o.Key, o => o.Value);
        }
        await ReportAsync(job, AiJobStage.Checking, 99, "仕上げています", ct);
        return created;
    }

    /// <summary>画面（スクリーンショット）の案か。</summary>
    private static bool IsScreen(LpVisualConcept concept, List<Source> sources) =>
        concept.SourceIndex < sources.Count && sources[concept.SourceIndex].Kind == LpSourceKind.Screen;

    private static byte[]? ScreenOf(LpVisualConcept concept, List<Source> sources) =>
        IsScreen(concept, sources) ? sources[concept.SourceIndex].Bytes : null;

    /// <summary>LP の世界観に合わせた背景の模様の描き方（案ごとに模様の位置を変える）。</summary>
    private static BackdropStyle Style(LpProject project, LpArtDirection direction, int concept, float time = 0) =>
        new(direction.Palette, direction.Motif, BitConverter.ToInt32(project.Id.ToByteArray(), 0) + concept * 7919, time);

    private static (int Width, int Height) GenerationSize(MediaOrientation o) => o switch
    {
        MediaOrientation.Portrait => (1024, 1536),
        MediaOrientation.Landscape => (1536, 1024),
        _ => (1024, 1024),
    };

    /// <summary>
    /// キービジュアルをつくる（案 × 向き、同時に <see cref="MaxParallel"/> 件まで）。
    /// 写真：素材の商品・被写体を使うよう画像生成 AI に渡し、出来を確かめる。生成できない・安全性の確認で止まった場合は、素材を切らずに枠に収める
    /// （素材もなければ LP の色で模様を描いた背景）。
    /// 画面：画像生成 AI で背景の情景をつくり（できなければ模様を描いた背景）、その上で画面を端末の枠に入れる。
    /// </summary>
    private async Task<Dictionary<(int Concept, MediaOrientation Orientation), KeyVisual>> KeyVisualsAsync(
        AiJob job, BrandContext ctx, LpProject project, LpArtDirection direction, IReadOnlyList<LpVisualConcept> concepts, List<Source> sources,
        IReadOnlyList<MediaOrientation> orientations, CancellationToken ct)
    {
        var work = concepts.SelectMany((c, i) => orientations.Select(o => (Concept: i, Orientation: o))).ToList();
        // 生成は MaxParallel 件ずつ並べて依頼し（DB は使わない）、まとまりごとに進み具合を出す。保存はあとでまとめて行う
        var results = new List<((int Concept, MediaOrientation Orientation) Work, Generated Generated)>();
        foreach (var batch in work.Chunk(MaxParallel))
        {
            await ReportAsync(job, AiJobStage.Generating, 10 + 30 * results.Count / work.Count,
                $"AI が広告の写真・背景をつくり、出来を確かめています（{results.Count + 1}〜{results.Count + batch.Length}/{work.Count}枚目）", ct);
            results.AddRange(await Task.WhenAll(batch.Select(async w =>
            {
                var concept = concepts[w.Concept];
                if (IsScreen(concept, sources))
                {
                    return (w, await GenerateAsync(job, ctx, concept, BackdropPrompt(concept, direction, w.Orientation), null, w.Orientation,
                        review: false, ct));
                }
                var source = concept.SourceIndex < sources.Count ? sources[concept.SourceIndex] : null;
                byte[]? reference = source is null ? null : (await images.ThumbnailAsync(source.Bytes, 1536, ct)).Bytes; // 大きすぎないよう縮めて PNG で渡す
                return (w, await GenerateAsync(job, ctx, concept, KeyVisualPrompt(concept, w.Orientation, reference is not null, direction), reference,
                    w.Orientation, review: true, ct));
            })));
        }

        var saved = new Dictionary<(int, MediaOrientation), KeyVisual>();
        foreach (var ((concept, orientation), generated) in results)
        {
            var c = concepts[concept];
            var source = c.SourceIndex < sources.Count ? sources[c.SourceIndex] : null;
            var screen = IsScreen(c, sources);
            byte[] bytes;
            byte[]? backdrop = null;
            if (screen)
            {
                var canvas = ScreenCanvas(orientation);
                backdrop = generated.Image is { } bg
                    ? (await images.ConvertAspectAsync(bg.Bytes, new AspectRatio(canvas.Width, canvas.Height), canvas, AspectMethod.SmartCrop,
                        "#000000", ct, exact: true)).Bytes
                    : (await images.RenderBackdropAsync(canvas, Style(project, direction, concept), ct)).Bytes;
                bytes = (await images.ComposeDeviceAsync(backdrop, source!.Bytes, canvas, null, ct)).Bytes;
            }
            else if (generated.Image is { } image)
            {
                bytes = image.Bytes;
            }
            else
            {
                // AI でつくれなかった：素材画像を切らずに枠に収める（素材もなければ LP の色で模様を描いた背景）
                bytes = source is null
                    ? (await images.RenderBackdropAsync(GenerationSize(orientation), Style(project, direction, concept), ct)).Bytes
                    : (await images.FitWithBackdropAsync(source.Bytes, GenerationSize(orientation), ct)).Bytes;
            }
            using var input = new MemoryStream(bytes);
            var normalized = await images.NormalizeAsync(input, MediaService.MaxDimension, ct);
            var name = $"広告写真{concept + 1}-{MediaFormat.OrientationLabel(orientation)}.jpg";
            MediaAsset asset;
            if (generated.Image is { } ai)
            {
                var composite = screen || source is not null;
                asset = await media.SaveAsync(normalized, MediaSource.AiEdited, name, source?.Asset.Id, null, ct,
                    new ProvenanceInfo(composite ? DigitalSourceType.CompositeWithTrainedAlgorithmicMedia : DigitalSourceType.TrainedAlgorithmicMedia,
                        composite ? "c2pa.edited" : "c2pa.created", ai.Model.Provider, ai.Model.ModelId, clock.GetUtcNow(), name));
                asset.IsAiLabeled = true;
                asset.SafetyResult = generated.Safety ?? SafetyVerdict.NotChecked.Result;
                asset.AiGenerationId = job.Id;
                asset.Provenance = JsonSerializer.Serialize(new
                {
                    kind = screen ? "lp-key-visual-screen" : "lp-key-visual", provider = ai.Model.Provider, model = ai.Model.ModelId, angle = c.Angle,
                    prompt = screen ? c.BackdropPrompt : c.ImagePrompt, source = source?.Asset.Id, orientation = orientation.ToString(),
                    review = generated.Review is { Score: > 0 } r ? new { r.Score, r.Approved, r.Feedback } : null, retried = generated.Retried,
                });
            }
            else
            {
                asset = await media.SaveAsync(normalized, MediaSource.Derived, name, source?.Asset.Id, null, ct,
                    source is null ? null : media.DerivedOrigin(source.Asset, screen ? "c2pa.placed" : "c2pa.resized"));
                asset.IsAiLabeled = source?.Asset.IsAiLabeled ?? false;
            }
            asset.AltText = string.IsNullOrWhiteSpace(c.Angle) ? source?.Asset.AltText : $"{c.Angle}が伝わる広告{(screen ? "（サービスの画面）" : "写真")}";
            saved[(concept, orientation)] = new KeyVisual(asset, normalized.Bytes, generated.Image is null, generated.Retried, backdrop);
        }
        await db.SaveChangesAsync(ct);
        return saved;
    }

    /// <summary>生成の結果（画像・安全性の確認・出来の確認・作り直したか）。画像が null ならつくれなかった。</summary>
    private sealed record Generated(GeneratedImage? Image, string? Safety, LpVisualReview? Review, bool Retried);

    /// <summary>
    /// 画像を生成する。<paramref name="review"/> が true なら画像理解 AI で出来を確かめ、不合格なら指摘を指示に足して1回だけつくり直し、
    /// 点数の高い方を使う。
    /// </summary>
    private async Task<Generated> GenerateAsync(AiJob job, BrandContext ctx, LpVisualConcept concept, string prompt, byte[]? reference,
        MediaOrientation orientation, bool review, CancellationToken ct)
    {
        var first = await GenerateOnceAsync(job, ctx, prompt, reference, orientation, ct);
        if (first.Image is null || !review) return first;
        var firstReview = await reviewer.ReviewAsync(first.Image.Bytes, first.Image.Mime, reference, reference is null ? null : "image/png", concept, ct);
        if (firstReview.Approved) return first with { Review = firstReview };

        log.LogInformation("Key visual rejected by review ({Score}): {Feedback}; regenerating once for job {JobId}", firstReview.Score,
            firstReview.Feedback, job.Id);
        var fix = string.IsNullOrWhiteSpace(firstReview.Feedback) ? "" : $" Important corrections: {firstReview.Feedback}";
        var second = await GenerateOnceAsync(job, ctx, prompt + fix, reference, orientation, ct);
        if (second.Image is null) return first with { Review = firstReview };
        var secondReview = await reviewer.ReviewAsync(second.Image.Bytes, second.Image.Mime, reference, reference is null ? null : "image/png", concept, ct);
        return secondReview.Score >= firstReview.Score
            ? second with { Review = secondReview, Retried = true }
            : first with { Review = firstReview, Retried = true };
    }

    private async Task<Generated> GenerateOnceAsync(AiJob job, BrandContext ctx, string prompt, byte[]? reference, MediaOrientation orientation,
        CancellationToken ct)
    {
        try
        {
            var generated = await imageGenerator.GenerateAsync(new ImageGenerationSpec
            {
                Prompt = prompt,
                Aspect = KeyVisualAspect(orientation),
                Size = GenerationSize(orientation),
                BrandColors = ctx.Profile.BrandColors,
                BrandName = ctx.Profile.BrandName,
                SourceImage = reference,
                SourceMime = reference is null ? null : "image/png",
            }, job.Id, ct);
            var image = generated[0];
            var verdict = await safety.CheckAsync(image.Bytes, image.Mime, ct);
            if (verdict.Blocked)
            {
                log.LogInformation("Key visual blocked by safety check ({Result}) for job {JobId}", verdict.Result, job.Id);
                return new Generated(null, verdict.Result, null, false);
            }
            return new Generated(image, verdict.Result, null, false);
        }
        catch (Exception ex) when (ex is DomainException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Key visual generation failed for job {JobId}; using a fallback", job.Id);
            return new Generated(null, null, null, false);
        }
    }

    private static string Frame(MediaOrientation orientation) => orientation switch
    {
        MediaOrientation.Portrait => "a vertical (portrait) frame; it will be cropped to 4:5 and 9:16, so keep the subject in the central area",
        MediaOrientation.Landscape => "a horizontal (landscape) frame; it will be cropped to 16:9 and 1.91:1, so keep the subject in the central area",
        _ => "a square frame",
    };

    private static string Look(LpArtDirection? direction) =>
        direction is null ? "" :
        (direction.Palette.Count > 0 ? $"Color palette: {string.Join(", ", direction.Palette)}. " : "") +
        (direction.Setting.Length > 0 ? $"World of the brand: {direction.Setting}. " : "");

    /// <summary>キービジュアルの指示（英語）。素材の商品はそのまま使い、あとで各 SNS の比率に切り出せるよう中央に置いて余白を残す。</summary>
    internal static string KeyVisualPrompt(LpVisualConcept concept, MediaOrientation orientation, bool hasReference, LpArtDirection? direction = null)
    {
        var subject = hasReference
            ? "Create a polished social media advertising photo featuring the exact product/subject shown in the reference image. " +
              "Keep its shape, colors, packaging and printed details exactly as in the reference — do not redesign or replace it. "
            : "Create a polished social media advertising photo. ";
        return subject + concept.ImagePrompt + " " + Look(hasReference ? null : direction) +
               $"Compose for {Frame(orientation)}, with generous margins on every side and a calm, uncluttered lower third for a text band. " +
               "Commercial product photography quality, natural lighting, high detail. " +
               "Do not add any text, letters, numbers, logos, watermarks, or people's faces.";
    }

    /// <summary>画面を置く背景の指示（英語）。中央には端末を置くので、主役のない奥行きのある情景にし、画面の中身・文字は描かない。</summary>
    internal static string BackdropPrompt(LpVisualConcept concept, LpArtDirection direction, MediaOrientation orientation) =>
        $"Create a cinematic, photorealistic background image for an advertisement of a software service. Scene: {concept.BackdropPrompt}. " +
        Look(direction with { Setting = "" }) +
        $"Compose for {Frame(orientation)}. A laptop or smartphone will be placed in the center later, so keep the center simple, softly lit and " +
        "slightly out of focus, with depth and atmosphere around the edges. Keep the lower third calm and darker for a text band. " +
        "Any monitors or screens in the scene must be blurred with no readable content. " +
        "Do not include devices in the foreground, text, letters, numbers, logos, watermarks, or people's faces.";

    /// <summary>
    /// SNS の形式ちょうどの画像。写真：キービジュアルを比率に合わせて切り出し、見出しを入れる。
    /// 画面：背景を形式ちょうどに切り出し、端末の枠に入れた画面と見出しを置く（端末が切れないよう、形式ごとに組み直す）。JPEG にする。
    /// </summary>
    private async Task<MediaAsset> PlatformImageAsync(LpProject project, SocialPlatform platform, MediaFormat format, LpVisualConcept concept,
        int index, KeyVisual keyVisual, byte[]? screen, BrandContext ctx, LpArtDirection direction, CancellationToken ct)
    {
        var size = (format.Width, format.Height);
        var band = Band(ctx, direction, null);
        var caption = concept.Headline.Length > 0
            ? new TextOverlay(concept.Headline, null, TextPosition.Bottom, band, SafeBottom: SafeBottom(format.Width, format.Height))
            : null;
        byte[] bytes;
        if (screen is not null && keyVisual.Backdrop is { } backdrop)
        {
            var background = await images.ConvertAspectAsync(backdrop, format.Aspect, size, AspectMethod.SmartCrop, "#000000", ct, exact: true);
            bytes = (await images.ComposeDeviceAsync(background.Bytes, screen, size, caption, ct)).Bytes;
        }
        else
        {
            bytes = (await images.ConvertAspectAsync(keyVisual.Bytes, format.Aspect, size, AspectMethod.SmartCrop, "#FFFFFF", ct, exact: true)).Bytes;
            if (caption is not null) bytes = (await images.RenderTextAsync(bytes, caption, ct)).Bytes;
        }
        var limit = platform == SocialPlatform.Instagram ? MediaService.InstagramMaxBytes : MediaService.LineMaxBytes;
        var encoded = await images.EncodeJpegAsync(bytes, Math.Min(limit, 5 * 1024 * 1024), ct); // X の画像広告などの上限（5MB）にも収める
        var name = $"{PlatformCatalog.Get(platform).DisplayName}_{format.Label}_{format.Width}x{format.Height}_{index + 1}.jpg";
        var asset = await media.SaveAsync(encoded, MediaSource.Derived, name, keyVisual.Asset.Id,
            $"lp:{project.Id:N}:{platform}:{format.Key}:{index}", ct, media.DerivedOrigin(keyVisual.Asset, "c2pa.edited"));
        asset.IsAiLabeled = keyVisual.Asset.IsAiLabeled;
        asset.AltText = keyVisual.Asset.AltText;
        asset.SafetyResult = keyVisual.Asset.SafetyResult;
        asset.Provenance = keyVisual.Asset.Provenance;
        return asset;
    }

    /// <summary>見出し・テロップの帯の色：ブランドの色 → LP の世界観の色 → LP の色の順。</summary>
    private static string Band(BrandContext ctx, LpArtDirection direction, WebPage? page) =>
        ctx.Profile.BrandColors.FirstOrDefault() ?? direction.Palette.FirstOrDefault() ?? page?.Colors.FirstOrDefault() ?? "#1B2333";

    /// <summary>
    /// 動画を向きごとにつくる。絵コンテとナレーションは共通で、シーンの映像はその向きのキービジュアルから。
    /// 違うビジュアルを使う最初の <see cref="MaxGeneratedClips"/> シーンは動画生成 AI で動かし（画面のシーンは背景の映像を動かして、その上に画面を重ねる）、
    /// ほかはゆっくりズームする。
    /// </summary>
    private async Task<Dictionary<MediaOrientation, MediaAsset>> VideosAsync(AiJob job, LpMediaRequest request, BrandContext ctx, WebPage page,
        LpProject project, LpArtDirection direction, IReadOnlyList<LpVisualConcept> concepts, List<Source> sources,
        Dictionary<(int Concept, MediaOrientation Orientation), KeyVisual> keyVisuals, IReadOnlyList<MediaOrientation> orientations,
        CancellationToken ct)
    {
        await ReportAsync(job, AiJobStage.Generating, 56, "AI が動画の構成（シーン・テロップ・ナレーション）を考えています", ct);
        // 絵コンテの画像の番号 = ビジュアル案の番号（素材画像の説明を代替テキストとして渡す）
        var planPage = page with
        {
            Images = [.. concepts.Select((c, i) => new WebImage(new Uri($"https://visual.local/{i}"),
                string.Join("：", new[] { c.Angle, c.SourceIndex < sources.Count ? sources[c.SourceIndex].Description : "" }.Where(x => x.Length > 0))))],
        };
        var seconds = Math.Clamp(request.VideoSeconds, VideoJobRequest.MinSeconds, VideoJobRequest.MaxSeconds);
        var sceneCount = seconds <= 15 ? 4 : seconds <= 20 ? 5 : 6;
        var plan = await videoPlanner.PlanAsync(ctx, planPage, sceneCount, seconds, ct);

        // ナレーション（向きによらず共通）
        var narrator = new Narrator(tts, log, job.Id);
        var narrations = new List<(byte[]? Wav, double Seconds)>();
        foreach (var (scene, i) in plan.Scenes.Select((s, i) => (s, i)))
        {
            await ReportAsync(job, AiJobStage.Generating, 58 + 4 * i / plan.Scenes.Count, $"ナレーションをつくっています（{i + 1}/{plan.Scenes.Count}）", ct);
            var speech = request.Narration ? await narrator.SpeakAsync(scene.Narration, ct) : null;
            narrations.Add((speech?.Wav, Math.Round(Math.Max(scene.Seconds, (speech?.Seconds ?? 0) + 0.4), 2)));
        }
        if (narrator.Failure is not null) narrations = [.. narrations.Select(n => ((byte[]?)null, n.Seconds))]; // 途中まで付いた分も外す

        var visualOf = plan.Scenes.Select((s, i) => s.ImageIndex is { } k && k < concepts.Count ? k : i % Math.Max(1, concepts.Count)).ToList();
        var band = Band(ctx, direction, page);
        var result = new Dictionary<MediaOrientation, MediaAsset>();
        foreach (var (orientation, n) in orientations.Select((o, n) => (o, n)))
        {
            var size = VideoSize(orientation);
            var label = MediaFormat.OrientationLabel(orientation);
            var basePercent = 64 + 34 * n / orientations.Count;
            var span = 34 / orientations.Count;

            // シーンの映像：写真はキービジュアルを切り出し、画面は背景を切り出して端末の枠に入れた画面を置く
            var frames = new byte[plan.Scenes.Count][];
            var backdrops = new byte[]?[plan.Scenes.Count];
            for (var i = 0; i < plan.Scenes.Count; i++)
            {
                var concept = concepts[visualOf[i]];
                var kv = keyVisuals[(visualOf[i], orientation)];
                if (ScreenOf(concept, sources) is { } screen && kv.Backdrop is { } backdrop)
                {
                    backdrops[i] = (await images.ConvertAspectAsync(backdrop, new AspectRatio(size.Width, size.Height), size, AspectMethod.SmartCrop,
                        "#000000", ct, exact: true)).Bytes;
                    frames[i] = (await images.ComposeDeviceAsync(backdrops[i], screen, size, null, ct)).Bytes;
                }
                else
                {
                    frames[i] = (await images.ConvertAspectAsync(kv.Bytes, new AspectRatio(size.Width, size.Height), size, AspectMethod.SmartCrop,
                        band, ct, exact: true)).Bytes;
                }
            }

            // 動かすシーン：違うビジュアルを使う最初のシーンから。写真を AI でつくれなかったもの（枠に収めた写真）は動かさない
            var clipScenes = visualOf.Select((v, i) => (v, i))
                .Where(x => IsScreen(concepts[x.v], sources) || !keyVisuals[(x.v, orientation)].Fallback)
                .DistinctBy(x => x.v).Take(MaxGeneratedClips).Select(x => x.i).ToHashSet();
            await ReportAsync(job, AiJobStage.Generating, basePercent,
                $"{label}の動画：動画生成 AI でシーンを動かしています（{clipScenes.Count}シーン。数分かかることがあります）", ct);
            using var gate = new SemaphoreSlim(MaxParallel);
            var clips = await Task.WhenAll(Enumerable.Range(0, plan.Scenes.Count).Select(async i =>
            {
                if (!clipScenes.Contains(i)) return null;
                var concept = concepts[visualOf[i]];
                var screen = IsScreen(concept, sources);
                await gate.WaitAsync(ct);
                try
                {
                    // 画面のシーン：背景の情景だけを動かす（AI でつくれなかった背景なら、文章から映像をつくる）
                    var start = screen ? (keyVisuals[(visualOf[i], orientation)].Fallback ? null : backdrops[i]) : frames[i];
                    var prompt = screen
                        ? $"{concept.BackdropPrompt}. {concept.MotionPrompt} Cinematic atmosphere with subtle motion of light and particles; keep the center calm. " +
                          "No readable screens, no text, no logos, no people's faces."
                        : $"{concept.MotionPrompt} Keep the product, colors and composition of the image. Do not add text, logos or people's faces.";
                    return await ClipAsync(job, prompt, start, size, narrations[i].Seconds, ct);
                }
                finally
                {
                    gate.Release();
                }
            }));

            await ReportAsync(job, AiJobStage.Checking, basePercent + span / 2, $"{label}の動画：テロップ・ナレーション・BGM を重ねて書き出しています", ct);
            var scenes = new List<VideoSceneInput>();
            var timeline = new List<(VideoScene Scene, double Seconds)>();
            for (var i = 0; i < plan.Scenes.Count; i++)
            {
                var scene = plan.Scenes[i];
                var caption = string.IsNullOrWhiteSpace(scene.Caption)
                    ? null
                    : new TextOverlay(PostText.Truncate(scene.Caption, TextOverlay.MaxHeadline), null, TextPosition.Bottom, band,
                        SafeBottom: SafeBottom(size.Width, size.Height));
                var still = caption is null ? frames[i] : (await images.RenderTextAsync(frames[i], caption, ct)).Bytes;
                byte[]? overlay = null;
                if (clips[i] is not null)
                {
                    // クリップの上に重ねるもの：画面のシーンは端末の枠に入れた画面（とテロップ）、写真のシーンはテロップだけ
                    overlay = ScreenOf(concepts[visualOf[i]], sources) is { } screen
                        ? (await images.ComposeDeviceAsync(null, screen, size, caption, ct)).Bytes
                        : caption is null ? null : (await images.RenderTextLayerAsync(caption, size, ct)).Bytes;
                }
                scenes.Add(new VideoSceneInput(still, narrations[i].Seconds, narrations[i].Wav, clips[i], overlay));
                timeline.Add((new VideoScene(scene.Caption, scene.Narration, narrations[i].Seconds), narrations[i].Seconds));
            }
            var video = await composer.ComposeAsync(scenes, ct, new VideoAudioOptions(request.BgmTrackId), size);
            var asset = await media.SaveVideoAsync(video, $"{PostText.Truncate(plan.Title, 40)}_{label}_{size.Width}x{size.Height}.mp4", scenes[0].Image,
                VideoService.BuildSrt(timeline), ct);
            asset.IsAiLabeled = true;
            asset.AltText = $"{plan.Title}（{label}・{video.DurationMs / 1000}秒の動画）";
            asset.AltTextIsAi = true;
            var summary = new LandingPageVideoSummary(page.Url.ToString(), plan.Product, plan.Target, plan.Benefits, plan.Offer, plan.CallToAction,
                plan.PostText, plan.Hashtags, clips.Any(c => c is not null), narrator.Failure);
            asset.Provenance = JsonSerializer.Serialize(new
            {
                kind = "landing-page-video", summary, title = plan.Title, orientation = orientation.ToString(),
                direction = new { direction.Mood, direction.Palette, motif = direction.Motif.ToString(), direction.Setting },
                scenes = plan.Scenes.Select((s, i) => new { s.Role, s.Caption, s.Narration, visual = visualOf[i], generated = clips[i] is not null }),
                narration = request.Narration && narrator.Failure is null, narrationError = narrator.Failure, bgm = request.BgmTrackId,
                project = project.Id, createdAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
            result[orientation] = asset;
        }
        return result;
    }

    /// <summary>
    /// 動画生成 AI で動きのあるシーンをつくる（<paramref name="frame"/> があれば、それを起点の画像にする。なければ文章から）。
    /// できなければ null（静止画にゆっくりズームする）。
    /// </summary>
    private async Task<byte[]?> ClipAsync(AiJob job, string prompt, byte[]? frame, (int Width, int Height) size, double seconds, CancellationToken ct)
    {
        try
        {
            var landscape = size.Width > size.Height;
            byte[]? start = null;
            if (frame is not null)
            {
                var resized = await images.ConvertAspectAsync(frame, new AspectRatio(size.Width, size.Height), landscape ? (1280, 720) : (720, 1280),
                    AspectMethod.SmartCrop, "#000000", ct, exact: true);
                start = (await images.EncodeJpegAsync(resized.Bytes, 5_000_000, ct)).Bytes;
            }
            var generated = await videoGenerator.GenerateAsync(new VideoGenerationSpec
            {
                Prompt = prompt + (landscape ? " Horizontal 16:9 video." : " Vertical 9:16 video."),
                Seconds = (int)Math.Clamp(Math.Ceiling(seconds), VideoGenerationSpec.MinSeconds, 10),
                Size = size,
                StartImage = start,
                StartImageMime = start is null ? null : "image/jpeg",
            }, job.Id, ct);
            return generated.Mp4;
        }
        catch (Exception ex) when (ex is DomainException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Clip generation failed for job {JobId}; using a still image", job.Id);
            return null;
        }
    }

    private async Task ReportAsync(AiJob job, AiJobStage stage, int percent, string text, CancellationToken ct)
    {
        job.Report(stage, percent, text);
        await db.SaveChangesAsync(ct);
    }
}

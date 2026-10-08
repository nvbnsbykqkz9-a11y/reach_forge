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
/// ① LP から選んだ特色のある画像ごとに、AI が広告のビジュアル案（切り口・見出し・画像と動画への指示）をつくる
/// ② 画像生成 AI が、素材画像の商品・被写体を使って、向き（正方形・縦長・横長）ごとの広告写真（キービジュアル）をつくる
/// ③ SNS の形式（Instagram フィード 1080×1350 など）ちょうどに切り出し、見出しを正確な日本語の文字で入れる
/// ④ 動画は向き（縦型 9:16・横型 16:9）ごとに、キービジュアルから動画生成 AI で動きのあるシーンをつくり、
///    テロップ・ナレーション・BGM を重ねて書き出す（動画生成ができないときは、キービジュアルにゆっくりズームをかける）
/// AI でつくれない場合（API キーがないなど）も、LP の画像を切らずに枠に収めて（ぼかした背景）仕上げる。
/// </summary>
public sealed class LpMediaService(
    IAppDbContext db,
    MediaService media,
    IImageProcessor images,
    IImageGenerationService imageGenerator,
    IImageSafetyChecker safety,
    ILpVisualPlanner visualPlanner,
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
    public const int MaxGeneratedClips = 3;

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

    /// <summary>ジョブを実行する（AiJobProcessor から、ジョブのテナント・ワークスペースのコンテキストで呼ぶ）。つくったファイルを返す。</summary>
    public async Task<IReadOnlyList<Guid>> ProcessAsync(AiJob job, CancellationToken ct)
    {
        var request = Read(job);
        var project = await db.LpProjects.FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct) ?? throw new NotFoundException("つくったもの");
        var ctx = await brand.BuildAsync(project.WorkspaceId, [], ct);

        await ReportAsync(job, AiJobStage.Generating, 2, "LP を読み込んでいます", ct);
        var page = await fetcher.FetchAsync(project.Url, ct);
        var sources = new List<(MediaAsset Asset, byte[] Bytes, string Description)>();
        foreach (var source in project.Sources)
        {
            var asset = await media.GetAsync(source.AssetId, ct);
            sources.Add((asset, await media.ReadAsync(asset, ct), source.Description));
        }

        // ① ビジュアル案（素材画像ごとに1案。素材がなければ LP の内容から1案）
        await ReportAsync(job, AiJobStage.Generating, 6, "AI が広告のビジュアル（切り口・見出し）を考えています", ct);
        var descriptions = sources.Count > 0
            ? sources.Select(s => string.IsNullOrWhiteSpace(s.Description) ? "LP の画像" : s.Description).ToList()
            : ["（素材画像なし）LP の内容から商品・サービスのイメージをつくる"];
        var concepts = await visualPlanner.PlanAsync(ctx, page, descriptions, ct);

        var (imageFormats, videoFormats) = FormatsFor(project.Platforms, request.MakeVideo);
        var orientations = imageFormats.Select(x => x.Format.Orientation)
            .Concat(videoFormats.Select(x => x.Format.Orientation)).Distinct().OrderBy(o => o).ToList();

        // ② キービジュアル（案 × 向き）
        var keyVisuals = await KeyVisualsAsync(job, ctx, concepts, sources, orientations, ct);
        project.Visuals = [.. concepts.Select((c, i) => new LpVisual
        {
            SourceIndex = c.SourceIndex, Angle = c.Angle, Headline = c.Headline, ImagePrompt = c.ImagePrompt, MotionPrompt = c.MotionPrompt,
            KeyVisuals = orientations.Where(o => keyVisuals.ContainsKey((i, o))).ToDictionary(o => o, o => keyVisuals[(i, o)].Asset.Id),
            Fallback = keyVisuals.Where(k => k.Key.Concept == i).Any(k => k.Value.Fallback),
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
                    var asset = await PlatformImageAsync(project, platform, format, concepts[i], i, keyVisuals[(i, format.Orientation)], ctx, ct);
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
            var videos = await VideosAsync(job, request, ctx, page, project, concepts, sources, keyVisuals,
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

    /// <summary>
    /// キービジュアルをつくる（案 × 向き、同時に <see cref="MaxParallel"/> 件まで）。素材画像があれば、その商品・被写体を使うよう画像生成 AI に渡す。
    /// 生成できない・安全性の確認で止まった場合は、素材画像を切らずに枠に収めたものを使う。
    /// </summary>
    private async Task<Dictionary<(int Concept, MediaOrientation Orientation), (MediaAsset Asset, byte[] Bytes, bool Fallback)>> KeyVisualsAsync(
        AiJob job, BrandContext ctx, IReadOnlyList<LpVisualConcept> concepts, List<(MediaAsset Asset, byte[] Bytes, string Description)> sources,
        IReadOnlyList<MediaOrientation> orientations, CancellationToken ct)
    {
        var work = concepts.SelectMany((c, i) => orientations.Select(o => (Concept: i, Orientation: o))).ToList();
        // 生成は MaxParallel 件ずつ並べて依頼し（DB は使わない）、まとまりごとに進み具合を出す。保存はあとでまとめて行う
        var results = new List<((int Concept, MediaOrientation Orientation) Work, (GeneratedImage? Image, byte[] Bytes, string? Safety) Generated)>();
        foreach (var batch in work.Chunk(MaxParallel))
        {
            await ReportAsync(job, AiJobStage.Generating, 10 + 30 * results.Count / work.Count,
                $"AI が広告の写真をつくっています（{results.Count + 1}〜{results.Count + batch.Length}/{work.Count}枚目）", ct);
            results.AddRange(await Task.WhenAll(batch.Select(async w =>
            {
                var concept = concepts[w.Concept];
                var source = concept.SourceIndex < sources.Count ? sources[concept.SourceIndex] : default;
                return (w, await GenerateKeyVisualAsync(job, ctx, concept, source.Bytes, source.Asset?.Mime, w.Orientation, ct));
            })));
        }

        var saved = new Dictionary<(int, MediaOrientation), (MediaAsset, byte[], bool)>();
        foreach (var ((concept, orientation), generated) in results)
        {
            var c = concepts[concept];
            var source = c.SourceIndex < sources.Count ? sources[c.SourceIndex].Asset : null;
            var bytes = generated.Bytes;
            if (generated.Image is null)
            {
                // AI でつくれなかった：素材画像を切らずに枠に収める（素材もなければブランドの色の背景）
                bytes = source is null
                    ? (await images.CreateBackgroundAsync(ctx.Profile.BrandColors.FirstOrDefault() ?? "#1B2333", Size(orientation), ct)).Bytes
                    : (await images.FitWithBackdropAsync(sources[c.SourceIndex].Bytes, Size(orientation), ct)).Bytes;
            }
            using var input = new MemoryStream(bytes);
            var normalized = await images.NormalizeAsync(input, MediaService.MaxDimension, ct);
            var name = $"広告写真{concept + 1}-{MediaFormat.OrientationLabel(orientation)}.jpg";
            MediaAsset asset;
            if (generated.Image is { } image)
            {
                asset = await media.SaveAsync(normalized, MediaSource.AiEdited, name, source?.Id, null, ct,
                    new ProvenanceInfo(source is null ? DigitalSourceType.TrainedAlgorithmicMedia : DigitalSourceType.CompositeWithTrainedAlgorithmicMedia,
                        source is null ? "c2pa.created" : "c2pa.edited", image.Model.Provider, image.Model.ModelId, clock.GetUtcNow(), name));
                asset.IsAiLabeled = true;
                asset.SafetyResult = generated.Safety ?? SafetyVerdict.NotChecked.Result;
                asset.AiGenerationId = job.Id;
                asset.Provenance = JsonSerializer.Serialize(new
                {
                    kind = "lp-key-visual", provider = image.Model.Provider, model = image.Model.ModelId, angle = c.Angle,
                    prompt = c.ImagePrompt, source = source?.Id, orientation = orientation.ToString(),
                });
            }
            else
            {
                asset = await media.SaveAsync(normalized, MediaSource.Derived, name, source?.Id, null, ct,
                    source is null ? null : media.DerivedOrigin(source, "c2pa.resized"));
                asset.IsAiLabeled = source?.IsAiLabeled ?? false;
            }
            asset.AltText = string.IsNullOrWhiteSpace(c.Angle) ? source?.AltText : $"{c.Angle}が伝わる広告写真";
            saved[(concept, orientation)] = (asset, normalized.Bytes, generated.Image is null);
        }
        await db.SaveChangesAsync(ct);
        return saved;

        static (int, int) Size(MediaOrientation o) => o switch
        {
            MediaOrientation.Portrait => (1024, 1536),
            MediaOrientation.Landscape => (1536, 1024),
            _ => (1024, 1024),
        };
    }

    private async Task<(GeneratedImage? Image, byte[] Bytes, string? Safety)> GenerateKeyVisualAsync(AiJob job, BrandContext ctx,
        LpVisualConcept concept, byte[]? source, string? sourceMime, MediaOrientation orientation, CancellationToken ct)
    {
        try
        {
            var aspect = KeyVisualAspect(orientation);
            byte[]? reference = null;
            if (source is not null)
            {
                // 参照画像は大きすぎないよう縮め、PNG で渡す
                reference = (await images.ThumbnailAsync(source, 1536, ct)).Bytes;
            }
            var generated = await imageGenerator.GenerateAsync(new ImageGenerationSpec
            {
                Prompt = KeyVisualPrompt(concept, orientation, reference is not null),
                Aspect = aspect,
                Size = orientation switch { MediaOrientation.Portrait => (1024, 1536), MediaOrientation.Landscape => (1536, 1024), _ => (1024, 1024) },
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
                return (null, [], verdict.Result);
            }
            return (image, image.Bytes, verdict.Result);
        }
        catch (Exception ex) when (ex is DomainException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Key visual generation failed for job {JobId}; using the LP image as is", job.Id);
            return (null, [], null);
        }
    }

    /// <summary>キービジュアルの指示（英語）。素材の商品はそのまま使い、あとで各 SNS の比率に切り出せるよう中央に置いて余白を残す。</summary>
    internal static string KeyVisualPrompt(LpVisualConcept concept, MediaOrientation orientation, bool hasReference)
    {
        var frame = orientation switch
        {
            MediaOrientation.Portrait => "a vertical (portrait) frame; it will be cropped to 4:5 and 9:16, so keep the subject in the central area",
            MediaOrientation.Landscape => "a horizontal (landscape) frame; it will be cropped to 16:9 and 1.91:1, so keep the subject in the central area",
            _ => "a square frame",
        };
        var subject = hasReference
            ? "Create a polished social media advertising photo featuring the exact product/subject shown in the reference image. " +
              "Keep its shape, colors, packaging and printed details exactly as in the reference — do not redesign or replace it. "
            : "Create a polished social media advertising photo. ";
        return subject + concept.ImagePrompt + " " +
               $"Compose for {frame}, with generous margins on every side and a calm, uncluttered lower third for a text band. " +
               "Commercial product photography quality, natural lighting, high detail. " +
               "Do not add any text, letters, numbers, logos, watermarks, or people's faces.";
    }

    /// <summary>SNS の形式ちょうどの画像：キービジュアルを比率に合わせて切り出し、見出しを入れて JPEG にする。</summary>
    private async Task<MediaAsset> PlatformImageAsync(LpProject project, SocialPlatform platform, MediaFormat format, LpVisualConcept concept,
        int index, (MediaAsset Asset, byte[] Bytes, bool Fallback) keyVisual, BrandContext ctx, CancellationToken ct)
    {
        var fitted = await images.ConvertAspectAsync(keyVisual.Bytes, format.Aspect, (format.Width, format.Height), AspectMethod.SmartCrop,
            "#FFFFFF", ct, exact: true);
        var bytes = fitted.Bytes;
        if (concept.Headline.Length > 0)
        {
            var band = ctx.Profile.BrandColors.FirstOrDefault() ?? "#1B2333";
            bytes = (await images.RenderTextAsync(bytes, new TextOverlay(concept.Headline, null, TextPosition.Bottom, band,
                SafeBottom: SafeBottom(format.Width, format.Height)), ct)).Bytes;
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

    /// <summary>
    /// 動画を向きごとにつくる。絵コンテとナレーションは共通で、シーンの映像はその向きのキービジュアルから。
    /// 最初の <see cref="MaxGeneratedClips"/> シーン（違うビジュアルのもの）は動画生成 AI で動かし、ほかはゆっくりズームする。
    /// </summary>
    private async Task<Dictionary<MediaOrientation, MediaAsset>> VideosAsync(AiJob job, LpMediaRequest request, BrandContext ctx, WebPage page,
        LpProject project, IReadOnlyList<LpVisualConcept> concepts, List<(MediaAsset Asset, byte[] Bytes, string Description)> sources,
        Dictionary<(int Concept, MediaOrientation Orientation), (MediaAsset Asset, byte[] Bytes, bool Fallback)> keyVisuals,
        IReadOnlyList<MediaOrientation> orientations, CancellationToken ct)
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
        var band = ctx.Profile.BrandColors.FirstOrDefault() ?? page.Colors.FirstOrDefault() ?? "#1B2333";
        var result = new Dictionary<MediaOrientation, MediaAsset>();
        foreach (var (orientation, n) in orientations.Select((o, n) => (o, n)))
        {
            var size = VideoSize(orientation);
            var label = MediaFormat.OrientationLabel(orientation);
            var basePercent = 64 + 34 * n / orientations.Count;
            var span = 34 / orientations.Count;

            // 動かすシーン：違うビジュアルを使う最初のシーンから
            var clipScenes = visualOf.Select((v, i) => (v, i)).DistinctBy(x => x.v).Take(MaxGeneratedClips).Select(x => x.i).ToHashSet();
            await ReportAsync(job, AiJobStage.Generating, basePercent,
                $"{label}の動画：動画生成 AI でシーンを動かしています（{clipScenes.Count}シーン。数分かかることがあります）", ct);
            var frames = new byte[plan.Scenes.Count][];
            for (var i = 0; i < plan.Scenes.Count; i++)
            {
                var kv = keyVisuals[(visualOf[i], orientation)];
                frames[i] = (await images.ConvertAspectAsync(kv.Bytes, new AspectRatio(size.Width, size.Height), size, AspectMethod.SmartCrop,
                    band, ct, exact: true)).Bytes;
            }
            using var gate = new SemaphoreSlim(MaxParallel);
            var clips = await Task.WhenAll(Enumerable.Range(0, plan.Scenes.Count).Select(async i =>
            {
                if (!clipScenes.Contains(i) || keyVisuals[(visualOf[i], orientation)].Fallback) return null;
                await gate.WaitAsync(ct);
                try
                {
                    return await ClipAsync(job, concepts[visualOf[i]], frames[i], size, narrations[i].Seconds, ct);
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
                byte[]? overlay = clips[i] is not null && caption is not null ? (await images.RenderTextLayerAsync(caption, size, ct)).Bytes : null;
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
                scenes = plan.Scenes.Select((s, i) => new { s.Role, s.Caption, s.Narration, visual = visualOf[i], generated = clips[i] is not null }),
                narration = request.Narration && narrator.Failure is null, narrationError = narrator.Failure, bgm = request.BgmTrackId,
                project = project.Id, createdAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
            result[orientation] = asset;
        }
        return result;
    }

    /// <summary>キービジュアルから動画生成 AI で動きのあるシーンをつくる。できなければ null（静止画にゆっくりズームする）。</summary>
    private async Task<byte[]?> ClipAsync(AiJob job, LpVisualConcept concept, byte[] frame, (int Width, int Height) size, double seconds,
        CancellationToken ct)
    {
        try
        {
            var landscape = size.Width > size.Height;
            var start = await images.ConvertAspectAsync(frame, new AspectRatio(size.Width, size.Height), landscape ? (1280, 720) : (720, 1280),
                AspectMethod.SmartCrop, "#000000", ct, exact: true);
            var jpeg = await images.EncodeJpegAsync(start.Bytes, 5_000_000, ct);
            var generated = await videoGenerator.GenerateAsync(new VideoGenerationSpec
            {
                Prompt = $"{concept.MotionPrompt} Keep the product, colors and composition of the image. " +
                         "Do not add text, logos or people's faces. " + (landscape ? "Horizontal 16:9 video." : "Vertical 9:16 video."),
                Seconds = (int)Math.Clamp(Math.Ceiling(seconds), VideoGenerationSpec.MinSeconds, 8),
                Size = size,
                StartImage = jpeg.Bytes,
                StartImageMime = "image/jpeg",
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

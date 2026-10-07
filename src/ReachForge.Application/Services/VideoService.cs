using System.Globalization;
using System.Text;
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

/// <summary>ショート動画の作り方（F-05）。</summary>
public enum VideoMode
{
    /// <summary>③ テンプレート合成（複数画像＋テロップ＋ナレーション＋BGM）。</summary>
    Template = 0,

    /// <summary>① テキスト→動画（生成 AI）。</summary>
    TextToVideo = 1,

    /// <summary>② 画像→動画（静止画を起点に生成 AI が動きをつける）。</summary>
    ImageToVideo = 2,

    /// <summary>LP（ランディングページ）から集客動画をつくる（訴求の整理・絵コンテ・LP の画像・ナレーション。冒頭だけ生成 AI で動かせる）。</summary>
    LandingPage = 3,
}

/// <summary>
/// ショート動画の依頼（F-05）。生成 AI の場合、<see cref="Theme"/> は動画の説明（プロンプト）として使う。
/// LP から作る場合は <see cref="SourceUrl"/> の LP を読み、<see cref="SourceImageUrls"/>（利用者が選んだ LP の画像）を素材にする。
/// <see cref="Theme"/> は任意の補足（伝えたいこと）。
/// </summary>
public sealed record VideoJobRequest(string Theme, IReadOnlyList<Guid> ImageAssetIds, bool Narration = true, int TargetSeconds = 20,
    VideoMode Mode = VideoMode.Template, string? BgmTrackId = null,
    string? SourceUrl = null, IReadOnlyList<string>? SourceImageUrls = null, bool AnimateHook = false, bool RightsConfirmed = false)
{
    public const int MinImages = 1;
    public const int MaxImages = 8;
    public const int MinSeconds = 10;
    public const int MaxSeconds = 30;

    public bool IsGenerative => Mode is VideoMode.TextToVideo or VideoMode.ImageToVideo;

    /// <summary>LP から作る動画で使える LP の画像の数。</summary>
    public const int MaxSourceImages = 8;
}

/// <summary>LP から作った動画の企画（動画の来歴に残し、投稿文の案として画面に出す）。</summary>
public sealed record LandingPageVideoSummary(string Url, string Product, string Target, IReadOnlyList<string> Benefits, string Offer,
    string CallToAction, string PostText, IReadOnlyList<string> Hashtags, bool HookAnimated);

/// <summary>
/// ショート動画（F-05）：③ テンプレート合成（複数画像＋テロップ＋ナレーション）。
/// 構成台本（AI）→ 各シーン画像を 9:16 に変換しテロップを焼き込み → TTS でナレーション → 音声の長さに合わせてシーン秒数を調整
/// → FFmpeg で 1080×1920・H.264/AAC・faststart に書き出し → 字幕（SRT）を作成。
/// 実行前に所要時間を表示して確認をとる（画面側）。生成 AI による動画（①②）はプロバイダ導入時に追加する。
/// </summary>
public sealed class VideoService(
    IAppDbContext db,
    ITenantContext tenant,
    MediaService media,
    IImageProcessor images,
    IVideoScriptWriter scripts,
    ITextToSpeech tts,
    IVideoComposer composer,
    IBrandContextProvider brand,
    IWorkQueue queue,
    IVideoGenerationService generator,
    IBgmLibrary bgm,
    IWebPageFetcher fetcher,
    ILandingPageVideoPlanner planner,
    ILogger<VideoService> log)
{
    public static readonly (int Width, int Height) Size = (1080, 1920);
    public static readonly TimeSpan EstimatedDuration = TimeSpan.FromSeconds(60);

    /// <summary>生成 AI の動画の所要時間の目安（数分〜10分。15分で失敗扱い）。</summary>
    public static readonly TimeSpan EstimatedGenerativeDuration = TimeSpan.FromMinutes(5);

    public IReadOnlyList<BgmTrack> BgmTracks => bgm.Tracks;

    /// <summary>LP を読み込む（タイトル・説明・画像の候補を画面に出す）。</summary>
    public async Task<WebPage> PreviewLandingPageAsync(string url, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        return await fetcher.FetchAsync(url, ct);
    }

    /// <summary>LP から作った動画の企画（来歴から読む）。LP から作った動画でなければ null。</summary>
    public static LandingPageVideoSummary? LandingPageSummaryOf(MediaAsset asset)
    {
        if (string.IsNullOrEmpty(asset.Provenance)) return null;
        try
        {
            using var doc = JsonDocument.Parse(asset.Provenance);
            return doc.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "landing-page-video"
                   && doc.RootElement.TryGetProperty("summary", out var summary)
                ? summary.Deserialize<LandingPageVideoSummary>()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<AiJob> EnqueueAsync(VideoJobRequest request, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        if (request.Mode == VideoMode.LandingPage) request = ValidateLandingPage(request);
        else if (string.IsNullOrWhiteSpace(request.Theme)) throw new DomainException(ErrorCodes.Validation, "動画のテーマを入力してください。");
        if (PostText.Length(request.Theme) > 300) throw new DomainException(ErrorCodes.Validation, "テーマは300字以内で入力してください。");
        if (Domain.Guardrails.PromptInjectionDetector.IsSuspicious(request.Theme)) throw new AiSafetyBlockedException("指示の書き換えの疑い");
        if (request.BgmTrackId is { Length: > 0 } track && bgm.Tracks.All(t => t.Id != track))
        {
            throw new DomainException(ErrorCodes.Validation, "選んだ BGM が見つかりません。");
        }
        var ids = request.ImageAssetIds.Distinct().ToList();
        var (min, max) = request.Mode switch
        {
            VideoMode.TextToVideo or VideoMode.LandingPage => (0, 0),
            VideoMode.ImageToVideo => (1, 1),
            _ => (VideoJobRequest.MinImages, VideoJobRequest.MaxImages),
        };
        if (ids.Count < min || ids.Count > max)
        {
            throw new DomainException(ErrorCodes.Validation, request.Mode switch
            {
                VideoMode.TextToVideo => "テキストから作る動画では画像を選ばないでください。",
                VideoMode.LandingPage => "LP から作る動画では、LP の画像から選んでください。",
                VideoMode.ImageToVideo => "動きをつける画像を1枚選んでください。",
                _ => $"画像を{VideoJobRequest.MinImages}〜{VideoJobRequest.MaxImages}枚選んでください。",
            });
        }
        foreach (var id in ids)
        {
            var asset = await media.GetAsync(id, ct);
            if (asset.Kind != MediaKind.Image) throw new DomainException(ErrorCodes.Validation, "動画の素材には画像を選んでください。");
        }
        var normalized = request with
        {
            ImageAssetIds = ids,
            TargetSeconds = request.IsGenerative
                ? Math.Clamp(request.TargetSeconds, VideoGenerationSpec.MinSeconds, VideoGenerationSpec.MaxSeconds)
                : Clamp(request.TargetSeconds),
            Narration = !request.IsGenerative && request.Narration,
        };
        var job = new AiJob
        {
            TenantId = tenant.TenantId,
            WorkspaceId = tenant.WorkspaceId,
            TaskType = AiTaskType.Video,
            RequestJson = JsonSerializer.Serialize(normalized),
            RequestedBy = tenant.UserName,
        };
        db.AiJobs.Add(job);
        db.Record(tenant, "ai.job_queued", nameof(AiJob), job.Id, AiTaskType.Video.ToString());
        await db.SaveChangesAsync(ct);
        await queue.NotifyAiJobAsync(job.Id, ct);
        return job;
    }

    private static VideoJobRequest ValidateLandingPage(VideoJobRequest request)
    {
        if (!Uri.TryCreate(request.SourceUrl?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            throw new DomainException(ErrorCodes.Validation, "LP の URL（https://〜）を入力してください。");
        }
        var images = (request.SourceImageUrls ?? []).Distinct().ToList();
        if (images.Count > VideoJobRequest.MaxSourceImages)
        {
            throw new DomainException(ErrorCodes.Validation, $"LP の画像は{VideoJobRequest.MaxSourceImages}枚まで選べます。");
        }
        if (images.Any(i => !Uri.TryCreate(i, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
        {
            throw new DomainException(ErrorCodes.Validation, "LP の画像の URL が正しくありません。");
        }
        if (images.Count > 0 && !request.RightsConfirmed)
        {
            throw new DomainException(ErrorCodes.Validation, "LP の画像を動画に使う権利があることを確認してください。");
        }
        if (request.AnimateHook && images.Count == 0)
        {
            throw new DomainException(ErrorCodes.Validation, "冒頭を AI で動かすには、LP の画像を1枚以上選んでください。");
        }
        return request with { SourceUrl = url.ToString(), SourceImageUrls = images, Theme = request.Theme?.Trim() ?? "" };
    }

    /// <summary>動画を作る（AiJobProcessor から、依頼したテナント・ワークスペースのコンテキストで呼ぶ）。</summary>
    public async Task<MediaAsset> ProcessAsync(AiJob job, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<VideoJobRequest>(job.RequestJson)!;
        if (request.Mode == VideoMode.LandingPage) return await LandingPageAsync(job, request, ct);
        if (request.IsGenerative) return await GenerateAsync(job, request, ct);
        var sources = new List<MediaAsset>();
        foreach (var id in request.ImageAssetIds) sources.Add(await media.GetAsync(id, ct));
        var ctx = await brand.BuildAsync(job.WorkspaceId, [], ct);

        // ① 構成台本（シーン数は 3〜6、画像が少なければ繰り返し使う）
        var sceneCount = Math.Clamp(sources.Count, 3, 6);
        var script = await scripts.WriteAsync(ctx, request.Theme, sceneCount, request.TargetSeconds, ct);
        if (script.Scenes.Count == 0) throw new AiUnavailableException("動画の構成をつくれませんでした。もう一度お試しください。");
        job.MoveTo(AiJobStage.Generating);
        await db.SaveChangesAsync(ct);

        // ② シーン画像（9:16・テロップ焼き込み）と ③ ナレーション
        var band = ctx.Profile.BrandColors.FirstOrDefault() ?? "#1B2333";
        var scenes = new List<VideoSceneInput>();
        var timeline = new List<(VideoScene Scene, double Seconds)>();
        byte[]? firstFrame = null;
        foreach (var (scene, i) in script.Scenes.Select((s, i) => (s, i)))
        {
            var source = sources[i % sources.Count];
            var converted = await images.ConvertAspectAsync(await media.ReadAsync(source, ct), new AspectRatio(9, 16), Size,
                AspectMethod.SmartCrop, band, ct);
            var frame = string.IsNullOrWhiteSpace(scene.Caption)
                ? converted
                : await images.RenderTextAsync(converted.Bytes, new TextOverlay(PostText.Truncate(scene.Caption, TextOverlay.MaxHeadline), null,
                    TextPosition.Bottom, band), ct);
            firstFrame ??= frame.Bytes;
            var seconds = Math.Clamp(scene.Seconds, 2, 10);
            byte[]? narration = null;
            if (request.Narration && !string.IsNullOrWhiteSpace(scene.Narration))
            {
                var speech = await tts.SynthesizeAsync(scene.Narration, ct);
                narration = speech.Wav;
                seconds = Math.Max(seconds, speech.Seconds + 0.4); // 音声の長さに合わせてシーンを延ばす
            }
            scenes.Add(new VideoSceneInput(frame.Bytes, Math.Round(seconds, 2), narration));
            timeline.Add((scene, Math.Round(seconds, 2)));
        }

        // ④⑤ 合成・書き出し
        job.MoveTo(AiJobStage.Checking);
        await db.SaveChangesAsync(ct);
        var video = await composer.ComposeAsync(scenes, ct, new VideoAudioOptions(request.BgmTrackId));
        var max = PlatformCatalog.All.Where(c => c.MaxVideoSeconds is not null).Min(c => c.MaxVideoSeconds!.Value);
        if (video.DurationMs > max * 1000) throw new DomainException(ErrorCodes.Validation, $"動画が長すぎます（{max}秒まで）。");

        var asset = await media.SaveVideoAsync(video, $"{PostText.Truncate(script.Title, 40)}.mp4", firstFrame!, BuildSrt(timeline), ct);
        asset.IsAiLabeled = true; // AI の台本・ナレーション（SNS の AI ラベルを付ける）
        asset.AltText = $"{script.Title}（{video.DurationMs / 1000}秒の動画）";
        asset.AltTextIsAi = true;
        asset.Provenance = JsonSerializer.Serialize(new
        {
            kind = "template-video", theme = request.Theme, scenes = timeline.Count, narration = request.Narration,
            sources = request.ImageAssetIds, bgm = request.BgmTrackId, createdAt = DateTimeOffset.UtcNow,
        });
        return asset;
    }

    /// <summary>
    /// ①② 生成 AI の動画：起点の画像（②）を 9:16 に整えて渡し、返ってきたクリップを 1080×1920・H.264/AAC に整えて BGM を重ねる。
    /// </summary>
    private async Task<MediaAsset> GenerateAsync(AiJob job, VideoJobRequest request, CancellationToken ct)
    {
        byte[]? start = null;
        if (request.Mode == VideoMode.ImageToVideo)
        {
            var source = await media.GetAsync(request.ImageAssetIds[0], ct);
            var converted = await images.ConvertAspectAsync(await media.ReadAsync(source, ct), new AspectRatio(9, 16), Size,
                AspectMethod.SmartCrop, "#000000", ct);
            start = (await images.EncodeJpegAsync(converted.Bytes, 5_000_000, ct)).Bytes;
        }
        job.MoveTo(AiJobStage.Generating);
        await db.SaveChangesAsync(ct);

        var generated = await generator.GenerateAsync(new VideoGenerationSpec
        {
            Prompt = request.Theme + "。縦型のショート動画。実在の人物・他社のキャラクターやロゴは出さない。",
            Seconds = request.TargetSeconds,
            Size = Size,
            StartImage = start,
            StartImageMime = start is null ? null : "image/jpeg",
        }, job.Id, ct);

        job.MoveTo(AiJobStage.Checking);
        await db.SaveChangesAsync(ct);
        var video = await composer.FinishClipAsync(generated.Mp4, new VideoAudioOptions(request.BgmTrackId), ct);
        var firstFrame = start ?? await composer.ExtractFrameAsync(video.Mp4, 0.5, ct);
        var title = PostText.Truncate(request.Theme, 40);
        var asset = await media.SaveVideoAsync(video, $"{title}.mp4", firstFrame, "", ct,
            request.Mode == VideoMode.TextToVideo ? DigitalSourceType.TrainedAlgorithmicMedia : DigitalSourceType.CompositeWithTrainedAlgorithmicMedia);
        asset.IsAiLabeled = true;
        asset.AltText = $"{title}（AIで生成した{video.DurationMs / 1000}秒の動画）";
        asset.AltTextIsAi = true;
        asset.Provenance = JsonSerializer.Serialize(new
        {
            kind = request.Mode == VideoMode.TextToVideo ? "text-to-video" : "image-to-video",
            provider = generated.Model.Provider, model = generated.Model.ModelId, fallbackUsed = generated.Model.FallbackUsed,
            theme = request.Theme, sources = request.ImageAssetIds, bgm = request.BgmTrackId, createdAt = DateTimeOffset.UtcNow,
        });
        return asset;
    }

    /// <summary>
    /// LP から集客動画をつくる：LP を読み → 選んだ画像を取り込み → AI が訴求を整理して絵コンテをつくり（価格は LP と突き合わせ）
    /// → 各シーンの画像を 9:16 に整えてテロップを焼き込み、ナレーションを付ける → （希望すれば）冒頭のシーンを生成 AI で動かす
    /// → BGM を重ねて書き出し、字幕と投稿文の案を残す。冒頭の生成に失敗しても静止画で作る。
    /// </summary>
    private async Task<MediaAsset> LandingPageAsync(AiJob job, VideoJobRequest request, CancellationToken ct)
    {
        await ReportAsync(job, AiJobStage.Generating, 3, "LP を読み込んでいます", ct);
        var page = await fetcher.FetchAsync(request.SourceUrl!, ct);
        var imported = new List<(WebImage Web, MediaAsset Asset)>();
        var sourceUrls = request.SourceImageUrls ?? [];
        foreach (var (url, n) in sourceUrls.Select((u, n) => (u, n)))
        {
            await ReportAsync(job, AiJobStage.Generating, 5 + 10 * n / sourceUrls.Count, $"LP の画像を取り込んでいます（{n + 1}/{sourceUrls.Count}）", ct);
            var web = page.ImageList.FirstOrDefault(i => i.Url.ToString() == url) ?? new WebImage(new Uri(url), null);
            try
            {
                imported.Add((web, await media.ImportFromWebAsync(await fetcher.FetchImageAsync(web.Url, ct), web, ct)));
            }
            catch (DomainException ex)
            {
                log.LogInformation("Skipped LP image {Url}: {Reason}", url, ex.Message); // 取得できない画像は使わずに続ける
            }
        }
        if (request.AnimateHook && imported.Count == 0)
        {
            log.LogInformation("No LP image could be fetched for job {JobId}; the hook stays still", job.Id);
        }

        // AI には取り込めた画像だけを番号付きで見せ、利用者の補足は LP の本文の前に添える
        var planPage = page with
        {
            Images = imported.Select(i => i.Web).ToList(),
            Text = string.IsNullOrWhiteSpace(request.Theme) ? page.Text : $"（伝えたいこと：{request.Theme}）\n{page.Text}",
        };
        var ctx = await brand.BuildAsync(job.WorkspaceId, [], ct);
        var sceneCount = request.TargetSeconds <= 15 ? 4 : request.TargetSeconds <= 20 ? 5 : 6;
        await ReportAsync(job, AiJobStage.Generating, 15, "AI が動画の構成（シーン・テロップ・ナレーション）を考えています", ct);
        var plan = await planner.PlanAsync(ctx, planPage, sceneCount, request.TargetSeconds, ct);

        var band = ctx.Profile.BrandColors.FirstOrDefault() ?? page.Colors.FirstOrDefault() ?? "#1B2333";
        var scenes = new List<VideoSceneInput>();
        var timeline = new List<(VideoScene Scene, double Seconds)>();
        byte[]? firstFrame = null;
        var hookAnimated = false;
        foreach (var (scene, i) in plan.Scenes.Select((s, i) => (s, i)))
        {
            await ReportAsync(job, AiJobStage.Generating, 20 + 60 * i / plan.Scenes.Count,
                $"シーン {i + 1}/{plan.Scenes.Count} をつくっています（画像・テロップ{(request.Narration ? "・ナレーション" : "")}）", ct);
            MediaAsset? source = scene.ImageIndex is { } k ? imported[k].Asset : imported.Count > 0 ? imported[i % imported.Count].Asset : null;
            var background = source is null
                ? await images.CreateBackgroundAsync(band, Size, ct)
                : await images.ConvertAspectAsync(await media.ReadAsync(source, ct), new AspectRatio(9, 16), Size, AspectMethod.SmartCrop, band, ct);
            var caption = string.IsNullOrWhiteSpace(scene.Caption)
                ? null
                : new TextOverlay(PostText.Truncate(scene.Caption, TextOverlay.MaxHeadline), null,
                    source is null ? TextPosition.Center : TextPosition.Bottom, band);
            var frame = caption is null ? background : await images.RenderTextAsync(background.Bytes, caption, ct);
            firstFrame ??= frame.Bytes;

            var seconds = scene.Seconds;
            byte[]? narration = null;
            if (request.Narration && !string.IsNullOrWhiteSpace(scene.Narration))
            {
                var speech = await tts.SynthesizeAsync(scene.Narration, ct);
                narration = speech.Wav;
                seconds = Math.Max(seconds, speech.Seconds + 0.4);
            }
            seconds = Math.Round(seconds, 2);

            byte[]? clip = null, overlay = null;
            if (i == 0 && request.AnimateHook && source is not null)
            {
                try
                {
                    await ReportAsync(job, AiJobStage.Generating, 20 + 60 * i / plan.Scenes.Count,
                        "冒頭のシーンを AI で動かしています（数分かかることがあります）", ct);
                    var start = (await images.EncodeJpegAsync(background.Bytes, 5_000_000, ct)).Bytes;
                    var generated = await generator.GenerateAsync(new VideoGenerationSpec
                    {
                        Prompt = $"{(string.IsNullOrWhiteSpace(plan.HookMotion) ? "slow push-in camera move" : plan.HookMotion)}. " +
                                 "Keep the product, colors and composition of the image. Do not add text, logos or people's faces. Vertical short video.",
                        Seconds = (int)Math.Clamp(Math.Ceiling(seconds), VideoGenerationSpec.MinSeconds, 8),
                        Size = Size,
                        StartImage = start,
                        StartImageMime = "image/jpeg",
                    }, job.Id, ct);
                    clip = generated.Mp4;
                    overlay = caption is null ? null : (await images.RenderTextLayerAsync(caption, Size, ct)).Bytes;
                    hookAnimated = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // 冒頭を動かせなくても静止画で作る
                    log.LogWarning(ex, "Hook animation failed for job {JobId}; using a still image", job.Id);
                }
            }
            scenes.Add(new VideoSceneInput(frame.Bytes, seconds, narration, clip, overlay));
            timeline.Add((new VideoScene(scene.Caption, scene.Narration, seconds), seconds));
        }

        await ReportAsync(job, AiJobStage.Checking, 85, "BGM を重ねて、動画を書き出しています", ct);
        var video = await composer.ComposeAsync(scenes, ct, new VideoAudioOptions(request.BgmTrackId));
        var max = PlatformCatalog.All.Where(c => c.MaxVideoSeconds is not null).Min(c => c.MaxVideoSeconds!.Value);
        if (video.DurationMs > max * 1000) throw new DomainException(ErrorCodes.Validation, $"動画が長すぎます（{max}秒まで）。");

        await ReportAsync(job, AiJobStage.Checking, 95, "仕上げています（サムネイル・字幕）", ct);
        var asset = await media.SaveVideoAsync(video, $"{PostText.Truncate(plan.Title, 40)}.mp4", firstFrame!, BuildSrt(timeline), ct);
        asset.IsAiLabeled = true; // AI の企画・ナレーション（冒頭を動かした場合は AI の映像も含む）
        asset.AltText = $"{plan.Title}（{page.Url.Host} の LP から作った{video.DurationMs / 1000}秒の動画）";
        asset.AltTextIsAi = true;
        var summary = new LandingPageVideoSummary(page.Url.ToString(), plan.Product, plan.Target, plan.Benefits, plan.Offer, plan.CallToAction,
            plan.PostText, plan.Hashtags, hookAnimated);
        asset.Provenance = JsonSerializer.Serialize(new
        {
            kind = "landing-page-video", summary, title = plan.Title,
            scenes = plan.Scenes.Select(s => new { s.Role, s.Caption, s.Narration, s.ImageIndex }),
            sources = imported.Select(i => new { url = i.Web.Url.ToString(), assetId = i.Asset.Id }),
            narration = request.Narration, bgm = request.BgmTrackId, createdAt = DateTimeOffset.UtcNow,
        });
        return asset;
    }

    private async Task ReportAsync(AiJob job, AiJobStage stage, int percent, string text, CancellationToken ct)
    {
        job.Report(stage, percent, text);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>字幕（SRT）：各シーンのナレーション（なければテロップ）を表示する。</summary>
    public static string BuildSrt(IReadOnlyList<(VideoScene Scene, double Seconds)> timeline)
    {
        var sb = new StringBuilder();
        var start = 0.0;
        var n = 1;
        foreach (var (scene, seconds) in timeline)
        {
            var text = string.IsNullOrWhiteSpace(scene.Narration) ? scene.Caption : scene.Narration;
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append(n++.ToString(CultureInfo.InvariantCulture)).Append('\n')
                  .Append(Time(start)).Append(" --> ").Append(Time(start + seconds)).Append('\n')
                  .Append(text.Trim()).Append("\n\n");
            }
            start += seconds;
        }
        return sb.ToString();

        static string Time(double s)
        {
            var t = TimeSpan.FromSeconds(s);
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
        }
    }

    private static int Clamp(int seconds) => Math.Clamp(seconds, VideoJobRequest.MinSeconds, VideoJobRequest.MaxSeconds);
}

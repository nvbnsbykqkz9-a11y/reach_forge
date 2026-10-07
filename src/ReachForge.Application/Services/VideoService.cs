using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;
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
}

/// <summary>ショート動画の依頼（F-05）。生成 AI の場合、<see cref="Theme"/> は動画の説明（プロンプト）として使う。</summary>
public sealed record VideoJobRequest(string Theme, IReadOnlyList<Guid> ImageAssetIds, bool Narration = true, int TargetSeconds = 20,
    VideoMode Mode = VideoMode.Template, string? BgmTrackId = null)
{
    public const int MinImages = 1;
    public const int MaxImages = 8;
    public const int MinSeconds = 10;
    public const int MaxSeconds = 30;

    public bool IsGenerative => Mode is VideoMode.TextToVideo or VideoMode.ImageToVideo;
}

/// <summary>
/// ショート動画（F-05）：③ テンプレート合成（複数画像＋テロップ＋ナレーション）。
/// 構成台本（AI）→ 各シーン画像を 9:16 に変換しテロップを焼き込み → TTS でナレーション → 音声の長さに合わせてシーン秒数を調整
/// → FFmpeg で 1080×1920・H.264/AAC・faststart に書き出し → 字幕（SRT）を作成。
/// 実行前に推定クレジットと所要時間を表示して確認をとる（画面側）。生成 AI による動画（①②）はプロバイダ導入時に追加する。
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
    ICreditService credits,
    IWorkQueue queue,
    IVideoGenerationService generator,
    IBgmLibrary bgm)
{
    public static readonly (int Width, int Height) Size = (1080, 1920);
    public static readonly TimeSpan EstimatedDuration = TimeSpan.FromSeconds(60);

    /// <summary>生成 AI の動画の所要時間の目安（数分〜10分。15分で失敗扱い）。</summary>
    public static readonly TimeSpan EstimatedGenerativeDuration = TimeSpan.FromMinutes(5);

    public IReadOnlyList<BgmTrack> BgmTracks => bgm.Tracks;

    /// <summary>推定クレジット（テンプレート合成＋ナレーション 30秒ごと／生成 AI の動画は1本あたり）。</summary>
    public static int EstimateCredits(VideoJobRequest r) => r.IsGenerative
        ? CreditTable.Cost(CreditOperation.ShortVideo)
        : CreditTable.Cost(CreditOperation.TemplateVideo)
          + (r.Narration ? CreditTable.Cost(CreditOperation.Narration30s, (int)Math.Ceiling(Clamp(r.TargetSeconds) / 30.0)) : 0);

    public async Task<AiJob> EnqueueAsync(VideoJobRequest request, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        if (string.IsNullOrWhiteSpace(request.Theme)) throw new DomainException(ErrorCodes.Validation, "動画のテーマを入力してください。");
        if (PostText.Length(request.Theme) > 300) throw new DomainException(ErrorCodes.Validation, "テーマは300字以内で入力してください。");
        if (Domain.Guardrails.PromptInjectionDetector.IsSuspicious(request.Theme)) throw new AiSafetyBlockedException("指示の書き換えの疑い");
        if (request.BgmTrackId is { Length: > 0 } track && bgm.Tracks.All(t => t.Id != track))
        {
            throw new DomainException(ErrorCodes.Validation, "選んだ BGM が見つかりません。");
        }
        var ids = request.ImageAssetIds.Distinct().ToList();
        var (min, max) = request.Mode switch
        {
            VideoMode.TextToVideo => (0, 0),
            VideoMode.ImageToVideo => (1, 1),
            _ => (VideoJobRequest.MinImages, VideoJobRequest.MaxImages),
        };
        if (ids.Count < min || ids.Count > max)
        {
            throw new DomainException(ErrorCodes.Validation, request.Mode switch
            {
                VideoMode.TextToVideo => "テキストから作る動画では画像を選ばないでください。",
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
        var amount = EstimateCredits(normalized);
        await credits.ReserveAsync(amount, ct);
        try
        {
            var job = new AiJob
            {
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                TaskType = AiTaskType.Video,
                RequestJson = JsonSerializer.Serialize(normalized),
                CreditsHeld = amount,
                RequestedBy = tenant.UserName,
            };
            db.AiJobs.Add(job);
            db.Record(tenant, "ai.job_queued", nameof(AiJob), job.Id, AiTaskType.Video.ToString());
            await db.SaveChangesAsync(ct);
            await queue.NotifyAiJobAsync(job.Id, ct);
            return job;
        }
        catch
        {
            await credits.ReleaseReservedAsync(amount, CancellationToken.None);
            throw;
        }
    }

    /// <summary>動画を作る（AiJobProcessor から、依頼したテナント・ワークスペースのコンテキストで呼ぶ）。</summary>
    public async Task<(MediaAsset Video, int Credits)> ProcessAsync(AiJob job, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<VideoJobRequest>(job.RequestJson)!;
        if (request.IsGenerative) return await GenerateAsync(job, request, ct);
        var sources = new List<MediaAsset>();
        foreach (var id in request.ImageAssetIds) sources.Add(await media.GetAsync(id, ct));
        var ctx = await brand.BuildAsync(job.WorkspaceId, [], null, ct);

        // ① 構成台本（シーン数は 3〜6、画像が少なければ繰り返し使う）
        var sceneCount = Math.Clamp(sources.Count, 3, 6);
        var script = await scripts.WriteAsync(ctx, request.Theme, sceneCount, request.TargetSeconds, ct);
        if (script.Scenes.Count == 0) throw new AiUnavailableException("動画の構成をつくれませんでした。もう一度お試しください（クレジットは消費されていません）。");
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
        var actual = CreditTable.Cost(CreditOperation.TemplateVideo)
                     + (request.Narration ? CreditTable.Cost(CreditOperation.Narration30s, (int)Math.Ceiling(video.DurationMs / 30_000.0)) : 0);
        return (asset, actual);
    }

    /// <summary>
    /// ①② 生成 AI の動画：起点の画像（②）を 9:16 に整えて渡し、返ってきたクリップを 1080×1920・H.264/AAC に整えて BGM を重ねる。
    /// </summary>
    private async Task<(MediaAsset Video, int Credits)> GenerateAsync(AiJob job, VideoJobRequest request, CancellationToken ct)
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
        return (asset, CreditTable.Cost(CreditOperation.ShortVideo));
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

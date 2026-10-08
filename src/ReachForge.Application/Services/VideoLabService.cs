using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

/// <summary>動画生成のテストの依頼（プロバイダ・モデル・プロンプト・比率・長さ・起点の画像）。</summary>
public sealed record VideoLabRequest
{
    public required string Provider { get; init; }
    public string? Model { get; init; }
    public required string Prompt { get; init; }
    public string? NegativePrompt { get; init; }

    /// <summary>横型（16:9）でつくるか。それ以外は縦型（9:16）。</summary>
    public bool Landscape { get; init; }

    public int Seconds { get; init; } = 5;

    /// <summary>高画質モード（Kling の pro）。</summary>
    public bool HighQuality { get; init; }

    /// <summary>試した内容の名前（プリセット名など。結果の一覧に表示する）。</summary>
    public string? Label { get; init; }

    public byte[]? StartImage { get; init; }
}

/// <summary>動画生成のテストの結果（保存した動画と、つくったときの設定・かかった時間・評価）。</summary>
public sealed record VideoLabResult(Guid AssetId, DateTimeOffset CreatedAt, string Label, string Provider, string Model, string Prompt,
    string NegativePrompt, bool Landscape, int Seconds, bool HighQuality, bool StartImage, long ElapsedMs, decimal CostUsd, int Rating, string Note);

/// <summary>試すプロンプトのひな形（LP の広告動画でアプリが使う場面ごと）。</summary>
public sealed record VideoLabPreset(string Name, string Description, string Prompt, bool NeedsStartImage = false);

/// <summary>
/// 動画生成 AI（Kling・Veo など）の品質テスト。プロバイダ・モデル・プロンプトを変えて生成し、動画と設定を残して見比べる。
/// 結果はメディアに保存し（来歴に設定を記録）、評価（良い・いまいち）とメモを付けられる。生成には各社の利用料がかかる。
/// </summary>
public sealed class VideoLabService(
    IAppDbContext db,
    MediaService media,
    IVideoGenerationService generator,
    IVideoComposer composer,
    IImageProcessor images,
    ITenantContext tenant,
    TimeProvider clock)
{
    public const string Kind = "video-lab";

    public const int MaxPromptLength = 2500;

    /// <summary>LP の広告動画でアプリが使う場面ごとのひな形（セキュリティ製品の LP を例にしたもの）。</summary>
    public static readonly IReadOnlyList<VideoLabPreset> Presets =
    [
        new("1-A 冒頭（英語・具体的）", "アプリの方式。映像の舞台を英語で具体的に",
            "A dim security operations center at night, walls of monitors glowing with red alert warnings flashing one after another, tense atmosphere, " +
            "slow dolly-in camera move, cinematic lighting with deep blue shadows and red highlights, shallow depth of field, photorealistic."),
        new("1-B 冒頭（日本語）", "1-A と同じ内容を日本語で（言語による違いを見る）",
            "夜のセキュリティ監視室。壁一面のモニターに赤い警告が次々と点滅し、緊張感が高まる。カメラはゆっくり前進。青い影と赤い光の映画的な照明、背景はぼかし、実写風。"),
        new("2-A 解決（抽象的）", "解決の場面を抽象的な表現で",
            "Countless red alert lights in a dark digital space are gathered and calmed by a glowing blue AI core, turning into a few clear signals, " +
            "smooth slow camera orbit, clean futuristic atmosphere, cinematic, high detail."),
        new("2-B 解決（実写的）", "解決の場面を実写的な表現で",
            "The same security operations center becomes calm: monitors switch from red alerts to calm blue dashboards, a soft blue light fills the room, " +
            "slow push-in camera, relief and confidence, cinematic photorealistic."),
        new("3 画面を重ねる背景", "中央に製品の画面を重ねるための背景（中央があいたままか）",
            "Cinematic background for a software advertisement: a modern dark office at night with blurred city lights, soft blue light particles drifting, " +
            "the center of the frame stays empty, simple and out of focus, slow gentle camera drift, no screens with readable content, photorealistic."),
        new("4 画像から動画", "起点の画像（LP の写真・画面）を動かす。画像の中身が崩れないか", 
            "Slow push-in camera move with subtle light reflections moving across the scene, keep the composition, colors and all details of the image exactly, no new objects.",
            NeedsStartImage: true),
    ];

    public IReadOnlyList<VideoProviderInfo> Providers() => generator.Providers();

    /// <summary>動画をつくって保存する。失敗したら、AI サービスが返した理由を添えて知らせる。</summary>
    public async Task<VideoLabResult> RunAsync(VideoLabRequest request, CancellationToken ct)
    {
        var prompt = request.Prompt.Trim();
        if (prompt.Length == 0) throw new DomainException(ErrorCodes.Validation, "プロンプトを入力してください。");
        if (prompt.Length > MaxPromptLength) throw new DomainException(ErrorCodes.Validation, $"プロンプトは{MaxPromptLength}字以内にしてください。");
        var size = request.Landscape ? (1280, 720) : (720, 1280);
        byte[]? start = null;
        if (request.StartImage is { Length: > 0 } image)
        {
            // 起点の画像は動画と同じ比率・大きさにそろえる（比率は起点の画像で決まるプロバイダがあるため）
            var fitted = await images.ConvertAspectAsync(image, new AspectRatio(size.Item1, size.Item2), size, AspectMethod.SmartCrop, "#000000", ct, exact: true);
            start = (await images.EncodeJpegAsync(fitted.Bytes, 5_000_000, ct)).Bytes;
        }

        var sw = Stopwatch.StartNew();
        GeneratedVideo video;
        try
        {
            video = await generator.GenerateAsync(new VideoGenerationSpec
            {
                Prompt = prompt,
                NegativePrompt = string.IsNullOrWhiteSpace(request.NegativePrompt) ? null : request.NegativePrompt.Trim(),
                Seconds = Math.Clamp(request.Seconds, VideoGenerationSpec.MinSeconds, 10),
                Size = request.Landscape ? (1920, 1080) : (1080, 1920),
                StartImage = start,
                StartImageMime = start is null ? null : "image/jpeg",
                Provider = request.Provider,
                Model = request.Model,
                HighQuality = request.HighQuality,
            }, null, ct);
        }
        finally
        {
            await db.SaveChangesAsync(ct); // 利用料金の記録（失敗した場合も）
        }
        sw.Stop();

        var provider = generator.Providers().FirstOrDefault(p => p.Name == video.Model.Provider);
        var seconds = Math.Clamp(request.Seconds, VideoGenerationSpec.MinSeconds, 10);
        var frame = await composer.ExtractFrameAsync(video.Mp4, 0.5, ct);
        var label = string.IsNullOrWhiteSpace(request.Label) ? "テスト" : request.Label.Trim();
        var asset = await media.SaveVideoAsync(new ComposedVideo(video.Mp4, seconds * 1000, size.Item1, size.Item2),
            $"動画テスト_{video.Model.Provider}_{clock.GetUtcNow().ToOffset(TimeSpan.FromHours(9)):yyyyMMdd-HHmmss}.mp4", frame, "", ct,
            start is null ? DigitalSourceType.TrainedAlgorithmicMedia : DigitalSourceType.CompositeWithTrainedAlgorithmicMedia);
        asset.AltText = $"動画生成のテスト（{label}）";
        var cost = seconds * (provider?.PricePerSecond ?? 0);
        asset.Provenance = JsonSerializer.Serialize(new
        {
            kind = Kind, label, provider = video.Model.Provider, model = video.Model.ModelId, prompt,
            negativePrompt = request.NegativePrompt?.Trim() ?? "", landscape = request.Landscape, seconds, highQuality = request.HighQuality,
            startImage = start is not null, elapsedMs = sw.ElapsedMilliseconds, costUsd = cost, rating = 0, note = "",
        });
        db.Record(tenant, "video-lab.run", nameof(MediaAsset), asset.Id, $"{video.Model.Provider} {video.Model.ModelId}");
        await db.SaveChangesAsync(ct);
        return Parse(asset)!;
    }

    /// <summary>これまでの結果（新しい順）。</summary>
    public async Task<IReadOnlyList<VideoLabResult>> ListAsync(CancellationToken ct)
    {
        var assets = await db.MediaAssets.AsNoTracking()
            .Where(m => m.Kind == MediaKind.Video && m.Provenance != null && m.Provenance.Contains("\"kind\":\"" + Kind + "\""))
            .OrderByDescending(m => m.CreatedAt)
            .Take(60)
            .ToListAsync(ct);
        return [.. assets.Select(Parse).OfType<VideoLabResult>()];
    }

    /// <summary>評価（1：いまいち・2：良い、0：未評価）とメモを残す。</summary>
    public async Task<VideoLabResult> RateAsync(Guid assetId, int rating, string? note, CancellationToken ct)
    {
        var asset = await db.MediaAssets.FirstOrDefaultAsync(m => m.Id == assetId, ct) ?? throw new NotFoundException("動画");
        var json = asset.Provenance is { } p ? JsonNode.Parse(p) as JsonObject : null;
        if (json?["kind"]?.GetValue<string>() != Kind) throw new NotFoundException("動画");
        json["rating"] = Math.Clamp(rating, 0, 2);
        json["note"] = PostText.Truncate((note ?? "").Trim(), 500);
        asset.Provenance = json.ToJsonString();
        await db.SaveChangesAsync(ct);
        return Parse(asset)!;
    }

    public Task DeleteAsync(Guid assetId, CancellationToken ct) => media.DeleteAsync(assetId, ct);

    private static VideoLabResult? Parse(MediaAsset asset)
    {
        try
        {
            if (asset.Provenance is null || JsonNode.Parse(asset.Provenance) is not JsonObject j || j["kind"]?.GetValue<string>() != Kind) return null;
            return new VideoLabResult(asset.Id, asset.CreatedAt,
                j["label"]?.GetValue<string>() ?? "", j["provider"]?.GetValue<string>() ?? "", j["model"]?.GetValue<string>() ?? "",
                j["prompt"]?.GetValue<string>() ?? "", j["negativePrompt"]?.GetValue<string>() ?? "",
                j["landscape"]?.GetValue<bool>() ?? false, j["seconds"]?.GetValue<int>() ?? 0, j["highQuality"]?.GetValue<bool>() ?? false,
                j["startImage"]?.GetValue<bool>() ?? false, j["elapsedMs"]?.GetValue<long>() ?? 0, j["costUsd"]?.GetValue<decimal>() ?? 0,
                j["rating"]?.GetValue<int>() ?? 0, j["note"]?.GetValue<string>() ?? "");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

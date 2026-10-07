using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;

namespace ReachForge.Infrastructure.Media;

/// <summary>
/// BGM（F-05 処理 4「ライセンス済み素材」）。
/// 組み込みの曲は FFmpeg の音声合成（aevalsrc）で作るため、著作権・利用料の心配がない。
/// 運用者は Video:BgmLibraryPath のフォルダに曲（mp3 / m4a / wav）と tracks.json（id・title・file・license）を置いて追加できる。
/// </summary>
public sealed class BgmLibrary(IOptions<VideoOptions> options, ILogger<BgmLibrary> log) : IBgmLibrary
{
    private sealed record BuiltIn(BgmTrack Track, string Expression);

    private sealed record FileTrack(string Id, string Title, string File, string License);

    private const string BuiltInLicense = "ReachForge 内で合成した音源（著作権の制約なし）";

    // 和音（C – Am – F – G）を4秒ごとに切り替える、やわらかいパッド
    private static readonly BuiltIn Calm = new(new BgmTrack("calm", "おだやか（パッド）", BuiltInLicense, true),
        "0.06*(sin(2*PI*if(lt(mod(t,16),4),261.63,if(lt(mod(t,16),8),220.00,if(lt(mod(t,16),12),174.61,196.00)))*t)" +
        "+sin(2*PI*if(lt(mod(t,16),4),329.63,if(lt(mod(t,16),8),261.63,if(lt(mod(t,16),12),220.00,246.94)))*t)" +
        "+sin(2*PI*if(lt(mod(t,16),4),392.00,if(lt(mod(t,16),8),329.63,if(lt(mod(t,16),12),261.63,293.66)))*t))" +
        "*(0.7+0.3*sin(2*PI*0.125*t))");

    // 8分音符のアルペジオ（減衰する音色）
    private static readonly BuiltIn Bright = new(new BgmTrack("bright", "あかるい（アルペジオ）", BuiltInLicense, true),
        "0.10*sin(2*PI*if(eq(mod(floor(t*4),4),0),523.25,if(eq(mod(floor(t*4),4),1),659.25,if(eq(mod(floor(t*4),4),2),783.99,659.25)))*t)" +
        "*exp(-5*mod(t,0.25))+0.04*sin(2*PI*130.81*t)");

    private static readonly BuiltIn[] s_builtIns = [Calm, Bright];

    private readonly Lazy<IReadOnlyList<(BgmTrack Track, string Path)>> _files = new(() => LoadFiles(options.Value, log));

    public IReadOnlyList<BgmTrack> Tracks => [.. s_builtIns.Select(b => b.Track), .. _files.Value.Select(f => f.Track)];

    /// <summary>FFmpeg の入力：組み込みは lavfi の式、ファイルはパス。見つからなければ null。</summary>
    public (string? Expression, string? File)? Resolve(string id)
    {
        if (s_builtIns.FirstOrDefault(b => b.Track.Id == id) is { } builtIn) return (builtIn.Expression, null);
        return _files.Value.FirstOrDefault(f => f.Track.Id == id) is { Track: not null } file ? (null, file.Path) : null;
    }

    private static IReadOnlyList<(BgmTrack, string)> LoadFiles(VideoOptions o, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(o.BgmLibraryPath)) return [];
        var manifest = Path.Combine(o.BgmLibraryPath, "tracks.json");
        if (!File.Exists(manifest)) return [];
        try
        {
            var tracks = JsonSerializer.Deserialize<List<FileTrack>>(File.ReadAllText(manifest), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            return tracks
                .Where(t => !string.IsNullOrWhiteSpace(t.License) && !string.IsNullOrWhiteSpace(t.Id))
                .Select(t => (new BgmTrack(t.Id, t.Title, t.License, false), Path.GetFullPath(Path.Combine(o.BgmLibraryPath, t.File))))
                .Where(t => File.Exists(t.Item2) && !s_builtIns.Any(b => b.Track.Id == t.Item1.Id))
                .ToList();
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Ignoring invalid BGM manifest {Path}", manifest);
            return [];
        }
    }
}

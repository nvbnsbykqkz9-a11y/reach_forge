using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;

namespace ReachForge.Infrastructure.Media;

/// <summary>
/// AI 生成物の来歴（RF-DES-001 F-04 処理 4・11章 AI ラベル）。
/// 1. 画像（JPEG / PNG）に IPTC の DigitalSourceType を XMP で埋め込む（再エンコードしない）。Meta などはこれを見て「AI 情報」ラベルを付ける。
/// 2. C2PA マニフェスト（作成・編集の操作、生成 AI の種類、学習利用の可否）を作り、署名の設定があれば c2patool で署名して埋め込む。
/// </summary>
public sealed class C2paProvenanceStamper(IOptions<MediaOptions> options, ILogger<C2paProvenanceStamper> log) : IProvenanceStamper
{
    public const string ClaimGenerator = "ReachForge/1.0";
    public const string IptcVocabulary = "http://cv.iptc.org/newscodes/digitalsourcetype/";

    public async Task<StampedMedia> StampAsync(byte[] content, string mime, ProvenanceInfo info, CancellationToken ct)
    {
        var manifest = Manifest(info, mime);
        var stamped = mime switch
        {
            "image/jpeg" => Xmp.InsertIntoJpeg(content, Xmp.Packet(info)),
            "image/png" => Xmp.InsertIntoPng(content, Xmp.Packet(info)),
            _ => content,
        };

        var c2pa = options.Value.C2pa;
        if (!c2pa.IsConfigured) return new StampedMedia(stamped, manifest.ToJsonString(), false);
        try
        {
            return new StampedMedia(await SignAsync(stamped, mime, manifest, c2pa, ct), manifest.ToJsonString(), true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 署名に失敗しても公開は止めない（XMP の情報と DB のマニフェストは残る）
            log.LogWarning(ex, "C2PA signing failed; stored the manifest without embedding a signature");
            return new StampedMedia(stamped, manifest.ToJsonString(), false);
        }
    }

    public static string SourceTypeUri(DigitalSourceType type) => IptcVocabulary + type switch
    {
        DigitalSourceType.CompositeWithTrainedAlgorithmicMedia => "compositeWithTrainedAlgorithmicMedia",
        _ => "trainedAlgorithmicMedia",
    };

    /// <summary>c2patool のマニフェスト定義（https://opensource.contentauthenticity.org/docs/manifest/）。</summary>
    public static JsonObject Manifest(ProvenanceInfo info, string mime)
    {
        var agent = info.Model is null ? ClaimGenerator : $"{ClaimGenerator} ({info.Provider}/{info.Model})";
        return new JsonObject
        {
            ["claim_generator"] = ClaimGenerator,
            ["title"] = info.Title,
            ["format"] = mime,
            ["assertions"] = new JsonArray
            {
                new JsonObject
                {
                    ["label"] = "c2pa.actions",
                    ["data"] = new JsonObject
                    {
                        ["actions"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["action"] = info.Action,
                                ["digitalSourceType"] = SourceTypeUri(info.SourceType),
                                ["softwareAgent"] = agent,
                                ["when"] = info.When.ToString("O"),
                            },
                        },
                    },
                },
                new JsonObject
                {
                    // 生成物を AI の学習に使わせない（利用者の制作物を守る）
                    ["label"] = "c2pa.training-mining",
                    ["data"] = new JsonObject
                    {
                        ["entries"] = new JsonObject
                        {
                            ["c2pa.ai_generative_training"] = new JsonObject { ["use"] = "notAllowed" },
                            ["c2pa.ai_training"] = new JsonObject { ["use"] = "notAllowed" },
                            ["c2pa.data_mining"] = new JsonObject { ["use"] = "notAllowed" },
                        },
                    },
                },
            },
        };
    }

    private static async Task<byte[]> SignAsync(byte[] content, string mime, JsonObject manifest, C2paOptions c2pa, CancellationToken ct)
    {
        var ext = mime switch { "image/png" => "png", "video/mp4" => "mp4", _ => "jpg" };
        var dir = Directory.CreateTempSubdirectory("rf-c2pa-");
        try
        {
            var input = Path.Combine(dir.FullName, $"in.{ext}");
            var output = Path.Combine(dir.FullName, $"out.{ext}");
            var manifestPath = Path.Combine(dir.FullName, "manifest.json");
            var signing = (JsonObject)manifest.DeepClone();
            signing["alg"] = c2pa.Algorithm;
            signing["sign_cert"] = Path.GetFullPath(c2pa.SignCertPath!);
            signing["private_key"] = Path.GetFullPath(c2pa.PrivateKeyPath!);
            if (!string.IsNullOrWhiteSpace(c2pa.TimestampUrl)) signing["ta_url"] = c2pa.TimestampUrl;
            await File.WriteAllBytesAsync(input, content, ct);
            await File.WriteAllTextAsync(manifestPath, signing.ToJsonString(), ct);

            var psi = new ProcessStartInfo(c2pa.ToolPath!)
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
                ArgumentList = { input, "--manifest", manifestPath, "--output", output, "--force" },
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("c2patool could not be started");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(c2pa.TimeoutSeconds));
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                throw new InvalidOperationException($"c2patool failed ({process.ExitCode}): {await stderr}");
            }
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

/// <summary>XMP（IPTC Photo Metadata の DigitalSourceType）を、画素を再エンコードせずにファイルへ入れる。</summary>
public static class Xmp
{
    private const string JpegNamespace = "http://ns.adobe.com/xap/1.0/\0";

    public static string Packet(ProvenanceInfo info) => $"""
        <?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:Iptc4xmpExt="http://iptc.org/std/Iptc4xmpExt/2008-02-29/"
            xmlns:xmp="http://ns.adobe.com/xap/1.0/"
            xmlns:dc="http://purl.org/dc/elements/1.1/"
            Iptc4xmpExt:DigitalSourceType="{C2paProvenanceStamper.SourceTypeUri(info.SourceType)}"
            xmp:CreatorTool="{SecurityElement.Escape(C2paProvenanceStamper.ClaimGenerator)}"
            xmp:CreateDate="{info.When:yyyy-MM-ddTHH:mm:ssZ}">
           <dc:description><rdf:Alt><rdf:li xml:lang="x-default">{(info.SourceType == DigitalSourceType.TrainedAlgorithmicMedia ? "AI generated" : "Edited with AI")}</rdf:li></rdf:Alt></dc:description>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;

    /// <summary>SOI（と JFIF の APP0）の直後に APP1（XMP）を入れる。</summary>
    public static byte[] InsertIntoJpeg(byte[] jpeg, string packet)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return jpeg;
        var payload = Encoding.UTF8.GetBytes(JpegNamespace + packet);
        if (payload.Length + 2 > ushort.MaxValue) return jpeg;
        var position = 2;
        if (jpeg[2] == 0xFF && jpeg[3] == 0xE0) position = 4 + ((jpeg[4] << 8) | jpeg[5]);
        using var ms = new MemoryStream(jpeg.Length + payload.Length + 4);
        ms.Write(jpeg, 0, position);
        var length = payload.Length + 2;
        ms.Write([0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF)]);
        ms.Write(payload);
        ms.Write(jpeg, position, jpeg.Length - position);
        return ms.ToArray();
    }

    /// <summary>IHDR の直後に iTXt（XML:com.adobe.xmp）チャンクを入れる。</summary>
    public static byte[] InsertIntoPng(byte[] png, string packet)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(signature)) return png;
        var ihdrLength = (png[8] << 24) | (png[9] << 16) | (png[10] << 8) | png[11];
        var position = 8 + 12 + ihdrLength;

        var data = new List<byte>();
        data.AddRange(Encoding.ASCII.GetBytes("XML:com.adobe.xmp"));
        data.AddRange([0, 0, 0, 0, 0]); // 区切り・非圧縮・圧縮方式・言語タグ・翻訳したキーワード
        data.AddRange(Encoding.UTF8.GetBytes(packet));
        var type = Encoding.ASCII.GetBytes("iTXt");

        using var ms = new MemoryStream(png.Length + data.Count + 12);
        ms.Write(png, 0, position);
        ms.Write([(byte)(data.Count >> 24), (byte)(data.Count >> 16), (byte)(data.Count >> 8), (byte)data.Count]);
        ms.Write(type);
        ms.Write(data.ToArray());
        var crc = Crc32([.. type, .. data]);
        ms.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        ms.Write(png, position, png.Length - position);
        return ms.ToArray();
    }

    private static readonly uint[] s_table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes) c = s_table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

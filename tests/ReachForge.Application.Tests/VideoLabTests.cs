using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Infrastructure.Media;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;

namespace ReachForge.Application.Tests;

/// <summary>動画生成のテスト画面：プロバイダを指定して生成し、設定とともに残し、評価を付ける。</summary>
public class VideoLabTests
{
    private static bool HasFfmpeg()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public async Task Runs_with_the_chosen_provider_keeps_the_settings_and_takes_a_rating()
    {
        if (!HasFfmpeg()) Assert.Skip("ffmpeg がない環境では実行しない");
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var lab = f.Get<VideoLabService>(scope);

        var provider = Assert.Single(lab.Providers(), p => p.Name == "local");
        Assert.True(provider.Configured);
        var result = await lab.RunAsync(new VideoLabRequest
        {
            Provider = "local", Prompt = VideoLabService.Presets[0].Prompt, Label = VideoLabService.Presets[0].Name, Landscape = true, Seconds = 5,
        }, CancellationToken.None);

        Assert.Equal(("local", true, 5), (result.Provider, result.Landscape, result.Seconds));
        Assert.Equal(VideoLabService.Presets[0].Name, result.Label);
        var listed = Assert.Single(await lab.ListAsync(CancellationToken.None));
        Assert.Equal(result.AssetId, listed.AssetId);

        var rated = await lab.RateAsync(result.AssetId, 2, "文字が出ない・自然", CancellationToken.None);
        Assert.Equal((2, "文字が出ない・自然"), (rated.Rating, rated.Note));
        Assert.Equal(2, (await lab.ListAsync(CancellationToken.None))[0].Rating);
    }

    [Fact]
    public async Task An_unavailable_provider_is_reported_by_name()
    {
        await using var f = await AppFixture.CreateAsync();
        await using var scope = f.Scope();
        var lab = f.Get<VideoLabService>(scope);
        var ex = await Assert.ThrowsAnyAsync<DomainException>(() => lab.RunAsync(new VideoLabRequest { Provider = "kling", Prompt = "x" }, CancellationToken.None));
        Assert.Contains("kling", ex.Message);
        await Assert.ThrowsAnyAsync<DomainException>(() => lab.RunAsync(new VideoLabRequest { Provider = "local", Prompt = " " }, CancellationToken.None));
    }

    private static readonly Uri Lp = new("https://example.com/lp/secureview");

    private sealed class Fetcher : IWebPageFetcher
    {
        public Task<WebPage> FetchAsync(string url, CancellationToken ct) => Task.FromResult(new WebPage(Lp, "SecureView | 検知から初動まで1画面で",
            "アラートが多すぎる。", "AI が検知・分析し、対応が必要なものだけを知らせます。", ["#1F4FD8"],
            [new WebImage(new Uri("https://example.com/img/dashboard.png"), "ダッシュボードの画面")]));

        public async Task<FetchedImage> FetchImageAsync(Uri url, CancellationToken ct)
        {
            var image = await new SkiaImageProcessor().RenderPlaceholderAsync(1600, 1000, 3, ["#F4F7FC", "#1F4FD8"], ct);
            return new FetchedImage(image.Bytes, image.Mime);
        }
    }

    [Fact]
    public async Task Prompts_are_written_from_the_lp_with_a_literal_version_to_compare()
    {
        if (!HasFfmpeg()) Assert.Skip("ffmpeg がない環境では実行しない");
        await using var f = await AppFixture.CreateAsync(configure: s => s.AddSingleton<IWebPageFetcher>(new Fetcher()));
        await using var scope = f.Scope();
        var lab = f.Get<VideoLabService>(scope);

        var plan = await lab.FromLpAsync(Lp.ToString(), CancellationToken.None);
        Assert.Equal("SecureView | 検知から初動まで1画面で", plan.Title);
        Assert.Equal(["LP：冒頭", "LP：解決", "LP：画面を重ねる背景", "LP：画像から動画", "LP：文言そのまま（比較用）"], plan.Presets.Select(p => p.Name));
        Assert.True(plan.Presets.Single(p => p.Name == "LP：画像から動画").NeedsStartImage);
        var literal = plan.Presets[^1];
        Assert.StartsWith("SecureView | 検知から初動まで1画面で。アラートが多すぎる。", literal.Prompt);
        Assert.Equal(literal.Prompt, literal.SourceText);
        var image = Assert.Single(plan.Images);
        Assert.NotEmpty(await lab.FetchImageAsync(image.Url, CancellationToken.None));

        // 元にした LP の文言を、結果とともに残す
        var hook = plan.Presets[0];
        var result = await lab.RunAsync(new VideoLabRequest
        {
            Provider = "local", Prompt = hook.Prompt, Label = hook.Name, SourceText = hook.SourceText, Seconds = 5,
            StartImage = await lab.FetchImageAsync(image.Url, CancellationToken.None),
        }, CancellationToken.None);
        Assert.Equal(hook.SourceText ?? "", result.SourceText);
        Assert.True(result.StartImage);
        Assert.Equal(hook.SourceText ?? "", (await lab.ListAsync(CancellationToken.None))[0].SourceText);
    }
}

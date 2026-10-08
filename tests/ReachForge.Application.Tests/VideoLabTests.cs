using System.Diagnostics;
using ReachForge.Application.Ai;
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
}

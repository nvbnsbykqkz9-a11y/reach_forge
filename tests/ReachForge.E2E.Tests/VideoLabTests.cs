using Microsoft.Playwright;

namespace ReachForge.E2E.Tests;

/// <summary>動画生成のテスト画面：ひな形を選んで動画をつくり、結果に評価を付ける。</summary>
[Collection(E2ECollection.Name)]
public class VideoLabTests(E2EFixture app)
{
    [Fact]
    public async Task Owner_tries_a_prompt_and_rates_the_video()
    {
        var errors = new List<string>();
        var page = await app.LoginAsync();
        page.PageError += (_, e) => errors.Add(e);
        await page.GotoAsync(app.BaseUrl + "/settings/video-lab");
        await E2EFixture.WaitForInteractiveAsync(page);
        await page.GetByText("動画生成のテスト").First.WaitForAsync();

        await page.GetByRole(AriaRole.Group, new() { Name = "プロンプトのひな形" }).GetByText("2-A 解決（抽象的）").ClickAsync();
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } d0)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(d0, "video-lab-form.png"), FullPage = true });
        }
        await Assertions.Expect(page.GetByLabel("プロンプト", new() { Exact = true })).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("glowing blue AI core"));
        await page.GetByRole(AriaRole.Button, new() { Name = "動画をつくる" }).ClickAsync();

        var result = page.GetByTestId("lab-result").First;
        await result.WaitForAsync(new() { Timeout = 120_000 });
        await Assertions.Expect(result.GetByText("2-A 解決（抽象的）")).ToBeVisibleAsync();
        await result.GetByRole(AriaRole.Button, new() { Name = "良い" }).ClickAsync();
        await Assertions.Expect(result.GetByRole(AriaRole.Button, new() { Name = "良い" })).ToHaveAttributeAsync("aria-pressed", "true");
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } dir)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "video-lab.png"), FullPage = true });
        }
        Assert.Empty(errors);
    }
}

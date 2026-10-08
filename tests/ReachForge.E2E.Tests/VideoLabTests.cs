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

        // 同じ DB をほかのテストと使うため、この結果を見出しで探す
        var result = page.GetByTestId("lab-result").Filter(new() { HasText = "2-A 解決（抽象的）" }).First;
        await result.WaitForAsync(new() { Timeout = 120_000 });
        await result.GetByRole(AriaRole.Button, new() { Name = "良い" }).ClickAsync();
        await Assertions.Expect(result.GetByRole(AriaRole.Button, new() { Name = "良い" })).ToHaveAttributeAsync("aria-pressed", "true");
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } dir)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "video-lab.png"), FullPage = true });
        }
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Owner_writes_prompts_from_a_landing_page_and_keeps_the_source_wording()
    {
        var errors = new List<string>();
        var page = await app.LoginAsync();
        page.PageError += (_, e) => errors.Add(e);
        await page.GotoAsync(app.BaseUrl + "/settings/video-lab");
        await E2EFixture.WaitForInteractiveAsync(page);

        await page.GetByLabel("LP の URL").FillAsync(FakeLandingPageFetcher.Url);
        await page.GetByRole(AriaRole.Button, new() { Name = "読み込む" }).ClickAsync();
        var presets = page.GetByTestId("lp-presets");
        try
        {
            await presets.WaitForAsync(new() { Timeout = 30_000 });
        }
        catch (TimeoutException)
        {
            if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } d1)
            {
                await page.ScreenshotAsync(new() { Path = Path.Combine(d1, "video-lab-lp-timeout.png"), FullPage = true });
            }
            throw;
        }
        await Assertions.Expect(presets.GetByText("LP：文言そのまま（比較用）")).ToBeVisibleAsync();
        await presets.GetByText("LP：冒頭").ClickAsync();
        await Assertions.Expect(page.GetByTestId("source-text")).ToContainTextAsync("元にした LP の文言");
        // LP の画像を起点の画像にする
        await page.GetByRole(AriaRole.Group, new() { Name = "起点にする LP の画像" }).GetByRole(AriaRole.Button).First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Img, new() { Name = "起点の画像" })).ToBeVisibleAsync();
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } d0)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(d0, "video-lab-lp.png"), FullPage = true });
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "動画をつくる" }).ClickAsync();
        var result = page.GetByTestId("lab-result").Filter(new() { HasText = "LP：冒頭" }).First;
        await result.WaitForAsync(new() { Timeout = 120_000 });
        await Assertions.Expect(result.GetByText("元の文言：", new() { Exact = false })).ToBeVisibleAsync();
        Assert.Empty(errors);
    }
}

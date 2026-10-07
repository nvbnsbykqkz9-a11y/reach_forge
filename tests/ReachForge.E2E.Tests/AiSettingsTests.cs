using Microsoft.Playwright;

namespace ReachForge.E2E.Tests;

/// <summary>生成 AI の API キーを画面で入れて保存し、確認の結果を表示する（キーは画面に戻さない）。</summary>
[Collection(E2ECollection.Name)]
public class AiSettingsTests(E2EFixture app)
{
    [Fact]
    public async Task Owner_saves_an_api_key_and_sees_the_check_result()
    {
        var errors = new List<string>();
        var page = await app.LoginAsync();
        page.PageError += (_, e) => errors.Add(e);
        await page.GotoAsync("/settings/ai");
        await E2EFixture.WaitForInteractiveAsync(page);

        var card = page.Locator("#openai");
        await card.GetByLabel("API キー").FillAsync("sk-e2e-test");
        await card.GetByRole(AriaRole.Button, new() { Name = "保存する" }).ClickAsync();
        await page.GetByText("OpenAI の設定を保存しました", new() { Exact = false }).WaitForAsync(new() { Timeout = 15000 });
        await Assertions.Expect(card.GetByText("設定済み", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(card.GetByLabel("API キー")).ToHaveValueAsync("");
        // 保存のあとに接続を確認する（テストの環境では外部に出られないので、結果の表示だけ確かめる）
        await Assertions.Expect(card.Locator(".mud-alert")).ToBeVisibleAsync(new() { Timeout = 30000 });
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } dir)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "ai-settings.png"), FullPage = true });
        }
        Assert.Empty(errors);
    }
}

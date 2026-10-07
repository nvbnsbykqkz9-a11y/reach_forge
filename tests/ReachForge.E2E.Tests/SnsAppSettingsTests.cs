using Microsoft.Playwright;

namespace ReachForge.E2E.Tests;

/// <summary>SNS アプリの ID・シークレットを画面で入れると、再起動せずにその SNS の認可画面へ進める。</summary>
[Collection(E2ECollection.Name)]
public class SnsAppSettingsTests(E2EFixture app)
{
    [Fact]
    public async Task Owner_sets_x_app_credentials_and_connects_without_restart()
    {
        var errors = new List<string>();
        var page = await app.LoginAsync();
        page.PageError += (_, e) => errors.Add(e);
        await page.GotoAsync("/settings/sns-apps");
        await E2EFixture.WaitForInteractiveAsync(page);

        var card = page.Locator("#x");
        await card.GetByLabel("Client ID").FillAsync("e2e-client");
        await card.GetByLabel("Client Secret").FillAsync("e2e-secret");
        await card.GetByRole(AriaRole.Button, new() { Name = "保存する" }).ClickAsync();
        await page.GetByText("X の設定を保存しました", new() { Exact = false }).WaitForAsync(new() { Timeout = 15000 });
        await Assertions.Expect(card.GetByText("設定済み", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(card.GetByLabel("Client Secret")).ToHaveValueAsync(""); // シークレットは画面に戻さない
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } dir)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "sns-apps.png"), FullPage = true });
        }

        // SNS 連携から X の認可画面へ（外部へは出ないので、要求の URL だけ確かめる）
        await page.GotoAsync("/settings/channels");
        await E2EFixture.WaitForInteractiveAsync(page);
        var authorize = page.WaitForRequestAsync(r => r.Url.StartsWith("https://x.com/i/oauth2/authorize", StringComparison.Ordinal),
            new() { Timeout = 15000 });
        // デモデータでは X はデモ接続済みなので「再接続」（アプリの設定を入れたので本物の X へ進む）
        await page.Locator(".mud-card").Filter(new() { Has = page.Locator("h6", new() { HasTextRegex = new("^X$") }) })
            .GetByRole(AriaRole.Button, new() { NameRegex = new("^(連携する|再接続)$") }).ClickAsync();
        var request = await authorize;
        Assert.Contains("client_id=e2e-client", request.Url);
        Assert.Empty(errors);
    }
}

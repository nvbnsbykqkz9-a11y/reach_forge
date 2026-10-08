using Microsoft.Playwright;

namespace ReachForge.E2E.Tests;

/// <summary>右上の氏名のメニュー：操作説明書（PDF版）と About（バージョン・開発元・使用許諾契約・第三者ソフトウェアのライセンス）。</summary>
[Collection(E2ECollection.Name)]
public class HelpMenuTests(E2EFixture app)
{
    [Fact]
    public async Task User_menu_shows_the_manual_and_about()
    {
        var errors = new List<string>();
        var page = await app.LoginAsync();
        page.PageError += (_, e) => errors.Add(e);
        await E2EFixture.WaitForInteractiveAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "アカウント" }).ClickAsync();
        var manual = page.GetByRole(AriaRole.Menuitem, new() { Name = "操作説明書（PDF版）" });
        await Assertions.Expect(manual).ToBeVisibleAsync();
        var pdf = await page.APIRequest.GetAsync("/help/manual.pdf");
        Assert.True(pdf.Ok, pdf.StatusText);
        Assert.Equal("application/pdf", pdf.Headers["content-type"]);

        await page.GetByRole(AriaRole.Menuitem, new() { Name = "About" }).ClickAsync();
        var about = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(about.GetByText("ver 1.00.00")).ToBeVisibleAsync();
        await Assertions.Expect(about.GetByRole(AriaRole.Link, new() { Name = "株式会社TechnologyFrontier" }))
            .ToHaveAttributeAsync("href", "https://www.technologyfrontier.co.jp");
        await Assertions.Expect(about.GetByRole(AriaRole.Link, new() { Name = "info@technologyfrontier.co.jp" }))
            .ToHaveAttributeAsync("href", "mailto:info@technologyfrontier.co.jp");
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } dir)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, "about.png") });
        }

        await about.GetByRole(AriaRole.Button, new() { Name = "使用許諾契約" }).ClickAsync();
        await Assertions.Expect(page.GetByText("第1条（使用許諾）")).ToBeVisibleAsync();
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } dir2)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir2, "eula.png") });
        }
        // 契約のダイアログを閉じると About に戻る（Esc はフォーカスの位置で効かないことがあるため「閉じる」を押す）
        await page.GetByRole(AriaRole.Dialog, new() { Name = "使用許諾契約" }).GetByRole(AriaRole.Button, new() { Name = "閉じる" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "第三者ソフトウェアのライセンス" }).ClickAsync();
        await Assertions.Expect(page.GetByText("MudBlazor", new() { Exact = true })).ToBeVisibleAsync();
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } dir3)
        {
            await page.WaitForTimeoutAsync(500); // 開くときのアニメーションを待つ
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir3, "third-party.png") });
        }
        Assert.Empty(errors);
    }
}

using Microsoft.Playwright;
using ReachForge.Infrastructure.Media;

namespace ReachForge.E2E.Tests;

/// <summary>広告を出す（お試しの広告アカウント）：連携 → 目的 → 画像 → AI の広告文 → だれに・いくら → 確認して出稿。</summary>
[Collection(E2ECollection.Name)]
public class CreateAdTests(E2EFixture app)
{
    private static async Task ShotAsync(IPage page, string name)
    {
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } dir)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, name + ".png"), FullPage = true });
        }
    }

    [Fact]
    public async Task Owner_runs_a_demo_instagram_ad()
    {
        var errors = new List<string>();
        var page = await app.LoginAsync();
        page.PageError += (_, e) => errors.Add(e);
        await page.GotoAsync("/create/instagram?tab=ad");
        await E2EFixture.WaitForInteractiveAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "お試しの広告アカウントをつくる" }).ClickAsync();
        Assert.Equal(0, await page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "Error" }).CountAsync()); // エラーの知らせが出ていない
        await page.GetByText("広告で、どうなってほしいですか？").WaitForAsync();
        await ShotAsync(page, "ad-step1");
        await page.GetByLabel("リンク先（お店・商品のページ）").FillAsync("https://shop.example/latte");
        await page.GetByRole(AriaRole.Button, new() { Name = "次へ：画像・動画" }).ClickAsync();

        await page.GetByText("広告の画像・動画を選びましょう").WaitForAsync();
        var png = (await new ImageSharpProcessor().RenderPlaceholderAsync(1080, 1080, 3, ["#B45309", "#FDE68A"], CancellationToken.None)).Bytes;
        await page.Locator("input[type=file]").First.SetInputFilesAsync(new FilePayload { Name = "latte.png", MimeType = "image/png", Buffer = png });
        await page.GetByText("広告に使う画像").WaitForAsync(new() { Timeout = 30000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "次へ：広告文" }).ClickAsync();

        await page.GetByLabel("広告で伝えたいこと").FillAsync("秋限定のさつまいもラテ。テイクアウトもできます");
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("AI に広告文を書いてもらう") }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "これにする" }).First.WaitForAsync(new() { Timeout = 30000 });
        await ShotAsync(page, "ad-step3");
        await page.GetByRole(AriaRole.Button, new() { Name = "これにする" }).Nth(1).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "次へ：だれに・いくら" }).ClickAsync();

        await page.GetByText("だれに見せて、いくら使いますか？").WaitForAsync();
        await page.GetByRole(AriaRole.Radio, new() { Name = "女性" }).CheckAsync();
        await page.GetByText("3,000円", new() { Exact = true }).ClickAsync();
        await page.GetByText("最大 21,000円").WaitForAsync();
        await ShotAsync(page, "ad-step4");
        await page.GetByRole(AriaRole.Button, new() { Name = "次へ：確認" }).ClickAsync();

        await page.GetByText("確認して広告を出しましょう").WaitForAsync();
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "広告を出す", Exact = true });
        Assert.True(await submit.IsDisabledAsync()); // 請求の確認のチェックが必要
        await page.GetByLabel(new System.Text.RegularExpressions.Regex("実際には出稿・請求されないことを確認しました")).CheckAsync();
        await ShotAsync(page, "ad-step5");
        await submit.ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "広告を出す", Exact = true }).ClickAsync();
        await page.GetByText("広告を出しました。").WaitForAsync(new() { Timeout = 30000 });
        await page.GetByText("審査中").First.WaitForAsync();
        await ShotAsync(page, "ad-done");

        // ホームの広告に出る
        await page.GotoAsync("/");
        await page.GetByText("審査中").First.WaitForAsync();
        Assert.Empty(errors);
    }
}

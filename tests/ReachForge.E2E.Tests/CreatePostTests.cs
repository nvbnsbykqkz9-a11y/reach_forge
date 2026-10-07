using Microsoft.Playwright;

namespace ReachForge.E2E.Tests;

/// <summary>つくる（SNS ごと）：伝えたいこと → 画像・動画 → AI の文章 → 確認して今すぐ投稿。</summary>
[Collection(E2ECollection.Name)]
public class CreatePostTests(E2EFixture app)
{
    private static async Task ShotAsync(IPage page, string name)
    {
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } dir)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, name + ".png"), FullPage = true });
        }
    }

    [Fact]
    public async Task Owner_creates_an_x_post_with_ai_and_publishes_now()
    {
        var errors = new List<string>();
        var page = await app.LoginAsync();
        page.PageError += (_, e) => errors.Add(e);
        await E2EFixture.WaitForInteractiveAsync(page);
        await page.GetByText("どの SNS でつくりますか？").WaitForAsync();
        await ShotAsync(page, "home");

        await page.GetByRole(AriaRole.Link, new() { Name = "X で投稿・広告をつくる" }).ClickAsync();
        await E2EFixture.WaitForInteractiveAsync(page);
        await page.GetByLabel("伝えたいこと").FillAsync("秋限定のさつまいもラテを10月1日から販売します");
        await ShotAsync(page, "create-step1");
        await page.GetByRole(AriaRole.Button, new() { Name = "次へ：画像・動画" }).ClickAsync();

        await page.GetByText("画像・動画を用意しましょう").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "次へ：文章" }).ClickAsync(); // X は画像なしでも投稿できる

        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("AI に文章を書いてもらう") }).ClickAsync();
        await page.GetByText("気に入った案を選んでください").WaitForAsync(new() { Timeout = 30000 });
        await ShotAsync(page, "create-step3-candidates");
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("この案を使う|採用") }).First.ClickAsync();
        await page.GetByLabel("本文").WaitForAsync(new() { Timeout = 30000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "次へ：確認して投稿" }).ClickAsync();

        await page.GetByText("確認して投稿しましょう").WaitForAsync();
        await ShotAsync(page, "create-step4");
        await page.GetByRole(AriaRole.Button, new() { Name = "今すぐ X に投稿する" }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "投稿する", Exact = true }).ClickAsync();
        await page.GetByText("X に投稿しました。").WaitForAsync(new() { Timeout = 30000 });
        await ShotAsync(page, "create-done");

        // ホームの最近の投稿に出る
        await page.GotoAsync("/");
        await page.GetByText("投稿済み").First.WaitForAsync();
        Assert.Empty(errors);
    }
}

using Microsoft.Playwright;

namespace ReachForge.E2E.Tests;

/// <summary>LP から集客動画をつくる流れ（LP の読み込み → 画像の選択と権利の確認 → 作成 → 訴求と投稿文の案）。</summary>
[Collection(E2ECollection.Name)]
public class LandingPageVideoTests(E2EFixture app)
{
    [Fact]
    public async Task Owner_makes_a_video_from_a_landing_page()
    {
        var errors = new List<string>();
        var page = await app.LoginAsync();
        page.PageError += (_, e) => errors.Add(e);
        // つくる（Instagram）：伝えたいことと LP の URL → 画像・動画 →「AI で動画をつくる」は LP から作る形で開く
        await page.GotoAsync("/create/instagram");
        await E2EFixture.WaitForInteractiveAsync(page);
        await page.GetByLabel("伝えたいこと").FillAsync("秋限定のさつまいもラテ");
        await page.GetByLabel("お店・商品のページの URL（任意）").FillAsync(FakeLandingPageFetcher.Url);
        await page.GetByRole(AriaRole.Button, new() { Name = "次へ：画像・動画" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("AI で動画をつくる") }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("LP の URL（必須）")).ToHaveValueAsync(FakeLandingPageFetcher.Url);
        await page.GetByRole(AriaRole.Button, new() { Name = "LP を読み込む" }).ClickAsync();
        await page.GetByText("秋限定さつまいもラテ | ほっこりカフェ").WaitForAsync(new() { Timeout = 15000 });

        // 画像を使うには権利の確認が必要
        var create = page.GetByRole(AriaRole.Button, new() { NameRegex = new("^動画をつくる（") });
        await Assertions.Expect(page.Locator("[aria-label=\"動画に使う LP の画像を選ぶ\"] button[aria-pressed=\"true\"]")).ToHaveCountAsync(2);
        await Assertions.Expect(create).ToBeDisabledAsync();
        await page.GetByText("SNS の動画に使う権利があります", new() { Exact = false }).ClickAsync();
        await Assertions.Expect(create).ToBeEnabledAsync();
        await ScreenshotAsync(page, "lp-video-ready");

        await create.ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "つくる", Exact = true }).ClickAsync();
        await page.GetByText("AI が整理した訴求").WaitForAsync(new() { Timeout = 120_000 });
        var post = page.GetByLabel("投稿文の案（コピーして投稿に使えます）");
        await Assertions.Expect(post).Not.ToBeEmptyAsync();
        await ScreenshotAsync(page, "lp-video-done");

        Assert.Empty(errors);
    }

    /// <summary>RF_E2E_SCREENSHOTS にフォルダーを指定したときだけ画面を保存する（見た目の確認用）。</summary>
    private static async Task ScreenshotAsync(IPage page, string name)
    {
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } dir)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, name + ".png"), FullPage = true });
        }
    }
}

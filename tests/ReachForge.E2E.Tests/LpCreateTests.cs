using Microsoft.Playwright;

namespace ReachForge.E2E.Tests;

/// <summary>主な流れ：LP を読み込む → SNS を選ぶ → つくる → SNS ごとの文章・画像と縦型の動画ができ、ダウンロードできる。</summary>
[Collection(E2ECollection.Name)]
public class LpCreateTests(E2EFixture app)
{
    private static async Task ShotAsync(IPage page, string name)
    {
        if (Environment.GetEnvironmentVariable("RF_E2E_SCREENSHOTS") is { Length: > 0 } dir)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, name + ".png"), FullPage = true });
        }
    }

    [Fact]
    public async Task Owner_creates_ads_and_a_video_from_a_landing_page()
    {
        var errors = new List<string>();
        var page = await app.LoginAsync();
        page.PageError += (_, e) => errors.Add(e);
        await E2EFixture.WaitForInteractiveAsync(page);
        await page.GetByText("LP から広告・動画をつくる").WaitForAsync();

        await page.GetByLabel("LP の URL").FillAsync(FakeLandingPageFetcher.Url);
        await page.GetByRole(AriaRole.Button, new() { Name = "読み込む" }).ClickAsync();
        await page.GetByText("秋限定さつまいもラテ | ほっこりカフェ").WaitForAsync(new() { Timeout = 15000 });
        var next = page.GetByRole(AriaRole.Button, new() { Name = "次へ：SNS を選ぶ" });
        await Assertions.Expect(next).ToBeDisabledAsync(); // 画像を使う権利の確認が必要
        await page.GetByText("選んだ画像を広告・動画に使う権利があります", new() { Exact = false }).ClickAsync();
        await ShotAsync(page, "lp-step1");
        await next.ClickAsync();

        await page.GetByText("どの SNS 向けにつくりますか？").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "TikTok" }).ClickAsync(); // 既定の3つに追加
        await ShotAsync(page, "lp-step2");
        await page.GetByRole(AriaRole.Button, new() { Name = "次へ：確認" }).ClickAsync();

        await page.GetByText("この内容でつくります").WaitForAsync();
        await ShotAsync(page, "lp-step3");
        await page.GetByRole(AriaRole.Button, new() { Name = "つくる", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "つくる", Exact = true }).ClickAsync();

        // つくったものの画面：SNS ごとのタブ（投稿文・画像・広告文）と動画
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/lp/[0-9a-f-]{36}$"), new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "投稿文をコピー" }).First.WaitForAsync(new() { Timeout = 30000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Tab)).ToHaveCountAsync(4);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { NameRegex = new("^案 1 をコピー") }).First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { NameRegex = new("画像 1 をダウンロード") }).First).ToBeVisibleAsync();
        try
        {
            await page.GetByRole(AriaRole.Link, new() { NameRegex = new("動画をダウンロード") }).WaitForAsync(new() { Timeout = 120_000 });
        }
        catch (TimeoutException)
        {
            await ShotAsync(page, "lp-video-timeout");
            throw;
        }
        await ShotAsync(page, "lp-result");

        // まとめてダウンロード（ZIP）
        // ファイル名はサーバーが付ける（LP のタイトル.zip）。ヘッドレスのブラウザは日本語の名前を使わないため、ヘッダーで確かめる
        var probe = await page.APIRequest.GetAsync(page.Url.Replace("/lp/", "/api/v1/lp/") + "/download");
        Assert.True(probe.Ok, probe.StatusText);
        Assert.Contains("filename*=UTF-8''%E7%A7%8B", probe.Headers.GetValueOrDefault("content-disposition") ?? ""); // 「秋…」
        var download = await page.RunAndWaitForDownloadAsync(() =>
            page.GetByRole(AriaRole.Link, new() { NameRegex = new("まとめてダウンロード") }).ClickAsync());
        Assert.Null(await download.FailureAsync());

        // ホームの「最近つくったもの」に出る
        await page.GotoAsync("/");
        await page.GetByText("最近つくったもの").WaitForAsync();
        Assert.Empty(errors);
    }
}

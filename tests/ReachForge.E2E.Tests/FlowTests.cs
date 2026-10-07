using Microsoft.Playwright;

namespace ReachForge.E2E.Tests;

/// <summary>主要な流れをブラウザで確認する（RF-DES-001 15章 E2E テスト：生成 → 変換 → 承認依頼 → 承認）。</summary>
[Collection(E2ECollection.Name)]
public class FlowTests(E2EFixture app)
{
    [Fact]
    public async Task Owner_creates_a_post_and_requests_approval_then_approver_approves()
    {
        var errors = new List<string>();
        var page = await app.LoginAsync();
        page.PageError += (_, e) => errors.Add(e);
        await page.GetByText("直近7日間のまとめ").WaitForAsync();

        await page.GotoAsync("/studio");
        await E2EFixture.WaitForInteractiveAsync(page);
        await page.GetByLabel("テーマ（必須）").FillAsync("秋限定さつまいもラテの発売告知");
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("AIで3案つくる") }).ClickAsync();
        await page.GetByText("AI 案A").First.WaitForAsync(new() { Timeout = 30000 });

        await page.GetByRole(AriaRole.Button, new() { Name = "次へ：SNSごとに整える" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("SNS別に変換する") }).ClickAsync();
        await page.GetByRole(AriaRole.Tab, new() { NameRegex = new("Instagram") }).WaitForAsync(new() { Timeout = 30000 });

        await page.GetByRole(AriaRole.Button, new() { Name = "次へ：確認して予約" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "公開日時" }).WaitForAsync();
        await page.Locator("[aria-label=\"おすすめの投稿時刻\"] button").First.ClickAsync();
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "承認を依頼する" });
        await Assertions.Expect(submit).ToBeEnabledAsync();
        await submit.ClickAsync();
        await page.GetByText(new System.Text.RegularExpressions.Regex("承認を依頼しました")).WaitForAsync(new() { Timeout = 15000 });

        var approver = await app.LoginAsync("approver@example.com");
        await approver.GotoAsync("/approvals");
        await E2EFixture.WaitForInteractiveAsync(approver);
        await approver.GetByText("Threads ／").First.ClickAsync();
        await approver.GetByRole(AriaRole.Button, new() { Name = "承認する（A）" }).ClickAsync();
        await approver.GetByText("承認しました").WaitForAsync(new() { Timeout = 15000 });

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Viewers_cannot_open_operator_screens()
    {
        var page = await app.LoginAsync("viewer@example.com");
        await page.GotoAsync("/ops");
        await page.GetByText(new System.Text.RegularExpressions.Regex("権限がありません")).First.WaitForAsync();
        Assert.Equal(0, await page.Locator("nav a[href=\"/ops\"]").CountAsync());
    }
}

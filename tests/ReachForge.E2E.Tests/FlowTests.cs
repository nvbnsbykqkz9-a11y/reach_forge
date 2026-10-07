using Microsoft.Playwright;

namespace ReachForge.E2E.Tests;

/// <summary>権限による画面の出し分けをブラウザで確認する（投稿をつくる流れは CreatePostTests）。</summary>
[Collection(E2ECollection.Name)]
public class FlowTests(E2EFixture app)
{
    [Fact]
    public async Task Viewers_cannot_open_operator_screens()
    {
        var page = await app.LoginAsync("viewer@example.com");
        await page.GotoAsync("/ops");
        await page.GetByText(new System.Text.RegularExpressions.Regex("権限がありません")).First.WaitForAsync();
        Assert.Equal(0, await page.Locator("nav a[href=\"/ops\"]").CountAsync());
    }
}

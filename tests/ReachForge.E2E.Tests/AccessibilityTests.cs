using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace ReachForge.E2E.Tests;

/// <summary>
/// アクセシビリティの自動検査（RF-UX-001 9章：WCAG 2.1 AA）。axe-core で主要画面を検査し、影響が「重大」「深刻」の違反を 0 件にする。
/// </summary>
[Collection(E2ECollection.Name)]
public class AccessibilityTests(E2EFixture app)
{
    public static readonly string[] Pages =
    [
        "/", "/studio", "/media", "/ideas", "/campaigns", "/calendar", "/approvals", "/inbox", "/analytics",
        "/settings", "/settings/brand", "/settings/channels", "/settings/members", "/settings/usage", "/settings/api-keys", "/settings/sns-apps", "/settings/ai", "/ops", "/ops/prompts",
    ];

    private static readonly AxeRunOptions s_wcag = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] },
    };

    private static string Describe(string url, IEnumerable<AxeResultItem> violations) => string.Join("\n", violations.Select(v =>
        $"{url} [{v.Impact}] {v.Id}: {v.Help} → {string.Join(" | ", v.Nodes.Take(3).Select(n =>
            $"{(n.Html.Length > 160 ? n.Html[..160] : n.Html)} {n.Any.FirstOrDefault()?.Message}"))}"));

    private static List<AxeResultItem> Serious(AxeResult result) =>
        result.Violations.Where(v => v.Impact is "serious" or "critical").ToList();

    [Fact]
    public async Task Login_page_has_no_serious_violations()
    {
        var page = await app.NewPageAsync();
        await page.GotoAsync("/account/login");
        var serious = Serious(await page.RunAxe(s_wcag));
        Assert.True(serious.Count == 0, Describe("/account/login", serious));
    }

    [Fact]
    public async Task Main_pages_have_no_serious_violations()
    {
        var page = await app.LoginAsync();
        var problems = new List<string>();
        foreach (var url in Pages)
        {
            await page.GotoAsync(url);
            await E2EFixture.WaitForInteractiveAsync(page);
            var serious = Serious(await page.RunAxe(s_wcag));
            if (serious.Count > 0) problems.Add(Describe(url, serious));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task Pages_fit_a_phone_screen_without_horizontal_scrolling()
    {
        var page = await app.LoginAsync(width: 390, height: 844);
        var overflowing = new List<string>();
        foreach (var url in Pages)
        {
            await page.GotoAsync(url);
            await E2EFixture.WaitForInteractiveAsync(page);
            var width = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
            if (width > 392) overflowing.Add($"{url}: {width}px");
        }
        Assert.True(overflowing.Count == 0, string.Join(", ", overflowing));
    }
}

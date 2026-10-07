using System.Net;

namespace ReachForge.Web.Tests;

/// <summary>運用管理（SCR-16）：ジョブ監視（Hangfire ダッシュボード）とデッドレターは運用者だけが見られる。</summary>
public class OpsTests
{
    private static WebFixture Create() => new(new Dictionary<string, string?>
    {
        ["Jobs:Engine"] = "Hangfire",
        ["Ops:Operators:0"] = "approver@example.com",
    });

    [Fact]
    public async Task Job_dashboard_and_ops_page_are_for_operators_only()
    {
        await using var app = Create();

        // 未ログイン：ログインへ
        var anonymous = app.Browser();
        var r = await anonymous.GetAsync("/ops/jobs");
        Assert.True(HttpAssert.IsRedirect(r));
        Assert.Contains("/account/login", HttpAssert.Location(r), StringComparison.OrdinalIgnoreCase);

        // オーナーでも運用者でなければ入れない
        var owner = app.Browser();
        await WebFixture.LoginAsync(owner, "owner@example.com");
        r = await owner.GetAsync("/ops/jobs");
        Assert.True(r.StatusCode == HttpStatusCode.Forbidden || HttpAssert.IsRedirect(r), r.StatusCode.ToString());
        Assert.DoesNotContain("デッドレター", await (await owner.GetAsync("/ops")).Content.ReadAsStringAsync());

        // 運用者
        var operatorClient = app.Browser();
        await WebFixture.LoginAsync(operatorClient, "approver@example.com");
        r = await operatorClient.GetAsync("/ops/jobs/recurring");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("ReachForge ジョブ", await r.Content.ReadAsStringAsync());
        var ops = await (await operatorClient.GetAsync("/ops")).Content.ReadAsStringAsync();
        Assert.Contains("デッドレター", ops);
        Assert.Contains("DataRetentionJob", ops);
    }

    [Fact]
    public async Task Api_keys_cannot_open_the_job_dashboard()
    {
        await using var app = Create();
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "rfk_" + new string('a', 40));
        var r = await client.GetAsync("/ops/jobs");
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }
}

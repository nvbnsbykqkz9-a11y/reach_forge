using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ReachForge.Web.Tests;

public class AuthTests
{
    private static async Task<string> TextAsync(HttpResponseMessage r) => WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());

    /// <summary>リダイレクト先（HTTP リダイレクトまたは描画中のナビゲーション）を含む文字列。</summary>
    private static async Task<string> DestinationAsync(HttpResponseMessage r) =>
        HttpAssert.IsRedirect(r) ? HttpAssert.Location(r) : await TextAsync(r);

    [Fact]
    public async Task Api_requires_authentication_and_pages_redirect_to_login()
    {
        await using var app = new WebFixture();
        var client = app.Browser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/files/{Guid.NewGuid()}")).StatusCode);
        var page = await client.GetAsync("/lp");
        Assert.True(HttpAssert.IsRedirect(page));
        Assert.Contains("/account/login", HttpAssert.Location(page));
    }

    [Fact]
    public async Task Login_grants_access_scoped_to_the_users_tenant()
    {
        await using var app = new WebFixture();
        var client = app.Browser();

        var login = await WebFixture.LoginAsync(client, "owner@example.com");
        Assert.True(HttpAssert.IsRedirect(login), $"status {(int)login.StatusCode}");

        var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("LP から広告・動画をつくる", await TextAsync(page));
    }

    [Fact]
    public async Task Wrong_password_shows_generic_message_and_locks_after_five_failures()
    {
        await using var app = new WebFixture();
        var client = app.Browser();

        var first = await WebFixture.LoginAsync(client, "approver@example.com", "wrong-password");
        Assert.Contains("メールアドレスまたはパスワードが違います。", await TextAsync(first));

        for (var i = 0; i < 4; i++) await WebFixture.LoginAsync(client, "approver@example.com", "wrong-password");
        var locked = await WebFixture.LoginAsync(client, "approver@example.com");
        Assert.False(HttpAssert.IsRedirect(locked));
        Assert.Contains("15分間ログインできません", await TextAsync(locked));
    }

    [Fact]
    public async Task Admins_without_mfa_are_sent_to_mfa_setup_when_required()
    {
        await using var app = new WebFixture(new() { ["Auth:RequireMfaForAdmins"] = "true" });
        var client = app.Browser();
        await WebFixture.LoginAsync(client, "owner@example.com");

        Assert.Contains("/account/mfa?required=true", await DestinationAsync(await client.GetAsync("/")));

        // 編集者は対象外
        var editor = app.Browser();
        await WebFixture.LoginAsync(editor, "editor@example.com");
        Assert.DoesNotContain("/account/mfa", await DestinationAsync(await editor.GetAsync("/")));
    }

    [Fact]
    public void Return_url_cannot_redirect_off_site()
    {
        Assert.Equal("/", Api.AccountEndpoints.SafeReturnUrl("//evil.example/phish"));
        Assert.Equal("/", Api.AccountEndpoints.SafeReturnUrl("https://evil.example"));
        Assert.Equal("/lp", Api.AccountEndpoints.SafeReturnUrl("/lp"));
    }
}

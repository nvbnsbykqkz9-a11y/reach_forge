using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/oauth/callback/X?code=a&state=b")).StatusCode);
        var page = await client.GetAsync("/settings/channels");
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

        var page = await client.GetAsync("/settings/channels");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("@hokkori_cafe", await TextAsync(page));
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
    public async Task Viewer_cannot_complete_a_connection()
    {
        await using var app = new WebFixture();
        var client = app.Browser();
        await WebFixture.LoginAsync(client, "viewer@example.com");

        // 閲覧者は SNS・広告アカウントの連携を完了できない（連携は画面からしか始められず、state も一致しない）
        foreach (var url in new[] { "/api/v1/oauth/callback/X?code=abc&state=forged", "/api/v1/oauth/ads/meta/callback?code=abc&state=forged" })
        {
            var response = await client.GetAsync(url);
            Assert.True(HttpAssert.IsRedirect(response));
            Assert.Contains("error=", HttpAssert.Location(response));
        }
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
    public async Task OAuth_callback_with_unknown_state_is_rejected()
    {
        await using var app = new WebFixture();
        var client = app.Browser();
        await WebFixture.LoginAsync(client, "owner@example.com");

        var response = await client.GetAsync("/api/v1/oauth/callback/X?code=abc&state=forged");
        Assert.True(HttpAssert.IsRedirect(response));
        Assert.StartsWith("/settings/channels?error=", HttpAssert.Location(response));
    }

    [Fact]
    public void Return_url_cannot_redirect_off_site()
    {
        Assert.Equal("/", Api.AccountEndpoints.SafeReturnUrl("//evil.example/phish"));
        Assert.Equal("/", Api.AccountEndpoints.SafeReturnUrl("https://evil.example"));
        Assert.Equal("/settings/channels", Api.AccountEndpoints.SafeReturnUrl("/settings/channels"));
    }

    [Fact]
    public async Task Meta_webhook_verifies_subscription_and_signature()
    {
        await using var app = new WebFixture();
        var client = app.Browser();

        var challenge = await client.GetStringAsync("/api/v1/webhooks/meta?hub.mode=subscribe&hub.verify_token=verify-me&hub.challenge=12345");
        Assert.Equal("12345", challenge);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/v1/webhooks/meta?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1")).StatusCode);

        const string body = """{"object":"page","entry":[]}""";
        var unsigned = new StringContent(body, Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/webhooks/meta", unsigned)).StatusCode);

        var signed = new StringContent(body, Encoding.UTF8, "application/json");
        signed.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("meta-secret"), Encoding.UTF8.GetBytes(body))));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/webhooks/meta", signed)).StatusCode);
    }
}

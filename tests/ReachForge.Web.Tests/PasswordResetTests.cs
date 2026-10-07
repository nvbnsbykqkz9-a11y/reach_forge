using System.Net;
using System.Text.RegularExpressions;

namespace ReachForge.Web.Tests;

public class PasswordResetTests
{
    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, string formName, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(url);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var body = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token, ["_handler"] = formName };
        return await client.PostAsync(url, new FormUrlEncodedContent(body));
    }

    [Fact]
    public async Task Reset_link_by_email_changes_password_and_does_not_reveal_unknown_accounts()
    {
        await using var app = new WebFixture();
        var client = app.Browser();

        // 登録されていないメールアドレスでも同じ表示（メールは送らない）
        var unknown = await PostFormAsync(client, "/account/forgot-password", "forgot", new() { ["Input.Email"] = "nobody@example.com" });
        Assert.Contains("登録されていれば", WebUtility.HtmlDecode(await unknown.Content.ReadAsStringAsync()));
        Assert.Empty(app.Emails);

        await PostFormAsync(client, "/account/forgot-password", "forgot", new() { ["Input.Email"] = "editor@example.com" });
        var mail = Assert.Single(app.Emails);
        Assert.Equal(["editor@example.com"], mail.To);
        var link = Regex.Match(mail.TextBody, @"https?://\S+/account/reset-password\?\S+").Value;
        var path = new Uri(link).PathAndQuery;

        // 確認用の入力が違う
        var mismatch = await PostFormAsync(client, path, "reset", new() { ["Input.Password"] = "NewPassword#1", ["Input.Confirm"] = "Other#12345" });
        Assert.Contains("一致しません", WebUtility.HtmlDecode(await mismatch.Content.ReadAsStringAsync()));

        var done = await PostFormAsync(client, path, "reset", new() { ["Input.Password"] = "NewPassword#1", ["Input.Confirm"] = "NewPassword#1" });
        Assert.Contains("パスワードを変更しました", WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync()));
        Assert.Contains(app.Emails, m => m.Subject.Contains("パスワードが変更されました"));

        // 同じリンクは2回使えない
        var reused = await PostFormAsync(client, path, "reset", new() { ["Input.Password"] = "Another#12345", ["Input.Confirm"] = "Another#12345" });
        Assert.Contains("このリンクは使えません", WebUtility.HtmlDecode(await reused.Content.ReadAsStringAsync()));

        var fresh = app.Browser();
        Assert.False(HttpAssert.IsRedirect(await WebFixture.LoginAsync(fresh, "editor@example.com"))); // 古いパスワードは使えない
        Assert.True(HttpAssert.IsRedirect(await WebFixture.LoginAsync(fresh, "editor@example.com", "NewPassword#1")));
    }

    [Fact]
    public async Task Lockout_sends_one_notification()
    {
        await using var app = new WebFixture();
        var client = app.Browser();
        for (var i = 0; i < 7; i++) await WebFixture.LoginAsync(client, "approver@example.com", "wrong-password");
        var mail = Assert.Single(app.Emails);
        Assert.Contains("ログインを一時的に停止", mail.Subject);
    }
}

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Social;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;
using ReachForge.Infrastructure.Settings;
using ReachForge.Social;

namespace ReachForge.Web.Tests;

/// <summary>SNS アプリの ID・シークレットを画面から設定する：保存するとすぐに連携に使われ、シークレットは暗号化して保存する。</summary>
public class SnsAppSettingsTests
{
    [Fact]
    public async Task Saved_app_settings_take_effect_without_restart_and_secrets_are_encrypted()
    {
        await using var app = new WebFixture(new Dictionary<string, string?> { ["Social:UseMock"] = "false" });
        app.CreateClient().Dispose(); // 起動する
        var services = app.Services;
        var settings = services.GetRequiredService<SnsAppSettingsService>();
        var options = services.GetRequiredService<IOptions<SocialOptions>>();
        var connectors = services.GetServices<IChannelConnector>().ToList();
        bool Connectable(SocialPlatform p) => connectors.Any(c => c.Supports(p));

        Assert.False(Connectable(SocialPlatform.X));
        Assert.False(Connectable(SocialPlatform.TikTok)); // デモ接続もオフ

        await settings.SaveAsync(new Dictionary<string, string?>
        {
            ["Social:X:ClientId"] = " client-1 ", ["Social:X:ClientSecret"] = "secret-1",
        }, "owner@example.com", CancellationToken.None);
        Assert.Equal(("client-1", "secret-1"), (options.Value.X.ClientId, options.Value.X.ClientSecret));
        Assert.True(Connectable(SocialPlatform.X));

        // シークレットは暗号化して保存し、状態にも値を出さない
        await using (var db = new ReachForgeDbContext(services.GetRequiredService<DbContextOptions<ReachForgeDbContext>>(),
                         new MutableTenantContext { IsSystem = true }))
        {
            var rows = await db.AppSettings.AsNoTracking().ToListAsync();
            var secret = rows.Single(r => r.Key == "Social:X:ClientSecret");
            Assert.True(secret.IsSecret);
            Assert.DoesNotContain("secret-1", secret.Value);
            Assert.Equal("client-1", rows.Single(r => r.Key == "Social:X:ClientId").Value);
            Assert.Contains(await db.AuditLogs.AsNoTracking().ToListAsync(), a => a.Action == "settings.updated" && a.Detail!.Contains("ClientSecret"));
        }
        var status = await settings.StatusAsync(CancellationToken.None);
        Assert.Equal(SettingSource.Screen, status["Social:X:ClientSecret"].Source);
        Assert.Null(status["Social:X:ClientSecret"].Value);
        Assert.Equal(SettingSource.File, status["Social:Meta:AppSecret"].Source); // 設定ファイルの値
        Assert.Equal(SettingSource.None, status["Social:YouTube:ClientId"].Source);

        // シークレットを空で保存しても消えない（ID だけ変える）
        await settings.SaveAsync(new Dictionary<string, string?> { ["Social:X:ClientId"] = "client-2", ["Social:X:ClientSecret"] = "" },
            "owner@example.com", CancellationToken.None);
        Assert.Equal(("client-2", "secret-1"), (options.Value.X.ClientId, options.Value.X.ClientSecret));

        // デモ接続の切り替えもすぐ反映する
        await settings.SaveAsync(new Dictionary<string, string?> { [SnsAppSettingsService.UseMockKey] = "true" }, "owner@example.com", CancellationToken.None);
        Assert.True(Connectable(SocialPlatform.TikTok));

        // 画面での設定を消すと元に戻る
        await settings.ClearAsync(SnsAppSettingsService.Apps.Single(a => a.Id == "x"), "owner@example.com", CancellationToken.None);
        Assert.True(string.IsNullOrEmpty(options.Value.X.ClientId)); // 設定ファイルの値（空）に戻る
        Assert.DoesNotContain(connectors.Where(c => c is not ReachForge.Social.Mock.DemoChannelConnector), c => c.Supports(SocialPlatform.X));

        await Assert.ThrowsAsync<ReachForge.Domain.Common.DomainException>(() => settings.SaveAsync(
            new Dictionary<string, string?> { ["ConnectionStrings:ReachForge"] = "x" }, "owner@example.com", CancellationToken.None));
    }

    [Fact]
    public async Task Settings_page_is_for_operators_and_for_the_owner_in_the_windows_edition()
    {
        await using (var web = new WebFixture(new Dictionary<string, string?> { ["Ops:Operators:0"] = "approver@example.com" }))
        {
            var owner = web.Browser();
            await WebFixture.LoginAsync(owner, "owner@example.com");
            var r = await owner.GetAsync("/settings/sns-apps");
            Assert.True(r.StatusCode == HttpStatusCode.Forbidden || HttpAssert.IsRedirect(r), r.StatusCode.ToString());

            var operatorClient = web.Browser();
            await WebFixture.LoginAsync(operatorClient, "approver@example.com");
            r = await operatorClient.GetAsync("/settings/sns-apps");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("SNSアプリの設定", await r.Content.ReadAsStringAsync());
        }

        await using var desktop = new WebFixture(new Dictionary<string, string?> { ["Desktop:Enabled"] = "true" });
        var local = desktop.Browser();
        await WebFixture.LoginAsync(local, "owner@example.com");
        var page = await local.GetAsync("/settings/sns-apps");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("api/v1/oauth/callback/X", html);
        Assert.DoesNotContain("meta-secret", html); // 設定ファイルのシークレットも画面に出さない
    }
}

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure.Persistence;
using ReachForge.Infrastructure.Settings;

namespace ReachForge.Web.Tests;

/// <summary>生成 AI の API キーを画面から設定する：保存するとすぐに使われ、キーは暗号化して保存し、画面には出さない。</summary>
public class AppSettingsTests
{
    [Fact]
    public async Task Ai_api_key_takes_effect_without_restart_and_is_encrypted()
    {
        await using var app = new WebFixture();
        app.CreateClient().Dispose(); // 起動する
        var settings = app.Services.GetRequiredService<AppSettingsService>();
        var ai = app.Services.GetRequiredService<IOptions<ReachForge.AI.AiOptions>>();
        Assert.False(ai.Value.Providers.TryGetValue("anthropic", out var before) && before.IsConfigured);

        await settings.SaveAsync(new Dictionary<string, string?> { ["AI:Providers:anthropic:ApiKey"] = " sk-ant-test " },
            "owner@example.com", CancellationToken.None);
        Assert.Equal("sk-ant-test", ai.Value.Providers["anthropic"].ApiKey);
        Assert.True(ai.Value.Providers["anthropic"].IsConfigured);

        await using (var db = new ReachForgeDbContext(app.Services.GetRequiredService<DbContextOptions<ReachForgeDbContext>>(),
                         new MutableTenantContext { IsSystem = true }))
        {
            var row = await db.AppSettings.AsNoTracking().SingleAsync(r => r.Key == "AI:Providers:anthropic:ApiKey");
            Assert.True(row.IsSecret);
            Assert.DoesNotContain("sk-ant-test", row.Value);
            Assert.Contains(await db.AuditLogs.AsNoTracking().ToListAsync(), a => a.Action == "settings.updated" && a.Detail!.Contains("ApiKey"));
        }
        var status = await settings.StatusAsync(CancellationToken.None);
        Assert.Equal(SettingSource.Screen, status["AI:Providers:anthropic:ApiKey"].Source);
        Assert.Null(status["AI:Providers:anthropic:ApiKey"].Value);

        // シークレットを空で保存しても消えない
        await settings.SaveAsync(new Dictionary<string, string?> { ["AI:Providers:anthropic:ApiKey"] = "" }, "owner@example.com", CancellationToken.None);
        Assert.Equal("sk-ant-test", ai.Value.Providers["anthropic"].ApiKey);

        // 画面での設定を消すと元に戻る
        await settings.ClearAsync(AppSettingsService.AiProviders.Single(a => a.Id == "anthropic"), "owner@example.com", CancellationToken.None);
        Assert.False(ai.Value.Providers["anthropic"].IsConfigured);

        // 画面の項目以外は保存できない
        await Assert.ThrowsAsync<ReachForge.Domain.Common.DomainException>(() => settings.SaveAsync(
            new Dictionary<string, string?> { ["ConnectionStrings:ReachForge"] = "x" }, "owner@example.com", CancellationToken.None));
    }

    [Fact]
    public async Task Ai_settings_are_for_operators_and_for_the_owner_in_the_windows_edition()
    {
        await using (var web = new WebFixture(new Dictionary<string, string?> { ["Ops:Operators:0"] = "approver@example.com" }))
        {
            var owner = web.Browser();
            await WebFixture.LoginAsync(owner, "owner@example.com");
            var r = await owner.GetAsync("/settings/ai");
            Assert.True(r.StatusCode == HttpStatusCode.Forbidden || HttpAssert.IsRedirect(r), r.StatusCode.ToString());

            var operatorClient = web.Browser();
            await WebFixture.LoginAsync(operatorClient, "approver@example.com");
            Assert.Equal(HttpStatusCode.OK, (await operatorClient.GetAsync("/settings/ai")).StatusCode);
        }

        await using var desktop = new WebFixture(new Dictionary<string, string?> { ["Desktop:Enabled"] = "true" });
        var local = desktop.Browser();
        await WebFixture.LoginAsync(local, "owner@example.com");
        Assert.Equal(HttpStatusCode.OK, (await local.GetAsync("/settings/ai")).StatusCode);
    }
}

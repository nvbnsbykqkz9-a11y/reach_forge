using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Web.Tests;

/// <summary>リアルタイム通知（RF-DES-001 3.3）：SignalR Hub で自分のワークスペースの出来事だけを受け取る。</summary>
public class RealtimeTests
{
    /// <summary>ログインした Cookie を共有する（画面と同じ認証で Hub に接続する）。</summary>
    private static async Task<CookieContainer> LoginAsync(WebFixture app)
    {
        var cookies = new CookieContainer();
        var client = app.CreateDefaultClient(new Uri("https://localhost"), new CookieContainerHandler(cookies));
        var login = await WebFixture.LoginAsync(client, "owner@example.com");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        return cookies;
    }

    private static HubConnection Connect(WebFixture app, CookieContainer? cookies) => new HubConnectionBuilder()
        .WithUrl(new Uri("https://localhost/hubs/realtime"), o =>
        {
            o.HttpMessageHandlerFactory = _ => new CookieContainerHandler(cookies ?? new CookieContainer()) { InnerHandler = app.Server.CreateHandler() };
            o.Transports = HttpTransportType.LongPolling;
        })
        .Build();

    [Fact]
    public async Task Webhook_ingestion_is_pushed_to_connected_clients()
    {
        await using var app = new WebFixture();
        _ = app.Server; // サーバーを起動する
        var cookies = await LoginAsync(app);

        await using var anonymous = Connect(app, null);
        var denied = await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
        Assert.Contains("401", denied.Message);

        await using var connection = Connect(app, cookies);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("inboxUpdated", e => received.TrySetResult(e));
        await connection.StartAsync();

        const string body = """
            {"object":"instagram","entry":[{"id":"demo-instagram","changes":[{"field":"comments",
              "value":{"id":"rt-1","text":"予約はできますか？","from":{"id":"u1","username":"rt_user"},"media":{"id":"m1"}}}]}]}
            """;
        var signed = new StringContent(body, Encoding.UTF8, "application/json");
        signed.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("meta-secret"), Encoding.UTF8.GetBytes(body))));
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().PostAsync("/api/v1/webhooks/meta", signed)).StatusCode);

        var e = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DemoSeeder.WorkspaceId, e.GetProperty("workspaceId").GetGuid());
        Assert.Equal(1, e.GetProperty("newMessages").GetInt32());
    }
}

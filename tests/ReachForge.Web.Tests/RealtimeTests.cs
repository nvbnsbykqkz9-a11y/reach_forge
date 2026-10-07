using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Enums;
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
    public async Task Job_progress_is_pushed_only_to_the_same_workspace()
    {
        await using var app = new WebFixture();
        _ = app.Server; // サーバーを起動する
        var cookies = await LoginAsync(app);

        await using var anonymous = Connect(app, null);
        var denied = await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
        Assert.Contains("401", denied.Message);

        await using var connection = Connect(app, cookies);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("jobProgress", e => received.TrySetResult(e));
        await connection.StartAsync();

        var notifier = app.Services.GetRequiredService<IRealtimeNotifier>();
        var jobId = Guid.NewGuid();
        notifier.Publish(new JobProgressEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AiTaskType.Video, AiJobStatus.Running, AiJobStage.Generating));
        notifier.Publish(new JobProgressEvent(DemoSeeder.TenantId, DemoSeeder.WorkspaceId, jobId, AiTaskType.Video, AiJobStatus.Running, AiJobStage.Generating));

        var e = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(jobId, e.GetProperty("jobId").GetGuid()); // 別のワークスペースの出来事は届かない
    }
}

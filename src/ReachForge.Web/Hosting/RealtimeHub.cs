using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure.Identity;
using ReachForge.Infrastructure.Realtime;

namespace ReachForge.Web.Hosting;

/// <summary>
/// 外部クライアント（ブラウザ拡張・社内ツールなど）向けのリアルタイム通知（RF-DES-001 3.3）。
/// 接続した利用者のワークスペースの出来事だけを受け取る。メソッド：jobProgress / inboxUpdated。
/// Blazor の画面はサーキット上で <see cref="RealtimeBus"/> を直接購読する。
/// </summary>
[Authorize]
public sealed class RealtimeHub : Hub
{
    public const string Path = "/hubs/realtime";

    public static string Group(Guid tenantId, Guid workspaceId) => $"ws:{tenantId:N}:{workspaceId:N}";

    /// <summary>接続時の認証情報（ログインまたは API キー）のテナント・ワークスペースのグループに入れる。</summary>
    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        if (!Guid.TryParse(user?.FindFirst(RfClaims.TenantId)?.Value, out var tenantId)
            || !Guid.TryParse(user?.FindFirst(RfClaims.WorkspaceId)?.Value, out var workspaceId))
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(tenantId, workspaceId));
        await base.OnConnectedAsync();
    }
}

/// <summary>
/// プロセス内の出来事を、このインスタンスに接続しているクライアントへ送る。
/// ほかのインスタンス・Worker の出来事は Redis 経由でこのプロセスの <see cref="RealtimeBus"/> に届くため、バックプレーンは使わない（二重送信を防ぐ）。
/// </summary>
public sealed class RealtimeHubForwarder(RealtimeBus bus, IHubContext<RealtimeHub> hub) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Subscribe(e =>
        {
            var clients = hub.Clients.Group(RealtimeHub.Group(e.TenantId, e.WorkspaceId));
            return e switch
            {
                JobProgressEvent job => clients.SendAsync("jobProgress", job, cancellationToken: CancellationToken.None),
                _ => Task.CompletedTask,
            };
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}

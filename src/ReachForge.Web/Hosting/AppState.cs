using ReachForge.Application.Services;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;

namespace ReachForge.Web.Hosting;

/// <summary>
/// シェル（トップバー・ナビ）に表示する共有状態。画面での操作後に <see cref="RefreshAsync"/> を呼ぶ。
/// サーキットの間ずっと生きるため、読み込みのたびに新しいスコープ（DbContext）で最新の値を取る。
/// </summary>
public sealed class AppState(TenantScopes scopes)
{
    public Workspace? Workspace { get; private set; }
    public CreditAccount? Credits { get; private set; }
    public int PendingApprovals { get; private set; }
    public int OpenInbox { get; private set; }
    public bool PublishingPaused { get; private set; }

    public event Action? Changed;

    public async Task RefreshAsync()
    {
        await scopes.RunAsync(async sp =>
        {
            Workspace = await sp.GetRequiredService<WorkspaceService>().CurrentAsync(CancellationToken.None);
            Credits = await sp.GetRequiredService<ICreditService>().GetAccountAsync(CancellationToken.None);
            PendingApprovals = (await sp.GetRequiredService<ApprovalService>().QueueAsync(CancellationToken.None)).Count;
            OpenInbox = await sp.GetRequiredService<InboxService>().OpenCountAsync(CancellationToken.None);
            PublishingPaused = await sp.GetRequiredService<SchedulingService>().IsPausedAsync(CancellationToken.None);
        });
        Changed?.Invoke();
    }
}

using ReachForge.Application.Services;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;

namespace ReachForge.Web.Hosting;

/// <summary>シェル（トップバー・ナビ）に表示する共有状態。画面での操作後に <see cref="RefreshAsync"/> を呼ぶ。</summary>
public sealed class AppState(WorkspaceService workspaces, ApprovalService approvals, SchedulingService scheduling,
    ICreditService credits)
{
    public Workspace? Workspace { get; private set; }
    public CreditAccount? Credits { get; private set; }
    public int PendingApprovals { get; private set; }
    public bool PublishingPaused { get; private set; }

    public event Action? Changed;

    public async Task RefreshAsync()
    {
        Workspace = await workspaces.CurrentAsync(CancellationToken.None);
        Credits = await credits.GetAccountAsync(CancellationToken.None);
        PendingApprovals = (await approvals.QueueAsync(CancellationToken.None)).Count;
        PublishingPaused = await scheduling.IsPausedAsync(CancellationToken.None);
        Changed?.Invoke();
    }
}

using Microsoft.AspNetCore.Components;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure.Realtime;

namespace ReachForge.Web.Hosting;

/// <summary>
/// DB を使うサービスを呼ぶ画面・部品の基底クラス。
/// Blazor Server のスコープはサーキット（タブを開いている間）単位のため、そこで解決した DbContext は長く生き続け、
/// 古い追跡データを返したり、別の画面の処理と同時に使われて失敗したりする。そこで画面（部品）ごとに DI スコープを作り、
/// 表示している間だけ DbContext を使い、閉じたら破棄する（OwningComponentBase）。
/// テナント・利用者・ロールはサーキットのもの（<see cref="WebTenantContext"/>）を引き継ぐ。
/// </summary>
public abstract class RfComponentBase : OwningComponentBase
{
    [Inject] private WebTenantContext CircuitTenant { get; set; } = default!;
    [Inject] private RealtimeBus Realtime { get; set; } = default!;

    private bool _bound;
    private readonly List<IDisposable> _subscriptions = [];

    /// <summary>
    /// このワークスペースの出来事（AI ジョブの進捗・受信箱の更新）を受け取る（RF-DES-001 3.3）。
    /// 画面を閉じると購読をやめる。処理は画面の同期コンテキストで実行する。
    /// </summary>
    protected void OnRealtime<T>(Func<T, Task> handler) where T : RealtimeEvent
    {
        var tenantId = CircuitTenant.TenantId;
        var workspaceId = CircuitTenant.WorkspaceId;
        _subscriptions.Add(Realtime.Subscribe(e => e is T match && match.TenantId == tenantId && match.WorkspaceId == workspaceId
            ? InvokeAsync(async () =>
            {
                await handler(match);
                StateHasChanged();
            })
            : Task.CompletedTask));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeSubscriptions();
        base.Dispose(disposing);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        DisposeSubscriptions();
        return base.DisposeAsyncCore();
    }

    private void DisposeSubscriptions()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
    }

    /// <summary>この画面のスコープからサービスを取得する。</summary>
    protected T Scoped<T>() where T : notnull
    {
        if (!_bound)
        {
            ScopedServices.GetRequiredService<TenantContextOverride>().Current = CircuitTenant;
            _bound = true;
        }
        return ScopedServices.GetRequiredService<T>();
    }
}

using Microsoft.AspNetCore.Components;
using ReachForge.Application.Abstractions;

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

    private bool _bound;

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

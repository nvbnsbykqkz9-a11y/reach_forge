using ReachForge.Application.Abstractions;

namespace ReachForge.Web.Hosting;

/// <summary>
/// 1回の処理だけの DI スコープ（＝新しい DbContext）を作り、サーキットのテナント・利用者を引き継いで実行する。
/// サーキット単位で生き続けるもの（AppState・レイアウト）から DB を使うときに使う。
/// </summary>
public sealed class TenantScopes(IServiceScopeFactory factory, WebTenantContext tenant)
{
    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = factory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current = tenant;
        return await work(scope.ServiceProvider);
    }

    public Task RunAsync(Func<IServiceProvider, Task> work) => RunAsync(async sp =>
    {
        await work(sp);
        return true;
    });
}

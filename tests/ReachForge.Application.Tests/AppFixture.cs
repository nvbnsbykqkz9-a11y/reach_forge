using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Application.Tests;

/// <summary>テストごとに独立した SQLite ファイル・スタブ AI・モック SNS でアプリケーション全体を組み立てる。</summary>
public sealed class AppFixture : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"rf-test-{Guid.NewGuid():N}.db");
    public string MediaPath { get; } = Path.Combine(Path.GetTempPath(), $"rf-media-{Guid.NewGuid():N}");

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
    public ServiceProvider Services { get; }

    /// <summary>テスト中に記録されたエラーログ。</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    private AppFixture(Dictionary<string, string?>? overrides, Action<IServiceCollection>? configure, bool prependConnector)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:ReachForge"] = $"Data Source={_dbPath}",
            ["AI:Providers:local:Type"] = "Stub",
            ["AI:Routes:Default:0"] = "local",
            ["Social:UseMock"] = "true",
            ["Media:LocalPath"] = MediaPath,
        };
        foreach (var (k, v) in overrides ?? []) settings[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(Logs));
        if (prependConnector) configure?.Invoke(services); // 実コネクタ・デモより前に登録して優先させる
        services.AddReachForge(config);
        if (!prependConnector) configure?.Invoke(services);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped<MutableTenantContext>(_ => new MutableTenantContext
        {
            TenantId = DemoSeeder.TenantId,
            WorkspaceId = DemoSeeder.WorkspaceId,
            UserName = "田中",
            Role = Role.Owner,
        });
        services.AddScoped<ITenantContext>(sp =>
            sp.GetRequiredService<TenantContextOverride>().Current ?? sp.GetRequiredService<MutableTenantContext>());
        Services = services.BuildServiceProvider();
    }

    public static async Task<AppFixture> CreateAsync(Dictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configure = null, bool prependConnector = false)
    {
        var f = new AppFixture(overrides, configure, prependConnector);
        await DemoSeeder.InitializeAsync(f.Services, seed: true);
        return f;
    }

    /// <summary>新しいスコープ（＝1リクエスト）を作る。<paramref name="configure"/> で利用者・ロールを変えられる。</summary>
    public AsyncServiceScope Scope(Action<MutableTenantContext>? configure = null)
    {
        var scope = Services.CreateAsyncScope();
        configure?.Invoke(scope.ServiceProvider.GetRequiredService<MutableTenantContext>());
        return scope;
    }

    public T Get<T>(AsyncServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(MediaPath)) Directory.Delete(MediaPath, recursive: true);
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(f)) File.Delete(f);
        }
    }
}

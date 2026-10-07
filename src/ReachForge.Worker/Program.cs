using ReachForge.Infrastructure.Settings;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure;
using ReachForge.Infrastructure.Jobs;
using ReachForge.Infrastructure.Persistence;

// ReachForge.Worker：予約配信・定期ジョブ・キューの処理を実行する（RF-DES-001 3.1 Web/API と Worker の2プロセス構成、14章）
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddReachForge(builder.Configuration);

// Worker はテナント横断で動作する（ジョブごとにシステムコンテキストで実行）
builder.Services.AddScoped<ITenantContext>(sp =>
    sp.GetRequiredService<TenantContextOverride>().Current ?? new MutableTenantContext { IsSystem = true, UserName = "system" });
builder.Services.AddReachForgeJobs(builder.Configuration);

var host = builder.Build();
await DemoSeeder.InitializeAsync(host.Services, seed: builder.Configuration.GetValue("Database:SeedDemo", false));
// 画面で保存した設定（SNS アプリの ID・シークレットなど）。Web で保存した変更も1分以内に読み直す
builder.Configuration.AddDbSettings(host.Services);
host.Run();

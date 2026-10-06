using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure;
using ReachForge.Infrastructure.Hosting;
using ReachForge.Infrastructure.Persistence;

// ReachForge.Worker：予約配信・定期ジョブを実行する（RF-DES-001 3.1 Web/API と Worker の2プロセス構成）
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddReachForge(builder.Configuration);
builder.Services.Configure<PublishDispatcherOptions>(builder.Configuration.GetSection(PublishDispatcherOptions.SectionName));

// Worker はテナント横断で動作する（ジョブごとにシステムコンテキストで実行）
builder.Services.AddScoped<ITenantContext>(sp =>
    sp.GetRequiredService<TenantContextOverride>().Current ?? new MutableTenantContext { IsSystem = true, UserName = "system" });
builder.Services.AddHostedService<PublishDispatcher>();
builder.Services.AddHostedService<TokenRefreshScheduler>();
builder.Services.AddHostedService<AiJobDispatcher>();
// TODO(14章): MetricsCollectJob / TokenRefreshJob / InboxPollJob / WeeklyReportJob / CreditResetJob などを Hangfire で登録する

var host = builder.Build();
await DemoSeeder.InitializeAsync(host.Services, seed: builder.Configuration.GetValue("Database:SeedDemo", false));
host.Run();

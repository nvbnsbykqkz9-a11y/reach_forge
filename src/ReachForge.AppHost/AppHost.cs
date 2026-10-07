// .NET Aspire の AppHost（RF-DES-001 3.5 Local 環境）：PostgreSQL・Redis（・Service Bus エミュレーター）と Web・Worker をまとめて起動し、
// ダッシュボードでログ・トレース・メトリクスを確認する。Docker（または Podman）が必要。
//   dotnet run --project src/ReachForge.AppHost
// Service Bus を使う場合：dotnet run --project src/ReachForge.AppHost -- --UseServiceBus=true
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres").WithDataVolume().WithPgAdmin();
var database = postgres.AddDatabase("reachforge");
var redis = builder.AddRedis("redis");

var useServiceBus = bool.TryParse(builder.Configuration["UseServiceBus"], out var sb) && sb;
IResourceBuilder<Aspire.Hosting.Azure.AzureServiceBusResource>? serviceBus = null;
if (useServiceBus)
{
    serviceBus = builder.AddAzureServiceBus("servicebus").RunAsEmulator();
    serviceBus.AddServiceBusQueue("webhooks").WithProperties(q => q.MaxDeliveryCount = 5);
    serviceBus.AddServiceBusQueue("ai-jobs").WithProperties(q => q.MaxDeliveryCount = 5);
}

// Web と Worker に共通の設定（PostgreSQL + RLS のマイグレーション、Hangfire、Redis によるリアルタイム通知）
IResourceBuilder<ProjectResource> Configure(IResourceBuilder<ProjectResource> project)
{
    project
        .WithReference(database, "ReachForge")
        .WithReference(redis, "Redis")
        .WaitFor(database)
        .WaitFor(redis)
        .WithEnvironment("Database__Provider", "Postgres")
        .WithEnvironment("Database__MigrateOnStartup", "true")
        .WithEnvironment("Jobs__Engine", "Hangfire");
    if (serviceBus is not null)
    {
        project.WithReference(serviceBus, "ServiceBus")
            .WaitFor(serviceBus)
            .WithEnvironment("Jobs__Queue", "ServiceBus")
            .WithEnvironment(ctx => ctx.EnvironmentVariables["Jobs__ServiceBus__ConnectionString"] = serviceBus.Resource.ConnectionStringExpression);
    }
    return project;
}

var worker = Configure(builder.AddProject<Projects.ReachForge_Worker>("worker"))
    .WithEnvironment("Database__SeedDemo", "true");

Configure(builder.AddProject<Projects.ReachForge_Web>("web"))
    .WithEnvironment("Worker__RunInWeb", "false")
    .WithEnvironment("Database__SeedDemo", "true")
    .WaitFor(worker)
    .WithExternalHttpEndpoints();

builder.Build().Run();

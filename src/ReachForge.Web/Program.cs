using MudBlazor;
using MudBlazor.Services;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure;
using ReachForge.Infrastructure.Hosting;
using ReachForge.Infrastructure.Persistence;
using ReachForge.Web.Api;
using ReachForge.Web.Components;
using ReachForge.Web.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices(o =>
{
    o.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
    o.SnackbarConfiguration.VisibleStateDuration = 4000;
    o.SnackbarConfiguration.PreventDuplicates = true;
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddReachForge(builder.Configuration);
builder.Services.AddScoped<WebTenantContext>();
builder.Services.AddScoped<ITenantContext>(sp =>
    sp.GetRequiredService<TenantContextOverride>().Current ?? sp.GetRequiredService<WebTenantContext>());
builder.Services.AddScoped<TimeDisplay>();
builder.Services.AddScoped<AppState>();

// ローカル開発では Web プロセス内でも予約配信を動かせる（本番は ReachForge.Worker が担当）
builder.Services.Configure<PublishDispatcherOptions>(builder.Configuration.GetSection(PublishDispatcherOptions.SectionName));
if (builder.Configuration.GetValue<bool>("Worker:RunInWeb"))
{
    builder.Services.AddHostedService<PublishDispatcher>();
}

var app = builder.Build();

await DemoSeeder.InitializeAsync(app.Services, seed: app.Configuration.GetValue("Database:SeedDemo", false));

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapReachForgeApi();
app.MapDefaultEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;

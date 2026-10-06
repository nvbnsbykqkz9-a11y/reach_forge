using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MudBlazor;
using MudBlazor.Services;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure;
using ReachForge.Infrastructure.Hosting;
using ReachForge.Infrastructure.Identity;
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

// ---- 認証・認可（RF-DES-001 9.1 / RF-UX-001 SCR-01） ----
var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingAuthStateProvider>();

var authentication = builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = IdentityConstants.ApplicationScheme;
    o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
});
authentication.AddIdentityCookies();

// 外部 ID プロバイダ（Google / Microsoft / Entra External ID 等）は設定がある場合のみ有効にする
foreach (var (scheme, oidc) in authOptions.Oidc.Where(p => p.Value.IsConfigured))
{
    authentication.AddOpenIdConnect(scheme, oidc.DisplayName, o =>
    {
        o.Authority = oidc.Authority;
        o.ClientId = oidc.ClientId;
        o.ClientSecret = oidc.ClientSecret;
        o.ResponseType = "code";
        o.UsePkce = true;
        o.SignInScheme = IdentityConstants.ExternalScheme;
        o.CallbackPath = $"/signin-{scheme.ToLowerInvariant()}";
        o.Scope.Add("email");
        o.MapInboundClaims = false;
        o.TokenValidationParameters.NameClaimType = "name";
    });
}

builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedAccount = false;
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Lockout.MaxFailedAccessAttempts = 5;                        // 5回失敗で
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);  // 15分ロック
        o.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<ReachForgeDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<RfClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/account/login";
    o.LogoutPath = "/account/logout";
    o.AccessDeniedPath = "/account/access-denied";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.Cookie.Name = "rf.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    // API はリダイレクトではなく 401 / 403 を返す
    o.Events.OnRedirectToLogin = ctx => ApiAware(ctx, StatusCodes.Status401Unauthorized);
    o.Events.OnRedirectToAccessDenied = ctx => ApiAware(ctx, StatusCodes.Status403Forbidden);
});
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
builder.Services.AddAuthorization();

builder.Services.AddScoped<WebTenantContext>();
builder.Services.AddScoped<ITenantContext>(sp =>
    sp.GetRequiredService<TenantContextOverride>().Current ?? sp.GetRequiredService<WebTenantContext>());
builder.Services.AddScoped<TimeDisplay>();
builder.Services.AddScoped<AppState>();

// ローカル開発では Web プロセス内でも予約配信・トークン更新を動かせる（本番は ReachForge.Worker が担当）
// Webhook は Web で受けるため、取り込み処理は常に Web プロセスで動かす
builder.Services.AddSingleton<WebhookQueue>();
builder.Services.AddHostedService<WebhookProcessor>();
builder.Services.Configure<PublishDispatcherOptions>(builder.Configuration.GetSection(PublishDispatcherOptions.SectionName));
if (builder.Configuration.GetValue<bool>("Worker:RunInWeb"))
{
    builder.Services.AddHostedService<PublishDispatcher>();
    builder.Services.AddHostedService<TokenRefreshScheduler>();
    builder.Services.AddHostedService<AiJobDispatcher>();
    builder.Services.AddHostedService<MetricsCollectScheduler>();
    builder.Services.AddHostedService<ReportScheduler>();
    builder.Services.AddHostedService<InboxPollScheduler>();
    builder.Services.AddHostedService<AbTestScheduler>();
    builder.Services.AddHostedService<TrendScheduler>();
}

var app = builder.Build();

await DemoSeeder.InitializeAsync(app.Services, seed: app.Configuration.GetValue("Database:SeedDemo", false));

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
// API は状態コード（401/403/404 と Problem Details）をそのまま返し、画面だけ「見つかりません」ページを表示する
app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/api"),
    b => b.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAccountEndpoints();
app.MapReachForgeApi();
app.MapMediaEndpoints();
app.MapAnalyticsEndpoints();
app.MapInboxEndpoints();
app.MapCampaignEndpoints();
app.MapWebhookEndpoints();
app.MapDefaultEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

static Task ApiAware(Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> ctx, int status)
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = status;
        return Task.CompletedTask;
    }
    ctx.Response.Redirect(ctx.RedirectUri);
    return Task.CompletedTask;
}

public partial class Program;

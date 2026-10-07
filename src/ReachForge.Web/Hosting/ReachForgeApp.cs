using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Hangfire;
using MudBlazor;
using MudBlazor.Services;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure;
using ReachForge.Infrastructure.Hosting;
using ReachForge.Infrastructure.Identity;
using ReachForge.Infrastructure.Jobs;
using ReachForge.Infrastructure.Persistence;
using ReachForge.Web.Api;
using ReachForge.Web.Components;

namespace ReachForge.Web.Hosting;

/// <summary>
/// アプリの組み立て（サービスの登録・初期化・要求の処理の順番）。Web 版（Program）と Windows 版（ReachForge.Desktop がプロセス内で起動）で共通。
/// </summary>
public static class ReachForgeApp
{
    /// <summary>
    /// アプリを組み立て、DB を初期化する（起動はしない）。
    /// <paramref name="desktopSettings"/> は Windows 版が渡す設定（データの場所・自動ログインの値など）で、利用者の設定ファイルより優先する。
    /// </summary>
    public static async Task<WebApplication> CreateAsync(WebApplicationBuilder builder,
        IReadOnlyDictionary<string, string?>? desktopSettings = null)
    {
        if (desktopSettings is not null) builder.Configuration.AddInMemoryCollection(desktopSettings);
        builder.AddDesktopMode(desktopSettings);
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
        // 外部連携用の API キー（/api/v1 のみ有効）
        authentication.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

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
        // パスワード再設定などのトークンは1時間で失効させる
        builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = AccountService.ResetTokenLifetime);
        // 既定の認可はログイン（Cookie）と API キーのどちらでもよい
        builder.Services.AddAuthorization(o => o.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
                IdentityConstants.ApplicationScheme, ApiKeyAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser().Build());
        builder.Services.AddReachForgeRateLimits(builder.Configuration);
        builder.Services.AddReachForgeOps(builder.Configuration);

        builder.Services.AddScoped<WebTenantContext>();
        builder.Services.AddScoped<ITenantContext>(sp =>
            sp.GetRequiredService<TenantContextOverride>().Current ?? sp.GetRequiredService<WebTenantContext>());
        builder.Services.AddScoped<TimeDisplay>();
        builder.Services.AddScoped<TenantScopes>();
        // リアルタイム通知（RF-DES-001 3.3）：外部クライアント向けの SignalR Hub
        builder.Services.AddSignalR();
        builder.Services.AddHostedService<RealtimeHubForwarder>();
        builder.Services.AddScoped<ReachForge.AI.Evals.EvalRunner>();
        builder.Services.AddScoped<AppState>();

        // ジョブ（14章）：本番は ReachForge.Worker が実行する。ローカル開発では Worker:RunInWeb で Web プロセス内でも動かせる
        if (builder.Configuration.GetValue<bool>("Worker:RunInWeb"))
        {
            builder.Services.AddReachForgeJobs(builder.Configuration);
        }
        else
        {
            // プロセス内キューの場合、Web で受けた Webhook は Web で取り込む
            builder.Services.AddReachForgeWorkConsumers(builder.Configuration, all: false);
            // Hangfire のダッシュボード（/ops/jobs）を表示するためにストレージだけ登録する
            if (builder.Configuration.GetValue<JobEngine>("Jobs:Engine") == JobEngine.Hangfire)
            {
                builder.Services.AddReachForgeHangfire(builder.Configuration, server: false);
            }
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
        app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/hubs"),
            b => b.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseMiddleware<IdempotencyMiddleware>();
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
        app.MapDesktopEndpoints();
        app.MapHub<RealtimeHub>(RealtimeHub.Path);
        if (app.Configuration.GetValue<JobEngine>("Jobs:Engine") == JobEngine.Hangfire)
        {
            // ジョブ監視（14章・SCR-16）：実行履歴・失敗の確認と再実行。運用者だけが見られる
            app.MapHangfireDashboard("/ops/jobs", new Hangfire.DashboardOptions
            {
                Authorization = [],
                AppPath = "/ops",
                DashboardTitle = "ReachForge ジョブ",
                DisplayStorageConnectionString = false,
            }).RequireAuthorization(OpsAccess.Policy);
        }
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        return app;
    }

    private static Task ApiAware(Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> ctx, int status)
    {
        // API と SignalR Hub はログイン画面へ転送せず、状態コードを返す
        if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs"))
        {
            ctx.Response.StatusCode = status;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }
}

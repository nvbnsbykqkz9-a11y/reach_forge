using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Entities;
using ReachForge.Infrastructure.Identity;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Web.Hosting;

/// <summary>
/// API キー認証（/api/v1 のみ）。「Authorization: Bearer rfk_…」または「X-Api-Key: rfk_…」を受け付ける。
/// キーはハッシュで照合し、失効・期限切れは拒否する。最終利用日時は5分ごとに更新する。
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DbContextOptions<ReachForgeDbContext> dbOptions,
    TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var hub = Request.Path.StartsWithSegments(RealtimeHub.Path);
        if (!Request.Path.StartsWithSegments("/api/v1") && !hub) return AuthenticateResult.NoResult();
        var secret = Request.Headers["X-Api-Key"].FirstOrDefault();
        // WebSocket ではヘッダーを付けられないため、Hub だけはクエリ（access_token）でも受け付ける（SignalR の標準の方法）
        if (secret is null && hub) secret = Request.Query["access_token"].FirstOrDefault();
        if (secret is null && Request.Headers.Authorization.FirstOrDefault() is { } auth
            && auth.StartsWith("Bearer " + ApiKey.KeyPrefix, StringComparison.Ordinal))
        {
            secret = auth["Bearer ".Length..].Trim();
        }
        if (string.IsNullOrEmpty(secret)) return AuthenticateResult.NoResult();
        if (!secret.StartsWith(ApiKey.KeyPrefix, StringComparison.Ordinal) || secret.Length > 100)
        {
            return AuthenticateResult.Fail("invalid api key");
        }

        await using var db = new ReachForgeDbContext(dbOptions, new MutableTenantContext { IsSystem = true, UserName = "apikey" }, null, clock);
        var hash = ApiKey.Hash(secret);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.SecretHash == hash, Context.RequestAborted);
        var now = clock.GetUtcNow();
        if (key is null || !key.IsUsable(now)) return AuthenticateResult.Fail("invalid api key");

        if (key.LastUsedAt is null || now - key.LastUsedAt > TimeSpan.FromMinutes(5))
        {
            key.LastUsedAt = now;
            try
            {
                await db.SaveChangesAsync(Context.RequestAborted);
            }
            catch (DbUpdateConcurrencyException)
            {
                // 同時の要求が更新済み
            }
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, key.Id.ToString()),
            new Claim(RfClaims.ApiKeyId, key.Id.ToString()),
            new Claim(RfClaims.TenantId, key.TenantId.ToString()),
            new Claim(RfClaims.WorkspaceId, key.WorkspaceId.ToString()),
            new Claim(RfClaims.Role, key.Role.ToString()),
            new Claim(RfClaims.DisplayName, $"APIキー：{key.Name}"),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Request.Path.StartsWithSegments("/api") || Request.Path.StartsWithSegments("/hubs") ? base.HandleForbiddenAsync(properties) : Task.CompletedTask;

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // 画面はログイン画面へのリダイレクト（Cookie 側）に任せる
        if (!Request.Path.StartsWithSegments("/api") && !Request.Path.StartsWithSegments("/hubs")) return Task.CompletedTask;
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"ReachForge\"";
        return Task.CompletedTask;
    }
}

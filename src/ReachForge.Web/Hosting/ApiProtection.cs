using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Entities;
using ReachForge.Infrastructure.Identity;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Web.Hosting;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>テナント単位の API 呼び出し（1分あたり、RF-DES-001 13章）。</summary>
    public int TenantPerMinute { get; set; } = 600;

    /// <summary>AI 生成系の API（1分あたり）。</summary>
    public int AiPerMinute { get; set; } = 60;

    /// <summary>ログイン前の操作（ログイン・パスワード再設定・登録）の IP 単位の上限（1分あたり）。</summary>
    public int AnonymousPerMinute { get; set; } = 20;
}

/// <summary>
/// レート制限（RF-DES-001 13章 / 9.1）：/api はテナント単位 600 回/分、AI 生成系は 60 回/分、
/// ログイン前の POST（ログイン・パスワード再設定など）は IP 単位。超過時は 429 と Retry-After・RateLimit-* ヘッダを返す。
/// 複数インスタンスでは各インスタンスで数えるため、全体の上限は Front Door / API Management でも設ける。
/// </summary>
public static class ApiProtection
{
    public const string AiPolicy = "ai";

    public static IServiceCollection AddReachForgeRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        var o = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                var path = http.Request.Path;
                if (path.StartsWithSegments("/api"))
                {
                    var tenant = http.User.FindFirstValue(RfClaims.TenantId);
                    return tenant is null
                        ? Window($"ip:{Ip(http)}", o.AnonymousPerMinute * 5)
                        : Window($"tenant:{tenant}", o.TenantPerMinute);
                }
                if (path.StartsWithSegments("/account") && HttpMethods.IsPost(http.Request.Method))
                {
                    return Window($"account:{Ip(http)}", o.AnonymousPerMinute);
                }
                return RateLimitPartition.GetNoLimiter("none");
            });
            limiter.AddPolicy(AiPolicy, http => Window($"ai:{http.User.FindFirstValue(RfClaims.TenantId) ?? Ip(http)}", o.AiPerMinute));
            limiter.OnRejected = async (context, ct) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? (int)Math.Ceiling(after.TotalSeconds) : 60;
                var response = context.HttpContext.Response;
                response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                response.Headers["RateLimit-Remaining"] = "0";
                response.Headers["RateLimit-Reset"] = retryAfter.ToString(CultureInfo.InvariantCulture);
                if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
                {
                    await Results.Problem(statusCode: 429, title: "E-SYS-429",
                        detail: $"リクエストが多すぎます。{retryAfter}秒ほど待ってから再試行してください。").ExecuteAsync(context.HttpContext);
                }
                else
                {
                    response.ContentType = "text/plain; charset=utf-8";
                    await response.WriteAsync("操作が多すぎます。しばらく待ってからお試しください。", ct);
                }
            };
        });
        return services;

        static RateLimitPartition<string> Window(string key, int permits) =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, permits), Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
            });

        static string Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

/// <summary>
/// Idempotency-Key（RF-DES-001 13章：POST に対応、24時間保持）。同じ呼び出し元・同じキーの再送には最初の応答をそのまま返す。
/// 内容が違う再送は 422、処理中の再送は 409。5xx の応答は保存せず、再試行できるようにする。
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, TimeProvider clock, ILogger<IdempotencyMiddleware> log)
{
    public const string Header = "Idempotency-Key";
    public const int MaxStoredBytes = 1024 * 1024;

    /// <summary>DbContextOptions はスコープ付きのため、コンストラクタではなく要求ごとに受け取る。</summary>
    public async Task InvokeAsync(HttpContext http, DbContextOptions<ReachForgeDbContext> dbOptions)
    {
        if (!HttpMethods.IsPost(http.Request.Method) || !http.Request.Path.StartsWithSegments("/api/v1")
            || http.Request.Headers[Header].FirstOrDefault() is not { Length: > 0 } key)
        {
            await next(http);
            return;
        }
        if (key.Length > 128)
        {
            await Results.Problem(statusCode: 400, title: "E-SYS-400", detail: "Idempotency-Key は128文字以内にしてください。").ExecuteAsync(http);
            return;
        }
        if (!Guid.TryParse(http.User.FindFirstValue(RfClaims.TenantId), out var tenantId))
        {
            await next(http); // 未認証は後段で 401
            return;
        }

        var scope = $"{tenantId:N}:{http.User.FindFirstValue(ClaimTypes.NameIdentifier)}";
        http.Request.EnableBuffering();
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(http.Request.Body, http.RequestAborted));
        http.Request.Body.Position = 0;
        hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{http.Request.Path}{http.Request.QueryString}|{hash}")));

        await using var db = new ReachForgeDbContext(dbOptions, new MutableTenantContext { IsSystem = true, UserName = "idempotency" }, null, clock);
        var now = clock.GetUtcNow();
        var existing = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Scope == scope && r.Key == key, http.RequestAborted);
        if (existing is not null && now - existing.CreatedAt > IdempotencyRecord.Retention)
        {
            db.IdempotencyRecords.Remove(existing);
            await db.SaveChangesAsync(http.RequestAborted);
            existing = null;
        }
        if (existing is not null)
        {
            await ReplayAsync(http, existing, hash);
            return;
        }

        var record = new IdempotencyRecord { TenantId = tenantId, Scope = scope, Key = key, RequestHash = hash };
        db.IdempotencyRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException)
        {
            // 同時に同じキーで来た（一意制約）。それ以外の保存エラーはそのまま投げる
            db.ChangeTracker.Clear();
            var other = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Scope == scope && r.Key == key, http.RequestAborted);
            if (other is null) throw;
            await ReplayAsync(http, other, hash);
            return;
        }

        var original = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            await next(http);
        }
        finally
        {
            http.Response.Body = original;
        }
        buffer.Position = 0;
        await buffer.CopyToAsync(original, http.RequestAborted);

        if (http.Response.StatusCode >= 500 || buffer.Length > MaxStoredBytes)
        {
            db.IdempotencyRecords.Remove(record); // 失敗は保存しない（再試行できるように）
        }
        else
        {
            record.StatusCode = http.Response.StatusCode;
            record.ContentType = http.Response.ContentType;
            record.Location = http.Response.Headers.Location.FirstOrDefault();
            record.Body = buffer.ToArray();
        }
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            log.LogWarning(ex, "Failed to store idempotent response for {Key}", key);
        }
    }

    private static async Task ReplayAsync(HttpContext http, IdempotencyRecord record, string hash)
    {
        if (record.RequestHash != hash)
        {
            await Results.Problem(statusCode: 422, title: "E-SYS-422",
                detail: "同じ Idempotency-Key で、内容の違うリクエストが送られました。新しいキーを使ってください。").ExecuteAsync(http);
            return;
        }
        if (record.StatusCode == 0)
        {
            await Results.Problem(statusCode: 409, title: "E-SYS-409", detail: "同じリクエストを処理中です。しばらく待ってから確認してください。")
                .ExecuteAsync(http);
            return;
        }
        http.Response.StatusCode = record.StatusCode;
        if (record.ContentType is not null) http.Response.ContentType = record.ContentType;
        if (record.Location is not null) http.Response.Headers.Location = record.Location;
        http.Response.Headers["Idempotent-Replayed"] = "true";
        if (record.Body is { Length: > 0 } body) await http.Response.Body.WriteAsync(body, http.RequestAborted);
    }
}

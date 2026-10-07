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

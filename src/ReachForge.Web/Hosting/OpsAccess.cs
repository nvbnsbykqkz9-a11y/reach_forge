using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace ReachForge.Web.Hosting;

/// <summary>システム運用者（RF-DES-001 2章：サービス全体の監視・障害対応）。テナントのロールとは別に、メールアドレスで指定する。</summary>
public sealed class OpsOptions
{
    public const string SectionName = "Ops";

    public List<string> Operators { get; set; } = [];

    public bool IsOperator(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.FindFirstValue(ClaimTypes.Email) is { Length: > 0 } email
        && Operators.Contains(email, StringComparer.OrdinalIgnoreCase);
}

public static class OpsAccess
{
    /// <summary>運用管理画面（SCR-16）・ジョブ監視（/ops/jobs）の認可ポリシー。API キーでは入れない。</summary>
    public const string Policy = "Operator";

    public static IServiceCollection AddReachForgeOps(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(OpsOptions.SectionName).Get<OpsOptions>() ?? new OpsOptions();
        services.AddSingleton(options);
        services.AddAuthorizationBuilder().AddPolicy(Policy, p => p
            .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => options.IsOperator(ctx.User)));
        return services;
    }
}

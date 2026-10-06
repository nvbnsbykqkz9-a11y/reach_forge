using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ReachForge.Infrastructure.Identity;

namespace ReachForge.Web.Api;

/// <summary>Cookie を発行・破棄する操作（ログアウト・外部ログイン・ワークスペース切替）。フォーム POST＋偽造防止トークンで受ける。</summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account");

        account.MapPost("/logout", async (SignInManager<AppUser> signIn, AccountService accounts, UserManager<AppUser> users,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (await users.GetUserAsync(principal) is { } user) await accounts.AuditAsync(user, "auth.logout", null, ct);
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/account/login");
        });

        // 外部 ID プロバイダでのログイン開始
        account.MapPost("/external-login", (HttpContext http, SignInManager<AppUser> signIn, [FromForm] string provider,
            [FromForm] string? returnUrl) =>
        {
            var redirect = $"/account/external-callback?returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}";
            var properties = signIn.ConfigureExternalAuthenticationProperties(provider, redirect);
            return TypedResults.Challenge(properties, [provider]);
        });

        // 外部ログインの戻り先。既存の利用者（同じメールアドレス）にだけ紐づける。新規登録は招待またはメールで行う。
        account.MapGet("/external-callback", async (SignInManager<AppUser> signIn, UserManager<AppUser> users,
            AccountService accounts, string? returnUrl, CancellationToken ct) =>
        {
            var info = await signIn.GetExternalLoginInfoAsync();
            if (info is null) return Results.LocalRedirect("/account/login?error=external");

            var result = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: false);
            if (result.RequiresTwoFactor) return Results.LocalRedirect($"/account/login-2fa?returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}");
            if (result.IsLockedOut) return Results.LocalRedirect("/account/login?error=locked");
            if (!result.Succeeded)
            {
                var email = info.Principal.FindFirstValue(ClaimTypes.Email) ?? info.Principal.FindFirstValue("email");
                var emailVerified = info.Principal.FindFirstValue("email_verified") is not "false";
                var user = email is null ? null : await users.FindByEmailAsync(email);
                if (user is null || !emailVerified) return Results.LocalRedirect("/account/login?error=unregistered");
                await users.AddLoginAsync(user, info);
                result = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: false);
                if (result.RequiresTwoFactor) return Results.LocalRedirect("/account/login-2fa");
                if (!result.Succeeded) return Results.LocalRedirect("/account/login?error=external");
            }
            if (await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey) is { } signedIn)
            {
                await accounts.AuditAsync(signedIn, "auth.login", info.LoginProvider, ct);
            }
            return Results.LocalRedirect(SafeReturnUrl(returnUrl));
        });

        // ワークスペース切替（代理店：RF-UX-001 3.2）
        account.MapPost("/switch-workspace", async ([FromForm] Guid workspaceId, ClaimsPrincipal principal,
            UserManager<AppUser> users, SignInManager<AppUser> signIn, AccountService accounts, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.LocalRedirect("/account/login");
            var memberships = await accounts.MembershipsAsync(user.Id, ct);
            if (memberships.All(m => m.WorkspaceId != workspaceId)) return Results.Forbid();
            user.LastWorkspaceId = workspaceId;
            await users.UpdateAsync(user);
            await signIn.RefreshSignInAsync(user);
            return Results.LocalRedirect("/");
        }).RequireAuthorization();
    }

    /// <summary>オープンリダイレクト対策：アプリ内の相対パスのみ許可する。</summary>
    public static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";
}

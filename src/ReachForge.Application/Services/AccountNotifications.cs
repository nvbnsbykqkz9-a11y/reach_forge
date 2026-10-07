using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Enums;

namespace ReachForge.Application.Services;

/// <summary>
/// 認証まわりのお知らせメール（招待・パスワード再設定・ロック・パスワード変更）。
/// 本文に個人情報や秘密を含めない（リンクは1回限り・期限付き）。送信に失敗しても操作自体は止めない。
/// </summary>
public sealed class AccountNotifications(IEmailSender email, TimeProvider clock, ILogger<AccountNotifications> log)
{
    public Task InvitationAsync(string to, string workspaceName, string invitedBy, Role role, string link, DateTimeOffset expiresAt,
        CancellationToken ct) =>
        SendAsync(to, $"【ReachForge】{workspaceName} への招待", $"""
            {invitedBy} さんから、ReachForge のワークスペース「{workspaceName}」に{role.ToLabel()}として招待されました。

            次のリンクから参加してください（{expiresAt:yyyy/MM/dd HH:mm} UTC まで有効、1回だけ使えます）。
            {link}

            心当たりがない場合は、このメールを破棄してください。
            """, ct);

    public Task PasswordResetAsync(string to, string link, TimeSpan lifetime, CancellationToken ct) =>
        SendAsync(to, "【ReachForge】パスワードの再設定", $"""
            パスワードの再設定を受け付けました。次のリンクから新しいパスワードを設定してください（{(int)lifetime.TotalMinutes}分間有効、1回だけ使えます）。
            {link}

            心当たりがない場合は、このメールを破棄してください。パスワードは変わりません。
            """, ct);

    public Task PasswordChangedAsync(string to, CancellationToken ct) =>
        SendAsync(to, "【ReachForge】パスワードが変更されました", $"""
            {clock.GetUtcNow():yyyy/MM/dd HH:mm} UTC に、ReachForge のパスワードが変更されました。
            心当たりがない場合は、すぐに管理者に連絡してください。
            """, ct);

    public Task LockedOutAsync(string to, DateTimeOffset until, CancellationToken ct) =>
        SendAsync(to, "【ReachForge】ログインを一時的に停止しました", $"""
            パスワードを5回間違えたため、{until:yyyy/MM/dd HH:mm} UTC までログインできません。
            ご本人の操作でない場合は、パスワードを再設定し、管理者に連絡してください。
            """, ct);

    private async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        try
        {
            await email.SendAsync(new EmailMessage([to], subject, body), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Failed to send account email \"{Subject}\"", subject);
        }
    }
}

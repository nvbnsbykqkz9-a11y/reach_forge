namespace ReachForge.Application.Abstractions;

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string TextBody, IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>メール送信（レポート配信・招待・通知）。</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

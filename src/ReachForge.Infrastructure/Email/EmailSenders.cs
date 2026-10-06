using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;

namespace ReachForge.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";
    public string From { get; set; } = "ReachForge <no-reply@reachforge.invalid>";
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SmtpHost);
}

/// <summary>SMTP によるメール送信（Azure Communication Services の SMTP、SendGrid など）。</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        using var mail = new MailMessage { From = new MailAddress(o.From), Subject = message.Subject, Body = message.TextBody };
        foreach (var to in message.To) mail.To.Add(to);
        foreach (var a in message.Attachments ?? [])
        {
            mail.Attachments.Add(new Attachment(new MemoryStream(a.Content), a.FileName, a.ContentType));
        }
        using var client = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.EnableSsl };
        if (o.UserName is { Length: > 0 }) client.Credentials = new NetworkCredential(o.UserName, o.Password);
        await client.SendMailAsync(mail, ct);
        log.LogInformation("Sent email \"{Subject}\" to {Count} recipient(s)", message.Subject, message.To.Count);
    }
}

/// <summary>SMTP 未設定時（ローカル開発）：送信せずログに記録する。本文・宛先はログに出さない（個人情報）。</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> log) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        log.LogInformation("[Email not configured] \"{Subject}\" to {Count} recipient(s), {Attachments} attachment(s)",
            message.Subject, message.To.Count, message.Attachments?.Count ?? 0);
        return Task.CompletedTask;
    }
}

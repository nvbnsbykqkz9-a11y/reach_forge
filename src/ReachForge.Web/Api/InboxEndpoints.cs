using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Web.Api;

/// <summary>統合受信箱（F-09：GET /inbox、返信案・返信・分類の修正）。</summary>
public static class InboxEndpoints
{
    public sealed record ReplyBody(string Text);
    public sealed record LabelsBody(Sentiment Sentiment, InboxIntent Intent, Urgency Urgency, SensitiveTopic Sensitive);
    public sealed record AssignBody(string? Assignee);

    public static void MapInboxEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/inbox").RequireAuthorization();

        api.MapGet("", (InboxView? view, SocialPlatform[]? platform, bool? mine, InboxService s, CancellationToken ct) =>
            s.ListAsync(new InboxQuery(view ?? InboxView.All, platform, mine ?? false), ct));
        api.MapGet("/counts", (InboxService s, CancellationToken ct) => s.CountsAsync(ct));
        api.MapGet("/{id:guid}", async (Guid id, InboxService s, CancellationToken ct) =>
        {
            var d = await s.GetAsync(id, ct);
            return new { d.Message, d.ChannelName, d.OriginalPost, d.TypingOther, d.ReplyCostUsd,
                knowledge = d.Knowledge.Select(k => new { k.Entry.Id, k.Entry.Question, k.Score }) };
        });
        api.MapPost("/{id:guid}/replies:suggest", (Guid id, InboxService s, CancellationToken ct) => s.SuggestRepliesAsync(id, ct))
            .RequireRateLimiting(Hosting.ApiProtection.AiPolicy);
        api.MapPost("/{id:guid}:reply", async (Guid id, ReplyBody body, InboxService s, CancellationToken ct) =>
        {
            await s.ReplyAsync(id, body.Text, ct);
            return Results.NoContent();
        });
        api.MapPut("/{id:guid}/labels", async (Guid id, LabelsBody b, InboxService s, CancellationToken ct) =>
        {
            await s.CorrectLabelsAsync(id, b.Sentiment, b.Intent, b.Urgency, b.Sensitive, ct);
            return Results.NoContent();
        });
        api.MapPut("/{id:guid}/assignee", async (Guid id, AssignBody b, InboxService s, CancellationToken ct) =>
        {
            await s.AssignAsync(id, b.Assignee, ct);
            return Results.NoContent();
        });
        api.MapPost("/{id:guid}:hide", async (Guid id, InboxService s, CancellationToken ct) =>
        {
            await s.HideAsync(id, ct);
            return Results.NoContent();
        });
        api.MapGet("/alerts", (InboxService s, CancellationToken ct) => s.OpenAlertsAsync(ct));
    }
}

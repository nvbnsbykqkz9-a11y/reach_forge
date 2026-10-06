using ReachForge.Application.Services;

namespace ReachForge.Web.Api;

/// <summary>キャンペーン・A/B テスト（F-11：/campaigns、/campaigns/{id}/ab-tests）。</summary>
public static class CampaignEndpoints
{
    public sealed record StartAbTest(DateTimeOffset StartAt, DateTimeOffset? TimeB);

    public static void MapCampaignEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/campaigns").RequireAuthorization();
        api.MapGet("", (CampaignService s, CancellationToken ct) => s.ListAsync(ct));
        api.MapPost("", async (SaveCampaign body, CampaignService s, CancellationToken ct) =>
        {
            var c = await s.SaveAsync(body with { Id = null }, ct);
            return Results.Created($"/api/v1/campaigns/{c.Id}", c);
        });
        api.MapGet("/{id:guid}", (Guid id, CampaignService s, CancellationToken ct) => s.GetAsync(id, ct));
        api.MapPut("/{id:guid}", (Guid id, SaveCampaign body, CampaignService s, CancellationToken ct) => s.SaveAsync(body with { Id = id }, ct));
        api.MapDelete("/{id:guid}", async (Guid id, CampaignService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        api.MapGet("/{id:guid}/ab-tests", (Guid id, AbTestService s, CancellationToken ct) => s.ListAsync(id, ct));

        var ab = app.MapGroup("/api/v1/ab-tests").RequireAuthorization();
        ab.MapPost("", async (CreateAbTest body, AbTestService s, CancellationToken ct) =>
        {
            var t = await s.CreateAsync(body, ct);
            return Results.Created($"/api/v1/ab-tests/{t.Id}", t);
        });
        ab.MapGet("/{id:guid}", (Guid id, AbTestService s, CancellationToken ct) => s.GetAsync(id, ct));
        ab.MapPost("/{id:guid}:start", (Guid id, StartAbTest body, AbTestService s, CancellationToken ct) =>
            s.StartAsync(id, body.StartAt, body.TimeB, ct));
        ab.MapPost("/{id:guid}:cancel", async (Guid id, AbTestService s, CancellationToken ct) =>
        {
            await s.CancelAsync(id, ct);
            return Results.NoContent();
        });
    }
}

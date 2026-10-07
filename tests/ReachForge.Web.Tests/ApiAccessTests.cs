using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Services;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Web.Tests;

public class ApiAccessTests
{
    private static async Task<(string Secret, Guid Id)> CreateKeyAsync(WebFixture app, Role role)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current = new MutableTenantContext
        {
            TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, UserName = "田中", Role = Role.Owner,
        };
        var created = await scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync("連携テスト", role, null, CancellationToken.None);
        return (created.Secret, created.Key.Id);
    }

    private static HttpClient Client(WebFixture app, string? key)
    {
        var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (key is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    [Fact]
    public async Task Api_key_authenticates_with_its_role_and_can_be_revoked()
    {
        await using var app = new WebFixture();
        var (secret, id) = await CreateKeyAsync(app, Role.Viewer);

        var channels = await Client(app, secret).GetFromJsonAsync<JsonElement>("/api/v1/channels");
        Assert.Equal(4, channels.GetArrayLength());

        var alt = Client(app, null);
        alt.DefaultRequestHeaders.Add("X-Api-Key", secret);
        Assert.Equal(HttpStatusCode.OK, (await alt.GetAsync("/api/v1/channels")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(app, "rfk_wrong").GetAsync("/api/v1/channels")).StatusCode);

        // 閲覧者のキーでは生成できない
        var generate = await Client(app, secret).PostAsJsonAsync("/api/v1/ai/copies", new
        {
            workspaceId = DemoSeeder.WorkspaceId, objective = "Traffic", theme = "秋の新作",
        });
        Assert.Equal(HttpStatusCode.Forbidden, generate.StatusCode);

        // キーで画面（Cookie 前提）には入れない
        var page = await Client(app, secret).GetAsync("/calendar");
        Assert.True(HttpAssert.IsRedirect(page));

        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current = new MutableTenantContext
            {
                TenantId = DemoSeeder.TenantId, WorkspaceId = DemoSeeder.WorkspaceId, UserName = "田中", Role = Role.Owner,
            };
            await scope.ServiceProvider.GetRequiredService<ApiKeyService>().RevokeAsync(id, CancellationToken.None);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(app, secret).GetAsync("/api/v1/channels")).StatusCode);
    }

    [Fact]
    public async Task Ai_endpoints_are_rate_limited_per_tenant()
    {
        await using var app = new WebFixture(new() { ["RateLimits:AiPerMinute"] = "2" });
        var (secret, _) = await CreateKeyAsync(app, Role.Editor);
        var client = Client(app, secret);
        HttpResponseMessage last = null!;
        for (var i = 0; i < 3; i++)
        {
            last = await client.PostAsJsonAsync("/api/v1/ai/copies", new { workspaceId = DemoSeeder.WorkspaceId, objective = "Traffic", theme = "秋の新作" });
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.True(last.Headers.Contains("Retry-After"));
        Assert.Equal("E-SYS-429", (await last.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        // 生成以外の API は使える
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/channels")).StatusCode);
    }

    [Fact]
    public async Task Idempotency_key_replays_the_first_response()
    {
        await using var app = new WebFixture();
        var (secret, _) = await CreateKeyAsync(app, Role.Editor);
        var client = Client(app, secret);

        async Task<HttpResponseMessage> PostAsync(string title)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/posts")
            {
                Content = JsonContent.Create(new { title, objective = "Awareness", coreMessage = "本文です" }),
            };
            request.Headers.Add("Idempotency-Key", "order-123");
            return await client.SendAsync(request);
        }

        var first = await PostAsync("冪等テスト");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var second = await PostAsync("冪等テスト");
        Assert.Equal(firstBody, await second.Content.ReadAsStringAsync());
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));

        var conflict = await PostAsync("別の内容");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);

        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current = new MutableTenantContext { IsSystem = true };
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        Assert.Equal(1, db.MasterPosts.Count(p => p.Title == "冪等テスト"));
    }
}

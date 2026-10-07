using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Infrastructure.Persistence;

namespace ReachForge.Web.Tests;

public class InboxEndpointTests
{
    [Fact]
    public async Task Signed_instagram_webhook_lands_in_inbox()
    {
        await using var app = new WebFixture();
        var client = app.Browser();
        await WebFixture.LoginAsync(client, "owner@example.com");

        const string body = """
            {"object":"instagram","entry":[{"id":"demo-instagram","changes":[{"field":"comments",
              "value":{"id":"wh-c1","text":"日曜日は何時まで営業していますか？","from":{"id":"u9","username":"webhook_user"},"media":{"id":"m1"}}}]}]}
            """;
        var signed = new StringContent(body, Encoding.UTF8, "application/json");
        signed.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("meta-secret"), Encoding.UTF8.GetBytes(body))));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/webhooks/meta", signed)).StatusCode);

        // 取り込みは非同期（WebhookProcessor）
        InboxMessage? found = null;
        for (var i = 0; i < 50 && found is null; i++)
        {
            using var scope = app.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContextOverride>().Current = new MutableTenantContext { IsSystem = true };
            found = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().InboxMessages.FirstOrDefaultAsync(m => m.ExternalId == "wh-c1");
            if (found is null) await Task.Delay(100);
        }
        Assert.NotNull(found);
        Assert.Equal("@webhook_user", found.AuthorName);
        Assert.Equal(InboxIntent.Question, found.Intent);
    }
}

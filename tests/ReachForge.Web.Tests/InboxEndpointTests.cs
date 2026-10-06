using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        JsonElement? found = null;
        for (var i = 0; i < 50 && found is null; i++)
        {
            var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/inbox");
            found = list.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(m => m!.Value.GetProperty("externalId").GetString() == "wh-c1");
            if (found is null) await Task.Delay(100);
        }
        Assert.NotNull(found);
        Assert.Equal("@webhook_user", found.Value.GetProperty("authorName").GetString());
        Assert.Equal("Question", found.Value.GetProperty("intent").GetString());

        var id = found.Value.GetProperty("id").GetGuid();
        var drafts = await (await client.PostAsync($"/api/v1/inbox/{id}/replies:suggest", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(drafts.GetArrayLength() > 0);
        var reply = await client.PostAsJsonAsync($"/api/v1/inbox/{id}:reply", new { text = drafts[0].GetProperty("text").GetString() });
        Assert.Equal(HttpStatusCode.NoContent, reply.StatusCode);
    }
}

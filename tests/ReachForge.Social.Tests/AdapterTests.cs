using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Social.Line;
using ReachForge.Social.Meta;
using ReachForge.Social.Threads;
using ReachForge.Social.Webhooks;
using ReachForge.Social.X;

namespace ReachForge.Social.Tests;

public class AdapterTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));

    private static PostVariant Variant(SocialPlatform platform, string body, params string[] tags)
    {
        var master = new MasterPost { Title = "t", CoreMessage = body };
        var channel = new Channel { Platform = platform, ExternalAccountId = "acct", DisplayName = "d", CredentialSecretRef = "db://x" };
        var v = PostVariant.Create(master, channel, body, tags);
        v.Schedule(Clock.GetUtcNow().AddHours(1), Clock.GetUtcNow(), requiresApproval: false);
        return v;
    }

    private static ChannelCredential Credential(SocialPlatform p, string account = "acct", StoredToken? token = null) =>
        new(Guid.NewGuid(), p, account, token ?? new StoredToken("access-token"));

    // ---------- X ----------

    [Fact]
    public void X_authorization_url_uses_pkce_s256_and_offline_access()
    {
        var url = new XConnector(new FakeHttp(), FakeHttp.Options(), Clock)
            .BuildAuthorizationUrl(SocialPlatform.X, "st", "challenge", "https://app/cb");
        Assert.StartsWith("https://x.com/i/oauth2/authorize?", url);
        Assert.Contains("code_challenge=challenge", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("offline.access", Uri.UnescapeDataString(url));
        Assert.Contains("state=st", url);
    }

    [Fact]
    public async Task X_exchange_uses_basic_auth_and_verifier_then_reads_profile()
    {
        var http = new FakeHttp()
            .Respond("2/oauth2/token", """{"access_token":"at","refresh_token":"rt","expires_in":7200,"token_type":"bearer"}""")
            .Respond("2/users/me", """{"data":{"id":"123","name":"Cafe","username":"hokkori"}}""");
        var accounts = await new XConnector(http, FakeHttp.Options(), Clock)
            .ExchangeAsync(SocialPlatform.X, "code", "verifier", "https://app/cb", CancellationToken.None);

        var token = http.Requests[0];
        Assert.Equal(HttpMethod.Post, token.Method);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-client:x-secret")), token.Headers["Authorization"]);
        Assert.Contains("code_verifier=verifier", token.Body);
        Assert.Contains("grant_type=authorization_code", token.Body);
        Assert.Equal("Bearer at", http.Requests[1].Headers["Authorization"]);

        var a = Assert.Single(accounts);
        Assert.Equal(("123", "@hokkori"), (a.ExternalAccountId, a.DisplayName));
        Assert.Equal("rt", a.Token.RefreshToken);
        Assert.Equal(Clock.GetUtcNow().AddHours(2), a.Token.ExpiresAt);
    }

    [Fact]
    public async Task X_refresh_failure_requires_reauth()
    {
        var http = new FakeHttp().Respond("2/oauth2/token", """{"error":"invalid_request","error_description":"Value passed for the token was invalid."}""",
            HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<SocialApiException>(() => new XConnector(http, FakeHttp.Options(), Clock)
            .RefreshAsync(SocialPlatform.X, new StoredToken("at", "rt"), CancellationToken.None));
        Assert.True(ex.RequiresReauth);
    }

    [Fact]
    public async Task X_publish_posts_text_with_bearer_and_builds_url()
    {
        var http = new FakeHttp().Respond("2/tweets", """{"data":{"id":"999","text":"..."}}""", HttpStatusCode.Created);
        var result = await new XPublisher(http).PublishAsync(Variant(SocialPlatform.X, "秋の新作", "秋限定"),
            Credential(SocialPlatform.X, token: new StoredToken("at").With("username", "hokkori")), [], CancellationToken.None);

        Assert.Equal("Bearer at", http.Requests[0].Headers["Authorization"]);
        Assert.Equal("秋の新作\n\n#秋限定",
            System.Text.Json.Nodes.JsonNode.Parse(http.Requests[0].Body!)!["text"]!.GetValue<string>());
        Assert.Equal("https://x.com/hokkori/status/999", result.Url);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, false)]
    [InlineData(HttpStatusCode.Forbidden, false, false)]
    public async Task Errors_are_classified(HttpStatusCode status, bool transient, bool reauth)
    {
        var http = new FakeHttp().Respond("2/tweets", """{"title":"Error","detail":"something"}""", status);
        var ex = await Assert.ThrowsAsync<SocialApiException>(() =>
            new XPublisher(http).PublishAsync(Variant(SocialPlatform.X, "本文"), Credential(SocialPlatform.X), [], CancellationToken.None));
        Assert.Equal(transient, ex.IsTransient);
        Assert.Equal(reauth, ex.RequiresReauth);
    }

    // ---------- Meta ----------

    [Fact]
    public async Task Meta_exchange_returns_pages_and_instagram_accounts_with_page_tokens()
    {
        var http = new FakeHttp()
            .Respond("v24.0/oauth/access_token?client_id", """{"access_token":"short"}""")
            .Respond("fb_exchange_token", """{"access_token":"long","expires_in":5184000}""")
            .Respond("v24.0/me/accounts", """
                {"data":[
                  {"id":"p1","name":"ほっこりカフェ","access_token":"pt1","instagram_business_account":{"id":"ig1","username":"hokkori_cafe"}},
                  {"id":"p2","name":"2号店","access_token":"pt2"}]}
                """);
        var accounts = await new MetaConnector(http, FakeHttp.Options(), Clock)
            .ExchangeAsync(SocialPlatform.Instagram, "code", "v", "https://app/cb", CancellationToken.None);

        Assert.Equal("Bearer long", http.Requests[2].Headers["Authorization"]);
        Assert.Equal(3, accounts.Count);
        var ig = Assert.Single(accounts, a => a.Platform == SocialPlatform.Instagram);
        Assert.Equal(("ig1", "@hokkori_cafe", "pt1"), (ig.ExternalAccountId, ig.DisplayName, ig.Token.AccessToken));
        Assert.Null(ig.Token.ExpiresAt); // ページトークンは失効しない
    }

    [Fact]
    public async Task Facebook_publish_sends_message_and_link_to_page_feed()
    {
        var http = new FakeHttp().Respond("v24.0/page-1/feed", """{"id":"page-1_42"}""");
        var result = await new FacebookPublisher(http, FakeHttp.Options()).PublishAsync(
            Variant(SocialPlatform.Facebook, "新作です https://example.com/menu"), Credential(SocialPlatform.Facebook, "page-1"), [],
            CancellationToken.None);
        Assert.Contains("link=https%3A%2F%2Fexample.com%2Fmenu", http.Requests[0].Body);
        Assert.Equal("https://www.facebook.com/page-1_42", result.Url);
    }

    [Fact]
    public async Task Meta_token_error_190_requires_reauth()
    {
        var http = new FakeHttp().Respond("feed", """{"error":{"message":"Error validating access token","type":"OAuthException","code":190}}""",
            HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<SocialApiException>(() => new FacebookPublisher(http, FakeHttp.Options())
            .PublishAsync(Variant(SocialPlatform.Facebook, "本文"), Credential(SocialPlatform.Facebook), [], CancellationToken.None));
        Assert.True(ex.RequiresReauth);
    }

    [Fact]
    public async Task Instagram_requires_media()
    {
        var publisher = new InstagramPublisher(new FakeHttp(), FakeHttp.Options());
        var validation = await publisher.ValidateAsync(Variant(SocialPlatform.Instagram, "本文"), CancellationToken.None);
        Assert.Contains(validation.Errors, e => e.Contains("画像"));
    }

    // ---------- Threads ----------

    [Fact]
    public async Task Threads_creates_container_then_publishes()
    {
        var http = new FakeHttp()
            .Respond("v1.0/th-1/threads", """{"id":"container"}""")
            .Respond("v1.0/th-1/threads_publish", """{"id":"post-1"}""")
            .Respond("v1.0/post-1?fields=permalink", """{"permalink":"https://www.threads.net/@hokkori/post/abc"}""");
        var result = await new ThreadsPublisher(http, FakeHttp.Options()).PublishAsync(
            Variant(SocialPlatform.Threads, "秋の新作、飲みました？", "秋限定"), Credential(SocialPlatform.Threads, "th-1"), [], CancellationToken.None);

        Assert.Contains("media_type=TEXT", http.Requests[0].Body);
        Assert.Contains("creation_id=container", http.Requests[1].Body);
        Assert.Equal(("post-1", "https://www.threads.net/@hokkori/post/abc"), (result.ExternalPostId, result.Url));
    }

    [Fact]
    public async Task Threads_exchange_upgrades_to_long_lived_token()
    {
        var http = new FakeHttp()
            .Respond("oauth/access_token", """{"access_token":"short","user_id":"th-1"}""")
            .Respond("access_token?grant_type=th_exchange_token", """{"access_token":"long","token_type":"bearer","expires_in":5183944}""")
            .Respond("v1.0/me", """{"id":"th-1","username":"hokkori"}""");
        var a = Assert.Single(await new ThreadsConnector(http, FakeHttp.Options(), Clock)
            .ExchangeAsync(SocialPlatform.Threads, "code", "v", "https://app/cb", CancellationToken.None));
        Assert.Equal("long", a.Token.AccessToken);
        Assert.Equal(Clock.GetUtcNow().AddSeconds(5183944), a.Token.ExpiresAt);
    }

    // ---------- LINE ----------

    [Fact]
    public async Task Line_connect_issues_stateless_token_and_reads_bot_info()
    {
        var http = new FakeHttp()
            .Respond("oauth2/v3/token", """{"access_token":"line-token","expires_in":900,"token_type":"Bearer"}""")
            .Respond("v2/bot/info", """{"userId":"Uabc","basicId":"@123abcd","displayName":"ほっこりカフェ"}""");
        var a = Assert.Single(await new LineConnector(http, FakeHttp.Options(), Clock).ConnectAsync(SocialPlatform.Line,
            new Dictionary<string, string> { ["channelId"] = "200000", ["channelSecret"] = "s3cret" }, CancellationToken.None));

        Assert.Contains("grant_type=client_credentials", http.Requests[0].Body);
        Assert.Equal(("Uabc", "ほっこりカフェ"), (a.ExternalAccountId, a.DisplayName));
        Assert.Equal("s3cret", a.Token.Get("channelSecret")); // 再発行と Webhook 署名検証に使う
    }

    [Fact]
    public async Task Line_wrong_credentials_give_friendly_error()
    {
        var http = new FakeHttp().Respond("oauth2/v3/token", """{"error":"invalid_client"}""", HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<DomainException>(() => new LineConnector(http, FakeHttp.Options(), Clock).ConnectAsync(
            SocialPlatform.Line, new Dictionary<string, string> { ["channelId"] = "1", ["channelSecret"] = "x" }, CancellationToken.None));
        Assert.Contains("チャネルID", ex.Message);
    }

    [Fact]
    public async Task Line_broadcast_uses_deterministic_retry_key_and_treats_409_as_accepted()
    {
        var variant = Variant(SocialPlatform.Line, "週末クーポン配信中");
        var http = new FakeHttp()
            .Respond("v2/bot/message/broadcast", "{}")
            .Respond("v2/bot/message/broadcast", """{"message":"The retry key is already accepted"}""", HttpStatusCode.Conflict);
        var publisher = new LinePublisher(http);

        var first = await publisher.PublishAsync(variant, Credential(SocialPlatform.Line), [], CancellationToken.None);
        var second = await publisher.PublishAsync(variant, Credential(SocialPlatform.Line), [], CancellationToken.None);

        Assert.Equal(http.Requests[0].Headers["X-Line-Retry-Key"], http.Requests[1].Headers["X-Line-Retry-Key"]);
        Assert.True(Guid.TryParse(http.Requests[0].Headers["X-Line-Retry-Key"], out _));
        Assert.Equal(first.ExternalPostId, second.ExternalPostId);
        Assert.Contains("\"type\":\"text\"", http.Requests[0].Body);
    }

    // ---------- 画像付き投稿 ----------

    private static PublishMedia Image(string url = "https://cdn.example/img.jpg", string? alt = "湯気の立つラテ") =>
        new(Guid.NewGuid(), "image/jpeg", alt, true, _ => Task.FromResult(new byte[] { 0xFF, 0xD8, 0xFF }),
            _ => Task.FromResult(url), _ => Task.FromResult(url + "?thumb"));

    [Fact]
    public async Task X_uploads_media_sets_alt_text_and_attaches_ids()
    {
        var http = new FakeHttp()
            .Respond("2/media/upload", """{"data":{"id":"m1","media_key":"3_m1"}}""")
            .Respond("2/media/metadata", "{}")
            .Respond("2/tweets", """{"data":{"id":"t1"}}""");
        await new XPublisher(http).PublishAsync(Variant(SocialPlatform.X, "秋の新作"), Credential(SocialPlatform.X), [Image()],
            CancellationToken.None);

        Assert.Contains("tweet_image", http.Requests[0].Body);
        Assert.Contains("alt_text", http.Requests[1].Body);
        var tweet = System.Text.Json.Nodes.JsonNode.Parse(http.Requests[2].Body!)!;
        Assert.Equal("m1", tweet["media"]!["media_ids"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task X_uploads_video_in_chunks_and_waits_for_processing()
    {
        XPublisher.PollScale = 0;
        var size = XPublisher.ChunkBytes * 2 + 100;
        var video = new PublishMedia(Guid.NewGuid(), "video/mp4", null, true, _ => Task.FromResult(new byte[size]),
            _ => Task.FromResult("https://cdn.example/v.mp4"), _ => Task.FromResult("https://cdn.example/v.jpg"));
        var http = new FakeHttp()
            .Respond("2/media/upload/initialize", """{"data":{"id":"v1","media_key":"7_v1"}}""")
            .Respond("2/media/upload/v1/append", "{}")
            .Respond("2/media/upload/v1/append", "{}")
            .Respond("2/media/upload/v1/append", "{}")
            .Respond("2/media/upload/v1/finalize", """{"data":{"id":"v1","processing_info":{"state":"pending","check_after_secs":1}}}""")
            .Respond("2/media/upload?command=STATUS&media_id=v1", """{"data":{"id":"v1","processing_info":{"state":"in_progress","check_after_secs":1}}}""")
            .Respond("2/media/upload?command=STATUS&media_id=v1", """{"data":{"id":"v1","processing_info":{"state":"succeeded"}}}""")
            .Respond("2/tweets", """{"data":{"id":"t2"}}""");
        await new XPublisher(http).PublishAsync(Variant(SocialPlatform.X, "新作の動画"), Credential(SocialPlatform.X), [video, Image()],
            CancellationToken.None);

        var init = System.Text.Json.Nodes.JsonNode.Parse(http.Requests[0].Body!)!;
        Assert.Equal(("video/mp4", size, "tweet_video"),
            (init["media_type"]!.GetValue<string>(), init["total_bytes"]!.GetValue<int>(), init["media_category"]!.GetValue<string>()));
        Assert.Contains("name=segment_index", http.Requests[3].Body);
        var tweet = System.Text.Json.Nodes.JsonNode.Parse(http.Requests[^1].Body!)!;
        Assert.Equal(["v1"], tweet["media"]!["media_ids"]!.AsArray().Select(x => x!.GetValue<string>())); // 画像は混ぜない
    }

    [Fact]
    public async Task X_reports_failed_video_processing_as_permanent()
    {
        XPublisher.PollScale = 0;
        var video = new PublishMedia(Guid.NewGuid(), "video/mp4", null, true, _ => Task.FromResult(new byte[10]),
            _ => Task.FromResult("u"), _ => Task.FromResult("p"));
        var http = new FakeHttp()
            .Respond("initialize", """{"data":{"id":"v2"}}""")
            .Respond("append", "{}")
            .Respond("finalize", """{"data":{"id":"v2","processing_info":{"state":"failed","error":{"message":"InvalidMedia"}}}}""");
        var ex = await Assert.ThrowsAsync<SocialApiException>(() => new XPublisher(http).PublishAsync(Variant(SocialPlatform.X, "動画"),
            Credential(SocialPlatform.X), [video], CancellationToken.None));
        Assert.False(ex.IsTransient);
        Assert.Contains("InvalidMedia", ex.Message);
    }

    [Fact]
    public async Task Instagram_publishes_image_container_with_public_url()
    {
        var http = new FakeHttp()
            .Respond("v24.0/ig1/media", """{"id":"c1"}""")
            .Respond("v24.0/c1?fields=status_code", """{"status_code":"FINISHED"}""")
            .Respond("v24.0/ig1/media_publish", """{"id":"p1"}""")
            .Respond("v24.0/p1?fields=permalink", """{"permalink":"https://www.instagram.com/p/abc/"}""");
        var v = Variant(SocialPlatform.Instagram, "秋の新作", "秋限定");
        v.SetMedia([Guid.NewGuid()], requiresApproval: false);
        var result = await new InstagramPublisher(http, FakeHttp.Options()).PublishAsync(v, Credential(SocialPlatform.Instagram, "ig1"),
            [Image("https://cdn.example/ig.jpg")], CancellationToken.None);

        Assert.Contains("image_url=https%3A%2F%2Fcdn.example%2Fig.jpg", http.Requests[0].Body);
        Assert.Equal("https://www.instagram.com/p/abc/", result.Url);
    }

    [Fact]
    public async Task Instagram_threads_and_facebook_publish_multiple_images_as_one_post()
    {
        var ig = new FakeHttp()
            .Respond("v24.0/ig1/media", """{"id":"k1"}""").Respond("v24.0/k1?fields=status_code", """{"status_code":"FINISHED"}""")
            .Respond("v24.0/ig1/media", """{"id":"k2"}""").Respond("v24.0/k2?fields=status_code", """{"status_code":"FINISHED"}""")
            .Respond("v24.0/ig1/media", """{"id":"parent"}""").Respond("v24.0/parent?fields=status_code", """{"status_code":"FINISHED"}""")
            .Respond("v24.0/ig1/media_publish", """{"id":"p9"}""")
            .Respond("v24.0/p9?fields=permalink", """{"permalink":"https://www.instagram.com/p/xyz/"}""");
        var v = Variant(SocialPlatform.Instagram, "秋の新作", "秋限定");
        await new InstagramPublisher(ig, FakeHttp.Options()).PublishAsync(v, Credential(SocialPlatform.Instagram, "ig1"),
            [Image("https://cdn.example/1.jpg"), Image("https://cdn.example/2.jpg")], CancellationToken.None);
        Assert.Contains("is_carousel_item=true", ig.Requests[0].Body);
        Assert.Contains("media_type=CAROUSEL", ig.Requests[4].Body);
        Assert.Contains("children=k1%2Ck2", ig.Requests[4].Body);
        Assert.Contains("creation_id=parent", ig.Requests[6].Body);

        var th = new FakeHttp()
            .Respond("v1.0/u1/threads", """{"id":"t1"}""").Respond("v1.0/u1/threads", """{"id":"t2"}""")
            .Respond("v1.0/u1/threads", """{"id":"tp"}""").Respond("v1.0/u1/threads_publish", """{"id":"tx"}""")
            .Respond("v1.0/tx?fields=permalink", """{"permalink":"https://www.threads.net/@a/post/1"}""");
        await new ThreadsPublisher(th, FakeHttp.Options()).PublishAsync(Variant(SocialPlatform.Threads, "本文"),
            Credential(SocialPlatform.Threads, "u1"), [Image(), Image()], CancellationToken.None);
        Assert.Contains("media_type=CAROUSEL", th.Requests[2].Body);
        Assert.Contains("children=t1%2Ct2", th.Requests[2].Body);

        var fb = new FakeHttp()
            .Respond("v24.0/page-1/photos", """{"id":"f1"}""").Respond("v24.0/page-1/photos", """{"id":"f2"}""")
            .Respond("v24.0/page-1/feed", """{"id":"page-1_77"}""");
        var r = await new FacebookPublisher(fb, FakeHttp.Options()).PublishAsync(Variant(SocialPlatform.Facebook, "本文"),
            Credential(SocialPlatform.Facebook, "page-1"), [Image(), Image()], CancellationToken.None);
        Assert.Contains("published=false", fb.Requests[0].Body);
        Assert.Contains("media_fbid", Uri.UnescapeDataString(fb.Requests[2].Body!));
        Assert.Equal("page-1_77", r.ExternalPostId);
    }

    [Fact]
    public async Task Facebook_threads_and_line_attach_images()
    {
        var fb = new FakeHttp().Respond("v24.0/page-1/photos", """{"id":"ph1","post_id":"page-1_9"}""");
        var r = await new FacebookPublisher(fb, FakeHttp.Options()).PublishAsync(Variant(SocialPlatform.Facebook, "本文"),
            Credential(SocialPlatform.Facebook, "page-1"), [Image()], CancellationToken.None);
        Assert.Equal("page-1_9", r.ExternalPostId);
        Assert.Contains("url=https%3A%2F%2Fcdn.example", fb.Requests[0].Body);

        var th = new FakeHttp()
            .Respond("threads", """{"id":"c"}""").Respond("threads_publish", """{"id":"p"}""").Respond("p?fields=permalink", "{}");
        await new ThreadsPublisher(th, FakeHttp.Options()).PublishAsync(Variant(SocialPlatform.Threads, "本文"),
            Credential(SocialPlatform.Threads, "th-1"), [Image()], CancellationToken.None);
        Assert.Contains("media_type=IMAGE", th.Requests[0].Body);

        var line = new FakeHttp().Respond("v2/bot/message/broadcast", "{}");
        await new LinePublisher(line).PublishAsync(Variant(SocialPlatform.Line, "本文"), Credential(SocialPlatform.Line), [Image()],
            CancellationToken.None);
        var messages = System.Text.Json.Nodes.JsonNode.Parse(line.Requests[0].Body!)!["messages"]!.AsArray();
        Assert.Equal("image", messages[1]!["type"]!.GetValue<string>());
        Assert.EndsWith("?thumb", messages[1]!["previewImageUrl"]!.GetValue<string>());
    }

    // ---------- Webhook ----------

    [Fact]
    public void Webhook_signatures_are_verified()
    {
        var body = Encoding.UTF8.GetBytes("""{"object":"page"}""");
        var meta = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("meta-secret"), body));
        Assert.True(WebhookSignature.VerifyMeta(body, meta, "meta-secret"));
        Assert.False(WebhookSignature.VerifyMeta(body, meta, "other"));
        Assert.False(WebhookSignature.VerifyMeta(body, "sha256=zz", "meta-secret"));
        Assert.False(WebhookSignature.VerifyMeta(body, null, "meta-secret"));

        var line = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("ch-secret"), body));
        Assert.True(WebhookSignature.VerifyLine(body, line, "ch-secret"));
        Assert.False(WebhookSignature.VerifyLine(Encoding.UTF8.GetBytes("tampered"), line, "ch-secret"));
    }
}

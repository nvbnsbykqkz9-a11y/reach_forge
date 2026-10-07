using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;
using ReachForge.Social.TikTok;
using ReachForge.Social.YouTube;

namespace ReachForge.Social.Tests;

public class TikTokYouTubeTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));

    private static PostVariant Variant(SocialPlatform platform, string body, string? title = null, params string[] tags)
    {
        var master = new MasterPost { Title = "t", CoreMessage = body };
        var channel = new Channel { Platform = platform, ExternalAccountId = "acct", DisplayName = "d", CredentialSecretRef = "db://x" };
        return PostVariant.Create(master, channel, body, tags, title);
    }

    private static ChannelCredential Credential(SocialPlatform p, StoredToken? token = null) =>
        new(Guid.NewGuid(), p, "acct", token ?? new StoredToken("access-token"));

    private static PublishMedia Video(int size, bool ai = false, string? srt = null) =>
        new(Guid.NewGuid(), "video/mp4", null, ai, _ => Task.FromResult(new byte[size]),
            _ => Task.FromResult("https://cdn.example/v.mp4"), _ => Task.FromResult("https://cdn.example/v.jpg"), srt);

    private static PublishMedia Image(string url = "https://cdn.example/a.jpg") =>
        new(Guid.NewGuid(), "image/jpeg", "alt", false, _ => Task.FromResult(new byte[3]), _ => Task.FromResult(url), _ => Task.FromResult(url));

    private static JsonNode Json(RecordedRequest r) => JsonNode.Parse(r.Body!)!;

    // ---------- TikTok ----------

    [Fact]
    public void TikTok_web_authorization_url_has_client_key_and_no_pkce()
    {
        var url = new TikTokConnector(new FakeHttp(), FakeHttp.Options(), Clock)
            .BuildAuthorizationUrl(SocialPlatform.TikTok, "st", "challenge", "https://app/cb");
        Assert.StartsWith("https://www.tiktok.com/v2/auth/authorize/?", url);
        Assert.Contains("client_key=tt-key", url);
        Assert.Contains("video.publish", Uri.UnescapeDataString(url));
        Assert.DoesNotContain("code_challenge", url);
    }

    [Fact]
    public void TikTok_desktop_pkce_uses_hex_sha256()
    {
        // RFC 7636 の例：verifier "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk" の S256 チャレンジ
        const string challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
        var url = new TikTokConnector(new FakeHttp(), FakeHttp.Options(o => o.TikTok.Desktop = true), Clock)
            .BuildAuthorizationUrl(SocialPlatform.TikTok, "st", challenge, "http://127.0.0.1:5245/cb");
        Assert.Contains("code_challenge=13d31e961a1ad8ec2f16b10c4c982e0876a878ad6df144566ee1894acb70f9c3", url);
        Assert.Contains("code_challenge_method=S256", url);
    }

    [Fact]
    public async Task TikTok_exchange_reads_open_id_and_profile()
    {
        var http = new FakeHttp()
            .Respond("v2/oauth/token/", """{"access_token":"act","expires_in":86400,"open_id":"oid-1","refresh_token":"rft","refresh_expires_in":31536000,"scope":"video.publish","token_type":"Bearer"}""")
            .Respond("v2/user/info/", """{"data":{"user":{"open_id":"oid-1","display_name":"ほっこり","username":"hokkori","avatar_url":"https://p/a.jpg"}},"error":{"code":"ok","message":""}}""");
        var a = Assert.Single(await new TikTokConnector(http, FakeHttp.Options(), Clock)
            .ExchangeAsync(SocialPlatform.TikTok, "code", "verifier", "https://app/cb", CancellationToken.None));

        Assert.Contains("client_key=tt-key", http.Requests[0].Body);
        Assert.Contains("client_secret=tt-secret", http.Requests[0].Body);
        Assert.DoesNotContain("code_verifier", http.Requests[0].Body);
        Assert.Equal("Bearer act", http.Requests[1].Headers["Authorization"]);
        Assert.Equal(("oid-1", "@hokkori"), (a.ExternalAccountId, a.DisplayName));
        Assert.Equal("rft", a.Token.RefreshToken);
        Assert.Equal(Clock.GetUtcNow().AddDays(1), a.Token.ExpiresAt);
        Assert.Equal("hokkori", a.Token.Get("username"));
    }

    [Fact]
    public async Task TikTok_token_error_in_ok_response_requires_reauth()
    {
        var http = new FakeHttp().Respond("v2/oauth/token/", """{"error":"invalid_grant","error_description":"Refresh token is invalid or expired."}""");
        var ex = await Assert.ThrowsAsync<SocialApiException>(() => new TikTokConnector(http, FakeHttp.Options(), Clock)
            .RefreshAsync(SocialPlatform.TikTok, new StoredToken("old", "rt"), CancellationToken.None));
        Assert.True(ex.RequiresReauth);
        Assert.Contains("expired", ex.Message);
    }

    [Fact]
    public async Task TikTok_uploads_video_in_chunks_and_waits_for_publish()
    {
        TikTokPublisher.PollScale = 0;
        var size = TikTokPublisher.ChunkBytes * 2 + 123; // 端数は最後の1回にまとめる → 2回
        var http = new FakeHttp()
            .Respond("v2/post/publish/creator_info/query/", """{"data":{"privacy_level_options":["PUBLIC_TO_EVERYONE","SELF_ONLY"],"comment_disabled":false,"duet_disabled":true,"stitch_disabled":false}}""")
            .Respond("v2/post/publish/video/init/", """{"data":{"publish_id":"v_pub_1","upload_url":"https://open-upload.tiktokapis.com/video/?upload_id=1"}}""")
            .Respond("open-upload.tiktokapis.com", "", HttpStatusCode.PartialContent)
            .Respond("open-upload.tiktokapis.com", "", HttpStatusCode.Created)
            .Respond("v2/post/publish/status/fetch/", """{"data":{"status":"PROCESSING_UPLOAD"}}""")
            .Respond("v2/post/publish/status/fetch/", """{"data":{"status":"PUBLISH_COMPLETE","publicaly_available_post_id":[7300000000000000001]}}""");
        var token = new StoredToken("act").With("username", "hokkori");
        var result = await new TikTokPublisher(http, FakeHttp.Options()).PublishAsync(
            Variant(SocialPlatform.TikTok, "秋の新作ラテ", null, "カフェ"), Credential(SocialPlatform.TikTok, token), [Video(size, ai: true)],
            CancellationToken.None);

        var init = Json(http.Requests[1]);
        Assert.Equal("PUBLIC_TO_EVERYONE", init["post_info"]!["privacy_level"]!.GetValue<string>());
        Assert.True(init["post_info"]!["is_aigc"]!.GetValue<bool>());
        Assert.True(init["post_info"]!["disable_duet"]!.GetValue<bool>());
        Assert.Contains("#カフェ", init["post_info"]!["title"]!.GetValue<string>());
        Assert.Equal((size, TikTokPublisher.ChunkBytes, 2), (init["source_info"]!["video_size"]!.GetValue<int>(),
            init["source_info"]!["chunk_size"]!.GetValue<int>(), init["source_info"]!["total_chunk_count"]!.GetValue<int>()));
        Assert.Equal($"bytes 0-{TikTokPublisher.ChunkBytes - 1}/{size}", http.Requests[2].Headers["Content-Range"]);
        Assert.Equal($"bytes {TikTokPublisher.ChunkBytes}-{size - 1}/{size}", http.Requests[3].Headers["Content-Range"]);
        Assert.Equal(HttpMethod.Put, http.Requests[2].Method);
        Assert.Equal("v_pub_1", Json(http.Requests[4])["publish_id"]!.GetValue<string>());
        Assert.Equal(("7300000000000000001", "https://www.tiktok.com/@hokkori/video/7300000000000000001"), (result.ExternalPostId, result.Url));
    }

    [Fact]
    public async Task TikTok_unaudited_app_posts_self_only()
    {
        TikTokPublisher.PollScale = 0;
        var http = new FakeHttp()
            .Respond("creator_info", """{"data":{"privacy_level_options":["PUBLIC_TO_EVERYONE","SELF_ONLY"]}}""")
            .Respond("video/init", """{"data":{"publish_id":"p2","upload_url":"https://open-upload.tiktokapis.com/u"}}""")
            .Respond("open-upload", "", HttpStatusCode.Created)
            .Respond("status/fetch", """{"data":{"status":"PUBLISH_COMPLETE"}}""");
        var v = Variant(SocialPlatform.TikTok, "動画");
        v.SetPlatformOption(PlatformOptionKeys.TikTokPrivacy, "PUBLIC_TO_EVERYONE", requiresApproval: false);
        var result = await new TikTokPublisher(http, FakeHttp.Options(o => o.TikTok.Audited = false))
            .PublishAsync(v, Credential(SocialPlatform.TikTok), [Video(100)], CancellationToken.None);

        Assert.Equal("SELF_ONLY", Json(http.Requests[1])["post_info"]!["privacy_level"]!.GetValue<string>());
        Assert.Equal("bytes 0-99/100", http.Requests[2].Headers["Content-Range"]); // 5MB 未満は1回で送る
        Assert.Equal("p2", result.ExternalPostId); // 「自分のみ」は投稿 ID が返らない
    }

    [Fact]
    public async Task TikTok_photos_are_pulled_from_public_urls()
    {
        TikTokPublisher.PollScale = 0;
        var http = new FakeHttp()
            .Respond("creator_info", """{"data":{"privacy_level_options":["PUBLIC_TO_EVERYONE"]}}""")
            .Respond("v2/post/publish/content/init/", """{"data":{"publish_id":"p3"}}""")
            .Respond("status/fetch", """{"data":{"status":"PUBLISH_COMPLETE","publicaly_available_post_id":[42]}}""");
        await new TikTokPublisher(http, FakeHttp.Options()).PublishAsync(Variant(SocialPlatform.TikTok, "1行目\n2行目"),
            Credential(SocialPlatform.TikTok), [Image("https://cdn/1.jpg"), Image("https://cdn/2.jpg")], CancellationToken.None);

        var init = Json(http.Requests[1]);
        Assert.Equal(("PHOTO", "DIRECT_POST", "PULL_FROM_URL"), (init["media_type"]!.GetValue<string>(), init["post_mode"]!.GetValue<string>(),
            init["source_info"]!["source"]!.GetValue<string>()));
        Assert.Equal(["https://cdn/1.jpg", "https://cdn/2.jpg"], init["source_info"]!["photo_images"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("1行目", init["post_info"]!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task TikTok_failed_processing_is_permanent()
    {
        TikTokPublisher.PollScale = 0;
        var http = new FakeHttp()
            .Respond("creator_info", """{"data":{}}""")
            .Respond("video/init", """{"data":{"publish_id":"p4","upload_url":"https://open-upload.tiktokapis.com/u"}}""")
            .Respond("open-upload", "", HttpStatusCode.Created)
            .Respond("status/fetch", """{"data":{"status":"FAILED","fail_reason":"file_format_check_failed"}}""");
        var ex = await Assert.ThrowsAsync<SocialApiException>(() => new TikTokPublisher(http, FakeHttp.Options())
            .PublishAsync(Variant(SocialPlatform.TikTok, "動画"), Credential(SocialPlatform.TikTok), [Video(10)], CancellationToken.None));
        Assert.False(ex.IsTransient);
        Assert.Contains("file_format_check_failed", ex.Message);
    }

    [Fact]
    public async Task TikTok_requires_media()
    {
        var result = await new TikTokPublisher(new FakeHttp(), FakeHttp.Options())
            .ValidateAsync(Variant(SocialPlatform.TikTok, "本文だけ"), CancellationToken.None);
        Assert.Contains(result.Errors, e => e.Contains("画像または動画"));
    }

    [Fact]
    public async Task TikTok_insights_query_videos_and_followers()
    {
        var http = new FakeHttp()
            .Respond("v2/video/query/", """{"data":{"videos":[{"id":"7300","view_count":1200,"like_count":80,"comment_count":5,"share_count":9}]}}""")
            .Respond("v2/user/info/?fields=follower_count", """{"data":{"user":{"follower_count":3456}}}""");
        var reader = new TikTokInsightsReader(http);
        var m = Assert.Single(await reader.GetPostMetricsAsync(["7300"], Credential(SocialPlatform.TikTok), CancellationToken.None));
        Assert.Equal((0L, 1200L, 80, 5, 9), (m.Impressions, m.Views, m.Likes, m.Comments, m.Shares));
        Assert.Equal(["7300"], Json(http.Requests[0])["filters"]!["video_ids"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(3456, (await reader.GetAccountMetricsAsync(new DateOnly(2026, 10, 6), Credential(SocialPlatform.TikTok), CancellationToken.None)).Followers);
    }

    // ---------- YouTube ----------

    [Fact]
    public void YouTube_authorization_url_requests_offline_access_with_pkce()
    {
        var url = Uri.UnescapeDataString(new YouTubeConnector(new FakeHttp(), FakeHttp.Options(), Clock)
            .BuildAuthorizationUrl(SocialPlatform.YouTube, "st", "challenge", "http://127.0.0.1:5245/cb"));
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("prompt=consent", url);
        Assert.Contains("code_challenge=challenge", url);
        Assert.Contains("youtube.upload", url);
    }

    [Fact]
    public async Task YouTube_exchange_lists_channels()
    {
        var http = new FakeHttp()
            .Respond("oauth2.googleapis.com/token", """{"access_token":"ya29","expires_in":3599,"refresh_token":"1//r","scope":"x","token_type":"Bearer"}""")
            .Respond("youtube/v3/channels?part=snippet&mine=true", """{"items":[{"id":"UC123","snippet":{"title":"ほっこりカフェ","customUrl":"@hokkori","thumbnails":{"default":{"url":"https://yt/a.jpg"}}}}]}""");
        var a = Assert.Single(await new YouTubeConnector(http, FakeHttp.Options(), Clock)
            .ExchangeAsync(SocialPlatform.YouTube, "code", "verifier", "http://127.0.0.1/cb", CancellationToken.None));
        Assert.Contains("code_verifier=verifier", http.Requests[0].Body);
        Assert.Equal(("UC123", "@hokkori", "1//r"), (a.ExternalAccountId, a.DisplayName, a.Token.RefreshToken));
    }

    [Fact]
    public async Task YouTube_exchange_without_channel_explains()
    {
        var http = new FakeHttp()
            .Respond("token", """{"access_token":"ya29","expires_in":3599}""")
            .Respond("channels", """{"items":[]}""");
        var ex = await Assert.ThrowsAsync<SocialApiException>(() => new YouTubeConnector(http, FakeHttp.Options(), Clock)
            .ExchangeAsync(SocialPlatform.YouTube, "code", "v", "http://127.0.0.1/cb", CancellationToken.None));
        Assert.Contains("チャンネル", ex.Message);
    }

    [Fact]
    public async Task YouTube_refresh_keeps_refresh_token()
    {
        var http = new FakeHttp().Respond("token", """{"access_token":"new","expires_in":3599}""");
        var token = await new YouTubeConnector(http, FakeHttp.Options(), Clock)
            .RefreshAsync(SocialPlatform.YouTube, new StoredToken("old", "1//r"), CancellationToken.None);
        Assert.Equal(("new", "1//r"), (token.AccessToken, token.RefreshToken));
        Assert.Contains("grant_type=refresh_token", http.Requests[0].Body);
    }

    [Fact]
    public async Task YouTube_uploads_resumably_declares_ai_and_adds_captions()
    {
        var http = new FakeHttp()
            .Respond("upload/youtube/v3/videos?uploadType=resumable", "", location: new Uri("https://www.googleapis.com/upload/youtube/v3/videos?upload_id=abc"))
            .Respond("upload_id=abc", """{"id":"vid1","status":{"uploadStatus":"uploaded"}}""")
            .Respond("upload/youtube/v3/captions", """{"id":"cap1"}""");
        var v = Variant(SocialPlatform.YouTube, "秋の新作ラテ <限定>", "新作ラテ ショート", "Shorts", "カフェ");
        v.SetPlatformOption(PlatformOptionKeys.YouTubePrivacy, "unlisted", requiresApproval: false);
        var result = await new YouTubePublisher(http, FakeHttp.Options()).PublishAsync(v, Credential(SocialPlatform.YouTube),
            [Video(1000, ai: true, srt: "1\n00:00:00,000 --> 00:00:02,000\nこんにちは\n")], CancellationToken.None);

        var meta = Json(http.Requests[0]);
        Assert.Equal("新作ラテ ショート", meta["snippet"]!["title"]!.GetValue<string>());
        Assert.DoesNotContain("<", meta["snippet"]!["description"]!.GetValue<string>());
        Assert.Equal(["Shorts", "カフェ"], meta["snippet"]!["tags"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("unlisted", meta["status"]!["privacyStatus"]!.GetValue<string>());
        Assert.True(meta["status"]!["containsSyntheticMedia"]!.GetValue<bool>());
        Assert.Equal("1000", http.Requests[0].Headers["X-Upload-Content-Length"]);
        Assert.Equal(HttpMethod.Put, http.Requests[1].Method);
        Assert.Contains("\"videoId\":\"vid1\"", http.Requests[2].Body);
        Assert.Contains("こんにちは", http.Requests[2].Body);
        Assert.Equal(("vid1", "https://www.youtube.com/watch?v=vid1"), (result.ExternalPostId, result.Url));
    }

    [Fact]
    public async Task YouTube_caption_failure_does_not_fail_publish()
    {
        var http = new FakeHttp()
            .Respond("uploadType=resumable", "", location: new Uri("https://www.googleapis.com/upload/x?upload_id=1"))
            .Respond("upload_id=1", """{"id":"vid2"}""")
            .Respond("captions", """{"error":{"code":403,"message":"forbidden","errors":[{"reason":"forbidden"}]}}""", HttpStatusCode.Forbidden);
        var result = await new YouTubePublisher(http, FakeHttp.Options()).PublishAsync(Variant(SocialPlatform.YouTube, "本文"),
            Credential(SocialPlatform.YouTube), [Video(10, srt: "1\n00:00:00,000 --> 00:00:01,000\nx\n")], CancellationToken.None);
        Assert.Equal("vid2", result.ExternalPostId);
    }

    [Fact]
    public async Task YouTube_quota_exceeded_is_transient()
    {
        var http = new FakeHttp().Respond("uploadType=resumable",
            """{"error":{"code":403,"message":"The request cannot be completed because you have exceeded your quota.","errors":[{"reason":"quotaExceeded"}]}}""",
            HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<SocialApiException>(() => new YouTubePublisher(http, FakeHttp.Options())
            .PublishAsync(Variant(SocialPlatform.YouTube, "本文"), Credential(SocialPlatform.YouTube), [Video(10)], CancellationToken.None));
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task YouTube_requires_video_and_valid_title()
    {
        var publisher = new YouTubePublisher(new FakeHttp(), FakeHttp.Options());
        var errors = (await publisher.ValidateAsync(Variant(SocialPlatform.YouTube, "本文", new string('あ', 101)), CancellationToken.None)).Errors;
        Assert.Contains(errors, e => e.Contains("動画が必要"));
        Assert.Contains(errors, e => e.Contains("タイトル"));
        Assert.Equal("1行目", YouTubePublisher.Title(Variant(SocialPlatform.YouTube, "1行目\n2行目")));
    }

    [Fact]
    public async Task YouTube_insights_and_subscribers()
    {
        var http = new FakeHttp()
            .Respond("youtube/v3/videos?part=statistics", """{"items":[{"id":"vid1","statistics":{"viewCount":"5000","likeCount":"120","commentCount":"8","favoriteCount":"0"}}]}""")
            .Respond("youtube/v3/channels?part=statistics&id=acct", """{"items":[{"id":"acct","statistics":{"subscriberCount":"789"}}]}""");
        var reader = new YouTubeInsightsReader(http);
        var m = Assert.Single(await reader.GetPostMetricsAsync(["vid1"], Credential(SocialPlatform.YouTube), CancellationToken.None));
        Assert.Equal((5000L, 120, 8), (m.Views, m.Likes, m.Comments));
        Assert.Equal(789, (await reader.GetAccountMetricsAsync(new DateOnly(2026, 10, 6), Credential(SocialPlatform.YouTube), CancellationToken.None)).Followers);
    }

    [Fact]
    public async Task YouTube_inbox_reads_comment_threads_replies_and_holds()
    {
        var http = new FakeHttp()
            .Respond("youtube/v3/commentThreads?part=snippet&videoId=vid1", """
                {"items":[
                  {"id":"th1","snippet":{"videoId":"vid1","topLevelComment":{"id":"c1","snippet":{"textOriginal":"営業時間は？","authorDisplayName":"@taro","authorChannelId":{"value":"UCtaro"},"publishedAt":"2026-10-06T01:00:00Z"}}}},
                  {"id":"th2","snippet":{"videoId":"vid1","topLevelComment":{"id":"c2","snippet":{"textOriginal":"古い","authorDisplayName":"@old","authorChannelId":{"value":"UCold"},"publishedAt":"2026-10-01T00:00:00Z"}}}},
                  {"id":"th3","snippet":{"videoId":"vid1","topLevelComment":{"id":"c3","snippet":{"textOriginal":"自分","authorDisplayName":"@me","authorChannelId":{"value":"acct"},"publishedAt":"2026-10-06T02:00:00Z"}}}}
                ]}
                """)
            .Respond("youtube/v3/comments?part=snippet", """{"id":"r1"}""")
            .Respond("youtube/v3/comments/setModerationStatus?id=c1&moderationStatus=heldForReview", "");
        var reader = new YouTubeInboxReader(http, Clock);
        var item = Assert.Single(await reader.FetchAsync(Clock.GetUtcNow().AddDays(-1), ["vid1"], Credential(SocialPlatform.YouTube), CancellationToken.None));
        Assert.Equal(("c1", "UCtaro", "営業時間は？", "vid1"), (item.ExternalId, item.AuthorId, item.Text, item.InReplyToExternalPostId));

        Assert.Equal("r1", await reader.ReplyAsync(item, "10時からです", Credential(SocialPlatform.YouTube), CancellationToken.None));
        Assert.Equal("c1", Json(http.Requests[1])["snippet"]!["parentId"]!.GetValue<string>());
        Assert.True(await reader.HideAsync(item, Credential(SocialPlatform.YouTube), CancellationToken.None));
    }

    [Fact]
    public void Platform_option_change_after_approval_requires_reapproval()
    {
        var v = Variant(SocialPlatform.TikTok, "本文");
        var before = v.ComputeContentHash();
        Assert.False(v.SetPlatformOption(PlatformOptionKeys.TikTokPrivacy, "SELF_ONLY", requiresApproval: true)); // 承認前は再承認不要
        Assert.NotEqual(before, v.ComputeContentHash());
        Assert.Throws<ReachForge.Domain.Common.DomainException>(() => v.SetPlatformOption(PlatformOptionKeys.TikTokPrivacy, "EVERYONE", false));
        Assert.False(v.SetPlatformOption(PlatformOptionKeys.TikTokPrivacy, "SELF_ONLY", false)); // 同じ値は変更なし

        var plain = Variant(SocialPlatform.X, "本文");
        var plainHash = plain.ComputeContentHash();
        plain.SetPlatformOption(PlatformOptionKeys.YouTubePrivacy, "public", false);
        plain.SetPlatformOption(PlatformOptionKeys.YouTubePrivacy, null, false);
        Assert.Equal(plainHash, plain.ComputeContentHash()); // 設定がなければ従来のハッシュと同じ
    }
}

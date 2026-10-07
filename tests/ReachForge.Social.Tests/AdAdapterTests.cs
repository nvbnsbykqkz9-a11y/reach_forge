using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using ReachForge.Application.Ads;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Social.Ads;

namespace ReachForge.Social.Tests;

public class AdAdapterTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));

    private static Microsoft.Extensions.Options.IOptions<AdsOptions> Ads(Action<AdsOptions>? configure = null)
    {
        var o = new AdsOptions
        {
            TikTok = { AppId = "tt-app", Secret = "tt-secret" },
            X = { ConsumerKey = "ck", ConsumerSecret = "cs" },
            Google = { DeveloperToken = "dev-token" },
        };
        configure?.Invoke(o);
        return Microsoft.Extensions.Options.Options.Create(o);
    }

    private static AdCampaign Campaign(SocialPlatform platform, AdNetwork network, AdObjective objective = AdObjective.Traffic) => new()
    {
        Name = "秋の新作", Platform = platform, Network = network, Objective = objective, DailyBudget = 1000, Currency = "JPY",
        StartAt = Clock.GetUtcNow().AddHours(1), EndAt = Clock.GetUtcNow().AddDays(7),
        Targeting = new AdTargeting { AgeMin = 25, AgeMax = 44, Gender = AdGender.Female },
        Creative = new AdCreative { PrimaryText = "さつまいもラテが登場", Headline = "秋限定", LinkUrl = "https://shop.example/latte", YouTubeVideoId = "vid123" },
    };

    private static AdAccount Account(AdNetwork network, string id) =>
        new() { Network = network, ExternalAccountId = id, Name = "広告", CredentialSecretRef = "db://a" };

    private static PublishMedia Image() =>
        new(Guid.NewGuid(), "image/jpeg", null, false, _ => Task.FromResult(new byte[] { 1, 2, 3 }), _ => Task.FromResult(""), _ => Task.FromResult(""));

    private static PublishMedia Video() =>
        new(Guid.NewGuid(), "video/mp4", null, false, _ => Task.FromResult(new byte[] { 4, 5, 6 }), _ => Task.FromResult(""), _ => Task.FromResult(""));

    // ---------- OAuth 1.0a ----------

    [Fact]
    public void OAuth1_signature_matches_the_reference_example()
    {
        // X（旧 Twitter）の開発者ドキュメントの「Creating a signature」の例
        var signature = OAuth1Signer.Signature("POST", new Uri("https://api.twitter.com/1.1/statuses/update.json?include_entities=true"),
        [
            new("status", "Hello Ladies + Gentlemen, a signed OAuth request!"),
            new("oauth_consumer_key", "xvz1evFS4wEEPTGEFPHBog"),
            new("oauth_nonce", "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg"),
            new("oauth_signature_method", "HMAC-SHA1"),
            new("oauth_timestamp", "1318622958"),
            new("oauth_token", "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb"),
            new("oauth_version", "1.0"),
            new("include_entities", "true"),
        ], "kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Z7kBw", "LswwdoUaIvS8ltyTt5jkRh4J50vUPVVHtR2YPi5kE");
        Assert.Equal("hCtSmYh+iHYCEqBWrE7C7hYmtUk=", signature);
    }

    [Fact]
    public void OAuth1_header_includes_query_parameters_in_signature()
    {
        var header = OAuth1Signer.AuthorizationHeader("POST", new Uri("https://api.twitter.com/1.1/statuses/update.json?include_entities=true"),
            [new("status", "Hello Ladies + Gentlemen, a signed OAuth request!")], "xvz1evFS4wEEPTGEFPHBog", "kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Z7kBw",
            "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb", "LswwdoUaIvS8ltyTt5jkRh4J50vUPVVHtR2YPi5kE",
            nonce: "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg", timestamp: 1318622958);
        Assert.StartsWith("OAuth ", header);
        Assert.Contains("oauth_signature=\"hCtSmYh%2BiHYCEqBWrE7C7hYmtUk%3D\"", header);
    }

    // ---------- Meta ----------

    [Fact]
    public async Task Meta_creates_everything_paused_then_activates()
    {
        var http = new FakeHttp()
            .Respond("act_1/adimages", """{"images":{"ad.jpg":{"hash":"h1"}}}""")
            .Respond("act_1/campaigns", """{"id":"c1"}""")
            .Respond("act_1/adsets", """{"id":"s1"}""")
            .Respond("act_1/adcreatives", """{"id":"cr1"}""")
            .Respond("act_1/ads", """{"id":"a1"}""")
            .Respond("v24.0/a1", """{"success":true}""")
            .Respond("v24.0/s1", """{"success":true}""")
            .Respond("v24.0/c1", """{"success":true}""");
        var adapter = new MetaAdAdapter(http, FakeHttp.Options(), Ads(), Clock);
        var c = Campaign(SocialPlatform.Instagram, AdNetwork.Meta);

        var result = await adapter.SubmitAsync(new AdSubmission(c, Account(AdNetwork.Meta, "act_1"), new StoredToken("tok"), Image(), null,
            "page1", "ig1", null, "ほっこりカフェ"), CancellationToken.None);

        Assert.Equal(AdStatus.InReview, result.Status);
        Assert.Equal("c1", result.ExternalIds["campaign"]);
        var campaign = Uri.UnescapeDataString(http.Requests[1].Body!);
        Assert.Contains("objective=OUTCOME_TRAFFIC", campaign);
        Assert.Contains("status=PAUSED", campaign);
        var adset = Uri.UnescapeDataString(http.Requests[2].Body!.Replace('+', ' '));
        Assert.Contains("daily_budget=1000", adset); // 円は小数がないため、そのままの単位
        Assert.Contains("\"publisher_platforms\":[\"instagram\"]", adset);
        Assert.Contains("\"genders\":[2]", adset);
        Assert.Contains("\"age_min\":25", adset);
        Assert.Contains("optimization_goal=LINK_CLICKS", adset);
        var creative = Uri.UnescapeDataString(http.Requests[3].Body!.Replace('+', ' '));
        Assert.Contains("\"instagram_user_id\":\"ig1\"", creative);
        Assert.Contains("\"image_hash\":\"h1\"", creative);
        Assert.All(http.Requests.Skip(5), r => Assert.Contains("status=ACTIVE", r.Body));
    }

    [Fact]
    public async Task Meta_failure_deletes_the_campaign()
    {
        var http = new FakeHttp()
            .Respond("act_1/adimages", """{"images":{"ad.jpg":{"hash":"h1"}}}""")
            .Respond("act_1/campaigns", """{"id":"c1"}""")
            .Respond("act_1/adsets", """{"error":{"message":"Invalid budget","code":100}}""", HttpStatusCode.BadRequest)
            .Respond("v24.0/c1", """{"success":true}""");
        var adapter = new MetaAdAdapter(http, FakeHttp.Options(), Ads(), Clock);

        var ex = await Assert.ThrowsAsync<SocialApiException>(() => adapter.SubmitAsync(new AdSubmission(Campaign(SocialPlatform.Facebook, AdNetwork.Meta),
            Account(AdNetwork.Meta, "act_1"), new StoredToken("tok"), Image(), null, "page1", null, null, "b"), CancellationToken.None));
        Assert.Contains("Invalid budget", ex.Message);
        Assert.Equal(HttpMethod.Delete, http.Requests[^1].Method);
        Assert.EndsWith("/c1", http.Requests[^1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Meta_requires_a_facebook_page()
    {
        var adapter = new MetaAdAdapter(new FakeHttp(), FakeHttp.Options(), Ads(), Clock);
        await Assert.ThrowsAsync<ReachForge.Domain.Common.DomainException>(() => adapter.SubmitAsync(new AdSubmission(
            Campaign(SocialPlatform.Facebook, AdNetwork.Meta), Account(AdNetwork.Meta, "act_1"), new StoredToken("t"), Image(), null, null, null, null, "b"),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("ACTIVE", AdStatus.Active)]
    [InlineData("PENDING_REVIEW", AdStatus.InReview)]
    [InlineData("CAMPAIGN_PAUSED", AdStatus.Paused)]
    [InlineData("DISAPPROVED", AdStatus.Rejected)]
    public void Meta_status_mapping(string effective, AdStatus expected) => Assert.Equal(expected, MetaAdAdapter.Status(effective));

    [Fact]
    public async Task Meta_state_reads_insights()
    {
        var http = new FakeHttp()
            .Respond("v24.0/a1", """{"effective_status":"ACTIVE"}""")
            .Respond("c1/insights", """{"data":[{"impressions":"1200","clicks":"30","spend":"850.5","reach":"900"}]}""");
        var c = Campaign(SocialPlatform.Facebook, AdNetwork.Meta);
        c.MarkSubmitting(Clock.GetUtcNow());
        c.MarkSubmitted(new Dictionary<string, string> { ["campaign"] = "c1", ["ad"] = "a1" }, AdStatus.InReview);
        var state = await new MetaAdAdapter(http, FakeHttp.Options(), Ads(), Clock)
            .GetStateAsync(c, Account(AdNetwork.Meta, "act_1"), new StoredToken("t"), Clock.GetUtcNow(), CancellationToken.None);
        Assert.Equal(AdStatus.Active, state.Status);
        Assert.Equal((1200, 30, 850.5m), (state.Results!.Impressions, state.Results.Clicks, state.Results.Spend));
    }

    // ---------- TikTok ----------

    [Fact]
    public async Task TikTok_exchange_lists_advertisers()
    {
        var http = new FakeHttp()
            .Respond("oauth2/access_token/", """{"code":0,"message":"OK","data":{"access_token":"tt-token","advertiser_ids":["7001"]}}""")
            .Respond("advertiser/info/", """{"code":0,"message":"OK","data":{"list":[{"advertiser_id":"7001","name":"ほっこり","currency":"JPY","status":"STATUS_ENABLE"}]}}""");
        var result = await new TikTokAdAdapter(http, Ads()).ExchangeAsync("auth", "", "https://app/cb", CancellationToken.None);
        Assert.Equal("tt-token", result.Token.AccessToken);
        Assert.Equal(("7001", "JPY"), (result.Accounts[0].ExternalAccountId, result.Accounts[0].Currency));
        Assert.Contains("\"auth_code\":\"auth\"", http.Requests[0].Body);
        Assert.Equal("tt-token", http.Requests[1].Headers["Access-Token"]);
    }

    [Fact]
    public async Task TikTok_submit_uploads_video_and_enables_after_creating()
    {
        var http = new FakeHttp()
            .Respond("file/video/ad/upload/", """{"code":0,"data":[{"video_id":"v1"}]}""")
            .Respond("file/image/ad/upload/", """{"code":0,"data":{"image_id":"i1"}}""")
            .Respond("identity/create/", """{"code":0,"data":{"identity_id":"id1"}}""")
            .Respond("campaign/create/", """{"code":0,"data":{"campaign_id":"c1"}}""")
            .Respond("adgroup/create/", """{"code":0,"data":{"adgroup_id":"g1"}}""")
            .Respond("ad/create/", """{"code":0,"data":{"ad_ids":["a1"]}}""")
            .Respond("status/update/", """{"code":0,"data":{}}""")
            .Respond("status/update/", """{"code":0,"data":{}}""")
            .Respond("status/update/", """{"code":0,"data":{}}""");
        var c = Campaign(SocialPlatform.TikTok, AdNetwork.TikTok, AdObjective.VideoViews);
        var result = await new TikTokAdAdapter(http, Ads()).SubmitAsync(new AdSubmission(c, Account(AdNetwork.TikTok, "7001"), new StoredToken("t"),
            Video(), [9, 9], null, null, null, "ほっこりカフェ"), CancellationToken.None);

        Assert.Equal("a1", result.ExternalIds["ad"]);
        Assert.Equal("id1", result.AccountExtra!["identityId"]);
        var adgroup = JsonNode.Parse(http.Requests[4].Body!)!;
        Assert.Equal("BUDGET_MODE_DAY", adgroup["budget_mode"]!.GetValue<string>());
        Assert.Equal(1000m, adgroup["budget"]!.GetValue<decimal>());
        Assert.Equal(TikTokAdAdapter.JapanLocationId, adgroup["location_ids"]![0]!.GetValue<string>());
        Assert.Equal("GENDER_FEMALE", adgroup["gender"]!.GetValue<string>());
        Assert.Equal("DISABLE", adgroup["operation_status"]!.GetValue<string>());
        Assert.Equal(["AGE_25_34", "AGE_35_44"], adgroup["age_groups"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.All(http.Requests.Skip(6), r => Assert.Contains("\"ENABLE\"", r.Body));
    }

    [Fact]
    public async Task TikTok_error_code_in_ok_response_fails_and_cleans_up()
    {
        var http = new FakeHttp()
            .Respond("file/video/ad/upload/", """{"code":0,"data":[{"video_id":"v1"}]}""")
            .Respond("file/image/ad/upload/", """{"code":0,"data":{"image_id":"i1"}}""")
            .Respond("campaign/create/", """{"code":0,"data":{"campaign_id":"c1"}}""")
            .Respond("adgroup/create/", """{"code":40002,"message":"Budget too low"}""")
            .Respond("campaign/status/update/", """{"code":0,"data":{}}""");
        var account = Account(AdNetwork.TikTok, "7001");
        account.Extra["identityId"] = "id1";
        var ex = await Assert.ThrowsAsync<SocialApiException>(() => new TikTokAdAdapter(http, Ads()).SubmitAsync(new AdSubmission(
            Campaign(SocialPlatform.TikTok, AdNetwork.TikTok), account, new StoredToken("t"), Video(), [9], null, null, null, "b"), CancellationToken.None));
        Assert.Contains("Budget too low", ex.Message);
        Assert.Contains("\"DELETE\"", http.Requests[^1].Body);
    }

    [Theory]
    [InlineData("AD_STATUS_DELIVERY_OK", "ENABLE", AdStatus.Active)]
    [InlineData("AD_STATUS_REVIEWING", "ENABLE", AdStatus.InReview)]
    [InlineData("AD_STATUS_NOT_APPROVE", "ENABLE", AdStatus.Rejected)]
    [InlineData("AD_STATUS_DISABLE", "DISABLE", AdStatus.Paused)]
    public void TikTok_status_mapping(string secondary, string operation, AdStatus expected) =>
        Assert.Equal(expected, TikTokAdAdapter.Status(secondary, operation));

    // ---------- X ----------

    [Theory]
    [InlineData(18, 65, "AGE_OVER_18")]
    [InlineData(25, 34, "AGE_25_TO_34")]
    [InlineData(20, 30, "AGE_18_TO_34")]
    [InlineData(35, 65, "AGE_OVER_35")]
    [InlineData(55, 60, "AGE_OVER_50")]
    public void X_age_bucket(int min, int max, string expected) =>
        Assert.Equal(expected, XAdAdapter.AgeBucket(new AdTargeting { AgeMin = min, AgeMax = max }));

    [Fact]
    public async Task X_submit_signs_requests_and_promotes_a_nullcast_post()
    {
        var http = new FakeHttp()
            .Respond("2/media/upload", """{"data":{"id":"m1","media_key":"3_m1"}}""")
            .Respond("accounts/acc1/media_library", """{"data":{}}""")
            .Respond("targeting_criteria/locations", """{"data":[{"targeting_value":"jp-key"}]}""")
            .Respond("accounts/acc1/campaigns", """{"data":{"id":"c1"}}""")
            .Respond("accounts/acc1/line_items", """{"data":{"id":"l1"}}""")
            .Respond("batch/accounts/acc1/targeting_criteria", """{"data":[]}""")
            .Respond("accounts/acc1/tweet", """{"data":{"id_str":"t1"}}""")
            .Respond("accounts/acc1/promoted_tweets", """{"data":[{"id":"p1"}]}""")
            .Respond("line_items/l1", """{"data":{}}""")
            .Respond("campaigns/c1", """{"data":{}}""");
        var account = Account(AdNetwork.X, "acc1");
        account.Extra["fundingInstrumentId"] = "fi1";
        var token = new StoredToken("at", null, null, new Dictionary<string, string> { ["secret"] = "ats", ["user_id"] = "u1" });

        var result = await new XAdAdapter(http, Ads()).SubmitAsync(new AdSubmission(Campaign(SocialPlatform.X, AdNetwork.X), account, token, Image(), null,
            null, null, null, "b"), CancellationToken.None);

        Assert.Equal("p1", result.ExternalIds["promoted_tweet"]);
        Assert.All(http.Requests, r => Assert.StartsWith("OAuth ", r.Headers["Authorization"]));
        Assert.Contains("oauth_token=\"at\"", http.Requests[0].Headers["Authorization"]);
        var campaign = Uri.UnescapeDataString(http.Requests[3].Uri.Query);
        Assert.Contains("daily_budget_amount_local_micro=1000000000", campaign);
        Assert.Contains("funding_instrument_id=fi1", campaign);
        Assert.Contains("entity_status=PAUSED", campaign);
        Assert.Contains("\"targeting_value\":\"jp-key\"", http.Requests[5].Body);
        Assert.Contains("\"targeting_value\":\"AGE_25_TO_49\"", http.Requests[5].Body);
        var tweet = Uri.UnescapeDataString(http.Requests[6].Uri.Query);
        Assert.Contains("nullcast=true", tweet);
        Assert.Contains("as_user_id=u1", tweet);
        Assert.Contains("https://shop.example/latte", tweet);
        Assert.Equal(HttpMethod.Put, http.Requests[^1].Method);
    }

    // ---------- Google ----------

    [Fact]
    public async Task Google_creates_demand_gen_campaign_in_one_mutate()
    {
        var http = new FakeHttp().Respond("customers/123/googleAds:mutate", """
            {"mutateOperationResponses":[{"campaignBudgetResult":{"resourceName":"customers/123/campaignBudgets/9"}},
             {"campaignResult":{"resourceName":"customers/123/campaigns/8"}},{"adGroupAdResult":{"resourceName":"customers/123/adGroupAds/7~6"}}]}
            """);
        var account = Account(AdNetwork.Google, "123");
        account.Extra["timeZone"] = "Asia/Tokyo";
        var result = await new GoogleAdAdapter(http, Ads(), FakeHttp.Options(), Clock).SubmitAsync(new AdSubmission(
            Campaign(SocialPlatform.YouTube, AdNetwork.Google, AdObjective.VideoViews), account, new StoredToken("gt", "rt", Clock.GetUtcNow().AddHours(1)),
            Image(), null, null, null, null, "ほっこりカフェ"), CancellationToken.None);

        Assert.Equal("customers/123/campaigns/8", result.ExternalIds["campaign"]);
        var r = http.Requests.Single();
        Assert.Equal("dev-token", r.Headers["developer-token"]);
        Assert.Equal("Bearer gt", r.Headers["Authorization"]);
        var ops = JsonNode.Parse(r.Body!)!["mutateOperations"]!.AsArray();
        var campaign = ops[1]!["campaignOperation"]!["create"]!;
        Assert.Equal("DEMAND_GEN", campaign["advertisingChannelType"]!.GetValue<string>());
        Assert.Equal("2026-10-06 10:00:00", campaign["startDateTime"]!.GetValue<string>()); // 日本時間
        Assert.Equal("1000000000", ops[0]!["campaignBudgetOperation"]!["create"]!["amountMicros"]!.GetValue<string>());
        Assert.Equal(GoogleAdAdapter.JapanGeoTarget, ops[2]!["campaignCriterionOperation"]!["create"]!["location"]!["geoTargetConstant"]!.GetValue<string>());
        Assert.Contains(ops, o => o?["assetOperation"]?["create"]?["youtubeVideoAsset"]?["youtubeVideoId"]?.GetValue<string>() == "vid123");
        Assert.Contains(ops, o => o?["adGroupCriterionOperation"]?["create"]?["gender"]?["type"]?.GetValue<string>() == "FEMALE");
        Assert.Equal(2, ops.Count(o => o?["adGroupCriterionOperation"]?["create"]?["ageRange"] is not null));
    }

    [Fact]
    public void Google_full_age_range_is_not_targeted() =>
        Assert.Empty(GoogleAdAdapter.AgeRanges(new AdTargeting()));

    // ---------- お試し ----------

    [Fact]
    public async Task Demo_adapter_moves_from_review_to_active_and_spends_within_budget()
    {
        var demo = new DemoAdAdapter(AdNetwork.Meta, Clock);
        var c = Campaign(SocialPlatform.Facebook, AdNetwork.Meta);
        c.MarkSubmitting(Clock.GetUtcNow());
        Assert.Equal(AdStatus.InReview, (await demo.GetStateAsync(c, Account(AdNetwork.Meta, "d"), new StoredToken("d"), Clock.GetUtcNow(), CancellationToken.None)).Status);

        var later = await demo.GetStateAsync(c, Account(AdNetwork.Meta, "d"), new StoredToken("d"), Clock.GetUtcNow().AddHours(25), CancellationToken.None);
        Assert.Equal(AdStatus.Active, later.Status);
        Assert.InRange(later.Results!.Spend, 1, c.DailyBudget);

        var ended = await demo.GetStateAsync(c, Account(AdNetwork.Meta, "d"), new StoredToken("d"), c.EndAt.AddHours(1), CancellationToken.None);
        Assert.Equal(AdStatus.Completed, ended.Status);
        // 開始は出稿の1時間後のため、使った金額は上限（1日の予算 × 7日）より少し少ない
        Assert.InRange(ended.Results!.Spend, c.MaxTotalSpend - c.DailyBudget / 24 - 1, c.MaxTotalSpend);
    }
}

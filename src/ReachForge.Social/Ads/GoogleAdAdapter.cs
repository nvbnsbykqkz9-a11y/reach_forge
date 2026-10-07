using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReachForge.Application.Ads;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Ads;

/// <summary>
/// Google 広告（Google Ads API）の YouTube 広告。投稿済みの YouTube の動画で、デマンド ジェネレーション キャンペーンをつくる。
/// 予算・キャンペーン・地域・広告グループ・素材・広告を1回の mutate（すべて成功するか、何もつくらないか）でつくる。
/// 開発者トークン（Google 広告の API センター）が必要。
/// </summary>
public sealed class GoogleAdAdapter(IHttpClientFactory http, IOptions<AdsOptions> options, IOptions<SocialOptions> social, TimeProvider clock)
    : IAdNetworkAdapter
{
    public const string HttpClientName = "ads-google";
    private const string Name = "Google 広告";

    /// <summary>Google 広告の地域（日本）。</summary>
    public const string JapanGeoTarget = "geoTargetConstants/2392";

    private GoogleAdsOptions O => options.Value.Google;
    private string? ClientId => string.IsNullOrWhiteSpace(O.ClientId) ? social.Value.YouTube.ClientId : O.ClientId;
    private string? ClientSecret => string.IsNullOrWhiteSpace(O.ClientSecret) ? social.Value.YouTube.ClientSecret : O.ClientSecret;
    private string Api => $"{O.ApiBaseUrl}{O.ApiVersion}/";

    public AdNetwork Network => AdNetwork.Google;
    public bool IsSimulation => false;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(O.DeveloperToken) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    public IReadOnlyList<SocialPlatform> Platforms => AdHttp.PlatformsOf(AdNetwork.Google);
    public decimal MinDailyBudget(string currency) => currency == "JPY" ? 500 : 5;
    public bool RequiresVideo(SocialPlatform platform) => true;

    public Task<AdAuthorizationStart> BeginAuthorizationAsync(string state, string codeChallenge, string redirectUri, CancellationToken ct) =>
        Task.FromResult(new AdAuthorizationStart($"{O.AuthorizeUrl}?" + SocialHttp.Query(
            ("client_id", ClientId), ("redirect_uri", redirectUri), ("response_type", "code"), ("scope", O.Scopes), ("state", state),
            ("access_type", "offline"), ("prompt", "consent"), ("code_challenge", codeChallenge), ("code_challenge_method", "S256"))));

    public async Task<AdConnectResult> ExchangeAsync(string code, string stateSecret, string redirectUri, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(Client, SocialHttp.Form(HttpMethod.Post, O.TokenUrl,
        [
            new("code", code), new("client_id", ClientId!), new("client_secret", ClientSecret!), new("redirect_uri", redirectUri),
            new("grant_type", "authorization_code"), new("code_verifier", stateSecret),
        ]), Name, ct);
        var token = new StoredToken(SocialHttp.Str(json, "access_token"), SocialHttp.StrOrNull(json, "refresh_token"),
            SocialHttp.ExpiresAt(json, clock.GetUtcNow()));

        var list = await SendAsync(HttpMethod.Get, "customers:listAccessibleCustomers", token, null, null, ct);
        var accounts = new List<AdAccountInfo>();
        foreach (var resource in (list["resourceNames"]?.AsArray() ?? []).Take(20))
        {
            var id = resource!.ToString().Replace("customers/", "", StringComparison.Ordinal);
            JsonNode found;
            try
            {
                found = await SendAsync(HttpMethod.Post, $"customers/{id}/googleAds:search", token, new JsonObject
                {
                    ["query"] = "SELECT customer.id, customer.descriptive_name, customer.currency_code, customer.time_zone, customer.manager FROM customer",
                }, null, ct);
            }
            catch (SocialApiException ex) when (!ex.RequiresReauth && !ex.IsTransient)
            {
                continue; // 停止中・権限のないアカウントは候補にしない
            }
            var customer = found["results"]?.AsArray().FirstOrDefault()?["customer"];
            if (customer is null || SocialHttp.StrOrNull(customer, "manager") == "true") continue; // MCC には広告を出せない
            accounts.Add(new AdAccountInfo(id, SocialHttp.StrOrNull(customer, "descriptiveName") is { Length: > 0 } n ? n : id,
                SocialHttp.StrOrNull(customer, "currencyCode") ?? "JPY",
                new Dictionary<string, string> { ["timeZone"] = SocialHttp.StrOrNull(customer, "timeZone") ?? "Asia/Tokyo" }));
        }
        return new AdConnectResult(token, accounts);
    }

    public async Task<StoredToken> RefreshAsync(StoredToken token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            throw new SocialApiException(ErrorCodes.SnsReauthRequired, "Google 広告の再連携が必要です", isTransient: false);
        }
        var json = await SocialHttp.SendAsync(Client, SocialHttp.Form(HttpMethod.Post, O.TokenUrl,
        [
            new("client_id", ClientId!), new("client_secret", ClientSecret!), new("refresh_token", token.RefreshToken), new("grant_type", "refresh_token"),
        ]), Name, ct);
        return token with { AccessToken = SocialHttp.Str(json, "access_token"), ExpiresAt = SocialHttp.ExpiresAt(json, clock.GetUtcNow()) };
    }

    public async Task<AdSubmitResult> SubmitAsync(AdSubmission s, CancellationToken ct)
    {
        var c = s.Campaign;
        var cid = s.Account.ExternalAccountId;
        if (string.IsNullOrWhiteSpace(c.Creative.YouTubeVideoId)) throw new DomainException(ErrorCodes.Validation, "広告にする YouTube の動画を選んでください。");
        if (s.Media is null || AdHttp.IsVideo(s.Media)) throw new DomainException(ErrorCodes.Validation, "YouTube の広告には、正方形のロゴ画像が必要です。");
        var token = await FreshAsync(s.Token, ct);
        var logo = await s.Media.ReadAsync(ct);
        var zone = Zone(s.Account);
        var link = string.IsNullOrEmpty(c.Creative.LinkUrl) ? $"https://www.youtube.com/watch?v={c.Creative.YouTubeVideoId}" : c.Creative.LinkUrl;
        string R(string kind, int temp) => $"customers/{cid}/{kind}/{temp}";

        var operations = new JsonArray
        {
            Create("campaignBudgetOperation", new JsonObject
            {
                ["resourceName"] = R("campaignBudgets", -1), ["name"] = Cut($"{c.Name} {c.Id:N}", 255),
                ["amountMicros"] = AdHttp.Micros(c.DailyBudget).ToString(CultureInfo.InvariantCulture),
                ["deliveryMethod"] = "STANDARD", ["explicitlyShared"] = false,
            }),
            Create("campaignOperation", new JsonObject
            {
                ["resourceName"] = R("campaigns", -2), ["name"] = $"{c.Name} {c.Id.ToString("N")[..8]}", ["status"] = "ENABLED",
                ["advertisingChannelType"] = "DEMAND_GEN", ["campaignBudget"] = R("campaignBudgets", -1), ["targetSpend"] = new JsonObject(),
                ["startDateTime"] = Local(c.StartAt, zone), ["endDateTime"] = Local(c.EndAt, zone),
                ["containsEuPoliticalAdvertising"] = "DOES_NOT_CONTAIN_EU_POLITICAL_ADVERTISING",
            }),
            Create("campaignCriterionOperation", new JsonObject
            {
                ["campaign"] = R("campaigns", -2), ["location"] = new JsonObject { ["geoTargetConstant"] = JapanGeoTarget },
            }),
            Create("adGroupOperation", new JsonObject
            {
                ["resourceName"] = R("adGroups", -3), ["name"] = c.Name, ["campaign"] = R("campaigns", -2), ["status"] = "ENABLED",
            }),
        };
        foreach (var range in AgeRanges(c.Targeting))
        {
            operations.Add(Create("adGroupCriterionOperation", new JsonObject { ["adGroup"] = R("adGroups", -3), ["ageRange"] = new JsonObject { ["type"] = range } }));
        }
        if (c.Targeting.Gender != AdGender.All)
        {
            operations.Add(Create("adGroupCriterionOperation", new JsonObject
            {
                ["adGroup"] = R("adGroups", -3), ["gender"] = new JsonObject { ["type"] = c.Targeting.Gender == AdGender.Male ? "MALE" : "FEMALE" },
            }));
        }
        operations.Add(Create("assetOperation", new JsonObject
        {
            ["resourceName"] = R("assets", -4), ["name"] = $"YouTube {c.Creative.YouTubeVideoId} {c.Id.ToString("N")[..8]}",
            ["youtubeVideoAsset"] = new JsonObject { ["youtubeVideoId"] = c.Creative.YouTubeVideoId },
        }));
        operations.Add(Create("assetOperation", new JsonObject
        {
            ["resourceName"] = R("assets", -5), ["name"] = $"Logo {c.Id.ToString("N")[..8]}", ["type"] = "IMAGE",
            ["imageAsset"] = new JsonObject { ["data"] = Convert.ToBase64String(logo) },
        }));
        var headline = Cut(c.Creative.Headline is { Length: > 0 } h ? h : c.Creative.PrimaryText, 40);
        operations.Add(Create("adGroupAdOperation", new JsonObject
        {
            ["adGroup"] = R("adGroups", -3), ["status"] = "ENABLED",
            ["ad"] = new JsonObject
            {
                ["name"] = c.Name, ["finalUrls"] = new JsonArray(link),
                ["demandGenVideoResponsiveAd"] = new JsonObject
                {
                    ["headlines"] = new JsonArray(Text(headline)),
                    ["longHeadlines"] = new JsonArray(Text(Cut(c.Creative.PrimaryText, 90))),
                    ["descriptions"] = new JsonArray(Text(Cut(c.Creative.Description is { Length: > 0 } d ? d : c.Creative.PrimaryText, 90))),
                    ["videos"] = new JsonArray(new JsonObject { ["asset"] = R("assets", -4) }),
                    ["logoImages"] = new JsonArray(new JsonObject { ["asset"] = R("assets", -5) }),
                    ["businessName"] = Text(Cut(s.BrandName, 25)),
                },
            },
        }));

        var result = await SendAsync(HttpMethod.Post, $"customers/{cid}/googleAds:mutate", token, new JsonObject { ["mutateOperations"] = operations }, cid, ct);
        var responses = result["mutateOperationResponses"]?.AsArray() ?? [];
        var ids = new Dictionary<string, string>
        {
            ["campaign"] = Resource(responses, "campaignResult"),
            ["adGroupAd"] = Resource(responses, "adGroupAdResult"),
        };
        return new AdSubmitResult(ids, AdStatus.InReview);
    }

    public async Task SetPausedAsync(AdCampaign campaign, AdAccount account, StoredToken token, bool paused, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, $"customers/{account.ExternalAccountId}/googleAds:mutate", await FreshAsync(token, ct), new JsonObject
        {
            ["mutateOperations"] = new JsonArray(new JsonObject
            {
                ["campaignOperation"] = new JsonObject
                {
                    ["update"] = new JsonObject { ["resourceName"] = campaign.ExternalIds["campaign"], ["status"] = paused ? "PAUSED" : "ENABLED" },
                    ["updateMask"] = "status",
                },
            }),
        }, account.ExternalAccountId, ct);

    public async Task<AdRemoteState> GetStateAsync(AdCampaign campaign, AdAccount account, StoredToken token, DateTimeOffset now, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, $"customers/{account.ExternalAccountId}/googleAds:search", await FreshAsync(token, ct), new JsonObject
        {
            ["query"] = "SELECT campaign.status, ad_group_ad.policy_summary.approval_status, ad_group_ad.policy_summary.review_status, " +
                        "metrics.impressions, metrics.clicks, metrics.cost_micros FROM ad_group_ad " +
                        $"WHERE campaign.resource_name = '{campaign.ExternalIds["campaign"]}'",
        }, account.ExternalAccountId, ct);
        var row = json["results"]?.AsArray().FirstOrDefault();
        var results = row?["metrics"] is { } m
            ? new AdResults(AdHttp.Long(m, "impressions"), AdHttp.Long(m, "clicks"), AdHttp.FromMicros(AdHttp.Long(m, "costMicros")), 0, 0, now)
            : null;
        var approval = SocialHttp.StrOrNull(row, "adGroupAd.policySummary.approvalStatus");
        return new AdRemoteState(Status(SocialHttp.StrOrNull(row, "campaign.status"), approval,
            SocialHttp.StrOrNull(row, "adGroupAd.policySummary.reviewStatus")), results,
            approval == "DISAPPROVED" ? "Google のポリシー審査で承認されませんでした。Google 広告の管理画面で理由を確認できます。" : null);
    }

    public static AdStatus Status(string? campaignStatus, string? approval, string? review) => (campaignStatus, approval, review) switch
    {
        (_, "DISAPPROVED", _) => AdStatus.Rejected,
        ("PAUSED", _, _) => AdStatus.Paused,
        ("REMOVED", _, _) => AdStatus.Completed,
        (_, _, "REVIEW_IN_PROGRESS") or (_, null or "UNKNOWN", _) => AdStatus.InReview,
        _ => AdStatus.Active,
    };

    /// <summary>年齢の範囲に重なる Google の年齢層（全年齢なら指定しない）。</summary>
    public static IReadOnlyList<string> AgeRanges(AdTargeting t)
    {
        if (t.AgeMin <= AdTargeting.MinAge && t.AgeMax >= AdTargeting.MaxAge) return [];
        (int Min, int Max, string Code)[] ranges =
        [
            (18, 24, "AGE_RANGE_18_24"), (25, 34, "AGE_RANGE_25_34"), (35, 44, "AGE_RANGE_35_44"),
            (45, 54, "AGE_RANGE_45_54"), (55, 64, "AGE_RANGE_55_64"), (65, 200, "AGE_RANGE_65_UP"),
        ];
        return ranges.Where(r => r.Max >= t.AgeMin && r.Min <= t.AgeMax).Select(r => r.Code).ToList();
    }

    private static JsonObject Create(string operation, JsonObject body) => new() { [operation] = new JsonObject { ["create"] = body } };

    private static JsonObject Text(string text) => new() { ["text"] = text };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    private static string Resource(JsonArray responses, string key) =>
        responses.Select(r => SocialHttp.StrOrNull(r, $"{key}.resourceName")).FirstOrDefault(r => r is not null)
        ?? throw AdHttp.Error($"Google 広告の応答に {key} がありません");

    private static TimeZoneInfo Zone(AdAccount account)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(account.Extra.GetValueOrDefault("timeZone") ?? "Asia/Tokyo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>広告アカウントのタイムゾーンでの日時（Google 広告の形式）。</summary>
    private static string Local(DateTimeOffset t, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(t, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private async Task<StoredToken> FreshAsync(StoredToken token, CancellationToken ct) =>
        token.ExpiresWithin(TimeSpan.FromMinutes(5), clock.GetUtcNow()) ? await RefreshAsync(token, ct) : token;

    private HttpClient Client => http.CreateClient(HttpClientName);

    private Task<JsonNode> SendAsync(HttpMethod method, string path, StoredToken token, JsonObject? body, string? customerId, CancellationToken ct)
    {
        var request = body is null ? SocialHttp.Get(Api + path, token.AccessToken) : SocialHttp.Json(method, Api + path, body, token.AccessToken);
        request.Headers.Add("developer-token", O.DeveloperToken);
        if (!string.IsNullOrWhiteSpace(O.LoginCustomerId) && customerId is not null)
        {
            request.Headers.Add("login-customer-id", O.LoginCustomerId.Replace("-", "", StringComparison.Ordinal));
        }
        return SocialHttp.SendAsync(Client, request, Name, ct);
    }
}

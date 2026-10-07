using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReachForge.Application.Ads;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Ads;

/// <summary>
/// TikTok 広告（TikTok API for Business v1.3）。動画のアップロード → キャンペーン → 広告グループ（予算・期間・対象）→ 広告。
/// すべて停止（DISABLE）でつくり、最後に配信を始める。途中で失敗したらキャンペーンを削除する。
/// 応答は HTTP 200 でも code が 0 以外ならエラー。
/// </summary>
public sealed class TikTokAdAdapter(IHttpClientFactory http, IOptions<AdsOptions> options) : IAdNetworkAdapter
{
    public const string HttpClientName = "ads-tiktok";
    private const string Name = "TikTok 広告";

    /// <summary>TikTok の地域 ID（日本）。</summary>
    public const string JapanLocationId = "1861060";

    private TikTokAdsOptions O => options.Value.TikTok;

    public AdNetwork Network => AdNetwork.TikTok;
    public bool IsSimulation => false;
    public bool IsConfigured => O.IsConfigured;
    public IReadOnlyList<SocialPlatform> Platforms => AdHttp.PlatformsOf(AdNetwork.TikTok);

    /// <summary>広告グループの1日の予算の下限（TikTok は 20 USD 相当）。</summary>
    public decimal MinDailyBudget(string currency) => currency == "JPY" ? 2000 : 20;
    public bool RequiresVideo(SocialPlatform platform) => true;

    public Task<AdAuthorizationStart> BeginAuthorizationAsync(string state, string codeChallenge, string redirectUri, CancellationToken ct) =>
        Task.FromResult(new AdAuthorizationStart($"{O.AuthorizeUrl}?" + SocialHttp.Query(("app_id", O.AppId), ("state", state), ("redirect_uri", redirectUri))));

    public async Task<AdConnectResult> ExchangeAsync(string code, string stateSecret, string redirectUri, CancellationToken ct)
    {
        var data = await SendAsync(HttpMethod.Post, "oauth2/access_token/", null, new JsonObject
        {
            ["app_id"] = O.AppId, ["secret"] = O.Secret, ["auth_code"] = code,
        }, ct);
        // 長期のアクセストークン（失効しない。取り消されたら再連携）
        var token = new StoredToken(SocialHttp.Str(data, "access_token"));
        var ids = (data["advertiser_ids"]?.AsArray() ?? []).Select(i => i?.ToString()).Where(i => !string.IsNullOrEmpty(i)).Cast<string>().ToList();
        if (ids.Count == 0) return new AdConnectResult(token, []);

        var info = await SendAsync(HttpMethod.Get, "advertiser/info/?" + SocialHttp.Query(
            ("advertiser_ids", new JsonArray([.. ids.Select(i => (JsonNode)i)]).ToJsonString()), ("fields", "[\"advertiser_id\",\"name\",\"currency\",\"status\"]")),
            token.AccessToken, null, ct);
        var accounts = (info["list"]?.AsArray() ?? [])
            .Where(a => SocialHttp.StrOrNull(a, "status") is null or "STATUS_ENABLE")
            .Select(a => new AdAccountInfo(SocialHttp.Str(a, "advertiser_id"), SocialHttp.StrOrNull(a, "name") ?? SocialHttp.Str(a, "advertiser_id"),
                SocialHttp.StrOrNull(a, "currency") ?? "JPY"))
            .ToList();
        return new AdConnectResult(token, accounts);
    }

    public async Task<AdSubmitResult> SubmitAsync(AdSubmission s, CancellationToken ct)
    {
        var c = s.Campaign;
        var adv = s.Account.ExternalAccountId;
        var token = s.Token.AccessToken;
        if (!AdHttp.IsVideo(s.Media)) throw new DomainException(ErrorCodes.Validation, "TikTok の広告には動画が必要です。");

        var video = await s.Media!.ReadAsync(ct);
        var videoId = Id(await UploadAsync("file/video/ad/upload/", adv, "video", video, s.Media.Mime, token, ct), "video_id");
        string coverId;
        if (s.Thumbnail is { } thumb)
        {
            coverId = Id(await UploadAsync("file/image/ad/upload/", adv, "image", thumb, "image/jpeg", token, ct), "image_id");
        }
        else
        {
            var info = await SendAsync(HttpMethod.Get, "file/video/ad/info/?" + SocialHttp.Query(("advertiser_id", adv),
                ("video_ids", $"[\"{videoId}\"]")), token, null, ct);
            var cover = SocialHttp.StrOrNull(info["list"]?.AsArray().FirstOrDefault(), "video_cover_url")
                        ?? throw AdHttp.Error("TikTok から動画の表紙を取得できませんでした");
            coverId = Id(await SendAsync(HttpMethod.Post, "file/image/ad/upload/", token,
                new JsonObject { ["advertiser_id"] = adv, ["upload_type"] = "UPLOAD_BY_URL", ["image_url"] = cover }, ct), "image_id");
        }

        // 広告の表示名（カスタムアイデンティティ）。1度つくったら広告アカウントに覚えておく
        Dictionary<string, string>? accountExtra = null;
        if (!s.Account.Extra.TryGetValue("identityId", out var identityId))
        {
            identityId = SocialHttp.Str(await SendAsync(HttpMethod.Post, "identity/create/", token,
                new JsonObject { ["advertiser_id"] = adv, ["display_name"] = PostText(s.BrandName, 40) }, ct), "identity_id");
            accountExtra = new() { ["identityId"] = identityId };
        }

        var ids = new Dictionary<string, string>();
        try
        {
            ids["campaign"] = SocialHttp.Str(await SendAsync(HttpMethod.Post, "campaign/create/", token, new JsonObject
            {
                ["advertiser_id"] = adv, ["campaign_name"] = c.Name, ["objective_type"] = Objective(c.Objective),
                ["budget_mode"] = "BUDGET_MODE_INFINITE", ["operation_status"] = "DISABLE",
            }, ct), "campaign_id");

            var adgroup = new JsonObject
            {
                ["advertiser_id"] = adv, ["campaign_id"] = ids["campaign"], ["adgroup_name"] = c.Name,
                ["placement_type"] = "PLACEMENT_TYPE_NORMAL", ["placements"] = new JsonArray("PLACEMENT_TIKTOK"),
                ["location_ids"] = new JsonArray(JapanLocationId),
                ["age_groups"] = new JsonArray([.. AgeGroups(c.Targeting).Select(a => (JsonNode)a)]),
                ["gender"] = c.Targeting.Gender switch { AdGender.Male => "GENDER_MALE", AdGender.Female => "GENDER_FEMALE", _ => "GENDER_UNLIMITED" },
                ["budget_mode"] = "BUDGET_MODE_DAY", ["budget"] = c.DailyBudget,
                ["schedule_type"] = "SCHEDULE_START_END",
                ["schedule_start_time"] = c.StartAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["schedule_end_time"] = c.EndAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["optimization_goal"] = c.Objective switch { AdObjective.Awareness => "REACH", AdObjective.VideoViews => "ENGAGED_VIEW", _ => "CLICK" },
                ["billing_event"] = c.Objective switch { AdObjective.Awareness => "CPM", AdObjective.VideoViews => "CPV", _ => "CPC" },
                ["bid_type"] = "BID_TYPE_NO_BID", ["pacing"] = "PACING_MODE_SMOOTH", ["operation_status"] = "DISABLE",
            };
            if (c.Objective == AdObjective.Traffic) adgroup["promotion_type"] = "WEBSITE";
            ids["adgroup"] = SocialHttp.Str(await SendAsync(HttpMethod.Post, "adgroup/create/", token, adgroup, ct), "adgroup_id");

            var creative = new JsonObject
            {
                ["ad_name"] = c.Name, ["identity_type"] = "CUSTOMIZED_USER", ["identity_id"] = identityId, ["ad_format"] = "SINGLE_VIDEO",
                ["video_id"] = videoId, ["image_ids"] = new JsonArray(coverId), ["ad_text"] = c.Creative.PrimaryText,
                ["call_to_action"] = c.Creative.CallToAction,
            };
            if (!string.IsNullOrEmpty(c.Creative.LinkUrl)) creative["landing_page_url"] = c.Creative.LinkUrl;
            var ad = await SendAsync(HttpMethod.Post, "ad/create/", token, new JsonObject
            {
                ["advertiser_id"] = adv, ["adgroup_id"] = ids["adgroup"], ["creatives"] = new JsonArray(creative),
            }, ct);
            ids["ad"] = ad["ad_ids"]?.AsArray().FirstOrDefault()?.ToString() ?? throw AdHttp.Error("TikTok の応答に ad_ids がありません");

            await SetStatusAsync(adv, token, ids, "ENABLE", ct);
            return new AdSubmitResult(ids, AdStatus.InReview, accountExtra);
        }
        catch when (ids.ContainsKey("campaign"))
        {
            await AdHttp.CleanupAsync(() => SendAsync(HttpMethod.Post, "campaign/status/update/", token, new JsonObject
            {
                ["advertiser_id"] = adv, ["campaign_ids"] = new JsonArray(ids["campaign"]), ["operation_status"] = "DELETE",
            }, ct));
            throw;
        }
    }

    public Task SetPausedAsync(AdCampaign campaign, AdAccount account, StoredToken token, bool paused, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "campaign/status/update/", token.AccessToken, new JsonObject
        {
            ["advertiser_id"] = account.ExternalAccountId, ["campaign_ids"] = new JsonArray(campaign.ExternalIds["campaign"]),
            ["operation_status"] = paused ? "DISABLE" : "ENABLE",
        }, ct);

    public async Task<AdRemoteState> GetStateAsync(AdCampaign campaign, AdAccount account, StoredToken token, DateTimeOffset now, CancellationToken ct)
    {
        var adv = account.ExternalAccountId;
        var ads = await SendAsync(HttpMethod.Get, "ad/get/?" + SocialHttp.Query(("advertiser_id", adv),
            ("filtering", new JsonObject { ["ad_ids"] = new JsonArray(campaign.ExternalIds["ad"]) }.ToJsonString())), token.AccessToken, null, ct);
        var ad = ads["list"]?.AsArray().FirstOrDefault();
        var report = await SendAsync(HttpMethod.Get, "report/integrated/get/?" + SocialHttp.Query(("advertiser_id", adv),
            ("report_type", "BASIC"), ("data_level", "AUCTION_CAMPAIGN"), ("dimensions", "[\"campaign_id\"]"),
            ("metrics", "[\"impressions\",\"clicks\",\"spend\",\"reach\",\"video_play_actions\"]"), ("query_lifetime", "true"),
            ("filtering", new JsonArray(new JsonObject
            {
                ["field_name"] = "campaign_ids", ["filter_type"] = "IN", ["filter_value"] = $"[\"{campaign.ExternalIds["campaign"]}\"]",
            }).ToJsonString())), token.AccessToken, null, ct);
        var m = report["list"]?.AsArray().FirstOrDefault()?["metrics"];
        var results = m is null ? null : new AdResults(AdHttp.Long(m, "impressions"), AdHttp.Long(m, "clicks"), AdHttp.Dec(m, "spend"),
            AdHttp.Long(m, "reach"), AdHttp.Long(m, "video_play_actions"), now);
        var reject = (ad?["reject_info"] ?? ad?["reject_reasons"])?.ToJsonString();
        return new AdRemoteState(Status(SocialHttp.StrOrNull(ad, "secondary_status"), SocialHttp.StrOrNull(ad, "operation_status")), results,
            string.IsNullOrEmpty(reject) || reject is "[]" or "null" ? null : reject);
    }

    public static AdStatus Status(string? secondary, string? operation)
    {
        var s = secondary ?? "";
        if (s.Contains("NOT_APPROVE", StringComparison.Ordinal) || s.Contains("REJECT", StringComparison.Ordinal)) return AdStatus.Rejected;
        if (operation == "DISABLE" || s.Contains("DISABLE", StringComparison.Ordinal)) return AdStatus.Paused;
        if (s.Contains("REVIEW", StringComparison.Ordinal) || s.Contains("AUDIT", StringComparison.Ordinal)) return AdStatus.InReview;
        if (s.Contains("TIME_DONE", StringComparison.Ordinal) || s.Contains("DONE", StringComparison.Ordinal)) return AdStatus.Completed;
        return AdStatus.Active;
    }

    /// <summary>年齢の範囲に重なる TikTok の年齢層。</summary>
    public static IReadOnlyList<string> AgeGroups(AdTargeting t)
    {
        (int Min, int Max, string Code)[] groups =
            [(18, 24, "AGE_18_24"), (25, 34, "AGE_25_34"), (35, 44, "AGE_35_44"), (45, 54, "AGE_45_54"), (55, 100, "AGE_55_100")];
        return groups.Where(g => g.Max >= t.AgeMin && g.Min <= t.AgeMax).Select(g => g.Code).ToList();
    }

    private static string Objective(AdObjective o) => o switch
    {
        AdObjective.Awareness => "REACH",
        AdObjective.VideoViews => "VIDEO_VIEWS",
        _ => "TRAFFIC",
    };

    private static string PostText(string text, int max) => text.Length <= max ? text : text[..max];

    private Task SetStatusAsync(string adv, string token, Dictionary<string, string> ids, string status, CancellationToken ct) =>
        Task.WhenAll(
            SendAsync(HttpMethod.Post, "ad/status/update/", token, new JsonObject
                { ["advertiser_id"] = adv, ["ad_ids"] = new JsonArray(ids["ad"]), ["operation_status"] = status }, ct),
            SendAsync(HttpMethod.Post, "adgroup/status/update/", token, new JsonObject
                { ["advertiser_id"] = adv, ["adgroup_ids"] = new JsonArray(ids["adgroup"]), ["operation_status"] = status }, ct),
            SendAsync(HttpMethod.Post, "campaign/status/update/", token, new JsonObject
                { ["advertiser_id"] = adv, ["campaign_ids"] = new JsonArray(ids["campaign"]), ["operation_status"] = status }, ct));

    /// <summary>アップロードの応答の data は配列のことも、オブジェクトのこともある。</summary>
    private static string Id(JsonNode data, string key) =>
        SocialHttp.StrOrNull(data is JsonArray a ? a.FirstOrDefault() : data, key) ?? throw AdHttp.Error($"TikTok の応答に {key} がありません");

    private async Task<JsonNode> UploadAsync(string path, string advertiserId, string kind, byte[] bytes, string mime, string token, CancellationToken ct)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        var form = new MultipartFormDataContent
        {
            { new StringContent(advertiserId), "advertiser_id" },
            { new StringContent("UPLOAD_BY_FILE"), "upload_type" },
            { new StringContent(AdHttp.Md5Hex(bytes)), $"{kind}_signature" },
            { file, $"{kind}_file", $"ad.{AdHttp.Ext(mime)}" },
        };
        return await EnvelopeAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = form }, token, ct);
    }

    private Task<JsonNode> SendAsync(HttpMethod method, string path, string? token, JsonObject? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = System.Net.Http.Json.JsonContent.Create(body);
        return EnvelopeAsync(request, token, ct);
    }

    private async Task<JsonNode> EnvelopeAsync(HttpRequestMessage request, string? token, CancellationToken ct)
    {
        if (token is not null) request.Headers.Add("Access-Token", token);
        var json = await SocialHttp.SendAsync(http.CreateClient(HttpClientName), request, Name, ct);
        var code = SocialHttp.StrOrNull(json, "code");
        if (code is null or "0") return json["data"] ?? new JsonObject();
        var message = SocialHttp.StrOrNull(json, "message") ?? $"code {code}";
        throw code switch
        {
            // アクセストークンが無効・取り消し
            "40104" or "40105" => new SocialApiException(ErrorCodes.SnsReauthRequired, $"{Name}の再連携が必要です（{message}）", false),
            // 呼び出しの上限・一時的な障害
            "40100" or "50000" or "50002" or "51021" => new SocialApiException(SocialHttp.TransientCode, $"{Name}が一時的に利用できません（{message}）", true),
            _ => new SocialApiException(ErrorCodes.PubFailed, $"{Name}：{message}", false),
        };
    }
}

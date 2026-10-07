using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReachForge.Application.Ads;
using ReachForge.Application.Social;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Social.Meta;

namespace ReachForge.Social.Ads;

/// <summary>
/// Meta 広告（Marketing API）。Facebook・Instagram の広告を、キャンペーン → 広告セット（予算・期間・対象）→ クリエイティブ → 広告 の順につくる。
/// すべて停止（PAUSED）でつくり、最後に配信を始める。途中で失敗したらキャンペーンごと削除する。
/// アプリは SNS 連携と同じ（Social:Meta）で、広告の権限（ads_management）は Meta のアプリ審査で承認が必要。
/// </summary>
public sealed class MetaAdAdapter(IHttpClientFactory http, IOptions<SocialOptions> social, IOptions<AdsOptions> ads, TimeProvider clock)
    : IAdNetworkAdapter
{
    private const string Name = "Meta";
    private MetaOptions O => social.Value.Meta;
    private string V => O.GraphVersion;

    public AdNetwork Network => AdNetwork.Meta;
    public bool IsSimulation => false;
    public bool IsConfigured => O.IsConfigured;
    public IReadOnlyList<SocialPlatform> Platforms => AdHttp.PlatformsOf(AdNetwork.Meta);

    /// <summary>1日の予算の下限の目安（実際の下限は Meta が通貨・目的ごとに決め、下回ると出稿時にエラーになる）。</summary>
    public decimal MinDailyBudget(string currency) => currency == "JPY" ? 200 : 2;
    public bool RequiresVideo(SocialPlatform platform) => false;

    public Task<AdAuthorizationStart> BeginAuthorizationAsync(string state, string codeChallenge, string redirectUri, CancellationToken ct) =>
        Task.FromResult(new AdAuthorizationStart($"{O.DialogBaseUrl}{V}/dialog/oauth?" + SocialHttp.Query(
            ("client_id", O.AppId), ("redirect_uri", redirectUri), ("state", state), ("response_type", "code"), ("scope", ads.Value.Meta.Scopes))));

    public async Task<AdConnectResult> ExchangeAsync(string code, string stateSecret, string redirectUri, CancellationToken ct)
    {
        var client = Client;
        var shortLived = await SocialHttp.SendAsync(client, SocialHttp.Get($"{V}/oauth/access_token?" + SocialHttp.Query(
            ("client_id", O.AppId), ("client_secret", O.AppSecret), ("redirect_uri", redirectUri), ("code", code))), Name, ct);
        var token = await LongLivedAsync(SocialHttp.Str(shortLived, "access_token"), ct);

        var list = await SocialHttp.SendAsync(client, SocialHttp.Get($"{V}/me/adaccounts?fields=id,name,currency,account_status&limit=100",
            token.AccessToken), Name, ct);
        var accounts = (list["data"]?.AsArray() ?? [])
            .Where(a => SocialHttp.StrOrNull(a, "account_status") == "1") // 1 = 有効
            .Select(a => new AdAccountInfo(SocialHttp.Str(a, "id"), SocialHttp.StrOrNull(a, "name") ?? SocialHttp.Str(a, "id"),
                SocialHttp.StrOrNull(a, "currency") ?? "JPY"))
            .ToList();
        return new AdConnectResult(token, accounts);
    }

    /// <summary>長期トークン（約60日）は、期限内なら交換し直して延ばせる。</summary>
    public async Task<StoredToken> RefreshAsync(StoredToken token, CancellationToken ct)
    {
        if (token.ExpiresAt is { } e && e <= clock.GetUtcNow())
        {
            throw new SocialApiException(ErrorCodes.SnsReauthRequired, "Meta 広告の再連携が必要です", isTransient: false);
        }
        return await LongLivedAsync(token.AccessToken, ct);
    }

    private async Task<StoredToken> LongLivedAsync(string accessToken, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(Client, SocialHttp.Get($"{V}/oauth/access_token?" + SocialHttp.Query(
            ("grant_type", "fb_exchange_token"), ("client_id", O.AppId), ("client_secret", O.AppSecret), ("fb_exchange_token", accessToken))), Name, ct);
        return new StoredToken(SocialHttp.Str(json, "access_token"), null,
            SocialHttp.ExpiresAt(json, clock.GetUtcNow()) ?? clock.GetUtcNow().AddDays(60));
    }

    public async Task<AdSubmitResult> SubmitAsync(AdSubmission s, CancellationToken ct)
    {
        var c = s.Campaign;
        var act = s.Account.ExternalAccountId;
        var token = s.Token.AccessToken;
        if (string.IsNullOrEmpty(s.PageId))
        {
            throw new DomainException(ErrorCodes.Validation, "Meta の広告には Facebook ページが必要です。先に「SNS連携」で Facebook ページを連携してください。");
        }
        if (s.Media is null) throw new DomainException(ErrorCodes.Validation, "広告に使う画像か動画を選んでください。");
        if (c.Platform == SocialPlatform.Instagram && string.IsNullOrEmpty(s.InstagramUserId))
        {
            throw new DomainException(ErrorCodes.Validation, "Instagram の広告には Instagram の連携が必要です。先に「SNS連携」で Instagram を連携してください。");
        }

        // 素材のアップロード（キャンペーンをつくる前に行い、失敗しても何も残らないようにする）
        var bytes = await s.Media.ReadAsync(ct);
        string? imageHash = null, videoId = null;
        if (AdHttp.IsVideo(s.Media))
        {
            videoId = SocialHttp.Str(await UploadAsync($"{V}/{act}/advideos", "source", bytes, s.Media.Mime, token, ct), "id");
            if (s.Thumbnail is { } thumb) imageHash = await UploadImageAsync(act, thumb, "image/jpeg", token, ct);
        }
        else
        {
            imageHash = await UploadImageAsync(act, bytes, s.Media.Mime, token, ct);
        }

        var ids = new Dictionary<string, string>();
        try
        {
            ids["campaign"] = SocialHttp.Str(await PostAsync($"{V}/{act}/campaigns", token, ct,
                ("name", c.Name), ("objective", Objective(c.Objective)), ("status", "PAUSED"),
                ("special_ad_categories", "[]"), ("is_adset_budget_sharing_enabled", "false")), "id");

            var targeting = new JsonObject
            {
                ["geo_locations"] = new JsonObject { ["countries"] = new JsonArray(c.Targeting.Country) },
                ["age_min"] = c.Targeting.AgeMin,
                ["age_max"] = c.Targeting.AgeMax,
                ["publisher_platforms"] = new JsonArray(c.Platform == SocialPlatform.Instagram ? "instagram" : "facebook"),
            };
            if (c.Targeting.Gender != AdGender.All) targeting["genders"] = new JsonArray(c.Targeting.Gender == AdGender.Male ? 1 : 2);
            var adset = new List<(string, string?)>
            {
                ("name", c.Name), ("campaign_id", ids["campaign"]), ("status", "PAUSED"),
                ("daily_budget", AdHttp.MinorUnits(c.DailyBudget, c.Currency).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("billing_event", "IMPRESSIONS"), ("optimization_goal", OptimizationGoal(c.Objective)),
                ("bid_strategy", "LOWEST_COST_WITHOUT_CAP"), ("start_time", AdHttp.Iso(c.StartAt)), ("end_time", AdHttp.Iso(c.EndAt)),
                ("targeting", targeting.ToJsonString()),
            };
            if (c.Objective == AdObjective.Traffic) adset.Add(("destination_type", "WEBSITE"));
            ids["adset"] = SocialHttp.Str(await PostAsync($"{V}/{act}/adsets", token, ct, [.. adset]), "id");

            var link = string.IsNullOrEmpty(c.Creative.LinkUrl) ? $"https://www.facebook.com/{s.PageId}" : c.Creative.LinkUrl;
            var cta = new JsonObject { ["type"] = c.Creative.CallToAction, ["value"] = new JsonObject { ["link"] = link } };
            var story = new JsonObject { ["page_id"] = s.PageId };
            if (c.Platform == SocialPlatform.Instagram) story["instagram_user_id"] = s.InstagramUserId;
            if (videoId is not null)
            {
                var video = new JsonObject { ["video_id"] = videoId, ["message"] = c.Creative.PrimaryText, ["title"] = c.Creative.Headline, ["call_to_action"] = cta };
                if (imageHash is not null) video["image_hash"] = imageHash;
                story["video_data"] = video;
            }
            else
            {
                story["link_data"] = new JsonObject
                {
                    ["message"] = c.Creative.PrimaryText, ["link"] = link, ["name"] = c.Creative.Headline,
                    ["description"] = c.Creative.Description, ["image_hash"] = imageHash, ["call_to_action"] = cta,
                };
            }
            ids["creative"] = SocialHttp.Str(await PostAsync($"{V}/{act}/adcreatives", token, ct,
                ("name", c.Name), ("object_story_spec", story.ToJsonString())), "id");
            ids["ad"] = SocialHttp.Str(await PostAsync($"{V}/{act}/ads", token, ct,
                ("name", c.Name), ("adset_id", ids["adset"]), ("creative", new JsonObject { ["creative_id"] = ids["creative"] }.ToJsonString()),
                ("status", "PAUSED")), "id");

            // 全部できたら配信を始める（Meta の審査が終わると配信される）
            foreach (var key in new[] { "ad", "adset", "campaign" })
            {
                await PostAsync($"{V}/{ids[key]}", token, ct, ("status", "ACTIVE"));
            }
            return new AdSubmitResult(ids, AdStatus.InReview);
        }
        catch when (ids.TryGetValue("campaign", out var campaignId))
        {
            await AdHttp.CleanupAsync(() => SocialHttp.SendAsync(Client, new HttpRequestMessage(HttpMethod.Delete, $"{V}/{campaignId}")
                { Headers = { Authorization = new("Bearer", token) } }, Name, ct));
            throw;
        }
    }

    public async Task SetPausedAsync(AdCampaign campaign, AdAccount account, StoredToken token, bool paused, CancellationToken ct) =>
        await PostAsync($"{V}/{campaign.ExternalIds["campaign"]}", token.AccessToken, ct, ("status", paused ? "PAUSED" : "ACTIVE"));

    public async Task<AdRemoteState> GetStateAsync(AdCampaign campaign, AdAccount account, StoredToken token, DateTimeOffset now, CancellationToken ct)
    {
        var ad = await SocialHttp.SendAsync(Client, SocialHttp.Get($"{V}/{campaign.ExternalIds["ad"]}?fields=effective_status,ad_review_feedback",
            token.AccessToken), Name, ct);
        var insights = await SocialHttp.SendAsync(Client, SocialHttp.Get(
            $"{V}/{campaign.ExternalIds["campaign"]}/insights?fields=impressions,clicks,spend,reach,video_thruplay_watched_actions&date_preset=maximum",
            token.AccessToken), Name, ct);
        var row = insights["data"]?.AsArray().FirstOrDefault();
        var results = row is null ? null : new AdResults(AdHttp.Long(row, "impressions"), AdHttp.Long(row, "clicks"), AdHttp.Dec(row, "spend"),
            AdHttp.Long(row, "reach"), AdHttp.Long(row?["video_thruplay_watched_actions"]?.AsArray().FirstOrDefault(), "value"), now);
        var feedback = ad["ad_review_feedback"]?["global"] is JsonObject g ? string.Join(" / ", g.Select(p => $"{p.Key}: {p.Value}")) : null;
        return new AdRemoteState(Status(SocialHttp.StrOrNull(ad, "effective_status")), results, feedback);
    }

    public static AdStatus Status(string? effectiveStatus) => effectiveStatus switch
    {
        "ACTIVE" => AdStatus.Active,
        "PAUSED" or "CAMPAIGN_PAUSED" or "ADSET_PAUSED" => AdStatus.Paused,
        "PENDING_REVIEW" or "IN_PROCESS" or "PREAPPROVED" or "PENDING_BILLING_INFO" => AdStatus.InReview,
        "DISAPPROVED" or "WITH_ISSUES" => AdStatus.Rejected,
        "ARCHIVED" or "DELETED" => AdStatus.Completed,
        _ => AdStatus.InReview,
    };

    public static string Objective(AdObjective o) => o switch
    {
        AdObjective.Awareness => "OUTCOME_AWARENESS",
        AdObjective.VideoViews => "OUTCOME_ENGAGEMENT",
        _ => "OUTCOME_TRAFFIC",
    };

    private static string OptimizationGoal(AdObjective o) => o switch
    {
        AdObjective.Awareness => "REACH",
        AdObjective.VideoViews => "THRUPLAY",
        _ => "LINK_CLICKS",
    };

    private HttpClient Client => http.CreateClient(MetaConnector.HttpClientName);

    private Task<JsonNode> PostAsync(string path, string token, CancellationToken ct, params (string Key, string? Value)[] fields) =>
        SocialHttp.SendAsync(Client, SocialHttp.Form(HttpMethod.Post, path,
            fields.Where(f => f.Value is not null).Select(f => KeyValuePair.Create(f.Key, f.Value!)), token), Name, ct);

    private async Task<string> UploadImageAsync(string act, byte[] bytes, string mime, string token, CancellationToken ct)
    {
        var json = await UploadAsync($"{V}/{act}/adimages", "filename", bytes, mime, token, ct);
        return (json["images"] as JsonObject)?.Select(p => SocialHttp.StrOrNull(p.Value, "hash")).FirstOrDefault(h => h is not null)
               ?? throw AdHttp.Error("Meta の応答に画像の hash がありません");
    }

    private Task<JsonNode> UploadAsync(string path, string field, byte[] bytes, string mime, string token, CancellationToken ct)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        var form = new MultipartFormDataContent { { file, field, $"ad.{AdHttp.Ext(mime)}" } };
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        request.Headers.Authorization = new("Bearer", token);
        return SocialHttp.SendAsync(Client, request, Name, ct);
    }
}

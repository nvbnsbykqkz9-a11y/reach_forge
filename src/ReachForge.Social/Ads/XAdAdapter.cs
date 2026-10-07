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
/// X 広告（X Ads API v12、OAuth 1.0a）。キャンペーン（1日の予算）→ 広告グループ（line item：期間・目的）→ 対象 → 広告用のポスト（タイムラインに出ない）→ プロモ。
/// すべて停止（PAUSED）でつくり、最後に配信を始める。途中で失敗したらキャンペーンを削除する。
/// 支払い方法（funding instrument）は X 広告の管理画面で登録しておく必要がある。
/// </summary>
public sealed class XAdAdapter(IHttpClientFactory http, IOptions<AdsOptions> options) : IAdNetworkAdapter
{
    public const string HttpClientName = "ads-x";
    private const string Name = "X 広告";
    private XAdsOptions O => options.Value.X;

    public AdNetwork Network => AdNetwork.X;
    public bool IsSimulation => false;
    public bool IsConfigured => O.IsConfigured;
    public IReadOnlyList<SocialPlatform> Platforms => AdHttp.PlatformsOf(AdNetwork.X);
    public decimal MinDailyBudget(string currency) => currency == "JPY" ? 100 : 1;
    public bool RequiresVideo(SocialPlatform platform) => false;

    /// <summary>
    /// リクエストトークンを取り、認可画面の URL を返す。X は戻り先に oauth_token を付けて戻すため、それを連携の目印（StateKey）にし、
    /// トークンの secret を StateSecret として覚えておく。
    /// </summary>
    public async Task<AdAuthorizationStart> BeginAuthorizationAsync(string state, string codeChallenge, string redirectUri, CancellationToken ct)
    {
        var form = await FormAsync(HttpMethod.Post, $"{O.ApiBaseUrl}oauth/request_token", null, null, [new("oauth_callback", redirectUri)], [], ct);
        var requestToken = form["oauth_token"];
        return new AdAuthorizationStart($"{O.ApiBaseUrl}oauth/authorize?oauth_token={Uri.EscapeDataString(requestToken)}",
            form["oauth_token_secret"], "ad_x_" + requestToken);
    }

    /// <param name="code">「oauth_token:oauth_verifier」（戻り先のエンドポイントが組み立てる）。</param>
    public async Task<AdConnectResult> ExchangeAsync(string code, string stateSecret, string redirectUri, CancellationToken ct)
    {
        var parts = code.Split(':', 2);
        if (parts.Length != 2) throw new DomainException(ErrorCodes.SnsAuthCanceled, "X の認可の結果を読み取れませんでした。もう一度お試しください。");
        var form = await FormAsync(HttpMethod.Post, $"{O.ApiBaseUrl}oauth/access_token", parts[0], stateSecret, [],
            [new("oauth_verifier", parts[1])], ct);
        var token = new StoredToken(form["oauth_token"], null, null, new Dictionary<string, string>
        {
            ["secret"] = form["oauth_token_secret"],
            ["user_id"] = form.GetValueOrDefault("user_id") ?? "",
        });

        var list = await SendAsync(HttpMethod.Get, "accounts?count=100", token, null, ct);
        var accounts = new List<AdAccountInfo>();
        foreach (var a in list["data"]?.AsArray() ?? [])
        {
            var id = SocialHttp.Str(a, "id");
            var funding = await SendAsync(HttpMethod.Get, $"accounts/{id}/funding_instruments", token, null, ct);
            var fi = funding["data"]?.AsArray().FirstOrDefault(f => SocialHttp.StrOrNull(f, "able_to_fund") == "true" && SocialHttp.StrOrNull(f, "deleted") != "true");
            if (fi is null) continue; // 支払い方法がない広告アカウントでは出稿できない
            accounts.Add(new AdAccountInfo(id, SocialHttp.StrOrNull(a, "name") ?? id, SocialHttp.StrOrNull(fi, "currency") ?? "JPY",
                new Dictionary<string, string> { ["fundingInstrumentId"] = SocialHttp.Str(fi, "id") }));
        }
        return new AdConnectResult(token, accounts);
    }

    public async Task<AdSubmitResult> SubmitAsync(AdSubmission s, CancellationToken ct)
    {
        var c = s.Campaign;
        var acc = s.Account.ExternalAccountId;
        var token = s.Token;
        var userId = token.Get("user_id") is { Length: > 0 } u ? u : s.SocialUserId
                     ?? throw new DomainException(ErrorCodes.Validation, "X の広告アカウントを連携し直してください（投稿するユーザーが分かりません）。");
        if (!s.Account.Extra.TryGetValue("fundingInstrumentId", out var funding))
        {
            throw new DomainException(ErrorCodes.Validation, "X 広告の支払い方法が見つかりません。X 広告の管理画面で支払い方法を登録してから、連携し直してください。");
        }

        string? mediaKey = null;
        if (s.Media is not null)
        {
            mediaKey = await UploadMediaAsync(s.Media, token, ct);
            await SendAsync(HttpMethod.Post, $"accounts/{acc}/media_library?" + SocialHttp.Query(("media_key", mediaKey)), token, null, ct);
        }
        var country = await SendAsync(HttpMethod.Get, "targeting_criteria/locations?" + SocialHttp.Query(
            ("location_type", "COUNTRIES"), ("country_code", c.Targeting.Country)), token, null, ct);
        var locationKey = SocialHttp.StrOrNull(country["data"]?.AsArray().FirstOrDefault(), "targeting_value")
                          ?? throw AdHttp.Error("X から配信する国の情報を取得できませんでした");

        var ids = new Dictionary<string, string>();
        try
        {
            ids["campaign"] = SocialHttp.Str(await SendAsync(HttpMethod.Post, $"accounts/{acc}/campaigns?" + SocialHttp.Query(
                ("funding_instrument_id", funding), ("name", c.Name), ("entity_status", "PAUSED"),
                ("daily_budget_amount_local_micro", AdHttp.Micros(c.DailyBudget).ToString(System.Globalization.CultureInfo.InvariantCulture))),
                token, null, ct), "data.id");
            ids["line_item"] = SocialHttp.Str(await SendAsync(HttpMethod.Post, $"accounts/{acc}/line_items?" + SocialHttp.Query(
                ("campaign_id", ids["campaign"]), ("name", c.Name), ("objective", Objective(c.Objective)),
                ("placements", "ALL_ON_TWITTER"), ("product_type", "PROMOTED_TWEETS"), ("bid_strategy", "AUTO"),
                ("start_time", AdHttp.Iso(c.StartAt)), ("end_time", AdHttp.Iso(c.EndAt)), ("entity_status", "PAUSED")),
                token, null, ct), "data.id");

            var criteria = new JsonArray(Criterion(ids["line_item"], "LOCATION", locationKey), Criterion(ids["line_item"], "AGE", AgeBucket(c.Targeting)));
            if (c.Targeting.Gender != AdGender.All) criteria.Add(Criterion(ids["line_item"], "GENDER", c.Targeting.Gender == AdGender.Male ? "1" : "2"));
            await SendAsync(HttpMethod.Post, $"batch/accounts/{acc}/targeting_criteria", token, criteria, ct);

            // 広告専用のポスト（nullcast：フォロワーのタイムラインには出ない）
            var text = c.Creative.PrimaryText + (string.IsNullOrEmpty(c.Creative.LinkUrl) ? "" : "\n" + c.Creative.LinkUrl);
            ids["tweet"] = SocialHttp.Str(await SendAsync(HttpMethod.Post, $"accounts/{acc}/tweet?" + SocialHttp.Query(
                ("as_user_id", userId), ("text", text), ("nullcast", "true"), ("media_keys", mediaKey)), token, null, ct), "data.id_str");
            var promoted = await SendAsync(HttpMethod.Post, $"accounts/{acc}/promoted_tweets?" + SocialHttp.Query(
                ("line_item_id", ids["line_item"]), ("tweet_ids", ids["tweet"])), token, null, ct);
            ids["promoted_tweet"] = SocialHttp.Str(promoted["data"]?.AsArray().FirstOrDefault(), "id");

            await SendAsync(HttpMethod.Put, $"accounts/{acc}/line_items/{ids["line_item"]}?entity_status=ACTIVE", token, null, ct);
            await SendAsync(HttpMethod.Put, $"accounts/{acc}/campaigns/{ids["campaign"]}?entity_status=ACTIVE", token, null, ct);
            return new AdSubmitResult(ids, AdStatus.InReview);
        }
        catch when (ids.ContainsKey("campaign"))
        {
            await AdHttp.CleanupAsync(() => SendAsync(HttpMethod.Delete, $"accounts/{acc}/campaigns/{ids["campaign"]}", token, null, ct));
            throw;
        }
    }

    public Task SetPausedAsync(AdCampaign campaign, AdAccount account, StoredToken token, bool paused, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"accounts/{account.ExternalAccountId}/campaigns/{campaign.ExternalIds["campaign"]}?entity_status={(paused ? "PAUSED" : "ACTIVE")}",
            token, null, ct);

    /// <summary>状態と成果。X の集計は1回で7日分までのため、成果は直近7日間の値。</summary>
    public async Task<AdRemoteState> GetStateAsync(AdCampaign campaign, AdAccount account, StoredToken token, DateTimeOffset now, CancellationToken ct)
    {
        var acc = account.ExternalAccountId;
        var campaignJson = await SendAsync(HttpMethod.Get, $"accounts/{acc}/campaigns/{campaign.ExternalIds["campaign"]}", token, null, ct);
        var promoted = await SendAsync(HttpMethod.Get, $"accounts/{acc}/promoted_tweets/{campaign.ExternalIds["promoted_tweet"]}", token, null, ct);

        var end = new DateTimeOffset(now.UtcDateTime.Date.AddHours(now.UtcDateTime.Hour + 1), TimeSpan.Zero);
        var startCandidate = end.AddDays(-7);
        var start = campaign.StartAt > startCandidate ? new DateTimeOffset(campaign.StartAt.UtcDateTime.Date.AddHours(campaign.StartAt.UtcDateTime.Hour), TimeSpan.Zero) : startCandidate;
        AdResults? results = null;
        if (start < end)
        {
            var stats = await SendAsync(HttpMethod.Get, $"stats/accounts/{acc}?" + SocialHttp.Query(("entity", "LINE_ITEM"),
                ("entity_ids", campaign.ExternalIds["line_item"]), ("start_time", AdHttp.Iso(start)), ("end_time", AdHttp.Iso(end)),
                ("granularity", "TOTAL"), ("metric_groups", "ENGAGEMENT,BILLING,VIDEO"), ("placement", "ALL_ON_TWITTER")), token, null, ct);
            var m = stats["data"]?.AsArray().FirstOrDefault()?["id_data"]?.AsArray().FirstOrDefault()?["metrics"];
            if (m is not null)
            {
                results = new AdResults(Sum(m["impressions"]), Sum(m["clicks"]), AdHttp.FromMicros(Sum(m["billed_charge_local_micro"])), 0,
                    Sum(m["video_total_views"]), now);
            }
        }
        return new AdRemoteState(Status(SocialHttp.StrOrNull(campaignJson, "data.entity_status"), SocialHttp.StrOrNull(promoted, "data.approval_status")),
            results, SocialHttp.StrOrNull(promoted, "data.approval_status") == "REJECTED" ? "X の審査で承認されませんでした" : null);
    }

    public static AdStatus Status(string? entityStatus, string? approvalStatus) => (entityStatus, approvalStatus) switch
    {
        (_, "REJECTED") => AdStatus.Rejected,
        ("PAUSED", _) => AdStatus.Paused,
        (_, "UNDER_REVIEW") => AdStatus.InReview,
        ("ACTIVE", _) => AdStatus.Active,
        _ => AdStatus.InReview,
    };

    /// <summary>年齢の範囲を含む X の年齢層（いちばん狭いもの。なければ「〜歳以上」）。</summary>
    public static string AgeBucket(AdTargeting t)
    {
        (int Min, int Max)[] ranges = [(18, 24), (18, 34), (18, 49), (21, 34), (21, 49), (25, 34), (25, 49), (25, 54), (35, 49), (35, 54)];
        if (t.AgeMax < AdTargeting.MaxAge)
        {
            var fit = ranges.Where(r => r.Min <= t.AgeMin && r.Max >= t.AgeMax).OrderBy(r => r.Max - r.Min).ToList();
            if (fit.Count > 0) return $"AGE_{fit[0].Min}_TO_{fit[0].Max}";
        }
        int[] over = [18, 21, 25, 35, 50];
        return $"AGE_OVER_{over.Where(o => o <= t.AgeMin).DefaultIfEmpty(18).Max()}";
    }

    private static string Objective(AdObjective o) => o switch
    {
        AdObjective.Awareness => "REACH",
        AdObjective.VideoViews => "VIDEO_VIEWS",
        _ => "WEBSITE_CLICKS",
    };

    private static JsonObject Criterion(string lineItemId, string type, string value) => new()
    {
        ["operation_type"] = "Create",
        ["params"] = new JsonObject { ["line_item_id"] = lineItemId, ["targeting_type"] = type, ["targeting_value"] = value },
    };

    private static long Sum(JsonNode? series) =>
        series is JsonArray a ? a.Sum(v => v is JsonValue jv && jv.TryGetValue<long>(out var n) ? n : 0) : 0;

    /// <summary>画像は1回で、動画は分割して送る（v2 のメディアのアップロード）。</summary>
    private async Task<string> UploadMediaAsync(PublishMedia media, StoredToken token, CancellationToken ct)
    {
        var bytes = await media.ReadAsync(ct);
        var baseUrl = $"{O.ApiBaseUrl}2/media/upload";
        if (!AdHttp.IsVideo(media))
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(media.Mime);
            var form = new MultipartFormDataContent { { file, "media", $"ad.{AdHttp.Ext(media.Mime)}" }, { new StringContent("tweet_image"), "media_category" } };
            return SocialHttp.Str(await SendAbsoluteAsync(HttpMethod.Post, baseUrl, token, form, ct), "data.media_key");
        }

        var init = await SendAbsoluteAsync(HttpMethod.Post, $"{baseUrl}/initialize", token, System.Net.Http.Json.JsonContent.Create(new JsonObject
        {
            ["media_type"] = media.Mime, ["total_bytes"] = bytes.Length, ["media_category"] = "amplify_video",
        }), ct);
        var id = SocialHttp.Str(init, "data.id");
        const int chunk = 4 * 1024 * 1024;
        for (int offset = 0, index = 0; offset < bytes.Length; offset += chunk, index++)
        {
            var part = new ByteArrayContent(bytes, offset, Math.Min(chunk, bytes.Length - offset));
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            await SendAbsoluteAsync(HttpMethod.Post, $"{baseUrl}/{id}/append", token,
                new MultipartFormDataContent { { part, "media", "chunk" }, { new StringContent(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), "segment_index" } }, ct);
        }
        var result = await SendAbsoluteAsync(HttpMethod.Post, $"{baseUrl}/{id}/finalize", token, null, ct);
        for (var i = 0; i < 120 && SocialHttp.StrOrNull(result, "data.processing_info.state") is "pending" or "in_progress"; i++)
        {
            var wait = AdHttp.Long(result, "data.processing_info.check_after_secs");
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 1, 10)), ct);
            result = await SendAbsoluteAsync(HttpMethod.Get, $"{baseUrl}?command=STATUS&media_id={id}", token, null, ct);
        }
        if (SocialHttp.StrOrNull(result, "data.processing_info.state") == "failed") throw AdHttp.Error("X で動画の処理に失敗しました");
        return SocialHttp.StrOrNull(result, "data.media_key") ?? SocialHttp.Str(init, "data.media_key");
    }

    private Task<JsonNode> SendAsync(HttpMethod method, string path, StoredToken token, JsonNode? body, CancellationToken ct) =>
        SendAbsoluteAsync(method, O.AdsApiBaseUrl + path, token, body is null ? null : System.Net.Http.Json.JsonContent.Create(body), ct);

    private Task<JsonNode> SendAbsoluteAsync(HttpMethod method, string url, StoredToken token, HttpContent? content, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", OAuth1Signer.AuthorizationHeader(method.Method, new Uri(url), null,
            O.ConsumerKey!, O.ConsumerSecret!, token.AccessToken, token.Get("secret")));
        return SocialHttp.SendAsync(http.CreateClient(HttpClientName), request, Name, ct);
    }

    /// <summary>OAuth のトークンのやりとり（応答はフォーム形式）。</summary>
    private async Task<Dictionary<string, string>> FormAsync(HttpMethod method, string url, string? token, string? tokenSecret,
        IReadOnlyList<KeyValuePair<string, string>> extraOAuth, IReadOnlyList<KeyValuePair<string, string>> fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.TryAddWithoutValidation("Authorization", OAuth1Signer.AuthorizationHeader(method.Method, new Uri(url), fields,
            O.ConsumerKey!, O.ConsumerSecret!, token, tokenSecret, extraOAuth));
        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SocialApiException(SocialHttp.TransientCode, $"X に接続できませんでした（{ex.Message}）", isTransient: true);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new SocialApiException(ErrorCodes.SnsAuthCanceled, $"X との連携に失敗しました（HTTP {(int)response.StatusCode}）。Ads API の利用申請と、アプリの Callback URL の登録を確認してください。", false);
            }
            return body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
        }
    }
}

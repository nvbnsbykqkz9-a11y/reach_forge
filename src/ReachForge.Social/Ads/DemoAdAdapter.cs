using ReachForge.Application.Ads;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Ads;

/// <summary>
/// お試しの広告アカウント（実際には出稿せず、請求もされない）。出稿から2分は「審査中」、その後は予算に合わせて成果が増えていく。
/// </summary>
public sealed class DemoAdAdapter(AdNetwork network, TimeProvider clock) : IAdNetworkAdapter
{
    public AdNetwork Network => network;
    public bool IsSimulation => true;
    public bool IsConfigured => true;
    public IReadOnlyList<SocialPlatform> Platforms => AdHttp.PlatformsOf(network);
    public decimal MinDailyBudget(string currency) => currency == "JPY" ? 100 : 1;
    public bool RequiresVideo(SocialPlatform platform) => platform is SocialPlatform.TikTok or SocialPlatform.YouTube;

    public Task<AdAuthorizationStart> BeginAuthorizationAsync(string state, string codeChallenge, string redirectUri, CancellationToken ct) =>
        throw new NotSupportedException("お試しの広告アカウントは認可画面を使いません。");

    public Task<AdConnectResult> ExchangeAsync(string code, string stateSecret, string redirectUri, CancellationToken ct) =>
        Task.FromResult(new AdConnectResult(new StoredToken("demo-ads-token"),
            [new AdAccountInfo($"demo-{network.ToString().ToLowerInvariant()}", $"お試しの広告アカウント（{AdNetworks.DisplayName(network)}）", "JPY")]));

    public Task<AdSubmitResult> SubmitAsync(AdSubmission submission, CancellationToken ct) =>
        Task.FromResult(new AdSubmitResult(new Dictionary<string, string> { ["campaign"] = $"demo-{submission.Campaign.Id:N}" }, AdStatus.InReview));

    public Task SetPausedAsync(AdCampaign campaign, AdAccount account, StoredToken token, bool paused, CancellationToken ct) => Task.CompletedTask;

    public Task<AdRemoteState> GetStateAsync(AdCampaign campaign, AdAccount account, StoredToken token, DateTimeOffset now, CancellationToken ct)
    {
        var submitted = campaign.SubmittedAt ?? campaign.CreatedAt;
        if (now - submitted < TimeSpan.FromMinutes(2)) return Task.FromResult(new AdRemoteState(AdStatus.InReview, null, null));

        // 配信した時間の割合で予算を使ったことにする（1円あたり表示 4回・クリック率 1.5% の目安）
        var start = campaign.StartAt > submitted ? campaign.StartAt : submitted;
        var hours = Math.Max(0, ((now < campaign.EndAt ? now : campaign.EndAt) - start).TotalHours);
        var spend = campaign.Status == AdStatus.Paused ? campaign.Results?.Spend ?? 0
            : Math.Min(campaign.MaxTotalSpend, decimal.Round(campaign.DailyBudget * (decimal)(hours / 24), 0));
        var impressions = (long)(spend * 4);
        var results = new AdResults(impressions, (long)(impressions * 0.015m), spend, (long)(impressions * 0.7m),
            campaign.Objective == AdObjective.VideoViews ? (long)(impressions * 0.3m) : 0, now);
        var status = now >= campaign.EndAt ? AdStatus.Completed : campaign.Status == AdStatus.Paused ? AdStatus.Paused : AdStatus.Active;
        _ = clock;
        return Task.FromResult(new AdRemoteState(status, results, null));
    }
}

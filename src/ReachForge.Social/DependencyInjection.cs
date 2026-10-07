using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Enums;
using ReachForge.Social.Inbox;
using ReachForge.Social.Insights;
using ReachForge.Social.Line;
using ReachForge.Social.Meta;
using ReachForge.Social.Mock;
using ReachForge.Social.Threads;
using ReachForge.Social.TikTok;
using ReachForge.Social.X;
using ReachForge.Social.YouTube;

namespace ReachForge.Social;

public static class DependencyInjection
{
    public static IServiceCollection AddReachForgeSocial(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SocialOptions>(configuration.GetSection(SocialOptions.SectionName));
        // 画面で SNS アプリの設定を変えたら、再起動せずに反映する（IOptions でも毎回最新の値を返す）
        services.AddSingleton<IOptions<SocialOptions>>(sp => new LiveOptions<SocialOptions>(sp.GetRequiredService<IOptionsMonitor<SocialOptions>>()));
        services.AddSingleton<IPublisherFactory, PublisherFactory>();

        // 公式 API（X / Facebook / Instagram / Threads / LINE / TikTok / YouTube）。
        // 再試行は ServiceDefaults の標準ハンドラ（unsafe メソッドは再試行しない）に任せ、投稿の再試行は PublishingService が分類して行う。
        services.AddHttpClient(XConnector.HttpClientName, (sp, c) =>
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SocialOptions>>().Value.X.ApiBaseUrl));
        services.AddHttpClient(MetaConnector.HttpClientName, (sp, c) =>
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SocialOptions>>().Value.Meta.GraphBaseUrl));
        services.AddHttpClient(ThreadsConnector.HttpClientName, (sp, c) =>
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SocialOptions>>().Value.Threads.GraphBaseUrl));
        services.AddHttpClient(LineConnector.HttpClientName, (sp, c) =>
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SocialOptions>>().Value.Line.ApiBaseUrl));
        // 動画のアップロードは数百MBになりうるため、標準の時間制限（1回10秒・全体30秒）を長くする
        services.AddHttpClient(TikTokConnector.HttpClientName, (sp, c) =>
                c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SocialOptions>>().Value.TikTok.ApiBaseUrl))
            .ForLargeUploads();
        services.AddHttpClient(YouTubeConnector.HttpClientName, (sp, c) =>
                c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SocialOptions>>().Value.YouTube.ApiBaseUrl))
            .ForLargeUploads();

        // コネクタは Supports() が設定の有無を見て判定する。デモ接続は最後に登録し、実コネクタを優先する。
        services.AddSingleton<IChannelConnector, XConnector>();
        services.AddSingleton<IChannelConnector, MetaConnector>();
        services.AddSingleton<IChannelConnector, ThreadsConnector>();
        services.AddSingleton<IChannelConnector, LineConnector>();
        services.AddSingleton<IChannelConnector, TikTokConnector>();
        services.AddSingleton<IChannelConnector, YouTubeConnector>();

        services.AddSingleton<ISocialPublisher, XPublisher>();
        services.AddSingleton<ISocialPublisher, FacebookPublisher>();
        services.AddSingleton<ISocialPublisher, InstagramPublisher>();
        services.AddSingleton<ISocialPublisher, ThreadsPublisher>();
        services.AddSingleton<ISocialPublisher, LinePublisher>();
        services.AddSingleton<ISocialPublisher, TikTokPublisher>();
        services.AddSingleton<ISocialPublisher, YouTubePublisher>();
        services.AddSingleton<IInsightsReaderFactory, InsightsReaderFactory>();
        services.AddSingleton<ISocialInsightsReader, XInsightsReader>();
        services.AddSingleton<ISocialInsightsReader, FacebookInsightsReader>();
        services.AddSingleton<ISocialInsightsReader, InstagramInsightsReader>();
        services.AddSingleton<ISocialInsightsReader, ThreadsInsightsReader>();
        services.AddSingleton<ISocialInsightsReader, LineInsightsReader>();
        services.AddSingleton<ISocialInsightsReader, TikTokInsightsReader>();
        services.AddSingleton<ISocialInsightsReader, YouTubeInsightsReader>();
        services.AddSingleton<IInboxReaderFactory, InboxReaderFactory>();
        services.AddSingleton<ISocialInboxReader, XInboxReader>();
        services.AddSingleton<ISocialInboxReader, FacebookInboxReader>();
        services.AddSingleton<ISocialInboxReader, InstagramInboxReader>();
        services.AddSingleton<ISocialInboxReader, ThreadsInboxReader>();
        services.AddSingleton<ISocialInboxReader, LineInboxReader>();
        services.AddSingleton<ISocialInboxReader, YouTubeInboxReader>();
        // TikTok はコメント取得 API が一般のアプリに提供されていないため受信箱は対象外
        // TODO(フェーズ2): LinkedIn / Pinterest（パートナー承認後）

        // デモ接続（モック）。使うかどうかは Social:UseMock で、画面から切り替えられる（DemoChannelConnector が判定）
        foreach (var platform in Enum.GetValues<SocialPlatform>())
        {
            services.AddSingleton<ISocialPublisher>(sp =>
                new MockPublisher(platform, sp.GetRequiredService<ILogger<MockPublisher>>(), sp.GetService<TimeProvider>()));
            services.AddSingleton<ISocialInboxReader>(sp =>
                new MockInboxReader(platform, sp.GetService<TimeProvider>() ?? TimeProvider.System));
            services.AddSingleton<ISocialInsightsReader>(sp =>
                new MockInsightsReader(platform, sp.GetService<TimeProvider>() ?? TimeProvider.System));
        }
        services.AddSingleton<IChannelConnector, DemoChannelConnector>();
        return services;
    }

    /// <summary>大きなファイルを送る SNS の HTTP クライアント。再試行は ServiceDefaults と同じく安全なメソッドだけ。</summary>
    private static IHttpClientBuilder ForLargeUploads(this IHttpClientBuilder builder)
    {
        builder.ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers は試験的 API
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.AddStandardResilienceHandler(o =>
        {
            o.Retry.DisableForUnsafeHttpMethods();
            o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(10);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(30);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(20);
        });
        return builder;
    }

    /// <summary>毎回 <see cref="IOptionsMonitor{T}.CurrentValue"/> を返す IOptions（設定の変更をすぐ反映する）。</summary>
    private sealed class LiveOptions<T>(IOptionsMonitor<T> monitor) : IOptions<T> where T : class
    {
        public T Value => monitor.CurrentValue;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
using ReachForge.Social.X;

namespace ReachForge.Social;

public static class DependencyInjection
{
    public static IServiceCollection AddReachForgeSocial(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SocialOptions>(configuration.GetSection(SocialOptions.SectionName));
        var options = configuration.GetSection(SocialOptions.SectionName).Get<SocialOptions>() ?? new SocialOptions();
        services.AddSingleton<IPublisherFactory, PublisherFactory>();

        // 公式 API（初期リリース：X / Facebook / Instagram / Threads / LINE）。
        // 再試行は ServiceDefaults の標準ハンドラ（unsafe メソッドは再試行しない）に任せ、投稿の再試行は PublishingService が分類して行う。
        services.AddHttpClient(XConnector.HttpClientName, (sp, c) =>
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SocialOptions>>().Value.X.ApiBaseUrl));
        services.AddHttpClient(MetaConnector.HttpClientName, (sp, c) =>
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SocialOptions>>().Value.Meta.GraphBaseUrl));
        services.AddHttpClient(ThreadsConnector.HttpClientName, (sp, c) =>
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SocialOptions>>().Value.Threads.GraphBaseUrl));
        services.AddHttpClient(LineConnector.HttpClientName, (sp, c) =>
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SocialOptions>>().Value.Line.ApiBaseUrl));

        // コネクタは Supports() が設定の有無を見て判定する。デモ接続は最後に登録し、実コネクタを優先する。
        services.AddSingleton<IChannelConnector, XConnector>();
        services.AddSingleton<IChannelConnector, MetaConnector>();
        services.AddSingleton<IChannelConnector, ThreadsConnector>();
        services.AddSingleton<IChannelConnector, LineConnector>();

        services.AddSingleton<ISocialPublisher, XPublisher>();
        services.AddSingleton<ISocialPublisher, FacebookPublisher>();
        services.AddSingleton<ISocialPublisher, InstagramPublisher>();
        services.AddSingleton<ISocialPublisher, ThreadsPublisher>();
        services.AddSingleton<ISocialPublisher, LinePublisher>();
        services.AddSingleton<IInsightsReaderFactory, InsightsReaderFactory>();
        services.AddSingleton<ISocialInsightsReader, XInsightsReader>();
        services.AddSingleton<ISocialInsightsReader, FacebookInsightsReader>();
        services.AddSingleton<ISocialInsightsReader, InstagramInsightsReader>();
        services.AddSingleton<ISocialInsightsReader, ThreadsInsightsReader>();
        services.AddSingleton<ISocialInsightsReader, LineInsightsReader>();
        services.AddSingleton<IInboxReaderFactory, InboxReaderFactory>();
        services.AddSingleton<ISocialInboxReader, XInboxReader>();
        services.AddSingleton<ISocialInboxReader, FacebookInboxReader>();
        services.AddSingleton<ISocialInboxReader, InstagramInboxReader>();
        services.AddSingleton<ISocialInboxReader, ThreadsInboxReader>();
        services.AddSingleton<ISocialInboxReader, LineInboxReader>();
        // TODO(フェーズ2): TikTok / YouTube / LinkedIn / Pinterest（アプリ審査・パートナー承認後）

        if (options.UseMock)
        {
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
        }
        return services;
    }
}

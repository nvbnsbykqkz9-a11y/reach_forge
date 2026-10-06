using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Social;
using ReachForge.Domain.Enums;
using ReachForge.Social.Mock;

namespace ReachForge.Social;

public static class DependencyInjection
{
    public static IServiceCollection AddReachForgeSocial(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(SocialOptions.SectionName).Get<SocialOptions>() ?? new SocialOptions();
        services.AddSingleton<IPublisherFactory, PublisherFactory>();

        if (options.UseMock)
        {
            foreach (var platform in Enum.GetValues<SocialPlatform>())
            {
                services.AddSingleton<ISocialPublisher>(sp =>
                    new MockPublisher(platform, sp.GetRequiredService<ILogger<MockPublisher>>()));
            }
            services.AddSingleton<IChannelConnector, DemoChannelConnector>();
        }
        // TODO(F-01/F-08): X / Meta(Instagram・Facebook・Threads) / LINE の公式 API アダプタを
        //   AddHttpClient<T>().AddStandardResilienceHandler() で登録する（初期リリース対象）。
        return services;
    }
}

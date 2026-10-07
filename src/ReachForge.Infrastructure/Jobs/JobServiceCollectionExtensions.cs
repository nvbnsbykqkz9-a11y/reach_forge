using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure.Hosting;

namespace ReachForge.Infrastructure.Jobs;

public static class JobServiceCollectionExtensions
{
    private static JobOptions Options(IConfiguration configuration) =>
        configuration.GetSection(JobOptions.SectionName).Get<JobOptions>() ?? new JobOptions();

    /// <summary>キュー（登録側）。AddReachForge から呼ばれる。</summary>
    internal static IServiceCollection AddReachForgeWorkQueue(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JobOptions>(configuration.GetSection(JobOptions.SectionName));
        var options = Options(configuration);
        if (options.Queue == QueueProvider.ServiceBus)
        {
            var sb = options.ServiceBus;
            if (string.IsNullOrWhiteSpace(sb.ConnectionString) && string.IsNullOrWhiteSpace(sb.FullyQualifiedNamespace))
            {
                throw new InvalidOperationException("Jobs:ServiceBus:ConnectionString または Jobs:ServiceBus:FullyQualifiedNamespace を設定してください。");
            }
            services.TryAddSingleton(_ => string.IsNullOrWhiteSpace(sb.ConnectionString)
                ? new ServiceBusClient(sb.FullyQualifiedNamespace, new DefaultAzureCredential())
                : new ServiceBusClient(sb.ConnectionString));
            services.TryAddSingleton<ServiceBusWorkQueue>();
            services.TryAddSingleton<IWorkQueue>(sp => sp.GetRequiredService<ServiceBusWorkQueue>());
            services.TryAddSingleton<IDeadLetterAdmin>(sp => sp.GetRequiredService<ServiceBusWorkQueue>());
            if (sb.CreateQueues)
            {
                services.TryAddSingleton(_ => string.IsNullOrWhiteSpace(sb.ConnectionString)
                    ? new ServiceBusAdministrationClient(sb.FullyQualifiedNamespace, new DefaultAzureCredential())
                    : new ServiceBusAdministrationClient(sb.ConnectionString));
                services.AddHostedService<ServiceBusQueueInitializer>();
            }
        }
        else
        {
            services.TryAddSingleton<InProcessWorkQueue>();
            services.TryAddSingleton<IWorkQueue>(sp => sp.GetRequiredService<InProcessWorkQueue>());
            services.TryAddSingleton<IDeadLetterAdmin>(sp => sp.GetRequiredService<InProcessWorkQueue>());
        }
        return services;
    }

    /// <summary>
    /// キューの処理役を登録する。
    /// <paramref name="all"/> = true は Worker（Webhook と AI ジョブの両方）。false は Web で、プロセス内キューの場合だけ
    /// Webhook を自分で処理する（Web で受けた Webhook はほかのプロセスへ渡せないため）。
    /// </summary>
    public static IServiceCollection AddReachForgeWorkConsumers(this IServiceCollection services, IConfiguration configuration, bool all)
    {
        var options = Options(configuration);
        var inProcess = options.Queue == QueueProvider.InProcess;
        if (!all && !inProcess) return services;

        services.TryAddSingleton<WebhookWorkHandler>();
        services.AddSingleton<IWorkHandler>(sp => sp.GetRequiredService<WebhookWorkHandler>());
        if (all) services.AddSingleton<IWorkHandler, AiJobWorkHandler>();
        if (inProcess) services.AddHostedService<InProcessWorkConsumer>();
        else services.AddHostedService<ServiceBusWorkConsumer>();
        return services;
    }

    /// <summary>
    /// ジョブを実行するプロセス（Worker、または Worker:RunInWeb の Web）に、予約配信・AI ジョブの巡回・定期ジョブ・キューの処理役を登録する。
    /// 定期ジョブは Jobs:Engine により、プロセス内のタイマーか Hangfire サーバで動かす。
    /// </summary>
    public static IServiceCollection AddReachForgeJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PublishDispatcherOptions>(configuration.GetSection(PublishDispatcherOptions.SectionName));
        // 予約配信（±60秒の精度が要るため短い間隔で巡回し、楽観排他で二重投稿を防ぐ）と AI ジョブの巡回は常にこのプロセスで動かす
        services.AddHostedService<PublishDispatcher>();
        services.AddHostedService<AiJobDispatcher>();

        if (Options(configuration).Engine == JobEngine.Hangfire)
        {
            services.AddReachForgeHangfire(configuration, server: true);
        }
        else
        {
            services.TryAddSingleton<SystemJobRunner>();
            services.AddHostedService<HostedJobScheduler>();
        }
        return services.AddReachForgeWorkConsumers(configuration, all: true);
    }
}

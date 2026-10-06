using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Services;

namespace ReachForge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddReachForgeApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IBrandContextProvider, BrandContextProvider>();
        services.AddScoped<ICreditService, CreditService>();
        services.AddScoped<StudioService>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<SchedulingService>();
        services.AddScoped<PublishingService>();
        services.AddScoped<ChannelService>();
        services.AddScoped<ChannelTokenService>();
        services.AddScoped<MemberService>();
        services.AddScoped<MediaService>();
        services.AddScoped<AiJobProcessor>();
        services.AddScoped<DashboardService>();
        services.AddScoped<AnalyticsService>();
        services.AddScoped<ReportService>();
        services.AddScoped<InboxService>();
        services.AddScoped<CampaignService>();
        services.AddScoped<AbTestService>();
        services.AddScoped<BrandDiagnosisService>();
        services.AddScoped<MetricsCollectionService>();
        services.AddScoped<WorkspaceService>();
        return services;
    }
}

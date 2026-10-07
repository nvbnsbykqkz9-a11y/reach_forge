using ReachForge.Application.Ai;
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
        services.AddScoped<MemberService>();
        services.AddScoped<MediaService>();
        services.AddScoped<AiJobProcessor>();
        services.AddScoped<LpStudioService>();
        services.AddScoped<BrandDiagnosisService>();
        services.AddScoped<CreditResetService>();
        services.AddScoped<VideoService>();
        services.AddScoped<AccountNotifications>();
        services.AddScoped<WorkspaceService>();
        return services;
    }
}

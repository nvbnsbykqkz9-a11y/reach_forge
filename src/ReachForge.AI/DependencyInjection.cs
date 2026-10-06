using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.AI.Providers;
using ReachForge.AI.Routing;
using ReachForge.AI.Services;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;

namespace ReachForge.AI;

public static class DependencyInjection
{
    public static IServiceCollection AddReachForgeAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<ProviderCircuitBreaker>();
        services.AddScoped<IAiUsageSink, AiUsageCollector>();
        services.AddScoped<IModelRouter, ConfigDrivenModelRouter>();
        services.AddScoped<ICopyGenerationService, CopyGenerationService>();
        services.AddScoped<IVariantGenerationService, VariantGenerationService>();
        services.AddScoped<IApprovalSummaryService, ApprovalSummaryService>();
        services.AddSingleton<IImageGeneratorFactory, ImageGeneratorFactory>();
        services.AddScoped<IImageGenerationService, ImageGenerationService>();
        services.AddScoped<IAltTextGenerator, AltTextGenerator>();
        services.AddScoped<IReportWriter, ReportWriter>();
        services.AddScoped<IInboxClassifier, InboxClassifier>();
        services.AddScoped<IReplySuggester, ReplySuggester>();
        services.AddScoped<IAbVariantGenerator, AbVariantGenerator>();
        services.AddScoped<IBrandAnalyzer, BrandAnalyzer>();
        services.AddScoped<ITrendIdeaWriter, TrendIdeaWriter>();
        services.AddScoped<IVideoScriptWriter, VideoScriptWriter>();
        services.AddScoped<ITextToSpeech, TextToSpeechService>();
        return services;
    }
}

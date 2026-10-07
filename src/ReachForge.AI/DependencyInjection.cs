using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ReachForge.AI.Prompts;
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
        // 画面で API キーを変えたら、再起動せずに反映する（IOptions でも毎回最新の値を返す）
        services.AddSingleton<IOptions<AiOptions>>(sp => new LiveOptions<AiOptions>(sp.GetRequiredService<IOptionsMonitor<AiOptions>>()));
        services.AddHttpClient(AiKeyChecker.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<AiKeyChecker>();
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<ProviderCircuitBreaker>();
        services.AddScoped<IAiUsageSink, AiUsageCollector>();
        // プロンプトの保存先は Infrastructure が DB 版に差し替える
        services.TryAddSingleton<IPromptStore, DefaultPromptStore>();
        services.AddScoped<IPromptCatalog, PromptCatalog>();
        services.AddScoped<IModelRouter, ConfigDrivenModelRouter>();
        services.AddScoped<ICopyGenerationService, CopyGenerationService>();
        services.AddScoped<IVariantGenerationService, VariantGenerationService>();
        services.AddScoped<IApprovalSummaryService, ApprovalSummaryService>();
        services.AddSingleton<IImageGeneratorFactory, ImageGeneratorFactory>();
        services.AddScoped<IImageGenerationService, ImageGenerationService>();
        // 動画生成（Sora / Veo）：依頼と状態の確認は短く、動画の取得は大きいため長めにする。再試行は代替プロバイダで行う
        services.AddHttpClient(VideoGeneratorFactory.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(10));
        services.AddSingleton<IVideoGeneratorFactory, VideoGeneratorFactory>();
        services.AddScoped<IVideoGenerationService, VideoGenerationService>();
        services.AddScoped<IAltTextGenerator, AltTextGenerator>();
        services.AddScoped<IReportWriter, ReportWriter>();
        services.AddScoped<IInboxClassifier, InboxClassifier>();
        services.AddScoped<IReplySuggester, ReplySuggester>();
        services.AddScoped<IAbVariantGenerator, AbVariantGenerator>();
        services.AddScoped<IBrandAnalyzer, BrandAnalyzer>();
        services.AddScoped<ITrendIdeaWriter, TrendIdeaWriter>();
        services.AddScoped<IVideoScriptWriter, VideoScriptWriter>();
        services.AddScoped<ILandingPageVideoPlanner, LandingPageVideoPlanner>();
        services.AddScoped<ITextToSpeech, TextToSpeechService>();
        return services;
    }

    /// <summary>毎回 <see cref="IOptionsMonitor{T}.CurrentValue"/> を返す IOptions（設定の変更をすぐ反映する）。</summary>
    private sealed class LiveOptions<T>(IOptionsMonitor<T> monitor) : IOptions<T> where T : class
    {
        public T Value => monitor.CurrentValue;
    }
}

using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Infrastructure.Hosting;

namespace ReachForge.Infrastructure.Jobs;

/// <summary>AiGenerationJob（14章）。本文は AiJob の ID。ジョブの状態で二重実行を防ぐため、再配信されても1回だけ実行される。</summary>
public sealed class AiJobWorkHandler(IServiceScopeFactory scopes) : IWorkHandler
{
    public string Queue => WorkQueues.AiJobs;

    public Task HandleAsync(string payload, CancellationToken ct) =>
        Guid.TryParse(payload, out var id) ? AiJobDispatcher.RunJobAsync(scopes, id, ct) : Task.CompletedTask;
}

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Services;

/// <summary>
/// AI 投稿文生成（RF-DES-001 F-03 / 付録 A.2）。
/// 入力ガードレール → ブランド知識 → プロンプト組立 → モデルルータ → 構造化出力（失敗時1回再生成）
/// → 出力ガードレール → ブランド適合度採点 → 並べ替え → 確定。
/// </summary>
public sealed class CopyGenerationService(
    IModelRouter router,
    IBrandContextProvider brand,
    IAppDbContext db,
    ITenantContext tenant,
    IPromptCatalog prompts,
    ILogger<CopyGenerationService> log) : ICopyGenerationService
{
    public async Task<CopyResult> GenerateAsync(CopyRequest request, CancellationToken ct)
    {
        Validate(request);
        CheckInput(request.Theme, request.AdditionalInstructions);

        var ctx = await brand.BuildAsync(request.WorkspaceId, request.ProductIds, ct);
        var system = await prompts.RenderAsync(PromptKeys.Copy, PromptLibrary.BrandValues(ctx), ct);
        var generation = StartGeneration(request, system, ctx);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, system.Text),
            new(ChatRole.User, PromptLibrary.CopyUser(request, request.TargetPlatforms.ToList())),
        ];
        var payload = new CopyStubPayload(request, ctx.Profile, ctx.Products);
        return await RunAsync(generation, messages, payload, ctx, request.Count, ct);
    }

    public async Task<CopyResult> RefineAsync(CopyRequest request, GeneratedCopy candidate, QuickFix fix, CancellationToken ct)
    {
        CheckInput(candidate.Body, null);

        var ctx = await brand.BuildAsync(request.WorkspaceId, request.ProductIds, ct);
        var system = await prompts.RenderAsync(PromptKeys.Copy, PromptLibrary.BrandValues(ctx), ct);
        var refine = await prompts.RenderAsync(PromptKeys.CopyRefine, PromptLibrary.RefineValues(candidate, fix), ct);
        var generation = StartGeneration(request, refine, ctx);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, system.Text),
            new(ChatRole.User, refine.Text),
        ];
        var payload = new CopyStubPayload(request, ctx.Profile, ctx.Products, candidate, fix);
        return await RunAsync(generation, messages, payload, ctx, 1, ct);
    }

    private async Task<CopyResult> RunAsync(AiGeneration generation, List<ChatMessage> messages, CopyStubPayload payload,
        BrandContext ctx, int count, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var client = router.Resolve(AiTaskType.Copy);
        var options = new AiCallContext(AiTaskType.Copy, generation.Id, payload).Apply();
        try
        {
            var (copies, response) = await GetStructuredAsync<CopyBatch>(client, messages, options, ct);
            var guardrailCtx = ctx.ToGuardrailContext();
            var candidates = new List<CopyCandidate>();
            foreach (var copy in copies.Candidates.Take(count).Select(c => EnforceDisclosure(c, guardrailCtx)))
            {
                var (score, reason) = await JudgeAsync(copy, ctx, generation.Id, ct);
                candidates.Add(new CopyCandidate
                {
                    Rank = 0,
                    Headline = copy.Headline,
                    Body = copy.Body,
                    Cta = copy.Cta,
                    Hashtags = copy.Hashtags.Select(PostText.Normalize).Where(h => h.Length > 0).Distinct().ToList(),
                    BrandFitScore = score,
                    BrandFitReason = reason,
                    Guardrail = GuardrailChecker.CheckContent(Compose(copy), guardrailCtx),
                });
            }
            if (candidates.Count == 0) throw new AiUnavailableException("AIから案を受け取れませんでした。もう一度お試しください。");

            // 適合スコアが高く、ガードレール指摘が少ない順に並べる
            var ranked = candidates
                .OrderByDescending(c => c.BrandFitScore)
                .ThenBy(c => c.Guardrail.Level)
                .Select((c, i) => c with { Rank = i + 1 })
                .ToList();

            var model = ModelInfo(response);
            generation.Provider = model.Provider;
            generation.ModelId = model.ModelId;
            generation.FallbackUsed = model.FallbackUsed;
            generation.Status = AiGenerationStatus.Succeeded;
            generation.Output = JsonSerializer.Serialize(copies, AIJsonUtilities.DefaultOptions);
            generation.LatencyMs = (int)sw.ElapsedMilliseconds;
            await db.SaveChangesAsync(ct);
            return new CopyResult(generation.Id, model, ranked);
        }
        catch (DomainException ex)
        {
            generation.Status = ex is AiSafetyBlockedException ? AiGenerationStatus.Blocked : AiGenerationStatus.Failed;
            generation.ErrorCode = ex.ErrorCode;
            generation.LatencyMs = (int)sw.ElapsedMilliseconds;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>構造化出力を取得する。パースに失敗した場合は1回だけ再生成する（RF-DES-001 4.5）。</summary>
    internal static async Task<(T Value, ChatResponse Response)> GetStructuredAsync<T>(IChatClient client,
        List<ChatMessage> messages, ChatOptions options, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await client.GetResponseAsync<T>(messages, options, cancellationToken: ct);
            try
            {
                if (response.TryGetResult(out var value) && value is not null) return (value, response);
            }
            catch (JsonException) when (attempt == 0)
            {
            }
            if (attempt >= 1)
            {
                throw new AiUnavailableException("AIの出力を読み取れませんでした。もう一度お試しください。");
            }
        }
    }

    private async Task<(double Score, string? Reason)> JudgeAsync(GeneratedCopy copy, BrandContext ctx, Guid generationId,
        CancellationToken ct)
    {
        try
        {
            var client = router.Resolve(AiTaskType.Judge);
            var options = new AiCallContext(AiTaskType.Judge, generationId, new JudgeStubPayload(copy, ctx.Profile)).Apply();
            var system = await prompts.RenderAsync(PromptKeys.Judge, PromptLibrary.BrandValues(ctx), ct);
            var (result, _) = await GetStructuredAsync<JudgeResult>(client,
                [new(ChatRole.System, system.Text), new(ChatRole.User, PromptLibrary.JudgeUser(copy))],
                options, ct);
            return (Math.Clamp(result.Score, 1, 5), result.Reason);
        }
        catch (DomainException ex)
        {
            // 採点の失敗で生成全体を失敗させない
            log.LogWarning(ex, "Brand-fit judging failed; returning unscored candidate");
            return (0, null);
        }
    }

    /// <summary>広告・PR 案件では必須表記を自動挿入する（F-03 業務ルール）。</summary>
    internal static GeneratedCopy EnforceDisclosure(GeneratedCopy copy, GuardrailContext ctx)
    {
        if (!ctx.IsAdvertisement) return copy;
        var text = Compose(copy);
        return RegulatedExpressions.PrDisclosures.Any(d => text.Contains(d, StringComparison.OrdinalIgnoreCase))
            ? copy
            : copy with { Body = $"#PR {copy.Body}" };
    }

    internal static string Compose(GeneratedCopy c) =>
        PostText.Compose($"{c.Headline}\n{c.Body}\n{c.Cta}", c.Hashtags);

    private static void Validate(CopyRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Theme))
        {
            throw new DomainException(ErrorCodes.Validation, "テーマを入力してください。例：秋の新メニュー、週末のイベント");
        }
        if (PostText.Length(r.Theme) > CopyRequest.MaxThemeLength)
        {
            throw new DomainException(ErrorCodes.Validation, $"テーマは{CopyRequest.MaxThemeLength}字以内で入力してください。");
        }
        if (r.Count is < 1 or > CopyRequest.MaxCount)
        {
            throw new DomainException(ErrorCodes.Validation, $"案の数は1〜{CopyRequest.MaxCount}で指定してください。");
        }
    }

    /// <summary>入力ガードレール：プロンプトインジェクションの疑いがある入力は生成しない。</summary>
    private static void CheckInput(params string?[] inputs)
    {
        if (inputs.Any(i => i is not null && PromptInjectionDetector.IsSuspicious(i)))
        {
            throw new AiSafetyBlockedException("指示の書き換えの疑い");
        }
    }

    private AiGeneration StartGeneration(CopyRequest request, PromptText prompt, BrandContext ctx)
    {
        var generation = new AiGeneration
        {
            TenantId = tenant.TenantId,
            WorkspaceId = request.WorkspaceId,
            TaskType = AiTaskType.Copy,
            PromptKey = prompt.Key,
            PromptVersion = prompt.Version,
            BrandProfileVersion = ctx.Profile.Version,
            InputSnapshot = JsonSerializer.Serialize(new
            {
                request.Objective, request.Theme, request.ProductIds, request.Framework, request.Count,
                request.Language, request.TargetPlatforms, request.AdditionalInstructions,
            }, AIJsonUtilities.DefaultOptions),
            Status = AiGenerationStatus.Running,
        };
        db.AiGenerations.Add(generation);
        return generation;
    }

    internal static AiModelInfo ModelInfo(ChatResponse response) => new(
        response.AdditionalProperties?.GetValueOrDefault("rf.provider") as string ?? "",
        response.ModelId ?? "",
        response.AdditionalProperties?.GetValueOrDefault("rf.fallback") is true);
}

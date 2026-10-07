using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;

namespace ReachForge.Application.Services;

/// <summary>
/// ブランド診断（F-02 処理 1〜2）：Web サイト・紹介文・過去の投稿から、AI がブランド設定の下書きをつくる。
/// 下書きは保存せず、利用者が確認して反映する。URL を取得できない場合は紹介文だけで診断する（W-BRD-001）。
/// </summary>
public sealed class BrandDiagnosisService(
    IAppDbContext db,
    ITenantContext tenant,
    IWebPageFetcher fetcher,
    IBrandAnalyzer analyzer)
{
    public const int MaxExtraText = 20_000;

    public sealed record Result(BrandProfileDraft Draft, string? Warning);

    public async Task<Result> DiagnoseAsync(string? url, string? extraText, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.ManageBrand);
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(extraText))
        {
            throw new DomainException(ErrorCodes.Validation, "WebサイトのURLか、紹介文を入力してください。");
        }
        if (extraText is { Length: > MaxExtraText }) throw new DomainException(ErrorCodes.Validation, "紹介文は2万字以内にしてください。");
        if (extraText is not null && PromptInjectionDetector.IsSuspicious(extraText)) throw new AiSafetyBlockedException("指示の書き換えの疑い");

        WebPage? page = null;
        string? warning = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                page = await fetcher.FetchAsync(url, ct);
            }
            catch (DomainException ex) when (ex.ErrorCode == ErrorCodes.BrdUrlUnavailable)
            {
                if (string.IsNullOrWhiteSpace(extraText)) throw;
                warning = ex.Message;
            }
        }

        IReadOnlyList<string> posts = [];

        var draft = await analyzer.AnalyzeAsync(new BrandAnalysisInput(page, extraText, posts), ct);
        db.Record(tenant, "brand.diagnosed", "BrandProfile", null, page?.Url.Host);
        await db.SaveChangesAsync(ct);
        return new Result(draft, warning);
    }
}

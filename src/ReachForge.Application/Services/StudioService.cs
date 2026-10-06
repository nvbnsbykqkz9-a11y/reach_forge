using Microsoft.EntityFrameworkCore;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;
using ReachForge.Domain.Credits;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Services;

public sealed record SaveMasterPost
{
    public Guid? Id { get; init; }
    public required string Title { get; init; }
    public required PostObjective Objective { get; init; }
    public string Theme { get; init; } = "";
    public required string CoreMessage { get; init; }
    public string Cta { get; init; } = "";
    public IReadOnlyList<string> Hashtags { get; init; } = [];
    public IReadOnlyList<Guid> ProductIds { get; init; } = [];
    public Guid? CampaignId { get; init; }
    public Guid? AiGenerationId { get; init; }
    public bool IsAiEdited { get; init; }
}

public sealed record VariantOptions(string? LinkUrl = null, bool IncludeUrlForX = false);

public sealed record VariantEditResult(PostVariant Variant, bool ReapprovalRequired);

/// <summary>
/// AI コンテンツスタジオ（SCR-05）のユースケース：生成 → マスター投稿保存 → SNS 別バリアント変換 → 編集。
/// </summary>
public sealed class StudioService(
    IAppDbContext db,
    ITenantContext tenant,
    ICopyGenerationService copies,
    IVariantGenerationService variants,
    IBrandContextProvider brand,
    ICreditService credits)
{
    public Task<CopyResult> GenerateCopiesAsync(CopyRequest request, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        return copies.GenerateAsync(request, ct);
    }

    public Task<CopyResult> RefineCopyAsync(CopyRequest request, GeneratedCopy candidate, QuickFix fix, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        return copies.RefineAsync(request, candidate, fix, ct);
    }

    public async Task<MasterPost> SaveMasterPostAsync(SaveMasterPost cmd, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        if (string.IsNullOrWhiteSpace(cmd.CoreMessage))
        {
            throw new DomainException(ErrorCodes.Validation, "本文を入力してください。");
        }

        MasterPost post;
        if (cmd.Id is { } id)
        {
            post = await db.MasterPosts.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("投稿");
        }
        else
        {
            post = new MasterPost
            {
                TenantId = tenant.TenantId,
                WorkspaceId = tenant.WorkspaceId,
                Title = cmd.Title,
                CreatedBy = tenant.UserName,
            };
            db.MasterPosts.Add(post);
        }

        post.Title = string.IsNullOrWhiteSpace(cmd.Title) ? PostText.Truncate(cmd.CoreMessage, 30) : cmd.Title;
        post.Objective = cmd.Objective;
        post.Theme = cmd.Theme;
        post.CoreMessage = cmd.CoreMessage;
        post.Cta = cmd.Cta;
        post.Hashtags = [.. cmd.Hashtags.Select(PostText.Normalize).Where(h => h.Length > 0).Distinct()];
        post.ProductIds = [.. cmd.ProductIds];
        post.CampaignId = cmd.CampaignId;
        post.AiGenerationId = cmd.AiGenerationId ?? post.AiGenerationId;
        post.IsAiEdited |= cmd.IsAiEdited;

        await db.SaveChangesAsync(ct);
        return post;
    }

    public async Task<GuardrailReport> CheckMasterAsync(Guid masterPostId, CancellationToken ct)
    {
        var post = await db.MasterPosts.FirstOrDefaultAsync(p => p.Id == masterPostId, ct) ?? throw new NotFoundException("投稿");
        var ctx = await brand.BuildAsync(post.WorkspaceId, post.ProductIds, post.CampaignId, ct);
        return GuardrailChecker.CheckContent(PostText.Compose(post.CoreMessage, post.Hashtags), ctx.ToGuardrailContext());
    }

    /// <summary>
    /// 選択チャネルごとにバリアントを生成する（F-06）。既存の下書きバリアントは作り直し、
    /// 承認済み以降のバリアントは変更しない（意図しない再承認を防ぐ）。消費は 1 クレジット／チャネル。
    /// </summary>
    public async Task<IReadOnlyList<PostVariant>> GenerateVariantsAsync(Guid masterPostId, IReadOnlyList<Guid> channelIds,
        VariantOptions options, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var post = await db.MasterPosts.FirstOrDefaultAsync(p => p.Id == masterPostId, ct) ?? throw new NotFoundException("投稿");
        var channels = await db.Channels
            .Where(c => channelIds.Contains(c.Id) && c.WorkspaceId == post.WorkspaceId && c.Status != ChannelStatus.Revoked)
            .ToListAsync(ct);
        if (channels.Count == 0)
        {
            throw new DomainException(ErrorCodes.Validation, "投稿先のSNSを1つ以上選んでください。");
        }

        var existing = await db.PostVariants.Where(v => v.MasterPostId == post.Id).ToListAsync(ct);
        var targets = channels
            .Where(c => existing.FirstOrDefault(v => v.ChannelId == c.Id) is not { } v || v.Status == VariantStatus.Draft)
            .ToList();

        var campaign = post.CampaignId is null ? null : await db.Campaigns.FirstOrDefaultAsync(c => c.Id == post.CampaignId, ct);
        var ctx = await brand.BuildAsync(post.WorkspaceId, post.ProductIds, post.CampaignId, ct);

        await using var hold = await credits.HoldAsync(CreditTable.Cost(CreditOperation.VariantConversion, targets.Count), ct);

        // AI 呼び出しはチャネルごとに並列（DbContext には触れない）
        var generated = await Task.WhenAll(targets.Select(channel => variants.GenerateAsync(new VariantRequest
        {
            WorkspaceId = post.WorkspaceId,
            Platform = channel.Platform,
            Headline = post.Title,
            Body = post.CoreMessage,
            Cta = post.Cta,
            Hashtags = post.Hashtags,
            LinkUrl = options.LinkUrl,
            CampaignCode = campaign?.Code,
            IncludeUrlForX = options.IncludeUrlForX,
        }, post.AiGenerationId, ct)));

        foreach (var (channel, result) in targets.Zip(generated))
        {
            var variant = existing.FirstOrDefault(v => v.ChannelId == channel.Id);
            if (variant is null)
            {
                variant = PostVariant.Create(post, channel, result.Body, result.Hashtags, result.Title, post.AiGenerationId);
                db.PostVariants.Add(variant);
                existing.Add(variant);
            }
            else
            {
                variant.Edit(result.Body, result.Hashtags, result.Title, requiresApproval: false);
            }
            variant.UrlCostAcknowledged = channel.Platform == SocialPlatform.X && options.IncludeUrlForX;
            variant.ApplyGuardrail(CheckVariant(variant, ctx.ToGuardrailContext()));
        }

        await hold.CommitAsync(CreditTable.Cost(CreditOperation.VariantConversion, targets.Count), ct);
        await db.SaveChangesAsync(ct);
        return existing.Where(v => channelIds.Contains(v.ChannelId)).OrderBy(v => v.Platform).ToList();
    }

    public async Task<IReadOnlyList<PostVariant>> GetVariantsAsync(Guid masterPostId, CancellationToken ct) =>
        await db.PostVariants.Where(v => v.MasterPostId == masterPostId).OrderBy(v => v.Platform).ToListAsync(ct);

    /// <summary>バリアントを個別に編集する。他のバリアントには影響しない。</summary>
    public async Task<VariantEditResult> UpdateVariantAsync(Guid variantId, string body, IReadOnlyList<string> hashtags,
        string? title, bool? urlCostAcknowledged, CancellationToken ct)
    {
        RolePolicy.Demand(tenant.Role, Permission.Generate);
        var variant = await db.PostVariants.FirstOrDefaultAsync(v => v.Id == variantId, ct) ?? throw new NotFoundException("投稿");
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == variant.WorkspaceId, ct);
        var post = await db.MasterPosts.FirstAsync(p => p.Id == variant.MasterPostId, ct);

        var reapproval = variant.Edit(body, hashtags.Select(PostText.Normalize).Where(h => h.Length > 0), title,
            workspace.RequiresApproval);
        if (urlCostAcknowledged is { } ack) variant.UrlCostAcknowledged = ack;

        var ctx = await brand.BuildAsync(post.WorkspaceId, post.ProductIds, post.CampaignId, ct);
        variant.ApplyGuardrail(CheckVariant(variant, ctx.ToGuardrailContext()));
        if (reapproval)
        {
            db.Record(tenant, "variant.reapproval_required", nameof(PostVariant), variant.Id, ErrorCodes.AprReapprovalRequired);
        }
        await db.SaveChangesAsync(ct);
        return new VariantEditResult(variant, reapproval);
    }

    public async Task<GuardrailReport> RecheckVariantAsync(Guid variantId, CancellationToken ct)
    {
        var variant = await db.PostVariants.FirstOrDefaultAsync(v => v.Id == variantId, ct) ?? throw new NotFoundException("投稿");
        var post = await db.MasterPosts.FirstAsync(p => p.Id == variant.MasterPostId, ct);
        var ctx = await brand.BuildAsync(post.WorkspaceId, post.ProductIds, post.CampaignId, ct);
        var report = CheckVariant(variant, ctx.ToGuardrailContext());
        variant.ApplyGuardrail(report);
        await db.SaveChangesAsync(ct);
        return report;
    }

    internal static GuardrailReport CheckVariant(PostVariant v, GuardrailContext ctx) =>
        GuardrailChecker.CheckVariant(v.Body, v.Hashtags, PlatformCatalog.Get(v.Platform), ctx, v.Title);
}

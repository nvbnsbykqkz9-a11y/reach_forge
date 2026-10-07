using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Entities;

/// <summary>広告文の案（本文・見出し・説明・ボタン）。</summary>
public sealed record LpAdCopy(string PrimaryText, string Headline, string Description, string CallToAction);

/// <summary>1つの SNS 向けにつくったもの（広告文の案・投稿文・ハッシュタグ・SNS のサイズの画像）。</summary>
public sealed record LpPlatformOutput
{
    public IReadOnlyList<LpAdCopy> AdCopies { get; init; } = [];
    public string PostText { get; init; } = "";
    public IReadOnlyList<string> Hashtags { get; init; } = [];
    public IReadOnlyList<Guid> ImageAssetIds { get; init; } = [];
}

public enum LpProjectStatus : short
{
    /// <summary>文章・画像をつくっている。</summary>
    Creating = 1,
    /// <summary>文章・画像ができた（動画はジョブで別につくる）。</summary>
    Ready = 2,
    Failed = 3,
}

/// <summary>
/// LP（ランディングページ）から、選んだ SNS 向けの広告文・投稿文・画像と縦型の動画をまとめてつくったもの。
/// SNS へのアップロードは利用者が手動で行う（ReachForge は SNS とつながない）。
/// </summary>
public sealed class LpProject : Entity
{
    public Guid WorkspaceId { get; set; }
    public required string Url { get; set; }
    public string Title { get; set; } = "";
    public List<SocialPlatform> Platforms { get; set; } = [];
    public LpProjectStatus Status { get; set; } = LpProjectStatus.Creating;

    /// <summary>LP から取り込んだ画像（元の大きさ）。</summary>
    public List<Guid> SourceImageAssetIds { get; set; } = [];

    /// <summary>SNS ごとにつくったもの。</summary>
    public Dictionary<SocialPlatform, LpPlatformOutput> Outputs { get; set; } = [];

    /// <summary>縦型の動画をつくるジョブ（つくらない場合は null）。</summary>
    public Guid? VideoJobId { get; set; }

    public string? Error { get; set; }
    public string CreatedBy { get; set; } = "";
    public int CreditsUsed { get; set; }
}

using ReachForge.Domain.Common;

namespace ReachForge.Domain.Entities;

/// <summary>テナント内のブランド／店舗単位の作業空間。</summary>
public sealed class Workspace : Entity
{
    public required string Name { get; set; }

    /// <summary>ワークスペース切替時の識別帯に使うブランド色（RF-UX-001 3.2）。</summary>
    public string BrandColor { get; set; } = "#0A5BD6";

    /// <summary>承認ルートの段数（0〜3）。0 の場合は承認不要（F-07）。</summary>
    public int ApprovalSteps { get; set; } = 1;

    public bool RequiresApproval => ApprovalSteps > 0;

    public ReportSettings Reports { get; set; } = new();
}

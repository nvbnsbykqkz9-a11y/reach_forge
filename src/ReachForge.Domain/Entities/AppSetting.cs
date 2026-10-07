using ReachForge.Domain.Common;

namespace ReachForge.Domain.Entities;

/// <summary>
/// 画面から変えるシステム全体の設定（SNS アプリの ID・シークレットなど）。テナントに属さない（TenantId は空）。
/// 値は設定のキー（例：Social:X:ClientId）ごとに1行で、シークレットは暗号化して保存する。設定ファイルより優先する。
/// </summary>
public sealed class AppSetting : Entity
{
    public const int MaxKeyLength = 128;
    public const int MaxValueLength = 4000;

    public required string Key { get; set; }

    /// <summary>値（<see cref="IsSecret"/> なら Data Protection で暗号化した値）。</summary>
    public required string Value { get; set; }

    public bool IsSecret { get; set; }
    public string UpdatedBy { get; set; } = "";
}

namespace ReachForge.Social;

public sealed class SocialOptions
{
    public const string SectionName = "Social";

    /// <summary>
    /// SNS をモックで動かす（Local / Dev 環境）。本番では false にし、各 SNS のアダプタを登録する。
    /// </summary>
    public bool UseMock { get; set; } = true;
}

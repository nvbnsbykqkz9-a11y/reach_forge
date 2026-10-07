namespace ReachForge.Domain.Platforms;

/// <summary>バリアントの SNS 固有設定（PostVariant.PlatformOptions）のキーと選択肢。</summary>
public static class PlatformOptionKeys
{
    /// <summary>TikTok の公開範囲（privacy_level）。</summary>
    public const string TikTokPrivacy = "privacy_level";

    /// <summary>TikTok のコメントを受け付けない（"true"）。</summary>
    public const string TikTokDisableComment = "disable_comment";

    /// <summary>YouTube の公開設定（privacyStatus）。</summary>
    public const string YouTubePrivacy = "privacy_status";

    public static readonly IReadOnlyList<(string Value, string Label)> TikTokPrivacyChoices =
    [
        ("PUBLIC_TO_EVERYONE", "全員に公開"),
        ("MUTUAL_FOLLOW_FRIENDS", "相互フォローの友達"),
        ("FOLLOWER_OF_CREATOR", "フォロワー"),
        ("SELF_ONLY", "自分のみ"),
    ];

    public static readonly IReadOnlyList<(string Value, string Label)> YouTubePrivacyChoices =
    [
        ("public", "公開"),
        ("unlisted", "限定公開"),
        ("private", "非公開"),
    ];

    /// <summary>キーごとに許す値か。</summary>
    public static bool IsValid(string key, string value) => key switch
    {
        TikTokPrivacy => TikTokPrivacyChoices.Any(c => c.Value == value),
        TikTokDisableComment => value is "true" or "false",
        YouTubePrivacy => YouTubePrivacyChoices.Any(c => c.Value == value),
        _ => false,
    };
}

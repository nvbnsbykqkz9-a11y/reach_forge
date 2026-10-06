namespace ReachForge.Web.Hosting;

/// <summary>認証の設定（RF-DES-001 9.1 / RF-UX-001 SCR-01）。</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>オーナー・管理者に MFA を必須にする（既定 true）。</summary>
    public bool RequireMfaForAdmins { get; set; } = true;

    /// <summary>新規登録（テナント作成）を許可する。</summary>
    public bool AllowRegistration { get; set; } = true;

    /// <summary>外部 ID プロバイダ（Google / Microsoft / Entra External ID など、OIDC）。キーはスキーム名。</summary>
    public Dictionary<string, OidcProviderOptions> Oidc { get; set; } = [];
}

public sealed class OidcProviderOptions
{
    public string DisplayName { get; set; } = "";
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}

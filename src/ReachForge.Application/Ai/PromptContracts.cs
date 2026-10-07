namespace ReachForge.Application.Ai;

/// <summary>保存されたプロンプトテンプレートの1版。</summary>
public sealed record StoredPrompt(string Key, int Version, string Body);

/// <summary>組み立て済みのプロンプト。生成記録に版数を残す。</summary>
public sealed record PromptText(string Key, int Version, string Text);

/// <summary>
/// プロンプトの保存先（RF-DES-001 4.5）。キーごとに、そのテナントで使う版（全体の版か、段階適用中の候補版）を返す。
/// 保存されていなければ null（コードの既定テンプレートを使う）。
/// </summary>
public interface IPromptStore
{
    Task<StoredPrompt?> ResolveAsync(string key, Guid tenantId, CancellationToken ct);
}

/// <summary>テンプレートを解決して値を埋め込む。</summary>
public interface IPromptCatalog
{
    Task<PromptText> RenderAsync(string key, IReadOnlyDictionary<string, object?> values, CancellationToken ct);

    /// <summary>保存前の確認用：指定した本文で組み立てる（運用管理画面のプレビュー・評価）。</summary>
    PromptText RenderBody(string key, int version, string body, IReadOnlyDictionary<string, object?> values);
}

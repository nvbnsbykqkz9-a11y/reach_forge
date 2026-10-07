using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace ReachForge.AI.Prompts;

/// <summary>DB を使わない構成（AI 単体のテストなど）では、常にコードの既定テンプレートを使う。</summary>
public sealed class DefaultPromptStore : IPromptStore
{
    public Task<StoredPrompt?> ResolveAsync(string key, Guid tenantId, CancellationToken ct) => Task.FromResult<StoredPrompt?>(null);
}

/// <summary>
/// テンプレート（Scriban）を解決して値を埋め込む（RF-DES-001 4.5）。安全規約（common.safety）は {{ safety }} で差し込む。
/// 保存された版が壊れていた場合は既定テンプレートで生成を続け、ログに残す。
/// </summary>
public sealed class PromptCatalog(IPromptStore store, ITenantContext tenant, ILogger<PromptCatalog> log) : IPromptCatalog
{
    public const int DefaultVersion = 1;

    private static readonly ConcurrentDictionary<string, Template> s_parsed = new();

    public async Task<PromptText> RenderAsync(string key, IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        var template = await ResolveAsync(key, ct);
        var all = new Dictionary<string, object?>(values);
        if (key != PromptKeys.Safety && template.Body.Contains("safety", StringComparison.Ordinal))
        {
            var safety = await ResolveAsync(PromptKeys.Safety, ct);
            all["safety"] = Render(safety.Body, new Dictionary<string, object?>()).Trim();
        }
        try
        {
            return new PromptText(key, template.Version, Render(template.Body, all));
        }
        catch (Exception ex) when (template.Version != DefaultVersion && ex is InvalidOperationException or ScriptRuntimeException)
        {
            log.LogError(ex, "Prompt {Key} v{Version} failed to render; falling back to the default template", key, template.Version);
            return new PromptText(key, DefaultVersion, Render(Default(key), all));
        }
    }

    public PromptText RenderBody(string key, int version, string body, IReadOnlyDictionary<string, object?> values) =>
        new(key, version, Render(body, values));

    private async Task<StoredPrompt> ResolveAsync(string key, CancellationToken ct) =>
        await store.ResolveAsync(key, tenant.TenantId, ct) ?? new StoredPrompt(key, DefaultVersion, Default(key));

    public static string Default(string key) =>
        PromptLibrary.Defaults.TryGetValue(key, out var body) ? body : throw new ArgumentException($"Unknown prompt '{key}'", nameof(key));

    /// <summary>テンプレートの文法を確認する。問題がなければ null。</summary>
    public static string? Validate(string body)
    {
        var template = Template.Parse(body);
        return template.HasErrors ? string.Join(" / ", template.Messages.Select(m => m.ToString())) : null;
    }

    /// <summary>
    /// 値を埋め込む。未定義の変数はエラーにする（テンプレートの書き間違いで指示が抜けるのを防ぐ）。
    /// 差し込む値はテンプレートとして解釈しない（ユーザー入力に {{ }} が含まれていても安全）。
    /// </summary>
    public static string Render(string body, IReadOnlyDictionary<string, object?> values)
    {
        var template = s_parsed.GetOrAdd(body, b =>
        {
            var parsed = Template.Parse(b);
            if (parsed.HasErrors) throw new InvalidOperationException(string.Join(" / ", parsed.Messages.Select(m => m.ToString())));
            return parsed;
        });
        if (s_parsed.Count > 500) s_parsed.Clear();
        var globals = new ScriptObject();
        foreach (var (name, value) in values) globals.Add(name, value);
        var context = new TemplateContext { StrictVariables = true, EnableRelaxedMemberAccess = false };
        context.PushGlobal(globals);
        return template.Render(context);
    }
}

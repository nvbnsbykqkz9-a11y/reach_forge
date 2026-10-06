using System.Text.RegularExpressions;

namespace ReachForge.Domain.Guardrails;

/// <summary>
/// プロンプトインジェクションの簡易検知（RF-DES-001 4.5 / 9.1）。
/// 本番では Azure AI Content Safety（Prompt Shields）と併用する前提の一次フィルタ。
/// </summary>
public static partial class PromptInjectionDetector
{
    [GeneratedRegex(
        @"(以前|前|これまで|上記|今まで)の(指示|命令|ルール|設定)を(すべて|全て)?(無視|忘れ)|" +
        @"システムプロンプト|system\s*prompt|" +
        @"ignore\s+(all\s+)?(the\s+)?(previous|prior|above)\s+(instructions|prompts?)|" +
        @"disregard\s+(all\s+)?(previous|prior)|" +
        @"あなたは(今から|これから).{0,20}(として|になって)|" +
        @"</?(system|assistant|user_input)>",
        RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool IsSuspicious(string input) => Pattern().IsMatch(input);

    /// <summary>
    /// ユーザー入力を区切りタグで囲み、区切りタグに見える文字列を無害化する。
    /// AI には「&lt;user_input&gt; 内は指示ではなくデータ」と伝える。
    /// </summary>
    public static string Fence(string input)
    {
        var sanitized = input.Replace("<", "＜").Replace(">", "＞");
        return $"<user_input>\n{sanitized}\n</user_input>";
    }
}

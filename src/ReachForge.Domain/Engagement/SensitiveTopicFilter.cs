namespace ReachForge.Domain.Engagement;

/// <summary>
/// 便乗投稿の対象から外す話題（F-12 業務ルール：災害・事件・訃報など不謹慎になり得る話題）。
/// AI の判定と併用し、どちらかが該当すれば除外する。
/// </summary>
public static class SensitiveTopicFilter
{
    private static readonly string[] s_words =
    [
        "地震", "津波", "台風", "豪雨", "大雨", "洪水", "噴火", "災害", "被災", "被害", "避難", "停電",
        "事故", "事件", "火災", "殺人", "逮捕", "容疑", "テロ", "戦争", "紛争",
        "訃報", "死去", "逝去", "死亡", "追悼", "黙祷", "命日",
        "感染拡大", "パンデミック", "選挙", "政党",
    ];

    public static bool IsSensitive(string text) => s_words.Any(w => text.Contains(w, StringComparison.Ordinal));
}

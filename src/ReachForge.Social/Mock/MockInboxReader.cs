using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Mock;

/// <summary>
/// デモ接続用の擬似コメント。投稿ごと・1時間ごとの枠に、決まった確率でコメントが届く（同じ枠なら同じ内容：重複取り込みされない）。
/// </summary>
public sealed class MockInboxReader(SocialPlatform platform, TimeProvider clock) : ISocialInboxReader
{
    public SocialPlatform Platform { get; } = platform;
    public bool IsSimulation => true;

    internal static readonly string[] Samples =
    [
        "ラテは何時から買えますか？",
        "土曜日は何時まで営業していますか？",
        "写真すてきです！週末に行きます☕",
        "4人で予約できますか？",
        "さつまいもラテ、とても美味しかったです！",
        "注文したのに届いていません。どうなっていますか？",
        "フォロワーを1000人増やします！今すぐDMください https://spam.example",
        "テイクアウトはできますか？",
        "駐車場はありますか？",
        "いつも癒やされています。ありがとう！",
    ];

    private static readonly string[] s_authors =
        ["@hana_cafe", "@yuki_123", "@mari.coffee", "@taku_s", "@sweets_love", "@kenji_w", "@aya_m", "@nao_t", "@riko.k", "@sho_1990"];

    public Task<IReadOnlyList<InboxItem>> FetchAsync(DateTimeOffset since, IReadOnlyList<string> recentPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var from = Math.Max(since.ToUnixTimeSeconds() / 3600 + 1, now.ToUnixTimeSeconds() / 3600 - 48);
        var to = now.ToUnixTimeSeconds() / 3600;
        var items = new List<InboxItem>();
        foreach (var post in recentPostIds.Take(10))
        {
            for (var hour = from; hour <= to; hour++)
            {
                var h = Hash($"{post}:{hour}");
                if (h % 40 != 0) continue; // 投稿1件あたり 1日に0〜1件程度
                var text = Samples[h / 40 % Samples.Length];
                var author = text.StartsWith("フォロワー", StringComparison.Ordinal) ? "@promo_bot99" : s_authors[h / 400 % s_authors.Length];
                items.Add(new InboxItem($"mock-c-{post}-{hour}", Platform,
                    Platform == SocialPlatform.Line ? InboxKind.DirectMessage : InboxKind.Comment,
                    author, author, text, DateTimeOffset.FromUnixTimeSeconds(hour * 3600 + h % 3000), post));
            }
        }
        return Task.FromResult<IReadOnlyList<InboxItem>>(items);
    }

    public Task<string?> ReplyAsync(InboxItem target, string text, ChannelCredential credential, CancellationToken ct) =>
        Task.FromResult<string?>($"mock-reply-{Guid.CreateVersion7(clock.GetUtcNow()):N}");

    public Task<bool> HideAsync(InboxItem target, ChannelCredential credential, CancellationToken ct) => Task.FromResult(true);

    private static int Hash(string value)
    {
        unchecked
        {
            var h = 23;
            foreach (var ch in value) h = h * 31 + ch;
            return Math.Abs(h % 1_000_000);
        }
    }
}

using System.Text.Json.Nodes;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.Social.Webhooks;

/// <summary>Webhook から取り込むコメント等（どのアカウント宛てか）。</summary>
public sealed record WebhookInboxEvent(SocialPlatform Platform, string AccountId, InboxItem Item);

/// <summary>
/// Webhook の本文を受信箱の項目に変換する。各社の形式は変わるため、知らない形のイベントは無視する
/// （取りこぼしはポーリングで補う）。本番接続前に各社の公式ドキュメントで形式を再確認すること。
/// </summary>
public static class WebhookParsers
{
    /// <summary>Meta（object=page：feed のコメント・Messenger／object=instagram：comments・mentions・DM）。</summary>
    public static IReadOnlyList<WebhookInboxEvent> ParseMeta(string body, DateTimeOffset now)
    {
        var json = Parse(body);
        var obj = SocialHttp.StrOrNull(json, "object");
        var platform = obj == "instagram" ? SocialPlatform.Instagram : SocialPlatform.Facebook;
        var result = new List<WebhookInboxEvent>();
        foreach (var entry in Array(json?["entry"]))
        {
            var account = SocialHttp.StrOrNull(entry, "id") ?? "";
            foreach (var change in Array(entry["changes"]))
            {
                var field = SocialHttp.StrOrNull(change, "field");
                var v = change["value"];
                if (platform == SocialPlatform.Facebook && field == "feed" && SocialHttp.StrOrNull(v, "item") == "comment"
                    && SocialHttp.StrOrNull(v, "verb") == "add")
                {
                    result.Add(new(platform, account, new InboxItem(SocialHttp.StrOrNull(v, "comment_id") ?? "", platform, InboxKind.Comment,
                        SocialHttp.StrOrNull(v, "from.id") ?? "", SocialHttp.StrOrNull(v, "from.name") ?? "Facebookユーザー",
                        SocialHttp.StrOrNull(v, "message") ?? "", UnixSeconds(SocialHttp.StrOrNull(v, "created_time"), now),
                        SocialHttp.StrOrNull(v, "post_id"))));
                }
                else if (platform == SocialPlatform.Instagram && field is "comments" or "mentions")
                {
                    var username = SocialHttp.StrOrNull(v, "from.username") ?? SocialHttp.StrOrNull(v, "username");
                    result.Add(new(platform, account, new InboxItem(SocialHttp.StrOrNull(v, "id") ?? SocialHttp.StrOrNull(v, "comment_id") ?? "",
                        platform, field == "comments" ? InboxKind.Comment : InboxKind.Mention,
                        SocialHttp.StrOrNull(v, "from.id") ?? username ?? "", "@" + (username ?? "instagram"),
                        SocialHttp.StrOrNull(v, "text") ?? "", now, SocialHttp.StrOrNull(v, "media.id") ?? SocialHttp.StrOrNull(v, "media_id"))));
                }
            }
            foreach (var m in Array(entry["messaging"]))
            {
                if (m["message"] is not { } message || SocialHttp.StrOrNull(message, "is_echo") == "true") continue;
                var sender = SocialHttp.StrOrNull(m, "sender.id") ?? "";
                result.Add(new(platform, account, new InboxItem(SocialHttp.StrOrNull(message, "mid") ?? "", platform, InboxKind.DirectMessage,
                    sender, platform == SocialPlatform.Instagram ? "Instagramのメッセージ" : "Messengerのメッセージ",
                    SocialHttp.StrOrNull(message, "text") ?? "", UnixMillis(SocialHttp.StrOrNull(m, "timestamp"), now), null)));
            }
        }
        return result.Where(e => e.Item.ExternalId.Length > 0 && e.Item.Text.Length > 0).ToList();
    }

    /// <summary>Threads（replies・mentions）。</summary>
    public static IReadOnlyList<WebhookInboxEvent> ParseThreads(string body, DateTimeOffset now)
    {
        var json = Parse(body);
        var result = new List<WebhookInboxEvent>();
        var changes = Array(json?["entry"]).SelectMany(e => Array(e["changes"]).Select(c => (Account: SocialHttp.StrOrNull(e, "id"), Change: c)))
            .Concat(Array(json?["values"]).Select(v => (Account: (string?)null, Change: v)));
        foreach (var (account, change) in changes)
        {
            var field = SocialHttp.StrOrNull(change, "field");
            if (field is not ("replies" or "mentions")) continue;
            var v = change["value"];
            var username = SocialHttp.StrOrNull(v, "username") ?? "threads";
            var owner = account ?? SocialHttp.StrOrNull(v, "root_post.owner_id") ?? SocialHttp.StrOrNull(v, "replied_to.owner_id") ?? "";
            result.Add(new(SocialPlatform.Threads, owner, new InboxItem(SocialHttp.StrOrNull(v, "id") ?? "", SocialPlatform.Threads,
                field == "replies" ? InboxKind.Comment : InboxKind.Mention, username, "@" + username, SocialHttp.StrOrNull(v, "text") ?? "",
                DateTimeOffset.TryParse(SocialHttp.StrOrNull(v, "timestamp"), out var t) ? t : now,
                SocialHttp.StrOrNull(v, "root_post.id") ?? SocialHttp.StrOrNull(v, "replied_to.id"))));
        }
        return result.Where(e => e.Item.ExternalId.Length > 0 && e.Item.Text.Length > 0).ToList();
    }

    /// <summary>LINE（友だちからのテキストメッセージ）。チャネルは URL で特定するためアカウント ID は空。</summary>
    public static IReadOnlyList<InboxItem> ParseLine(string body, DateTimeOffset now)
    {
        var json = Parse(body);
        return Array(json?["events"])
            .Where(e => SocialHttp.StrOrNull(e, "type") == "message" && SocialHttp.StrOrNull(e, "message.type") == "text"
                        && SocialHttp.StrOrNull(e, "source.type") == "user")
            .Select(e => new InboxItem(SocialHttp.StrOrNull(e, "message.id") ?? SocialHttp.StrOrNull(e, "webhookEventId") ?? "",
                SocialPlatform.Line, InboxKind.DirectMessage, SocialHttp.StrOrNull(e, "source.userId") ?? "", "LINEの友だち",
                SocialHttp.StrOrNull(e, "message.text") ?? "", UnixMillis(SocialHttp.StrOrNull(e, "timestamp"), now), null))
            .Where(i => i.ExternalId.Length > 0 && i.AuthorId.Length > 0)
            .ToList();
    }

    private static JsonNode? Parse(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonNode> Array(JsonNode? node) => node is JsonArray a ? a.OfType<JsonNode>() : [];

    private static DateTimeOffset UnixSeconds(string? value, DateTimeOffset fallback) =>
        long.TryParse(value, out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : fallback;

    private static DateTimeOffset UnixMillis(string? value, DateTimeOffset fallback) =>
        long.TryParse(value, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : fallback;
}

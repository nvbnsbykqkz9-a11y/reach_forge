using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ReachForge.Application.Social;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Social.Line;
using ReachForge.Social.Meta;
using ReachForge.Social.Threads;
using ReachForge.Social.X;

namespace ReachForge.Social.Inbox;

internal static class InboxJson
{
    /// <summary>Graph API の "2026-10-06T10:00:00+0000" 形式にも対応する。</summary>
    public static DateTimeOffset Time(string? value, DateTimeOffset fallback)
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        if (value.Length > 5 && (value[^5] == '+' || value[^5] == '-') && value[^3] != ':') value = value[..^2] + ":" + value[^2..];
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : fallback;
    }

    public static IEnumerable<JsonNode> Items(JsonNode json, string key = "data") =>
        json[key] is JsonArray a ? a.OfType<JsonNode>() : [];
}

/// <summary>
/// X：自分宛てのメンション・リプライ（GET /2/users/{id}/mentions）。読み取りは従量課金のため取得件数を絞る。
/// 返信は POST /2/tweets（reply.in_reply_to_tweet_id）で、投稿と同じく費用がかかる。
/// </summary>
public sealed class XInboxReader(IHttpClientFactory http, TimeProvider clock) : ISocialInboxReader
{
    public const int MaxResults = 20;
    public SocialPlatform Platform => SocialPlatform.X;
    public bool IsSimulation => false;

    public async Task<IReadOnlyList<InboxItem>> FetchAsync(DateTimeOffset since, IReadOnlyList<string> recentPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(http.CreateClient(XConnector.HttpClientName), SocialHttp.Get(
            $"2/users/{Uri.EscapeDataString(credential.ExternalAccountId)}/mentions?" + SocialHttp.Query(
                ("max_results", MaxResults.ToString(CultureInfo.InvariantCulture)),
                ("start_time", since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                ("tweet.fields", "created_at,author_id,referenced_tweets"),
                ("expansions", "author_id"),
                ("user.fields", "username,name")), credential.AccessToken), "X", ct);
        var users = InboxJson.Items(json["includes"] ?? new JsonObject(), "users")
            .ToDictionary(u => SocialHttp.StrOrNull(u, "id") ?? "", u => "@" + SocialHttp.StrOrNull(u, "username"));
        return InboxJson.Items(json).Select(t =>
        {
            var author = SocialHttp.StrOrNull(t, "author_id") ?? "";
            var repliedTo = (t["referenced_tweets"] as JsonArray)?.OfType<JsonNode>()
                .FirstOrDefault(r => SocialHttp.StrOrNull(r, "type") == "replied_to");
            return new InboxItem(SocialHttp.Str(t, "id"), Platform, InboxKind.Mention, author, users.GetValueOrDefault(author, author),
                SocialHttp.StrOrNull(t, "text") ?? "", InboxJson.Time(SocialHttp.StrOrNull(t, "created_at"), clock.GetUtcNow()),
                repliedTo is null ? null : SocialHttp.StrOrNull(repliedTo, "id"));
        }).ToList();
    }

    public async Task<string?> ReplyAsync(InboxItem target, string text, ChannelCredential credential, CancellationToken ct)
    {
        var json = await SocialHttp.SendAsync(http.CreateClient(XConnector.HttpClientName), SocialHttp.Json(HttpMethod.Post, "2/tweets",
            new { text, reply = new { in_reply_to_tweet_id = target.ExternalId } }, credential.AccessToken), "X", ct);
        return SocialHttp.StrOrNull(json, "data.id");
    }

    /// <summary>自分の投稿へのリプライを非表示にする（PUT /2/tweets/{id}/hidden）。</summary>
    public async Task<bool> HideAsync(InboxItem target, ChannelCredential credential, CancellationToken ct)
    {
        if (target.InReplyToExternalPostId is null) return false;
        await SocialHttp.SendAsync(http.CreateClient(XConnector.HttpClientName), SocialHttp.Json(HttpMethod.Put,
            $"2/tweets/{Uri.EscapeDataString(target.ExternalId)}/hidden", new { hidden = true }, credential.AccessToken), "X", ct);
        return true;
    }
}

/// <summary>Facebook ページの投稿へのコメント（GET /{post-id}/comments）。返信は POST /{comment-id}/comments。</summary>
public sealed class FacebookInboxReader(IHttpClientFactory http, IOptions<SocialOptions> options, TimeProvider clock) : ISocialInboxReader
{
    public SocialPlatform Platform => SocialPlatform.Facebook;
    public bool IsSimulation => false;
    private string V => options.Value.Meta.GraphVersion;

    public async Task<IReadOnlyList<InboxItem>> FetchAsync(DateTimeOffset since, IReadOnlyList<string> recentPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var client = http.CreateClient(MetaConnector.HttpClientName);
        var result = new List<InboxItem>();
        foreach (var post in recentPostIds)
        {
            var json = await SocialHttp.SendAsync(client, SocialHttp.Get(
                $"{V}/{Uri.EscapeDataString(post)}/comments?" + SocialHttp.Query(("fields", "id,message,from{id,name},created_time"),
                    ("order", "reverse_chronological"), ("limit", "50"), ("since", since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))),
                credential.AccessToken), "Facebook", ct);
            result.AddRange(InboxJson.Items(json)
                .Where(c => SocialHttp.StrOrNull(c, "from.id") != credential.ExternalAccountId) // 自分（ページ）の返信は除く
                .Select(c => new InboxItem(SocialHttp.Str(c, "id"), Platform, InboxKind.Comment, SocialHttp.StrOrNull(c, "from.id") ?? "",
                    SocialHttp.StrOrNull(c, "from.name") ?? "Facebookユーザー", SocialHttp.StrOrNull(c, "message") ?? "",
                    InboxJson.Time(SocialHttp.StrOrNull(c, "created_time"), clock.GetUtcNow()), post)));
        }
        return result;
    }

    public async Task<string?> ReplyAsync(InboxItem target, string text, ChannelCredential credential, CancellationToken ct) =>
        SocialHttp.StrOrNull(await SocialHttp.SendAsync(http.CreateClient(MetaConnector.HttpClientName), SocialHttp.Form(HttpMethod.Post,
            $"{V}/{Uri.EscapeDataString(target.ExternalId)}/comments", [new("message", text)], credential.AccessToken), "Facebook", ct), "id");

    public async Task<bool> HideAsync(InboxItem target, ChannelCredential credential, CancellationToken ct)
    {
        await SocialHttp.SendAsync(http.CreateClient(MetaConnector.HttpClientName), SocialHttp.Form(HttpMethod.Post,
            $"{V}/{Uri.EscapeDataString(target.ExternalId)}", [new("is_hidden", "true")], credential.AccessToken), "Facebook", ct);
        return true;
    }
}

/// <summary>Instagram のメディアへのコメント（GET /{media-id}/comments）。返信は POST /{comment-id}/replies。</summary>
public sealed class InstagramInboxReader(IHttpClientFactory http, IOptions<SocialOptions> options, TimeProvider clock) : ISocialInboxReader
{
    public SocialPlatform Platform => SocialPlatform.Instagram;
    public bool IsSimulation => false;
    private string V => options.Value.Meta.GraphVersion;

    public async Task<IReadOnlyList<InboxItem>> FetchAsync(DateTimeOffset since, IReadOnlyList<string> recentPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var client = http.CreateClient(MetaConnector.HttpClientName);
        var result = new List<InboxItem>();
        foreach (var media in recentPostIds)
        {
            var json = await SocialHttp.SendAsync(client, SocialHttp.Get(
                $"{V}/{Uri.EscapeDataString(media)}/comments?fields=id,text,username,timestamp,from&limit=50", credential.AccessToken),
                "Instagram", ct);
            result.AddRange(InboxJson.Items(json)
                .Select(c => new InboxItem(SocialHttp.Str(c, "id"), Platform, InboxKind.Comment,
                    SocialHttp.StrOrNull(c, "from.id") ?? SocialHttp.StrOrNull(c, "username") ?? "",
                    "@" + (SocialHttp.StrOrNull(c, "username") ?? "instagram"), SocialHttp.StrOrNull(c, "text") ?? "",
                    InboxJson.Time(SocialHttp.StrOrNull(c, "timestamp"), clock.GetUtcNow()), media))
                .Where(i => i.ReceivedAt >= since));
        }
        return result;
    }

    public async Task<string?> ReplyAsync(InboxItem target, string text, ChannelCredential credential, CancellationToken ct) =>
        SocialHttp.StrOrNull(await SocialHttp.SendAsync(http.CreateClient(MetaConnector.HttpClientName), SocialHttp.Form(HttpMethod.Post,
            $"{V}/{Uri.EscapeDataString(target.ExternalId)}/replies", [new("message", text)], credential.AccessToken), "Instagram", ct), "id");

    public async Task<bool> HideAsync(InboxItem target, ChannelCredential credential, CancellationToken ct)
    {
        await SocialHttp.SendAsync(http.CreateClient(MetaConnector.HttpClientName), SocialHttp.Form(HttpMethod.Post,
            $"{V}/{Uri.EscapeDataString(target.ExternalId)}", [new("hide", "true")], credential.AccessToken), "Instagram", ct);
        return true;
    }
}

/// <summary>
/// Threads の投稿への返信（GET /{media-id}/replies、threads_read_replies 権限）。
/// 返信は TEXT コンテナ（reply_to_id）を作成して公開する。非表示は POST /{reply-id}/manage_reply。
/// </summary>
public sealed class ThreadsInboxReader(IHttpClientFactory http, IOptions<SocialOptions> options, TimeProvider clock) : ISocialInboxReader
{
    public SocialPlatform Platform => SocialPlatform.Threads;
    public bool IsSimulation => false;
    private string V => options.Value.Threads.ApiVersion;

    public async Task<IReadOnlyList<InboxItem>> FetchAsync(DateTimeOffset since, IReadOnlyList<string> recentPostIds,
        ChannelCredential credential, CancellationToken ct)
    {
        var client = http.CreateClient(ThreadsConnector.HttpClientName);
        var result = new List<InboxItem>();
        foreach (var media in recentPostIds)
        {
            var json = await SocialHttp.SendAsync(client, SocialHttp.Get(
                $"{V}/{Uri.EscapeDataString(media)}/replies?fields=id,text,username,timestamp", credential.AccessToken), "Threads", ct);
            result.AddRange(InboxJson.Items(json)
                .Select(c => new InboxItem(SocialHttp.Str(c, "id"), Platform, InboxKind.Comment, SocialHttp.StrOrNull(c, "username") ?? "",
                    "@" + (SocialHttp.StrOrNull(c, "username") ?? "threads"), SocialHttp.StrOrNull(c, "text") ?? "",
                    InboxJson.Time(SocialHttp.StrOrNull(c, "timestamp"), clock.GetUtcNow()), media))
                .Where(i => i.ReceivedAt >= since));
        }
        return result;
    }

    public async Task<string?> ReplyAsync(InboxItem target, string text, ChannelCredential credential, CancellationToken ct)
    {
        var client = http.CreateClient(ThreadsConnector.HttpClientName);
        var container = SocialHttp.Str(await SocialHttp.SendAsync(client, SocialHttp.Form(HttpMethod.Post,
            $"{V}/{credential.ExternalAccountId}/threads",
            [new("media_type", "TEXT"), new("text", text), new("reply_to_id", target.ExternalId)], credential.AccessToken), "Threads", ct), "id");
        return SocialHttp.StrOrNull(await SocialHttp.SendAsync(client, SocialHttp.Form(HttpMethod.Post,
            $"{V}/{credential.ExternalAccountId}/threads_publish", [new("creation_id", container)], credential.AccessToken), "Threads", ct), "id");
    }

    public async Task<bool> HideAsync(InboxItem target, ChannelCredential credential, CancellationToken ct)
    {
        await SocialHttp.SendAsync(http.CreateClient(ThreadsConnector.HttpClientName), SocialHttp.Form(HttpMethod.Post,
            $"{V}/{Uri.EscapeDataString(target.ExternalId)}/manage_reply", [new("hide", "true")], credential.AccessToken), "Threads", ct);
        return true;
    }
}

/// <summary>
/// LINE：トーク（DM）は Webhook でのみ届く（ポーリング API はない）。返信はプッシュメッセージ（月の無料通数を消費）。
/// 再試行で二重送信しないよう、対象と本文から決まる X-Line-Retry-Key を付ける。
/// </summary>
public sealed class LineInboxReader(IHttpClientFactory http) : ISocialInboxReader
{
    public SocialPlatform Platform => SocialPlatform.Line;
    public bool IsSimulation => false;

    public Task<IReadOnlyList<InboxItem>> FetchAsync(DateTimeOffset since, IReadOnlyList<string> recentPostIds,
        ChannelCredential credential, CancellationToken ct) => Task.FromResult<IReadOnlyList<InboxItem>>([]);

    public async Task<string?> ReplyAsync(InboxItem target, string text, ChannelCredential credential, CancellationToken ct)
    {
        var request = SocialHttp.Json(HttpMethod.Post, "v2/bot/message/push",
            new { to = target.AuthorId, messages = new[] { new { type = "text", text } } }, credential.AccessToken);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{target.ExternalId}\n{text}"));
        request.Headers.Add("X-Line-Retry-Key", new Guid(hash.AsSpan(0, 16)).ToString());
        try
        {
            var json = await SocialHttp.SendAsync(http.CreateClient(LineConnector.HttpClientName), request, "LINE", ct);
            return (json["sentMessages"] as JsonArray)?.FirstOrDefault() is { } m ? SocialHttp.StrOrNull(m, "id") : null;
        }
        catch (SocialApiException ex) when (ex.ErrorCode == SocialHttp.ConflictCode)
        {
            return null; // 同じキーで受付済み
        }
    }
}

public sealed class InboxReaderFactory(IEnumerable<ISocialInboxReader> readers) : IInboxReaderFactory
{
    private readonly ISocialInboxReader[] _all = [.. readers];

    public ISocialInboxReader? Get(SocialPlatform platform, bool demo) =>
        _all.LastOrDefault(r => r.Platform == platform && r.IsSimulation == demo);
}

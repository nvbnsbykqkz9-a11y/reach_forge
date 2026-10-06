using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.AI.Providers;

/// <summary>
/// ローカル開発・自動テスト用の決定的な AI スタブ（RF-DES-001 3.5 Local 環境）。
/// API キーなしで画面・フロー全体を動かすためのもので、本番のルートには含めない。
/// </summary>
public sealed class StubChatClient : IChatClient
{
    private static readonly JsonSerializerOptions s_json = AIJsonUtilities.DefaultOptions;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = AiCallContext.From(options);
        var text = ctx?.Payload switch
        {
            CopyStubPayload p when p.Base is not null && p.Fix is not null =>
                Serialize(new CopyBatch([Refine(p.Base, p.Fix.Value)])),
            CopyStubPayload p => Serialize(new CopyBatch(Copies(p))),
            VariantStubPayload p => Serialize(Variant(p)),
            JudgeStubPayload p => Serialize(Judge(p)),
            DigestStubPayload p => Serialize(new DigestResult(Digest(p))),
            _ => "{}",
        };
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = "stub-local",
            Usage = new UsageDetails
            {
                InputTokenCount = messages.Sum(m => m.Text.Length) / 2,
                OutputTokenCount = text.Length / 2,
            },
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("stub", null, "stub-local")
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, s_json);

    private static GeneratedCopy[] Copies(CopyStubPayload p)
    {
        var theme = p.Request.Theme.Trim();
        var product = p.Products.FirstOrDefault(x => p.Request.ProductIds.Contains(x.Id));
        var emoji = p.Brand.Tone.EmojiLevel > 0;
        var casual = p.Brand.Tone.Casualness >= 50;
        var subject = product?.Name ?? theme;
        var when = product?.AvailableFrom is { } from ? $"{from:M/d}から" : "いまだけ";
        var price = product?.Price is { } yen ? $"{yen:N0}円" : null;
        var tags = BuildTags(theme, product?.Name, p.Brand.PreferredHashtags);
        var cta = p.Request.Objective switch
        {
            PostObjective.Traffic => "お近くの店舗でお待ちしています",
            PostObjective.Conversion => "詳しくはプロフィールのリンクから",
            PostObjective.Engagement => "みなさんの感想もぜひ教えてください",
            _ => "ぜひチェックしてみてください",
        };

        var drafts = new[]
        {
            new GeneratedCopy(
                $"{Season()}は、{subject}から。",
                $"{subject}が{when}登場します{(emoji ? "✨" : "。")}\n{Describe(product?.Description, theme)}{(price is null ? "" : $"\n価格は{price}です。")}",
                cta, tags),
            new GeneratedCopy(
                casual ? $"{subject}、はじまりました！" : $"{subject}のお知らせ",
                casual
                    ? $"お待たせしました{(emoji ? "🎉" : "！")}{subject}、{when}楽しめます。\n{Describe(product?.Description, theme)}"
                    : $"{subject}を{when}ご用意いたしました。\n{Describe(product?.Description, theme)}",
                cta, tags.Take(Math.Max(1, tags.Length - 1)).ToArray()),
            new GeneratedCopy(
                $"知っていましたか？{subject}のこと",
                $"「{theme}」について、{p.Brand.Tone.FirstPerson}がいちばん伝えたいことをまとめました{(emoji ? "☕" : "。")}\n{Describe(product?.Description, theme)}",
                cta, tags),
            new GeneratedCopy(
                $"{subject}をもっと楽しむ3つのポイント",
                $"1. {subject}の魅力\n2. おすすめの楽しみ方\n3. {when}の特典\n{Describe(product?.Description, theme)}",
                cta, tags),
            new GeneratedCopy(
                $"スタッフおすすめ：{subject}",
                $"スタッフが自信をもっておすすめする{subject}{(emoji ? "😊" : "。")}{when}お試しください。",
                cta, tags),
        };
        return drafts.Take(Math.Clamp(p.Request.Count, 1, drafts.Length)).ToArray();
    }

    private static GeneratedCopy Refine(GeneratedCopy c, QuickFix fix) => fix switch
    {
        QuickFix.Shorter => c with { Body = FirstSentences(c.Body, 1) },
        QuickFix.Longer => c with { Body = $"{c.Body}\nひとつひとつ丁寧に仕上げました。季節の味わいをゆっくり楽しんでください。" },
        QuickFix.Casual => c with { Body = c.Body.Replace("いたしました", "しました").Replace("ございます", "です").Replace("ください", "ね") },
        QuickFix.Polite => c with { Body = c.Body.Replace("！", "。").Replace("しました", "いたしました") },
        QuickFix.MoreEmoji => c with { Body = c.Body.Replace("。", "😊", StringComparison.Ordinal) + " ✨" },
        QuickFix.LessEmoji => c with { Body = StripEmoji(c.Body) },
        QuickFix.StrongerCta => c with { Cta = $"今すぐ{c.Cta.TrimEnd('。')}！" },
        _ => c,
    };

    private static VariantDraft Variant(VariantStubPayload p)
    {
        var r = p.Request;
        var c = PlatformCatalog.Get(r.Platform);
        var tags = r.Hashtags.Take(Math.Max(c.RecommendedHashtags.Max, 0)).ToArray();
        var core = r.Body.Trim();
        var link = p.Link is null ? "" : $"\n{p.Link}";
        string body = r.Platform switch
        {
            SocialPlatform.X => $"{r.Headline}\n{FirstSentences(core, 1)}{link}",
            SocialPlatform.Instagram => $"{r.Headline}\n\n{core}\n\n{r.Cta}\n▶ 詳しくはプロフィールのリンクから",
            SocialPlatform.Threads => $"{FirstSentences(core, 2)}\nみなさんはどう楽しみますか？{link}",
            SocialPlatform.Line => $"【{r.Headline}】\n{FirstSentences(core, 1)}\n{r.Cta}{link}",
            SocialPlatform.Facebook => $"{r.Headline}\n\n{core}\n\n{r.Cta}{link}",
            SocialPlatform.TikTok => $"{r.Headline}（冒頭2秒のフック）\n{FirstSentences(core, 1)}",
            SocialPlatform.YouTube => $"{core}\n\n{r.Cta}{link}",
            SocialPlatform.LinkedIn => $"{r.Headline}\n\n{core}\n\n私たちが大切にしていること：お客様の体験です。{link}",
            SocialPlatform.Pinterest => $"{r.Headline}｜{FirstSentences(core, 1)}{link}",
            _ => core,
        };
        if (r.Platform == SocialPlatform.YouTube && !tags.Contains("Shorts")) tags = ["Shorts", .. tags.Take(2)];
        var title = c.MaxTitleLength is { } max ? PostText.Truncate(r.Headline, max) : null;
        if (c.TargetBodyLength is { } target) body = PostText.Truncate(body, Math.Max(target, PostText.Length(link) + 20));
        return new VariantDraft(body, tags, title);
    }

    private static JudgeResult Judge(JudgeStubPayload p)
    {
        var score = 4.0;
        var text = p.Copy.Body + p.Copy.Headline;
        if (p.Brand.MustPhrases.All(m => text.Contains(m, StringComparison.Ordinal))) score += 0.3;
        if (p.Brand.NgWords.Any(n => text.Contains(n, StringComparison.Ordinal))) score -= 1.5;
        var len = PostText.Length(p.Copy.Body);
        score += len is >= 40 and <= 200 ? 0.3 : -0.2;
        if (p.Brand.Tone.EmojiLevel == 0 && text.Any(char.IsSurrogate)) score -= 0.4;
        return new JudgeResult(Math.Round(Math.Clamp(score, 1, 5), 1), "ブランドの口調・必須表記・長さを評価しました");
    }

    private static string Digest(DigestStubPayload p) =>
        $"{p.Headline.TrimEnd('。')}を伝え、{(p.Body.Contains("店舗") ? "来店" : "反応")}を促す投稿";

    private static string[] BuildTags(string theme, string? product, IEnumerable<string> preferred)
    {
        var words = theme.Split([' ', '　', '、', '。', 'の', 'を', 'が'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length is >= 2 and <= 12);
        return preferred.Concat(product is null ? [] : [product.Replace(" ", "")]).Concat(words)
            .Select(PostText.Normalize).Where(t => t.Length > 0).Distinct().Take(6).ToArray();
    }

    private static string Describe(string? description, string theme) =>
        string.IsNullOrWhiteSpace(description) ? $"{theme}の魅力をぜひ体験してください。" : description.Trim();

    private static string FirstSentences(string text, int count)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var ch in text)
        {
            sb.Append(ch);
            if (ch is '。' or '！' or '!' or '\n' && ++n >= count) break;
        }
        return sb.ToString().Trim();
    }

    private static string StripEmoji(string text)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsSurrogate(text[i]) || text[i] is '✨' or '☕' or '️') continue;
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    private static string Season() => DateTime.Now.Month switch
    {
        3 or 4 or 5 => "この春",
        6 or 7 or 8 => "この夏",
        9 or 10 or 11 => "今年の秋",
        _ => "この冬",
    };
}

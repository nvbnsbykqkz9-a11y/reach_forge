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
            JudgeStubPayload p => Serialize(Judge(p)),
            BrandStubPayload p => Serialize(Brand(p.Input)),
            LpCreativeStubPayload p => Serialize(LpCreativeOf(p)),
            ScriptStubPayload p => Serialize(Script(p)),
            LandingPageStubPayload p => Serialize(LandingPage(p)),
            LpImagesStubPayload p => Serialize(new LpImagePicksDraft([.. p.Candidates
                .OrderByDescending(c => (long)c.Width * c.Height)
                .Take(p.Max)
                .Select(c => new LpImagePickDraft(c.Index, string.IsNullOrWhiteSpace(c.Alt) ? "商品の魅力が伝わる写真" : PostText.Truncate(c.Alt, 30),
                    (c.Alt ?? "").Contains("画面", StringComparison.Ordinal) ? "screen" : "photo"))])),
            LpVisualsStubPayload p => Serialize(new LpVisualsDraft(
                new LpDirectionDraft("落ち着いた信頼感のある雰囲気", [.. p.Page.Colors.Take(3)], "aurora",
                    "a calm modern workspace at dusk with soft window light"),
                [.. p.Sources.Select((d, i) => new LpVisualDraft(i,
                new[] { "こだわり", "使うシーン", "できあがり", "おすすめ" }[i % 4],
                PostText.Truncate((p.Page.Title.Split('|', '｜', '-')[0]).Trim() is { Length: > 0 } t ? t : p.BrandName, 15),
                "Place the product on a warm wooden table in soft morning light, with a few natural props, shallow depth of field.",
                "Slow push-in camera move with gentle light flicker.",
                "A softly lit modern desk with a blurred city view behind, cinematic depth of field."))])),
            LpReviewStubPayload => Serialize(new LpReviewDraft(5, true, "", "")),
            AltStubPayload p => string.IsNullOrWhiteSpace(p.Hint) || p.Hint.Contains('.')
                ? "お店の雰囲気が伝わる、明るい色合いのイメージ"
                : $"{PostText.Truncate(p.Hint, 40)}を表したイメージ",
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

    /// <summary>LP のタイトル・説明から決定的な企画をつくる（画像は順に割り当てる）。</summary>
    private static LandingPlanDraft LandingPage(LandingPageStubPayload p)
    {
        var product = PostText.Truncate((p.Page.Title.Split('|', '｜', '-')[0]).Trim() is { Length: > 0 } t ? t : p.BrandName, 14);
        var roles = new (string Role, string Caption, string Narration, string[] Points)[]
        {
            ("hook", $"{product}、知ってる？", $"{product}、もう試しましたか？", []),
            ("problem", "こんなお悩みに", "毎日の小さな悩み、ありませんか。", ["時間が足りない", "手間がかかる", "続かない"]),
            ("solution", $"{product}で解決", $"{product}なら、気軽に始められます。", ["すぐに始められる"]),
            ("benefit", "うれしいポイント", "選ばれている理由をチェック。", ["気軽に始められる", "わかりやすい", "続けやすい"]),
            ("offer", "詳しくはLPで", "詳しい内容はページで確認できます。", []),
            ("cta", "プロフィールから", "プロフィールのリンクからどうぞ。", []),
        };
        var count = Math.Clamp(p.SceneCount, 2, roles.Length);
        var picked = roles.Take(count - 1).Append(roles[^1]).ToArray();
        var per = Math.Round((double)p.TargetSeconds / count, 1);
        var images = p.Page.ImageList.Count;
        var scenes = picked.Select((r, i) => new LandingSceneDraft(r.Role, r.Caption, r.Narration, per, images == 0 ? null : i % images, r.Points)).ToArray();
        return new LandingPlanDraft($"{product}の紹介動画", product, "はじめての方", ["気軽に始められる"], "", "プロフィールのリンクから",
            scenes, $"{product}をショート動画で紹介します。詳しくはプロフィールのリンクから。", ["PR", PostText.Normalize(product)],
            "slow push-in camera move, soft natural light, subtle steam");
    }

    private static ScriptDraft Script(ScriptStubPayload p)
    {
        var theme = PostText.Truncate(p.Theme.Trim(), 14);
        var lines = new[]
        {
            ($"{theme}", $"{theme}、もうチェックしましたか？"),
            ("こだわりのポイント", "素材と仕上げにこだわりました。"),
            ("おすすめの楽しみ方", "ゆったりした時間にぴったりです。"),
            ("期間限定です", "期間限定なので、お早めにどうぞ。"),
            ("お店で待っています", $"{p.BrandName}でお待ちしています！"),
            ("詳しくはプロフィールへ", "詳しくはプロフィールのリンクから。"),
        };
        var count = Math.Clamp(p.SceneCount, 1, lines.Length);
        var per = Math.Round((double)p.TargetSeconds / count, 1);
        var picked = lines.Take(count - 1).Append(lines[4]).Take(count);
        return new ScriptDraft($"{theme}のショート動画", [.. picked.Select(l => new SceneDraft(l.Item1, l.Item2, per))]);
    }

    /// <summary>LP のタイトル・説明から決定的に作る、SNS 向けの広告文（3案）と投稿文。</summary>
    private static LpCreativeDraft LpCreativeOf(LpCreativeStubPayload p)
    {
        var limits = AdCopyLimits.For(p.Platform);
        var c = PlatformCatalog.Get(p.Platform);
        var product = PostText.Truncate((p.Page.Title.Split('|', '｜', '-')[0]).Trim() is { Length: > 0 } t ? t : p.BrandName, 20);
        var about = PostText.Truncate(p.Page.Description is { Length: > 0 } d ? d : product, 40);
        string Fit(string text, int max) => max == 0 ? "" : PostText.Truncate(text, max);
        var tags = new[] { p.BrandName.Replace(" ", ""), product.Replace(" ", ""), "おすすめ", "期間限定", "新商品" }
            .Take(Math.Max(0, c.RecommendedHashtags.Max)).ToArray();
        return new LpCreativeDraft(
        [
            new(Fit($"{product}。{about}", limits.PrimaryText), Fit(product, limits.Headline), Fit($"{p.BrandName}のおすすめ", limits.Description), "LEARN_MORE"),
            new(Fit($"気になっていた方へ。{product}をチェックしてみませんか。", limits.PrimaryText), Fit($"{p.BrandName}の{product}", limits.Headline),
                Fit("くわしくはページで", limits.Description), "SHOP_NOW"),
            new(Fit($"毎日をちょっと楽しく。{p.BrandName}の{product}。", limits.PrimaryText), Fit("いまチェック", limits.Headline), Fit(about, limits.Description), "LEARN_MORE"),
        ], PostText.Truncate($"{product}のご紹介です。{about}", Math.Min(c.MaxBodyLength, 200)), tags);
    }

    /// <summary>ページの文章から決定的に作るブランドの下書き（FAQ は「Q／A」形式の行だけを拾う）。</summary>
    private static BrandDraftOutput Brand(BrandAnalysisInput input)
    {
        var text = $"{input.Page?.Title}\n{input.Page?.Description}\n{input.Page?.Text}\n{input.ExtraText}\n{string.Join('\n', input.PastPosts)}";
        var name = (input.Page?.Title ?? "").Split(['|', '｜', '-', '–', '　'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        var industry = text.Contains("カフェ") || text.Contains("コーヒー") ? "カフェ" : text.Contains("美容") ? "美容室"
            : text.Contains("化粧品") || text.Contains("コスメ") ? "化粧品" : "小売";
        var exclaims = text.Count(c => c is '！' or '!');
        var casual = Math.Clamp(30 + exclaims * 5, 0, 90);
        var emoji = text.Count(char.IsSurrogate) / 2 > 3 ? 2 : 1;
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var faqs = new List<FaqDraft>();
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (lines[i].StartsWith("Q", StringComparison.OrdinalIgnoreCase) && lines[i + 1].StartsWith("A", StringComparison.OrdinalIgnoreCase))
            {
                faqs.Add(new FaqDraft(lines[i].TrimStart('Q', 'q', '.', '．', ':', '：', ' '), lines[i + 1].TrimStart('A', 'a', '.', '．', ':', '：', ' ')));
            }
        }
        var appeal = lines.Where(l => l.Length is >= 8 and <= 40).Take(3).ToArray();
        return new BrandDraftOutput(name, industry, casual, industry == "カフェ" ? "私たち" : "当店", emoji, null,
            [new PersonaDraft("近所で働く人", "25〜39歳", $"{industry}・ひと息つける時間", "忙しくてゆっくりできない")],
            appeal, [.. BuildTags(industry, name.Length is > 0 and <= 12 ? name : null, [])], industry == "化粧品" ? ["シワが消える", "必ず痩せる"] : ["No.1"],
            [.. faqs.Take(5)]);
    }

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

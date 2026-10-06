using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ReachForge.AI.Prompts;
using ReachForge.AI.Routing;
using ReachForge.Application.Ai;
using ReachForge.Domain.Analytics;
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
            ReportStubPayload p => Serialize(Report(p.Input)),
            ClassifyStubPayload p => Serialize(Classify(p.Text)),
            AbStubPayload p => Serialize(new AbVariantDraft(AbVariant(p.Body, p.Variable))),
            BrandStubPayload p => Serialize(Brand(p.Input)),
            TrendStubPayload p => Serialize(Ideas(p)),
            ReplyStubPayload p => Serialize(Replies(p.Request)),
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

    /// <summary>根拠データの値をそのまま引用する決定的なレポート（検証を通る形）。</summary>
    private static InsightDraft Report(ReportWriterInput input)
    {
        ReportFact? Find(string label) => input.Facts.FirstOrDefault(f => f.Label == label);
        ClaimDraft Claim(string text, params ReportFact?[] facts) =>
            new(text, facts.Where(f => f is not null).Select(f => f!.Id).ToArray());

        var imp = Find("表示回数");
        var impChange = Find("表示回数（比較期間比）");
        var er = Find("反応の割合");
        var top = input.Facts.FirstOrDefault(f => f.Label.StartsWith("上位1位", StringComparison.Ordinal));
        var best = Find("反応が多い曜日・時刻");
        var ai = Find("AI生成投稿の反応の割合");
        var manual = Find("手動投稿の反応の割合");
        var topPost = input.TopPosts.FirstOrDefault();
        var bottom = input.BottomPosts.FirstOrDefault();

        var summary = new List<ClaimDraft>
        {
            Claim($"表示回数は{imp?.Value ?? "—"}回{(impChange is null ? "" : $"（比較期間比{impChange.Value}）")}でした。", imp, impChange),
            Claim($"反応の割合は{er?.Value ?? "—"}です。", er),
        };
        if (top is not null) summary.Add(Claim($"{top.Label.Split('の')[0]}の投稿が最も反応を集めました（{top.Value}）。", top));
        var good = new List<ClaimDraft>();
        if (topPost is not null && top is not null)
        {
            good.Add(Claim($"{topPost.Platform}の{(topPost.HasImage ? "画像付き" : "文章中心の")}投稿が好調でした（{top.Value}）。", top));
        }
        if (ai is not null && manual is not null)
        {
            good.Add(Claim($"AI生成の投稿は反応の割合{ai.Value}、手動の投稿は{manual.Value}でした。", ai, manual));
        }
        var issues = new List<ClaimDraft>();
        if (bottom is not null)
        {
            issues.Add(Claim($"{bottom.Platform}の「{bottom.Title}」は反応が少なめでした。投稿の時間帯が合っていない可能性があります。",
                input.Facts.FirstOrDefault(f => f.Label.StartsWith("最下位", StringComparison.Ordinal)) ?? er));
        }
        var actions = new List<ClaimDraft>
        {
            Claim(best is null ? "反応が多い時間帯に合わせて予約しましょう。" : $"{best.Value.Split('（')[0]}に合わせて予約しましょう。", best ?? er),
            Claim("反応が良かった投稿の切り口で、画像付きの投稿を1本つくりましょう。", top ?? er),
            Claim("反応が少なかった投稿は、冒頭の一文を短くして再投稿を試しましょう。", er),
        };
        return new InsightDraft([.. summary], [.. good], [.. issues], [.. actions]);
    }

    /// <summary>業種の言葉を含む話題ほど関連度を高くする決定的な採点。</summary>
    private static IdeaBatch Ideas(TrendStubPayload p)
    {
        var industry = p.Brand.Profile.Industry;
        var food = industry.Contains("カフェ") || industry.Contains("飲食");
        string[] foodWords = ["コーヒー", "さつまいも", "ハロウィン", "クリスマス", "バレンタイン", "お月見", "七夕", "ポッキー", "冬至"];
        return new IdeaBatch([.. p.Candidates.Select(c =>
        {
            var hit = food && foodWords.Any(w => c.Topic.Contains(w));
            var relevance = Math.Round((hit ? 0.8 : 0.45) + (c.Topic.Length % 5) / 50.0, 2);
            var name = p.Brand.Profile.BrandName;
            return new IdeaDraft(c.Topic, relevance, hit ? "カルーセル" : "画像1枚",
                [$"{c.Topic}限定の楽しみ方を紹介", $"スタッフの{c.Topic}エピソード", $"{name}で{c.Topic}を過ごす提案"],
                hit ? $"{industry}と相性がよく、お客様の関心が高い話題です" : "季節感を伝えられる話題です",
                Domain.Engagement.SensitiveTopicFilter.IsSensitive(c.Topic), hit ? 5 : 2);
        })]);
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

    /// <summary>書き出しを問いかけに、または最後の行を具体的な呼びかけに変える。</summary>
    private static string AbVariant(string body, Domain.Entities.AbVariable variable)
    {
        var lines = body.Split('\n').ToList();
        if (variable == Domain.Entities.AbVariable.Hook)
        {
            lines[0] = "知っていましたか？" + lines[0];
            return string.Join('\n', lines);
        }
        lines[^1] = "今週末までに、ぜひお店で試してみてください！";
        return string.Join('\n', lines);
    }

    private static ClassificationDraft Classify(string text)
    {
        var l = Domain.Engagement.InboxHeuristics.Classify(text);
        return new ClassificationDraft(l.Sentiment.ToString(), l.Intent.ToString(), l.Urgency.ToString(), l.Sensitive.ToString(), l.Language);
    }

    /// <summary>参考情報（FAQ）の回答を使った決定的な返信案。</summary>
    private static ReplyBatch Replies(ReplyRequest r)
    {
        var polite = r.Brand.Profile.Tone.Casualness < 50;
        var thanks = polite ? "お問い合わせありがとうございます。" : "コメントありがとうございます！";
        var replies = new List<ReplyDraftItem>();
        if (r.Knowledge.FirstOrDefault() is { } hit)
        {
            replies.Add(new ReplyDraftItem($"{thanks}{hit.Entry.Answer}", ["K1"]));
            replies.Add(new ReplyDraftItem($"{hit.Entry.Answer}{(polite ? "お待ちしております。" : "お待ちしています☕")}", ["K1"]));
        }
        replies.Add(new ReplyDraftItem(polite
            ? $"{thanks}確認のうえ、あらためてご連絡いたします。"
            : $"{thanks}確認してお返事しますね。", []));
        if (replies.Count < 3) replies.Add(new ReplyDraftItem(polite ? "ご来店を心よりお待ちしております。" : "またお店でお会いできるのを楽しみにしています！", []));
        return new ReplyBatch([.. replies.Take(3)]);
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

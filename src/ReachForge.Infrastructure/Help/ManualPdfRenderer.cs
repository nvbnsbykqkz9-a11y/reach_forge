using Microsoft.Extensions.Options;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;
using ReachForge.Infrastructure.Media;

namespace ReachForge.Infrastructure.Help;

/// <summary>
/// 操作説明書（PDF 版）。画面の操作を初心者向けに順番に説明する。内容はコードで持ち、画面の変更と一緒に更新する。
/// 日本語のフォントは OS のもの（Noto Sans JP・IPA・Yu Gothic・Meiryo）か、Media:FontPath に置いたものを使う。
/// </summary>
public sealed class ManualPdfRenderer
{
    private static readonly string[] s_fonts = ["Noto Sans JP", "Noto Sans CJK JP", "IPAexGothic", "IPAGothic", "Yu Gothic", "Meiryo"];
    private const string Ink = "#1F2937";
    private const string Muted = "#6B7280";
    private const string Primary = "#0A5BD6";
    private const string Soft = "#EEF4FF";

    private readonly Lazy<byte[]> _pdf;

    public ManualPdfRenderer(IOptions<MediaOptions> media)
    {
        // Community License：年商100万USD未満の営利企業・非営利団体・OSS は無償（第三者ソフトウェアのライセンスを参照）
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.UseSystemFonts = true;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
        if (media.Value.FontPath is { Length: > 0 } path)
        {
            if (Directory.Exists(path)) FontManager.RegisterFontsFromDirectory(path);
            else if (File.Exists(path)) FontManager.RegisterFontFromFile(path);
        }
        _pdf = new Lazy<byte[]>(Render);
    }

    /// <summary>PDF のバイト列（内容はアプリの版ごとに同じなので、1度だけつくる）。</summary>
    public byte[] Pdf => _pdf.Value;

    public const string FileName = "ReachForge_操作説明書.pdf";

    private static byte[] Render() => Document.Create(doc =>
    {
        doc.Page(page =>
        {
            Setup(page);
            page.Content().Column(col =>
            {
                col.Spacing(12);
                col.Item().PaddingTop(120).AlignCenter().Width(110).Image(AppInfo.Icon());
                col.Item().PaddingTop(24).AlignCenter().Text(AppInfo.Name).FontSize(32).Bold().FontColor(Primary);
                col.Item().AlignCenter().Text("操作説明書").FontSize(22).Bold();
                col.Item().AlignCenter().Text(AppInfo.Subtitle).FontSize(12).FontColor(Muted);
                col.Item().PaddingTop(160).AlignCenter().Text(AppInfo.DisplayVersion).FontColor(Muted);
                col.Item().AlignCenter().Text(AppInfo.Developer).FontColor(Muted);
            });
        });

        doc.Page(page =>
        {
            Setup(page);
            Footer(page);
            page.Content().Column(col =>
            {
                col.Spacing(6);
                Chapter(col, "もくじ");
                foreach (var (title, i) in Chapters.Select((c, i) => (c, i + 1))) Para(col, $"{i}. {title}");

                Chapter(col, $"1. {Chapters[0]}");
                Para(col, "ReachForge は、お店・商品のページ（LP：ランディングページ）の URL を入れるだけで、SNS ごとの広告文・投稿文・画像と、縦型の動画をまとめてつくるアプリです。");
                Bullets(col,
                [
                    "Instagram・X・Facebook・Threads・TikTok・YouTube・LINE から、つくる SNS を選べます（いくつでも）。",
                    "SNS ごとに「広告文の案3つ」「投稿文とハッシュタグ」「その SNS のサイズの画像」をつくります。",
                    "縦型（1080×1920）の動画を1本つくります。リール・ショート動画などに使えます。",
                    "できたものはダウンロード・コピーして、各 SNS のアプリや広告マネージャーから、ご自身でアップロードします（ReachForge は SNS に投稿しません）。",
                ]);
                Note(col, "AI がつくった文章は、公開・出稿の前に必ずご自身で内容を確認してください。価格・効果などは LP に書かれたものだけを使うようにしていますが、間違いがないとは限りません。");

                Chapter(col, $"2. {Chapters[1]}");
                Bullets(col,
                [
                    "Windows 10（1809 以降）または Windows 11、64 ビット。",
                    "インターネット接続（LP の読み込みと生成AIの利用に使います）。",
                    "インストーラー（ReachForge-Setup）を実行し、画面の案内に従ってインストールします。WebView2 ランタイムがない場合はインストーラーが案内します。",
                    "はじめて起動すると登録画面が開きます。会社名・お名前・メールアドレス・パスワードを入れてワークスペースをつくります。2回目からは自動でログインします。",
                    "ウィンドウを閉じても、つくっている途中の動画を仕上げるためにタスクトレイ（画面右下）に残ります。終了するときは、トレイのアイコンを右クリックして「終了」を選びます。",
                ]);

                Chapter(col, $"3. {Chapters[2]}");
                Table(col, ["場所", "内容"],
                [
                    ["左のメニュー「ホーム」", "LP から広告・動画をつくる画面です。最近つくったものも表示します。"],
                    ["左のメニュー「つくったもの」", "これまでにつくったものの一覧です。選ぶと、文章・画像・動画を見たりダウンロードしたりできます。"],
                    ["左のメニュー「設定」", "ブランド（話し方・NG ワード・商品）、プラン・利用量、生成AIの設定を開きます。"],
                    ["右上の「つくる」", "いつでもホーム（つくる画面）へ戻れます。"],
                    ["右上の数字", "使えるクレジットの残りです。"],
                    ["右上のお名前", "2段階認証の設定、表示の明るさの切り替え、操作説明書（この PDF）、About（バージョン・開発元・お問い合わせ先・使用許諾契約・ライセンス）、ログアウトがあります。"],
                ]);

                Chapter(col, $"4. {Chapters[3]}");
                Step(col, "生成AIの API キーを入れる", "「設定」→「生成AIの設定」で、Claude（Anthropic）の API キーを入れて「保存する」を押します。「接続を確認」でキーが使えるか確かめられます（利用料はかかりません）。ナレーション（AI の声）を使う場合は OpenAI のキーも入れます。キーがない間は、お試しの決まった文章でつくります。");
                Step(col, "ブランドを設定する", "「設定」→「ブランド」で、話し方（ていねい／くだけた）、一人称、絵文字の量、使わない言葉（NG ワード）、必ず入れる表記、お客様像、よく使うハッシュタグ、商品と価格を入れます。AI はこれに沿って文章をつくります。「AIでブランド診断」を使うと、ホームページの URL から下書きをつくれます。");

                Chapter(col, $"5. {Chapters[4]}");
                Para(col, "ホーム画面で、次の3つの手順で進めます。いまどの手順かは、画面上の番号で分かります。");
                Step(col, "① LP を読み込む", "「LP の URL」に https:// から始まるページのアドレスを入れて「読み込む」を押します。ページのタイトル・説明と、画像の候補が表示されます。広告・動画に使う画像を選び（4枚まで）、「選んだ画像を広告・動画に使う権利があります」にチェックを入れて「次へ」を押します。");
                Step(col, "② SNS を選ぶ", "つくる SNS のボタンを押して選びます（もう一度押すと外れます）。「縦型の動画もつくる」をオンにすると、動画の長さ（15・20・30秒）と、ナレーションの有無を選べます。");
                Step(col, "③ 確認してつくる", "内容と使うクレジットを確認して「つくる」を押します。文章と画像は1分ほどでできあがり、自動で「つくったもの」の画面に移ります。動画はそのあと数分かけてつくり、できあがると画面が切り替わります（画面を閉じても続きます）。");
                Note(col, "LP の画像は、自社の LP の画像など、広告・動画に使う権利があるものだけを選んでください。ほかの人がつくった写真・イラスト・人物の写真を無断で使うことはできません。");

                Chapter(col, $"6. {Chapters[5]}");
                Table(col, ["場所", "使い方"],
                [
                    ["縦型の動画", "再生して確認できます。「動画をダウンロード（MP4）」で保存し、Instagram のリール・TikTok・YouTube ショートなどにアップロードします。「動画に添える文章の例」もコピーできます。"],
                    ["SNS ごとのタブ", "上のタブで SNS を切り替えます。"],
                    ["投稿文", "その SNS にふつうに投稿するときの文章とハッシュタグです。「投稿文をコピー」で貼り付けられます。"],
                    ["画像", "その SNS のサイズにした画像です。画像の下の「ダウンロード」で保存します。"],
                    ["広告文の案（3つ）", "広告マネージャーの入力欄（見出し・本文・説明・ボタン）に合わせた案です。「案1をコピー」などでまとめてコピーできます。"],
                    ["アップロードするところ", "その SNS の投稿画面と、広告マネージャーを開くボタンです。"],
                    ["まとめてダウンロード（ZIP）", "動画と、SNS ごとのフォルダー（画像・文章のテキストファイル）を1つのファイルにまとめて保存します。"],
                    ["削除", "一覧から消します（ダウンロードしたファイルは消えません）。"],
                ]);

                Chapter(col, $"7. {Chapters[6]}");
                Para(col, "ReachForge は SNS とつながらないため、アップロードはご自身で行います。下の表は目安です（各社の画面は変わることがあります）。");
                Table(col, ["SNS", "画像のサイズ", "ふつうの投稿", "広告を出すところ"],
                    [.. LpPlatforms.Select(p => new[] { PlatformCatalog.Get(p).DisplayName, Size(p), PostHow(p), AdsWhere(p) })]);
                Bullets(col,
                [
                    "広告文は、広告マネージャーの「見出し」「メインテキスト（本文）」「説明」「ボタン（CTA）」の欄にそれぞれ貼り付けます。",
                    "リンク先には、読み込んだ LP の URL を入れます（文章には URL を入れていません）。",
                    "広告は各社の審査があります。承認されなかった場合は、理由を確認して文章や画像を直してください。",
                ]);

                Chapter(col, $"8. {Chapters[7]}");
                Para(col, "AI を使う作業ではクレジットを使います。残りは右上に表示され、毎月1日に付与されます。失敗したときはクレジットを使いません。");
                Table(col, ["作業", "クレジットの目安"],
                [
                    ["SNS ごとの文章（広告文3案・投稿文・ハッシュタグ）", "1つの SNS につき 3"],
                    ["縦型の動画（画像＋テロップ）", "20"],
                    ["ナレーション（AI の声）", "30秒ごとに 2"],
                    ["AI でブランド診断", "3"],
                ]);
                Para(col, "「設定」→「プラン・利用量」で、使った量の内訳と、なくなる見込みの日を確認できます。");

                Chapter(col, $"9. {Chapters[8]}");
                Qa(col, "LP を読み込めません", "URL が https:// から始まっているか、ブラウザで開けるかを確認してください。社内ネットワーク内のページや、ログインが必要なページ、ロボットによる読み取りを禁止しているページは読み込めません。");
                Qa(col, "LP の画像が表示されません", "サイトによっては、ほかのアプリからの画像の表示を禁止しています。その場合でも文章はつくれます。動画は文字だけでつくります。");
                Qa(col, "文章がいつも同じです", "生成AIの API キーが入っていないと、お試しの決まった文章になります。「設定」→「生成AIの設定」でキーを入れてください。");
                Qa(col, "動画ができません", "「つくったもの」の画面にエラーの内容が表示されます。しばらくしてからもう一度つくってください（失敗した分のクレジットは使われません）。");
                Qa(col, "クレジットが足りません", "「設定」→「プラン・利用量」で残りを確認し、翌月の付与を待つか、お問い合わせください。");
                Qa(col, "アプリが起動しません", "ほかの ReachForge が起動していないか、タスクトレイを確認してください。起動できない理由は画面に表示されます。「ログを開く」で記録を確認できます。");

                Chapter(col, $"10. {Chapters[9]}");
                Table(col, ["項目", "内容"],
                [
                    ["アプリケーション", $"{AppInfo.Name}　{AppInfo.DisplayVersion}"],
                    ["開発元", AppInfo.Developer],
                    ["ホームページ", AppInfo.DeveloperUrl],
                    ["お問い合わせ先", AppInfo.ContactEmail],
                ]);
                Para(col, "使用許諾契約と第三者ソフトウェアのライセンスは、画面右上のお名前 →「About」から確認できます。");
            });
        });
    }).GeneratePdf();

    private static readonly string[] Chapters =
    [
        "ReachForge でできること", "動作環境とインストール（Windows 版）", "画面の見かた", "はじめにする設定",
        "LP から広告・動画をつくる", "つくったものを使う", "各 SNS へのアップロード", "クレジット", "困ったとき", "お問い合わせ・バージョン",
    ];

    private static readonly SocialPlatform[] LpPlatforms =
    [
        SocialPlatform.Instagram, SocialPlatform.X, SocialPlatform.Facebook, SocialPlatform.Threads,
        SocialPlatform.TikTok, SocialPlatform.YouTube, SocialPlatform.Line,
    ];

    private static string Size(SocialPlatform p)
    {
        var c = PlatformCatalog.Get(p);
        return c.VideoOnly ? "動画のみ" : $"{c.ImageSize.Width}×{c.ImageSize.Height}";
    }

    private static string PostHow(SocialPlatform p) => p switch
    {
        SocialPlatform.Instagram => "アプリの「＋」から投稿・リール",
        SocialPlatform.X => "x.com の「ポストする」",
        SocialPlatform.Facebook => "ページの「投稿を作成」",
        SocialPlatform.Threads => "アプリの「新規スレッド」",
        SocialPlatform.TikTok => "tiktok.com/upload",
        SocialPlatform.YouTube => "YouTube Studio の「作成」",
        SocialPlatform.Line => "LINE Official Account Manager",
        _ => "",
    };

    private static string AdsWhere(SocialPlatform p) => p switch
    {
        SocialPlatform.Instagram or SocialPlatform.Facebook or SocialPlatform.Threads => "Meta 広告マネージャ",
        SocialPlatform.X => "X 広告（ads.x.com）",
        SocialPlatform.TikTok => "TikTok 広告マネージャー",
        SocialPlatform.YouTube => "Google 広告",
        SocialPlatform.Line => "LINE 広告",
        _ => "",
    };

    private static void Setup(PageDescriptor page)
    {
        page.Size(PageSizes.A4);
        page.Margin(40);
        page.DefaultTextStyle(t => t.FontFamily(s_fonts).FontSize(10.5f).FontColor(Ink).LineHeight(1.5f));
    }

    private static void Footer(PageDescriptor page) =>
        page.Footer().Row(row =>
        {
            row.RelativeItem().Text($"{AppInfo.Name} 操作説明書　{AppInfo.DisplayVersion}").FontSize(8).FontColor(Muted);
            row.ConstantItem(60).AlignRight().Text(t =>
            {
                t.CurrentPageNumber().FontSize(8).FontColor(Muted);
                t.Span(" / ").FontSize(8).FontColor(Muted);
                t.TotalPages().FontSize(8).FontColor(Muted);
            });
        });

    private static void Chapter(ColumnDescriptor col, string title) =>
        col.Item().EnsureSpace(140).PaddingTop(14).BorderBottom(1.5f).BorderColor(Primary).PaddingBottom(4).Text(title).FontSize(15).Bold().FontColor(Primary);

    private static void Para(ColumnDescriptor col, string text) => col.Item().Text(text);

    private static void Bullets(ColumnDescriptor col, IEnumerable<string> items)
    {
        foreach (var item in items)
        {
            col.Item().Row(row =>
            {
                row.ConstantItem(14).Text("・");
                row.RelativeItem().Text(item);
            });
        }
    }

    private static void Step(ColumnDescriptor col, string title, string text) =>
        col.Item().Background(Soft).Padding(8).Column(c =>
        {
            c.Item().Text(title).Bold();
            c.Item().Text(text);
        });

    private static void Note(ColumnDescriptor col, string text) =>
        col.Item().BorderLeft(3).BorderColor("#F59E0B").PaddingLeft(8).Text(t =>
        {
            t.Span("ご注意　").Bold();
            t.Span(text);
        });

    private static void Qa(ColumnDescriptor col, string q, string a) =>
        col.Item().PaddingBottom(2).Column(c =>
        {
            c.Item().Text($"Q. {q}").Bold();
            c.Item().PaddingLeft(14).Text($"A. {a}");
        });

    private static void Table(ColumnDescriptor col, string[] header, IReadOnlyList<string[]> rows) =>
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                for (var i = 0; i < header.Length; i++)
                {
                    if (i == 0) c.RelativeColumn(header.Length > 2 ? 1.1f : 1.4f);
                    else c.RelativeColumn(header.Length > 2 ? 1.6f : 3);
                }
            });
            table.Header(h =>
            {
                foreach (var cell in header) h.Cell().Background(Primary).Padding(5).Text(cell).FontColor(Colors.White).Bold();
            });
            foreach (var row in rows)
            {
                foreach (var cell in row) table.Cell().BorderBottom(0.5f).BorderColor("#D1D5DB").Padding(5).Text(cell);
            }
        });
}

namespace ReachForge.Infrastructure.Help;

/// <summary>条文（見出しと本文の段落）。</summary>
public sealed record LegalArticle(string Title, IReadOnlyList<string> Paragraphs);

/// <summary>ReachForge に含まれる第三者のソフトウェア・素材と、そのライセンス。</summary>
public sealed record ThirdPartyComponent(string Name, string Holder, string License, string Url, string? Note = null);

/// <summary>使用許諾契約と第三者ソフトウェアのライセンス（About ダイアログ・操作説明書に表示する）。</summary>
public static class LegalTexts
{
    public const string EulaTitle = "ReachForge 使用許諾契約";
    public const string EulaEffective = "2026年10月1日 制定";

    public const string EulaPreamble =
        "この使用許諾契約（以下「本契約」）は、" + AppInfo.Developer + "（以下「当社」）が提供するソフトウェア「" + AppInfo.Name +
        "」（関連する文書・更新版を含み、以下「本ソフトウェア」）の使用について、本ソフトウェアを使用するお客様（以下「お客様」）と当社との間で適用されます。" +
        "お客様は、本ソフトウェアをインストールし、または使用した時点で、本契約に同意したものとみなされます。";

    public static readonly IReadOnlyList<LegalArticle> Eula =
    [
        new("第1条（使用許諾）",
        [
            "当社は、お客様が本契約に従うことを条件に、本ソフトウェアを、お客様の事業のために、お客様が管理するコンピューターにインストールして使用する、譲渡できず、再許諾できない、非独占的な権利を許諾します。",
            "本ソフトウェアの使用にあたり当社との間で別途の契約（ライセンス数・期間・料金等）を締結した場合は、その契約が本契約に優先します。",
        ]),
        new("第2条（禁止事項）",
        [
            "お客様は、次の行為をしてはなりません。(1) 本ソフトウェアの全部または一部の複製（バックアップを目的とする場合を除く）、改変、翻案。(2) 法令で認められる場合を除く、逆コンパイル、逆アセンブル、リバースエンジニアリング。(3) 本ソフトウェアの第三者への販売、貸与、譲渡、再許諾、公衆送信。(4) 本ソフトウェアに表示された著作権表示その他の権利表示の削除・変更。(5) 法令、公序良俗または第三者の権利を侵害する目的での使用。(6) 虚偽・誇大な広告、なりすまし、迷惑行為その他各 SNS の規約に反する内容の作成のための使用。",
        ]),
        new("第3条（生成AIの利用）",
        [
            "本ソフトウェアは、お客様が設定した第三者の生成AIサービス（Anthropic、OpenAI、Google 等。以下「AIサービス」）を利用して文章・音声・画像・動画を作成します。AIサービスの利用には、お客様と各提供者との契約（利用規約・料金を含む）が別途適用され、その利用料はお客様が負担します。",
            "本ソフトウェアは、作成のために必要な範囲で、お客様が入力した情報（ランディングページの内容、ブランド情報等）を AIサービスへ送信します。お客様は、送信が許される情報だけを入力するものとします。",
            "AIサービスの出力は、不正確・不適切な内容を含むことがあります。本ソフトウェアは規制表現等の確認を補助しますが、その結果を保証するものではありません。お客様は、出力物を公開・出稿する前に、内容の正確性、法令（景品表示法、医薬品医療機器等法等）および各 SNS の規約への適合性を自らの責任で確認するものとします。",
        ]),
        new("第4条（お客様が使用する素材と出力物の権利）",
        [
            "お客様は、本ソフトウェアで使用するランディングページ、画像その他の素材について、広告・動画等に使用するために必要な権利を有していることを保証します。",
            "本ソフトウェアで作成した文章・画像・動画（以下「出力物」）の権利は、法令およびAIサービスの規約が許す範囲でお客様に帰属し、当社は出力物について権利を主張しません。出力物の使用はお客様の責任で行うものとします。",
        ]),
        new("第5条（知的財産権）",
        [
            "本ソフトウェアに関する著作権その他の知的財産権は、当社または正当な権利者に帰属します。本契約は、本契約に明示する使用の権利以外の権利をお客様に移転するものではありません。",
            "本ソフトウェアには第三者のソフトウェアが含まれます。これらには、それぞれのライセンス条件（「第三者ソフトウェアのライセンス」に表示します）が本契約に優先して適用されます。",
        ]),
        new("第6条（データの取扱い）",
        [
            "Windows 版の本ソフトウェアは、お客様が入力・作成したデータをお客様のコンピューターに保存し、当社のサーバーへ送信しません（第3条の AIサービスへの送信を除きます）。API キー等の秘密情報は暗号化して保存します。データのバックアップはお客様の責任で行うものとします。",
        ]),
        new("第7条（保証の否認）",
        [
            "当社は、本ソフトウェアを現状有姿で提供し、本ソフトウェアに不具合がないこと、特定の目的に適合すること、出力物により特定の成果（集客・売上等）が得られること、ならびに AIサービス・各 SNS の仕様変更や停止の影響を受けないことを保証しません。",
        ]),
        new("第8条（責任の制限）",
        [
            "当社は、本ソフトウェアの使用または使用できないことによってお客様に生じた損害について、当社の故意または重大な過失による場合を除き、責任を負いません。当社が責任を負う場合でも、その範囲は通常かつ直接の損害に限り、お客様が本ソフトウェアの対価として当社に支払った金額（直近12か月分）を上限とします。",
            "前項の規定は、消費者契約法その他の法令により当社の責任の制限が認められない場合には、その限りで適用されません。",
        ]),
        new("第9条（サポートと更新）",
        [
            "当社は、当社の定める範囲で、本ソフトウェアの問い合わせ対応および更新版の提供を行います。更新版にも本契約（更新版に付属する改定後の契約がある場合はその契約）が適用されます。",
        ]),
        new("第10条（契約の終了）",
        [
            "お客様が本契約に違反した場合、当社は通知により本契約を終了できます。本契約が終了したときは、お客様は本ソフトウェアの使用を中止し、本ソフトウェアおよびその複製物を削除するものとします。第4条、第5条、第7条、第8条および第12条は、本契約の終了後も効力を有します。",
        ]),
        new("第11条（輸出管理）",
        [
            "お客様は、本ソフトウェアを輸出または提供する場合、外国為替及び外国貿易法その他の輸出関連法令を遵守するものとします。",
        ]),
        new("第12条（準拠法・管轄）",
        [
            "本契約は日本法に準拠し、本契約に関する紛争については、東京地方裁判所を第一審の専属的合意管轄裁判所とします。",
        ]),
        new("お問い合わせ先",
        [
            AppInfo.Developer + "　" + AppInfo.DeveloperUrl + "　" + AppInfo.ContactEmail,
        ]),
    ];

    /// <summary>本ソフトウェアに含まれる、または本ソフトウェアが利用する第三者のソフトウェア・素材。</summary>
    public static readonly IReadOnlyList<ThirdPartyComponent> ThirdParty =
    [
        new(".NET（ASP.NET Core・Entity Framework Core・Microsoft.Extensions 各種・Microsoft.Extensions.AI を含む）", "Microsoft Corporation and .NET Foundation", "MIT License", "https://github.com/dotnet/runtime"),
        new("Microsoft.Data.Sqlite / SQLitePCLRaw", "Microsoft Corporation / Eric Sink", "MIT License / Apache License 2.0", "https://github.com/dotnet/efcore"),
        new("SQLite", "SQLite Consortium", "Public Domain", "https://www.sqlite.org/copyright.html"),
        new("MudBlazor", "MudBlazor contributors", "MIT License", "https://github.com/MudBlazor/MudBlazor"),
        new("Material Design Icons（MudBlazor が同梱）", "Google LLC", "Apache License 2.0", "https://github.com/google/material-design-icons"),
        new("SixLabors.ImageSharp / ImageSharp.Drawing", "Six Labors", "Six Labors Split License 1.0", "https://sixlabors.com/pricing/",
            "年間売上100万米ドル以上の営利企業による利用には商用ライセンスが必要です。"),
        new("QuestPDF", "QuestPDF contributors", "QuestPDF Community License", "https://www.questpdf.com/license/",
            "年間売上100万米ドル以上の営利企業による利用には商用ライセンスが必要です。"),
        new("Hangfire / Hangfire.InMemory", "Hangfire OÜ", "GNU Lesser General Public License v3.0", "https://github.com/HangfireIO/Hangfire",
            "改変せずにライブラリとして使用しています。ライセンス全文とソースコードは左記から入手できます。"),
        new("Scriban", "Alexandre Mutel", "BSD 2-Clause License", "https://github.com/scriban/scriban"),
        new("Cronos", "Hangfire OÜ", "MIT License", "https://github.com/HangfireIO/Cronos"),
        new("Newtonsoft.Json", "James Newton-King", "MIT License", "https://github.com/JamesNK/Newtonsoft.Json"),
        new("Anthropic C# SDK", "Anthropic, PBC", "MIT License", "https://github.com/anthropics/anthropic-sdk-csharp"),
        new("OpenAI .NET", "OpenAI / Microsoft Corporation", "MIT License", "https://github.com/openai/openai-dotnet"),
        new("Polly（Microsoft.Extensions.Http.Resilience が使用）", "App vNext", "BSD 3-Clause License", "https://github.com/App-vNext/Polly"),
        new("OpenTelemetry .NET", "OpenTelemetry Authors", "Apache License 2.0", "https://github.com/open-telemetry/opentelemetry-dotnet"),
        new("Azure SDK for .NET（Identity・Storage・Key Vault・Service Bus）", "Microsoft Corporation", "MIT License", "https://github.com/Azure/azure-sdk-for-net"),
        new("StackExchange.Redis", "Stack Exchange, Inc.", "MIT License", "https://github.com/StackExchange/StackExchange.Redis"),
        new("Microsoft Edge WebView2（Windows 版）", "Microsoft Corporation", "Microsoft WebView2 ライセンス", "https://aka.ms/webviewnugetlicense",
            "WebView2 ランタイムは Microsoft のソフトウェアライセンス条項に従います。"),
        new("FFmpeg（動画の作成に使用）", "FFmpeg developers", "GNU LGPL v2.1 以降（ビルドにより GPL）", "https://ffmpeg.org/legal.html",
            "別のプログラムとして呼び出して使用しています。同梱する場合は、そのビルドのライセンスとソースコードの入手方法に従います。"),
        new("Noto Sans JP（画面のフォント）", "Google LLC", "SIL Open Font License 1.1", "https://fonts.google.com/noto/specimen/Noto+Sans+JP"),
    ];
}

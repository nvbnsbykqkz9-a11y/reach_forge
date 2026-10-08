# ReachForge（LP から SNS の広告・動画をつくる）

C#（.NET 10 LTS）と生成AIで、**お店・商品のページ（LP）の URL から、SNS ごとの広告文・投稿文と、各 SNS の形式に合わせた広告の画像・動画（縦型・横型）をまとめてつくる**アプリケーションです。
SNS とはつながず、できたものはダウンロード・コピーして、利用者が各 SNS のアプリや広告マネージャーから**手動でアップロード**します。初心者が迷わないよう、手順を番号で示し、1画面ではやることを1つにしています。

- 基本設計書・機能設計書：RF-DES-001
- UI/UX仕様書：RF-UX-001

## できること

1. **LP を読み込む**（ホーム）：URL を入れると、タイトル・説明・画像の候補を読み取る。AI（画像理解）が商品・サービスの特色が伝わる画像を選んで最初から選んでおく（ロゴ・アイコン・スクリーンショット・細長いバナーは除く）。選び直し（4枚まで）、使う権利があることを確認する
2. **SNS を選ぶ**：Instagram・X・Facebook・Threads・TikTok・YouTube・LINE から、いくつでも。SNS ごとにつくる画像・動画の形式が表に出る。動画をつくるか（長さ・ナレーション）も選ぶ
3. **確認してつくる**：内容を確認して「つくる」

つくったもの（`/lp/{id}`、一覧は `/lp`）：

| SNS ごと | 内容 |
|---|---|
| 投稿文・ハッシュタグ | その SNS の文字数・ハッシュタグの数・書き方に合わせた文章（コピーできる） |
| 広告文の案（3つ） | 各社の広告マネージャーの入力欄に合わせた見出し・本文・説明・ボタン。文字数の上限に収める（X 280 字、TikTok 100 字など） |
| 画像 | AI の広告写真を、その SNS の形式ちょうどに切り出して見出しを入れたもの（下の表。素材の画像ごとに1枚ずつ） |
| 動画 | その SNS で使う向きの動画（縦型 9:16・横型 16:9） |

| 共通 | 内容 |
|---|---|
| 広告の写真（キービジュアル） | 素材の画像ごとに AI がビジュアル案（切り口・見出し・画像と動画への指示）をつくり、画像生成 AI が素材の商品をそのまま使った広告写真を向き（正方形・縦長・横長）ごとにつくる |
| 動画 | 縦型（1080×1920）と横型（1920×1080）のうち必要なものを、AI の写真を動画生成 AI で動かしたシーン＋テロップ＋AI ナレーション＋BGM でつくる |
| 進み具合 | 画像と動画はジョブとして裏でつくり、いまの作業と割合を画面に出す（数分〜十数分） |
| まとめてダウンロード | 動画・SNS ごとの画像・文章（テキストファイル）を1つの ZIP に。1つずつのダウンロード・コピーもできる |
| アップロードの案内 | SNS ごとに、投稿する画面と広告マネージャー（Meta 広告マネージャ・X 広告・TikTok 広告マネージャー・Google 広告・LINE 広告）へのリンク |

文章は LP に書かれた価格・数値・効果だけを使い（AI がつくった数値は使わない）、ブランドの NG ワード・景表法／薬機法の規制表現をガードレールで確認します。LP の中の指示文には従いません（区切りタグで囲んで渡す）。プロンプトは DB で版管理（`lp.creative`・`video.landing_page`）し、運用管理から改善・評価できます。

## すぐに動かす（API キー不要）

```bash
# .NET 10 SDK が必要（動画には ffmpeg と日本語フォント）
dotnet run --project src/ReachForge.Web
# → http://localhost:5245（Development 環境）
```

| 項目 | ローカルの動作 |
|---|---|
| DB | SQLite（`reachforge.local.db`）。起動時に作成し、デモデータ（ほっこりカフェ渋谷店）を投入 |
| 生成AI | 決定的なスタブ（`local`）。API キーを設定すると Anthropic / OpenAI が優先され、障害時のみスタブへフォールバック |
| 動画のジョブ | Web プロセス内で実行（`Worker:RunInWeb`） |
| ログイン | デモ利用者 `owner@example.com`（パスワード `ReachForge#2026`）。Development では管理者の MFA 必須を無効化 |

実際の生成AIを使う場合は、**「設定 → 生成AIの設定」（`/settings/ai`）** で Claude（Anthropic）・OpenAI・Google・Kling の API キーを入力します（運用者のみ。Windows 版はオーナー）。保存するとすぐに使われ（再起動不要）、「接続を確認」で各社のモデル一覧を取得してキーが使えるかを確かめます（生成はしないので利用料はかからない）。キーは暗号化して DB に保存し、画面には表示しません。User Secrets・環境変数でも設定でき、画面の値が優先します。

```bash
dotnet user-secrets --project src/ReachForge.Web set "AI:Providers:anthropic:ApiKey" "<key>"
# モデルは appsettings.json の AI:Providers / AI:Routes で設定（コードにハードコードしない）
```

## Windows 版（PC 単体で完結）

サーバーを用意せず、Windows の PC だけで使えるデスクトップアプリです（Windows 10 1809 以降 / 11、x64）。インストーラーで配布し、PC の中の1つのプロセスで動かします。画面は Web 版と同じものを WebView2 で表示します。

| 項目 | 内容 |
|---|---|
| 構成 | `ReachForge.exe` だけで動く。ReachForge.Web を同じプロセスの中で `https://localhost:47120` で起動し（外部からは接続できない）、WPF ＋ WebView2 の画面で表示する |
| データ | `%LOCALAPPDATA%\ReachForge`：DB（SQLite）・画像と動画・ログ（14日分）・証明書・設定。アプリを更新・アンインストールしても残る。DB は起動時にマイグレーションで新しい版にそろえる（`ReachForge.Migrations.Sqlite`） |
| ログイン | 起動ごとに作る秘密の値で、最初に登録した利用者（オーナー）として自動ログインする。初回は登録画面でワークスペースを作る |
| 常駐 | ウィンドウを閉じてもタスクトレイに残り、動画づくりを続ける。終了はトレイのメニューから |
| 設定 | 生成AI の API キーは「設定 → 生成AIの設定」で入力する（すぐ反映）。それ以外の細かな設定はトレイの「設定ファイルを開く」で `appsettings.user.json` を編集し、アプリを再起動する。キーは暗号化して保存し、暗号鍵は Windows の DPAPI で利用者ごとに保護する |

- 動画の書き出しに使う ffmpeg を同梱する：`publish.ps1` が決めた版（8.1.2、gyan.dev の essentials ビルド・libx264 入りの GPL v3 版）を取得し、SHA-256 を確かめて `app\ffmpeg` に入れる（`LICENSE.txt`・`README.txt`・`SOURCE.txt` も）。ffmpeg のソースコードは `artifacts/desktop/ffmpeg-source` に保管し、CI の成果物にも残す。手元の ffmpeg を使うときは `-FfmpegDir`、同梱しないときは `-NoFfmpeg`。版を上げるときは `publish.ps1` の版・URL・SHA-256 をそろえて変える
- WebView2 ランタイムがなければインストーラーが案内する
- 本番用のインストーラーは GitHub Actions の「Windows installer」（`.github/workflows/desktop-release.yml`）が Windows 上でつくる
  - リリース：`ReachForge.Desktop.csproj` の `Version` をそろえ、`v{Version}` のタグ（例：`v1.0.0`）を push する → インストーラー（`ReachForge-Setup-{Version}.exe`）と SHA-256 を「リリースの下書き」に添付する。確認してから公開する
  - main への push・手動実行、またはコミットのメッセージに `[build installer]` を含めた push（どのブランチでも）でも、成果物（Artifacts）としてつくる。同梱した ffmpeg（GPL v3）のソースコードも別の成果物として保管する
  - コード署名：Azure Artifact Signing を使う（手順は `deploy/desktop/CODE-SIGNING.md`）。リポジトリの変数 `ARTIFACT_SIGNING_ENDPOINT`・`ARTIFACT_SIGNING_ACCOUNT`・`ARTIFACT_SIGNING_PROFILE` とシークレット `AZURE_CLIENT_ID`・`AZURE_TENANT_ID`・`AZURE_SUBSCRIPTION_ID` を登録すると、アプリ本体とインストーラーに署名し、署名が有効かを確かめる。署名がないと、初回に Windows SmartScreen の警告が出る
- プロセス内での起動・自動ログイン・画面の静的ファイル・終了・マイグレーションは `tests/ReachForge.Desktop.Tests` で Linux でも確認する

### SNS ごとの画像・動画の形式

2026年10月時点の各社のヘルプと主要な解説で確認した値です（`PlatformCatalog` の `ImageFormats`・`VideoFormats`）。入稿前に各社の最新の仕様も確認してください。

| SNS | 画像 | 動画 |
|---|---|---|
| Instagram | フィード 4:5（1080×1350）、ストーリーズ 9:16（1080×1920） | リール・ストーリーズ 9:16（1080×1920、広告は90秒まで） |
| Facebook | フィード 4:5（1080×1350）、リンク広告 1.91:1（1200×628） | リール・ストーリーズ 9:16（1080×1920） |
| X | 画像広告・投稿 1:1（1200×1200）、投稿 16:9（1600×900） | タイムライン 16:9（1920×1080、2分20秒まで） |
| Threads | 投稿 4:5（1080×1350） | 投稿 9:16（1080×1920、5分まで） |
| TikTok | フォトモード 9:16（1080×1920） | 動画 9:16（1080×1920） |
| YouTube | サムネイル 16:9（1280×720） | ショート 9:16（1080×1920、3分まで）、通常の動画 16:9（1920×1080） |
| LINE | LINE広告 Square・メッセージ 1:1（1080×1080）、Card 1.91:1（1200×628） | Vertical・VOOM 9:16（1080×1920）、Card 16:9（1920×1080） |

縦型（9:16）は画面の下にアプリのボタンや説明文が重なるため、見出し・テロップを下から約2割上げて置きます。

## LP の読み取りと動画づくり

| 段階 | 使う AI・処理 | 選んだ理由 |
|---|---|---|
| ① LP の読み取り | SSRF 対策付きの取得（http/https・公開アドレスのみ、DNS 解決後も再検査、リダイレクトは各ホップを検査、robots.txt を尊重、2MB／10秒）。本文・説明・色と、SNS 共有用の画像（og:image）・本文の画像を取り出す | 外部サイトを安全に読む |
| ② SNS ごとの文章 | LLM（`AI:Routes:Copy`、既定は Claude）の構造化出力：広告文3案（見出し・本文・説明・ボタン）・投稿文・ハッシュタグ。文字数の上限・URL の除去・ガードレールはコードでも確認する | 日本語の広告文と構造化出力の質 |
| ③ 素材の画像を選ぶ | 画像理解（`AI:Routes:Vision`、プロンプト `lp.images`）で、商品・場面など特色が伝わる写真と、ソフトウェアの画面（スクリーンショット）をおすすめ順に選び、写真か画面かを見分ける。小さい画像・細長いバナー・取得できない画像は先に除く | SaaS の LP では画面こそが製品。ロゴ・文字だけのバナーは素材にしない |
| ④ 世界観とビジュアル案 | LLM（`lp.visuals`）が LP の内容・色から、雰囲気・色（#RRGGBB）・背景の模様・映像の舞台（例：深夜の監視室）と、画像ごとの切り口・見出し・画像／背景／動きの指示をつくる | 背景を LP の内容から決める |
| ⑤ 広告の写真 | 画像生成（`AI:Routes:ImageEdit`、既定は Google **Nano Banana Pro**（`gemini-3-pro-image`、2K）→ OpenAI GPT Image）に素材の画像を渡し、商品はそのまま、背景・光・置き方で広告写真にする。画像理解（`lp.review`）で出来（商品が変わっていないか・文字の崩れ・不自然さ・魅力）を確かめ、不合格なら指摘を足して1回つくり直す。**画面**は描き直さず、世界観の背景（画像生成、または LP の色で描く模様）の上でノートパソコン・スマートフォンの枠に入れる。SNS の形式ちょうどに仕上げ、見出しを正確な日本語のフォントで入れる。AI でつくれないときも無地にはしない（写真は枠に収め、背景は模様を描く） | 商品・画面を変えずに伝わる絵にする。文字は AI に描かせない |
| ⑥ 動画の企画 | LLM で商品・お客様像・良さ・特典・行動喚起を整理し、hook／problem／solution／benefit／proof／offer／cta のシーン（テロップ・ナレーション・秒数・使う画像）をつくる | 価格は LP に書かれているものだけを許す |
| ⑦ 動画の合成 | 向き（縦型 1080×1920・横型 1920×1080）ごとに、シーンの映像をその向きの広告写真からつくり、最大4シーンは動画生成（`AI:Routes:VideoGeneration`、既定は **Kling**（`kling-v3`、5／10秒）→ Google **Veo 3.1 Fast**（720p、4／6／8秒））で動かす（ほかはゆっくりズーム）。画面のシーンは背景の情景だけを動かし、その上に端末の枠に入れた画面とテロップを重ねる。テロップ、音声合成（OpenAI TTS）のナレーション、ライセンス済み BGM（ナレーション中は自動で音量を下げる）→ FFmpeg で 30fps・H.264/AAC。画像は比率を保って枠を覆う（引き伸ばさない） | AI ラベル（IPTC／C2PA の来歴）付き |

- 権利：LP の画像を使うときは「広告・動画に使う権利がある」ことの確認を必須にする。取り込んだ画像には取得元の URL を来歴に記録する
- AI の利用料金：アプリ内のクレジット・回数の上限はなく、料金は API キーを発行した各 AI サービスから直接請求される。AI を呼ぶたびに推定料金（`AI:Providers` の単価 × トークン数・画像の枚数・動画の秒数・ナレーションの分数）を `AiUsageLogs` に記録し、「設定 → AI の利用料金」で今月・先月の目安を機能別・サービス別に表示する（円換算は `AI:UsdJpyRate`、既定 150）
- 動画生成のテスト（「設定 → 動画生成のテスト」`/settings/video-lab`、運用者のみ）：Kling・Veo などのプロバイダとモデル・プロンプト・描かないもの・比率・長さ・高画質（Kling の pro）・起点の画像を指定して動画をつくり、結果を設定とともに残して見比べる（良い／いまいち・メモ）。広告動画づくりの場面ごとのひな形（冒頭・解決・画面を重ねる背景・画像から動画）付き。プロバイダを指定したときは代替プロバイダへ切り替えず、AI サービスが返したエラーをそのまま表示する。「LP から考える」に LP の URL を入れると、LLM（`lp.video-prompts`）が LP の文言（困りごと・解決策・機能）を場面ごと（冒頭・解決・画面を重ねる背景・画像から動画）の映像の描写（英語、文字は描かせない）に書き換え、元にした LP の文言（本文にあるものだけ）を並べて表示する。比べるための「文言そのまま（日本語）」と、起点の画像に使える LP の画像（AI のおすすめ順）も出す
- 設定：`Video:FfmpegPath`（既定 `ffmpeg`）、`Video:BgmLibraryPath`（ライセンス済みの曲と `tracks.json`）、`Media:FontPath`（テロップのフォント。未設定なら OS の Noto Sans JP / IPA フォント等）

> 画像処理は SkiaSharp（MIT）を使用しています（`IImageProcessor` の実装 `SkiaImageProcessor`）。
> 以前の ImageSharp は脆弱性の修正版（4.x）がライセンスキー必須になったため置き換えました。

## 操作説明書・About

- 画面右上の氏名のメニュー →「操作説明書（PDF版）」：`/help/manual.pdf`（ログイン不要。QuestPDF で生成、`src/ReachForge.Infrastructure/Help/ManualPdfRenderer.cs`）。Windows 版は既定の PDF ビューアーで開く
- 「About」：アイコン・名前・バージョン（ver 1.00.00）・開発元（ブラウザで開く）・お問い合わせ先（メーラーを起動）、「使用許諾契約」「第三者ソフトウェアのライセンス」
- バージョン・開発元は `Help/AppInfo.cs`、契約とライセンスの文面は `Help/LegalTexts.cs` で管理（使用許諾契約はひな形のため、リリース前に法務の確認を受けること）

## ブランド設定

- 口調・一人称・絵文字・NG ワード・必須表記・お客様像・よく使うハッシュタグ・商品（価格は文章の事実確認に使う）。AI はこれに沿って文章をつくる
- 「AIでブランド診断」：Web サイトの URL・紹介文から、口調・お客様像・ハッシュタグ・避けたい表現・ブランドカラーの下書きをつくり、選んだ項目だけ反映

## プロンプト管理・AI Evals（RF-DES-001 4.5・4.6）

- AI への指示文は Scriban テンプレートとして DB（`PromptTemplates`）で版管理する。初回起動時にコードの既定テンプレート（`PromptLibrary.Defaults`）を v1 として登録する。データはコードで整形して `{{ brand }}` などの値として差し込み、値の中の `{{ }}` はテンプレートとして解釈しない。安全規約（`common.safety`）は `{{ safety }}` で共通化し、外した版は保存できない
- 運用管理 → プロンプト管理（`/ops/prompts`）：下書き → プレビュー（見本の値）→ 評価 → 一部のテナントで試す → 全体に公開。前の公開版は保管になり、公開し直せば元に戻せる
- AI Evals（`src/ReachForge.AI.Evals`）：`dotnet run --project src/ReachForge.AI.Evals -- [--cases 50] [--out report.md]`。CI では AI・プロンプト・設定を変えたコミットで、シークレットがあれば実モデルで評価する

## 認証・アクセスの保護

- ASP.NET Core Identity（Cookie）。5回失敗で15分ロック、2段階認証（TOTP・回復コード。オーナー・管理者は必須：`Auth:RequireMfaForAdmins`）
- パスワード再設定：ログイン画面からメールで再設定（1時間・1回限り、登録の有無は表示しない）
- 操作は画面（ログイン）からだけ。`/api/v1` は、つくったもののダウンロード（`/api/v1/files/{id}`、`/api/v1/lp/{id}/download`：ログインした利用者の自分のワークスペースのものだけ）。画面の画像・動画の表示は期限付きの署名 URL（`/media/{token}`、改ざん・期限切れは 404）
- レート制限：テナント単位 600 回/分、AI 生成系 60 回/分、ログイン前の POST は IP 単位 20 回/分（`RateLimits:*`）

## データベース（SQLite）

- DB は SQLite だけを使う（Windows 版・1社で使う前提）。接続は `ConnectionStrings:ReachForge`（既定 `Data Source=reachforge.local.db`）
- Windows 版は EF Core マイグレーション（`src/ReachForge.Migrations.Sqlite`）で、利用者の PC の DB を新しい版にそろえる（データは残す）。開発・テストはモデルから作成し、スキーマが変わったらデモ用 DB を作り直す
- モデルを変えたら `dotnet ef migrations add <名前> -p src/ReachForge.Migrations.Sqlite -s src/ReachForge.Migrations.Sqlite -o Migrations` で追加する（漏れは ReachForge.Desktop.Tests で検出）

## ジョブ基盤

- 動画・画像の生成は AI ジョブ（`AiJob`）として裏で実行し、段階（待機中 → 生成中 → 確認中 → 完了）をすぐ画面へ届ける（Blazor のサーキット＝SignalR。外部クライアント向けに `/hubs/realtime` の `jobProgress`）
- 定期ジョブ：DataRetentionJob（02:00：監査ログ2年、終了した AI ジョブ・期限切れ招待 90日。つくったものの動画のジョブは残す）。時刻は `Jobs:TimeZone`（既定 Asia/Tokyo）
- 実行エンジン `Jobs:Engine`：`Hosted`（既定）／`Hangfire`（メモリのストレージ）。キュー `Jobs:Queue`：`InProcess`（既定）／`ServiceBus`
- 運用管理（`/ops`）：`Ops:Operators` に書いたメールアドレスの利用者（Windows 版はオーナー）だけが見られる

## テスト

```bash
dotnet test   # xUnit v3（Microsoft.Testing.Platform）
```

- `ReachForge.Domain.Tests`・`ReachForge.Application.Tests`（SQLite＋スタブAI）・`ReachForge.AI.Tests`・`ReachForge.Web.Tests`（認証・ダウンロード・設定の結合テスト）・`ReachForge.Desktop.Tests`
- `ReachForge.E2E.Tests`：Web を実ポートで起動し、Playwright（Chromium）で主な流れ（LP を読み込む → SNS を選ぶ → つくる → 文章・画像・動画ができてダウンロードできる）を操作する。ブラウザは `PLAYWRIGHT_CHROMIUM` → `/opt/pw-browsers` → Playwright の既定の順に探す

## 構成

```
src/
  ReachForge.Domain/          エンティティ・プラットフォーム制約マスタ・ガードレール（外部依存なし）
  ReachForge.Application/     ユースケース（LP からつくる・メディア・動画・ブランド・AI の利用料金）、権限、AI の抽象
  ReachForge.AI/              設定駆動モデルルータ（フェイルオーバー・サーキットブレーカー・計量）、プロンプト、文章・動画の企画・画像・音声
  ReachForge.Infrastructure/  EF Core（SQLite）、テナント分離、デモデータ、ジョブ基盤、画像処理・FFmpeg・LP の取得
  ReachForge.ServiceDefaults/ OpenTelemetry・ヘルスチェック・HTTP 回復性
  ReachForge.Web/             Blazor Web App（MudBlazor 9）＋ ダウンロード用の Minimal API
  ReachForge.Worker/          ジョブの処理（Web と分ける場合）
  ReachForge.Migrations.Sqlite/ SQLite 用マイグレーション（Windows 版）
  ReachForge.Desktop.Host/    Windows 版のアプリのプロセス内起動・停止・証明書・データフォルダー・ログ（OS 非依存）
  ReachForge.Desktop/         Windows 版の画面（WPF ＋ WebView2・タスクトレイ）
```

依存方向は Domain ← Application ← (AI / Infrastructure) ← (Web / Worker)。外部 AI はすべてインタフェース越しに利用します。

Blazor Server の DI スコープはサーキット（タブを開いている間）単位のため、DB を使う画面・部品は `RfComponentBase`（OwningComponentBase）を継承して画面ごとのスコープからサービスを取得し（`Scoped<T>()`）、閉じたときに DbContext を破棄します。

## 次の段階

1. 実際の Claude・OpenAI（音声合成）・ffmpeg での文章と動画の品質の確認（プロンプトの改善は運用管理から）
2. LP の画像が少ないときの AI 画像（商品を描き直さない背景のみ）や、文字入りのバナー画像
3. SNS ごとの動画の長さ・比率の追加（X・YouTube 用の 16:9 など）

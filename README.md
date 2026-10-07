# ReachForge（AI SNS集客プラットフォーム）

C#（.NET 10 LTS）と複数の生成AIを組み合わせ、SNS投稿の **企画 → 生成 → SNS別最適化 → 承認 → 予約配信 → 分析** を一気通貫で支援するアプリケーションです。

- 基本設計書・機能設計書：RF-DES-001
- UI/UX仕様書：RF-UX-001

## すぐに動かす（API キー不要）

```bash
# .NET 10 SDK が必要
dotnet run --project src/ReachForge.Web
# → http://localhost:5245（Development 環境）
```

Development 環境では次の状態で起動します。

| 項目 | ローカルの動作 |
|---|---|
| DB | SQLite（`reachforge.local.db`）。起動時に作成し、デモデータ（ほっこりカフェ渋谷店・連携済み4SNS・過去60日の指標）を投入 |
| 生成AI | 決定的なスタブ（`local`）。API キーを設定すると Anthropic / OpenAI が優先され、障害時のみスタブへフォールバック |
| SNS | 公式APIの設定がないSNSはデモ接続（モック、実際には投稿しない）。本文に `[[transient]]` / `[[fail]]` を含めると一時的／恒久的エラーを再現 |
| 予約配信・トークン更新 | Web プロセス内で実行（本番は `ReachForge.Worker`） |
| ログイン | 下記のデモ利用者（パスワード `ReachForge#2026`）。Development では管理者の MFA 必須を無効化 |

| メール | ロール |
|---|---|
| owner@example.com | オーナー |
| editor@example.com | 編集者 |
| approver@example.com | 承認者 |
| viewer@example.com | 閲覧者 |

実際の生成AIを使う場合（User Secrets または環境変数）：

```bash
dotnet user-secrets --project src/ReachForge.Web set "AI:Providers:anthropic:ApiKey" "<key>"
# モデルは appsettings.json の AI:Providers / AI:Routes で設定（コードにハードコードしない）
```

## Windows 版（PC 単体で完結）

サーバーを用意せず、Windows の PC だけで使えるデスクトップアプリです（Windows 10 1809 以降 / 11、x64）。画面は Web 版と同じものを WebView2 で表示します。

| 項目 | 内容 |
|---|---|
| 構成 | `ReachForge.exe`（WPF ＋ WebView2）が、同梱のサーバー（`server\ReachForge.Web.exe`）を子プロセスとして `https://localhost:47120` で起動する（外部からは接続できない） |
| データ | `%LOCALAPPDATA%\ReachForge`：DB（SQLite）・メディア・ログ（14日分）・証明書・設定。アプリを更新・アンインストールしても残る。DB は起動時にマイグレーションで新しい版にそろえる（`ReachForge.Migrations.Sqlite`） |
| ログイン | 起動ごとに作る秘密の値で、最初に登録した利用者（オーナー）として自動ログインする。初回は登録画面でワークスペースを作る |
| 常駐 | ウィンドウを閉じてもタスクトレイに残り、予約投稿・指標の取得・受信箱の取り込みを続ける。終了はトレイのメニューから。「Windows の起動時に開始」も選べる |
| 設定 | トレイの「設定ファイル（API キー）を開く」で `appsettings.user.json` を編集する（生成AI の API キー、SNS アプリの ID・シークレット）。保存するとすぐ反映される。SNS のトークンの暗号鍵は Windows の DPAPI で利用者ごとに保護する |
| HTTPS | SNS の OAuth は HTTPS のコールバックを求めるため、初回に `localhost` 用の自己署名証明書を作り、アプリの画面だけがこの証明書を信頼する（OS の信頼ストアには入れない） |

SNS アプリに登録するコールバック URL：`https://localhost:47120/api/v1/oauth/callback/<X|Facebook|Instagram|Threads|TikTok|YouTube>`
（ポートは `desktop.json` の `Port` で変えられるが、変えたら SNS 側の登録も変える）

PC 単体で使えない・制限があるもの：

- Webhook（SNS 側からの通知）は届かないため、受信箱は定期取得（5〜15分ごと）で取り込む。LINE のトーク（Webhook のみ）は取り込めない
- Instagram・Threads・Facebook・TikTok の写真投稿は、SNS 側が画像を URL から取得するため、外部から届く保存先（`Media:BlobServiceUri` の Azure Blob Storage など）を設定ファイルで指定する必要がある。X・YouTube・TikTok の動画はアップロードするので不要
- 予約投稿は PC が起動していて ReachForge が動いている間だけ実行される（止まっていた間の分は次の起動時に投稿する）
- チームでの承認フロー・複数人の同時利用は Web 版を使う
- SNS 各社がアプリ内の画面（WebView2）での OAuth を拒む場合がある（特に Google）。その場合は Web 版で連携するか、各社の設定を確認する

発行・インストーラー：

```powershell
pwsh deploy/desktop/publish.ps1 [-FfmpegDir C:\ffmpeg\bin]   # artifacts/desktop/app（.NET を同梱）
iscc deploy/desktop/ReachForge.iss                             # Inno Setup 6 でインストーラーを作る（管理者権限なしで入る）
```

#### Windows 版のデバッグ

前提：Windows 10/11、Visual Studio 2026（「.NET デスクトップ開発」と「ASP.NET と Web 開発」）または .NET 10 SDK、WebView2 ランタイム。

| やりたいこと | 方法 |
|---|---|
| アプリ全体を動かす | `ReachForge.Desktop` をスタートアップにして F5（または `dotnet run --project src/ReachForge.Desktop`）。サーバーは自動でビルドされ、リポジトリの `src\ReachForge.Web\bin\Debug\net10.0` のものを子プロセスで起動する（別のサーバーを使うなら環境変数 `REACHFORGE_SERVER_PATH`） |
| 画面（Blazor）を調べる | アプリの画面で F12 → WebView2 の開発者ツール（Debug ビルドのみ。Release では無効） |
| サーバーのコードで止める | ① アプリを F5 で起動したまま「デバッグ → プロセスにアタッチ」で `ReachForge.Web.exe` を選ぶ、または ② サーバーだけを単独で起動：`dotnet run --project src/ReachForge.Web --launch-profile desktop`（Visual Studio ではプロファイル「desktop」を選んで F5）→ ブラウザが `https://localhost:47120/desktop/signin?t=debug` で開き自動ログインする。DB はリポジトリ直下の `reachforge.desktop.db`、設定は `appsettings.desktop-debug.json`（どちらも Git の対象外）。この場合はアプリ（WPF）を起動しない（ポートが重なる） |
| 起動しない・止まる | `%LOCALAPPDATA%\ReachForge\logs\server-日付.log`（サーバーの出力）。起動画面の「ログを開く」からも開ける。ポートが使用中なら `desktop.json` の `Port` |
| データを初期状態に戻す | アプリを終了して `%LOCALAPPDATA%\ReachForge`（DB・メディア・設定・証明書）を削除する。WebView2 の Cookie だけなら `webview` フォルダー |
| お試し用のデモデータ | 単独起動（②）で環境変数 `Database__SeedDemo=true` を付ける（owner@example.com ／ ReachForge#2026） |
| トレイから起動した状態を試す | 引数 `--minimized`（プロジェクトのデバッグ設定のコマンドライン引数） |
| 証明書のエラー | `%LOCALAPPDATA%\ReachForge\certs\localhost.pfx` を削除すると次の起動で作り直す |

- CI（`desktop` ジョブ）で Windows 上で発行・インストーラーの作成まで行い、成果物として残す
- 動画機能には ffmpeg が必要（`-FfmpegDir` で同梱するか、PC に入れて PATH を通す）。WebView2 ランタイムがなければインストーラーが案内する
- サーバーの起動・自動ログイン・終了・マイグレーションは `tests/ReachForge.Desktop.Tests` で Linux でも確認する（画面の WPF は Windows でのみ動く）

## リアルタイム通知（RF-DES-001 3.3）

- AI ジョブの段階（待機中 → 生成中 → 確認中 → 完了）と受信箱の更新（新着・対応状況・炎上アラート）は、保存した時点で画面へ届く（Blazor のサーキット＝SignalR）。ナビの未対応件数・クレジット残量もすぐ更新される
- Web と Worker を分ける構成では `ConnectionStrings:Redis` を設定すると、Redis の Pub/Sub で Worker の出来事が全 Web インスタンスへ届く。未設定でも画面は数秒〜30秒ごとの読み直しで追いつく
- 外部クライアント向けに SignalR Hub `/hubs/realtime`（ログインまたは API キー。WebSocket は `access_token` クエリ）を用意。メソッド `jobProgress` / `inboxUpdated` で、自分のワークスペースの出来事だけを受け取る
- テスト：`RF_TEST_REDIS=127.0.0.1:6379 dotnet test` で Redis 経由の配信も確認する

## プロンプト管理・AI Evals（RF-DES-001 4.5・4.6）

- AI への指示文は Scriban テンプレートとして DB（`PromptTemplates`）で版管理する。初回起動時にコードの既定テンプレート（`PromptLibrary.Defaults`）を v1 として登録する。ブランド・商品・SNS の制約などのデータはコードで整形して `{{ brand }}` などの値として差し込み、値の中の `{{ }}` はテンプレートとして解釈しない。安全規約（`common.safety`）は `{{ safety }}` で共通化し、外した版は保存できない
- 運用管理 → プロンプト管理（`/ops/prompts`）：下書き → プレビュー（見本の値）→ 評価 → 一部のテナントで試す（1〜99%、テナントごとに固定）→ 全体に公開。前の公開版は保管になり、公開し直せば元に戻せる。変更は1分以内に全インスタンスへ反映。生成記録（`AiGeneration`）に使った版を残す
- AI Evals（`src/ReachForge.AI.Evals`）：評価セット 240 件（10業種×7テーマ×3目的＋レッドチーム30件、`datasets/copy.generate.json`）で、ブランド適合度（別モデルによる採点、平均 4.0 以上）・制約遵守率（99% 以上）・安全性（規制表現・禁止表現 0 件）・事実性（商品マスタとの価格の突合 0 件）を判定する（`Microsoft.Extensions.AI.Evaluation`）
  - CLI：`dotnet run --project src/ReachForge.AI.Evals -- [--cases 50] [--prompt-file draft.sbn] [--out report.md]`（不合格なら終了コード 1）。モデルは `AI__Providers__…`／`AI__Routes__Copy__0`・`AI__Routes__Judge__0` で指定。未設定ならスタブで、評価の仕組みだけを確認する（スタブは指示をそのまま書くため不合格になる）
  - CI（`.github/workflows/ci.yml`）：AI・プロンプト・設定を変えたコミットで、`ANTHROPIC_API_KEY` などのシークレットがあれば実モデルで評価してレポートを残す
  - 管理画面の「評価する」は copy.generate・common.safety・judge.brand_fit が対象（既定 20 ケース。AI の利用料がかかる）

## .NET Aspire（ローカルの一括起動）

- `dotnet run --project src/ReachForge.AppHost`：PostgreSQL・Redis（コンテナ）と Web・Worker をまとめて起動し、Aspire ダッシュボードでログ・トレース・メトリクスを確認する（Docker または Podman が必要）。Web・Worker は PostgreSQL（起動時マイグレーション）・Hangfire・Redis のリアルタイム通知で動く
- `-- --UseServiceBus=true` で Azure Service Bus エミュレーター（`webhooks`・`ai-jobs` キュー）も起動し、キューを Service Bus に切り替える
- ローカルの PostgreSQL は管理者ロールで接続するため行レベルセキュリティは効かない（RLS の確認は `RF_TEST_POSTGRES` のテストで行う）
- 起動時の初期化（マイグレーション・デモデータ）は PostgreSQL のアドバイザリロックで1プロセスずつ行う（Web と Worker の同時起動で競合しない）

## E2E・アクセシビリティ検査

- `tests/ReachForge.E2E.Tests`：Web を実ポート（Kestrel）で起動し、Playwright（Chromium）で操作する。投稿の作成 → 承認依頼 → 承認、権限のない画面、主要17画面の axe-core 検査（WCAG 2.1 AA の「重大」「深刻」違反 0 件）、スマホ幅（390px）で横スクロールが出ないこと
- ブラウザは `PLAYWRIGHT_CHROMIUM`（実行ファイル）→ `/opt/pw-browsers` → Playwright の既定の順に探す。初回は `pwsh tests/ReachForge.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`（CI でも実行）

## 認証（RF-DES-001 9.1 / RF-UX-001 SCR-01・SCR-15）

- ASP.NET Core Identity（Cookie）。5回失敗で15分ロック、エラー文はどちらが違うかを示さない、パスワード貼り付け可
- 2段階認証（認証アプリの TOTP、回復コード）。**オーナー・管理者は必須**（`Auth:RequireMfaForAdmins`）
- 新規登録でテナント・ワークスペース・オーナーを作成。メンバーは招待リンク（7日間有効、トークンはハッシュのみ保存）で参加
- ロールは要求ごとに `WorkspaceMember` から解決するため、権限変更・メンバー削除は即時に反映される
- 外部 ID（Google / Microsoft / Entra External ID）：`Auth:Oidc:<名前>` に Authority・ClientId・ClientSecret を設定すると有効。既存利用者（同じ確認済みメールアドレス）に紐づける
- 監査ログ：ログイン・ログアウト・MFA 変更・招待・ロール変更・チャネル連携

## データベース（PostgreSQL・行レベルセキュリティ）

- ローカル開発・テストは SQLite（モデルから作成）。本番は `Database:Provider=Postgres` と `ConnectionStrings:ReachForge`
- スキーマは EF Core マイグレーション（`src/ReachForge.Infrastructure/Persistence/Migrations`）。CI/CD で表の所有者ロールとして適用し（例：`dotnet ef migrations bundle` で作った実行ファイル）、アプリは別ロールで接続する。`Database:MigrateOnStartup=true` で起動時に未適用分を適用することもできる（開発環境向け）
- テナント分離はアプリのクエリフィルタと RLS の二重化。`TenantId` 列を持つ全表に、接続ごとに設定するセッション変数（`app.tenant_id`／`app.is_system`）と一致する行だけ読み書きできるポリシーを付けている（FORCE で所有者にも適用）。スーパーユーザーと BYPASSRLS のロールは RLS を回避するため、アプリは `deploy/postgres/setup-roles.sql` の `reachforge_app`（NOSUPERUSER・NOBYPASSRLS）で接続する
- モデルを変えたら `dotnet ef migrations add <名前> -p src/ReachForge.Infrastructure -s src/ReachForge.Infrastructure -o Persistence/Migrations`。新しい表を作るマイグレーションでは `SELECT rf_enable_rls();` を呼ぶ（テストでマイグレーション漏れ・RLS 漏れを検出）。Windows 版の SQLite 用も同じ名前で `dotnet ef migrations add <名前> -p src/ReachForge.Migrations.Sqlite -s src/ReachForge.Migrations.Sqlite` で追加する（漏れは ReachForge.Desktop.Tests で検出）
- PostgreSQL でテストする：`RF_TEST_POSTGRES="Host=…;Username=postgres;Database=postgres" dotnet test`（テストごとに DB を作り、RLS 付き・アプリ専用ロールで実行）

## ジョブ基盤（RF-DES-001 14章）

- 定期ジョブ：TokenRefreshJob（03:00）、MetricsCollectJob（15分ごと。アカウント指標は1日1回）、Weekly／MonthlyReportJob、InboxPollJob（毎分確認・チャネルごとに5〜15分）、A/B 判定、TrendResearchJob、CreditResetJob（毎月1日 00:00・テナントのタイムゾーン）、DataRetentionJob（02:00）。時刻は `Jobs:TimeZone`（既定 Asia/Tokyo）。一覧と再試行回数は `SystemJobCatalog`
- 実行エンジン `Jobs:Engine`：`Hosted`（既定。プロセス内のタイマー）／`Hangfire`（PostgreSQL の `hangfire` スキーマ。複数 Worker でも1回だけ実行、14章の回数で再試行、失敗は残して運用者が再実行）。Hangfire は LGPL v3（改変せずに参照する限り商用利用可）
- キュー `Jobs:Queue`：`InProcess`（既定）／`ServiceBus`（`webhooks`・`ai-jobs` キュー、5回失敗でデッドレター）。Webhook は Web で受けてキューへ渡し、Worker が取り込む。AI ジョブは保存後にキューで通知し、届かなかった分は巡回（`Jobs:AiSweepSeconds`）で拾う。Service Bus は `Jobs:ServiceBus:FullyQualifiedNamespace`（マネージド ID）か `ConnectionString`。`CreateQueues=true` で起動時にキューを作成
- 予約配信（PublishJob）は ±60 秒の精度が要るため、どちらのエンジンでも Worker の短い間隔の巡回＋楽観排他で実行する
- 運用管理（`/ops`）：`Ops:Operators` に書いたメールアドレスの利用者だけが見られる。定期ジョブの一覧、デッドレターの確認・再投入、Hangfire の実行履歴（`/ops/jobs`）
- データ保存期限：Idempotency-Key 24時間、監査ログ2年、終了した AI ジョブ・使用済み／見送りのネタ・期限切れ招待 90日
- 投稿指標（PostMetrics）：PostgreSQL では取得日時（`CapturedAt`）で月ごとにパーティション分割する。DataRetentionJob が3か月先までのパーティションを作り、13か月より前の明細を投稿×月の集計（`PostMetricRollups`、その月の最後の値）へ移して削除し、空になったパーティションを削除する（所有者権限の関数 `rf_ensure_post_metric_partitions`／`rf_drop_post_metric_partitions` を使うため、アプリ用ロールに CREATE 権限は不要）。分析は明細のない古い投稿を集計の値で表示する

## 外部連携 API（RF-DES-001 13章）

- 認証：ログイン（Cookie）または API キー。キーは「設定 → API キー」でオーナー・管理者が発行（権限は編集者／承認者／返信担当／閲覧者から選択、有効期限つき、本体は一度だけ表示・DB にはハッシュのみ）。`Authorization: Bearer rfk_…` または `X-Api-Key: rfk_…`。キーは `/api/v1` 以外（画面）では使えない
- レート制限：テナント単位 600 回/分、AI 生成系（投稿文・SNS 別変換・画像・レポート・返信案・A/B）60 回/分、ログイン前の POST（ログイン・パスワード再設定など）は IP 単位 20 回/分。超過時は 429・`Retry-After`。設定は `RateLimits:*`（複数インスタンスでは Front Door / API Management でも制限する）
- 冪等性：POST に `Idempotency-Key` を付けると、同じキーの再送には最初の応答を返す（`Idempotent-Replayed: true`、24時間保持）。内容の違う再送は 422、処理中は 409、5xx は保存しない
- パスワード再設定：ログイン画面の「パスワードを忘れた場合」からメールで再設定（1時間・1回限り、登録の有無は表示しない）。ロック時・パスワード変更時・招待時にメールで通知

## SNS 公式 API 連携（X / Facebook / Instagram / Threads / LINE / TikTok / YouTube）

各 SNS の開発者サイトでアプリを作成し、User Secrets・環境変数・Key Vault で設定します（リポジトリに置かない）。
コールバック URL は `https://<ホスト>/api/v1/oauth/callback/<X|Facebook|Instagram|Threads|TikTok|YouTube>` を登録します。

| SNS | 設定キー | 方式 |
|---|---|---|
| X | `Social:X:ClientId` / `ClientSecret` | OAuth 2.0 PKCE（S256）。約2時間で失効するため使用前にリフレッシュ |
| Facebook / Instagram | `Social:Meta:AppId` / `AppSecret`（`GraphVersion`） | Facebook Login for Business → 長期トークン → ページ・IG ビジネスアカウントを選択。Instagram は画像必須（画像の公開 URL を Meta が取得） |
| Threads | `Social:Threads:AppId` / `AppSecret` | 長期トークン（60日）、期限7日前から自動更新 |
| TikTok | `Social:TikTok:ClientKey` / `ClientSecret`（`Audited`・`Desktop`） | Login Kit v2（24時間トークン＋365日の更新用トークン）。Content Posting API の直接投稿：動画は分割アップロード、写真は公開 URL から取得（ドメイン確認が必要）。投稿前に creator_info で公開範囲の選択肢を確認し、公開完了まで status/fetch で確認。審査前（`Audited=false`）は「自分のみ」で投稿される。AI 生成の動画は `is_aigc` で申告。指標は video.list、フォロワー数は user.info.stats。コメント取得 API はないため受信箱は対象外。API から削除はできない |
| YouTube | `Social:YouTube:ClientId` / `ClientSecret` | Google OAuth 2.0（PKCE、`access_type=offline`）。動画のみ（縦型・3分以内はショート）。再開可能アップロード、AI 生成の動画は `containsSyntheticMedia` で申告、字幕（SRT）は字幕トラックとして追加。タイトルはバリアントのタイトル（なければ本文の1行目）。指標は videos.list の statistics、受信箱は直近の動画のコメント（15分ごと、返信・確認待ちへの移動）。クォータ超過（403 quotaExceeded）は一時的エラーとして再試行。テスト中（未確認）のアプリはトークンが7日で失効し、アップロードした動画は非公開になる |
| LINE 公式アカウント | （アプリ設定なし） | 画面でチャネル ID・シークレットを入力。ステートレストークンを都度発行。一斉配信は決定的な `X-Line-Retry-Key` で二重配信を防止 |

- 投稿作成画面で TikTok の公開範囲・コメント可否、YouTube の公開設定（公開／限定公開／非公開）を選べる。承認後に変えると再承認が必要
- state（CSRF 対策）・PKCE は暗号化して10分保存し、1回限り・開始した利用者のみ有効（複数インスタンスでは `ConnectionStrings:Redis`）
- トークンは `Secrets:KeyVaultUri` があれば Key Vault、なければ Data Protection で暗号化して DB に保存（DB には参照キーのみ）
- エラー分類：401・Meta code 190 → 「要再接続」、429・5xx・通信断 → 指数バックオフで再試行、その他 4xx → 失敗。投稿の POST は HTTP 層では再試行しない
- Webhook：`/api/v1/webhooks/meta`・`/threads`（`X-Hub-Signature-256`）、`/api/v1/webhooks/line/{channelId}`（`X-Line-Signature`）を署名検証。取り込み（受信箱）は F-09 で実装
- 各社の API 仕様は変わるため、URL・バージョンは設定値。本番接続前に各社の公式ドキュメントで再確認すること（RF-DES-001 0.3）

テスト：

```bash
dotnet test   # xUnit v3（Microsoft.Testing.Platform）
```

## 画像生成・メディア配信（F-04）

- メディアライブラリ（`/media`）：JPEG・PNG・WebP（20MB まで）。取り込み時に向き補正・位置情報などのメタデータ削除・sRGB 化。代替テキストは手入力か AI 生成（AI 生成は「AI」表示）。投稿で使用中の画像は削除不可
- AI 画像生成：スタジオのステップ1またはメディア画面から。非同期ジョブ（`AiJob`）で、受付時にクレジットを予約し、成功時に確定・失敗時に解放。安全性チェック → 正規化 → ロゴ合成（任意）→ 来歴記録 → 代替テキスト生成
- SNS 別の画像：変換時に SNS ごとの比率へ自動調整（スマートクロップ／余白追加／AI 拡張＝アウトペイント・クレジット消費）。Instagram は JPEG 8MB 以下（4:5 等）、LINE は 10MB 以下＋サムネイル 1MB 以下。拡大はしない
- 投稿時：X は `2/media/upload` → 代替テキスト登録 → `media_ids`（4枚まで）、Facebook は `/photos`（複数は非公開でアップロードして `attached_media` で1投稿に）、Instagram は `image_url`（2〜10枚はカルーセル）、Threads は `IMAGE`（複数はカルーセル）、LINE は画像メッセージ（本文＋4枚まで）
- 参照画像の AI 編集（メディア画面の「AIで編集する」、5クレジット）：
  - 背景差替：AI が背景を描き直し、単色に近い背景の写真では切り抜いた被写体の画素を元のまま戻す（商品の形・色・文字を変えない）
  - 不要物除去：4×4 のマスで範囲を選び（キーボード操作可）、その範囲だけを AI が描き直す。範囲外は元の画素のまま
  - 商品の配置：商品写真を切り抜き（白・単色背景または透過 PNG）、AI が作った背景に影を付けて重ねる。商品そのものは生成しない（F-04 業務ルール）。切り抜けない写真は受け付けずクレジットを解放
- AI 生成物の来歴（AI ラベル）：AI 生成・編集した画像とそこから作った SNS 用の画像に、IPTC の DigitalSourceType（trainedAlgorithmicMedia／compositeWithTrainedAlgorithmicMedia）を XMP で埋め込む（再エンコードしない）。Meta などはこれを見て「AI 情報」ラベルを付ける。C2PA マニフェスト（操作・モデル・学習利用不可）は DB に保存し、`Media:C2pa:ToolPath`（c2patool）・`SignCertPath`・`PrivateKeyPath` を設定すると署名して画像・動画に埋め込む
- 文字入れ：メディア画面の「文字を入れる」で見出し・補足を帯の上に描画（生成 AI に日本語を描かせず、フォントで正確に描く。文字色は帯の色に合わせて自動。0クレジット）。フォントは `Media:FontPath`（未設定なら OS の Noto Sans JP / IPA フォント等）
- 配信 URL：Blob 未設定時はアプリ経由の期限付き署名トークン（`/media/{token}`、改ざん・期限切れは 404）、Blob 設定時はユーザー委任 SAS。有効期限は LINE 30日、その他 30分

| 設定キー | 用途 |
|---|---|
| `Media:LocalPath` | ローカル保存先（Web と Worker で同じ場所） |
| `Media:BlobServiceUri` / `BlobContainer` | Azure Blob Storage（Managed Identity）。設定時は SAS で配信 |
| `Media:PublicBaseUrl` | Blob を使わずに実投稿する場合の公開 HTTPS URL（SNS が画像を取得できること） |
| `ContentSafety:Endpoint` / `Key` / `BlockSeverity` | Azure AI Content Safety（未設定時はチェックを省略しログに記録） |
| `AI:Providers:openai:ApiKey` | 画像生成（`AI:Routes:Image` / `ImageEdit`）。開発時は `local`（スタブ）で動作 |

> **ライセンス注意**：画像処理は SixLabors.ImageSharp 3.1 を使用しています（`IImageProcessor` の実装を差し替え可能）。
> Six Labors Split License では、年間売上 100万米ドル以上の営利企業による利用は商用ライセンスが必要です。本番利用前に確認してください。
> 4.x はライセンスキーがないとリリースビルドが失敗するため、3.1 系に固定しています。

## ブランド診断（F-02）

- ブランド設定「基本」の「AIでブランド診断」：Web サイトの URL・紹介文・過去の投稿（最大50件）から、口調・お客様像・ハッシュタグ・避けたい表現・ブランドカラー・よくある質問の下書きをつくり、選んだ項目だけ反映（3クレジット）
- サーバーから外部サイトを取得するため SSRF 対策を実装：http/https・標準ポートのみ、接続先 IP がプライベート・ループバック・リンクローカル（クラウドのメタデータ）等なら拒否（DNS 解決後の接続時にも再検査）、リダイレクトは各ホップを検査（最大3回）、robots.txt を尊重、2MB・10秒まで
- URL を読み込めない場合は紹介文だけで診断（W-BRD-001）

## ショート動画（F-05）

- メディア画面の「ショート動画をつくる」：3つの作り方。実行前に推定クレジットと所要時間を確認し、非同期ジョブで作成（進捗はリアルタイム表示）
  - ③ 画像＋テロップ（テンプレート合成）：選んだ画像＋テロップ＋AI ナレーション、10〜30秒。20クレジット＋ナレーション 30秒ごとに2
  - ② 画像に動きをつける／① 文章から動画（生成 AI）：4〜12秒、300クレジット。`AI:Routes:VideoGeneration` の順に Google Veo（`Type: Google`）・OpenAI Sora を使い、失敗時は次のプロバイダへ。15分を超えたら失敗としてテンプレート合成を案内。実在の人物は生成しない設定で依頼し、結果は 1080×1920・H.264/AAC に整える
- BGM：組み込みの2曲（FFmpeg で合成した音源のため著作権の制約なし）と、`Video:BgmLibraryPath` に置いたライセンス済みの曲（`tracks.json` に id・title・file・license）。ナレーションの間は自動で音量を下げる（サイドチェインのダッキング）
- 処理：AI が構成台本（シーンごとのテロップ・ナレーション・秒数）→ 各画像を 9:16 にスマートクロップしてテロップを焼き込み → TTS でナレーション（音声の長さに合わせてシーンを延長）→ FFmpeg でゆっくりズームをかけて連結、1080×1920・30fps・H.264/AAC・faststart で書き出し → 字幕（SRT）・サムネイルを作成。AI ラベル付き
- 投稿：Instagram はリール、Threads・Facebook は動画、LINE は動画メッセージ、X は分割アップロード（initialize → append（4MB ごと）→ finalize → 処理完了の確認）、TikTok は分割アップロードの直接投稿、YouTube は再開可能アップロード（字幕トラック付き）
- 設定：`Video:FfmpegPath`（既定 `ffmpeg`。コンテナに ffmpeg と日本語フォントを入れる）、`AI:Routes:Tts`（OpenAI の音声合成、`AI:Providers:openai:Voice`）。ローカルは無音のスタブ音声
- 未着手：動画の有害性判定（Content Safety は画像のみのため、生成 AI の動画はプロバイダ側の安全性フィルタに依存）

### LP から集客動画をつくる（生成 AI）

メディア画面の「LP から集客動画をつくる」：LP（ランディングページ）の URL から、SNS 広告向けの縦型ショート動画（10〜30秒）と投稿文の案をつくる。

| 段階 | 使う AI・処理 | 選んだ理由 |
|---|---|---|
| ① LP の読み取り | SSRF 対策付きの取得（公開アドレスのみ・robots.txt を尊重・2MB／10秒）。本文・説明・色と、SNS 共有用の画像（og:image）・本文の画像（アイコン・計測用・SVG・小さい画像は除く）を取り出す | ブランド診断と同じ安全な取得を使い、画像も同じ検査を通す |
| ② 訴求の整理・絵コンテ | LLM（`AI:Routes:Video`、既定は Claude）の構造化出力：商品・お客様像・良さ・特典・行動喚起 → hook／problem／solution／benefit／proof／offer／cta の役割を持つシーン（テロップ・ナレーション・秒数・使う LP の画像の番号）、投稿文・ハッシュタグ、冒頭の動きの指示 | 日本語の広告文と構造化出力の質。プロンプトは DB で版管理（`video.landing_page`、運用管理から改善・評価できる） |
| ③ 映像 | LP の画像を 9:16 にスマートクロップしてテロップを焼き込み、ゆっくりズーム。画像がないシーンはブランドカラーの背景にテロップ | 商品の見た目を AI に描き直させない（実物と違う映像は景表法・信頼の面で危険）。早く安い |
| ④ 冒頭だけ動かす（任意） | 画像→動画の生成 AI（Veo／Sora、`AI:Routes:VideoGeneration`）で1シーン目に自然な動き（カメラワーク・光・湯気）をつけ、テロップは透明な画像で重ねる | 冒頭2秒の目を止める力に効く部分だけ高価な生成 AI を使う。文字・ロゴ・人の顔は描き足させない。失敗しても静止画で完成させ、その分のクレジットは使わない |
| ⑤ 音 | 音声合成（OpenAI TTS）のナレーション、ライセンス済み BGM（ナレーション中は自動で音量を下げる） | 既存のテンプレート合成と同じ |

- 出力の確認：テロップ・ナレーション・投稿文・特典をブランドの NG ワード・景表法／薬機法の規制表現で確認し、価格（「1,980円」「¥1980」「１９８０円」）は LP に書かれているものだけを許す（AI がつくった価格は使わない）
- 権利：LP の画像を使うときは「自社の LP の画像で SNS の動画に使う権利がある」ことの確認を必須にする。取り込んだ画像はメディアライブラリに残り、取得元の URL を来歴に記録する
- AI の表示：動画は AI ラベル付き（C2PA／IPTC の来歴、TikTok の `is_aigc`、YouTube の `containsSyntheticMedia`）。冒頭を生成 AI で動かした場合も含めて SNS 側で「AI 生成」と表示される
- クレジット：テンプレート合成（20）＋ナレーション（30秒ごとに2）＋冒頭を動かす場合 300。完成後、AI が整理した訴求と投稿文の案を画面に表示する（来歴にも保存）
- 検討したが採用しなかったもの：AI アバター（実在人物風の話者。肖像権・なりすましの懸念と費用）、全編を生成 AI で作る方式（商品の見た目が変わる・1本の費用と時間が大きい）、LP 全体の画面録画（スクロール動画は SNS で離脱されやすい）

## ネタ帳（F-12）

- `/ideas`：季節の行事・記念日の辞書から候補を集め、災害・事件・訃報・政治などの話題を除外（キーワード＋AI の判定の両方）→ AI がブランド・お客様像との関連度で採点 → 上位10件に「おすすめ投稿日・形式・切り口3案」をつけて保存（TrendResearchJob：毎朝6時、テナントのタイムゾーン）
- 「この話題でつくる」／切り口を押すとスタジオのテーマに入る。スタジオの「おまかせ」もネタ帳から提案
- 情報源は `ITrendSource` で追加できる（Web 検索・X 検索 API は従量課金のため、取得件数の上限を決めてから追加する）。競合アカウントの傾向分析は未着手

## 分析・AIレポート（F-10）

- 指標収集（MetricsCollectJob、15分ごと）：公開後 1h／6h／24h／72h／7d／30d に各 SNS のインサイト API から投稿の指標を取得。フォロワー数は1日1回（フォロワー純増＝期間末 − 期間初）
  - X：`GET /2/tweets`（public_metrics・non_public_metrics）／Facebook・Instagram・Threads：`/{id}/insights`／LINE：友だち数のみ
  - 指標名は `Social:Meta:FacebookPostMetrics`・`InstagramMediaMetrics`、`Social:Threads:MediaMetrics` で変更可能（Meta は指標を随時廃止するため）。Meta・Threads の連携スコープにインサイト権限を追加済み（既存の連携は再接続が必要）
- 分析画面（`/analytics`）：期間（今週〜過去90日）・比較（前期間／前年同期）・SNS・キャンペーンで絞り込み（URL に保存して共有可能）。推移・SNS比較・曜日×時間帯ヒートマップ（各グラフに「表で見る」）、投稿ランキング、AI生成と手動の比較、CSV 出力
- AI レポート：数値は C# で確定 → 根拠データ（F1, F2…）と上位・下位投稿の特徴を Analyst に渡す → 各主張が引用した根拠の値と文中の数値が一致するか事後検証し、一致しない主張は表示しない → QuestPDF で PDF 化。クレジットは消費しない
- 定期配信：毎週月曜・毎月1日の 07:00（テナントのタイムゾーン）に作成し、PDF をメール送信。PDF のロゴはクライアント向けに差し替え可能

| 設定キー | 用途 |
|---|---|
| `Reports:FontPath` / `FontFamilies` / `UseSystemFonts` | PDF の日本語フォント。コンテナに日本語フォントがない場合は Noto Sans JP などのフォントファイルを指定（未設定でフォントがないと PDF のみ作成されず、画面のレポートは表示される） |
| `Email:SmtpHost` / `SmtpPort` / `UserName` / `Password` / `From` | レポートのメール配信（未設定時は送信せずログに記録） |

> **ライセンス注意**：PDF は QuestPDF（Community License）を使用しています。年間売上 100万米ドル以上の営利企業は商用ライセンスが必要です。

## 統合受信箱（F-09）

- 取り込み：Webhook（Meta：Facebook ページのコメント・Messenger／Instagram のコメント・メンション・DM、Threads：返信・メンション、LINE：トーク）は署名検証後すぐ 200 を返し、プロセス内キューで取り込む。取りこぼしはポーリング（InboxPollJob：5分ごと、X は従量課金のため15分ごと）で補う。重複はチャネル＋SNS上のIDで排除
- AI 分類（無料）：個人情報（メール・電話番号・郵便番号・カード番号・アカウント名）をマスクしてから、感情・意図（質問／購入／予約／苦情／称賛／スパム）・急ぎ・言語・人が対応すべき話題（苦情・健康・法律）を判定。AI が使えないときはキーワード分類で代替し、キーワードで危険と判断したものは AI が否定しても人に回す。分類は画面で直せる（元の判定は記録）
- 返信案（1クレジット）：FAQ（ブランド設定「よくある質問・受信箱」）と商品情報を参照して最大3案、参照した根拠を表示。案を押すと入力欄に入るだけで送信しない。苦情・健康・法律は案を出さない
- 自動対応（管理者が許可した場合のみ）：スパムの非表示、自動返信を許可した FAQ に一致する質問への自動返信
- SLA（急ぎ1時間・ふつう24時間、変更可）と期限切れ表示、担当の割り当て、「〇〇さんが入力中」、X の返信費用・LINE の通数消費の表示
- 炎上の兆し：直近1時間の否定的な割合が平常時（7日間）の3倍超、または1つの投稿に否定的なコメントが1時間で5件以上 → 赤いアラートと「予約投稿を一時停止」
- Webhook の URL：Meta `https://<ホスト>/api/v1/webhooks/meta`、Threads `/api/v1/webhooks/threads`、LINE `/api/v1/webhooks/line/{チャネルID}`（LINE Developers の Webhook URL に登録）

## キャンペーン・A/Bテスト（F-11）

- キャンペーン（`/campaigns`）：ウィザード（目的 → 期間・予算・UTM コード → 対象SNS → 目標）で作成。KPI（表示回数・反応の割合・クリック・フォロワー増加）の実績と目標達成率はシステムが計算。スタジオで選ぶと投稿のリンクに `utm_campaign` が付く
- A/B テスト：変える要素を1つ選ぶ（書き出し／画像／CTA／投稿時間）→ AI が指定部分だけを変えた B 案をつくる（1クレジット、編集可）→ 配信は「同じSNSで1週間後の同じ曜日・時刻」または「別のSNSで同時」（投稿時間のテストは同じ日の別時刻）。承認が必要なワークスペースでは希望日時付きで承認を依頼
- 判定（1時間ごと）：両方の公開から72時間後の反応の割合を二項比率の検定（有意水準5%）で比較し、「Bの方が反応が 1.4倍（信頼できる差）」のように結論から表示。表示回数が足りなければ「まだ判断できません（あと約2日）」、14日たっても足りなければ判定不能
- 勝ちパターンはブランド設定「お手本」に候補として届き、「お手本に使う」にすると AI が文章をつくるときの参考（Few-shot）にする

## 構成（RF-DES-001 3.4）

```
src/
  ReachForge.Domain/          エンティティ・状態遷移・プラットフォーム制約マスタ・ガードレール・クレジット・分析計算（外部依存なし）
  ReachForge.Application/     ユースケース（スタジオ・承認・予約・配信・チャネル・ダッシュボード）、権限マトリクス、AI/SNS の抽象
  ReachForge.AI/              設定駆動モデルルータ（フェイルオーバー・サーキットブレーカー・計量）、プロンプト、投稿文生成・SNS別変換・承認要約
  ReachForge.Social/          SNS アダプタ（X / Meta / Threads / LINE / TikTok / YouTube ＋デモ接続）
  ReachForge.Infrastructure/  EF Core（SQLite / PostgreSQL）、テナント分離、デモデータ、ジョブ基盤（Hangfire・Service Bus）
  ReachForge.ServiceDefaults/ OpenTelemetry・ヘルスチェック・HTTP 回復性
  ReachForge.Web/             Blazor Web App（MudBlazor 9）＋ Minimal API（/api/v1）
  ReachForge.Worker/          予約配信・定期ジョブ・キューの処理
  ReachForge.Migrations.Sqlite/ SQLite 用マイグレーション（Windows 版）
  ReachForge.Desktop.Host/    Windows 版のサーバー起動・停止・証明書・データフォルダー（OS 非依存）
  ReachForge.Desktop/         Windows 版の画面（WPF ＋ WebView2・タスクトレイ）
tests/
  ReachForge.Domain.Tests / ReachForge.Application.Tests（SQLite＋スタブAI＋モックSNSのE2E）/ ReachForge.AI.Tests
  ReachForge.Social.Tests（SNS アダプタの HTTP 検証）/ ReachForge.Web.Tests（認証・権限・Webhook の結合テスト）
  ReachForge.Desktop.Tests（Windows 版のサーバー起動・自動ログイン）/ ReachForge.E2E.Tests（Playwright・axe）
```

依存方向は Domain ← Application ← (AI / Social / Infrastructure) ← (Web / Worker)。外部 AI・SNS はすべてインタフェース越しに利用します。

Blazor Server の DI スコープはサーキット（タブを開いている間）単位のため、DB を使う画面・部品は `RfComponentBase`（OwningComponentBase）を継承して画面ごとのスコープからサービスを取得し（`Scoped<T>()`）、閉じたときに DbContext を破棄します。サーキット単位の `AppState`・レイアウトは `TenantScopes` で処理ごとに新しいスコープを使います。

## 実装状況（初期リリース範囲）

| 機能 | 状況 | 補足 |
|---|---|---|
| F-01 SNS連携 | ○ | X / Facebook / Instagram / Threads（OAuth）、LINE（チャネル資格情報）、アカウント選択、暗号化保存、自動更新・要再接続、Webhook 署名検証 |
| F-02 ブランド | ○ | 口調・NGワード・必須表記・商品・版管理、AI ブランド診断（URL・紹介文・過去投稿 → 下書き、色抽出、FAQ 登録）、お手本（Few-shot）。資料ファイルの取り込み・埋め込み検索は未着手 |
| F-03 投稿文生成 | ○ | 構造化出力（失敗時1回再生成）・入出力ガードレール・PR表記自動挿入・ブランド適合度採点・クイック修正 |
| F-04 画像 | ○ | ライブラリ、AI 生成（非同期ジョブ・クレジット予約）、SNS 別リサイズ（スマートクロップ／余白／AI 拡張）、代替テキスト、安全性チェック、来歴記録、署名付き配信、全 SNS の画像投稿。カルーセル（IG・Threads・FB）、文字入れ。参照画像の編集（背景差替・不要物除去・商品の配置）、IPTC／C2PA の来歴 |
| F-05 動画 | ○ | テンプレート合成（画像＋テロップ＋AI ナレーション → FFmpeg、字幕 SRT）、生成 AI 動画（Veo・Sora、テキスト／画像から）、BGM（ダッキング）、全 SNS の動画投稿 |
| F-06 マルチSNS変換 | ○ | 9 SNS の制約マスタ、並列変換、自動修正最大2回、UTM 付与、X の URL 費用対策、SNS別プレビュー |
| F-07 承認 | ○ | 状態遷移、承認後編集の再承認、エラー時の承認不可・オーナー例外承認、一括承認、AI確認ポイント要約、期限切れ保留、監査ログ |
| F-08 予約・配信 | ○ | 最適時刻（過去90日・30件未満は既定値）、日次上限、指数バックオフ再試行、二重投稿防止、緊急停止 |
| F-09 受信箱 | ○ | Webhook＋ポーリング取り込み、個人情報マスク、AI 分類（人が修正可）、FAQ 参照の返信案、自動対応（許可制）、SLA、炎上検知と一時停止、担当・入力中表示 |
| F-10 分析 | ○ | 指標収集ジョブ（各 SNS インサイト API）、横断ダッシュボード・グラフ・ヒートマップ、AI レポート（数値の事後検証）、PDF・CSV、週次・月次の定期配信 |
| F-11 キャンペーン・A/B | ○ | キャンペーン（期間・予算・KPI・対象SNS・UTM）、A/B テスト（AI の B 案、ずらし配信／別SNS同時、72時間後の自動判定、勝ちパターンの Few-shot 登録） |
| F-12 ネタ帳 | ○ | イベント辞書、不謹慎な話題の除外、AI 採点・切り口、毎朝の更新、スタジオへの引き継ぎ。Web・X 検索と競合分析は未着手 |
| F-13 利用量 | ○ | クレジットの予約・確定・解放、機能別内訳、消費見込み、X API 費用 |

## 次の段階（設計書との差分）

1. 実アカウントでの SNS 接続確認（各社アプリ審査：Meta App Review など）と、SNS 契約テスト（日次）
2. 実際の Veo・Sora・c2patool（署名証明書）での動作確認
3. 実環境での Hangfire（PostgreSQL）・Service Bus の負荷確認

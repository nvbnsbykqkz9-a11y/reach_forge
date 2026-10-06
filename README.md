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

## 認証（RF-DES-001 9.1 / RF-UX-001 SCR-01・SCR-15）

- ASP.NET Core Identity（Cookie）。5回失敗で15分ロック、エラー文はどちらが違うかを示さない、パスワード貼り付け可
- 2段階認証（認証アプリの TOTP、回復コード）。**オーナー・管理者は必須**（`Auth:RequireMfaForAdmins`）
- 新規登録でテナント・ワークスペース・オーナーを作成。メンバーは招待リンク（7日間有効、トークンはハッシュのみ保存）で参加
- ロールは要求ごとに `WorkspaceMember` から解決するため、権限変更・メンバー削除は即時に反映される
- 外部 ID（Google / Microsoft / Entra External ID）：`Auth:Oidc:<名前>` に Authority・ClientId・ClientSecret を設定すると有効。既存利用者（同じ確認済みメールアドレス）に紐づける
- 監査ログ：ログイン・ログアウト・MFA 変更・招待・ロール変更・チャネル連携

## SNS 公式 API 連携（初期リリース：X / Facebook / Instagram / Threads / LINE）

各 SNS の開発者サイトでアプリを作成し、User Secrets・環境変数・Key Vault で設定します（リポジトリに置かない）。
コールバック URL は `https://<ホスト>/api/v1/oauth/callback/<X|Facebook|Instagram|Threads>` を登録します。

| SNS | 設定キー | 方式 |
|---|---|---|
| X | `Social:X:ClientId` / `ClientSecret` | OAuth 2.0 PKCE（S256）。約2時間で失効するため使用前にリフレッシュ |
| Facebook / Instagram | `Social:Meta:AppId` / `AppSecret`（`GraphVersion`） | Facebook Login for Business → 長期トークン → ページ・IG ビジネスアカウントを選択。Instagram は画像必須（画像の公開 URL を Meta が取得） |
| Threads | `Social:Threads:AppId` / `AppSecret` | 長期トークン（60日）、期限7日前から自動更新 |
| LINE 公式アカウント | （アプリ設定なし） | 画面でチャネル ID・シークレットを入力。ステートレストークンを都度発行。一斉配信は決定的な `X-Line-Retry-Key` で二重配信を防止 |

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

- メディア画面の「ショート動画をつくる」：③ テンプレート合成（選んだ画像＋テロップ＋AI ナレーション、10〜30秒）。実行前に推定クレジット（20＋ナレーション 30秒ごとに2）と所要時間を確認し、非同期ジョブで作成
- 処理：AI が構成台本（シーンごとのテロップ・ナレーション・秒数）→ 各画像を 9:16 にスマートクロップしてテロップを焼き込み → TTS でナレーション（音声の長さに合わせてシーンを延長）→ FFmpeg でゆっくりズームをかけて連結、1080×1920・30fps・H.264/AAC・faststart で書き出し → 字幕（SRT）・サムネイルを作成。AI ラベル付き
- 投稿：Instagram はリール、Threads・Facebook は動画、LINE は動画メッセージ。X は動画の分割アップロードが未対応のため、変換時に動画を外して画像で投稿
- 設定：`Video:FfmpegPath`（既定 `ffmpeg`。コンテナに ffmpeg と日本語フォントを入れる）、`AI:Routes:Tts`（OpenAI の音声合成、`AI:Providers:openai:Voice`）。ローカルは無音のスタブ音声
- 未着手：① テキスト→動画・② 画像→動画（生成 AI 動画モデル）、BGM（ライセンス済み素材の用意が必要）、YouTube への字幕トラック送信（フェーズ2）

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
  ReachForge.Social/          ISocialPublisher アダプタ（現状はモック＋デモ接続）
  ReachForge.Infrastructure/  EF Core（SQLite / PostgreSQL）、テナント分離、デモデータ、配信ディスパッチャ
  ReachForge.ServiceDefaults/ OpenTelemetry・ヘルスチェック・HTTP 回復性
  ReachForge.Web/             Blazor Web App（MudBlazor 9）＋ Minimal API（/api/v1）
  ReachForge.Worker/          予約配信などのバックグラウンド処理
tests/
  ReachForge.Domain.Tests / ReachForge.Application.Tests（SQLite＋スタブAI＋モックSNSのE2E）/ ReachForge.AI.Tests
  ReachForge.Social.Tests（SNS アダプタの HTTP 検証）/ ReachForge.Web.Tests（認証・権限・Webhook の結合テスト）
```

依存方向は Domain ← Application ← (AI / Social / Infrastructure) ← (Web / Worker)。外部 AI・SNS はすべてインタフェース越しに利用します。

## 実装状況（初期リリース範囲）

| 機能 | 状況 | 補足 |
|---|---|---|
| F-01 SNS連携 | ○ | X / Facebook / Instagram / Threads（OAuth）、LINE（チャネル資格情報）、アカウント選択、暗号化保存、自動更新・要再接続、Webhook 署名検証 |
| F-02 ブランド | ○ | 口調・NGワード・必須表記・商品・版管理、AI ブランド診断（URL・紹介文・過去投稿 → 下書き、色抽出、FAQ 登録）、お手本（Few-shot）。資料ファイルの取り込み・埋め込み検索は未着手 |
| F-03 投稿文生成 | ○ | 構造化出力（失敗時1回再生成）・入出力ガードレール・PR表記自動挿入・ブランド適合度採点・クイック修正 |
| F-04 画像 | ○ | ライブラリ、AI 生成（非同期ジョブ・クレジット予約）、SNS 別リサイズ（スマートクロップ／余白／AI 拡張）、代替テキスト、安全性チェック、来歴記録、署名付き配信、全 SNS の画像投稿。カルーセル（IG・Threads・FB）、文字入れ。C2PA・参照画像の編集（背景差替など）は未着手 |
| F-05 動画 | ○ | テンプレート合成（画像＋テロップ＋AI ナレーション → FFmpeg、字幕 SRT）、リール・動画投稿。生成 AI 動画・BGM は未着手 |
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
2. 生成 AI 動画（F-05 ①②）・BGM、C2PA 署名、参照画像の編集（背景差替・不要物除去）、X の動画投稿
3. パスワード再設定・メール送信（招待・ロック通知）、API キー認証（外部連携）、レート制限
4. Hangfire ＋ Service Bus へのジョブ移行（PublishJob / MetricsCollectJob / TokenRefreshJob など）
5. PostgreSQL のマイグレーションと RLS
6. プロンプトのDB管理・AI Evals・SignalR による進捗通知
7. .NET Aspire AppHost、Playwright＋axe-core の E2E / アクセシビリティ自動検査

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
| SNS | モック（実際には投稿しない）。本文に `[[transient]]` / `[[fail]]` を含めると一時的／恒久的エラーを再現 |
| 予約配信 | Web プロセス内で 15 秒ごとに実行（本番は `ReachForge.Worker`） |
| 認証 | 未導入。デモテナントで動作し、右上メニューからロール（オーナー／編集者／承認者／閲覧者）を切り替えて権限を確認可能 |

実際の生成AIを使う場合（User Secrets または環境変数）：

```bash
dotnet user-secrets --project src/ReachForge.Web set "AI:Providers:anthropic:ApiKey" "<key>"
# モデルは appsettings.json の AI:Providers / AI:Routes で設定（コードにハードコードしない）
```

テスト：

```bash
dotnet test   # xUnit v3（Microsoft.Testing.Platform）
```

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
```

依存方向は Domain ← Application ← (AI / Social / Infrastructure) ← (Web / Worker)。外部 AI・SNS はすべてインタフェース越しに利用します。

## 実装状況（初期リリース範囲）

| 機能 | 状況 | 補足 |
|---|---|---|
| F-01 SNS連携 | △ | チャネル管理・プラン上限・重複接続防止・解除時の保留化。OAuth はデモ接続（各SNSの実接続は次段階） |
| F-02 ブランド | ○ | 口調・NGワード・必須表記・商品・版管理。ブランド診断（URL解析・RAG）は未着手 |
| F-03 投稿文生成 | ○ | 構造化出力（失敗時1回再生成）・入出力ガードレール・PR表記自動挿入・ブランド適合度採点・クイック修正 |
| F-04/F-05 画像・動画 | − | 未着手 |
| F-06 マルチSNS変換 | ○ | 9 SNS の制約マスタ、並列変換、自動修正最大2回、UTM 付与、X の URL 費用対策、SNS別プレビュー |
| F-07 承認 | ○ | 状態遷移、承認後編集の再承認、エラー時の承認不可・オーナー例外承認、一括承認、AI確認ポイント要約、期限切れ保留、監査ログ |
| F-08 予約・配信 | ○ | 最適時刻（過去90日・30件未満は既定値）、日次上限、指数バックオフ再試行、二重投稿防止、緊急停止 |
| F-09 受信箱 | − | 画面の枠のみ |
| F-10 分析 | △ | KPI（前期間比）、投稿ランキング、反応の多い時間帯。指標収集ジョブ・AIレポート・PDF は未着手 |
| F-11 A/B | △ | 判定ロジック（二項比率の検定）のみ |
| F-13 利用量 | ○ | クレジットの予約・確定・解放、機能別内訳、消費見込み、X API 費用 |

## 次の段階（設計書との差分）

1. 認証・認可：ASP.NET Core Identity ＋ Entra External ID、MFA、ワークスペース単位の権限
2. SNS 公式 API アダプタ（X / Instagram・Facebook・Threads / LINE）と OAuth（state・PKCE）、Key Vault
3. Hangfire ＋ Service Bus へのジョブ移行（PublishJob / MetricsCollectJob / TokenRefreshJob など）
4. PostgreSQL のマイグレーションと RLS、Redis（キャッシュ・分散ロック）
5. 画像生成（F-04）・プロンプトのDB管理・AI Evals・SignalR による進捗通知
6. .NET Aspire AppHost、Playwright＋axe-core の E2E / アクセシビリティ自動検査

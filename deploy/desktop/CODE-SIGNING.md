# Windows 版のコード署名（Azure Artifact Signing）

インストーラー（`ReachForge-Setup-*.exe`）とアプリ本体（`ReachForge.exe`）に、発行元「株式会社TechnologyFrontier」のデジタル署名を付ける手順です。
署名がないと、利用者が初めて実行するときに Windows SmartScreen の「Windows によって PC が保護されました」が出ます。

## なぜ Azure Artifact Signing か

- 2023年6月以降、コード署名証明書の秘密鍵は HSM（専用の機器）の中に置くことが必須になり、`.pfx` ファイルでは受け取れなくなりました。
  GitHub Actions のような自動ビルドで署名するには、クラウドの HSM で署名するサービスが必要です。
- Azure Artifact Signing（旧 Trusted Signing）は Microsoft のサービスで、証明書の取得・更新・保管を Microsoft が行います。
  Basic プランは月 9.99 ドル（5,000 署名まで）。日本の法人も公開用（Public Trust）の証明書の対象です（申請時に Azure ポータルで確認してください）。

## 1. Azure の準備（会社の管理者が行う）

1. Azure の従量課金（Pay-As-You-Go）のサブスクリプションを用意する（無料試用版・スポンサー付きは不可）。
2. サブスクリプションの「リソースプロバイダー」で `Microsoft.CodeSigning` を登録する。
3. 「Artifact Signing アカウント」を作成する
   - リージョン：東日本（Japan East）→ エンドポイントは `https://jpe.codesigning.azure.net/`
   - 価格レベル：Basic
4. 作成したアカウントで、自分に「Artifact Signing Identity Verifier」ロールを割り当てる。
5. 「ID 検証（Identity validation）」を作成する
   - 種類：Public（公開）／Organization（組織）
   - 法人名・所在地・連絡先などを、登記と同じ表記で入力する（英語表記。証明書の発行元名になる）
   - Microsoft の審査に数日かかることがあります。追加の書類を求められたら提出します。
6. 審査が通ったら「証明書プロファイル」を作成する（種類：Public Trust、上の ID 検証を選ぶ）。

## 2. GitHub から署名できるようにする

1. Microsoft Entra ID で「アプリの登録」を新規に作る（例：`reachforge-github-signing`）。
   「アプリケーション（クライアント）ID」と「ディレクトリ（テナント）ID」を控える。
2. そのアプリの「証明書とシークレット」→「フェデレーション資格情報」→「追加」
   - シナリオ：GitHub Actions でデプロイする Azure リソース
   - 組織：`nvbnsbykqkz9-a11y`、リポジトリ：`reach_forge`
   - エンティティ：**環境（Environment）**、名前：`release`
   （ワークフローは環境 `release` で動くので、この1つでどのブランチ・タグからでも署名できます）
3. Artifact Signing アカウント（または証明書プロファイル）の「アクセス制御（IAM）」で、
   このアプリに「Artifact Signing Certificate Profile Signer」ロールを割り当てる。
4. GitHub のリポジトリの Settings → Secrets and variables → Actions で登録する

   | 種類 | 名前 | 値 |
   |---|---|---|
   | Secret | `AZURE_CLIENT_ID` | 手順1のアプリケーション（クライアント）ID |
   | Secret | `AZURE_TENANT_ID` | 手順1のディレクトリ（テナント）ID |
   | Secret | `AZURE_SUBSCRIPTION_ID` | サブスクリプション ID |
   | Variable | `ARTIFACT_SIGNING_ENDPOINT` | `https://jpe.codesigning.azure.net/`（アカウントのリージョンのもの） |
   | Variable | `ARTIFACT_SIGNING_ACCOUNT` | Artifact Signing アカウントの名前 |
   | Variable | `ARTIFACT_SIGNING_PROFILE` | 証明書プロファイルの名前 |

   変数 `ARTIFACT_SIGNING_ACCOUNT` が空の間は、署名なしでインストーラーをつくります。

5. （任意）Settings → Environments → `release` で、承認者（Required reviewers）を設定すると、署名・リリースの前に承認を求められます。

## 3. 署名したインストーラーをつくる

- コミットのメッセージに `[build installer]` を含めて push する、または `v1.0.0` のようなタグを push する。
- ワークフロー「Windows installer」が、アプリ本体 → インストーラーの順に署名し、署名が有効かを確かめます（無効なら失敗します）。
- 署名には RFC 3161 のタイムスタンプを付けるため、証明書の期限が切れた後も署名は有効なままです。

## 4. 確かめる

インストーラーを右クリック →「プロパティ」→「デジタル署名」タブに、発行元の名前が表示されれば完了です。

## 補足

- SmartScreen の評価は、署名した発行元のダウンロード実績で上がっていきます。配布を始めた直後は、署名があっても警告が出ることがあります。
- Azure Artifact Signing を使えない場合は、認証局のクラウド署名（DigiCert KeyLocker、SSL.com eSigner など）の証明書でも同じことができます。
  その場合はワークフローの署名の手順を、その認証局の GitHub Actions 用の手順に差し替えます。

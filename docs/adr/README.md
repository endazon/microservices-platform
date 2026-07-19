# 実装ADR（Implementation ADR）

本リポジトリ内の意思決定記録（Implementation ADR）の索引である。実装に閉じた技術・設計・運用の決定を `IADR-XXXX` として記録する（必須）。

## 計画ADR との違い

| | 計画ADR | 実装ADR |
| --- | --- | --- |
| 場所 | 計画リポ `projects/<name>/07_adr/` | 本リポ `docs/adr/` |
| ID | `ADR-XXXX` | `IADR-XXXX` |
| 対象 | 上流の意思決定（プロダクト全体） | 実装レベルの意思決定（内部設計・ライブラリ選定等） |

> 計画に影響する決定は、実装ADR に記録するのではなく `/plan-feedback` で計画側へ環流する。

## 運用ルール

- 1 ファイル = 1 意思決定。`IADR-<連番4桁>_<タイトル>.md`（雛形 `docs/templates/adr_template.md`、`/new-spec adr` で採番作成）。
- 連番はリポジトリ内で一意・昇順・欠番なし。
- 状態は `Proposed / Accepted / Deprecated / Superseded`。既存決定を覆す場合は新 IADR を作り、旧 IADR に `Superseded by IADR-XXXX` を追記する。
- 重要な実装判断は必ず IADR に残す（必須）。

## 一覧

| IADR | タイトル | 状態 |
| --- | --- | --- |
| IADR-0000 | 実装意思決定の記録方針 | Accepted |
| IADR-0001 | カタログの正本所有と DocumentNormalized の購読責務 | Accepted |
| IADR-0002 | 取り込みパイプライン構造・冪等チャンク ID・Qdrant ブートストラップ | Accepted |
| IADR-0003 | EFCore.Relational のバージョン直接ピン（MSB3277 解消） | Accepted |
| IADR-0004 | ABAC フィルタの多値 allow-list 化と deny-by-default | Accepted |
| IADR-0005 | 指定データ範囲は ABAC スコープと交差させ権限を広げない（narrowing-only） | Accepted |
| IADR-0006 | ABAC 属性・ポリシー管理の検証と DocumentService 疎結合 | Accepted |
| IADR-0007 | LLM 呼び出し先の切替は設定駆動のエンドポイント定義＋越境マトリクスで行う | Accepted |
| IADR-0008 | 正規化変換はポート分離＋deny-by-default 縮退＋決定的 DocumentId で構成する | Accepted |
| IADR-0009 | Wiki 閲覧の権限外アクセスは 404 で存在秘匿し、ABAC はメモリ内で後段評価する | Accepted |
| IADR-0010 | フィードバックサービスと upsert | Accepted |
| IADR-0011 | ダッシュボードサービスの利用状況集計 | Accepted |
| IADR-0012 | Retrieval /search は Scope 未指定を deny 扱いにし fail-closed で ABAC を強制する | Accepted |
| IADR-0013 | Wiki 閲覧は自前軽量読み取り API を採用し ADR-0011 の Supersede を計画へ提案する | Superseded（by IADR-0020） |
| IADR-0014 | Qdrant の ABAC 属性ペイロードは両表現で復元し、フィルタキー解釈を実機確認する | Accepted |
| IADR-0015 | CI トリガーの develop 整合・コミット規約チェック・CHANGELOG 誤帰属補正 | Accepted |
| IADR-0016 | Microsoft.OpenApi を推移的ピンでパッチ版に固定し NU1903 を解消する | Accepted |
| IADR-0017 | mesh 導入までのサービス間認証はネットワーク分離を第一防御とする | Superseded by IADR-0026 |
| IADR-0018 | 推移依存の脆弱性を CI で定期スキャンする | Accepted |
| IADR-0019 | データソースが原本へ既定 ABAC 属性（機密区分）を付与する | Accepted |
| IADR-0020 | Wiki.js を配備し WikiService を「同期・ABAC ゲートウェイ」へ縮退する（IADR-0013 を Supersede、ADR-0011 に追従） | Accepted |
| IADR-0021 | Wiki.js への同期は GraphQL API push を採用する | Accepted |
| IADR-0022 | 既定モデルを opus 化し、fable-5（最難関）と GitHub Copilot 経路を設定駆動で追加する | Accepted |
| IADR-0023 | 文書の削除・アーカイブを Wiki.js へ伝播する（削除イベント新設＋status 拡張） | Accepted |
| IADR-0024 | MinIO のバケット/キー設計・バージョニング・アクセス制御と共有クライアント | Accepted |
| IADR-0025 | 埋め込みを機密区分ルーティング（Voyage 既定＋高機密セルフホスト fail-closed）とモデル別コレクションで実装する | Accepted |
| IADR-0026 | Istio STRICT mTLS をサービス間認証の第一防御とし、IADR-0017（ネットワーク分離）を解消する | Accepted |
| IADR-0027 | 固定/可変分離のフォルダ・名前空間規約（Foundation / Composable、ADR-0018 対応） | Accepted |
| IADR-0028 | 宣言的パイプライン構成は JSON 単一宣言＋起動時 fail-fast 照合で実現する（FR-14, ADR-0018） | Accepted |
| IADR-0029 | 構成情報 API は BFF 配下の管理 API へ同居させ、自己申告集約＋宣言突合でドリフトを検出する（FR-15, ADR-0018） | Accepted |
| IADR-0030 | 運用者ロールは platform-operator を新設し ConfigViewer ポリシーで判定する（FR-15, SC-11） | Accepted |
| IADR-0031 | 送信者名クレームは preferred_username を Identity.Name に解決する（FR-08, FR-15） | Accepted |
| IADR-0032 | Wiki.js の dev ホスト公開は残し、本番系(Helm)の非公開を回帰ガードで保証する（IADR-0020 追補） | Accepted |
| IADR-0033 | フロントエンド SPA 基盤（React+TS+Vite、foundation/features 分離、Keycloak OIDC、BFF 境界） | Accepted |
| IADR-0034 | フロントエンド カバレッジゲート（単体テストのカバレッジ計測＋ラチェット型しきい値 CI） | Accepted |
| IADR-0035 | フロントエンドのロールベース・ナビゲーションと存在秘匿（SC-09/10/11、realm ロール判定） | Accepted |
| IADR-0036 | SC-11 構成ビューアの可視化方式（グラフ描画ライブラリ非導入、CSS チェーン＋表） | Accepted |
| IADR-0037 | LLM 回答の SSE ストリーミング（egress ゲート保持、SC-01・FR-04/FR-11） | Accepted |
| IADR-0038 | 文書閲覧の BFF 側 ABAC ゲーティングと本文サーバサイド取得（SC-03・FR-06/FR-12） | Accepted |
| IADR-0039 | データソース管理の BFF 集約と管理系画面のロールゲーティング | Accepted |
| IADR-0040 | 管理者設定（ABAC）の BFF 透過中継と AdminOnly ゲーティング | Accepted |
| IADR-0041 | 文書管理（書き込み）の BFF 集約とスコープ内限定・楽観ロック透過 | Accepted |
| IADR-0042 | 変換ジョブ読み取りモデル（インメモリ）と状況照会・人手補正 API | Accepted |
| IADR-0043 | 変換ジョブ読み取りモデルの永続化（Postgres+EF）と非同期ストア | Accepted |
| IADR-0044 | バックエンドサービスの書き込み/管理APIへの認可強制（多層防御） | Accepted |
| IADR-0045 | BFF 文書書き込みのスコープ確認往復は多層防御の要のため現時点で維持し最適化を保留する | Accepted |
| IADR-0046 | 構成バージョン履歴の正データ源は GitOps 層とし、API は注入スライスを surfacing する | Accepted |
| IADR-0047 | 文書の必須属性（機密区分）のサーバー側検証 | Accepted |
| IADR-0048 | バックエンドは .NET 10 / C# 13 を採用する（計画制約「.NET 8」からの乖離） | Accepted |
| IADR-0049 | コンポーザビリティ標準（共通エンベロープ・CI契約テスト・ステージング適用順序）の段階適用と繰延条件 | Accepted |
| IADR-0050 | HPA/PDB の適用対象はステートレス要求処理系に限定し、キュー駆動ワーカーは対象外とする | Accepted |
| IADR-0051 | データソースコネクタのポート分離（Discover/Fetch）と filesystem コネクタ・同期基盤 | Accepted |
| IADR-0052 | 性能負荷試験ツールに k6 を採用する | Accepted |
| IADR-0053 | Wiki コネクタは設定駆動の汎用 REST 契約で実装し、製品固有アダプタは後続とする | Accepted |
| IADR-0054 | SaaS コネクタは設定駆動の汎用 REST 契約＋カーソルページング＋429 バックオフで実装する | Accepted |
| IADR-0055 | 業務DB コネクタは参照専用の設定駆動 SQL（id/updated/content 別名）で「行→文書」化する | Accepted |
| IADR-0056 | リポジトリ最上位のユニット構成（src/&lt;unit&gt;/{backend,frontend} = platform / knowledge） | Accepted |
| IADR-0057 | ユニット依存方向の機械検査は軽量スクリプト（csproj 走査）＋フロント ESLint で行う | Accepted |
| IADR-0058 | planning submodule 配下の破損リンクはトークン付きの定期ジョブで検査する | Accepted |
| IADR-0059 | 契約を階層化しナレッジ固有イベントを Knowledge.Contracts へ分離する（URN は新名前空間から導出・後方互換なし。#227 で URN 固定を撤回） | Accepted |
| IADR-0060 | 追加可変機能ユニットの submodule 運用（CI 自動発見・トークン付き取得・バージョン固定） | Accepted |
| IADR-0061 | デプロイ資産（Helm/k8s/realm/イメージ）の改名は Blue/Green 移行で行う（起草・実行は stg 検証後） | Proposed |
| IADR-0062 | KnowledgePlatform ブランドの .NET 名前空間・アセンブリとフロント package をユニット構成へ改名する | Accepted |
| IADR-0063 | BFF のユニット別エンドポイント合成方式とナレッジ DTO の分離（ビルド時合成点・段階実装） | Accepted |
| IADR-0064 | 単独ビルド用フォールバック props はパスをプロパティへ束ねて MSB4092 を回避し、実ファイル同梱でコピペ事故を防ぐ | Accepted |
| IADR-0065 | public な追加ユニットの CI submodule 取得はトークン不要（src/* のみ非再帰 init）で有効化する | Accepted |
| IADR-0066 | ローカル k8s dev 環境は k3d ＋ dev 専用 in-cluster インフラ資産で構成し、mesh/NP/HPA を無効化する | Accepted |
| IADR-0067 | サービスイメージのビルド検証は compose を単一情報源とする独立ワークフローで行い、集約ジョブを必須チェックにする | Accepted |
| IADR-0068 | k8s-local-images.sh の MAPPING と compose build 定義のドリフトは機械突合スクリプト＋独立ワークフローで検査する | Accepted |
| IADR-0069 | 構成バージョン履歴は現在バージョンと同一注入経路で Helm から env 配列供給する（GitOps 配線・既定空で縮退） | Accepted |
| IADR-0074 | データソース定期同期は Helm の専用 dataSourceSync ブロックで配線し、本番有効・経路B(ローカル k8s)で検証する | Accepted |
| IADR-0075 | AST の KB 書き込みは microservices-platform レルムの専用 confidential client（service-account に platform-operator）で受ける（AST #18・基盤無改修・最小権限・追加のみ） | Accepted |
| IADR-0076 | エッジ /bff/* は Helm の edge ブロック（Istio Gateway/VirtualService・rewrite 無し）で templating し、経路B は values-local で無効化。ブラウザ OIDC issuer は browser/cluster の URL 一致原則（手順A 主・edge.oidc 機構）で統一する | Accepted |
| IADR-0077 | 経路B の可観測性スタック（Prometheus/Loki/Tempo/Grafana）・Vault dev・GitOps(ArgoCD) は deploy/local の opt-in オーバーレイ＋k8s-local-up.sh の env ゲートで追加のみ配線し、既定は現状不変（外部送信なし・平文秘密なし・fail-safe）。Hetzner 実 stand-up は Tier 3（AST #24） | Accepted |
| IADR-0078 | SPA(frontend) は専用 template（templates/frontend.yaml）＋トップレベル frontend values ブロックで k8s 配信し（wikijs/minio と同じ非 .NET パターン）、エッジ VirtualService に SPA catch-all（/bff 等の後・先勝ち）と allow-edge-ingress-to-frontend を追加。#275 ドリフト検査は COMPOSE_ONLY 除外を解消し MAPPING へ載せる（#313） | Accepted |
| IADR-0079 | compose 基盤インフラの永続化: Keycloak は start-dev を維持したまま共有 Postgres（keycloak DB・所有者 kp・kp/kp）へ外部 DB 化して runtime state を永続化、Loki/Tempo は既存 config の storage パス（/tmp/loki・/tmp/tempo）へ名前付きボリューム＋user:0:0（空ボリューム root 所有の書込回帰を回避）。realm 更新は永続後スキップされるため再投入手順を operations に明記。経路B は対象外（#282） | Accepted |
| IADR-0080 | Headlamp（k8s 管理 UI）を dev 専用 raw manifest の opt-in オーバーレイ（`deploy/local/headlamp/`・`HEADLAMP=1` ゲート）で導入し、認証は Keycloak OIDC の token passthrough、RBAC は fail-safe（SA 無権限・`developer` に cluster-admin bind）とする（#271） | Accepted |
| IADR-0081 | frontend base イメージを docker.io 直参照から mirror.gcr.io/library（Google の Docker Hub プルスルーミラー・匿名/チャレンジ無し）へ ARG BASE_REGISTRY で既定差し替え。真因は 401 Bearer チャレンジが Rancher Desktop の破損した資格情報ヘルパ（errorCode 255）を呼ぶことで、public.ecr.aws/ghcr.io も 401 で同様に失敗（実測）。build args を増やさず #275 ドリフト検査は緑・byte 等価（#325） | Accepted |
| IADR-0082 | 経路B（ローカル k8s dev・deploy/local）の基盤インフラ永続化: opt-in kustomize オーバーレイ（deploy/local/infra-persistence・PERSIST=1）で Keycloak=H2-file-on-PVC（/opt/keycloak/data）／Postgres=data-on-PVC（/var/lib/postgresql/data）を local-path で永続化し realm+runtime state/アプリ DB を保持。既定は emptyDir 不変（後方互換・provisioner 不在で Pod Pending 化させない fail-safe）。compose の共有 Postgres 外部 DB 化（IADR-0079）ではなく独立 PVC を採るのは k8s に depends_on:healthcheck 相当が無く起動順結合を避けるため。realm 更新は永続後スキップされるため再投入手順を README/operations に明記（#324） | Accepted 
| IADR-0083 | データソース定期同期の単一書き手化: 本番 HPA（minReplicas 2）で 2 pod が冗長 fetch する問題を、各サイクルで PostgreSQL セッションレベル advisory lock（`pg_try_advisory_lock`・専用接続・固定キー `0x44535053`）を取得したレプリカのみが同期することで解消。非ブロッキングで取得不可（他レプリカ実行中/一時障害）は安全側でスキップし次周期へ（fail-safe）。単一レプリカ（経路B）は常に取得＝従来どおり、非リレーショナル（InMemory）は NoOp コーディネータで従来どおり。k8s Lease/専用 Deployment/CronJob は infra/RBAC 波及のため却下し helm/infra は不変（#328 と非干渉）。API 可用性（minReplicas 2/PDB）不変（#305） | Accepted |
| IADR-0084 | `scripts/k8s-local-up.sh` の k3d `cluster create` に apiserver OIDC 検証フラグ（`--k3s-arg "--kube-apiserver-arg=oidc-...@server:0"`）を opt-in（`HEADLAMP_OIDC_APISERVER`・既定=`HEADLAMP` 追従）で配線。issuer は in-cluster 正準名 `http://keycloak:8080/realms/microservices-platform`（IADR-0076 手順A整合）、claim は #271 の `ClusterRoleBinding`（User=`oidc:developer`）に一致させる `username-claim=preferred_username`＋`username-prefix=oidc:`（groups-claim は bind 先が無く inert のため付けない）。既定オフで `cluster create` はバイト等価、既存クラスタ再利用時は後付け不可のため再作成 WARN。realm/manifest/values 無改変（#328）。〔採番注記〕IADR-0083 は並行 in-flight の #305（datasource）に予約済みのため本 PR は 0084 を採る。欠番はマージ順の一時的なもので #305 マージ後に解消する（#329 の重複採番修正と同様に、並行採番は collision 回避を優先する運用）。 | Accepted ||
| IADR-0085 | セルフホスト埋め込み（Ruri v3 / 768 次元）推論基盤を opt-in 配備物として追加: Helm 専用テンプレート（templates/embedding.yaml・第三者 pull の TEI イメージ・`.Values.embedding.enabled` 既定 false）＋compose の `profiles:["embedding"]` サービス＋deployment.yaml の `selfHostedEmbedding && embedding.enabled` 条件ブロックで llmgateway へ `Embedding__SelfHosted__BaseUrl`／`Endpoints__1__Enabled=true` を注入。既定オフで現行 fail-closed（高機密は索引しない）を byte 等価に維持し、値 1 つで案 A へ移行。第三者 pull・build 無しのため #275／images.yml の検査対象外。実モデル取得・実埋め込み疎通・nDCG@10 実測・Voyage ゼロ保持認定は稼働環境依存＝分離（#303） | Accepted |
| IADR-0086 | backend OIDC 検証を metadata 取得アドレスと issuer 検証値に分離: `AuthExtensions.AddPlatformAuth` に任意キー `Auth:MetadataAddress`（in-cluster well-known＝JWKS 取得先）／`Auth:ValidIssuers`（エッジ host issuer の追加許可リスト・カンマ区切り）を新設し、`MetadataAddress` 設定時は `Authority` と排他で metadata 取得先を分離。chart は `global.auth.metadataAddress`/`validIssuers`（既定 unset）を deployment.yaml へ条件描画。これで CoreDNS/hosts 改変なしに単一エッジ host OIDC（IADR-0076 手順B）が成立。issuer 検証は弱めず（`ValidateIssuer=true`・JWKS は in-cluster metadata 由来・許可リストを足すのみ）、既定（新キー未設定）は backend/chart ともバイト等価（#314・#284 follow-up。実ブラウザ疎通=live） | Accepted |
| IADR-0087 | `scripts/k8s-local-up.sh` の opt-in ゲート（`HEADLAMP_OIDC_APISERVER`／`PERSIST`／`OBSERVABILITY`／`VAULT`／`ARGOCD`／`HEADLAMP`）を横断で固定する smoke test を **bash stub-on-PATH（スクリプト無改変）** で追加。外部バイナリ（`k3d`/`kubectl`/`helm`/`docker`）を PATH 上の記録スタブへ差し替え、副作用ゼロで `k8s-local-up.sh` を実行し発行コマンド列へアサートする（`scripts/k8s-local-up.test.js`・Node 標準 `assert` のみ・`ci.yml` に独立ジョブ）。既定オフで `k3d cluster create` がバイト等価・opt-in 由来リソースが不在であること、各フラグ ON で該当リソース/引数（apiserver OIDC 4 フラグ・issuer/client override・`=0` escape・kustomize 切替）が現れることを検証。sourceable 関数抽出／plan モード（スクリプト改変案）は後退リスクのため不採用（#334）。〔採番注記〕IADR-0086 は並行 in-flight の #314（backend OIDC）に予約済みのため本 PR は 0087 を採る。 | Accepted |

> **索引 backfill に関する注記**: 本 PR は既存債務（0039–0046 未掲載）の解消と併せて索引を欠番なしに揃える。
> 実体ファイルの所在は **0047＝PR #211（マージ済）／0050＝PR #213（マージ済）／0048・0049＝本 PR**。#211・#213 は
> 既に develop へマージ済みで対応ファイルが存在するため、本 PR マージ後の索引に不整合は残らない（#211/#213 は
> README を編集しないため索引更新の競合も生じない）。

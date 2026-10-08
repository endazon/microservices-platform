---
title: 運用仕様書
type: operations-spec
status: in-progress
created: 2026-07-04
updated: 2026-10-09
author: claude
---
<!-- trace:
ids: [FR-01, FR-02, FR-03, FR-04, FR-05, FR-10, FR-11, FR-13, FR-15, FR-16, SC-12, NFR-02, NFR-05, NFR-09, NFR-13, NFR-18, NFR-21, SC-01, SC-02, SC-10, SC-15, SC-22, UC-01, UC-04, UC-05, UC-07, FR-09, SC-17, FR-19, SC-09, FR-06]
adrs: [ADR-0107, ADR-0112, ADR-0125, ADR-0084, ADR-0124, ADR-0080, ADR-0122, ADR-0005, ADR-0006, ADR-0007, ADR-0008, ADR-0009, ADR-0011, ADR-0016, ADR-0017, ADR-0026, ADR-0030, ADR-0038, ADR-0040, ADR-0042, ADR-0044, ADR-0071, ADR-0072, ADR-0076, ADR-0078, ADR-0079, ADR-0085, ADR-0095, ADR-0106, ADR-0111, ADR-0115, ADR-0074, ADR-0097, ADR-0113, ADR-0118, ADR-0116, ADR-0121, ADR-0036, ADR-0127, ADR-0013, ADR-0027, ADR-0123]
iadrs: [IADR-0516, IADR-0514, IADR-0513, IADR-0424, IADR-0504, IADR-0503, IADR-0502, IADR-0500, IADR-0492, IADR-0489, IADR-0486, IADR-0485, IADR-0484, IADR-0483, IADR-0482, IADR-0481, IADR-0002, IADR-0009, IADR-0013, IADR-0017, IADR-0020, IADR-0021, IADR-0023, IADR-0025, IADR-0026, IADR-0028, IADR-0029, IADR-0032, IADR-0046, IADR-0049, IADR-0050, IADR-0051, IADR-0066, IADR-0069, IADR-0074, IADR-0076, IADR-0079, IADR-0080, IADR-0081, IADR-0082, IADR-0085, IADR-0088, IADR-0104, IADR-0110, IADR-0112, IADR-0149, IADR-0165, IADR-0168, IADR-0210, IADR-0225, IADR-0248, IADR-0265, IADR-0284, IADR-0294, IADR-0304, IADR-0313, IADR-0318, IADR-0322, IADR-0327, IADR-0339, IADR-0345, IADR-0354, IADR-0367, IADR-0369, IADR-0370, IADR-0374, IADR-0377, IADR-0378, IADR-0382, IADR-0404, IADR-0420, IADR-0422, IADR-0432, IADR-0433, IADR-0453, IADR-0461, IADR-0466, IADR-0471, IADR-0472, IADR-0473, IADR-0470, IADR-0477, IADR-0480, IADR-0497, IADR-0498]
specs: [20261009_1818_sc12-idp-drift-detection, 20261009_1814_base-and-testcontainers-digest, 20261008_1822_infra-image-redeploy-window, 20261008_1787_infra-audit-digest-pin, 20261008_1811_ast-llmgw-ingress-netpol, 20261008_1756_ast-kb-ingress-netpol, 20261006_1764_voyage-key-wiring, 20261006_1762_republish-document-updated, 20261006_1760_qdrant-keyword-indexes, 20261006_1696_lift-kb-reader-prod-hold, 20261006_1755_ast-kb-reader-confidentiality-cap, 20261004_issue-1472_sc22-t40-live-procedure, 20261002_issue-1696_ast-kb-read-policy, 20261001_1709_backup-image-build-credential-helper, 20261001_issue-1709_backup-suspended-status, 20260928_issue-1683_vault-audit-to-observability, 20260928_issue-1682_paired-secrets-outside-sc22, 20260928_issue-1667_ast-stale-copies-enumeration, 20260928_issue-1676_adr0121-audit-followups, 20260928_issue-1615_content-abac-document-reads, 20260927_issue-1666_sc09-dynamic-binding-conditions, 20260927_issue-1665_owner-read-policy-guard-and-content-abac-gate, 20260927_issue-1617_t25-chance-red-rerun-and-monthly-summary, 20260927_issue-1605_checker-residual-precision, 20260926_issue-1595_grafana-check6-yaml-and-emptiness, 20260926_issue-1588_grafana-rule-verify-and-workflow-read-scopes, 20260926_1577_grafana-filter-evaluator-never-fires, 20260926_issue-1550_live-script-opt-in, 20260926_1544_reset-floor-zero-endpoint-alert, 20260926_deployment-name-population-scan, 20260926_issue-1435_wikijs-recreate-strategy, 20260925_1422_k8s-local-down-teardown-order, 20260925_458_secret-rotation-runbook, 20260911_issue-1411_sc22-console-fallback-and-bff-vault-write, 20260914_issue-1411_sc22-secret-injection-screen, 20260904_issue-1159_mesh-mtls-declaration-as-single-writer, 20260904_issue-1198_usage-event-subject-and-retention, 20260904_issue-1202_absent-series-slo-alerts, 20260905_issue-1203_analysis-ask-absent-companion, 20260905_issue-1203_synthetic-monitoring-marker-and-exclusion, 20260905_issue-1215_search-collection-gate, 20260906_issue-1245_nearby-mta-relay, 20260909_issue-1287_synthetic-monitor-launcher-gate, 20260909_issue-336_ndcg-harness-and-query-embedding-profile, 20260925_1499_object-storage-seaweedfs, 20260926_1543_reset-floor-replicas-pdb, 20260926_issue-1111_llm-budget-alert-configurable, 20260926_issue-1560_platform-infra-encrypted-backup, 20260926_issue-1557_department-domain-validation, 20260926_issue-1573_department-attribute-follows-group, 20260927_issue-1609_department-clear-and-dictionary-from-realm, 20260927_issue-1610_sc17-department-edits-group-membership, 20260927_issue-1664_owner-read-policy-seed-and-deploy-step, 20261006_1746_claude-rerank]
issues: [#1818, #1822, #1787, #1814, #1811, #1756, #1764, #1762, #1760, #1755, #1746, #1472, #1696, #1709, #1683, #1682, #1667, #1676, #1615, #1666, #1665, #1664, #1609, #1610, #1617, #1597, #1605, #1595, #1588, #1577, #1550, #1544, #1558, #1435, #1560, #1111, #1543, #1499, #1422, #458, #1088, #1108, #1110, #1159, #1411, #1198, #1202, #1203, #1204, #1215, #1233, #1245, #1287, #124, #144, #145, #192, #196, #197, #198, #207, #271, #299, #303, #320, #324, #325, #336, #395, #438, #443, #455, #466, #532, #536, #546, #587, #66, #665, #674, #863, #88, #98, #992, #1557, #1573, planning#196, planning#524, planning#538, AST#346, planning#672, AST#1078, planning#712, planning#750]
-->

# 運用仕様書

## 目次

- [いつ読むか](#いつ読むか)
- [起点となる計画書（トレーサビリティ）](#起点となる計画書トレーサビリティ)
- [デプロイ](#デプロイ)
- [可用性・水平スケール（HPA / PDB）（NFR / #197）](#可用性水平スケールhpa--pdbnfr--197)
- [監視・アラート（NFR / #198）](#監視アラートnfr--198)
- [データ保持期間（利用イベント）](#データ保持期間利用イベント)
- [バックアップ・リストア（NFR / #198）](#バックアップリストアnfr--198)
- [障害対応（Runbook）（NFR / #198）](#障害対応runbooknfr--198)
- [定期点検（年次）](#定期点検年次)
- [未決事項](#未決事項)

> 必須ドキュメント（リポジトリ単位）。本リポジトリの運用を定める。雛形は `docs/templates/operations_spec_template.md`。
> **未記入のまま放置しない**。デプロイ・監視・バックアップ・障害対応を埋めること。

## いつ読むか

| 読む場面 | 節 |
| --- | --- |
| 初回デプロイ・サービス単位のロールバック手順を知りたい | §デプロイ |
| 同じタグで再デプロイしても新イメージが反映されない | §イメージ参照と再デプロイ安全性 |
| Keycloak realm を編集したのに反映されない | §Keycloak realm（`microservices-platform-realm.json`）を更新したときの反映手順 |
| ローカル k8s（経路B）でデータを永続化したい | §経路B（ローカル k8s dev）の永続化 |
| Wiki.js の初期セットアップ・OIDC 連携をしたい | §Wiki.js の起動・初期セットアップ・ヘルスチェック |
| データソース定期同期を有効化・監視したい | §データソース定期同期の有効化と監視 |
| 埋め込みプロバイダのゼロ保持・fail-closed 挙動を確認したい | §埋め込みプロバイダの設定・ゼロ保持・再索引 |
| 経路B の LLM ゲートウェイへ埋め込み（Voyage）の鍵を入れる・届いたか確かめる | [`voyage-embedding-key-runbook.md`](voyage-embedding-key-runbook.md) |
| HPA/PDB でスケール・可用性を確保したい | §可用性・水平スケール |
| アラートが実際にどこへ届くか（未配線の現状）を確認したい | §監視・アラート |
| 利用イベントがいつ消えるか・消えていないときの見方を知りたい | §データ保持期間（利用イベント） |
| 検索が全件 0 件になる（応答は 200 のまま）理由を切り分けたい | §検索が全件 0 件になる（読み書き先コレクションの乖離・全文索引の欠落） |
| 本番へ所有者の読み取りのポリシーを投入する・投入済みか確かめる | §所有者の読み取りのポリシーの投入 |
| 取引ユニットの KB の読み手のポリシーを投入する・投入済みか確かめる | §AST の KB の読み手のポリシーの投入 |
| 障害発生時の一次対応を知りたい | §障害対応（Runbook） |

---

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR・運用/可用性）: 運用・保守（障害検出 5 分以内・MTTR 30 分以内・アラート/Runbook 整備）、
  可用性 99.9%、スケーラビリティ（HPA で水平スケール）、独立デプロイ。計画: `02_requirements/01_requirements.md`、
  技術検討 `06_technical/05_observability-ops.md`。
- 関連 ADR / 技術検討: 可観測性（OTel/Prometheus/Loki/Tempo）／ CI/CD GitOps（ArgoCD + Helm）／
  ランタイム（k3s）／ サービスメッシュと STRICT mTLS ／ Wiki エンジンと Wiki.js 配備。
  実装 ADR: 起動時 fail-fast ／ 構成ドリフト検出。

## デプロイ

| 項目 | 内容 |
| --- | --- |
| 環境 | dev（docker-compose） / stg・prod（k3s + Istio + ArgoCD） |
| 実行基盤 | k3s。Helm チャート `deploy/helm/microservices-platform`。Namespace `microservices-platform`（Istio 注入有効） |
| 配備方式 | GitOps。ArgoCD が Git を単一の真実源として同期（`deploy/argocd/`）。レジストリは Harbor（`harbor.internal`） |
| サービス間通信 | Istio STRICT mTLS。手順 `deploy/istio/README.md` |
| 手順 | ① Secret 投入（`deploy/bootstrap/README.md`）② Istio 導入（`deploy/istio/README.md`）③ ArgoCD 登録（`deploy/argocd/README.md`）。以降は Git 更新で自動同期 |
| デプロイ（サービス単位） | `values.yaml` の `services.<name>.tag` を Git 更新 → ArgoCD 自動同期（NFR: 独立デプロイ） |
| ロールバック | `argocd app rollback microservices-platform <revision>` もしくは Git revert（GitOps 原則） |

### イメージ参照と再デプロイ安全性（非機能要件: 運用性/信頼性/再現性 / #320）

配布物であるコンテナイメージは、**浮動タグ（`:latest` 等）＋ `imagePullPolicy: IfNotPresent`**
の組合せだと、同名タグで再ビルド・再 push しても既存 Pod/Node のキャッシュにより再 pull されず
**古いイメージが配信され続ける**。区分ごとに次の方針で再デプロイの確実性を担保する。

#### 自製イメージ（`services.*`・`frontend`）— CD が一意タグ/digest を渡す

- chart 既定の `services.<name>.tag: latest` は **CD 上書き用のプレースホルダ**である。本番デプロイでは
  **一意タグ（git SHA 等）または digest（`@sha256:...`）** を渡すこと。一意タグ/digest なら pod template が
  毎回変わって自動 rollout され、`IfNotPresent` でも新イメージが pull される（stale を掴まない）。

  ```bash
  # 例: BFF を現在の HEAD の短縮 SHA で独立デプロイする（他サービスは据え置き）
  argocd app set microservices-platform \
    --helm-set services.bff.tag=$(git rev-parse --short HEAD)
  # values-<env>.yaml の services.bff.tag を更新して Git commit でも可（GitOps 原則）
  ```

- `imagePullPolicy` は `global.image.pullPolicy` で **per-env に `Always` へ上書き可能**。ただし
  **既定は `IfNotPresent` のまま**にする（既定を `Always` にしない）:
  - 経路B（ローカル k3d）は `registry: k3d-local` の**擬似レジストリ**で、`Always` にすると存在しない
    レジストリへ pull しにいき **Pod が起動不能**になる（local import ＋ `IfNotPresent` が前提。
    `scripts/k8s-local-images.sh`）。
  - 本番も毎回 registry へ問い合わせる pull 負荷が増える。**一意タグ運用なら `Always` は不要**。

- **同名タグを再利用せざるを得ない場合**（ローカル再ビルド・緊急ホットフィックス等）は、キャッシュ済み
  Pod を明示的に入れ替える:

  ```bash
  # ローカル: イメージ再ビルド/import 後に Pod を作り直して新イメージを反映
  bash scripts/k8s-local-images.sh --live && kubectl -n microservices-platform rollout restart deployment/frontend-service
  ```

  宣言（`pipeline.json`）変更時は pod template の `checksum/pipeline-config` アノテーション
  （`templates/deployment.yaml`）が変わり自動 rollout されるが、これは**イメージ更新は捕捉しない**。
  イメージ更新は上記の一意タグ運用（推奨）か `rollout restart` で行う。

#### Third-party イメージ — 具体版タグ ＋ digest で固定（インフラ製品の選定基準の計画 ADR / #1787）

- 依存イメージ（keycloak/postgres/redis/rabbitmq/qdrant/seaweedfs/otel/prometheus/loki/tempo/grafana/
  wiki 等）は **`<repo>:<tag>@sha256:<digest>`** で固定する（`docker-compose.yml`・`deploy/local/`・`deploy/mail-relay/`・
  helm `values.yaml`）。digest は multi-arch の **image index（manifest list）** のもので、tag は人が版を読むために残す。
  helm values の `registry` / `image` / `tag` 形式は同じマッピングに `digest:` を置き、テンプレートが `tag@digest` を描く。
- **自製イメージの基底イメージ**（`src/` の Dockerfile の `FROM`。`mcr.microsoft.com/dotnet/{sdk,aspnet}`・frontend の
  `node`・`caddy`）と**統合試験の Testcontainers のイメージ**（`PostgreSqlBuilder("…")`・`RabbitMqBuilder("…")`・
  `QdrantTestImage.Reference`・`SeaweedFsContainer.Image`）も同じ表記で固定する。自製イメージそのものは上の「自製イメージ」の節の
  とおり対象外のままで、固定するのはそれが載る上流の基底イメージである。統合試験の `postgres:16-alpine` と frontend の
  `node:22-alpine` は配備と同じ digest にする（試験と配備で中身を違えない）。
- **tag だけの参照の再混入は CI が止める**（`node scripts/check-image-digests.js`。`static-checks` ジョブ）。走査するのは
  `deploy/` の YAML・Containerfile と、`src/` の Containerfile・C#（submodule の `src/ai-stock-trading` は別リポジトリなので除く）。
  Containerfile は `FROM` に加えて `COPY --from=<外部イメージ>`・`RUN --mount=…,from=<外部イメージ>` を、helm テンプレートは
  `image:` 行に直書きした `default "<参照>"` を拾う。
  同じ `repo:tag` が別の digest で書かれている（compose と k8s の片側だけ、配備と試験の片側だけを更新した）ときも落ちる。
  digest を付けられない参照は `scripts/image-digest-exceptions.json` に理由つきで載せる（2026-10-08 時点で 0 件）。
- 🔴 **digest で固定すると、浮動 tag（`7-alpine`・`2.5`・`22-alpine` 等）の自動パッチは効かない。** 以前は
  「minor 固定・patch は許容」（Wiki.js の `2.5` は `2.5.x` 系列の自動パッチを受ける。実測 PoC は `2.5.314`＝
  `docs/tech/20260707_wikijs-poc-record.md`）としていたが、**同じ tag の中身が差し替わっても検知できない**ため改めた。
  パッチ（`-alpine` のセキュリティ修正を含む）は、年次点検と契機ごとに digest を解決し直して取り込む
  （手順は本書 [§インフラ製品の点検（基準 A〜D）](#インフラ製品の点検基準-ad選定基準の計画-adr--1787) の「digest の解決と更新」）。
  **基底イメージ（`dotnet/aspnet`・`caddy` 等）はアプリの実行環境の修正を運ぶ**ので、上流のセキュリティ修正の告知
  （.NET の月例のサービシングリリース等）も契機に入れる（下の点検節の契機⑤。告知の検知は人が行う。自動の検知は無い）。
- **Harbor へのミラーは Harbor の配備後に行う**（未配備のあいだは対象外。上流のレジストリから digest で取得する）。
- 🔴 **インフラのイメージ参照（tag・digest）を変える配備は、市場の場中を避けて引け後に行う。**参照が変わると
  platform-infra の Postgres・RabbitMQ・Keycloak 等の Pod が作り直され、その間は上に載る全サービス（AST を含む）の
  DB 接続・認証・メッセージングが切れる。2026-10-08 の配備（場中）では AST 全サービスで DB 接続が切れ、ヘルスチェックの
  エラー（`57P01`）と現在値の補充のエラーが出た（一過性で回復）。digest の解決し直し（年次点検・契機）も同じ扱いとする。
  基底イメージと Testcontainers のイメージの digest の更新はこれに当たらない（前者は自製イメージの次のビルドと
  通常のロールアウトで入り、platform-infra を作り直さない。後者は CI と手元の試験だけで使う）。

### 基盤インフラの永続化（compose・非機能要件: 運用性/可観測性/信頼性 / Keycloak=共有 Postgres／Loki・Tempo=名前付きボリューム） / #282）

`deploy/docker-compose.yml` のステートフル infra はすべて名前付きボリューム等で永続化し、`docker compose down`
（`-v` なし）→ `up -d` を跨いで状態を保持する。#282 で欠落していた 3 サービスを補完した。

| サービス | 永続化先 | 保持される状態 |
| --- | --- | --- |
| Keycloak | 共有 Postgres の `keycloak` DB（`postgres-data` 上・所有者 `kp`） | realm 実行時変更（ユーザー・パスワード・クライアントシークレット・セッション・同意 等） |
| Loki | `loki-data`（`/tmp/loki`＝config `path_prefix` と一致） | 蓄積ログ（index/chunks） |
| Tempo | `tempo-data`（`/tmp/tempo`＝config `local.path`/`wal.path` の親） | 蓄積トレース（blocks/wal） |

- **Keycloak の外部 DB 化**: `start-dev` を維持したまま `KC_DB=postgres`（`KC_DB_URL_HOST=postgres` /
  `KC_DB_URL_DATABASE=keycloak` / `KC_DB_USERNAME=KC_DB_PASSWORD=kp`）で H2 を置換する。`keycloak` DB は
  `create-multiple-dbs.sh` が作成（所有者 `kp`）。`KC_HOSTNAME_URL`（issuer 固定・#88）・healthcheck・`--import-realm` は不変。
- **Loki/Tempo を root 実行にする理由**: 空の名前付きボリュームは root 所有で生成されるため、非 root イメージ
  （uid 10001）でも storage 配下に書き込めるよう `user: "0:0"` を付与している（dev/staging compose 限定。compose 永続化の実装 ADR §3）。

> **⚠️ 既存 dev 環境の移行注記**: `create-multiple-dbs.sh` は `/docker-entrypoint-initdb.d/` で **Postgres データ
> ディレクトリが空の初回起動時のみ** 実行される。**既に `postgres-data` ボリュームが存在する環境**では本 PR を
> pull しても `keycloak` DB が自動作成されず、Keycloak が接続先 DB 不在で起動失敗する。次のいずれかで移行する:
> - **A（dev データを作り直してよい・簡単）**: `docker compose -f deploy/docker-compose.yml down -v && up -d`
>   （全ボリューム削除・init 再実行。全 dev データが消える）。
> - **B（既存データを保持・非破壊）**: 稼働中の Postgres に `keycloak` DB だけ手動作成してから Keycloak を再作成:
>   ```bash
>   docker compose -f deploy/docker-compose.yml exec -T postgres \
>     psql -U postgres -c 'CREATE DATABASE keycloak OWNER kp;'
>   docker compose -f deploy/docker-compose.yml up -d keycloak
>   ```
> 新規（クリーン）環境では init が走るため追加操作は不要。

#### ⚠️ Keycloak realm（`microservices-platform-realm.json`）を更新したときの反映手順

外部 DB 永続化により、`--import-realm` は **既存 realm をスキップ**する（default: 上書きしない）。H2 時代は毎回
`up` で realm が再 import されていたが、**永続化後は `realm.json` を編集しても自動反映されない**。これは
runtime state 保持（本 issue の目的）と realm 定義の再現性のトレードオフで、後者は次の手順で担保する。

1. **開発中に realm 定義を作り直してよい場合（推奨・破壊的）**: keycloak DB を落として再 import させる。
   ```bash
   docker compose -f deploy/docker-compose.yml rm -sf keycloak
   docker compose -f deploy/docker-compose.yml exec -T postgres \
     psql -U postgres -c 'DROP DATABASE IF EXISTS keycloak WITH (FORCE);' -c 'CREATE DATABASE keycloak OWNER kp;'
   docker compose -f deploy/docker-compose.yml up -d keycloak   # --import-realm が最新 realm.json を再投入
   ```
   実行時変更（ユーザー等）は失われる。realm 定義（クライアント・ロール・マッパー）は `realm.json` が単一の真実源
   （バックアップ・リストア節と整合）。
2. **runtime state を保持したまま部分反映したい場合（非破壊）**: 管理コンソール（`http://localhost:8080`・admin/admin）
   または `kcadm` の partial-import で当該変更のみ適用する。
   ```bash
   docker compose -f deploy/docker-compose.yml exec keycloak \
     /opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 \
       --realm master --user admin --password admin
   # 例: 追加/変更したクライアントのみ partial import（realmPartialImport エンドポイント / 管理 UI「Partial import」）
   ```

#### 永続化の確認（要 docker daemon）

```bash
docker compose -f deploy/docker-compose.yml up -d
# 管理コンソールで検証用ユーザーを追加し、Grafana で Loki/Tempo にデータが出ることを確認
docker compose -f deploy/docker-compose.yml down          # -v は付けない（付けるとボリュームも削除）
docker compose -f deploy/docker-compose.yml up -d
# 追加ユーザーが残存し、Loki/Tempo の過去データが参照できることを確認
```

> ローカル k8s dev 環境（`deploy/local/`＝経路B）は「k3d ＋ dev 専用 in-cluster インフラ資産で構成する」という
> 割り切りで infra が既定 `emptyDir`（Pod 再起動で再 init）であり、本節の compose 永続化とは別レイヤ。経路B の
> 恒久化（Keycloak realm/runtime state の保持）は #324（opt-in オーバーレイで Keycloak/Postgres を local-path PVC 化する実装 ADR）
> で **opt-in（`PERSIST=1`）** を追加し、#1088 で**既定オン**へ返した（下記「経路B の永続化」節）。

#### 経路B（ローカル k8s dev）の永続化（既定オン・非機能要件: 運用性 / #324、経路B の Qdrant／可観測性 4 種の永続化と Prometheus 保持期間 / #787、既定化と realm の後追い / #1088）

`bash scripts/k8s-local-up.sh --live` は**既定で** [`deploy/local/infra-persistence`](../../deploy/local/infra-persistence/)
オーバーレイを適用し、**Keycloak（`/opt/keycloak/data`＝`start-dev` の file H2）・Postgres
（`/var/lib/postgresql/data`）・Qdrant（`/qdrant/storage`）を `local-path` PVC で永続化**する。realm + runtime state
（追加ユーザー・シークレット・セッション）・全アプリ DB・コレクション/ベクトルが Pod 再起動でも保持される。
**`OBSERVABILITY=1` を併用**すると [`deploy/local/observability-persistence`](../../deploy/local/observability-persistence/)
が素の観測 overlay を**置換**し、**Prometheus（`/prometheus`）・Loki（`/tmp/loki`）・Tempo（`/tmp/tempo`）・
Grafana（`/var/lib/grafana`）**も永続化される（マウント先は各 config の storage パスと一致させ、config は書き換えない）。
**opt-out は `PERSIST=0`（使い捨てスタック専用）。StorageClass `local-path` が無ければ起動器は止まる**（黙って emptyDir へは
落とさない —— 稼働 dev クラスタが誰にも気付かれず非永続で立っていたのが #1088 である）。
**rabbitmq/redis/otel は emptyDir 継続**（queue/cache は揮発前提・otel は stateless。**qdrant は #787 で永続化対象へ移った**）。

- **Prometheus の保持期間**は `--storage.tsdb.retention.time=35d` / `--storage.tsdb.retention.size=4GB` を
  args で明示する（[`deploy/local/observability/prometheus.yaml`](../../deploy/local/observability/prometheus.yaml) の base
  ＝ `PERSIST` の有無に関わらず効く）。**`size` を PVC 容量（5Gi）未満に置いてあるため、流入が増えても
  PVC 満杯で書き込み不能になることはない**（経路 B の永続化の実装 ADR の決定 3）。compose にも同じ 2 引数がある（パリティ）。
- **Pod は root へ落とさない**（4 種とも `securityContext` を付けない）。compose の `user: "0:0"`
  （compose 永続化の実装 ADR §3）は **docker の named volume が root:root 0755 で生成される**ことへの対処であり、
  **k8s へは転用できない** —— local-path provisioner は `mkdir -m 0777` でボリュームディレクトリを作る。
  実測（2026-08-16・稼働中の k3s）で loki（uid 10001）/ tempo（uid 10001）/ grafana（uid 472）が
  非 root のまま PVC へ書き、4 件とも再起動 0 回で Ready だった（同実装 ADR の決定 6）。
- **PVC を掴む Deployment は `strategy: Recreate`** になる（postgres / keycloak / qdrant ＋ 可観測性 4 種の
  計 7 件）。`ReadWriteOnce` と `RollingUpdate` は両立せず、local-path では**アプリのロックで詰まる**
  （Prometheus は `storage.tsdb.no-lockfile=false`・再起動後に `lock` 実在を実測）。同実装 ADR の決定 7。
  **helm チャートの Deployment も同じ**である（PVC を掴む seaweedfs / wiki-js）。wiki-js を RollingUpdate のまま
  残すと、新旧 2 つの Pod が空の DB へ migration を同時に走らせ、knex のロック表に行が 2 つでき、
  以後どの Pod も「Migration table is already locked」で起動しなくなる。チャートの全テンプレートを走査する
  検査が `scripts/k8s-local-up.test.js` にある（対象を名前で列挙しない）。
- **⚠️ PVC の要求容量は縮小できない。** 小さくして既存クラスタへ再 apply すると API サーバが拒否する
  （実測: `spec.resources.requests.storage: Forbidden: field can not be less than status.capacity`）。
- **★ 稼働クラスタで受け入れ済み**（2026-08-16・#787）。**PR #816 を書いた環境には `kubectl`/`helm`/
  `k3d`/`kustomize` が無く測れなかった**が、同じ #787 を並走実装した **PR #815 の環境（稼働中の k3s）で実測し、
  PR #819 で書き戻した**。実測: **PVC 7 本すべて `Bound`** ／ **strategy 7 件すべて `Recreate`** ／
  Qdrant のコレクションが **Qdrant 再起動後も残存** ／ Prometheus の `numSeries` が再起動前後で **8564 のまま**
  （`/prometheus/data` に `chunks_head` / `wal` / `lock` が残存）。
  配備先が変わったら `kubectl -n platform-infra get pvc`（有効にしたゲートの PVC が**すべて** Bound であること）と
  `curl prometheus:9090/api/v1/status/runtimeinfo` の `storageRetention` で同じ確認を行うこと。

- **realm 更新の反映は自動**（#1088）: 永続化後は `--import-realm` が既存 realm をスキップするため、`realm.json` の編集は
  import では届かない。`k8s-local-up.sh` は [7/7] の後に
  [`deploy/local/keycloak-setup/reconcile-realm.sh`](../../deploy/local/keycloak-setup/README.md) を呼び、
  **宣言と稼働 realm の差分を Job（Admin REST API）で当てる**。**realm JSON を変えたら up を再実行すれば届く**
  （単独実行も可・冪等）。届いているかは `node scripts/check-stack-ready.js --live` の **G9** が見る。
  **既存の人間の利用者は触らない**（実行時が正）。**［2026-09-06］送出先は宣言が正になった** ——
  クラスタ内の中継を指す固定値であり、稼働側で消えても外を向いても、次の適用で宣言へ戻る。
  **パスワードリセットの申請の開閉だけは条件つきで例外**である（機械の門が閉じたと記録している間は開き直さない。
  逆向き —— 宣言では閉じているのに稼働で開いている —— は従来どおり差分として直す）。
  seed 利用者の宣言を変えるときだけ **破壊経路**
  （`kubectl -n platform-infra delete pvc keycloak-data && kubectl -n platform-infra rollout restart deploy/keycloak`）を使う。
  **Keycloak pod で `kcadm.sh` を exec しない**（本体が OOMKilled になる）。
- **非永続で立っていた環境の移行**: up を再実行すると初回は空 PVC のため realm/DB は再生成される（既存 emptyDir データは
  元々揮発）。手順の全文は [`deploy/local/README.md`](../../deploy/local/README.md) の「永続化」節を参照。
  非永続で立っていることは `check-stack-ready.js` の **G10** が赤くし、稼働イメージが宣言（chart / kustomize の描画結果）と
  違うことは **G11** が赤くする。
- **保持範囲**: 保持されるのは Pod の再起動/再作成まで。`scripts/k8s-local-down.sh --apply` はクラスタ／`platform-infra`
  ほかアプリの namespace を削除するため PVC も消える（`down`→`up` では realm/DB は再生成）。PVC を残すなら `down` を使わず
  Pod のみ再作成する。**既定（引数なし）は `--dry-run`** で、消す予定のものを表示するだけで何も変えない。

### Headlamp（k8s 管理 UI・dev opt-in）（非機能要件: 運用性 / #271）

ローカル k8s dev（経路B。k3d ＋ dev 専用 in-cluster インフラ資産で構成する）に [Headlamp](https://headlamp.dev/)
（CNCF Sandbox の k8s 管理 UI）を **opt-in** で導入し、Pod / Deployment / Service / ログ等をブラウザから閲覧・
操作できる。認証は既存 Keycloak（OIDC）に一元化し、`developer` / `Developer-2026` を流用する（新規資格情報を作らない）。
初回ログインでは TOTP の登録画面が挟まる（多要素認証を必須にしたため）。
本番像（`deploy/helm` / `deploy/argocd` / compose）は不変で、資産は `deploy/local/headlamp/`（dev 専用）に閉じる。

- **有効化**: `HEADLAMP=1 bash scripts/k8s-local-up.sh --live`（既定オフ・fail-safe）。`deploy/local/headlamp` を適用し、
  OIDC client secret を Secret `headlamp-oidc`（`platform-infra`・dev 既定＝realm import の dev 値・`HEADLAMP_OIDC_CLIENT_SECRET`
  で上書き可）へ作成する。UI 到達は `kubectl -n platform-infra port-forward svc/headlamp 4466:80`（http://localhost:4466）。
- **realm client**: `deploy/keycloak/microservices-platform-realm.json` の client `headlamp`（confidential）が単一情報源。
  経路B の Keycloak は永続化が既定で realm が残るため、realm client の変更は起動器の後段（realm の後追い Job）が
  差分として当てる（上記「経路B の永続化」の realm 更新の反映）。
- **認証モデル / RBAC**: OIDC token passthrough（Headlamp が利用者 id_token を API server へ委譲）。fail-safe として
  Headlamp の ServiceAccount には広域権限を与えず、OIDC ログイン無しではクラスタ可視化不可。`developer` の OIDC
  アイデンティティ `oidc:developer` に `cluster-admin` を bind する（`headlamp-developer-cluster-admin`）。
- **ブラウザ OIDC 到達性 / live 前提**: issuer 到達性は、エッジ `/bff/*` ルーティングとブラウザ OIDC issuer 統一を定めた実装 ADR の
  手順A（hosts＋port-forward で `http://keycloak:8080` を共有）で解く。加えて **k8s API server の OIDC 検証フラグ**
  （`--oidc-issuer-url` 等をクラスタ (再)作成時に付与）が実ログイン・リソース閲覧の前提（稼働 k3d 依存＝live）。
  手順の全文は [`deploy/local/README.md`](../../deploy/local/README.md) の「Headlamp」節を参照。
- **本番導入は非スコープ**（本書の範囲では dev のみ）: 公開範囲・アクセス制御・RBAC 設計が別問題のため、まず dev で確立し本番導入は別 issue／
  計画フィードバック（`projects/microservices-platform/10_feedback/20260719_headlamp-k8s-management-ui.md`）で論点化する。
  - **［2026-08-04 追記］計画側で方針が起案された**——
    運用管理 UI の本番導入を扱う計画 ADR
    「Kubernetes 管理 UI を本番へ導入し、**内部限定（VPN／踏み台経由）・閲覧専用**で公開する」
    （計画リポジトリ `d980a01`。状態 `Proposed`）。
    k8s 管理 UI の選定を扱う計画 ADR（`Proposed`）と対で読む。
    **本書の記述は dev の手順として有効**であり、本番導入の作業は当該 ADR が `Accepted` になってから
    別 issue で行う（本 PR の範囲外）。

### サービス構成に関する運用注記

- **WikiService と Wiki.js**（Wiki 閲覧の要求・ユースケース。Wiki.js を配備し `WikiService` を「同期・ABAC ゲートウェイ」へ縮退する、
  Wiki.js への同期は GraphQL API push を採用する、という実装 ADR による）:
  閲覧・編集 UI の実体は **Wiki.js**（`ghcr.io/requarks/wiki:2.5`、専用 DB `wikijs`）が担う。`WikiService` は
  「**同期・統合・ABAC ゲートウェイ**」に責務を縮退する。認可（ABAC）は本システムが単一の真実源であり、
  WikiService が Wiki.js の**前段**で deny-by-default の属性フィルタと 404 存在秘匿を強制する。
  Wiki.js 側のページ/グループ権限は補助的な表示制御に留める。
  （旧来の「Wiki.js 非配備・自前閲覧 API」の判断は Issue #66 の (a) 選択により Superseded。）
  - **ネットワーク分離**: Wiki.js への ABAC は WikiService ゲートウェイに集約するため、共有/stg/prod では
    Wiki.js を host 公開せず、到達を WikiService 経由に限定する（ネットワーク分離。k8s の Ingress 無効・NetworkPolicy）。
    **dev の compose は管理 UI セットアップ便宜のため 3001 を公開する（dev ホスト公開は残し、本番系〔Helm〕の非公開を回帰ガードで保証する・#124）**が、
    **本番系（Helm）は `wikijs.ingress.enabled: false` で公開しない**。
    「本番系構成では 3001（ゲートウェイ迂回の外部到達）が公開されない」ことは `NetworkIsolationTests`
    （Helm `wikijs.ingress.enabled: false` の検証＋dev 公開が wiki-js に限定され他内部サービスへ波及しないこと）が回帰ガードする。
  - **段階導入（現状）**: 段1（配備・OIDC 構成・意思決定記録）に続き、**段2（本 PR）で実コードを実装**した ──
    `DocumentSyncConsumer` を Wiki.js への **GraphQL push 同期**へ置換し、`/wiki/pages` 系を
    Wiki.js 前段の**認可プロキシ**へ改修（ABAC 通過時のみ Wiki.js 本文をプロキシ）。`wiki_svc` は同期メタデータに
    限定した。フォロー作業（Issue #88）は**完了**: 稼働 Wiki.js での GraphQL PoC 実測・OIDC ローカルログイン
    無効化の稼働検証は [PoC 実測記録](../tech/20260707_wikijs-poc-record.md)、API キーの発行/投入手順は
    後述「Wiki.js 同期シークレットの発行・投入」を参照。削除・アーカイブの同期経路は
    文書の削除・アーカイブを Wiki.js へ伝播する実装 ADR で実装済みである。

### データソース定期同期の有効化と監視（データソースのカタログ化・登録および同期のユースケース・非機能要件 / #299）

DataSourceService の定期同期ワーカー `DataSourceSyncHostedService`（コネクタのポート分離と同期基盤の実装 ADR）は **既定無効**で、有効化は
config（Helm values）で行う。同期ユースケースの基本フロー「システムが定期的に原本を取得」と非機能要件「文書更新後 15 分以内に
検索結果へ反映」の実現手段。

- **有効化（本番）**: `deploy/helm/microservices-platform/values.yaml` の `services.datasource.dataSourceSync` で
  既定有効（`enabled: true` / `intervalSeconds: 300`）。`deployment.yaml` が env `DataSourceSync__Enabled` /
  `DataSourceSync__IntervalSeconds` を描画する（ASP.NET の `__`→`:` 規約で `DataSourceSyncOptions` へバインド）。
- **間隔の根拠（300 秒＝5 分）**: 反映総遅延 = 検出遅延（≤ 間隔）＋ 下流パイプライン遅延（fetch→convert→ingest→
  index）。間隔 300 秒で検出 ≤5 分・下流に ≥10 分の予算を残し NFR 15 分を余裕充足する。実効間隔はワーカーが
  最短 30 秒へ丸める（過負荷防止）。下流実測後に調整可。
- **経路B（ローカル k8s / k3d）**: `deploy/local/values-local.yaml` で明示有効化＋間隔 60 秒（反映確認を高速化。
  本番像は不変）。`scaling.enabled=false`＝replicas 1 で多重実行なし。active データソース／実ファイル共有が無い
  環境では sync 対象ゼロで安全に空回りする（fail-safe。実データ疎通の live 部分は別手順・実コネクタと SMB/NFS
  マウント前提）。**compose（dev）は既定無効のまま**（挙動不変。手動 `POST /datasources/{id}/sync` のみ）。
- **ロールバック**: `--set services.datasource.dataSourceSync.enabled=false`（もしくは values 差戻し）で即無効化。
  手動同期エンドポイントは常に有効で影響しない。
- **fail-safe（挙動保証。同実装 ADR）**: 増分 watermark（`LastSyncedAt`）は**完全成功時のみ**前進し、discover
  失敗・一部 fetch 失敗では進めず次回再試行する（欠落防止）。1 サイクルの例外で停止しない。未対応 SourceType・
  未構成ストレージは縮退する。重複発行（多重実行時）は決定的 DocumentId により下流が冪等 upsert する。
- **監視（継続失敗アラート。同期の例外フロー）**: 同じデータソースが連続 3 回以上同期に失敗すると、構造化ログに
  **継続失敗アラート（`Alert=true`）**を出す（`DataSourceSyncService.AlertThreshold`）。監視スタック（本書
  「監視・アラート」）の Loki クエリ／ログベースアラートで `Alert=true` を拾って通知経路へ接続する。
- **多重実行の注記**: 本番 HPA（`scaling`）で datasource は minReplicas 2 のため 2 pod が同時に sync ループを
  回すが、上記の下流冪等性により**不整合は生じない**（原本 fetch は冗長になる）。冗長排除（単一書き手化）は
  フォローアップ issue で対応する。

### 利用者の部門属性を部門グループへ合わせる同期の有効化（利用者属性の割当の要求 / #1573）

認可サービスの定期処理 `DepartmentAttributeSyncHostedService` は、ABAC が読む利用者属性 `department` を部門グループ
（`/department/<コード>`）の所属へ合わせる。**既定無効**（`Off`）で、有効化は構成で行う。**helm values・compose には既定値を置いていない**
（＝デプロイしただけでは稼働 realm に何も起きない）。

- **構成**: env `DepartmentAttributeSync__Mode`（`Off` / `Report` / `Fix`）と `DepartmentAttributeSync__Interval`（既定 `01:00:00`）を
  authorization-service へ与える。値域外は起動時に落ちる（打ち間違いを黙って無効にしない）。
  🔴 **周期は `hh:mm:ss` 形式で `00:01:00`〜`23:59:59` だけを受け付ける**。`60` や `24:00:00` は起動時に落ちる
  （.NET の既定の解釈ではそれぞれ 60 日・24 日になるため受け付けない。1 日以上の周期は使えない）。
- **段階的な適用（稼働 realm。AST の PoC と共有）**:
  1. `Report` で起動し、ログの「部門の同期（Report）」行で食い違いと未解決（複数所属）の件数・対象（IdP 内部 ID）を確かめる。**書き込みは起きない。**
  2. 食い違いがグループ側の誤りなら**グループ所属を直す**（属性ではない）。属性側の誤りなら `Fix` へ切り替える。
  3. 🔴 **`Fix` へ切り替えたら、まず試験利用者 1 人で書けることを確かめる。** 部門グループに 1 つだけ属し、属性 `department` を
     わざと別の値にした試験利用者を用意し、1 周後にその人の属性がグループのコードへ直ったこと（ログ「直した」・計器 `corrected`）を見る。
     「見送り（変更あり）」になり直らない場合は、所属者の一覧（`/groups/{id}/members`）と利用者の個別取得（`/users/{id}`）で
     属性の見え方が違う realm であり、**同期は全員を見送り続けて誰も直さない**（周期の結末 `all_skipped_changed`・Warning ログ）。
     その場合は `Off` に戻して報告する。
  4. 以後、属性はグループのコードへ直る。2 周目以降の「直した」は 0 件になる（冪等）。
- **書くもの**: 部門グループにちょうど 1 つ属する利用者の属性 `department`（グループのコードへ直す）と、部門グループに 1 つも属さない利用者の
  属性 `department`（消す。下の「部門グループから外された利用者」）だけ。グループ所属・ロール・realm の構成・マッパー・
  クライアント・secret には触れない。サービスアカウント（AST のクライアントを含む）の属性は書かない。
  **realm の reconcile Job は変えていない。**
- 🔴 **利用者アカウント管理画面の操作との競合（残る窓）**: Keycloak の利用者更新は表現全体の置き換え（PUT）で、条件付き更新が無い。
  同期は書く直前に利用者をもう一度読み、**計画を立てたときから有効状態か部門以外の属性が変わっていれば、その人への書き込みを見送る**
  （ログ「見送り（変更あり）」・計器の `skipped_changed`。次の周期で読み直す）。それでも**その読み直しから PUT までの 1 往復の間**に
  管理画面の無効化（有効状態と保持起点の属性の書き込み）が入ると、同期の PUT がそれを古い値で上書きし得る。**この窓はゼロにできない。**
  影響を避けたい作業（利用者の無効化を多数行う等）の間は `Off` にしておくか、作業後に対象者の状態を確かめる。
- **失敗の見え方**: 1 人の書き込みが失敗しても周期は止まらず、他の人は直る。失敗した人は IdP 内部 ID つきでエラーログに出て、
  周期のまとめ行が Warning になり、計器 `department_sync.users.total{department_sync.outcome="failed"}`（周期の結末は
  `department_sync.cycles.total`）が増える。周期ごと中断したとき（部門グループの木が読めない等）は `aborted`、
  直そうとした全員が見送られたときは `all_skipped_changed` として数える。
  **アラート `DepartmentSyncNotCorrecting`（warning）** が、直近 1 時間に `failed` の利用者・`aborted` / `all_skipped_changed` の周期・
  全利用者の列挙の未完了（`department_sync.enumeration_incomplete.total`）のいずれかがあれば鳴る（同期が `Off` のときは系列が無く鳴らない）。
- **利用者アカウント管理画面の部門欄**: ［2026-09-27 改訂］画面の部門欄は**部門グループの所属を変える**（選んだ部門グループへ入れ、ほかの部門グループから外す。
  「部門なし」はすべてから外す）。**属性 `department` は画面から書かない。** 属性は `Fix` の同期が次の周期で新しい部門グループへ追随させる
  （画面は追随するまで「属性は部門の同期で追随します（未反映）」と示す）。従前の「画面で変えた部門が次の周期で戻る」「部門グループに 1 つも属さない
  利用者に画面で部門を付けても消える」は起きなくなった（画面が部門グループそのものを変えるため）。
  - 🔴 **`Off` / `Report` の環境では、画面で部門を変えても認可に使う属性は変わらない**（同期が追随させないため）。その環境で部門を認可へ反映させるには
    `Fix` を有効にするか、属性を別の手段でそろえる。画面の「未反映」の表示が残り続けるのはこの状態である。
  - 部門グループが 2 つ以上の利用者は画面から変えられない（理由を示す）。管理コンソールで所属を 1 つにしてから変える。
  - 所属の変更は画面から先に入れてから外す。途中で失敗したときは元に戻し、戻せなかったときも部門グループ 0 個にはせず、画面にいまの所属を示す
    （サービスのエラーログ「部門グループの変更が途中で失敗し、元に戻せなかった」に IdP 内部 ID が出る）。管理コンソールで所属を確かめる。
  - 書く主体は同期と同じ機密クライアント（`manage-users` の範囲。権限は増やしていない）。所属の変更は認可基盤の管理イベントに残る。
- **部門グループから外された利用者**: ［2026-09-27 改訂］どの部門グループにも属さなくなった利用者の属性 `department` は、`Fix` の同期が**消す**
  （部門なし ＝ その部門の資料が見えなくなる側に倒れる）。`Report` はログ「部門グループに 1 つも属さないのに属性 department を持つ」を出すだけで書かない。
  **外したときに属性を手で消す作業は、`Fix` で動かす環境では要らない**（次の周期で消える。`Off` / `Report` の環境では従前どおり手で消す）。
  - 🔴 **消すのは全利用者の列挙を最後まで読めた周期だけ**である。列挙がページの途中で失敗した・上限（10 万人）で打ち切られた周期は、
    **誰の属性も消さない**（読めなかった人を「部門グループに属さない」と推定しない）。その周期は Error ログ「全利用者の列挙が途中で失敗した／打ち切られた」、
    計器 `department_sync.enumeration_incomplete.total{department_sync.reason="page_failed" | "truncated"}`、周期のまとめ行の Warning
    （「全利用者の列挙 未完了（消去なし）」）で分かる。部門グループにちょうど 1 つ属する人の是正はその周期も続く。
  - 消す直前にその人の所属を個別に読み直し、部門グループが見つかれば消さずに見送る（ログ「見送り（変更あり）」・計器 `skipped_changed`）。
  - **2 個以上の部門グループに属する利用者は、今回も触らない**（ログ「未解決（複数所属）」に出る。どれに合わせるかは決めていない）。
  - **サービスアカウント（`service-account-` で始まる利用者）は消さない。**
  - 消した人数は計器 `department_sync.users.total{department_sync.outcome="cleared"}` とログ「属性 department を消した」（IdP 内部 ID つき）で分かる。
  - 🔴 **有効化の前に `Report` で「部門グループに 1 つも属さない」の件数と対象を確かめる。** 部門グループを組む前の realm で `Fix` にすると、
    部門グループに入っていない人の部門が一斉に消える（その部門の資料が見えなくなる）。
- **属性辞書の部門の値**: ［2026-09-27］管理者設定画面の属性辞書の `department`（利用者・文書の両方）の許可値は、realm の部門グループ
  （`/department/<コード>`）から導かれる。**部門を足す・消すときは realm の部門グループを変える**（画面からは足せない。手で値を足す登録・更新は 400）。
  realm を読めないときは最後に確かめた値のまま「不明（realm を読めないため最後に確かめた値）」と表示され、値は消えない。
  seed が入れていた `finance` / `legal` は realm に無いため辞書から消える。これらを条件に持つポリシーは評価は変わらないが、保存し直すと 400 になる
  （配備後に管理者設定画面でポリシーの条件を確かめる）。
- **ロールバック**: env を外す（または `Off`）。既に直した属性・消した属性は戻らない（直した値はグループのコードそのもの。消した人は部門グループへ入れれば次の周期で戻る）。
- **多重実行**: 複数レプリカが同じ処理を回すが、書く値はグループから決まるので結果は同じである（書き込みが重複するだけ）。
### データソースの明示部門の値域検証 —— 配備順（データソース登録のユースケース / #1557）

データソース管理画面・API で部門を明示した登録・更新は、DataSourceService が書き込み時に認可サービスへ
「この値は部門グループ（`/department` 直下）のコードか」を east-west gRPC で照会する（値域の外は 400、照会できなければ 502）。

- 🔴 **配備順: authorization-service を datasource-service より先に（または同時に）上げる。** 照会先の口は新しい認可サービスにしか無い。
  datasource-service だけを先に上げると、照会は UNIMPLEMENTED になり、**部門を明示した書き込みが 502 で保存されない**
  （安全側だが、管理者からは登録・更新が失敗して見える）。部門が空・予約値 `unassigned` の書き込みと、部門を変えない更新は照会しないので影響を受けない。
- **照会の締切は 5 秒**。Keycloak が応答しないときも 5 秒で 502 になる（画面は固まらない）。
- **gRPC 宛先（`Services__AuthorizationServiceGrpc`）を持たない配備**では照会できない扱いになり、部門を明示した書き込み（部門を変える更新を含む）は 502 になる。
- **realm への作業は要らない**（認可サービスの既存の機密クライアントの権限でグループを読める）。
- **既存データ**: 値域が定まる前に保存された部門（部門グループに無いコード）は、部門を変えない限りそのまま残り、他の項目の編集も妨げない。部門を変えるときだけ検証される。

### 所有者の読み取りのポリシーの投入（本番の配備の手順。ABAC の要求）

**所有者の読み取りのポリシーは本番でも必須である。** 認可サービスは組み込みの「所有者は自分の文書を読める」判定を持たず、
ポリシー 1 件ごとに許可の分岐を 1 本作る。このポリシーが無い環境では、所有者が**共有していない自分の個人資料**を
開けない（文書閲覧が 404 になる）。開発環境（経路B）では ABAC の初期投入（`deploy/local/abac-seed/policies.json`・
`node scripts/seed-abac-policies.js --live`）が入れる。**本番では配備の手順としてシステム管理者が投入する。**

- **形（これ以外にしない）**: 動作は `read`、**利用者の条件なし**、文書の条件は `owner` が `${current_user}` に一致すること**だけ**。
  `${current_user}` は認可サービスが判定のたびに利用者名へ置き換える。
  - 🔴 **文書の条件を空にしない。** 文書の条件が空のポリシーは「全件許可」として読まれる（全利用者が全文書を読める）。
  - 🔴 **利用者の条件を足さない。** 取扱区分（`clearance`）などで絞ると、区分を持たない所有者（機械の書き手など）が自分の文書を読めなくなる。
  - 🔴 **文書の条件へ他の属性を足さない。** 所有者の分岐が他の軸との組み合わせになり、区分の割り当ての判定（無人アカウントへ配れる区分）が前提とする形から外れる。
- **投入の口**: **管理者設定画面（ABAC）から作る**のが既定である。画面とポリシーの API は同じ口で、同じ検証を通る。
  - **画面の手順**: ポリシー定義タブの「ポリシーを追加」で、名前を入れる → 対象アクション「閲覧」→ 対象属性「所有者（文書）」→
    条件の値「動的束縛: 操作する利用者本人（`${current_user}`）」→「条件を追加」→「検証」で「矛盾なし」を確かめる →「保存」。
    利用者の属性は条件に足さない（上の形のとおり）。値は選択肢から選ぶだけで、自由入力の欄は無い。
  - 画面を使えないとき（配備の自動化・画面の不調）は、下の API へ同じ本文を直接投入してよい。
  - 認可サービスの管理 API: `POST /authz/policies`（`platform-admin` ロールを持つ主体のアクセストークン。クラスタの内側から叩く。
    例: `kubectl -n microservices-platform port-forward svc/authorization-service 18091:8080`）。
  - 同じ API は BFF の `POST /bff/admin/authz/policies` からも届く（管理者設定画面が使う中継。管理者のセッションと CSRF ヘッダが要る）。
- **API で投入するときの手順**:
  1. 先に検証だけを行う（保存しない）: `POST /authz/policies/validate` に下の本文を送り、検証の誤りが無いことを確かめる。
  2. 投入する: `POST /authz/policies` に同じ本文を送る。

     ```json
     {
       "name": "所有者は自分の文書を読める",
       "action": "read",
       "userConditions": {},
       "documentConditions": { "owner": ["${current_user}"] }
     }
     ```

  3. 投入済みかを確かめる（下）。
- **投入済みかの確かめ方**: `GET /authz/policies`（または管理者設定画面のポリシー一覧）で、次をすべて満たすポリシーが**ちょうど 1 件**あること。
  **名前では判定しない**（名前は管理者が変えられる）。
  - `action` が `read`、`isActive` が `true`
  - `userConditions` が空
  - `documentConditions` のキーが `owner` だけで、値が `["${current_user}"]` だけ
  - 2 件以上あっても許可は変わらないが、消すときに片方を残し忘れる原因になるので 1 件にそろえる。
  - 実際の効き目は、共有していない個人資料を 1 件持つ利用者が、その資料を文書閲覧で開けることで確かめる（開けなければ 404）。
- **消したとき・無効にしたときの影響**: **所有者が、共有していない自分の個人資料を開けなくなる**（404）。
  自分の組織文書のうち取扱区分の外にあるものも読めなくなり、検索・グラフ・Wiki からも自分の文書が消える。
  誤りとしては表に出ない（読めないだけで、エラーにはならない）。削除そのものは止めていないので、消す前に影響を確かめる。
  **削除・無効化は検知して知らせる**（下の「消えたときの検知と通知」）。
- **配備の順番**: このポリシーの投入は、文書サービスで内容の ABAC を有効にする作業の**1 番目**である。
  内容の ABAC はまだ有効にしない（投入しても文書サービス自身の判定は変わらない。効くのは境界層・検索・グラフ・Wiki の判定である）。
  続く作業は、古い写しの削除（[手順](ast-stale-copies-deletion-runbook.md)）→ 消えたときの検知と通知（配備済み。下）→ 内容の ABAC の有効化（下の門を通る）の順で、
  それぞれの手順が揃ってから行う。**検知と通知は、内容の ABAC を有効にするより前に働いていなければならない。**
- 🔴 **予約値と同じ名前の利用者を認証基盤（Keycloak）に作らない**: `system`（取り込みで所有者を解決できなかった文書の所有者の値・
  外部システムの古い写しの所有者の値）と `anonymous`（未認証の要求の身元）。その名前の利用者が居ると、このポリシーでその利用者が
  **予約値を所有者に持つ文書をすべて読める**。名簿に居ない名前は判定で拒否される（許可へは倒れない）ので、作らない限り問題にならない。
  開発用の realm にこれらの利用者が居ないことは回帰試験が確かめる。
- **切り戻し**: ポリシーを削除する（`DELETE /authz/policies/{id}`）か無効にする（`PATCH /authz/policies/{id}/active`）。影響は上のとおり。
  消すと下の警報が鳴る（切り戻しの意図であっても鳴る。鳴り止ませるには投入し直す）。

#### 消えたときの検知と通知

- **検査**: 認可サービスが**起動時に 1 回と、以後 1 分ごと**に、上の形のポリシーの有効な件数を数える（上の「投入済みかの確かめ方」と
  同じ条件。名前では判定しない）。**構成で有効にする必要は無い**（常に働く）。周期は `OwnerReadPolicyCheck__Interval`
  （`hh:mm:ss`・`00:01:00`〜`23:59:59`。値域外は起動時に落ちる）で変えられる。
- **計器**: ゲージ `authz_owner_read_policy_active`（直近の検査で数えた件数。**0 が「無い」**）。ポリシーの表を読めなかったときと
  起動直後の未検査のときは、**系列を出さない**（古い値も 0 も出さない）。検査の結末は
  `authz_owner_read_policy_checks_total{authz_owner_read_policy_outcome="present" | "absent" | "failed"}`。
- **警報**（下の「監視・アラート」と同じ経路。Alertmanager と Grafana の Alerting 画面）:
  - `OwnerReadPolicyMissing`（critical）: 件数が 1 未満のまま 5 分。**消えてから鳴るまで最大およそ 6 分**（検査の周期 ＋ 5 分）。
  - `OwnerReadPolicyCheckSeriesAbsent`（warning）: ゲージの系列が無い（**見ていない**）。認可サービスが止まっている、ポリシーの表を
    読めない状態が続いている、または収集が欠けている。このあいだ `OwnerReadPolicyMissing` は鳴らない。
    **「数えられない」（表を読めない失敗）が続き始めてから鳴るまで、最大およそ 11 分**が目安である —— 失敗した検査で系列が止まるまで
    検査の周期（最大 1 分）、止まった系列が Prometheus の瞬間ベクタの lookback（約 5 分。remote write で入る系列は止まった時点では消えず、
    最後の値から 5 分たって初めて「無い」になる）、そこへ `for: 5m` が積まる。周期を延ばせば、その分だけ遅れる。
- **ログ**: 無いあいだは検査のたびに認可サービスが Error「所有者の読み取りのポリシーが有効な状態で 1 件も無い」を出す。
  戻ったら Information「所有者の読み取りのポリシーが戻った」。
- **鳴ったときの対応**: 上の「投入済みかの確かめ方」で状態を確かめ、無ければ「手順」で投入し直す（無効なら有効へ戻す）。
  🔴 **誰が消したかは、ポリシーの API が記録していない**（削除・無効化の口は監査の記録を持たない）。管理者の間で確かめる。

#### 内容の ABAC の有効化の門（文書サービス）

- **構成**: `ContentAbac__Mode`（`Off` 既定 / `On`）。値域外は起動時に落ちる。
- **門**: `On` にしても、文書サービスが**認可サービスで上の形のポリシーが 1 件以上あることを確かめるまで、内容の ABAC は有効にならない**。
  無い・数えられない（認可サービスの gRPC 宛先 `Services__AuthorizationServiceGrpc` が無い・届かない・時間切れ）ときは閉じたままで、
  文書サービスが 1 分ごとに確かめ直し、投入されれば開く。**1 度開いたら、その実行の間は閉じない**（開いた後に消えたことは上の警報が知らせる）。
  再起動したときは改めて確かめる。
- **見え方**: ゲージ `documents_content_abac_gate_open`（1 = 開・0 = 閉）の属性 `documents_content_abac_gate_state`
  （`disabled` / `not_evaluated` / `owner_read_policy_absent` / `owner_read_policy_unknown` / `open`）と、閉じている理由の Warning ログ。
- **門が開いたときに変わること**: 文書サービスの読み取り（一覧・詳細・版・絞り込みとページング。REST と gRPC）が、認可サービスの読み取りの許可だけで
  判定されるようになる。機械の呼び出し元（外部システムのサービスアカウントを含む）も、取扱区分の外の組織文書を読めなくなり、属性を持たなければ
  自分が所有する文書だけを読める。所有者・共有先の判定も認可サービスへ問うので、**認可サービスが止まると読み取りはすべて空・404 になる**
  （自分の個人資料を含む）。閉じている間は従前どおり（組織文書は認証済みの全主体に返り、個人資料は所有者と共有先に返る）。
- 🔴 **`On` にするのは、古い写しの削除（所有者が `system`・欠落の外部システムの写しの列挙と削除、または切替後の 0 件の確認）を済ませてから**である。
  門が確かめるのは所有者の読み取りのポリシーだけで、古い写しの有無は確かめない。先に `On` にすると、古い写しが外部システムから見えなくなり、
  写しが重複して作り直される。
  **列挙・削除・0 件の確認の手順は [古い写しの Runbook](ast-stale-copies-deletion-runbook.md) にある**（列挙は文書サービスの管理者だけの読み取りの口
  `GET /documents/ast-stale-copies`。消すのは管理者の手作業）。
- 🔴 **配備順: authorization-service を document-service より先に（または同時に）上げる。** 門が問う口は新しい認可サービスにしか無い。
  逆順でも門は `owner_read_policy_unknown` で閉じたまま（安全側）である。

### MCP クライアント登録簿と認証基盤の照合（食い違いの検知と通知）（MCP サーバーの要求 / #1818）

無人（サービスアカウント）の MCP クライアントの属性は、**認証基盤（Keycloak）のサービスアカウントの属性が正**であり、
MCP クライアント登録管理の画面の登録簿はその写しである（判定は認可サービスが認証基盤から引き直した値で行う）。
登録・差し替えは「検証 → 認証基盤 → 登録簿」の順で書くが、認証基盤の管理画面での直接の操作・取り消しの失敗・
2 つの差し替えの交差（行の排他は無い）で両者はずれ得る。**ずれを定期の照合で検知して知らせる。直しはしない。**

- **照合**: MCP サーバーが**起動時に 1 回と、以後 1 分ごと**に、登録簿の無人の行と認証基盤を突き合わせる。
  **構成で有効にする必要は無い**（常に働く）。周期は `McpClientProvisioning__Reconciliation__Interval`
  （`hh:mm:ss`・`00:01:00`〜`23:59:59`。値域外は起動時に落ちる）で変えられる。helm・compose には値を置いていない（コードの既定で回る）。
  - 1 回の照合は、認証基盤のクライアントの一覧（100 件ごとに 1 要求。最後の頁が 100 件に満たないと分かるまで読むので、クライアント数 n に対し ⌊n/100⌋＋1 要求。1 万件以上なら読み切らずに失敗）と、
    入口の印のあるクライアントの行ごとに 1 要求（認可サービスと同じ利用者名の完全一致の照会。同時に 4 要求まで）を送る。
    各要求の期限は書き込みと同じ `McpClientProvisioning__Keycloak__TimeoutSeconds`（既定 10 秒）、**1 回の照合の期限は周期と同じ長さ**
    （超えたら失敗として数える）。管理用の資格情報は書き込みと同じ `mcp-client-admin` である。
  - 有人の行は比べない（認証基盤へ書かない）。**有効・無効も比べない**（無効化はまだ認証基盤へ写していない）。
- **食い違いの種類**（ログの `kind=`）:

  | `kind` | 意味 | 主な原因 |
  | --- | --- | --- |
  | `client_missing` | 登録簿に無人の行があるのに、認証基盤に同じクライアント ID が無い | 認証基盤での直接の削除。認証基盤への書き込みの口ができる前に登録した行 |
  | `not_managed` | 認証基盤に同じクライアント ID はあるが、入口の印（`msp.mcp-client.managed-by=mcp-server`）が無い | 入口を通らずに作られたクライアント（プラットフォーム自身の機密クライアントを含む）と同名の古い行 |
  | `service_account_missing` | 印つきのクライアントはあるが、サービスアカウントの利用者が利用者名の完全一致で引けない | 認証基盤でサービスアカウントを外した。判定ではその主体は名簿に居ない（拒否） |
  | `attributes_differ` | サービスアカウントの属性が登録簿の行と違う（集合値の順序は問わない） | 認証基盤での直接の割当。2 つの差し替えの交差（認証基盤は後の要求・登録簿は先の要求） |
  | `orphan` | 印つきのクライアントが認証基盤にあるのに、登録簿に無人の行が無い | 補償（作りかけの削除）が走らなかった・失敗した残骸。同じクライアント ID の並行登録 |

- **計器**: ゲージ `mcp_idp_reconciliation_drifted`（直近の照合で食い違ったクライアントの件数。**0 が正常**）。照合に失敗したときと
  起動直後の未照合のときは、**系列を出さない**（古い値も 0 も出さない）。照合の結末は
  `mcp_idp_reconciliation_checks_total{mcp_idp_reconciliation_outcome="match" | "drift" | "failed"}`（1 回の照合に 1 つ）。
  **クライアント ID は計器の属性に載せない**（系列の数を有界に保つ）。どのクライアントかはログで見る。
- **警報**（下の「監視・アラート」と同じ経路。Alertmanager と Grafana の Alerting 画面）:
  - `McpClientIdpDrift`（warning）: 食い違いが 1 件以上のまま 5 分。**食い違いが起きてから鳴るまで最大およそ 7 分**
    （照合の周期 1 分 ＋ 1 回の照合の期限 1 分 ＋ 5 分）。登録・差し替えの最中を照合が見た一時の食い違いは次の周期で消えるので、
    5 分の持続で鳴らない。
  - `McpClientIdpReconciliationSeriesAbsent`（warning）: ゲージの系列が無い（**見ていない**）。MCP サーバーが止まっている、
    照合の失敗が続いている（認証基盤・登録簿を読めない、照合の期限切れ、書き込み口の未構成）、または収集が欠けている。
    このあいだ `McpClientIdpDrift` は鳴らない。失敗が続き始めてから鳴るまで最大およそ 12 分（周期 ＋ 照合の期限 ＋ 瞬間ベクタの
    lookback 約 5 分 ＋ 5 分。理由は上の「所有者の読み取りのポリシー」の同じ警報と同じ）。
- **ログ**: 食い違いは照合のたびに MCP サーバーが Warning「登録簿と IdP の食い違いを検知した: `client=<クライアント ID> kind=<種類>`」を出す
  （1 回に 20 件まで。超えた分は件数だけ）。**名指しは重大度の高い順**（`attributes_differ` → `orphan` → `not_managed` → `service_account_missing` → `client_missing`）で、
  古い行の `client_missing` が多くても属性違いと孤児は押し出されない。種類ごとの件数は照合ごとの Information の 1 行（`attributes_differ=… orphan=… …`）に出る。属性の値はログに出さない。照合ごとに Information「無人の行 N 件・食い違い M 件」。
  照合できなかったときは Error（書き込み口が構成されていないときは Warning）。
- **鳴ったときの対応**（種類ごと。🔴 **認証基盤の管理画面で属性を直接割り当てて合わせない** —— 検証（登録者の属性の部分集合・個人資料の割当禁止）が掛からない）:
  - `attributes_differ`: どちらが意図した値かを登録者に確かめ、**MCP クライアント登録管理の画面で属性を差し替え直す**（検証 → 認証基盤 → 登録簿の順で両方が揃う）。
    繰り返すなら、認証基盤の管理イベントで `mcp-client-admin` 以外の主体による利用者属性の更新を探す（直接の操作の痕跡）。
  - `client_missing`: 同じ画面で属性を差し替える（認証基盤にクライアントが無い行は、差し替えのときに作られる。無効化した行は無効のまま作られる）。
  - `orphan`: 登録簿に行が無いことを確かめてから、認証基盤の管理画面でそのクライアントを消す（属性は検証を経ていない可能性がある）。
    使い続けるなら、消した後に画面から登録し直す。
  - `service_account_missing`: 認証基盤でそのクライアントを消し、画面で属性を差し替えて作り直す。
  - `not_managed`: 登録簿の行が入口を通らない主体と同名の古い行である。画面の差し替えは拒まれる（入口の印が無い主体へは書かない）。
    不要なら登録簿の行を消す（画面に削除は無いので、MCP サーバーの DB `mcp_svc` の `"Clients"` から当該クライアント ID の行を消す）。
  - 対応の後、次の照合（1 分以内）でログの名指しが消え、ゲージが 0 へ戻ることを確かめる。
- 🔴 **配備の直後に `McpClientIdpDrift` が鳴り得る**: 認証基盤への書き込みの口ができる前に登録した無人の行は、認証基盤にクライアントが無く
  `client_missing` として数えられる。対処は MCP クライアント登録管理の画面でその行の属性を保存し直すこと（差し替えで認証基盤に作られる）。警報は弱めない。
- 🔴 **`McpClientIdpReconciliationSeriesAbsent` が続くとき、原因は 1 行だけのことがある**: 照合は 1 行でも読めなければ全体を失敗にする（途中までの結果で数えない）。
  MCP サーバーの Error ログ「登録簿と IdP を照合できなかった: 行 `client=<クライアント ID>` のサービスアカウントを IdP から読めない」でその行を特定し
  （例: `kubectl -n microservices-platform logs deploy/mcp-service --since=15m | grep '照合できなかった'`）、認証基盤の管理画面でそのクライアントと
  サービスアカウントの状態を確かめる。行の名指しが無い Error（一覧・登録簿を読めない・照合の期限切れ）は認証基盤そのもの・DB・管理用の資格情報を疑う。

### AST の KB の読み手のポリシーの投入（本番の配備の手順。ABAC の要求）

取引ユニット（ai-stock-trading）の取引判断は、KB を検索して判断の参考情報（収集したニュース・確定した報告書など）を得る。
その検索は**読み手の機密クライアント `ai-stock-trading-kb-reader`**（書き手 `ai-stock-trading-kb-writer` とは別の主体）で名乗る。
読み手が読める範囲は、**次の形のポリシー 1 本**だけで決まる。開発環境（経路B）では ABAC の初期投入
（`deploy/local/abac-seed/policies.json`・`node scripts/seed-abac-policies.js --live`）が入れる。**本番では配備の手順としてシステム管理者が投入する。**
このポリシーが無い環境では、読み手の検索は常に 0 件になる（拒否はエラーとして表に出ず、判断は参考情報なしで続く）。

> **本番への投入の保留は解いた**（2026-10-06）。機密区分の上限（`public`・`internal`）を入れた改定（下の「形」と保存時の検証の例外）が入ったため。本番へ投入するときは次を守る。
> 1. authorization-service を、上限の例外を持つ版（develop の 2026-10-06 以降）へ先に上げる。より前の版では 2 キーの文書の条件が保存時に 400 になる。**上限を外した形で入れ直さない。**
> 2. 本文は下の JSON だけを使う。
> 3. 本番の既定拒否の NetworkPolicy は、取引ユニットの名前空間からの通信を既定で塞いでいる。chart の値で読み手の穴を開け、取引ユニットの Pod をメッシュへ入れるまでは、投入しても読み手の検索は届かない（下の「本番の前提」）。

- **形（これ以外にしない）**: 動作は `read`、利用者の条件は `projects` が `ai-stock-trading` を含むこと**だけ**、
  文書の条件は `project` が `ai-stock-trading` に一致し、**かつ** `confidentiality` が `public`・`internal` のどちらかであること（この 2 つだけ）。
  - 🔴 **文書の条件から機密区分の上限を外さない。** 管理者と operator は `project=ai-stock-trading` を持つ文書を区分を問わず作れる。
    上限が無いと、`confidential`・`restricted` の文書がこのラベルだけで読み手へ届き、取引判断の LLM のプロンプトに載る。
    取引ユニットが自分で保存する文書は `internal` 以下なので、上限で失うものは無い。
  - 上限が効くのはこのポリシーの枝だけである。所有者・共有先の分岐は全主体に効き、文書の所有者が読み手の識別子へ明示的に共有した文書は、
    区分を問わず届く（所有者の裁量による開示）。
  - 🔴 **利用者の条件へ取扱区分（`clearance`）を足さない・読み手へ `clearance` を与えない。** 与えると、取扱区分の階段のポリシーに
    マッチして基盤全体の `internal` の文書が読める。読み手が読めるのは取引ユニットの文書だけである。
  - 🔴 **`read` 以外の動作で作らない。** 読み手は書かない（書き手は別の主体である）。
  - 🔴 **`projects` / `project` を条件に持つポリシーをこれ以外に作らない。** `project` は基盤では任意の属性であり、
    ほとんどの文書が持たない。他の主体の判定へ持ち込むと、`project` を持たない文書がその主体から見えなくなる。
- **主体の側（認証基盤）**: 読み手の service-account（`service-account-ai-stock-trading-kb-reader`）に属性
  `projects = ai-stock-trading` を付け、**ロールは 1 つも与えない**（文書の作成・更新の口はロールで閉じているので書けない）。
  クライアントの既定スコープは `profile` にする —— トークンに `preferred_username` が載らないと、検索サービスは主体の名前を引けず拒否する。
  開発環境の宣言は `deploy/keycloak/microservices-platform-realm.json`（稼働中の realm へは `deploy/local/keycloak-setup/reconcile-realm.sh` が当てる）。
  - 🔴 **管理画面の利用者アカウント管理から、読み手の属性を差し替えない。** 差し替えは属性の全置換で、`projects` は属性辞書に無いので
    画面からは付け直せない（読み手の検索が 0 件になる）。開発環境では realm の再適用が宣言の値へ戻す。
- **投入の口と手順**: 所有者の読み取りのポリシー（上）と同じ口・同じ手順（検証 → 投入 → 確かめ）で、本文だけを次にする。

  ```json
  {
    "name": "AST の KB の読み手は AST の文書を読める",
    "action": "read",
    "userConditions": { "projects": ["ai-stock-trading"] },
    "documentConditions": { "project": ["ai-stock-trading"], "confidentiality": ["public", "internal"] }
  }
  ```

- **上限の無い旧い形が入っている環境**（文書の条件が `project` だけのポリシー。開発環境の初期投入を上限の追加より前に行った環境を含む）:
  🔴 **初期投入の再実行では直らない**（同じ名前のポリシーがあると作らない）。再実行すると、seed と食い違う同名のポリシーを名指しで警告する（書き換えはしない）ので、残っている環境の見つけ方には使える。**新しいポリシーを足して旧いものを残すことも、しない**
  （評価器は分岐の和を取るので、旧い枝が残る限り上限は効かない）。管理者が既存のポリシーを書き換える。
  1. `GET /authz/policies` で、`action` が `read`・利用者の条件が `{"projects":["ai-stock-trading"]}` の有効なポリシーを探し、`id` を控える（ちょうど 1 件であること）。
  2. `PUT /authz/policies/{id}` で、本文を上の JSON にする（名前・動作・利用者の条件は同じで、文書の条件だけが増える）。
     口・資格（`platform-admin` のアクセストークン・クラスタの内側から）は投入と同じである。
     🔴 **文書の条件に 2 つの属性キーを持てるのは、この形のポリシーだけである**（保存時の検証は、ほかのポリシーでは 2 つ以上のキーを拒否する）。
     値を変えると（`project` を増やす・機密区分に `confidential` / `restricted` を足す・利用者の条件を変える）、保存は拒否される。
  3. 下の「投入済みかの確かめ方」で、文書の条件が 2 キーになっていることと、分岐の形を確かめる。
- **投入済みかの確かめ方**: `GET /authz/policies` で、上の形（`action`・利用者の条件・文書の条件の 2 キー）に一致する有効なポリシーが**ちょうど 1 件**あり、
  `projects` / `project` を条件に持つ有効なポリシーがほかに無いこと（名前では判定しない）。
  効き目は、認可サービスの `POST /authz/scope` に読み手の名前（`service-account-ai-stock-trading-kb-reader`）と `action: read` を送り、
  `granted` が `true` で、利用者名に束縛されない分岐が「`project ∈ {ai-stock-trading}` かつ `confidentiality ∈ {public, internal}`」の 1 本だけであることで確かめる。
- **切り戻し**: ポリシーを削除するか無効にする。読み手の検索は 0 件に戻る（取引判断は参考情報なしで続く）。消えたことを知らせる計器は持たない。
- **本番の前提（ネットワーク）**: 本番の既定拒否の NetworkPolicy は、取引ユニットの名前空間からの通信を**既定で塞いでいる**。
  chart の値 `networkPolicy.fromAst` で、用途ごとに 1 本ずつ開ける（既定は 3 用途とも `enabled: false`）。

  | 用途 | 値 | 開く穴（これ以外は開かない） |
  | --- | --- | --- |
  | KB の読み手（取引判断の検索） | `networkPolicy.fromAst.kbReader.enabled=true` | 取引ユニットの `trade-decision-service` の Pod → `retrieval-service` の REST（8080） |
  | KB の書き手（収集記事・確定報告書の保存） | `networkPolicy.fromAst.kbWriter.enabled=true` | 取引ユニットの `information-collection-service`・`report-service` の Pod → `document-service` の REST（8080） |
  | LLM ゲートウェイ（報告書の散文・取引判断の生成） | `networkPolicy.fromAst.llmGateway.enabled=true` | 取引ユニットの `report-service`・`trade-decision-service` の Pod → `llmgateway-service` の REST（8080） |

  - 送り元は「名前空間（`networkPolicy.fromAst.namespace`。既定 `ai-stock-trading`。`kubernetes.io/metadata.name` で選ぶ）**かつ** `clients` の Pod（ラベル `app`）」である。
    名前空間の他の Pod・east-west の gRPC（8081）・ほかのサービスへは届かない。`clients` は取引ユニットの chart の Pod ラベル `app: <名前>-service` と一致させる。
  - 有効にしたのに `clients`・`namespace` が空、または行き先のサービスが無効のときは、`helm template` が失敗する（黙って広い穴を開けない）。
  - 🔴 **メッシュ**: 基盤の名前空間は STRICT の mTLS である。穴を開けても、取引ユニットの Pod がサイドカーを持たなければ平文として拒否される。
    取引ユニットの chart の `mesh.sidecarInjection.enabled=true`（名前空間を chart が作らない構成では、運用者が名前空間へ `istio-injection=enabled` を付ける）と組で行う。
  - **確かめ方**（クラスタの外で）: `helm template` に同じ値を渡し、`allow-ast-kb-reader-ingress` / `allow-ast-kb-writer-ingress` / `allow-ast-llm-gateway-ingress` の NetworkPolicy が描かれ、
    `from` の 1 要素に `namespaceSelector` と `podSelector` が並び、`ports` が 8080 だけであることを見る。描画の形は CI の `scripts/helm-ast-kb-ingress.test.js` が固定している。
  - **切り戻し**: 値を `false` へ戻して同期する。その用途の NetworkPolicy が消え、取引ユニットからの通信は再び塞がれる
    （読み手の検索・書き手の保存・LLM の生成のうち、戻した用途が届かなくなる。LLM を戻すと取引ユニットは定型の散文・見送りへ縮退する）。
  - 🔴 **書き手を開ける前に、内容の ABAC の門が開いているかを確かめる。** 門が閉じている間に `kbWriter` を開けると、取引ユニットの書き手の Pod が
    文書サービスの `GET /documents` で組織文書（個人資料を除く）をすべて読めるようになる（組織文書の読み取りを、取引ユニットの書き手の Pod へ広げる）。
    門の状態は上の「内容の ABAC の有効化の門（文書サービス）」のゲージ `documents_content_abac_gate_open` が 1（属性 `open`）であることで確かめる。読み手（`kbReader`）は検索サービスへの穴で、これには当たらない。
  - `target` は用途ごとに固定である（読み手は `retrieval`、書き手は `document`、LLM ゲートウェイは `llmgateway`）。ほかの値は `helm template` が失敗する。
  - **LLM ゲートウェイを開ける前に確かめること**（書き手の内容の ABAC の門に当たる、閉じていると保存済みの組織データが漏れる門は無い。穴は保存済みの組織データを読む口ではない）:
    1. 取引ユニットの呼び出しの主体（基盤の認証基盤の `ai-stock-trading-llm-caller`。サービス呼び出し用のロールだけを持つ）の秘密が、
       認証基盤と取引ユニットの `ast-secrets`（`llm-auth-client-id` / `llm-auth-client-secret`）の両方に入っていること。
       生成・埋め込みの口（`/complete`・`/complete/stream`・`/embed`）はどれもサービス呼び出し用のロールを要するので、無ければ穴を開けても 401 で、
       取引ユニットは定型の散文・見送りへ縮退する。ヘルス・自己申告・OpenAPI の口（`/health/*`・`/internal/introspection`・`/openapi/v1.json`）は匿名だが、
       保存済みデータを返さない（KB の穴と同じ）。
    2. 費用の統制が働いていること。月次予算は用途ごとで（呼び出し元ごとではない）、取引ユニットの用途（`report-daily` / `report-weekly` / `report-monthly` /
       `trade-decision` / `trade-decision-screening`）も数える。上限は要求を止めずアラートだけで、金額が未設定のあいだは鳴らない
       （下の「LLM 費用の統制（暫定）」。その間は月次の手動確認が唯一の統制である）。開けると取引ユニットの呼び出しの分だけ費用が増える。
       用途（`purpose`）は呼び出し側が名乗る値なので、穴を開けると取引ユニットは登録済みのどの用途でも要求でき、費用の帰属は自己申告になる（これを変えるのは本手順の外）。
    - 取引ユニットは REST だけを使う。gRPC（8081）は開けない。取引ユニットが gRPC の輸送を有効にするときは、この値では届かない（そのときは用途の形〔REST だけ〕を変える判断として改めて判断する）。
  - 認証基盤（`platform-infra`）への到達は本 chart の外である。

### 適用直後のドリフト即時検出（構成情報 API の要求 / 実装 ADR のフォローアップ 4 / #145）

宣言（`pipeline.json`）と実効構成のドリフトは、BFF が **定期（既定 5 分・`Drift:IntervalSeconds`）** に加え
**適用直後にも即時検出**する。不一致は構造化ログ `ConfigDrift=true`（`IDriftAlertSink`）で運用アラート経路へ流れる。

- **起動時即時検出**: `DriftDetectionHostedService` は起動直後に 1 回検出する。宣言（`pipeline.json`）変更時は
  BFF がロールアウト（#146 の checksum アノテーション）するため、宣言の適用直後はこの起動時検出で捕捉される。
- **ArgoCD PostSync フック**: `templates/drift-postsync-job.yaml` が各同期の完了後に BFF の
  `POST /internal/config/drift-run`（メッシュ内部限定・応答 202）を叩き、任意の同期後にも即時検出を起動する。
  無効化は `--set drift.postSyncHook.enabled=false`。
  - **Istio STRICT mTLS 下の到達性**: STRICT mTLS（PeerAuthentication STRICT）では、サイドカー
    未注入 Pod からの `bff-service` 到達が Envoy に拒否される。そこで本 Job は `mesh.enabled` のとき
    **サイドカーを注入**し（`sidecar.istio.io/inject: "true"` ＋ `holdApplicationUntilProxyStarts`）、curl 実行前に
    Envoy 起動を待つ。処理後は `POST http://127.0.0.1:15020/quitquitquit` で Envoy を終了させて **Job を完了**させる
    （サイドカーが残って Job が完了しない既知事象を回避。native sidecar 非対応の Istio でも完了する）。`mesh.enabled=false`
    の場合はサイドカーを注入しない。
  - **失敗時の扱い**: BFF へ到達できない場合は Job が非ゼロ終了し、PostSync の失敗として顕在化する
    （`hook-delete-policy: BeforeHookCreation,HookSucceeded` により**失敗 Job は次回同期前まで残置**し調査可能）。
    ドリフト検出は起動時検出（BFF ロールアウト）でも行われるため、フック失敗＝「即時検出が一度実行できなかった」
    ことを意味し、ドリフト自体の有無とは独立。
  - **手動起動**: `kubectl run drift-trigger --rm -it --image=curlimages/curl --restart=Never -- \
    curl -fsS -X POST http://bff-service:8080/internal/config/drift-run`（mesh 有効時はサイドカー注入に留意）
- **手動確認（権限者）**: 運用者・管理者は `GET /bff/admin/config/drift` でドリフト結果を取得できる
  （`ConfigViewer` ポリシー。非権限者は 404 で秘匿）。

### 構成バージョンの注入（構成情報 API の要求 / 実装 ADR のフォローアップ 3 / #144）

BFF の構成情報 API（`GET /bff/admin/config`）は、適用中の構成定義の**構成バージョン**
（`Version.GitCommit` / `AppliedAt` / `AppliedBy`）を返す。値は環境変数
`Config__GitCommit` / `Config__AppliedAt` / `Config__AppliedBy`（`ConfigVersionOptions`）から取得する。

- **k8s（stg/prod）**: Helm values `config.gitCommit` / `config.appliedAt` / `config.appliedBy` を
  BFF Deployment へ注入する（`bff.configVersion: true`）。既定は `appliedBy: argocd`、gitCommit/appliedAt は空。
  **実値の供給**は GitOps側で行う:
  - ArgoCD Application（`deploy/argocd/application.yaml`）の `helm.parameters` が `config.appliedBy=argocd` を固定。
  - **適用リビジョン（コミット ID）と適用日時**は、ArgoCD ネイティブ Helm がビルド変数をパラメータへ
    自動展開しないため、CD が同期時に上書きする:
    `argocd app set microservices-platform --helm-set config.gitCommit=$(git rev-parse HEAD) --helm-set config.appliedAt=$(date -u +%Y-%m-%dT%H:%M:%SZ)`
    （または release automation が `values-<env>.yaml` の `config.*` を更新して Git にコミットする）。
  - 手動確認: `helm template deploy/helm/microservices-platform --set config.gitCommit=deadbeef` で
    BFF env に `Config__GitCommit=deadbeef` が反映される。
- **dev（compose）**: compose 起動時に**環境変数で実 Git コミット ID を渡す**。BFF は
  `Config__GitCommit=${GIT_COMMIT:-dev-local}` / `Config__AppliedAt=${GIT_COMMIT_DATE:-}` /
  `Config__AppliedBy=${GIT_COMMIT_BY:-compose}` を参照する。
  - **ヘルパ**: `scripts/compose-up.sh up -d` が `GIT_COMMIT`（`git rev-parse --short HEAD`）・
    `GIT_COMMIT_DATE`・`GIT_COMMIT_BY` を自動注入して起動する。これで dev の構成ビューアでも実コミット ID が返る。
  - 手動指定も可: `GIT_COMMIT=$(git rev-parse --short HEAD) docker compose -f deploy/docker-compose.yml up -d`。
  - 環境変数未設定時は `dev-local`（実適用リビジョンではないダミー）へフォールバックする。

#### 構成バージョン**履歴**の注入（`GET /bff/admin/config/history`。構成情報 API の要求・#192）

適用履歴（新しい順の複数エントリ）の**正データ源は GitOps 層**（Git のコミット履歴 / ArgoCD リビジョン履歴）で、
BFF は永続化せず注入スライスを surfacing する（履歴ストアを新設しない）。現在バージョンと**同じ注入経路**
（Helm values → env）で供給し、env 命名は ASP.NET の構成配列規約に従う。

- **k8s（stg/prod）**: Helm values `config.history`（リスト、既定 `[]`）を BFF Deployment へ
  `Config__History__<i>__{GitCommit,AppliedAt,AppliedBy,HadDrift}` として注入する（`bff.configVersion: true`）。
  実値の供給は CD が同期時に上書きする（現在バージョン注入と同じ役割分担）:
  - `argocd app set microservices-platform --helm-set config.history[0].gitCommit=$(git rev-parse HEAD) --helm-set config.history[0].appliedAt=$(date -u +%Y-%m-%dT%H:%M:%SZ) --helm-set config.history[0].appliedBy=argocd`
    のように、ArgoCD リビジョン／Git ログの各適用を新しい順の要素として供給する
    （または release automation が `values-<env>.yaml` の `config.history` を更新して Git にコミットする）。
  - `hadDrift` はその時点のドリフト有無が判明していれば `true`/`false` を供給する（不明なら省略＝画面「—」）。
  - 手動確認: `helm template deploy/helm/microservices-platform --set config.history[0].gitCommit=deadbeef --set config.history[0].appliedBy=argocd`
    で BFF env に `Config__History__0__GitCommit=deadbeef` 等が反映される。
- **縮退（後方互換）**: `config.history` が空（dev/compose・既定）なら履歴 env を一切出さず、
  API は**現在バージョン単一エントリへ縮退**する（現在バージョンも空なら空一覧）。dev/compose に追加設定は不要。
- **残作業**: 実 ArgoCD リビジョン／Git ログからの**自動**履歴生成（ライブ CD 供給）は稼働 CD・環境に依存する。
  上記は配線（Helm→env→Options→API）と手動／自動供給手順であり、CD 自動化の実装は環境整備後に行う。

### Wiki.js の起動・初期セットアップ・ヘルスチェック

- **起動**: `docker compose -f deploy/docker-compose.yml up -d` で `postgres` → `keycloak`（`--import-realm` で
  realm `platform` と `wiki-js` クライアントを取り込む）→ `wiki-js` の順に起動する。
- **管理 UI への直接アクセス（dev のみ）**: 下記の初期セットアップ（OIDC 構成・ja ロケール導入・API キー発行）は
  ブラウザから Wiki.js 管理 UI（`http://localhost:3001`）へアクセスする。dev の compose は 3001 を公開している
  （dev ホスト公開は残し、本番系〔Helm〕の非公開を回帰ガードで保証する・#124）。**本番系（Helm）は Wiki.js を公開しない**ため、
  管理 UI の直接操作は dev でのみ行う（本番系の到達は ABAC ゲートウェイ経由に限定）。
- **ヘルスチェック**: Wiki.js は `GET /healthz`（コンテナ内 3000）を返す。compose の healthcheck は node で
  `/healthz` を叩く。dev では `http://localhost:3001/healthz`。
- **管理者ブートストラップ**: 初回アクセス（`http://localhost:3001`）で管理者アカウントのセットアップ画面が出る。
  管理者メール/パスワードを設定してセットアップを完了する（初回のみ。この初期管理者は保守用）。
- **ja ロケールのインストール（必須・Issue #88 実測）**: 素の Wiki.js は `en` のみで、同期
  （GraphQL push）はロケール `ja` でページを作成するため、未インストールだと **FK 違反
  （`pages_localecode_foreign`）で全同期が失敗する**。管理 UI → Administration → Locale で Japanese を
  ダウンロードする（GraphQL では `mutation { localization { downloadLocale(locale: "ja") { ... } } }`。
  Wiki.js のロケール配信サーバへの外向き通信が必要）。
- **OIDC 連携（Keycloak）**: 管理 UI → Administration → Authentication で **Generic OpenID Connect / OAuth2** を
  追加し、以下を設定する。Keycloak 側クライアントは realm import 済み（`wiki-js`、confidential、
  redirect `http://localhost:3001/*`）。
  - Client ID: `wiki-js` / Client Secret: realm import の値（dev は `wiki-js-dev-secret-change-me`。**本番は必ず変更**）。
  - Authorization Endpoint URL: `http://localhost:8080/realms/platform/protocol/openid-connect/auth`
  - Token Endpoint URL: `http://keycloak:8080/realms/platform/protocol/openid-connect/token`
    （サーバ間はコンテナ名 `keycloak`、ブラウザ経路は `localhost:8080`）。
  - **Issuer: `http://localhost:8080/realms/platform`**。issuer はブラウザ経路のホストに
    固定される（compose の `KC_HOSTNAME_URL` で固定済み）。`keycloak:8080` を設定すると ID トークン
    検証と userinfo が失敗する（「Failed to fetch user profile」。Issue #88 実測）。
  - User Info / Logout: 同 realm の対応エンドポイント（User Info はコンテナ内経路 `keycloak:8080`）。
    Scope は Wiki.js 固定の `openid profile email`（realm 側は `profile`/`email` スコープを定義済み。
    `abac-attributes` が default scope のため `clearance`/`department`/`groups` クレームは自動付与）。
  - Email Claim: `email` / Display Name Claim: `name` / Map Groups: 有効・Groups Claim `groups`
    （Keycloak サブグループ名に一致する Wiki.js グループへ自動割当。Self Registration 有効で
    初回ログイン時にユーザー自動作成）。
- **ローカルログイン無効化（OIDC 単一経路）**: OIDC が疎通したら、Administration → Authentication で
  **Local** ストラテジを無効化し、OIDC のみを有効にする。これで受け入れ基準①「ローカルログイン不可」を満たす。
  **稼働検証済み（Issue #88）**: 無効化後のローカルログインは `errorCode 1003（Invalid authentication
  provider）` で拒否され、OIDC 経路のみ有効となることを実測確認した。設定・検証の詳細は
  [Wiki.js 稼働 PoC 実測記録](../tech/20260707_wikijs-poc-record.md) を参照。

### Wiki.js 同期シークレットの発行・投入（Wiki 閲覧の要求 / Issue #88）

同期（GraphQL push）用のサービスアカウント API キーと、Wiki.js 専用 DB のパスワードは
**コミットせず**、以下の手順で発行・投入する。

> **経路B（ローカル k8s）では手で発行しない。** `scripts/k8s-local-up.sh` が既定で呼ぶ
> `deploy/local/wikijs-setup/bootstrap.sh` が、Wiki.js の初期セットアップ・API キーの発行・
> Secret への書き戻し・`wiki-service` の再起動までを冪等に行う。下の手動手順は
> **共有/stg/prod（人がセットアップする環境）**のためのものである。
>
> 🔴 **セットアップを終えただけでは同期は成立しない。** 初期セットアップが入れる locale は
> `en` だけで、同期が push に使う `ja` が無いと `pages.create` が外部キー制約
> `pages_localecode_foreign` に違反して落ちる。**Wiki.js は GraphQL 200 を返す**ので、
> 失敗は同期側のエラーキューにしか残らない。管理 UI の Locale で対象 locale を導入すること。

- **API キーの発行（Wiki.js 管理 UI）**:
  1. 管理者で Wiki.js にログインし、Administration → **API Access** を開き、API を **Enabled** にする。
  2. **New API Key** で作成する。名前は `wiki-service-sync`、有効期限は運用ポリシーに合わせる
     （既定 3 年。ローテーション手順を後述）。権限グループは**ページの read/write/manage/delete を持つ
     グループ**を割り当てる（同期は `pages.create/update/delete` と `pages.singleByPath` を呼ぶ）。
  3. 表示されたキー（JWT）を安全な場所（シークレットマネージャ）へ控える。**再表示はできない**。
- **compose（dev）への投入**: リポジトリ直下または `deploy/` の `.env`（gitignore 済み）に
  `WIKIJS_API_KEY=<キー>` を記載し、`docker compose -f deploy/docker-compose.yml up -d wiki-service`
  で反映する（compose は `WikiJs__ApiKey: ${WIKIJS_API_KEY:-}` を参照）。
- **Helm（共有/stg/prod）への投入**: チャートは Secret を**参照のみ**するため、事前に作成する。
  ```bash
  # 同期用 API キー（wiki サービスの WikiJs__ApiKey が secretKeyRef で参照。key=apiKey）
  kubectl create secret generic wikijs-sync -n <namespace> \
    --from-literal=apiKey='<Wiki.js で発行した API キー>'
  # Wiki.js 専用 DB のパスワード（wiki-js Deployment が参照。key=password）
  kubectl create secret generic wikijs-db -n <namespace> \
    --from-literal=password='<wikijs DB ユーザのパスワード>'
  ```
  ArgoCD 等の GitOps では SealedSecret / ExternalSecret で同名 Secret を供給する。
- **ローテーション**: Wiki.js 管理 UI で新キーを発行 → Secret を更新
  （`kubectl create secret ... --dry-run=client -o yaml | kubectl apply -f -`）→
  `kubectl -n <namespace> rollout restart deployment/wiki-service` → 旧キーを Wiki.js 側で Revoke する。
  dev は `.env` を書き換えて `docker compose up -d wiki-service`。
- **注意**: API キーは Wiki.js の管理 GraphQL 全体に及ぶ強い権限を持つ。付与グループは最小権限とし、
  キーは wiki-service 以外へ配布しない（認可は本システムの ABAC ゲートウェイが単一真実源であり、
  キー漏えい時は Wiki.js 全ページの読み書きが可能になるため即時 Revoke する）。

### 埋め込みプロバイダの設定・ゼロ保持・再索引（取り込みの要求 / 埋め込みプロバイダ選定の計画 ADR / Issue #98）

埋め込みは取り込み時に**全文書本文**を送信するため、LLM 呼び出しよりデータ露出が大きい。機密区分で
送信先・モデル・コレクションが分かれる（`Embedding:Routing`）。
**［2026-10-05］高機密文書（confidential・restricted・機密区分が未指定・未知）は埋め込まない。** 取り込みは埋め込みを
呼ぶ前に機密区分で分け、高機密文書をベクトルを持たない専用のコレクション（語彙索引。`Qdrant__LexicalCollection`。
Helm は `lexicalIndex.collection`、compose は `.env` の `SEARCH_LEXICAL_COLLECTION`。**取り込みと検索で同じ値**）へ全文索引だけで書く。

| 機密区分 | 送信先ティア | モデル / 次元 | コレクション | 既定状態 |
| --- | --- | --- | --- | --- |
| public / internal | ティアB（Voyage・保護契約） | voyage-3.5 / 1024 | `knowledge_chunks_voyage_3_5` | 有効（要 API キー） |
| confidential / restricted / 未指定・未知 | **埋め込まない**（どの送信先へも本文を送らない） | なし（ベクトルを持たない） | `knowledge_chunks_lexical`（語彙索引） | **常に有効**（全文索引だけ。キーワードとハイブリッドで現れ、意味検索には現れない） |
| （opt-in）セルフホスト有効時 | ティアA（セルフホスト） | ruri-v3 / 768 | `knowledge_chunks_ruri_v3` | 無効。**有効にしても高機密文書は埋め込まれない**（上の行のまま。検索クエリと測定用） |
| （検証スタック専用）全区分 | ティアA（決定的ローカル・プロセス内計算） | deterministic-hash-v1 / 1024 | `knowledge_chunks_deterministic_v1` | 無効＝**既定では存在しないのと同じ** |

🔴 **3 行目は使い捨ての検証スタック専用である。** 表層の文字 3-gram をハッシュするだけで
**意味的な近さを持たず、検索品質を評価する用途には使えない**。存在理由は、埋め込みの鍵が無い
統合スタックで「検索が実際に効くこと」を観測できるようにすること（それが無いと、索引に 1 点も
入らないまま `POST /bff/search` が「壊れている」ときと同じ `200 ＋ 空` を返し、**壊れていても緑になる**）。
**HTTP を一切行わないため、ティアA（社外送信なし）の定義をそのまま満たす** ——
機密区分 × ティアの越境判定は 1 バイトも緩めていない。
有効にすると起動時に警告ログが出る（本番へ紛れ込んだことに気づけるようにするため）。

- **Voyage AI（ティアB）のゼロ保持設定（必須・受け入れ基準）**: 本番データを流す前に、Voyage AI の
  組織設定で**学習利用のオプトアウト（ゼロ保持 / zero-day retention）を有効化**する。08_data-egress-policy
  のティアB要件（ゼロ保持・学習不使用・レジデンシー）を契約で確認し、確認できるまで本番文書を索引しない。
  未認定の間は `Embedding__Routing__Endpoints__0__Enabled=false` で Voyage 経路を止められる。
  - API キーは Secret 経由で投入する（コミットしない）。compose: `.env` の `VOYAGE_API_KEY`
    （`Embedding__Voyage__ApiKey`）。~~k8s は Secret（例 `embedding-voyage`、key=`api-key`）。~~
    **［2026-10-06］k8s（Helm）は Secret `llm-provider-credentials` のキー `voyage-api-key` を `Embedding__Voyage__ApiKey` へ渡す**
    （`optional: true`。本番像の `values.yaml` と経路B の `values-local.yaml` の両方。外部 LLM の鍵と同じ Secret）。
    経路B は Vault の KV `secret/msp/llm-provider-credentials` のプロパティ `voyage-api-key` を ESO が写す。
    鍵の入れ方と、値を表示せずに届いたことを確かめる手順は [`voyage-embedding-key-runbook.md`](voyage-embedding-key-runbook.md)。
    **再索引の前に行う**（鍵が無いまま再発行すると、`public` / `internal` の文書は全件が再試行の後 DLQ へ行くだけになる）。
  - キー未設定でも起動する（fail-open しない）。Voyage 呼び出しが失敗した文書は索引されないだけで、
    高機密文書の本文が外部へ出ることはない（ルーティングで候補にならないため）。
    ［2026-10-06］Secret にキーが無い（古い Secret）場合も起動する（`optional: true` で env を置かない）。
  - **ゼロ保持認定状況の記録（#303 受け入れ基準）**: 実環境構築前チェックリストの一項目として、契約でのゼロ保持
    （学習不使用・レジデンシー含む）認定の可否をここに記録する。**現状: 未認定（2026-07-19 時点）**。
    **［2026-10-06］未認定のまま（#1740）。オーナーは同日、認定の前に経路B（稼働の開発クラスタ）で Voyage の鍵を配線して使うことを受け入れた。**
    本番相当の環境は従来どおり、認定まで本番文書を流さない。
    - ⚠️ **既定構成は Voyage 経路が有効**（`appsettings.json` の `voyage-managed`＝index 0 が `Enabled: true`。
      compose/Helm に既定の無効化上書きは無い）。したがって「未認定＝自動で停止」ではない。**未認定の環境へデプロイ
      する場合は、運用者が本番文書を流す前に明示的に Voyage 経路を無効化すること**（`Embedding__Routing__Endpoints__0__Enabled=false`。
      compose は `.env`、k8s は values/`--set` で上書き）。本 PR は既定挙動（Voyage 有効）を変更しない（後方互換）。
    - 実際の契約認定は稼働環境／調達手続き依存＝分離（フォローアップ #336）。
- **セルフホスト（ティアA / Ruri v3）の有効化**: 基盤（TEI / vLLM 等の OpenAI 互換 `/v1/embeddings`）を
  構築後、`SELFHOSTED_EMBEDDING_URL`（`Embedding__SelfHosted__BaseUrl`）と
  `SELFHOSTED_EMBEDDING_ENABLED=true`（`Embedding__Routing__Endpoints__1__Enabled`）を設定して有効化する。
  ~~有効化まで confidential/restricted 文書は**索引されない**（fail-closed。設計どおり）。~~
  **［2026-10-05］改まった。** 高機密文書は有効化の有無に依らず埋め込まれず、語彙索引に全文索引だけで載る（上の表）。
  セルフホストの配備物は opt-in のまま残る（検索クエリの埋め込みと nDCG の測定用）。
  - **配備物（opt-in・#303）**: 推論基盤（TEI）の配備物をリポに opt-in で用意済み。
    - k8s（Helm）: `values.yaml` の `embedding.enabled=true`（既定 `false`）で `templates/embedding.yaml` が
      TEI Deployment/Service を描画し、`llmgateway` へ `Embedding__SelfHosted__BaseUrl=http://embedding-service:<port>`
      と `Embedding__Routing__Endpoints__1__Enabled=true` を自動注入する（`services.llmgateway.selfHostedEmbedding`）。
    - compose: `docker compose --profile embedding up` で `embedding`（TEI）サービスを起動し、`.env` に
      `SELFHOSTED_EMBEDDING_URL=http://embedding:80` / `SELFHOSTED_EMBEDDING_ENABLED=true` を与える。
    - 🔴 **検索側も同時に束ねる**（2026-09-26）: 検索サービスが Ruri のコレクションも読まないと、高機密の文書は
      **索引されるが検索されない**。Helm は `embedding.enabled=true` で retrieval へ
      `Qdrant__FusedCollections__0=<embedding.collection>` を自動で描画する。compose は `.env` に
      `SEARCH_FUSED_COLLECTION=knowledge_chunks_ruri_v3` を与える。束ね方（順位で合成・スコアは比べない・
      権限フィルタは全コレクションに掛ける）は [ハイブリッド検索 機能仕様書](../functional/FR-03_hybrid-search.md) にある。
      有効化の後は、Ruri コレクションの全文索引が張られていること（取り込みの起動時ログ）を確かめる ——
      検索の readiness は主コレクションの索引しか見ない。
    - **稼働環境依存（分離）**: 実モデル（Ruri v3）の取得・GPU/CPU リソース・実埋め込み疎通・下記 nDCG@10 実測は
      稼働環境で行う。既定の image tag / モデル ID はプレースホルダであり、実運用前に稼働環境で固定する。
  - 有効化後、社内文書サンプルで検索精度（nDCG@10）を実測し、voyage-3.5 比で大幅劣化しないことを確認する
    （セルフホスト埋め込みの計画 ADR が求める事前 PoC の代替）。劣る場合は BGE-M3 へ切替（モデル別コレクション分離のため影響は局所）。
    - **測定の道具と手順は `perf/ndcg/README.md`**（実体は `scripts/measure-search-ndcg.js`）。正解ラベル（qrels）の
      雛形・実行例・結果の読み方はそちらにある。**収集と集計が分かれており、保存した順位から集計だけを追試できる。**
  - **A/B のときは 2 つの設定を必ず対で切り替える**（片方だけ動かすと、**別モデルの空間へ問い合わせる**ことになる）。

    | 何を | どこで | 値の例 |
    | --- | --- | --- |
    | 検索クエリの埋め込み先 | LlmGateway `Embedding__Routing__QueryProfile`（既定は空＝優先度順） | `selfhosted-ruri` |
    | 検索が読むコレクション | RetrievalService `Qdrant__CollectionName` | `knowledge_chunks_ruri_v3` |

    - 綴り間違い・無効なエンドポイントの指定は**起動時に失敗する**（黙って既定へ落とすと、
      別のモデルを測ったまま数字だけが出るため）。
    - 2 つが食い違ったまま検索した場合、RetrievalService は**クエリのベクトルを捨てて全文検索だけで応答する**
      （ゲートウェイが答えたコレクション名と、自分が読むコレクション名を突き合わせている）。
      **測定が壊れたまま成立しないための歯止め**であり、この縮退はログ（`collection mismatch`）に出る。
    - 🔴 **`QueryProfile` は検索クエリにだけ効く。** 取り込み（文書本文）の送信先は従来どおり
      機密区分が決めるものであり、この設定では動かない（越境統制は 1 バイトも緩んでいない）。
  - **⚠️ 配列インデックス依存の環境変数に注意（Issue #98）**: 上記 `Endpoints__0__Enabled`（Voyage）/
    `Endpoints__1__Enabled`（セルフホスト）/ `Endpoints__2__Enabled`（決定的ローカル・検証スタック専用）は
    `appsettings.json` の `Embedding:Routing:Endpoints` 配列の並び順に依存する。エンドポイントの追加・並び替え時はインデックスを必ず見直すこと。取り違え
    （例 Voyage を誤って無効化し、セルフホストも無効のまま＝全 public 取り込み・検索クエリが黙って
    fail-closed）は起動時バリデーション（`EmbeddingRoutingOptionsValidator` / `ValidateOnStart`）が
    fail-fast で検知し、LlmGateway は起動に失敗する（ログに不整合内容を出力）。ティア↔プロバイダの
    取り違え・必須項目欠落も同時に検証される。
- **一時障害と fail-closed（意図的拒否）の区別（Issue #98）**: `/embed` は応答に `Retryable` を返す。
  - **一時障害**（送信先の不調・タイムアウト・予期しない空応答など、`Retryable=true`）: 取り込み消費側
    （`DocumentUpdatedConsumer`）は当該メッセージを**恒久スキップにせず例外を送出**し、MassTransit の
    受信リトライ／(枯渇後) DLQ に回す。一括再索引中に Voyage が一時的に不調でもチャンクを取りこぼさない。
    → 運用: DLQ を監視し、滞留があれば原因（Voyage 障害・鍵の未配線・URL 誤設定等）を解消して再発行する。
    ［2026-10-06］取り込みは Wolverine へ移っており、DLQ は RabbitMQ の `wolverine-dead-letter-queue`（全サービスで共有）である
    （`*_error` は MassTransit の段の命名）。DLQ のメッセージは古い状態の写しなので再投入せず、下の「`DocumentUpdated` の再発行」で今の台帳から作り直す。
    → 注意（削除後・再構築前の空白期間）: 取り込みは冒頭で当該文書の既存チャンクを全モデル別コレクションから
    削除してから再索引する（機密区分変更時の残存防止）。一時障害でリトライ枯渇→DLQ 送りとなった文書は、
    **削除済み・未索引（0 チャンク）の状態で一時的に検索不可**となる（恒久欠落ではなく DLQ 再投入で回復する）。
    このため DLQ 滞留は検索網羅性に直結する運用指標として監視し、速やかに再投入すること。
  - **fail-closed / 恒久的理由**（次元不整合・プロバイダ未登録・外部経路の無効化など、`Retryable=false`。［2026-10-05］高機密文書は
    埋め込みを呼ばずに語彙索引へ行くので、ここへは来ない。ここへ来るのは public / internal の文書だけである）:
    設計どおり当該チャンクを**索引スキップ**し、`IngestionCompleted` は索引できた件数で発行する
    （警告ログに機密区分を記録）。再試行では解消しないため DLQ には回さない。
- **再索引手順（次元 1536→1024・モデル別コレクション移行）**:
  1. 取り込みサービスは起動時に不足コレクション（`knowledge_chunks_voyage_3_5` / `_ruri_v3`。
     検証スタックでは `_deterministic_v1` も）を実次元で自動作成する（`QdrantBootstrapHostedService`）。旧 `knowledge_chunks`（1536 次元）は使用しない。
  2. 全文書に対し `DocumentUpdated` を再発行する（原本→正規化→取り込みを再走）。取り込み冒頭で全モデル別
     コレクションから当該文書を削除してから再索引するため、決定的チャンク ID により冪等に再構築される。
     ［2026-10-06］**手段は下の「`DocumentUpdated` の再発行（再索引の手段）」**（`scripts/republish-document-updated.js`）。
  3. 旧コレクション `knowledge_chunks` は移行完了後に手動削除する（`DELETE /collections/knowledge_chunks`）。
  - モデル差し替え（例 ruri-v3→BGE-M3）時も、当該コレクションを作り直し同手順で再索引する。
  - **［2026-10-05］語彙索引の展開順序**: 語彙索引のコレクション（既定 `knowledge_chunks_lexical`）を作るのは**取り込みサービスの起動時だけ**である。
    **取り込みサービスを先に（または同時に）展開し、起動ログに Qdrant のコレクションと全文索引の確保の成功が出ていること**
    （`Failed to ensure Qdrant collection / full-text payload index at startup` の Error が無いこと）を確かめてから、検索サービスを展開する。
    逆順やブートストラップの失敗の間は、キーワード／ハイブリッド検索のたびに縮退の警告と計器 `search.keyword_degraded.total`
    （理由 `backend_error`）が増える（検索は 200 で続く。文書削除は語彙索引の分だけ何もしないので失敗しない）。取り込みサービスを再起動して作らせれば収まる。
  - **［2026-10-05］語彙索引の導入後は、既に取り込まれた高機密文書（confidential・restricted・機密区分なし）に対して手順 2 を行う。**
    これらは従来どこにも索引されておらず、再発行するまで語彙索引にも入らない。語彙索引のコレクションは取り込みサービスが起動時に自動で作る。
    高機密文書だけを流すなら `--attr confidentiality=confidential` / `--attr confidentiality=restricted` で絞る（機密区分の無い文書は属性の一致では選べないので、全件か `--ids` で流す）。
#### `DocumentUpdated` の再発行（再索引の手段）

射影（Qdrant の索引・Wiki.js・グラフ）は文書台帳の写しであり、作り直す手段は `DocumentUpdated` の再発行である。
文書サービスの**管理者だけの口** `POST /documents/republish-updated` が、通常の発行の門を通して 1 ページずつ発行する
（中身は通常の経路と同じ。台帳は書き換えない＝版も更新時刻も動かない）。量の制御・進捗・中断と再開・DLQ の監視は
駆動スクリプト `scripts/republish-document-updated.js` が持つ。**稼働クラスタへ当たる**ので `--live` が要る。

**🔴 流す前に、埋め込み先があることを確かめる。** `public` / `internal` の文書は埋め込み（既定は Voyage）へ進む。
LLM ゲートウェイに Voyage の鍵（`Embedding__Voyage__ApiKey`）が無いと、埋め込みは一時障害として扱われ、**全件が再試行の後 DLQ へ行くだけになる**
（索引には 1 点も入らない。取り込みは書く前に当該文書の点を消すため）。
［2026-10-06］チャート（経路B の値を含む）は、この鍵を Secret `llm-provider-credentials` のキー `voyage-api-key` から渡すよう配線してある（キーが無い・空でも起動する）。
**鍵の値はチャートにもリポジトリにも無い。** 用意の仕方は、鍵の値を Vault へ入れて同期させる（手順は [Voyage の鍵の Runbook](voyage-embedding-key-runbook.md)。課金が発生する。ゼロ保持の認定は本節の上の注記のとおり未了）か、
**使い捨ての検証スタックに限り** `LOCALEMBED=1`（決定的ローカル埋め込み。意味的な近さは無い）で立てるかである。高機密文書（語彙索引だけ）は鍵が無くても入る。
env の名前（`Embedding__Voyage__ApiKey`）は鍵が空でも下の確認に出るので、**値が入っていることは同 Runbook の手順 4（長さで測る）で確かめる。**

```console
# 埋め込み先の確認（無ければ流さない。env の名前だけを見る。値の長さは Voyage の鍵の Runbook の手順 4）
kubectl -n microservices-platform get deploy llmgateway-service \
  -o jsonpath='{range .spec.template.spec.containers[0].env[*]}{.name}{"\n"}{end}' | grep -iE 'voyage|Endpoints__2__Enabled'
# 取り込みのキューと DLQ の深さ（ready + unacked）
kubectl -n platform-infra exec deploy/rabbitmq -- rabbitmqctl list_queues -q name messages \
  | grep -E 'wolverine-dead-letter-queue|ingestion-service\.DocumentUpdated'
```

**手順**:

1. **dry-run で件数と内訳を見る**（発行しない）。埋め込みへ進む件数（`public` + `internal`）が費用の母数である
   （1 チャンクにつき埋め込み 1 回。本文なしの文書は題名などで 1 回。単価は契約の価格表を見る）。
   ```console
   node scripts/republish-document-updated.js --live --dry-run --operator <あなたの名前>
   node scripts/republish-document-updated.js --live --dry-run --operator <あなたの名前> --attr confidentiality=internal   # 絞った場合
   ```
   - **記録**: 口は dry-run も発行も 1 呼び出しごとにログへ 1 行残す（認証済みの主体＝駆動スクリプトの client と、`--operator`〔既定は環境変数 `USER`〕・`--reason` の札）。
     **発行する実行（`--resume` を含む）は `--reason <理由>` が必須**（dry-run では任意）。札は改行などの制御文字を潰してからログへ出す。
2. **流す**。最初のページは**カナリア**（既定 10 件）で、取り込みのキューが空になるまで待ち（再試行を含めて 1 件あたり最大でおよそ 1 分）、
   DLQ が 1 件でも増えていれば止まる。以後は、取り込みのキューが `--max-queue-depth`（既定 200）以下になってから次のページ（`--page-size` 既定 50）を出し、
   ページの間に `--sleep-ms`（既定 2000）待つ。DLQ の増加が `--max-dlq-growth`（既定 20）を超えたら止まる。
   **走査の終わり（と `--max-pages` の区切り）では、取り込みのキューが空になるまで待って（上限 `--drain-timeout-ms`）DLQ を確かめてから「完了」を出す**
   （最後のページの失敗は後から DLQ に現れるため）。増えていれば完了と言わずに exit 1 で止まる。許容の内で増えていたら件数を出すので、DLQ の中身で文書を確かめて `--ids` で流し直す。
   絞り込みが 0 件なら待たずに終わる。
   ```console
   node scripts/republish-document-updated.js --live --operator <あなたの名前> --reason "<理由>"
   node scripts/republish-document-updated.js --live --operator <あなたの名前> --reason "<理由>" --page-size 20 --sleep-ms 5000 --max-pages 50   # 控えめに区切る
   ```
   - 目安: 取り込みは 1 件ずつ外部の埋め込みを呼ぶので、2 万件規模は数時間かかる。ゲートウェイの埋め込みの速度制限やクラスタの負荷を見て `--page-size` / `--sleep-ms` を下げる。
   - 絞り込み: `--attr <キー>=<値>`（完全一致・AND・繰り返し可）／`--ids <id,id,...>`（500 件まで）／`--created-before <ISO8601>`。
     新規の走査は `createdBefore` を開始時刻に固定する（走査の途中で作られた文書は作成の経路で既に発行されているので選ばない）。
   - 副作用: fan-out なので Wiki 同期（`published` / `normalized` の組織文書を Wiki.js へ書き直す）とグラフ同期（同じ更新時刻なら何もしない）も動く。
3. **中断と再開**。状態（カーソル・**確かめた位置**・`createdBefore`・絞り込み・累計）は `./republish-document-updated.state.json`（`--state` で変更）に**ページごとに**書かれる。
   **確かめた位置**は、DLQ の確認を通り、かつ取り込みのキューが空だった時点のカーソルである（キューに残っている配信は、まだ DLQ へ行くかが決まっていない）。
   Ctrl-C・連続失敗（既定 3 回）・キューの待ちの超過・DLQ の増加で止まるときは、**状態のカーソルを確かめた位置へ戻して保存する** ——
   確かめていないページ（DLQ へ行った文書を含む）は `--resume` で**もう一度発行される**（再発行は冪等。埋め込みの費用はその分重なる）。止まった後の `--resume` はカナリアからやり直す。
   原因を直したら、同じ指定で `--resume` する（`--resume` も発行する実行なので `--reason` が要る。`--page-size` などの量の指定は状態に残らないので毎回渡す）:
   ```console
   node scripts/republish-document-updated.js --live --resume --operator <あなたの名前> --reason "<直した原因>"
   ```
   **`--resume` は DLQ の基準をその時点の DLQ の深さへ取り直し、前の基準と今の深さを表示する**（前の実行の増加でカナリアが直後に止まり続けないため。
   取り直しても取りこぼさないのは、止まったときに確かめた位置へ戻してあるからである）。DLQ を purge してから再開してもよい（戻したページが再発行される）。
   状態ファイルが在るまま新規に流すと拒否される（やり直すなら状態ファイルを消す）。版の違う（確かめた位置を持たない）状態ファイルからは再開しない。
   ブローカ（RabbitMQ。経路B は PVC を持たない）が途中で作り直された疑いがあるときは、`--resume` ではなく状態ファイルを消して最初から流す
   （カーソルは「発行した」位置であり「索引された」位置ではない。再発行は冪等なので重ねて流してよい）。
4. **確かめる**。取り込みのキューが空になってから、各コレクションの `points_count` が増えていること（読み取りだけ）:
   ```console
   kubectl -n platform-infra run qdrant-check --rm -i --restart=Never --image=curlimages/curl -- sh -c \
     'for c in knowledge_chunks_voyage_3_5 knowledge_chunks_lexical knowledge_chunks_ruri_v3; do curl -s http://qdrant:6333/collections/$c; echo; done'
   ```
   `points_count` は文書数ではなくチャンク数である（本文の所在が無い文書は点を作らない。dry-run の「本文の所在が無い」件数を差し引いて読む）。

**DLQ の扱い**: DLQ（`wolverine-dead-letter-queue`）は全サービスで共有している。再発行で増えたメッセージは古い状態の写しなので**再投入しない**。
駆動スクリプトが DLQ の増加で止まったときは、状態のカーソルを確かめた位置へ戻してあるので、原因（埋め込み先・本文の取得）を直して `--resume` すれば
DLQ へ行った文書を含む確かめていないページがもう一度発行される（冪等。`--resume` は DLQ の基準を今の深さへ取り直す）。中身を確かめずに DLQ を purge しない
（確かめるときは `kubectl -n platform-infra port-forward svc/rabbitmq 15672` で管理画面を開く）。

**口の仕様**（直接叩く場合。メッシュ内部・管理者のトークンが要る）: 要求は `dryRun`（必須）・`limit`（既定 100・1〜500）・`cursor`（前の応答の `nextCursor`）・
`createdBefore`・`ids`（空の配列は 400。省略が全件）・`attributes`・`requestedBy`（100 文字まで）・`reason`（500 文字まで）。`requestedBy` / `reason` は記録だけに使う札で、
認証済みの主体と一緒にログへ出る（dry-run も記録する）。応答は `matched`（絞り込みの全件）・`remaining`（この呼び出しの前に残っていた件数）・`selected`・`published`・
`skippedByGate`（露出の 3 トグルが OFF の個人資料は発行しない）・`withoutBody`・`byConfidentiality`・`nextCursor`（尽きたら null）。詳細は `docs/api/openapi.yaml`。

- **ペイロード項目を増やしたときの再索引（#536）**: 索引ペイロードへ**新しい項目**を
  足した場合も、上記手順 2（**全文書に対する `DocumentUpdated` の再発行**）がそのまま使える。
  コレクションの作り直しは要らない —— 決定的チャンク ID により同じ点が上書きされる。
  - **直近の該当**: **`updated_at`**（文書の更新日時。Unix epoch ミリ秒の整数）を #536 で追加した。
  - **再索引が済むまでの振る舞いは縮退であって障害ではない。** 当該項目を持たないチャンクは
    検索応答で `updatedAt` が `null` になり、画面は `—` を描く。**検索そのものは従来どおり
    ヒットする**（項目の欠落で結果から落とすことはしない）。
  - **急ぐ必要は無いが、放置すると「更新日時の新しい順」が実質的に使えない**
    （日時を知らないチャンクが混ざり続けるため）。並び順の提供時期に合わせて実施すること。

### 検索の再順位付け（Claude）の有効化と費用（横断検索の要求 / #1746）

検索結果の候補を Claude（LLM ゲートウェイの用途 `rerank` → `claude-haiku-4-5`）で並べ替える段は、**既定で無効**である。
有効にすると、検索結果一覧（並び「関連度」・ハイブリッド／キーワード）と RAG 回答の候補の両方に効く
（仕組みは [ハイブリッド検索 機能仕様書](../functional/FR-03_hybrid-search.md) §Claude による再順位付け）。

- **有効化**: 検索サービスの `Rerank__Enabled=true`。Helm は `searchRerank.enabled: true`、compose は `.env` の `SEARCH_RERANK_ENABLED=true`。
  宛先は埋め込みと同じ LLM ゲートウェイ（`Services__LlmGateway`。`Services__LlmGatewayGrpc` が在れば gRPC）で、追加の配線は要らない。
  有効なとき introspection（構成情報）にポート `search-rerank` が載る。
- 🔴 **有効化は越境の判断を伴う。** 候補の本文（1 件あたり最大 400 字・最大 20 件）が、`restricted` と機密区分が未指定・未知の文書を含めて
  ティア B（Claude・ZDR）へ出る（計画が受け入れたリスク。追加統制は未定義）。守りは ZDR（用途 `rerank` は区分によらず ZDR 必須）・ABAC・
  個人資料の「AI の入力に含める」だけである。
- **費用の目安**（単価表 `claude-haiku-4-5` $1 / $5 per 1M tokens）: 1 回 平均 約 $0.0055・最大 約 $0.011。1 日 1,000 検索で 約 $165／月。
  実績は `llm_cost_total{llm_purpose="rerank"}`（回答生成 `rag-answer` と別の軸）で見る。
  🔴 **検索の回数には MCP のツールの検索も入る**（同じ検索の出口を通る）。検索結果一覧と RAG 回答だけで見積もらない。
  合成監視の検索（`/bff/analysis/ask` 等）は内周の標識で見分けて再順位付けを呼ばない（費用に入らない）。
- **用途のモデルを変えるとき**: `rerank` の割当モデルを ZDR 非対応（`NonZdrModels`）にすると、ゲートウェイは既定モデル
  （`claude-opus-5`）へ倒れる。ZDR の外へは出ないが、費用は最大で約 5 倍になる。
- 🔴 **有効化は #1746 の段 S5 で扱う**（検索 p95 の目標 1.5 秒と、再順位付けの往復〔期限 8 秒〕の扱いの裁定が前提）。
- **調整の口**（範囲外は既定へ倒れ、警告ログが出る）: `Rerank__CandidateCount`（既定 20・2〜50）・`Rerank__MaxCharsPerCandidate`（既定 400・50〜2000）・
  `Rerank__TimeoutSeconds`（既定 8・1〜30）・`Rerank__MaxOutputTokens`（既定 512・64〜2048）。
- **監視**: `search_rerank_total{search_rerank_result="degraded"}` が増えたら、理由（`search_rerank_reason`）を見る。
  `timeout`（期限・ゲートウェイの遅延）／`transport`（ゲートウェイに届かない）／`not_sent`（越境拒否・上流の不調。ゲートウェイのログに理由）／
  `refusal`（モデルの拒否）／`unparseable`（出力の形）。**どの理由でも検索は従来の並びで成功している**（並べ替えだけが抜ける）。
- **戻す**: `Rerank__Enabled=false`（段の型が登録されなくなり、並びは従来どおり）。

## 可用性・水平スケール（HPA / PDB）（NFR / #197）

計画 NFR「スケーラビリティ: HPA で水平スケール」「可用性: 99.9% 以上（月間ダウンタイム約 43 分以内）」の
実現手段を Helm チャート（`deploy/helm/microservices-platform/`）の構成で提供する。適用は GitOps（ArgoCD）
経由で、構成変更のみで完結する。

### 実現手段

- **HorizontalPodAutoscaler（`templates/hpa.yaml`）**: CPU 使用率（`requests.cpu` に対する平均）で `minReplicas`〜
  `maxReplicas` に自動スケールする。metrics-server が必要（k3s は既定同梱）。既定は min=2 / max=4 /
  目標 CPU 70%（`values.yaml` の `scaling.hpa`）。
- **PodDisruptionBudget（`templates/pdb.yaml`）**: 自発的中断（ノードドレイン・ローリング更新）時に
  `minAvailable`（既定 1）レプリカを維持し、瞬断を防ぐ。
- **レプリカ所有権**: HPA 対象サービスは Deployment に静的 `replicas` を持たず HPA が所有する
  （`deployment.yaml` は `scaling.services` に含まれるサービスの `replicas` を出力しない。値の綱引きを避ける）。
- **ヘルスプローブ / ロールアウト**: 各 HTTP サービスは `readinessProbe`（`/health/ready`）・`livenessProbe`
  （`/health/live`）を持ち、Deployment 既定の RollingUpdate（maxUnavailable による無停止更新）と PDB で
  更新時の可用性を担保する。

### 適用対象（段階適用）

| 区分 | サービス | HPA/PDB |
| --- | --- | --- |
| 要求処理（ステートレス） | bff / retrieval / authorization / aianalysis / document / datasource / dashboard / feedback / wiki / llmgateway | **有効**（min 2 / max 4 / PDB minAvailable 1） |
| キュー駆動ワーカー | conversion / ingestion（`worker: true`） | 対象外（replicas 1 のまま） |
| ステートフル | seaweedfs / wikijs / postgres / qdrant | 対象外（各自の可用性方針） |

- **チャートの外にも PDB を持つ部品が 1 つある**: パスワードリセット申請の床の器（`platform-infra` の `reset-floor`。
  宣言は `deploy/mail-relay/reset-floor/`）。申請の経路が器だけを向くため、**2 レプリカ ＋ PDB（`minAvailable: 1`）**で
  動かす（HPA は持たない）。器がすべて落ちたときの扱いは
  [Keycloak smtpServer の設定の Runbook](keycloak-smtp-relay-setup-runbook.md) の「器がすべて落ちたとき」。

- ワーカー（conversion/ingestion）は RabbitMQ 競合コンシューマで水平化自体は可能だが、CPU ベース HPA が
  不適（キュー滞留がスケール指標）なため本段では対象外とし、負荷実測後に KEDA 等のキュー長ベース
  スケールを別途検討する。
- 対象の増減は `values.yaml` の `scaling.services` リストの変更（＋ GitOps 適用）のみで行う。

### 前提・確認事項

- **metrics-server** がクラスタに導入済みであること（HPA の CPU 指標に必須。k3s は既定同梱）。
- 全対象サービスに `resources.requests.cpu` が定義済みであること（HPA の利用率計算の分母。定義済み）。
- **Istio サイドカーの CPU 算入（既知の考慮事項）**: 本チャートは `mesh.enabled: true`（Envoy サイドカー自動注入）で、
  HPA の `metrics` は `type: Resource`（Pod 内**全コンテナ横断**の平均使用率）である。そのため Envoy サイドカーの
  CPU request/使用量も利用率計算の分母・分子に混入し、目標 70% の判定精度がアプリコンテナ実使用率からずれ得る
  （過小/過大スケール）。必要に応じて `autoscaling/v2` の `ContainerResource` 型でアプリコンテナ（`<name>-service`）
  のみを対象にする選択肢がある。この妥当性は負荷試験の確認項目とし、乖離が大きければ `ContainerResource`
  への切替を検討する（HPA/PDB の適用対象を定めた実装 ADR のフォローアップ）。
- 実クラスタでの HPA スケール挙動・目標 CPU 値の妥当性は負荷試験で検証し、`scaling.hpa` を調整する。

## 監視・アラート（NFR / #198）

可観測性スタック（OTel Collector → Prometheus / Loki / Tempo → Grafana）を配備済み。アプリは OTLP で
メトリクス/ログ/トレースを送出し、Collector が Prometheus（remote write）/ Loki / Tempo へ振り分ける。NFR
「障害検出 5 分以内・MTTR 30 分以内」に対し、SLO ベースのアラートルールを Prometheus に定義する。

- **アラートルール**: [`deploy/prometheus/alerts.yml`](../../deploy/prometheus/alerts.yml)（`prometheus.yml` の
  `rule_files` で読み込む）。**通知経路**は Alertmanager（`prometheus.yml` の `alerting`）。
  **［2026-08-30 更新 / #546］Alertmanager は compose・経路B の両方へ配備済みで、`alertmanagers.targets` も
  2 か所とも埋まっている。発火は Alertmanager まで届く**（実測。下記★）。**受信先＝メール/チャットは
  運用環境ごとに設定するもので、既定は `default-null`＝どこへも送らない**（設定漏れではなく既定）。
- **暫定のアラート（Grafana 統合アラート。#665 / 計画 決定 42）**:
  [`deploy/grafana/provisioning/alerting/slo-alerts.yaml`](../../deploy/grafana/provisioning/alerting/slo-alerts.yaml)
  が同じ 22 ルール（所有者の読み取りのポリシーの 2 件を足して数え直した。［2026-09-26 / #1577］で数え直した。部門の同期の 1 件が足された後も 19 のままだった。
  #1544 の時点は 19、#1111 の時点は 17、その前の「13」は既に実体の 16 と食い違っていた）を
  Grafana 側でも評価し、**Alerting 画面に発火を表示する**。**通知は送らない**（下記★）。
  `alerts.yml` との対応は `node scripts/check-grafana-alerting.js` が CI で突合する。
- **★ 経路間のパリティ（#674。Grafana provisioning は経路間で同内容とする実装 ADR）**: provisioning（datasources / dashboards / alerting）は
  **compose と k8s の両方に同内容で置く**。`node scripts/check-grafana-provisioning-parity.js` が突合する。
  **是正前は k8s 側にダッシュボードが 1 枚も無く、下記 `llm-usage.json` へ経路 B から辿り着けなかった。**
- **ダッシュボード**: `deploy/grafana/provisioning/dashboards/microservices-platform-overview.json`（サービス別
  スループット・5xx 率・p99・RAG レイテンシ・**RAG 初回応答 p95**）と
  [`llm-usage.json`](../../deploy/grafana/provisioning/dashboards/llm-usage.json)（**LLM の金額・トークン消費量・
  呼び出し回数**。**［2026-08-23］費用のパネルを追加した**）。
- **LLM 費用の統制（暫定）**: **上限アラートは月次予算の金額が設定された時点で有効となる。それまでは月次の手動確認である**
  （計画 決定 39〜41 / #546 / #1111）。手順・担当・記録は
  [`llm-cost-monthly-review-runbook.md`](llm-cost-monthly-review-runbook.md) が定める。
  **［2026-08-23］費用の金額が出るようになった**（用途別・モデル別のトークン消費量と金額換算。
  換算はゲートウェイが**有効期間つき単価表**を読んで行い、Grafana のクエリには単価を書かない）。
  🔴 **金額は「単価を解決できなかった呼び出し」が 0 のときだけ正しい** —— 該当する単価が無い呼び出しは
  金額に計上されないため、**0 でなければ表示は過小である**（無音で 0 円にしないための警報である）。
  **［2026-08-30 更新 / #546］Alertmanager を配備しても、自動検知が無いことと検知の遅れが最大 1 か月で
  あることは変わらない。** 🔴 **理由が変わっただけである** —— 配備前の理由は「通知基盤が無い」だったが、
  いまの理由は**月次予算のしきい値が計画側で未確定**だからである（計画 決定 41。実測を待って確定する）。
  **しきい値が無いものにアラートは置けない。** したがって月次の手動確認は**引き続き唯一の統制**であり、
  終了しない（終了条件は「配備」ではなく「配備 **かつ** 上限アラートの配線」である）。
  🔴 **［2026-09-26 更新 / #1111］配線は入った。自動検知はまだ働いていない。** 用途別の上限アラート
  `LlmMonthlyBudgetExceeded`（直近 30 日の `llm_cost_total` と、ゲートウェイが出すゲージ
  `llm_budget_monthly_limit` を用途・通貨で比べる）が compose・経路B の Prometheus と Grafana に入っている。
  **ただし金額（`Llm:Budget:MonthlyLimits`）は既定を持たず未設定であり、そのあいだゲージの系列が無いので
  アラートは評価対象を持たず発火しない。** 金額を決めるのは所有者であり（実装側は数字を置かない）、
  **金額を初めて設定する変更が同じ変更で Runbook を `superseded` にする**（併存させない。食い違いは
  `node scripts/scripts.test.js` が CI で落とす）。設定の置き場・系列の確かめ方・意図的な発火の手順は
  [Runbook](llm-cost-monthly-review-runbook.md) §金額を設定する手順（所有者）が定める。
  **終了条件は「配備 かつ 配線 かつ 金額の設定」となり、残るのは金額だけである。**
- **ピン留めモデルの版数移行と利用不能時の振る舞い**: 用途別にピン留めした LLM モデルの版数を上げる手順
  （**Stage 0 再検証が前提**）と、**モデルが使えないときは取引判断を実行せず発注もしない**（**障害ではなく
  設計上の正常な結果**）ことは [`llm-model-pin-runbook.md`](llm-model-pin-runbook.md) が定める（#587。報告書の種別別用途と取引判断モデルの改定を定めた実装 ADR の決定 3）。
  **提供終了の監視は月次の費用確認に相乗りする**（自動検知は無い。検知の遅れは最大 1 か月）。
- **適用範囲（現状）**: Prometheus/アラートルール（`deploy/prometheus/alerts.yml`）と可観測性スタックは
  **dev の 2 経路（docker-compose と、ローカル k8s の可観測性オーバーレイ）に配線**されている。
  **［2026-08-30 更新 / #546］経路B（ローカル k8s）にも Alertmanager を配備し、両経路のルールが
  同じ受け手へ届くようにした**（それ以前は compose だけだった）。
  🔵 **［2026-09-09 更新］経路B の Prometheus の inline は compose と同数である**（両経路とも 22 件。［2026-09-26 / #1544］［2026-09-26 / #1573］と、所有者の読み取りのポリシーの 2 件を足したときに数え直した。
  `node scripts/check-prometheus-alerts-parity.js` が群名・ルール名・`expr`・`for`・`severity` で 1 対 1 を
  確かめる）。**2026-09-05 時点の「2 件が写されていない」はその後の是正で解消しており、本追記はその訂正である。**
  **件数は導出値なので、数えるのは実体である。****stg/prod は依然として対象外**である
  （`deploy/helm/microservices-platform/` 配下に Prometheus / Alertmanager リソースは無い）。
  展開は follow-up（下記「未決事項」）。本節のアラート定義・閾値は環境非依存に流用できる。

> **★ 通知先の現状（#546 / #665 / 計画 決定 40・42）**: 下表の**ルールは Prometheus が実際に評価しており、
> 発火は Alertmanager まで届く**（2026-08-30 に配備。`alertmanagers.targets` は compose・k8s の 2 か所とも
> `['alertmanager:9093']`）。**ただし Alertmanager から先へは、まだ誰にも届かない。**
> 既定の受信先は `default-null`＝**どこへも送らない**である（設定漏れではなく既定。実装 ADR の決定 2）。
> **「通知先」列はいま働いている経路ではない。**
>
> **したがって気づき方は「Alertmanager の画面（`/#/alerts`）または Grafana の Alerting 画面を見る」ことである。**
> **非機能要件「障害検出 5 分以内」を満たしているのは評価の側だけ**であり、
> **人が気づくまでの時間は見に行く間隔に等しい。** ここは配備前から変わっていない。
>
> 計画が定めた**暫定の通知先＝ Grafana の内蔵アラート**（決定 42）は、**#665 で provisioning を配線した**
> （[`deploy/grafana/provisioning/alerting/slo-alerts.yaml`](../../deploy/grafana/provisioning/alerting/slo-alerts.yaml)。
> compose・k8s の 2 か所。22 ルールは `alerts.yml` と 1 対 1。以前ここにあった「19」は、20 に増えた後も直っていなかった）。**ただし、配線したのは検知と可視化までである。**
>
> - **push 配信の宛先（contactPoints / policies）は設定していない。** 届かない宛先を書くと「配線した」と
>   読めてしまうため、**意図的に書いていない**（SLO の暫定通知先を Grafana 統合アラートへ配線する実装 ADR の決定 3）。
> - **したがって暫定期間に人が気づく経路は「Grafana の Alerting 画面を見る」ことだけ**である。
>   **非機能要件「障害検出 5 分以内」を満たしているのは評価の側だけ**であり、
>   **人が気づくまでの時間は見に行く間隔に等しい。**
> - **Grafana が provisioning を受理するかは、CI では見ていない。** 機械で確かめているのは
>   `node scripts/check-grafana-alerting.js` の範囲（ルール数・名前の 1 対 1・`datasourceUid` の実在・
>   compose と k8s の同内容・必須キー・式の絞り込みの後に残る値で評価器が真になり得ること。**読めない式は「任意の値」として通さず報告し**、評価器とクエリの対応は `condition` から refId で辿る。provisioning は YAML として読み（数の読み方は配備の Grafana が使う YAML ライブラリに合わせる。`010` は 8 進で 8）、読めなければ違反にする。**決して値を返さない式**も、健全に認識できる形に限って止める。評価器の型は配備の版が受け付ける `gt` / `lt` / `within_range` / `outside_range` だけを通す）まで。**配備時に `/api/v1/provisioning/alert-rules` が 20 件返すことを確かめる。**
>   🔵 **［2026-09-04 更新］稼働クラスタでは受理された** —— `reload` の後に当時の 9 件が返ることを実測した
>   （「実装環境で Grafana を起動できない」という以前の記述は、もう当てはまらない）。
>   **ルールを増減させたら毎回確かめること**（件数は導出値である）。
>
> **★ 暫定経路を閉じる条件（併存させない）**: **可観測性の計画 ADR は改めない**（アラートは Alertmanager を用いる）。
> 次の 3 つが揃った時点で、**`deploy/grafana/provisioning/alerting/` を削除する**。
> **［2026-08-30 更新 / #546］3 つのうち 1 と 3 は満たした。2 が残っているので、まだ削除しない。**
>
> | # | 条件 | 状態 |
> | ---: | --- | --- |
> | 1 | `prometheus.yml`（compose・k8s の**両方**）の `alertmanagers.targets` に到達可能な Alertmanager がある | ✅ **満たした**（`/api/v1/alertmanagers` が `activeAlertmanagers` を 1 件返す） |
> | 2 | Alertmanager 側に受信先（メール/チャット）が設定され、**テスト通知が実際に届いた** | 🔴 **満たしていない。** 既定は `default-null`＝どこへも送らない。**実環境の宛先は利用者が決める事柄**であり、実装側で代替値を置けない |
> | 3 | 下表のルールの発火が Alertmanager 経由で通知されることを**1 件以上、実際に確かめた** | ✅ **満たした**（`OtelCollectorDown` の発火が Alertmanager の `/api/v2/alerts` に `active` として現れた。**合成ルールではなく下表の実ルールである**） |
>
> **条件 2 が満たされるまで併存させる。** 本来は避けたい状態だが、**暫定側（Grafana）にも宛先が無い**ため
> **二重通知は構造的に起き得ない** —— 併存を禁じた理由（重複通知が「既知の誤報」の習慣を生む）は現時点では働かない。
>
> **併存させない理由**: 同じルール群が 2 系統で評価されると**同じ事象に対して 2 通の通知が出る**。
> 重複は「片方は既知の誤報だ」という運用習慣を生み、**本物の通知を握り潰す方向に働く。**
> 削除の際は `scripts/check-grafana-alerting.js` も併せて削除する（対象ファイルが消えると門 A で fail するため、
> **残したままにはできない** ——「暫定を消し忘れる」ことが CI で表面化する）。

| 監視対象 | 指標（メトリクス） | 閾値 | 通知先（Alertmanager までは到達。**その先は未配線**） | 対応 NFR |
| --- | --- | --- | --- | --- |
| 可観測性パイプライン | `up{job="otel-collector"}`（唯一の scrape 対象） | ==0 が 2 分 | Alertmanager（critical） | 検出 5 分以内 |
| サービス応答断（近似） | `rate(http_server_request_duration_seconds_count)` の途絶（`job` 別・直近まで受信有） | 0 が 5 分 | Alertmanager（warning） | 可用性 99.9% |
| HTTP エラー率 | 5xx 率 = `http_server_request_duration_seconds_count{http_response_status_code=~"5.."}` 比率（`job` 別） | > 5% が 5 分 | Alertmanager（critical） | 可用性 99.9% |
| 検索レイテンシ | retrieval-service p95（`http_server_request_duration_seconds_bucket`） | > 1.5（**秒**）が 10 分 | Alertmanager（warning） | 検索 p95 1.5s |
| **RAG 初回応答（SLO 判定）** | aianalysis `/analysis/ask/stream` の**初回トークンまでの時間**（`rag_answer_first_token_duration_seconds_bucket`）p95 | > 5（**秒**）が 10 分 | Alertmanager（warning） | **RAG 初回応答 p95 5s** |
| RAG 応答完了（**傾向の観察に留める**） | aianalysis `/analysis/ask`（一括経路）の応答完了 p95 | > 5（**秒**）が 10 分 | Alertmanager（warning） | — （**判定に用いない**） |
| **LLM 費用（用途別の月次予算）** — ［2026-09-26 / #1111］ | 直近 30 日の `llm_cost_total` ＞ ゲージ `llm_budget_monthly_limit`（用途・通貨ごと） | 超過が 5 分（**金額は未設定＝不活性**） | Alertmanager（warning） | — （費用の統制。金額が設定されるまでは Runbook の月次確認） |
| **パスワードリセット申請の床の器（全滅）** — ［2026-09-26 / #1544］ | `up{job="reset-floor"}`（収集器が器の **Service** の `/metrics` を 30 秒ごとに取る。準備のできた器が 0 なら届かず 0） | ==0 が 2 分（**器が 1 つでも準備完了なら鳴らない**） | Alertmanager（critical） | 検出 5 分以内・可用性 99.9% |
| **評価対象の不在** — 収集経路 | `absent(up{job="otel-collector"})` | 系列が無い状態が 5 分 | Alertmanager（warning） | 検出（**統制**） |
| **評価対象の不在** — 全サービスの HTTP メトリクス | `absent(http_server_request_duration_seconds_count)` | 系列が無い状態が 5 分 | Alertmanager（warning） | 検出（**統制**） |
| **評価対象の不在** — 検索レイテンシ | `absent(http_server_request_duration_seconds_bucket{job="…retrieval-service"})` | 系列が無い状態が 5 分 | Alertmanager（warning） | 検出（**統制**） |
| **評価対象の不在** — RAG 応答完了（一括経路） | `absent(http_server_request_duration_seconds_bucket{job="…aianalysis-service", http_route="/analysis/ask"})` | 系列が無い状態が 5 分 | Alertmanager（warning） | 検出（**統制**） |
| **評価対象の不在** — 床の器（全滅の行が見る系列） — ［2026-09-26 / #1544］ | `absent(up{job="reset-floor"})` | 系列が無い状態が 5 分 | Alertmanager（warning） | 検出（**統制**） |

> 🔴 **［2026-09-26 追記 / #1544］床の器の 2 行。** リセット申請の POST は床の器だけへ向かい、認証基盤へ戻る予備の経路は無い。
> **準備のできた器が 0 になると申請はすべて 503（申請を閉じた状態）になる。** それを知らせるのが「全滅」の行である。
> 収集器（OTel Collector）が器の Service 越しに**器自身が答える `/metrics`** を取る —— 準備のできた器が 1 つでもあれば
> Service がそこへ振り分けて `up` は 1、0 なら接続が拒まれて 0 になる（エッジが 503 を返す条件と同じ情報源を見ている）。
> 器は 2 レプリカなので、**1 つ落ちただけでは鳴らない。** 検出はおよそ 3 分（収集 30 秒 ＋ 評価 ＋ `for: 2m`）。
> 「評価対象の不在」の行は、収集器の受け口（receiver）が欠けて `up` の系列ごと消えた状態を拾う（そのあいだ全滅の行は鳴りようがない）。
> **Prometheus を直接 scrape する対象は増やしていない**（唯一の scrape 対象は収集器のまま）。対応は下の「障害対応」の表。
> 🔴 **稼働クラスタでは未確認である**（器を 0 へ絞って `firing` になるまでの時間を測る手順は[パスワードリセットのテスト仕様書](../tests/SC-15_password-reset.md)の手動項目）。
> 🔴 **compose 経路には床の器が居ない**ので、compose では不在の行が鳴り続ける（近接 MTA の不在の行と同じ既知の状態）。
>
> 🔵 **［2026-09-04 追記 / 2026-09-05 更新］下 5 行は「評価対象そのものが無いこと」を鳴らす。上の行の SLO とは別物である。**（［2026-09-26 / #1544］床の器の不在の行を足して 4 → 5 行）
>
> **「サービス応答断（近似）」の行と読み分けること。** あちらは
> **直近まで受信していたのに途絶した**場合だけを拾い（`== 0` かつ `offset 15m > 0`）、
> **系列そのものが消えると式が空になって発火しない。** 2026-08-30 まで 4 ルールが
> 存在しないメトリクス名を見ていた事故は、**下 5 行の形でしか検知できない。**
>
> 🔴 **対象は「無風でいられる時間が検知要件（5 分）より短い経路」だけである。**
> 全 SLO へ対で置くと低頻度経路で恒常発火し、**警報を無視する習慣を作る**ため、計画がその案を却下している。
>
> 🔵 **［2026-09-05 更新］RAG の扱いが 2 つに割れた。従前ここには「`/analysis/ask` 系（RAG の 2 行）は
> 対象外。合成監視で常時トラフィックを作ってから対象へ入れる」と書いてあった。**
> **合成監視は着地し、計画が実行間隔（常時トラフィック用 60 秒・SLO 評価用 60 分）まで確定させたので、
> 繰り延べの条件は満たされた。** ただし満たされたのは**片方だけ**である。
>
> | RAG の行 | いまの扱い | 理由 |
> | --- | --- | --- |
> | **RAG 応答完了（一括経路）** | ✅ **対象へ入れた**（表の「評価対象の不在 — RAG 応答完了」の行） | 60 秒間隔の合成プローブが `/analysis/ask` を叩く。**LLM を呼ばなくても検索までは走る**ので HTTP 系列は立つ。60 秒 × 5 ＝ 5 分の余裕があり `absent()` の既定 lookback に収まる |
> | **RAG 初回応答（SLO 判定）** | 🔴 **依然として対象外** | 初回トークンの計器は**トークンが 1 件も出なければ記録しない**。系列を立てるのは LLM を呼ぶ 60 分間隔の合成であり、**それは未配備である**（下の未決事項）。いま置くと**恒常発火**になる。配備後に入れる形は `absent_over_time(…[2h])`（周期 60 分の 2 周期ぶん）である |
>
> 🔴 **これらの行が意味を持つのは、合成監視のオーバーレイが当たっているクラスタだけである**（opt-in）。
> 当てていないクラスタで RAG 応答完了の行が鳴るのは**誤報ではなく「評価対象が本当に無い」状態**である。
>
> 🔴 **下 5 行の検知は最大およそ 10 分であり、「5 分以内」ではない。** `absent()` は瞬間ベクタ選択子の
> 既定 5 分 lookback が空になって初めて真になり、そこへ `for: 5m` が積まる。
> **これが拾うのはサービス障害ではなく統制の欠落**であり、障害の 5 分検知は上の行が担う。
> `for` を短くして早める案は採らない（恒常発火の回避を優先している）。
>
> **Grafana 版は下 5 行と床の器の「全滅」の行が `noDataState: OK` である**（全滅の行は系列の不在を対の不在の行へ任せ、同じ不在で 2 通鳴らさないため）。不在の行は、`m` が存在するとき `absent(m)` が空ベクタを返す
> （＝正常時が「データ無し」）ため、他と同じ `NoData` にすると**正常時に恒常発火する。**
>
> 🔴 **［2026-09-26 / #1577］「可観測性パイプライン」と「サービス応答断（近似）」の 2 行も、Grafana 版は `noDataState: OK` である。**
> 両行の Grafana 版は、それまで Prometheus 版の式（`== 0` の絞り込み）をそのまま写して `gt 0` で比べていた。
> 絞り込みの後に残る値は 0 なので `0 > 0` が偽になり**永久に発火せず**、正常時は式が空になって逆に `NoData` が立っていた。
> いまは「可観測性パイプライン」が `up` をそのまま取って `lt 1` で比べ（全滅の行と同じ形）、
> 「サービス応答断（近似）」が `== bool 0`（途絶で 1）を `gt 0` で比べる。空になるのは系列の不在か
> 「直近まで受信していたサービスが無い」ときで、前者は下 5 行が拾い、後者は途絶ではないので `OK` にしている。
> **この組み合わせ（絞り込みで 0 だけが残る式と、0 で真にならない評価器）は `node scripts/check-grafana-alerting.js` が CI で止める。**
>
> **実測済み**（2026-09-04・ローカル k8s。**当時この群は 3 行であり、RAG 応答完了の行はまだ無い**）。
> 転送だけを切って系列を途切れさせると
> **「全サービスの HTTP メトリクス」と「検索レイテンシ」の 2 行が 9 分台で `firing` へ到達し、
> Alertmanager に `active` として現れた。**
> 同じ窓で**「サービス応答断（近似）」・「HTTP エラー率」・「検索レイテンシ」はすべて `inactive` のまま**であり、
> **既存ルールでは検知できない状態が実在する**ことも同時に確かめている。
> 収集経路の行は同じ窓で `inactive` のままであった（`up` は scrape が作るので残る＝陰性対照）。

> 🔴 **［2026-09-03 更新］RAG 回答の SLO 判定は「初回応答」を測る計器へ移した。**
> それまでこの表は `/analysis/ask` の**応答完了 p95** を「RAG 初回 5s」の指標として載せていたが、
> **応答完了と初回応答は別物であり、しかも画面が実際に使うのは SSE 経路 `/analysis/ask/stream` である。**
>
> **SLI の定義は変えていない**（要求の数値も据え置き）。変えたのは**計器の側**である ——
> AiAnalysisService が要求受領から最初の `token` イベントを書き出すまでの秒数を記録する。
> **`token` が 1 件も出なかったストリームは記録しない**（初回トークンが無かったことを「速かった」として積まない）。
>
> **応答完了 p95 の行は残してある。ただし SLO の判定には用いない（傾向の観察に留める）。**
> 応答完了を SLI にすると**長い回答ほど SLO 違反になり、回答品質を上げると SLO が悪化する**
> —— 指標として逆向きの誘因を持つため、計画がその案を却下している。
>
> 🔴 **この指標は呼ばれない限り系列を持たない。** ダッシュボードのパネルが空でも「速い」ではなく
> **「まだ誰も質問していない」**である。
>
> 🔵 **［2026-09-04 更新］系列の不在を warning とする規則は入った（上表の下 3 行）。ただし
> この経路は対象外である。** 理由が変わったので書き直す —— **規則が無いからではなく、
> この経路の無風が 5 分を超え得るから**である（対象へ入れると恒常発火する）。
> **したがって RAG の 2 行については、無風時に「鳴らない」と「鳴りようがない」の区別が依然として付かない。**
> 区別を付けるには**合成監視で常時トラフィックを作る**しかない。下の未決事項に残す。
>
> 🔴 **当時の 5 ルールのうち 4 件は、2026-08-31 まで一度も発火し得なかった**（上表は #1204 で 6 行、#1202 で 9 行になった）。
> `up{job="otel-collector"}` を見る 1 件を除く 4 件が、**Prometheus に一度も存在したことのない**
> メトリクス名（`http_server_duration_milliseconds_*`）を参照していた。**式は構文として正当**なので
> Prometheus はエラーを出さず、ルールは `health: "ok"` / `state: "inactive"` のまま静かに評価され続けていた。
> **「アラートが設定されている」ことと「アラートが発火しうる」ことは別である。**
>
> ずれは 4 種類あった —— **名前**（旧 HTTP セマンティック規約の `http.server.duration`）／
> **ラベル軸**（`service_name` はアプリ由来のメトリクスに付かない。付くのは `job` である）／
> **ラベル名**（`http_status_code` → `http_response_status_code`）／**単位**（ミリ秒 → **秒**）。
> **ダッシュボードも同じ名前を使っており、5xx 率・p99・RAG レイテンシのパネルは空だった。**
> アラートだけ直すと、**運用者が空のグラフを見て「異常なし」と記録する**状態が残るため、同時に直した。
>
> **Alertmanager の束ね（`group_by`）と抑止（`inhibit_rules.equal`）も同じラベルを使う。**
> 片方だけ `job` へ移すと束ねが全サービス 1 群へ潰れるため、`deploy/alertmanager/alertmanager.yml`
> と経路B の inline も同時に直してある。
>
> **式を書き換えるときは、稼働 Prometheus に対して参照先が実在することを 1 件ずつ確かめること。**
> 手順（`/api/v1/series` での問い合わせと、**「0 件だった」を「無い」と読む前に置く陽性対照**）は
> `deploy/prometheus/alerts.yml` の冒頭コメントが正本である。**静的な機械検査は置いていない**
> —— 式が参照する名前が稼働 TSDB に存在するかは、リポジトリの静的検査では原理的に判定できない。
>
> 🔵 **［2026-09-04 追記］稼働環境の側には器が入った**（上表の「評価対象の不在」3 行）。
> **静的検査の代わりにはならない。** 稼働側は**壊れてから最大 10 分で**気づき、
> 静的検査は**変更を出す時点で**気づく —— **検知できる時点が違う。**
> 静的検査の新設は「同型の事故の 2 回目」まで繰り延べたままであり、計画もその判断を覆していない。

### LLM 拒否率の監視（LLM 送信先切替の要求 / 非機能要件 / #395）

LlmGateway は補完 1 回ごとに `llm.completion.total`（Prometheus では `llm_completion_total`）を計上する。
**送信可否（`llm.result`）とモデル側の終了理由（`llm.stop_reason`）は独立した属性**であり、
「機密区分により送信しなかった（`egress_denied`）」と「送ったがモデルが拒否した（`refusal`）」を
取り違えずに集計できる（`stop_reason` の判別と拒否の伝達を定めた実装 ADR）。

- **拒否率** = `sum(rate(llm_completion_total{llm_stop_reason="refusal"}[30m])) / sum(rate(llm_completion_total{llm_result="sent"}[30m]))`
- 属性・値域・クエリ例・しきい値の方針は
  [`docs/observability/llm-completion-metrics.md`](../observability/llm-completion-metrics.md) を参照する。
- 監視観点の目安（初期値・実測前）: 全体の拒否率 > 5%（30 分・warning）／用途別の拒否率 > 20%（30 分・warning）／
  `upstream_error` 率 > 10%（10 分・critical）／`llm.purpose="other"` の出現（1 時間・warning。
  未定義 purpose＝ルーティングが既定へ無音で落ちている疑い）。
- **［2026-08-18 追記 / #863］`llm.result` に `fallback` が加わった**（計画 `ADR-0038` 決定 6 /
  用途別フォールバック順序は設定駆動の鎖として持ち、発火は 400 系に限り 429 を除外する、という実装 ADR による）。
  **上流が HTTP 400 系を返して次の候補モデルへ切り替えた呼び出し**を表す。
  **`upstream_error` には含まれない** —— 回復した呼び出しを障害の率に入れると上の critical が誤発火する。
  **429 ではフォールバックしない**（429 は再試行の対象。同決定 4）ため、429 は従来どおり
  `upstream_error` に現れる。フォールバック率のしきい値は**実測前のため置かない**。
- **［2026-09-05 追記 / #1091］`llm.upstream_status` が加わった**（`none` / `rate_limited` /
  `client_error` / `server_error` / `transport` / `other` の 6 値）。**`llm.result` とは独立した軸**で、
  「基盤側が何をしたか」と「上流が何を返したか」を別々に読む。**429 が他の失敗と区別できる**ように
  なった —— `sum by (llm_upstream_status) (rate(llm_completion_total{llm_result="upstream_error"}[30m]))`。
  **上のしきい値の式も数値も変えていない**（429 を除きたいときだけ `llm_upstream_status!="rate_limited"`
  を足す）。設定ミス（`other`）と通信障害（`transport`）は別の値である —— 混ぜると直す対象を取り違える。
  🔴 **「429 が起きていない」と読むときは、同じ期間に `llm_upstream_status="none"` の系列が実在する
  ことを陽性対照として対で示す**（Prometheus は起きていないラベル値を 0 として持たないため、
  空ベクタは「起きていない」とも「計器が動いていない」とも読める）。
- **アラートルールの実配線は未了**（`deploy/prometheus/alerts.yml` への追加と Alertmanager 通知先の設定）。
  本節はしきい値の方針までを定める（補完の終了理由メトリクスの実装 ADR §フォローアップ 1）。

- **push モデルの制約**: メトリクスは remote write（push）のため、古典的な per-service `up` は無い。サービスダウンは
  「直近まで受信していたリクエストメトリクスの途絶」で近似検知する（アイドル時の誤検知を避けるため `for` を長めに設定）。
  厳密なダウン検知は blackbox exporter / k8s の liveness による補完を follow-up とする。
- **follow-up（exporter/カスタムメトリクス配線後に有効化）**: RabbitMQ キュー滞留・デッドレター（RabbitMQ
  Prometheus プラグイン）、構成ドリフト Warning（ドリフト検出のカスタムメトリクス化。現状は監査/警告ログで表出）。
  `alerts.yml` 末尾にコメントで雛形を用意。

### 合成監視（synthetic）の運用（非機能要件: 可観測性 / #1203）

低頻度の経路（`/analysis/ask` 系）へ一定間隔で代表リクエストを打ち、SLO の**評価対象そのもの**を
存在させる常駐プローブである。**クラスタ内で完結し、外部の監視 SaaS は使わない。**

配備物と手順は `deploy/local/synthetic-monitor/README.md` に置く（`docs/` の外なのでリンクは張らない）。
**既定ではプローブは立たない（opt-in）。**
🔵 **［2026-09-09 更新］ローカル起動器（`scripts/k8s-local-up.sh`）に `SYNTHETIC=1` の門を用意した。**
門は「標識を除外の 3 サービスへ与える → 3 サービスが揃うのを待つ → プローブを配備する」の順で動き、
**揃わなければプローブを配備せずに起動器ごと落ちる**（「除外できない構成では配備しない」の機械化）。
既定をオンにする切り替えは同スクリプトの 1 行（`SYNTHETIC_DEFAULT`）に集めてある。
🔴 **本番構成（helm チャート）にはまだ入っていない。** そこへの投入は**イメージの再ビルドと稼働クラスタ**が
要るため、実装側だけでは完了できない。

🔴 **標識と除外が揃っていない構成では配備しない。** 合成トラフィックが利用実績・費用・検索傾向へ
混ざると、**それらの指標が「人が使った量」を表さなくなる。** 除外はコード側（BFF・DashboardService・
LlmGateway）に在るため、**当該イメージが更新済みであることを確かめてから当てる。**

| 項目 | 現在の扱い |
| --- | --- |
| **標識** | 専用の Keycloak クライアント（`synthetic-monitor`）で `client_credentials` 認証し、**検証済み JWT の主体**で判定する。**受信ヘッダは外周では一切見ない**（外から印を付けて費用計上を免れる経路を作らない） |
| **除外先** | 利用状況・検索傾向（利用イベントの発火の口と受け口）／ LLM のトークン累計・金額換算 |
| **除外の可視化** | `usage_event_dispatch_total{usage_event_outcome="excluded_synthetic"}` と `llm_usage_synthetic_excluded_total` |
| **実行頻度** | 🔵 **［2026-09-05 更新］計画が確定させた（従前は「未確定」）。2 段である** —— **常時トラフィックの生成＝60 秒**（LLM を呼ばない）／ **SLO 評価用＝60 分**（LLM を呼ぶ）。**配備済みなのは 60 秒側だけである。** 配備時に `PROBE_INTERVAL_SECONDS` で与える点は変えない（**実装は既定値を持たない**。未設定ならプローブは起動しない） |
| **費用の上限** | 🔵 **［2026-09-05 更新］計画が確定させた（従前は「未確定」）。絶対額のしきい値は置かず、間隔で実質的に固定する** —— 60 分間隔＝月 720 回であり、費用は事前に計算できる（概算 月約 4,400 円）。**ただし現状の配備では 60 分側が未着手であり、`AllowLlmEgress` の既定は `false` のままなので、恒常的に発生する費用は依然として 0 である** |
| **配備の口** | 🔵 **［2026-09-09 追加］ローカル起動器の `SYNTHETIC=1`**（既定はオフ）。前提の投入（realm クライアントの追随・標識の env・プローブの資格情報）と順序を門が持つ。手で当てる手順も従来どおり残っている |
| **停止手順** | `kubectl -n microservices-platform scale deploy/synthetic-monitor --replicas=0`（次の間隔を待たずに止まる）。恒久的に外すなら `kubectl delete -k deploy/local/synthetic-monitor` ＋ Secret の削除 |

🔴 **除外は指標を守るためのものであり、費用そのものを減らさない。**
`excluded_synthetic` が伸びていて `sent` が伸びていないときは、**実利用が 0 である。**

🔴 **初回応答の SLO については、これでもまだ評価対象が生まれない。** 初回トークンの計器は
最初のトークンが 1 件も出て初めて記録する設計であり、**合成が実際に LLM を呼ばない限り系列が立たない。**
🔵 **［2026-09-05 更新］理由が変わった。** 従前の理由は「頻度と費用の上限が計画側で未確定だから」であったが、
**計画は 60 分間隔で LLM を呼ぶ合成を認める裁定を下した。** いま空いているのは
**その 60 分側の配備がまだ行われていないから**であり、**裁定待ちではなく実装の残作業である。**
あわせて、**60 分間隔の標本で初回応答 p95 が成立するよう判定窓（`rate()` の範囲と `for`）を広げること**が
実装に課されている（**検知遅延の許容上限は 8 時間**。具体値は実測で定めて計画へ環流する）。

## データ保持期間（利用イベント）

利用イベント（検索実行・AI 回答生成の 1 行）は **90 日を超えて保持しない**。
90 日は**画面から照会できる最大期間**そのものであり、それより古い行はどの照会からも読まれない。

**利用イベントは利用者識別子を持たない。** 残るのは種別・検索語・発生時刻の 3 つである。
受け口は認証必須のままであり（認証済みでなければ記録できない）、変わったのは
**解決した主体を列へ書かないこと**だけである。

| 項目 | 値 |
| --- | --- |
| 対象 | `dashboard` DB の `UsageEvents` テーブル |
| 保持期間 | **90 日**（画面の集計期間の上限と同じ値。**別々には変更できない**） |
| 削除の主体 | `dashboard-service` の常駐処理（起動直後に 1 周、以後は既定 6 時間ごと） |
| 削除の基準時刻 | 集計の起点と同じ 1 点（UTC の日境界）。**基準時刻ちょうどの行は残る** |
| 構成 | `UsageRetention__Enabled`（既定 `true`）／ `UsageRetention__IntervalMinutes`（既定 `360`） |

- **保持期間は構成キーを持たない。** 集計の上限を変えるときだけ、コードの同じ 1 つの定数が動く
  —— 片方だけ動かすと、**画面からは照会できるのに行が無い期間**が生じるためである。
- **間隔の不正値（0 以下）でサービスは落ちない。** 既定へ倒し、起動時に警告を残す。
  **ログに出る周期は倒した後の値**である（実際の周期と食い違わせない）。
- **削除は 1 周あたり 500 行ずつ**進み、上限に達した周はその場で続きを消す。
  初回適用時（古い行が溜まった DB）に全件をメモリへ載せない。

### 失敗時の見え方

| 事象 | 見え方 | 対応 |
| --- | --- | --- |
| 1 周が例外で落ちた | `dashboard-service` のエラーログ（`利用イベントの保持期間の削除で例外が発生した`）。**サービスは動き続け、次の周期で再試行する** | ログの原因（多くは DB 到達性）を解消する。行は次周で消える |
| 掃除が無効化されている | 起動ログに `保持期間の削除は無効である` が出る。**行は無期限に残る** | 意図した構成か確認する。既定は有効である |
| 削除が走っているか確かめたい | 削除した周だけ `保持期間（90 日）を過ぎた利用イベントを N 件削除した` が出る（**0 件の周は出さない**） | 出ていないときは、古い行がそもそも無いか、掃除が無効かのどちらかである |

**画面（運用ダッシュボード）の表示は保持期間の影響を受けない** —— 照会の上限が 90 日であり、
消える行は最初から画面に出ていなかった行だからである。

## バックアップ・リストア（NFR / #198）

状態を持つデータストアを対象にバックアップを取得し、復旧手順を定める。RPO/RTO は運用要件に応じて確定する
（初期目安 RPO ≤ 24h・RTO ≤ MTTR 30 分。重要度に応じ日次〜時間次へ調整）。

| 対象 | 内容 | 方式（例） | 頻度 | 保管 |
| --- | --- | --- | --- | --- |
| PostgreSQL（各サービス DB） | 業務データ（DB per Service。document / datasource / authorization / feedback / dashboard 等） | `pg_dump`／論理レプリケーション／ボリュームスナップショット | 日次（重要 DB は時間次） | 世代管理（例 7 日 + 週次 4） |
| Qdrant | ベクトル索引 | Qdrant スナップショット API（コレクション単位）。※ 索引は再取り込みで再構築可能（決定的チャンク ID）＝ RPO 緩め | 日次 or 再構築前提 | 直近数世代 |
| オブジェクトストレージ（SeaweedFS） | 正規化本文・資産（`knowledge-normalized` バケット） | S3 互換の同期ツール（`aws s3 sync` 等）でのバケット複製／ボリュームスナップショット | 日次 | 世代管理 |
| Wiki.js DB（PostgreSQL `wikijs`） | Wiki 閲覧コンテンツ（同期の従。正本は本システム側） | `pg_dump`。※ DocumentUpdated 再同期で再構築可能 | 日次 | 直近数世代 |
| Keycloak realm | 認証設定（クライアント・ロール・マッパー） | realm export（`deploy/keycloak/*-realm.json` を単一の真実源に、IaC で再適用） | 変更時（Git 管理） | Git 履歴 |

- **リストア手順（概略）**: ①対象データストアを停止/隔離 → ②該当バックアップからリストア（Postgres は
  `pg_restore`、オブジェクトストレージは複製からの書き戻し、Qdrant はスナップショット復元）→ ③依存サービスを再起動しヘルス確認 →
  ④整合確認（Qdrant/Wiki は必要なら `DocumentUpdated` 再発行で再構築。埋め込み再索引は本書「埋め込みプロバイダ」節参照。
  再発行の手段は同節の「`DocumentUpdated` の再発行（再索引の手段）」）。
- **リストア演習**: ステージング整備後に定期実施し、RTO の実測と手順の妥当性を検証する（follow-up）。

### ローカル環境（`deploy/local`）で稼働しているもの

- **platform-infra の Postgres（全 DB と globals）と Vault（file ストレージ）は、日次で age の公開鍵へ暗号化して
  本機の C: と E: の 2 か所へ置く。** 永続化の既定と一緒に CronJob が入る（JST 12:00 / 12:15）。保持は日次 30 世代、
  各月の最初の回と切替前の回は 7 年。準備（公開鍵と保管先の目印）・日々の確認・リストア試験（四半期と切替前）の手順は
  [platform-infra-backup-runbook.md](platform-infra-backup-runbook.md)。
- 🔴 **ローカル開発・PoC 環境専用であり、上の表の本番方式を置き換えない**（保管先は同じ筐体でオフサイトではない）。
- 🔴 **現状（2026-10-02）: 稼働 PoC ではバックアップは停止中であり、1 本も取れていない。** 2 本の CronJob は前提
  （バックアップのイメージと age の受取人）が揃うまで `suspend: true` で置かれている。揃っていない前提は次の 2 つである。
  - age の受取人（鍵の生成は利用者の操作）。
  - バックアップのイメージ（ビルドが資格情報ヘルパーの失敗で通っていなかった）。ベースの取得元を、匿名の取得に認証の
    チャレンジを返さないミラー（`mirror.gcr.io/library`。中身は digest で固定したまま）へ替え、資格情報ヘルパーを呼ばずに
    作れるようにした。稼働 PoC での作り直しはまだである。

  再開の順序は「受取人を作る → イメージを作り直す → 起動スクリプトを再実行して有効化 → 手動 Job を 1 回走らせてリストア試験」。
  コマンドつきの手順は [platform-infra-backup-runbook.md](platform-infra-backup-runbook.md) の「2. 日々の確認」冒頭の注記にある。
  再開を確かめたら、この行を消す。

## 障害対応（Runbook）（NFR / #198）

| 事象 | 検知 | 一次対応 | エスカレーション |
| --- | --- | --- | --- |
| LLM ゲートウェイ/外部 LLM 不調 | RAG レイテンシ/5xx アラート、`LlmGateway` 縮退ログ | RAG は縮退応答（送信せず縮退・fail-closed）。検索（非 LLM）は継続。エンドポイント設定/疎通確認 | 外部プロバイダ障害なら egress 設定でセルフホスト/別ティアへ切替 |
| 埋め込みプロバイダ停止 | 取り込み失敗ログ、`EmbeddingEndpointTests` 相当の縮退 | 高機密は埋め込まず語彙索引へ書くので影響を受けない（［2026-10-05］）。public / internal の恒久的な拒否は索引スキップ（fail-closed）。プロバイダ復旧後に再索引（本書「埋め込み」節） | セルフホスト基盤の起動、モデル/次元整合の確認 |
| RabbitMQ 停止 | サービス接続エラー、パイプライン滞留 | ブローカ再起動。MassTransit は再接続。未処理は再配信（冪等消費のため重複安全） | 永続化ボリューム/ディスク確認。デッドレター滞留は原因メッセージを調査 |
| Qdrant 停止 | 検索 5xx/エラーログ | Qdrant 再起動。索引は再取り込みで再構築可能（決定的チャンク ID） | ボリューム障害時はスナップショットからリストア（バックアップ節） |
| PostgreSQL 停止 | サービス起動失敗/DB 接続エラー | DB 再起動・接続確認。書き込み不可の間は該当サービスを縮退 | データ破損時はバックアップからリストア（RPO/RTO 節） |
| パスワードリセット申請の床の器が全滅（申請がすべて 503） | `ResetFloorNoReadyEndpoint` アラート（critical）。`ResetFloorUpSeriesAbsent` は「見ていない」（収集器の受け口の欠落）であり全滅ではない | 🔴 **503 は「申請を閉じた状態」であり、床を外さない**（本番で `RESET_FLOOR=0` を退路に使わない —— 外している間は所要時間で利用者名を列挙できる）。器を戻す: `kubectl -n platform-infra get deploy,pdb,pods -l app=reset-floor`・ログ・ConfigMap `reset-floor-script` の有無を見て直す。利用者は**管理者による一時パスワード発行**で復旧する。手順は [運用 Runbook](keycloak-smtp-relay-setup-runbook.md) の「器がすべて落ちたとき」 | 器が戻らない（イメージ取得・ノード資源・PDB による退避の停止）ならノードと Deployment の事象を確認する。全滅が繰り返すならレプリカ数・分散の見直しを計画へ環流する |
| 所有者の読み取りのポリシーが消えた（所有者が自分の文書を読めない） | `OwnerReadPolicyMissing` アラート（critical）と認可サービスの Error ログ。`OwnerReadPolicyCheckSeriesAbsent` は「見ていない」（認可サービスの停止・ポリシーの表を読めない）であり、消えたことではない | 本書「所有者の読み取りのポリシーの投入」の確かめ方で状態を見て、無ければ手順で投入し直す（無効なら有効へ戻す）。削除そのものは止めていない。誰が消したかはポリシーの API が記録していないので、管理者の間で確かめる | 繰り返し消されるなら、管理者の操作の手順（削除の前の確認）を見直す。投入し直しは管理者設定画面から行える（所有者の条件は動的束縛で選ぶ） |
| MCP クライアント登録簿と認証基盤が食い違った（判定に使われる属性が画面の表示と違う） | `McpClientIdpDrift` アラート（warning）と MCP サーバーの Warning ログ（`client=… kind=…`）。`McpClientIdpReconciliationSeriesAbsent` は「見ていない」（MCP サーバーの停止・照合の失敗の継続）であり、食い違いではない | 本書「MCP クライアント登録簿と認証基盤の照合」の種類ごとの対応。照合は直さないので、画面からの差し替え（または認証基盤の残骸の削除）で揃える。認証基盤の管理画面で属性を直接割り当てない | `attributes_differ` が繰り返すなら、認証基盤の管理イベントで直接の操作の主体を探し、管理者の手順を見直す。2 つの差し替えの交差が原因なら、行の排他の導入を起票する |
| サービス 5xx スパイク | `HighHttp5xxRate` アラート | 対象サービスのログ/トレース（Tempo）で原因特定。必要ならロールバック（Git revert → ArgoCD 同期） | 依存（DB/ブローカ/外部）起因の切り分け。HPA 上限到達なら `scaling` 見直し |
| 構成ドリフト検出 | ドリフト検出 Warning（監査/警告ログ） | 宣言（`pipeline.json`）と実効の差分を確認。意図せぬ差分は Git を正として再同期 | 起動時 fail-fastで不整合構成の反映は阻止済み。恒常化は宣言の是正 |

### 秘密情報を 1 項目だけコンソールから投入する（画面が使えないときの退避手段）（NFR / #1411）

**秘密情報の既定の投入面は製品の画面である**（`/admin/secrets`。運用者・システム管理者だけが開ける。
画面の仕様は [秘密情報・接続設定の管理](../screens/SC-22_secret-item-management.md)）。
画面は 1 回に 1 プロパティだけを保管先（Vault）へ部分更新で書き、値は読み出せない。
境界層（BFF）の保管先への書き込み権限は `deploy/local/vault/eso/policy-bff-secret-write.hcl`（項目ごとの完全一致パス）と
role `bff-secret-writer`（BFF 専用 ServiceAccount `bff` にだけ束縛）であり、`deploy/local/vault/eso/bootstrap.sh` が入れる。
**保管先が配備されていない構成（`VAULT=1 ESO=1` でない）では画面は「接続できません」を出す**（静かに成功しない）。

画面が到達不能・障害中・保管先の権限が未配備のときに限り、
コンソールから**その項目のパスだけ**を書く退避手順を
[`secret-item-console-injection-runbook.md`](secret-item-console-injection-runbook.md) が定める。

稼働クラスタで画面 → 保管先 → 同期 → Secret → 消費側の作り直しが成立することを 1 回の作業で確かめる手順（画面の手動の試験 T-40）は
[`secret-item-live-sync-check-runbook.md`](secret-item-live-sync-check-runbook.md) にある（試験値を書いて長さとキー名だけを見て、保管先の版を戻す）。

🔴 **一括再投入（`deploy/local/vault/eso/bootstrap.sh`）を 1 項目の修正に使わないこと。**
2026-09-10 の稼働作業で、同スクリプトが env で値を渡さなかった項目を**既定値で上書きする**ことが判明し、**手順書に無い回避策**がその場で判断された。
今は保管先の KV を**無いときだけ作る**（画面が書く KV も、対になる秘密の KV も）ので、稼働中の保管先の値は上書きされない。
それでも `keycloak-smtp` の構成値は毎回揃え直され、起動器の手動の Secret 作成は一部の Secret を env か既定値で書き直すので、1 項目の修正には使わない。

**投入してよい項目の集合は `deploy/bootstrap/sc22-secret-items.json` が持つ**（`items[]` だけが対象）。
**`deferred[]`（認証基盤のクライアントシークレット）・`excluded[]`（データストアの資格情報）・`ast-app-secrets` の `*-auth-client-*` は「対になる秘密」であり、画面の対象外である** ——
相手（認証基盤・データストア）と同時に変えないと認証が壊れ、画面の 1 欄では変えられない。Git には置かず、相手と保管先を対で書く運用手順で回す。
境界は性質であり、同じ性質の秘密を足すならこちらへ入れる。

🔴 **コンソール操作は画面の監査ログに乗らない。** Runbook の「記録」節に従い、**使った事実を必ず残すこと。**
**記録先は #458 へのコメントただ 1 か所**である（保管先の audit には画面以外の root トークンの行までしか残らず、人と理由はこの記録で残す）。
［2026-09-28 追記］保管先（Vault）の audit には「画面以外の主体が、いつ・どの項目のどのプロパティへ書いたか」が残る（値は残らない。抽出の条件は [セキュリティ仕様書](../security/security.md)の「保管先（Vault）の audit」）。共有の root トークンで書くため人と理由は残らず、Runbook の記録は引き続き要る。

**秘密情報を新しい値へ回す（ローテーション）手順**は別の Runbook
[`secret-rotation-runbook.md`](secret-rotation-runbook.md) が定める。上の退避手段は「差し替え」であって回転ではない ——
回転は分類ごとに扱いが違い（画面から回せる `items[]`／相手と保管先を対で書いて回す対になる秘密）、
`scripts/k8s-local-up.sh` の再実行が回した値を元へ戻し得る経路まで含めて同書が扱う。
対になる秘密の手順（書く順序と、途中で止まったときの戻し方）は [`paired-secret-rotation-runbook.md`](paired-secret-rotation-runbook.md) にある。
**本番の client シークレットを realm の宣言（`deploy/keycloak/microservices-platform-realm.json`）に書かない** —— 宣言が持つのは開発用の値だけで、client を作るときにだけ使われる。

### メッシュ設定のドリフトと、helm リリースが固まったときの復旧（NFR / #1159）

サービスメッシュの `PeerAuthentication` / `AuthorizationPolicy` / `DestinationRule` は **helm チャートの
描画物**であり、稼働の値を書いてよいのは helm だけである。モードの切り替えは
`scripts/lib/mesh-mtls-mode.sh` の `set_mesh_mtls_mode`（内部で `helm upgrade` を呼ぶ）を使う。

🔴 **`kubectl patch` / `kubectl apply` で直接書かないこと。** Helm 4 はサーバサイド apply を使うため、
外から書くとフィールドの所有者（field manager）が helm から奪われ、**以後の `helm upgrade` が
conflict で必ず失敗する**。`--take-ownership` も `--force` も効かない（後者はサーバサイド apply と
併用できない）。結果として `scripts/k8s-local-up.sh` は helm の段で止まり、**再実行しても収束しない。**

| 事象 | 検知 | 一次対応 |
| --- | --- | --- |
| 宣言と稼働の mTLS モードが食い違う／`spec` を helm 以外が書いている | `node scripts/check-stack-ready.js --live` の門 G12 が対象を名指しして落ちる | 下の復旧手順。以後はモードを `set_mesh_mtls_mode` で切り替える |
| `helm upgrade` が `conflict with "kubectl-patch" … .spec.mtls.mode` で失敗する | 起動スクリプトが helm の段で停止する | 同上（値を戻すだけでは直らない。所有権が残っているため） |

復旧（対象を消して helm に作り直させる。ダウンタイムは秒単位で、その間は名前空間の既定 = 平文許容になる）:

```sh
kubectl -n microservices-platform delete peerauthentication microservices-platform-mtls
helm upgrade msp deploy/helm/microservices-platform -n microservices-platform --reuse-values
kubectl -n microservices-platform get peerauthentication microservices-platform-mtls \
  -o yaml --show-managed-fields | grep -A1 'manager:'   # helm 以外が居ないこと
```

サービス間の gRPC（h2c・8081）の往復を稼働クラスタで PERMISSIVE / STRICT の両方について実測する手順（REST の east-west を退役させる前の観測点。#1255 / #1517）は
[east-west gRPC（h2c）往復の実測 Runbook](east-west-grpc-h2c-roundtrip-measurement-runbook.md) にある。モードの切り替えは上と同じく `set_mesh_mtls_mode` だけを使う。

### 検索が全件 0 件になる（読み書き先コレクションの乖離・全文索引の欠落）（非機能要件: 可観測性 / #1215）

検索はベクトル DB の**単一のコレクション**しか読まない。取り込みが書くコレクションは埋め込みモデルごとに
分かれているため、**モデルの切り替えを片側にだけ入れると読み先と書き先が食い違う**。このとき
取り込みも検索も健全で `Ready` のまま、**検索だけが全件 0 件**になる。

🔴 **応答では区別できない。** 検索の `200 ＋ 空` は「該当が無い」と同じ形であり、状態コードでも
readiness でも捕まらない。**「当たっている」ことも索引の証拠にならない** —— 全文ペイロード索引が
無いとき、ベクトル DB は例外を返さず部分文字列の全走査へ静かに落ちる（語順を替えると 0 件になる）。

| 事象 | 検知 | 一次対応 |
| --- | --- | --- |
| 点は在るのに検索が全件 0 件 | `node scripts/check-stack-ready.js --live` の門 **G13** が「点が在るのは X なのに検索側が読む Y は 0 点」と名指しして落ちる | 埋め込みの向き（プロバイダの有効化）を**取り込み・検索・ゲートウェイの 3 サービスすべて**に入れ直す。片側だけ入れると再発する |
| 全文ペイロード索引が無い／パラメータが違う | 同 **G13** が対象のコレクションとキーを名指しして落ちる | 取り込みサービスを再起動する（起動時に**存在の有無によらず**索引を張り直す。冪等） |
| 対象範囲フィルタの候補（タグ・部門・プロジェクト）が空、または候補の照会がエラーになる | 取り込みサービスのログに `Failed to ensure Qdrant keyword payload index`（書き込み時の Warning）／`Failed to ensure Qdrant collection / full-text payload index at startup`（起動時の Error。キーワード索引の失敗も同じ文言）／`Failed to ensure keyword payload indexes for attribute keys on existing points`（発見の Error）。検索サービスのログに `has no keyword payload index`（Warning。そのキーを持つ点が無いときと、索引がまだ構築されていないときに出る）。取り込みサービスの `Failed to scan Qdrant collection`（発見の Warning。そのコレクションだけ飛ばして続ける）。ベクトル DB の `GET /collections/<name>` の `payload_schema` に `tags`・`shared_with`・`attributes.<key>`（`keyword`）が在るかを見る。G13 はキーワード索引を見ない | 取り込みサービスを再起動する（起動時に集合値キーを張り直し、起動後のバックグラウンドで既存の点の属性キーを走査して張る。冪等）。走査の完了は `Ensured N keyword payload index(es)` のログで確かめる |
| どのコレクションにも点が無い | 同 **G13** は notice に落とす（まだ何も取り込んでいない状態と区別できないため）。`SEARCHSEED=1` を宣言した実行では赤になる | 本文つきの文書が在るかを先に見る（本文の無い文書は索引に載らない） |

確かめ方（**稼働コレクションには読み取りしか行わない**）:

```sh
node scripts/check-stack-ready.js --live            # 門 G13 を含む全門
# 全文索引そのものの挙動（使い捨てコレクションで陽性・陰性の対）を測る場合:
kubectl -n platform-infra port-forward svc/qdrant 6333:6333
QDRANT_URL=http://localhost:6333 bash scripts/verify-qdrant-fulltext-index.sh --live
```

- **エスカレーション/通知**: **Alertmanager の配備後**に受信先（メール/チャット）と担当・当番を運用体制に応じて定める（環境ごと）。
  **配備までは自動通知が無い** —— 一次検知は Prometheus UI / Grafana の目視である。
- **MTTR 目標（30 分）**: アラート（検出 5 分以内）→ Runbook 一次対応 → 復旧、の各段を Grafana/Tempo/Loki で追跡する。

### 統合スタックの所要時間の判定（T-25）が赤になった —— 偶然の赤の確かめ方と月次の記録（NFR / #1617）

統合スタック（`integration-stack.yml`）のパスワードリセットの門は、実在／非実在の申請の所要時間を
順位和検定（両側・有意水準 1%。反復 3〔1 回目は暖機〕× 片側 12 標本）で比べる。
**系統差が無くても約 100 回に 1 回は赤になる**（偶然の赤）。赤はすべて CI の失敗の自動起票
（`ci-failure` ラベル・マーカー `ci-failure:integration-stack`）で issue になる。
**起票の条件は置かない。確かめの最初の手順は、同じコミットでの 1 回だけの再実行である。**

| 事象 | 検知 | 一次対応 |
| --- | --- | --- |
| T-25 だけが赤（ほかの門はすべて緑） | 自動起票の issue。`stack` の手順「T-25 only red (chance-red candidate)」が success | **自動**: `integration-stack-rerun.yml` が同じ実行を 1 回だけ再実行し（失敗したジョブの再実行＝同じコミット・同じ定義）、再実行の結果を同じ issue へ書く。人が行うのは下の「閉じる前の確かめ」だけ |
| T-25 と別の門も赤 | 同上（「T-25 only red」は skipped） | **偶然の赤として扱わない。** 自動の再実行もしない。ほかの門の失敗を先に調べる |
| 自動の再実行が起きない・結果が書かれない | issue に再実行のコメントが無い／`rerun` ジョブが赤 | 手で 1 回だけ再実行する: `gh run rerun <run id> --failed --repo endazon/microservices-platform`。結果（両方の attempt の p と W・実行の URL）を issue へ書く |

**偶然の赤として issue を閉じる前の確かめ**（自動のコメントがあっても人が行う）:

1. 再実行（同じ実行の attempt 2）が**合格**したこと。
2. 🔴 **同じ実行（attempt 1）で、ほかの門が失敗していないこと。** `gh run view <run id> --attempt 1` の手順一覧で、
   パスワードリセットの門以外の門と 2 つの投入がすべて成功していること。パスワードリセットの門の中でも、
   失敗が `[T-25][所要時間] … 順位和検定（両側）で p=…` の 1 件だけであること（`--log-failed` で見る）。
   自動の再実行はこの条件を満たしたときにしか起きないが、手で再実行したときは自分で確かめる。
3. 🔴 **issue に、ほかの実行の失敗が積まれていないこと。** マーカーはワークフロー単位なので、issue が開いている間に落ちた
   ほかの実行（本物の失敗を含む）も、同じ issue へのコメントとして集まる。ほかの実行の失敗があれば、それを片付けるまで閉じない。
4. 記録（両方の attempt の p と W・実行の URL）が issue に残っていること（自動のコメントに入る。手で再実行したときは書く）。そのうえで閉じる。

- **再実行も赤なら偶然の赤ではない**（偶然の赤が同じ条件で 2 回続くのは約 1 万回に 1 回）。床の値の引き直しの契機として調べ、
  **計画へ環流する**。issue は閉じない。
- **再実行は赤の実行ごとに 1 回まで**（再実行の再実行はしない）。自動の再実行が完了しなかったとき
  （待機中に後続の push の実行へ置き換えられて取り消された等。同時実行は 1 本）に限り、手で 1 回だけやり直す。
- **手動実行（`workflow_dispatch`）の赤は自動で再実行しない** —— `istio=false` の床なしの比較実行があり得るからである。
  床を入れた既定の手動実行が赤なら、上と同じ手順を手で行う。
- **偶然の赤と確かめた実行は、「直近 7 日間の定期実行がすべて合格」の窓で不合格に数えない。**

**月次の記録**: 月に 1 回、`node scripts/t25-monthly-summary.js --month <YYYY-MM>` を実行し（GitHub の Actions の API を読むだけ。
稼働クラスタへは触れない）、出力を順位和検定の実装の意思決定記録への日付つき追記として残す。数えるのは
検査まで届いた件数・不合格（うち偶然の赤／再実行も赤／再実行なし）・評価不能・p の中央値・一様分布からの KS 距離（と目安 1.36/√n）である。
**計画へ環流するのは、KS 距離が目安を超えたときと、再実行も赤が 1 件以上あったときだけである。**

## 定期点検（年次）

### 採用ライブラリのライセンス・保守状況の点検（バックエンド標準ライブラリの計画 ADR のフォローアップ / #455）

計画側のバックエンド標準ライブラリの ADR は
**年 1 回、採用ライブラリのライセンス・保守状況を点検し、同 ADR の選定基準で再評価する**ことを求めている。
2025〜2026 年に MediatR / AutoMapper / MassTransit / FluentAssertions が相次いで商用化し、Mapster が保守停滞に
陥った経緯があり、**ライセンス変更は予告なく起こる**という前提で点検する。

| 項目 | 内容 |
| --- | --- |
| 実施時期 | 毎年 7 月（同計画 ADR の起票月に合わせる） |
| 重点対象 | **AwesomeAssertions**（FluentAssertions v7 のコミュニティフォーク。上流分裂のリスクが残る）、**Wolverine**（比較的新しい選択） |
| 併せて見る | Riok.Mapperly・FluentValidation・Scrutor・Scalar・Testcontainers・Respawn（いずれも棚卸し表の採用分） |
| 点検内容 | ①ライセンスの変更有無 ②直近 12 か月のリリース有無 ③未解決の重大 issue ④.NET の次期メジャーへの追随状況 |
| 判定 | 同計画 ADR の選定基準（ライセンス持続性 / 標準機能優先 / 層の依存規律 / ソースジェネレータ親和）で再評価する |
| 逸脱時 | 置き換えが必要なら実装 ADR（IADR）を起こし、計画側へ `/plan-feedback` で環流する（棚卸し表は計画が正） |

不採用ライブラリの混入は `scripts/check-backend-libraries.js` が CI で継続的に止めるため、本点検は
**「採用したものが採用に値し続けているか」**だけを見る。残件（`scripts/backend-library-baseline.json`）の
消化状況もあわせて確認する。

### インフラ製品の点検（基準 A〜D）（選定基準の計画 ADR / #1787）

計画側のインフラ製品の選定基準の ADR は、**採用済みのインフラ製品を年 1 回と契機ごとに点検する**ことを求め、
管理用の口の基準を足した計画 ADR が点検に基準 D を加えた。本節はその手順・記録先・初回の記録である。
上の「採用ライブラリ」の点検（アプリケーション層）とは対象が違う —— こちらは `deploy/` が配備する**インフラのイメージ**と、
自製イメージの**基底イメージ**・統合試験の**Testcontainers のイメージ**（`src/`）を見る。

| 項目 | 内容 |
| --- | --- |
| 実施時期 | **毎年 7 月**。計画が定めるのは「採用ライブラリの年次点検と同じ時期に行う」ことであり、7 月という月は、上の採用ライブラリの点検に合わせて本リポジトリが選んだ |
| 契機（時期を待たずに行う） | ①イメージの**取得の失敗**（CI の結合テスト・integration-stack・経路 B の起動が pull で落ちた）②上流リポジトリの **archived** ③**ライセンス変更の告知** ④新しいインフラ製品の採用（選定時に同じ基準を当てる）⑤**基底イメージ**の上流のセキュリティ修正の告知（.NET の月例のサービシングリリース・node・caddy の修正版）—— 基準の点検は要らず、digest の解決し直しだけを行う |
| 母集合 | **`node scripts/check-image-digests.js --list`** の出力（製品ごとの tag・固定の有無・参照箇所）。**手で列挙しない** —— `deploy/` と `src/`（基底イメージ・Testcontainers）の参照から機械的に引く。自製イメージ（`microservices-platform/*`・`k3d-local/*`）は対象外 |
| 母集合の外（未決） | `scripts/` が chart / マニフェストで入れる製品（Istio・External Secrets・Reloader・cert-manager・Argo CD・k3s）は `deploy/` に参照が無く、`--list` に現れない。引くときは `grep -nE 'helm upgrade --install\|kubectl apply .*https' scripts/*.sh`。点検の対象に入れるかは計画へ確認中（初回の記録の「残り」） |
| 記録先 | **本節の「点検の記録」**（回ごとに日付つきの小見出しを足し、過去の回は消さない）。判断を伴う変更（差し替え・例外の追加）は実装 ADR に残す |
| 逸脱時 | **基準 A・B・D を満たさない製品は差し替えずに記録し、計画へ環流する**（差し替えは計画の裁定。`/plan-feedback`）。基準 C で既定の外部通信が見つかったら、配備へ無効化を入れる issue を起こし、計画のデータ外部送信方針の統制表への追加を環流する |

#### 基準と確かめ方

| 基準 | 見るもの | 確かめ方（初回の実績） |
| --- | --- | --- |
| **A** ライセンスの持続性 | OSS ライセンスが続いているか。上流の保守が止まっていないか（archived 等） | 固定した版のタグの `LICENSE` を読む（`https://raw.githubusercontent.com/<owner>/<repo>/<tag>/LICENSE`）。イメージの OCI ラベル `org.opencontainers.image.licenses` があれば併記する。保守は上流のリリースとイメージの再ビルド日（config の `created`）で見る |
| **B** 配布の持続性 | 匿名で取得できるか。digest で固定されているか | 下の「digest の解決と更新」の手順で**匿名で 2 回解決して一致する**こと。固定は `check-image-digests.js`（CI）が見る |
| **C** 既定の外部通信 | テレメトリ・更新確認・外部アセット取得が既定で有効か。設定で無効化できるか。**配備に無効化が入っているか** | 上流文書の既定値と、配備側の設定（compose・`deploy/local/`・helm）を突き合わせる。可能なら通信の捕捉で実測する |
| **D** 既定で開く管理用の口 | 構成・身元・権限を変えられる口（管理 API・管理用 gRPC・管理画面）が既定で開くか。閉じられるか。閉じられないなら認証を必須にでき、到達を制限できるか | 上流文書の既定値と、配備側のポート公開・NetworkPolicy・認証の設定を突き合わせる |

#### digest の解決と更新

docker のデーモンは要らない。**匿名のトークン → manifest の HEAD → `docker-content-digest`** で multi-arch の index の digest を読む。

```bash
# Docker Hub（library/ の公式イメージは repository:library/<name>）
TOKEN=$(curl -sS "https://auth.docker.io/token?service=registry.docker.io&scope=repository:library/redis:pull" | jq -r .token)
curl -sSI -H "Authorization: Bearer $TOKEN" \
  -H 'Accept: application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json' \
  https://registry-1.docker.io/v2/library/redis/manifests/7-alpine | grep -i '^docker-content-digest'
# ghcr.io / quay.io は 401 の WWW-Authenticate が示す realm（例 https://ghcr.io/token?scope=repository:<repo>:pull）から同じ形で取る
```

- 応答の `content-type` が **index / manifest list** であることを確かめる（単一 manifest の digest を固定すると、別アーキテクチャの手元で起動しない）。
- **tag と digest を対で変え、`--list` が示す全参照を同じ値に揃える**（片側だけ変えると `check-image-digests.js` が落とす）。
  qdrant は統合試験の定数（`QdrantTestImage.Reference`）も同じ参照に揃える（PR の CI の定義試験が突き合わせる）。
  postgres（`16-alpine`）と node（`22-alpine`）も `--list` に統合試験・frontend の Dockerfile の行が並ぶので、同じ値に揃える。
- `mcr.microsoft.com` と `mirror.gcr.io` はチャレンジを返さないので、トークン無しで同じ HEAD を送る
  （`https://mcr.microsoft.com/v2/dotnet/aspnet/manifests/10.0`・`https://mirror.gcr.io/v2/library/caddy/manifests/2.11-alpine`）。
  frontend の基底は `mirror.gcr.io/library` から引くので、Docker Hub と同じ digest が返ることも確かめる。
- 例外（digest を付けられない参照）は `scripts/image-digest-exceptions.json` に `file`・`ref`・`reason` で足す。直ったら外す。

#### Harbor へのミラー

**Harbor の配備後に行う。配備までは本点検の対象外である**（計画が「配備した後」と定めている。Harbor は未配備）。
未配備のあいだ、上流の配布が止まると digest で固定していても取得できない（検知は契機①の取得の失敗に頼る）。

#### 点検の記録

##### 2026-10-08（初回。#1787）

- 母集合: `--list` で **21 製品・42 参照**（自製 21 参照は対象外）。点検の前に固定済みだったのは seaweedfs と backup イメージの `FROM` の 3 参照だけで、**本回で 42 参照すべてを digest で固定した**（例外 0 件）。
- 基準 C・D は**上流文書の既定値と配備側の設定の突き合わせ**であり、通信の捕捉による実測はしていない。
- 基準 B は全製品で**匿名取得でき、index の digest を 2 回解決して一致した**（TEI の `cpu-1.5` だけは index が `linux/amd64` のみ）。下表では B 列を省く。

| 製品（tag → 実体） | A ライセンス・保守 | C 既定の外部通信（配備の状態） | D 既定で開く管理用の口（配備の状態） | 判定 |
| --- | --- | --- | --- | --- |
| postgres（`16-alpine` → 16.15） | PostgreSQL License | 無し | 無し（SQL の口は認証必須） | 適合 |
| rabbitmq（`3.13-management-alpine` → 3.13.7） | MPL-2.0。⚠️ 3.13 系はコミュニティ保守の外で、イメージは 2025-12-02 以降再ビルドが無い | 無し | management の UI / API（15672）。認証必須。compose はホストへ公開 | 適合（⚠️ 系列の保守。4.x への移行を次回の点検で見る） |
| **redis（`7-alpine` → 7.4.11）** | 🔴 **RSALv2 / SSPLv1**（7.4 以降。OSI の OSS ではない） | 無し | 既定で認証が無く管理コマンド（`CONFIG`・`FLUSHALL` 等）が通る。認証（`requirepass` / ACL）と到達制限は掛けられる。**配備は認証なし**（compose はホストへ 6379 を公開） | 🔴 **A 不適合 → 環流** |
| keycloak（`24.0` → 24.0.5） | Apache-2.0。⚠️ 24 系は保守の続く系列ではない | 無し | 管理コンソール・管理 REST（`/admin`。認証必須）。経路 B のエッジは `/` を出すので `/admin` も届く（既知。admin entrypoint の TLS 化の別件） | 適合 |
| qdrant（`v1.18.1`） | Apache-2.0 | 🟡 **テレメトリが既定で有効**（`QDRANT__TELEMETRY_DISABLED` で無効化できる）。**配備は未設定** | REST / gRPC が管理（コレクション削除・スナップショット）を兼ね、既定で認証なし。API キーで必須にでき、到達は絞れる。**配備は認証なし**、経路 B はエッジ（`qdrant.localhost`）へ素通しで出している | 適合（配備に C・D の穴） |
| seaweedfs（`4.47`） | Apache-2.0 | テレメトリが既定で有効 → `-master.telemetry=false` で**無効化済み** | 管理用 gRPC（18333）→ 起動ごとの署名鍵と NetworkPolicy で**塞ぎ済み** | 適合 |
| opentelemetry-collector-contrib（`0.102.0`） | Apache-2.0 | 無し（zpages / pprof 拡張は未設定） | 無し（8888 は自己メトリクスの読み取り） | 適合 |
| prometheus（`v2.52.0`） | Apache-2.0 | 無し | 管理 API（`--web.enable-admin-api`）と lifecycle は既定で無効、**配備も無効**。remote-write の受け口は有効（書き込み口。管理用の口ではない） | 適合 |
| alertmanager（`v0.27.0`） | Apache-2.0 | 無し | API v2 で silence の作成・削除が既定で認証なし。web config で認証を掛けられ、到達は絞れる。**配備は認証なし** | 適合（配備の注意） |
| loki（`3.0.0`） | AGPL-3.0 | 🟡 **利用統計の送信（`analytics.reporting_enabled`）が既定で有効。配備は未設定** | `auth_enabled: false`。運用の口（`/flush` 等）は製品単体で認証を掛けられず、前段（メッシュ・プロキシ）で掛ける形 | 判定保留（D の読み方を計画へ確認） |
| tempo（`2.5.0`） | AGPL-3.0 | 🟡 **利用統計の送信（`usage_report.reporting_enabled`）が既定で有効。配備は未設定** | loki と同じ | 判定保留（同上） |
| grafana（`11.0.0`） | AGPL-3.0 | 🟡 **利用統計の送信・更新確認・プラグインの更新確認が既定で有効。配備は未設定** | 管理 UI（認証必須。経路 B は OIDC・匿名無効）。**compose は匿名に Admin を与えている**（dev の利便。ホストへ 3000 を公開） | 適合（配備に C の穴・compose に D の穴） |
| wiki（`2.5`） | AGPL-3.0。イメージは 2026-10-02 に再ビルド | テレメトリは初期設定で `false`、ロケールの同期（Graph）も初期設定で止めている | 管理画面（認証必須） | 適合 |
| text-embeddings-inference（`cpu-1.5`） | Apache-2.0 | 起動時に Hugging Face Hub からモデルを取得する（既定は無効の opt-in。有効化の前にモデルの事前配置かミラーが要る） | 無し | 適合（有効化の前提に記録） |
| postfix（boky、`v5.1.0`） | MIT（Postfix 本体は IPL / EPL） | 無し（上流の中継先を必須にし、宛先 MX への直接配送を塞いでいる） | 無し | 適合 |
| mailpit（`v1.21.8`） | MIT | 🟡 最新版の確認（GitHub への問い合わせ。無効化の設定がある）。**配備は未設定** | Web UI / API（8025）が既定で認証なし（メールの閲覧・削除）。認証を掛けられる。dev 限定 | 適合（dev 限定） |
| headlamp（`v0.43.0`） | Apache-2.0（上流は kubernetes-sigs/headlamp へ移った） | 既知の既定の外部通信は無い（未実測） | UI 自体が k8s の管理画面。OIDC と閲覧専用の RBAC | 適合 |
| **vault（`1.16` → 1.16.3）** | 🔴 **BUSL-1.1**（1.15 以降。OSS ではない） | 無し | API は token 必須。dev モード（root token 固定）は経路 B の opt-in 限定 | 🔴 **A 不適合 → 環流** |
| node（`22-alpine`）・busybox（`1.37`）・curl（`8.11.1`） | MIT・GPL-2.0・curl License | 無し | 無し（試験・プローブ・初期化の道具） | 適合 |

**残り（次の点検または別 issue）**:

- 🔴 **redis と vault の差し替え**は計画の裁定を待つ（環流）。差し替えまでは現行の版を digest で固定して使う。
- 🟡 **基準 C の無効化が配備に入っていない 4 製品（qdrant・loki・tempo・grafana）と mailpit**は、配備へ無効化を入れる別 issue と、計画の統制表への追加の環流を要する。
- 基準 D の「認証を必須にできる」に前段の認証（メッシュ・プロキシ）を含めてよいか（loki・tempo・otel-collector・alertmanager）を計画へ確認する。
- `scripts/` が入れる製品（上の「母集合の外」）は本回の母集合に入っていない。**Argo CD は `stable` ブランチのマニフェストを直接 apply しており版すら固定されていない。**

## 未決事項

- **Alertmanager の受信先設定**: **［2026-08-30 更新 / #546］Alertmanager 本体は配備済みで、
  `alertmanagers.targets` は compose・k8s の 2 か所とも埋まっている**（発火が Alertmanager へ届くことは
  意図的に閾値を割って実測した）。**残っているのは受信先（メール/チャット）だけ**であり、
  既定は `default-null`＝どこへも送らない。**実環境の宛先は利用者が決める事柄**であり、実装側で代替値を置かない。
  **暫定の通知先（Grafana 内蔵アラート）は #665 で配線済み**だが、**push 配信の宛先は依然として無い**
  （本書「監視・アラート」の★参照。気づく経路は Alertmanager / Grafana の画面を見ることだけ）。
  **受信先が設定されテスト通知が届いた時点で暫定経路を削除する**（併存させない。条件は同★）。**#546 で追跡している。**
- **LLM 費用の自動検知**: **配線はあるが、金額が未設定のため働いていない**（［2026-09-26 更新 / #1111］。
  従前ここは「無い」と書いていた。上限アラートは配線済みで、金額 `Llm:Budget:MonthlyLimits` は既定を持たず
  所有者が設定する。未設定のあいだアラートは評価対象を持たず発火しない。**残る未決は金額だけ**であり、
  設定する変更が同時に Runbook を `superseded` にする）。以下はそれ以前の経緯である。
  **［2026-08-30 更新 / #546］理由は「通知基盤が無い」ではなくなった** ——
  Alertmanager は配備済みであり、**残る障害は月次予算のしきい値が計画側で未確定であること**である
  （計画 決定 41。実測を待って確定し、確定の前提は費用の実績が数か月分そろうこと）。
  **検知の遅れは最大 1 か月**であることを受け入れ、月次の手動確認を暫定の統制として置いている
  （計画 決定 39 / [Runbook](llm-cost-monthly-review-runbook.md)）。
  🔴 **しきい値が定まるまで金額は置かない。** 実装が数字を決めると、それが既成事実として
  計画へ逆流する（計画が明示的に禁じている）。［2026-09-26 / #1111］**配線だけを先に置いた**のは、
  所有者が「金額を所有者が設定する値にできるなら配線してよい」と裁定したためである —— 配線は数字を持たず、
  金額が無いあいだ何も評価しない。
- **監視の stg/prod（k3s）展開**: Prometheus/Alertmanager を Helm（Operator 等）で配備し、`alerts.yml` 相当の
  ルールと通知を k3s にも展開する（**［2026-08-30 更新 / #546］現状は dev の 2 経路のみ**。
  本番像の chart には Prometheus / Alertmanager / Grafana のリソースが無い）。
- **RabbitMQ キュー滞留・デッドレター・構成ドリフトのアラート**: それぞれ RabbitMQ Prometheus プラグインの
  exporter メトリクスと、ドリフト検出のカスタムメトリクス化が必要（`alerts.yml` 末尾に雛形をコメントで用意）。
- **サービスダウンの厳密検知**: push（remote write）モデルのため per-service `up` が無く、メトリクス途絶での
  近似検知に留まる。blackbox exporter / k8s liveness による補完を検討する。
- **合成監視（synthetic）**: **［2026-09-05 更新（2 度目）］標識と除外、配備物（opt-in）は入っており、
  かつ ①〜③ の裁定も下りた。** 従前ここには「①実行頻度と費用の上限が計画側で未確定 ②初回応答の
  SLO の評価対象が生まれない ③意図的に 5xx を出す経路 —— の 3 点を裁定依頼中」と書いてあった。
  **裁定の結果、残っているのは実装作業だけである。**
  ① **確定した** —— 間隔は 2 段（常時トラフィック用 60 秒・LLM を呼ばない ／ SLO 評価用 60 分・呼ぶ）。
  費用の上限は絶対額で置かず**間隔で実質的に固定**する（60 分＝月 720 回・概算 月約 4,400 円）。
  ② **道筋が付いた** —— **ただし 60 分側の配備は未着手であり、初回応答の評価対象は依然として無い。**
  あわせて**判定窓を標本量に合わせて広げる**ことが実装に課された（検知遅延の許容上限は 8 時間。
  具体値は実測で定めて環流する）。③ **置かないことで確定した**（製品の振る舞いを変えないため）。
  🔵 **［2026-09-09 追記 / #1287］残る実装作業のうち、AI 側で先行できる分は着地した** ——
  ローカル起動器に `SYNTHETIC=1` の門（標識の投入・順序・「揃わなければ配備しない」の fail-closed）を
  用意した。**残りは利用者の手が要る**: 本番像イメージの再ビルドと稼働クラスタへの適用、
  60 分側（LLM を呼ぶ）の**課金の承認**、およびそれに従属する判定窓と費用の実測・環流である。
- **`absent` の対象拡大**: 🔵 **［2026-09-05 更新 / #1203］半分だけ済んだ。**
  **RAG 応答完了（一括経路）の行を「評価対象の不在」へ入れた**（本書の表は 4 行になった）。
  **RAG 初回応答の行は入っていない** —— 上記②の 60 分側が配備されるまで系列が立たず、
  いま置くと恒常発火になるためである。**入れるときの形は決まっている**（周期 60 分の 2 周期ぶんの窓）。
- **保存時暗号化**: PostgreSQL/SeaweedFS/Qdrant のインフラ層暗号化の有効化・鍵管理（`docs/security/security.md`
  データ保護表と連動）。
- **監査ログの保管期間・改ざん防止・エクスポート**: 可観測性基盤側の保持設定で確定する（NFR「監査ログ保持」の具体化）。
- **バックアップの RPO/RTO 確定とリストア演習**: ステージング整備（#207）後に定期実施し実測する。

---
title: 運用仕様書
type: operations-spec
status: in-progress
created: 2026-07-04
updated: 2026-09-05
author: claude
---
<!-- trace:
ids: [FR-01, FR-02, FR-04, FR-10, FR-11, FR-13, FR-15, NFR-02, NFR-21, SC-01, SC-02, SC-10, UC-01, UC-04, UC-05, UC-07]
adrs: [ADR-0005, ADR-0006, ADR-0007, ADR-0008, ADR-0011, ADR-0016, ADR-0017, ADR-0026, ADR-0030, ADR-0038, ADR-0040, ADR-0042, ADR-0044, ADR-0072, ADR-0076]
iadrs: [IADR-0002, IADR-0009, IADR-0013, IADR-0017, IADR-0020, IADR-0021, IADR-0023, IADR-0025, IADR-0026, IADR-0028, IADR-0029, IADR-0032, IADR-0046, IADR-0049, IADR-0050, IADR-0051, IADR-0066, IADR-0069, IADR-0074, IADR-0076, IADR-0079, IADR-0080, IADR-0081, IADR-0082, IADR-0085, IADR-0088, IADR-0104, IADR-0110, IADR-0112, IADR-0149, IADR-0165, IADR-0168, IADR-0210, IADR-0225, IADR-0265, IADR-0284, IADR-0294, IADR-0304, IADR-0313, IADR-0322, IADR-0327, IADR-0345, IADR-0354, IADR-0367, IADR-0369, IADR-0370, IADR-0374]
specs: [20260904_issue-1198_usage-event-subject-and-retention, 20260904_issue-1202_absent-series-slo-alerts]
issues: [#1088, #1108, #1110, #1198, #1202, #1204, #124, #144, #145, #192, #196, #197, #198, #207, #271, #299, #303, #320, #324, #325, #395, #438, #443, #455, #466, #532, #536, #546, #587, #66, #665, #674, #863, #88, #98, #992, planning#196, planning#524]
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
| HPA/PDB でスケール・可用性を確保したい | §可用性・水平スケール |
| アラートが実際にどこへ届くか（未配線の現状）を確認したい | §監視・アラート |
| 利用イベントがいつ消えるか・消えていないときの見方を知りたい | §データ保持期間（利用イベント） |
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
  bash scripts/k8s-local-images.sh && kubectl -n microservices-platform rollout restart deployment/frontend-service
  ```

  宣言（`pipeline.json`）変更時は pod template の `checksum/pipeline-config` アノテーション
  （`templates/deployment.yaml`）が変わり自動 rollout されるが、これは**イメージ更新は捕捉しない**。
  イメージ更新は上記の一意タグ運用（推奨）か `rollout restart` で行う。

#### Third-party イメージ — 具体版タグ（可能なら digest）で固定

- 依存イメージ（keycloak/postgres/redis/rabbitmq/qdrant/minio/otel/prometheus/loki/tempo/grafana/
  wiki 等）は**具体バージョンタグ**で固定する（`values.yaml`・`docker-compose.yml`）。浮動 major タグは
  避ける（例: Wiki.js は `2` ではなく `2.5`。#320 で固定）。粒度は **minor 固定・patch は許容**
  （`2.5` は `2.5.x` 系列内の自動パッチを受ける。実測 PoC は `2.5.314`＝`docs/tech/20260707_wikijs-poc-record.md`）。
  完全固定が要るなら CD 層で digest ピンする。
- 最上位の再現性は **digest ピン**（`image: <repo>@sha256:...`）である。per-arch・per-registry の digest 解決と
  ミラー（`mirror.gcr.io`。frontend base イメージの非 docker.io 化・#325）整合の検証が要るため、
  稼働環境の CD 自動化（ArgoCD image updater / kustomize digest 運用）で段階導入するのが望ましい。
  infra の `-alpine` major タグはセキュリティ自動パッチの利点があり、固定と自動パッチはトレードオフで評価する。

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

`bash scripts/k8s-local-up.sh` は**既定で** [`deploy/local/infra-persistence`](../../deploy/local/infra-persistence/)
オーバーレイを適用し、**Keycloak（`/opt/keycloak/data`＝`start-dev` の file H2）・Postgres
（`/var/lib/postgresql/data`）・Qdrant（`/qdrant/storage`）を `local-path` PVC で永続化**する。realm + runtime state
（追加ユーザー・シークレット・セッション）・全アプリ DB・コレクション/ベクトルが Pod 再起動でも保持される。
**`OBSERVABILITY=1` を併用**すると [`deploy/local/observability-persistence`](../../deploy/local/observability-persistence/)
が素の観測 overlay を**置換**し、**Prometheus（`/prometheus`）・Loki（`/tmp/loki`）・Tempo（`/tmp/tempo`）・
Grafana（`/var/lib/grafana`）**も永続化される（マウント先は各 config の storage パスと一致させ、config は書き換えない）。
**opt-out は `PERSIST=0`（使い捨てスタック専用）。StorageClass `local-path` が無ければ起動器は止まる**（黙って emptyDir へは
落とさない —— 稼働 dev クラスタが誰にも気付かれず非永続で立っていたのが #1088 である）。
**rabbitmq/redis/otel は emptyDir 継続**（queue/cache は揮発前提・otel は stateless。**qdrant は #787 で永続化対象へ移った**）。

- **Prometheus の保持期間**は `--storage.tsdb.retention.time=7d` / `--storage.tsdb.retention.size=4GB` を
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
  （単独実行も可・冪等）。届いているかは `node scripts/check-stack-ready.js` の **G9** が見る。
  **既存の人間の利用者と `smtpServer` は触らない**（実行時が正）。seed 利用者の宣言を変えるときだけ **破壊経路**
  （`kubectl -n platform-infra delete pvc keycloak-data && kubectl -n platform-infra rollout restart deploy/keycloak`）を使う。
  **Keycloak pod で `kcadm.sh` を exec しない**（本体が OOMKilled になる）。
- **非永続で立っていた環境の移行**: up を再実行すると初回は空 PVC のため realm/DB は再生成される（既存 emptyDir データは
  元々揮発）。手順の全文は [`deploy/local/README.md`](../../deploy/local/README.md) の「永続化」節を参照。
  非永続で立っていることは `check-stack-ready.js` の **G10** が赤くし、稼働イメージが宣言（chart / kustomize の描画結果）と
  違うことは **G11** が赤くする。
- **保持範囲**: 保持されるのは Pod の再起動/再作成まで。`scripts/k8s-local-down.sh` はクラスタ／`platform-infra`
  namespace を削除するため PVC も消える（`down`→`up` では realm/DB は再生成）。PVC を残すなら `down` を使わず Pod のみ再作成する。

### Headlamp（k8s 管理 UI・dev opt-in）（非機能要件: 運用性 / #271）

ローカル k8s dev（経路B。k3d ＋ dev 専用 in-cluster インフラ資産で構成する）に [Headlamp](https://headlamp.dev/)
（CNCF Sandbox の k8s 管理 UI）を **opt-in** で導入し、Pod / Deployment / Service / ログ等をブラウザから閲覧・
操作できる。認証は既存 Keycloak（OIDC）に一元化し、`developer` / `Developer-2026` を流用する（新規資格情報を作らない）。
初回ログインでは TOTP の登録画面が挟まる（多要素認証を必須にしたため）。
本番像（`deploy/helm` / `deploy/argocd` / compose）は不変で、資産は `deploy/local/headlamp/`（dev 専用）に閉じる。

- **有効化**: `HEADLAMP=1 bash scripts/k8s-local-up.sh`（既定オフ・fail-safe）。`deploy/local/headlamp` を適用し、
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
  計画フィードバック（`feedback/20260719_headlamp-k8s-management-ui.md`）で論点化する。
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
  # Wiki.js 専用 DB のパスワード（wikijs Deployment が参照。key=password）
  kubectl create secret generic wikijs-db -n <namespace> \
    --from-literal=password='<wikijs DB ユーザのパスワード>'
  ```
  ArgoCD 等の GitOps では SealedSecret / ExternalSecret で同名 Secret を供給する。
- **ローテーション**: Wiki.js 管理 UI で新キーを発行 → Secret を更新
  （`kubectl create secret ... --dry-run=client -o yaml | kubectl apply -f -`）→
  `kubectl rollout restart deployment/wiki` → 旧キーを Wiki.js 側で Revoke する。
  dev は `.env` を書き換えて `docker compose up -d wiki-service`。
- **注意**: API キーは Wiki.js の管理 GraphQL 全体に及ぶ強い権限を持つ。付与グループは最小権限とし、
  キーは wiki-service 以外へ配布しない（認可は本システムの ABAC ゲートウェイが単一真実源であり、
  キー漏えい時は Wiki.js 全ページの読み書きが可能になるため即時 Revoke する）。

### 埋め込みプロバイダの設定・ゼロ保持・再索引（取り込みの要求 / 埋め込みプロバイダ選定の計画 ADR / Issue #98）

埋め込みは取り込み時に**全文書本文**を送信するため、LLM 呼び出しよりデータ露出が大きい。機密区分で
送信先・モデル・コレクションが分かれる（`Embedding:Routing`）。

| 機密区分 | 送信先ティア | モデル / 次元 | コレクション | 既定状態 |
| --- | --- | --- | --- | --- |
| public / internal | ティアB（Voyage・保護契約） | voyage-3.5 / 1024 | `knowledge_chunks_voyage_3_5` | 有効（要 API キー） |
| confidential / restricted | ティアA（セルフホスト固定） | ruri-v3 / 768 | `knowledge_chunks_ruri_v3` | 無効＝**fail-closed** |
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
    （`Embedding__Voyage__ApiKey`）。k8s は Secret（例 `embedding-voyage`、key=`api-key`）。
  - キー未設定でも起動する（fail-open しない）。Voyage 呼び出しが失敗した文書は索引されないだけで、
    高機密文書の本文が外部へ出ることはない（ルーティングで候補にならないため）。
  - **ゼロ保持認定状況の記録（#303 受け入れ基準）**: 実環境構築前チェックリストの一項目として、契約でのゼロ保持
    （学習不使用・レジデンシー含む）認定の可否をここに記録する。**現状: 未認定（2026-07-19 時点）**。
    - ⚠️ **既定構成は Voyage 経路が有効**（`appsettings.json` の `voyage-managed`＝index 0 が `Enabled: true`。
      compose/Helm に既定の無効化上書きは無い）。したがって「未認定＝自動で停止」ではない。**未認定の環境へデプロイ
      する場合は、運用者が本番文書を流す前に明示的に Voyage 経路を無効化すること**（`Embedding__Routing__Endpoints__0__Enabled=false`。
      compose は `.env`、k8s は values/`--set` で上書き）。本 PR は既定挙動（Voyage 有効）を変更しない（後方互換）。
    - 実際の契約認定は稼働環境／調達手続き依存＝分離（フォローアップ #336）。
- **セルフホスト（ティアA / Ruri v3）の有効化**: 基盤（TEI / vLLM 等の OpenAI 互換 `/v1/embeddings`）を
  構築後、`SELFHOSTED_EMBEDDING_URL`（`Embedding__SelfHosted__BaseUrl`）と
  `SELFHOSTED_EMBEDDING_ENABLED=true`（`Embedding__Routing__Endpoints__1__Enabled`）を設定して有効化する。
  有効化まで confidential/restricted 文書は**索引されない**（fail-closed。設計どおり）。
  - **配備物（opt-in・#303）**: 推論基盤（TEI）の配備物をリポに opt-in で用意済み。
    - k8s（Helm）: `values.yaml` の `embedding.enabled=true`（既定 `false`）で `templates/embedding.yaml` が
      TEI Deployment/Service を描画し、`llmgateway` へ `Embedding__SelfHosted__BaseUrl=http://embedding-service:<port>`
      と `Embedding__Routing__Endpoints__1__Enabled=true` を自動注入する（`services.llmgateway.selfHostedEmbedding`）。
    - compose: `docker compose --profile embedding up` で `embedding`（TEI）サービスを起動し、`.env` に
      `SELFHOSTED_EMBEDDING_URL=http://embedding:80` / `SELFHOSTED_EMBEDDING_ENABLED=true` を与える。
    - **稼働環境依存（分離）**: 実モデル（Ruri v3）の取得・GPU/CPU リソース・実埋め込み疎通・下記 nDCG@10 実測は
      稼働環境で行う。既定の image tag / モデル ID はプレースホルダであり、実運用前に稼働環境で固定する。
  - 有効化後、社内文書サンプルで検索精度（nDCG@10）を実測し、voyage-3.5 比で大幅劣化しないことを確認する
    （セルフホスト埋め込みの計画 ADR が求める事前 PoC の代替）。劣る場合は BGE-M3 へ切替（モデル別コレクション分離のため影響は局所）。
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
    → 運用: DLQ（`*_error` キュー）を監視し、滞留があれば原因（Voyage 障害・URL 誤設定等）を解消して再投入する。
    → 注意（削除後・再構築前の空白期間）: 取り込みは冒頭で当該文書の既存チャンクを全モデル別コレクションから
    削除してから再索引する（機密区分変更時の残存防止）。一時障害でリトライ枯渇→DLQ 送りとなった文書は、
    **削除済み・未索引（0 チャンク）の状態で一時的に検索不可**となる（恒久欠落ではなく DLQ 再投入で回復する）。
    このため DLQ 滞留は検索網羅性に直結する運用指標として監視し、速やかに再投入すること。
  - **fail-closed / 恒久的理由**（高機密でセルフホスト未有効・次元不整合・プロバイダ未登録、`Retryable=false`）:
    設計どおり当該チャンクを**索引スキップ**し、`IngestionCompleted` は索引できた件数で発行する
    （警告ログに機密区分を記録）。再試行では解消しないため DLQ には回さない。
- **再索引手順（次元 1536→1024・モデル別コレクション移行）**:
  1. 取り込みサービスは起動時に不足コレクション（`knowledge_chunks_voyage_3_5` / `_ruri_v3`。
     検証スタックでは `_deterministic_v1` も）を実次元で自動作成する（`QdrantBootstrapHostedService`）。旧 `knowledge_chunks`（1536 次元）は使用しない。
  2. 全文書に対し `DocumentUpdated` を再発行する（原本→正規化→取り込みを再走）。取り込み冒頭で全モデル別
     コレクションから当該文書を削除してから再索引するため、決定的チャンク ID により冪等に再構築される。
  3. 旧コレクション `knowledge_chunks` は移行完了後に手動削除する（`DELETE /collections/knowledge_chunks`）。
  - モデル差し替え（例 ruri-v3→BGE-M3）時も、当該コレクションを作り直し同手順で再索引する。
- **ペイロード項目を増やしたときの再索引（#536）**: 索引ペイロードへ**新しい項目**を
  足した場合も、上記手順 2（**全文書に対する `DocumentUpdated` の再発行**）がそのまま使える。
  コレクションの作り直しは要らない —— 決定的チャンク ID により同じ点が上書きされる。
  - **直近の該当**: **`updated_at`**（文書の更新日時。Unix epoch ミリ秒の整数）を #536 で追加した。
  - **再索引が済むまでの振る舞いは縮退であって障害ではない。** 当該項目を持たないチャンクは
    検索応答で `updatedAt` が `null` になり、画面は `—` を描く。**検索そのものは従来どおり
    ヒットする**（項目の欠落で結果から落とすことはしない）。
  - **急ぐ必要は無いが、放置すると「更新日時の新しい順」が実質的に使えない**
    （日時を知らないチャンクが混ざり続けるため）。並び順の提供時期に合わせて実施すること。

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
| ステートフル | minio / wikijs / postgres / qdrant | 対象外（各自の可用性方針） |

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
  が同じ 9 ルールを Grafana 側でも評価し、**Alerting 画面に発火を表示する**。**通知は送らない**（下記★）。
  `alerts.yml` との対応は `node scripts/check-grafana-alerting.js` が CI で突合する。
- **★ 経路間のパリティ（#674。Grafana provisioning は経路間で同内容とする実装 ADR）**: provisioning（datasources / dashboards / alerting）は
  **compose と k8s の両方に同内容で置く**。`node scripts/check-grafana-provisioning-parity.js` が突合する。
  **是正前は k8s 側にダッシュボードが 1 枚も無く、下記 `llm-usage.json` へ経路 B から辿り着けなかった。**
- **ダッシュボード**: `deploy/grafana/provisioning/dashboards/microservices-platform-overview.json`（サービス別
  スループット・5xx 率・p99・RAG レイテンシ・**RAG 初回応答 p95**）と
  [`llm-usage.json`](../../deploy/grafana/provisioning/dashboards/llm-usage.json)（**LLM の金額・トークン消費量・
  呼び出し回数**。**［2026-08-23］費用のパネルを追加した**）。
- **LLM 費用の統制（暫定）**: **上限アラートは Alertmanager 配備後に有効となる。配備までは月次の手動確認である**
  （計画 決定 39〜41 / #546）。手順・担当・記録は
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
- **ピン留めモデルの版数移行と利用不能時の振る舞い**: 用途別にピン留めした LLM モデルの版数を上げる手順
  （**Stage 0 再検証が前提**）と、**モデルが使えないときは取引判断を実行せず発注もしない**（**障害ではなく
  設計上の正常な結果**）ことは [`llm-model-pin-runbook.md`](llm-model-pin-runbook.md) が定める（#587。報告書の種別別用途と取引判断モデルの改定を定めた実装 ADR の決定 3）。
  **提供終了の監視は月次の費用確認に相乗りする**（自動検知は無い。検知の遅れは最大 1 か月）。
- **適用範囲（現状）**: Prometheus/アラートルール（`deploy/prometheus/alerts.yml`）と可観測性スタックは
  **dev の 2 経路（docker-compose と、ローカル k8s の可観測性オーバーレイ）に配線**されている。
  **［2026-08-30 更新 / #546］経路B（ローカル k8s）にも Alertmanager を配備し、両経路で同じ 9 ルールが
  同じ受け手へ届くようにした**（それ以前は compose だけだった）。**stg/prod は依然として対象外**である
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
> compose・k8s の 2 か所。9 ルールは `alerts.yml` と 1 対 1）。**ただし、配線したのは検知と可視化までである。**
>
> - **push 配信の宛先（contactPoints / policies）は設定していない。** 届かない宛先を書くと「配線した」と
>   読めてしまうため、**意図的に書いていない**（SLO の暫定通知先を Grafana 統合アラートへ配線する実装 ADR の決定 3）。
> - **したがって暫定期間に人が気づく経路は「Grafana の Alerting 画面を見る」ことだけ**である。
>   **非機能要件「障害検出 5 分以内」を満たしているのは評価の側だけ**であり、
>   **人が気づくまでの時間は見に行く間隔に等しい。**
> - **Grafana が provisioning を受理するかは、CI では見ていない。** 機械で確かめているのは
>   `node scripts/check-grafana-alerting.js` の範囲（ルール数・名前の 1 対 1・`datasourceUid` の実在・
>   compose と k8s の同内容・必須キー）まで。**配備時に `/api/v1/provisioning/alert-rules` が 9 件返すことを確かめる。**
>   🔵 **［2026-09-04 更新］稼働クラスタでは受理された** —— `reload` の後に 9 件が返ることを実測した
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
> | 3 | 下表 9 ルールの発火が Alertmanager 経由で通知されることを**1 件以上、実際に確かめた** | ✅ **満たした**（`OtelCollectorDown` の発火が Alertmanager の `/api/v2/alerts` に `active` として現れた。**合成ルールではなく下表の実ルールである**） |
>
> **条件 2 が満たされるまで併存させる。** 本来は避けたい状態だが、**暫定側（Grafana）にも宛先が無い**ため
> **二重通知は構造的に起き得ない** —— 併存を禁じた理由（重複通知が「既知の誤報」の習慣を生む）は現時点では働かない。
>
> **併存させない理由**: 同じ 9 ルールが 2 系統で評価されると**同じ事象に対して 2 通の通知が出る**。
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
| **評価対象の不在** — 収集経路 | `absent(up{job="otel-collector"})` | 系列が無い状態が 5 分 | Alertmanager（warning） | 検出（**統制**） |
| **評価対象の不在** — 全サービスの HTTP メトリクス | `absent(http_server_request_duration_seconds_count)` | 系列が無い状態が 5 分 | Alertmanager（warning） | 検出（**統制**） |
| **評価対象の不在** — 検索レイテンシ | `absent(http_server_request_duration_seconds_bucket{job="…retrieval-service"})` | 系列が無い状態が 5 分 | Alertmanager（warning） | 検出（**統制**） |

> 🔵 **［2026-09-04 追記］下 3 行は「評価対象そのものが無いこと」を鳴らす。上の行の SLO とは別物である。**
>
> **「サービス応答断（近似）」の行と読み分けること。** あちらは
> **直近まで受信していたのに途絶した**場合だけを拾い（`== 0` かつ `offset 15m > 0`）、
> **系列そのものが消えると式が空になって発火しない。** 2026-08-30 まで 4 ルールが
> 存在しないメトリクス名を見ていた事故は、**下 3 行の形でしか検知できない。**
>
> 🔴 **対象は「無風でいられる時間が検知要件（5 分）より短い経路」だけである。**
> **`/analysis/ask` 系（RAG の 2 行）は対象外**である —— 呼ばれない限り系列を持たないため、
> 無風が 5 分を超え得る。**合成監視で常時トラフィックを作ってから対象へ入れる**（下の未決事項）。
> 全 SLO へ対で置くと低頻度経路で恒常発火し、**警報を無視する習慣を作る**ため、計画がその案を却下している。
>
> 🔴 **下 3 行の検知は最大およそ 10 分であり、「5 分以内」ではない。** `absent()` は瞬間ベクタ選択子の
> 既定 5 分 lookback が空になって初めて真になり、そこへ `for: 5m` が積まる。
> **これが拾うのはサービス障害ではなく統制の欠落**であり、障害の 5 分検知は上の行が担う。
> `for` を短くして早める案は採らない（恒常発火の回避を優先している）。
>
> **Grafana 版は下 3 行だけ `noDataState: OK` である。** `absent(m)` は `m` が存在するとき空ベクタを返す
> （＝正常時が「データ無し」）ため、他と同じ `NoData` にすると**正常時に恒常発火する。**
>
> **実測済み**（2026-09-04・ローカル k8s）。転送だけを切って系列を途切れさせると
> **下 2 行が 9 分台で `firing` へ到達し、Alertmanager に `active` として現れた。**
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
| MinIO | 正規化本文・資産（`knowledge-normalized` バケット） | バケットレプリケーション／`mc mirror`／ボリュームスナップショット | 日次 | 世代管理 |
| Wiki.js DB（PostgreSQL `wikijs`） | Wiki 閲覧コンテンツ（同期の従。正本は本システム側） | `pg_dump`。※ DocumentUpdated 再同期で再構築可能 | 日次 | 直近数世代 |
| Keycloak realm | 認証設定（クライアント・ロール・マッパー） | realm export（`deploy/keycloak/*-realm.json` を単一の真実源に、IaC で再適用） | 変更時（Git 管理） | Git 履歴 |

- **リストア手順（概略）**: ①対象データストアを停止/隔離 → ②該当バックアップからリストア（Postgres は
  `pg_restore`、MinIO は mirror 復元、Qdrant はスナップショット復元）→ ③依存サービスを再起動しヘルス確認 →
  ④整合確認（Qdrant/Wiki は必要なら `DocumentUpdated` 再発行で再構築。埋め込み再索引は本書「埋め込みプロバイダ」節参照）。
- **リストア演習**: ステージング整備後に定期実施し、RTO の実測と手順の妥当性を検証する（follow-up）。

## 障害対応（Runbook）（NFR / #198）

| 事象 | 検知 | 一次対応 | エスカレーション |
| --- | --- | --- | --- |
| LLM ゲートウェイ/外部 LLM 不調 | RAG レイテンシ/5xx アラート、`LlmGateway` 縮退ログ | RAG は縮退応答（送信せず縮退・fail-closed）。検索（非 LLM）は継続。エンドポイント設定/疎通確認 | 外部プロバイダ障害なら egress 設定でセルフホスト/別ティアへ切替 |
| 埋め込みプロバイダ停止 | 取り込み失敗ログ、`EmbeddingEndpointTests` 相当の縮退 | 高機密はセルフホスト固定・未有効なら索引スキップ（fail-closed。埋め込みの機密区分ルーティング）。プロバイダ復旧後に再索引（本書「埋め込み」節） | セルフホスト基盤の起動、モデル/次元整合の確認 |
| RabbitMQ 停止 | サービス接続エラー、パイプライン滞留 | ブローカ再起動。MassTransit は再接続。未処理は再配信（冪等消費のため重複安全） | 永続化ボリューム/ディスク確認。デッドレター滞留は原因メッセージを調査 |
| Qdrant 停止 | 検索 5xx/エラーログ | Qdrant 再起動。索引は再取り込みで再構築可能（決定的チャンク ID） | ボリューム障害時はスナップショットからリストア（バックアップ節） |
| PostgreSQL 停止 | サービス起動失敗/DB 接続エラー | DB 再起動・接続確認。書き込み不可の間は該当サービスを縮退 | データ破損時はバックアップからリストア（RPO/RTO 節） |
| サービス 5xx スパイク | `HighHttp5xxRate` アラート | 対象サービスのログ/トレース（Tempo）で原因特定。必要ならロールバック（Git revert → ArgoCD 同期） | 依存（DB/ブローカ/外部）起因の切り分け。HPA 上限到達なら `scaling` 見直し |
| 構成ドリフト検出 | ドリフト検出 Warning（監査/警告ログ） | 宣言（`pipeline.json`）と実効の差分を確認。意図せぬ差分は Git を正として再同期 | 起動時 fail-fastで不整合構成の反映は阻止済み。恒常化は宣言の是正 |

- **エスカレーション/通知**: **Alertmanager の配備後**に受信先（メール/チャット）と担当・当番を運用体制に応じて定める（環境ごと）。
  **配備までは自動通知が無い** —— 一次検知は Prometheus UI / Grafana の目視である。
- **MTTR 目標（30 分）**: アラート（検出 5 分以内）→ Runbook 一次対応 → 復旧、の各段を Grafana/Tempo/Loki で追跡する。

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

## 未決事項

- **Alertmanager の受信先設定**: **［2026-08-30 更新 / #546］Alertmanager 本体は配備済みで、
  `alertmanagers.targets` は compose・k8s の 2 か所とも埋まっている**（発火が Alertmanager へ届くことは
  意図的に閾値を割って実測した）。**残っているのは受信先（メール/チャット）だけ**であり、
  既定は `default-null`＝どこへも送らない。**実環境の宛先は利用者が決める事柄**であり、実装側で代替値を置かない。
  **暫定の通知先（Grafana 内蔵アラート）は #665 で配線済み**だが、**push 配信の宛先は依然として無い**
  （本書「監視・アラート」の★参照。気づく経路は Alertmanager / Grafana の画面を見ることだけ）。
  **受信先が設定されテスト通知が届いた時点で暫定経路を削除する**（併存させない。条件は同★）。**#546 で追跡している。**
- **LLM 費用の自動検知**: **無い**。**［2026-08-30 更新 / #546］理由は「通知基盤が無い」ではなくなった** ——
  Alertmanager は配備済みであり、**残る障害は月次予算のしきい値が計画側で未確定であること**である
  （計画 決定 41。実測を待って確定し、確定の前提は費用の実績が数か月分そろうこと）。
  **検知の遅れは最大 1 か月**であることを受け入れ、月次の手動確認を暫定の統制として置いている
  （計画 決定 39 / [Runbook](llm-cost-monthly-review-runbook.md)）。
  🔴 **しきい値が定まるまで上限アラートは置かない。** 実装が数字を決めると、それが既成事実として
  計画へ逆流する（計画が明示的に禁じている）。
- **監視の stg/prod（k3s）展開**: Prometheus/Alertmanager を Helm（Operator 等）で配備し、`alerts.yml` 相当の
  ルールと通知を k3s にも展開する（**［2026-08-30 更新 / #546］現状は dev の 2 経路のみ**。
  本番像の chart には Prometheus / Alertmanager / Grafana のリソースが無い）。
- **RabbitMQ キュー滞留・デッドレター・構成ドリフトのアラート**: それぞれ RabbitMQ Prometheus プラグインの
  exporter メトリクスと、ドリフト検出のカスタムメトリクス化が必要（`alerts.yml` 末尾に雛形をコメントで用意）。
- **サービスダウンの厳密検知**: push（remote write）モデルのため per-service `up` が無く、メトリクス途絶での
  近似検知に留まる。blackbox exporter / k8s liveness による補完を検討する。
- **合成監視（synthetic）**: **無い**。**［2026-09-04 追記］「評価対象の不在」を鳴らす規則は入ったが、
  `/analysis/ask` 系は対象外のままである** —— 呼ばれない限り系列を持たず、無風が検知要件（5 分）を
  超え得るため、対象へ入れると恒常発火する。🔴 **したがって RAG の 2 行は、無風時に「鳴らない」と
  「鳴りようがない」の区別が付かない。** 区別を付けるには一定間隔で代表リクエストを打つ観測専用の経路が要る。
  🔴 **合成トラフィックは識別できる標識を持ち、LLM 費用の計測と利用状況・検索傾向の集計から除外する。
  除外できない構成では配備しない**（標識と除外は同時に入れる）。**頻度・費用の上限は未確定**である。
- **保存時暗号化**: PostgreSQL/MinIO/Qdrant のインフラ層暗号化の有効化・鍵管理（`docs/security/security.md`
  データ保護表と連動）。
- **監査ログの保管期間・改ざん防止・エクスポート**: 可観測性基盤側の保持設定で確定する（NFR「監査ログ保持」の具体化）。
- **バックアップの RPO/RTO 確定とリストア演習**: ステージング整備（#207）後に定期実施し実測する。

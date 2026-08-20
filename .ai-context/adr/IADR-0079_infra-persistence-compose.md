---
title: IADR-0079 compose 基盤インフラの永続化は Keycloak=共有 Postgres 外部 DB／Loki・Tempo=既存 storage パスへの名前付きボリューム＋user:0:0 で行う
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0004
  - IADR-0020
  - IADR-0066
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md (認証＝Keycloak)
  - planning:projects/microservices-platform/02_requirements/ (NFR 運用性・可観測性・信頼性)
---

# IADR-0079: compose 基盤インフラの永続化（Keycloak=共有 Postgres／Loki・Tempo=名前付きボリューム）

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用性・可観測性・信頼性＝コンテナ再作成でインフラ状態を失わない）／ADR-0004（認証＝Keycloak）
- 関連 ADR: [IADR-0020](./IADR-0020_wiki-js-deployment-abac-gateway.md)（Wiki.js が共有 Postgres の専用 DB を `kp/kp` で利用する先例）／[IADR-0066](./IADR-0066_local-k8s-dev-environment.md)（経路B は
  `emptyDir` を意図採用＝本 issue の対象外根拠）
- 関連仕様書: `docs/specs/20260719_issue-282_infra-persistence-compose.md`
- Issue: #282（運用/基盤・方針確定 2026-07-18）

## コンテキストと課題

`deploy/docker-compose.yml` の infra は大半が名前付きボリュームで永続化されているが、**Keycloak / Loki / Tempo の
3 つだけボリュームが無く、コンテナ再作成で状態を失う**（postgres/rabbitmq/redis/qdrant/minio/prometheus/grafana は
永続化済みで非対称）。方式は issue で確定済み（Keycloak→共有 Postgres／Loki・Tempo→名前付きボリューム）。本 ADR は
確定方式の **実装上の細部**（`start-dev` 維持可否・接続資格情報・storage パス整合・空ボリュームの権限）を決める。

## 決定

### 1. Keycloak は `start-dev --import-realm` を維持したまま共有 Postgres へ外部 DB 接続する

`start-dev`（quarkus dist）は開発向け既定（strict-hostname 無効・HTTP 許可・ローカルキャッシュ）を与えるだけで、
**データベースは `KC_DB` 系 env で上書き可能**。H2 依存の解消に prod 起動（`start`）へ移行する必要は無く、`start`
は `KC_HOSTNAME`/HTTPS/optimized ビルド等の追加要件を課すため、issuer 固定（`KC_HOSTNAME_URL`・#88）と
ヘルスチェックの既存挙動を回帰させない **最小変更＝`start-dev` 維持** を採る。配線:

```yaml
KC_DB: postgres
KC_DB_URL_HOST: postgres
KC_DB_URL_DATABASE: keycloak
KC_DB_USERNAME: kp
KC_DB_PASSWORD: kp
```

`command`（`start-dev --import-realm`）・`KC_HOSTNAME_URL`・`KC_HEALTH_ENABLED`・realm import マウント・
healthcheck は不変。`depends_on: postgres(condition: service_healthy)` を追加し、DB 準備前起動を防ぐ（`wiki-js` と同型）。

### 2. Keycloak DB は共有 Postgres の `keycloak` DB（所有者 `kp`）を新設し、資格情報は `kp/kp` を流用する

`create-multiple-dbs.sh` に `CREATE DATABASE keycloak; ALTER DATABASE keycloak OWNER TO kp;` を追加する
（他サービス DB・`wikijs` と同一パターン。PostgreSQL 15+ の public スキーマ CREATE 権限問題を所有者付与で回避）。
専用 DB ユーザーを新設せず既存 `kp/kp` を流用するのは、(a) `wiki-js`（[IADR-0020](./IADR-0020_wiki-js-deployment-abac-gateway.md)）が同じ `kp/kp` で共有
Postgres を利用する先例に揃える、(b) dev/staging compose に新規シークレットを増やさない、ため。Keycloak は
`keycloak` DB の所有者として全 DDL（約 90 テーブル）を実行できる。**平文資格情報は既存の dev 方針どおり**で、
本番の Vault 移行は既存トラッカ（#310）配下（本 issue で新たな平文を増やさない）。

### 3. Loki / Tempo は **既存 config の storage パスへ** 名前付きボリュームをマウントし、`user: "0:0"` を付与する

- `loki-data:/tmp/loki`（`loki-config.yaml` の `path_prefix: /tmp/loki`＝index/chunks の親と一致）
- `tempo-data:/tmp/tempo`（`tempo.yaml` の `local.path: /tmp/tempo/blocks`・`wal.path: /tmp/tempo/wal` の親と一致）

**config を書き換えず既存パスにマウント** することで storage パス整合を機械的に保証し、config ドリフト・回帰の余地を無くす
（issue の「/loki にマウント」は例示であり、要件は「storage パスと整合」。既存パスへのマウントで充足する。config を
`/loki` へ変える案は churn とドリフト源を増やすため却下）。

`user: "0:0"` を付与する理由（**本 ADR の肝**）: 空の名前付きボリュームは、マウント先ディレクトリがイメージに
存在しない場合 **root:root 0755 で生成**される。grafana/loki・grafana/tempo は非 root（uid 10001）で動作しうるため、
そのままでは新規ボリューム直下に index/chunks/wal を作成できず **起動時 permission denied で回帰**する。`user: "0:0"` は
イメージの既定ユーザーに依存せず（既定が root でも 10001 でも）書き込みを保証する image-user 非依存の fail-safe。
dev/staging compose の可観測性コンテナに限った措置で、本番系（Helm・外部プロビジョニング）には及ばない。

### 4. 対象は compose のみ。経路B（`deploy/local/`）は本 ADR の対象外

経路B（ローカル k8s dev）を統べる決定は [IADR-0066](./IADR-0066_local-k8s-dev-environment.md)（k3d ＋ dev 専用 in-cluster インフラ資産）であり、その infra が
**永続化なし＝`emptyDir`（Pod 再起動で再 init）** である旨は `deploy/local/README.md`（「永続化なし: infra は
emptyDir（Pod 再起動で再 init）。dev 用途の割り切り。」）に明記されている（＝直接の根拠。IADR-0066 本文自体は
emptyDir に言及しない）。この dev 専用・揮発許容の割り切りを尊重し、本 ADR は経路B の PVC 化を扱わない。経路B の
恒久化は別 issue（フォローアップ #324）で、IADR-0066 の割り切り見直し＋#271（Headlamp）との infra/values 調整とともに判断する。

## 影響

- **realm 更新の反映が変わる（重要な運用差分）**: これまで H2 が揮発していたため `up` の度に realm.json が
  再 import され、realm.json の編集は再作成で反映されていた。永続 Postgres 化後は `--import-realm` が **既存 realm を
  スキップ**（default: 既存を上書きしない）するため、**realm.json の編集はそのままでは反映されない**。更新反映の運用手順
  （realm 削除→再 import、または kcadm partial import）を `docs/operations/operations.md` に明記する。runtime state の
  保持（本 issue の目的）と realm 定義の再現性（Git 単一情報源）はトレードオフで、後者は運用手順で担保する。
- 既存の起動順・healthcheck・issuer 固定は不変。`docker compose config` で反映を静的検証。
- CI: `build:` 対象・MAPPING（[IADR-0068](./IADR-0068_image-mapping-drift-check.md) / #275）不変、realm.json 不変のため image-mapping / realm-constraints 検査は非回帰。

## 却下した代替案

- **Keycloak を H2 ボリュームで永続化**: 単一ファイル H2 の運用性・共有インフラ流用方針に反する（issue で不採用確定）。
- **`start`（prod）へ移行**: `KC_HOSTNAME`/HTTPS/optimized 等の追加制約で issuer 固定・healthcheck を回帰させうる。`start-dev`
  ＋ `KC_DB` で十分。
- **Loki/Tempo を MinIO(S3) へ集約**: 設定量・バケット分離設計が増える（issue で不採用確定）。他 infra と同じボリューム方式に揃える。
- **config を `/loki`・`/var/tempo` へ変更**: storage パス整合は既存パスへのマウントで満たせるため不要な churn。
- **`user` 無しで名前付きボリュームのみ追加**: 非 root イメージで新規ボリュームに書けず起動回帰の恐れ（§3）。

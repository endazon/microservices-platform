---
title: platform 基盤インフラの永続化（Keycloak 外部 Postgres / Loki・Tempo 名前付きボリューム・docker-compose）（Issue #282）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0004
  - IADR-0020
  - IADR-0066
  - IADR-0079
author: claude
created: 2026-07-19
updated: 2026-07-19
related_specs:
  - "../adr/IADR-0079_infra-persistence-compose.md"
  - "../adr/IADR-0020_wiki-js-deployment-abac-gateway.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../operations/operations.md"
---

# 仕様書: platform 基盤インフラの永続化（Keycloak / Loki / Tempo・docker-compose）（Issue #282）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): なし（運用・基盤インフラの永続化。プロダクト機能ではない）
- 非機能要件(NFR): 運用性・可観測性・信頼性（コンテナ再作成でインフラ状態を失わないこと）
- 関連 ADR: ADR-0004（認証＝Keycloak）／可観測性（Loki/Tempo）。方式判断は [[IADR-0079]]。既存 [[IADR-0020]]（Wiki.js↔Keycloak OIDC・共有 Postgres 利用の先例）／[[IADR-0066]]（経路B は対象外の根拠）。
- Issue: #282（本 issue・運用/基盤）。方針確定 2026-07-18（Keycloak→共有 Postgres／Loki・Tempo→名前付きボリューム）。

## 目的・背景（As-Is）

`deploy/docker-compose.yml` の基盤（infra）サービスは、ステートフルなものに名前付きボリュームを与えて永続化している
（`postgres` / `rabbitmq` / `redis` / `qdrant` / `minio` / `prometheus` / `grafana`）。一方で **同じくステートフルなのに
ボリュームが無く、コンテナ再作成で状態を失う** サービスが 3 つ残る:

1. **Keycloak**: `start-dev --import-realm` により組み込み H2（`/opt/keycloak/data`・非マウント）で動作。管理コンソールでの
   実行時変更（ユーザー追加・パスワード変更・クライアントシークレット・セッション・同意等）が `down`/再作成で消失する。
   realm 定義自体は `--import-realm` で毎回再投入されるため一見「戻る」が、**import に含まれない runtime state は失われる**。
2. **Loki**: `loki-config.yaml` の storage は `/tmp/loki`（filesystem）だがデータ用ボリューム無し → 再作成でログ消失。
3. **Tempo**: `tempo.yaml` の storage は `/tmp/tempo`（local backend）だがデータ用ボリューム無し → 再作成でトレース消失。

Prometheus / Grafana はボリュームがあるのに Loki / Tempo だけ非対称で欠落している。

### スコープ確認（compose のみ / 経路B は対象外）

本 issue の受け入れ条件は **compose 側（`deploy/docker-compose.yml`）に閉じる**。ローカル k8s dev 環境
（`deploy/local/`＝経路B）は [[IADR-0066]] の割り切りで `emptyDir`（Pod 再起動で再 init）を **意図的に採用** しており、
issue 本文「含まない（意図的に除外）」で明示除外されている。経路B の PVC 化は別 issue（フォローアップ）で扱う。
Helm chart（`deploy/helm`）も外部プロビジョニング前提で対象外。

## 対応方針（To-Be）

詳細な設計判断と根拠は [[IADR-0079]] に記録する。要点:

1. **Keycloak → 共有 Postgres 外部 DB 化**。`start-dev --import-realm` を維持したまま `KC_DB=postgres` 系 env を配線し、
   共有 Postgres の新規 `keycloak` DB（所有者 `kp`）へ接続する（`wiki-js` と同じ `kp/kp` 資格情報・パターン）。
   `depends_on: postgres(service_healthy)` を追加。`KC_HOSTNAME_URL`（issuer 固定・#88）・`--import-realm`・
   ヘルスチェックは不変。
2. **Loki / Tempo → 名前付きボリューム**。`loki-data` を `/tmp/loki`、`tempo-data` を `/tmp/tempo`（＝各 config の
   既存 storage パス）へマウントし、storage パスと完全整合させる（config 無改変）。空の名前付きボリュームが root 所有で
   生成される既知の挙動に対し、非 root イメージでも書き込めるよう `user: "0:0"` を付与する（[[IADR-0079]] §3）。
3. `volumes:` セクションへ `loki-data` / `tempo-data` を追加。
4. `create-multiple-dbs.sh` へ `keycloak` DB 作成＋所有権付与（他 DB と同じパターン）。

## 影響範囲

| ファイル | 変更 |
| --- | --- |
| `deploy/docker-compose.yml` | keycloak に `KC_DB*` env・`depends_on postgres`／loki・tempo に volume + `user`／`volumes:` に 2 件追加 |
| `deploy/create-multiple-dbs.sh` | `CREATE DATABASE keycloak;` ＋ `ALTER DATABASE keycloak OWNER TO kp;` |
| `docs/operations/operations.md` | 永続化と **realm 更新の運用手順**（import 冪等性と更新反映）を追記 |
| `docs/adr/IADR-0079_*.md` | 新規（方式・パス整合・権限の決定） |

realm.json（client 定義）・frontend・edge・helm・経路B manifest は **変更しない**。

## 受け入れ基準の写像

| Issue 受け入れ基準 | 充足方法 |
| --- | --- |
| Keycloak が共有 Postgres（`keycloak` DB）を外部 DB として利用・H2 非依存 | `KC_DB=postgres` 系 env を配線。`docker compose config` で反映を確認 |
| `down && up -d` 後も管理コンソールの実行時変更が保持 | 状態が Postgres（`postgres-data` 永続）に格納されるため保持。手動検証手順を operations に記載 |
| `create-multiple-dbs.sh` に `keycloak` DB 追加＋所有権 | `CREATE DATABASE keycloak; ALTER DATABASE keycloak OWNER TO kp;` |
| Loki ログが再作成後も参照可（`loki-data`） | `loki-data:/tmp/loki`（config storage パスと一致） |
| Tempo トレースが再作成後も参照可（`tempo-data`） | `tempo-data:/tmp/tempo` |
| `volumes:` に `loki-data`/`tempo-data` 追加・データパスにマウント | compose 編集で充足 |
| 採用方式と storage パス整合の判断が IADR に記録 | [[IADR-0079]] |
| 既存の起動・ヘルスチェック・issuer 固定が非回帰 | env 追加のみ。command/healthcheck/`KC_HOSTNAME_URL` は不変。`docker compose config` で確認 |

## 検証

- **静的**: `docker compose -f deploy/docker-compose.yml config`（daemon 不要）でパース・volume/mount/env 反映を確認。
- **CI 非回帰**: `node scripts/check-image-mapping.js`（build 対象・MAPPING 不変）／`node scripts/check-realm-constraints.js`
  （realm.json 不変）が緑。`helm template`/`lint` は本 PR が helm 非改変のため対象範囲外だが影響が無いことを確認。
- **ランタイム（要 docker daemon・reviewer/運用者が実行）**: 下記手順で down→up 越しの保持を確認（本環境は daemon 停止のため
  手順を operations に記載し、静的検証で代替）。
  1. `docker compose up -d`（初回 realm import・keycloak DB 生成）。
  2. 管理コンソールで検証用ユーザーを追加、Grafana で Loki/Tempo にデータが出ることを確認。
  3. `docker compose down`（`-v` を付けない）→ `docker compose up -d`。
  4. 追加ユーザーが残存・Loki/Tempo の過去データが参照可能なことを確認。

## 未決・フォローアップ

- 経路B（`deploy/local/`）infra の PVC 化（Keycloak 再起動で realm/runtime state 消失の実害解消）は別 issue で起票。
  IADR-0066 の `emptyDir` 割り切りの見直し＋専用 IADR＋#271（Headlamp）との infra/values 調整を要する。

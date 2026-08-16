---
title: IADR-0210 経路B の永続化を Qdrant と可観測性 4 種へ広げ、PVC を掴む Deployment は Recreate にする
type: impl-adr
status: Accepted
related_ids:
  - NFR-19
  - FR-02
  - ADR-0006
  - IADR-0066
  - IADR-0077
  - IADR-0082
  - IADR-0087
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
---

# IADR-0210: 経路B の永続化スコープ拡張（Qdrant ＋ 可観測性 4 種）と Recreate

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: claude（実装）

## 起点・関連

- Issue: **#787**。仕様書: `../specs/20260816_issue-787_local-persistence-expansion.md`。
- 実装 ADR: [[IADR-0082]]（経路B 基盤インフラの永続化・本 ADR が**スコープを広げる**）、
  [[IADR-0077]]（opt-in オーバーレイの流儀）、[[IADR-0066]]（経路B は emptyDir の割り切り）、
  [[IADR-0087]]（ゲート横断 smoke test）。

## コンテキストと課題

**実測（2026-08-16）で、経路B の 5 コンポーネントが再起動のたびにデータを失っていることが分かった。**

| 対象 | 実測 |
| --- | --- |
| Qdrant | `emptyDir`。**コレクション 0 件**（`/qdrant/storage/collections` が空） |
| Prometheus | **データ用 volume が無い**（volumes は ConfigMap のみ）。TSDB はコンテナ書き込みレイヤ。**実効保持は約 4.7 時間**（`runtimeinfo.startTime` と最古サンプルの差）。`storage.tsdb.retention.time` は **`0s`**（未指定） |
| Loki | 同上。`/tmp/loki` に 16.3M |
| Tempo | 同上。`/tmp/tempo` に 13.4M |
| Grafana | 同上。`/var/lib/grafana`（SQLite）が未マウント |

Pod は**日次規模で再起動している**（RESTARTS 9〜40）。
[[IADR-0082]] は `PERSIST=1` の opt-in で Keycloak / Postgres を PVC 化したが、
**`PERSIST` は `INFRA_KUSTOMIZE` しか差し替えておらず、`deploy/local/observability` には一切効いていなかった**
（apply 先がハードコード）。

## 決定

### 1. Qdrant を `infra-persistence` へ、可観測性 4 種を新設 `observability-persistence` へ

[[IADR-0082]] は「**スコープを Keycloak/Postgres に絞る（過剰実装を避ける）。qdrant は要すれば同型
（PVC + patch）で拡張可能**」と将来の拡張を明示しており、本 ADR はその拡張を行う。**同 ADR を Supersede しない。**

- **Qdrant** は base に `storage`（emptyDir）と mountPath が既にあるので **postgres 型**
  （`op: replace /spec/template/spec/volumes/0`）。
- **可観測性 4 種**はデータ用 volume を持たないので **keycloak 型**
  （volume と volumeMount を `op: add ... /-` で末尾追加）。インデックス依存が無い。

**マウント先は config を読んで確定した**（推測しない）。

| 対象 | mountPath | 根拠 |
| --- | --- | --- |
| Qdrant | `/qdrant/storage` | base の既存 mountPath |
| Prometheus | `/prometheus` | `--storage.tsdb.path` は既定の相対 `data/` で WORKDIR が `/prometheus` ＝ 実効 `/prometheus/data`（`/api/v1/status/flags` で実測） |
| Loki | `/tmp/loki` | config の `common.path_prefix` と `tsdb_shipper` / `filesystem` の全パスがこの配下 |
| Tempo | `/tmp/tempo` | config の `storage.trace.local.path` と `wal.path` の共通親 |
| Grafana | `/var/lib/grafana` | SQLite の既定位置 |

**`/tmp` そのものはマウントしない。** 覆うと Go の `os.TempDir()` が使う一時ファイルまで PVC に載り、
`/tmp` のセマンティクスも壊れる。**config は 1 バイトも触らない** ——
base の `loki.yaml` / `tempo.yaml` は「compose の設定と同内容」と宣言しており、崩すと compose 側も追随が要る。

### 2. Grafana を対象に含める

**「provisioning で再生成できるから対象外」とは判断しない。**
datasources / alerting / dashboards は ConfigMap から復元されるが、
**OIDC ログインで作られた利用者レコード・手動作成のダッシュボード・アラートの silence と state は再生成できない。**

[[IADR-0082]] の「再生成可能な派生データは除外」に照らすと境界だが、**同じオーバーレイに PVC 1 本を足すだけ**で、
**「5 件測って 4 件だけ直す」は母集合の規則 7 の破れそのもの**である（本リポで最も繰り返し起きている事故の型）。

### 3. PVC を掴む Deployment は `Recreate` にする

`ReadWriteOnce` と `RollingUpdate` は両立しない。**local-path は単一ノードの hostPath なので
スケジューリングでは詰まらず、アプリのロックで詰まる。**

- Prometheus は `storage.tsdb.no-lockfile: false` を**実測**（`/api/v1/status/flags`）。
  再起動後の `/prometheus/data` に **`lock` ファイルが実在する**ことも確認した。
- Postgres は `postmaster.pid`、Qdrant は RocksDB の LOCK、Grafana は SQLite。

**[[IADR-0082]] の既存 2 件（postgres / keycloak）にも `Recreate` が無かったので、併せて付ける。**
新規 5 件だけ直して既存 2 件を残すのは、決定 2 と同じ型の破れになる。
**base（emptyDir）側は RollingUpdate のまま**でよい —— 奪い合うボリュームが無い。

### 4. Prometheus の保持期間を明示する

base の args に retention 指定が無く、**実測値は `retention.time=0s`（＝既定 15d）**だった。
PVC 無しでは実効しても意味が無かったが、**永続化して初めて上限が要る**（ディスクを食い潰さないため）。
`--storage.tsdb.retention.time=15d` と `--storage.tsdb.retention.size=4GB` を**オーバーレイの patch で**足す
（base に直書きすると既定経路のバイト等価が壊れる）。

### 5. `PERSIST` を observability にも効かせる

`scripts/k8s-local-up.sh` の `OBSERVABILITY=1` ブロックで apply 先を変数化し、
`PERSIST=1` のとき `deploy/local/observability-persistence` を選ぶ（`INFRA_KUSTOMIZE` と同型）。
**`OBSERVABILITY=1` 単独（PERSIST 未設定）のコマンド列は現行とバイト等価**である。

### 6. securityContext / fsGroup は付けない

local-path provisioner の setup が `mkdir -m 0777` でボリュームディレクトリを作る
（`kube-system/local-path-config` を実読）。**非 root（Prometheus 65534 / Loki 10001 / Tempo 10001 /
Grafana 472）でも書ける。** 既存の `infra-persistence` も付けていない。

## 影響・トレードオフ

- **切替時に Qdrant の既存コレクションは失われる。** emptyDir から空 PVC へ移るため。
  **実測で確認済み**（切替直後 0 件 → ingestion 再起動で 2 件再作成 → Qdrant 再起動後も残存）。
  退避が要る場合は Qdrant の snapshot API を使う。README に注記した。
- **既定（`PERSIST` 未設定）は一切変わらない。** PVC はオーバーレイにしか存在せず、
  provisioner 不在のクラスタでも Pod を Pending にしない（[[IADR-0082]] の fail-safe を踏襲）。
- ディスクを消費する（PVC 合計 21Gi 要求。実使用は遥かに小さい）。

## 代替案

| 案 | 採否 |
| --- | --- |
| **Loki / Tempo の config でデータパスを `/var/lib/...` へ移す** | 却下。意図は明確になるが、**base の config が compose と同内容という宣言を崩す**（compose 側の追随が要る）。`/tmp/loki` を mountPath にすれば config 無変更で同じ目的を達する |
| **Grafana を対象外にする** | 却下（決定 2） |
| **`Recreate` を base に直書き** | 却下。emptyDir では不要であり、**既定経路のバイト等価**が崩れる |
| **StatefulSet へ移行** | 却下。単一レプリカの dev 環境で PVC を得るためだけに種別を変えるのは過剰。`deploy/local/` に StatefulSet は 1 件も無い |

## 検出しないこと（明示）

- **PVC が実際に Bound するか**は CI では検査しない（クラスタが要る）。
  静的検査が固定するのは「`claimName` に対応する PVC が同じ overlay に宣言されている」ことまで。
- **本番像（`deploy/helm/`）** は触らない。MinIO / Wiki.js は既に PVC 化済みである。
- **RabbitMQ / Redis / otel-collector / Headlamp / Vault** は対象外
  （queue・cache・stateless パイプライン・stateless UI・dev モードの in-memory backend）。

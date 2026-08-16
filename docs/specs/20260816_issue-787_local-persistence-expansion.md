---
title: 作業仕様書 — 経路B の永続化を Qdrant と可観測性 4 種へ広げる（#787）
type: spec
status: done
related_ids:
  - NFR-19
  - FR-02
  - ADR-0006
  - IADR-0066
  - IADR-0077
  - IADR-0082
  - IADR-0087
  - IADR-0210
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
related_specs:
  - "../adr/IADR-0210_local-persistence-scope-expansion.md"
  - "../adr/IADR-0082_local-k8s-infra-persistence.md"
---

# 作業仕様書: 経路B の永続化スコープ拡張（#787）

## 1. 起点

**#380 / #336 のローカル解消可能性を調べる過程で発見した。** どちらも前提として永続化を要する ——
「上限到達率が**定常値として**読める」（#380 基準 ②）は実効保持 4.7 時間では成立せず、
nDCG@10 の測定（#336）は索引が再起動で消えると測れない。

## 2. 実測（2026-08-16・稼働中の k3s）

| 対象 | 実測 |
| --- | --- |
| Qdrant | `emptyDir`。**コレクション 0 件** |
| Prometheus | データ用 volume **無し**。**実効保持は約 4.7 時間**。`storage.tsdb.retention.time` は **`0s`**（未指定） |
| Loki | 同上。`/tmp/loki` に 16.3M |
| Tempo | 同上。`/tmp/tempo` に 13.4M |
| Grafana | 同上。`/var/lib/grafana`（SQLite）が未マウント |

**`PERSIST=1` は `INFRA_KUSTOMIZE` しか差し替えておらず、`deploy/local/observability` には一切効いていなかった**
（apply 先がハードコード）。

## 3. 母集合（`.claude/rules/traceability.md` 規則 1〜8）

**「永続化されていない Deployment」を全数で引いた。** `deploy/local/` 配下の Deployment は **12 件**、
`StatefulSet` は **0 件**。各ファイルの `volumes` / `volumeMounts` を機械的に判定した。

| 判定 | 件数 | 内訳 |
| --- | ---: | --- |
| **対象（本 PR）** | **5** | qdrant（emptyDir）／prometheus・loki・tempo・grafana（データ用 volume 無し） |
| 対応済み | 2 | postgres・keycloak（`IADR-0082`） |
| 対象外 | 5 | rabbitmq・redis（queue / cache は揮発前提）／otel-collector（stateless）／headlamp（stateless UI）／vault（`-dev` は in-memory backend が仕様） |

### ★ Grafana を落としかけた

**当初 issue のタイトルは「Qdrant と可観測性 3 種」で、Grafana は入っていなかった。**
機械判定で 5 件目として出てきたので入れた。**「5 件測って 4 件だけ直す」は規則 7 の破れ**であり、
本リポで最も繰り返し起きている事故の型である（判断の根拠は `IADR-0210` 決定 2）。

### 規則 10 —— この是正で新たに誤りになる自分の記述

- `deploy/local/README.md:103`「qdrant / rabbitmq / redis / otel は emptyDir 継続」→ **qdrant を外して是正**
- `docs/adr/IADR-0082` の却下代替案「スコープを Keycloak/Postgres に絞る」→ **日付つき追記で拡張を記録**
- `emptyDir` を含む記述を全走査し、上記 2 件以外に追随が要るものが無いことを確認した

## 4. 変更

| ファイル | 変更 |
| --- | --- |
| `deploy/local/infra-persistence/pvcs.yaml` | `qdrant-storage` PVC を追加 |
| `deploy/local/infra-persistence/kustomization.yaml` | qdrant の volume 置換 ＋ **postgres / keycloak / qdrant に `Recreate`** |
| `deploy/local/observability-persistence/`（新規） | PVC 4 本 ＋ volume/volumeMount パッチ ＋ `Recreate` ＋ Prometheus の retention |
| `scripts/k8s-local-up.sh` | `OBS_KUSTOMIZE` を変数化し `PERSIST=1` で永続化オーバーレイを選ぶ |
| `scripts/k8s-local-up.test.js` | テスト 9 件追加（ゲート 3 件 ＋ 静的検査 6 件） |
| `docs/adr/IADR-0210_*.md`（新規） / `IADR-0082` / `docs/adr/README.md` / `deploy/local/README.md` | 決定と追随 |

**マウント先は config を読んで確定した**（推測しない）。根拠は `IADR-0210` 決定 1 の表。

## 5. 検証

### 静的検査を足した理由

**既存の `PERSIST` テストは `apply -k <path>` というコマンド文字列しか見ておらず、
「PVC が本当に生えるか」は 1 行も検証されていなかった**（`postgres-data` / `keycloak-data` は
`OPTIN_TOKENS` にも個別テストにも無い）。CI に `kustomize build` を走らせるジョブが無いので、
`#779` で edge overlay に置いたのと同じ型（マニフェストを読んで固定する）で足した。

### 変異試験（10 通り・全件 RED）

| # | 壊し方 | 結果 |
| --- | --- | :---: |
| MP1 | grafana の PVC を落とす（4 → 3 件） | RED |
| MP2 | `claimName` を PVC 名と食い違わせる | RED |
| MP3 / MP4 | `Recreate` を落とす（observability / infra） | RED / RED |
| MP5 | Loki の mountPath を `/tmp` にする | RED |
| MP6 | retention を落とす | RED |
| MP7 | base に PVC を持ち込む | RED |
| MP8 | qdrant の PVC 参照を落とす | RED |
| MP9 | `OBS_KUSTOMIZE` の分岐を外す | RED |
| MP10 | PERSIST ブロック内で observability を触る | RED |
| — | 変異なし | GREEN（67 件） |

> **★ MP10 は 1 度目 GREEN を返した。** 挿入位置が `OBSERVABILITY=1` ブロックの**内側**で、
> PERSIST 単独では実行されない —— **変異が退行を模していなかった**。位置を直したら RED になった。
> #779 でも同じ型の誤りを踏んでおり（`echo` を壊してスタブログに出なかった）、**2 回目である。**

### 実機（稼働中の k3s）

| 観点 | 実測 |
| --- | --- |
| PVC | 7 本すべて `Bound`（既存 2 ＋ 新規 5） |
| strategy | postgres / keycloak / qdrant / prometheus / loki / tempo / grafana の **7 件すべて `Recreate`** |
| Prometheus retention | `0s` → **`retention.time=15d` / `retention.size=4GiB`**（`/api/v1/status/flags` で確認） |
| **Qdrant の永続化** | 切替直後 0 件 → ingestion 再起動で 2 件再作成 → **Qdrant 再起動後も 2 件残存** |
| **Prometheus の永続化** | 再起動前後で `numSeries` が **8564 のまま**。`/prometheus/data` に `chunks_head` / `wal` / **`lock`** が残存 |

> `lock` ファイルの実在は、**`Recreate` が要る理由そのもの**の裏取りでもある。

## 6. 既知の制約

- **切替時に Qdrant の既存コレクションは失われる**（emptyDir → 空 PVC）。README に snapshot API での退避を明記した。
- **PVC が実際に Bound するかは CI では検査しない**（クラスタが要る）。静的検査が固定するのは
  「`claimName` に対応する PVC が同じ overlay に宣言されている」ことまで。

## 7. 採番の衝突（解消済み）

- 着手時、`0209` は**未マージの PR #814 が予約**していたため、本 PR は先着尊重で `IADR-0210` を採った。
  その間 `check-adr-numbering.js` は `missing-number IADR-0209` で落ちる状態だった。
  **`IADR-0144` 決定 3「並行 PR の衝突は着地後にしか見えない」の型**である。
- **#814 が着地したので develop を取り込み、欠番は解消した**（`check-adr-numbering.js` exit 0）。
  索引の衝突は `IADR-0209` → `IADR-0210` の順で両方残して解決した。

> **★ 索引の競合解決で、conflict マーカーを残したままコミットしかけた。**
> `node -e` に渡した複数行スクリプトが**エラーも出さずに no-op を返した**（既知の罠）。
> `git diff --check` が拾ったので commit --amend で是正した。
> **競合解決の後は必ず `git diff --check` を通す。**

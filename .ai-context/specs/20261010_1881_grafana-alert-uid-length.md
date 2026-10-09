---
title: 作業仕様書 — Grafana の警報ルールの UID 3 件が上限 40 文字を超え、プロビジョニングが失敗して Grafana が起動しない（#1881）
type: spec
status: done
related_ids: [NFR-21, ADR-0006, IADR-0165, IADR-0168]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md（アラートは Alertmanager。Grafana の警報は決定 42 の暫定）
issue: "#1881"
---

# 作業仕様書 — Grafana の警報ルールの UID の上限（#1881）

> 本仕様書は実装着手前に作成した（着手 2026-10-10）。基点は MSP `origin/develop` `34c6fd4e`。
> 新しい IADR は起こさない —— 規約の変更ではなく、Grafana の既存の制約（UID ≤ 40 文字・`[A-Za-z0-9_-]`）への
> 適合と、既存の検査器 `scripts/check-grafana-alerting.js` への検査 7 の追加だけである（判断の記録は本仕様書と検査器の冒頭）。

## 起点（トレーサビリティ）

- 起点 issue: **#1881**（PoC 実測。`OBSERVABILITY=1`、helm rev 24、`c0dfd319`）。
- 計画: **ADR-0006**（観測・アラート）・**NFR-21**（障害検出 5 分以内）。実装 ADR: **IADR-0165**（Grafana の暫定警報）・**IADR-0168**（provisioning の経路間パリティ）。

## 事象と原因

Grafana の警報ルールの UID は 40 文字以下（Grafana `pkg/util` の `MaxUIDLength`）かつ `^[a-zA-Z0-9\-\_]*$`（`IsValidShortUID`）でなければならない。
provisioning は 1 件でも不正なルールがあると全体が失敗し、Grafana が CrashLoopBackOff になる。結果として**全部の SLO 警報が評価されていなかった。**

## 母集合（規則 9: 誤りの側の文字列で全文書を走査）

1. **問題の 3 UID**（`git grep -n -E 'knowledge-health-unresolved-links-producer-absent|knowledge-health-edge-type-usage-producer-absent|mcp-client-idp-reconciliation-series-absent'`、全追跡ファイル）:
   - `deploy/grafana/provisioning/alerting/slo-alerts.yaml` 3 行（452 / 480 / 686）
   - `deploy/local/observability/grafana.yaml` 3 行（908 / 936 / 1142。`slo-alerts.yaml` の inline）
   - **他に 0 件**（runbook・docs・ダッシュボード・試験・スクリプト・helm・`.ai-context/` のいずれにも無い）。
2. **40 文字を超える他の UID**（`deploy/` の `uid:` / `"uid":` を全件長さ順に列挙）: 上の 3 件だけ。次点は
   `unit-documents-missing-project-attribute`（ちょうど 40。上限内なので触らない）。ダッシュボードの uid（最長 31）・datasource の uid も上限内。
3. **UID の参照元**: contactPoints / policies は意図的に置いていない（`slo-alerts.yaml` 冒頭 §1）。ダッシュボード JSON に警報ルールへのリンクは無い
   （`llm-usage` はダッシュボードの uid で、ルールの uid ではない）。helm chart（`deploy/helm/microservices-platform/`）は Grafana の provisioning を持たない
   （経路 B は `scripts/k8s-local-up.sh` が kustomize の `deploy/local/observability*` を適用する）。**helm が描画する写しは存在しない。**
4. **写しの同期**: 既存の `check-grafana-alerting.js` 検査 4 が compose と k8s の inline の同内容（正規化後の一致）を見ている。UID の制約は見ていなかった。
5. **規則 10（この変更で新たに誤りになる自分の記述）**: 両ファイルの冒頭の「機械で確かめたのは…」の列挙と件数（22 のまま。実体は #1818 で 24）、
   `docs/operations/operations.md` の「同じ 22 ルール」。件数は `grep -c '^      - uid:'` で計算し直して 24。

## 決定

| 旧 UID（文字数） | 新 UID（文字数） |
| --- | --- |
| `knowledge-health-unresolved-links-producer-absent`（49） | `knowledge-health-unresolved-links-absent`（40） |
| `knowledge-health-edge-type-usage-producer-absent`（48） | `knowledge-health-edge-type-usage-absent`（39） |
| `mcp-client-idp-reconciliation-series-absent`（43） | `mcp-client-idp-reconcile-series-absent`（38） |

- 命名: 群名（`knowledge-health-` / `mcp-client-idp-`）を前置きし、不在系は `-absent` / `-series-absent` で終える既存の形を保つ。
  `producer` は群名（`knowledge-health-producers`）と title が持つので UID からは落とす。**title・式・意味は変えない。**
- UID は永続キーだが、既存環境で一度も作成に成功していない（provisioning が全体失敗していた）ので、改名による孤児は生じない。
- **再発防止**: 新しい検査器は作らず、既存の `check-grafana-alerting.js` に**検査 7**を足す（issue の「既存のテストに足すだけで済むならそれで足りる」に沿う。同型事故は 1 回目）。
  compose と k8s の inline の**両方**について、各ルールの `uid` が必須・40 文字以下・`[A-Za-z0-9_-]` のみ・ファイル内で一意であること、
  および両者の UID の集合が一致することを見る。YAML は検査 6 と同じ部分集合パーサの木で読む（読めなければ違反）。
  CI では既存の `scripts.repo.test.js`（`ci.yml` の scripts テストジョブ）経由で走る。**必須 check 名は変えない。**

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 | 両ファイルの UID がすべて 40 文字以下 | `node scripts/check-grafana-alerting.js`（実データ 24 件 OK）・`scripts.repo.test.js` #1881 節（全 UID ≤ 40） |
| AC2 | 41 文字以上の UID を写しの両方で検出する | self-test「uid が 40 文字を超えるルールを写しの両方で検出する」（境界 40 は通す）・`scripts.repo.test.js` 実データ変異試験 |
| AC3 | 許されない文字・欠落・重複・写し間の UID 集合の差を検出する | self-test 2 件（名指しで `scripts.repo.test.js` が走行を確認） |
| AC4 | title・式は不変 | diff（`uid:` 行と冒頭コメントだけ） |

## 検証

- `node scripts/check-grafana-alerting.js --self-test`（63 件）／実データ OK。実データの UID を元の 43 文字へ戻すと exit 1（違反 4 件）を確認。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 緑。
- Grafana 実機での受理は本環境では確かめられない（docker / クラスタなし）。**配備時に `/api/v1/provisioning/alert-rules` が 24 件返すことを確かめる。**

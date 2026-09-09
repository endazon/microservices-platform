---
title: LLM 月次予算アラートの前提 3（[30d] 窓を評価できる保持）を解消する —— 経路 B / compose の Prometheus 保持を 7d → 35d
type: spec
status: done
related_ids: [FR-11, NFR-21, SC-10, ADR-0044, IADR-0210, IADR-0312, IADR-0369]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 前提 3 だけを先に解く（#1111）

## 起点

- FR-11 / NFR-21 / SC-10 / 計画 `ADR-0044`（LLM 費用の統制）。issue #1111（LLM 月次予算の上限アラート）
- #1111 は `blocked`（前提 1: **月次予算の金額は計画側が「実測を待って確定する」と定めており実装側で数字を置けない**。#380 の実績蓄積の下流）。
  **本作業は前提 1 に触れない。** 棚卸し（2026-09-05）が「前提 3 は PVC ではなく保持期間の話になった。7d では `[30d]` の窓が常に空」と
  指摘した**前提 3 だけ**を、deploy の設定変更で解消する。

## 現状（実測。作業ツリー）

```
$ grep -rn "retention.time" deploy
deploy/docker-compose.yml:194:          "--storage.tsdb.retention.time=7d",
deploy/local/observability/prometheus.yaml:186: - "--storage.tsdb.retention.time=7d"
```

[[IADR-0210]] 決定 3: 保持は args で明示し、compose と経路 B で**同じ 2 引数**を置く（パリティ）。`size=4GB` は PVC 5Gi 未満に取り、
満杯 → 書き込み不能を「形」で塞ぐ。永続化は [[IADR-0369]] 決定 1 で既定オン。

## 母集合（規則 1・2・9）

誤りの側の語 `retention.time=7d` / `7d / 4GB` / `保持期間` で `deploy` `docs` `.ai-context` を走査:

| 位置 | 扱い |
| --- | --- |
| `deploy/docker-compose.yml:194` / `deploy/local/observability/prometheus.yaml:186` | **変更**（2 引数の片方。パリティ維持） |
| `deploy/local/README.md:114,120` / `deploy/local/observability/README.md:101` / `docs/operations/operations.md:207` | **追随**（値の写し） |
| `.ai-context/specs/20260816_issue-787_*.md` / `20260830_issue-1090-546_*.md` | 除外（凍結記録。書いた時点の値） |
| `docs/observability/llm-completion-metrics.md:207-209`（`[7d]` の PromQL 例） | 除外（窓の例であり保持の値ではない） |

## 決定

1. **`retention.time` を 7d → 35d**（30 日窓 ＋ 評価の余裕 5 日）。`size=4GB` は据え置く（PVC 5Gi 未満という不変条件を保つ）。
2. 🔴 **size の上限が先に効くと窓の先頭が欠ける**ことは受容する（流入量の実測が無い。欠けたら PVC と size を**対で**上げる —— 決定 3 の形）。
3. 本番像（helm）に Prometheus は無い（射程は経路 B / compose に閉じる。[[IADR-0210]] のとおり）。
4. **アラート本体（しきい値）は置かない。** 前提 1 は計画側の裁定事項である（#1111 の `blocked` は据え置き）。

## 受け入れ基準

- [x] 2 経路の `retention.time` が同じ値（35d）で、`size` は変えていない（パリティ）
- [x] 値を写した文書 3 件を追随した
- [ ] 🔴 **稼働クラスタでの確認は利用者の手が要る**: `kubectl -n platform-infra exec deploy/prometheus -- wget -qO- localhost:9090/api/v1/status/flags` の
  `storage.tsdb.retention.time` が `35d`、かつ 30 日後に `count_over_time(llm_cost_total[30d])` が空でないこと

## 変えていないもの

- `retention.size` / PVC 容量 / Tier 3 の対象外宣言 / アラート規則（#1111 本体）

---
title: 作業仕様書 — SC-10 に SLO・LLM コストの Grafana への導線カードを置き、不在を固定していた試験を反転する（#2125）
type: spec
status: done
related_ids: [FR-10, SC-10, UC-05, IADR-0121, IADR-0129, IADR-0134]
author: claude
created: 2026-10-29
updated: 2026-10-29
issue: "#2125"
---

# 作業仕様書 — SC-10 の Grafana への導線カード（#2125）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。develop `a2226815`）。親は #2047（指摘 M-24 の SC-10）。

## 現状（実測）

- 計画 SC-10 §主要素は「**SLO・レイテンシと LLM コストは Grafana への導線カード**」と定める。KPI カードの確定（Q25 / Q26）は、値を BFF で集約せず Grafana が正としたうえで「Grafana への導線カードは引き続き残す」とする。
- hi-fi（`sc-10.html` 419・421）は「SLO・レイテンシ ／ Grafana で確認 → ／ 達成率・検索 p95 は可観測性基盤が正」「LLMコスト ／ Grafana で確認 → ／ 使用量は可観測性基盤が担う…」を描く。
- 実装（`OperationsDashboardPage.tsx`）に 2 枚が無い。試験 `renders only the three KPI cards the contract can fill` が `SLO・レイテンシ` / `LLMコスト` の不在を固定している。画面仕様書 §hi-fi 対応 #3・#5 は「しない（契約の不在）」。
- Grafana の provisioning は `llm-usage` と `microservices-platform-overview` の 2 つのダッシュボードを持つ（`deploy/grafana/provisioning/dashboards/*.json` と `deploy/local/observability/grafana.yaml` に同じ uid）。

## 方針

- 2 枚の導線カードを置く。値は出さず、`Stat` の値の位置に「Grafana で確認 →」のリンクを置く（新しいタブ・`noreferrer`。専用ツールのボタンと同じ扱い）。
  - SLO・レイテンシ → `<grafanaUrl>/d/microservices-platform-overview`（HTTP p99・RAG の初回応答 p95 の盤）。
  - LLM コスト → `<grafanaUrl>/d/llm-usage`（LLM ゲートウェイのトークン使用量の盤）。
  - 接続先は既存の実行時 config `opsLinks.grafanaUrl`（ビルドに焼き込まない）。未設定ならカードは残し、リンクを持たず「Grafana の接続先が未設定です」と出す。
- カードは運用サマリの取得とは独立に描く（サマリが読めないときこそ Grafana への道が要る）。
- 送り先の組み立ては純関数 `grafanaGuides`（`opsTools.ts`）に置き、値集合と並びを描画なしで固定する（同ファイルの `opsTools` と同じ考え方。IADR-0129 決定 6）。
- 文言が 6 つ増え、カタログ（入口チャンク）が +0.54 kB になる。初期ロードの床は `check-chunk-budget --update` で上げる（意図した増加）。

## 受け入れ基準

- [x] SC-10 に SLO と LLM コストの導線カードがあり、Grafana の該当ダッシュボードの URL を指す（現行試験を反転）。修正前は赤
- [x] 接続先が未設定でもカードは残り、リンクを持たない
- [x] 数字（値）は出さない
- [x] en のロケールで指標名が訳される

## 母集合（規則 9）

`git grep -n "SLO・レイテンシ\|LLMコスト\|LLM コスト"` を `src/` と `docs/` で引いた。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `OperationsDashboardPage.tsx` の冒頭コメント（実装しない要素） | **直す** | 値だけが無く、カードは置く |
| `OperationsDashboardPage.test.tsx` の不在の主張 | **直す**（反転） | 本件 |
| `docs/screens/SC-10_operations-dashboard.md` §hi-fi 対応 #3・#5・レイアウト・(b)・未決事項 1・2 | **直す** | 同 |
| `docs/tests/SC-10_operations-dashboard.md` の対応表・#12・#13・純関数・件数の表 | **直す** | 同。件数の表は従前から古かった（21 件・2 ファイル → 実測 40 件・3 ファイル）ので実測し直した |
| 副題「SLO・コストは Grafana で参照」 | 変えない | 引き続き正しい |

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。

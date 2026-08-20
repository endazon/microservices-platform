---
title: 性能（NFR）負荷試験ハーネスの整備（Issue #196）
type: spec
status: done
related_ids:
  - NFR
  - FR-02
  - FR-03
  - IADR-0052
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 性能・受け入れ基準)
---

# 仕様書: 性能（NFR）負荷試験ハーネスの整備（Issue #196）

## 起点となる計画書（トレーサビリティ）

- 非機能要件(NFR): 性能目標（検索 p95 ≤ 1.5s／RAG 初回 p95 ≤ 5s／取り込み ≥ 1 万件・時／更新 15 分以内反映）と
  受け入れ基準「主要画面・API で p95 レイテンシ目標を満たす（負荷試験で確認）」
- 関連 FR: FR-03（横断検索）／FR-02（取り込み）
- 関連 ADR: [IADR-0052](../adr/IADR-0052_load-test-tooling-k6.md)（負荷試験ツールに k6 を採用・本 PR で作成）
- Issue: #196

## 目的・背景

性能目標の**実測は環境ブロック**（デプロイ済み環境＋本番相当データ＋LLM 経路構成が必要）で #196 は OPEN 維持が
妥当。ただし環境非依存で前進できる**受け皿**（負荷試験ハーネス・測定手順・テスト仕様）を整備し、環境が用意でき
次第すぐ実測できる状態にする。

## 対象範囲（本 PR）

- 対象:
  - **k6 ハーネス** `perf/k6/`: `search-load.js`（threshold `p(95)<1500`）・`rag-load.js`（`p(95)<5000`）・
    `lib/config.js`（BASE_URL・認証＝TOKEN もしくは Keycloak パスワードグラント・クエリ）。threshold 未達で非ゼロ終了。
  - **測定手順** `perf/k6/README.md`: 取り込みスループット（投入→完了レート）・反映時間（検索ポーリング）・
    観察（Grafana／Prometheus SLO アラート #198／Tempo）・調整（HPA #197）。
  - **性能テスト仕様** `docs/tests/NFR-01_performance-load-test.md`（P-01〜P-04・実測記録表）。
  - FR-02/FR-03 テスト仕様の「負荷試験は別タスク」記述を本ハーネス・#196 へ紐付け。
- 対象外:
  - **実測の実行**（環境ブロック。環境準備後に実施し #196 をクローズ）。
  - CI/nightly ゲート化（ステージング整備 #207／IADR-0049 と連動）。

## 実装方針

1. ツールは **k6** を採用（[IADR-0052](../adr/IADR-0052_load-test-tooling-k6.md)）。SLO を threshold として宣言し、未達で非ゼロ終了＝ゲート化に適合。
2. 認証・接続先は env 経由（秘密はコミットしない）。BFF は必ず `/bff/*` 経由（フロント境界と一致）。
3. コード（バックエンド）変更なし＝CI のビルド/テストに影響しない（`perf/` 追加＋docs）。

## 受け入れ基準（Issue #196）との対応

- [x] 負荷試験ツールを選定し（k6・IADR-0052）、`/bff/search`・`/bff/analysis/ask`・取り込みのシナリオ/手順を作成。
- [x] 検索 p95/RAG p95 を threshold 化（未達で非ゼロ終了）。
- [x] 取り込みスループット・反映時間の測定手順を文書化。
- [x] 性能テスト仕様（実測記録表）を用意し、FR-02/FR-03 の「別タスク」を #196 へ紐付け。
- [ ] 実測の実施・記録（環境ブロックのため #196 は OPEN 維持。環境準備後に実施）。

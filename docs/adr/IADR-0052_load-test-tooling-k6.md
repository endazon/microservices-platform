---
title: IADR-0052 性能負荷試験ツールに k6 を採用する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-02
  - FR-03
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR: 性能・受け入れ基準)"
---

# IADR-0052: 性能負荷試験ツールに k6 を採用する

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（性能目標・受け入れ基準「負荷試験で確認」）／FR-02（取り込み）／FR-03（検索）
- 関連仕様書: `docs/specs/20260710_issue-196_load-test-harness.md`、`docs/tests/NFR-01_performance-load-test.md`、`perf/k6/`
- Issue: #196

## コンテキストと課題

NFR は「主要画面・API で p95 レイテンシ目標を満たす（負荷試験で確認）」を受け入れ基準とするが、負荷試験ハーネスが
リポジトリに存在しなかった（#193 の BFF 往復ベンチは局所実測）。実測は環境ブロックだが、環境非依存の受け皿として
ハーネスを用意するにあたり、ツールを選定する必要がある。

## 検討した選択肢

1. **k6（本決定）**: JS でシナリオ記述。threshold（`p(95)<1500` 等）を宣言でき、**未達で非ゼロ終了**するため
   そのまま nightly/ゲートに組み込める。SLO をコードで表現でき、CI 連携が容易。単一バイナリで導入が軽い。
2. **NBomber**（.NET）: 既存スタックと同じ C# で書ける利点があるが、HTTP ロードのシナリオ記述・閾値ゲートは
   k6 の方が定型化しており、負荷生成ノードの分散・エコシステム（Grafana 連携等）も k6 が成熟。
3. **JMeter 等 GUI 系**: リッチだが XML/GUI 中心で Git 管理・CI 連携・レビュー容易性で劣る。

## 決定

**k6 を採用する。** ハーネスは `perf/k6/`（`search-load.js`／`rag-load.js`／`lib/config.js`）に置き、SLO を
k6 threshold として宣言する（検索 p95<1500ms・RAG p95<5000ms・失敗率上限）。認証・接続先は env 経由（秘密は
非コミット）。取り込みスループット・反映時間は HTTP 負荷では測りにくいため、`perf/k6/README.md` の手順
（パイプライン投入→完了レート・検索ポーリング）で測る。

## 理由

- **SLO のコード表現＋ゲート化**: threshold 未達で非ゼロ終了するため、ステージング整備（[[IADR-0049]] / #207）後に
  nightly ゲートへ組み込める。合否が機械判定できる。
- **導入・レビュー容易性**: 単一バイナリ、JS スクリプトは Git 管理・PR レビューに乗る。Grafana/Prometheus（#198）・
  Tempo での観察と組み合わせやすい。
- **バックエンド非侵襲**: `perf/` 追加のみでアプリコードに手を入れず、CI のビルド/テストに影響しない。

## 影響

- 追加: `perf/k6/`（スクリプト・README）、`docs/tests/NFR-01_performance-load-test.md`（テスト仕様）、
  `docs/specs/20260710_issue-196_load-test-harness.md`（作業仕様）。
- コード変更なし。実測は環境ブロックのため #196 は OPEN 維持（受け皿の整備）。

## フォローアップ

- 実測の実施・記録（環境準備後）。結果に応じて HPA しきい値（#197）・RRF 等を調整。
- ステージング整備（#207 / [[IADR-0049]]）で nightly ゲート化を検討。

## 関連

- Supersedes: なし
- Superseded by: なし

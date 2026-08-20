---
title: 性能（NFR）負荷試験 テスト仕様書
type: test-spec
status: in-progress
created: 2026-07-10
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-02, FR-03, NFR-01]
adrs: [ADR-0031]
iadrs: [IADR-0052, IADR-0134]
specs: [01_requirements, 20260805_issue-512_spa-route-code-splitting]
issues: [#196, #197, #512]
-->

# テスト仕様書: 性能（NFR）負荷試験

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR）: 性能目標（検索 p95 ≤ 1.5s／RAG 初回 p95 ≤ 5s／取り込み ≥ 1 万件・時／更新 15 分以内反映）と
  受け入れ基準「主要画面・API で p95 レイテンシ目標を満たす（負荷試験で確認）」
- 関連する機能要求: ハイブリッド横断検索／取り込み
- Issue: #196

## テスト対象・範囲

- 対象: `/bff/search`（検索 p95）、`/bff/analysis/ask`（RAG p95）、取り込みパイプライン（スループット・反映時間）。
- 対象外: 個別ロジックの単体性能、外部 LLM プロバイダ自体の SLA。
- **§フロントエンドの初期ロードは別ハーネスである**——k6 ではなくビルド成果物の実測と
  Vitest / Playwright で見る（サーバ側の p95 とは指標も測り方も違うので節を分ける）。

## ハーネス

- **k6**（`perf/k6/`）。`search-load.js`（p95<1500）・`rag-load.js`（p95<5000）は threshold 未達で非ゼロ終了する。
  取り込みスループット・反映時間は `perf/k6/README.md` の手順（パイプライン投入レート・検索ポーリング）で測る。
- 観察は Grafana（`microservices-platform-overview`）＋ Prometheus SLO アラート（`deploy/prometheus/alerts.yml`・#198）＋ Tempo。

## テストケース一覧

| ID | 指標 | 手順 | 合格条件 | 区分 |
| --- | --- | --- | --- | --- |
| P-01 | 検索 p95 | `k6 run search-load.js`（本番相当データ・段階負荷） | `http_req_duration{scenario:search}` p(95) < 1500ms・失敗率 < 1% | 負荷（手動/環境） |
| P-02 | RAG 初回 p95 | `k6 run rag-load.js` | `http_req_duration{scenario:rag}` p(95) < 5000ms・失敗率 < 2% | 負荷（手動/環境） |
| P-03 | 取り込みスループット | N 件投入→`IngestionCompleted`/Qdrant points が N に達する時間 T を計測 | `N/T` ≥ 1 万件/時（必要ワーカー数を記録） | 負荷（手動/環境） |
| P-04 | 反映時間 | 一意語文書を投入→`/bff/search` ポーリングでヒットまで計測 | ≤ 15 分 | 負荷（手動/環境） |

## フロントエンドの初期ロード（#512）

**計画は初期バンドルの上限値を定めていない。** よって合否は (a) ビルドツールの既定予算
（Vite の 500 kB/チャンク）と (b) 前後の実測差で判定する。**目標値ではなく退行の検出**が目的である。

| ID | 指標 | 手順 | 合格条件 | 区分 |
| --- | --- | --- | --- | --- |
| P-05 | 1 チャンクの上限 | `pnpm run build`（**警告は stderr に出る**ので `2>&1`） | `Some chunks are larger than 500 kB` が出ない | ビルド（手動。**機械検査は未整備**——バンドル分割境界の実装判断が置いた但し書き） |
| P-06 | 画面が遅延側にあること | `pnpm vitest run knowledge/frontend/src/features/routeSplitting.test.ts` | 画面 11 本が feature index の静的 import に無く、遅延境界（`.preload` / `wrapInSuspense`）が宣言されている | 単体（CI） |
| P-07 | 共通シェル・認証・UI プリミティブが初期側にあること | `pnpm vitest run platform/frontend/src/foundation/routing/initialChunk.test.ts` | `Layout` / `NotFound` / `RequireAuth` / `RequireRole` / `AuthProvider` / `@platform/ui` が初期側で読まれる | 単体（CI） |
| P-08 | 分割成果物で実ブラウザから起動できること | `playwright test e2e/bundle-splitting.smoke.spec.ts` | 要求した資産がすべて 200・`pageerror` なし・`/assets/*.js` を 2 本以上読む・ログイン画面が描画される | E2E |
| P-09 | 外部 egress が**全チャンク**に無いこと | `node scripts/check-static-egress.js --require src/platform/frontend/dist` | 検出 0 件（走査対象は分割で 4 → 20 ファイルへ増えた。**判定は「検出 0 件」であってファイル数ではない**——ファイル数は画面やチャンク規則が変われば動く環境依存の値であり、参考値として書いている） | 成果物（CI） |

**実測（#512 時点。測定条件は仕様書: SPA のバンドルをルート単位で分割する）**:

| | 分割前（`68d91ce`） | 分割後 |
| --- | --- | --- |
| 最大チャンク | 632.98 kB | **274.33 kB** |
| 初期ロード JS 合計 | 632.98 kB（1 本） | **577.54 kB**（4 本） |
| 同 gzip | 190.04 kB | **177.94 kB** |
| 500 kB 警告 | あり | **なし** |

## 実測記録

> 実測はデプロイ済み環境（compose / k3s stg）＋本番相当データ＋（RAG は）LLM 経路構成が必要（環境ブロック）。
> 実行のたびに下表へ追記する（日時・環境・負荷レベル・p95/スループット・合否）。

| 日付 | 環境 | 指標 | 負荷レベル | 実測 | 合否 | 備考 |
| --- | --- | --- | --- | --- | --- | --- |
| （未実施） |  |  |  |  |  | 環境準備後に実施（#196 OPEN 維持） |

## 追随（実測後に更新する）

- 横断検索の受け入れ基準の未チェック 2 項（p95・15 分反映）を実測で更新する。
- HPA しきい値（#197 `scaling.hpa`）・リソース requests/limits・RRF k 値/候補数（横断検索の未決事項）を実測で調整する。
- 目標未達は改善タスクを分割起票する。

## 関連仕様

- ハーネス: `perf/k6/README.md`
- 機能: `../functional/FR-03_hybrid-search.md`、`../tests/FR-02_ingestion.md`、`../tests/FR-03_hybrid-search.md`
- 監視: `../operations/operations.md`（監視・アラート）、`deploy/prometheus/alerts.yml`
- スケール: `../../.ai-context/adr/IADR-0050_hpa-pdb-scaling-scope.md`、#197
- フロントの分割境界: `../../.ai-context/adr/IADR-0134_spa-route-code-splitting-boundaries.md`、`../../.ai-context/specs/20260805_issue-512_spa-route-code-splitting.md`

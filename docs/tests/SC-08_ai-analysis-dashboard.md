---
title: SC-08 AI分析ダッシュボード テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-08
  - UC-02
  - FR-05
  - FR-07
  - FR-11
  - IADR-0005
  - IADR-0111
  - IADR-0127
author: claude
created: 2026-07-08
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../screens/SC-08_ai-analysis-dashboard.md"
  - "../specs/20260805_issue-503_sc05-08-admin-screens.md"
  - "../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md"
---

# テスト仕様書: AI分析ダッシュボード（SC-08）

> **［2026-08-05 / #503］新スタックでの再実装に合わせて全面改訂した。**

対象: `src/knowledge/frontend/src/features/sc08-analysis/`
テスト: `analysisRange.test.ts`（純関数）／ `AnalysisDashboardPage.test.tsx`（Vitest + Testing Library）／
E2E は `src/platform/frontend/e2e/sc08-analysis.smoke.spec.ts`

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-08 ／ ユースケース（UC）: **UC-02**（AI 分析を依頼する）／ 機能要求（FR）: FR-07・FR-11・FR-05
  - 着手時点の issue #503 の表は「UC-05」と書いていたが、計画（05_screens 画面一覧・UC-02 §関連画面）は
    SC-08 を **UC-02** に対応づけている。**計画を正とした**（作業仕様書 §計画書との差異）。
    **issue 本文は 2026-08-05 に UC-02 へ訂正済みである。**

## UC-02 のフロー → テストの写像

| UC-02 のフロー | 画面での現れ方 | テスト |
| --- | --- | --- |
| **基本 1. 利用者が分析対象（タグ／フォルダ／検索条件）と分析内容を指定する** | **検索条件（`range.query`）と分析内容のみ**。タグ／フォルダのチップは契約が無く実装しない | `sends the task type and the search condition as the data range` ／ `does not render tag or folder chips (no contract for permitted candidates)` |
| **基本 3. AI 分析サービスが LLM ゲートウェイ経由で分析を実行する** | `POST /bff/analysis/analyze` | `runs an analysis and links the citations to SC-03` |
| **基本 4. 結果と出典を返す** | 結果パネル ＋ 出典（`/docs/$id` へのリンク） | 同上 |
| **代替. 機密区分の高いデータは外部 API へ送信せず、セルフホスト LLM で処理する** | **利用面のみ**——`model` が空なら「未使用（AI へ送信なし）」（[[IADR-0111]]）＋ 静的な注記 | `says the AI was not used when the model is empty` ／ `states the egress policy and the existence-hiding rule` |
| **例外. 対象が権限外の場合は対象から除外する。権限の有無は開示しない** | 空回答・403・404 を**同一の中立文言**へ寄せる | `shows the same neutral message for %s`（3 件） |
| 基本 2. 認可で対象範囲を権限内に限定する | **写像しない**（サーバ側が narrowing-only で行う。[[IADR-0005]]）。画面側は「クライアントがスコープを送らない」ことを純関数で固定する | `never sends an access scope or attribute filters from the client` |

## テストケース

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | 分析実行と出典 | UC-02 基本 3・4 | `POST /bff/analysis/analyze` を呼び、回答と出典（`/docs/$id`）を表示する |
| 2 | 要求の組み立て | FR-07 | タスク種別と検索条件が `{ instruction, taskType, range: { query } }` として載る |
| 3 | **存在秘匿（3 経路）** | **UC-02 例外** / [[IADR-0009]] | 空回答・403・404 のいずれでも同じ中立文言。`role="alert"` を出さない |
| 4 | **中立へ寄せない異常系** | — | 5xx はエラー（`role="alert"`）として出し、「該当なし」へ寄せない（誤解して再試行しなくなるため） |
| 5 | **縮退の可視化** | FR-11 / [[IADR-0111]] | `model` が空なら「未使用（AI へ送信なし）」 |
| 6 | モデル・トークン | FR-11 | `model` があればそれとトークン数を出す |
| 7 | 未入力 | — | 指示が空では実行できず、要求も出ない |
| 8 | 注記 | UC-02 代替・例外 | データ越境ポリシーと存在秘匿を明示する |
| 9 | **契約の不在**（実装しない要素） | 画面仕様書 §hi-fi 対応 #4 | タグ／フォルダのチップが無い。**先に「分析対象（権限内に限定）」の見出しと検索条件の欄が在ることを確かめてから**無いことを見る |
| 10 | ロケール `en` | ADR-0031 | 見出し・実行ボタンが英語で描画される |

## 純関数（`analysisRange.test.ts`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| P1 | タスク種別 | FR-07 の 3 値（分析 / 比較 / 抽出）と表示名 |
| P2 | 範囲の省略 | 検索条件が空なら `range` そのものを付けない（サーバの「省略時は instruction を流用」と食い違わせない） |
| P3 | 範囲の付与 | 検索条件があれば `range.query` に載る（前後空白は落とす） |
| P4 | **スコープを送らない** | FR-05。要求のキーは `instruction` / `taskType` / `range` のみ、`range` のキーは `query` のみ |
| P5 | 指示の上限 | 空・上限超過は送信不可（サーバの 400 を手前で防ぐ） |

## モックの当て先

本画面は **orval 生成フック（`useBffAnalysisAnalyze`）** に載る（[[IADR-0127]] 決定 3）。
生成コードは mutator（`bffFetch`）→ `apiRequest` を通るため、**モックは `apiRequest` に当てる**。
`apiFetch` を差し替えても生成コードの経路には効かない——ここを取り違えると、
「モックしたのに実際のネットワーク呼び出しが走る」形の見えない失敗になる。

> **［2026-08-06 追記］この節はかつて「モックの当て先（他の 3 画面と異なる）」であり、
> 「本画面だけが `useAnalysisAnalyze` に載る」と書いていた。両方とも失効している。**
> フック名は `operationId` の規約統一で `useBffAnalysisAnalyze` へ改名され（[[IADR-0135]] 決定 5 が
> [[IADR-0131]] 決定 3 を改定）、**当て先も特別ではなくなった**——#519 / [[IADR-0135]] 決定 4 で
> 画面テスト 13 ファイルすべてが `apiRequest` を差し替える形へ揃ったためである。

## 分析対象のチップ（#539 / 裁定 Q3）

**チップの部品と軸の定義は SC-01 と共有する**（`features/scope-filter`）。
共有そのものが裁定の要求である——「同じ『範囲を絞る』操作が画面ごとに違う挙動になると、
利用者は操作を覚え直すことになる」。**部品のテストは `../tests/SC-01_search-chat.md` の T-30〜T-43 を参照。**

本画面に固有なのは、選択が **`range.attributeFilters`** へ載ることである
（SC-01 は `attributeFilters` 直下。器が違う）。

| # | 確かめること | 実装 |
| --- | --- | --- |
| T-20 | ★ 選択したチップが `range.attributeFilters` へ載る | `puts the selected scope chips into the data range` |
| T-21 | **何も選ばなければ載らない**（旧来の要求と同じ形） | `omits the filters when no chip is selected` |

## 実行

- `pnpm run test -- knowledge/frontend/src/features/sc08-analysis`（純関数 **5** ＋ 画面 **14** ケース）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）

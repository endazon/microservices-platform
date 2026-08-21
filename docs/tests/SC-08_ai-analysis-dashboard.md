---
title: SC-08 AI分析ダッシュボード テスト仕様書
type: test-spec
status: completed
created: 2026-07-08
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-05, FR-07, FR-11, SC-01, SC-03, SC-08, UC-02, UC-05]
adrs: [ADR-0031]
iadrs: [IADR-0005, IADR-0009, IADR-0111, IADR-0127, IADR-0131, IADR-0135]
specs: [20260805_issue-503_sc05-08-admin-screens, SC-08_ai-analysis-dashboard]
issues: [#503, #519, #539]
-->

# テスト仕様書: AI分析ダッシュボード

> **［2026-08-05 / #503］新スタックでの再実装に合わせて全面改訂した。**

対象: `src/knowledge/frontend/src/features/sc08-analysis/`
テスト: `analysisRange.test.ts`（純関数）／ `AnalysisDashboardPage.test.tsx`（Vitest + Testing Library）／
E2E は `src/platform/frontend/e2e/sc08-analysis.smoke.spec.ts`

## 起点となる計画書（トレーサビリティ）

- 画面: AI 分析ダッシュボード ／ ユースケース: **AI 分析を依頼する** ／ 機能要求: 指定データ範囲の分析・LLM 送信先の切替・ABAC アクセス制御
  - 着手時点の issue #503 の表は本画面を「ABAC 権限を管理する」に対応づけていたが、計画側（画面一覧・
    AI 分析ユースケースの §関連画面）は本画面を **AI 分析を依頼する**に対応づけている。**計画を正とした**
    （作業仕様書 §計画書との差異）。**issue 本文は 2026-08-05 に訂正済みである。**

## ユースケースのフロー → テストの写像

| AI 分析のフロー | 画面での現れ方 | テスト |
| --- | --- | --- |
| **基本 1. 利用者が分析対象（タグ／フォルダ／検索条件）と分析内容を指定する** | **検索条件（`range.query`）と分析内容のみ**。タグ／フォルダのチップは契約が無く実装しない | `sends the task type and the search condition as the data range` ／ `does not render tag or folder chips (no contract for permitted candidates)` |
| **基本 3. AI 分析サービスが LLM ゲートウェイ経由で分析を実行する** | `POST /bff/analysis/analyze` | `runs an analysis and links the citations to SC-03` |
| **基本 4. 結果と出典を返す** | 結果パネル ＋ 出典（`/docs/$id` へのリンク） | 同上 |
| **代替. 機密区分の高いデータは外部 API へ送信せず、セルフホスト LLM で処理する** | **利用面のみ**——`model` が空なら「未使用（AI へ送信なし）」（縮退応答のモデル名の実装判断）＋ 静的な注記 | `says the AI was not used when the model is empty` ／ `states the egress policy and the existence-hiding rule` |
| **例外. 対象が権限外の場合は対象から除外する。権限の有無は開示しない** | 空回答・403・404 を**同一の中立文言**へ寄せる | `shows the same neutral message for %s`（3 件） |
| 基本 2. 認可で対象範囲を権限内に限定する | **写像しない**（サーバ側が narrowing-only で行う。データ範囲と ABAC の交差の実装判断）。画面側は「クライアントがスコープを送らない」ことを純関数で固定する | `never sends an access scope or attribute filters from the client` |

## テストケース

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | 分析実行と出典 | AI 分析 基本 3・4 | `POST /bff/analysis/analyze` を呼び、回答と出典（`/docs/$id`）を表示する |
| 2 | 要求の組み立て | —| タスク種別と検索条件が `{ instruction, taskType, range: { query } }` として載る |
| 3 | **存在秘匿（3 経路）** | **AI 分析 例外** / 権限外は 404 とする存在秘匿 | 空回答・403・404 のいずれでも同じ中立文言。`role="alert"` を出さない |
| 4 | **中立へ寄せない異常系** | — | 5xx はエラー（`role="alert"`）として出し、「該当なし」へ寄せない（誤解して再試行しなくなるため） |
| 5 | **縮退の可視化** | LLM 送信先切替 / 縮退応答のモデル名 | `model` が空なら「未使用（AI へ送信なし）」 |
| 6 | モデル・トークン | —| `model` があればそれとトークン数を出す |
| 7 | 未入力 | — | 指示が空では実行できず、要求も出ない |
| 8 | 注記 | AI 分析の代替・例外 | データ越境ポリシーと存在秘匿を明示する |
| 9 | **契約の不在**（実装しない要素） | 画面仕様書 §hi-fi 対応 #4 | タグ／フォルダのチップが無い。**先に「分析対象（権限内に限定）」の見出しと検索条件の欄が在ることを確かめてから**無いことを見る |
| 10 | ロケール `en` | —| 見出し・実行ボタンが英語で描画される |

## 純関数（`analysisRange.test.ts`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| P1 | タスク種別 | 指定データ範囲分析の 3 値（分析 / 比較 / 抽出）と表示名 |
| P2 | 範囲の省略 | 検索条件が空なら `range` そのものを付けない（サーバの「省略時は instruction を流用」と食い違わせない） |
| P3 | 範囲の付与 | 検索条件があれば `range.query` に載る（前後空白は落とす） |
| P4 | **スコープを送らない** | ABAC アクセス制御。要求のキーは `instruction` / `taskType` / `range` のみ、`range` のキーは `query` のみ |
| P5 | 指示の上限 | 空・上限超過は送信不可（サーバの 400 を手前で防ぐ） |

## モックの当て先

本画面は **orval 生成フック（`useBffAnalysisAnalyze`）** に載る（管理画面の実装方針による）。
生成コードは mutator（`bffFetch`）→ `apiRequest` を通るため、**モックは `apiRequest` に当てる**。
`apiFetch` を差し替えても生成コードの経路には効かない——ここを取り違えると、
「モックしたのに実際のネットワーク呼び出しが走る」形の見えない失敗になる。

> **［2026-08-06 追記］この節はかつて「モックの当て先（他の 3 画面と異なる）」であり、
> 「本画面だけが `useAnalysisAnalyze` に載る」と書いていた。両方とも失効している。**
> フック名は `operationId` の規約統一で `useBffAnalysisAnalyze` へ改名され（生成クライアントへの載せ替えが
> 従来の命名規約を改定した）、**当て先も特別ではなくなった**——#519 の載せ替えで
> 画面テスト 13 ファイルすべてが `apiRequest` を差し替える形へ揃ったためである。

## 分析対象のチップ（#539 / 裁定 Q3）

**チップの部品と軸の定義は検索・チャット画面と共有する**（`features/scope-filter`）。
共有そのものが裁定の要求である——「同じ『範囲を絞る』操作が画面ごとに違う挙動になると、
利用者は操作を覚え直すことになる」。**部品のテストは `../tests/SC-01_search-chat.md` の T-30〜T-43 を参照。**

本画面に固有なのは、選択が **`range.attributeFilters`** へ載ることである
（検索・チャット画面は `attributeFilters` 直下。器が違う）。

| # | 確かめること | 実装 |
| --- | --- | --- |
| T-20 | ★ 選択したチップが `range.attributeFilters` へ載る | `puts the selected scope chips into the data range` |
| T-21 | **何も選ばなければ載らない**（旧来の要求と同じ形） | `omits the filters when no chip is selected` |

## 実行

- `pnpm run test -- knowledge/frontend/src/features/sc08-analysis`（純関数 **5** ＋ 画面 **14** ケース）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）

<!-- trace-table:
row1: FR-07
row2: FR-11
row3: ADR-0031
-->

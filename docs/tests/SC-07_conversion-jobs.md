---
title: SC-07 変換ジョブ テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-07
  - UC-06
  - FR-12
  - IADR-0042
  - IADR-0127
author: claude
created: 2026-07-09
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - "../screens/SC-07_conversion-jobs.md"
  - "../specs/20260805_issue-503_sc05-08-admin-screens.md"
  - "../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md"
---

# テスト仕様書: 変換ジョブ（SC-07）

> **［2026-08-05 / #503］計画の 2026-08-04 確定（4 状態モデル・状態フィルタ・再変換の管理者ロール限定・
> 同一ジョブの直列化）へ追随して全面改訂した。**

対象: `src/knowledge/frontend/src/features/sc07-conversions/`
テスト: `jobStatus.test.ts`（純関数）／ `ConversionJobsPage.test.tsx`（Vitest + Testing Library）／
導線は `src/knowledge/frontend/src/features/adminFlow.test.tsx`／
E2E は `src/platform/frontend/e2e/sc07-conversions.smoke.spec.ts`

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-07 ／ ユースケース（UC）: **UC-06**（文書を正規化変換する）／ 機能要求（FR）: FR-12
- **計画の確定事項（2026-08-04。05_screens §SC-07 §データソース）を受け入れ基準として写像する。**
- 連携: **#501**（API 側の管理者ロール強制の突合。本書は**画面側**を固定する）

## 計画の確定事項 → テストの写像

| 計画の確定 | テスト |
| --- | --- |
| ジョブ状態モデルは **4 値** | `covers exactly the four statuses the plan fixed` ／ `maps %s to a labelled badge`（4 件）／ `lists jobs with the four-value status model` |
| デッドレターの表示は `failed` の**内訳** | **実装しない**（契約に標識が無い）。理由は画面仕様書 §実装しない要素 (b) |
| 照会 API は `GET /jobs` 相当・**状態でのフィルタ**を備える | `sends the status filter to the query API` ／ `starts with the "all" filter so the first view is not narrowed` |
| 再変換 API は `retry` 相当 | `lets an administrator retry a failed job` |
| **再変換の実行権限は管理者ロールに限る** | `lets an administrator retry a failed job` ／ **`hides the retry button from an operator and says why`** |
| 回数上限は設けない。**同一ジョブの再変換は直列化**し、実行中（`processing`）の要求は拒否する | `allows retry only for failed jobs`（純関数）／ `offers no retry for jobs that are not failed`（画面）／ **`explains the 409 rejection as a serialisation conflict`**（サーバ側の拒否） |

## UC-06 のフロー → テストの写像

| UC-06 のフロー | 画面での現れ方 | テスト |
| --- | --- | --- |
| **代替（2026-08-04 追記）. 変換ジョブの状況を照会する** | 一覧 ＋ 状態フィルタ | `lists jobs with the four-value status model` ／ `sends the status filter to the query API` |
| **代替（2026-08-04 追記）. 失敗した変換を再実行する** | `failed` の行の再変換ボタン（管理者のみ） | `lets an administrator retry a failed job` |
| **例外. 恒久失敗は再試行し、継続失敗はデッドレターへ送る** | `failed` として表示する（内訳は区別しない） | `lists jobs with the four-value status model`（`failed` の表示） |
| 基本 1〜4（受領・pandoc・図の LLM コード化・登録） | **写像しない**（ワーカー側の責務） | — |

## テストケース

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | 一覧 | UC-06 代替 | `GET /bff/conversion/jobs` を呼び、ジョブ ID・原本・**状態（4 値）**・備考を表示する |
| 2 | 状態フィルタ | 計画確定 | 選択で `?status=failed` を送る。**既定は「すべて」** |
| 3 | **再変換（管理者）** | **計画確定 2026-08-04** | `POST …/retry` を呼び、受付を伝える |
| 3-b | **再取得** | [[IADR-0127]] 決定 5 | 再変換の成功後に一覧を取り直す（`invalidateQueries` のみ） |
| 4 | **再変換（運用者に出さない）** | **計画確定 2026-08-04** / [[IADR-0127]] 決定 1 | 画面は見えるがボタンが無く、**「再変換は管理者のみ実行できます」と理由が出る**。**先に失敗ジョブの行が描かれていることを確かめてから**無いことを見る |
| 5 | 直列化（画面側） | 計画確定 | `failed` 以外の行に再変換を出さない |
| 6 | **直列化（サーバ側 409）** | 計画確定 | `not_retryable` を「実行中、または失敗以外の状態です」と伝える（`role="alert"`・`warning`） |
| 7 | 変換結果への導線 | 遷移図 `SC07 → SC03` | `succeeded` かつ `documentId` があれば `/docs/$id` へリンクする |
| 8 | **異常系（縮退しない）** | [[IADR-0042]] | 取得失敗を `role="alert"` で出し、**「ジョブはありません」へ寄せない** |
| 9 | 0 件 | — | 「該当する変換ジョブはありません。」 |
| 10 | **権限別の出し分け** | [[IADR-0035]] / [[IADR-0009]] | ロールを持たない利用者には画面が無い（`NotFound`）。**要求も出さない** |
| 11 | 導線 | 遷移図 | 「← データソース管理へ戻る」が `/admin/sources` を指す |
| 12 | **契約の不在**（実装しない要素） | 画面仕様書 §hi-fi 対応 #10・#12 | 人手補正の 2 ペインが無い。**先に管理者として再変換ボタンが在ることを確かめてから**無いことを見る |
| 13 | ロケール `en` | ADR-0031 | 見出しと状態が英語で描画される |

## 純関数（`jobStatus.test.ts`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| P1 | 値集合 | 計画確定の 4 値と完全一致する |
| P2 | 4 値の写像 | 各値に文言と tone が対で決まる（INDEX 決定 21） |
| P3 | **未知の状態** | 生値をそのまま出す（`—`・「不明」へ丸めない） |
| P4 | 再変換可否 | `failed` のみ `true` |

## 導線（`adminFlow.test.tsx`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A | SC-06 → SC-07 → SC-03 | データソース → 変換ジョブ → 変換結果の文書まで 1 本で通る |
| B | SC-07 → SC-06 | 「← データソース管理へ戻る」で戻れる |

## 実行

- `pnpm run test -- knowledge/frontend/src/features/sc07-conversions`（純関数 **7** ＋ 画面 **15** ケース）
- `pnpm run test -- knowledge/frontend/src/features/adminFlow.test.tsx`（導線）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）

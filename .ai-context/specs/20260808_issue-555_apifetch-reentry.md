---
title: 作業仕様書 画面からの apiFetch 再混入を ESLint で止める（#555）
type: spec
status: done
related_ids: [NFR, IADR-0121, IADR-0131, IADR-0135, IADR-0141, IADR-0146]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
related_specs:
  - ../adr/IADR-0146_apifetch-reentry-guard.md
---

# 仕様書: 画面からの apiFetch 再混入を止める（#555）

> **本作業は「違反を直す」作業ではない** —— 本番コードの `apiFetch` 呼び出しは既に 0 件である。
> 作るのは**壊れたときに止まる仕組み**であり、#519 の成果が静かに巻き戻る経路を塞ぐ。

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#555**（親 #454）／起点 ID: **NFR**
- 出所: **#519 のクロス監査**（トレーサビリティ監査 🟡 申し送り 4）
- 分類（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4）: **「機械検査を新設する」** —— クロス監査は**フェーズ末に 1 回**
  （同 決定 4 の 2026-08-08 追記）
- 制約: [IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 3（BFF 境界）／[IADR-0131](../adr/IADR-0131_openapi-as-bff-contract-source.md) 決定 4（SSE は生成対象外）

## 母集合の引き直し（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）

**走査基準**: `origin/develop` = `8fbd730`（#611 マージ後）。

**issue 本文の記述（「`apiFetch` の呼び出しは SPA から 0 件」）は前提であって母集合ではない。**
**誤りの側から引き直した** —— 「`apiFetch` を含むファイル」をまず全部出し、そこから
**呼び出し・import・コメントを分けた**。

| 軸 | 走査 | 実測 |
| --- | --- | ---: |
| `apiFetch` / `apiStream` を**文字列として含む**ファイル | `grep -rl` | **23** |
| うち **`apiFetch(` の呼び出し**（テスト除く） | `grep -rn 'apiFetch('` | **0** |
| うち **`apiStream(` の呼び出し**（テスト除く） | 同上 | **2**（`foundation/api/apiClient.ts` の定義本体 ＋ `sc01-search/useAskStream.ts`） |
| knowledge 配下の **`apiFetch` の import** | `grep -rn 'import .*apiFetch' knowledge/` | **0** |

**23 → 0 の差はすべてコメントである。** `useSearchQuery.ts` / `useDocumentAdmin.ts` /
`useDataSources.ts` / `useConversionJobs.ts` などは「旧実装は `apiFetch` だった」という**経緯の記述**を
持つだけで、呼び出しは無い。**ファイル名の一覧（`grep -l`）を母集合にすると 23 件の是正対象があるように
見える** —— 行まで見て初めて 0 件と分かる。

### 引いた軸と、引かなかった軸

| 軸 | 引いたか | 理由 |
| --- | --- | --- |
| `knowledge/frontend/src/**` | ✅ | **中身は `features/` だけ**（実測）。ここが「画面」の全体 |
| `platform/frontend/src/features/**` | ❌ | 合成点（`features/index.ts`）1 ファイルのみで画面が置かれない。詳細と見直し条件は [IADR-0146](../adr/IADR-0146_apifetch-reentry-guard.md)「検出しないこと」 |
| `ai-stock-trading/frontend/**` | ❌ | 別プロジェクトの submodule。本リポの規約を及ぼさない（既存の除外と同じ扱い） |
| `apiRequest` の直接利用 | ❌ | `apiFetch` の下位 API。画面からの利用は 0 件であり、**実際に起きた退行の型に絞る** |
| テストファイル | ✅（同じ規則が当たる） | knowledge 配下のテストは `apiFetch` を import していない（実測 0 件）。除外を作る必要が無かった |

## やったこと

`src/eslint.config.js` に定数 `NO_APIFETCH_IN_FEATURES` を追加し、
**既存の knowledge ブロックの `paths` へ展開**した（`no-restricted-imports` の `importNames`）。

### ★ 専用ブロックを新設しなかった理由（設計上いちばん効いた判断）

flat config は同一ルールを**後勝ちで置換**する。`features/**` を対象にした 2 本目の
`no-restricted-imports` を置くと、**既存ブロックの `BANNED_IMPORT_PATTERNS`（Redux / axios /
`@platform/ui` 内部参照の禁止）と `@features` 禁止が丸ごと無効化される**。
`src/eslint.config.js` の冒頭（9〜13 行目）が自らこの型を警告しており、
**新しい禁止を足すつもりで既存の禁止を消す**のが最も起きやすい事故である。

`knowledge/frontend/src/` の中身が `features/` だけであることを実測したので、
既存ブロックへ 1 行足すだけで足りた。

## 変異試験（実測）

| 変異 | 期待 | 結果 |
| --- | --- | --- |
| **正例（是正の対象）**: features へ `import { apiFetch }` を戻す | **落ちる** | **error 1 件**。`'apiFetch' import from '@foundation/api/apiClient' is restricted.` ＋ 規約メッセージ（`9 problems (1 error, 8 warnings)`） |
| **負例（SSE の例外）**: features へ `import { apiStream }` を足す | **通る** | **0 errors**（`8 problems (0 errors, 8 warnings)`） |
| **負例（現状）**: 変異なし | **通る** | **0 errors** |

**警告 8 件は既存の `react-refresh/only-export-components`** であり、本変更とは無関係
（是正前後で件数が変わらないことを実測した）。

## 素通りするもの（開示）

- **`platform/frontend/src/features/`**（合成点のみ・[IADR-0146](../adr/IADR-0146_apifetch-reentry-guard.md)「検出しないこと」）
- **`ai-stock-trading/frontend`**（別プロジェクト）
- **`apiRequest` の直接利用**（画面からの利用 0 件・禁止対象を広げない）
- **手書き型そのもの**。本検査が塞ぐのは呼び出し口であって型の書き方ではない

## 受け入れ基準（#555）

- [x] features 配下に `apiFetch` の呼び出しを足すと**検査が落ちる**（実測）
- [x] SSE（`apiStream`）と `foundation/api` 自身は**引き続き通る**（実測）
- [x] 例外の許可が**明示的**である ——**禁止対象を `apiFetch` に絞ること自体が例外の明示**
      であり、許可リストという手作業の更新点を作らない（[IADR-0146](../adr/IADR-0146_apifetch-reentry-guard.md) 決定 2）
- [x] `pnpm run lint` が 0 errors のまま

## 検証

```
cd src
pnpm install --frozen-lockfile
pnpm run lint            # 0 errors（警告 8 件は既存の react-refresh）
```

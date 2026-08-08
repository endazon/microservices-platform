---
title: IADR-0146 画面からの apiFetch 再混入を ESLint で止め、禁止対象を絞ることで例外表を作らない
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0121, IADR-0131, IADR-0135, IADR-0141]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md"
related_specs:
  - ../specs/20260808_issue-555_apifetch-reentry.md
---

# IADR-0146: 画面からの apiFetch 再混入を ESLint で止め、禁止対象を絞ることで例外表を作らない

- 状態: Accepted
- 日付: 2026-08-08
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（退行防止）／計画 ADR `ADR-0031`（フロントスタック）
- 関連 issue: [#555](https://github.com/endazon/microservices-platform/issues/555)（起点。親 [#454](https://github.com/endazon/microservices-platform/issues/454)）。出所は **#519 のクロス監査 🟡 申し送り 4**
- 関連する実装 ADR: [[IADR-0121]] 決定 3（BFF 境界・手書き HTTP クライアント禁止）／[[IADR-0131]] 決定 4（SSE は orval の生成対象外）／[[IADR-0135]]（生成物の採用）／[[IADR-0141]]（母集合）

## 背景 —— 「壊れても何も赤くならない」経路が空いていた

#519 が画面の通信を orval 生成物へ載せ替え、**本番コードの `apiFetch` 呼び出しは 0 件**になった。
これにより契約の変更が型検査で捕まるようになった。**しかしその状態を守る仕組みが無かった。**

ESLint は `fetch` / `XMLHttpRequest` / `axios` を止めるが、
**`apiFetch` は `foundation/api` の正規 API なので止まらない**（[[IADR-0121]] 決定 3）。
次の実装者が `apiFetch` ＋ 手書き型で書いても **CI は緑**であり、
その画面ぶんだけ「契約を変えても型検査が落ちない」状態が静かに戻る。
**テストも lint も型検査も緑のままなので、気づく手段が無い** ——
#512 の変異試験 M5a（共通シェルを遅延側へ移すとバンドルは縮み・テストは全 green・警告も出ない）と同じ型である。

## 決定

### 決定 1: ESLint の `no-restricted-imports` で止める（`scripts/` の検査器を作らない）

対象は import の静的検査であり **ESLint の守備範囲そのもの**である。
決定的だったのは**結線の要否**である。

| 案 | 結線 |
| --- | --- |
| **ESLint（採用）** | **不要**。既存の flat config に載り、`pnpm run lint` がそのままゲートになる |
| `scripts/` の 1 本 | `.github/workflows/` の編集が要り、**GitHub App 権限では行えない**。既存の呼び出し口へ相乗りする経路も、フロント依存（pnpm / node_modules）を `scripts-tests` ジョブへ持ち込むことになる |

「走らない検査を増やさない」（#512 の申し送り）に従い、**走ることが確実な方**を採る。

### 決定 2: **禁止するのは `apiFetch` だけ。`apiStream` は禁止しない**

SSE は orval が扱えず生成物が存在しないため（[[IADR-0131]] 決定 4）、
`apiStream` は **恒久的に正規の口**である。実際の利用箇所は
`knowledge/frontend/src/features/sc01-search/useAskStream.ts` の 1 箇所である。

**禁止の対象を `apiFetch` に絞ることが、そのまま例外の明示になっている。**
「`apiStream` も禁止し、SSE のファイルだけ許可リストへ載せる」形も採り得たが、
**許可リストという新しい手作業の更新点**が増える（#454 原則 12）。
`apiFetch` と `apiStream` は戻り値の型が違い、`apiStream` を REST の代用にはできないので、
絞っても抜け道にならない。

### 決定 3: **専用のブロックを新設せず、既存の knowledge ブロックへ足す**

flat config は同一ルールを**後勝ちで置換**する。`features/**` を対象にした 2 本目の
`no-restricted-imports` を置くと、**既存ブロックの `BANNED_IMPORT_PATTERNS` と
`@features` 禁止が丸ごと無効化される** —— `src/eslint.config.js` の冒頭が自ら警告している型である。

`knowledge/frontend/src/` の中身は **`features/` だけ**である（実測）。
したがって既存の knowledge ブロックの適用範囲がそのまま「画面」の範囲であり、
そこへ `paths` を 1 つ足せば足りる。

## 検出しないこと（本検査は網羅ではない）

- **`platform/frontend/src/features/` 配下**。ここは合成点（`features/index.ts`）1 ファイルだけであり、
  画面は置かれない（[[IADR-0121]]）。platform ブロックは `foundation/api` 自身を含むため
  `apiFetch` を一律に禁止できず、features だけを狙う 2 本目のブロックは決定 3 の置換問題を踏む。
  **将来 platform 側へ画面が置かれるなら、本決定を見直すこと。**
- **`ai-stock-trading/frontend`**。別プロジェクトの submodule であり、本リポの規約を及ぼさない
  （[[IADR-0120]] / 既存の除外と同じ扱い）。
- **`apiRequest` の直接利用**。`apiFetch` の下位にある低水準 API で、
  現に画面からの利用は 0 件である。禁止対象を広げるより、**実際に起きた退行の型に絞る**
  （広げると `foundation` 自身とテストの例外が増え、決定 2 の判断と矛盾する）。
- **手書き型そのもの**。`apiFetch` を使わずに手書き型を置くことは止められない。
  本検査が塞ぐのは「契約を迂回する呼び出し口」であって型の書き方ではない。

## 影響

- `src/eslint.config.js` に定数 `NO_APIFETCH_IN_FEATURES` を追加し、knowledge ブロックの `paths` へ展開した。
- **既存コードへの是正は 0 件**（本番コードの `apiFetch` 呼び出しは既に 0 件）。本決定は**予防**である。
- `pnpm run lint` は **0 errors** のまま（警告 8 件は既存の `react-refresh` のもので本決定とは無関係）。

## 棄却した案

| 案 | 棄却理由 |
| --- | --- |
| `scripts/` の検査器を新設する | `.github/workflows/` を編集できず結線できない（決定 1） |
| `apiStream` も禁止し許可リストで例外を管理する | 許可リストという手作業の更新点が増える。絞っても抜け道にならない（決定 2） |
| `features/**` 専用の ESLint ブロックを足す | flat config の後勝ち置換で既存の禁止が無効化される（決定 3） |
| `apiFetch` を `foundation/api` から export しない | `foundation` 内部と将来の正当な用途を塞ぐ。境界は「誰が呼ぶか」であって「在るか」ではない |

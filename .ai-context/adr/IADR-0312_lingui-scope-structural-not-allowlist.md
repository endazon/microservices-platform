---
title: IADR-0312 lingui の適用範囲は許可リストではなくユニット全体で表し、抽出範囲と一致させる
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0066
  - ADR-0031
  - ADR-0067
  - IADR-0125
  - IADR-0311
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0066_frontend-feature-isolation-and-import-direction.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
---

# IADR-0312: lingui の適用範囲は許可リストではなくユニット全体で表し、抽出範囲と一致させる

> **番号は `origin/develop` 時点の最大値（`IADR-0311`）＋ 1 で採った。** 並行して開いている PR が
> あるため、統括側がマージ時に採番を引き直してよい。

- 状態: Accepted
- 日付: 2026-08-30
- 起点: issue #1078。作業仕様書 `.ai-context/specs/20260830_issue-1078_lingui-scope-allowlist-removal.md`

## 起点・関連

- 計画 **ADR-0066 §理由** が、本件の許可リストを**規則の穴の実例として名指ししている**:
  「実装の ESLint 設定には画面を作るたびに `files` を伸ばす運用の規則が既にあり（lingui 適用範囲）、
  同じ形を増やすと伸ばし忘れが規則の穴になる」
- **IADR-0125 決定 6** が許可リストを作った当事者である。当時 SC-04〜11 は未 i18n であり、
  規則を全体へ及ぼすと「その issue では直さないと決めた箇所」の error が数百件出た。
  **決定 6 は「繰り延べであって放棄ではない」と明記していた。** 本 IADR はその繰り延べを消化する。

## コンテキストと課題

`src/eslint.config.js` の lingui ブロックの `files` は 19 行の許可リストであり、
**i18n 済みのファイルを人が 1 行ずつ登録する**運用だった。

**実測（2026-08-30 / `develop` `a2c7e5b1`）** —— ESLint の `calculateConfigForFile()` で、
lingui マクロを使う全ファイルについて規則が有効かを判定した:

| | |
| --- | ---: |
| lingui マクロを使うファイル | 68 |
| うち規則が**効いていない** | 🔴 **19** |

しかも取りこぼしは `features/` に限らなかった。`lib/scope-filter`（2）・
`components/notifications`（2）・`app/routing/breadcrumbs.ts`（1）・合成点 `features/index.ts`（1）は
**issue 本文の数え（`features/` 軸のみ）からも漏れていた**。

### これは 1 回目の事故ではない

issue 本文は「本件は 1 回目である」としていたが、履歴を引くと**独立した日付で 4 回**、
同じ形の取りこぼしが `develop` へ入っている（`git rev-parse --is-shallow-repository` = `false` を
確認したうえで `git log --diff-filter=A` で引いた）:

| 取りこぼした対象 | 入った PR | 日付 |
| --- | --- | --- |
| `features/sc04-wiki` | #233 → #1009 | 2026-07-11 / 08-23 |
| `features/sc18〜sc21` | #1009 | 2026-08-23 |
| `components/notifications` | #1021 | 2026-08-28 |
| `app/routing/breadcrumbs.ts` | #1045 | 2026-08-29 |
| `lib/scope-filter` | #1065 | 2026-08-30 |

さらに **PR 内で取りこぼしかけて捕まえた例が 2 回**ある（#1065 の `abac`・#1087 の `lib/i18n` と
`Layout.tsx`）。どちらも「この行を足さないと**静かに検査されなくなる**」とコメントに残っている ——
**危険が自覚されていたのに、自覚だけでは止まらなかった。**

## 決定

### 決定 1 — `files` の列挙を撤去し、両ユニット全体で表す

```js
files: ['platform/frontend/src/**/*.{ts,tsx}', 'knowledge/frontend/src/**/*.{ts,tsx}']
```

**19 行が 2 行になる。** 画面・feature・共有ディレクトリを足しても `eslint.config.js` を
触る必要が無い。**伸ばし忘れが構造的に起こり得なくなる。**

### 決定 2 — 検査器を足さない

`CLAUDE.md`「同型の事故が 2 回起きたら」の閾値は**満たしている**（上表のとおり 4 回）。
それでも検査器を足さない。**閾値が求めるのは同型の事故を止めることであって、検査器を増やすことではない。**
決定 1 で許可リストそのものが消えるため、「`features/` の一覧と `files` を突合する検査器」は
**検査する対象を失う**。穴を塞ぐ最短の手段は**検知ではなく消去**である。

### 決定 3 — 除外を 1 つも作らない（`lib/api` の文言も i18n する）

適用範囲を広げると `platform/.../lib/api` に 9 件の未国際化リテラルが出た。
**除外して回避しない。** `ApiError` の文言は `components/ui/apiErrors.ts` 経由で**画面に表示される**ため、
除外は「en ロケールで日本語が出る」不具合をそのまま残すことを意味する。
**除外リストを作った時点で「保守が人に戻る」構図が再発する。**

React の外なので `<Trans>` ではなく `i18n._(msg` … `)` を使う（`notificationMessages.ts` と同じ作法。
**新しいパターンを持ち込まない**）。**ja の表示は変わらない** —— msgid は原文そのもの
（`sourceLocale: 'ja'`。ハッシュ ID ではない）であり、i18n が未活性でも msgid へフォールバックする。
日本語文字列を assert している既存テストがそのまま通ることを実測で確かめた。

### 決定 4 — HTML のタグ・実体参照だけの文字列は文言として扱わない

`no-unlocalized-strings` の `ignore` へ `^(?:<[^>]*>|&[a-z]+;)+$` を足す。
`escapeHtml` の置換先（`&amp;` 等）と ECharts tooltip formatter の断片（`<br/>` `</b><br/>`）が
拾われたためである。**語を 1 つも含まない文字列しか当たらない**ので未国際化の文言を隠さない
（`<b>保存</b>` / `<b>Save changes</b>` が依然 error になることを注入試験で確認した）。

### 決定 5 — 範囲定義を共有モジュールへ括り出さない

`lingui.config.ts` の `catalogs[].include` と同じ範囲になるため、共有 `.mjs` へ括り出す案を検討した。
**採らない。** 範囲は 2 行であり、括り出すと新しいモジュール・knip 登録・TS からの `.mjs` 読み込みという
可動部が 3 つ増える（`CLAUDE.md` 禁止事項「過剰な抽象化」）。代わりに**両ファイルへ相互参照コメント**を置く。

## 理由

- **従来、カタログ抽出（`lingui.config.ts`）の範囲のほうが lint の範囲より広かった。**
  つまり「**抽出されるのに検査されない**」ファイルが構造的に生まれていた。決定 1 は 2 つの範囲を揃える。
- **決定 3 を採ると初期ロードが 0.95 kB 増える**（617.16 kB）。`lib/api` は共通シェルが起動時に通る
  経路であり遅延チャンクではないためで、床は実測値で更新し理由を `chunk-budget-baseline.json` に残した。
  **バンドルの都合で不具合を残さない。**

## 結果

- **良い影響**: 画面を足すときに `eslint.config.js` を思い出す必要が無くなる。
  en ロケールで BFF 境界のエラー文言が日本語で出る不具合が解消する。
  プレースホルダが `{0}` `{1}` から名前付き（`{shownCount}` 等）へ変わり、翻訳者が意味を読めるようになる。
- **悪い影響 / トレードオフ**:
  - 新しい画面を書くと**最初から** lingui 規則が効く。未 i18n のまま置く自由が無くなる（意図した効果）。
  - カタログが 21 件増え、初期ロードが 0.95 kB 増えた。**ロケール別カタログの遅延読み込みは
    引き続き未実施**であり、次に数 kB 増える作業が来たら分割を先に検討する（#1066 の申し送りを引き継ぐ）。

## 検証

- `pnpm run lint` **0 errors / 9 warnings**（基点と同一）
- **注入試験**: 新たに対象へ入った 5 箇所（`sc04-wiki` / `sc18-graph` / `lib/scope-filter` /
  `components/notifications` / `app/routing`）へ未国際化リテラルを注入し、すべて error になることを確認。
  **同じ 5 ファイルを `develop` の `eslint.config.js`（`--config` で差し替え）で走らせると lingui の
  error は 0 件**であり、従来それらが無検査だったことを実証した。
- `typecheck` / `build` / `format:check` / `check-i18n-catalogs`（未翻訳 0）ほか一式が緑。

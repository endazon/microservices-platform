---
title: Base UI（vendor-baseui）を初期ロードから外す（右レールの遅延化と ui チャンクの分離）
type: spec
status: done
related_ids: [NFR, ADR-0031, IADR-0121, IADR-0125, IADR-0134, IADR-0436, IADR-0439, IADR-0443]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# 仕様書: `vendor-baseui` の切り離し（#1437 作業 4）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（既存画面の挙動は変えない）
- 非機能要件（NFR）: **無採番**（初期ロード量の削減。`.claude/rules/traceability.md` §起点 ID の種別
  の場合 2 ではなく、`NFR-xx` の表に「初期ロードのバイト数」に当たる個別番号が無いための無採番。
  従前の同種作業（IADR-0134 / #556 / #788）と同じ扱いに揃える。環流しない）
- ユースケース（UC）: `UC-01`（AI への質問。右レールの経路のみ）
- 画面（SC）: `SC-01`（共通シェルの右レール AI チャットパネル）
- 関連 ADR: `ADR-0031`（フロントエンドスタック）/ `IADR-0121` 決定 4・5（共有 UI と右レール）/
  `IADR-0125` 決定 1（`@platform/ui` の公開面は 1 ファイル）/ `IADR-0134`（初期ロードの ratchet）/
  `IADR-0436`（Base UI 採用）/ `IADR-0439`（AI 対話の体験）
- 計画書リンク: `projects/microservices-platform/06_technical/13_frontend-stack.md`

## 目的・背景

`@base-ui/react`（`vendor-baseui`）が初期ロードに載っている。#1436 の統合実測（`scripts/chunk-budget-baseline.json`
の `$comment_initialTotalBytes_20260912_integration`）が「⚠️ 初期ロードの 12% が Base UI である。
Tooltip を右レールから外す／manualChunks を分けると 85 kB 前後は遅延へ動かせる。**次に初期ロードを
増やす作業で先に検討すること**」と申し送っており、#1437 作業 4 がその回収である。

**着手前の実測（develop `be072d2` ＋ AST submodule `7780edc`。`pnpm run build`）**

```
初期ロード合計 731,293 B（床 731,293 B）/ 最大チャンク 586.04 kB（上限 600.00 kB）/ 1 kB 未満の遅延チャンク 10 本
  index-x3QoMxE1.js        296,953
  vendor-react-C8NUNAFX.js 196,828
  vendor-baseui-B3c9JI1O.js 114,124   ← 初期ロードの 15.6%
  ui-DbQpoIC7.js            77,786
  vendor-query-D-RyhCi4.js  45,602
```

`dist/index.html` の `modulepreload` は上記 4 本（＋エントリ）で、`vendor-baseui` はそこに含まれる。

## 対象範囲

- 対象: `src/platform/frontend/src/components/ai-chat/`（右レールの分割）、
  `src/platform/frontend/vite.config.ts`（`manualChunks`）、`scripts/chunk-budget-baseline.json`（床）、
  文言カタログ（`platform/frontend/src/locales/{ja,en}`）
- 対象外: `@platform/ui` の公開面（`src/index.ts`）の変更、Base UI の採否そのもの（`IADR-0436` を覆さない）、
  Dialog を使う各画面、AST submodule（`src/ai-stock-trading`）の中身

## 設計

### 1. なぜ「片方だけ」では外れないか（実測で確定した因果）

`vendor-baseui` が初期ロードへ載る静的な辺は **2 本ある**。

**経路 A（アプリ側の静的到達）**
`app/Layout.tsx` → `<AiChatPanel />`（静的 import）→ `AiChatPanel.tsx` が `TooltipProvider` を静的 import、
内部の `AiChatRail` が `IconButton`（`Tooltip` / `TooltipTrigger` / `TooltipContent`）を使う。
実測: エントリ `index-x3QoMxE1.js` は `./vendor-baseui-B3c9JI1O.js` を**直接**静的 import している。

**経路 B（チャンク割り付け）**
`vite.config.ts` の `manualChunks` は `/packages/ui/` のソースを**すべて** `'ui'` へ寄せる。`ui` は
エントリが `Button` 等を引くため初期チャンクである。`packages/ui/src/components/{Tooltip,Dialog}.tsx` は
`@base-ui/react/{tooltip,dialog}` を静的 import しており、`@base-ui/` は `vendor-baseui` へ固定される。
実測: `ui-DbQpoIC7.js` の先頭行が `vendor-baseui-B3c9JI1O.js` からの import を持つ。

`manualChunks` はモジュール → チャンクの割り付けを**強制する**ため、**遅延ルートからしか到達しない
`Dialog.tsx` も `ui` に居座る**。したがって経路 A だけを切っても `ui` 経由で `vendor-baseui` は初期に残り、
経路 B だけを直しても右レールが Tooltip を引く限り新チャンクごと初期に載る。
**#1436 の G3 はこれを「React.lazy では回避できない」と記録していた**（片側だけを試したためである）。

### 2. 母集合（`@platform/ui` の `Tooltip` / `Dialog` を import する全箇所）

引き方: `grep -rn "Tooltip" / "Dialog" --include=*.ts --include=*.tsx src`（拡張子で絞らず `node_modules`
のみ除外。規則 3・5 に従い「部品名」と「`from '@platform/ui'`」の 2 軸で引いた）。

| 種別 | 箇所 | 初期／遅延 |
| --- | --- | --- |
| Tooltip | `platform/frontend/src/components/ai-chat/AiChatPanel.tsx`（`TooltipProvider`） | **初期** |
| Tooltip | `platform/frontend/src/components/ai-chat/IconButton.tsx`（`Tooltip`/`Trigger`/`Content`） | **初期**（`CopyButton` も経由） |
| Tooltip | `knowledge/frontend/src/features/sc01-search/components/SearchChatPage.tsx`（`TooltipProvider`） | 遅延（`lazyRouteComponent`） |
| Dialog | `knowledge/frontend/src/components/ConfirmDialog.tsx` → SC-07 / SC-17 / SC-19 / SC-20 | 遅延 |
| Dialog | `ai-stock-trading/frontend/src/features/sc02-risk-settings/components/RiskSettingsPage.tsx` | 遅延（submodule。本作業では触らない） |

**除外したものと理由**: `packages/ui/src/stories/Nocturne.stories.tsx`（Storybook。アプリのビルド対象外）/
`packages/ui/src/components/{Tooltip,Dialog}.{test,}.tsx`（部品本体と単体テスト）/
`ai-stock-trading/frontend/test/ui-stub/*`（AST のテスト用スタブ）/
`knowledge/frontend/src/lib/echarts/*` の `TooltipComponent`（ECharts であり Base UI とは別物）。

### 3. 決定（採用案）

**(1) 右レール本体を `React.lazy` で遅延にする（経路 A を切る）**

- `components/ai-chat/AiChatRail.tsx` を新設し、右レール本体（`AiChatRail` / `TurnItem` / `UserBubble` /
  `AnswerBubble`）と `TooltipProvider` のラップを移す。**Tooltip 系の import はこのファイルだけが持つ。**
- `AiChatPanel.tsx` は「閉じた列 ＋ ランチャー」だけを静的に持ち、開いたときだけ
  `lazy(() => import('./AiChatRail').then((m) => ({ default: m.AiChatRail })))` を
  `<Suspense fallback={<LoadingState label={t`AI チャットを読み込み中…`} />}>` で包む。
- 既定は閉じている（`aiChatStore.open = false`）ので、**初期表示で読みに行かない**。列そのものは
  従来どおり常に描かれ、開閉で本文の幅が揺れない（`IADR-0121` 決定 5 の性質を維持）。

**(2) `ui` チャンクから重なりの部品を外す（経路 B を切る）**

`manualChunks` に `'ui'` 規則より前段の規則を置き、`packages/ui/src/components/{Tooltip,Dialog}.tsx` を
`ui` から外す。名前つき（`'ui-overlay'`）と無名（`undefined` で Rollup の自動共有チャンクに委ねる）の
両方をビルドで実測し、初期ロードが小さい方を採る（採らなかった方の数値は IADR に残す）。

**実測の結果、名前つき（`'ui-overlay'`）を採った。**

| 変種 | 初期ロード | `ui` チャンクの `vendor-baseui` 静的 import |
| --- | --- | --- |
| 着手前 | 731,293 B | あり |
| 無名（`undefined`） | 721,151 B | **残る**（＝重なりの部品が `ui` に居座ったまま） |
| **名前つき `'ui-overlay'`** | **605,877 B** | **無し** |

無名では Rollup の自動分割が重なりの部品を `ui` へ吸収してしまい、経路 B が切れない。

🔴 **述語の文字列リテラルを `return` 文の中に書かない。** `check-chunk-budget.js --self-test` は
`manualChunks` の `return` 句に現れる裸の文字列（`/` を含まず `@` で始まらないもの）を**チャンク名**として
拾うため、`return id.includes('Tooltip') ? … : …` の形にすると `Tooltip` をチャンク名と誤認して落ちる。
`if (…) return 'ui-overlay';` の形にする。名前つきを採るなら `chunk-budget-baseline.json` の
`requiredChunks` にも同じ名前を足す（自己試験が完全一致を突き合わせる）。

### 4. 採らなかった案

- **(b) foundation 側の `LazyTooltip` ラッパ**: 動的 import の先が `ui` チャンク（既に初期）なので
  **単独では効かない**（経路 B が残る）。Tooltip ごとに Suspense 境界が要り、`TooltipTrigger render={…}` の
  合成も壊れやすい。`@platform/ui` 側で Tooltip だけ別エントリに切る案は**公開面 1 ファイル**規約
  （`IADR-0121` 決定 4 / `IADR-0125` 決定 1・ESLint の深い参照禁止）に反するため採れない。
- **(c) 初期側をネイティブ `title` 属性へ戻す**: `title` は**キーボードフォーカスで表示されない**ため
  利用者裁定 7（a11y を後退させない）に反する。しかも経路 B が残るので効果も無い。

## 受け入れ基準

- [x] `dist/index.html` の `modulepreload` に `vendor-baseui` が**現れない**（4 本 → 3 本＋エントリ）
- [x] `vendor-baseui` は遅延チャンクとして**残る**（114,124 B。`requiredChunks` 8 本すべて実在）
- [x] `node scripts/check-chunk-budget.js --require` が緑。床を 731,293 → **605,877** へ下げ、
      `$comment_initialTotalBytes_20260912_baseui-split` に前後の実測と内訳を書いた
- [x] 右レールの挙動が変わらない（`AiChatPanel.test.tsx` 15 検査が緑。変更は遅延 import を待つ 1 行のみ）
- [x] キーボード E2E（`keyboard-navigation.smoke.spec.ts`）と a11y E2E（`a11y.smoke.spec.ts`）が緑
- [x] 新規文言は ja / en とも埋まり `check-i18n-catalogs.js` が緑
- [x] **回帰ガードが実際に落ちることを変異試験で確認した** —— `AiChatPanel.tsx` の `lazy(...)` を
      静的 import へ戻すと `AiChatPanel.lazy.test.tsx` が赤になる（実測）

## テスト方針

- `AiChatPanel.test.tsx`: ランチャー押下後の取得を `findBy*` へ改める（遅延 import の解決を待つ）。
  「既定は閉じている」検査はそのまま（同期のまま通ることが遅延化の証明でもある）。
- **回帰ガード（新規 1 本）**: 閉じている間は `AiChatRail` モジュールが読み込まれないことを、
  `app/routing/initialChunk.test.ts`／`features/routeSplitting.test.ts` と同じ `vi.mock` のトレース手法
  （factory は実際に import されたときだけ評価される）で固定する。**これが無いと、誰かが
  `AiChatPanel.tsx` へ Tooltip を書き戻しても全部緑のまま初期ロードだけが太る。**
- E2E は既存の `a11y.smoke.spec.ts` / `sc01-search.smoke.spec.ts` が右レールを開く経路を踏む
  （Playwright の自動待機が遅延 import を吸収する）。

## 計画書との差異

- 差異: なし（画面仕様・a11y の要求は変えない。読み込みの分割だけを変える）

## 未決事項

- **IADR の採番**: 本作業は `IADR-0443` を用いる。`.ai-context/adr/` の最大は `IADR-0441` であり、
  `IADR-0442` は並行作業（#1438）が確保している。したがって**このブランチ単体では
  `check-adr-numbering.js` の判定 2（欠番なし）が `0442` 欠番で赤くなる**。親が cherry-pick で
  束ねた後に緑へ戻る前提で進める（コーディネータ裁定 2026-09-12）。
  ［2026-09-12 追記 / #1440］**束ね後に解消。** #1438（IADR-0442）と同じ PR（#1440）へ cherry-pick で束ねた時点で
  欠番は無くなり、`check-adr-numbering.js` は緑（親の実測）。

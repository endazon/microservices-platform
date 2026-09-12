---
title: IADR-0443 Base UI を初期ロードから外すには「右レールの遅延化」と「ui チャンクの分離」の両方が要る
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0031, SC-01, UC-01, IADR-0121, IADR-0125, IADR-0134, IADR-0436, IADR-0439]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
related_specs:
  - ../specs/20260912_1437_vendor-baseui-split.md
---

# IADR-0443: Base UI を初期ロードから外すには「右レールの遅延化」と「ui チャンクの分離」の両方が要る

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID:
  ADR-0031（計画リポ）（Accepted。フロントエンドスタック）／
  05_screens（計画リポ）（§共通シェル「AIチャットパネル（右レール）」＝ SC-01・UC-01）
- 関連する実装 ADR:
  [IADR-0436](IADR-0436_shared-ui-states-panels-and-base-ui-adoption.md)（**本決定が補完する**。
  Base UI を重なりの部品の土台に採る判断は不変で、**その配り方だけを決め直す**。**supersede ではない**）／
  [IADR-0134](IADR-0134_spa-route-code-splitting-boundaries.md)（初期ロードの ratchet）／
  [IADR-0121](IADR-0121_spa-stack-migration-staging.md) 決定 4（`@platform/ui` の公開面は 1 ファイル）／
  [IADR-0125](IADR-0125_ui-primitives-i18n-catalog-and-storybook.md) 決定 1（同上・深い参照の禁止）／
  [IADR-0439](IADR-0439_ai-chat-streaming-ux-and-markdown.md)（右レール AI 対話の体験）
- 関連する実装仕様書: [`20260912_1437_vendor-baseui-split.md`](../specs/20260912_1437_vendor-baseui-split.md)

## コンテキストと課題

`@base-ui/react`（チャンク `vendor-baseui`・114,124 B）が初期ロードに載っていた。
初期ロード合計 731,293 B の **15.6%** である（実測 2026-09-12。develop `be072d2` ＋ AST submodule `7780edc`）。

Base UI を実際に使うのは `@platform/ui` の `Dialog`（確認ダイアログ。SC-07 / SC-17 / SC-19 / SC-20 と
AST の SC-02＝いずれも遅延ルート）と `Tooltip`（右レール AI チャットのアイコンボタン）だけであり、
**最初の画面で必ず要るものではない**。#1436 の統合実測（`chunk-budget-baseline.json` の
`$comment_initialTotalBytes_20260912_integration`）は「Tooltip を右レールから外す／manualChunks を
分ければ 85 kB 前後は遅延へ動かせる。次に初期ロードを増やす作業で先に検討すること」と申し送っていた。

**課題は「どこを切れば外れるか」であり、そこに落とし穴があった。** #1436 の G3 は
「**React.lazy では回避できない**」と記録していたが、これは**片側だけを試した結論**である。

## 検討した選択肢

初期ロードへ `vendor-baseui` を引き込む**静的な辺は 2 本ある**（成果物で実測）。

| 経路 | 実体 | 実測の証跡 |
| --- | --- | --- |
| **A アプリ側の静的到達** | `app/Layout.tsx` → `<AiChatPanel />` → `TooltipProvider` / `IconButton` の `Tooltip` | エントリ `index-*.js` が `vendor-baseui-*.js` を直接 import |
| **B チャンク割り付け** | `manualChunks` が `/packages/ui/` を全部 `'ui'` へ寄せる。`ui` は初期チャンク。`Dialog.tsx` / `Tooltip.tsx` が `@base-ui/react` を静的 import | `ui-*.js` の先頭行が `vendor-baseui-*.js` からの import を持つ |

経路 B の肝は、**`manualChunks` の割り付けが到達経路を問わない**ことである。Dialog を使う画面は
すべて遅延ルートだが、`Dialog.tsx` は割り付け規則によって初期チャンク `ui` に居座る。

| 案 | 経路 A | 経路 B | 初期ロード（実測） | 判定 |
| --- | --- | --- | --- | --- |
| (a-1) 右レール本体を `React.lazy` にするだけ | 切れる | **残る** | **721,151 B**（−10.14 kB） | 不足 |
| (a-2) `manualChunks` を分けるだけ | **残る** | 切れる | （新チャンクごと初期に載るため実質不変） | 不足 |
| **(a) (a-1) ＋ (a-2)** | 切れる | 切れる | **605,877 B（−125.42 kB）** | **採用** |
| (b) foundation 側に `LazyTooltip` ラッパを置く | 切れる | **残る** | (a-1) と同じ | 棄却 |
| (c) 初期側の Tooltip をネイティブ `title` へ戻す | 切れる | **残る** | (a-1) と同じ | 棄却 |

**(b) の棄却理由**: 動的 import の先が `ui` チャンク（すでに初期）なので**単独では効かない**。
仮に (a-2) と併用しても、Tooltip ごとに Suspense 境界が要り `TooltipTrigger render={<Button …/>}` の
合成が壊れやすい。`@platform/ui` 側で Tooltip だけを別エントリに切る案は
**公開面は `src/index.ts` の 1 ファイル・深い参照は ESLint が禁止**（IADR-0121 決定 4 / IADR-0125 決定 1）
に正面から反するため採れない。

**(c) の棄却理由**: `title` 属性は**キーボードフォーカスで表示されない**（読み上げには届くがホバー以外の
経路で出ない）。a11y を後退させない裁定（2026-09-12 裁定 7・NFR-12。IADR-0440）に反する。
しかも経路 B が残るので**効果もほとんど無い**。

さらに (a-2) の書き方として「無名（`undefined`）にして Rollup の自動分割へ委ねる」も実測したが、
**重なりの部品は `ui` に残ったまま**で（`ui-*.js` の先頭に `vendor-baseui` の import が残る）、
初期ロードは 721,151 B にしか下がらなかった。**名前を付ける必要がある。**

## 決定

1. **右レール本体（`AiChatRail`）を `React.lazy` で遅延にする。**
   `components/ai-chat/AiChatRail.tsx` を新設し、レール本体と `TooltipProvider` を移す。
   **`@platform/ui` の Tooltip 系を静的 import してよいアプリ側モジュールはここだけ**とする。
   `AiChatPanel.tsx`（初期チャンク）は**閉じた列とランチャーだけ**を持ち、開いたときに
   `Suspense`（fallback = `LoadingState`）で本体を読む。既定は閉じている（`aiChatStore.open = false`）
   ので初期表示では読みに行かない。**列そのものは常に描く**——ここまで遅延にすると初期描画で
   3 列目が空のまま 1 往復待たされる（IADR-0121 決定 5 の「開閉で本文の幅が揺れない」を壊す）。

2. **`manualChunks` に `'ui-overlay'` を置き、`packages/ui/src/components/{Dialog,Tooltip}.tsx` を
   `ui` から外す。** `requiredChunks` にも同じ名前を足す（自己試験が完全一致を突き合わせる）。
   **無名に落とさない**（上記の実測）。

3. **`vendor-baseui` の規則は消さない。** 遅延チャンクとして残す——規則が無いと、ダイアログを使う
   画面ごとに Base UI の断片が散る（IADR-0436 の当初の理由がそのまま生きている）。

4. **回帰ガードを置く**（`AiChatPanel.lazy.test.tsx`）。閉じている間は `AiChatRail` モジュールが
   読み込まれないことを `vi.mock` のトレース（factory は実際に import されたときだけ評価される）で
   固定する。**専用のファイルに置く**——同居させると先に走った検査がレールを開いてしまい、常に緑になる。

## 理由

- **最も小さい変更で最大の効果が出る。** 変更はアプリ側 1 ファイルの分割と `manualChunks` 1 規則で、
  画面の挙動・a11y・公開面の規約をいずれも変えない。初期ロードは **−125.42 kB（−17.2%）**。
- **a11y を後退させない。** `IconButton` は `aria-label` を持ち続け、ツールチップも従来どおり出る
  （出るのが「開いた後」になるだけで、閉じている間はボタン自体が存在しない）。
- **Base UI の採否（IADR-0436）は動かさない。** 変えたのは**配り方**だけである。

## 結果

- 良い影響:
  - 初期ロード 731,293 → 605,877 B。内訳は vendor-baseui −114,124 / index −10,081 / ui −1,211。
    `dist/index.html` の `modulepreload` は 4 本 → 3 本（＋エントリ）になった。
  - 右レール本体が 6,741 B の独立チャンクになり、AI チャットを使わない利用者に届かなくなった。
- 悪い影響・トレードオフ:
  - **右レールを初めて開くとき 1 往復増える**（`AiChatRail` 6,741 B ＋ `ui-overlay` 1,248 B ＋
    `vendor-baseui` 114,124 B）。待ちは `LoadingState`（輪 ＋ 見える文言）で示す。
    **開くのは利用者の明示的な操作**であり、初期表示を全員に負担させるより筋が良い。
  - チャンクが 1 本増える（`ui-overlay` 1,248 B。1 kB 超なので `smallLazyChunks` は 10 のまま）。
  - 文言が 1 件増える（`AI チャットを読み込み中…`。ja / en とも実装済み）。
- フォローアップ:
  - **AST submodule を前進させたら床を測り直す**（合成ビルドのため AST 側の増減が初期ロードへ効く）。
  - Dialog を使う画面が増えても本決定は不変（`ui-overlay` は遅延のまま）。逆に**初期側の画面が
    Dialog / Tooltip を使い始めたら経路 A が復活する**ので、そのときは本 IADR を読み直すこと。

## 関連

- Supersedes: なし
- Superseded by: なし
- **補完**: IADR-0436（Base UI 採用）。採否は不変で、配り方だけを本決定が定める。

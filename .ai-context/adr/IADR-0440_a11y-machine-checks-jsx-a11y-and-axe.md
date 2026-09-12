---
title: IADR-0440 アクセシビリティの機械検査（jsx-a11y を error・axe の E2E・キーボード経路）と抑制ファイルへ逃がさない裁定
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-12, ADR-0031, SC-01, SC-05, IADR-0120, IADR-0121, IADR-0124, IADR-0125, IADR-0435, IADR-0436, IADR-0438]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
related_specs:
  - ../specs/20260912_frontend-nocturne-and-experience-gaps.md
---

# IADR-0440: アクセシビリティの機械検査と、抑制ファイルへ逃がさない裁定

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: 利用者裁定（2026-09-12 裁定 7）／実装は Claude

## 起点・関連

- 関連する計画書 ID:
  **NFR-12（アクセシビリティ）**（02_requirements（計画リポ））／
  13_frontend-stack（計画リポ）（fixed。§採用技術一覧 Linter 欄）／
  ADR-0031（計画リポ）／
  05_screens（計画リポ）§共通シェル（skip link・キーボード操作）／
  INDEX（計画リポ） 決定 21（色だけで意味を持たせない）
- 関連する実装 ADR:
  [IADR-0125](IADR-0125_ui-primitives-i18n-catalog-and-storybook.md)（共有 UI の公開面）／
  [IADR-0436](IADR-0436_shared-ui-states-panels-and-base-ui-adoption.md)（`Dialog` のフォーカストラップ・`initialFocus`）／
  [IADR-0435](IADR-0435_nocturne-token-layering-and-theme-switch.md)（コントラストの実測・切替ボタンの文言併用）／
  [IADR-0438](IADR-0438_route-level-error-boundary-pending-and-intent-preload.md)（`NotFound` の `<main>` 入れ子＝既存負債）／
  [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)（submodule ユニットは別プロジェクト。**本決定はこの線引きを一部越える**）／
  [IADR-0124](IADR-0124_tanstack-router-unit-composition.md)（決定 7 = a11y を型で強制する作法）
- 関連する実装仕様書:
  [20260912_frontend-nocturne-and-experience-gaps](../specs/20260912_frontend-nocturne-and-experience-gaps.md)
- 関連 issue: 本 PR（UI/UX 改善）。検討メモ 第 4 弾「体験の穴」(6) と次段「skip link とダイアログの閉じ込め」（別途送付）

## コンテキストと課題

計画（13_frontend-stack §採用技術一覧 Linter 欄）は TanStack / Testing Library / Storybook / Lingui の
プラグイン併用を挙げるが、**アクセシビリティの Linter が入っていない**。実装側も
`eslint-plugin-jsx-a11y` を持たず、a11y は**人が気をつける**だけで担保されていた。

実際には、本リポジトリは a11y を**作法として**は守っている（役割で引く E2E・色だけに頼らない
StatusBadge / Alert・確認ダイアログの初期フォーカス）。しかし**守られていることを機械が確かめていない**ため、
退行は人のレビューが拾わない限り入る。

決めるべきは 3 点。(a) 導入の強さ（warn か error か）、(b) 既存違反の扱い、(c) 適用範囲。

## 検討した選択肢

| 論点 | 案 | 却下理由 |
| --- | --- | --- |
| 強さ | **`error`（採用。裁定 7）** | — |
| | `warn` | **warn は無視される**（本リポジトリでの実測がある）。入れた事実だけが残る |
| 既存違反 | **今回すべて直す（採用。裁定 7）** | — |
| | `eslint-suppressions.json` へ入れて後日直す | 抑制ファイルは grandfather の器であって、**アクセシビリティの欠陥を抱えたまま緑にする**ための器ではない |
| 配置 | **独立したブロック（採用）** | — |
| | 各ユニットのブロックへ相乗り | flat config の**同名ルール後勝ち置換**で `no-restricted-imports` を落とす事故が起きる（本リポジトリで実測済みの型） |
| E2E | **`@axe-core/playwright`（採用）** | — |
| | 単体テストだけ | 実際に描画された DOM 全体のコントラスト・landmark・見出し階層は静的解析では見られない |

## 決定

### 決定 1: `eslint-plugin-jsx-a11y` の `flat.recommended` を **error** で採り、**独立したブロック**に置く

- 対象は `.tsx` だけでよい（TypeScript は `.ts` で JSX を許さないため、JSX は `.tsx` にしか無い）。
- **テストと stories も含める**（役割・ラベルの誤りはテスト側の期待にも現れる）。
- **既存違反は `eslint-suppressions.json` へ逃がさない。** 本 PR ですべて直す。

### 決定 2: 🔴 `settings['jsx-a11y'].components` の対応表を必ず与える

**この `settings` が無いと、規則はほぼ何も守らない。** `jsx-a11y` は JSX の**要素名しか見ない**ため、
画面が `<Input>` / `<Label>` / `<Button>`（＝ `@platform/ui` のプリミティブ）で書かれている本リポでは
`label-has-associated-control` も `alt-text` も**素通りする**。
**実測: 対応表なしでは全 182 ファイルで 0 件。**

**`@platform/ui` にネイティブ要素を包むプリミティブを足したら、この対応表へも足すこと。**

### 決定 3: 適用範囲に `ai-stock-trading/**` を**含める**

TanStack / Testing Library の各ブロックは submodule ユニットを外している（IADR-0120 の線引き
＝本リポの**技術選定**を他リポジトリへ及ぼさない）。**a11y はこの線引きの外側に置く** ——
**アクセシビリティは技術選定ではなく、合成した SPA が利用者へ出す品質そのもの**であり、
当該ユニットの画面は本リポジトリのシェルの中で描画される。

実測（2026-09-12）: 当該ユニットの `.tsx` 34 ファイルで違反 0 件 —— **含めても現に落ちない。**
🔴 **落ちるようになったら、その時点で範囲から外すのではなく、当該リポジトリへ環流する。**

### 決定 4: E2E に `@axe-core/playwright` の走査と、キーボード経路の追跡を足す

- `a11y.smoke.spec.ts`: ログイン後のシェル ＋ SC-01 ＋ SC-05 の 3 面。`wcag2a` / `wcag2aa` タグで走査し、
  **違反 0 を assert する**。
- `keyboard-navigation.smoke.spec.ts`: skip link → 左ナビ → 本文 → ダイアログの閉じ込め・Esc・
  フォーカス復帰。
- 🔴 **spec 名に `sc` プレフィクスを使わない。** `scripts/check-route-manifest.js` が
  `sc<NN>-*.spec.ts` を画面の母集合として数えるため、横断の spec を混ぜると母集合が乱れる。
- 新規 E2E は `getByRole` のみで引く（testid へ逃げない）。

### 決定 5: CI の配線は既存の lint ジョブに乗せ、必須 check の表は変えない

`jsx-a11y` は `src/` の ESLint 実行にそのまま乗り、axe の spec は既存の Playwright 実行に乗る。
**新しい必須 check を増やさない**（`docs/ai-workflow.md` の表を変えない）。

## 理由

- 裁定 7 が「error で導入し、既存違反も今回すべて直す」と定めた。**warn で入れる案は、
  同じ器（抑制ファイル・warn）が過去に無視された実測があるため採らない。**
- 決定 2 は**検査が効いていないことを先に測った**結果である。対応表なしで「0 件」を見て
  「違反が無い」と読むのが最も危険な状態であり、それを避けるために実測を記録に残す。
- 決定 3 は、IADR-0120 の線引きの**理由**（技術選定を及ぼさない）が a11y には当てはまらない、
  という判断である。線引きそのものは有効なまま残す。

## 結果

- 良い影響:
  - 役割・ラベル・キーボード操作の退行が機械で止まる。
  - skip link とダイアログの閉じ込めが**テストで固定された**（実装の作法から、壊れたら落ちる資産へ）。
- 悪い影響・トレードオフ:
  - **`jsx-a11y` は静的解析である。** 実際の DOM で初めて分かるもの（コントラスト比・
    landmark の入れ子・見出し階層）は見ない。決定 4 の axe が補うが、走査するのは 3 面だけである。
  - **`NotFound` の `<main>` 入れ子（IADR-0438）は、どちらの検査でも落ちない。**
    静的解析は要素名しか見ず、axe の対象 3 面に `NotFound` は含まれない。
    - ［2026-09-12 追記 / #1438］**塞いだ**（[IADR-0442](IADR-0442_notfound-landmark-and-axe-scan-surfaces.md)）。
      タグへ `best-practice` を足し（**ランドマークの規則はこのタグにしか属さない** ——
      WCAG タグだけでは規則そのものが評価されていなかった）、走査面を 3 → 5 へ広げた
      （＋存在秘匿の 404 ＋ SC-19 の確認ダイアログ）。**静的解析が見ない点は変わらない**
      ——コンポーネント境界を跨ぐ入れ子は `jsx-a11y` の射程の外である。
  - 対応表（決定 2）は**手で維持する**。プリミティブを足して足し忘れると、その部品を使う画面が
    静かに検査から外れる（機械検査は無い）。
- フォローアップ:
  - **本 IADR の起草時点では、既存違反の件数を計測していない。**
    a11y の違反修正は同じ PR の別タスクが並行して実施中であり、
    **実測（違反件数・修正ファイル数・axe の結果）は作業仕様書
    [20260912_frontend-nocturne-and-experience-gaps](../specs/20260912_frontend-nocturne-and-experience-gaps.md)
    §検証記録に残す。** 本 IADR には決定と根拠だけを置く（推測値を書かない）。
  - axe の走査面を 3 面から広げるかは、実行時間の実測を見てから判断する。
    - ［2026-09-12 追記 / #1438］**5 面へ広げた**（IADR-0442 決定 3）。選ぶ軸は画面数ではなく
      **「通常の画面では届かない状態か」**とした（畳まれた DOM・例外の DOM）。

## 関連

- Supersedes: なし。
- Superseded by: なし。

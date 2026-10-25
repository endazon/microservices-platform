---
title: IADR-0614 axe の E2E は NFR-30 のタグで SPA の全画面と操作後の UI を走査し、既知の違反は理由つきの台帳と一致させる（Keycloak のテーマの画面は L1 の対象外）
type: impl-adr
status: Accepted
related_ids: [NFR-30, ADR-0031, ADR-0032, IADR-0440, IADR-0442, IADR-0435, IADR-0532, IADR-0569, IADR-0330]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-30）
  - planning:projects/microservices-platform/05_screens/01_screens.md（§共通シェル「アクセシビリティ」）
related_specs:
  - ../specs/20261024_2015_axe-all-screens.md
---

# IADR-0614: axe の E2E を NFR-30 のタグで全画面へ広げ、既知の違反は台帳で受ける

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2015）

## 起点・関連

- 関連する計画書 ID: NFR-30（WCAG 2.2 レベル AA。planning#814 の裁定 2026-10-21・2026-10-22）、計画 ADR-0031（フロントの技術）・ADR-0032（BFF セッション）。
- 前提とする実装 ADR: [IADR-0440](IADR-0440_a11y-machine-checks-jsx-a11y-and-axe.md)（jsx-a11y と axe の E2E）、
  [IADR-0442](IADR-0442_notfound-landmark-and-axe-scan-surfaces.md)（`best-practice` と走査面の選び方）、
  [IADR-0435](IADR-0435_nocturne-token-layering-and-theme-switch.md)（意味トークンとテーマ）、
  [IADR-0532](IADR-0532_keycloak-theme-nocturne-parents-and-token-sync.md)（Keycloak テーマのトークンは生成物）、
  [IADR-0569](IADR-0569_e2e-layers-and-live-opt-in.md)（E2E の層）、[IADR-0330](IADR-0330_authenticated-browser-e2e-via-bff-stub.md)（L1 のセッションの土台）。
- 作業仕様書: [20261024_2015_axe-all-screens](../specs/20261024_2015_axe-all-screens.md)。

## コンテキストと課題

- 計画は NFR-30 で「axe のタグ `wcag2a`・`wcag2aa`・`wcag21a`・`wcag21aa`・`wcag22aa` が検出する違反を 0 件に保つ」と定めた。
  実装の `a11y.smoke.spec.ts` は `wcag2a`・`wcag2aa`・`best-practice` で 6 面だけを走査し、テスト名は「WCAG 2.1 A/AA」と称していた。
- 網羅の設計（#2013 基準書 §8・網羅表 M-017）は「全画面・両テーマ・操作後の UI」と「既知の違反は `rule id + target` の指紋を `toMatchSnapshot` で固定」を求める。
- 全画面へ広げた初回の走査で、既存の 6 面では届かなかった違反が 3 面に出た（SC-19 一覧の `aria-valid-attr-value`、SC-06・SC-07 の `color-contrast`）。

## 決定

1. **タグは NFR-30 の 5 つ＋`best-practice` に固定し、走査のたびに「そのタグで走ったこと」を確かめる。**
   タグの組は `e2e/support/axe.ts` の 1 か所だけに置き、fixture `makeAxeBuilder` から作る。結果の `toolOptions.runOnly` を毎回突き合わせる
   ——タグが黙って欠けると、その版で加わった達成基準は違反 0 件のまま永久に緑になるためである。
2. **既知の違反は TS の台帳（`e2e/support/axeKnownViolations.ts`）に指紋と理由の組で載せ、走査結果と「一致」を求める。`toMatchSnapshot` は採らない。**
   指紋は `<規則 ID> <対象セレクタ>`（node 単位）。一致なので、増えれば落ち、直して減っても台帳から消すまで落ちる（受け入れが恒久化しない）。
   台帳のキーは spec の `expectAxeClean('<面>')` の呼び出しから作る「面 / テーマ」の集合に含まれなければならず、専用の 1 本が確かめる
   （綴り違い・改名で残ったキーは比べられずに死に行になるため。集合は実行時ではなく本文から作る——再試行・シャードで欠けない）。
   `toMatchSnapshot` を退けた理由は、`--update-snapshots` の一括更新で**理由なしに受け入れを増やせる**ことと、受け入れの理由を指紋の隣に書けないこと。
3. **Keycloak のテーマが描く画面（SC-13 の Keycloak 側・SC-14〜16）は L1 の対象外とする。** SPA のビルド成果物に含まれず、基盤を 1 つも起動しない層
   （IADR-0330 / IADR-0569）では描けない。Keycloak を起動する層での走査は別に扱う。SC-13 は SPA 側の入口（未認証で開くログイン画面）を走査する。
4. **画面側で直せる違反は直し、台帳へ載せない。** 現在の台帳は 0 件である。
   - SC-19 一覧: タブの器を一覧の末尾まで広げ、一覧を `TabsContent` に入れる（`aria-controls` の指し先を実在させる）。
   - ライトの `--color-warning` を `#8a6210` → `#845f0f` に下げる。`Note tone="warn"` は「warning の文字 × surface-muted の面」が部品の定義そのものであり、
     要素を避難させても同じ組み合わせが再び現れる（IADR-0435 の後のダークの accent と同じ直し方）。Keycloak テーマのトークンは再生成する（IADR-0532）。

## 退けた案

- **`toMatchSnapshot` で指紋を固定する**（基準書 §8 の書き方）: 決定 2 の理由で退けた。基準書との差分は `docs/tests/E2E_STANDARDS.md` §7 に書いた。
- **台帳を部分集合（既知に含まれれば可）で比べる**: 直した違反が台帳に残り続け、同じ指紋が再発しても緑のままになる。
- **`disableRules` で規則ごと外す／面を `exclude()` する**: 外した範囲の他の要素まで測られなくなる（IADR-0442 の裁定 7 と同じ）。
- **`Note` の warn だけ別の面（`bg-surface`）に載せる**: 部品の見た目を変え、他の「warning × muted」の組み合わせ（`Alert` 等の今後の配置）を直さない。

## 結果

- `a11y.smoke.spec.ts` は 28 面（SPA の全画面・共通シェル・存在秘匿の 404・確認ダイアログ 5 種・共有先の listbox・緊急アクセスの候補・編集パネル・タブ・競合の解決欄）× 2 テーマで違反 0 件。
- NFR-30 の「2.1・2.2 の機械検査は配備後に効く」は、本 IADR の PR がマージされた時点で L1 では効く（Keycloak のテーマの画面を除く）。
- 残余: Keycloak のテーマの画面の機械検査（L2 以降）、`src/eslint.config.js` と IADR-0440・0442 に残る「NFR-12（アクセシビリティ）」の誤参照（凍結記録は直さない）。

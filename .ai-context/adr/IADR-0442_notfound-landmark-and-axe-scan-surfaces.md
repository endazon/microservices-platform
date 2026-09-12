---
title: IADR-0442 NotFound のランドマーク重複を解消し、axe のタグへ best-practice を、走査面へ存在秘匿の 404 と確認ダイアログを加える
type: impl-adr
status: Accepted
related_ids: [NFR-12, ADR-0031, SC-11, SC-19, IADR-0009, IADR-0035, IADR-0120, IADR-0124, IADR-0134, IADR-0438, IADR-0440]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
related_specs:
  - ../specs/20260912_1438_notfound-landmark-and-axe-coverage.md
---

# IADR-0442: NotFound のランドマーク重複の解消と、axe の走査面・タグの拡張

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: 実装は Claude（#1438。裁定 7「抑制へ逃がさない」を継承）

## 起点・関連

- 関連する計画書 ID:
  **NFR-12（アクセシビリティ）**（02_requirements（計画リポ））／
  `SC-11`（構成ビューア。存在秘匿の 404 を出す面として使う）／
  `SC-19`（個人資料。確認ダイアログを開いた面として使う）／
  ADR-0031（計画リポ。フロントエンドスタック）
- 関連する実装 ADR:
  [IADR-0009](IADR-0009_wiki-browsing-404-hides-existence.md)（404 で存在秘匿）／
  [IADR-0035](IADR-0035_frontend-role-based-nav-and-existence-hiding.md)（権限外は `RequireRole` → `NotFound`）／
  [IADR-0124](IADR-0124_tanstack-router-unit-composition.md) 決定 8（catch-all はシェル配下）／
  [IADR-0134](IADR-0134_spa-route-code-splitting-boundaries.md)（SPA の分割境界。`NotFound` を初期チャンクに残す判断の当事者）／
  [IADR-0438](IADR-0438_route-level-error-boundary-pending-and-intent-preload.md)
  （**本負債を「据え置いた」当事者**。フォローアップに挙げていた）／
  [IADR-0440](IADR-0440_a11y-machine-checks-jsx-a11y-and-axe.md)
  （a11y の機械検査。**走査面 3 つ・WCAG タグのみという限界を自ら開示していた**）／
  [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)（submodule ユニットは本リポジトリから変更しない）
- 関連する実装仕様書:
  [20260912_1438_notfound-landmark-and-axe-coverage](../specs/20260912_1438_notfound-landmark-and-axe-coverage.md)
- 関連 issue: #1438（#1436 の bot レビュー 🟡 3 点目 ／ IADR-0440 §結果 ／ IADR-0438 §フォローアップ）

## コンテキストと課題

`NotFound`（存在秘匿の 404）が自前の `<main>` を持ち、共通シェル `Layout` の
`<main id="main-content">` の**中**で入れ子になっていた。IADR-0438 はこれを既存負債として
据え置き、IADR-0440 は「**どちらの検査でも落ちない**」と自ら開示していた。

見逃しの機序は 2 つあり、**どちらも「検査が無い」のではなく「検査の射程の外」**である。

1. `eslint-plugin-jsx-a11y` は **JSX の要素名しか見ない**。`Layout.tsx` の `<main>` と
   `NotFound.tsx` の `<main>` は別ファイルにあり、**入れ子になるのは実行時**である。
   静的解析でこれを見る道は無い（プラグインの設計上の限界であって、設定不足ではない）。
2. axe は実 DOM を見るが、**走査面 3 つ（共通シェル・SC-01・SC-05）はいずれも `NotFound` を通らない**。
   さらにタグが `wcag2a` / `wcag2aa` だけであり、**ランドマークの規則は 1 つも評価されていなかった**
   ——`landmark-no-duplicate-main` / `landmark-main-is-top-level` / `landmark-unique` / `region` は
   いずれも `best-practice` タグにしか属さない。

決めるべきは 3 点。(a) `NotFound` の外側を何にするか、(b) シェルの**外**で描かれる経路をどうするか、
(c) 機械検査をどこまで広げるか。

## 検討した選択肢

| 論点 | 案 | 却下理由 |
| --- | --- | --- |
| (a) 外側の要素 | **`<section aria-labelledby>`（採用）** | — |
| | 素の `<div>` | シェル内では成立するが、**器が無名の箱になり**、`region` として支援技術の見出しから辿れない。`<section>` ＋ 見出しの id で 1 行増えるだけである |
| | `<main>` のまま ＋ `Layout` 側を条件分岐 | シェルが「いま何を描いているか」を知る必要が出る。**存在秘匿の判断がシェルへ漏れる**（`Layout` が 404 を特別扱いすれば、その分岐自体が手掛かりになる） |
| (b) シェル外の経路 | **`NotFoundPage`（`<main>` の器）を足して `rootRoute` だけ差し替え（採用）** | — |
| | `rootRoute` も素の `NotFound` | **ページにランドマークが 1 つも無くなる**（`landmark-one-main` / `region` に落ちる状態を新たに作る） |
| | `NotFound` 自身が文脈を見て `<main>` を出し分ける | 出し分けの条件は「シェルの中か外か」であり、**部品が自分の祖先を知る**設計になる。テストも「どちらで描かれたか」を作り分ける必要が出る |
| (c) 検査 | **`best-practice` をタグへ追加（採用）** | — |
| | 規則 ID を個別に `withRules` で足す | 「入れ子の `main`」だけが塞がり、**同じ理由で見えていない他の規則**（`region` 等）は見えないまま残る |
| | 走査面を増やさず規則だけ足す | `NotFound` はどの面にも出ない。**規則を足しても評価される DOM が無い**（実測で確認済み） |

## 決定

### 決定 1: `NotFound` の外側を `<section aria-labelledby>` にする（文言・役割・見出しレベルは不変）

- 見出しの id は **固定値**（`not-found-heading`）。🔴 **`useId()` を使わない** ——
  React の生成 id は描画位置で変わり、「未知パスと権限秘匿で markup が完全一致する」ことを
  `outerHTML` で固定している `Layout.test.tsx`（＝存在秘匿の担保そのもの）が割れる。
- **`見つかりませんでした` / `お探しのページは存在しないか、アクセスできません。` と
  見出しレベル 1 は一字も変えない。** これらを `getByRole('heading')` で引くテストが
  本リポジトリに 4 本、knowledge に 3 本、submodule のユニットに 4 本、E2E に 9 本ある。

### 決定 2: シェルの**外**で描かれる経路にだけ `<main>` の器（`NotFoundPage`）を与える

`NotFound` は 4 箇所から描かれる。**`rootRoute.notFoundComponent` だけがシェルの外**である。

| 経路 | 位置 | 使う部品 |
| --- | --- | --- |
| `shellRoute.notFoundComponent` | シェルの内 | `NotFound` |
| `catchAllRoute.component` | シェルの内 | `NotFound` |
| `RequireRole`（権限外） | シェルの内 | `NotFound` |
| `rootRoute.notFoundComponent` | **シェルの外** | **`NotFoundPage`** |

🔴 **この非対称は存在秘匿を損なわない。** URL で到達できる未知パスは必ず**シェル配下**の
`catchAllRoute` が受ける（IADR-0124 決定 8。配線は `router.test.ts` が固定）。`rootRoute` 側が
出るのは「シェルの外で `notFound()` が投げられた場合」だけで、**利用者が URL を突いて 2 つの応答を
比較できる面ではない**。逆に揃えると、解消したはずの入れ子が戻る。
**「揃っていないから直す」で戻されないよう、理由を `shell.tsx` と `router.test.ts` の両方に書いた。**

### 決定 3: axe のタグへ `best-practice` を足し、走査面を 3 → 5 へ

- 追加面は **存在秘匿の 404**（権限外ロールで `/admin/config-viewer`＝SC-11 を開く）と
  **確認ダイアログを開いた状態**（SC-19 の削除確認）。
  どちらも**通常の画面を何面足しても届かない状態**である（畳まれた DOM・例外の DOM）。
- 引き方は**役割と表示名だけ**（`getByTestId` へ逃げない。IADR-0440 決定 4 の踏襲）。
- **spec ファイルを新設しない。** `scripts/check-route-manifest.js` が `sc<NN>-*.spec.ts` を
  画面の母集合として数えるため、横断の spec を足すと母集合が乱れる。
- `expectNoAxeViolations` に `disableRules`（**規則単位**）の口を用意したが、**使用箇所は 0 件**である。
  ファイル単位・面単位の抑制はしない（裁定 7）。使うときは構造上直せない理由を本 IADR へ追記する。

### 決定 4: 実測を先に取る（陰性対照）

**先に spec だけ変えて赤を見てから、実装を直して緑を見る。** 実測値は作業仕様書 §検証記録が持つ。

## 理由

- 決定 1・2 は「**部品は自分が置かれる文脈を知らない**」という原則に沿う。
  文脈（シェルの内か外か）を知っているのはルート定義の側なので、器を与えるのもそちらである。
- 決定 3 は、**「規則を足す」と「評価される DOM を足す」は別の作業**だという実測に基づく。
  タグだけ足しても `NotFound` はどの面にも出ないため、入れ子は緑のままだった。
- 決定 4 は、IADR-0440 §フォローアップが残した宿題（「実測は作業仕様書へ残す」）の踏襲である。
  **`best-practice` を足して既存 4 面が緑だったことも実測である** ——
  「入れたが何も見ていない」と区別できる形で記録する。

## 結果

- 良い影響:
  - ランドマークの重複が消え、支援技術から「主領域」が一意に決まる。
  - **今後この型の退行は機械が止める。** 単体（`Layout.test.tsx` の `main` は 1 つ）と
    E2E（axe の 404 面）の 2 段で落ちる。前者はビルド前に落ちるので速い。
  - 畳まれた状態・例外の状態の DOM が初めて a11y 検査の対象になった。
- 悪い影響・トレードオフ:
  - **走査面が 5 つでも「全画面を測っている」わけではない。** 17 画面のうち axe に掛かるのは
    2 画面と 3 つの状態だけである。面を増やす基準は「**通常の画面では届かない状態か**」であり、
    画面数そのものではない。
  - `best-practice` は WCAG の失敗条件ではない規則を含むため、**将来 axe の更新で新しい規則が
    増えると、こちらの都合と無関係に赤くなり得る**。そのときも抑制ではなく是正で応じる
    （直せないものだけ規則単位で外し、理由を本 IADR へ追記する）。
  - `NotFoundPage` という**シェル外専用の部品**が 1 つ増えた。使う場所は 1 箇所だけで、
    増やす理由も無い（増えたら、それはシェルの外に画面が増えたということである）。
- フォローアップ:
  - なし。IADR-0438 §フォローアップ「`NotFound` の `<main>` 入れ子の解消」と
    IADR-0440 §フォローアップ「走査面を 3 面から広げるか」は**本 IADR で閉じた**。

## 関連

- Supersedes: なし（IADR-0438 / IADR-0440 の**フォローアップを閉じる**決定であり、
  両者の決定そのものは覆していない）。
- Superseded by: なし。

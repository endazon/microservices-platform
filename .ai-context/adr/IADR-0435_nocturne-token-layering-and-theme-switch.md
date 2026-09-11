---
title: IADR-0435 Nocturne トークンの二層化とテーマ切替（system 既定・3 状態循環）
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-12, ADR-0031, SC-01, SC-02, SC-03, SC-04, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, SC-12, SC-17, SC-18, SC-19, SC-20, SC-21, IADR-0121, IADR-0125, IADR-0134, IADR-0436]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/05_screens/mockups/hi-fi/index.html
related_specs:
  - ../specs/20260912_frontend-nocturne-and-experience-gaps.md
---

# IADR-0435: Nocturne トークンの二層化とテーマ切替（system 既定・3 状態循環）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）／テーマ方針は利用者裁定（2026-09-12 裁定 1）

## 起点・関連

- 関連する計画書 ID:
  ADR-0031（計画リポ）（Accepted。UI = Tailwind v4 ＋ shadcn/ui ＋ Lucide）／
  13_frontend-stack（計画リポ）（fixed。§採用技術一覧）／
  08_data-egress-policy（計画リポ）（**外部 CDN・Web フォント・analytics の利用禁止**）／
  05_screens（計画リポ）（§共通シェル・`mockups/hi-fi/`＝**Nocturne デザインシステム**）／
  INDEX（計画リポ） 決定 21（色だけで意味を持たせない）
- 関連する実装 ADR:
  [IADR-0121](IADR-0121_spa-stack-migration-staging.md)（決定 4 = `@platform/ui` の切り出し単位）／
  [IADR-0125](IADR-0125_ui-primitives-i18n-catalog-and-storybook.md)（決定 1 = 共有 UI は**表示文言を持たない**／決定 5 = egress は成果物走査で担保）／
  [IADR-0134](IADR-0134_spa-route-code-splitting-boundaries.md)（初期チャンクの ratchet）／
  [IADR-0436](IADR-0436_shared-ui-states-panels-and-base-ui-adoption.md)（本決定のトークンを使う部品群。同じ PR）
- 関連する実装仕様書:
  [20260912_frontend-nocturne-and-experience-gaps](../specs/20260912_frontend-nocturne-and-experience-gaps.md)
- 関連 issue: 本 PR（UI/UX 改善。検討メモ 第 3 弾「UI 部品の現在地」は別途送付でリポジトリに実体を持たない）

## コンテキストと課題

計画の hi-fi モックアップ（Nocturne）は**暗い面を前提とした 1 テーマ**だけを持ち、色は
`--color-*` / `--space-*` / `--radius-*` / `--shadow-*` の CSS 変数で与えられている。
一方、実装の `@platform/ui` は Tailwind v4 の `@theme` に**明るい面の値**を焼き込んでおり、
モックとの見た目の差はトークンの値そのものにあった。

決めるべき論点は 3 つある。

1. **トークンをどう並べるか。** Tailwind v4 の素の `@theme` は**ユーティリティへ値を焼き込む**。
   `bg-surface` を生成した時点で色が確定するため、`:root` 側の変数を後から差し替えても効かない。
   ダークとライトを両立させるには、焼き込む層と差し替える層を分ける必要がある。
2. **既定のテーマと切替の作法。** モックは 1 テーマしか与えておらず、ライト値は計画に無い。
3. **既存の記法をどう扱うか。** 既存画面は任意値記法（`text-[--color-fg-muted]` の形）で
   トークンを引いていた。この記法を残すか、名前付きユーティリティへ寄せるか。

## 検討した選択肢

| # | 案 | トークンの持ち方 | 切替 | 却下理由 |
| --- | --- | --- | --- | --- |
| A | **二層（採用）** | `@theme` ＝ テーマ非依存の素材／`:root` ＝ 意味トークン／`@theme inline` で var 参照のまま写す | `prefers-color-scheme` ＋ `[data-theme]` の 4 段 | — |
| B | 単層（`@theme` に全部） | 1 層 | 不可 | **切替が原理的に効かない**（値が焼き込まれる）。ライト対応を諦めることになる |
| C | ライト既定 ＋ ダークを追加 | 二層 | 同上 | モックが**ダーク 1 本**である。既定をライトにすると、**計画が与えた唯一の値が「例外側」になる**——モックとの差分を読むたびに反転が要る |
| D | OS 追従のみ（切替 UI なし） | 二層 | `prefers-color-scheme` だけ | 裁定 1 が切替ボタンを求めた。OS 設定を変えられない環境（共用端末・キオスク）で選べない |
| E | `[data-theme]` のみ（OS を見ない） | 二層 | 属性のみ | **初回訪問が必ずダーク**になる。OS をライトに設定している利用者にとって、既定が意思表示を無視する |

## 決定

### 決定 1: トークンは 2 層に分け、第 2 層は `@theme inline` で var 参照のまま写す

- **第 1 層（`@theme`）＝ テーマ非依存の素材**: 色の ramp（`neutral` / `accent` / `accent-2` 各 9 段）・
  意味色の原色（`ok` / `warn` / `err`）・フォント・角丸（`sm` / `md` / `lg`）・影（`sm` / `md` / `lg`）・
  Nocturne の間隔目盛り。**テーマを切り替えても動かない。**
- **第 2 層（`:root` の素の CSS 変数）＝ 意味トークン**: `bg` / `surface` / `surface-muted` / `border` /
  `divider` / `fg` / `fg-muted` / `brand` / `brand-fg` / `accent` / `accent-soft` / `success` /
  `warning` / `danger`。**テーマごとに値が入れ替わる。**
- 第 2 層を `@theme inline { --color-surface: var(--color-surface); … }` で写す。`inline` は
  変数を `:root` へ出力せず、ユーティリティ側に `var(--color-surface)` をそのまま置く。
  **実測: 同名でも循環しない**（`inline` 側は出力されないため自己参照にならない）。

🔴 **`--spacing-1` という名前は使えない。** Tailwind v4 の既定は乗算器 1 本（`--spacing`）で
`p-1` = `calc(var(--spacing) * 1)` を作る。そこへ `--spacing-1` を定義すると **`p-1` だけが
2.8px に乗っ取られ、`p-2` は 8px のまま**という不揃いが生まれる（実測）。よって Nocturne の
目盛りは `n` を付けた別名（`--spacing-n1` … `--spacing-n8`。`p-n3` の形）で持ち、
Tailwind 既定の 4px 刻みはそのまま残す。

### 決定 2: 意味トークンの**名前は既存のものを維持**し、モックの語彙は別名として足す

`surface` / `fg` / `brand` 等の既存名を変えない。**改名はリポジトリ全体の置換になり、
見た目の是正と混ざって差分が読めなくなる**ためである。モックの語彙（`accent` / `divider` / `bg` /
`accent-soft`）は**足す**。`brand` と `accent` は**同値**であり、名前を 2 つ持つのは既存コードと
モックの双方から引けるようにするためである（**値をずらさない**）。

### 決定 3: 既定は system。切替は 3 状態を 1 つのボタンが循環し、localStorage に保つ

利用者裁定 1 のとおり。実装は `platform/frontend/src/lib/theme/`（純関数 ＋ DOM ＋ localStorage のみ。
React も Lingui も参照しない）と `components/theme/ThemeToggle.tsx`（文言）に分ける。

- **循環は `system` → `system` の反転 → もう一方（明示） → `system`。** 順序が OS 設定で変わるのは
  意図である —— **1 回押したときに見た目が必ず変わる**ようにするため、最初に行くのは
  「今見えているものの反対」でなければならない。順序を固定（常に light → dark）にすると、
  OS がライトの利用者が `system` から `light` を選んだときに**何も起きない**。
- **`system` のときは `data-theme` 属性を消す**（`data-theme="system"` とは書かない）。
  CSS のライト規則は `:root:not([data-theme='dark'])` で書かれており、**属性が無いことが
  「OS に従う」の表現そのもの**である。値を書くと 4 段のどれにも当たらない。
- **適用は React の描画より前**（`main.tsx` で `applyStoredTheme()`）。描画してから当てると
  最初の 1 フレームだけ反対のテーマが見える。
- **localStorage の読み書きは必ず try/catch で囲む。** プライベートブラウズ・企業ポリシーで
  例外を投げ得る。読めなければ `system` へ倒し、書けなくても操作は失敗させない。
- **色・アイコンだけに意味を持たせない**（INDEX 決定 21 の敷衍）。ボタンは
  アイコン（`Monitor` / `Sun` / `Moon`）＋ 見える文言（「表示: システム / ライト / ダーク」）で示す。

CSS の段は**この順序**で書く（3・4 は 2 と同じ詳細度 0,2,0 なので**順序で決まる**）。

```text
1. :root                                                  … ダーク（既定）
2. @media (prefers-color-scheme: light) の
   :root:not([data-theme='dark'])                         … system がライト
3. :root[data-theme='light']                              … 明示ライト
4. :root[data-theme='dark']                               … 明示ダーク
```

`:root:not([data-theme='dark'])` にするのは、**明示ダークを選んだ利用者の指定を OS 設定で覆さない**ためである。

### 決定 4: ライト値は Nocturne の ramp を反転して定義し、コントラストを実測で確かめる

モックはライト値を与えていない。**発明した値であることを記録に残す**。実測値（本文のコントラスト比）:
`brand` #5d5294 は白面 6.77:1 / 地色 #f3f5fe 6.23:1、`fg-muted` #595d6c は白面 6.55:1、
`success` 6.31:1 / `warning` 5.48:1 / `danger` 6.18:1 —— いずれも 4.5:1 以上。

**ダーク側も 1 値だけモックから逸脱した。** axe（wcag2aa）の実測で、右レールのランチャー
（`text-accent` の ghost ボタンが `bg-surface-muted` に載る）が **4.38:1** で AA を割った。要素を
避難させるのではなく組み合わせ自体を直すため、ダークの `--color-brand` / `--color-accent` を
**`#9184d9` → `#988cdb`**（HSL 明度のみ +0.02、色相・彩度は不変）へ持ち上げた。持ち上げ後の実測:
bg 5.96 / surface 5.14 / surface-muted 4.79 / accent-soft 4.84。**モックの `--color-accent` からの
意図的な逸脱**であり、計画がダーク値を再定義した場合はコントラストの実測を添えて再裁定する。

### 決定 5: 任意値記法（`text-[--color-x]` の形）は使わない。名前付きユーティリティへ寄せる

🔴 **この記法は Tailwind 4.3 では無効な CSS を出す**（実測）。v3 系の慣用として書かれていたものが
残っていたが、**生成される宣言が `var()` に包まれず、値として解釈されない**。既存の記述は
名前付きユーティリティ（`text-fg-muted` / `bg-surface` / `rounded-md`）へ是正する。

🔴 **あわせて `packages/ui/src` が Tailwind の走査対象に入っていなかった**ことが分かった。
自動ソース検出はビルドのルート（`platform/frontend`）から辿るため、共有 UI パッケージ自身は
入らない。`@source '.'` を足して是正した。**実測**（tailwindcss 4.3.3 / `pnpm run build`）:
出力 CSS 19,887 → 30,255 B。**`bg-surface` / `text-fg-muted` / `p-n3` のような本パッケージにしか
現れないクラスは、`@source` 無しでは 1 つも生成されていなかった。** 気付かれなかったのは、
旧実装が使っていたクラス文字列（`rounded-[--radius-control]` 等）が platform / knowledge 側にも
同じ綴りで存在し、そちら経由で生成されていたためである。

### 決定 6: Web フォントを読み込まない（モックの `@import` は写さない）

hi-fi モックは Inter を Google Fonts から読む。**この `@import` は写さない**（08_data-egress-policy）。
OS のシステムフォントスタックを使い、日本語は各 OS 同梱のゴシック体へフォールバックする。
アイコンは lucide-react（npm パッケージ＝バンドル同梱）で、アイコン用 Web フォントも使わない。
退行は `node scripts/check-static-egress.js --require <dist>` が成果物を走査して止める。

## 理由

- 決定 1 は**代替が無い**。`@theme` が値を焼き込む以上、切替を実現する層は別に要る。
  `inline` は Tailwind v4 が用意した正規の逃がし方であり、自前の CSS 変数運用へ降りる必要がない。
- 決定 2 は**差分の可読性**を優先した判断である。見た目の是正と識別子の改名を同じ PR に混ぜると、
  レビューが「色が変わったのか名前が変わったのか」を毎行判定することになる。
- 決定 3 の循環順は**「押しても変わらないボタンを作らない」**という単一の基準から導かれている。
- 決定 5 は**壊れていた記法の是正**であり、様式の好みではない。

## 結果

- 良い影響:
  - ライト・ダークの両方が 1 つのトークン表から出る。画面側は意味トークンだけを見ればよい。
  - 共有 UI パッケージのクラスが**初めて生成されるようになった**（決定 5 の副産物）。
  - チャンクへの影響は**ほぼゼロ**（実測: 初期ロード 555.16 → 556.01 kB。差 +0.85 kB の内訳は
    ほぼ全額がテーマ切替の文言カタログ。CSS は +10.37 kB だが**本検査の母数ではない**）。
- 悪い影響・トレードオフ:
  - **ライト値は計画に無い**（決定 4）。計画がライト値を定めた場合は上書きされる。
  - 4 段の CSS 規則は**順序に依存する**。段の並べ替えは静かに壊れる（テストは値の実体ではなく
    純関数の巡回を固定しているだけである）。
  - **任意値記法の是正は完了していない**。HEAD 時点の残存は `lib/scope-filter/ScopeFilter.tsx`
    7 行・`lib/auth/LoginPage.tsx` 2 行の計 2 ファイル 9 行である（走査と除外理由は作業仕様書
    §母集合）。どちらも本 PR の担当タスクの専有領域外にあり、落ち穂として残った。
- フォローアップ:
  - 残存 2 ファイルの是正（上記）。
  - `@platform/ui` へプリミティブを足すときは、**第 2 層の意味トークンだけを引く**こと
    （第 1 層の ramp を直に引いてよいのは、モックが ramp を名指ししている面に限る）。

## 関連

- Supersedes: なし。
- Superseded by: なし。

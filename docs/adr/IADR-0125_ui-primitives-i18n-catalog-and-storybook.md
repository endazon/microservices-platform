---
title: IADR-0125 共有 UI プリミティブの移植範囲・Lingui カタログの検査方式・Storybook の egress 遮断
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0031, SC-01, SC-02, SC-03, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-13, IADR-0033, IADR-0034, IADR-0116, IADR-0118, IADR-0119, IADR-0120, IADR-0121, IADR-0124]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - ../specs/20260804_issue-496_ui-i18n-storybook.md
  - ../specs/20260804_issue-490_spa-router-shell.md
---

# IADR-0125: 共有 UI プリミティブの移植範囲・Lingui カタログの検査方式・Storybook の egress 遮断

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  UI = Tailwind v4 + shadcn/ui + Lucide、i18n = Lingui（ja / en）、カタログ = Storybook）／
  [13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)（fixed。
  §採用技術一覧が正。i18n 欄「コンパイル時抽出」・Linter 欄「Storybook / Lingui のプラグインを併用」・
  §ディレクトリ構成 `locales/ ja / en（Lingui）`）／
  [08_data-egress-policy](../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md)
  （**外部 CDN・Web フォント・analytics 禁止／既定テレメトリのオプトアウト**）／
  [01_screens](../../planning/projects/microservices-platform/05_screens/01_screens.md)（§画面詳細・`mockups/hi-fi/`）／
  [INDEX](../../planning/projects/microservices-platform/INDEX.md) 決定 21（色だけで意味を持たせない）
- 関連する実装 ADR:
  [IADR-0121](IADR-0121_spa-stack-migration-staging.md)（**決定 4 = `@platform/ui` の切り出し単位。
  本決定 1 がその「以後 Input / Dialog / Table 等を第 2 段で追加する」を実値で埋める**）／
  [IADR-0124](IADR-0124_tanstack-router-unit-composition.md)（決定 7 = a11y を型で強制する作法）／
  [IADR-0119](IADR-0119_fr17-21-hold-until-adr-fixed.md)（**FR-17〜21 の着手保留**。本決定 2 の根拠）／
  [IADR-0034](IADR-0034_frontend-coverage-gate.md) / [IADR-0118](IADR-0118_backend-coverage-floor.md)（床の ratchet）／
  [IADR-0116](IADR-0116_reimplementation-branching-and-pr-policy.md)（規約 4）／
  [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)（AST は別プロジェクト）
- 関連する実装仕様書:
  [20260804_issue-496](../specs/20260804_issue-496_ui-i18n-storybook.md)（本決定と対で読む）
- 関連 issue: #496（起点・親 #454）／#490（第 2 段の前半 = PR #495）／#452（画面実装）

## コンテキストと課題

移行第 2 段の残り 3 件（shadcn/ui 本移植・Lingui・Storybook）を入れるにあたり、
計画が値を与えていない論点が 3 つある。

1. **どこまで移植するか。** ADR-0031 は「shadcn/ui を採用する」としか書かない。shadcn/ui は
   50 以上のコンポーネントを持つ「コピーして所有する」方式であり、**全部入れる／画面が要求する分だけ
   入れる／画面が来てから入れる**のどれを採るかは実装の判断である。
   一方、部品の利用者（#452 の画面実装）は**まだ存在しない**。
   > **［2026-08-04 追記］計画が「shadcn/ui 派生の範囲」を確定した**
   > （[13_frontend-stack §shadcn/ui 派生の範囲](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)。
   > planning `d980a01` / planning#182）。Radix を使ってよい 4 基準（フォーカストラップ／複合キーボード操作／
   > ポータルの配置計算／`aria-*` の動的同期）と**部品ごとの判定表**が置かれ、
   > 「本家が Radix を使っていても追随しないこと自体は**逸脱として記録しなくてよい**」と明記された。
   > **本決定 1 で移植した 8 部品は判定表と完全に一致する**（下記 決定 1 の追記）。
   > すなわち本論点は「計画が値を与えていない」状態ではなくなった（本文は起案時の記録として残置する）。
2. **「未翻訳キーを CI で検出できる」をどう実現するか。** Lingui は `extract` / `compile` /
   `--strict` / `failOnMissing` など複数の口を持ち、どれを組み合わせるかで**検出できる漏れの型が変わる**。
3. **Storybook の外部通信をどう塞ぎ、それをどう担保するか。** Storybook は既定でテレメトリを送る。
   08_data-egress-policy はこれを禁じるが、「設定した」だけでは退行を検出できない。

## 検討した選択肢

### 論点 A: プリミティブの移植範囲

| | A1. shadcn/ui を一括で全部入れる | **A2. 計画（画面設計・モックアップ）が要求する分だけ（採用）** | A3. 画面（#452）が要求した時点で都度入れる |
| --- | --- | --- | --- |
| CLAUDE.md「計画外の機能追加・過剰な抽象化を避ける」 | **反する**（使わない部品が大量に入る） | 適合 | 適合 |
| #452 の着手時に部品が揃っているか | 揃う | **概ね揃う**（要求根拠のあるものは揃う） | **揃わない**（issue #496 の存在意義が消える） |
| 判断の根拠が説明できるか | 「shadcn/ui だから」 | **3 情報源の突き合わせで説明できる** | 都度 |
| Knip（第 5 段）の未使用検出 | 大量に出る | 出る（利用者は #452） | 出ない |
| 保守コスト | 高い（所有するコードが増える） | 中 | 低 |

A1 を採らないのは、shadcn/ui が npm 依存ではなく**コードをリポジトリで所有する**方式だからである。
入れた分だけ保守・テスト・カタログの対象が増える。A3 を採らないのは、issue #496 のスコープ
（「#452 の画面実装が要求する部品を移植する」）を空にしてしまうためである。

### 論点 B: 未翻訳キーの検出方式

| | B1. `lingui extract` の差分検査のみ | B2. `lingui compile --strict` のみ | **B3. 差分検査 ＋ 自前の空文字検査 ＋ `--strict`（採用）** |
| --- | --- | --- | --- |
| **キーの取りこぼし**（ソースに足してカタログ未更新） | **検出する** | 検出しない（カタログに無い＝欠落として見えない） | 検出する |
| **訳文の空白**（キーはあるが `msgstr` が空） | **検出しない**（`extract` は空エントリを正常に生成する） | 条件付きで検出（`sourceLocale` の扱いに依存） | **検出する（全ロケール一律）** |
| `fuzzy` / `obsolete` の残置 | 検出しない | 検出しない | **検出する** |
| 検査が何を保証しているかの自明さ | 中 | 低（Lingui の内部規則に依存） | **高**（「全ロケールの `msgstr` が非空」） |
| 既存の作法との整合 | orval と同型 | 別型 | **orval と同型 ＋ 追加の不変条件** |

**B1 単独では受け入れ基準を満たさない。** `lingui extract` は新しいメッセージを
`msgstr ""` の空エントリとしてカタログへ追加するのが正常動作であり、
**「抽出したが訳していない」状態は差分検査を通過する**（実測は §実測）。
「未翻訳キーを CI で検出する」ためには、空の `msgstr` そのものを違反と見なす検査が要る。

B2 単独を採らないのは、`--strict` が守ってくれる範囲が Lingui の版と `sourceLocale` の扱いに依存し、
**保証内容がこちらから読み取りにくい**ためである。検査器は「何を保証しているか」が
1 行で言えるものにする（`lib/ci-annotate.js` を含む既存検査器の作法）。

### 論点 C: Lingui のビルド経路

| | C1. `@lingui/vite-plugin` ＋ `.po` の直接 import | **C2. `lingui compile` の生成物を素の TS として import（採用）** | C3. マクロを使わず実行時 API（`i18n._()`）だけ |
| --- | --- | --- | --- |
| 追加ツールチェーン | **rolldown / @rolldown/plugin-babel（peer）** | 不要（`@vitejs/plugin-react` の babel に 1 行） | 不要 |
| 計画「コンパイル時抽出」 | 適合 | 適合 | **反する**（抽出できない） |
| Vitest との整合 | プラグインを 2 か所へ | **同じ babel 設定が両方に効く** | — |
| 生成物のコミット | 不要 | 要（orval と同じ扱い） | — |

### 論点 D: Storybook の egress 担保

| | D1. 設定（`disableTelemetry`）だけ | D2. 設定 ＋ レビュー時の目視 | **D3. 設定 ＋ ビルド成果物の機械走査（採用）** |
| --- | --- | --- | --- |
| 初回の遮断 | できる | できる | できる |
| **退行の検出**（addon 追加・依存更新で外部参照が復活） | できない | 人依存 | **できる** |
| SPA 本体（`dist/`）への適用 | — | — | **同じ検査が効く**（08_data-egress-policy の統制対象そのもの） |
| 誤検出のリスク | — | — | ある（対策は決定 5） |

## 決定

### 決定 1: 移植するプリミティブは「3 情報源の突き合わせで要求が示せるもの」に限る（論点 A = A2）

移植するのは **Input / Textarea / Select / Label / Table 一式 / Card / Alert / Tabs** の 8 件である。
選定表（計画の明示・モックアップの語彙・既存実装の DOM 要素数）は
[作業仕様書 §1](../specs/20260804_issue-496_ui-i18n-storybook.md#1-移植するプリミティブの選定推測で盛らない)を正とする。

- **`@platform/ui` の公開面は `src/index.ts` の 1 ファイルのまま**とする（IADR-0121 決定 4）。
- **プリミティブは文言を持たない。** 表示文字列はすべて呼び出し側から渡す。
  部品が既定文言を内蔵すると i18n の入口が 2 つに割れ、カタログの網羅検査（決定 4）が
  「カタログにも無いしソースにも無い」文言を見逃す。
- `Alert` は `StatusBadge` と同じく**アイコン ＋ テキストラベルを型で強制**する（INDEX 決定 21・IADR-0124 決定 7）。

> **［2026-08-05 追記・実値の更新（#502）］移植済みのプリミティブは 8 件から `Tag` を加えた 9 件になった。**
> 上の「8 件である」および「選定表を正とする」は**起案時点（#496）の値**であり、現行値ではない。
> **現行値は [`src/packages/ui/README.md`](../../src/packages/ui/README.md) と
> [`src/packages/ui/src/index.ts`](../../src/packages/ui/src/index.ts) を正とする。**
>
> `Tag`（分類の名前を表すチップ）を足した根拠は、本決定と**同じ 3 情報源の突き合わせ**である——
> (a) 計画の明示: 05_screens §SC-02 主要素「結果テーブル（文書／**タグ**／更新日時）」・§SC-03 主要素
> 「属性・**タグ**パネル」・§SC-01「区別の表示方法」のラベル、(b) hi-fi モックの語彙: `tag`（全画面 120 箇所。
> `tag-accent` / `tag-neutral` / `tag-outline` の 3 種）、(c) 既存実装: 旧 3 画面が素の `<span>` で自作していた。
> #496 の選定表は `tag` を既存の `StatusBadge` で足りると見なしていたが、**画面へ適用して初めて差が現れた**
> （同 PR 自身が「プリミティブの本番の呼び出し元は 0 件」と記録している）。`StatusBadge` は tone ごとに
> 固定アイコンを描く**状態**の部品であり、分類の名前に `Info` アイコンが付くと意味が変わる。
> Radix の要否は計画 13_frontend-stack §shadcn/ui 派生の範囲 の 4 基準で判定し、**全て非該当**のため
> ネイティブ HTML ＋ `cva` ＋ `cn()` とした（判定の記録は
> [#502 作業仕様書 §3](../specs/20260804_issue-502_sc01-03-search-flow.md) と
> [SC-01 画面仕様書 §UI 部品](../screens/SC-01_search-chat.md)）。
> **本決定の骨格（3 情報源の突き合わせ・公開面 1 ファイル・文言を持たない）は変えていない。**
- **`Select` は Radix ではなく素の `<select>` を採る。** shadcn/ui の Select は
  `@radix-ui/react-select` によるポータル描画のカスタムリストボックスだが、計画が要求しているのは
  「定義済み区分のみ」「権限内のタグ／フォルダのみ」といった**値の選択**であり、
  ネイティブの `<select>` で満たせる。ネイティブはモバイル・スクリーンリーダ・キーボードの
  既定挙動をそのまま得られ、テストの足場も要らない。`Tabs` は逆に、ロービングタブインデックスと
  `aria-*` の整合を自前で書くと誤りやすいため `@radix-ui/react-tabs`（shadcn/ui の実装そのもの）を使う。
- **`packages/ui` は React 以外の実行時依存を増やさない**方針を保つ（現状 cva / clsx / tailwind-merge /
  lucide-react ＋ 本決定の `@radix-ui/react-tabs` のみ）。

> **［2026-08-04 追記］計画が同内容を確定し、本決定は計画の判定表と一致する。**
> [13_frontend-stack §shadcn/ui 派生の範囲](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)
> （planning `d980a01` / planning#182。利用者裁定・質問票 第 1 回 Q12）は、Radix を使ってよい条件を
> **4 基準**（1 フォーカストラップ／2 ロービングタブインデックス等の**複合キーボード操作**／
> 3 ポータル・ポップアップの**配置計算**／4 `aria-*` の**動的な同期を要する開閉状態**）に限り、
> 該当しない部品はネイティブ HTML ＋ `cva` ＋ `cn()` で実装する、と定めた。実装との対応:
>
> | 部品 | 計画の判定 | 本 PR の実装 | 一致 |
> | --- | --- | --- | --- |
> | `Tabs` | 基準 2 に該当 → Radix | `@radix-ui/react-tabs` | ○ |
> | `Select` | 要求は値の選択のみ → ネイティブ | ネイティブ `<select>` | ○ |
> | `Label` | 該当なし → 素の `<label>` | 素の `<label>` | ○ |
> | `Input` / `Textarea` / `Table` / `Card` / `Alert` | 該当なし → `cva` ＋ `cn()` | 同左 | ○ |
>
> **帰結**: 計画は「本家が Radix を使っていても追随しないこと自体は**逸脱として記録しなくてよい**」と
> 明記した。したがって作業仕様書 §計画書との差異 の `Select` / `Label` の行は
> **「差異」ではなくなり、「計画の判定表と一致」へ書き換えた**。
> `Tabs` を Radix にした一次根拠も、モックアップの語彙（`seg` / `seg-opt`）に加えて
> **計画の基準 2（複合キーボード操作）への該当**が加わった。

### 決定 2: Dialog は移植しない（FR-19 / FR-20 の着手保留に従う）

計画で「確認ダイアログ」を要求しているのは `01_screens` の **SC-19 / SC-20 のみ**である。
両画面は FR-19 / FR-20 に属し、[IADR-0119](IADR-0119_fr17-21-hold-until-adr-fixed.md) 決定 1 が
「保留の対象は当該 FR を実現するプロダクトコードと、**その受け入れを担う画面**」と定めている。
保留中の画面のためだけに部品を先回りで作らない。

**これは繰り延べであって放棄ではない。** 引き受け先は **#452**（FR-19 / FR-20 の保留解除後、
当該画面と同時に足す）であり、`packages/ui/README.md` と作業仕様書 §親への申し送り に明記する。

### 決定 3: Lingui は babel マクロ ＋ コンパイル済みカタログの import で通す（論点 C = C2）

- 変換は `@lingui/babel-plugin-lingui-macro` を `@vitejs/plugin-react` の `babel.plugins` へ足す。
  足す先は **`src/platform/frontend/vite.config.ts`（ビルド）と `src/vitest.config.ts`（テスト）の 2 か所**である。
  **片方だけに入れると、テストは通るのにビルドが壊れる（あるいはその逆）という静かな破綻**になるため、
  両方に同じ変換が効いていることをテストで固定する。
- カタログは `src/platform/frontend/src/foundation/i18n/locales/<locale>/messages.{po,ts}`。
  `.po` が人の編集する単一情報源、`.ts` は `lingui compile` の生成物で**コミットする**
  （orval 生成物と同じ扱い。IADR-0121 決定 3）。
- `sourceLocale` は `ja`。**`ja` の `msgstr` も空にせず埋める**——空を許すと決定 4 の検査が
  en だけの片肺になる。

### 決定 4: 未翻訳キーの検出は 3 段で行い、中核は「全ロケールの `msgstr` 非空」とする（論点 B = B3）

| # | 検査 | 保証する内容 |
| --- | --- | --- |
| 1 | `pnpm run i18n`（extract ＋ compile）＋ `git diff --exit-code` | **カタログがソースを網羅している**（キーの取りこぼしが無い） |
| 2 | `node scripts/check-i18n-catalogs.js` | **全ロケールの全エントリの `msgstr` が非空で、`fuzzy` / `obsolete` が残っていない** |
| 3 | `lingui compile --strict` | Lingui 自身の欠落検出（2 の裏取り） |

検査 2 を自前スクリプトにするのは、**保証内容が 1 行で言えること**を優先したためである
（論点 B の表）。作法は既存の検査器に倣う——外部依存ゼロ・`--self-test`・`lib/ci-annotate.js`・fail-closed。

### 決定 5: 外部 egress の担保は「ビルド成果物の走査」で行い、Storybook と SPA の両方に効かせる（論点 D = D3）

`scripts/check-static-egress.js` を新設し、静的ビルド成果物に**外部オリジンから取得する参照**が
無いことを検査する。

- **検出するのは「取りに行く参照」に限る**: HTML の `src` / `href`、CSS の `@import` / `url()`、
  および既知の禁止ホスト（`fonts.googleapis.com` / `fonts.gstatic.com` / `cdn.jsdelivr.net` /
  `unpkg.com` / analytics 系）。後者だけは JS 文字列の中でも違反とする。
- **検出しないのは「取りに行かない URL 文字列」**: XML 名前空間（`http://www.w3.org/2000/svg`）・
  JSON Schema の `$schema`・ライブラリのエラーメッセージ中のドキュメント URL。
  **除外は用途ではなくパターンで書く**——「これは大丈夫」と個別に許すと、許可リストが
  実質的な無効化装置になる。
- 対象は Storybook の静的ビルドと **SPA の `dist/`** の両方とする。08_data-egress-policy が
  統制対象に挙げているのは「SPA フロントエンド」そのものであり、カタログだけを見ても片手落ちである。
- Storybook 側では併せて **`core.disableTelemetry: true`** を設定する（同ポリシー
  §非LLM外部送信の統制 の「既定テレメトリをオプトアウトする」）。
- **検出できないものを明記する（この検査は網羅ではない）**: 本検査が見るのは
  「HTML のリソースタグ」「CSS の `@import` / `url()`」「既知の禁止ホスト表」の 3 つだけである。
  したがって**禁止ホスト表に載っていないホストへの `fetch()` / `XMLHttpRequest` / `WebSocket` /
  動的 `import()` は検出しない**（実行時に組み立てられる URL も同様である）。
  この穴を塞いでいるのは本検査ではなく、**ESLint の `no-restricted-globals`**
  （`foundation/api` 以外での `fetch` / `XMLHttpRequest` / `EventSource` の禁止。IADR-0121 決定 8）と
  **BFF 境界の生成器制限**（IADR-0121 決定 3）である。本決定は「アセットの取得経路」を、
  IADR-0121 決定 3・8 は「API 呼び出しの経路」を担当し、両者で 08_data-egress-policy を覆う。
  禁止ホスト表は網羅表ではなく**代表例の表**であり、新しい SaaS を使い始めたら追記が要る。

### 決定 6: i18n の適用範囲は platform の foundation に限り、既存 11 画面は #452 へ繰り延べる

`#452` が SC-01〜11 の Page を作り直す（[#490 仕様書 §親への申し送り](../specs/20260804_issue-490_spa-router-shell.md)）。
いま画面へ `<Trans>` を入れると**同じ画面を 2 回書く**ことになり、
[IADR-0121](IADR-0121_spa-stack-migration-staging.md) 決定 1 が第 2 段の分割で守っている原則に反する。

**繰り延べであって放棄ではない。** 本 issue が入れるのは「文言を国際化する**仕組み**と、
それが動いていることを示す**実例**（foundation の文言）と、**退行を止める検査**」であり、
語彙の移送は #452 が画面を書く際に同時に行う。#454 のチェックリストへ明記する。

### 決定 7: ロケール切替の UI は作らない

`01_screens` で言語切替を要求しているのは **SC-13（Keycloak のログインテーマ）だけ**であり、
§共通シェル の要素に言語切替は無い。無い UI を先回りで作らない（CLAUDE.md 禁止事項）。
実行時は `navigator.language` から判定し、切替そのものは `activate(locale)` の公開 API で行う。
UI が必要になったら #452 が共通シェルへ足す。

### 決定 8: ブランド表示名（「汎用プラットフォーム」）はロケールで差し替えず、翻訳カタログの対象にしない

**起案時（暫定判断）**: `01_screens` §共通シェル は「ブランド表示名は『汎用プラットフォーム』で統一する」
「…**ブランド名は差し替えない**」と定めるが、当時は**言語による差し替えの可否**に触れていなかった。
実装が独断で英語名（例: "General Platform"）を作ると計画の「統一」に反する既成事実が生まれるため、
安全側に倒して en カタログにも同じ文字列を入れ、計画へ問う扱いとした。

> **［2026-08-04 追記・暫定 → 確定］計画が同じ結論を確定した。**
> [01_screens §共通シェル](../../planning/projects/microservices-platform/05_screens/01_screens.md)
> （planning `d980a01` / planning#184。利用者裁定・質問票 第 1 回 Q13）:
> 「**ロケールによっても差し替えない**（固有名詞として扱う。en ロケールでも「汎用プラットフォーム」を表示し、
> **翻訳カタログの対象としない**）」。理由も計画に書かれている——識別の手段をブランド名に持たせない設計と
> 同じ向きであり、SC-13〜16（Keycloak テーマ・SPA とは別実装）と訳語を二重管理せずに済む。
> したがって本決定は**暫定判断ではなく計画の確定事項の実装**である。
> なお計画は「**正式なブランド名を定める場合は本裁定を再検討する**」とも述べている。

**実装方式（計画の字義に従う）**:

- **カタログを経由しない**。`foundation/ui/Layout.tsx` と `foundation/auth/LoginPage.tsx` で
  **リテラルとして描画**する。計画が「翻訳カタログの対象としない」と明記したためである。
- 起案時の方式（カタログに同じ文字列を入れる）は**採らない**。表示結果は同じだが、
  **en の `msgstr` を書き換えるだけで差し替えられてしまう**——`check-i18n-catalogs.js` は
  `msgstr` が非空かどうかしか見ないため、その差し替えを止められない。
  「カタログに載っていない」ことが唯一の実効的な担保である。
- リテラルは `eslint-plugin-lingui` の `no-unlocalized-strings` が error にするため、
  **`eslint-disable-next-line` に計画の該当箇所を根拠として明記**する。
  規則が例外の明文化を強制する形になり、根拠なしの未国際化リテラルとは区別できる。
  ディレクティブが load-bearing であること（外すと error が出ること）は実測した（§実測）。
- 副作用として、E2E のブランド名アサーションは**ロケールに依存しない**。
  そのため「表示言語がロケールで決まる」ことの検査は、訳される別の文言
  （「社内ナレッジ検索・AI 回答プラットフォーム」）で行う（§実測 の変異試験）。

この判断を決定として立てるのは、**決定 7（ロケール切替 UI を持たない）とは別の論点**だからである。
決定 7 は「切替の手段」の話であり、ブランド名を訳すかどうかには触れていない。
番号だけを借りて参照すると、参照先に書かれていない主張の根拠になってしまう。

### 決定 9: 本計画に属さないユニットの左ナビグループは「ユニットの機能名」とし、機能名は合成点が与える

[01_screens §共通シェル ［2026-08-04 確定］](../../planning/projects/microservices-platform/05_screens/01_screens.md)
（planning `d980a01` / planning#185。利用者裁定・質問票 第 1 回 Q15）が次を定めた。

> 本計画に属さない可変機能ユニットの画面は、実装側でグループを設けて分類してよい（計画の 4 グループは
> 変更しない）。**ただしグループ名は「ユニットの機能名」とする**（例: `ai-stock-trading` →
> 「**株式自動売買**」）。並び順は計画の 4 グループの後とする。**総称としての「その他」は使わない**

[IADR-0124 §計画書との差異](IADR-0124_tanstack-router-unit-composition.md) で #490 が置いた
5 番目のグループ「その他」は、この確定により**計画違反**になった。実装を次のとおり改める。

1. **総称のフォールバックを廃止する。** `nav.ts` から `OTHER` を削除し、
   グループ未宣言の項目が落ちる先を無くす。
2. **機能名は合成点（`platform/frontend/src/features/index.ts`）が与える。**
   ユニット自身（AST）は本リポジトリから変更できず（IADR-0120）、
   `foundation` に置くと共通シェルが可変ユニットを知ることになる（IADR-0124 決定 1 に反する）。
   合成点は**ユニットを知る唯一の場所**であり、ここが自然な置き場である。
   `UnitNavGroup { id, label, items }` を `features/index.ts` の `unitNavGroups` として公開し、
   `router.tsx` が `registerUnitNavGroups()` で登録する。
3. **宣言漏れを型で塞ぐ。** 総称フォールバックが無くなった以上、グループ未宣言の項目は
   「どのグループにも属さず**静かに消える**」ことを意味する。計画に属するユニットのナビ項目は
   `PlanNavItem`（`group` 必須）で受け、`tsc` に落とさせる。
4. **並び順**は計画の 4 グループの後（実装は元からこの順であり変更していない）。
5. **機能名の en 訳は置く**（下記）。

**機能名の英訳を置く判断（計画は定めていない）**: 計画は**ブランド表示名**については
「ロケールによっても差し替えない（固有名詞として扱う）」と裁定した（決定 8）が、
**ユニットの機能名については定めていない**。両者は別物であるため、決定 8 の裁定を拡大解釈しない。
**訳す**（`株式自動売買` → `Automated stock trading`）を採る。理由は次の 3 点である。

- 計画がブランド名を訳さない根拠として挙げたのは「**固有名詞として扱う**」ことと
  「SC-13〜16 と訳語を二重管理せずに済む」ことである。ユニットの機能名は固有名詞ではなく
  **画面を探すための記述的なラベル**であり、Keycloak テーマ側に対応物も無い。**根拠がどちらも当たらない。**
- 計画自身が、このグループ名の役割を「**利用者が機能を探す唯一の手掛かり**」と述べている
  （総称を禁じた理由）。英語利用者に対して手掛かりとして働くには訳語が要る。
- 左ナビの計画 4 グループ（利用者／個人／管理／運用）は訳している。同じ一覧の中で
  1 グループだけ日本語が残ると、**言語が混ざった見出し列**になり、かえって読み取りを妨げる。

訳語（`Automated stock trading`）は AST の計画（`projects/ai-stock-trading/`）にも英語名の定めが
無いため実装の判断である。**計画が機能名の訳語方針を定めた場合はそれに従う**
（作業仕様書 §未決事項 に挙げる）。

## 理由

- **決定 1 が守っているもの**は「所有するコードの量」である。shadcn/ui はライブラリではなく
  コードの配布であり、入れた瞬間からテスト・カタログ・a11y・トークン追随の対象になる。
  利用者（#452 の画面）がまだ無い状態で 50 個を所有するのは、CLAUDE.md が名指しで禁じる
  「過剰な抽象化」に当たる。逆に 0 個では issue のスコープが空になる。
  **3 情報源の突き合わせ**は、この 2 つの失敗のあいだに引ける唯一の客観的な線である。
- **決定 2 は「作らない判断にも根拠が要る」**という一点に尽きる。Dialog は issue 本文の例示に
  含まれていたが、計画で要求しているのは保留中の FR に属する画面だけであった。
  例示を根拠に作ると、保留の意味が消える。
- **決定 4 の核心は「B1 だけでは通ってしまう」という実測**である（§実測）。
  差分検査は「カタログを更新したか」しか見ておらず、「訳したか」は見ていない。
  受け入れ基準の文言（**未翻訳**キーを検出できる）に照らすと、この違いが致命的である。
- **決定 5 が「設定」で終わらない理由**は、egress 統制が**退行しやすい**種類の統制だからである。
  addon を 1 つ足すだけで外部フォントが復活し得るが、画面は正常に見える。
  08_data-egress-policy が禁じているのは通信であって設定ではないため、**通信の材料（成果物）を見る**。

## 結果

- 良い影響:
  - `@platform/ui` が「#452 が着手した時点で必要な素部品が揃っている」状態になり、
    かつ**なぜその 8 件なのかが説明できる**。
  - 文言の国際化が「規約」ではなく**検査**になった（未翻訳・未抽出のどちらも CI で落ちる）。
  - 08_data-egress-policy の SPA 統制が、Storybook と SPA 本体の**両方**で機械検査になった。
- 悪い影響・トレードオフ:
  - `.po` と `.ts` の 2 系統をコミットするため、文言を 1 つ足すたびに生成物の差分が PR に現れる
    （orval と同じトレードオフ。代わりに CI と IDE が codegen の実行順に依存しない）。
  - Storybook は依存が重く、`packages/ui` の devDependencies が大きく増える。
  - **プリミティブの利用者が本 PR 時点では 0 件**である（#452 が使う）。したがって被覆率は
    「専用テストと stories による被覆」であり、実利用による被覆ではない。#490 の `notify` と同型の
    留保であり、床の読み方としてこの点を差し引く必要がある。
  - `@radix-ui/react-tabs` を `packages/ui` の実行時依存に加える（決定 1）。
- フォローアップ:
  - **Dialog の移植**（決定 2）: FR-19 / FR-20 の保留解除後、#452 が当該画面と同時に行う。
  - **既存 11 画面の i18n 化**（決定 6）: #452。
  - **ロケール切替 UI**（決定 7）: 計画が要求した時点で #452。
  - **［消化済み・2026-08-04］検査の CI 結線**: `check-i18n-catalogs.js` と `check-static-egress.js`（＋
    `pnpm run i18n` の再生成差分・Storybook ビルド）を `frontend.yml` の `build-test` ジョブへ結線した。
    `.github/workflows/` は GitHub App 権限で編集できないため、人間（親）がローカル権限でコミットしている。
    あわせて `paths` へ `src/lingui.config.ts` を追加した（走らない経路を残さない）。

## 実測

**測定条件**: worktree `feat/ADR-0031-ui-i18n-storybook`（`origin/develop` `4147899` 基点）／
Node 22.22.2 ／ pnpm 10.33.0 ／ Vitest 3.2.7（v8 provider）／ TypeScript 5.9.3 ／
Lingui 6.6.0（`@lingui/cli` / `@lingui/format-po` / `@lingui/babel-plugin-lingui-macro`）／
Storybook 10.5.6（`@storybook/react-vite`）／ Vite 6.4.3 ／
**submodule `src/ai-stock-trading` と `planning` は populate 済み**。

### 未翻訳キーの検出（決定 4 の根拠）

3 つのシナリオを**実際に作って**各検査を走らせた。`✗` = 検出（exit 1）、`—` = 素通り（exit 0）。

| シナリオ | 検査 1<br>`pnpm run i18n` ＋ `git diff` | 検査 2<br>`check-i18n-catalogs.js` | 検査 3<br>`lingui compile --strict` |
| --- | --- | --- | --- |
| **A**: ソースへ `msg` を足したが**カタログを更新していない** | **✗ 検出** | — | — |
| **B**: カタログは更新済みだが **`en` の `msgstr` が空** | — | **✗ 検出** | **✗ 検出** |
| **C**: カタログは更新済みだが **`ja`（sourceLocale）の `msgstr` が空** | — | **✗ 検出** | — |

**3 者はいずれも他を置き換えない。**

- **A で検査 2・3 が素通りするのはなぜか**: メッセージがカタログに**そもそも存在しない**ため、
  「空の訳文」も「欠落」も観測できない。差分検査だけがソースとカタログの対応を見ている。
- **B で検査 1 が素通りするのはなぜか**: `lingui extract` が未訳を `msgstr ""` の空エントリとして
  生成するのは**正常動作**であり、再実行しても差分が出ない（実測: `git diff --quiet` が exit 0）。
  **「未翻訳キーを CI で検出する」に差分検査だけでは届かない**という、本決定の中心的な根拠である。
- **C で検査 3 が素通りするのはなぜか**: `--strict` は `sourceLocale` を検査しない。
  `lingui extract` の統計表でも ja の Missing は `-` と表示される（実測）。

### `lingui extract` の非決定性（決定 3 の付随的な発見）

`@lingui/format-po@6.6.0` は **実行時刻を `POT-Creation-Date` ヘッダへ毎回書き込む**。
連続 2 回の `lingui extract --clean` で `.po` の md5 が変化することを実測した
（`d90fb72…` → `118b3c7…`）。これがあると検査 1（再生成差分）が**常に赤**になる。
当該ヘッダを無効化するオプションは存在しない（`PoFormatterOptions` の型定義で確認）ため、
`src/lingui.config.ts` で `serialize` をラップして**当該行を落とす**。
固定の日時を書かない（嘘の値を残さない）——抽出日時は git のコミット日時が正確に答える。
ラップ後は連続実行で md5 が一致することを実測した。

### `<Trans>` と `msg` の選択（決定 3 の実装上の帰結）

`@lingui/react/macro` の `<Trans>` は `I18nProvider` を**必須**とする（無いと実行時に例外）。
foundation のコンポーネント（Layout・NotFound・LoginPage・RequireAuth ほか）は
**多数の単体テストが素で描画する**ため、`<Trans>` を使うと 3 ファイル 31 件のテストが
「本質でない wrapper の有無」で落ちた（実測）。foundation が出すのは素の文字列だけであり、
リッチテキスト（要素の埋め込み）や複数形を使わないため、**`i18n._(msg`…`)` に統一**した
（プロバイダに依存しない）。`I18nProvider` は `App.tsx` に置いたままにする——
画面側（#452）が `<Trans>` を使えるようにするためである。

### 表示言語とテストの決定性（決定 7 の帰結）

ロケールをブラウザ設定から決めると、**テスト環境の既定ロケールが描画言語を決めてしまう**。

| 層 | 既定 | 対処 |
| --- | --- | --- |
| Vitest（jsdom） | `navigator.language` = `en-US` | `src/test/setup.ts` で `activate('ja')` |
| Playwright | `en-US` | `playwright.config.ts` の `use.locale = 'ja-JP'` |

Playwright 側は**実際に落ちて見つかった**（ブランド表示名を en へ訳していた時点で、
日本語の見出しを待つスモークが失敗した）。固定を入れたうえで、
**固定が load-bearing であること**を変異試験で確かめた——`locale: 'ja-JP'` を外すと
`login.smoke.spec.ts` が失敗し、戻すと通る（実測）。

### Storybook の外部 egress（決定 5 の根拠）

`storybook build` の成果物（**20 ファイル**）を走査した実測。

| 種別 | 実測 |
| --- | --- |
| `<link>` / `<script>` / `<img>` などのリソースタグの外部参照 | **0 件** |
| CSS の `@import` / `url()` の外部参照 | **0 件**（`.css` は 1 ファイル・外部 url なし） |
| 既知の禁止ホスト（フォント CDN・汎用 CDN・analytics・エラー報告 SaaS） | **0 件** |
| 外部オリジンの `<a href>`（＝**違反ではない**） | 4 件。すべて `https://storybook.js.org/docs/…` の説明リンク |
| Web フォント | **自己ホスト**（`nunito-sans-*.woff2` を成果物に同梱） |

SPA 本体（`platform/frontend/dist`・**4 ファイル**）も同様に 0 件である。

**検査が効いていることの変異試験**: `.storybook/preview-head.html` に
`<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter">` を仕込んで再ビルドすると、
`check-static-egress.js` が **2 件**（リソースタグとしての検出 ＋ 既知の禁止ホストとしての検出）を
報告して exit 1 になった。除去して再ビルドすると exit 0 に戻る。

### ブランド名のリテラル化（決定 8）と総称グループの廃止（決定 9）の実測

| 検査 | 実測 |
| --- | --- |
| カタログに「汎用プラットフォーム」の msgid が残っていないこと | `ja/en` の `messages.po` と `messages.ts` の 4 ファイルすべてで **0 件** |
| カタログに「その他」が残っていないこと | 同 4 ファイルで **0 件**（`lingui extract --clean` が obsolete ごと除去） |
| `eslint-disable-next-line lingui/no-unlocalized-strings` が load-bearing か | ディレクティブを外すと `Layout.tsx` で **error 1 件**（`String not marked for translation`）。戻すと 0 件。**未使用ディレクティブの警告も出ない**＝実際に効いている |
| 「その他」へ戻すと落ちるテスト | `Layout.test.tsx` の **2 件**（機能名の見出しと配下リンク／4 グループの後という並び順）。戻すと 15/15 green |
| ナビ到達性検査の網羅 | 総称の廃止で `navItems()` が計画グループのみを返すようになり、**AST 3 画面の到達性検査が一度静かに外れた**（36 → 33 ケース）。`router.test.ts` を「計画グループ ＋ ユニットグループ」を対象にする形へ改め、`navGroups()` の描画結果と突き合わせる検査を足して塞いだ |

### カバレッジ（stories を母数から外すことの影響）

| 集計 | lines/statements | branches | functions |
| --- | --- | --- | --- |
| 全ユニット横断（除外あり） | **93.86%** | **84.11%** | **86.58%**（厳密 86.5889%） |
| MSP 所有分（除外あり） | **92.04%** | **82.93%** | **86.08%** |
| MSP 所有分（**除外なし**） | 87.96% | 82.95% | 86.13% |

同じ導出規則（MSP 所有分の実測から 5pt 下・切り捨て）から出る床は、
除外ありで **87 / 77 / 81**、除外なしで **82 / 77 / 81** である。
**`**/*.stories.*` の除外は lines の床を 5pt 動かす**（`foundation/testing/**` を足した #490 とは違い、
「動かしていない」とは言えない）。差は stories 1 ファイル（145 行・テストから実行されない）に由来する。
除外を採るのは、**カタログの行数が被覆率を左右する状態そのものが誤り**だからである
（stories を消すと床が上がるという、成果物の品質と無関係な動き方をする）。
なお**除外なしの実測でも移行前の床 86 は満たしている**（87.96% > 86）ため、
この除外は「床を割るのを避けるための除外」ではない。

## 関連

- Supersedes: なし。ただし [IADR-0121](IADR-0121_spa-stack-migration-staging.md) 決定 4 の
  「以後 Input / Dialog / Table 等を第 2 段で追加する」という**予告部分を実値で埋める**（部分改定）。
  被改定側へ同日付の追記を入れる。IADR-0121 の骨格（入れる／入れないの判定規則・公開面 1 ファイル・
  依存規則の改定）は有効なため `Accepted` を維持する。
- Superseded by: なし。ただし**部分改定が 1 件ある**（骨格は有効なため `Accepted` を維持し、
  該当決定の直後へ日付付き［追記］を入れた）。
  1. [IADR-0126](IADR-0126_sse-answer-state-and-search-url-state.md) を伴う #502 の作業:
     §決定 1 の実値「移植は 8 件」を **9 件（`Tag` を追加）** へ更新し、現行値の所在を
     `src/packages/ui/README.md` / `src/index.ts` へ移した。選定の 3 情報源という**規則は不変**である

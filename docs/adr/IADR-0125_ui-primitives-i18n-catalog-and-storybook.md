---
title: IADR-0125 共有 UI プリミティブの移植範囲・Lingui カタログの検査方式・Storybook の egress 遮断
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0031, SC-02, SC-05, SC-06, SC-07, SC-08, SC-09, IADR-0034, IADR-0116, IADR-0118, IADR-0119, IADR-0120, IADR-0121, IADR-0124]
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
- **`Select` は Radix ではなく素の `<select>` を採る。** shadcn/ui の Select は
  `@radix-ui/react-select` によるポータル描画のカスタムリストボックスだが、計画が要求しているのは
  「定義済み区分のみ」「権限内のタグ／フォルダのみ」といった**値の選択**であり、
  ネイティブの `<select>` で満たせる。ネイティブはモバイル・スクリーンリーダ・キーボードの
  既定挙動をそのまま得られ、テストの足場も要らない。`Tabs` は逆に、ロービングタブインデックスと
  `aria-*` の整合を自前で書くと誤りやすいため `@radix-ui/react-tabs`（shadcn/ui の実装そのもの）を使う。
- **`packages/ui` は React 以外の実行時依存を増やさない**方針を保つ（現状 cva / clsx / tailwind-merge /
  lucide-react ＋ 本決定の `@radix-ui/react-tabs` のみ）。

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
  - **`check-static-egress.js` の CI 結線**: `.github/workflows/` は GitHub App 権限では編集できない。
    必要な差分案は作業仕様書 §ワークフロー変更の要否 に置き、親（ローカル権限）が適用する。

## 実測

（実装後に記入する）

## 関連

- Supersedes: なし。ただし [IADR-0121](IADR-0121_spa-stack-migration-staging.md) 決定 4 の
  「以後 Input / Dialog / Table 等を第 2 段で追加する」という**予告部分を実値で埋める**（部分改定）。
  被改定側へ同日付の追記を入れる。IADR-0121 の骨格（入れる／入れないの判定規則・公開面 1 ファイル・
  依存規則の改定）は有効なため `Accepted` を維持する。
- Superseded by: なし

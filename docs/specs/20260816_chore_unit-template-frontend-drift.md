---
title: 作業仕様書 — unit-template のフロントエンドを現行スタックへ追随させる（旧契約・依存宣言・設定一式の欠落）
type: spec
status: done
related_ids:
  - FR-14
  - NFR
  - ADR-0031
  - IADR-0056
  - IADR-0060
  - IADR-0117
  - IADR-0121
  - IADR-0124
  - IADR-0125
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs: []
---

# 仕様書: unit-template フロントエンドの追随（旧契約・依存宣言・設定一式）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-14`（構成変更容易性 — 追加可変機能ユニットの submodule 組み込み。雛形は本要求の
  実現手段である）
- ユースケース（UC）: なし
- 画面（SC）: なし（雛形は画面を持たない）
- 非機能: **無採番 `NFR`**。計画の非機能要件表に当たる番号が無く、`.claude/rules/traceability.repo.md`
  「無採番 `NFR`」の**場合 2（メタ作業 = 規約・検査器・文書統制）**に当たる。**計画へ環流しない。**
- 関連 ADR: 計画 `ADR-0031`（フロントエンドスタック）。実装 ADR は
  [[IADR-0056]]（ユニット構成）/ [[IADR-0060]]（submodule 運用・雛形）/ [[IADR-0117]]（Shared 2→3）/
  [[IADR-0121]]（SPA 移行の段分割・pnpm・`@platform/ui`）/ [[IADR-0124]]（TanStack Router のユニット合成）/
  [[IADR-0125]]（UI プリミティブ・i18n・ナビグループ）
- 関連 issue: 起票なし（本作業で起票する）。先行は #230（雛形の新設）/ #245（通し検証）/ #256（MSB4092）
  — **いずれも closed**。drift を作った側は #490（TanStack Router 移行）/ #502〜#504（画面再実装）/
  #591（pnpm 移行）
- 計画書リンク: `planning/projects/microservices-platform/06_technical/13_frontend-stack.md`
  （`status: fixed` / `updated: 2026-08-04`。**submodule は未 populate のため、利用者から全文の提供を
  受けて照合した**。以下の引用はその原文による）

## 目的・背景

`templates/unit-template/` は追加可変機能ユニットの雛形であり、[IADR-0060](../adr/IADR-0060_submodule-unit-operations.md)
がこれを submodule 組み込みの出発点と定めている。**この雛形を複製して作られるユニットは、雛形が持つ
誤りをそのまま継承する。**

バックエンド側は `ci.yml:291-294` が **意図的に `templates/` を走査対象へ含めている**（同箇所の注釈:
「雛形の直書きは全新規サービスへ伝播し、`templates/` は上の build-and-test のビルド対象外のため他で
捕まらない」）。実際 `check-backend-libraries.js` / `check-cpm-versions.js` / `check-doc-links.js` は
いずれも通る。

**フロントエンド側には同等の手当てが無い。** `templates/` は `src/` の外にあるため pnpm workspace・
ESLint・Prettier・Vitest のいずれの射程にも入らず、以下 4 つの改定を取りこぼした。

| 改定 | 雛形の状態 |
| --- | --- |
| #490 / [[IADR-0124]]: 旧契約 `FeatureModule` → 型付きルート factory | **旧契約のまま** |
| #591 / [[IADR-0121]] 決定 2: npm workspaces → pnpm（依存の宣言が必須になった） | **依存宣言ゼロ** |
| [[IADR-0121]] 決定 4: フロントの参照許可先 1 → 2（`@platform/ui` 追加） | **1 のまま** |
| [[IADR-0125]] 決定 6・9: ナビ文言の `MessageDescriptor` 化・ナビグループ必須化 | **未反映** |

本作業はこの追随と、**同型の再発を止める CI 手当て**を行う。

> **「同型の事故が 2 回起きたら検査器」（CLAUDE.md）の条件充足**: 1 回目 = #490 のルータ移行、
> 2 回目 = #591 の pnpm 移行。どちらもフロントの雛形だけが追随せず、機械検査が無いため検出されなかった。
> よって検査器（CI 手当て）の追加を記録に留めず本作業に含める。

## 対象範囲

### 対象

1. `templates/unit-template/frontend/` の全面刷新（契約・依存・設定・サンプル）
2. `templates/unit-template/README.md` の是正（依存規則・構成図・組み込みチェックリスト）
3. `docs/how-to/adding-a-unit-submodule.md` §4 の追随
4. `templates/` のフロントを typecheck / lint / format の射程へ入れる CI 手当て

### 対象外

- **バックエンド雛形**（`templates/unit-template/backend/`）。3 つの検査器を通り、記述（xUnit v2 の据え置き
  理由・Wolverine・[[IADR-0117]] の 2→3・[[IADR-0064]] の MSB4092 回避）はいずれも現行と整合する。
  唯一 README 構成図に `Shared/<Unit>.Contracts/` が無い（本文 50-51 行には記載あり）が、本作業の
  射程（フロント追随）と別軸のため触らない。
- **移行第 4・5 段の未導入ライブラリ**（Zustand / TanStack Table / ECharts / RHF + Zod / dayjs / Knip /
  Plop / Commitlint / lint-staged）。§実測 2 のとおり**リポジトリ全体で未導入**であり、雛形の欠陥では
  なく移行の未消化である。雛形へ先に入れると、実装のどこにも無いライブラリを新ユニットへ配ることになる。
  ただし**第 4・5 段の完了時に雛形へ追随させる担保**を §設計 4 の検査器で作る。
- **`src/knowledge/frontend` の 13 画面を計画のディレクトリ構成へ是正すること**、および
  **platform `foundation/` と計画 `app/` の対応の是正**。実測 4 で**不適合が確定した**が、規模が本作業と
  2 桁違うため別 issue へ切り出す（§未決事項 1）。本作業では**起票のみ**行う。
- **未導入ライブラリ 8 群の導入**（実測 5）。移行第 4・5 段（#493 ほか）の範囲であり、雛形の欠陥ではない。
  ECharts の SC-08 / SC-10 への適用漏れも同様に別 issue とする。

## 実測

### 実測 1: 雛形と実装の差（フロントエンド）

雛形 `frontend/` の中身は **3 ファイル**（`package.json` / `src/features/index.ts` /
`src/features/sample/index.ts`）。`src/knowledge/frontend` との差は次のとおり。

| 項目 | knowledge | 雛形 |
| --- | --- | --- |
| 公開契約 | `createKnowledgeRoutes(shell)` タプル ＋ `knowledgeNavItems: readonly PlanNavItem[]` | `features: FeatureModule[]`（**`@deprecated`**） |
| `tsconfig.json` | あり（`paths` で `@foundation/*` `@features/*` `@<unit>/*`） | **無し** |
| `dependencies` | 8 個 | **無し** |
| `devDependencies` | 3 個 | **無し** |
| JSX の拡張子 | `index.tsx` | `index.ts`（コメントは `element: <SamplePage />` を勧める） |
| ナビ文言 | `msg` マクロ（`MessageDescriptor`） | 生文字列 `'Sample'` |
| ナビグループ | `group` 必須（`PlanNavItem`） | **欠落** |
| テストの例 | 多数 | **ゼロ** |

`typecheck: tsc --noEmit` は `tsconfig.json` が無いため**複製した時点で失敗する**（一度も実行されて
いない）。

### 実測 2: 「裁定済みだが未導入」のライブラリ（全 `package.json` 走査）

```
zustand / @tanstack/react-table / echarts / react-hook-form / zod / dayjs /
knip / plop / @commitlint/* / lint-staged  → どの package.json にも無い
husky → src/package.json のみ（#768 / IADR-0203）
```

`feedback/20260804_frontend-migration-staging-interpretation.md:116` が第 4・5 段の未消化として列挙した
ものと一致する。**雛形の欠陥ではない**（→ 対象外）。なお ESLint は既に Zustand 前提で動いている
（`eslint.config.js:16`。Redux を error）——**裁定と検査器はあり実体だけ無い**状態である。

### 実測 3: 母集合（誤りの側の文字列で全文書を走査）

`.claude/rules/traceability.repo.md` 規則 9 に従い、**正しい表記ではなく誤りの側**で引いた。

軸 1 `RouteObject`（`--exclude-dir=node_modules`、拡張子で絞らない）: 12 件。
軸 2 `features as `: 4 件。軸 3 `FeatureModule\[\]`: 5 件。

| 所在 | 判定 |
| --- | --- |
| `templates/unit-template/frontend/src/features/index.ts:7` / `sample/index.ts:3` | **是正対象** |
| `docs/how-to/adding-a-unit-submodule.md:93-94` | **是正対象**（しかも `[...knowledgeFeatures, ...]` は knowledge が `features` を export しなくなったため現状動かない） |
| `src/platform/frontend/src/foundation/routing/featureRegistry.ts:98-99` | 対象外（旧契約の**経緯説明**。正しい記述） |
| `src/platform/frontend/src/features/index.ts:10,32` | 対象外（AST は旧契約のまま。[[IADR-0120]] / [[IADR-0124]] 決定 2） |
| `docs/adr/IADR-0070_ast-frontend-integration.md:59` | 対象外（AST 固有。正しい） |
| `docs/adr/IADR-0121:120` / `IADR-0124:40,69,133` / `docs/adr/README.md:177,180` | 対象外（決定の**論拠**としての言及。正しい） |
| `docs/specs/20260804_issue-446_*:99` / `20260804_issue-490_*:71,77,241,421` | 対象外（**確定済み仕様書は書き換えない**。`.claude/rules/traceability.repo.md`） |

**除外したものと理由は上表のとおりで、黙って落としたものは無い**（規則 6）。

## 設計

### 設計 1: 雛形フロントを新契約へ書き換える

[[IADR-0124]] 決定 1 と `src/README.md` §項 4 に従い、ユニットが公開する契約を 2 本にする。

- `createSampleUnitRoutes = (shell: ShellRoute) => [...] as const` — **戻り値へ型注釈を書かない**
  （`readonly AnyRoute[]` を付けるとルート ID とパスの union が失われる。[[IADR-0124]] §実測）。
  この禁止を雛形のコメントで明示する。
- `sampleUnitNavItems: readonly PlanNavItem[]` — `group` を型で強制する。総称フォールバックが
  廃止済み（05_screens §共通シェル ［2026-08-04 確定］）のため、宣言漏れは画面がナビから静かに
  消えることを意味する。
- ナビ文言は `msg` マクロの `MessageDescriptor`（[[IADR-0125]] 決定 6）。
- JSX を含むため `index.tsx` にする。
- **旧契約は雛形から完全に消す。** `FeatureModule` は AST（[[IADR-0120]]）のための互換ブリッジであり、
  `src/README.md` §項 4 が「新規ユニットでは使わない」と明記している。

### 設計 1b: 雛形の Feature を計画のディレクトリ構成にする

計画 §ディレクトリ構成 の `features/ # Feature 単位（api/ components/ hooks/ routes/ stores/ types/）`
に従う。**雛形は新規であり旧実装を持たないため、実装側（knowledge）の現状ではなく計画に合わせる**
——ここで knowledge に合わせると、これから作られる全ユニットが不適合を継承する。

```text
frontend/
  package.json
  tsconfig.json
  src/
    features/
      index.ts              ← ユニットの束ね（createSampleUnitRoutes ＋ sampleUnitNavItems）
      sample/
        index.ts            ← feature の公開面（routes factory と nav 項目だけを出す）
        api/                ← orval 生成フックの再輸出・feature 固有のクエリ
        components/         ← feature 固有コンポーネント
        hooks/
        routes/             ← ルート定義（createSampleRoute）
        types/
                            ← stores/ は Zustand 導入まで置かない（§未決事項 2）
```

**雛形と実装が食い違う状態が一時的に生じる**が、これは意図的である。是正の向きは
「計画 ← 雛形 ← 新ユニット」であり、knowledge の是正（別 issue）が済めば収束する。
この意図を雛形 README に明記し、複製者が knowledge を真似て戻さないようにする。

### 設計 2: `tsconfig.json` と依存宣言を足す

`knowledge/frontend/tsconfig.json` と同型（`paths` は `@foundation/*` `@features/*` `@<unit>/*`）。
`dependencies` / `devDependencies` は knowledge と同じ集合に揃える。**pnpm は npm と違いユニットごとの
宣言を厳密に守る**（`src/package.json` の `//overrides` 注釈）ため、宣言なしでは `@foundation` の解決に
失敗する。バージョンは `src/package.json` の `overrides`（react 19 系・vite 6・vitest 3）と矛盾しない
range にする。

### 設計 3: 文書の追随

- 雛形 README: 依存規則を **1 → 2**（`@foundation` ＋ `@platform/ui`。[[IADR-0121]] 決定 4 が
  `src/README.md` 例外 2 を部分改定済み）、構成図へ `tsconfig.json` を追記、組み込みチェックリスト項 4 を
  **import 1 行 ＋ スプレッド 2 か所 ＋ vite alias ＋ tsconfig paths** へ是正。
- `docs/how-to/adding-a-unit-submodule.md` §4: 旧契約スニペットを新契約へ差し替える。

### 設計 4: CI 手当て（再発防止）

`templates/unit-template/frontend` を機械検査の射程へ入れる。**バックエンド側 `ci.yml:291-294` と
同じ理由・同じ作法**を採る。実現方法は 2 案あり、§未決事項 2 で確定する。

- 案 A: `templates/unit-template/frontend` を pnpm workspace のメンバへ加える（`pnpm-workspace.yaml`）。
  横断 typecheck / lint / format / vitest がそのまま効く。**懸念**: workspace ルートは `src/` であり
  `templates/` は 1 階層上のため、`'../templates/*/frontend'` が引けるか要検証。また雛形の
  `@<scope>/...` というプレースホルダ名が workspace 名として不正になる。
- 案 B: `frontend.yml` へ雛形専用のジョブを足し、`tsc --noEmit` と `eslint` を雛形ディレクトリで走らせる。
  **懸念**: 依存解決のために別途 install が要る。

**どちらでも「雛形が現行スタックで型検査を通ること」を CI が保証する**——これが満たされれば、
次の改定でも drift は PR 段階で赤くなる。

## 受け入れ基準

- [x] 雛形の `frontend/` に `FeatureModule` / `RouteObject` / 旧契約の記述が 1 件も残っていない
- [x] 雛形の `frontend/` を複製して `pnpm typecheck` が通る（`tsconfig.json` と依存宣言が揃っている）
- [x] 雛形のナビ項目が `PlanNavItem`（`group` 必須）で、文言が `MessageDescriptor` である
- [x] 雛形 README の依存規則が `@foundation` ＋ `@platform/ui` の 2 になっている
- [x] 雛形 README の組み込みチェックリスト項 4 が スプレッド 2 か所 と tsconfig paths に触れている
- [x] `docs/how-to/adding-a-unit-submodule.md` §4 のスニペットが新契約である
- [x] CI が雛形フロントの型検査を実行し、旧契約へ戻すと**赤くなる**ことを実際に確認した証跡がある（§検証）
- [x] `node scripts/check-doc-links.js` / `check-cross-repo-refs.js` / `check-plan-id-qualification.js` が通る
- [x] `pnpm run lint` / `format:check` / `typecheck` / `test` が通る（§検証の但し書きを参照）

## 検証（実行コマンドと出力。宣言だけの監査は不合格）

環境: Node 22.22.2 / pnpm 10.33.0 / ESLint 9.39.5 / TypeScript 5.9.3。
`src/ai-stock-trading` は **populate 済み**（public のため `git submodule update --init` で取得できる）。
`planning` は未 populate（private でスコープ外。`check-doc-links.js` が該当分を skip する）。

> **［作業中に踏んだ事故］最初は `src/ai-stock-trading` が未 populate のまま `pnpm install` を実行した。**
> pnpm は**存在しない workspace メンバの依存を lockfile から削る**ため、AST の 23 依存が消えた
> lockfile をコミットしてしまい、CI（`pnpm install --frozen-lockfile`）が
> `ERR_PNPM_OUTDATED_LOCKFILE … not up to date with <ROOT>/ai-stock-trading/frontend/package.json` で
> 即座に落ちた（PR #777 の初回。frontend 系 5 ジョブが巻き添え）。
> **ローカルは 5 プロジェクト、CI は 6 プロジェクト**という差が原因である。
> 是正は「submodule を取得 → lockfile を develop 版へ戻す → `pnpm install` をやり直す」。
> 結果、lockfile の差分は**追加 46 行のみ・削除ゼロ**になった。
> **教訓: workspace メンバを増減させる変更では、submodule を取得してから lockfile を生成する。**

### 検証 1: 雛形が workspace メンバとして型検査される

```
$ pnpm install            → Scope: all 5 workspace projects
$ pnpm -r run typecheck   → ../templates/unit-template/frontend typecheck: Done
```

### 検証 2: 旧契約へ戻すと赤くなる（本作業の要）

`features/index.ts` を #490 以前の形（`export const features: FeatureModule[] = [sampleFeature]`）へ
差し戻して実行:

```
$ pnpm --filter @sample-unit/frontend run typecheck
src/features/index.ts(2,10): error TS2305: Module '"./sample"' has no exported member 'sampleFeature'.
Exit status 2
```

復元後は `Done`。**検査器が実際に効いている**ことの証跡である。

### 検証 3: 不採用ライブラリは型検査でも止まる

依存を明示宣言したため、pnpm の isolated な `node_modules` では未宣言のパッケージが解決できない:

```
error TS2307: Cannot find module 'react-router-dom' or its corresponding type declarations.
error TS2307: Cannot find module 'axios' or its corresponding type declarations.
```

### 検証 4: 雛形への lint / format が違反を捕まえる

| 仕込んだ違反 | 結果 |
| --- | --- |
| `import ... from 'react-router-dom'` | error（`react-router は不採用（ADR-0031）`） |
| `import ... from '@platform/ui/src/components/Button'` | error（`@platform/ui の内部実装を直接参照しない`） |
| `import { apiFetch } from '@foundation/api/apiClient'` | error（`画面（features）から apiFetch を呼ばない`） |
| `import { bffFetch } from '@foundation/api/orvalMutator'` | error（同上） |
| `import ... from '@features/index'` | error（`platform の合成点（@features）へ依存しない`） |
| 整形崩し（`const d   =    {a:1,   b:2}`） | `format:templates` が warn ＋ 非 0 終了 |
| **対照**: `import { create } from 'zustand-x'`（禁止対象外） | 禁止 import の error は**出ない**（未使用変数のみ）＝過剰検出なし |

**`apiFetch` / `bffFetch` / `@features` は最初の実装で漏れていた**（`BANNED_IMPORT_PATTERNS` と
`NO_LEGACY_ROUTER_PATHS` しか import しておらず、knowledge ブロックが課している規則一式と
食い違っていた）。上表の 1 回目の実行で検出し、`NO_APIFETCH_IN_FEATURES` /
`NO_BFFFETCH_IN_FEATURES` / `@features` を足して再実測した。

### 検証 5: 既存ゲートへの影響

submodule を取得し lockfile を作り直したうえで、**全ゲートが緑**である。

```
$ pnpm install --frozen-lockfile → Lockfile is up to date   EXIT=0（CI と同条件）
$ pnpm run typecheck     → platform / knowledge / templates すべて Done   EXIT=0
$ pnpm run test          → Test Files 72 passed (72) / Tests 926 passed (926)
$ pnpm run lint          → ✖ 9 problems (0 errors, 9 warnings)   EXIT=0（warning は既存の react-refresh）
$ pnpm run format:check  → All matched files use Prettier code style!   EXIT=0
$ pnpm run lint:templates    → EXIT=0
$ pnpm run format:templates  → EXIT=0
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js  → ✓ 610 tests passed
$ node scripts/check-doc-links.js               → OK: 636 件
$ node scripts/check-cross-repo-refs.js         → OK: 1634 件
$ node scripts/check-plan-id-qualification.js   → OK: 1338 件
$ node scripts/check-doc-type-vocabulary.js     → OK: 610 件
$ node scripts/check-backend-libraries.js       → OK（新規混入 0 件）
$ node scripts/check-cpm-versions.js            → OK（直書き 0 件）
```

**`check-reading-budget.js` は warn（Claude Code 集合が予算の 98%）だが、これは既存の水準であり
本変更は `CLAUDE.md` と `.claude/rules/*.md` を 1 バイトも触っていない**（母集合に変化なし）。

## テスト方針

検査器が実際に効くことは、**変異試験**（旧契約への差し戻し・禁止 import の注入・整形崩し）の
実行ログを PR に残して示す（宣言だけの監査は不合格。CLAUDE.md §実装作業の進め方）。

加えて雛形に**サンプルテスト**（`sample/components/SamplePage.test.tsx`・4 件）を置く。新ユニットが
「テストをどこに置き何を import するか」を雛形から読み取れるようにするためで、実装と同居する規約
（`*.{test,spec}.{ts,tsx}`）と `renderUnitRoute` ハーネスの使い方を体現させる。

**このテストは実際に走らせる**（`src/vitest.config.ts` の `test.include` へ雛形を追加）。
**走らないテストを雛形に置かない**——雛形は複製されるので、腐ったテストの型を全新規ユニットへ
配ることになる。カバレッジの母数（`coverage.include`）には入れないので**ラチェットの水準は動かない**。

> **［実測でつまずいた 2 点］**
> 1. 雛形は Vite のルート（`src/`）の外にあるため、`test.include` に足すだけでは
>    「ファイルが在るのに `Cannot find module`」になる。`server.fs.allow: ['..']` が要る。
> 2. ナビ項目の遷移先がルート木に解決することを、**ルート配列からパスを読み出す形では書けない**。
>    `route.path` は木を組んだ後に解決されるため `undefined` で**試験が空振りし**、
>    `route.options.path` は実行時には取れるが**型に無く `tsc` が落ちる**。
>    **実際に各 `to` で描画して到達を確かめる形**に改めた（型にも内部表現にも依存しない）。

## 実測 4: 計画 §ディレクトリ構成 との照合（原文入手後に確定）

計画の当該節（`status: fixed`）は次のとおり。見出しは「ディレクトリ構成（**ユニット内 SPA**）」である。

```text
src/
├── app/          # providers / router / i18n / config
├── assets/       # 自己ホストのフォント・画像
├── components/   # 共通コンポーネント
├── features/     # Feature 単位（api/ components/ hooks/ routes/ stores/ types/）
├── hooks/ lib/ stores/ testing/ types/ utils/
├── locales/      # ja / en（Lingui）
└── main.tsx
```

§基本方針 は「設計: **Bulletproof React**（Feature First Architecture）」、追補（2026-07-30 裁定）は
「**計画書は絶対的な正である。実装を計画へ合わせる**」と定める。

| 対象 | 計画 | 実態 | 判定 |
| --- | --- | --- | --- |
| 雛形 `frontend/src/` | 上記構成 | `features/index.ts` ＋ `features/sample/index.ts` の 2 ファイル | **不適合** |
| knowledge の feature 内 | `api/ components/ hooks/ routes/ stores/ types/` | 下位分割ゼロ（`sc09-admin-abac/` は 10 ファイルが 1 階層） | **不適合** |
| platform の `src/` | `app/ components/ hooks/ lib/ stores/ testing/ types/ utils/ locales/ assets/` | `foundation/`（api / auth / config / i18n / routing / testing / ui）へ集約 | **要判断**（下記） |

- **`features/` の内部分割は解釈の余地が無い**（計画が括弧書きで 6 つ列挙している）。
- **platform の `foundation/` は解釈の余地がある。** ADR-0019 / [[IADR-0056]] のユニット分割
  （アプリホストと features の分離）との整合という実装側の解釈が `src/lingui.config.ts:34` と
  [[IADR-0033]] 決定 3（「Bulletproof React の Feature First と両立する」）に残る。ただし**これは実装側の
  主張であって計画の文言ではない**。
- 走査の裏取り: `api/` `components/` `hooks/` `routes/` `stores/` `types/` `utils/` `app/` の該当は
  `platform/frontend/src/foundation/api` **ただ 1 つ**。

## 実測 5: §採用技術一覧（全 26 行）との照合

**採用と裁定されたがリポジトリのどこにも無いもの**（全 `package.json` 走査）:

| ライブラリ | 計画の備考 |
| --- | --- |
| Zustand | クライアント状態（`eslint.config.js:16` は既に Zustand 前提で Redux を error にしている） |
| React Hook Form / Zod / @hookform/resolvers / RHF DevTools | フォーム |
| TanStack Table | テーブル |
| Apache ECharts | 「**SC-08 / SC-10 のダッシュボードで使用**」 |
| dayjs | 日付 |
| react-error-boundary | 実装は `foundation/ui/ErrorBoundary.tsx` の自前 |
| Knip / Plop.js | **Plop は「Feature 雛形生成」——本作業と直結する** |
| lint-staged / Commitlint | Git Hooks（Husky / Renovate は #768 で導入済み） |

**ECharts は「未着手」ではなく「作り直したのに入っていない」。** 計画が名指しした SC-08 / SC-10 は
#503 / #504 で再実装済みだが、`AnalysisDashboardPage.tsx` / `OperationsDashboardPage.tsx` に
チャート描画（ECharts・SVG とも）が 1 件も無い。

**不採用と裁定されたのに在るもの**: `oidc-client-ts`（★不採用）が platform / knowledge の双方に。
これは [[IADR-0121]] 決定 6 で第 3 段（#439）まで据え置きと**既に記録済み**であり、想定内である。

**揃っているもの**: React 19 / TS / Vite / Tailwind v4 / shadcn/ui 派生（`@platform/ui`）/ lucide-react /
cva ＋ clsx ＋ tailwind-merge / sonner / TanStack Query / TanStack Router / Lingui / orval /
Vitest ＋ RTL / Playwright / MSW / Storybook / ESLint ＋ Prettier / Husky / Renovate / pnpm。

## 計画書との差異

- 差異: **あり（重大・本作業の射程を超える）。**
  1. **Feature の内部構成**が計画（`api/ components/ hooks/ routes/ stores/ types/`）に従っていない。
     **雛形だけでなく knowledge の 13 画面も同様**である。
  2. **採用技術一覧の 8 群が未導入**（実測 5）。うち ECharts は再実装済み画面に入っていない。
  3. platform の `foundation/` と計画の `app/` ほかの対応関係が、実装側の解釈としてのみ存在し
     ADR に明示されていない。
- 対応: 1 のうち**雛形の分**と、3 の**明文化**は本作業で行う。**knowledge 13 画面の是正（1）と
  ライブラリ導入（2）は本作業の対象外**とし、別 issue へ切り出す（§未決事項 1）。
  計画側への環流は不要——**計画に誤りは無く、実装が追いついていないだけ**である。

## 未決事項

1. **【要・裁定】実装側（knowledge 13 画面 ＋ platform）の是正をどう扱うか。** 計画は fixed で
   「実装を計画へ合わせる」と裁定済みのため、**やるかどうか**ではなく**いつ・どの単位で**の問題である。
   規模が本作業と 2 桁違うため別 issue とし、[[IADR-0116]] 規約 4（1 PR が大きくなる場合は issue を分割）
   に従って画面群ごとに割る想定。**本作業では起票のみ行い実装しない。**
2. ~~**雛形の `stores/` をどうするか。**~~ **決着（利用者指示・2026-08-16）**: **計画の全区分をフォルダと
   `.gitkeep` で置く。** 当初は `src/README.md`「存在しない区分のフォルダは作らない」に従って
   見送ったが、利用者から「フォルダだけは作っておく」との指示があった。
   - 同 README の当該条文は **§サービスユニットの標準レイアウト（backend 内）に属する記述**であり、
     しかも計画 `12_backend-application-stack` §規範性・粒度・置き場（2026-08-04 確定）が
     **バックエンドについても「実体が無いものは空フォルダ ＋ `.gitkeep`」へ改めている**。
     理由は「**意図的に不在なのか単に作り忘れなのかが一見して分からない**」ことであり、
     フロントにも同じ理由が当てはまる。よって指示は計画の作法と整合する。
   - 置いた区分: `app/ assets/ components/ hooks/ lib/ stores/ testing/ types/ utils/ locales/`
     （トップレベル 10）と `features/sample/stores/`（Feature 単位の残り 1）。
3. ~~**設計 4 の案 A / 案 B の選択。**~~ **決着**: 案 A（pnpm workspace メンバ化）を採用し、`'../templates/*/frontend'`
   が解決することを実測した。ただし**案 A だけでは lint と format が届かない**ことが判明した（下記）。
   - **ESLint**: flat config は「設定ファイルのあるディレクトリ」を実行の基準とし、**その外を検査しない**
     （実測のエラー: `… located outside of the base path`）。`src/` から実行する `eslint .` は
     `templates/` へ到達できず、逆にルートから実行すると基準がルートになって `eslint.config.js` の
     ユニット別 `files` が一致しなくなる。**1 つの設定で両立できない**ため、雛形専用の入口
     `src/eslint.templates.config.js` を置き、**禁止リストは `eslint.config.js` から import**して
     二重管理を避けた。
   - **Prettier**: 各ファイルの位置から上へ設定を探すため `src/.prettierrc.json` を見つけられない。
     **実測では既定（ダブルクォート）で整形され、`--check` はそれと自己矛盾しないので通ってしまった**
     ——「整形ゲートが在るのに効かない」最悪の形だったので、`--config` の明示で塞いだ。
4. **本作業の issue 起票。** [[IADR-0116]] 規約 1（1 issue = 1 PR）に従い起票する。既存の open issue に
   本件を扱うものは無い（#230 / #245 / #256 はいずれも closed）。

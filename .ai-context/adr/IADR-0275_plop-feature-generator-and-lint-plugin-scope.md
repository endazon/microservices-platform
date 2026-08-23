---
title: IADR-0275 Plop による feature 雛形生成と、新規 ESLint プラグイン 3 種の適用範囲
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0031
  - IADR-0120
  - IADR-0121
  - IADR-0124
  - IADR-0203
  - IADR-0211
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs: []
---

# IADR-0275: Plop による feature 雛形生成と、新規 ESLint プラグイン 3 種の適用範囲

- 状態: Accepted
- 日付: 2026-08-23
- 決定者: 実装エージェント（issue #493）

## 起点・関連

- 関連する計画書 ID: 計画 `ADR-0031`（`06_technical/13_frontend-stack` §採用技術一覧 の
  **Generator = Plop.js** 行と **Linter = ESLint（TanStack / Testing Library / Storybook / Lingui の
  プラグインを併用）** 行）/ NFR（運用保守。無採番。`.claude/rules/traceability.repo.md` の場合 2）
- 関連する実装仕様書: [`20260823_issue-493_spa-stage5-tooling.md`](../specs/20260823_issue-493_spa-stage5-tooling.md)
- 関連 IADR: [IADR-0121](./IADR-0121_spa-stack-migration-staging.md) 決定 1（第 5 段 = 運用系ツーリング）/
  [IADR-0124](./IADR-0124_tanstack-router-unit-composition.md) 決定 1（ルートのタプルと型安全）/
  [IADR-0203](./IADR-0203_renovate-husky-hook-scope.md)（第 5 段の切り出し 1。決定 3 が lint-staged / Commitlint を「入れない」と定める）/
  [IADR-0211](./IADR-0211_knip-scope-and-unused-ratchet.md)（Knip のスコープとラチェット）/
  [IADR-0120](./IADR-0120_excluded-units-from-gitmodules.md)（AST submodule の境界）
- 関連 issue: #493（本決定）/ #446（親）/ #768（切り出し 1）/ planning#468（Git Hooks 行の裁定依頼。**本 issue を塞いでいる**）

## コンテキストと課題

第 5 段の残りは **Plop（feature 雛形生成）** と **ESLint プラグイン 3 種**
（`@tanstack/eslint-plugin-query` / `@tanstack/eslint-plugin-router` / `eslint-plugin-testing-library`）である。
決めるべきことは 4 つある。

1. **Plop の雛形は何を写すのか。** 「Bulletproof React 風の一般的な雛形」を書くと、この repo に
   実在する feature（ルート factory・`PlanNavItem`・`renderUnitRoute`）と形が違うものを量産する。
2. **生成した feature を合成点（`features/index.ts`）へ機械で配線するか。**
3. **新規プラグインの適用範囲。** 素で入れると、**本リポジトリから是正できない領域**
   （`ai-stock-trading` submodule）と、**そもそも Testing Library ではない領域**（Playwright の E2E）に
   error が湧く。
4. **既存コードに出た違反をどう処理するか。**

## 検討した選択肢

| 論点 | 案 A | 案 B（採用） |
| --- | --- | --- |
| 雛形の出所 | Plop の一般的な React 雛形を書き下ろす | **実在する feature（`sc04-wiki` / `sample`）を写す** |
| 合成点への配線 | `append` アクションで自動追記する | **貼る行を出力し人が足す** |
| 新規プラグインの範囲 | 全ファイルへ一律適用 | **AST と Playwright E2E を射程外にする** |
| 既存違反 | 全部 `off` にする／全部直す | **規則ごとに判断し、直せないものだけ抑制ファイルで grandfather** |

## 決定

### 1. 雛形は「この repo に実在する feature」を写す

`src/plop-templates/feature/` は次の実物から写した。**架空の理想形を書かない。**

- `knowledge/frontend/src/features/sc04-wiki/`（最小の feature。`api/` `hooks/` `stores/` `types/` は
  `.gitkeep` のみ、`components/` と `routes/` に実体、`index.ts` が公開面）
- `templates/unit-template/frontend/src/features/sample/`（雛形の正解形。Lingui マクロ・
  `@platform/ui`・`renderUnitRoute` まで揃っている）

生成物は計画 §ディレクトリ構成 の Feature 単位 6 区分（`api/ components/ hooks/ routes/ stores/ types/`）を
**すべて作る**。空の区分は `.gitkeep` で枠だけ残す —— 消すと次の実装者に「この区分は不要」と伝わる。

**生成した feature のテストは、自 feature の factory だけを `renderUnitRoute` に載せる。**
`sample` の雛形は合成点（`../../index`）を経由しているが、**生成直後は合成点へ未配線**なので
そのままでは落ちる。`sc11-config` のテストと同じ形（`renderUnitRoute((shell) => [createXxxRoute(shell)], …)`）に
することで、**配線前でも生成物単体で緑になる**。

### 2. 合成点（`features/index.ts`）へは自動で追記しない

あちらは**ルートのタプル（`as const`）とナビ配列の 2 経路**であり、タプルを崩すとルート ID とパスの
union が失われて `<Link to>` の静的検査が丸ごと消える（[IADR-0124](./IADR-0124_tanstack-router-unit-composition.md) 決定 1）。
**その壊れ方は静かである**（型エラーにならず、ただ検査されなくなる）。機械で書き換える価値より
壊す危険のほうが大きいので、**貼る行を出力して人が足す**。

**条件付きプロンプト（`when`）も使わない。** plop の CLI は bypass 引数を順番で受け取るため、
条件付きプロンプトがあると非対話実行が `You can not bypass conditional prompts` で止まる（実測）。
CI・スクリプトから叩けない生成器は使われなくなるので、**「左ナビへ出さない」を `navGroup` の
選択肢の 1 つに畳んで 1 本のプロンプトにした。**

### 3. 新規プラグインの適用範囲 —— 「規則を弱める」のではなく「射程を確定する」

| 除外 | 理由 |
| --- | --- |
| `ai-stock-trading/**` | 別プロジェクトの submodule で本リポジトリからは是正できない（[IADR-0120](./IADR-0120_excluded-units-from-gitmodules.md)）。既存の `NO_LEGACY_ROUTER_PATHS` が同じ理由で AST を外しているのと同じ線引きである。実測: AST には TanStack Query / Router の利用が **0 件**、Testing Library の利用が **15 ファイル**（`prefer-screen-queries` 144 件 ＋ `no-node-access` 18 件） |
| `**/e2e/**`（Testing Library のみ） | Playwright の E2E である。当プラグインは "aggressive reporting" で**名前の形だけ**から利用を推測するため、`page.getByRole(...)` を Testing Library のクエリと誤認する。実測 13 件（`platform/frontend/e2e/*.smoke.spec.ts`）で、**いずれも Testing Library を一切 import していない** |

**雛形（`templates/*/frontend`）にも同じ規則を効かせる**（`src/eslint.templates.config.js`）。
そこは Plop の生成先候補でもあり、**緩いままだと「生成した瞬間は緑だが実ユニットへ複製すると赤い」
雛形**が育つ。規則の値は `eslint.config.js` から `TESTING_LIBRARY_RULE_OVERRIDES` として import する
（[IADR-0203](./IADR-0203_renovate-husky-hook-scope.md) 追記 条件 2「規則の情報源を増やさない」）。

### 4. 既存違反は規則ごとに判断する —— 一律 off にしない

**`eslint-plugin-testing-library` の 22 規則のうち off にしたのは 3 つだけ**であり、
それぞれ**この repo の慣用と構造的に衝突する**ことを理由に挙げる（詳細は `src/eslint.config.js` の
`TESTING_LIBRARY_RULE_OVERRIDES` に併記した）。

| 規則 | 実測 | off にする理由 |
| --- | ---: | --- |
| `no-container` | 6 | 「状態を色だけで表さない」の回帰試験は**装飾アイコン**（`aria-hidden="true"`）の有無と差を見る。装飾要素にはアクセシブルな名前が無く、**Testing Library のクエリで取れない**。本番へ `data-testid` を足すのは試験の都合で markup を変える手であり採らない |
| `no-node-access` | 18 | 行スコープの慣用 `within(link.closest('tr')!)` と衝突する。Testing Library に「この要素を含む行」を取るクエリは無く、**規則が指す代替が存在しない** |
| `render-result-naming-convention` | 8 | aggressive reporting の誤爆。**名前が `render` で始まる関数はすべて render とみなす**ため、`renderUnitRoute` / `renderLayout` / `renderFilter` の戻り値まで対象になる（実測: `const onChange = renderFilter(...)` は **`vi.fn()` を受けている**） |

**残る 19 規則と TanStack の 9 規則は `error` のままである。** 導入時点の違反は
`@tanstack/query/no-unstable-deps` 2 件と `testing-library/no-manual-cleanup` 2 件の**計 4 件**だけで、
他は 0 件だった。

**その 4 件は `src/eslint-suppressions.json`（ESLint 9.24+ の抑制ファイル）で grandfather する。**

- **これは承認ではない。** 4 件はいずれも**別作業（#917）が同時に触っている領域**
  （`src/knowledge/**` と `src/platform/frontend/src/**`）にあり、本 PR で書き換えると衝突する。
  `no-unstable-deps` の 2 件は `useMutation` の戻り値を `useCallback` の依存へ直接渡しており、
  **本物の指摘である**（分割代入へ直すのが是正）。
- **規則は弱めていない。** 抑制ファイルは file × rule 単位で「その時点の件数」だけを抑えるため、
  **同じファイルに新しい違反が出れば件数が超えて error になる。**
- **ラチェットとして働く。** 抑制が使われなくなると ESLint は **exit 2 で落ちる**（実測）。
  是正したら `cd src && pnpm exec eslint . --prune-suppressions` で締める。
  `scripts/knip-baseline.json` ほか本リポジトリの他の床と同じ性質である。

## 理由

- 決定 1・2: 雛形の価値は「正しい形を配ること」にあり、**実在しない形を配ると雛形が事故の発生源になる**。
  合成点の自動追記は、壊れても赤くならない箇所を機械に触らせることになる。
- 決定 3: **是正できない領域と、そもそも対象でない領域に規則を及ぼしても、赤が増えるだけで守られるものは
  増えない。** 一方で射程を絞ったぶん「雛形にも同じ規則を効かせる」ことは必須になる。
- 決定 4: 「全部 off」は検査しているのに何も守らない状態を作る。「全部直す」は並行作業の領域を
  書き換えることになる。**規則ごとに判断し、直せないものだけ機械が追跡する形へ落とす**のが両者の間である。

## 結果

- 良い影響: TanStack Query / Router の誤用（依存配列の不安定な値・プロパティ順）と Testing Library の
  誤用（`waitFor` + `getBy` / 非同期クエリの await 漏れ / `debug` の消し忘れ等）が CI で止まる。
  feature の雛形が 1 コマンドで出る。
- トレードオフ: AST（submodule）には規則が及ばない。抑制ファイルは**使われなくなると落ちる**ため、
  #917 側が該当箇所を直した場合は `--prune-suppressions` が要る。
- **本 issue #493 は close しない。** 計画 §採用技術一覧 との**完全一致**が受け入れ基準であり、
  Git Hooks 行の `lint-staged` / `Commitlint` を [IADR-0203](./IADR-0203_renovate-husky-hook-scope.md) 決定 3 が「入れない」と定めている
  （planning#468 で裁定依頼中）。**本決定はその論点を動かさない。**
- フォローアップ: 抑制した 4 件の是正（#917 の着地後）。Supersedes / Superseded by いずれも なし。

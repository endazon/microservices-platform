---
title: 作業仕様書 — 雛形（`templates/*/frontend`）の変更で `frontend-tests.yml` を起動させ、`vitest` の `include` ⊆ `paths` を機械で閉じる（#801）
type: spec
status: done
related_ids:
  - NFR
  - FR-14
  - IADR-0033
  - IADR-0034
  - IADR-0056
  - IADR-0060
  - IADR-0121
  - IADR-0141
  - IADR-0179
  - IADR-0183
  - IADR-0209
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
  - planning:docs/ai-implementation-workflow-guide.md
related_specs:
  - "../adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md"
  - "./20260810_issue-558_carried-debt.md"
  - "./20260815_issue-747_ast-bump-frontend-ci-paths.md"
  - "./20260808_issue-562_frontend-format-gate.md"
---

# 作業仕様書: `frontend-tests.yml` の `paths:` へ雛形を足し、`include` ⊆ `paths` を検査器で閉じる（#801）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **`FR-14`**（追加可変機能ユニット。雛形 `templates/unit-template` はその配布物）。
  ただし**本作業そのものは CI の起動条件という工程の統制**であり、成果物の機能を変えない。
- ユースケース（UC）/ 画面（SC）: なし
- 非機能要件: **`NFR`（無採番）** —— 検査基盤・CI の起動条件に関するメタ作業であり、計画側の
  非機能要件表（`NFR-01`〜`NFR-27`）に当たる番号が無い（`.claude/rules/traceability.md`
  「起点 ID の種別」の 2 の場合。[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）。
  **無いことは「実装側で採番してよい」ではない**（同 決定 2）。**環流しない。**
- 関連 ADR:
  - 計画側: `ADR-0031`（SPA スタック）。**本作業では制約に触れない。**
  - 実装側: [IADR-0033](../adr/IADR-0033_frontend-spa-foundation.md) / [IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)
    （単体テスト＋カバレッジの専用 CI とラチェット）・
    [IADR-0060](../adr/IADR-0060_submodule-unit-operations.md)（可変ユニットの雛形）・
    [IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md)（ユニット構成）・
    [IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md)（**検査器が「偽の緑」を返す条件は警告する**）。
- 計画書リンク: `planning/docs/ai-implementation-workflow-guide.md`（計画リポ）
  （フェーズ末監査は**証跡（実行コマンドと出力）必須**。宣言だけの検証は不合格）

## 目的・背景

`src/vitest.config.ts` の `test.include` は **雛形のテストを収集する**。

```ts
// src/vitest.config.ts（test.include の最終行）
'../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}',
```

一方、そのテストを実際に走らせる唯一の CI（`.github/workflows/frontend-tests.yml`、
`pnpm run test:coverage`）の `paths:` に **`templates/` が 1 件も無い**。すなわち
**雛形のテストだけを壊す変更では、そのテストを走らせるジョブが起動しない。**

着手時点の実測（`origin/develop` = `da05768c`）:

| 事実 | 実測 |
| --- | --- |
| 雛形の実テストファイル | `templates/unit-template/frontend/src/features/sample/components/SamplePage.test.tsx`（**1 件**） |
| `frontend.yml` の `paths:` | `templates/*/frontend/**` を**持つ**（push / pull_request 両方） |
| `frontend-tests.yml` の `paths:` | `templates` を**持たない**（push / pull_request 両方） |
| `pnpm run test:coverage` を走らせるワークフロー | **`frontend-tests.yml` のみ**（`frontend.yml` は typecheck/lint/build/e2e） |

穴が開いた経路も実測した。**`include`・`frontend.yml` の `paths:`・雛形のテスト本体は
すべて同じコミットで入り、`frontend-tests.yml` だけが取り残されている。**

```console
$ git log --oneline -S"../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}" -- src/vitest.config.ts
dca76ced fix(FR-14): unit-template のフロントを現行スタックへ追随させ、雛形を CI の射程へ入れる (#777)
$ git log --oneline -S'templates/*/frontend/**' -- .github/workflows/frontend.yml
dca76ced fix(FR-14): unit-template のフロントを現行スタックへ追随させ、雛形を CI の射程へ入れる (#777)
$ git log --oneline --diff-filter=A -- templates/unit-template/frontend/src/features/sample/components/SamplePage.test.tsx
dca76ced fix(FR-14): unit-template のフロントを現行スタックへ追随させ、雛形を CI の射程へ入れる (#777)
```

**まだ実害は出ていない**（`dca76ced` 以降に `templates/*/frontend` だけを触った PR は **0 件**。
下の §母集合 軸 5 で実測）。**出る前に閉じる。**

### 同型は 4 件目である

「`paths:` の取りこぼしで検査が静かに素通りする」型は、着地日順に次のとおり **4 件目**である。

| # | issue | 着地 | 内容 |
| --- | --- | --- | --- |
| 1 | #562 | `ce96eb81`（2026-08-08） | 整形ゲートの設定（`.prettierrc.json` / `.prettierignore`）が `paths:` に無く、単独変更で CI が走らなかった |
| 2 | #558 | `4dbd5010`（2026-08-10） | 契約と生成の設定（`openapi.yaml` / `orval.*`）が `frontend-tests.yml` に無く、契約だけの PR でカバレッジ床の検査が起動しなかった |
| 3 | #747 | `3cf2437a`（2026-08-15） | AST submodule の gitlink が `src/*/frontend/**` に一致せず、**3 回の bump が素通り**して初期ロードが +35.51 kB 増えた |
| 4 | **本件 #801** | — | 雛形のテストを `include` が収集するのに `frontend-tests.yml` の `paths:` が拾わない |

> **順序の注記**: `scripts/scripts.repo.test.js` の #747 節のコメントは同じ 3 件を **issue 番号順**
> （1=#558 / 2=#562 / 3=#747）で並べている。**集合は同一で、違うのは先頭 2 件の並びだけ**である
> （上表は**是正が着地した日付順**。日付は上の実測による）。**既存コメントは書き換えない**——
> 誤りではなく別の並べ方であり、書き換えても情報は増えない。

`CLAUDE.md`「検査器・規約の追加は**同型の事故が 2 回起きたら**」の条件を（3 件目の時点で既に）満たす。
**3 件目（#747）で置いた検査器は `.gitmodules` の gitlink しか見ておらず、本件は素通りする。**
よって**検査器を 1 本足すのではなく、同じ場所の隣に「別の不変条件」を 1 本置く。**

## 対象範囲

- 対象:
  1. `.github/workflows/frontend-tests.yml` の **`on.push.paths` と `on.pull_request.paths` の両方**へ
     `templates/*/frontend/**` を足す（理由コメントつき）。
  2. `scripts/scripts.repo.test.js` に**突合テスト**を足す。不変条件は
     **「`src/vitest.config.ts` の `test.include` が拾うパスは `frontend-tests.yml` の `paths:` にも載る」**
     （= `include` ⊆ `paths`）。
  3. [IADR-0209](../adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md) と本仕様書。
     `docs/adr/README.md` の索引へ 1 行。
- 対象外:
  - **`scripts/scripts.test.js`**（キット配布物・分類 A）。**触らない。** 固有テストは companion へ。
  - **`CLAUDE.md` / `.claude/rules/`**。必読規約の余白は 1,000B 台（50,130 / 51,200）。**0 バイト増**とする。
    正本は本仕様書と IADR-0209、および検査器のコメントが持つ。
  - **`planning/`**（編集禁止）・**`src/ai-stock-trading`**（submodule）。
  - **`frontend.yml` の `paths:`**。既に `templates/*/frontend/**` を持ち、是正の必要が無い。
  - **`src/eslint.templates.config.js` を `frontend-tests.yml` へ足すこと。** `Frontend Tests` が走らせるのは
    `pnpm run test:coverage` だけで、**ESLint の設定を読まない**（#558 の判断と同じ理由。下の §設計 2）。
  - **2 本のワークフローの `paths:` を対称にすること。** 意図的な非対称が現に 3 件ある（§設計 2）。
  - **カバレッジのしきい値**（`coverage.include` に `templates` は入っておらず、母数は動かない）。
  - **雛形のテストが CI で実際に走ったことの確認**。PR 後に CI の実行結果で行う（#801 の受け入れ基準 4）。
    本作業ではローカルに `src/node_modules` が無く（実測: 未インストール）`pnpm run test:coverage` を実走できない。

## 母集合の実測（`.claude/rules/traceability.md` 規則 1〜8 ／ `traceability.repo.md` 規則 9・10）

**走査基準は `origin/develop` = `da05768c`。** `planning/`（submodule）だけをパスで除外し、
**拡張子でも行フィルタでも絞っていない**（規則 3・4）。**出力は加工していない**（規則 7）。

### 誤りの側から引く（規則 1）

探しているのは「**`vitest` が収集するのに `frontend-tests.yml` が起動しない**」という穴である。
語 1 本では引けないため、**5 軸**で引いた（規則 5）。

| 軸 | 走査コマンド | 結果 |
| --- | --- | --- |
| 1 | `git grep -n -I "templates" -- . ':!planning'` | **344 行 / 121 ファイル**（言及の全体） |
| 2 | `git ls-files \| grep -i "vitest.*config\|playwright.*config"` | **2 件**（`src/vitest.config.ts` / `src/platform/frontend/playwright.config.ts`） |
| 3 | `git grep -n -I "include:" -- '*vitest*' '*playwright*'` | **2 行**（`src/vitest.config.ts:39` = `test.include` / `:59` = `coverage.include`。**playwright は 0**） |
| 4 | `grep -ln "paths:" .github/workflows/*.yml` | **6 本**（`changelog` / `codeql` / `copilot-setup-steps` / `frontend-tests` / `frontend` / `openapi`） |
| 5 | `git log --oneline dca76ced..HEAD -- 'templates/*/frontend'` | **0 件**（穴が開いてから雛形だけを触った変更はまだ無い） |

### 軸 4 の 6 本を全数で分類する（規則 6: 除外を黙って落とさない）

不変条件が掛かるのは「**`vitest` を走らせるワークフロー**」だけである。実測で絞った。

```console
$ git grep -n -I -E "test:coverage|vitest|pnpm run test" -- '.github/workflows'
```

| ワークフロー | `vitest` を走らせるか | 判定 |
| --- | --- | --- |
| **`frontend-tests.yml`** | **走らせる**（`run: pnpm run test:coverage`） | **本検査の対象** |
| `frontend.yml` | 走らせない（typecheck / lint / build / **e2e = Playwright**）。冒頭コメントが「単体テストは frontend-tests.yml へ集約（二重実行回避・IADR-0034）」と明記 | 対象外 |
| `changelog.yml` / `openapi.yml` | 補助成果物の自動生成 | 対象外（フロントのテストと無関係） |
| `codeql.yml` | 静的解析（`paths:` 付きのため必須チェックにしない） | 対象外 |
| `copilot-setup-steps.yml` | エージェント用の環境準備 | 対象外 |

### 軸 3 の非対称を全数で取る（`frontend.yml` にあり `frontend-tests.yml` に無いもの）

各ワークフローの**自己参照**（`.github/workflows/<自身>.yml`）を除いて集合差を取った（push / pull_request 同値）。

| `frontend.yml` のみが持つ | `frontend-tests.yml` へ足すか | 理由 |
| --- | --- | --- |
| `src/.prettierrc.json` | **足さない** | 整形ゲートは `frontend.yml` の lint 相当ジョブが持つ（#562）。`Frontend Tests` は `test:coverage` だけを走らせ整形を見ない（**#558 で理由つきで残した非対称**） |
| `src/.prettierignore` | **足さない** | 同上（同じく #558 の意図的な非対称） |
| `src/lingui.config.ts` | **足さない** | `vitest.config.ts` は `lingui.config.ts` を読まない（使うのは babel マクロだけ。カタログはコミット済み生成物）。**同じく #558 の意図的な非対称** |
| `src/eslint.templates.config.js` | **足さない** | ESLint の設定であり `test:coverage` の結果を変えない。上の 3 件と同型 |
| **`templates/*/frontend/**`** | **足す** | **`test.include` が収集する**。本件の是正対象 |

**結論: 是正するのは 1 件だけで、残る 4 件は理由つきで非対称のまま残す。**
**したがって「2 本の `paths:` を対称にする」検査を書いてはならない**——書けばこの 4 件を誤検出する。

### 規則 9: 追随する文書を記憶で挙げず、**誤りの側の文字列で全文書を走査してから**挙げる

`frontend-tests.yml` の `paths:` を語る記述を全走査した。

```console
$ git grep -n -I "frontend-tests" -- . ':!planning' | wc -l
```

結果は §検証の実測 に生の出力で置く。**追随が要るのは「`frontend-tests.yml` の `paths:` の中身を
列挙している文書」だけ**であり、列挙している文書は無い（ワークフロー自身が正本）。

### 規則 10: この変更で新たに誤りになる自分の記述を引き直す

- **`docs/specs/20260810_issue-558_carried-debt.md` の非対称表（6 件）**: 当時の実測であり、
  **確定済み `docs/specs/` の本文は書き換えない**（`.claude/rules/traceability.repo.md`）。
  本書の表が現時点の実測を持つ。**#558 の表は「当時 6 件」という事実として正しいまま**である
  （その後 `templates/*/frontend/**` と `src/eslint.templates.config.js` が `frontend.yml` へ足され、
  本書時点では 5 件になっている。**数を持つ記述をここで新たに増やさない**）。
- **`scripts/scripts.repo.test.js` の #747 節コメント（同型 3 件の列挙）**: 本件で 4 件目になる。
  **新設する節のコメントで 4 件目であることを述べ、既存コメントは書き換えない**（上の §順序の注記）。
- **導出値は走査ではなく計算し直した**: 非対称の件数（5）・`paths:` の件数（`frontend.yml` 17 /
  `frontend-tests.yml` 12）はいずれも本作業でスクリプトにより数え直した値である。

### 走査の時点（規則 8: 自分の記録が母集合を動かす）

上の値は**本仕様書・IADR-0209 を追跡下へ入れる前**のものである（`git grep` は追跡下しか見ない）。
`git add` 後の実測は §検証の実測 に置き、**引き算を見せる**。

## 設計

### 設計 1: `paths:` へ 1 行足す

`.github/workflows/frontend-tests.yml` の **`on.push.paths` と `on.pull_request.paths` の両方**へ
`- "templates/*/frontend/**"` を足す。理由コメント（起点 `NFR` / #801 / 同型 4 件目 / `FR-14`・`IADR-0060` が
雛形の根拠）を添える。**push と pull_request を別々に足す**（片方だけの事故が過去に起きうる形である）。

### 設計 2: 突合テストを `scripts/scripts.repo.test.js` へ足す

**不変条件は `include` ⊆ `paths` であり、`frontend.yml` との対称性ではない。**
理由は §母集合 の非対称表のとおり（意図的な非対称が 4 件ある）。**この理由をテストのコメントに書く。**

置き場所は **#747 の節と同じブロックの隣**とし、**既存の `paths:` 抽出ヘルパ `pathsOf` を再利用する**
（重複実装しない）。`scripts.repo.test.js` は companion 形式であり、
**正しい入口は `node scripts/scripts.test.js` と `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`**
（単体実行は #797 / [IADR-0208](../adr/IADR-0208_companion-direct-run-guard.md) のガードで exit 1）。

**方式は「代表パス合成・fail-closed」を採る。実ファイル突合は採らない。**

| 案 | 内容 | 判定 |
| --- | --- | --- |
| **A（採用）** | `include` の各 glob から**代表パスを機械合成**し、`paths:` の glob に一致するかを見る | 実ファイルに依存しない。**submodule 未 populate でも同じ判定**になる |
| B | `git ls-files` で `include` に一致する実ファイルを集め、それが `paths:` に載るかを見る | **採らない。** `src/ai-stock-trading` は submodule で `git ls-files` に **0 件**しか出ず、**AST の include が空走査で静かに緑になる**（#664 / PR #672 が扱った fail-open の新設に当たる） |

手順:

1. `src/vitest.config.ts` から **`test.include`** の glob 配列を抽出する。
   **`coverage.include` はインデントが違う**（`test.include` は 4 スペース、`coverage.include` は 6 スペース）。
   取り違えないよう `  test: {` を起点に非貪欲で最初の 4 スペース `include: [` を取る。
2. 各 glob を**リポジトリルート相対**へ正規化する。vite root は `src/` なので
   `path.posix.normalize('src/' + glob)` を使う（`../templates/...` → `templates/...`、
   それ以外は `src/` 前置）。**リポジトリ外へ出たら throw**。
3. 各 glob から**代表パスを機械合成**する（`**` → `a/b`、`*` → `a`、`{x,y}` → `x`。**`**` を先に置換**）。
4. その代表パスが `push.paths` / `pull_request.paths` の**いずれかの glob に一致**することを assert する。
   **push と pull_request を別々に見る**（片方だけ足す事故を止める）。
5. **fail-closed の門**を置く。`include` の抽出が **0 件なら throw**、`paths:` の抽出が **0 件でも throw**。
   （正規表現が壊れたときに「0 件検査して緑」を返させない。IADR-0183 の「偽の緑」と同族）。

glob → `RegExp` は**素の Node で自作する**。本リポには `package.json` も `node_modules` も無く、
`minimatch` 等は使えない。`**`（`/` を跨ぐ）/ `*`（`/` を跨がない）/ `{a,b}` を扱えれば足りる。

### 設計 3: IADR-0209 と索引

`docs/adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md` を起こす。**番号は着手時点の最大 + 1**
（実測: `docs/adr/` の最大は `IADR-0208`）。`docs/adr/README.md` の索引へ 1 行足す
（**索引セルは 200 字以内**。`scripts/adr-index-title-baseline.json` のラチェット）。

## 受け入れ基準（#801 逐語）

- [x] `templates/*/frontend/**` だけを触る変更で `frontend-tests.yml` が起動する
- [x] **変異試験で実測する** —— `frontend-tests.yml` の `paths:` から `templates` を抜くと、新設した突合テストが **fail する**こと。宣言だけの検証は不合格
- [x] `vitest.config.ts` の `include` に新しいパターンを足しても同じ検査が働く（パターンを 1 本ハードコードして終わりにしない）
- [ ] 雛形のテストが実際に走ることを、CI の実行結果で確認する（**PR 後に行う。本作業の射程外**）

## テスト方針

`scripts/scripts.repo.test.js` の 1 節（`ok(...)` 1 件）で不変条件を固定し、**変異試験で実効性を実測する**。

| 変異 | 変異内容 | 期待 |
| --- | --- | --- |
| **M1a** | `frontend-tests.yml` の **push 側だけ**から `templates/*/frontend/**` を抜く | **fail** |
| **M1b** | **pull_request 側だけ**から抜く | **fail** |
| **M1c** | **両方**から抜く | **fail** |
| **M2** | `src/vitest.config.ts` の `include` へ `'../docs/**/*.{test,spec}.{ts,tsx}'` を足す | **fail**（受け入れ基準 3 の実証） |
| **M3** | `include` 抽出の起点（`  test: {` の節名）を壊す | **throw**（0 件で緑にならない） |

**各変異は必ず元へ戻し、`git diff` と `cmp` でバイト単位の復元を確認する。**

## 検証の実測

### 変異試験（M1a / M1b / M1c / M2 / M3）

**各変異は 1 つずつ適用し、その都度 `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` を実走した。**
**終了コードは `; echo "EXIT=$?"` で別途取っている**（パイプで終端すると終了コードがパイプ先のものになる）。

> **省略について（規則 7 の趣旨）**: 各ログの本体は `  ok  <テスト名>` の羅列である。
> 下に**逐語で載せているのは失敗（AssertionError / Error）のブロック全体**であり、
> **判断に用いた行を切ったり潰したりしていない**。省いた部分の行数を明示するので追試できる。
>
> | 実行 | ログ総行数 | うち `  ok  ` 行 | EXIT |
> | --- | --- | --- | --- |
> | 変異なし（是正後） | 695 | 634 | **0** |
> | M1a | 678 | 585 | **1** |
> | M1b | 678 | 585 | **1** |
> | M1c | 681 | 585 | **1** |
> | M2 | 681 | 585 | **1** |
> | M3 | 661 | 585 | **1** |
>
> 変異時に `ok` が 585 で揃うのは、**新設した検査が落ちた時点で以降のテストが走らない**ためである
> （`scripts.test.js` の `ok()` は fail-fast）。**新設分を含む 634 件は変異なしでのみ全通する。**

#### M1a: `frontend-tests.yml` の **push 側だけ**から `templates/*/frontend/**` を抜く

```console
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js > $SP/m1a.log 2>&1; echo "EXIT=$?"
EXIT=1
```

```
AssertionError [ERR_ASSERTION]: vitest が収集するのにテストを走らせる CI が起動しない（#801）。frontend-tests.yml の push / pull_request の**両方**の paths: へ足すこと:
  frontend-tests.yml: push.paths が test.include "../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}" を拾わない（代表パス "templates/a/frontend/src/a/b/a.test.ts"）
+ actual - expected

+ [
+   'frontend-tests.yml: push.paths が test.include "../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}" を拾わない（代表パス "templates/a/frontend/src/a/b/a.test.ts"）'
+ ]
- []

    at /home/user/wt-paths/scripts/scripts.repo.test.js:7748:14
    at ok (/home/user/wt-paths/scripts/scripts.test.js:22:3)
    at countingOk (/home/user/wt-paths/scripts/scripts.test.js:1118:12)
    at module.exports (/home/user/wt-paths/scripts/scripts.repo.test.js:7694:5)
    at loadCompanionTests (/home/user/wt-paths/scripts/scripts.test.js:1120:16)
    at Object.<anonymous> (/home/user/wt-paths/scripts/scripts.test.js:1185:15)
    at Module._compile (node:internal/modules/cjs/loader:1705:14)
    at Object..js (node:internal/modules/cjs/loader:1838:10)
    at Module.load (node:internal/modules/cjs/loader:1441:32)
    at Function._load (node:internal/modules/cjs/loader:1263:12) {
  generatedMessage: false,
  code: 'ERR_ASSERTION',
  actual: [
    'frontend-tests.yml: push.paths が test.include "../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}" を拾わない（代表パス "templates/a/frontend/src/a/b/a.test.ts"）'
  ],
  expected: [],
  operator: 'deepStrictEqual',
  diff: 'simple'
}
```

**`push` 側だけが上がっている** —— 片側の欠落を片側として検出できている。

#### M1b: **pull_request 側だけ**から抜く

```console
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js > $SP/m1b.log 2>&1; echo "EXIT=$?"
EXIT=1
```

```
    at /home/user/wt-paths/scripts/scripts.repo.test.js:7748:14
    at ok (/home/user/wt-paths/scripts/scripts.test.js:22:3)
    at countingOk (/home/user/wt-paths/scripts/scripts.test.js:1118:12)
    at module.exports (/home/user/wt-paths/scripts/scripts.repo.test.js:7694:5)
    at loadCompanionTests (/home/user/wt-paths/scripts/scripts.test.js:1120:16)
    at Object.<anonymous> (/home/user/wt-paths/scripts/scripts.test.js:1185:15)
    at Module._compile (node:internal/modules/cjs/loader:1705:14)
    at Object..js (node:internal/modules/cjs/loader:1838:10)
    at Module.load (node:internal/modules/cjs/loader:1441:32)
    at Function._load (node:internal/modules/cjs/loader:1263:12) {
  generatedMessage: false,
  code: 'ERR_ASSERTION',
  actual: [
    'frontend-tests.yml: pull_request.paths が test.include "../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}" を拾わない（代表パス "templates/a/frontend/src/a/b/a.test.ts"）'
  ],
  expected: [],
  operator: 'deepStrictEqual',
  diff: 'simple'
}
```

**`pull_request` 側だけが上がっている。**

#### M1c: **両方**から抜く

```console
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js > $SP/m1c.log 2>&1; echo "EXIT=$?"
EXIT=1
```

```
    at /home/user/wt-paths/scripts/scripts.repo.test.js:7748:14
    at ok (/home/user/wt-paths/scripts/scripts.test.js:22:3)
    at countingOk (/home/user/wt-paths/scripts/scripts.test.js:1118:12)
    at module.exports (/home/user/wt-paths/scripts/scripts.repo.test.js:7694:5)
    at loadCompanionTests (/home/user/wt-paths/scripts/scripts.test.js:1120:16)
    at Object.<anonymous> (/home/user/wt-paths/scripts/scripts.test.js:1185:15)
    at Module._compile (node:internal/modules/cjs/loader:1705:14)
    at Object..js (node:internal/modules/cjs/loader:1838:10)
    at Module.load (node:internal/modules/cjs/loader:1441:32)
    at Function._load (node:internal/modules/cjs/loader:1263:12) {
  generatedMessage: false,
  code: 'ERR_ASSERTION',
  actual: [
    'frontend-tests.yml: push.paths が test.include "../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}" を拾わない（代表パス "templates/a/frontend/src/a/b/a.test.ts"）',
    'frontend-tests.yml: pull_request.paths が test.include "../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}" を拾わない（代表パス "templates/a/frontend/src/a/b/a.test.ts"）'
  ],
  expected: [],
  operator: 'deepStrictEqual',
  diff: 'simple'
}
```

**2 件そろって上がっている。**

#### M2: `src/vitest.config.ts` の `include` へ `paths:` が拾わない新パターンを足す（受け入れ基準 3）

足したのは `'../docs/**/*.{test,spec}.{ts,tsx}'` の 1 行のみ。

```console
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js > $SP/m2.log 2>&1; echo "EXIT=$?"
EXIT=1
```

```
    at /home/user/wt-paths/scripts/scripts.repo.test.js:7748:14
    at ok (/home/user/wt-paths/scripts/scripts.test.js:22:3)
    at countingOk (/home/user/wt-paths/scripts/scripts.test.js:1118:12)
    at module.exports (/home/user/wt-paths/scripts/scripts.repo.test.js:7694:5)
    at loadCompanionTests (/home/user/wt-paths/scripts/scripts.test.js:1120:16)
    at Object.<anonymous> (/home/user/wt-paths/scripts/scripts.test.js:1185:15)
    at Module._compile (node:internal/modules/cjs/loader:1705:14)
    at Object..js (node:internal/modules/cjs/loader:1838:10)
    at Module.load (node:internal/modules/cjs/loader:1441:32)
    at Function._load (node:internal/modules/cjs/loader:1263:12) {
  generatedMessage: false,
  code: 'ERR_ASSERTION',
  actual: [
    'frontend-tests.yml: push.paths が test.include "../docs/**/*.{test,spec}.{ts,tsx}" を拾わない（代表パス "docs/a/b/a.test.ts"）',
    'frontend-tests.yml: pull_request.paths が test.include "../docs/**/*.{test,spec}.{ts,tsx}" を拾わない（代表パス "docs/a/b/a.test.ts"）'
  ],
  expected: [],
  operator: 'deepStrictEqual',
  diff: 'simple'
}
```

**新しく足したパターンに対して同じ検査が働いている**（パターンをハードコードしていない証拠）。

#### M3: `include` 抽出の起点（`  test: {` の節名）を壊す —— fail-closed の門

`  test: {` を `  testSuite: {` へ一時的に改名した。

```console
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js > $SP/m3.log 2>&1; echo "EXIT=$?"
EXIT=1
```

```
  ok  #755: check-reading-budget は集合ごとに判定し、予算値を出典つきで持つ
  ok  #755 / #751: 実データ走査は planning を populate するジョブへ移り --require-planning が付いている
  ok  NFR / #747: .gitmodules の src/ 配下 submodule がフロント CI の paths: に全て挙がっている
/home/user/wt-paths/scripts/scripts.repo.test.js:7701
        throw new Error(
        ^

Error: src/vitest.config.ts の test.include 節を読めない（抽出の正規表現が壊れている）。0 件検査で緑を返さないため fail させる
    at /home/user/wt-paths/scripts/scripts.repo.test.js:7701:15
    at ok (/home/user/wt-paths/scripts/scripts.test.js:22:3)
    at countingOk (/home/user/wt-paths/scripts/scripts.test.js:1118:12)
    at module.exports (/home/user/wt-paths/scripts/scripts.repo.test.js:7694:5)
    at loadCompanionTests (/home/user/wt-paths/scripts/scripts.test.js:1120:16)
    at Object.<anonymous> (/home/user/wt-paths/scripts/scripts.test.js:1185:15)
    at Module._compile (node:internal/modules/cjs/loader:1705:14)
    at Object..js (node:internal/modules/cjs/loader:1838:10)
    at Module.load (node:internal/modules/cjs/loader:1441:32)
    at Function._load (node:internal/modules/cjs/loader:1263:12)
```

**0 件検査で緑にならず throw している。** 抽出が壊れたことが検査の失敗として表に出る。

#### 変異の復元（バイト単位）

```console
$ cmp $SP/orig/frontend-tests.yml .github/workflows/frontend-tests.yml && echo "cmp OK: frontend-tests.yml"
cmp OK: frontend-tests.yml
$ cmp $SP/orig/vitest.config.ts src/vitest.config.ts && echo "cmp OK: vitest.config.ts"
cmp OK: vitest.config.ts
$ cmp $SP/orig/scripts.repo.test.js scripts/scripts.repo.test.js && echo "cmp OK: scripts.repo.test.js"
cmp OK: scripts.repo.test.js
$ git diff --stat
 .github/workflows/frontend-tests.yml |  14 ++++
 docs/adr/README.md                   |   1 +
 scripts/scripts.repo.test.js         | 139 +++++++++++++++++++++++++++++++++++
 3 files changed, 154 insertions(+)
```

**`src/vitest.config.ts` は差分に現れない**（M2 / M3 の変異が完全に戻っている）。

### 規則 9 の走査（誤りの側の文字列で全文書を引く）

```console
$ git grep -n -I "frontend-tests" -- . ':!planning' | wc -l
74
$ git grep -n -I -E 'templates/\*/frontend' -- . ':!planning' ':!.github/workflows/frontend-tests.yml'
```

**74 行 / 39 ファイル**（本仕様書・IADR-0209 を追跡下へ入れる前）。2 本目の走査の結果、
**`frontend-tests.yml` の `paths:` の中身を列挙している文書は 1 件も無い**（ワークフロー自身が正本で、
他は `frontend.yml` / `pnpm-workspace.yaml` / `eslint.templates.config.js` / `package.json` /
`vitest.config.ts` といった**別ファイルの自分の設定**か、雛形への言及である）。
**したがって追随が要る文書は無い。**

### 走査の時点の引き算（規則 8）

**`git grep` は追跡下の作業ツリーを読む**ため、同じコマンドが 3 つの時点で違う数を返す。全部出す。

```console
$ git grep -n -I "frontend-tests" HEAD -- . ':!planning' | wc -l
63
$ git grep -n -I "frontend-tests" -- . ':!planning' | wc -l      # 追跡下の変更のみ反映（新規 2 ファイルは未追跡で見えない）
74
$ git add -A && git grep -n -I "frontend-tests" -- . ':!planning' | wc -l
135
```

**引き算**:

- **63**（`develop` = `da05768c`。本作業の変更が 1 バイトも入っていない値）
- **→ 74**（+11）: 追跡下ファイルへの本作業の加筆。内訳は
  `scripts/scripts.repo.test.js` の新設節（12 行中、`develop` 時点から増えた分）と
  `docs/adr/README.md` の索引 1 行。`.github/workflows/frontend-tests.yml` は 2 行のまま
  （新設コメントに `frontend-tests` の語が無いため増えない）。
- **→ 135**（+61）: `git add` で追跡下に入った**新規 2 ファイル**。
  内訳は本仕様書 **46 行** ＋ [IADR-0209](../adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md) **15 行**（46 + 15 = 61。**一致する**）。

**すなわち母集合の判断（「`paths:` を列挙している文書は無い」）に効くのは 63 行の側**であり、
74 / 135 の増分は**すべて本作業自身の記録**である。

### 検査器・テストの実行結果

**`git add -A` の後に実測した**（[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md) の順序）。**終了コードはパイプを挟まず `echo "EXIT=$?"` で取った。**

```console
$ for s in check-doc-links check-doc-type-vocabulary check-doc-status-vocabulary check-cross-repo-refs \
           check-plan-id-qualification check-adr-numbering check-reading-budget check-kit-sync; do
    node scripts/$s.js > $SP/$s.log 2>&1; echo "$s EXIT=$?"
  done
check-doc-links EXIT=0
check-doc-type-vocabulary EXIT=0
check-doc-status-vocabulary EXIT=0
check-cross-repo-refs EXIT=0
check-plan-id-qualification EXIT=0
check-adr-numbering EXIT=0
check-reading-budget EXIT=0
check-kit-sync EXIT=0
```

主要な出力（逐語）:

```
[check-adr-numbering] OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です。
[check-kit-sync] OK: キット 115 件を分類表と突合しました（A 77 件はバイト一致 / B 26 件は固有デルタ / C 4 件は同期しない / 対象外 8 件）。
  warn  Claude Code: 50,130 バイト（予算 51,200 の 97.9%）
          CLAUDE.md  19,981
          .claude/rules/traceability.md  24,592
          .claude/rules/traceability.repo.md  5,557
```

**必読規約は 50,130 バイトのまま**である（着手時と同値。**0 バイト増**を実測で確認した）。
`check-reading-budget` の `warn` は 90% 帯の警告であり **exit 0**（本作業が増やしたものではない）。

```console
$ node scripts/scripts.test.js > $SP/final-plain.log 2>&1; echo "EXIT=$?"
EXIT=0
✓ 634 tests passed
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js > $SP/final-repo.log 2>&1; echo "EXIT=$?"
EXIT=0
✓ 634 tests passed
```

**新設した検査は `ok  NFR / #801: vitest の test.include が拾うパスは frontend-tests.yml の paths: にも載る` として通っている**
（変異なしの実行ログ。上の変異試験の表と同じ 634 件）。

**`node scripts/scripts.repo.test.js` を単体で叩いていない** —— companion であり
**沈黙の exit 0 は検証の証跡にならない**（#797 / [IADR-0208](../adr/IADR-0208_companion-direct-run-guard.md)）。

### コミット後の検査（HEAD を読むもの）

コミットは**論理単位で 3 本**に分けた（`ci` = `paths:` の是正 / `test` = 検査器 / `docs` = ADR・仕様書）。

```console
$ node scripts/check-doc-updated.js > $SP/check-doc-updated.log 2>&1; echo "EXIT=$?"
EXIT=0
[check-doc-updated] OK: 変更された docs/ の Markdown 3 件に updated: の据え置きはありません。（updated: を持たない 1 件は対象外）
$ node scripts/check-commit-messages.js > $SP/check-commit-messages.log 2>&1; echo "EXIT=$?"
EXIT=0
コミット規約チェック: 範囲 origin/develop..HEAD（3 件）
検査対象 3 件 / 除外 0 件
✓ すべてのコミットが規約に適合
```

**起点 ID は無採番の `NFR`** ＋ 新設した `IADR-0209` を併記した。

## 計画書との差異

- 差異: なし（計画書の記述に反する実装は無い。CI の起動条件の是正のみ）

## 未決事項

- **雛形のテストが CI で実際に走ることの確認**は PR 後に行う（受け入れ基準 4。本作業の射程外）。
- `src/eslint.templates.config.js` を `frontend-tests.yml` へ足さない判断は、
  「`test:coverage` は ESLint 設定を読まない」という現在の実装に依存する。
  **将来 `frontend-tests.yml` が lint を走らせるようになったら再判定が要る**（IADR-0209 §結果 に申し送り）。

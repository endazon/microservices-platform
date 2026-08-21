---
title: IADR-0209 `vitest` の `test.include` は `frontend-tests.yml` の `paths:` に包含される（対称性ではなく包含を検査する）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-14
  - IADR-0033
  - IADR-0034
  - IADR-0056
  - IADR-0060
  - IADR-0141
  - IADR-0179
  - IADR-0183
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
  - planning:docs/ai-implementation-workflow-guide.md
---

# IADR-0209: `test.include` ⊆ `frontend-tests.yml` の `paths:` を不変条件とする

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: 実装担当（AI）／起票 #801

## 起点・関連

- 関連する計画書 ID: **`NFR`（無採番）** —— CI の起動条件という**工程の統制**であり、計画側の
  非機能要件表（`NFR-01`〜`NFR-27`）に当たる番号が無い（`.claude/rules/traceability.md`
  「起点 ID の種別」の 2 の場合。[IADR-0179](./IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）。
  **環流しない。** 雛形そのものの根拠は **`FR-14` / [IADR-0060](./IADR-0060_submodule-unit-operations.md)**。
- 作業仕様書: [`docs/specs/20260816_issue-801_frontend-tests-paths-templates.md`](../specs/20260816_issue-801_frontend-tests-paths-templates.md)
- 関連 IADR: [IADR-0033](./IADR-0033_frontend-spa-foundation.md) / [IADR-0034](./IADR-0034_frontend-coverage-gate.md)
  （単体テスト＋カバレッジの専用 CI とラチェット）・
  [IADR-0056](./IADR-0056_repo-unit-structure-platform-knowledge.md)（ユニット構成）・
  [IADR-0183](./IADR-0183_false-green-warning-on-worktree-state.md)（**検査器が「偽の緑」を返す条件は警告する**）・
  [IADR-0141](./IADR-0141_audit-rounds-and-population-drawing.md)（母集合の引き方）

## コンテキストと課題

`src/vitest.config.ts` の `test.include` は **雛形（`templates/*/frontend`）のテストを収集する**
（`'../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}'`）。一方、そのテストを実際に走らせる
**唯一の CI**（`.github/workflows/frontend-tests.yml`。`run: pnpm run test:coverage`）の `paths:` に
`templates` が 1 件も無かった。**雛形のテストだけを壊す変更では、それを走らせるジョブが起動しない。**

穴が開いた経路は実測できる。**`include`・`frontend.yml` の `paths:`・雛形のテスト本体は
すべて同じコミット `dca76ced`（#777）で入り、`frontend-tests.yml` だけが取り残された。**

```console
$ git log --oneline -S"../templates/*/frontend/src/**/*.{test,spec}.{ts,tsx}" -- src/vitest.config.ts
dca76ced fix(FR-14): unit-template のフロントを現行スタックへ追随させ、雛形を CI の射程へ入れる (#777)
$ git log --oneline -S'templates/*/frontend/**' -- .github/workflows/frontend.yml
dca76ced fix(FR-14): unit-template のフロントを現行スタックへ追随させ、雛形を CI の射程へ入れる (#777)
```

**「`paths:` の取りこぼしで検査が静かに素通りする」型は、着地日順に 4 件目である。**

| # | issue | 着地 | 内容 |
| --- | --- | --- | --- |
| 1 | #562 | `ce96eb81`（2026-08-08） | 整形ゲートの設定が `paths:` に無く、単独変更で CI が走らなかった |
| 2 | #558 | `4dbd5010`（2026-08-10） | 契約と生成の設定が `frontend-tests.yml` に無く、契約だけの PR でカバレッジ床の検査が起動しなかった |
| 3 | #747 | `3cf2437a`（2026-08-15） | AST submodule の gitlink が `src/*/frontend/**` に一致せず、**3 回の bump が素通り**した |
| 4 | **#801** | — | `test.include` が雛形のテストを収集するのに `frontend-tests.yml` の `paths:` が拾わない |

`CLAUDE.md`「**検査器・規約の追加は同型の事故が 2 回起きたら**」の条件を（3 件目の時点で既に）満たす。
**3 件目で置いた検査器（`scripts/scripts.repo.test.js` の `.gitmodules` 突合）は gitlink しか見ておらず、
本件は素通りする。** よって同じ場所へ**別の不変条件**を 1 本置く。

> **順序の注記**: `scripts.repo.test.js` の #747 節のコメントは同じ 3 件を **issue 番号順**
> （1=#558 / 2=#562 / 3=#747）で並べている。**集合は同一で、違うのは先頭 2 件の並びだけ**である
> （上表は是正が着地した日付順で、日付は実測による）。既存コメントは書き換えない。

## 検討した選択肢

### 軸 A: 何を不変条件にするか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A1（採用）** | **`test.include` ⊆ `frontend-tests.yml` の `paths:`**（包含） | **走らせるものと起動条件を直接結ぶ。**意図的な非対称と衝突しない |
| A2 | `frontend.yml` と `frontend-tests.yml` の `paths:` を**対称**にする | **採らない**（下の決定 1） |
| A3 | 何も検査せず、`paths:` へ 1 行足して終わる | **採らない。**同型が既に 3 回起きており、4 回目も同じ手順で再発する |

### 軸 B: 一致をどう判定するか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **B1（採用）** | `include` の glob から**代表パスを機械合成**し、`paths:` の glob と突き合わせる | 実ファイルに依存せず、**submodule の populate 状態で判定が変わらない** |
| B2 | `git ls-files` で `include` に一致する実ファイルを集め、`paths:` に載るかを見る | **採らない**（下の決定 3） |

## 決定

1. **不変条件は「`test.include` ⊆ `frontend-tests.yml` の `paths:`」であり、`frontend.yml` との
   対称性ではない。** 理由は**意図的な非対称が現に 4 件ある**ことである。
   [`docs/specs/20260810_issue-558_carried-debt.md`](../specs/20260810_issue-558_carried-debt.md) が
   非対称を全数で測り、`src/.prettierrc.json` / `src/.prettierignore` / `src/lingui.config.ts` の
   **3 件を理由つきで意図的に残した**。本件で `src/eslint.templates.config.js` が 4 件目として同じ側に立つ。
   **`frontend.yml` と `frontend-tests.yml` は役割が違う** —— 前者は **typecheck / lint / format / build /
   e2e（Playwright）**、後者は **`pnpm run test:coverage` の 1 本だけ**である。整形設定も
   `lingui.config.ts` も ESLint 設定も `test:coverage` の結果を変えないため、足すと
   **何も新しく確かめられないジョブが起動して CI 時間だけ伸びる**。
   **対称性を検査にすれば、この 4 件をすべて誤検出する。**
2. **`frontend-tests.yml` の `push.paths` と `pull_request.paths` の両方**へ
   `templates/*/frontend/**` を足し、検査も **push / pull_request を別々に見る**
   （片方だけ足す事故をそのまま検出できるようにする）。
3. **判定は「代表パス合成」で行い、実ファイル突合は採らない。**
   `src/ai-stock-trading` は submodule であり、未 populate では `git ls-files` に **0 件**しか出ない。
   実ファイル依存にすると **AST の `include` が空走査で静かに緑になる** —— #664 / PR #672 が扱った
   **fail-open を新設する**ことになる。代表パスは `**` → `a/b`、`*` → `a`、`{x,y}` → `x` で機械合成し、
   populate の有無に関わらず同じ判定を返す。
4. **fail-closed の門を 3 つ置く**（[IADR-0183](./IADR-0183_false-green-warning-on-worktree-state.md)
   の「偽の緑」と同族）。
   ① `test.include` 節を読めない、② `include` の glob が 0 件、③ `paths:` が読めない／0 件 ——
   いずれも **throw** する。**正規表現が腐ったときに「0 件検査して緑」を返さない。**
5. **`include` は 1 本もハードコードしない。** 設定ファイルから抽出した**全 glob**を回す。
   `include` にパターンを足せば、そのパターンに対して同じ検査が自動で掛かる（#801 受け入れ基準 3）。
   `test.include` と `coverage.include` は**インデントで区別する**（前者 4 スペース・後者 6 スペース）。
6. **glob → `RegExp` は素の Node で自作する。** 本リポジトリには `package.json` も `node_modules` も
   無く、`minimatch` 等を使えない。`**`（`/` を跨ぐ）/ `*`（跨がない）/ `{a,b}`（選択）を扱えれば足りる。
7. **置き場所は `scripts/scripts.repo.test.js` の #747 節の隣**とし、**既存の `paths:` 抽出ヘルパ
   `pathsOf` を再利用する**（重複実装しない）。`scripts/scripts.test.js` は**キット配布物・分類 A**
   であり触らない。正しい入口は `node scripts/scripts.test.js` と
   `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（単体実行は #797 /
   [IADR-0208](./IADR-0208_companion-direct-run-guard.md) のガードで exit 1）。
8. **必読規約（`CLAUDE.md` / `.claude/rules/`）は 0 バイト増とする。** 余白は 1,000B 台しかない。
   正本は本 IADR・作業仕様書・検査器のコメントが持つ。

## 理由

- **走らせるものと起動条件は別々のファイルにあり、片方だけ直しても機械が何も言わない。**
  #777 は `include` と `frontend.yml` を同時に直しながら `frontend-tests.yml` を落とした。
  **人の注意で防ぐ形はこれで 4 回失敗している。**
- **包含は「意図」と一致する**が、対称性は一致しない。**両ワークフローが同じものを検査すべきだという
  前提そのものが誤り**であり、それを機械化すると**正しい設計が赤くなる検査器**ができる ——
  そういう検査器は外される。#558 が理由つきで残した非対称を、本検査は 1 件も踏まない。
- **fail-open な検査器を新設しない。** #664 / PR #672 の教訓（空走査で緑）と IADR-0183 の
  「偽の緑」は同じ族であり、**検査器を足す作業そのものが同じ穴を開けうる**。だから
  実ファイル依存を避け、抽出 0 件を throw にした。

## 結果

- 良い影響:
  - **雛形（`templates/*/frontend`）だけを触る変更で `frontend-tests.yml` が起動する。**
  - `test.include` へパターンを足したとき、`paths:` の追随漏れが**その場で赤くなる**。
  - **submodule の populate 状態に依存しない**（AST が未取得のローカルでも CI と同じ判定）。
- 悪い影響・トレードオフ:
  - **`paths:` の側は依然として手で書く。** 検査は「足し忘れ」を検出するが、自動では足さない。
  - **`frontend.yml` は本検査の対象外**である（`vitest` を走らせないため）。同ファイルの `paths:` の
    取りこぼしは #747 の検査器（gitlink）と人のレビューが見る。
  - **代表パスは 1 本だけ合成する。** 例えば `{ts,tsx}` の `tsx` 側や `**` の 0 階層一致は試さない。
    `paths:` 側の glob は概ね接頭辞（`src/*/frontend/**` 等）であり、選択肢の 1 本が通れば他も通る
    構造だからである。**これは近似であり、完全な包含判定ではない。**
- フォローアップ:
  - **`frontend-tests.yml` が将来 lint / format を走らせるようになったら、
    `src/eslint.templates.config.js` などの非対称 4 件を再判定すること**（決定 1 の前提が変わる）。
  - **雛形のテストが CI で実際に走ったこと**は、PR 後の CI 実行結果で確認する（#801 受け入れ基準 4）。
    **確認は「success か」ではなく「skipped になっていないか」で見る**（#524 の先例）。

## 関連

- Supersedes: なし
- Superseded by: なし

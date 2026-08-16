---
title: IADR-0211 Knip の走査スコープと、未使用件数の baseline ラチェット
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0031
  - IADR-0056
  - IADR-0060
  - IADR-0120
  - IADR-0121
  - IADR-0125
  - IADR-0141
  - IADR-0147
  - IADR-0179
  - IADR-0183
  - IADR-0203
  - IADR-0209
  - IADR-0214
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md (採用技術一覧: Dead Code 検出 = Knip)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
---

# IADR-0211: Knip の走査スコープと、未使用件数の baseline ラチェット

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: 実装担当（AI）／起票 #493

## 起点・関連

- 関連する計画書 ID:
  - **`ADR-0031`**（SPA スタック）。計画 `06_technical/13_frontend-stack.md` §採用技術一覧の
    「**Dead Code 検出 = Knip（採用）**」がそのまま要求である。
  - **`NFR`（無採番）** —— 検査基盤の追加というメタ作業であり、計画側の非機能要件表
    （`NFR-01`〜`NFR-27`）に当たる番号が無い（[IADR-0179](./IADR-0179_unnumbered-nfr-for-meta-work.md)
    決定 1。**無いことは実装側で採番してよいではない**＝同 決定 2。**環流しない**）。
- 作業仕様書: [`docs/specs/20260816_issue-493_knip-unused-detection.md`](../specs/20260816_issue-493_knip-unused-detection.md)
- 関連 IADR:
  - [IADR-0121](./IADR-0121_spa-stack-migration-staging.md) 決定 1（**第 5 段 = 運用系**〔Knip / Plop / Renovate / Husky〕）
    ・決定 3（orval 生成物はコミットし再生成差分で検査する）
  - [IADR-0203](./IADR-0203_renovate-husky-hook-scope.md)（第 5 段の先行分 1/2 = Renovate / Husky。#768）
  - [IADR-0125](./IADR-0125_ui-primitives-i18n-catalog-and-storybook.md)（**「Knip の未使用検出は出る（利用者は #452）」と予告していた**）
  - [IADR-0120](./IADR-0120_excluded-units-from-gitmodules.md) / [IADR-0056](./IADR-0056_repo-unit-structure-platform-knowledge.md)（別プロジェクト submodule の不干渉・ユニット構成）
  - [IADR-0141](./IADR-0141_audit-rounds-and-population-drawing.md)（**同じ値を 2 箇所に置くと片方が腐る**）
  - [IADR-0147](./IADR-0147_chunk-rule-presence-check.md)（**検出漏れは開示してよいが偽陽性は塞ぐ**）
  - [IADR-0183](./IADR-0183_false-green-warning-on-worktree-state.md)（**偽の緑**を返す条件は fail へ倒す）
- 関連 issue: #493（親）/ #768（切り出し 1/2）/ #452（画面の再実装）/ #788（第 4 段）

## コンテキストと課題

計画 `13_frontend-stack` §採用技術一覧は Knip を「採用」と定めるが、本リポジトリには
**未使用のファイル・export・依存を検出する機構が 1 つも無い**。ESLint の `no-unused-vars` は
**ファイル内スコープ**しか見ないため、「export したが誰も import しないシンボル」「どこからも
到達しないファイル」「宣言したが誰も使わない依存」は素通りする。SPA は第 1〜3 段で旧スタックを
丸ごと置き換えてきたため、置き換えの残骸が残っていても誰も赤くならない。

素の Knip を走らせると **692 件**出る（実測。内訳は作業仕様書 §実測）。この状態で決めるべきは 5 点である。

- **A. 走査スコープ**（別プロジェクトの submodule をどう外すか。既存の除外集合と二重管理になるか）
- **B. 生成物（orval）の扱い**（`ignore` か `entry` か）
- **C. ゲートの形**（0 件を要求するか、ラチェットにするか）
- **D. 設定ファイルの形式**（`knip.json` / `knip.jsonc` / `knip.config.ts`）
- **E. #452 を待つべきか**（先行仕様書 #768 は Knip を「#452 待ち」としていた）

## 検討した選択肢

### 論点 A: 別プロジェクト submodule（`src/ai-stock-trading`）の除外と、除外の単一情報源

| | A1. 除外しない | A2. `knip` の設定へ書く（採用） | A3. `knip.config.ts` にして `.gitmodules` から導出する |
| --- | --- | --- | --- |
| 本リポの未使用判定として正しいか | **誤り**（別プロジェクトの残件を本リポの床に持つ） | 正しい | 正しい |
| 件数（実測） | 692 件 | **648 件**（−44） | 648 件 |
| 除外の宣言箇所 | — | **4 つ目**（`.prettierignore` / `eslint.config.js` / `.gitmodules` に続く） | 3 つのまま |
| 新たに増えるコード | なし | なし（宣言 1 行） | **導出ロジック**（テストされない新コード） |
| 設定を読むために必要なもの | — | なし | **TS のトランスパイル** |
| 腐りの検出 | — | **検査器の self-test が `.gitmodules` と突合** | 構造的に腐らない |

### 論点 B: orval 生成物の扱い

| | B1. `ignore` に入れる | **B2. `entry` に入れる（採用）** |
| --- | --- | --- |
| 生成物自身の未使用 export | 報告されない | 報告されない |
| **生成物が使う依存**（`msw` / `@faker-js/faker`） | **未使用として湧く（偽陽性）** | 使用済みになる |
| **生成物からしか使われない本体の export** | **未使用として湧く（偽陽性）** | 使用済みになる |
| 件数（実測・A2 の上で） | — | **44 件**（648 → 44） |

### 論点 C: ゲートの形

| | C1. 0 件を要求する | **C2. baseline ラチェット（採用）** | C3. warn のみ |
| --- | --- | --- | --- |
| いま通せるか | **通せない**（41 件を消す＝本 PR が片付けになる） | 通せる | 通せる |
| 新規混入を止めるか | 止める | **止める** | **止めない**（緑のまま増える） |
| #452（画面の再実装）と衝突するか | **する**（未使用 export の多くは #452 が触る） | しない | しない |
| 本リポの既定の作法との整合 | — | **一致**（backend-library / chunk-budget / adr-index-title / test-spec-coverage） | — |

### 論点 D: 設定ファイルの形式

| | D1. `knip.json` | **D2. `knip.jsonc`（採用）** | D3. `knip.config.ts` |
| --- | --- | --- | --- |
| **設定の隣に理由を書けるか** | **書けない**（実測: 未知キー `description` は `ERROR: Invalid input (unrecognized_keys)` で exit 2） | **書ける**（コメント） | 書ける |
| 設定の実行が要るか | 不要 | 不要 | **要る**（宣言だけの設定に処理を持ち込む） |
| 本リポの作法 | `renovate.json` は `description` 配列で理由を持つ | **同じ目的を満たす** | `eslint.config.js` / `vite.config.ts` はロジックがあるから JS/TS |

### 論点 E: #452（画面の再実装）を待つか

| | E1. 待つ（#768 の当初判断） | **E2. 待たずに床を打つ（採用）** |
| --- | --- | --- |
| 待つ理由（大量に出る）は解消されるか | 時間で解消 | **ラチェットが解消する**（据え置いて増加だけ止める） |
| #452 が積む未使用を測れるか | **測れない**（比較対象が無い） | **測れる**（床との差分がそのまま増分） |
| 導入の所要 | 同じ | 同じ |

## 決定

### 決定 1: Knip を `src/` の devDependency として導入し、設定は `src/knip.jsonc` に置く（論点 D = D2）

`.json` を採らないのは、**knip が未知のキーを弾くため理由を設定の隣に書けない**からである（実測）。
`.config.ts` を採らないのは、本設定が**宣言だけ**で済み、設定の実行を要求する理由が無いためである。
`pnpm run knip`（`src/package.json`）は**人が内訳を読むための入口**であり、CI のゲートではない。

### 決定 2: 別プロジェクト submodule は `ignoreWorkspaces` で外す。**二重管理は 1 箇所残し、腐りは機械で見る**（論点 A = A2）

`src/ai-stock-trading` を外す意図は、既に **3 通りの書式で 3 箇所**に存在する
（`src/.prettierignore` の `ai-stock-trading/`、`src/eslint.config.js` の `ignores`、
`scripts/lib/excluded-units.js` が読む `.gitmodules`）。**すでに単一情報源ではない。**
Knip の設定はこの 4 つ目になるが、増分は「1 箇所増える」であって「単一情報源が壊れる」ではない。

[IADR-0141](./IADR-0141_audit-rounds-and-population-drawing.md) の要請（同じ値を 2 箇所に置くと片方が腐る）には、
**導出ではなく検出**で応える。`node scripts/check-knip.js --self-test` が
**「`.gitmodules` の `src/<unit>` submodule が `knip.jsonc` の `ignoreWorkspaces` で外れているか」**を突き合わせ、
**`src/` へ submodule を足したのに Knip の除外へ足し忘れると落ちる。**
`ci.yml` の `scripts-tests` ジョブ（`REQUIRE_REPO_TESTS=1`）が毎 PR でこれを走らせる。

### 決定 3: 生成物（orval）は `ignore` ではなく `entry` に置く（論点 B = B2）

`ignore` にすると Knip は生成物の中の import を辿らなくなり、**生成物からしか使われていない
本体の export と依存が「未使用」として大量に湧く**（＝偽陽性）。
[IADR-0147](./IADR-0147_chunk-rule-presence-check.md) と同じ判断軸——**検出漏れは開示してよいが、偽陽性は塞ぐ**。
生成物自身の品質は生成器の責務であり、乖離は `frontend.yml` の `Codegen is up to date (orval)` が
再生成差分で検出する（[IADR-0121](./IADR-0121_spa-stack-migration-staging.md) 決定 3）。

**同じ理由で、Knip が発見できない実在の入口も `entry` に置く** ——
`lingui.config.ts`（`pnpm run i18n` が起動）・`eslint.templates.config.js`（`pnpm run lint:templates` が起動）・
`platform/frontend/public/config.js`（`index.html` が `<script src="/config.js">` で読む実行時 config）。
これらを `ignore` にすると、そこから辿れる依存が未使用として湧く（実測: `@lingui/format-po`）。

### 決定 4: CI ゲートは「0 件」の要求ではなく **baseline ラチェット**とし、**増加も減少も fail** させる（論点 C = C2）

- 床は `scripts/knip-baseline.json`（区分ごとの件数 ＋ **なぜ残っているのかの `$comment`**）。
- 判定は 3 つ: **増加**（未使用が増えた）／**減少**（片付けたのに床を締めていない）／
  **新区分**（床に無い区分が 1 件以上出た。Knip の版が上がって検出種別が増えた場合を含む）。
- **fail-closed**（[IADR-0183](./IADR-0183_false-green-warning-on-worktree-state.md)）:
  Knip の終了コードが 0 / 1 以外、JSON が読めない、`issues` 配列が無い、
  **床が 0 件でないのに走査結果が空**、床に未知の区分名がある —— いずれも **0 件で緑にせず落とす**。
- 結線は `.github/workflows/frontend.yml` の `build-test` へ 1 ステップ（`--require`）。
  **`paths:` は `src/knip.jsonc` の 1 行だけ足す**（設定を単独で変えても CI が走らないと、
  除外を広げすぎ／狭めすぎる変更を検出できない。`.prettierignore` を足したときと同じ理由）。
  **`frontend-tests.yml` は触らない**（[IADR-0209](./IADR-0209_vitest-include-subset-of-frontend-tests-paths.md)
  の `test.include` ⊆ `paths:` に `knip.jsonc` は一致せず、不変条件に影響しない）。

  > ［2026-08-16 追記 / 波 7 末クロス監査］ **「`src/knip.jsonc` の 1 行だけ足す」は誤りだった。**
  > 後継は [IADR-0214](./IADR-0214_gate-inputs-subset-of-workflow-paths.md)
  > （本決定 4 の**ラチェットの形は生きており**、IADR-0214 は起動条件だけを改める**追補**である）。
  > **床 `scripts/knip-baseline.json` と検査器本体 `scripts/check-knip.js` が `paths:` に無いため、
  > 床だけを緩める PR ではゲートが 1 度も起動しなかった。** 実測（床の `counts.exports` を 18 → 60）:
  > `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` は **EXIT=0 / 636 tests passed** で素通りし、
  > `node scripts/check-knip.js --require` だけが **EXIT=1** になる。
  > **「床側は `ci.yml` の `scripts-tests` が見る」という当時の想定も誤り**である ——
  > `scripts-tests` が走らせるのは `--self-test` で、**純関数の振る舞い・`.gitmodules` と
  > `ignoreWorkspaces` の突合・床の区分名という構造検査だけ**であり、**床と実測値の突合は行わない**。
  > `frontend-tests.yml` を触らない判断は**不変**（同ワークフローは `node scripts/*.js` のゲートを持たない）。

### 決定 5: #452 を待たずに床を打つ（論点 E = E2）。**ただし本 PR では 1 件も削らない**

先行仕様書 [`20260815_issue-768_renovate-husky.md`](../specs/20260815_issue-768_renovate-husky.md) §対象範囲は
Knip を「#452 待ち」としていた。待つ理由は「画面を作り直す前に走らせると未使用が大量に出る」ことである。
**ラチェットはその状態を据え置いたまま増加だけを止める**ため、待つ理由が消える。むしろ
**#452 の前に床を打っておかないと、#452 が新たに積む未使用を誰も測れない。**
（確定済み仕様書 #768 は書き換えない。`.claude/rules/traceability.repo.md` §Superseded の項。）

**本 PR は検出の導入であって片付けではない。** 床の 41 件は 1 件も削らない。片付けは別 issue とする。

### 決定 6: 検出しないことを明記する（本検査は網羅ではない）

- **雛形（`templates` 配下の各ユニットの frontend）を走査しない。** pnpm workspace のメンバだが
  Knip の project root（`src/`）の外にあり射程へ入らない（**実測: 雛形へ未使用 export を 1 つ足しても件数は 1 件も動かない**）。
  雛形は typecheck / lint / format / 単体テストが別途見ている。
- **バックエンド（C#）は対象外**（Knip は JS/TS のツール）。
- **どの識別子が未使用かは見ていない**（数だけの突合）。同じ区分で 1 件消えて 1 件増えると素通りする。
  識別子まで固定すると床が数百行になり保守が破綻するほうを重く見た。

## 理由

- **ラチェット**は本リポの既定の作法であり（4 つの先例）、レビュアが読み方を知っている。
- **偽陽性を塞ぐ**（決定 3）のは、偽陽性を残すと検査そのものが外されるという実測（`check-review-verdict.js` の
  docstring・`.prettierignore` 冒頭の警告）に基づく。
- **二重管理を導出で消さない**（決定 2）のは、導出ロジックが「テストされない新しいコード」になるためである。
  `src/eslint.config.js` 冒頭が同じ理由で専用の検査スクリプトを作らないと述べている。

## 結果

- 良い影響:
  - 未使用の**新規混入**が CI で止まる。SPA の段階移行で置き換えの残骸が積み上がるのを防げる。
  - 着手時点の残債 **41 件**が、理由つきで 1 箇所（`knip-baseline.json`）に可視化された。
  - `@lingui/conf` の**宣言漏れ**という実在の欠落が、導入によって初めて表面化した。
- 悪い影響・トレードオフ:
  - 除外の宣言が 4 箇所になった（決定 2。腐りは self-test が見る）。
  - 雛形とバックエンドは射程外（決定 6）。
  - 数だけの突合であり、同数の入れ替わりは検出しない（決定 6）。
- フォローアップ:
  - **#493 は本 PR では閉じない。** 残るのは **Plop.js** であり、**第 4 段（#788 = Zustand /
    TanStack Table / ECharts）に従属する** —— 雛形が生成すべき feature が第 4 段の技術を使うため、
    先に書くと計画外スタックの feature を量産する型になる。
  - 床の 41 件の**片付け**（別 issue）。とくに `@lingui/conf` の宣言追加と、
    `packages/ui` の `@vitejs/plugin-react`（直接の参照 0 件）の精査。

## 補足（後続の是正）

### ［2026-08-16 追記 / 波 7 末クロス監査］作業仕様書の母集合 軸 2 の内訳に 1 件の取り違えがあった

作業仕様書 [`20260816_issue-493_knip-unused-detection.md`](../specs/20260816_issue-493_knip-unused-detection.md)
§母集合 の 軸 2（`git grep -ril "knip"` = **10 件**）は、**件数は合うが集合が 1 件入れ替わっている。**

- 仕様書が挙げる `docs/specs/20260808_issue-562_frontend-format-gate.md` は **"knip" を含まない**
  （同ファイルの `:122` は `#491 / #493（Husky）` の行であり、**Husky のヒットを取り違えたとみられる**）。
- 実際に含むのに落ちているのは **`docs/specs/20260816_chore_unit-template-frontend-drift.md`** であり、
  そこには **#493 の中心論点そのもの**が書かれている
  （`| Knip / Plop.js | **Plop は「Feature 雛形生成」——本作業と直結する** |`）。

**再実行（走査基準 = `14704442` ＝ #493 着地の直前）:**

```console
$ git grep -ril "knip" 14704442 -- . ':!planning' ':!src/ai-stock-trading'
14704442:docs/adr/IADR-0121_spa-stack-migration-staging.md
14704442:docs/adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md
14704442:docs/adr/IADR-0203_renovate-husky-hook-scope.md
14704442:docs/specs/20260804_issue-446_spa-foundation-stack-migration.md
14704442:docs/specs/20260804_issue-490_spa-router-shell.md
14704442:docs/specs/20260804_issue-496_ui-i18n-storybook.md
14704442:docs/specs/20260815_issue-454_open-issue-stocktake-and-waves.md
14704442:docs/specs/20260815_issue-768_renovate-husky.md
14704442:docs/specs/20260816_chore_unit-template-frontend-drift.md
14704442:feedback/20260804_frontend-migration-staging-interpretation.md
$ echo "EXIT=$?"
EXIT=0
$ grep -c "knip" docs/specs/20260808_issue-562_frontend-format-gate.md   # 仕様書が挙げた側
0
```

**結論は不変である。** 入れ替わった 2 件はどちらも `status: done` の確定済み `docs/specs/` であり、
「**書き換えない**」側に立つ（`.claude/rules/traceability.repo.md` §Superseded の項）。
追随書き換え 0 件という判断も、決定 1〜6 も動かない。

**それでも記録するのは、規則 6 の目的が「次の走査が同じ判断を再現できること」だからである。**
内訳が違うと、次に同じ語で引いた人が「10 件のはずが集合が違う」で必ず止まる。
**確定済み仕様書は触らず、live な本 ADR に実測を残す**（[IADR-0171](./IADR-0171_backlink-obligation-one-way.md)：
リンクの義務は片方向であり、仕様書側へ逆リンクを張る義務は無い）。

## 関連

- Supersedes: なし
- Superseded by: なし（決定 4 の `paths:` に関する部分は
  [IADR-0214](./IADR-0214_gate-inputs-subset-of-workflow-paths.md) が**追補**として改めた。
  ラチェットの形そのものは生きているため Supersede ではない）

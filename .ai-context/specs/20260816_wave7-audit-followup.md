---
title: 作業仕様書 — 波 7 末クロス監査の是正（Knip ゲートの起動条件を機械で閉じる ＋ 追随漏れ・記述誤りの是正）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0031
  - IADR-0066
  - IADR-0082
  - IADR-0141
  - IADR-0171
  - IADR-0179
  - IADR-0183
  - IADR-0191
  - IADR-0209
  - IADR-0210
  - IADR-0211
  - IADR-0214
author: claude
created: 2026-08-16
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR 表の射程注記: メタ作業は本表の対象外)
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md (採用技術一覧: Dead Code 検出 = Knip)
  - planning:docs/ai-implementation-workflow-guide.md (フェーズ末監査は証跡必須)
related_specs:
  - "../adr/IADR-0214_gate-inputs-subset-of-workflow-paths.md"
  - "../adr/IADR-0211_knip-scope-and-unused-ratchet.md"
  - "../adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md"
  - "../adr/IADR-0210_local-k8s-observability-persistence.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "./20260816_issue-493_knip-unused-detection.md"
  - "./20260816_issue-801_frontend-tests-paths-templates.md"
  - "./20260816_issue-787_k8s-observability-persistence.md"
---

# 作業仕様書: 波 7 末クロス監査の是正

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）/ ユースケース（UC）/ 画面（SC）: **なし。**
- 非機能要件: **`NFR`（無採番）** —— 本作業は **CI の起動条件・文書統制というメタ作業**であり、
  計画側の非機能要件表（`NFR-01`〜`NFR-27`）に当たる番号が無い。
  計画 `02_requirements/01_requirements.md` の表の直前注記が
  「**本表の射程は「稼働する製品」の要件である**」「**実装側のメタ作業には当たる `NFR-xx` が無い**」と
  定めている（確定・2026-08-11 / planning#311）。
  [IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1。
  **無いことは「実装側で採番してよい」ではない**（同 決定 2）。**環流しない。**
- 関連 ADR:
  - 計画側: `ADR-0031`（SPA スタック。Knip の採用元）。**本作業では制約に触れない。**
  - 実装側（新規）: [IADR-0214](../adr/IADR-0214_gate-inputs-subset-of-workflow-paths.md)
  - 実装側（既存）: [IADR-0209](../adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md)（同族の不変条件）／
    [IADR-0211](../adr/IADR-0211_knip-scope-and-unused-ratchet.md)（Knip ゲート本体）／
    [IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md)（偽の緑は fail へ倒す）／
    [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)（母集合の引き方）／
    [IADR-0171](../adr/IADR-0171_backlink-obligation-one-way.md)（逆リンク義務は無い）／
    [IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md)（書き換え禁止の境界）
- 計画書リンク: `planning/docs/ai-implementation-workflow-guide.md`（計画リポ）
  （**フェーズ末監査は証跡（実行コマンドと出力）必須。宣言だけの監査は不合格**）

## 目的・背景

波 7（`d63c3a6e`〜`f423ca4e`）の末にフレッシュな文脈のエージェントが行ったクロス監査が 5 件を指摘した。
本作業はその 5 件を是正する。**うち 1 件は現に検査が素通りする穴**であり、残り 4 件は
「まだ嘘ではないが、次の読み手が現況を取り違える／次の走査が同じ判断を再現できない」種類である。

### 是正 1（最重要）: Knip ゲートの入力が起動条件に無い

`.github/workflows/frontend.yml` の `paths:` には `src/knip.jsonc` だけが在り、
**ゲート本体 `scripts/check-knip.js` も床 `scripts/knip-baseline.json` も無い。**
すなわち **床だけを緩める PR ではゲートが 1 度も起動しない。**

さらに同ファイルのコメントが

```
# （.prettierignore と同じ理由）。床 scripts/knip-baseline.json 側は ci.yml の scripts-tests が見る。
```

と書いているが**これは誤りである**。`scripts-tests` が走らせるのは `check-knip.js --self-test`
（**構造検査のみ**——`aggregate` / `evaluate` の純関数・`.gitmodules` と `ignoreWorkspaces` の突合・
床の区分名が既知であること）であって、**床と実測値の突合は 1 度も行わない**。
突合を行うのは `frontend.yml` の `node scripts/check-knip.js --require` **1 箇所だけ**である。

**「`paths:` の取りこぼしで検査が静かに素通りする」型は本件で 5 件目**であり
（1: #562 / 2: #558 / 3: #747 / 4: #801 / 5: 本件）、**4 件目（#801）を直したのと同じ波で作り込んだ**。
`CLAUDE.md`「同型の事故が 2 回起きたら検査器を置く」の条件をとうに超えている。

### 是正 2: `IADR-0066` への追随漏れ（規則 10）

`IADR-0066` §補足（後続の部分的見直し）は `IADR-0082` が `emptyDir` の割り切りを広げたときの追記を持つ。
`IADR-0210` は**同じ割り切りを 2 度目に広げた**（qdrant ＋ 可観測性 4 種）が、`IADR-0066` は 1 バイトも変わっていない。
**まだ嘘ではない**（「既定は `emptyDir` のまま不変」は真）が、IADR-0066 だけを読む人は
**現在永続化されている範囲を取り違える**。

### 是正 3: `IADR-0210` に無採番 `NFR` の判定根拠が無い

波 7 の 5 本のうち **#787 / IADR-0210 だけ**が「なぜ無採番 `NFR` なのか」を書いていない。
しかも本件は**最も判断が自明でない**題材である（同じ `deploy/local/observability/` を触った
先行コミット `39d6973b` は `fix(NFR-19,IADR-0168):` と**採番付き**を使っている）。

### 是正 4: `IADR-0211` の母集合 軸 2 の内訳に取り違え

確定済み `docs/specs/20260816_issue-493_knip-unused-detection.md` の 軸 2（`git grep -ril "knip"` = 10 件）は
**件数は合うが集合が 1 件入れ替わっている**。**結論は変わらない**が、規則 6 の目的
（次の走査が同じ判断を再現できること）を満たしていない。

### 是正 5: 小さな記述の誤り 3 件

1. `src/knip.jsonc` の `platform/frontend` ブロックのコメントが「**`ignore` ではなく `entry` に置く**
   （IADR-0211 決定 4）」と書くが、正しくは**決定 3**（決定 4 は baseline ラチェット）。
2. `scripts/scripts.repo.test.js` の #801 節のコメントが「fail-closed の門を **2 つ**置く」と書くが、
   実装は **3 つ**で `IADR-0209` 決定 4 も「3 つ置く」と書いている。**コメントだけが古い。**
3. `scripts/check-knip.js` の docstring に、**`src/<unit>` submodule が未 populate だと落ちる**ことが無い。
   `check-doc-links.js` / `check-commit-messages.js` が「未 populate は skip して notice」の作法を持つのと
   **あえて違う**ので、その旨を明記する。

## 対象範囲

- **対象**:
  - `.github/workflows/frontend.yml`（`paths:` の追加とコメントの是正）
  - `scripts/scripts.repo.test.js`（回帰テスト 1 節の追加 ＋ #801 節のコメント是正）
  - `scripts/check-knip.js`（docstring 1 節の追加）
  - `src/knip.jsonc`（コメントの決定番号の是正）
  - `docs/adr/IADR-0214_gate-inputs-subset-of-workflow-paths.md`（新規）
  - `docs/adr/IADR-0066` / `IADR-0210` / `IADR-0211`（日付つき追記 ＋ `updated:` の前進）
  - `docs/adr/README.md`（索引 1 行）
  - 本仕様書
- **対象外（明示）**:
  - `scripts/scripts.test.js`（**キット配布物・分類 A**。1 バイトも触らない）
  - `planning/` / `src/ai-stock-trading`（別リポジトリ）
  - `CLAUDE.md` / `.claude/rules/`（**0 バイト増**。余白は 1,070 B しかない）
  - **確定済み（`status: done`）の `docs/specs/`**（是正 4 の対象文書を含む。是正は live な ADR 側へ）
  - `frontend-tests.yml` の `paths:`（本不変条件は「そのゲートを走らせるワークフロー」を見る。
    `frontend-tests.yml` は `pnpm run test:coverage` 1 本だけで `node scripts/*.js` のゲートを持たない。
    IADR-0209 決定 1 の「対称性を検査にしない」と同じ理由）
  - **床の 41 件の片付け**（`IADR-0211` 決定 5 のとおり別 issue）

## 母集合の実測（`.claude/rules/traceability.md` 規則 1〜8 ／ `traceability.repo.md` 規則 9・10）

**走査基準は `origin/develop` = `f423ca4e`**（＝本ブランチの基点。`git status` はクリーン）。
除外は `planning/`（submodule）と `src/ai-stock-trading`（別プロジェクト submodule）の 2 つだけを
**パスで**指定する（`':!planning' ':!src/ai-stock-trading'`）。**除外理由は「別リポジトリであり本リポの
是正の射程に無い」**——中身の変更が禁じられているため、母集合に入れても是正できない。

### 規則 1: 誤りの側の文字列で引く

| # | 走査 | 件数 | 扱い |
| --- | --- | --- | --- |
| 1-1 | `git grep -n "scripts-tests が見る"` | **2 件** | `frontend.yml:28` / `:67`。**両方とも誤り。両方直す**（是正 1-2） |
| 1-2 | `git grep -n "1 行だけ足す"` | **1 件** | `IADR-0211:159`（決定 4）。本作業が**新たに誤りにする**（規則 10）。live な ADR なので日付つき追記で是正 |
| 1-3 | `git grep -n "IADR-0211 決定 4"` | **1 件** | `src/knip.jsonc:42`。**決定 3 の誤り**（是正 5-1） |
| 1-4 | `git grep -n "門\*\*を 2 つ"` | **1 件** | `scripts.repo.test.js:7701`（是正 5-2） |
| 1-5 | `git grep -n "無採番"` を `docs/adr/IADR-020[6-9]*.md` `IADR-021[01]*.md` `docs/specs/20260816_issue-787*.md` へ | **4 件**（0207 / 0208 / 0209 / 0211） | **0210 と #787 の仕様書に無い**（是正 3） |

### 規則 2 / 規則 9: 「追随する文書」を記憶で挙げず、**関係する文字列で全文書を走査してから**挙げる

| # | 走査 | 件数 | 判断 |
| --- | --- | --- | --- |
| 2-1 | `git grep -ln "check-knip"` | **9 件** | 下表で 1 件ずつ判断 |
| 2-2 | `git grep -ln "knip-baseline"` | **7 件** | 2-1 の部分集合 |
| 2-3 | `git grep -ln "IADR-0066"` | **63 件** | 大半は `deploy/local/**` の資産コメント（「経路B の出典」としての参照）。**IADR-0066 の割り切りの範囲を語っているのは ADR 本体だけ**（下表） |
| 2-4 | `git grep -ril "knip" 14704442`（**#493 着地前**の基準） | **10 件** | 是正 4 の追試。§是正 4 の実測を参照 |

`check-knip` を含む 9 件の判断:

| ファイル | 判断 |
| --- | --- |
| `.github/workflows/frontend.yml` | **直す**（是正 1。`paths:` ＋ コメント） |
| `scripts/check-knip.js` | **直す**（是正 5-3。docstring 1 節） |
| `scripts/knip-baseline.json` | 触らない（床の値も `$comment` も現況どおり正しい） |
| `scripts/scripts.repo.test.js` | **直す**（是正 1 の回帰テスト ＋ 是正 5-2） |
| `scripts/README.md` | **触らない**——検査器表は `check-knip.js` を既に 1 行持ち、**本作業は検査器を新設しない**（回帰テストは既存 companion の 1 節）。登録簿の記載は現況どおり正しい |
| `src/knip.jsonc` | **直す**（是正 5-1） |
| `src/package.json` | 触らない（`knip` の script と devDependency。現況どおり） |
| `docs/adr/IADR-0211_*.md` | **直す**（決定 4 の「1 行だけ足す」を追記で是正 ＋ 是正 4 の追記） |
| `docs/specs/20260816_issue-493_*.md` | **触らない**（`status: done`。是正は IADR-0211 側へ。`.claude/rules/traceability.repo.md` §Superseded の項） |

`IADR-0066` の割り切りの範囲を語っている文書（2-3 の 63 件からの絞り込み）:

| ファイル | 判断 |
| --- | --- |
| `docs/adr/IADR-0066_*.md` §補足 | **直す**（是正 2） |
| `docs/adr/IADR-0082_*.md` | **触らない**——`［2026-08-16 追記 / #787］` で `[[IADR-0210]]` を既に併記済み（#787 が実施） |
| `docs/adr/IADR-0210_*.md` | **直す**（是正 3。無採番 `NFR` の判定根拠） |
| `docs/specs/20260719_issue-324_*.md` / `20260816_issue-787_*.md` | **触らない**（`status: done`） |
| `deploy/local/**`（`README.md` / マニフェスト / `values-local.yaml` / `scripts/k8s-local-*.sh`） | **触らない**——「本資産の出典は IADR-0066」という参照であり、**永続化の範囲を語っていない**。#787 が既に `infra-persistence` / `observability-persistence` 側へ書いた |

### 規則 10: 本作業が**新たに誤りにする自分の記述**を引き直す

**是正前の語では捕まらない**ため、変更後の姿から引く。

1. **`IADR-0211` 決定 4 の「`paths:` は `src/knip.jsonc` の 1 行だけ足す」** —— 本作業が
   `scripts/check-knip.js` / `scripts/knip-baseline.json` / `.gitmodules` などを足すため**誤りになる**。
   → `IADR-0211` へ日付つき追記で是正する（`docs/specs/` 側は確定済みのため触らない）。
2. **`IADR-0211` 決定 4 の「`frontend-tests.yml` は触らない」** —— 本作業も触らないため**不変**。
3. **`docs/specs/20260816_issue-493_*.md` の同趣旨の記述** —— 確定済みのため触らない（規約どおり）。
4. **導出値の引き直し**: 必読規約の総量（`check-reading-budget.js`）は**走査ではなく計算し直す**。
   本作業は `CLAUDE.md` / `.claude/rules/` を 1 バイトも変えないため **50,130 バイトのまま**であることを実測で示す。

### 規則 8: 自分の記録が母集合を動かす（走査の時点）

上の全走査は**本作業の変更を 1 バイトも入れていない `f423ca4e`** に対して行った。
本仕様書自身は `docs/specs/` に置かれるため、以後 `knip` / `IADR-0066` の走査は 1 件増える。
**この仕様書は「是正の記録」であって「是正の対象」ではない。**

## 設計

決定の正本は [IADR-0214](../adr/IADR-0214_gate-inputs-subset-of-workflow-paths.md)。ここには適用形だけを書く。

### 設計 1: 不変条件は「**ゲートが読むファイル ⊆ そのゲートを走らせるワークフローの `paths:`**」

[IADR-0209](../adr/IADR-0209_vitest-include-subset-of-frontend-tests-paths.md) が置いた
「`test.include` ⊆ `frontend-tests.yml` の `paths:`」と**同じ族だが対象が違う**。
あちらは**走らせる対象**（テストファイル）を見る。こちらは**検査器が読む入力**（床・設定・検査器本体）を見る。
`#747` の検査器（`.gitmodules` の gitlink）も `#801` の検査器（`test.include`）も**本件を素通りする**。

### 設計 2: 対象ゲートも入力ファイルも**ハードコードしない**

- **ゲートの一覧はワークフローから導く** —— `frontend.yml` の `run:` に現れる
  `node scripts/<name>.js` を全部拾う（実測で 4 本: `check-knip` / `check-i18n-catalogs` /
  `check-chunk-budget` / `check-static-egress`）。
- **入力ファイルは検査器の実体から導く** —— 各検査器のソースを静的に読み、
  `path.join(...)` / `path.resolve(...)` の**リテラルと既知の定数だけで組まれた式**を解決して
  リポジトリ相対パスへ落とし、**実在するファイルだけ**を残す。基点は `__dirname`（= `scripts`）で、
  `const NAME = path.join(...)` は解決結果を記号表へ入れて後続の式から参照できるようにする。
  検査器自身のパスも常に含める（**本体を書き換える PR でゲートが起動しないのは同じ穴**）。

  実測（本設計の抽出結果）:

  | 検査器 | 導出されたリポジトリ内ファイル |
  | --- | --- |
  | `check-knip.js` | `.gitmodules` / `scripts/check-knip.js` / `scripts/knip-baseline.json` / `src/knip.jsonc` / `src/package.json` |
  | `check-chunk-budget.js` | `scripts/check-chunk-budget.js` / `scripts/chunk-budget-baseline.json` / `src/platform/frontend/vite.config.ts` |
  | `check-static-egress.js` | `scripts/check-static-egress.js` |
  | `check-i18n-catalogs.js` | `scripts/check-i18n-catalogs.js` / `src/lingui.config.ts` |

- **検出しないこと（意図的な穴。網羅ではない）**:
  - **`require()` の依存グラフは辿らない。** 辿ると `scripts/lib/ci-annotate.js` のような共有ライブラリを
    引き込むが、それらは**壊れれば例外で落ちる**ので「静かに素通りする」型ではない。
    共有ライブラリの回帰は `ci.yml` の `scripts-tests`（各検査器の `--self-test`）が見ている。
  - **実行時引数で決まる入力は見えない**（`check-static-egress.js --require <dist>` の走査先など）。
  - **変数・テンプレートリテラルで組まれたパスは解決できず、黙って落ちる**（解決できない式はスキップする）。
    だから **fail-closed の門**（設計 3）で「式を 1 件も切り出せない」形を止める。

### 設計 3: fail-closed の門を **3 つ**置く（[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md)「偽の緑」）

1. ワークフローから **`node scripts/*.js` のゲートが 0 件**しか取れない → **throw**
2. いずれかの検査器で、本文に `path.join(` / `path.resolve(` が在るのに**式を 1 件も切り出せない**
   → **throw**（括弧の対応取り・記号表が腐ったときに「0 件検査で緑」を返さない）
3. `paths:` が読めない／0 件 → **throw**（`#747` / `#801` 節と同じ扱い）

### 設計 4: **push と pull_request を別々に見る**（片側だけ足す事故を止める）

既存の `pathsOf` ヘルパ（`scripts.repo.test.js` の `#747` 節が定義し `#801` 節が使う）と
`globToRegExp` を**再利用する**（重複実装しない）。違反メッセージは
`frontend.yml: push.paths に "scripts/knip-baseline.json" が無い` の形で**どちら側かを名指しする**。

### 設計 5: `frontend.yml` の `paths:` へ足すもの（設計 2 の導出結果 − 既に在るもの）

```
.gitmodules
scripts/check-knip.js
scripts/knip-baseline.json
scripts/check-chunk-budget.js
scripts/chunk-budget-baseline.json
scripts/check-static-egress.js
scripts/check-i18n-catalogs.js
```

（`src/knip.jsonc` / `src/package.json` / `src/lingui.config.ts` /
`src/platform/frontend/vite.config.ts` は既に `paths:` に在る。）

**監査が挙げた 2 件だけを足す形は採らない。** 同じ抽出をすれば残り 3 本のゲートにも**同型の穴**が
在ることがその場で分かるためで、片方だけ塞ぐと「機械が同型を挙げているのに人が 2 件だけ直した」記録が残る。

### 設計 6: コメントの是正（是正 1-2）

誤った 1 行を、**`--self-test` が何を見て何を見ないか**を書いた形へ置き換える。
`ci.yml` の `scripts-tests` を引き合いに出すのをやめるのではなく、**その射程を正しく書く**。

### 設計 7: 是正 2〜5 は live な文書へ**日付つき追記**で行い、確定済み仕様書は触らない

`.claude/rules/traceability.repo.md` §Superseded / Deprecated な ADR を引用するときの書式に従う ——
**旧記述を消さず、後継 ID を旧記述の隣に併記し、注記そのものへ起票 ID を書き、`updated:` を前進させる。**

## 受け入れ基準

- [ ] `frontend.yml` の **`push.paths` と `pull_request.paths` の両方**に、
      設計 5 の 7 件が入っている
- [ ] `frontend.yml` の誤ったコメント（`scripts-tests が見る`）が **2 箇所とも**是正されている
- [ ] `scripts/scripts.repo.test.js` に回帰テストが 1 節あり、
      **`node scripts/scripts.test.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` の両方で通る**
- [ ] **変異試験（M1a / M1b / M1c / M2 / M3）を実走し、生の出力を本書に記録した**（宣言だけは不合格）
- [ ] `IADR-0066` §補足に `［2026-08-16 追記 / #787］` があり `[[IADR-0210]]` を旧記述の隣に併記、`updated:` 前進
- [ ] `IADR-0210` に無採番 `NFR` の判定根拠（`NFR-19` を当てなかった理由）があり、`updated:` 前進
- [ ] `IADR-0211` に 軸 2 の取り違えの追記（**生の走査出力つき**）と、決定 4 の `paths:` 記述の是正があり、`updated:` 前進
- [ ] `src/knip.jsonc` の決定番号が **決定 3** に是正されている
- [ ] `scripts.repo.test.js` #801 節のコメントが **3 つ** に是正されている
- [ ] `scripts/check-knip.js` の docstring に未 populate 時の挙動が 1 節ある
- [ ] `check-reading-budget` が **50,130 バイト（0 バイト増）**であることを実測で示した
- [ ] 全検査器が **EXIT=0**（`cmd > log 2>&1; echo "EXIT=$?"` で終了コードを別途取る）

## テスト方針

`scripts/scripts.repo.test.js` の 1 節（`ok(...)` 1 件）で不変条件を固定し、**変異試験で実効性を実測する**。

| 変異 | 変異内容 | 期待 |
| --- | --- | --- |
| **M1a** | `frontend.yml` の **push 側だけ**から `scripts/knip-baseline.json` を抜く | fail し、**`push` 側だけ**を名指しする |
| **M1b** | **pull_request 側だけ**から抜く | fail し、**`pull_request` 側だけ**を名指しする |
| **M1c** | **両方**から抜く | fail し、**両方**を名指しする |
| **M2** | `scripts/check-knip.js` を両方から抜く | fail する |
| **M3** | 抽出（`path.join` 式の切り出し）を壊す | **0 件で緑にならず throw する**（fail-closed） |

**各変異は 1 つずつ適用し、その都度 `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` を実走する。
終わったら必ず元へ戻し、`cmp` でバイト単位の復元を確認する。**

## 検証の実測

<!-- 実装後に追記する -->

## 計画書との差異

- **差異: なし。** 本作業は実装リポジトリ内の CI 起動条件と文書統制に閉じる。
  計画 `13_frontend-stack` の「Dead Code 検出 = Knip（採用）」の要求は `IADR-0211` が満たしており、
  本作業は**その要求が実際に検査されるようにする**だけである。
- **計画の誤り・不足は見つからなかった。** **計画側へ環流しない。**

## 未決事項

- なし（着手前に解消済み）。

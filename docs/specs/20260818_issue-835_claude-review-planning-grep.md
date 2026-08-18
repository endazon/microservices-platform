---
title: 作業仕様書 — AI ワークフローの `--allowedTools` へ `git -C <submodule> grep` を 3 パス分追加する（#835）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0115
  - IADR-0169
  - IADR-0183
  - IADR-0192
  - IADR-0201
author: claude
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)"
  - "../../planning/docs/ai-implementation-workflow-guide.md"
related_specs:
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
  - "../adr/IADR-0192_kit-sync-classification-and-check.md"
  - "../adr/IADR-0201_class-c-rejudgement-and-fail-closed-kit-checks.md"
  - "../adr/IADR-0169_cross-repo-ref-scan-beyond-markdown.md"
  - "../adr/IADR-0183_false-green-warning-on-worktree-state.md"
---

# 作業仕様書: `git -C <submodule> grep` を許可リストへ足す（#835）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし
- ユースケース（UC）: なし
- 画面（SC）: なし
- 起点 ID: **`NFR`（無採番）**。CI の許可リスト配線という統制のメタ作業であり、計画側の非機能要件表
  （`NFR-01`〜`NFR-27`）はすべて製品品質の要件で、この作業に当たる番号が無い。
  `.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」の**メタ作業は代表例**に該当する。
  **「番号が無い」ことは実装側で新番号を作ってよい意味ではない**（[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 2）ため、無採番 `NFR` のまま扱い、計画へは環流しない。
- 関連 ADR:
  [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（運用装備の配布点はキット）／
  [IADR-0192](../adr/IADR-0192_kit-sync-classification-and-check.md)（キット同期の分類と検査）／
  [IADR-0201](../adr/IADR-0201_class-c-rejudgement-and-fail-closed-kit-checks.md)（分類 C の再判定と fail-closed）／
  [IADR-0169](../adr/IADR-0169_cross-repo-ref-scan-beyond-markdown.md)（`.github/workflows/` は編集できる）／
  [IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md)（検査の実行順序）

## 直す問題

`.github/workflows/claude-code-review.yml` の `--allowedTools` は、前方一致の落とし穴を避けるため
`git -C <パス>` の**読み取り専用サブコマンドを個別に列挙**している。列挙は
`log` / `show` / `diff` / `ls-tree` の **4 つ**で、**`grep` が無い**。

`Bash(grep:*)` 単体は許可済みだが、Bash の許可は**コマンド文字列の前方一致**であるため
`git -C planning grep` には当たらない。計画 pin の更新 PR ではレビューが計画書 submodule を
横断検索するのが自然であり、この類型では毎回拒否が出る。拒否が 1 件でもあると
`check-permission-denials.js` がジョブを落とすため、**レビュー本体が成功していても
`claude-review` が赤になる**（#835 の事象。PR #833 で実際に発生）。

## ★ issue の記述と実際の向きが逆であること（自分で再実測した）

issue #835 §提案 は「本リポだけ直して終わりにせず、**キットへ環流する**」と書いている。
**これは向きが逆である。キットは既に直っており、追随していないのは本リポである。**

```
$ git submodule update --init planning
Submodule path 'planning': checked out '282c2d06f528ccaf204397933af2f446988454e1'

$ grep -c 'git -C planning grep' .github/workflows/claude-code-review.yml
0
$ echo "EXIT=$?"
EXIT=1

$ grep -c 'git -C planning grep' planning/tools/impl-handoff-kit/repo-template/.github/workflows/claude-code-review.example.yml
1
$ echo "EXIT=$?"
EXIT=0
```

キット側は `claude-coding.example.yml` と `.claude/settings.json` にも同じ追加を持つ。

```
$ grep -c 'git -C planning grep' planning/tools/impl-handoff-kit/repo-template/.github/workflows/claude-coding.example.yml
1
$ grep -rn 'git -C planning grep' planning/tools/impl-handoff-kit/repo-template/.claude/
planning/tools/impl-handoff-kit/repo-template/.claude/settings.json:17:      "Bash(git -C planning grep:*)",
```

よって本作業は**環流ではなく「kit → MSP の追随」**である。環流（`/plan-feedback`）は行わない。

## ★ なぜこの乖離が機械に見えなかったのか（構造的な穴。記録として残す）

`.github/workflows/claude-code-review.example.yml` は `scripts/kit-sync-classification.json` の
**トップレベルの `notApplicable`**（`classes` の下ではない）に入っている。

```
$ node -e 'const j=require("./scripts/kit-sync-classification.json");
> console.log("top-level keys:", Object.keys(j).join(", "));
> console.log(JSON.stringify(j.notApplicable,null,2));'
top-level keys: $comment, classes, notApplicable
[
  ".github/workflows/ci.example.yml",
  ".github/workflows/claude-code-review.example.yml",
  ".github/workflows/claude-coding.example.yml",
  ".github/workflows/codeql.example.yml",
  ".github/workflows/copilot-setup-steps.example.yml",
  ".github/workflows/doc-links-planning.example.yml",
  ".github/workflows/frontend-tests.example.yml",
  ".github/workflows/frontend.example.yml"
]
```

`scripts/check-kit-sync.js` の `inspect()` は `notApplicable` を **`classified` 集合に入れるだけ**で、
バイト照合（`bytesEqual`）にも実在確認（`existsInRepo` / `existsInKit`）にも一切かけない。

```
$ grep -n 'notApplicable' scripts/check-kit-sync.js
101: * @param {{classes:{A:string[],B:Object,C:string[]},notApplicable:string[]}} table 分類表
111:  const NA = table.notApplicable || [];
238:    notApplicable: over.notApplicable || ['x.example.yml'],
281:  T('notApplicable は分類済みとして扱う（unclassified を出さない）', () => {
```

`inspect()` 本体で `NA` が使われるのは 111 行の `classified` への合流ただ 1 箇所である
（`[unclassified]` を出さないためだけの登録）。

**したがって、キットと本リポのこの 8 ファイルの乖離は、機械には永久に見えない。**
`CLAUDE.md` の「**運用装備の配布点は kit に一本化する**」は、この 8 ファイルについては
**人間と AI の記憶だけが担保**であり、#835 はその担保が破れた実例である
（キットが 3 ファイルを直したのに、本リポへ 1 つも届かなかった）。

### 分類の見直しを **行わない**（その理由）

**結論: `notApplicable` を別の扱いへ移す是正は、本 PR では行わない。**
理由は「やらない方がよい」ではなく、**`check-kit-sync.js` 本体を改造しないと表現できない**からである
（本体の改造は本作業の射程外と指示されている）。実測で示す。

`notApplicable` の 8 件はすべて `*.example.yml` であり、本リポでは**有効化して実名へ改名済み**である。

```
$ ls .github/workflows/
changelog.yml  ci.yml  claude-code-review.yml  claude-coding.yml  codeql.yml
copilot-setup-steps.yml  doc-links-planning.yml  frontend-tests.yml  frontend.yml
image-mapping.yml  images.yml  openapi.yml  planning-pin-freshness.yml
pr-size.yml  pr-title.yml  security.yml

$ git ls-files '.github/workflows/*.example.yml'
（出力なし。EXIT=0）
```

`inspect()` は分類 A / B / C のキーを**キット相対パスと本リポ相対パスの同一文字列**として扱う
（`existsInRepo(f)` と `existsInKit(f)` に同じ `f` を渡す）。**改名を表現する写像が無い。**
実際に分類 A へ移すと、バイト照合が走るどころか `[missing]` で落ちる。

```
$ node -e '<inspect() を直接呼び、F を classes.A へ移した表で走らせる>'
SIMULATION: F moved to class A ->
  [missing] .github/workflows/claude-code-review.example.yml が分類表に在るが本リポに実在しない。表を追随させること
  分類 A の照合対象が 0 件だった。検査が実質何も見ていない
```

（再現コマンドの全文）

```
node -e '
const fs=require("fs");
const {inspect}=require("./scripts/check-kit-sync.js");
const F=".github/workflows/claude-code-review.example.yml";
const repoRoot=".", kitRoot="./planning/tools/impl-handoff-kit/repo-template";
const existsInRepo=(r)=>fs.existsSync(repoRoot+"/"+r);
const existsInKit=(r)=>fs.existsSync(kitRoot+"/"+r);
const bytesEqual=(r)=>existsInRepo(r)&&existsInKit(r)&&
  fs.readFileSync(repoRoot+"/"+r).equals(fs.readFileSync(kitRoot+"/"+r));
const table={classes:{A:[F],B:{},C:[]},notApplicable:[]};
inspect(table,[F],existsInRepo,existsInKit,bytesEqual).errors.forEach(e=>console.log("  "+e));'
```

**穴を塞ぐには「キット側パス → 本リポ側パス」の改名写像を `check-kit-sync.js` が持つ必要がある**
（例: `notApplicable` を配列から `{kitPath: repoPath | null}` の写像へ広げ、`repoPath` が在るものは
分類 B 相当として扱う）。これは検査器本体の設計変更であり、`--allowedTools` に `grep` を足す
本 issue と**レビュー単位が別**である（`CLAUDE.md`「人間がレビューできる変更単位を維持する」／
1 issue = 1 PR）。

**黙って素通りさせないための措置**として、本仕様書に上記の実測を残し、
**別 issue の起票を親へ申し送る**。既存 issue の有無は確認済みで、該当するものは無い
（`search_issues` で `notApplicable` / キット同期 / 改名 の語で検索。ヒット 0 件、
および無関係な closed #60 のみ）。

## 母集合（規則 9・10 で自分で引いた）

**issue 本文の「反映先」は母集合ではない。** 着手前に自分で引き直した。
走査は `git grep -In`、**拡張子で絞らず**、パス除外は `':!planning' ':!src/ai-stock-trading'` のみ。
追跡下ファイルは **1863 件**（`git ls-files -- ':!planning' ':!src/ai-stock-trading' | wc -l`）。

### 軸 1: `git -C planning`（誤りの側 = 4 サブコマンドしか列挙していない面）

ヒット 29 ファイル。うち**許可リストの実体**は 4 ファイル
（`claude-code-review.yml` / `claude-coding.yml` / `.claude/settings.json` / `scripts/check-ai-workflow-config.js`）。
残る 25 は `docs/specs/` `feedback/` `docs/adr/` `docs/how-to/` `scripts/check-permission-denials.js`
`scripts/scripts.test.js` の**記録・試験フィクスチャ**である。

### 軸 2: `ls-tree`（列挙の 4 つ目。並びの実体）

ヒット 22 ファイル。実体は軸 1 と同じ 4 ファイル。

### 軸 3: `allowedTools`

ヒット 22 ファイル。実体は上記 4 ファイル ＋ `.github/workflows/ci.yml`（無関係な文脈）。

### 軸 4: `notApplicable`

ヒット 10 ファイル。`scripts/check-kit-sync.js` / `scripts/kit-sync-classification.json` /
同 `.example.json` / `scripts/scripts.test.js` / `scripts/scripts.repo.test.js` と、記録 5 ファイル。

### 軸 5（規則 10 の先取り）: 4 つ組の列挙 `log … show … diff … ls-tree`

**是正後に「次で全部」が偽になる面**を、是正前に引いておいた。

| ファイル | 行 | 種類 |
| --- | --- | --- |
| `.github/workflows/claude-code-review.yml` | 193-194 | 【置換点】コメント「4 サブコマンド（log / show / diff / ls-tree）」 |
| `.github/workflows/claude-code-review.yml` | 266-268 | プロンプトの**正の一覧**「このレビューで実行できる Bash コマンドは**次で全部**」 |
| `.github/workflows/claude-code-review.yml` | 437 | 「submodule の**履歴**は `git -C planning log / show / diff / ls-tree` で検証できる」 |
| `.github/workflows/claude-coding.yml` | 165-166 | 【git -C の列挙】コメント「4 サブコマンド」 |
| `.github/workflows/claude-coding.yml` | 207 | `--append-system-prompt` の**正の一覧**「使える Bash コマンドは次で全部」 |
| `.claude/settings.json` | 150 | `"//"` コメント「4 サブコマンド（log / show / diff / ls-tree）で列挙する」 |
| `docs/specs/*` 3 件・`feedback/*` 1 件 | — | 過去の記録 |

### 軸 6: 文字列 `4 サブコマンド`

軸 5 の部分集合（同じ 2 ワークフロー ＋ `settings.json` ＋ 記録 4 件）。新規は出なかった。

### ★ 軸 1〜4 から出た**最重要の追随先**（規則 10 の本体）

`scripts/check-ai-workflow-config.js` の `genericBashDrift()` は、**実装用⇔レビュー用の
汎用 Bash 指定の差分を双方向に ERROR** にする。比較は**ツール指定そのもの**
（`Bash(git -C planning log:*)` の粒度）で行う。

```
$ sed -n '303,340p' scripts/check-ai-workflow-config.js
 * 比較は toolchainDrift と同じく**ツール指定そのもの**（`Bash(git -C planning log:*)`）の
 * 粒度で行う。`git -C` はパスごとに別エントリであり、コマンド名へ畳み込むと
 * submodule パスの片落ち（#163 の本体）が消えるためである。
…
  const missingInReview = [...a].filter((t) => !b.has(t));
  const missingInCoding = [...b].filter((t) => !a.has(t));
```

意図的な非対称の宣言は `CODING_ONLY_BASH`（`git add` / `git commit` / `git push` / `git switch` /
`git checkout` / `git branch` / `find` / `mkdir`）と `REVIEW_ONLY_BASH`（`gh issue view` /
`gh pr view` / `gh run list`）だけで、**`git -C … grep` はどちらにも無い**。

→ **レビュー用にだけ `grep` を足すと `check-ai-workflow-config.js` が ERROR になる。**
**両ワークフローへ同時に足すのが正しい**（キット側も両方へ足している。上の実測）。

### 引いたが除外したもの（と理由）

| 引いたもの | 除外理由 |
| --- | --- |
| `.claude/settings.json`（13-24 行のエントリ・150 行のコメント） | **本作業では編集しない。** 同ファイルは `permissions.deny` で自分自身の Edit / Write を塞いでおり（`hooks/guard-bash.js` が第 2 層）、ローカル設定の同期は #854 の領分である。**キット側は既に `Bash(git -C planning grep:*)` を持つ**（実測: kit `.claude/settings.json:17`）ため、本リポの settings.json は**なお追随債務が残る**。親へ申し送る |
| `scripts/check-ai-workflow-config.js` | 4 サブコマンドを**ハードコードしていない**。2 ワークフローを動的に突き合わせるだけで、`Bash(git -C planning log:*)` は自己試験のフィクスチャ（536-541 行）。是正で偽になる記述は無い |
| `scripts/check-permission-denials.js` | `git -C` は**拒否ラベルのトークン化**の話であり許可リストの写しではない。`grep` の追加で偽になる記述は無い |
| `scripts/scripts.test.js` / `scripts.repo.test.js` | 上記 2 検査器の companion 試験。同上 |
| `docs/specs/*`（`20260803_issue-460_…` ほか） | **確定済み（`status: done`）の作業仕様書は書き換えない**（本リポの禁止事項）。当時の実測記録として正しい |
| `feedback/*`（`20260803_ai-workflow-grep-sort-and-submodule-git-c.md` ほか） | 環流記録。凍結の射程内（`.claude/rules/traceability.repo.md`）。トリアージ結果でも自己是正でもない |
| `docs/adr/*` / `docs/how-to/*` | 過去の決定・別紙。`grep` 追加で偽になる記述は無い（`git -C planning log / show / diff / ls-tree` は**履歴**の検証手段の列挙であり、`grep` は履歴コマンドではない） |
| `.github/workflows/claude-code-review.yml:437` | 同上。**履歴**の検証手段の話であり、`grep` を足しても偽にならない。キット側も変えていない |
| `planning/` / `src/ai-stock-trading` | submodule。編集禁止 |

## 対象範囲（変更するファイルは 2 つだけ）

### 1. `.github/workflows/claude-code-review.yml`

- `--allowedTools` に **3 エントリ**を追加する。既存の並び（パスごとに `log` → `show` → `diff` →
  `ls-tree` の順で 4 連）へ、**各パスの `ls-tree` の直後に `grep` を 5 つ目**として挿す。
  - `Bash(git -C planning grep:*)`
  - `Bash(git -C src/ai-stock-trading grep:*)`
  - `Bash(git -C src/ai-stock-trading/planning grep:*)`
- 【置換点】コメント（193-194 行）を **4 → 5 サブコマンド**へ直す。
- プロンプトの**正の一覧**（266-268 行）へ `grep` を足す。文言はキット版に合わせる。

### 2. `.github/workflows/claude-coding.yml`

- `--allowedTools` に**同じ 3 エントリ**を同じ位置へ追加する（`genericBashDrift` の対称性）。
- 【git -C の列挙】コメント（165-166 行）を **4 → 5 サブコマンド**へ直す。
- `--append-system-prompt` の**正の一覧**へ `grep` を足す。

**`Bash(git -C:*)` の一括許可は採らない**（前方一致で `push` / `commit` / `reset` まで通り、
書き込み系を外した設計が崩れる）。issue #835 §提案 の禁止事項どおりである。

## やらないこと

- **issue #835 の②（`sed` / `mcp__github__list_issues` の拒否）は許可リストで解決しない。**
  プロンプト側の遵守の問題であり、issue の判断どおり**記録に留める（1 回目）**。
  同型が再発したらプロンプト側を狭める（`CLAUDE.md`「検査器・規約の追加は同型の事故が 2 回起きたら」）。
- **`scripts/check-kit-sync.js` 本体の改造**（改名写像の導入）。射程外。上記のとおり別 issue を申し送る。
- **`.claude/settings.json` の追随**。#854 の領分。申し送る。
- **キットへの環流**。向きが逆であり、キットは既に正しい。

## 受け入れ基準

1. `.github/workflows/claude-code-review.yml` の `--allowedTools` が
   `git -C {planning, src/ai-stock-trading, src/ai-stock-trading/planning} grep` を**3 件**含む。
2. `.github/workflows/claude-coding.yml` も**同じ 3 件**を含む。
3. `node scripts/check-ai-workflow-config.js` が **ERROR 0**（`genericBashDrift` の対称性が保たれている）。
   **ただし `STRICT_AI_WORKFLOW_CONFIG=1`（CI と同条件）は warn 2 件で exit 1 になる** —— 3 系統の
   3 つ目（`.claude/settings.json`）が本作業の権限外で未追随のためである。§阻害要因 を見ること。
4. `--allowedTools` は**引用符でくくった 1 引数・カンマ区切り**のままであり、空白で割れていない。
5. **起動条件（`on:` / `paths:`）と job 名（＝必須チェック名）が変わっていない。**
6. `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が全 pass（`KIT_DIR` の skip 迂回をしない）。
7. `node scripts/check-kit-sync.js` が pass（submodule を populate した状態で走らせる）。

## 検証（[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md) の順序）

`git add -A` → 検査器 → コミット → HEAD を読む検査器。結果は §検証結果 に記す。

## ★ 未解決の阻害要因: 3 系統の 3 つ目（`.claude/settings.json`）が未追随である

**本作業では `.claude/settings.json` を編集していない**（同ファイルは自分自身の Edit / Write を
`permissions.deny` で塞いでおり、ローカル設定の同期は #854 の領分である）。

```
$ node -e 'const j=require("./.claude/settings.json");
> ((j.permissions||{}).deny||[]).filter(x=>/settings\.json/.test(x)).forEach(x=>console.log(x));'
Edit(./.claude/settings.json)
Write(./.claude/settings.json)
```

**その結果、`ci` ジョブの `ai-workflow-config` ステップが失敗する。** 同ステップは
`STRICT_AI_WORKFLOW_CONFIG: "1"` を渡しており（`.github/workflows/ci.yml` の 217-220 行）、
**warn を失敗として扱う**。実測:

```
$ STRICT_AI_WORKFLOW_CONFIG=1 node scripts/check-ai-workflow-config.js; echo "EXIT=$?"
AI ワークフロー設定チェック: 2 件を検査
  warn  claude-code-review.yml: settings.json の allow に無いツールを CI で許可している: Bash(git -C planning grep:*), Bash(git -C src/ai-stock-trading grep:*), Bash(git -C src/ai-stock-trading/planning grep:*)（ローカルと CI で挙動が変わる。3 系統を揃えること）
  warn  claude-coding.yml: settings.json の allow に無いツールを CI で許可している: Bash(git -C planning grep:*), Bash(git -C src/ai-stock-trading grep:*), Bash(git -C src/ai-stock-trading/planning grep:*)（ローカルと CI で挙動が変わる。3 系統を揃えること）

✗ 検査が成立していない警告が 2 件ある（STRICT_AI_WORKFLOW_CONFIG=1）
EXIT=1
```

**これは検査器が正しく働いている姿である**（`settings.json` のコメントが言う「3 系統を手作業で
同期する構造」を機械が守っている）。**塞ぐには `.claude/settings.json` の `permissions.allow` へ
次の 3 行を、各パスの `ls-tree` の直後に足すだけでよい。**

```
      "Bash(git -C planning grep:*)",
      "Bash(git -C src/ai-stock-trading grep:*)",
      "Bash(git -C src/ai-stock-trading/planning grep:*)",
```

**これはキットへの追随として正当である。** キット側は既に `Bash(git -C planning grep:*)` を持ち
（実測: キットの `.claude/settings.json` 17 行）、同ファイルは分類 **B**（固有デルタの種 1 =
リポジトリ構成。`src/ai-stock-trading` submodule 向けの許可コマンド）であるため、
**2 パス分を足すことも既存デルタの範囲内**である。

```
$ node -e 'const j=require("./scripts/kit-sync-classification.json");
> console.log("in B:", (j.classes.B||{})[".claude/settings.json"]);'
in B: 1. リポジトリ構成（src/ai-stock-trading submodule 向けの許可コマンド）
```

**あわせて `settings.json` の `"//"` コメント（150 行）の「4 サブコマンド（log / show / diff /
ls-tree）」も 5 へ直す必要がある**（規則 10 で引いた面。本作業の是正で偽になる記述の唯一の残りである）。

**判断**: 本作業の権限外であるため実装せず、**親へ申し送る**。

## 検証結果

すべて `git add -A` の後に実行した（[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md) の順序）。
`planning` submodule は `git submodule update --init planning`（`282c2d06`）で populate 済みである
（未 populate だと `check-kit-sync.js` が隣接クローンへフォールバックし偽の `[drift]` を出す）。

| 検査器 | EXIT | 判定行 |
| --- | --- | --- |
| `check-doc-links.js` | 0 | `OK: 699 件の Markdown に破損した相対リンクはありません` |
| `check-doc-status-vocabulary.js` | 0 | `OK: 658 件の仕様書の status が値域に収まっています` |
| `check-doc-type-vocabulary.js` | 0 | `OK: 672 件の文書の type が、テンプレート 19 種類の値域に収まっています` |
| `check-cross-repo-refs.js` | 0 | `走査 1791 件 / 除外 73 件` ＋ `OK: 1791 件に他リポジトリ参照の表記違反はありません` |
| `check-plan-id-qualification.js` | 0 | `OK: 1454 件に他プロジェクト ID の修飾違反はありません` |
| `check-adr-numbering.js` | 0 | `OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です` |
| `check-reading-budget.js` | 0 | `warn Claude Code: 49,885 バイト（予算 51,200 の 97.4%）`（**本作業前と同値。`CLAUDE.md` / `.claude/rules/` を増やしていない**） |
| `check-kit-sync.js` | 0 | `OK: キット 117 件を分類表と突合しました（A 81 件はバイト一致 / B 24 件は固有デルタ / C 4 件は同期しない / 対象外 8 件）` |
| `check-ai-workflow-config.js`（素） | 0 | `✓ AI ワークフローのツール許可設定に問題なし`（ERROR 0。warn 2 件は上記 §阻害要因） |
| `check-ai-workflow-config.js`（`STRICT_AI_WORKFLOW_CONFIG=1`。**CI と同条件**） | **1** | `✗ 検査が成立していない警告が 2 件ある` —— **上記 §阻害要因** |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 0 | `✓ 651 tests passed` |

`check-kit-sync.js` の「**対象外 8 件**」が、上で述べた `notApplicable` の穴の実測値である
（本作業で直した 2 ファイルの元になる `*.example.yml` は、この 8 件に含まれ照合されていない）。

### テスト件数（develop 時点との比較。自分で測った）

| 時点 | 件数 |
| --- | --- |
| develop `3ad5ad15`（2 ワークフローを `git checkout HEAD --` で戻して実走） | **651 tests passed** |
| 本変更後 | **651 tests passed** |

**増減なし。** 本作業は検査器・規約を追加していない（許可リストの配線変更のみ）ため、新規テストは
追加していない。既存の `check-ai-workflow-config` の `genericBashDrift` と `parityWarnings` が、
そのままこの変更の回帰検査になっている（後者は現に上記 §阻害要因を検出した）。

### 起動条件・必須チェック名が変わっていないことの実測

```
$ for f in .github/workflows/claude-code-review.yml .github/workflows/claude-coding.yml; do
>   diff <(git show HEAD:$f | sed -n '/^on:/,/^jobs:/p') <(sed -n '/^on:/,/^jobs:/p' $f) \
>     && echo "  on: ブロック 差分なし"
> done
  on: ブロック 差分なし     ← claude-code-review.yml
  on: ブロック 差分なし     ← claude-coding.yml
```

| ファイル | `name:`（旧 → 新） | job キー（旧 → 新） |
| --- | --- | --- |
| `claude-code-review.yml` | `Claude Code Review` → 同じ | `claude-review` → 同じ |
| `claude-coding.yml` | `Claude Coding` → 同じ | `claude` → 同じ |

`--allowedTools` の記法（**引用符でくくった 1 引数・カンマ区切り**）も保たれている。

```
  claude-code-review.yml: 先頭/末尾が二重引用符 true / 内側の二重引用符 0 個 / エントリ数 60
  claude-coding.yml:      先頭/末尾が二重引用符 true / 内側の二重引用符 0 個 / エントリ数 66
```

### 規則 10 の引き直し（是正後の語で走査した）

```
$ git grep -In '5 サブコマンド' -- ':!planning' ':!src/ai-stock-trading'
.github/workflows/claude-code-review.yml:193
.github/workflows/claude-coding.yml:165

$ git grep -In 'git -C [a-z/-]* grep' -- ':!planning' ':!src/ai-stock-trading'
.github/workflows/claude-code-review.yml:244
.github/workflows/claude-coding.yml:209
docs/how-to/plan-id-range-history-annex.md:59
docs/specs/20260816_issue-790_planning-pin-8cae89d-and-kit-rejudgement.md:84
docs/specs/20260817_planning-pin-767a9d48.md:55

$ git grep -In '4 サブコマンド' -- ':!planning' ':!src/ai-stock-trading'
.claude/settings.json:150
docs/specs/20260803_issue-460_ai-review-permission-denials.md:31
docs/specs/20260804_kit-sync-round14-interim-delta-removal.md:61
feedback/20260803_ai-workflow-grep-sort-and-submodule-git-c.md:57
feedback/20260803_ai-workflow-grep-sort-and-submodule-git-c.md:112
```

- 後 2 者の `docs/how-to/` / `docs/specs/` のヒットは、**過去の作業で実際に `git -C planning grep`
  を走らせた記録**であって許可リストの写しではない。追随の対象ではない。
- `4 サブコマンド` が残る 5 箇所のうち、**live な権威文書は `.claude/settings.json:150` の 1 つだけ**
  である（残る 4 つは確定済み仕様書と環流記録＝凍結）。**その 1 つが上記 §阻害要因の対象**であり、
  本作業では触れない。

## ★ `.claude/settings.json` の追随（利用者の許可を得て実施）

**着手時、本ファイルは編集対象外だった** —— 同ファイル自身が `permissions.deny` に
`Edit(./.claude/settings.json)` / `Write(./.claude/settings.json)` を持つためである。

**しかし触らないと CI が赤のままだった。** `.github/workflows/ci.yml:219` が
`STRICT_AI_WORKFLOW_CONFIG: "1"` を渡しており、`check-ai-workflow-config.js` の
`parityWarnings`（3 系統の乖離）が **warn ではなく失敗**になる。

```console
$ STRICT_AI_WORKFLOW_CONFIG=1 node scripts/check-ai-workflow-config.js; echo "EXIT=$?"
  warn  claude-code-review.yml: settings.json の allow に無いツールを CI で許可している:
        Bash(git -C planning grep:*), Bash(git -C src/ai-stock-trading grep:*),
        Bash(git -C src/ai-stock-trading/planning grep:*)
  warn  claude-coding.yml: （同上）
✗ 検査が成立していない警告が 2 件ある（STRICT_AI_WORKFLOW_CONFIG=1）
EXIT=1
```

**変更内容が「AI 自身の許可リストを広げる」ものであるため、独断で行わず利用者に諮り、
許可を得てから実施した。** deny を迂回する形（Bash での書き換え）を無断で採らない。

追加したのは 3 行で、**各パスの `ls-tree` の直後**（キット・両ワークフローと同じ位置）:

```json
"Bash(git -C planning grep:*)",
"Bash(git -C src/ai-stock-trading grep:*)",
"Bash(git -C src/ai-stock-trading/planning grep:*)",
```

あわせて同ファイルの `"//"` コメントの
**「4 サブコマンド（log / show / diff / ls-tree）で列挙する」→「5 サブコマンド（… / grep）」**
を追随させた（規則 10。是正後の語で引き直して見つけた）。

**キット追随として正当である** —— キットの `.claude/settings.json:17` は既に
`Bash(git -C planning grep:*)` を持ち、同ファイルは分類 **B**（`1. リポジトリ構成`）なので
submodule 2 パス分の追加も既存デルタの範囲内にある。

```console
$ STRICT_AI_WORKFLOW_CONFIG=1 node scripts/check-ai-workflow-config.js; echo "EXIT=$?"
✓ AI ワークフローのツール許可設定に問題なし
EXIT=0
```

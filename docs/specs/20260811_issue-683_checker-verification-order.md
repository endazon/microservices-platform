---
title: 作業仕様書 — 検査器が「偽の緑」を返す条件を警告する（#683）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0141
  - IADR-0143
  - IADR-0183
author: claude
created: 2026-08-11
updated: 2026-08-11
plan_refs:
  - "../../planning/docs/ai-implementation-workflow-guide.md (§6 検査器・規約の追加は同型の事故が 2 回起きたら)"
related_specs:
  - "../adr/IADR-0183_false-green-warning-on-worktree-state.md"
  - "./20260811_issue-707_feedback-dispatch-backlog.md"
---

# 作業仕様書: 検査器の「偽の緑」を警告する（#683）

## 起点

- **NFR**（文書統制・運用保守。**メタ作業なので無採番**。[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）
- 起点 issue: **#683**（`priority:should` / `type:chore`）。実装 ADR: **[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md)**

> **★ 値の基準時点は develop `846d101`（2026-08-11 実測）である。**

## ★★ 母集合 —— 3 軸で引いた

### ★★ 軸 a: **#683 の母集合は 1 クラスしか挙げていない。実際は 2 クラスある**

`scripts/` の**検査器 32 本を全数**で分類した（何を走査対象として読むか）。

| クラス | 見えないもの | 該当 | 件数 |
| --- | --- | --- | ---: |
| **A. HEAD（コミット済み）を読む** | **未コミットの変更** | `check-doc-updated.js` / `check-landed-subjects.js` | **2** |
| **B. 追跡下のみ（`git ls-files`）** | **untracked の新規ファイル** | `check-cross-repo-refs.js` / `check-plan-id-qualification.js` / `check-action-versions.js` / `check-test-spec-coverage.js` | **4** |
| C. 作業ツリーを読む | （順序に依存しない） | 残り | **26** |

**#683 が挙げたのはクラス A の 1 本（`check-doc-updated.js`）だけ**である。
「残りの 30 本が作業ツリーを読むのか HEAD を読むのかは未確認。この issue で全数を引く」と自ら書いており、**引いた結果が上表**である。

### ★★ 軸 b: **クラス B は本セッションで実際に踏んだ**

**#707（PR #708）で、新規作成した作業仕様書に「列挙形の修飾漏れ」が 3 箇所あった。**
**`git add` 前に検査器を全数走らせて OK を得ており、`check-cross-repo-refs.js` は最後まで緑だった。**
**気づけたのは `check-commit-messages.js` がコミット本文の同じ形を検出したから**である。

**これは #683 が挙げた事故（PR #682 の `check-doc-updated.js`）と原因は違うが、症状も被害も同じ**である。

> **★ しかも本リポは、この型を planning#316 として**同じ日に環流したばかり**だった** ——
> 「走査対象を `git ls-files` から引く検査器は、**新設直後は untracked なのでローカルでは見えず、
> コミット後の CI で初発火する**」（`IADR-0143` 決定 4）。**環流した当人が踏んだ。**

### 軸 c: **検証順序を書く場所は 1 つに決まる**

| 候補 | 現状 |
| --- | --- |
| **`docs/DEFINITION_OF_DONE.md`** | **§品質・検証**を持ち、`/verify` の実行を項目化している。**完了判定の正本** |
| `docs/ai-workflow.md` | 「完了の定義 = `DEFINITION_OF_DONE.md` ＋ `/verify`」と**DoD を指している** |

**DoD が正本であり、`ai-workflow.md` は既にそこを指している。** **DoD にだけ書く**（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 参照点を 1 つに畳む）。

## 判断

### ★★ 判断 1: **失敗させない。警告に留める**

**#683 の但し書きをそのまま採る** ——
「**未コミットで走らせること自体は正当な使い方**（書きかけの確認）であり、**落とすと検査器が邪魔者になって外される**」。

**`scripts/lib/ci-annotate.js` の `warn` を使う**（18 本が既に利用している既存の導線。新しい仕組みを作らない）。
**終了コードは変えない。**

### ★ 判断 2: **クラスごとに「何が見えないか」を出し分ける**

**同じ文言にしない。** 読み手が採るべき行動が違う。

| クラス | 警告の条件 | 促す行動 |
| --- | --- | --- |
| **A** | **未コミットの変更が 1 件以上ある** | **コミットしてから再実行** |
| **B** | **untracked のファイルが 1 件以上ある** | **`git add` してから再実行** |

**クラス C には何も足さない** —— 作業ツリーを読むので、順序で結果が変わらない。**足すと嘘になる。**

### ★ 判断 3: **CI では警告を出さない**

CI は**クリーンなチェックアウト**で走るので該当が 0 件になり、自然に無音になる。
**明示的な環境分岐は書かない**（`GITHUB_ACTIONS` を見ない）——
**条件が実際に成り立たないだけ**であり、分岐を足すと**条件と分岐が二重管理**になる。

> **★ ただしレビュー環境では鳴りうる。** レビュー用の実行環境は `.claude/` と `CLAUDE.md` を
> develop へ復元するため、**未コミットの変更が常に存在する**。**警告が出るのは正しい挙動**である
> （その環境の結果は実 CI と一致しない、というのが事実だからである）。

### 判断 4: **`check-planning-pin-freshness.js` は射程外**

git を使うが**軸が違う** —— **planning submodule の pin と planning の HEAD** を比べるものであり、
**本リポの「作業ツリー vs HEAD」ではない**。**未コミットの有無で結果は変わらない。**

### ★ 判断 5: **`paths:` 由来の偽陽性は作らない**

**クラス B の警告は「untracked が 1 件でもあれば鳴る」**という粗い条件にする。
**検査器ごとの走査範囲と突き合わせて絞り込まない。**

**理由**: 走査範囲は検査器ごとに違い（`:!planning` / `:!src/ai-stock-trading` など）、
**範囲の複製は必ず腐る**（[IADR-0169](../adr/IADR-0169_cross-repo-ref-scan-beyond-markdown.md) 決定 2 が
「名指しの除外リストは腐る」として退けたのと同じ理由）。
**粗くて鳴りすぎる方が、静かに見落とすより安い** —— **警告であって失敗ではない**ため実害が無い。

### ★★ 判断 6: **注入が到達可能かを、静的判定ではなく実挙動で測る**

**初回実装では 6 本のうち 3 本が dead code だった** ——
`--self-test` ブロックが `return;` で終わっており、その**内側**へ挿していたためである。
**「到達可能か」を静的に見積もったヒューリスティックは、6 本すべて到達可能と誤答した。**

| 検査器 | 初回の注入位置 | 実挙動 |
| --- | --- | --- |
| `check-cross-repo-refs.js` / `check-plan-id-qualification.js` / `check-landed-subjects.js` | 自己試験ブロックの**内側** | **無音（dead code）** |
| `check-doc-updated.js` / `check-test-spec-coverage.js` / `check-action-versions.js` | ブロックの外 | 鳴る |

**是正**: 3 本の呼び出しをブロックの**外**へ移し、**untracked のファイルを実際に作って 6 本を 1 本ずつ観測**した（下記）。
**回帰テストも同じやり方で測る**（静的な位置検査にしない）。

> **★ 規約にはしない。** 「同型の事故が 2 回起きたら」の**1 回目**である。**IADR-0183 決定 7 に記録するに留める。**

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#683） | 確かめ方 |
| --- | --- | --- |
| 1 | 差分ベースの検査器を全数で列挙した | 軸 a ／ **回帰テスト①**（A・B の該当を**ファイル名の集合**で固定） |
| 2 | 未コミットがあるとき警告する（失敗させない） | **回帰テスト②**（untracked を実際に作り、6 本が鳴る・**exit code が基準と一致**） |
| 3 | 検証順序を **1 箇所だけ**に書く | **回帰テスト⑤**（DoD に在り、`ai-workflow.md` に重複が無い） |
| 4 | **クラス B（untracked）も対象** | **回帰テスト①②**（#683 の母集合の拡張） |
| 5 | クラス C へ足していない | **回帰テスト①**（`worktree-state` を参照するクラス C が 0 件） |
| 6 | **注入が到達可能である** | **回帰テスト②**（判断 6。静的判定は誤答したので実挙動で測る） |
| 7 | `check-doc-links.js` ほかが緑 | 検証 |

### 変異試験（**7 件すべて検出**）

| # | 変異 | 検出 |
| ---: | --- | --- |
| 1 | `check-cross-repo-refs.js` から警告呼び出しを削除 | ✓ |
| 2 | `check-action-versions.js` の `MODE` を `TRACKED` → `HEAD` へ取り違え | ✓ |
| 3 | DoD から「検証の順序」節を削除 | ✓ |
| 4 | `docs/ai-workflow.md` へ順序を重複させる | ✓ |
| 5 | `MODE.TRACKED` 分岐を無条件で無警告にする | ✓ |
| 6 | クラス A の促し文言（「コミットしてから再実行」）を消す | ✓ |
| 7 | クラス C の検査器（`check-adr-numbering.js`）へ順序警告を混入させる | ✓ |

## 着地の実測

**commit 基準: develop `846d101` からの差分。**

### 実挙動（**untracked のファイルを作って 6 本を 1 本ずつ観測**）

| 検査器 | クラス | 警告 | 終了コード |
| --- | --- | --- | --- |
| `check-doc-updated.js` | A | **鳴る** | **不変** |
| `check-landed-subjects.js` | A | **鳴る** | **不変** |
| `check-cross-repo-refs.js` | B | **鳴る** | **不変** |
| `check-plan-id-qualification.js` | B | **鳴る** | **不変** |
| `check-test-spec-coverage.js` | B | **鳴る** | **不変** |
| `check-action-versions.js` | B | **鳴る** | **不変** |

`scripts/scripts.test.js` は **472 件すべて緑**（新規 5 件を含む）。

### 変更の全数と実効（[IADR-0178](../adr/IADR-0178_claude-md-defers-to-docs-readme.md) 決定 6）

| # | 変更 | 旧 | 新 | 実効 |
| ---: | --- | ---: | ---: | ---: |
| 1 | `scripts/lib/worktree-state.js`（新規） | 0 | 6,511 | **＋6,511** |
| 2 | `scripts/check-doc-updated.js`（クラス A） | 10,912 | 11,185 | **＋273** |
| 3 | `scripts/check-landed-subjects.js`（クラス A） | 20,591 | 20,868 | **＋277** |
| 4 | `scripts/check-cross-repo-refs.js`（クラス B） | 44,198 | 44,478 | **＋280** |
| 5 | `scripts/check-plan-id-qualification.js`（クラス B） | 19,641 | 19,927 | **＋286** |
| 6 | `scripts/check-test-spec-coverage.js`（クラス B） | 41,863 | 42,145 | **＋282** |
| 7 | `scripts/check-action-versions.js`（クラス B） | 25,263 | 25,542 | **＋279** |
| 8 | `scripts/scripts.repo.test.js`（回帰テスト 5 件） | 300,988 | 307,822 | **＋6,834** |
| 9 | `docs/DEFINITION_OF_DONE.md`（検証の順序） | 4,769 | 6,170 | **＋1,401** |
| 10 | `docs/adr/IADR-0183_...md`（新規） | 0 | 7,266 | **＋7,266** |
| 11 | `docs/adr/README.md`（索引 1 行） | 216,744 | 217,172 | **＋428** |
| 12 | `docs/specs/20260811_issue-683_...md`（本書・新規） | 0 | 11,981 | **＋11,981** |
| | **計** | | | **＋36,098** |

> **★ 索引 1 行は 154 字**（上限 200 字）。**縮め直しは発生していない** —— 前 2 PR が `title-too-long` に
> 計 3 回当たったのを受け、**書く前に測ってから置いた。**

> **★ 必読 2 ファイルを 1 バイトも触っていない。必読合計 49,845B のまま**（50,000 まで 155B）。

## 射程外

- **検査器そのものの判定式の変更** —— #683 の射程外宣言に従う
- **CI 側の変更** —— CI は既にコミット済みの HEAD を見ており正しい
- **`check-planning-pin-freshness.js`** —— 軸が違う（判断 4）
- **クラス C の 26 本** —— 順序に依存しない（判断 2）

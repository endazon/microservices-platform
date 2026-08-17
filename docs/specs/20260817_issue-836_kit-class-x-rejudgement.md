---
title: 作業仕様書 — 計画 pin 767a9d48 でキット側の是正 3 件が着地したため、分類 X 4 件を再判定する（#836）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0115
  - IADR-0130
  - IADR-0169
  - IADR-0183
  - IADR-0192
  - IADR-0201
  - IADR-0204
  - IADR-0207
  - IADR-0208
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/tools/impl-handoff-kit/HOWTO.md (§B-5 キット版を採る前に実走して差を確かめる)"
  - "../../planning/tools/impl-handoff-kit/repo-template/scripts/kit-sync-classification.example.json"
  - "../../planning/docs/ai-implementation-workflow-guide.md (§8 必読規約の予算)"
related_specs:
  - "../adr/IADR-0204_kit-catchup-deferral-with-expiry-ratchet.md"
  - "../adr/IADR-0207_pr-title-trailing-number-must-be-own.md"
  - "20260816_issue-790_planning-pin-8cae89d-and-kit-rejudgement.md"
  - "20260816_issue-799_pr-title-number-match.md"
---

# 作業仕様書: 分類 X 4 件の再判定（#836）

> 起点 ID は **NFR**（無採番）。キット追随・文書統制は**メタ作業**であり、計画側の非機能要件表
> `NFR-01`〜`NFR-27` は稼働する製品の要件なので当たる番号が無い
> （`.claude/rules/traceability.md`「無採番 `NFR` を許す 2 つの場合」の**場合 2**。[[IADR-0179]] 決定 2）。
> **環流しない。**

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（メタ作業）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: [[IADR-0115]] 決定 2（分類 A/B/C）／[[IADR-0192]] 決定 2（X の定義）／
  **[[IADR-0204]] 決定 2（分類 X 再判定の判定基準。本作業の判定基準そのもの）**／
  [[IADR-0183]]（`lib/worktree-state.js` の結線）／[[IADR-0130]]（0 件走査の門）／
  [[IADR-0207]]（PR タイトル末尾の `(#NNN)`）
- 計画書リンク: [`planning/tools/impl-handoff-kit/HOWTO.md`](../../planning/tools/impl-handoff-kit/HOWTO.md)

## 目的・背景

計画 pin `767a9d48` で、本リポからの環流 3 件がキットに受理された。

| 受理された環流 | 内容 |
| --- | --- |
| planning#380 | `scripts.test.js` の「拡張点を持たない構成」断定を、両方向を固定する 2 試験へ分割 |
| planning#379 | `check-cross-repo-refs.js` へ **0 件走査の門**（fail-closed）を追加 |
| #799 | `check-commit-messages.js` / `pr-title.yml` / `traceability.md` へ「PR タイトル末尾の `(#NNN)` が PR 自身の番号かの検査」を追加 |

**環流が着地すると、その環流を理由に置いていた分類 X の根拠が消える。** 消えた根拠を放置すると、
分類表は「現実から離れた記録」になり、`check-kit-sync.js` は見ているつもりで何も見なくなる。

## 対象範囲

- **対象**: `scripts/check-cross-repo-refs.js` / `scripts/scripts.test.js` /
  `.github/workflows/pr-title.yml` / `scripts/check-commit-messages.js`（理由欄のみ）／
  `scripts/kit-sync-classification.json` / `scripts/scripts.repo.test.js`（コメント）／
  `scripts/README.md` / `docs/how-to/session-handoff.md`
- **対象外**: `planning/`（submodule）／`src/`／`.claude/rules/`・`CLAUDE.md`（必読規約の余白が無い）／
  確定済み（`status: done`）の `docs/specs/`

## ★ 判定基準 —— [[IADR-0204]] 決定 2 の 3 点突合

決定 2 は「**検出力が同値であっても、fail の向き（門）が違えばキット版へ戻さない**」と定める。
突合するのは次の 3 点である。

1. 違反入力に対する検出結果
2. 入力が空・読めない・設定が未充填のときの**終了コード**（fail-closed / fail-open の向き）
3. **本リポにしか無いモジュールへの結線**（`lib/worktree-state.js` 等）

### 突合 1: `scripts/check-cross-repo-refs.js`

同一の違反フィクスチャ（型 1〜4 ＋ `〔〕` 区切りの計 5 件）を、本リポ版とキット版
（`767a9d48`）へ食わせる。キット版は置換点がプレースホルダのままなので、
**環境変数で本リポと同じ設定を注入**して条件を揃える。

```
$ CROSS_REPO_NAMES='project-planning:planning,ai-stock-trading:AST' \
  CROSS_REPO_SELF_NAMES='MSP,microservices-platform' CROSS_REPO_OWNERS='endazon' \
  node <版>/scripts/check-cross-repo-refs.js <版>/fixture.md
```

| 点 | 本リポ版 | キット版 `767a9d48` | 判定 |
| --- | --- | --- | --- |
| **1 検出結果** | EXIT=1・**5 件**（長い表記 / 列挙形 / 空白区切り / owner 誤り / `〔〕`）。提案文字列まで一致 | EXIT=1・**5 件**。同文 | **同値** |
| **2a 0 件走査**（git 済み・追跡 0 件） | **EXIT=1**「0 件検査は…fail させています」 | **EXIT=1** 同文 | **同値**（planning#379 が着地した） |
| **2b git 非管理下** | **EXIT=0**「git ls-files を実行できないため走査をスキップした」 | **EXIT=0** 同文 | **同値**（fail-open のまま） |
| **2c 置換点 未充填** | 該当なし（6 つとも埋めている） | `KNOWN_OWNERS` 未設定の notice を出し型 4 を検査しない | 本リポは充填済み |
| **3 固有モジュール結線** | `warnIfResultMayDifferFromCi` の warn 行が出る | **出ない**（キットは `lib/worktree-state.js` を配らない） | **差あり（種 3）** |

→ **結論: X を外す。** X の理由だった「キット版は 0 件走査の門を持たない」は planning#379 の着地で
**偽になった**。残る差は **種 5（置換点 6 つの充填）＋ 種 3（`lib/worktree-state.js` への結線）**
だけであり、いずれも [[IADR-0115]] 決定 2 の種に当たる。**恒久的に正しいデルタを X に置かない**
（分類表 `$comment`）。

**ただしキット原文で上書きはしない** —— 上書きすると種 3 の結線と置換点の充填が消える。
土台がキットと同型になったことを、**キット版の門試験（`scripts.test.js`）が本リポ版に対して
通ること**で確かめる。そのために本リポ版へ次の 3 点だけを施す。

- `lib/worktree-state.js` の require を **`MODULE_NOT_FOUND` だけを握る try/catch で遅延化**する。
  キット版の門試験は**検査器 1 ファイルだけを一時ディレクトリへコピーして子プロセス実行する**ため、
  `lib/` が存在しない。**他のエラーは握り潰さない**（lib 側の構文エラーを飲むと結線が黙って切れる）。
- **`EXCLUDED_DIRS` を export へ足す**（キット版が持つ。門試験が「ディレクトリ 1 本の規則」を見る）。
- **`trackedMarkdown` の export を外す**（キット版が外した。門試験が `undefined` を固定する）。
- あわせて、**キット版と同型になった箇所のコメントをキット原文の文言へ揃える**
  （0 件走査の門の説明・`module.exports` の 2 つの注記）。従来のコメントは
  「キット版はこの門を持ち込んでいない」と書いており、**着地によって偽になった**（規則 10）。

### 突合 2: `scripts/scripts.test.js`

配布物の**テスト**であり検査器ではないため、点 2・点 3 は「本リポの構成で走るか」に読み替える。

| 点 | 判定 |
| --- | --- |
| 1 検出結果 | キット原文の新試験は、`loadExistingPlanIds()` が **Set を返す側**（＝本リポ）と **null を返す側**の両方向を固定する形へ分割された。**拡張点を埋めた本リポで通る** |
| 2 終了コード | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が exit 0（companion 込み） |
| 3 固有モジュール結線 | キット原文の門試験が `check-cross-repo-refs.js` を**単体でコピーして子プロセス実行**するため、`lib/worktree-state.js` への結線がそのままだと `MODULE_NOT_FOUND` で落ちる。→ **突合 1 の遅延化で解消**（結線自体は残す） |

→ **結論: X → A（キットとバイト一致）。** 固有デルタは 0 になる。

### 突合 3: `.github/workflows/pr-title.yml`

| 点 | 判定 |
| --- | --- |
| 1 検出結果 | **非コメント行の diff が空**（`grep -v '^\s*#'` ＋ 空行除去で突合）。起動条件 `types:`・ジョブ ID `pr-title`・`PR_TITLE` / `PR_AUTHOR` / `PR_NUMBER` の受け渡しはすべて同一 |
| 2 終了コード | ワークフローは判定を持たず、判定は `check-commit-messages.js` にある（変更なし） |
| 3 固有モジュール結線 | なし |
| **X の旧理由**（他リポ番号の表記違反） | キット原文の `planning#202` は**短縮形で正しく修飾されている**。`node scripts/check-cross-repo-refs.js` を上書き後に実走して確認する（grep で済ませない） |

→ **結論: X → A。** 起動条件・必須チェック名（＝ジョブ ID `pr-title`）は不変であり、
ブランチ保護の設定は影響を受けない。不変であることは `scripts.repo.test.js` の
`#799: pr-title.yml が PR_NUMBER を渡し、起動条件とジョブ ID は不変` が固定し続ける。

### 突合 4: `scripts/check-commit-messages.js`

`diff -u` の全数（生出力は「検証」節）。**機能差は 1 つも残っていない。**

| 残差 | 種別 |
| --- | --- |
| 置換点 `PLAN_PROJECT` を `microservices-platform` で埋めている | **種 5** |
| docstring / コメントの文言（本リポの実測値 66 件・58 件と `#799` の引用を持つ） | 文言のみ。機能差なし |
| `module.exports` の `checkSingleTitle` の並び順 | 文言のみ。機能差なし |

→ **結論: X → 種 5。** キットが #799 を**完全実装**した（`validateTitlePrNumber` /
`normalizePrNumber` / `checkSingleTitle` の第 3 引数がすべて存在する）ため、
「キットに無い検査 1 つ」という X の理由が消えた。**ファイル自体は差し替えない**
（置換点を埋めているため分類 A にはならない）。

## ★ 母集合（規則 9・10）

**「この変更で新たに誤りになる自分の記述」を、誤りの側の文字列で全文書から引き直した。**
軸は 7 本（規則 5「軸を 1 本で終わらせない」）。走査は追跡下の全ファイル、拡張子で絞らない（規則 3）。

```
$ for pat in 'planning#379' 'planning#380' '#799' '2 引数' '形状しか見て' '環流は未了' '未送付'; do
    grep -rn --exclude-dir=planning --exclude-dir=node_modules --exclude-dir=.git -F "$pat" .
  done
```

### 引いた結果（是正する）

| # | 箇所 | 何が偽になったか |
| --- | --- | --- |
| 1 | `scripts/kit-sync-classification.json` の `scripts/scripts.test.js` | 「キット側の是正が着地したらバイト一致へ戻す。追跡: planning#380」→ **着地した** |
| 2 | 同 `.github/workflows/pr-title.yml` | 「キット原文の他リポ番号の表記違反を是正」「#799 で 1 行を足した」→ **どちらも解消** |
| 3 | 同 `scripts/check-commit-messages.js` | 「キット版は形状しか見ておらず」「環流は**未了**」→ **着地済みで偽** |
| 4 | 同 `scripts/check-cross-repo-refs.js` | 「**キット版は 0 件走査の門を持たない**」→ **planning#379 の着地で偽** |
| 5 | `scripts/check-cross-repo-refs.js` の 0 件走査の門のコメント | 「キット版はこの門を持ち込んでいない（実測: 空リポで exit 0）」→ **偽**（実測し直した） |
| 6 | 同 `module.exports` の `trackedMarkdown` | 呼び出し元が無い export。キットが外した |
| 7 | `scripts/check-commit-messages.js:449-452` の docstring | 「**キットには無い**」「環流は未了で、記録の草案は…付録に在る」→ **偽** |
| 8 | `scripts/scripts.repo.test.js:209-211` | 「キット版 `scripts.test.js` は `checkSingleTitle` を **2 引数**でしか呼ばないため、ここでしか固定されない」→ **新 pin は 3 引数で呼ぶ**ので偽 |
| 9 | `docs/how-to/session-handoff.md` | 「`scripts.test.js` は分類 B（X・期限つきの暫定）」「planning#380 が着地したらバイト一致へ戻す」→ **着地した** |
| 10 | `scripts/README.md` の CI ジョブ表 | **`pr-title` の行が無い**（キットは持つ。土台の表はキットが正） |

### 除外したものと理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| `docs/specs/` の既往仕様書（`20260790` / `20260799` 系ほか） | **`status: done` の確定済み仕様書は書き換えない**（`.claude/rules/traceability.repo.md`）。作業時点の実測記録であり、時点つきで正しい |
| `CHANGELOG.md` | **生成物**（`gen-changelog.js` が再生成する）。手で書き足さない |
| `docs/adr/IADR-0204` / `IADR-0201` / `IADR-0207` | **決定記録**であり、当時の実測と当時の判断を述べている（「環流待ち planning#379」は #790 時点の事実）。本文への後付け注記は**決定を変えるときだけ**。本作業は既存決定（[[IADR-0204]] 決定 2）の**適用**であって改定ではない |
| `docs/adr/README.md` / `docs/how-to/commit-message-rules-annex.md` の `#799` 引用 | **起票 ID の引用**であり、着地しても指す先は変わらない |
| `.github/workflows/claude-code-review.yml:287` の `#799` | **別件の #799 引用**（`list_pull_requests` の許可）。本件と無関係 |
| `deploy/` ほかの「2 引数」（Prometheus retention 等） | **同名の別語**。本件と無関係 |
| `feedback/` の既存記録 | 本件で状態が動く記録は無い。#799 の環流記録は [[IADR-0207]] 決定 7 の判断で **`feedback/` へ置いていない**（未送付 0 件のラチェットのため。キットが実装済みになった今、置く必要も消えた） |
| `.claude/rules/*.md` / `CLAUDE.md` | 必読規約。**1 バイトも増やせない**（余白が僅少）。本件で偽になる記述は無い |

### 導出値は計算し直す（規則 10）

分類件数（A / B / C / X）は**走査ではなく計算**で出す。値は「検証」節に、
**変更前・変更後の両方**を実行コマンドつきで載せる。

## ★ 重複を残す判断（`scripts.repo.test.js` の #799 試験群）

キット原文 `scripts.test.js` が新設した 7 試験と、companion `scripts.repo.test.js:202-340` の
#799 試験群は**入力も対象も重なる**。両方 pass するので機械的な失敗は起きない。

**それでも companion を削らない。本リポ版が厳密に上位だからである。** companion にしか無い assert:

| companion にしか無い assert | キット版に無い理由 |
| --- | --- |
| 違反理由の**文言**（`/外すか/`・`/Closes/`） | キット版は `/#100/`・`/#200/`（番号）しか見ない。**CI ログを読んで直す人が要る情報**が消えても気付かない |
| **文字列で渡した PR 番号**（`'794'`）の正規化と不一致検出 | 実運用は環境変数経由＝**必ず文字列**である。キット版は数値でしか呼ばない |
| `normalizePrNumber('12x')` | 「数字で始まるが数値でない」形。キット版は `'abc'` / `'0'` / `'-1'` のみ |
| **develop に実際に着地した 6 件名の回帰 fixture ＋ 反証** | 実データ由来。キットは配布先の履歴を知らない。**反証**（同じ件名を別番号の PR タイトルとして渡すと必ず違反）まで置いている |
| **ジョブ ID `^ {2}pr-title:$` と起動条件 `types:` の不変性** | キット版は `PR_NUMBER` の受け渡ししか見ない。**必須チェックの context はジョブ ID** であり、改名すると**ブランチ保護が黙って外れる** |

**重複を理由に削ると検出力が落ちる。** 分類 A のファイル（`scripts.test.js`）は
キット原文のまま置くしかないため、**上位の assert は companion にしか置けない**
（これが companion 方式の存在理由そのものである）。

## 受け入れ基準

- [x] `scripts/check-cross-repo-refs.js` が 3 点の改修後も **0 件走査で EXIT=1**、
      **git 非管理下で EXIT=0** を返す（変異試験 2 方向を実測で示す）
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が exit 0
- [x] `scripts/scripts.test.js` が計画 pin `767a9d48` のキット原文とバイト一致
- [x] `.github/workflows/pr-title.yml` が同じくバイト一致で、**ジョブ ID・起動条件・
      `PR_NUMBER` の受け渡しが不変**
- [x] `node scripts/check-kit-sync.js` が exit 0（A の増分がバイト一致で通る）
- [x] 分類 X が **11 件 → 7 件**（本件の 4 件がすべて X を外れる）
- [x] 母集合の 10 件がすべて是正されている
- [x] `.claude/rules/` と `CLAUDE.md` のバイト数が不変

## テスト方針

- **変異試験**（門が効いていることの側）は `scripts.repo.test.js` の
  `0 件走査の門: … は走査ルートが無いと fail する（変異試験）` が既に固定している。
  本作業では**手元でも 2 方向を実走して生出力を残す**（宣言だけの監査は不合格）。
- **キット版の門試験**（`scripts.test.js` が検査器 1 ファイルだけをコピーして走らせる形）が
  新たに `MODULE_NOT_FOUND` の経路を踏むため、遅延化が効いていることは
  `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` の緑で確かめる。

## 計画書との差異

- 差異: なし。本作業は計画リポの受理（planning#379・planning#380 と、本リポ発 #799 の相当分）を
  **取り込む側**である。

## 未決事項

なし（着手前時点）。作業中に生じた判断は「判断に迷った点」へ記す。

## 検証

**終了コードはパイプで終端せず**（`cmd > log 2>&1; echo "EXIT=$?"`）、**判定行は末尾とは限らない**ので
`grep` で全数を拾って読んでいる。**出力は加工していない**（`head` で切らない・`sed` で潰さない）。

### 検査器の EXIT と判定行

| コマンド | EXIT | 判定行 |
| --- | ---: | --- |
| `node scripts/check-doc-links.js` | 0 | `OK: 684 件の Markdown に破損した相対リンクはありません`（＋ 未 populate submodule 2 件は対象外の notice） |
| `node scripts/check-doc-type-vocabulary.js` | 0 | `OK: 658 件の文書の type が、テンプレート 19 種類の値域に収まっています` |
| `node scripts/check-cross-repo-refs.js` | 0 | `走査 1771 件 / 除外 73 件` ＋ `OK: 1771 件に他リポジトリ参照の表記違反はありません` |
| `node scripts/check-plan-id-qualification.js` | 0 | `OK: 1447 件に他プロジェクト ID の修飾違反はありません` |
| `node scripts/check-adr-numbering.js` | 0 | `OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です` |
| `node scripts/check-reading-budget.js` | 0 | `warn Claude Code: 50,132 バイト（予算 51,200 の 97.9%）`（**本 PR で 0 バイト増**） |
| `node scripts/check-kit-sync.js` | 0 | `OK: キット 115 件を分類表と突合しました（**A 78 件**はバイト一致 / **B 25 件**は固有デルタ / C 4 件は同期しない / 対象外 8 件）` |
| `node scripts/check-commit-messages.js --title "chore(ADR-0030): テスト"` | 0 | `✓ PR タイトルが規約に適合`（単一件名モードの回帰） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 0 | **`✓ 649 tests passed`** |

**`skip` と `pass` はどちらも EXIT=0 である**ため、全 710 行から `skip` / `notice` / `FAIL` を
全数拾って読んだ。skip はいずれも**意図された fail-open**（キット未参照・planning 未 populate・
置換点が空）であり、本作業で新たに skip へ落ちた検査は無い。

### ★ 変異試験 —— 2 方向の実測（[[IADR-0204]] 決定 2 の点 2）

**キット門試験と同条件**（検査器 1 ファイルだけを一時ディレクトリへ置き、`lib/` を持たせない）。

```
$ mkdir -p mut/a/scripts && cp scripts/check-cross-repo-refs.js mut/a/scripts/ && git -C mut/a init -q
$ node $PWD/mut/a/scripts/check-cross-repo-refs.js; echo "EXIT=$?"
[check-cross-repo-refs] 走査 0 件 / 除外 0 件（scripts/ の非 Markdown）
[check-cross-repo-refs] 走査対象のファイルを 1 件も見つけられませんでした。
  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。
EXIT=1

$ mkdir -p mut/b/scripts && cp scripts/check-cross-repo-refs.js mut/b/scripts/   # git 非管理下
$ node $PWD/mut/b/scripts/check-cross-repo-refs.js; echo "EXIT=$?"
fatal: not a git repository (or any of the parent directories): .git
[check-cross-repo-refs] git ls-files を実行できないため走査をスキップした。
EXIT=0
```

`lib/` を置いた（＝本リポと同条件の）場合も同じ向きで、**worktree 警告が 1 行増えるだけ**である。

```
$ node $PWD/mut2/a/scripts/check-cross-repo-refs.js; echo "EXIT=$?"
  warn  [check-cross-repo-refs.js] untracked のファイルが 1 件ある。…（#683 / IADR-0183）。
[check-cross-repo-refs] 走査 0 件 / 除外 0 件（scripts/ の非 Markdown）
[check-cross-repo-refs] 走査対象のファイルを 1 件も見つけられませんでした。
  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。
EXIT=1
```

**門と worktree 警告はどちらも落ちていない。**

### ★ 遅延化した require が「MODULE_NOT_FOUND だけ」を握っていることの反証

初版は `e.code === 'MODULE_NOT_FOUND' && /worktree-state/.test(e.message)` で見分けていたが、
**握り潰しが実測で起きた** —— Node の `MODULE_NOT_FOUND` の message は `Require stack:` を含み、
**lib が別モジュールを見失った場合にも本モジュールのパスが載る**ためである。
**解決（`require.resolve`）と読み込み（`require`）を分ける**形へ直し、3 方向で確かめた。

| 入力 | 期待 | 実測 |
| --- | --- | --- |
| `lib/worktree-state.js` が**無い** | 握って続行（門は効く） | EXIT=1・`0 件検査は…fail させています` |
| `lib/worktree-state.js` が**構文エラー** | **握らず落ちる** | EXIT=1・`SyntaxError: missing ) after argument list` |
| `lib/worktree-state.js` が**別モジュールを見失う** | **握らず落ちる** | EXIT=1・`Error: Cannot find module './nonexistent-xyz.js'` |

（初版はこの 3 行目が握り潰されて EXIT=1・`0 件検査…` を返していた。**規則 7 の「引き直す」で検出した。**）

#### ★ 上の 3 方向のうち、CI で固定したのは 2 本である（判別力は非対称）

**手で実走しただけでは退行を止められない。** 上の表は本書に残る実行ログでしかなく、
**旧実装へ戻しても CI は緑を返す**（既存の 2 試験 —— `lib/` 不在の門と git 非管理下の
fail-open —— は**キット版 `scripts.test.js` が元から持っていたもの**で、握り潰しそのものは
1 件も見ていない）。よって `scripts/scripts.repo.test.js` の末尾へ**回帰試験 2 本**を足した。

**置き場所が companion である理由**: `scripts/scripts.test.js` は本作業で**分類 A（バイト一致）**へ
戻したところであり 1 バイトも足せない。そして `lib/worktree-state.js` への結線は**分類 B 種 3
（本リポ固有）**で、キット版の検査器には存在しない —— **キットへ環流する筋のものではない。**

| 試験 | 変異させる `lib/worktree-state.js` | 判別力 |
| --- | --- | --- |
| **1（本命）** | `require('./nonexistent-xyz.js')` を含む | **旧実装を捕まえる** |
| **2（保険）** | 構文エラー | **旧実装を捕まえない**（下記） |

**2 本は等価ではない。** 旧実装の条件は `e.code === 'MODULE_NOT_FOUND' && /worktree-state/.test(e.message)`
であり、**`SyntaxError` は `.code` を持たない**ので条件が偽になって旧実装でも throw する。
試験 2 が効くのは `catch (e) {}` のように catch を広げすぎる**将来の**退行に対してであって、
**当のバグは検出しない。「2 本あるから握り潰しは固定されている」と読んではならない。**

**判定は終了コードではない。** 握り潰した場合も 0 件走査の門が exit 1 を返すため、
両者はどちらも EXIT=1 である。**門のメッセージが出ていないこと**（`assert.doesNotMatch`）が
「握らずに伝播した」ことの証拠であり、これが 3 つ目の assert の役目である。

**この 2 本自身に変異試験を当てた**（宣言だけでは不合格。検査器の 91-98 行を旧実装へ
一時的に戻して実測し、**確認後に元へ戻した**。`git diff` で復元を確認済み）。

```
# 旧実装（メッセージ判別）へ戻して全件実行
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js > t2.log 2>&1; echo "EXIT=$?"
EXIT=1
$ tail -12 t2.log
AssertionError [ERR_ASSERTION]: lib 側の MODULE_NOT_FOUND を握り潰している（伝播していない）:
[check-cross-repo-refs] 走査 0 件 / 除外 0 件（scripts/ の非 Markdown）
[check-cross-repo-refs] 走査対象のファイルを 1 件も見つけられませんでした。
  0 件検査は「検査しているつもりで何も見ていない」状態なので fail させています。
    at .../scripts/scripts.repo.test.js:8093:14      # ← 試験 1 の 2 つ目の assert
```

**試験 1 が落ちた**（＝退行を捕まえる）。走者は最初の失敗で中断するため試験 2 はこの実行では
走らない。よって**試験 2 の 3 つの assert を同じ旧実装に対して個別に実測した**:

```
$ node mt2/scripts/check-cross-repo-refs.js; echo "EXIT=$?"
SyntaxError: Unexpected end of input
    ...
    at Object.<anonymous> (.../mt2/scripts/check-cross-repo-refs.js:91:44)
EXIT=1
```

`status !== 0`・`/SyntaxError/` に一致・**門のメッセージは出ていない** —— **3 つとも成立する。
つまり試験 2 は旧実装でも通ってしまう。** 上に書いた非対称の実証である。

**復元後の全件実行**: `EXIT=0` / **`✓ 651 tests passed`**（649 → 651。増分は上の 2 本）。

### キット原文とのバイト一致

```
$ git -C planning show 767a9d48:tools/impl-handoff-kit/repo-template/scripts/scripts.test.js | cmp - scripts/scripts.test.js
$ git -C planning show 767a9d48:tools/impl-handoff-kit/repo-template/.github/workflows/pr-title.yml | cmp - .github/workflows/pr-title.yml
（どちらも出力なし＝一致）
```

`pr-title.yml` の**非コメント行 diff は空**（上書き前に確認）、上書き後も
**ジョブ ID `pr-title`（34 行目）／起動条件 `types: [opened, edited, reopened, synchronize]`（23 行目）／
`PR_TITLE`・`PR_AUTHOR`・`PR_NUMBER` の受け渡し（55 / 57 / 62 行目）／`run: node scripts/check-commit-messages.js`（63 行目）**
はすべて不変である。**必須チェック名（＝ジョブ ID）が変わらないので、ブランチ保護の設定は影響を受けない。**

### 分類表の変更前後（導出値は**計算し直した**。規則 10）

| | 変更前 | 変更後 |
| --- | ---: | ---: |
| A（バイト一致） | 76 | **78** |
| B（固有デルタ） | 27 | **25** |
| C（同期しない） | 4 | 4 |
| 対象外 | 8 | 8 |
| **うち X** | **11** | **7** |

**本件の 4 件がすべて X を外れた。**

| ファイル | 変更前 | 変更後 |
| --- | --- | --- |
| `scripts/scripts.test.js` | B〔X〕 | **A** |
| `.github/workflows/pr-title.yml` | B〔X〕 | **A** |
| `scripts/check-commit-messages.js` | B〔X〕 | **B 種 5** |
| `scripts/check-cross-repo-refs.js` | B〔X〕 | **B 種 3**（＋種 5） |

残る X 7 件（本件の射程外）: `.claude/rules/traceability.md`（[[IADR-0204]] 決定 1 のラチェットつき暫定）／
`.claude/agents/spec-implementer.md`／`.claude/commands/new-spec.md`／`docs/DEFINITION_OF_DONE.md`／
`scripts/check-doc-links.js`／`scripts/check-planning-pin-freshness.js`／`scripts/setup.sh`。

### 変更したファイル

| ファイル | 変更 |
| --- | --- |
| `scripts/check-cross-repo-refs.js` | require の遅延化（`require.resolve` と `require` を分離）／`EXCLUDED_DIRS` を export／`trackedMarkdown` を削除・export から撤去／偽になった 2 つのコメントをキット原文の文言へ |
| `scripts/scripts.test.js` | **キット原文（`767a9d48`）で上書き**（バイト一致） |
| `.github/workflows/pr-title.yml` | **キット原文で上書き**（バイト一致。非コメント行の差分は元から空） |
| `scripts/check-commit-messages.js` | `validateTitlePrNumber` の docstring から偽になった「キットには無い」「環流は未了」を削除 |
| `scripts/kit-sync-classification.json` | 2 件を B〔X〕→ A へ移動（A はアルファベット順を維持）／2 件の理由欄を書き換え |
| `scripts/scripts.repo.test.js` | #799 試験群の見出しコメント（「キット版は 2 引数でしか呼ばない」が偽）を追記形式で是正し、**削らない根拠**を明記 |
| `scripts/README.md` | CI ジョブ表へ `pr-title` の行を追加／`check-cross-repo-refs.js` の「対象は追跡下の `*.md`」を実測どおり是正 |
| `docs/how-to/session-handoff.md` | `scripts.test.js` の分類を A（変更禁止）へ戻す |

## 判断に迷った点（人の裁定を仰ぐ）

1. **`scripts/check-commit-messages.js` の docstring をキット原文へ揃えるか。**
   分類表 `$comment` の 種 5 の定義は「**土台（説明文・規約文）はキット側が正であり追随の対象**」
   と書いている。厳密に読むと、置換点以外の docstring はキット文言へ揃えるのが 種 5 の姿である。
   本作業では**偽になった 2 文だけを削り、本リポの実測値（66 件 / 58 件）と `#799` の引用は残した**
   —— 実測値は本リポにしか無い記録であり、消すと根拠が失われるためである。
   **`module.exports` の `checkSingleTitle` の並び順**も同じ理由で据え置いた（機能差なし）。
   **完全にキット文言へ揃えるべきなら次の追随で行う。**
2. **`scripts/README.md` の `check-cross-repo-refs.js` 行が、キット版より大幅に古い。**
   本作業では**実測で偽と確かめた 1 節**（走査範囲）だけを是正した。行全体をキット原文へ
   揃えるのは別の母集合（README 全体の追随）であり、本 PR の射程を超えると判断した。
   **追随 issue を起こすかどうかは裁定を仰ぐ。**
3. **`docs/adr/IADR-0204` / `IADR-0201` / `IADR-0207` に後付け注記を入れなかった。**
   いずれも当時の実測・当時の判断を述べた**決定記録**であり、本作業は既存決定
   （[[IADR-0204]] 決定 2）の**適用**であって改定ではないと判断した。
   **新 IADR も起こしていない**（新しい決定をしていないため）。異論があれば裁定を仰ぐ。

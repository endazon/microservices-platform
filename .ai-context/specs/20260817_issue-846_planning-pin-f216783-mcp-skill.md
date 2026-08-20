---
title: 作業仕様書 — 計画 pin を f216783 へ進め、スキル・MCP を配備して Playwright の棲み分けを決める
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - IADR-0033
  - IADR-0121
  - IADR-0179
  - IADR-0192
  - IADR-0204
  - IADR-0221
  - IADR-0222
author: claude
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - planning:draft/cross-project/20260817_skill-mcp-adoption-decision.md
  - planning:tools/impl-handoff-kit/HOWTO.md (§B-3.5 MCP を承認し、プラグインとブラウザ操作を整える)
  - planning:tools/impl-handoff-kit/repo-template/AI_SETUP.md (§4)
  - planning:docs/ai-implementation-workflow-guide.md (§8 必読規約の予算 51,200 バイト)
related_specs:
  - "../adr/IADR-0221_playwright-cli-vs-test-runner-scope.md"
  - "../adr/IADR-0222_mcp-json-scope-and-github-server-collision.md"
  - "20260817_planning-pin-767a9d48.md"
---

# 作業仕様書: 計画 pin `f216783` の追随とスキル・MCP の配備

## 1. 起点となる ID（トレーサビリティ）

- **`ADR-0030`**（バックエンド標準構成。ブランチ名の起点 ID）。`IADR-0116` 規約 3 に従い、`NFR` と併記されていても
  最初の**具体 ID** を採る。
- **無採番 `NFR`**（キット追随・pin 更新＝メタ作業。`.claude/rules/traceability.md`「無採番 `NFR` を許す 2 つの場合」の**場合 2**）。
- 起票: [#846](https://github.com/endazon/microservices-platform/issues/846)

## 2. 母集合の引き方（実測）

**走査基準コミット**: `develop` `7aa0976`（作業開始時）。**計画 pin**: `767a9d48` → `f216783`。**当初は `2c78212` を予定していたが、キット原本の是正（planning#402）が着地したため最新 main まで進めた**（後述）。

```text
git -C planning diff --stat 767a9d48 f216783 -- tools/impl-handoff-kit/
  HOWTO.md                            | 39 +++
  generators/handoff.js               | 80 +++++   ← 承認ゲート（planning#405）。配布物ではない
  repo-template/.mcp.json             |  9 +++     ← 新規（context7 のみ）
  repo-template/AI_SETUP.md           | 73 ++++
  repo-template/CLAUDE.md             |  1 +
```

**`generators/` は `repo-template/` の外であり、実装リポへは配布されない**（`check-kit-sync.js` の対象外）。
配布物に効くのは `.mcp.json` / `AI_SETUP.md` / `CLAUDE.md` の 3 件である。

**キットの `kit-sync-classification.example.json` と `.claude/rules/traceability.md` は pin 間で無変更である**
（実測: `git -C planning diff --stat` が空 / `wc -c` が 25,963 B のまま）。したがって
分類 A の drift も、`traceability.md` の追随債務（種 X・追跡 #793 系）の増減も**発生しない**。

追随前の実測:

```text
node scripts/check-kit-sync.js
  [check-kit-sync] 追随の違反 1 件を検出しました:
      [unclassified] .mcp.json が分類表に無い。…
```

**違反はこの 1 件だけである。** AST が同時に 5 件を抱えるのと対照的で、本リポは pin を進めても
抱き合わせの追随債務が無い。

## 3. 決めたこと

### 3-1. `.mcp.json` は Context7 のみとする（[IADR-0222](../adr/IADR-0222_mcp-json-scope-and-github-server-collision.md)）

🔴 **キットが当初配布した `.mcp.json`（pin `2c78212`）は `github` という名前の GitHub MCP サーバを
含んでいた。これを置くと CI の AI レビューが静かに死ぬ。**

| # | 事実 | 出典 |
| --- | --- | --- |
| 1 | Claude Code は `${VAR}` が未定義で既定値も無いとき、**設定を読み込み、警告を出してリテラル文字列をそのまま使う** | Claude Code 公式ドキュメント（MCP / 環境変数展開） |
| 2 | 非対話モードは cwd の `.mcp.json` を読む。かつ claude-code-action は **`enableAllProjectMcpServers` を自動的に true にする** | claude-code-action base-action README ／ `restore-config.ts` |
| 3 | claude-code-action は **`github` という名前のサーバを組み込みで供給**し、**同名のカスタムサーバが組み込みを上書きする** | claude-code-action docs/configuration.md |
| 4 | PR 実行時、`.mcp.json` は**ベースブランチ版へ復元される** | `restoreConfigFromBase()` |

本リポの `claude-coding.yml` / `claude-code-review.yml` は `mcp__github__*` を 13〜15 件許可しており、
**レビューコメントの投稿そのもの**（`pull_request_review_write` / `add_comment_to_pending_review`）が
これに依存する。**そしてジョブは success で終わる。**

**事実 4 により、本 PR 自身のレビューは無事である**（ベース `develop` に `.mcp.json` がまだ無い）。
**発火するのはマージ後の次の PR からである。**

**分類は A（キットとバイト一致）である。** キット原本の是正
（[planning#402](https://github.com/endazon/project-planning/pull/402)）が着地したため、
pin をその後の計画 main（`f216783`）まで進めることで**環流債務を作らずに済んだ**。

> **起案時は分類 B（種 X）を予定していた。** 暫定の X は環流債務の測定値を汚すため、
> **計画側の是正を待って A で置く**ほうが望ましい。今回は待てた。

### 3-2. Playwright は役割で棲み分ける（[IADR-0221](../adr/IADR-0221_playwright-cli-vs-test-runner-scope.md)）

- **CI の E2E テストは `@playwright/test` を継続**する（`IADR-0033` を覆さない）
- **AI のブラウザ操作は `playwright-cli` + Skills**
- **Playwright MCP は導入しない**
- **`@playwright/cli` は `package.json` に加えない** —— CI のどのジョブも起動せず、pnpm workspace に
  2 つ目の Playwright が入る。`frontend.yml:240-243` の `ERR_PNPM_RECURSIVE_EXEC_FIRST_FAIL` の罠もある

### 3-3. ワークフローの許可リストへ `mcp__context7__*` を加えない

3 系統同期（`.claude/settings.json` / `claude-coding.yml` / `claude-code-review.yml`）の対象は
**その面で実際に使えるツール**である。Context7 は `npx -y @upstash/context7-mcp` の stdio サーバで、
**CI の毎回の実行で npm レジストリへの取得が走る**。AI レビューは外部ドキュメントの参照を要さない
（差分と計画書の突合が仕事である）。→ **ワークフロー 2 本は無変更**。

## 4. 変更したファイル

| ファイル | 変更 |
| --- | --- |
| `.mcp.json`（新規） | `context7` のみ |
| `scripts/kit-sync-classification.json` | `.mcp.json` を **A（バイト一致）** へ登録。`AI_SETUP.md` の行へ種 1・種 2 のデルタを追記。`$comment` の pin 表記を `f216783` へ |
| `AI_SETUP.md` | キット §4 を取り込み（旧 §4 → §5）。§4-1 は**是正版**（GitHub MCP を書かない理由）。§4-3 に本リポの固有デルタ注記 |
| `CLAUDE.md` | **無変更**（当初 1 行を足したが、余白下限ラチェットに掛かったため撤回。IADR-0221 決定 3） |
| `docs/adr/IADR-0221_*.md`（新規） | Playwright の棲み分け |
| `docs/adr/IADR-0222_*.md`（新規） | `.mcp.json` のスコープと同名衝突 |
| `docs/adr/README.md` | 索引 2 行 |
| `planning` | pin `767a9d48` → `f216783` |

## 5. 検証（実測）

```text
node scripts/check-kit-sync.js
  OK: キット 116 件を分類表と突合しました（A 79 件 / B 25 件 / C 4 件 / 対象外 8 件）  exit=0

node scripts/check-kit-sync.js --self-test
  self-test OK（13 件）

node scripts/check-adr-numbering.js
  OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です  exit=0

node scripts/check-reading-budget.js
  warn  Claude Code: 50,393 バイト（予算 51,200 の 98.4%）  exit=0
          CLAUDE.md 20,242 / traceability.md 24,592 / traceability.repo.md 5,559

node scripts/check-doc-links.js               exit=0
node scripts/check-cross-repo-refs.js         exit=0
node scripts/check-plan-id-qualification.js   exit=0
```

### `scripts/scripts.test.js` は Windows で完走しない（**本 PR とは無関係の既存事象**）

ローカル（Windows）で横断テストを走らせると、次で止まる。

```text
AssertionError: 走査母集合を git ls-files から引く検査器と MODE.TRACKED の宣言が食い違う
  actual: []   expected: [ 'check-cross-repo-refs.js', 'check-plan-id-qualification.js' ]
  at scripts/scripts.repo.test.js:5075
```

**既存事象であることを実測で切り分けた** —— `develop`（`7aa0976`）を別 worktree へ取り出し、
**本 PR の変更を一切含まない状態で同じ assertion が同じ値で落ちる**ことを確認した。

原因は `scripts.repo.test.js:5056` が子プロセスの `PATH` を**コロン直書き**で組むことである
（Windows の区切りは `;`）。git シムが `PATH` に載らず、実 `git` が走ってログが空になる。
`path.delimiter` へ替えても解消しなかったため、**シム本体が拡張子なしで Windows から起動できない**
という第 2 の原因もある。

**Linux の CI では発生しない**（区切りが `:` のため）。**判定は CI を正とする。**
追随は別 issue とする（本 PR の射程外。`scripts.repo.test.js` は companion＝本リポ所有であり
キット環流は要らない）。

**この事象より手前のテストはすべて緑である**（索引タイトルのラチェット・`check-doc-links` の
実データ検査を含む）。本 PR で 2 回落ちて 2 回とも直した:

1. `docs/adr/README.md` の索引 2 行が **200 字上限**を超えた → 163 字 / 162 字へ縮めた
2. `plan_refs` が当時の pin に存在しない計画リポのファイルを指した（`20260817_mcp-json-github-server-collision.md`
   は planning#402 の中身）→ 一度 `plan_refs` から外したが、**同 PR がマージされ pin を進めたため復帰させた**

### 🔴 読書予算 —— **必読への加筆を撤回した**

**当初 `CLAUDE.md` へ 1 行（261 B）足したが、CI で落ちた。**

```text
AssertionError: 必読の余白が 807B まで減った（下限 1000B）。
  at scripts/scripts.repo.test.js:5571   ← #730 / IADR-0190 決定 2 のラチェット
```

**`check-reading-budget.js` だけでは気づけない。** 同スクリプトは**上限（51,200 B）しか見ず**、
98.4% でも `warn` を出して exit 0 を返す。**下限（余白 1,000 B）は別のラチェットが持つ。**

**縮めても入らなかった**（実測）。

| 案 | 行 | 余白 | 判定 |
| --- | ---: | ---: | --- |
| 当初 | 261 B | 807 B | NG |
| 短縮 A | 155 B | 913 B | NG |
| 短縮 B | 139 B | 929 B | NG |
| 最短 C | 100 B | 968 B | NG |
| **削除** | **0 B** | **1,068 B** | **OK** |

→ **`CLAUDE.md` は無変更とした**（[IADR-0221](../adr/IADR-0221_playwright-cli-vs-test-runner-scope.md) 決定 3）。
規範は `AI_SETUP.md` §4-3 と同 IADR が持ち、`CLAUDE.md` は冒頭と §生成 AI の活用 で既に
「**AI の有効化・認証は `AI_SETUP.md` が正本**」と述べている。

**本 PR は必読の債務を増やしていない。** 保留中の `traceability.md` キット版取り込み
（+1,371 B・追跡 #793 系）が要する減量は **302 B のまま**である（判定は実ファイルからの
ライブ計算であり、この数値は使わない）。

## 6. 未了（本 PR では完了できないもの）

| # | 内容 | 理由 |
| --- | --- | --- |
| 1 | **`.claude/settings.json` への `mcp__context7__*` 追加** | 同ファイルは `Edit` / `Write` とも deny のため代行できない。**利用者が適用する**（PR 本文「6. 利用者にお願いしたいこと」項目 1 と同一） |
| 2 | **`/mcp` での `context7` 接続の目視確認** | ヘッドレス実行では承認プロンプトを出せない。**利用者が対話モードで確認する** |
| ~~3~~ | ~~`.mcp.json` を分類 A へ戻す~~ | **完了。** planning#402 のマージ後に pin を `f216783` まで進め、**最初から分類 A で置いた**（暫定の種 X を作らずに済んだ） |
| 4 | **`playwright-cli` の実導入と `--skills` の挙動確認** | ユーザー単位の導入であり CI で固定できない（`IADR-0221` §未解決） |

> **［2026-08-17 追記 / #846］項目 1 を「完了・確認済み」と書いていたのは誤りであった。**
> AI レビューの指摘（2 回）を受けて実測したところ、`.claude/settings.json` に `mcp__context7` は
> **0 件**であり（`grep -c "mcp__context7" .claude/settings.json` → `0`）、同ファイルは
> **本 PR の作業期間中に一度も変更されていない**（最終変更は `ab26b7d`・#498）。
> 裏付けの無い完了報告であり、PR 本文「6. 利用者にお願いしたいこと」項目 1 が同じ作業を未完了として
> 依頼している矛盾も生んでいた。**取り消し線を外し未了へ戻した。**
>
> **再発防止**: 利用者へ依頼した作業を完了として記録するときは、**依頼した事実ではなく適用後の
> 実測（`grep` の出力・`git log` の対象ファイル）を根拠に書く**。代行できないファイル
> （`Edit` / `Write` が deny）は、AI 側から完了を宣言できる面が無いことを前提にする。

> **［2026-08-17 追記 / #846］上の追記自身が、同じ型の誤りを 1 つ含んでいた（#853 のレビューが検出）。**
> 当初この追記とコミット `1cf233e` は、最終変更を **`859b9fe`（#759）** と書いていた。**誤りである。**
> 正しくは **`ab26b7d`（#498「キット同期 第 14 ラウンド」）** である（`859b9fe` は
> `.claude/settings.json` を 1 行も変更していない）。**結論（本 PR 期間中は無変更）は変わらない** ——
> 独立な根拠である `grep -c` → `0` が支えており、是正の内容そのものは正しい。誤ったのは**出典の引用**である。
>
> **原因は作業環境の shallow clone であった。** 本セッションの作業ツリーは 53 コミットしか持たず、
> `859b9fe` は**その grafted boundary**（`.git/shallow` の唯一のエントリ）である。境界コミットは
> 打ち切られた歴史をすべて自分の変更として見せる（`git show 859b9fe --stat` → **1,684 files changed**）ため、
> `git log -1 -- <path>` は「そのファイルを最後に触ったコミット」ではなく**境界を返す**。
> `git fetch --unshallow` の後に引き直して確定させた。
>
> **再発防止（2 点）**:
> 1. **`git log` / `git blame` の出力を出典として引く前に `git rev-parse --is-shallow-repository` を見る。**
>    `true` なら、その出力は履歴の打ち切り位置を指している可能性があり、**出典として使えない**。
>    CI と手元で同じコマンドが別の答えを返す（レビューは fuller な履歴を持つため `ab26b7d` を得た）。
> 2. **編集後に変わる値は、編集後に測り直す。** 同レビューは索引セル長の自己申告が **174 字（誤）／
>    実測 175 字**とずれていることも検出した。これは編集前の 162 字に見積り +12 を足した**予測値**を、
>    測り直さずに実測として書いたものである。**予測値と実測値を同じ書式で書かない。**

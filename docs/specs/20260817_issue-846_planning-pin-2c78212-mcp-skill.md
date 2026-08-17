---
title: 作業仕様書 — 計画 pin を 2c78212 へ進め、スキル・MCP を配備して Playwright の棲み分けを決める
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
  - "../../planning/draft/cross-project/20260817_skill-mcp-adoption-decision.md"
  - "../../planning/tools/impl-handoff-kit/HOWTO.md (§B-3.5 MCP を承認し、プラグインとブラウザ操作を整える)"
  - "../../planning/tools/impl-handoff-kit/repo-template/AI_SETUP.md (§4)"
  - "../../planning/docs/ai-implementation-workflow-guide.md (§8 必読規約の予算 51,200 バイト)"
related_specs:
  - "../adr/IADR-0221_playwright-cli-vs-test-runner-scope.md"
  - "../adr/IADR-0222_mcp-json-scope-and-github-server-collision.md"
  - "20260817_planning-pin-767a9d48.md"
---

# 作業仕様書: 計画 pin `2c78212` の追随とスキル・MCP の配備

## 1. 起点となる ID（トレーサビリティ）

- **`ADR-0030`**（バックエンド標準構成。ブランチ名の起点 ID）。`IADR-0116` 規約 3 に従い、`NFR` と併記されていても
  最初の**具体 ID** を採る。
- **無採番 `NFR`**（キット追随・pin 更新＝メタ作業。`.claude/rules/traceability.md`「無採番 `NFR` を許す 2 つの場合」の**場合 2**）。
- 起票: [#846](https://github.com/endazon/microservices-platform/issues/846)

## 2. 母集合の引き方（実測）

**走査基準コミット**: `develop` `7aa0976`（作業開始時）。**計画 pin**: `767a9d48` → `2c78212`。

pin 間の計画リポ差分は 2 コミット（[planning#396](https://github.com/endazon/project-planning/pull/396) / [planning#399](https://github.com/endazon/project-planning/pull/399)）。
このうち**キット配布物に触れたのは planning#399 のみ**である。

```text
git -C planning diff --stat 767a9d48 2c78212 -- tools/impl-handoff-kit/
  HOWTO.md                            |  9 +
  repo-template/.mcp.json             | 17 ++  ← 新規
  repo-template/AI_SETUP.md           | 53 +++
  repo-template/CLAUDE.md             |  1 +
```

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

🔴 **キット原本をバイト一致で採らなかった。** キット版（pin `2c78212`）は `github` という名前の
GitHub MCP サーバを含むが、**これを置くと CI の AI レビューが静かに死ぬ**。

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

分類は暫定的に **B（種 X・環流債務）**。キット原本の是正 PR は
[planning#402](https://github.com/endazon/project-planning/pull/402)。**マージ後に pin を進め、
キット原文で上書きして分類 A へ戻す。**

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
| `scripts/kit-sync-classification.json` | `.mcp.json` を **B（種 X）** へ登録。`AI_SETUP.md` の行へ種 1・種 2 のデルタを追記。`$comment` の pin 表記を `2c78212` へ |
| `AI_SETUP.md` | キット §4 を取り込み（旧 §4 → §5）。§4-1 は**是正版**（GitHub MCP を書かない理由）。§4-3 に本リポの固有デルタ注記 |
| `CLAUDE.md` | §生成 AI の活用 へ 1 行（**実測 +261 B**） |
| `docs/adr/IADR-0221_*.md`（新規） | Playwright の棲み分け |
| `docs/adr/IADR-0222_*.md`（新規） | `.mcp.json` のスコープと同名衝突 |
| `docs/adr/README.md` | 索引 2 行 |
| `planning` | pin `767a9d48` → `2c78212` |

## 5. 検証（実測）

```text
node scripts/check-kit-sync.js
  OK: キット 116 件を分類表と突合しました（A 78 件 / B 26 件 / C 4 件 / 対象外 8 件）  exit=0

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
2. `plan_refs` が **現 pin に存在しない計画リポのファイル**を指した（`20260817_mcp-json-github-server-collision.md`
   は planning#402 の中身）→ `plan_refs` から外し、**PR URL での参照と「pin を進めたときに加える」注記**に替えた

### 🔴 読書予算 —— 余白は **807 B** しかない

追加した 261 B の分だけ余白が縮んだ（1,068 B → 807 B）。

**保留中の `traceability.md` キット版取り込み（+1,371 B・追跡 #793 系）は、もともと 302 B 超過で入らず、
本 PR でさらに 261 B 遠のいた。** 減量が要る量は **302 B → 563 B** になった。
**この数値は判定に使わない**（判定は `scripts.repo.test.js` の #790 / #793 ラチェットが実ファイルから
ライブ計算する）。ここに残すのは、**本 PR が債務を増やした事実**を記録するためである。

## 6. 未了（本 PR では完了できないもの）

| # | 内容 | 理由 |
| --- | --- | --- |
| 1 | **`.claude/settings.json` への `mcp__context7__*` 追加** | 同ファイルは `Edit` / `Write` とも **deny**。**利用者に適用を依頼する** |
| 2 | **`/mcp` での `context7` 接続の目視確認** | ヘッドレス実行では承認プロンプトを出せない。**利用者が対話モードで確認する** |
| 3 | **`.mcp.json` を分類 A へ戻す** | [planning#402](https://github.com/endazon/project-planning/pull/402) のマージと pin 更新が要る |
| 4 | **`playwright-cli` の実導入と `--skills` の挙動確認** | ユーザー単位の導入であり CI で固定できない（`IADR-0221` §未解決） |

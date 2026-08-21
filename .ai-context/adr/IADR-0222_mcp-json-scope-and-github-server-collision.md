---
title: IADR-0222 `.mcp.json` は Context7 のみとし、GitHub MCP はユーザースコープへ置く（アクション組み込みとの同名衝突を避ける）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0179
  - IADR-0192
  - IADR-0221
plan_refs:
  - planning:draft/cross-project/20260817_mcp-json-github-server-collision.md
  - planning:draft/cross-project/20260817_skill-mcp-adoption-decision.md
  - planning:tools/impl-handoff-kit/repo-template/AI_SETUP.md (§4-1)
author: claude
created: 2026-08-17
updated: 2026-08-17
---

# IADR-0222: `.mcp.json` は Context7 のみとする

- 状態: Accepted
- 日付: 2026-08-17
- 決定者: 実装担当（AI）／計画側の是正（planning#402）と同一の判断

## 起点・関連

- 関連する計画書 ID: **`NFR`（無採番）** —— 開発ツールの設定という**工程の統制**であり、
  計画側の非機能要件表に当たる番号が無い（[IADR-0179](./IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）。
- 計画側の是正 PR: [planning#402](https://github.com/endazon/project-planning/pull/402)（キット原本の修正。**マージ済み**）
- 関連 IADR: [IADR-0221](./IADR-0221_playwright-cli-vs-test-runner-scope.md)（同じ配備で決めた Playwright の棲み分け）

## コンテキストと課題

キットが当初配布した `.mcp.json`（pin `2c78212`）は、`github`（GitHub MCP）と `context7` の 2 つを持っていた。

**この `github` サーバを本リポへ置くと、CI の AI レビューが静かに死ぬ。** 4 つの事実が連鎖する。

| # | 事実 | 出典 |
| --- | --- | --- |
| 1 | Claude Code は `${VAR}` が未定義で既定値も無いとき、**設定を読み込み、警告を出してリテラル文字列をそのまま使う** | Claude Code 公式ドキュメント（MCP / 環境変数展開） |
| 2 | 非対話モードは cwd の `.mcp.json` を読む。かつ claude-code-action は **`enableAllProjectMcpServers` を自動的に true にする** | claude-code-action base-action README ／ `restore-config.ts` |
| 3 | claude-code-action は **`github` という名前のサーバを組み込みで供給**し、**同名のカスタムサーバが組み込みを上書きする** | claude-code-action docs/configuration.md |
| 4 | PR 実行時、`.mcp.json` は**ベースブランチ版へ復元される**（PR head は untrusted 扱い） | `restoreConfigFromBase()` |

本リポの `claude-coding.yml` / `claude-code-review.yml` は `mcp__github__*` を 13〜15 件許可しており、
**レビューコメントの投稿そのもの**（`pull_request_review_write` / `add_comment_to_pending_review`）が
これに依存する。上書きされれば**レビュー本文が 1 文字も出ない**。

**そしてジョブは `success` で終わる。** `.claude/settings.json` 末尾のコメントが記録するとおり、
本リポは既に一度**同じ症状**（原因は許可リストの乖離）で **AI レビューが 2 週間退行**している。

事実 4 により、**本 PR 自身は無事である**（ベース `develop` に `.mcp.json` がまだ無い）。
**発火するのはマージ後の次の PR からである。**

## 検討した選択肢

| # | 案 | 評価 |
| --- | --- | --- |
| 1 | **`github` を落とし `context7` のみを置く** | **採用**。シークレット不要・CI 無影響・ローカルは各自のユーザースコープで賄える |
| 2 | `GITHUB_PAT` をワークフローへ渡す | 不採用。利用者がシークレットを作る必要があり、かつ toolset を絞った版が組み込みを上書きするため、アクションが要するツールが欠ける恐れが残る（未検証） |
| 3 | サーバ名を `github-pat` 等へ改名する | 不採用。ローカルのツール名が `mcp__github-pat__*` に変わり、`settings.json` の許可 15 件と 3 系統同期の対象がすべて書き換えになる |
| 4 | ワークフローへ `--setting-sources user` を渡して隔離する | 不採用。CI が `CLAUDE.md` と `.claude/settings.json` も読まなくなり、AI レビューの品質が大きく落ちる |

## 決定

### 決定 1: `.mcp.json` は `context7` のみとする

**`github` サーバを定義しない。** GitHub 操作の手段は面ごとに分かれる。

| 面 | 供給元 |
| --- | --- |
| **CI**（`claude-coding.yml` / `claude-code-review.yml`） | アクションの**組み込み** GitHub MCP。`.mcp.json` は関与しない |
| **ローカルの Claude Code** | 各自の**ユーザースコープ設定**（`--scope user`）。リポジトリでは配布しない |

**Context7 はプロジェクトスコープでよい。** 同名の組み込みが無く、API キー不要（匿名モード）で
環境変数の展開に依存しないため、**上の連鎖はどの段も成立しない**。

### 決定 2: 分類は **A（キットとバイト一致）** とする

**キット原本の是正（[planning#402](https://github.com/endazon/project-planning/pull/402)）がマージされたため、
環流債務を作らずに済んだ。** 本 PR は pin を是正後の計画 main（`f216783`）へ進めており、
`.mcp.json` はキット原本とバイト一致である（`cmp` で確認）。

> **起案時は分類 B（種 X）を予定していた。** キット原本がまだ `github` を含んでいたためである。
> **計画側の是正が先に着地したので A で置けた** —— 暫定の分類 X は環流債務の測定値を汚すため、
> 避けられるなら避けるのが望ましい（[IADR-0192](./IADR-0192_kit-sync-classification-and-check.md)）。

### 決定 3: ワークフローの許可リストへ `mcp__context7__*` を加えない

3 系統同期（`.claude/settings.json` / `claude-coding.yml` / `claude-code-review.yml`）の対象は
**その面で実際に使えるツール**である。**CI では `context7` を使わせない**ため、加えない。

理由: Context7 は `npx -y @upstash/context7-mcp` の stdio サーバであり、**CI の毎回の実行で
npm レジストリへの取得が走る**。AI レビューは外部ドキュメントの参照を要さない（差分と計画書の突合が仕事である）。
**ローカルの技術検討・ADR 執筆でのみ使う。**

## 影響

- `.claude/settings.json` への `mcp__context7__*` 追加が**必要になる**（ローカル面）。
  同ファイルは `Edit` / `Write` とも deny のため、**利用者に適用を依頼する**
- ワークフロー 2 本は**無変更**（決定 3）
- `kit-sync-classification.json` の A へ 1 件追加（決定 2）

## 未解決

- **実 CI での再現は取っていない。** 結論はアクションと Claude Code の仕様・実装コードの読みによる。
  再現には `.mcp.json` を載せたブランチをベースとする PR が要り、**それ自体が AI レビューを壊す**。
  是正を先に入れる判断を採った（計画側 planning#402 も同じ判断である）。

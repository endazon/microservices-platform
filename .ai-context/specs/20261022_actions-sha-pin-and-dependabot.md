---
title: 作業仕様書 — GitHub Actions を commit SHA で固定し、Dependabot の対象に nuget と npm を足す
type: spec
status: done
related_ids: [NFR, IADR-0580, IADR-0578]
author: claude
created: 2026-10-22
updated: 2026-10-22
issue: "GHSA-552h-47r7-6v6p"
---

# 作業仕様書 — Actions の SHA 固定と Dependabot（GHSA-552h-47r7-6v6p の 2 本目）

> 本仕様書は実装着手前に作成した（2026-10-22）。1 本目（ワークフローの信頼の絞り込み＋入力の扱い）は IADR-0578、3 本目（イメージのスキャン・CodeQL の JS/TS）は別の作業仕様書で扱う。

## 起点

- GHSA-552h-47r7-6v6p（多層防御の強化。サプライチェーンの固定と依存の更新）。
- 計画: NFR（統制・セキュリティ）。

## 方式

1. 全ワークフローの外部の `uses:` を 40 桁の commit SHA へ固定し、同じ行に版の注記（`# vX.Y.Z`）を置く。SHA は各アクションのリポジトリの現行のメジャーのタグ（`git ls-remote`。注釈付きタグは指す commit）から引き、注記はそのタグと同じ commit を指す最も詳しい版にする。版は上げない（固定だけ）。
2. `check-action-versions.js` は SHA 固定の行の版の注記を版として読む（メジャーの退行の検査を保つ）。
3. `dependabot.yml` に `nuget`（`/src`。`Directory.Packages.props`）と `npm`（`/src`。pnpm workspace）を足す。`github-actions` は SHA と注記を対で更新する。
4. `scripts.repo.test.js` で「全ワークフローの外部の `uses:` が SHA 固定＋版の注記」と Dependabot の対象を固定する（変異試験つき）。

起動条件・job 名・必須チェックは変えない（`uses:` の参照だけを変える）。

## 母集合（規則 9・10）

- `.github/workflows/*.yml` の `uses:` を全数走査した（98 箇所・16 アクション。2026-10-22）。`anthropics/claude-code-action` は既に SHA 固定（版の注記あり）。ローカル参照（同じリポジトリの再利用ワークフロー ci-failure-issue.yml）は対象外。
- `.github/actions/` は無い（複合アクションなし）。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | 外部の `uses:` がすべて SHA 固定＋版の注記 | `scripts.repo.test.js`「外部の uses: は…」と変異試験（タグ・注記なし・短い SHA → 検出） |
| AC2 | 版の退行の検査が SHA 固定でも働く | `check-action-versions.js --self-test`（注記の読み取りの 3 件）と本走（`--compare-with-ref origin/develop`）が緑 |
| AC3 | Dependabot が github-actions / nuget / npm を対象にする | `scripts.repo.test.js`「dependabot.yml は…」 |
| AC4 | 起動条件・job 名・必須チェックが変わらない | `git diff` が `uses:` の行・dependabot.yml・検査器・試験だけ |
| AC5 | CI の全ジョブが固定後の SHA で緑 | この PR の CI |

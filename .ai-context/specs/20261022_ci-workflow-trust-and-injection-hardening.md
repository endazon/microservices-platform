---
title: 作業仕様書 — CI の AI 実装ワークフローの起動を本文の書き手で絞り、許可ツールを最小化し、外から与えられる値をシェルへ展開しない
type: spec
status: done
related_ids: [NFR, IADR-0578, IADR-0232]
author: claude
created: 2026-10-22
updated: 2026-10-22
issue: "GHSA-552h-47r7-6v6p"
---

# 作業仕様書 — CI ワークフローの信頼の絞り込みと入力の扱い（GHSA-552h-47r7-6v6p）

> 本仕様書は実装着手前に作成した（2026-10-22）。GHSA-552h-47r7-6v6p は論理単位で 3 本の PR に分ける。本書は **1 本目（ワークフローの信頼の絞り込み＋入力の扱い）** を扱う。2 本目（Actions の SHA 固定・Dependabot）と 3 本目（イメージのスキャン・CodeQL の JS/TS）は別の作業仕様書で扱う。

## 起点

- GHSA-552h-47r7-6v6p（多層防御の強化。CI で AI が書き込み権限を持って動く面と、外から与えられる値の扱いを絞る）。
- 計画: NFR（統制・セキュリティ）。製品の FR には当たらない。

## 方式

1. `claude-coding.yml` の claude ジョブの `if` を、句ごとに**本文の書き手**の `author_association` が OWNER / MEMBER / COLLABORATOR のときだけ真にする（起動した人ではなく、AI が読む本文を書いた人で判定する）。
2. `claude-coding.yml` の許可ツールを最小化する: `find`・`awk` を外し、`node` は `node scripts/` の前置の形だけにする。システムプロンプトの許可一覧の写しも同じに直す。`check-ai-workflow-config.js` の非対称の宣言（`CODING_ONLY_BASH` / `REVIEW_ONLY_BASH`）と狭めた対（`CODING_NARROWED_TOOLCHAIN`）を合わせる。レビュー用（`claude-code-review.yml`）は変えない。
3. `scripts/gen-changelog.js` は git を引数の配列（`execFileSync`）で呼び、シェルを介さない。タグ名は参照名の形（英数字と `.` `_` `/` `-`、先頭は英数字、`..` なし、200 文字以内）を確かめ、外れたものは生成から外して警告する。
4. `obsidian-plugin-release.yml` の版の突合のステップは、タグを env で受けて run の本文へ式を展開しない。
5. 全ワークフローの run の本文に、外から与えられる値（`github.event.*` / `inputs.*` / `github.head_ref` / `github.ref` / `github.ref_name`）の式を直接展開しないことを `scripts.repo.test.js` で固定する。

起動条件（`on:`）・job 名・必須チェックは変えない（1 は job の `if` を狭めるだけで、`claude-coding` は必須チェックではない）。

## 母集合（規則 9・10）

- run の本文の式展開: 全 `.github/workflows/*.yml` を行で走査した（2026-10-22）。外から与えられる値を展開していたのは `obsidian-plugin-release.yml` の 1 箇所だけ。他（`matrix.unit`・`github.sha`・`github.event_name`・同じジョブのステップ出力）は対象外の文脈。
- シェル経由の child_process: `scripts/` の `execSync` は `gen-changelog.js`・`check-commit-messages.js`・`check-landed-subjects.js`。後 2 者は固定の引数か、CI の `github.sha` などで組んでおり、外から与えられる参照名を受けない。本 PR は助言の対象の `gen-changelog.js` に絞る。
- 許可ツール: `claude-coding.yml` と `claude-code-review.yml` の 2 つ。最小化は書き込み権限を持つ実装用に絞った。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | claude ジョブの `if` の 4 句すべてが本文の書き手を判定する | `scripts.repo.test.js`「claude-coding.yml は句ごとに…」と変異試験（1 句から外す・対象を取り違える → 赤） |
| AC2 | 実装用の許可ツールに `find` / `awk` / `node:*` が無く、`node scripts/` がある | 同「許可ツールは最小化した形を保つ」・`node scripts/check-ai-workflow-config.js`（自己試験の対・逆向きを含む） |
| AC3 | gen-changelog はシェルを介さず、受け付けない形のタグを外して他の節を出す | 同「参照名の判定」「シェル経由で呼ばない」「実走（一時リポジトリ）」。旧版へ戻すと赤 |
| AC4 | run の本文に外から与えられる値の式が無い | 同「run の本文に…」と変異試験（1 行形・ブロック形・現物へ戻す → 検出） |
| AC5 | 起動条件・job 名・必須チェックが変わらない | `git diff` で `on:` と job のキーに差分が無い |
| AC6 | CHANGELOG の生成結果が変わらない（安全なタグだけの履歴） | 新旧の `gen-changelog.js` の出力の一致（手元の実測） |
| AC7 | 既存の試験・検査器が緑 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・文書系検査 |

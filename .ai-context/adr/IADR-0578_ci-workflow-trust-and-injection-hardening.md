---
title: IADR-0578 CI の AI 実装ワークフローは本文の書き手が OWNER / MEMBER / COLLABORATOR のときだけ起動し、許可ツールを最小化する。外から与えられる値はシェルへ展開せず、git は引数の配列で呼ぶ
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0232]
author: claude
created: 2026-10-22
updated: 2026-10-22
related_specs:
  - ../specs/20261022_ci-workflow-trust-and-injection-hardening.md
---

# IADR-0578: CI ワークフローの信頼の絞り込みと入力の扱い（GHSA-552h-47r7-6v6p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: オーナー指示（2026-10-22）・claude（判定の置き場と最小化の範囲）

## 起点・関連

- 起点: GHSA-552h-47r7-6v6p（多層防御の強化）。本 IADR はその 1 本目（信頼の絞り込み＋入力の扱い）を扱う。
- 前提: [IADR-0232](./IADR-0232_ci-pr-latency-reduction.md)（ワークフローの権限を YAML で絞る運用。#1581 の追記）。

## コンテキストと課題

`claude-coding.yml` は書き込みのトークンを持つ AI の実装セッションを起動する。AI が読む本文（コメント・レビュー・issue）の信頼は、起動した人とは別に決まる。許可ツールにはスクリプトの実行に要る以上の汎用のコマンドが含まれていた。また、CHANGELOG の生成とプラグインの配布の 2 箇所は、参照名の受け渡しを引数の配列と env へ統一する余地があった。

## 検討した選択肢

1. **アクション内蔵の権限確認だけに任せる** — 起動した人の確認であり、本文の書き手は見ない。
2. **job の `if` で本文の書き手を判定する（採用）** — ランナーを確保する前に止まり、句ごとに静的に検査できる。
3. **ワークフローを撤去する** — worker のフォールバック経路（`docs/ai-orchestration.md`）を失う。

## 決定

1. **claude ジョブの `if` の各句に、本文の書き手の `author_association` が OWNER / MEMBER / COLLABORATOR である判定を置く**（`issue_comment` / `pull_request_review_comment` は comment、`pull_request_review` は review、`issues` は issue の書き手）。`on:` は変えない。
2. **実装用の許可ツールから `find` と `awk` を外し、`node` は `node scripts/` の前置だけにする。** レビュー用は変えない。差は `check-ai-workflow-config.js` の宣言（`REVIEW_ONLY_BASH` に `awk`・`CODING_ONLY_BASH` から `find` を削除・`CODING_NARROWED_TOOLCHAIN`）で表し、宣言外の差は従来どおり ERROR にする。広げるときは本 IADR を改定する。
3. **`gen-changelog.js` は `execFileSync('git', [...])` で呼び、参照名は形を確かめてから渡す。** 外れたタグは生成から外して警告する（生成全体は止めない。CHANGELOG の自動更新を止めないため）。
4. **run の本文に外から与えられる値の式を展開しない。** 値は env で渡す。`scripts.repo.test.js` が全ワークフローを行で走査して固定する。

## 結果

- 良い影響: AI の実装セッションは信頼できる書き手の本文でだけ起動する。参照名は常に引数か env として渡る。
- 悪い影響: 協力者でない人の issue へ `@claude` を使うには、書き手の条件を満たす人がコメントで呼ぶ必要がある。実装セッションは `node -e` や環境変数の要る検査を走らせられない（未検証と書く）。`.claude/settings.json` との突き合わせは、宣言した狭めた形（`Bash(node scripts/:*)`）を広い形（`Bash(node:*)`）が覆うものとして扱う（ローカルの方が広い。CI は警告を失敗として扱うため）。

## 残余

1. 書き手の条件を満たす人の呼び出しでも、AI はスレッド内の他の書き手の本文を読む。許可ツールの最小化と、ブランチ保護（`develop` の必須チェック）が後段の守りになる。
2. `node scripts/` の前置は、AI が scripts/ へ書いたファイルの実行を妨げない（実装セッションの性質上、書いて試すことは止めない）。差分は PR のレビューに載る。`Bash(node scripts/:*)` は前置きの一致なので、`..` を含んで scripts/ の外を指す形も通り得る（許可の判定が `..` を正規化するかは未確認）。受容する理由: 実行したものが成果物に残れば差分として PR のレビューに載り、`develop` はブランチ保護（必須チェック）で守られる。前置きはワンライナーの実行（`node -e`）を外すための最小化であり、パスの閉じ込めは目的にしていない。
3. `gen-changelog.js` が受け付けない形のタグを外すと、その前後の節の範囲は隣の安全なタグまで広がる（コミットは失われず、隣の節にまとまる）。
4. Actions の SHA 固定・依存の自動更新・イメージのスキャン・CodeQL の JS/TS は GHSA-552h-47r7-6v6p の 2 本目・3 本目で扱う。

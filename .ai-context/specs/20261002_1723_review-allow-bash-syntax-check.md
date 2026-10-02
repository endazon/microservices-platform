---
title: AI レビュー／実装ワークフローの --allowedTools に bash の構文検査と k8s-local-down.test.sh の固定形を足す（#1723 の必須チェック）
type: spec
status: done
related_ids: [NFR, IADR-0198]
author: claude
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/ (NFR 運用性・CI)
issue: "#1723"
---

# 仕様書: AI レビューがシェルの変更を検証できるよう、bash の 2 形を許可する（#1723）

> 本仕様書は実装着手前に作成する。起点は PR #1723（#1722 の helm v4 修正）。必須チェック `claude-review` が 2 回続けて赤になった（run 36997160031 の job 110806413637・110808541264）。

## 起点となる計画書（トレーサビリティ）

- 要求: 無採番の `NFR`（CI の運用性）。製品の FR には当たらない。
- 関連 IADR: IADR-0198（判定投稿の検査。権限拒否の検査と 2 本セットで配布）。
- 利用者裁定（2026-10-02）: 「許可を足す別 PR」。#1723 には同梱しない。

## 事象

- レビュー本文は 2 回とも 🔴/🟡/🟢 で指摘なしだった。`check-review-verdict` も OK だった。
- 落としたのは `Check permission denials`（`scripts/check-permission-denials.js`）である。
- レビュー役が、シェルの変更を検証しようとして次を打ち、拒否された。
  - `bash -n scripts/lib/mesh-mtls-mode.sh`
  - `bash scripts/k8s-local-down.test.sh | tail`
  - `helm list --help`
- `--allowedTools` に `bash` が 1 つも無い。そのため、シェルを触る PR ではレビュー役が許可の外へ手を伸ばし、必須チェックが落ちることがある（#1718 では起きず、#1723 では 2 回続けて起きた）。

## 変更

1. `.github/workflows/claude-code-review.yml` と `.github/workflows/claude-coding.yml` の `--allowedTools` に、次の 2 つを足す。
   - `Bash(bash -n:*)`: 構文検査だけで、スクリプトは実行しない。
   - `Bash(bash scripts/k8s-local-down.test.sh)`: 引数を固定した形にする。前方一致で別のスクリプトへ広がらない。
2. レビュー用プロンプトの「使える Bash コマンドの一覧」に、上の 2 形と「他の `bash <スクリプト>` や `helm` は拒否される」を書き足す。
   - 一覧に無いものは試さずに未検証と書く、という既存の規律を保つためである。

## 足さないもの（理由）

- `Bash(bash:*)`: 任意のスクリプトを実行できてしまう。
- `helm`: レビューは実機の helm を持たない。読むだけなら Read で足りる。
- `.claude/settings.json` の allow:
  - 権限設定の変更は利用者の明示の指示が要るので、本件の裁定の射程外とした。
  - `check-ai-workflow-config.js` は「settings.json の allow に無い」として warn（exit 0）を出す。これは受容する。

## 母集合（規則 9）

- `--allowedTools` を持つワークフローは `claude-code-review.yml` と `claude-coding.yml` の 2 本である（`grep -n 'allowedTools "' .github/workflows/*.yml`）。2 本とも直す。
- 写しの一覧（レビュー用プロンプトの「正の一覧」）は `claude-code-review.yml` の 1 か所だけにある。

## 規則 10・11

- 規則 10: 「`bash` は一覧に無い」と読める記述は、プロンプトの一覧だけである。その一覧を更新した。
- 規則 11: 窓（時間差）を扱わないので該当しない。

## 検証

- `node scripts/check-ai-workflow-config.js`: OK（settings.json についての warn 2 件は受容する）。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 853 件すべて合格。
- 本 PR のマージ後、#1723 に develop を取り込み、`claude-review` が緑になることを確かめる。

---
title: 自動 CHANGELOG PR 用 PAT の登録を利用者が迷わず行える手順にし、登録後に必須 check が通ることを事前に確かめる
type: spec
status: done
related_ids:
  - NFR
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs: []
---

# 作業仕様書: 自動 PR 用 PAT の登録手順と、登録後に落ちる箇所の事前確認（#1237）

## 背景

#1237 の AI 側の作業（`AI_SETUP.md` へ用途・権限・未設定時の挙動を記す）は PR #1285 で着地済みである。
残るのは利用者によるシークレット登録だけだが、2026-09-26 の利用者依頼は
「どのトークン種別・権限か、`gh secret set` の形（値なし）、確認方法」を正確に書くことである。

## 起点 ID

無採番 `NFR`（メタ作業。CI とブランチ保護の噛み合わせであり製品の非機能要件に当たる番号が無い）。
#1285 の判断をそのまま継ぐ（issue 件名の `ADR-0007` / `ADR-0008` は主題が一致しない誤帰属。20260905 の仕様書参照）。

## 事前に実測したこと（2026-09-26・`origin/develop` `e868ddda`）

```console
$ gh api repos/endazon/microservices-platform/branches/develop/protection --jq '{checks:.required_status_checks.contexts, strict:.required_status_checks.strict, sig:.required_signatures.enabled, reviews:.required_pull_request_reviews}'
{"checks":["build-and-test","lint","commit-messages","pr-title","image-build","static-checks-units","claude-review","scripts-tests"],"reviews":null,"sig":false,"strict":false}

$ gh pr view 1475 --json mergeStateStatus,statusCheckRollup --jq '{m:.mergeStateStatus,n:(.statusCheckRollup|length)}'
{"m":"UNKNOWN","n":0}      ← 現在の CHANGELOG PR も check 0 件のまま

$ node scripts/check-commit-messages.js --title "docs(NFR): CHANGELOG を自動更新" --author endazon --pr-number 1475
✓ PR タイトルが規約に適合（exit 0）

$ git log --format='%h %an <%ae> | %cn | %s' origin/develop..origin/automation/changelog-update-develop
9d1f11e9 endazon <61076714+endazon@users.noreply.github.com> | github-actions[bot] | docs(NFR): CHANGELOG を自動更新
$ node scripts/check-commit-messages.js --range origin/develop..origin/automation/changelog-update-develop
検査対象 1 件 / 除外 0 件 / ✓ すべてのコミットが規約に適合（exit 0）
```

- 8 件の必須 check のうち `claude-review` は `claude-code-review.yml` の `if:` が `automation/` ブランチを除外する。
  skip されたジョブは必須チェックとして成功扱いであり、マージを止めない。
- 他 7 件は `paths:` を持たず全 PR で起動する（`docs/ai-workflow.md` の必須チェック表）。
- コミットの author は既に `endazon`（`create-pull-request` の既定 author が `github.actor`）であり、
  bot 除外に頼らず件名が規約を満たしている。PAT で PR の作成者が利用者になっても `pr-title` は落ちない。

## 登録後に初めて表面化する罠（手順へ書く）

- `secrets.AUTOMATION_PR_TOKEN || secrets.GITHUB_TOKEN` は**空のときだけ**フォールバックする。
  **期限切れの PAT は空ではないので使われ、run が認証エラーで赤くなる。** 有効期限を必須にしている以上、必ず起きる。

## 変更

- `AI_SETUP.md` §6: 登録手順（作成画面の欄と値・`gh secret set` の対話入力・確認コマンドと期待値・失効時の挙動と戻し方）を追記する。

## 受け入れ基準

- [x] 利用者が値以外を判断せずに登録と確認を終えられる手順が `AI_SETUP.md` にある
- [x] 登録後に `pr-title` / `commit-messages` が落ちないことを事前に確かめ、証跡を残した
- [ ] 登録後の実測（8 check の起動と `CLEAN`）—— **利用者の登録後**。本 PR では閉じない

## 母集合

`AUTOMATION_PR_TOKEN` を全追跡ファイルで引いた（`CHANGELOG.md` を除く）: `changelog.yml:52,61` / `AI_SETUP.md` / 本仕様書以前の仕様書 2 件。
手順の正本は `AI_SETUP.md` 1 か所に置き、`changelog.yml` の注記は変えない（手順を 2 か所に持たない）。

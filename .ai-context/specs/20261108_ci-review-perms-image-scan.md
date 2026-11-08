---
title: 作業仕様書 — AI のジョブから要らない id-token を外し、bot の PR では AI レビューを走らせない。配備する第三者のイメージを週次で走査し、更新を Dependabot で知らせる
type: spec
status: in-progress
related_ids: [NFR-18, IADR-0708, IADR-0232, IADR-0584, IADR-0514]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - 20260926_1581_workflow-token-permissions.md
---

# 作業仕様書 — AI のジョブの権限・第三者のイメージの走査

> 着手 2026-11-08（develop `3ba6611f1`）。判断の記録は IADR-0708。非公開の監査（クラウド稼働前・第 3 ラウンド）の medium 1 件と low 1 件の是正。

## 着手前の確かめ（今の develop に残っているか）

| 項目 | 結果（develop `3ba6611f1`） |
| --- | --- |
| `claude-code-review.yml` の `id-token: write` | **残っている**（ジョブの permissions）。`claude-coding.yml` にも在る |
| レビューの checkout の `persist-credentials` | **既に `false`**（IADR-0232 / #1581）。ただしアクションが起動時に `origin` をトークン入りの URL へ書き換える（`allowed_non_write_users` を使わない分岐。アクションの `git-config.ts` の `replaceCheckoutCredentials` で確かめた） |
| レビューでの `npm ci` などの事前導入 | **本リポには無い**（setup-dotnet だけ）。lifecycle script の経路は無い |
| bot の PR での実行 | **残っている**。ジョブの if が除くのは `dependabot[bot]` の起動と `automation/` のブランチだけ。`allowed_bots: '*'`。許可に `Bash(node:*)`・`dotnet build/test` 等 |
| 第三者のイメージの走査 | **無い**。`images.yml` の Trivy は自製のイメージだけ・報告だけ |
| 第三者のイメージの更新の通知 | **無い**。`dependabot.yml` に docker が無い。`renovate.json` は npm だけ（IADR-0203 の棲み分け） |

- 本リポの PR の作成者（直近 200 本）: 人 160・`dependabot[bot]` 25・`github-actions[bot]` 15。後 2 者は従前から AI レビューが走らない
  （dependabot は if で除外、`github-actions[bot]` は GITHUB_TOKEN で作られるので pull_request が起動しない）。bot の除外を広げても今の流れは変わらない。

## 方式

1. `claude-code-review.yml`・`claude-coding.yml` の `id-token: write` を外す。アクション（固定の SHA）は `github_token` が渡ると
   その値を使い、OIDC を求めない（`src/github/token.ts` の `setupGitHubToken`）。workload identity の入力は渡していない。
2. `claude-review` のジョブの if に「作成者のログインが `[bot]` で終わらない」「起動した者のログインが `[bot]` で終わらない」を足す。
   止め方は**ジョブ単位の if**（check は skipped。GitHub は必須チェックの skipped を合格として扱う。dependabot の PR で従前から同じ）。
   ジョブ名・`on.pull_request.types` は変えない。`allowed_bots` は `''`（既定。bot の起動をアクションも拒む）。
   - `user.type` は使わない（#524 の試験が全ワークフローで禁じる。ログインの接尾辞で判定する）。
   - `GH_TOKEN` のステップの env は残す。アクション自身が同じ値を `GH_TOKEN`・`GITHUB_TOKEN` に入れる（`run.ts`）ので、外しても届く範囲は変わらない。
3. 第三者のイメージの走査 `third-party-image-scan.yml` を足す。対象は `check-image-digests.js --scan-targets`（`deploy/` の第三者・digest 固定・重複なし）。
   Trivy（`images.yml` と同じ固定の版）で CRITICAL / HIGH・修正版ありを数え、SARIF を code scanning へ上げる。
   - 週次・dispatch: 1 件でもあれば脚を落とし、`ci-failure-issue.yml` が起票する。
   - PR（`deploy/**` 等の paths）: 注記だけで落とさない。必須チェックにしない。
   - 受け入れ: `.github/image-scan.trivyignore.yaml`（id・statement・90 日以内の expired_at）。
   - ［2026-11-08 追記 / #2516 のレビュー］ignore の YAML がアクション・Trivy の固定の版で読まれることをソースで確かめた（IADR-0708 決定 3）。Trivy の版を明示し、code scanning の category は digest を落として記号を `-` にした名前にした。
4. `dependabot.yml` に docker（`/deploy/cloud/**`・1 本の PR へまとめる）を足す。片側の更新は `[digest-mismatch]` が止めるので、手順書で残りを揃える。
5. 手順書 `docs/operations/third-party-image-scan-runbook.md`（赤の扱い・受け入れ・更新の PR の仕上げ）。

## 母集合（規則 9・10）

- `id-token` の全出現（`grep -rn id-token` から specs と CHANGELOG を除く）: 2 つのワークフロー・`scripts.repo.test.js` の JOB_SCOPES の 2 行・
  `docs/ai-workflow.md` の権限の表の 2 行。全部直す。
- `allowed_bots`: `claude-code-review.yml` の 1 か所だけ（`claude-coding.yml` は持たない）。
- ジョブの if の `dependabot[bot]` の句を変えたことに依る記述: `docs/ai-workflow.md` の表の行に bot の扱いを書き足した。
- `security-events: write` を持つジョブ: `codeql.yml` に加え新しい `scan`。JOB_SCOPES と `docs/ai-workflow.md` の表に足した。
- `ci-failure-issue.yml` の呼び出し側（#1588 の試験）: 新しい `report-failure` は `actions: read` を与える。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | 2 つの AI のジョブが `id-token: write` を持たない | `scripts.repo.test.js` の #1581 の JOB_SCOPES・IADR-0708 の言明 |
| AC2 | `claude-review` は作成者か起動した者が bot の PR ではジョブの if で走らず、ジョブ名・types は不変、`allowed_bots` は `*` でない | IADR-0708 の言明と陰性対照 |
| AC3 | 走査は週次・dispatch で修正版のある CRITICAL / HIGH を落とし、PR では落とさず、落ちたら起票する | 同上 |
| AC4 | 受け入れのファイルの各項目は CVE・statement・90 日以内の expired_at を持つ | 同上 |
| AC5 | Dependabot の docker が `deploy/cloud/**` を見る | 同上 |
| AC6 | 走査の対象は `deploy/` の第三者・digest 固定・重複なしで、RabbitMQ・Prometheus・Alertmanager・collector・OpenBao・Keycloak を含む | `check-image-digests.js --self-test`・IADR-0708 の言明 |
| AC7 | 陰性対照: 直しを外した（2 つのワークフローと dependabot.yml を develop に戻した）状態では AC1・AC2・AC5 が赤。`scanTargets` の deploy の絞り・補間の除外を外すと自己試験が赤 | 手で確かめ、PR 本文に記録 |
| AC8 | 既存の試験が全部緑 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` |
| AC9 | PR の run で走査のワークフローが動き、対象の件数と各イメージの件数を出す（PR では緑） | PR の run |

## 残余

- 週次の走査の最初の結果（古い版の Prometheus・Alertmanager・collector・RabbitMQ 等）は、マージ後の最初の週次で赤になる見込み。
  上げるか期限つきで受け入れるかは手順書に従って利用者が判断する。RabbitMQ の 4.x への移行は計画へ載せる。
- bot の PR は AI レビューを受けない（人のレビューが持つ）。

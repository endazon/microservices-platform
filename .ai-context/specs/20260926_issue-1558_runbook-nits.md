---
title: 作業仕様書 — #1558 出力トークン実測・秘密項目投入・リセット近接 MTA の Runbook の小さな不整合を直す
type: spec
status: done
related_ids: [FR-11, FR-05, SC-15, SC-22, ADR-0025, ADR-0044, ADR-0078, IADR-0466]
author: Claude Opus 5.5 (worker)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
related_specs:
  - 20260926_issue-380_output-token-measurement-runbook
  - 20260926_issue-1245_pr-d-state-measurement-runbook
  - 20260926_issue-1111_llm-budget-alert-configurable
issue: "1558"
---

# 作業仕様書 — #1558 Runbook の小さな不整合 3 件

## 起点

- issue #1558。#1536・#1538 の監査で見つかった、ブロックしない指摘 3 件。文書だけの変更（コード・deploy は触らない）。

## 変更（3 件）と、編集前に確かめた事実

### 1. 出力トークン実測 Runbook「限界」節の「月次予算の上限アラートは無い」

- 対象: `docs/operations/llm-output-token-measurement-runbook.md` 474-475 行。
- 事実: #1539（`21e08c34`、`origin/develop` の祖先）で用途別の上限アラート `LlmMonthlyBudgetExceeded` が配線された
  （`deploy/prometheus/alerts.yml` 344 行〜）。金額 `Llm:Budget:MonthlyLimits` は既定を持たず
  （`LlmBudgetOptions.cs`、`appsettings.json` は `{}`）、**未設定のあいだ発火しない**。
  既定の受信先は `default-null`（`deploy/alertmanager/alertmanager.yml`）で、通知は届かない。
  金額をコミットで置くと、月次確認 Runbook を同じ変更で終了させる必要がある（CI が食い違いを落とす。
  `llm-cost-monthly-review-runbook.md` §金額を設定する手順 2）。
- 直し方: 「無い」を「配線済み・金額未設定のあいだ不活性」へ改め、金額を設定すれば安全網として使えること、
  ただし検知だけで費用は止めないこと・設定は所有者が月次確認 Runbook の手順で行うことを書く。
  金額が無い限り上限は承認額と §3-5 の停止でしか守られない、という結論は残す。
- trace ブロックへ `IADR-0466` を足す（可視本文には書かない）。

### 2. 秘密項目のコンソール投入 Runbook 手順 4 の `deploy/llm-gateway`

- 対象: `docs/operations/secret-item-console-injection-runbook.md` 163-164 行。
- 事実: `deploy/helm/microservices-platform/templates/deployment.yaml` の `metadata.name: {{ $name }}-service` と
  values のキー `llmgateway`（`values.yaml` 209 行）から、Deployment 名は `llmgateway-service`。
  `llm-gateway` は compose のサービス名（`deploy/docker-compose.yml` 383 行のコメント）。
  名前空間 `microservices-platform` は values の `namespace.name` と一致しており変えない。
- 直し方: 2 行とも `deploy/llmgateway-service` へ。

### 3. リセット近接 MTA Runbook §2.3（C3）の「戻す」が C3a 用にしか書かれていない

- 対象: `docs/operations/password-reset-relay-state-measurement-runbook.md` 539-548 行。
- 事実: `boky/postfix:v5.1.0`（`deploy/mail-relay/mail-relay.yaml` 61 行）の `scripts/functions.sh` は
  `do_postconf -e "smtpd_client_restrictions=permit_mynetworks,permit_sasl_authenticated,reject"` を実行する
  （`gh api repos/bokysan/docker-postfix/contents/scripts/functions.sh?ref=v5.1.0` の 333 行で確認。
  既存仕様書 `20260906_issue-1245_nearby-mta-relay` の V9 とも一致）。したがってスナップショットの
  `explicit=[...]` は空にならず、`postconf -X smtpd_client_restrictions` は Postfix の既定値へ戻して控えた値に戻らない。
- 直し方: 「戻す」のコードブロックに C3b 用の `postconf -e 'smtpd_client_restrictions=<値>'`（値はスナップショットの
  explicit）を明示し、C3b に `-X` を使わない理由を 1 文添える。確かめの `postconf -h` も C3b の項目名を併記する。

## 母集合（規則 9・10）

- 誤りの側の文字列で追跡下の全ファイルを走査した:
  - `git grep -n "deploy/llm-gateway\|deployment/llm-gateway"` → docs は対象の 2 行のみ。他は凍結済みの
    `.ai-context/specs/20260926_issue-380_output-token-measurement-runbook.md` 142 行（見つけたものの記録。書き換えない）。
  - `git grep -n "上限アラート\|予算のアラート\|予算アラート" -- docs` → 「無い」と書くのは対象の 1 か所のみ。
    `operations.md` の該当箇所は #1539 で更新済み。
  - `smtpd_client_restrictions` の戻し方を書く docs は対象の Runbook のみ（§0.5 の表は explicit の有無で分岐しており正しい）。
- 本変更で新たに誤りになる記述: 無し（数値・導出値を足していない）。

## 受け入れ基準

- 3 件が issue の記述どおりに直っている。
- `docs/` の可視本文に計画 ID・IADR・仕様書名・修飾付き issue 参照を足していない。変更した 3 文書の `updated:` が 2026-09-26。
- オフライン検査器（`check-trace-blocks` / `check-doc-type-vocabulary` / `check-reading-budget` / `check-doc-links` /
  `check-cross-repo-refs` / `check-plan-id-qualification`）が通る。
- Runbook の手順は実行しない（稼働クラスタに触れない）。

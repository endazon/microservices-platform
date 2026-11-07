---
title: 運用 Runbook — 計画リポジトリを読む CI のトークン（PLANNING_REPO_TOKEN）の権限を読み取りだけ・期限つきに絞る
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR]
iadrs: [IADR-0701, IADR-0423]
specs: [20261107_nuget-lock-and-pr-ci-token]
-->

# 運用 Runbook: 計画リポジトリを読む CI のトークン

> 対象は本リポジトリの Actions の secret `PLANNING_REPO_TOKEN`。使うのは `ci.yml` の計画 ID レンジの突合（develop / main への push のときだけ）と、
> 週次の `backlog-audit.yml` の 2 か所である。どちらも計画リポジトリの公開ファイル 1 つを読むだけで、書き込みはしない。
>
> 🔴 **本書の手順はすべてオーナーが行う。** トークンの値はチャット・issue・ログへ出さない。
>
> **状態を `draft` に留めている理由**: 現在のトークンの権限をリポジトリの側から確かめられない（値も設定も見えない）。

## あるべき形

| 項目 | 値 |
| --- | --- |
| 種類 | fine-grained personal access token |
| Resource owner | `endazon` |
| Repository access | Only select repositories → `project-planning` の 1 つだけ |
| Repository permissions | Contents: Read-only（Metadata: Read-only は自動で付く）。ほかは No access |
| Expiration | 90 日以内 |

## 手順

1. GitHub の Settings → Developer settings → Personal access tokens → Fine-grained tokens で、上の表のとおりに新しいトークンを作る。
2. 本リポジトリの Settings → Secrets and variables → Actions で `PLANNING_REPO_TOKEN` を新しい値で上書きする。
3. 本リポジトリの Actions で `backlog-audit.yml` を手動で実行し、報告の「計画 ID レンジ宣言の鮮度」の節が「未確認」でないこと（突合した種別の数が 0 でないこと）を確かめる。
4. 古いトークン（classic を含む）を GitHub の設定から削除する。
5. Settings → Secrets and variables → Dependabot に同名の secret が残っていれば、使う設定が無い（`.github/dependabot.yml` は計画リポジトリを取らない）ので削除する。
6. 期限の 2 週間前に 1. から繰り返す（予定表へ入れる）。

## 切り戻し

- 3. で「未確認」になったら、作ったトークンの Repository access と Contents の権限を見直す。突合は警告だけなので、CI が止まることは無い。

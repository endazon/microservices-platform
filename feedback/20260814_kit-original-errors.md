---
title: キット原文の誤り 2 件（new-spec.md のテンプレート名・pr-title.yml の他リポ番号の表記）
type: plan-feedback
status: open
category: その他
related_ids: [NFR, IADR-0115]
source_repo: microservices-platform
source_ref: "claude/issue-response-handoff-2hl25v / docs/specs/20260814_issue-736_kit-reflux.md（実装側 issue #736。2 件目は #739 で検出）"
author: Claude（実装）
created: 2026-08-14
dispatched: true
planning_issue: 338
---

# 環流: キット原文の誤り 2 件

いずれも実装側の固有デルタではなく、**キット原文の誤り**である。

## 1. `.claude/commands/new-spec.md` のテンプレート名

`runbook` に `operations_spec_template.md` を指しているが、正しくは `runbook_template.md`（`how-to` も同様）。
**指示どおりに実行すると、粒度も構成も違うテンプレートから作ることになる。**

## 2. `.github/workflows/pr-title.yml` の他リポ番号の表記

修飾語と番号の間に空白がある形で計画リポの issue 番号が書かれており、
**実装側の `check-cross-repo-refs.js` が違反として検出した**（キット原文をそのまま取り込んで発覚）。

空白が入ると機械的突合に掛からず、自動リンクが効く面では番号が実装リポの issue へ張り付く。
**表記規約そのものがキット由来であり、キットが自分の規約を自分で破っている形である。**

## 補足

別途 planning#337 で環流を提案している検査器をキット自身へ掛ければ、2 の型は再発しない。

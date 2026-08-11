---
title: キットの必須チェック名がワークフロー名になっており、そのとおり設定すると恒久 pending になる
type: plan-feedback
status: triaged
category: 手順の誤り
related_ids: [NFR, IADR-0182]
source_repo: microservices-platform
source_ref: "claude/issue-705-required-checks-handbook / docs/specs/20260811_issue-705_required-checks-handbook.md（実装側 issue #705）"
author: Claude（実装）
created: 2026-08-11
updated: 2026-08-11
---

# 環流: 必須チェックの手順が、そのとおりにすると壊れる（2 件）

## 指摘 1: **`repo-template/docs/ai-workflow.md` の必須チェック名がワークフロー名である**

該当箇所（キット `2cf0795`）:

```markdown
- Require status checks to pass before merging → `CI`・`Security`・`CodeQL` を必須に
```

**`CI` と `Security` は check として存在しない。** どちらも**ワークフローの `name:`** である。

GitHub Actions が report する status check の context は**ジョブ側の名前**であり、
`ci.yml` が report するのは `build-and-test` / `lint` / `doc-links` … である。
**存在しない context を必須に指定すると、永久に pending のままマージできなくなる。**

### 実測（`microservices-platform` の PR #704・2026-08-11）

report された **check 名 28 件を全数**で突き合わせた。

| 名前 | check として実在するか |
| --- | --- |
| `CI` | **存在しない** |
| `Security` | **存在しない** |
| `Images` / `PR Title` | **存在しない** |
| `CodeQL` | **存在する**（集約 check。ジョブ名 `Analyze (csharp)` とは別物） |
| `build-and-test` / `lint` / `pr-title` / `image-build` / `claude-review` | **存在する** |

> **★ 同じ節が 12 行下で「`paths:` フィルタ付きを必須にすると永久 pending になる」と警告している。**
> **原因は違うが結果は同じ事故**であり、**警告の側が原因を `paths:` に限定して書いていた**ため、
> 上の推奨と矛盾していることに気づけない構造になっている。

### 提案

- 推奨を**ジョブ名の実名**に直す。スタックによって名前が変わるため、
  **「ワークフロー名ではなくジョブ名（＝ report される check 名）を指定する」という原則の側を書く**のが安全である
- 警告を**「その PR で起動しないことがあるチェックを必須にしてはならない」へ一般化**し、
  既知の原因（`paths:` / `types:` の取りこぼし）をその下に並べる

## 指摘 2: **`claude-code-review.example.yml` の `types:` に `reopened` が無い**

該当箇所（キット `2cf0795`）:

```yaml
on:
  pull_request:
    types: [opened, synchronize]
```

**再オープンされた PR ではレビューが起動しない。**
**この check を required status check に指定すると、その PR は永久にマージできなくなる。**

キット自身が「必須チェックの有効化」で AI レビューを品質ゲートに数えており、
**必須にしたくなる作りでありながら、必須にすると壊れる**組み合わせになっている。

### 実測（`microservices-platform`・2026-08-11）

`pull_request` で起動する全ワークフローの `types:` を全数で引いた。

| | 件数 |
| --- | ---: |
| `reopened` を含む | **8** |
| **含まない** | **1**（**`claude-code-review.yml` だけ**） |

**唯一の例外が、キットの `.example` から継承したこのファイルであった。**

### 提案

- `types: [opened, synchronize, reopened]` へ揃える（実装側は #705 で是正済み）
- 実装側では**「`pull_request` で起動する全ワークフローが `reopened` を含む」ことを回帰テストで固定**した。
  同型の検査はキット側の `scripts.test.js` にも置ける

## 起票状況

| | |
| --- | --- |
| 計画リポへの起票 | **planning#313 として起票済み（2026-08-11・`decision-needed`）** |

> **★ 「環流した」と書けるのは、計画リポへのコピーまたは Issue 起票まで済んだときだけ**である
> （`docs/README.md` 運用ルール 5）。

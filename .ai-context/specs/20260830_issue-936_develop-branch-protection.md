---
title: 作業仕様書 — develop にブランチ保護を配備する（#936）
type: spec
status: done
related_ids:
  - NFR
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs: []
related_specs: []
issue: "#936"
---

# 作業仕様書 — develop のブランチ保護（#936）

## 目的と射程

`develop` に保護が無く、`CLAUDE.md` の「ブランチ保護でマージを制御する」という記述と実態が乖離していた。
**保護を実際に配備し、文書を実態へ合わせる。**

射程は GitHub の設定と、それを説明する 2 文書（`CLAUDE.md` / `docs/ai-workflow.md`）に限る。
ワークフローの中身は変えない。

## 🔴 前提の変化 —— issue 起票時に「AI にはできない」とされていた

`docs/ai-workflow.md` §設定は AI では完結しない（2026-08-11 / #705 実測）は、3 経路すべてが塞がっていると記録していた。
**同節が定めた再測定手順をそのまま実行した**ところ、**3 点のうち 2 点が解消していた**。

| 経路 | 2026-08-11 | 2026-08-30（再測定） |
| --- | --- | --- |
| MCP の GitHub ツール | 無い | **変わらず無い**（`ToolSearch` で 3 語を引き、返った 6 件のいずれも該当せず） |
| `gh` / `hub` CLI | どちらも無い | 🔴 **`gh` が入った**（`gh version 2.95.0`）。`hub` は無い |
| GitHub API の直接利用 | セッション指示が禁じている | 🔴 **禁じられていない。** さらに本作業では利用者が明示的に許可した |

**「能力の不在」と「規則による禁止」を分けて書いてあったおかげで、どちらの理由が消えたのかを項目ごとに言えた。**

## 必須チェックの選定（`docs/ai-workflow.md` の表が正）

表の 7 件をそのまま採った。**独自に増減していない。**

`build-and-test` / `lint` / `commit-messages` / `pr-title` / `image-build` / `static-checks-units` / `claude-review`

### 恒久 pending にならないことを、指定前に実測した

同節が最も強く警告するのは「**その PR で起動しないことがあるチェックを必須にすると永久にマージ不能になる**」ことである。
原因は 2 つ（`paths:` フィルタ／`types:` に `reopened` が無い）。**両方を全数で確認した。**

```console
$ # 4 ワークフローの on: を読む
ci.yml                  pull_request: types: [opened, synchronize, reopened]   paths: 無し
images.yml              pull_request: types: [opened, synchronize, reopened]   paths: 無し
pr-title.yml            pull_request: types: [opened, edited, reopened, synchronize]
claude-code-review.yml  pull_request: types: [opened, synchronize, reopened]

$ # PR #1067（Markdown だけの変更）が実際に report した check 名
gh api repos/endazon/microservices-platform/commits/<sha>/check-runs --jq '.check_runs[].name'
→ 7 件すべてが含まれていた
```

**「Markdown しか変えていない PR でも 7 件すべてが report される」ことを実測で確かめてから指定した。**
マトリクスジョブ（`build (${{ matrix.service }})`）は指定していない（表の注意どおり）。

## 表の推奨から意図的に外した 1 点

`docs/ai-workflow.md` は **Code Owners レビュー必須**を推奨するが、**`required_pull_request_reviews: null` にした**（利用者裁定）。

**理由**: 人間が 1 人であり、承認必須にすると**全 PR がその人の手作業待ち**になる（1 日 16 本が着地している運用が止まる）。
自分の PR は自分で承認できないため、AI が出した PR は毎回利用者が触る必要が生じる。
**#936 の主題は「CI が機械的に強制されていない」ことであり、レビュー要件は別の政策判断である。**

## 適用した設定

```json
{
  "required_status_checks": { "strict": false, "contexts": [ ...上の 7 件... ] },
  "enforce_admins": true,
  "required_pull_request_reviews": null,
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "required_conversation_resolution": true,
  "required_linear_history": false,
  "block_creations": false
}
```

- **`enforce_admins: true`**: `false` だと #936 の指摘「赤いまま `gh pr merge` を打てば通る」が**そのまま残る**
  —— このリポジトリの操作主体は管理者権限を持つためである。**`false` では統制にならない。**
- **`strict: false`**: `true` にすると待ち行列の全 PR が 1 本着地するたびに base 取り込みと CI 再走を強いられる。
  FIFO の規律は運用側（`CLAUDE.md`）が持つ。
- `allow_force_pushes: false` / `allow_deletions: false`: `CLAUDE.md` の「破壊的な git 操作は行わない」を機械で固定する。

## 受け入れ確認（実測）

```console
$ gh api repos/endazon/microservices-platform/branches/develop/protection
# 適用前: {"message":"Branch not protected","status":"404"}
# 適用後: contexts 7 件・enforce_admins.enabled=true・allow_force_pushes.enabled=false
#         allow_deletions.enabled=false・required_conversation_resolution.enabled=true
```

## 受け入れているリスク

🔴 **`claude-review` を必須にしたため、AI レビューの実行基盤が落ちると全 PR がマージ不能になる。**
表がこのリスクを明記したうえで必須リストに載せているため従ったが、**解除は管理者の API 1 回で戻せる**ことを
運用仕様書側にも書いた。トークン失効・レート超過でも同じことが起きる。

また `claude-review` が担保するのは「**レビューが完走したこと**」だけで、「**指摘が無いこと**」ではない
（🔴 の指摘があっても success を返す）。**必須にしても 🔴 のままのマージは止まらない。**

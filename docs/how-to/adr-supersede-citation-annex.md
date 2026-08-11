---
title: 別紙 — Superseded な ADR を引用する書式の経緯と、機械検査を置けない理由の測定
type: how-to
status: fixed
related_ids:
  - NFR
  - ADR-0003
  - ADR-0027
  - IADR-0172
  - IADR-0173
  - IADR-0176
author: claude
created: 2026-08-11
updated: 2026-08-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)"
---

# 別紙: Superseded な ADR の引用 —— 経緯と測定

> **★ これは「参照時にだけ読む別紙」である。** 毎セッション読む必要は無い。
> **規約の入口は [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
> 「残す箇所と書式」節**であり、**規範（ID を付け替えない・旧 ID の隣に後継を置く・注記には起票 ID を
> 添える・母集合は live な権威文書とコードに限る・frontmatter とコードでの書き分け）はそちらに在る。**
>
> **本別紙が持つのは「なぜその規約があるのか」の経緯と、機械検査の可否の実測だけ**である
> （[[IADR-0172]] 決定 3 の段 4 ／ [[IADR-0173]] 決定 2）。

## 1. 機械検査を置いていない理由（#580 の測定）

入口の規範は「**機械検査は置いていない。よって本規約は人と AI が守るものであり、CI は守っていない**」である。
**その根拠が以下である。**

計画 ADR の `status` を読むには planning submodule が
必要だが、**PR で起動する決定的な検査ジョブ**（`ci.yml` の `doc-links` / `scripts-tests` /
`commit-messages` 等、`pr-title.yml`）は**どれも submodule を populate しない**ため、検査を作っても
常に skip され緑のまま素通りする。

### 例外は 2 本あるが、いずれもゲートではない

**例外は 2 本あるが、いずれもゲートではない**（#580 の測定・実測日 2026-08-07）。どちらも
`PLANNING_REPO_TOKEN` を使って `git submodule update --init --recursive` を実行する。

| ワークフロー | トリガ | PR 文脈で起動するか |
| --- | --- | --- |
| `claude-code-review.yml` | `on: pull_request`（`opened` / `synchronize`） | する |
| `claude-coding.yml` | `issue_comment` / `pull_request_review_comment` / `pull_request_review` / `issues` | する（PR へのコメント・レビューで起動する） |

ただしどちらも **AI 実行系であってマージを止める決定的ゲートではない**（前者は AI レビュー、
後者は `@claude` メンションでの対話実装）ので、これらに検査を載せても「PR で planning を読む
**検査**」にはならない。「**PR で planning は絶対に取れない**」と読み違えないこと——取れるジョブは
在るが、ゲートではない、が正しい。

> **★ 実効させたいなら**、`check-commit-messages.js` を走らせるジョブへ `submodules` ＋ `token` を
> 付ける必要がある。**入口の「起点 ID の種別」節にある同趣旨の注も同じ測定に由来する。**

## 2. コードを対象外にしない理由

入口の規範は「**注記の起票 ID を添える対象は live な権威文書とコードの両方**」である。
**コードだけを外す案を退けた根拠が以下である。**

**コードを対象外にしない理由**:
母集合を切る基準は「**書いた時点の記録か否か**」であり（入口の「母集合」のとおり `docs/specs/` 等はそれで外れる）、
コードはその基準に当たらない。「`git blame` で辿れるから注記 ID は要らない」は `.md` にも等しく
当てはまるので、コードだけを外す根拠にならない。

## 関連

- 入口: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「残す箇所と書式」
- 減量の計画: [[IADR-0172]] ／ 別紙化の方式: [[IADR-0173]] ／ 入口の総括: [[IADR-0176]]
- 同じ方式の別紙: [`commit-message-rules-annex.md`](./commit-message-rules-annex.md) ／
  [`changelog-overrides-annex.md`](./changelog-overrides-annex.md) ／
  [`cross-project-id-refs-annex.md`](./cross-project-id-refs-annex.md)

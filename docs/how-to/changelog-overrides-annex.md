---
title: 別紙 — CHANGELOG 生成時の誤記補正・除外の仕組み
type: how-to
status: fixed
created: 2026-08-11
updated: 2026-08-11
author: claude
---
<!-- trace:
ids: []
adrs: []
iadrs: [IADR-0172, IADR-0173, IADR-0174]
specs: [01_requirements]
issues: []
-->

# 別紙: CHANGELOG 生成時の誤記補正・除外の仕組み

> **★ これは「参照時にだけ読む別紙」である。** 毎セッション読む必要は無い。
> **規約の入口は [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
> 「CHANGELOG 生成時の誤記補正・除外規定」節**であり、**規範（履歴は書き換えない。誤記があっても
> `rebase` / force push で直さない）はそちらに在る。**
>
> **本別紙が持つのは「では、どう直すか」の仕組みだけ**である
> （[[IADR-0172]] 決定 3 の段 2 ／ [[IADR-0173]] 決定 2）。

## いつ読むか

| 読む場面 | |
| --- | --- |
| **過去コミットの起点 ID・種別・要約の誤記に気づいた**とき | §誤記補正（`action: "remap"`） |
| **CHANGELOG に載せるべきでないコミットがある**とき | §除外（`action: "exclude"`） |

> **★ 先に規範を読むこと。** **履歴は書き換えない。** 直すのは**生成物だけ**である。

## 仕組み

- **誤記補正（`action: "remap"`）**: 誤った起点 ID・種別・要約を、CHANGELOG 上でのみ差し替える。
  `type` / `scope` / `desc` を任意に指定でき、省略した項目は元コミットの値を保つ。
  - 例: 件名の起点 ID が誤って `feat(FR-10)` となっているが、実体は基盤スケルトン（P0）である場合、
    `scope` を `FR-10` → `P0` へ補正する。実体が大規模実装なら `type` は `feat` のまま保持し、
    `docs` へは remap しない（実装をドキュメントとして過小計上する新たな誤帰属を避ける）。
  - 配布時の `overrides` は空である。他リポジトリの SHA を引き継がないこと
    （`hash` は前方一致のため、偶然一致した無関係なコミットを誤って差し替える）。
- **除外（`action: "exclude"`）**: CHANGELOG に載せるべきでないコミット（試験的・巻き戻し前提の
  作業等）を生成物から除外する。git 履歴には残るため追跡可能性は失われない。
- 未知の `action`（タイプミス等）は `gen-changelog.js` が警告を出して補正を無視する（黙って
  remap 扱いにしない）。許可値は `remap` / `exclude` の 2 種のみ。

補正・除外はいずれも「履歴は不変・生成物のみ是正」という原則に従い、その根拠を各エントリの
`reason` に必ず残す。CI（`changelog.yml`）は `develop` / `main` への push で `fetch-depth: 0` の
全履歴から CHANGELOG を再生成し、本補正を含む差分を PR 経由で反映する。

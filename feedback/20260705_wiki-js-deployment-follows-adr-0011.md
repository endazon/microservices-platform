---
title: ADR-0011（Wiki.js 採用）に実装を追従 — Proposed→Accepted 確定と記述整合を提案
type: plan-feedback
status: accepted
category: 状態確定・記述整合
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
  - IADR-0009
source_repo: microservices-platform
source_ref: "branch claude/issue-66-20260705-0804 / Issue #66（親 #56 → #48）/ docs/adr/IADR-0020_wiki-js-deployment-abac-gateway.md"
author: claude
created: 2026-07-05
updated: 2026-08-05
supersedes: ./20260703_wiki-selfhosted-supersedes-adr-0011.md
---

# フィードバック: ADR-0011（Wiki.js 採用）に実装を追従し、状態確定・記述整合を求める

## 種別

状態確定・記述整合 — 確定方針（ADR-0011）に実装を追従させ、計画側の状態表記（`Proposed`）と要求側の
「確定済み」表記の不整合是正を求める。

## 経緯

- Issue #56（親 #48）で ADR-0011 逸脱（Wiki.js 不在）が検出された。実装側は当初 [IADR-0013] で **(b)**
  （自前軽量閲覧 API を正式化し ADR-0011 を Supersede）を提案した（[前フィードバック](./20260703_wiki-selfhosted-supersedes-adr-0011.md)）。
- Issue #66 で人間（endazon）が正規化方針として **(a) Wiki.js 配備**を選択した。よって前フィードバックは
  取り下げ、実装は ADR-0011 に**追従**する（[IADR-0013] を Superseded、[IADR-0020] で追従を記録）。

## 起点となる計画書

- 機能要求（FR）: FR-13（正規化文書を Wiki サービスで閲覧。ABAC・横断検索・AI 回答と統合）
- ユースケース（UC）: UC-07（Wiki で閲覧する）
- 関連 ADR: ADR-0011（閲覧基盤に Wiki.js 採用）、ADR-0004（ABAC / deny-by-default）
- 計画書リンク: `projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md`

## 現状（計画書の記述 / As-Is）

- ADR-0011 の状態は `Proposed`。一方 `02_requirements/01_requirements.md` は「ADR-0001〜0014 は作成・確定済み」と
  記し、**不整合**がある（前フィードバックでも指摘済み）。

## あるべき姿（To-Be）／提案

1. **ADR-0011 を `Proposed`→`Accepted`** に確定する（実装が (a) で追従するため）。
2. 要求側「確定済み」表記と ADR 状態の**整合**を取る（`Proposed` のままなら要求側注記を修正、Accepted 化なら
   ADR-0011 側を更新）。
3. ADR-0011 の「ABAC は本システムが真実源／Wiki.js は表示制御」の主旨に、実装の **ABAC 強制点（WikiService を
   Wiki.js 前段ゲートウェイとし deny-by-default + 404 存在秘匿を強制。Wiki.js のページ/グループ権限は補助）** を
   追記できると、細粒度認可の担保箇所が計画上も明確になる（[IADR-0020] / [IADR-0009] 参照）。

## 実装側の対応（本リポジトリ・Issue #66）

- [IADR-0020]: Wiki.js を配備し WikiService を「同期・ABAC ゲートウェイ」へ縮退（[IADR-0013] を Supersede）。
- [IADR-0021]: Wiki.js 同期は GraphQL API push。
- 配備: `deploy/docker-compose.yml`（Wiki.js + `wikijs` DB + Keycloak realm import）、`deploy/helm/`（Wiki.js 一式）、
  `deploy/keycloak/knowledge-platform-realm.json`（`wiki-js` OIDC クライアント）。
- ドキュメント: `docs/functional/FR-13`・`docs/operations/`・`docs/security/` を新構成へ更新。

## 確定は人間が行う

計画リポジトリ（`project-planning`）への反映と ADR-0011 の Accepted 化は `/triage-feedback` と人間が判断する。

[IADR-0009]: ../docs/adr/IADR-0009_wiki-browsing-404-hides-existence.md
[IADR-0013]: ../docs/adr/IADR-0013_wiki-selfhosted-read-api-supersedes-adr-0011.md
[IADR-0020]: ../docs/adr/IADR-0020_wiki-js-deployment-abac-gateway.md
[IADR-0021]: ../docs/adr/IADR-0021_wiki-js-sync-graphql-push.md

## ［2026-08-05 追記 / #497］計画側の実態へ status を同期した

**判定: accepted。** ADR-0011 は本記録の提案どおり ABAC 強制点を明確化したうえで `Accepted` 化された。

確認は planning submodule pin `d980a01` に対して行った（**行番号は pin が動くとずれるため内容で特定する**）。

| 確認先（計画リポジトリ） | 確認した記述 |
| --- | --- |
| [draft/feedback/20260705_wiki-js-deployment-follows-adr-0011.md](../planning/draft/feedback/20260705_wiki-js-deployment-follows-adr-0011.md) | `status: accepted`（「トリアージ結果」節） |
| [07_adr/ADR-0011_wiki-engine.md](../planning/projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md) `:5` | `status: Accepted` |
| 同 `:34` | 「**ABAC 強制点の明確化**」= WikiService を Wiki.js の前段ゲートウェイとし deny-by-default と 404 による存在秘匿を強制する旨を本文へ追記済み |
| 同 `:46` | 「確定の経緯」が**本記録を相対リンクで参照**し、Issue #66 の (a) Wiki.js 配備 と実装の追従（IADR-0020）をもって確定したと記す |

作業仕様書: [docs/specs/20260805_issue-497_feedback-status-sync.md](../docs/specs/20260805_issue-497_feedback-status-sync.md)（#497）

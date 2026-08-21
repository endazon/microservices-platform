---
title: IADR-0013 Wiki 閲覧は自前の軽量読み取り専用 API を採用し、ADR-0011（Wiki.js 採用）の Supersede を計画へ提案する
type: impl-adr
status: Superseded
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
  - IADR-0009
author: claude（実装）
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-13)
  - planning:projects/microservices-platform/03_usecases/01_usecases.md (UC-07)
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md
---

# IADR-0013: Wiki 閲覧は自前の軽量読み取り専用 API を採用し、ADR-0011 の Supersede を計画へ提案する

> **⚠️ この決定は [IADR-0020](./IADR-0020_wiki-js-deployment-abac-gateway.md) により Superseded（2026-07-05）。**
> Issue #66 で人間が正規化方針 **(a) Wiki.js 配備**を選択したため、実装は ADR-0011 に**追従**する方向へ転換した。
> 本 IADR（(b) 自前閲覧 API を正式化し ADR-0011 を Supersede 提案）は無効化され、後継 IADR-0020 が置き換える。

- 状態: Superseded（by IADR-0020）
- 日付: 2026-07-03
- 決定者: claude（実装）
- 関連: ADR-0011（閲覧基盤に Wiki.js 採用）、ADR-0004（Keycloak + ABAC / deny-by-default）、
  [IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)（404 存在秘匿・メモリ内 ABAC 評価）、
  [IADR-0004](./IADR-0004_abac-multivalue-allowlist-deny-by-default.md)（多値 allow-list・deny-by-default の評価意味論）

## コンテキストと課題

Issue #56（親 #48 の横断監査 `adr-guardian`）が、計画 ADR-0011 からの設計乖離を検出した。

- **ADR-0011 の決定**: 閲覧基盤に既存 OSS の **Wiki.js** を採用し、閲覧・編集の実体を Wiki.js へ委譲する。`WikiService` は「同期・統合」に責務を限定する。ABAC を本システム側の真実源とし、Wiki.js 側は表示制御に留める。
- **実装の実態**: Wiki.js はコード・デプロイ（`deploy/docker-compose.yml`・`deploy/helm/`）のいずれにも存在しない。`WikiService` が自前 DB（`wiki_svc`）に `WikiPage` を保持し、`WikiEndpoints`（`/wiki/pages` 一覧・`/wiki/pages/{slug}`・`/wiki/pages/by-doc/{id}`）で閲覧 API を自ら提供している。ABAC・404 存在秘匿は [IADR-0009] で正しく適用済み。

すなわち機密性要件は充足しているが、確定決定（ADR-0011）からの逸脱が未正規化のまま残っている。これを「(a) Wiki.js を配備して WikiService を同期責務へ縮退」か「(b) 自前軽量閲覧 API を正式決定して ADR-0011 を Supersede」で正規化する必要がある。

## 検討した選択肢

1. **(b) 自前の軽量読み取り専用閲覧 API を正式決定**し、ADR-0011 の Supersede を計画へ提案する（本 IADR で採用）。
2. **(a) Wiki.js を配備**し、`WikiService` を Wiki.js への同期・統合責務へ縮退させ、閲覧・編集 UI を Wiki.js へ委譲する。
3. 現状維持（逸脱を記録しない）— 監査で再検出されトレーサビリティが破れるため不可。

## 決定

**選択肢 1（(b)）を採用する。**

- Wiki 閲覧は、正規化済み Markdown を対象とする**自前の軽量な読み取り専用閲覧 API**（`WikiService` が保持・提供）を正式な実装方式とする。
- ABAC は本システム側（`AuthorizationService` / `AbacPageFilter`）を単一の真実源として評価する（ADR-0011 の「ABAC は本システムが真実源」という主旨は維持）。
- この決定は ADR-0011 の「Wiki.js を採用し閲覧実体を委譲する」部分を**覆す**ため、計画 ADR-0011 の `Superseded` 化と後継 ADR の起票を `/plan-feedback`（feedback 記録（環流記録。計画リポ `projects/microservices-platform/10_feedback/20260703_wiki-selfhosted-supersedes-adr-0011.md` へ移設））で提案する。計画確定は `/triage-feedback` と人間が行う。

## 理由

- **機密性要件は現行実装で充足**: deny-by-default・404 存在秘匿・ABAC 一元管理は [IADR-0009] で実装済みで、Issue も機密性は満たすと認めている。逸脱の正規化に必要なのは「決定の追認」であって挙動の変更ではない。
- **認可の二重管理リスクの解消**: ADR-0011 自身が Wiki.js のトレードオフとして「Wiki.js の権限はページ／グループ単位であり、属性ベース（ABAC）の細粒度判定は本システム側で担保する必要がある」と明記していた。自前閲覧 API は ABAC を単一実装（`AbacPageFilter`、検索側 `InMemoryVectorStore.MatchesFilters` と同一意味論）で評価し、経路間の可視性ずれと二重管理を構造的に排除する。
- **要件に対する適合**: FR-13 / UC-07 は「正規化済み文書の**閲覧**（ABAC・横断検索・AI 回答と統合）」を求める。編集は UC-03（文書管理）側であり、Wiki 経路は読み取りに限定される。Wiki.js のフル機能（編集 UI・独自認証・ストレージ同期）は本要件に対して過剰で、運用面（新ミドルウェア・OIDC 連携・同期整合性）の負荷を増やす。
- **(a) は逆行**: Wiki.js 配備は上記の二重管理リスクを再導入し、現状の fail-closed 設計（[IADR-0012] の scope 未指定 deny 化を含む）と方向が逆になる。早期の閲覧機能提供という ADR-0011 の狙いも、自前軽量 API で既に達成されている。

## 結果

- 良い影響: 逸脱が正規化され、トレーサビリティ（ADR ↔ 実装）が復旧する。ABAC の単一真実源が維持され、監査（`adr-guardian`）の再検出を防ぐ。
- 悪い影響・トレードオフ: Wiki.js が備える編集 UI・テーマ・拡張機能は得られない。将来、閲覧を超える Wiki 機能（協調編集・版管理 UI 等）が要求された場合は、本決定を再評価し新 ADR を要する。閲覧一覧のページング等の性能課題は [IADR-0009] のフォローアップとして継続。
- フォローアップ:
  - 計画 ADR-0011 の `Superseded` 化と後継 ADR 起票を feedback で提案（記録（環流記録。計画リポ `projects/microservices-platform/10_feedback/20260703_wiki-selfhosted-supersedes-adr-0011.md` へ移設））。
  - 計画側 `01_requirements.md` の「ADR-0001〜0014 は確定済み」表記と ADR-0011 の `Proposed` 表記の不整合是正も同 feedback で指摘。
  - `docs/functional/FR-13_wiki-browsing.md`・`docs/operations/operations.md`・deploy コメントを本決定に整合させる（本 PR で実施）。

## 関連

- Supersedes: なし（実装側。計画 ADR-0011 の Supersede は計画側で確定）
- Superseded by: [IADR-0020](./IADR-0020_wiki-js-deployment-abac-gateway.md)（Wiki.js 配備・(a) 追従へ転換）
- 作業仕様書: [20260703_ADR-0011-normalization-wiki-selfhosted](../specs/20260703_ADR-0011-normalization-wiki-selfhosted.md)
- 計画フィードバック: 20260703_wiki-selfhosted-supersedes-adr-0011（環流記録。計画リポ `projects/microservices-platform/10_feedback/20260703_wiki-selfhosted-supersedes-adr-0011.md` へ移設）
- 参照 IADR: [IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md), [IADR-0004](./IADR-0004_abac-multivalue-allowlist-deny-by-default.md)

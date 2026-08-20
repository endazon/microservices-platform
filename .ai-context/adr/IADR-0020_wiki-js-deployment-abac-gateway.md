---
title: IADR-0020 Wiki.js を配備し WikiService を「同期・ABAC ゲートウェイ」へ縮退する（IADR-0013 を Supersede、ADR-0011 に追従）
type: impl-adr
status: Accepted
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
  - IADR-0009
author: claude（実装）
created: 2026-07-05
updated: 2026-07-05
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-13)
  - planning:projects/microservices-platform/03_usecases/01_usecases.md (UC-07)
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md
---

# IADR-0020: Wiki.js を配備し WikiService を「同期・ABAC ゲートウェイ」へ縮退する

- 状態: Accepted
- 日付: 2026-07-05
- 決定者: endazon（方針選択）/ claude（実装記録）
- 関連: ADR-0011（閲覧基盤に Wiki.js 採用）、
  [IADR-0013](./IADR-0013_wiki-selfhosted-read-api-supersedes-adr-0011.md)（本 IADR で Supersede）、
  [IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)（404 存在秘匿・メモリ内 ABAC）、
  [IADR-0004](./IADR-0004_abac-multivalue-allowlist-deny-by-default.md)（多値 allow-list・deny-by-default）、
  [IADR-0021](./IADR-0021_wiki-js-sync-graphql-push.md)（同期方式）

## コンテキストと課題

Issue #56 が検出した ADR-0011 逸脱（Wiki.js 不在のまま WikiService が閲覧実体を自前実装）に対し、
実装側は当初 [IADR-0013] で選択肢 **(b)**（自前軽量閲覧 API を正式化し ADR-0011 の Supersede を提案）を採った。

その後、Issue #66 において人間（endazon）が正規化方針として **(a) Wiki.js 配備**を選択した。これにより、
実装は ADR-0011（閲覧基盤に Wiki.js を採用し、閲覧・編集の実体を Wiki.js に委譲、WikiService は
「同期・統合」に責務限定、ABAC は本システムが真実源）に**追従**する。

したがって [IADR-0013] の決定（自前閲覧 API を正式方式とする）は覆される。CLAUDE.md / ADR 運用ルールに従い、
本 IADR で決定を上書きし、[IADR-0013] に `Superseded by IADR-0020` を追記する。

**最重要リスク（ADR-0011 も明記）**: Wiki.js の権限はページ／グループ単位であり、属性ベース（ABAC）の
細粒度判定を代替できない。閲覧を Wiki.js へ委譲すると、現行の deny-by-default 属性フィルタ + 404 存在秘匿
（[IADR-0009]、機密性要件・受け入れ基準②を充足）を**どこで強制するか**が設計上の要点になる。

## 検討した選択肢

1. **WikiService を Wiki.js 前段の ABAC ゲートウェイ（認可プロキシ）に縮退**し、既存の
   `AbacPageFilter` / 404 存在秘匿の意味論を Wiki.js への到達可否判定へ転用する（本 IADR で採用）。
2. Wiki.js のグループ/ページ権限だけで認可する（却下：属性ベース細粒度・deny-by-default・存在秘匿を
   表現できず、[IADR-0009] の機密性要件が退行する）。
3. リバースプロキシ（例: OpenResty/Envoy 外部認可）に ABAC を実装する（将来検討：現状は WikiService が
   既に `/authz/scope` 連携・属性フィルタ・404 意味論を実装済みで、そこへ集約するのが最小リスク）。

## 決定

**選択肢 1 を採用する。**

- **配備**: Wiki.js（`ghcr.io/requarks/wiki:2`）を `deploy/docker-compose.yml`・`deploy/helm/` に追加し、
  専用 DB `wikijs`（Postgres）で永続化する。閲覧・編集 UI の実体は Wiki.js が担う。
- **認証**: Wiki.js の認証を Keycloak（realm `knowledge-platform`、既存 `Authority` を共有）に OIDC 連携し、
  ローカルログインを無効化して OIDC 単一経路に統一する（[IADR-0021] と同 PR の realm import で `wiki-js`
  クライアントを定義）。
- **認可（ABAC の強制点）**: 本システムが単一の真実源。**WikiService を Wiki.js の前段ゲートウェイ**に縮退し、
  利用者 JWT 属性 × `/authz/scope` から解決した許可スコープを、Wiki.js の閲覧要求に対して
  **deny-by-default で強制**する。権限外ページは一覧に出さず、個別アクセスは **404 相当で存在秘匿**する
  （[IADR-0009] の意味論を継承）。Wiki.js 側のページ/グループ権限は補助的な表示制御に留め、
  属性ベース細粒度判定の代替とはしない。
- **同期**: `DocumentUpdated` 受信で正規化 Markdown を Wiki.js へ反映する（方式は [IADR-0021]）。
  WikiService は「同期・統合・認可ゲートウェイ」に責務を限定し、自前 `wiki_svc` の閲覧用スキーマは
  ゲートウェイ移行後に撤去・整理する。

### 移行方針（段階導入）

本決定は挙動と構成の変更が大きく、稼働する Wiki.js との結合検証と同期方式の PoC を要する。以下の段で進める。

1. **段1（本 PR・配備と決定記録）**: Wiki.js の配備（compose/Helm/DB）、Keycloak realm import と `wiki-js`
   クライアント、意思決定記録（本 IADR・[IADR-0021]・[IADR-0013] Supersede）、ドキュメント更新。
   既存 ABAC 実装（`AbacPageFilter`・`WikiEndpoints`・対応テスト）は**変更せず温存**し green を維持する。
2. **段2（本 PR で実装）**: `DocumentSyncConsumer` を Wiki.js への同期（GraphQL push・[IADR-0021]）へ置換。
   WikiService 閲覧経路を Wiki.js への**認可プロキシ**へ改修し、`AbacPageFilter` の判定を到達可否に転用。
   `WikiEndpointsAbacTests` / `AbacPageFilterTests` が担保する受け入れ基準（一覧=権限内のみ・個別=404）を
   新構成で再充足。自前 `wiki_svc` は**閲覧本文の実体提供を撤去**し、ABAC 判定用の**同期メタデータ**
   （属性/タグ/slug/status）に限定する（[IADR-0021]: 認可属性は Wiki.js に持ち込まず本システムが単一真実源）。
   稼働 Wiki.js を要する GraphQL スキーマ整合・エラー時再送・レイテンシの**PoC 実測は [IADR-0021] のフォロー**
   として残る（実コードはスキーマ差異を吸収しやすい形で `IWikiJsClient` 背後に隔離）。

## 理由

- **ABAC 単一真実源の維持**: 既存 `AbacPageFilter`（検索側 `InMemoryVectorStore.MatchesFilters` と同一意味論、
  [IADR-0004]）と 404 存在秘匿（[IADR-0009]）を前段ゲートウェイへ転用することで、経路間の可視性ずれと
  認可の二重管理を避けつつ、Wiki.js の粗い権限モデルの限界を構造的に補う。
- **要件充足**: FR-13 / UC-07 の「Wiki で閲覧」を Wiki.js の成熟した UI で満たしつつ、機密性要件
  （受け入れ基準②・[IADR-0009]）を退行させない。
- **ADR-0011 追従**: 計画の確定決定に実装を一致させ、トレーサビリティ（ADR ↔ 実装）を復旧する。

## 結果

- 良い影響: Wiki.js の閲覧/編集 UI・テーマ・拡張を活用可能。ADR-0011 と実装の整合。ABAC は単一真実源を維持。
- 悪い影響・トレードオフ: 新ミドルウェア（Node.js・専用 DB・OIDC・同期）の運用負荷が増える。ABAC 強制点を
  ゲートウェイに集約するため、Wiki.js への**直接到達を塞ぐ**ネットワーク分離（[IADR-0017]）が前提となる
  （Wiki.js は host 非公開、到達は WikiService ゲートウェイ経由に限定）。段階導入のため、段1 時点では
  自前閲覧 API と Wiki.js が併存する。
  - **改定注記（[IADR-0032](./IADR-0032_wikijs-dev-exposure-opt-in.md)・#124）**: この「Wiki.js は host 非公開」は
    **本番系（Helm）に限る制約**へ具体化された。**dev の compose は管理 UI セットアップ便宜のため 3001 を公開**する
    （IADR-0032 が IADR-0026 §2 を dev 範囲で改定）。本番系の非公開（`wikijs.ingress.enabled: false`）は
    `NetworkIsolationTests` が回帰ガードする。
- フォローアップ:
  - ~~段2（同期コード置換・認可プロキシ化・`wiki_svc` 撤去・結合テスト）~~ → **本 PR で実装**。
    残: 稼働 Wiki.js での GraphQL PoC 実測（[IADR-0021]）・OIDC ローカルログイン無効化の稼働検証
    （手順は `docs/operations/operations.md`）。
  - 計画 ADR-0011 の `Proposed`→`Accepted` 確定を feedback で提案
    （記録（環流記録。計画リポ `projects/microservices-platform/10_feedback/20260705_wiki-js-deployment-follows-adr-0011.md` へ移設））。
  - Wiki.js への直接到達を塞ぐネットワーク分離（compose の `expose`、k8s の NetworkPolicy）の担保。

## 関連

- Supersedes: [IADR-0013](./IADR-0013_wiki-selfhosted-read-api-supersedes-adr-0011.md)
- Superseded by: なし
- 作業仕様書: [20260705_ADR-0011-wiki-js-deployment](../specs/20260705_ADR-0011-wiki-js-deployment.md)
- 計画フィードバック: 20260705_wiki-js-deployment-follows-adr-0011（環流記録。計画リポ `projects/microservices-platform/10_feedback/20260705_wiki-js-deployment-follows-adr-0011.md` へ移設）
- 参照 IADR: [IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md),
  [IADR-0004](./IADR-0004_abac-multivalue-allowlist-deny-by-default.md),
  [IADR-0021](./IADR-0021_wiki-js-sync-graphql-push.md),
  [IADR-0017](./IADR-0017_internal-service-auth-network-isolation.md)

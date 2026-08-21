---
title: 作業仕様書 — Wiki.js 配備（ADR-0011 追従・WikiService を同期/ABAC ゲートウェイへ縮退）
type: spec
status: superseded
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
  - IADR-0009
author: claude
created: 2026-07-05
updated: 2026-07-05
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-13)
  - planning:projects/microservices-platform/03_usecases/01_usecases.md (UC-07)
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md
related_specs:
  - ./20260703_FR-13_wiki-browsing-abac.md
  - ./20260703_ADR-0011-normalization-wiki-selfhosted.md
related_adrs:
  - ADR-0011 (閲覧基盤に Wiki.js 採用。ABAC は本システム側が真実源、Wiki 側は表示制御)
  - IADR-0009 (Wiki 閲覧の 404 存在秘匿・メモリ内 ABAC 評価)
  - IADR-0013 (本作業で Superseded。自前軽量閲覧 API を採用し ADR-0011 の Supersede を提案していた)
  - IADR-0020 (本作業で新設。Wiki.js を配備し WikiService を同期/ABAC ゲートウェイへ縮退。IADR-0013 を Supersede)
  - IADR-0021 (本作業で新設。Wiki.js 同期方式は GraphQL API push)
---

# 作業仕様書: Wiki.js 配備（Issue #66）

## 目的

親 Issue #56（ADR-0011 逸脱の検出）の正規化方針として、人間が **(a) Wiki.js 配備**を選択したことに伴う実装。
ADR-0011（閲覧基盤に Wiki.js を採用、WikiService は「同期・統合」に責務限定）に**実装を追従**させる。

## 前提の反転（重要）

本リポジトリは直前まで **IADR-0013（Accepted）** で選択肢 **(b)（Wiki.js 非配備・自前軽量閲覧 API）** を採り、
ADR-0011 の Supersede を計画へ提案していた。本 Issue は人間の意思決定により **(a)** を選ぶため、
**IADR-0013 を Superseded** とし、新 **IADR-0020** で「(a) 追従」を記録する（CLAUDE.md「確定決定を覆す場合は
新 ADR を作り旧 ADR に `Superseded by` を追記」）。計画側 `ADR-0011` は `Proposed`→`Accepted` 化を
`/plan-feedback` でフォローする（本 Issue は ADR-0011 に**追従**するため Supersede は不要）。

## 制約（本セッション環境）

- 本セッションでは **dotnet SDK が利用不可**（リポジトリは `.slnx` のみで SessionStart フックが dotnet セットアップを
  スキップ）。C# の再ビルド・テストが実行できない。
- Wiki.js 同期方式（Git 同期 / GraphQL push）は **Issue が「PoC で決定」と規定**しており、稼働する Wiki.js が必要。
- ABAC 強制点（Wiki.js 前段ゲートウェイ）の 404 存在秘匿は、稼働する Wiki.js との結合試験が必要。

→ 上記より、本 PR は「配備・構成・意思決定記録・ドキュメント」を**ビルドを壊さない範囲**で確実に提供し、
WikiService の C# カットオーバー（同期先を Wiki.js へ、閲覧を ABAC ゲートウェイ化）は **PoC + ビルド環境**での
後続 PR に切り出す。既存の ABAC 実装（`AbacPageFilter` / `WikiEndpoints` / 対応テスト）は**変更せず温存**し、
green を維持したまま次段でゲートウェイへ転用する。

## アーキテクチャ（目標構成）

```
利用者 ──JWT──> BFF(エッジ) ──> [WikiService = ABAC ゲートウェイ] ──認可済みのみ──> Wiki.js(閲覧/編集UI)
                                     │  deny-by-default 属性フィルタ + 404 存在秘匿（IADR-0009 を継承）
                                     └─ DocumentSyncConsumer ──GraphQL push──> Wiki.js(ページ実体)
Keycloak(realm: knowledge-platform) ──OIDC──> Wiki.js（ローカルログイン無効・OIDC 単一経路）
```

- **閲覧・編集の実体**: Wiki.js（`ghcr.io/requarks/wiki:2`）。専用 DB `wikijs`（Postgres）。
- **認可（ABAC）**: 本システムが単一の真実源。WikiService が Wiki.js の**前段**で deny-by-default の
  属性フィルタと 404 存在秘匿を強制する（[IADR-0020]）。Wiki.js のページ/グループ権限は補助的な表示制御に留め、
  属性ベース細粒度判定の代替とはしない。
- **同期**: `DocumentUpdated` 受信で正規化 Markdown を Wiki.js へ **GraphQL API push**（[IADR-0021]）。

## 作業範囲

### 含むもの（本 PR）
- **配備**:
  - `deploy/docker-compose.yml` に `wiki-js`（`ghcr.io/requarks/wiki:2`）と専用 DB `wikijs` を追加。ヘルスチェック付き。
  - `deploy/create-multiple-dbs.sh` に `wikijs` DB と権限付与を追加。
  - `deploy/helm/knowledge-platform/` に Wiki.js の Deployment/Service/Ingress/PVC を追加（`values.yaml` でパラメータ化）。
- **OIDC（Keycloak）**:
  - `deploy/keycloak/knowledge-platform-realm.json` を新設し、realm `knowledge-platform` と `wiki-js` クライアント
    （confidential・redirect URI・スコープ・グループ/属性マッピング）を定義。既存サービスの `Authority` と共有。
  - `docker-compose.yml` の keycloak を `start-dev --import-realm` + realm import マウントに変更。
  - Wiki.js のローカルログイン無効化・OIDC 単一経路化の手順を運用仕様に明記（Wiki.js の OIDC は管理 UI/DB
    シードで確定するため、Keycloak 側クライアントを本 PR で用意し、Wiki.js 側手順を運用仕様へ記載）。
- **意思決定記録**:
  - IADR-0020（配備・縮退・ABAC 強制点、IADR-0013 Supersede）、IADR-0021（同期方式 GraphQL push）。
  - IADR-0013 を `Superseded by IADR-0020` に更新。`docs/adr/README.md` 索引更新。
- **ドキュメント**: `docs/functional/FR-13`・`docs/operations/`・`docs/security/` を新構成へ更新。
- **plan-feedback**: ADR-0011 追従（`Proposed`→`Accepted` 提案）へ環流記録を更新。
- **コード側トレーサビリティ**: `WikiEndpoints.cs` / `DocumentSyncConsumer.cs` のヘッダコメントを新方針へ更新。

> **注記（2026-07-05 更新・superseded）**: 本作業仕様書は「段1（配備・OIDC 構成・意思決定記録・
> ドキュメント）」の仕様であり、下記「含まないもの」に列挙した段2（同期コード置換・認可プロキシ化・
> `wiki_svc` 縮退・結合テスト）は後続 PR で**実装済み**。段2 の作業仕様は
> [20260705_wiki-js-stage2-sync-gateway](./20260705_wiki-js-stage2-sync-gateway.md) を参照。以降の残作業は
> 稼働環境が必要な検証・PoC と削除/アーカイブ同期の上流拡張のみ（[IADR-0021] フォロー課題）。

### 含まないもの（段2＝後続 PR で実装済み。当初は要 PoC・ビルド環境）
- ~~`DocumentSyncConsumer` の**実コード**を Wiki.js GraphQL push へ置換~~ → 段2 で実装済み。
- ~~WikiService 閲覧経路の**リバースプロキシ化**と `wiki_svc` 閲覧スキーマの撤去~~ → 段2 で実装済み
  （`WikiEndpoints` を前段 ABAC ゲートウェイ化・`wiki_svc` は同期メタデータに限定）。
- ~~上記に対する結合テスト~~ → 段2 で `WikiEndpointsAbacTests` / `DocumentSyncConsumerTests` を更新済み。
  `AbacPageFilterTests` の意味論（一覧=権限内のみ・個別=404）は不変で温存。

## 受け入れ基準の対応

> **注記（2026-07-05 更新）**: 下表の「段2」欄は当初「後続 PR」を前提としていたが、段2は
> [20260705_wiki-js-stage2-sync-gateway](./20260705_wiki-js-stage2-sync-gateway.md) で**実装済み**。残る要検証項目は
> 稼働環境が必要な PoC・結合検証のみ（別 Issue 化）。

| # | 受け入れ基準 | 段1（本 PR）での対応 | 段2以降の状況 |
| --- | --- | --- | --- |
| 1 | `docker compose up` で Wiki.js 起動・OIDC ログイン（ローカル不可） | compose に Wiki.js + realm import を追加。OIDC/ローカル無効化手順を運用仕様に明記 | Wiki.js 側 OIDC 確定は要稼働検証（別 Issue） |
| 2 | `DocumentUpdated` で Markdown が Wiki.js に反映 | 同期方式を IADR-0021 に確定 | 段2で GraphQL push を**実装済み**（`WikiJsGraphQlClient` / `DocumentSyncConsumer`）。稼働 PoC 検証は別 Issue |
| 3 | 権限外は一覧非表示・個別 404（存在秘匿） | ABAC 強制点を IADR-0020 に確定（既存 `AbacPageFilter`/404 を前段ゲートウェイへ転用） | 段2で前段 ABAC ゲートウェイ化を**実装済み**（`WikiEndpoints` / `WikiEndpointsAbacTests`）。稼働結合試験は別 Issue |
| 4 | Helm でも同等構成 | Helm に Wiki.js 一式を追加（本 PR） | — |
| 5 | WikiService が同期・統合へ縮退・自前閲覧実体の撤去/非公開化 | 目標構成と撤去方針を確定（本 PR は設計・配備） | 段2でコード縮退を**実装済み**（`wiki_svc` は同期メタデータに限定） |

## テスト観点（段2で担保済み）
段2 [20260705_wiki-js-stage2-sync-gateway](./20260705_wiki-js-stage2-sync-gateway.md) で以下を実装済み。稼働 Wiki.js を要する結合・機密性回帰の実走は別 Issue。
- deny-by-default：`Granted=false` で一覧空・個別 404（ゲートウェイ層で再現）。→ `WikiEndpointsAbacTests` で担保。
- 個別：権限外 slug/doc は 404、権限内は 200（Wiki.js 実ページに対しても）。→ ゲートウェイ層は担保、実ページ突合は要稼働検証。
- 同期：`DocumentUpdated` 受信で Wiki.js のページが GraphQL で作成・更新される。→ `DocumentSyncConsumerTests` で担保、稼働 push は要 PoC。
- 機密性回帰：IADR-0009 の存在秘匿が新構成で退行しないこと。→ `AbacPageFilterTests` の意味論は不変で温存。

## トレーサビリティ
起点 ID: FR-13, UC-07, ADR-0011, IADR-0009（親 Issue: #56 → #48 / 本 Issue: #66）

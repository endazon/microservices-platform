---
title: 作業仕様書 — Wiki.js 稼働検証・シークレット手順・削除/アーカイブ同期（Issue #88）
type: work-spec
status: in-progress
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
  - IADR-0009
  - IADR-0020
  - IADR-0021
author: claude
created: 2026-07-07
updated: 2026-07-07
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-13)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-07)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md"
related_specs:
  - ./20260705_wiki-js-stage2-sync-gateway.md
  - ./20260705_ADR-0011-wiki-js-deployment.md
related_adrs:
  - ADR-0011 (閲覧基盤に Wiki.js 採用。ABAC は本システム側が真実源、Wiki 側は表示制御)
  - IADR-0009 (Wiki 閲覧の 404 存在秘匿)
  - IADR-0020 (Wiki.js 配備・WikiService を同期/ABAC ゲートウェイへ縮退)
  - IADR-0021 (GraphQL API push 同期。削除・アーカイブ同期はフォロー課題 → 本 spec で実装)
---

# 作業仕様書: Wiki.js 稼働検証・シークレット手順・削除/アーカイブ同期（Issue #88）

## 目的

Issue #66（Wiki.js 配備）で意図的にフォローへ切り出された残作業（Issue #88）を完了する。
(1) OIDC 稼働検証、(2) GraphQL 同期 PoC 実測、(3) シークレット発行・投入手順の整備、
(4) 文書の削除・アーカイブ（非公開化）の Wiki.js への伝播（設計ギャップの解消）。

## スコープ

### 1. 削除・アーカイブ同期の実装（Issue #88 スコープ4・コード変更）

- **上流イベントの拡張**（`KnowledgePlatform.Shared.Contracts`）:
  - `DocumentDeleted(DocumentId, DeletedAt)` イベントを新設する（物理削除の伝播）。
  - `DocumentStatus` に `archived` を追加し、`DocumentUpdated.Status == "archived"` で非公開化を伝播する。
- **DocumentService**:
  - `Document.Archive()`（状態遷移 + 版スナップショット `archived`）と `POST /documents/{id}/archive` を追加。
    アーカイブ済み文書は `DocumentUpdated(status=archived)` を発行する。
  - `DELETE /documents/{id}` が削除後に `DocumentDeleted` を発行する（現状イベント未発行の欠落を解消）。
- **WikiService**:
  - `IWikiJsClient` に `DeletePageAsync(path)` を追加（`pages.delete`。存在しない場合は冪等に成功扱い）。
  - `DocumentSyncConsumer`: `archived` 受信で Wiki.js ページを**非公開化**（`isPrivate=true, isPublished=false` で
    update）し、`wiki_svc` メタデータを `WikiPageStatus.Archived` にする（定義済み・未使用だった状態を使用）。
  - `DocumentDeletedConsumer`（新設）: `DocumentDeleted` 受信で Wiki.js の `pages.delete` により**実体を撤去**し、
    `wiki_svc` メタデータ行を削除する（社内文書の外部システム残存防止）。未同期の文書 ID は冪等に無視。
  - **ゲートウェイの Archived 除外**: 一覧・個別とも `Status == Active` のページのみ対象とし、Archived は
    404 / 非表示（存在秘匿の意味論 [IADR-0009] を維持）。
- **実装 ADR**: 削除＝実体撤去（pages.delete）・アーカイブ＝非公開化（unpublish + private）という対応付けと
  「削除イベント新設 + status 拡張」の判断を IADR-0023 として記録する。

### 2. シークレット発行・投入手順（Issue #88 スコープ3・ドキュメント）

- `docs/operations/operations.md` に、Wiki.js 管理 UI / GraphQL での API キー発行手順と、
  compose（`WIKIJS_API_KEY`）・Helm（Secret `wikijs-sync` key=apiKey / `wikijs-db`）への投入手順を追記する。

### 3. 稼働検証・PoC 実測（Issue #88 スコープ1・2。この PC の Docker で実施）

- `deploy/docker-compose.yml` で `postgres`/`keycloak`/`wiki-js` を起動し、以下を実測して記録する:
  - Keycloak(OIDC) ログイン（realm `knowledge-platform` / client `wiki-js`）とローカルログイン無効化、
    `clearance`/`department`/`groups` クレームの受け渡し。
  - `pages.singleByPath` → `create`/`update`/`delete` のスキーマ整合（本実装のクエリ・ミューテーションの実測）。
  - `isPrivate=true` ページをサービスアカウント（API キー）で `singleByPath` 本文取得できるか
    （不可なら認可プロキシが fail-closed 404 → 要調整）。
  - エラー時再送（GraphQL エラー → 例外 → MassTransit リトライ）・レイテンシ（FR-13 p95）の実測。
- 実測結果は Issue #88 コメントと `docs/tech`（PoC 記録）へ残し、IADR-0021 のフォロー欄を更新する。

## 含まないもの

- stg/prod 環境そのものへのシークレット投入（手順の整備まで。実投入は運用作業）。
- Wiki.js 3.x への追従（2.x スキーマを対象とする）。
- 検索インデックス（Retrieval 側）からの削除伝播（別 Issue。本 spec は Wiki.js 経路のみ）。

## 受け入れ基準

| # | 基準 | 検証方法 |
| --- | --- | --- |
| 1 | 文書削除で Wiki.js ページが撤去され、wiki_svc メタデータも消える | `DocumentDeletedConsumer` 単体テスト + PoC 実測 |
| 2 | アーカイブで Wiki.js ページが非公開化され、ゲートウェイ一覧/個別から消える（404） | Consumer/Endpoints 単体テスト |
| 3 | 削除・アーカイブイベントは再配信に対して冪等 | 単体テスト（未同期 ID・二重配信） |
| 4 | 既存の published/normalized 同期・ABAC 挙動が退行しない | 既存テストの全通過 |
| 5 | OIDC ログイン・クレーム受け渡し・ローカルログイン無効化を稼働確認 | PoC 実測記録（Issue #88 コメント） |
| 6 | GraphQL スキーマ整合・isPrivate 本文取得可否・レイテンシを実測記録 | PoC 実測記録（docs/tech + IADR-0021 更新） |
| 7 | API キー・DB シークレットの発行・投入手順が文書化されている | docs/operations レビュー |

## テスト観点

- `DocumentDeleted` 受信: Wiki.js `pages.delete` 呼び出し + メタデータ行削除。未同期 ID は no-op（冪等）。
- `DocumentUpdated(status=archived)` 受信: unpublish/private の update push + `WikiPageStatus.Archived`。
- Archived ページ: 一覧に出ない・slug/by-doc とも 404（権限があっても）。
- DocumentService: `POST /archive` で status=archived の `DocumentUpdated` 発行、`DELETE` で `DocumentDeleted` 発行。
- 既存: published/normalized の upsert 同期・deny-closed isPrivate・ABAC フィルタが不変。

## 検証

- この PC（Windows / Docker Desktop）で `dotnet build` / `dotnet test` と PoC 実測（compose 起動）を実走する。
- `/verify` で DoD（`docs/DEFINITION_OF_DONE.md`）と突き合わせる。

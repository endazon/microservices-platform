---
title: Wiki 文書閲覧（Wiki.js 委譲・WikiService ABAC ゲートウェイ）機能仕様書
type: functional-spec
status: draft
created: 2026-07-03
updated: 2026-08-28
author: claude
---
<!-- trace:
ids: [FR-05, FR-13, UC-03, UC-07]
adrs: [ADR-0011]
iadrs: [IADR-0009, IADR-0013, IADR-0020, IADR-0021]
specs: [20260703_ADR-0011-normalization-wiki-selfhosted, 20260703_FR-13_wiki-browsing-abac, 20260705_ADR-0011-wiki-js-deployment]
issues: [#66]
-->

# 機能仕様書: Wiki 文書閲覧

## 起点となる計画書（トレーサビリティ）

- 機能要求: 正規化文書を Wiki サービスで閲覧できること（ABAC・横断検索・AI 回答と統合）
- ユースケース: Wiki で閲覧する（前提として ABAC アクセス制御）
- 計画書リンク: `02_requirements/01_requirements.md`、`03_usecases/01_usecases.md`、`07_adr/ADR-0011`
- 実装 ADR: Wiki.js を配備し `WikiService` を「同期・ABAC ゲートウェイ」へ縮退する
  （自前の軽量閲覧 API を採った先行判断を Supersede）、
  Wiki.js への同期は GraphQL API push を採用する、
  権限外アクセスは 404 で存在を秘匿し ABAC はメモリ内で後段評価する

## 概要

管理している**正規化済み Markdown 文書**を、利用者が Wiki 形式で**閲覧**する機能。閲覧・編集 UI の実体は
**Wiki.js**（`ghcr.io/requarks/wiki:2.5`、専用 DB `wikijs`）が担い、`WikiService` は「**同期・統合・ABAC
ゲートウェイ**」に責務を縮退する（計画側の Wiki エンジン採用の決定に追従）。

> **実装方式に関する注記**: 計画側の Wiki エンジンの決定は「閲覧基盤に Wiki.js（既存 OSS Wiki）を採用」である。
> 実装は当初、自前の軽量閲覧 API（(b)）を採ったが、Issue #66 で人間が **(a) Wiki.js 配備**を
> 選択したため、その先行判断を Superseded とし、Wiki.js 配備の実装 ADR で計画側の決定に追従する。認可（ABAC）は本システムが
> 単一の真実源であり、WikiService が Wiki.js の**前段**で deny-by-default の属性フィルタと 404 存在秘匿を強制する
> （Wiki.js のページ/グループ権限だけでは属性ベース細粒度判定を代替しない）。
> **段階導入**: 段1（Wiki.js の配備・OIDC 構成・意思決定記録）に続き、**段2（本 PR）で実コードを実装**した
> ── `DocumentSyncConsumer` を Wiki.js への GraphQL push 同期へ置換し、`/wiki/pages` 系を Wiki.js 前段の
> **認可プロキシ**へ改修（ABAC 通過時のみ Wiki.js 本文をプロキシ、権限外・不存在は 404）。自前 `wiki_svc` は
> 閲覧本文の実体提供を撤去し、ABAC 判定用の同期メタデータに限定した。稼働 Wiki.js を要する GraphQL PoC 実測
> と OIDC ローカルログイン無効化の稼働検証はフォローとして残る。

## 機能詳細

| 項目 | 内容 |
| --- | --- |
| 入力 | 利用者属性（JWT クレーム: clearance / department 等）, 一覧要求 / slug / documentId |
| 処理 | `IWikiAccessResolver` が `/authz/scope` で利用者属性 × ポリシーを解決 → `AbacPageFilter` が `WikiPage.Attributes`(jsonb) にメモリ内で ABAC を適用 → 権限内ページのみ返却 |
| 出力 | 一覧（権限内ページのサマリ配列）／ 個別ページ本文（権限内のみ 200、権限外・不存在は 404） |
| 業務ルール | ①フィルタ間は AND、許可値集合内は OR。②スコープ属性キーを持たないページは不一致。③`Granted=false`（マッチ無し）は deny-by-default。④認可サービス障害時も deny-by-default へ縮退し 500 を伝播しない。 |
| 対象外 | 文書の**編集・作成**（文書管理側の責務）。本 API は読み取り専用。 |

## エンドポイント

`src/knowledge/backend/Services/WikiService/Features/Wiki/WikiEndpoints.cs`

| メソッド | パス | 説明 | 権限外の挙動 |
| --- | --- | --- | --- |
| GET | `/wiki/pages` | 権限内ページの一覧（軽量サマリ。`WikiPath`=Wiki.js 上の閲覧パス） | `Granted=false` は空配列（列挙に出さない） |
| GET | `/wiki/pages/{slug}` | slug 指定の個別ページ（ABAC 通過時のみ Wiki.js 本文をプロキシ） | 404（存在秘匿） |
| GET | `/wiki/pages/by-doc/{documentId}` | documentId 指定の個別ページ（同上・プロキシ） | 404（存在秘匿） |

## 主要コンポーネント

- **Wiki.js**（`ghcr.io/requarks/wiki:2.5`）: 閲覧・編集 UI の実体。専用 DB `wikijs`（Postgres）。認証は
  Keycloak(OIDC)。ローカルログインは無効化し OIDC 単一経路（運用仕様参照）。
- `DocumentSyncConsumer`（Consumers）: `DocumentUpdated`（`Attributes` / `Tags` 含む）を購読し、`IWikiContentReader`
  で正規化 Markdown を取得して `IWikiJsClient` 経由で Wiki.js へ **GraphQL push** で冪等同期する
  （path=`doc/<DocumentId>`）。文書更新後、定義時間内に反映（受け入れ基準③）。認可属性は Wiki.js へ push しない。
  多層防御として機密区分由来の `isPrivate`（`confidentiality=public` 以外＝属性欠落含む は非公開・deny-closed）
  のみを付与する（表示制御であり、ABAC の代替ではない）。**削除・アーカイブ（非公開化）文書の Wiki.js
  同期経路は未実装**（既存の設計ギャップ。同期方式の実装 ADR のフォロー課題。`isPrivate` で public 以外は非公開だが実体撤去は別途）。
- `IWikiJsClient` / `WikiJsGraphQlClient`（Services）: Wiki.js 管理 GraphQL への upsert（`singleByPath`→`create`/`update`）
  と、認可プロキシ用の本文取得。API キーは Bearer（環境変数/Secret）。
- `IWikiContentReader` / `StorageMarkdownReader`（Services）: `MarkdownUri` から本文取得（http(s) 実取得・dev は代替）。
- `IWikiAccessResolver` / `WikiAccessResolver`（Services）: JWT 属性から `/authz/scope` を解決。障害時は deny-by-default。
- `AbacPageFilter`（Services）: `AccessScopeResponse` を文書属性に適用する純粋関数。検索側
  `InMemoryVectorStore.MatchesFilters` と同一意味論。Wiki.js 前段ゲートウェイの到達可否判定へ転用する。
- `WikiPage` / `WikiDbContext`（`wiki_svc`）: ゲートウェイの**同期メタデータ**（属性/タグ/slug/status。属性フィルタ・
  存在秘匿判定用）。閲覧本文の実体は保持せず Wiki.js に委譲する（自前閲覧実体は撤去済み）。

## 受け入れ基準の対応

| 受け入れ基準 | 対応 |
| --- | --- |
| ① 横断検索・出典との統合 | RetrievalService / AiAnalysisService / BFF で担保 |
| ② 権限外は一覧・本文いずれにも出さない | 一覧は空配列除外、個別は 404（deny-by-default の ABAC） |
| ③ 更新後の反映 | `DocumentSyncConsumer` のイベント駆動同期 |
| ④ 個別デプロイ・ロールバック | WikiService は独立サービス（独自 DB・Dockerfile） |
| ⑤ p95 レイテンシ | インデックス済み一覧＋属性フィルタで軽量。一覧のページングは後続課題 |

## 関連仕様

- 作業仕様書: 作業仕様書: Wiki.js 配備（Issue #66）（本 Issue #66）、
  作業仕様書: Wiki 閲覧の ABAC 適用、
  作業仕様書: 計画側 Wiki エンジン決定からの逸脱の正規化（(b)、Superseded）
- セキュリティ: [security](../security/security.md)（Wiki.js 前段 ABAC 強制点）、運用: [operations](../operations/operations.md)（Wiki.js 配備・OIDC）
- ABAC: [FR-05_abac-access-control](./FR-05_abac-access-control.md)

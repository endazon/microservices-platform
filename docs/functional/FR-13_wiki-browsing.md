---
title: Wiki 文書閲覧（自前軽量読み取り API・ABAC 適用）機能仕様書
type: functional-spec
status: draft
related_ids:
  - FR-13
  - UC-07
  - FR-05
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-13)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-07)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md"
---

# 機能仕様書: Wiki 文書閲覧

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-13（正規化文書を Wiki サービスで閲覧。ABAC・横断検索・AI 回答と統合）
- ユースケース（UC）: UC-07（Wiki で閲覧する）, FR-05（ABAC アクセス制御）
- 計画書リンク: `02_requirements/01_requirements.md`、`03_usecases/01_usecases.md`、`07_adr/ADR-0011`
- 実装 ADR: [IADR-0013](../adr/IADR-0013_wiki-selfhosted-read-api-supersedes-adr-0011.md)（自前軽量閲覧 API を採用、ADR-0011 Supersede 提案）、[IADR-0009](../adr/IADR-0009_wiki-browsing-404-hides-existence.md)（404 存在秘匿・メモリ内 ABAC）

## 概要

管理している**正規化済み Markdown 文書**を、利用者が Wiki 形式で**閲覧**する機能。閲覧の実体は
`WikiService`（`WikiService.Api`）が提供する**自前の軽量な読み取り専用 API** である。

> **実装方式に関する注記**: 計画 ADR-0011 は当初「閲覧基盤に Wiki.js（既存 OSS Wiki）を採用」と決定して
> いたが、実装では自前の軽量読み取り API を採用している。理由は認可（ABAC）の二重管理リスク回避・要件
> （閲覧のみ、編集は UC-03）への適合であり、[IADR-0013] に記録のうえ `/plan-feedback`
> （[feedback 記録](../../feedback/20260703_wiki-selfhosted-supersedes-adr-0011.md)）で ADR-0011 の
> Supersede を計画へ提案している。Wiki.js は**意図的に配備しない**。

## 機能詳細

| 項目 | 内容 |
| --- | --- |
| 入力 | 利用者属性（JWT クレーム: clearance / department 等）, 一覧要求 / slug / documentId |
| 処理 | `IWikiAccessResolver` が `/authz/scope` で利用者属性 × ポリシーを解決 → `AbacPageFilter` が `WikiPage.Attributes`(jsonb) にメモリ内で ABAC を適用 → 権限内ページのみ返却 |
| 出力 | 一覧（権限内ページのサマリ配列）／ 個別ページ本文（権限内のみ 200、権限外・不存在は 404） |
| 業務ルール | ①フィルタ間は AND、許可値集合内は OR。②スコープ属性キーを持たないページは不一致。③`Granted=false`（マッチ無し）は deny-by-default。④認可サービス障害時も deny-by-default へ縮退し 500 を伝播しない。 |
| 対象外 | 文書の**編集・作成**（UC-03 文書管理側）。本 API は読み取り専用。 |

## エンドポイント

`src/Services/WikiService/src/WikiService.Api/Endpoints/WikiEndpoints.cs`

| メソッド | パス | 説明 | 権限外の挙動 |
| --- | --- | --- | --- |
| GET | `/wiki/pages` | 権限内ページの一覧（軽量サマリ） | `Granted=false` は空配列（列挙に出さない） |
| GET | `/wiki/pages/{slug}` | slug 指定の個別ページ | 404（存在秘匿） |
| GET | `/wiki/pages/by-doc/{documentId}` | documentId 指定の個別ページ | 404（存在秘匿） |

## 主要コンポーネント

- `WikiPage`（Domain）: `Id` / `DocumentId` / `Title` / `Slug` / `Status` / `Attributes`(jsonb) / `Tags` / `SyncedAt`。
- `WikiDbContext`（Infrastructure）: 自前 DB `wiki_svc` に正規化 Markdown を保持。
- `DocumentSyncConsumer`（Consumers）: `DocumentUpdated`（`Attributes` / `Tags` 含む）を購読し Wiki ページへ同期。文書更新後、定義時間内に閲覧へ反映（受け入れ基準③）。
- `IWikiAccessResolver` / `WikiAccessResolver`（Services）: JWT 属性から `/authz/scope` を解決。障害時は deny-by-default。
- `AbacPageFilter`（Services）: `AccessScopeResponse` を `WikiPage.Attributes` に適用する純粋関数。検索側 `InMemoryVectorStore.MatchesFilters` と同一意味論。

## 受け入れ基準の対応

| 受け入れ基準 | 対応 |
| --- | --- |
| ① 横断検索・出典との統合 | RetrievalService / AiAnalysisService / BFF で担保 |
| ② 権限外は一覧・本文いずれにも出さない | 一覧は空配列除外、個別は 404（deny-by-default の ABAC） |
| ③ 更新後の反映 | `DocumentSyncConsumer` のイベント駆動同期 |
| ④ 個別デプロイ・ロールバック | WikiService は独立サービス（独自 DB・Dockerfile） |
| ⑤ p95 レイテンシ | インデックス済み一覧＋属性フィルタで軽量。一覧のページングは後続課題（[IADR-0009]） |

## 関連仕様

- 作業仕様書: [20260703_FR-13_wiki-browsing-abac](../specs/20260703_FR-13_wiki-browsing-abac.md)、[20260703_ADR-0011-normalization-wiki-selfhosted](../specs/20260703_ADR-0011-normalization-wiki-selfhosted.md)
- セキュリティ: [security](../security/security.md)、運用: [operations](../operations/operations.md)
- ABAC: [FR-05_abac-access-control](./FR-05_abac-access-control.md)

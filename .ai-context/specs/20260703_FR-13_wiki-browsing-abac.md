---
title: 作業仕様書 — FR-13 Wiki 閲覧の ABAC 適用（横断検索・AI回答と統合）
type: spec
status: completed
related_ids:
  - FR-13
  - FR-05
  - UC-07
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-13)
  - planning:projects/microservices-platform/03_usecases/01_usecases.md (UC-07)
  - planning:projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md
related_specs:
  - ./20260627_FR-02_ingestion-pipeline.md
  - ../../docs/functional/FR-05_abac-access-control.md
related_adrs:
  - ADR-0011 (閲覧基盤に Wiki.js 採用。ABAC は本システム側が真実源、Wiki 側は表示制御)
  - ADR-0004 (ABAC / deny-by-default)
---

# 作業仕様書: FR-13 Wiki 閲覧の ABAC 適用

## 目的

FR-13「管理している正規化文書を、Wiki サービスで閲覧できる（ABAC・横断検索・AI回答と統合）」（UC-07）を、
**認可（ABAC）の欠落を埋める**形で完成させる。ADR-0011 は「ABAC を本システム側でソースオブトゥルースとし、
Wiki 側は表示制御に留める。属性ベース（ABAC）の細粒度判定は本システム側で担保する」と定める。
本 PR は WikiService の閲覧 API に、AuthorizationService の `/authz/scope` を用いた
**deny-by-default の属性フィルタ**を適用する。

## 背景・現状（調査結果）

- `WikiService` は既に以下を実装済み。
  - `WikiPage`（`DocumentId`・`Attributes`(jsonb)・`Tags`・`Slug` ほか）と `WikiDbContext`。
  - `DocumentSyncConsumer`：`DocumentUpdated`（`Attributes`/`Tags` を含む）を購読し Wiki ページへ同期。
    → 受け入れ基準③「文書更新後、定義時間内に反映」はイベント駆動同期で満たす。
  - `WikiEndpoints`：`/wiki/pages`（一覧）・`/wiki/pages/{slug}`・`/wiki/pages/by-doc/{id}`。
- **ギャップ**：閲覧 API に ABAC が未適用。コメントには「ABAC は RetrievalService で適用済み」とあるが、
  それは横断検索経路のみ。利用者が Wiki を**直接閲覧**する経路（一覧・本文）は認可を素通りしており、
  UC-07 例外フロー「権限外の文書は一覧・本文のいずれにも表示しない」／受け入れ基準②
  「権限の無い文書は検索結果・AI回答のいずれにも一切現れない」に反する。
- ABAC の評価意味論は既存実装（`AbacEvaluator` / `InMemoryVectorStore.MatchesFilters` /
  `DataRangeScopeResolver`）と一致させる：**フィルタ間は AND、値集合内は OR、属性キーを持たない文書は不一致**。
  `Granted=false`（マッチするポリシー無し）は deny-by-default。

## 作業範囲

### 含むもの（本 PR）
- WikiService に AuthorizationService 連携（named HttpClient）を追加。
- `IWikiAccessResolver`：JWT の利用者属性（clearance/department）から `/authz/scope` を解決。
  認可サービス障害時も deny-by-default（`Granted=false`）へ縮退し 500 を伝播させない
  （`RagOrchestrator.ResolveScopeAsync` と同一方針）。
- `AbacPageFilter`：`AccessScopeResponse` を `WikiPage.Attributes` に適用する純粋関数
  （検索側 `MatchesFilters` と同一意味論）。
- `WikiEndpoints` を ABAC 適用へ改修：
  - 一覧：`Granted=false` は空配列。可視ページのみ返す。
  - 個別（slug / by-doc）：不可視は **404**（存在を秘匿。403 で存在を漏らさない）。
- テスト：ABAC 可視性（一覧の絞り込み・個別 404）・deny-by-default・同期反映。

### 含まないもの（別 PR / 別 FR）
- Wiki.js 本体の導入・OIDC 連携（ADR-0011 のフォローアップ PoC）。本 PR は「同期・統合・認可」の責務に限定。
- 横断検索・AI 回答の統合本体（FR-03/FR-04/FR-07 で実装済み。ここでは重複させない）。
- 負荷試験による p95 実測（受け入れ基準⑤。CI 外の別作業）。

## 受け入れ基準の対応

| 受け入れ基準 | 対応 |
| --- | --- |
| ① 横断検索・出典 | 既存 RetrievalService/AiAnalysisService/BFF で担保（本 PR 対象外） |
| ② 権限外は検索・AI・**閲覧**に現れない | 本 PR：Wiki 閲覧 API に deny-by-default の ABAC を適用 |
| ③ 更新後の反映（例: 15分以内） | 既存 `DocumentSyncConsumer` のイベント駆動同期で担保 |
| ④ 個別デプロイ・ロールバック | WikiService は独立サービス（独自 DB・Dockerfile）。本 PR は他サービス非改変 |
| ⑤ p95 レイテンシ | 対象外（負荷試験は別作業）。閲覧はインデックス済み一覧＋属性フィルタで軽量 |

## 実装方針（IADR 化した判断）
- 属性フィルタは jsonb のため DB 側 SQL ではなく**取得後のメモリ内評価**とする
  （検索側 `InMemoryVectorStore` と同方針、意味論の一致を優先）。
- 個別ページの権限外アクセスは **404**（存在秘匿）に統一。
- 以上 2 点は [IADR-0009](../adr/IADR-0009_wiki-browsing-404-hides-existence.md) に記録した。
  評価意味論（多値 allow-list・deny-by-default）は既存 [IADR-0004](../adr/IADR-0004_abac-multivalue-allowlist-deny-by-default.md) を流用する。

## フォローアップ課題
- 一覧 `GET /wiki/pages` は現状「全件取得 → メモリ内 ABAC 絞り込み」（検索側と同方針の意図的トレードオフ）。
  Wiki ページ数の増加に伴いページング無しの全件ロードはコスト増となるため、**ページング／サーバ側絞り込みの導入**を
  後続課題とする。受け入れ基準⑤（p95）の負荷試験実測も併せて別作業で対応する。
- 計画側 `ADR-0004` / `ADR-0011` は現在 `Proposed`。Accepted への昇格を `/plan-feedback` でフォローする。

## テスト観点
- deny-by-default：`Granted=false` で一覧が空・個別が 404。
- 一覧絞り込み：権限内の属性を持つページのみ返る。
- 個別：権限外ページ slug/doc は 404、権限内は 200。
- 同期：`DocumentUpdated` 受信でページが作成・更新される。

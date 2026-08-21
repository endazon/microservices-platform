---
title: 作業仕様書 — FR-05 /search の Scope 未指定を deny 化（fail-closed による ABAC バイパス是正）
type: spec
status: completed
related_ids:
  - FR-05
  - UC-01
  - ADR-0004
  - ADR-0005
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-05)
  - planning:projects/microservices-platform/07_adr/ (ADR-0004 Keycloak + ABAC, ADR-0005 Istio mTLS)
related_specs:
  - ../specs/20260627_FR-05_abac-deny-by-default.md
  - ../specs/20260627_FR-03_hybrid-search.md
related_adrs:
  - ADR-0004 (Keycloak + ABAC)
  - IADR-0004 (ABAC 多値 allow-list ＋ deny-by-default)
  - IADR-0012 (本作業で起票: /search の Scope 未指定を deny 扱いにし fail-closed 化)
issue: "#55（親監査: #48）"
---

# 作業仕様書: FR-05 /search の Scope 未指定を deny 化（fail-closed）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05「利用者属性と文書の属性／タグに基づき（ABAC）、アクセス可能な文書のみを検索・回答対象とする」
- ユースケース（UC）: UC-01（横断検索）
- 関連 ADR: ADR-0004（Keycloak + ABAC）、ADR-0005（Istio mTLS）、IADR-0004、IADR-0012
- 出典: `02_requirements/01_requirements.md`、Issue #55（親監査 #48 の `adr-guardian` 検出）

## 目的・背景

#48 の横断監査で検出した **ADR-0004 / IADR-0004 違反（重大）** を是正する。

`RetrievalService.HybridSearchService.SearchAsync` は `Scope is { GrantsAccess: false }` の場合のみ拒否して
いたため、**`Scope == null`（未指定）はスコープフィルタ無しで全文書を返していた**。`/search` は認証必須でも
なく、呼び出し側が渡す `AccessScope` を無検証で信任している。`deploy/docker-compose.yml` で 5003 番ポートが
ホスト公開され、Istio mTLS（ADR-0005）未実装の現状では、ネットワーク到達可能な相手が Scope を付けない
呼び出しで ABAC を全面バイパスできた（FR-05 受け入れ基準「権限の無い文書は一切現れない」に反する）。

## 対象範囲

### 含むもの
- `HybridSearchService.SearchAsync` の deny 判定を **fail-closed** 化（`GrantsAccess=true` の明示的許可が
  無い限り空を返す。`null` と `GrantsAccess=false` を等価に deny）。
- 受け入れ基準「Scope 未指定 → 結果 0 件」の単体・結合テストへの写像。
- 既存テストのうち Scope 無しで結果を期待していたケースを、許可スコープ付与に更新（後方互換の意味づけを是正）。
- 実装 ADR（IADR-0012）。

### 含まないもの（後続タスクへ分離。IADR-0012 に判断を記録）
- `/search` の `RequireAuthorization` による認証必須化、または RetrievalService 自身での Scope 自己解決
  （AiAnalysisService → RetrievalService の JWT 伝播、テスト認証基盤の整備を伴う）。
- `deploy/docker-compose.yml` の 5003 番ポート公開の見直し（ADR-0005 mTLS 導入まで内部限定）。

## 設計

### 変更点（1 論理変更）

`HybridSearchService.SearchAsync`:

```csharp
// 変更前: GrantsAccess=false のみ拒否。Scope==null は素通り（全件返却）。
if (request.Scope is { GrantsAccess: false })
    return [];

// 変更後: fail-closed。GrantsAccess=true の明示的許可が無い限り空。
if (request.Scope is not { GrantsAccess: true })
    return [];
```

- `Scope == null`（未指定）＝呼び出し側が ABAC スコープを解決していない → deny。
- `GrantsAccess == false`＝許可ポリシー無し（閲覧可能文書なし）→ deny（従来どおり）。
- `GrantsAccess == true` の時だけ検索を実行（`BuildFilters` で多値 allow-list を評価）。

### 正規経路への影響（なし）

唯一の本番呼び出し元 `RagOrchestrator.GenerateAsync` は常に `AccessScope{ Filters, GrantsAccess }` を明示
付与して `/search` を呼ぶ（未許可時は `GrantsAccess=false`）。よって `null` の deny 化で正規経路の挙動は不変。

### 後方互換の意味づけ是正

単値 `AttributeFilters`（FR-03）は認可境界ではなく任意のクエリフィルタ。許可スコープ（`GrantsAccess=true`）が
無い限り `AttributeFilters` 単独の呼び出しも deny する。認可境界は一貫して `AccessScope` に集約する。

## 受け入れ基準（本作業で満たす範囲）

- [x] Scope 未指定（`null`）の `/search` は、クエリ一致文書が存在しても結果 0 件（fail-closed）。
- [x] `GrantsAccess=false` の Scope は従来どおり 0 件。
- [x] `GrantsAccess=true` ＋多値 allow-list は許可文書のみ返し、権限外・属性欠落文書は除外（IADR-0004 継承）。
- [x] 本番正規経路（RagOrchestrator 経由）の挙動不変（契約変更なし・個別デプロイ／ロールバック可能）。

## テスト方針

- 結合（InMemory）: `PostSearch_ScopeUnspecified_ReturnsEmpty` を追加（Scope 未指定→0 件）。
- 既存の `PostSearch_ScopeDeniesAccess_ReturnsEmpty`（`GrantsAccess=false`→0 件）は継続 green。
- Scope 無しで結果を期待していた既存テスト（`PostSearch_KeywordMatch_...`、`PostSearch_AppliesAbacFilter_...`）は
  `AccessScope([], GrantsAccess: true)` を付与して更新。多値 allow-list・属性欠落除外の各テストは不変。

## 計画書との差異

- 差異なし。ADR-0004 / IADR-0004 の deny-by-default をより厳密に強制する是正（二次強制点の抜け穴封鎖）。

## 未決事項（後続タスク）

- `/search` 認証必須化 or RetrievalService の Scope 自己解決（IADR-0012 選択肢 2）。
- 5003 番ポート公開の見直し（ADR-0005 Istio mTLS 導入まで内部ネットワーク限定）。

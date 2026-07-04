---
title: 作業仕様書 — FR-05 ABAC による文書アクセス制御（deny-by-default の是正）
type: work-spec
status: completed
related_ids:
  - FR-05
  - UC-01
  - UC-05
  - NFR (p95 レイテンシ)
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-05)"
  - "../../planning/projects/microservices-platform/03_usecases/ (UC-01, UC-05)"
  - "../../planning/projects/microservices-platform/07_adr/ (ADR-0004 Keycloak + ABAC)"
related_specs:
  - ../specs/20260627_FR-03_hybrid-search.md
  - ../specs/20260627_FR-04_ai-answer-citations.md
related_adrs:
  - ADR-0004 (Keycloak + ABAC)
  - IADR-0004 (本作業で起票: ABAC フィルタの多値 allow-list 化と deny-by-default)
---

# 作業仕様書: FR-05 ABAC 文書アクセス制御（deny-by-default の是正）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05 「利用者属性と文書の属性／タグに基づき（ABAC）、アクセス可能な文書のみを検索・回答対象とする」
- ユースケース（UC）: UC-01（横断検索）, UC-05（権限管理）
- 画面（SC）: （未設定）
- 関連 ADR: ADR-0004（Keycloak + ABAC）
- 出典: `02_requirements/01_requirements.md`

## 目的・背景

ABAC の骨格（`AuthorizationService` のポリシー評価 `/authz/scope`、`RetrievalService` の属性フィルタ、
`AiAnalysisService` の RAG スコープ解決）は既に存在する。しかし **受け入れ基準②「権限の無い文書は
検索結果・AI 回答のいずれにも一切現れない（deny-by-default）」に反する欠陥**を確認したため、本作業で是正する。

### 確認した欠陥

1. **多値 allow-list フィルタの欠落（情報漏えい）**: `RagOrchestrator` がスコープを検索フィルタへ変換する際
   `AllowedValues.Count == 1` のフィルタのみを採用していた。`confidentiality ∈ {public, internal}` のような
   **多値の許可条件は丸ごと破棄**され、その属性は無制約となる → 機密文書が検索・回答に混入しうる。
2. **deny-by-default 不成立（全件開放）**: 利用者にマッチするポリシーが 1 つも無い場合、解決結果の
   フィルタは空になる。空フィルタを下流に渡すと検索は**無制約で全件を返す**。本来は「何も見せない」が正。
3. **フィルタ契約の表現力不足**: 検索の `AttributeFilters` は `Dictionary<string,string>`（単値完全一致）で
   ABAC の「key ∈ 許可値集合」を表現できない。

## 対象範囲

### 含むもの
- ABAC スコープ契約の精緻化:
  - `AccessScopeResponse` に `Granted`（いずれかのポリシーが利用者にマッチしたか）を追加。
  - 検索ワイヤ契約 `AccessScope`（多値 allow-list ＋ アクセス可否フラグ）を追加し `SearchRequest` に載せる。
- `AbacEvaluator`: マッチ有無を `Granted` として返す（deny-by-default の判定材料）。
- `IVectorStore`／`InMemoryVectorStore`／`QdrantVectorStore`: フィルタを多値 allow-list（key ∈ values の AND 結合）へ統一。
- `HybridSearchService`: `GrantsAccess == false` で**短絡的に空を返す**（deny-by-default の一元的強制点）。
- `RagOrchestrator`: 多値フィルタを破棄せず全条件を後段へ伝播。未許可時は検索・LLM を呼ばず空回答へ縮退。
- 単体・統合テスト（受け入れ基準②の写像）。
- 実装 ADR（IADR-0004）。

### 含まないもの
- 画面（SC 未設定）。
- Keycloak 連携の本実装（属性は JWT クレーム抽出の既存経路を踏襲）。
- 文書更新の反映時間（FR-02/FR-03 のインジェスト経路で担保）。
- 負荷試験による p95 実測（別タスク。本作業は無制約全件返却の抑止で素地を改善）。

## 設計

### スコープ解決とフィルタ伝播

```
POST /analysis/ask（利用者属性: JWT クレーム）
   │
   ├─ AuthorizationService POST /authz/scope
   │     → AccessScopeResponse{ AllowedFilters: [ {key, [v1,v2,...]} ], Granted }
   │
   ├─ Granted == false  →  検索/LLM を呼ばず空回答へ縮退（deny-by-default）
   │
   └─ Granted == true   →  RetrievalService POST /search
             SearchRequest{ Query, Scope: AccessScope{ Filters, GrantsAccess=true } }
                  │
                  ├─ HybridSearchService: GrantsAccess==false なら即 []（多重防御）
                  ├─ 各系統（ベクトル/全文）へ多値 allow-list フィルタを適用
                  │     文書が結果に出る条件 = ∀filter: doc.attr[key] ∈ filter.AllowedValues
                  └─ RRF 統合 → topK
```

### 多値 allow-list の意味論

- 各フィルタ `key → [v1, v2, ...]` は「文書の属性 `key` の値が許可値集合に含まれること」を要求する。
- 複数フィルタは **AND**（全フィルタを満たす文書のみ）。値集合内は **OR**（いずれかに一致で可）。
- 文書が当該属性キーを**持たない**場合は不一致（除外）。deny-by-default の徹底。
- 既存の単値 `AttributeFilters`（FR-03）は `key → [単値]` と等価に正規化し、同一経路で評価する（後方互換）。

### Qdrant 実装

- 属性は `attributes.{key}` ペイロードに保持済み（既存）。多値は gRPC `Match.Keywords`（いずれか一致）で表現し、
  キー間は `Must`（AND）で結合する。
- 全文検索側にも同一フィルタを適用（権限外文書を候補から除外）。

### deny-by-default の強制点

- 一次強制: `RagOrchestrator`（未許可なら検索・LLM を呼ばない＝コスト削減）。
- 二次強制: `HybridSearchService`（`GrantsAccess == false` で空）＝多重防御。検索を直接叩く経路でも漏れない。

## 受け入れ基準（本作業で満たす範囲）

- [x] 利用者は 1 つの検索窓から権限内データを横断検索でき、結果に出典が付く（FR-03/04 を継承）。
- [x] 権限の無い文書は検索結果・AI 回答のいずれにも一切現れない（多値 allow-list ＋ deny-by-default）。
- [ ] 文書更新後 15 分以内に反映（インジェスト経路の責務。本作業の対象外）。
- [x] 各サービスを個別デプロイ・ロールバック可能（契約追加は後方互換、サービス単位の変更に閉じる）。
- [ ] p95 レイテンシ目標（負荷試験で別途確認。本作業は全件返却の抑止で素地を改善）。

## テスト方針

- **AbacEvaluator 単体**: マッチ無し→`Granted=false`／マッチ有り→`Granted=true`、多値文書条件の集約。
- **HybridSearchService 単体**: `GrantsAccess=false` で空、`true` で多値フィルタが AND 結合される。
- **検索エンドポイント結合（InMemory）**: 多値 allow-list で許可文書のみ返る、属性キー欠落文書は除外、
  スコープ未許可で空。
- 既存 FR-03 テスト（単値 `AttributeFilters`）が引き続き green（後方互換）。

## 計画書との差異

- 差異: なし（ADR-0004 の ABAC 制約に忠実。契約変更は後方互換で DB-per-Service / 個別デプロイを維持）。

## 未決事項

- 利用者属性の正規ソース（Keycloak クレームのマッピング）確定は別タスク（現状は JWT クレーム抽出）。
- 多値フィルタの Qdrant 全文インデックス前提（`attributes.*` のキーワードインデックス）はブートストラップで担保。

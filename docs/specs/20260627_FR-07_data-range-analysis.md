---
title: 作業仕様書 — FR-07 指定データ範囲での分析・比較・抽出
type: work-spec
status: completed
related_ids:
  - FR-07
  - UC-02
  - FR-05
author: claude
created: 2026-06-27
updated: 2026-06-29
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-07)"
  - "../../planning/projects/microservices-platform/03_usecases/ (UC-02)"
  - "../../planning/projects/microservices-platform/07_adr/ (ADR-0010)"
related_specs:
  - ./20260627_FR-04_ai-answer-citations.md
  - ./20260627_FR-05_abac-deny-by-default.md
  - ../adr/IADR-0004_abac-multivalue-allowlist-deny-by-default.md
  - ../adr/IADR-0005_data-range-intersect-abac-narrowing-only.md
related_adrs:
  - ADR-0010 (関連 ADR)
  - ADR-0004 (ABAC / deny-by-default)
  - ADR-0002 (サービス境界・DB per Service)
---

# 作業仕様書: FR-07 指定データ範囲での分析・比較・抽出

## 目的

FR-07「AI に対し、**指定データ範囲**での**分析・比較・抽出**を依頼できる」（UC-02）を実装する。
既存の FR-04（`/analysis/ask`：自然文の質問回答）に対し、本 PR では利用者が
**データ範囲（data range）を明示して**、種別（分析 / 比較 / 抽出）を選んで AI に作業を依頼できる
エンドポイントを追加する。

## 背景・現状（調査結果）

- `AiAnalysisService` には FR-04 の RAG 質問回答（`/analysis/ask`）と、出典付与
  （[FR-04](./20260627_FR-04_ai-answer-citations.md) / `CitationMapper`）、
  ABAC スコープ解決→検索→LLM のオーケストレーション（`RagOrchestrator`）が実装済み。
- FR-05 により ABAC は**多値 allow-list ＋ deny-by-default**で強制される
  （[IADR-0004](../adr/IADR-0004_abac-multivalue-allowlist-deny-by-default.md)）。
- 一方、FR-07 固有の「**指定データ範囲**」（利用者が分析対象の母集合を絞る）と、
  分析・比較・抽出の**タスク種別**は未実装だった（`AnalysisRequest.Scope` は未使用の placeholder）。

## 作業範囲

### 含むもの（本 PR）

- **DTO**（`Shared.Contracts`）: `AnalysisTaskType`（Analyze/Compare/Extract）、
  `AnalysisDataRange`（Query・AttributeFilters・TopK）、`AnalysisTaskRequest`。
  タスク種別は JSON 文字列で表現（`JsonStringEnumConverter`）。
- **交差ロジック**（`DataRangeScopeResolver`）: 利用者のデータ範囲を ABAC 許可スコープと
  **AND で交差**し、検索へ渡す実効スコープを導出する純粋ロジック。**権限を一切広げない（narrowing-only）**。
  詳細決定は [IADR-0005](../adr/IADR-0005_data-range-intersect-abac-narrowing-only.md)。
- **プロンプト**（`AnalysisPromptBuilder`）: タスク種別ごとに LLM プロンプトを切り替える純粋ロジック。
  いずれも「参照文書を根拠に・根拠の無い情報を含めない・出典番号 [1][2] を付す」を厳守させる。
- **オーケストレーション**（`RagOrchestrator.AnalyzeAsync`）: ABAC 解決 → データ範囲と交差 →
  （deny なら空回答へ縮退）→ ハイブリッド検索 → 出典写像 → LLM 本文生成。FR-04 と検索・LLM 経路を共通化。
- **API**: `POST /analysis/analyze`（AiAnalysisService）、`POST /bff/analysis/analyze`（BFF 集約）。
  BFF は ABAC 権限解決のため Authorization ヘッダを後段へ伝播する。
- **バリデーション**: `instruction` 必須（空は 400）。
- **テスト**: 交差ロジック（narrowing 不変条件）・プロンプト切替・エンドポイント（service/BFF）。

### 含まないもの（後続タスク）

- データ範囲のタグ（`Tags`）指定（retrieval の属性フィルタが `Attributes` のみを対象とするため、
  本 PR は属性キーでの範囲指定に限定）。
- 範囲指定 UI（画面 SC 未設定）。
- 負荷試験による p95 レイテンシ実測。
- LLM 実接続（`LlmGateway` のスタブ/実装は別タスク）。

## 受け入れ基準（Issue）との対応

| Issue 受け入れ基準 | 本 PR | 備考 |
| --- | --- | --- |
| 横断検索・出典付与 | 対応 | `AnalyzeAsync` が権限内全データソースを横断検索し、`CitationMapper` で出典を付与。 |
| 権限外文書を一切出さない | 対応（中核） | データ範囲は ABAC と交差し**広げない**。範囲が権限外を指せば deny（IADR-0005）。二次強制は retrieval 側。 |
| 更新の N 分以内反映 | 既達 | 索引化パイプライン（FR-02）に依存。本 PR は読取り側のため不変。 |
| 個別デプロイ・ロールバック | 既達 | サービス分割（ADR-0002）で担保。 |
| p95 レイテンシ | 範囲外 | 負荷試験は後続。候補段階で範囲を絞るため探索コストはむしろ減る。 |

## 実装方針

- **narrowing-only 不変条件**: 実効スコープ ⊆ ABAC 許可スコープ。共有キーは値集合の積、
  範囲のみのキーは追加（ABAC が当該キーを無制約に許可していたため安全）、積が空なら全体を deny。
- deny-by-default の二重強制（[IADR-0004](../adr/IADR-0004_abac-multivalue-allowlist-deny-by-default.md)）を維持：
  一次は `RagOrchestrator`（空回答へ縮退）、二次は `HybridSearchService`（`GrantsAccess=false` で即空）。
- FR-04 と FR-07 で検索・出典・LLM 経路を共通化（`GenerateAsync`）し、プロンプトのみ差し替える。

## テスト方針

- ユニット（`DataRangeScopeResolverTests`）: deny-by-default 継承、共有キーの積、権限外→deny、
  範囲のみキーの追加、空値の無視、大文字小文字非依存。
- ユニット（`AnalysisPromptBuilderTests`）: 種別ごとの指示切替と、出典・根拠制約の恒常的付与。
- エンドポイント（`AnalysisEndpointTests` / `AnalysisBffEndpointTests`）: 分析結果＋出典の返却、
  空 `instruction` の 400、BFF 集約と Authorization 伝播。

## リスク・注意事項

- データ範囲はあくまで絞り込みであり、ABAC を上書きしない。レビュー時は「広げていないか」を重点確認。
- 範囲が権限外を指す場合に「結果ゼロ」と「アクセス拒否」を区別しない（いずれも空回答）。
  情報漏えい防止（範囲の存在を露呈しない）のため意図的。

## 完了条件（Definition of Done 参照）

`docs/DEFINITION_OF_DONE.md` 準拠。ビルド成功・テスト pass・トレーサビリティ ID 付与・OpenAPI 更新。

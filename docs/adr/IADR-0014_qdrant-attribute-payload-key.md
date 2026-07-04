---
title: IADR-0014 Qdrant の ABAC 属性ペイロードは両表現で復元し、フィルタキー解釈を実機確認する
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - FR-11
  - FR-02
  - UC-02
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0009_vector-store-qdrant.md"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
related_specs:
  - ../specs/20260704_FR-11_llm-routing-runtime-fixes.md
  - ../specs/20260702_FR-11_llm-egress-routing.md
  - ../specs/20260627_FR-05_abac-deny-by-default.md
related_adrs:
  - IADR-0004 (ABAC 多値 allow-list / deny-by-default)
  - IADR-0007 (config 駆動 LLM ルーティング)
  - IADR-0012 (検索スコープ fail-closed)
---

# IADR-0014: Qdrant の ABAC 属性ペイロードは両表現で復元し、フィルタキー解釈を実機確認する

- 状態: Accepted
- 日付: 2026-07-04
- 決定者: claude（実装）
- 関連: FR-05（ABAC）、FR-11（機密区分別ルーティング）、ADR-0009（ベクトルDBポート）、Issue #58（親 #48 監査）、Issue #71（フォローアップ）

## コンテキストと課題

`QdrantVectorStore` は ABAC 属性（`confidentiality` 等）をペイロードに保持し、検索時のフィルタと
回答生成時の機密区分判定に用いる。監査（#48 `adr-guardian`）で以下が判明した。

1. `MapPayload` が検索結果の `Attributes` を常に空（`[]`）で返しており、AiAnalysisService の
   `RagOrchestrator.HighestConfidentiality` が本番 Qdrant 経路で常に「属性欠落 → restricted」へ縮退。
   FR-11 の機密区分別ルーティングが事実上無効化されていた（漏えい方向でなく安全側だが、用途どおりに機能しない）。
2. 書き込み（`UpsertAsync`）はリテラルなフラットキー `attributes.{k}` でペイロードへ格納するが、
   Qdrant はフィルタキーのドットを**ネストパス**として解釈し得る。書き込み表現とフィルタ解釈が
   不一致だと、ABAC 属性フィルタ（`BuildAttributeConditions`）が過剰除外を起こす恐れがある。

## 検討した選択肢

- **A. 復元をフラットキーのみ対応**: 現行の書き込み表現に合わせる。実装は最短だが、Qdrant が
  ドットをネストパスとして格納する場合に復元できず、格納表現の前提が崩れると再び機密区分が縮退する。
- **B. 復元を両表現（フラットキー＋ネスト構造体）対応**（本決定）: 実際の格納表現がどちらでも
  機密区分を正しく復元でき、#3 の不確実性に対して堅牢。
- **C. 書き込み・フィルタ・復元をネスト構造体へ全面統一**: 表現を一貫させる本来的な解だが、既存
  ベクトルのデータ移行を伴い、実機 Qdrant での検証なしに変更するのはリスクが高い。

## 決定

- 復元は **選択肢B** を採用し、`QdrantVectorStore.ExtractAttributes` が
  (a) フラットキー `attributes.{k}` と (b) ネスト構造体 `attributes → { k: v }` の両方から属性を復元する。
  フラットキーが存在する場合はそれを尊重する（ネスト側で上書きしない）。
- 書き込み表現（`UpsertAsync` のフラットキー）とフィルタキー（`BuildAttributeConditions` の `attributes.{k}`）の
  **整合は本 PR では変更しない**。実機 Qdrant でのフィルタ解釈（ドット＝リテラル or ネストパス）を
  統合テストで確認し、過剰除外が確認された場合に選択肢Cへの統一を別 PR で行う。

## フォローアップ（実機確認事項）

> 追跡: PR #70（Closes #58）のレビュー指摘を受けたフォローアップ。Issue #71 で追跡する。実機確認後、
> 下記に従い (b) ネスト復元パスの要否を確定する（`docs/DEFINITION_OF_DONE.md`「不要な防御的実装がない」観点）。

- [ ] 実機 Qdrant に `attributes.confidentiality` を持つ点を upsert し、返却ペイロードのキー表現
      （フラットキー or ネスト構造体）を確認する。
- [ ] `attributes.{k}` を条件にした検索フィルタが、書き込んだ点を正しく通過（過剰除外しない）ことを確認する。
- [ ] **過剰除外あり**の場合: 書き込み・フィルタ・復元をネスト構造体へ統一（選択肢C）し、
      既存データの移行方針を本 IADR に追記する。
- [ ] **過剰除外なし**（フラットキー格納で確定）の場合: `QdrantVectorStore.ExtractAttributes` の
      (b) ネスト構造体復元パスは不要と判明するため速やかに削除し、本 IADR を更新する。

## 結果

- 良い影響: 実際の格納表現に依存せず機密区分を復元でき、FR-11 の機密区分別ルーティングが本番経路で機能する。
- トレードオフ: 書き込み・フィルタの整合確認を実機テストへ先送りする（安全側＝過剰除外は漏えいを招かない）。
- 安全性: 復元失敗時も既存の deny-by-default（欠落・未知は restricted）により漏えい方向には倒れない。

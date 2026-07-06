---
title: IADR-0014 Qdrant の ABAC 属性ペイロードはネスト構造体へ統一する（実機検証確定）
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - FR-11
  - FR-02
  - UC-02
author: claude
created: 2026-07-04
updated: 2026-07-06
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

# IADR-0014: Qdrant の ABAC 属性ペイロードはネスト構造体へ統一する（実機検証確定）

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
- **B. 復元を両表現（フラットキー＋ネスト構造体）対応**（実機検証前の暫定決定）: 実際の格納表現が
  どちらでも機密区分を正しく復元でき、#3 の不確実性に対して堅牢。
- **C. 書き込み・フィルタ・復元をネスト構造体へ全面統一**（実機検証後の最終決定・後述）: 表現を
  一貫させる本来的な解だが、既存ベクトルのデータ移行を伴うため、実機 Qdrant での検証を経てから採用する。

## 実機検証結果（Issue #71・確定）

`scripts/verify-qdrant-attribute-payload.sh` を実機 Qdrant（`qdrant/qdrant:latest`, ローカル
Docker/Rancher Desktop）に対して実行し、以下を確認した（作業仕様
`docs/specs/20260705_IADR-0014_qdrant-attribute-payload-verification.md` 参照）。

1. **格納表現**: リテラルなフラットキー `attributes.confidentiality` で upsert すると、返却ペイロードも
   フラットキー（`"attributes.confidentiality": "confidential"`）のまま格納される。ドットはネストパスへ
   変換されない。
2. **フィルタ通過可否**: 同じフラットキーを条件にした `attributes.confidentiality` フィルタ（scroll）は、
   書き込んだ点を**通過させなかった**（**過剰除外あり**）。理由は、Qdrant のフィルタエンジンがフィールド
   条件のキーに含まれるドットを JSON パス（ネスト）として解釈するため、格納側の「フラットキー・リテラル」
   と解釈側の「ネストパス」が不一致になるから。
   - 追加確認: 同じ点をペイロード `{"attributes": {"confidentiality": "confidential"}}`（ネスト構造体）で
     upsert すると、同一のフィルタキー `attributes.confidentiality` が正しく通過することを確認した。

結論: **過剰除外あり**の分岐が確定した。IADR-0014 の選択肢Cを採用する。

## 決定（確定・選択肢C）

- **書き込み**（`QdrantVectorStore.UpsertAsync` / `QdrantIngestionVectorStore.UpsertChunkAsync`）を、
  フラットキー `attributes.{k}` からネスト構造体 `attributes -> { k: v }` へ統一した。
- **フィルタ**（`BuildAttributeConditions`）はキー表現 `attributes.{k}` を変更していない。実機検証のとおり、
  このキー表現はネスト構造体書き込みに対して JSON パスとして正しく解決されるため、フィルタ側の変更は不要。
- **復元**（`QdrantVectorStore.ExtractAttributes`）は、不要と判明した (a) フラットキー復元パスを削除し、
  (b) ネスト構造体復元のみに一本化した（`docs/DEFINITION_OF_DONE.md`「不要な防御的実装がない」）。

## 既存データの移行方針

- IngestionService の索引付けはドキュメント単位で冪等（`DocumentUpdatedConsumer` が
  `DeleteByDocumentAsync` → 再 upsert、チャンク ID は決定的）。そのため専用の移行バッチは新設せず、
  **既存ドキュメントの `DocumentUpdated` を再発行して全件再取込する**ことを移行手順とする
  （具体的な再発行トリガー・運用手順は `docs/operations/operations.md` の運用整備時に確定する）。
- 再取込が完了するまでの間、旧フラットキー形式で格納済みのチャンクは `ExtractAttributes` が属性を
  復元できず空辞書を返す。これは deny-by-default（属性欠落 → restricted）により**安全側**に倒れる
  （漏えい方向にはならない）。ただし FR-11 の機密区分別ルーティングは、当該チャンクについては
  再取込完了まで意図どおりに機能しない点に留意する。
- 現時点（本リポジトリはまだ本番データを保有しない開発段階）では移行対象データは存在しないため、
  上記手順は将来の本番投入以降に適用する運用方針として記録する。

## 結果

- 良い影響: 書き込み・フィルタ・復元の表現が一致し、実機で確認済みの過剰除外が解消される。
  FR-11 の機密区分別ルーティングが本番経路で正しく機能する。不要だった (a) フラットキー復元パスと
  対応テストを削除し、DoD の「不要な防御的実装がない」を満たす。
- トレードオフ: 既存（旧フラットキー形式）データは再取込までは属性が復元されない（安全側の劣化）。
- 安全性: 復元失敗時も既存の deny-by-default（欠落・未知は restricted）により漏えい方向には倒れない。

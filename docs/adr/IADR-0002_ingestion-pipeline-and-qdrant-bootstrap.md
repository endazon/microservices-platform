---
title: IADR-0002 取り込みパイプライン構造・冪等チャンク ID・Qdrant ブートストラップ
type: impl-adr
status: Accepted
related_ids:
  - FR-02
  - UC-04
  - ADR-0009
  - ADR-0013
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# IADR-0002: 取り込みパイプライン構造・冪等チャンク ID・Qdrant ブートストラップ

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-06-27
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-02 / UC-04 / ADR-0009（Qdrant 直接書き込み）/ ADR-0013（LLM Gateway 埋め込み）
- 関連する実装仕様書: `../specs/20260627_FR-02_ingestion-pipeline.md`, `../functional/FR-02_ingestion.md`

## コンテキストと課題

`IngestionService` のスケルトンは消費者・チャンク化・埋め込み・ベクトルストアの各ポートを備えるが、FR-02 のパイプラインとして成立させるために 4 つの実装判断が必要であった。

1. **パース段の所在**: 消費者が本文をハードコードしており、本文取得の責務が分離されていない。
2. **チャンク ID の冪等性**: ランダム `Guid.NewGuid()` のため、再取り込み時に旧チャンク削除に失敗すると重複が残り得る。
3. **索引の存在保証**: Qdrant コレクションを作成するコードが無く、未作成環境で upsert が失敗する。
4. **コレクション名の設定不整合**: `appsettings.json` は `Qdrant:CollectionName="knowledge_chunks"` だが、コードは `Qdrant:Collection`（既定 `knowledge-chunks`）を読み、設定値が無視されていた。

## 検討した選択肢

- **パース段**: (a) 消費者内に本文取得を直書き / (b) `IDocumentContentReader` ポートを新設し DI。
- **チャンク ID**: (a) ランダム GUID 継続 / (b) `documentId` + `chunkIndex` から決定的 GUID を生成。
- **索引ブートストラップ**: (a) 手動運用（外部スクリプト）/ (b) `IHostedService` で起動時に存在保証。
- **設定キー**: (a) appsettings を `Collection` に変更 / (b) コードを `CollectionName` 優先 + `Collection` フォールバックに統一。

## 決定

- パース段は **(b) `IDocumentContentReader` ポート**を新設する。`http(s)` URI は HTTP 取得、それ以外（dev 未配備のストレージ）はプレースホルダーへグレースフルデグレード（`PandocConversionService` と同方針）。
- チャンク ID は **(b) 決定的 GUID**（`documentId` のバイト列 + `chunkIndex` を MD5 ハッシュして GUID 化）。
- 索引は **(b) `QdrantBootstrapHostedService`** が起動時に `EnsureCollectionAsync` を呼び、未存在なら作成（次元 `Qdrant:VectorSize` 既定 1536、Cosine）。
- 設定キーは **(b) `CollectionName` 優先 + `Collection` フォールバック + 既定 `knowledge_chunks`** に統一し、IngestionService / RetrievalService 双方で同一解決にする。

## 理由

- ポート分離により消費者がパイプライン（parse→chunk→embed→index）として読め、テストでスタブ注入が容易。
- 決定的 ID は再取り込みを冪等にし、削除漏れ時も上書きで重複を防ぐ（FR-02 の更新反映要件に資する）。
- 起動時ブートストラップにより、デプロイ直後でも登録先が保証され「各サービスを個別にデプロイ可能」を阻害しない。
- 設定キー統一により取り込み先と検索先のコレクションが恒久的に一致し、「更新が検索へ反映される」前提が崩れない。

## 結果

- 良い影響: パイプラインの可読性・テスト容易性・冪等性・デプロイ堅牢性が向上。取り込み/検索のコレクション整合が保証される。
- 悪い影響・トレードオフ: パース段は dev 環境では実ストレージ非対応（プレースホルダー）。決定的 ID 生成に MD5 を用いる（暗号用途ではなく ID 導出用途なので許容）。
- フォローアップ: オブジェクトストレージ（MinIO 等）連携の本実装、埋め込み次元の最終確定（`Qdrant:VectorSize`）。

## 関連

- Supersedes: なし
- Superseded by: なし

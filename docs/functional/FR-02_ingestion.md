---
title: 機能仕様書 — FR-02 取り込み（パース・チャンク化・埋め込み・索引登録）
type: functional-spec
status: in-progress
created: 2026-06-27
updated: 2026-08-30
author: claude
---
<!-- trace:
ids: [FR-02, FR-03, FR-05, UC-04]
adrs: [ADR-0003, ADR-0009, ADR-0013, ADR-0027]
iadrs: [IADR-0002, IADR-0149]
specs: [20260627_FR-02_ingestion-pipeline, 20260809_issue-536_search-result-updated-at]
issues: [#532, #536, #580]
-->

# 機能仕様書: 取り込み

## 起点

- 機能要求: 文書のパース・チャンク化・埋め込み生成と検索インデックスへの登録 ／ ユースケース: データソースを登録・同期する
- 関連 ADR: メッセージング基盤（MassTransit + RabbitMQ。後継の Wolverine 採用により Superseded・注記は #580）、ベクトルストア Qdrant への直接書き込み、LLM Gateway 経由の埋め込み生成

## 機能概要

`IngestionService` が `DocumentUpdated` イベントを購読し、文書本文を検索可能なチャンクへ変換して Qdrant に登録する。これにより横断検索と AI 回答が文書を参照できるようになる。

## 入力 / 出力

### 入力イベント: `DocumentUpdated`

| フィールド | 型 | 用途 |
| --- | --- | --- |
| `DocumentId` | Guid | 文書識別子。チャンク削除・冪等 ID の基。 |
| `Title` | string | ペイロード `document_title`。 |
| `Status` | string | 文書状態。 |
| `MarkdownUri` | string? | 本文の所在。null の場合は取り込みをスキップ。 |
| `Attributes` | Dictionary<string,string> | ABAC 属性。ペイロード `attributes.<key>`。 |
| `Tags` | List<string> | タグ。ペイロード `tags`。 |
| `UpdatedAt` | DateTimeOffset | 更新時刻。**ペイロード `updated_at`（Unix epoch ミリ秒の整数）としてそのまま索引へ載せる**（#536。更新日時は索引ペイロードへ epoch ミリ秒で持つという実装判断）。**取り込み時刻を書かない** —— 書くと再索引のたびに全文書の「更新日時」が今になる。 |

### 出力イベント: `IngestionCompleted`

`DocumentId` / `ChunkCount` / `CompletedAt` を発行し、後続の検索反映へ連鎖する。

## 処理フロー（データソース登録・同期の基本フロー）

1. `DocumentUpdated` を受信する。
2. `MarkdownUri` が null なら警告ログを残しスキップする（例外フロー E1）。
3. 既存チャンクを `DeleteByDocumentAsync(DocumentId)` で削除する（再取り込みの冪等性）。
4. **parse**: `IDocumentContentReader.ReadAsync(MarkdownUri)` で本文 Markdown を取得する。
5. **chunk**: `IChunkingService.Chunk(text, maxTokens, overlap)` で見出し単位 + オーバーラップで分割する。
6. 各チャンクについて:
   1. `chunkIndex`（0始まり）を採番する。
   2. `chunkId` を `DocumentId` + `chunkIndex` から決定的に生成する。
   3. **embed**: `IEmbeddingService.EmbedAsync(text)` で埋め込みベクトルを得る。
   4. **index**: `IIngestionVectorStore.UpsertChunkAsync(...)` で Qdrant に登録する（`chunk_index`/`tags`/`attributes` を含む）。
7. `IngestionCompleted(DocumentId, chunkCount, now)` を発行する。

### 例外フロー

- **E1（本文所在なし）**: `MarkdownUri` が null。警告ログを残し、何も登録せず正常終了する（メッセージは ack）。
- **E2（本文取得失敗）**: HTTP 取得が失敗した場合、`IDocumentContentReader` は例外を送出し、MassTransit のリトライ/エラーキューに委ねる。

## チャンク化規則（`MarkdownChunkingService`）

- Markdown 見出し（`#`〜`######`）でセクション分割する。
- セクションが `maxTokens`（既定 512、4文字≒1トークン推定）以下ならそのまま 1 チャンク。
- 超える場合は文（`。` `.` 改行）単位で詰め、`maxTokens` 到達で切り出す。
- **overlap**（既定 50 トークン ≒ 200 文字）: 長いセクションを分割する際、直前チャンク末尾の文字を次チャンク先頭へ引き継ぎ、文脈の断絶を防ぐ。

## 索引（Qdrant コレクション）

- コレクション名: `Qdrant:CollectionName`（既定 `knowledge_chunks`）。後方互換で `Qdrant:Collection` もフォールバックで解決する。
- ベクトル: 次元 = `Qdrant:VectorSize`（既定 1536）、距離 = Cosine。
- 起動時に `QdrantBootstrapHostedService` が存在保証（無ければ作成）する。
- ペイロード: `document_id` / `document_title` / `text` / `markdown_uri` / `chunk_index` / `tags` / `attributes.<key>` / **`updated_at`**。
- **`updated_at` は Unix epoch ミリ秒の整数**である（同実装判断の決定 1）。ISO-8601 文字列にすると同じ時刻を `+09:00` とも `Z` とも書けるため、辞書順が実時刻順と一致しない（並び順は #532 が使う）。
  **本項目より前に索引されたチャンクはキーを持たない** —— 検索側は `null` で返す（縮退。再索引で解消する）。

## トレーサビリティ

- コード: `IngestionService`（`DocumentUpdatedConsumer`, `MarkdownChunkingService`, `QdrantIngestionVectorStore`, `IDocumentContentReader`, `QdrantBootstrapHostedService`）。各所に `// FR-02, UC-04` を付す。
- テスト: `IngestionService.Tests`。

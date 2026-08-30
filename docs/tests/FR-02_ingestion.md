---
title: テスト仕様書 — FR-02 取り込み
type: test-spec
status: in-progress
created: 2026-06-27
updated: 2026-08-30
author: claude
---
<!-- trace:
ids: [FR-02, FR-03, FR-05, SC-02, UC-04]
adrs: []
iadrs: [IADR-0149]
specs: [20260627_FR-02_ingestion-pipeline]
issues: [#536]
-->

# テスト仕様書: 取り込み

## 対象

`src/knowledge/backend/Services/IngestionService/Tests`

## テストケース（受け入れ基準・フローの写像）

| ID | 観点 | 内容 | 期待 | 起点 |
| --- | --- | --- | --- | --- |
| T-01 | パイプライン | `DocumentUpdated` を発行し消費されること | Consumed = true、ストアにチャンクが登録される | 取り込み: 基本フロー |
| T-02 | パース段 | `IDocumentContentReader` が返した本文がチャンク化に渡る | 本文由来のチャンクが登録される | 取り込み: parse |
| T-03 | ペイロード | 登録チャンクに `chunk_index` / `tags` / `attributes` が保持される | 各値が一致 | 取り込み: 索引 / ABAC の前提 |
| T-04 | 冪等チャンク ID | 同一文書・同一インデックスのチャンク ID が再取り込みで一致する | ID 一致 | 取り込み: 冪等性 |
| T-05 | 例外 E1 | `MarkdownUri` が null | 登録 0 件・正常終了（ack） | 取り込み: 例外フロー |
| T-06 | 完了イベント | 取り込み後に `IngestionCompleted` が発行される | Published = true、ChunkCount > 0 | 取り込み: 連鎖 |
| T-07 | チャンク化 | overlap 指定時に隣接チャンクが文脈を共有する | 末尾文字が次チャンク先頭に出現 | 取り込み: chunk |
| T-08 | チャンク化 | 見出しで分割される | 見出し数に応じたチャンク | 取り込み: chunk |
| T-09 | **更新日時の索引** | `BuildChunkPayload` が `updated_at` を **Unix epoch ミリ秒の整数**で書く | キーが整数型で存在し `ToUnixTimeMilliseconds()` と一致（`QdrantIngestionVectorStoreTests`） | 横断検索 / 検索結果一覧の裁定 Q6 / 更新日時の索引表現の実装判断 |
| T-09b | **表記非依存** | 同じ瞬間を `+09:00` と `Z` で渡す | **同じ値になる**（整数で持つ目的そのもの。文字列だと辞書順が実時刻順と一致しない） | 更新日時の索引表現の実装判断 |
| T-09c | **日時が無い場合** | `updatedAt` を渡さない | **キーを置かない**（既定値で埋めない。「知らない」を「とても古い」に化けさせない） | 同実装判断（未再索引は `null`） |
| T-10 | **取り込み時刻を書かない** | 過去日時を持つ `DocumentUpdated` を消費させる | 索引に載るのは**イベントの `UpdatedAt`**（`DocumentUpdatedConsumerTests`）。**再索引のたびに「今」へ書き換わらない** | 同実装判断（取り込み時刻を書かない） |

## 補足

- 外部依存（LLM Gateway / Qdrant）はスタブ/インメモリ実装で差し替える。
- 実 Qdrant・実埋め込みに対する結合試験、負荷試験（取り込みスループット ≥ 1 万件/時・p95）は
  負荷試験タスク（**#196**）で扱う。ハーネス `perf/k6/`、手順・テスト仕様 `NFR-01_performance-load-test.md`（実測は環境準備後）。

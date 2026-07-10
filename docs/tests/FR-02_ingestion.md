---
title: テスト仕様書 — FR-02 取り込み
type: test-spec
status: in-progress
related_ids:
  - FR-02
  - UC-04
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ../specs/20260627_FR-02_ingestion-pipeline.md
  - ../functional/FR-02_ingestion.md
---

# テスト仕様書: FR-02 取り込み

## 対象

`src/Services/IngestionService/tests/IngestionService.Worker.Tests`

## テストケース（受け入れ基準・フローの写像）

| ID | 観点 | 内容 | 期待 | 起点 |
| --- | --- | --- | --- | --- |
| T-01 | パイプライン | `DocumentUpdated` を発行し消費されること | Consumed = true、ストアにチャンクが登録される | FR-02 基本フロー |
| T-02 | パース段 | `IDocumentContentReader` が返した本文がチャンク化に渡る | 本文由来のチャンクが登録される | FR-02 parse |
| T-03 | ペイロード | 登録チャンクに `chunk_index` / `tags` / `attributes` が保持される | 各値が一致 | FR-02 索引 / FR-05 前提 |
| T-04 | 冪等チャンク ID | 同一文書・同一インデックスのチャンク ID が再取り込みで一致する | ID 一致 | FR-02 冪等性 |
| T-05 | 例外 E1 | `MarkdownUri` が null | 登録 0 件・正常終了（ack） | FR-02 例外フロー |
| T-06 | 完了イベント | 取り込み後に `IngestionCompleted` が発行される | Published = true、ChunkCount > 0 | FR-02 連鎖 |
| T-07 | チャンク化 | overlap 指定時に隣接チャンクが文脈を共有する | 末尾文字が次チャンク先頭に出現 | FR-02 chunk |
| T-08 | チャンク化 | 見出しで分割される | 見出し数に応じたチャンク | FR-02 chunk |

## 補足

- 外部依存（LLM Gateway / Qdrant）はスタブ/インメモリ実装で差し替える。
- 実 Qdrant・実埋め込みに対する結合試験、負荷試験（取り込みスループット ≥ 1 万件/時・p95）は
  負荷試験タスク（**#196**）で扱う。ハーネス `perf/k6/`、手順・テスト仕様 `NFR-01_performance-load-test.md`（実測は環境準備後）。

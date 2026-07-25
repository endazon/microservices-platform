---
title: テスト仕様書 — FR-12 原本の正規化変換
type: test-spec
status: in-progress
related_ids:
  - FR-12
  - UC-06
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-12)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-06)"
related_specs:
  - ../specs/20260703_FR-12_document-normalization-pipeline.md
  - ../functional/FR-12_document-normalization.md
  - ../adr/IADR-0008_conversion-ports-deny-by-default-and-idempotent-id.md
---

# テスト仕様書: FR-12 原本の正規化変換

## 対象

`src/knowledge/backend/Services/ConversionService/tests/ConversionService.Worker.Tests`

## テストケース（受け入れ基準・フローの写像）

| ID | 観点 | 内容 | 期待 | 起点 |
| --- | --- | --- | --- | --- |
| T-01 | 図コード化成功 | コード化成功時、本文へコードブロックを埋込み画像資産は作らない | `DiagramsCoded=1`、`AssetUris` 空、本文に ```` ```mermaid ```` | FR-12 基本フロー / `NormalizationServiceTests` |
| T-02 | 画像保持（不能） | コード化不能時、画像を保存し本文へ参照を埋込む | `DiagramsRetained=1`、`AssetUris` 1件、本文に `![fig-1](` | FR-12 段階的コード化 |
| T-03 | 画像保持（送信拒否） | `Sent=false`（機密区分で送信拒否）は画像保持へ縮退する | `DiagramsRetained=1`、`AssetUris` 1件 | FR-12 機密制御 / ADR-0012・0010 |
| T-04 | 冪等 DocumentId | `SourceId`＋原本パスから決定的に導出され、再変換で一致する | `r1.DocumentId == r2.DocumentId == DeterministicGuid.ForDocument(...)` | FR-12 冪等性 |
| T-05 | 送信制御委譲 | `/complete` に `confidentiality`＋`purpose="diagram-coding"` を渡す | リクエスト本文に両フィールドが含まれる | ADR-0010 / `LlmGatewayDiagramCoderTests` |
| T-06 | 縮退（呼び出し失敗） | LLM 呼び出しが例外／非200でも例外送出せず画像保持へ縮退する | `Coded=false`、`Reason` に失敗理由 | FR-12 例外 E3 |
| T-07 | コード抽出 | ```` ```mermaid ```` / ```` ```plantuml ```` のフェンスから言語とコードを抽出する | `Coded=true`、`Language`/`Code` 一致 | FR-12 基本フロー |
| T-08 | 決定的 Guid | 同一入力で同一 Guid、異なる入力で異なる Guid（RFC4122 v5 相当） | 期待どおり | FR-12 冪等性 / `DeterministicGuidTests` |
| T-09 | pandoc 変換 | pandoc 導入環境でローカル Markdown 原本を実変換し本文を返す | 本文に原本タイトルが出現、図0件 | FR-12 本文変換 / `PandocConversionServiceTests` |
| T-10 | pandoc デグレード | pandoc 未導入／原本がローカル解決不能ならプレースホルダ本文（図0件） | 本文にファイル名が出現、`Figures` 空 | FR-12 例外 E1 |
| T-11 | 完了イベント | 変換後に `DocumentNormalized` が発行され後続へ連鎖する | Published = true、`MarkdownUri` 非空 | FR-12 連鎖 / `RawDocumentFetchedConsumerTests` |
| T-12 | **画像保持（モデル拒否）（IADR-0104 / #379）** | `stopReason="refusal"`（送信は成立したがモデルが拒否）は本文が空で返るためフェンスも無いが、T-02 の「コード化不能」と混同せず拒否として記録する。縮退先（画像保持）は不変 | `Coded=false`、`Reason="llm-refused"`（`not-codeable` でない） | FR-11・FR-12 / `LlmGatewayDiagramCoderTests.Retains_with_refusal_reason_when_model_refuses` |

## 補足

- 外部依存（pandoc / LLM Gateway / オブジェクトストレージ）はフェイク／インメモリ実装で差し替える
  （`FakeBodyConverter` / `FakeDiagramCoder` / `RecordingObjectStore`）。
- `PandocConversionServiceTests` は pandoc の導入有無が環境依存のため、前提を満たさないケースはソフトスキップする。
- 実 pandoc 変換（docx 等の実原本・実図抽出）、実オブジェクトストレージ、Vision 画像入力に対する結合試験は
  別タスク（フォローアップ、IADR-0008「スコープ外」参照）で扱う。

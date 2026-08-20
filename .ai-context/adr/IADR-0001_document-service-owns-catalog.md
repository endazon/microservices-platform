---
title: IADR-0001 カタログの正本所有とDocumentNormalizedの購読責務
type: impl-adr
status: Accepted
related_ids:
  - FR-01
  - UC-04
author: claude
created: 2026-06-27
updated: 2026-08-07
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-01)
---

# IADR-0001: カタログの正本所有と DocumentNormalized の購読責務

- 状態: Accepted
- 日付: 2026-06-27
- 決定者: claude（実装）
- 関連: ADR-0002（サービス境界・DB per Service）、ADR-0003（MassTransit + RabbitMQ。Superseded by ADR-0027・注記は #580）

## コンテキストと課題

FR-01 の同期パイプラインでは、`ConversionService` が変換完了後に `DocumentNormalized` イベントを
発行する。しかしこのイベントを購読してカタログへ正規化文書を登録する責務がどのサービスにも
割り当てられておらず、同期した文書がカタログ化されない（検索・取り込みに到達しない）。

「カタログ（正規化文書の正本）」をどのサービスが所有し、`DocumentNormalized` を誰が購読するかを
決める必要がある。

## 検討した選択肢

1. **DocumentService がカタログを所有し `DocumentNormalized` を購読する。**
2. ConversionService が直接 DocumentService の API を呼んで登録する（同期 HTTP 結合）。
3. IngestionService が `DocumentNormalized` を直接購読し、カタログを介さず索引化する。

## 決定

選択肢 1 を採用する。**カタログの正本は `DocumentService` が所有**し、`DocumentService` に
`DocumentNormalizedConsumer` を設けて `DocumentNormalized` を購読・登録する。登録後は
既存の `DocumentUpdated` を発行し、取り込み（IngestionService）・Wiki 同期（WikiService）へ連鎖させる。

カタログ文書の ID はパイプライン全体で一貫させるため、`DocumentNormalized.DocumentId` を採用する。
同一イベントの再配信に備え、ID による upsert で冪等に処理する。

## 理由

- ADR-0002（DB per Service）に従い、文書（カタログ）の状態は `DocumentService` が単独所有すべき。
  ConversionService から他サービスの DB/API を直接触れさせない。
- ADR-0003（イベント駆動。Superseded by ADR-0027・注記は #580）に沿い、サービス間は疎結合なイベントで連携する（選択肢 2 の同期結合を避ける）。
- カタログを経由することで、Wiki 同期・検索索引化・API 参照が単一の `DocumentUpdated` 連鎖に
  集約され、責務とイベントフローが一貫する（選択肢 3 はカタログを迂回し整合が崩れる）。

## 結果

- 良い影響: 同期→カタログ→索引化の連鎖が成立し、FR-01 の中核が機能する。サービス境界が明確に保たれる。
- 悪い影響・トレードオフ: `ConversionService` が `DocumentId` を採番するため、再同期時は新規文書として
  追加され得る（業務的な重複統合は実コネクタ整備時に出自キーで対応）。
- フォローアップ: 実データソースコネクタ、同期ジョブ状態管理、検索結果の出典整形を後続タスクで実装。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: [20260627_FR-01_data-source-catalog-pipeline](../specs/20260627_FR-01_data-source-catalog-pipeline.md)

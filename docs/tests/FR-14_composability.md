---
title: FR-14 コンポーザビリティ（宣言的パイプライン構成） テスト仕様書
type: test-spec
status: draft
related_ids:
  - FR-14
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-14)"
related_specs:
  - ../functional/FR-14_composability.md
  - ../specs/20260708_issue-111_declarative-pipeline-config.md
related_adrs:
  - ADR-0018 / IADR-0027 / IADR-0028
---

# テスト仕様書: FR-14 コンポーザビリティ（宣言的パイプライン構成）

> Issue #118 監査で欠落が判明したため後追いで作成（テスト実装は Issue #111 で完了済み）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-14（宣言的構成変更のみでの組み替え）
- 関連 ADR: ADR-0018（コンポーザビリティ）/ IADR-0027（フォルダ構造）/ IADR-0028（宣言的構成）

## テスト対象・範囲

- 対象: `PipelineExtensions`（宣言に基づく段登録と fail-fast）、`scripts/validate-pipeline-config.js`
  （宣言のスキーマ・接続性・循環検証）、段の組み替え（enabled/queue 変更）。
- 対象外: MassTransit 本体・RabbitMQ ブローカーの挙動、Helm/ArgoCD の適用動作（運用検証）。

## テスト観点

- 既定互換: 宣言なしでは既定配線で登録される（ローカル・テスト回帰なし）。
- fail-fast: 未宣言の段・consumer 型名不一致・input 型名不一致で起動失敗する。
- 組み替え: `enabled: false` で購読・キューが生成されない。`queue` 指定で受信エンドポイント名が変わる。
- 宣言検証: スキーマ違反・発行元のないイベント購読・循環・型名形式違反を CI 段階で検出する。

## テストケース（実装済みテストへの写像）

| # | 観点 | ケース | 実装 |
| --- | --- | --- | --- |
| 1 | 既定互換 | 宣言なし（Steps 空）で段が既定登録される | `ConversionService.Worker.Tests/PipelineStepRegistrationTests` |
| 2 | fail-fast | 宣言に段が無い/型名不一致で起動失敗 | 同上 |
| 3 | 組み替え | enabled: false で購読が生成されない / queue 上書き | `WikiService.Api.Tests/PipelineRecomposeTests` |
| 4 | 宣言検証 | スキーマ・接続性・循環・型名形式（V1〜V6） | `scripts/validate-pipeline-config.js --self-test`（CI: `ci.yml`） |
| 5 | 参照方向 | Foundation → Composable 参照なし（IADR-0027） | レビュー・grep による検査（Issue #118 監査で確認済み） |

## 合否判定

- `dotnet test`（該当テスト全緑）および CI の `validate-pipeline-config.js` が成功すること。

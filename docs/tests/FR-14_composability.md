---
title: FR-14 コンポーザビリティ（宣言的パイプライン構成） テスト仕様書
type: test-spec
status: draft
created: 2026-07-08
updated: 2026-08-23
author: claude
---
<!-- trace:
ids: [FR-14, FR-15]
adrs: [ADR-0018]
iadrs: [IADR-0027, IADR-0028, IADR-0268]
specs: [20260708_issue-111_declarative-pipeline-config]
issues: [#444]
-->

# テスト仕様書: コンポーザビリティ（宣言的パイプライン構成）

> Issue #118 監査で欠落が判明したため後追いで作成（テスト実装は Issue #111 で完了済み）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: 宣言的構成変更のみでの組み替え
- 関連 ADR: コンポーザブルアーキテクチャ（計画）／固定・可変分離のフォルダ構造・宣言的パイプライン構成（実装）

## テスト対象・範囲

- 対象: `PipelineExtensions`（宣言に基づく段登録と fail-fast）、`scripts/validate-pipeline-config.js`
  （宣言のスキーマ・接続性・循環検証）、段の組み替え（enabled/queue 変更）。
- 対象外: MassTransit 本体・RabbitMQ ブローカーの挙動、Helm/ArgoCD の適用動作（運用検証）。

## テスト観点

- 既定互換: 宣言なしでは既定配線で登録される（ローカル・テスト回帰なし）。
- fail-fast: 未宣言の段・consumer 型名不一致・input 型名不一致で起動失敗する。
- 組み替え: `enabled: false` で購読・キューが生成されない。`queue` 指定で受信エンドポイント名が変わる。
  **宣言の値が実効構成の表示（イベント接続）にまで届くことを、宣言が在ることとは別に確かめる。**
- ポート差し替え: 接続先コンポーネントの選択が構成だけで入れ替わり、宣言的な段の登録を乱さない。
- 宣言検証: スキーマ違反・発行元のないイベント購読・循環・型名形式違反を CI 段階で検出する。

## テストケース（実装済みテストへの写像）

| # | 観点 | ケース | 実装 |
| --- | --- | --- | --- |
| 1 | 既定互換 | 宣言なし（Steps 空）で段が既定登録される | `ConversionService.Worker.Tests/PipelineStepRegistrationTests` |
| 2 | fail-fast | 宣言に段が無い/型名不一致で起動失敗 | 同上 |
| 3 | 組み替え | enabled: false で購読が生成されない | `WikiService.Api.Tests/PipelineRecomposeTests` |
| 3b | 組み替え | `queue` の**構成バインド**（宣言値が `PipelineStepOptions.Queue` へ載る） | `ConversionService.Worker.Tests/PipelineStepRegistrationTests`（`Pipeline:Steps:0:Queue`） |
| 3c | 組み替え | `queue` 上書きの**実挙動**（受信エンドポイント名が宣言値へ差し替わる）。2 購読者へ同一の queue を宣言すると競合コンシューマになり丁度 1 つが受信する | `Knowledge.IntegrationTests/Messaging/QueueOverrideFanOutTests`（実ブローカ） |
| 4 | 宣言検証 | スキーマ・接続性・循環・型名形式（V1〜V6） | `scripts/validate-pipeline-config.js --self-test`（CI: `ci.yml`） |
| 5 | 参照方向 | Foundation → Composable 参照なし | レビュー・grep による検査（Issue #118 監査で確認済み） |
| 6 | 宣言の実効性 | **正の宣言そのもの**を本番と同じ読み込み経路で束縛し、入出力イベントが events 列挙に閉じる | `Platform.Shared.Infrastructure.Tests/Foundation/Pipeline/PipelineDeclarationEffectivenessTests` |
| 6b | 宣言の実効性 | 宣言の有効な段の担当サービスが compose・Helm の自己申告収集対象に実在する（宣言が突合へ届いている） | 同上 |
| 6c | 宣言の実効性 | 無効化した段が実効構成のイベント接続（購読者・発行者）から消える（＋有効な段は現れる対照条件） | 同上 |
| 7 | ポート差し替え | 構成だけでポート実装が入れ替わり（縮退 ↔ 実クライアント）、段の登録・実効構成の段/イベント接続は不変 | `Platform.Shared.Infrastructure.Tests/Foundation/Pipeline/PortSwapCompositionTests` |

## 合否判定

- `dotnet test`（該当テスト全緑）および CI の `validate-pipeline-config.js` が成功すること。

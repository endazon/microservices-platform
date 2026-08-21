---
title: 作業仕様書 — 統合テスト CI 失敗（MassTransit Bus 起動レース）の修正
type: spec
status: completed
related_ids:
  - FR-01
  - UC-04
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - ../../docs/tests/FR-01_data-source-catalog.md
related_adrs:
  - ADR-0003 (MassTransit + RabbitMQ)
issue: "#33"
---

# 作業仕様書: 統合テスト CI 失敗（MassTransit Bus 起動レース）の修正

> 本仕様書は実装着手前に作成する。Issue #33 で報告された CI 失敗を是正する作業仕様。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01（DocumentNormalized 購読 → カタログ登録）
- ユースケース（UC）: UC-04
- 関連 ADR: ADR-0003（MassTransit + RabbitMQ）
- Issue: #33

## 事象

`KnowledgePlatform.IntegrationTests.DocumentService.DocumentNormalizedSyncTests.PublishDocumentNormalized_CatalogsDocument`
が CI で不安定に失敗する。`DocumentNormalized` を発行後、`GET /documents/{id}` を 500ms×20 回（計 10 秒）
ポーリングしても 404 のままで `doc` が `null` になる。同クラスの冪等テストは成功することがある。

## 根本原因

`IntegrationTestFactoryBase.ConfigureWebHost` は `AddMassTransit(...).UsingRabbitMq(...)` を登録するが、
MassTransit の Bus は `MassTransitHostedService` によって**バックグラウンドで起動**される（既定
`MassTransitHostOptions.WaitUntilStarted = false`）。このため `WebApplicationFactory.CreateClient()` が
返った時点では、RabbitMQ 上で Consumer のレシーブエンドポイント（キュー）が Exchange にバインド完了して
いない場合がある。

テストは `CreateClient()` 直後に `IBus.Publish(DocumentNormalized)` を呼ぶため、バインド前にパブリッシュ
された場合、トピック Exchange はメッセージをルーティングできず**破棄**する。結果として Consumer は
`DocumentNormalized` を受信せず、カタログ登録（`Cataloged normalized document` ログ）が発生しない。
これは Issue #33 のログ事実（失敗テストで当該ログが一切出ていない）と一致する。

これはタイミング依存のレースコンディションであり、Bus 起動が速ければ偶発的に成功する。

## 修正方針

### 1. 根本対処 — Bus 起動完了を待ってからクライアントを返す

テスト用 `IntegrationTestFactoryBase` の DI に `MassTransitHostOptions` を構成し、
`WaitUntilStarted = true` を設定する。これにより `MassTransitHostedService.StartAsync` が
Bus の起動（レシーブエンドポイントのバインドを含む）完了まで待機し、ホスト起動＝`CreateClient()`
完了時点で Consumer が確実に購読済みとなる。`StartTimeout` / `StopTimeout` も明示する。

```csharp
services.AddOptions<MassTransitHostOptions>().Configure(o =>
{
    o.WaitUntilStarted = true;
    o.StartTimeout = TimeSpan.FromSeconds(30);
    o.StopTimeout = TimeSpan.FromSeconds(10);
});
```

InMemory トランスポート時も同設定で問題なく、購読確立後にパブリッシュされることが保証される。

### 2. 暫定対処 — ポーリングのタイムアウト延長

CI 環境のばらつきに備え、`WaitForDocumentAsync` のポーリングを 10 秒 → 30 秒へ延長する
（500ms × 60 回）。根本対処と併用することで安定性を高める。

## 対象範囲

- `src/Tests/KnowledgePlatform.IntegrationTests/Fixtures/IntegrationTestFactory.cs`
  （`MassTransitHostOptions` の構成を追加）
- `src/Tests/KnowledgePlatform.IntegrationTests/DocumentService/DocumentNormalizedSyncTests.cs`
  （ポーリング回数を延長）

## 受け入れ基準

- [ ] `PublishDocumentNormalized_CatalogsDocument` が安定して成功する（Consumer がメッセージを受信する）
- [ ] `PublishDocumentNormalized_Twice_IsIdempotent` も引き続き成功する
- [ ] Bus 起動待機により、パブリッシュ前に Consumer の購読が確立している

## 副次的な警告（別 Issue 推奨）

Issue 記載の以下は本作業のスコープ外（CI を壊さない範囲で安全なもののみ別コミットで対応）。

- `NU1510`（`Microsoft.Extensions.Diagnostics.HealthChecks` が冗長）: `Microsoft.AspNetCore.App`
  共有フレームワークが提供済みのため `KnowledgePlatform.Shared.Infrastructure` の明示参照を削除可能。安全。
- `MSB3277`（EFCore.Relational 10.0.4 vs 10.0.9）/ `CS0618`（Testcontainers の Obsolete コンストラクタ）:
  ビルドによる検証が必要なため本 PR では扱わず、別途対応を推奨。

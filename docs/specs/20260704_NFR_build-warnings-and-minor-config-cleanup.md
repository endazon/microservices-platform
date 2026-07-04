---
title: 作業仕様書 — ビルド警告の解消と軽微な構成不備の整理
type: work-spec
status: review
related_ids:
  - NFR
  - ADR-0003
  - IADR-0011
author: claude
created: 2026-07-04
updated: 2026-07-04
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0003_messaging-masstransit-rabbitmq.md"
related_specs:
  - ../tech/tech-requirements.md
  - ../operations/operations.md
related_adrs:
  - ../adr/IADR-0011_dashboard-service-usage-aggregation.md
issue: "#63"
parent_issue: "#48"
related_issues:
  - "#33"
  - "#34"
  - "#35"
---

# 作業仕様書: ビルド警告の解消と軽微な構成不備の整理

## 目的

#48 の横断監査（`/verify` のビルド検証・`adr-guardian`）で検出した軽微な品質課題を解消する。
警告・構成不備を取り除き、ADR-0003（非同期メッセージングの再試行・デッドレターによる回復性）および
IADR-0011（Dashboard 集計サービス）とデプロイ構成の整合を回復する。

起点: NFR（品質）、ADR-0003、IADR-0011。

## 実行環境に関する注記（重要）

本 PR を作成した Claude GitHub App 実行環境では `dotnet build` / `dotnet test` が allowedTools に
含まれず実走できなかった。ビルド警告の確認は静的解析で代替しており、警告件数の実測値による裏取りは
CI（`ci.yml`）のビルドに委ねる。また `.github/workflows/` 配下は App 権限で編集不可のため、
`-warnaserror` 導入は提案に留める（後述）。

## 現状分析と対応方針

| 項目 | 現状（本ブランチ） | 対応 |
| --- | --- | --- |
| CS0618 Testcontainers Obsolete | `PostgresFixture` / `RabbitMqFixture` は既に `PostgreSqlBuilder().WithImage(...)` / `RabbitMqBuilder().WithImage(...)` の非 Obsolete API に置換済み。Obsolete コンストラクタの残存呼び出しは無し（`rg` で確認） | 追加変更なし（既に解消済みと確認） |
| CS8604 null 参照 | `DashboardEndpointTests` が `GetFromJsonAsync<T>()`（`T?`）の戻り値を拡張メソッド `Where` 等の `source` へ渡し得る | 逆デシリアライズ結果を代入時に `!` で非 null 確定させ、以降の利用から `!` を除去 |
| NU1510 不要依存 | `Shared.Infrastructure.csproj` の `Microsoft.Extensions.Diagnostics.HealthChecks` 明示参照は既に除去済み（コメントのみ残存）。中央 `Directory.Packages.props` に未使用の `PackageVersion` が残存 | 未使用の中央 `PackageVersion` を撤去（どの csproj も直接参照せず、共有フレームワーク由来のため） |
| helm values の欠落 | `deploy/helm/knowledge-platform/values.yaml` に dashboard-service エントリが無く、IADR-0011 実装がデプロイ対象から漏れる | `dashboard` サービスエントリ（port 8080・API 系と同一リソース）を追加 |
| MassTransit リトライの偏在 | `UseMessageRetry` が ConversionService のみ。ADR-0003 は全非同期連携での再試行・デッドレターを要求 | `Shared.Infrastructure` に共通リトライ拡張 `UseKnowledgePlatformRetry` を提供し、消費者を持つ Ingestion / Document / Wiki へ適用。Conversion も共通化して DRY 化 |
| `-warnaserror` | `Directory.Build.props` は `TreatWarningsAsErrors=false` | CI での有効化は「警告ゼロ」を実測確認後に別途対応（本 App は workflow 編集不可）。方針のみ記載 |

## 実装物（本 PR）

### 1. MassTransit 共通リトライ（ADR-0003）

- 新規 `src/Shared/KnowledgePlatform.Shared.Infrastructure/Extensions/MassTransitExtensions.cs`。
  `IBusFactoryConfigurator`（ブローカ非依存）への拡張 `UseKnowledgePlatformRetry()` を定義し、
  `2s / 10s / 30s` の 3 回リトライを設定する。使い切った継続失敗は MassTransit が `<queue>_error`
  （デッドレター）へ自動退避する。
- `KnowledgePlatform.Shared.Infrastructure.csproj` に `MassTransit`（core のみ）を追加。RabbitMQ 依存は
  各サービス側（`*.Worker` / `*.Api`）が引き続き保持し、共通ライブラリはブローカ非依存に保つ。
- 適用: `IngestionService.Worker` / `DocumentService.Api` / `WikiService.Api` の `UsingRabbitMq` 内へ
  `cfg.UseKnowledgePlatformRetry();` を追加。`ConversionService.Worker` は既存のインライン設定を
  同拡張呼び出しへ置換（挙動同一・重複排除）。

### 2. CS8604 の解消（`DashboardEndpointTests`）

- `var x = (await client.GetFromJsonAsync<T>(...))!;` の形で受け、拡張メソッド `Where` / インデクサ
  へ null 許容値が渡らないようにする。テストの検証内容は不変。

### 3. NU1510 の後片付け

- `Directory.Packages.props` から未使用の `Microsoft.Extensions.Diagnostics.HealthChecks` の
  `PackageVersion` を撤去（中央管理・推移ピン留めは無効のため安全）。理由をコメントで明記。

### 4. helm values に dashboard 追加

- `services.dashboard`（`enabled`/`replicas`/`image: knowledge-platform/dashboard-service`/
  `port: 8080`/`resources`）を追加。テンプレートは汎用 range のため values 追加のみでデプロイ対象になる。

## 受け入れ基準

- [x] Testcontainers に Obsolete コンストラクタの残存呼び出しが無い（`rg` で確認）。
- [x] `DashboardEndpointTests` が逆デシリアライズ結果を非 null 確定してから利用し、CS8604 誘発箇所を排除した。
- [x] `Shared.Infrastructure.csproj` に `Microsoft.Extensions.Diagnostics.HealthChecks` の明示参照が無く、
      中央 `PackageVersion` の未使用エントリも撤去した。
- [x] `values.yaml` に dashboard-service エントリを追加した（port 8080）。
- [x] `Shared.Infrastructure` が共通リトライ拡張を提供し、Ingestion / Document / Wiki / Conversion の各
      MassTransit 設定に適用されている（ADR-0003 整合）。
- [ ] CI（`ci.yml`）のビルドで対象警告が解消していること（本 App では `dotnet build` 不可のため CI で確認）。
- [ ] `-warnaserror` / `TreatWarningsAsErrors` の CI 導入（警告ゼロ実測後・要人手。App は workflow 編集不可）。
- [x] 本作業仕様書を作成した。

## 残課題・フォローアップ

- **警告ゼロの実測と `-warnaserror` 導入**: 本 PR は識別可能な指摘を静的解析ベースで解消した。実際の
  残存警告件数は CI ビルドで確認し、ゼロが確認できた段階で `Directory.Build.props` の
  `TreatWarningsAsErrors=true` もしくは CI での `-warnaserror` を導入する（回帰防止）。workflow への
  反映は App 権限外のため人手適用が必要。
- **DataSourceService**: 消費者（`AddConsumer`）を持たず発行のみのため、リトライ設定の対象外とした。
  将来コンシューマを追加する場合は同様に `UseKnowledgePlatformRetry()` を適用する。

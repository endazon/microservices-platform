---
title: 作業仕様書 — ObjectStorageBootstrapHostedService の試験を追加する（#939）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0014
  - ADR-0015
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "ADR-0014 / ADR-0015（オブジェクトストレージ）"
issue: "#939"
---

# 作業仕様書: ObjectStorageBootstrapHostedService の試験を追加する（#939）

## 起点

- 実装 issue: `#939`（親: `#901`）。`起点` は issue 本文どおり **NFR**（測定基盤・被覆向上。
  当たる番号が無いメタ作業。`.claude/rules/traceability.md`「起点 ID の種別」の場合 2 に該当し、
  計画側への環流は行わない）。
- 対象: `Platform.Shared.Infrastructure/Composable/Adapters/Storage/ObjectStorageBootstrapHostedService.cs`
  （19 行）。テスト参照 0 件であることは issue 本文の走査どおり、本仕様書冒頭の実測で再確認する。

## 母集合の取り方（`traceability.repo.md` 規則 9・10）

「起動時 HostedService でテストが無いもの」という母集合を、issue 本文の記憶（ObjectStorage だけ）で
決め打たず、リポジトリ全体を走査して引き直した。

```
$ grep -rln ': IHostedService\|BackgroundService' src --include='*.cs'
Foundation/Introspection/DriftDetectionHostedService.cs
Composable/Adapters/Storage/ObjectStorageBootstrapHostedService.cs
IngestionService.Worker/Composable/Adapters/QdrantBootstrapHostedService.cs
DataSourceService.Api/Foundation/Services/DataSourceSyncHostedService.cs

$ grep -rn 'AddHostedService<' src --include='*.cs'
ObjectStorageExtensions.cs:      AddHostedService<ObjectStorageBootstrapHostedService>()
ConfigInspectionExtensions.cs:  AddHostedService<DriftDetectionHostedService>()
DataSourceService.Api/Program.cs: AddHostedService<DataSourceSyncHostedService>()
IngestionService.Worker/Program.cs: AddHostedService<QdrantBootstrapHostedService>()
```

母集合は 4 件。1 件（本 issue の対象）を除く 3 件の除外理由:

| 対象 | 除外理由 |
| --- | --- |
| `DriftDetectionHostedService` | issue #939 本文の走査表（#901 由来）に記載済み: **`#901` 優先 4・Wolverine チェーンの C3 待ち**。本 issue の対象外として issue 側が既に確定している |
| `QdrantBootstrapHostedService` | `IngestionService.Worker.Tests/IntrospectionEndpointTests.cs:59` が DI 登録（`ImplementationType == typeof(QdrantBootstrapHostedService)`）を検査済み。**テスト参照 0 件ではない**ため「無試験」の対象に当たらない（挙動の被覆が薄いかどうかは別の issue の射程） |
| `DataSourceSyncHostedService` | 専用テスト `DataSourceService.Api.Tests/DataSourceSyncHostedServiceTests.cs` が既に存在する。**テスト参照 0 件ではない** |

→ 「テスト参照 0 件」の条件に一致するのは `ObjectStorageBootstrapHostedService` のみ。issue 本文の主張と一致。
他 3 件を本 PR の対象に含めない（1 issue = 1 PR。IADR-0116 規約 1）。

## 対象範囲

- 対象: `ObjectStorageBootstrapHostedService.StartAsync` のスキップ分岐 2 系統（クライアント型 /
  `EnsureBucketOnStartup`）と `StopAsync`。
- 対象外: `S3ObjectStorageClient.EnsureBucketAsync` の成功経路・例外握り潰し経路。**MinIO 実体が要る**
  ため単体では扱わない。issue 本文が明記するとおり `ObjectStorageRoundTripTests`
  （`[Trait("Category","Integration")]` + `[DockerFact]`）の射程であり、本 PR では新設しない
  （既存ファイルであり他担当が触れる領域でもない。存在確認のみ行い変更はしない）。

## 設計

`Platform.Shared.Infrastructure.Tests` は NSubstitute 等のモックライブラリを参照しない
（同ディレクトリの既存テスト `ObjectStorageExtensionsTests.cs` も実体オブジェクトのみで構成）。
本ホストサービスの `StartAsync` は判別 union 的な分岐

```csharp
if (client is not S3ObjectStorageClient s3 || !options.EnsureBucketOnStartup)
{ ... return; }
try { await s3.EnsureBucketAsync(cancellationToken); }
```

であり、2 つの独立した条件を **それぞれ単独で** スキップさせられるケースを用意しないと、
「`is not` → `is`」と「`!EnsureBucketOnStartup` → `EnsureBucketOnStartup`」の 2 変異を区別できない
（片方だけ検証すると、もう片方の退行が緑のまま残る）。

- **ケース A（クライアント型）**: `NullObjectStorageClient`（実クライアント未構成の標準経路）+
  `EnsureBucketOnStartup = true`（既定値）。`client is not S3ObjectStorageClient` 側だけで
  スキップが成立する。
- **ケース B（オプション）**: `S3ObjectStorageClient`（実体は `AmazonS3Client` を渡すが、
  到達しない設計であるため接続先の実在は問わない）+ `EnsureBucketOnStartup = false`。
  `!options.EnsureBucketOnStartup` 側だけでスキップが成立する。

いずれのケースも **同期的に完了する**（`StartAsync` のスキップ経路は `await` を 1 つも通らないため、
呼び出した瞬間に返る `Task` は既に `RanToCompletion` である——C# の `async Task` メソッドの一般規則）。
したがって `task.IsCompletedSuccessfully` を **await せずに** 直後で観測することが、モックなしでも
「実際に `EnsureBucketAsync` へ到達しなかったこと」を検出できる、環境非依存で決定的な assert になる
（`EnsureBucketAsync` に到達すれば `IAmazonS3` への実 I/O を `await` することになり、その瞬間には
完了し得ない）。

## 受け入れ基準（issue 本文の 3 点）

- [x] 実クライアント未構成（`NullObjectStorageClient`）のとき `StartAsync` が何もせず正常終了する
- [x] `EnsureBucketOnStartup = false` のときスキップする
- [x] `StopAsync` が完了済みタスクを返す
- [x] 変異試験: スキップ条件の反転が実際に落ちることを実測する（後述）
- 対象外（MinIO 必須）: `EnsureBucketAsync` の成功経路・例外握り潰し経路

## テスト方針

3 `[Fact]`。起点 ID は issue 自身の起点にならい無採番 `NFR`（規約上の場合 2）。

1. `実クライアント未構成のとき何もせず正常終了する`（ケース A）
2. `EnsureBucketOnStartupがfalseのときスキップする`（ケース B）
3. `StopAsyncは完了済みタスクを返す`

## 変異試験

被テストクラスを一時的に改変し、変異ごとに `dotnet build` → `dotnet test --filter ObjectStorage` を
実行して落ちる（またはビルド自体が失敗する）ことを確認したのち、必ず元に戻す。

| 変異 | 内容 | 予想される結果 |
| --- | --- | --- |
| M1 | `is not S3ObjectStorageClient s3` → `is S3ObjectStorageClient s3` | **ビルド失敗**（`s3` が定義代入されない経路が生まれ CS0165）。パターンの否定を使っているのは、if 内 return 後の到達性解析で `s3` の定義代入を成立させるためであり、素朴な反転はコンパイルさえ通らない |
| M2 | `!options.EnsureBucketOnStartup` → `options.EnsureBucketOnStartup` | ケース B が失敗する（スキップされず `try` 節に入り `IsCompletedSuccessfully` が直後には真にならない） |

証跡はレポートに実行コマンドと出力を残す。

## 計画書との差異

- 差異: なし。

## 未決事項

なし。着手前の曖昧点は無かった。

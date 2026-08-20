---
title: 作業仕様書 — EFCore.Relational バージョン競合（MSB3277）の解消
type: spec
status: completed
related_ids:
  - NFR
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - planning:projects/microservices-platform/06_technical/01_architecture-overview.md
related_specs:
  - ../../docs/tech/tech-requirements.md
  - ../adr/IADR-0003_efcore-relational-version-pin.md
related_adrs:
  - ADR-0002 (サービス境界・DB per Service)
issue: "#34"
---

# 作業仕様書: EFCore.Relational バージョン競合（MSB3277）の解消

## 目的

ビルド時に発生する `MSB3277: Found conflicts between different versions of
"Microsoft.EntityFrameworkCore.Relational"`（10.0.4 vs 10.0.9）を解消する。
DB を持つ各 API プロジェクトのビルド警告を除去し、実行時に意図しない版が
バインドされるリスクを排除する。

起点 Issue: #34

## 原因分析

`Microsoft.EntityFrameworkCore.Relational` は中央パッケージ管理
（`src/Directory.Packages.props`）で**直接ピンされておらず**、以下 2 経路で
版の異なる推移的依存として取り込まれていた。

| 取り込み元 | 版（中央ピン） | もたらす Relational |
| --- | --- | --- |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.2 | 10.0.4 |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.9 | 10.0.9 |

両者を同時参照する DB バックエンドの 4 API プロジェクトで版が衝突し MSB3277 が出る。

- `src/Services/AuthorizationService/src/AuthorizationService.Api`
- `src/Services/DataSourceService/src/DataSourceService.Api`
- `src/Services/DocumentService/src/DocumentService.Api`
- `src/Services/WikiService/src/WikiService.Api`

## 方針

中央パッケージ管理に `Microsoft.EntityFrameworkCore.Relational` の
`PackageVersion`（10.0.9）を追加し、衝突する 4 API プロジェクトに
`PackageReference`（直接ピン）を加える。これにより推移経路に関わらず
Relational は 10.0.9 に固定される（上位版が下位版を満たすため安全）。

詳細な意思決定は `../adr/IADR-0003_efcore-relational-version-pin.md` を参照。

## 作業範囲

### 含むもの
- `src/Directory.Packages.props` に `Microsoft.EntityFrameworkCore.Relational` 10.0.9 を追加
- 上記 4 API プロジェクトの `.csproj` に `Relational` の `PackageReference` を追加
- IADR-0003 の作成

### 含まないもの
- EFCore / Npgsql 本体のバージョン更新（別途検討）
- 機能・スキーマ・マイグレーションの変更

## 各テストプロジェクトへの影響確認

- 各サービスのテスト（`*.Api.Tests`）および `KnowledgePlatform.IntegrationTests`
  は `Microsoft.EntityFrameworkCore.InMemory`（Relational を取り込まない）と
  対象 API への `ProjectReference` のみを持つ。
- API プロジェクトに直接ピンした Relational 10.0.9 は通常 `PackageReference`
  として推移し、テストでも 10.0.9 に解決される。`Design` は `PrivateAssets=all`
  のため推移しない。よって**テストプロジェクト側の .csproj 変更は不要**。
- 結論: テストプロジェクトは API 側の直接ピンを継承し、新たな競合は発生しない。

## 受け入れ基準

- [ ] 4 API プロジェクトで `dotnet build` 時に MSB3277（Relational）が出ない
- [ ] `Microsoft.EntityFrameworkCore.Relational` が全プロジェクトで 10.0.9 に解決される
- [ ] テストプロジェクト（各サービス + IntegrationTests）が引き続きビルドできる
- [ ] 機能・マイグレーションに差分が出ない（パッケージ参照のみの変更）

## リスク・注意事項

- 本作業環境では .NET SDK が無効化されており `dotnet build` を実走できない。
  検証は CI（`ci.yml`）に委ねる。MSB3277 の消失は CI ログで確認すること。
- Npgsql 10.0.2 は Relational 10.0.x と互換のため、10.0.9 への引き上げは API 互換。

## 完了条件（Definition of Done 参照）

- パッケージ参照の追加のみで、ビルド警告 MSB3277 が解消されること（CI で確認）。

---
title: 全 DbContext の jsonb ValueComparer を内容ベースハッシュへ是正（Issue #184）
type: spec
status: completed
related_ids:
  - FR-12
  - SC-07
  - IADR-0042
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "PR #180 claude-review（ValueComparer hash/equals 契約）"
---

# 仕様書: 全 DbContext の jsonb ValueComparer を内容ベースハッシュへ是正（Issue #184）

## 起点となる計画書（トレーサビリティ）

- 起点: PR #180（#173 / IADR-0042）の claude-review 指摘（🟡 推奨）
- 参照実装: `ConversionJobDbContext.cs`（PR #180 で内容ベースハッシュへ是正済み）
- Issue: #184

## 目的・背景

EF Core の `ValueComparer` は `equalsExpression` と `hashCodeExpression` が同じ意味論（等しい値は等しい
ハッシュを返す）で契約しなければならない。jsonb 列（`Dictionary` 系）の一部で `equalsExpression` を
**内容ベース**（JSON 文字列比較）にしている一方、`hashCodeExpression` に `v => v.GetHashCode()`
（`Dictionary`/`List` 既定 = 参照ベース）を渡しており、**ハッシュと等価判定の契約が不整合**である。

PR #180 で導入ファイル（`ConversionJobDbContext.cs`）は是正済み。同一パターンが既存の他サービスの
DbContext にも存在するため、横断的に是正する（既存負債の是正であり機能バグではない）。

## 対象範囲

内容ベース equals（JSON serialize 比較）と参照ベース hash（`v => v.GetHashCode()`）が不整合な
`Dictionary` 系 `ValueComparer` の `hashCodeExpression` のみを是正する。

| ファイル | 対象コンパレータ | 現状 hash |
| --- | --- | --- |
| `AuthorizationService.Api/Foundation/Persistence/AuthorizationDbContext.cs` | `dictListComparer`（`Dictionary<string,List<string>>`） | `v => v.GetHashCode()` |
| `DataSourceService.Api/Foundation/Persistence/DataSourceDbContext.cs` | `Attributes` / `Tags`（`Dictionary<string,string>` ×2） | `v => v.GetHashCode()` |
| `DocumentService.Api/Foundation/Persistence/DocumentDbContext.cs` | `DictionaryComparer()`（`Dictionary<string,string>`） | `v => v.GetHashCode()` |
| `WikiService.Api/Foundation/Persistence/WikiDbContext.cs` | `Dictionary<string,string>` コンパレータ | `v => v.GetHashCode()` |

**対象外（既に内容ベースで整合）**: `List<string>` 系コンパレータ（equals=`SequenceEqual` /
hash=`v.Aggregate(0,(h,e)=>HashCode.Combine(h,e.GetHashCode()))`）。要素ベースで契約整合しているため変更しない。

## 対応内容

各対象コンパレータの `hashCodeExpression` を、`equalsExpression` と同じ内容ベースへ揃える。

```csharp
v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode()
```

参照実装（`ConversionJobDbContext.cs`）と同じ表現に統一し、意図コメントも合わせて付与する。

## 受け入れ基準

- [ ] 4 ファイルの `Dictionary` 系コンパレータの hash が内容ベース（JSON serialize）に是正されている。
- [ ] `List<string>` 系コンパレータは変更されていない（過剰変更の回避）。
- [ ] `grep -rn "v => v.GetHashCode()" src/Services/*/src/*/Foundation/Persistence/` が 0 件。
- [ ] `dotnet build` が通る。既存テストが回帰しない。
- [ ] マイグレーション・スキーマへの影響なし（`ValueComparer` は変更検知のみに影響し DDL 非依存）。

## 影響・リスク

- 現状 `Attributes`/`Tags` を検索条件・グルーピングに使っていないため実害の顕在化可能性は低い。
- `ValueComparer` はモデル比較（変更検知）にのみ用いられ、DB スキーマ／マイグレーションには影響しない。

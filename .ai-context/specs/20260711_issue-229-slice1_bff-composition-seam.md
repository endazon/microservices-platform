---
title: BFF エンドポイント合成点（器）の導入と DTO 階層化の型単位精査（Issue #229 スライス1）
type: spec
status: done
related_ids:
  - FR-14
  - ADR-0018
  - IADR-0027
  - IADR-0056
  - IADR-0063
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
related_specs:
  - "../adr/IADR-0063_bff-unit-endpoint-composition.md"
---

# 仕様書: BFF 合成点（器）の導入（Issue #229 スライス1）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14／ADR-0018／IADR-0027・IADR-0056
- 実装判断: [IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md)（合成方式 A＝ビルド時合成点。承認済み・Accepted）
- Issue: #229（フォローアップ 3・段階実装スライス1）

## 目的・背景

[IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md) で承認された合成方式 A（ビルド時合成点）の段階実装スライス1。BFF は全フロントの唯一の入口で
クリティカルなため、まず**非破壊の器（合成点の骨格）**を導入し、DTO 階層化は型単位で精査する。

## 対象範囲

- 対象（本スライス）:
  - `Platform.Bff/Composition/BffEndpointComposition.cs`（新規）: `IBffEndpointModule`＋合成点（登録簿）＋
    `MapComposedBffEndpoints()`。既存 9 モジュールを合成点経由の列挙登録へ束ねる。
  - `Program.cs`（変更）: 9 個の `app.MapXxxBffEndpoints()` を `app.MapComposedBffEndpoints()` の 1 行へ置換（挙動不変）。
  - `Platform.Bff.Tests/BffEndpointCompositionTests.cs`（新規）: 合成点経由が個別 9 呼び出しと同数のルートグループを
    登録すること・登録簿が 9 モジュールを保持することを固定。
  - [IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md) を Accepted 化し、段階計画をドメイン単位移設へ精緻化（下記知見）。
- 対象外（後続スライス）:
  - DTO 階層化・BFF エンドポイントの knowledge 移設・依存規則 例外3・`check-unit-dependencies.js` 更新。

## DTO 階層化の型単位精査（本スライスの成果・知見）

claude-review（#243）の指摘「DTO はファイル数であり型単位再精査が必要」に沿って型単位で精査した結果:

- ファイル名 ≠ 型名（例: `AnalysisDto.cs` の実型は `AnalysisTaskType` / `AnalysisDataRange` / `AnalysisTaskRequest`、
  `SearchDto.cs` は `SearchRequest` / `SearchResponse`）。
- **ナレッジ固有 DTO は事実上すべて BFF の集約エンドポイントから参照されている**（`SearchRequest`/`SearchResponse`・
  `AnalysisTaskRequest`・`DashboardUsageDto` 等）。`ChunkDto` は全域未参照（移設は無意味）。
- したがって **DTO を BFF エンドポイントより先に knowledge へ移すと platform(BFF)→可変ユニットの依存禁止に抵触**
  （鶏卵が型レベルで確定）。DTO 分離はエンドポイント移設と**ドメイン単位で同時**に行う必要がある。

→ この知見に基づき [IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md) の段階計画を「DTO 分離→エンドポイント移設」の二段から「ドメイン単位で
エンドポイント＋DTO を同時移設」へ改めた（本スライスで IADR を更新）。**本スライスでは DTO は移設しない**。

## 受け入れ基準（Issue #229）との対応

- [~] 可変ユニット追加時に platform 契約・BFF を改修せず（または合成点 1 箇所のみで）拡張できる
  → **合成点（器）を導入**（ユニットの BFF エンドポイントは合成点の登録簿 1 行で組み込む土台）。ドメイン移設は後続スライス。
    本 PR は `Refs #229`。

## 検証

- `dotnet build src/platform/backend/backend.slnx` → 0 エラー。
- `dotnet test Platform.Bff.Tests` → **96 pass**（既存 93/1skip ＋ 合成点テスト 2）。器は非破壊（既存統合テストが実ルートを担保）。
- `dotnet format --verify-no-changes`（platform）→ 差分なし。
- `node scripts/check-unit-dependencies.js` → 違反 0。`node scripts/check-doc-links.js` → 破損 0。

## 実装判断・フォローアップ

- 合成方式・段階計画（ドメイン単位移設へ精緻化）は [IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md)（Accepted）に記録。
- 次スライス: 依存規則 例外3 の準備（`src/README.md`＋`check-unit-dependencies.js`）→ ドメイン単位移設の反復。

---
title: platform からナレッジ固有イベント契約を Knowledge.Contracts へ分離する（Issue #229 スライス1）
type: spec
status: done
related_ids:
  - FR-14
  - ADR-0018
  - IADR-0027
  - IADR-0056
  - IADR-0059
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-14)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018 (契約・イベント疎結合)"
related_specs:
  - "../adr/IADR-0059_contract-layering-unit-contracts.md"
  - "../../src/README.md"
---

# 仕様書: ナレッジ固有イベント契約の Knowledge.Contracts 分離（Issue #229 スライス1）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14（構成変更のみで完結する疎結合ユニット）
- 関連 ADR: ADR-0018／IADR-0027／IADR-0056
- 実装判断: [[IADR-0059]]（契約階層化・イベント移設・URN 固定・DTO/BFF 繰延）
- Issue: #229（フォローアップ 3）

## 目的・背景

再編（#210）で platform を基盤分離したが、knowledge ドメインの 6 イベント契約が platform の
`KnowledgePlatform.Shared.Contracts` に同居している。本スライスでイベント契約を **`Knowledge.Contracts`**
（knowledge ユニット内）へ分離し、可変ユニットの契約はユニット内に閉じる階層化の第一歩とする。
既存 6 イベントの wire 後方互換（MassTransit URN）を維持する。

## 対象範囲

- 対象（新規/変更）:
  - `src/knowledge/backend/Shared/Knowledge.Contracts/Knowledge.Contracts.csproj`（新規・MassTransit 参照）。
  - 6 イベントを `Knowledge.Contracts/Events/*.cs`（namespace `Knowledge.Contracts.Events`）へ移設し、
    各に `[MessageUrn("KnowledgePlatform.Shared.Contracts.Events:<Name>")]` を付与。
  - platform `Shared.Contracts/Events/*.cs`（6 ファイル）を削除。
  - `src/knowledge/backend/backend.slnx` に Knowledge.Contracts と Tests を登録。
  - 5 サービス src csproj（Conversion/DataSource/Document/Ingestion/Wiki）に Knowledge.Contracts 参照追加。
  - 上記サービス・テスト・IntegrationTests の `using KnowledgePlatform.Shared.Contracts.Events;` →
    `using Knowledge.Contracts.Events;`。
  - 回帰テスト `src/knowledge/backend/Shared/Knowledge.Contracts.Tests/`（6 イベントの URN 一致）。
- 対象外（[[IADR-0059]] の後続スライス。#229 に follow-up として残す）:
  - knowledge 固有 DTO の Knowledge.Contracts 移設（BFF の DTO 依存解消とセット）。
  - BFF のユニット別エンドポイント合成方式。

## 後方互換（要）

- MassTransit は既定で URN を名前空間＋型名から導出する。名前空間変更で URN が変わるため、
  `[MessageUrn]` で旧 URN（`urn:message:KnowledgePlatform.Shared.Contracts.Events:<Name>`）に固定する。
- 実測確認済み: MassTransit 8.4.1 で新名前空間 + `[MessageUrn("KnowledgePlatform.Shared.Contracts.Events:DocumentUpdated")]`
  → `urn:message:KnowledgePlatform.Shared.Contracts.Events:DocumentUpdated`（旧既定と一致）。
- 回帰テストで 6 イベント全ての URN を固定する。

## 実装方針

1. イベント型は knowledge サービスのみが購読/発行（BFF はイベント**名**を pipeline.json 経由の文字列で扱い型参照しない）。
   よって移設は platform→knowledge 依存を生まず、依存方向検査（IADR-0057）を通る。
2. サービスは DTO のため `KnowledgePlatform.Shared.Contracts` 参照を維持しつつ、イベントは `Knowledge.Contracts` を参照。
3. TDD: 先に URN 一致テストを用意 → 移設 → 全テスト green。

## 受け入れ基準（Issue #229）との対応

- [x] 既存 6 イベントの後方互換が維持される → `[MessageUrn]` 固定＋URN 回帰テスト（旧値一致）。
- [~] 可変機能ユニット追加時に platform 側の契約・BFF を改修せず拡張できる → **イベント契約について達成**
  （ユニット固有イベントは `<unit>.Contracts` に置き、platform 契約に触れない）。DTO/BFF 合成は
  [[IADR-0059]] の後続スライス（#229 継続）。本 PR は `Refs #229`（Closes ではない）。

## 検証

- `dotnet build src/knowledge/backend/backend.slnx` / `dotnet build src/platform/backend/backend.slnx` green。
- `dotnet test src/knowledge/backend/backend.slnx` green（URN 回帰テスト含む）。
- `dotnet format` 差分なし。
- `node scripts/check-unit-dependencies.js` 違反 0（Knowledge.Contracts はユニット内参照）。

## 実装判断・フォローアップ

- 方式・トレードオフ（URN 固定・DTO/BFF 繰延）は [[IADR-0059]] に記録。
- DTO 移設・BFF 合成点は #229 の後続スライス。#227（改名）とは URN 固定済みのため独立。

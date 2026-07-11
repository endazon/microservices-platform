---
title: BFF ユニット別エンドポイント合成方式とナレッジ DTO 分離の設計（Issue #229 スライス2・設計）
type: spec
status: done
related_ids:
  - FR-14
  - ADR-0018
  - IADR-0027
  - IADR-0056
  - IADR-0059
  - IADR-0063
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
related_specs:
  - "../adr/IADR-0063_bff-unit-endpoint-composition.md"
  - "../adr/IADR-0059_contract-layering-unit-contracts.md"
---

# 仕様書: BFF 合成方式・ナレッジ DTO 分離の設計（Issue #229 スライス2）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14／ADR-0018（合成可能アーキテクチャ）／IADR-0027・IADR-0056・IADR-0059
- 実装判断: [[IADR-0063]]（合成方式の設計・段階実装）
- Issue: #229（フォローアップ 3・後続スライス）

## 目的・背景

#229 のイベント契約分離は [[IADR-0059]]（PR #239）で完了。残る後続スライス「knowledge 固有 DTO の分離」
「BFF のユニット別エンドポイント合成方式」を設計する。BFF は全フロントの唯一の入口でクリティカルなため、
本スライスは**設計（IADR）まで**とし、段階実装は方式承認後に別 PR で行う。

## 現状（精査結果）

- BFF（`Platform.Bff`）は `Program.cs` で `app.MapXxxBffEndpoints()` を 9 モジュール分ハードコードで呼ぶ。
  ナレッジ固有 7（Search/Document/Analysis/Feedback/Dashboard/Conversion/DataSource）＋ platform 固有 2（Config/Authz）。
- 各モジュールは名前付き `HttpClient`＋共有 DTO（`Platform.Shared.Contracts/Dtos/`）でナレッジ集約（ABAC
  スコープ解決→下流→フィルタ・存在秘匿 404）を実装。DTO は platform サービスからは未使用（knowledge＋BFF が使用）。
- DTO を `Knowledge.Contracts` へ移すと BFF（platform）が参照＝platform→可変ユニット依存禁止に抵触（鶏卵）。

## 対象範囲

- 対象（本スライス＝設計のみ）:
  - `docs/adr/IADR-0063`（合成方式の選択肢・推奨・段階実装計画。status: Proposed）。
  - 本仕様書。
- 対象外（[[IADR-0063]] 承認後の別スライス）:
  - `IBffEndpointModule` 器の導入と Program.cs 列挙化（非破壊）。
  - ナレッジ固有 DTO の `Knowledge.Contracts/Dtos/` 移設。
  - BFF エンドポイントモジュールの knowledge ユニット移設＋合成点＋依存規則 例外3＋`check-unit-dependencies.js` 更新。

## 設計判断（IADR-0063）

- **合成方式**: ビルド時合成点（フロント `features/index.ts` パターンの BFF 版）を推奨。型安全とナレッジ集約
  ロジックを保ちつつ、可変ユニットの BFF 拡張を「合成点 1 行」に閉じる。ランタイムプラグイン（複雑）・汎用
  プロキシ（ABAC 集約ロジック喪失）は不採用。
- **DTO 階層化**: ナレッジ固有 DTO → `Knowledge.Contracts/Dtos/`。platform 横断 DTO（Abac/AccessScope/ConfigInfo/
  Completion/Embed）は `Platform.Shared.Contracts` に残す。
- **依存規則**: BFF 合成点の例外3 を追加（`check-unit-dependencies.js` に実装）。

## 受け入れ基準（Issue #229）との対応

- [~] 可変機能ユニット追加時に platform 側の契約・BFF を改修せず（または合成点 1 箇所のみで）拡張できる
  → イベント契約は [[IADR-0059]] で達成済み。DTO/BFF は**本 IADR で合成方式を設計**（実装は承認後の別スライス）。
- [x] 既存 6 イベントの後方互換 → #227/IADR-0062 で後方互換なしの新体系へ統一済み（本スライス対象外・既決）。

## 検証

- `node scripts/check-doc-links.js` → 破損 0（IADR/spec の相互リンク実在）。
- 現状精査（BFF エンドポイント 9・DTO 14 の分類）を実コードと突合。

## 実装判断・フォローアップ

- 合成方式（ビルド時合成点）と段階実装計画は [[IADR-0063]] に記録。方式承認後に段階実装スライスを起票・実施。
- 依存規則 例外3・`check-unit-dependencies.js` 更新は実装スライスで行う。
- 追加ユニットの通し確認は #230（submodule 運用）のサンプルと連携。

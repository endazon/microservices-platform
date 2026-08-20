---
title: 依存規則 例外3（BFF 合成点）の準備 — 規則追記と機械検査（Issue #229・IADR-0063 段階実装 step2 = 例外3 準備）
type: spec
status: done
related_ids:
  - FR-14
  - IADR-0027
  - IADR-0056
  - IADR-0057
  - IADR-0063
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
related_specs:
  - "../adr/IADR-0063_bff-unit-endpoint-composition.md"
  - "../../src/README.md"
---

# 仕様書: 依存規則 例外3（BFF 合成点）の準備（Issue #229・IADR-0063 段階実装 step2）

> スライス採番の注記: PR #244（器）が「slice1」。本書は IADR-0063 の段階実装 **step2「例外3 準備」**。
> #243 の設計仕様書（`..._slice2_bff-composition-design.md`）とファイル名の「slice2」が重複していたため、
> 本書は slice 番号を外した命名（`..._bff-composition-exception.md`）とした（claude-review 指摘対応）。
> 以降のドメイン単位移設は step3 系として起票する。

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14／IADR-0027・IADR-0056（依存方向）・IADR-0057（機械検査）
- 実装判断: [IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md)（BFF 合成点。段階実装の「例外3 準備」ステップ）
- Issue: #229（フォローアップ 3・IADR-0063 段階実装 step2 = 例外3 準備）

## 目的・背景

[IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md) の段階実装で、次のドメイン単位移設（BFF エンドポイント＋DTO を knowledge へ移し、platform BFF の
合成点から参照）が platform→可変ユニットの参照を生む。これを許可する**例外3（BFF 合成点例外）**を、依存規則の
文書（`src/README.md`）と機械検査（`check-unit-dependencies.js`）へ**先に整備**する（移設スライスの前提）。

## 対象範囲

- 対象:
  - `src/README.md` §依存規則: 例外3 を追記（フロント合成点 例外2 の backend 版）。
  - `scripts/check-unit-dependencies.js`: `isBffCompositionHost` / `isUnitBffEndpoints` を追加し、
    `classifyProjectReference` に例外3（`Platform.Bff` → `<unit>/backend/Bff/`）を実装。ヘッダ説明・`--self-test`・
    `module.exports` を更新。
  - `scripts/scripts.test.js`: 例外3 の単体テストを追加。
- 対象外（後続スライス）:
  - 実際のドメイン移設（BFF エンドポイント＋DTO の knowledge 移設、合成点の登録簿差し替え）。

## 例外3 の定義（src/README.md §依存規則）

- **BFF の合成点（`platform/backend/Bff/Platform.Bff/`）のみ**、可変ユニットの BFF エンドポイントプロジェクト
  （`<unit>/backend/Bff/`）を参照してよい。可変ユニットは自分の BFF エンドポイントを合成点経由で BFF へ組み込む。
- 合成点以外の platform → 可変ユニット参照は引き続き禁止（BFF → 可変ユニットの**サービス**直接参照も不可）。

## 実装方針

- `classifyProjectReference` に、`fromUnit === 'platform' && isBffCompositionHost(from) && isUnitBffEndpoints(to)`
  のとき `bff-composition-exception` を許可する分岐を、`platform → 可変ユニット禁止` の前に追加。
- 現ツリーには BFF→可変ユニットの参照が存在しないため、例外3 は**不活性**（誤許可・誤検出なし）。移設スライスで
  初めて実際の参照を許可する。

## 受け入れ基準（Issue #229）との対応

- [~] 可変ユニット追加時に platform 契約・BFF を改修せず（または合成点 1 箇所のみで）拡張できる
  → 合成点（器・slice1）に続き、**BFF 合成点例外を規則・機械検査へ整備**（移設スライスの前提を用意）。`Refs #229`。

## 検証

- `node scripts/check-unit-dependencies.js --self-test` → 13 件 OK（例外3 の許可/非許可 3 ケース追加）。
- `node scripts/scripts.test.js` → 43 pass（例外3 の単体テスト追加）。
- `node scripts/check-unit-dependencies.js` → 現ツリー 違反 0（例外3 は不活性）。
- `node scripts/check-doc-links.js` → 破損 0。

## 実装判断・フォローアップ

- 例外3 の方式は [IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md) に記録済み。次スライス: ドメイン単位移設の反復（例外3 を実際に行使）。

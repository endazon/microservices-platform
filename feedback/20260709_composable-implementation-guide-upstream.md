---
title: 可変部品（プラグイン）提供者向け共通仕様の上流不足の疑い — 実装側で共通実装ガイドを新設、計画側の対応を提案
type: plan-feedback
status: open
category: 要求の不足
related_ids:
  - FR-14
  - FR-15
  - ADR-0018
source_repo: microservices-platform
source_ref: "branch claude/variable-component-specs-mziseb / docs/tech/composable-component-guide.md"
author: claude
created: 2026-07-09
---

# フィードバック: 可変部品（プラグイン）提供者向け共通仕様の上流不足の疑い

## 種別

要求の不足（の疑い）。ADR-0018 / `10_composability-design` は固定/可変の区分と宣言的構成の
**設計**を確定しているが、可変部品を**提供する側**（プラグイン開発者・新サービス追加者・
コネクタ実装者）に対する共通仕様 — 基盤が提供する契約・互換性境界・受け入れ条件 — が
計画書として存在するかが実装側から確認できず、存在しない場合は不足である。

## 起点となる計画書

- 機能要求（FR）: FR-14（宣言的構成とプラグイン追加のみで組み替え）・FR-15（構成情報 API）
- ユースケース（UC）: —
- 画面（SC）: —
- 関連 ADR: ADR-0018（コンポーザブルアーキテクチャ）
- 計画書リンク:
  - `projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md`
  - `projects/microservices-platform/06_technical/10_composability-design.md`
  - `projects/microservices-platform/06_technical/09_datasource-connectors.md`

## 現状（計画書の記述 / As-Is）

- `10_composability-design` は固定/可変の区分・宣言的パイプライン構成・安全弁（誤構成対策）の
  設計を扱う（実装リポの IADR-0027/0028 の上流）。
- `09_datasource-connectors` はコネクタの計画を扱う（実装未着手）。
- **注記（事実の限定）**: 本フィードバック起票時、実装リポジトリの planning サブモジュールが
  未取得のため計画書本文は参照できていない。上記は実装リポ内の既存参照（plan_refs・IADR の
  コンテキスト記述）からの再構成である。計画側トリアージ時に「プラグイン提供者向け共通仕様が
  既に存在するか」をまず確認されたい。存在するなら本件は「実装側ガイドとの相互リンク追加」に
  縮退する。

## 実装側の状況（To-Be の根拠）

実装リポジトリでは、可変部品を実装するための規約が以下に**分散**しており、単一の入口が
無かった:

- IADR-0027（Foundation/Composable フォルダ・依存方向規約）
- IADR-0028（宣言的パイプライン構成・fail-fast）
- `src/Services/README.md`（サービスユニット規約・サブモジュール追加手順）
- `deploy/helm/knowledge-platform/files/README.md`（段追加 3 手順・構成変更運用）
- `docs/tech/composability-classification.md`（固定/可変の棚卸し）

これを受け、実装側に **可変部品 共通実装ガイド**（`docs/tech/composable-component-guide.md`）を
新設した。内容: 基盤が提供する接続仕様（契約・横断基盤・実行時構成）、部品種別ごとの実装手順
（段・アダプタ・プロバイダ・コネクタ・新サービスユニット・フロント feature）、共通ルール、
受け入れチェックリスト。

## 提案（計画側へのお願い）

1. **確認**: プラグイン提供者向け共通仕様（またはそれに相当する節）が計画書に存在するかの確認。
2. **不足していた場合**: `10_composability-design` への追記または新規文書として、少なくとも
   以下を上流で確定することを提案する（実装非依存の水準で):
   - 基盤がプラグインに保証する契約面（イベント契約の互換性ポリシー・ポート抽象の安定性）
   - プラグインの受け入れ条件（宣言的構成への登録・誤構成時の挙動・検証ゲート）
   - コネクタ（09）を含む部品種別の分類と、それぞれの変更影響範囲（構成のみ／改版が必要）
3. **相互参照**: 計画側文書から実装ガイド（`docs/tech/composable-component-guide.md`）への参照を
   追加し、上流仕様と実装指示の対応を明示する。

## 影響

- 対応がない場合: 可変部品の追加者ごとに解釈がぶれ、FR-14 の「コア改修なしの組み替え」が
  規約の漂流により崩れるリスクがある（特に外部チーム・サブモジュールでのサービス追加時）。
- 対応した場合: 実装側ガイド §1（接続仕様）を上流仕様と照合・追随させる（実装側の保守は
  `docs/tech/composable-component-guide.md` §5 に明記済み）。

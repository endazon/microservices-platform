---
title: 10_composability-design「リスク・未決事項」のうち実装で確定済みの 3 項目（スキーマ表現力・構成情報API配置・ドリフト粒度）の反映提案
type: plan-feedback
status: open
category: その他
related_ids:
  - FR-14
  - FR-15
  - ADR-0018
source_repo: microservices-platform
source_ref: "docs/adr/IADR-0028_declarative-pipeline-config.md / docs/adr/IADR-0029_config-info-api-placement-and-drift-granularity.md / docs/specs/20260709_composability-plan-feedback-reflux.md"
author: claude
created: 2026-07-09
---

# フィードバック: 10_composability-design 未決事項のうち実装確定済み 3 項目の反映提案

## 種別

その他（計画書の誤り・不足ではなく、未決事項の実装確定に伴う状態環流）。

## 起点となる計画書

- 機能要求（FR）: FR-14・FR-15
- ユースケース（UC）: —
- 画面（SC）: —
- 関連 ADR: ADR-0018（コンポーザブルアーキテクチャ）
- 計画書リンク: `projects/microservices-platform/06_technical/10_composability-design.md`（リスク・未決事項）

## 現状（計画書の記述 / As-Is）

`10_composability-design.md` 末尾「リスク・未決事項」は 6 項目を未決として列挙している
（2026-07-07 時点の記述のまま）。うち以下は「実装リポジトリで設計する」と委ねられた項目である。

1. パイプライン定義スキーマの表現力（条件分岐・並列段をどこまで許すか）
2. 構成情報 API の実装配置（BFF 配下か既存サービス同居か）
3. ドリフト検出の判定粒度と誤検知の抑制

## 問題点 / あるべき姿（To-Be）

上記 3 項目は実装リポジトリの IADR で**確定済み**だが、上流の未決事項欄に反映されておらず、
計画書だけを読む関係者には未決のままに見える。確定分に IADR 参照を追記し、未決欄を現状へ
更新した状態があるべき姿である。

## 実装で判明した経緯（確定内容の要約）

| 上流の未決事項 | 実装での確定 | 根拠 |
| --- | --- | --- |
| 1. スキーマの表現力 | 初期範囲は直列＋段の有効/無効・キュー名上書きに限定。**入力イベント型の実行時再バインドは行わず**、入力変更はプラグイン改版（コード変更＋宣言更新）として扱う | IADR-0028（issue #111） |
| 2. 構成情報 API の配置 | **BFF 配下の管理 API**（`/bff/admin/config`・`/drift`）へ同居。独立サービス化しない（過剰分割回避を維持）。自己申告はメッシュ内部限定の `GET /internal/introspection` | IADR-0029（issue #112） |
| 3. ドリフト判定粒度 | 段の存在・有効状態・購読バインディングを突合し 5 分類（MissingApply / UndeclaredSubscription / StaleStage / BindingMismatch / Unverifiable）。キュー名差は情報レベル、到達不能時は Unverifiable に留めて誤検知を抑制 | IADR-0029（issue #112） |

残る 3 項目の実装側の状態は以下（上流の記述はそのまま有効）。

- 段数上限・負荷試験基準: 未実施（実装 issue #196 で追跡）
- イベント共通エンベロープの項目確定: 未実装・IADR-0028 で繰延（実装 issue #206。
  別記録 [20260709_composability-safety-net-gaps.md](./20260709_composability-safety-net-gaps.md) 参照）
- 既存実装への段階適用: **完了**（issue #102/#111/#112/#113、IADR-0027〜0029、
  固定/可変区分表 `docs/tech/composability-classification.md`）

## 提案（計画への反映案）

- 反映先候補: `06_technical/10_composability-design.md` の「リスク・未決事項」更新
- 提案内容:
  1. 確定済み 3 項目（スキーマ表現力・構成情報 API 配置・ドリフト粒度）を未決欄から確定扱いへ
     移し、実装 IADR（IADR-0028・IADR-0029）への参照を追記する。
  2. 「既存実装への段階適用」を完了として更新する。
  3. 本文書の status（draft）確定は、フィードバック
     `20260709_frontend-sc-screens-implemented-status.md` の提案 4 と併せて判断されたい。

## 影響範囲

- 計画側: `10_composability-design.md` の未決事項欄の更新のみ（要求・ADR の変更なし）。
- 実装側: 変更なし（確定済み内容の環流であり、実装は IADR どおり稼働中）。

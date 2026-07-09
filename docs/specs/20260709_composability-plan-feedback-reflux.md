---
title: コンポーザビリティ実装状態の計画環流 — 未決事項の確定・安全弁の未整備分のフィードバック起票とガイド追随修正
type: spec
status: fixed
related_ids:
  - FR-14
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
---

# 仕様書: コンポーザビリティ実装状態の計画環流とガイド追随修正

> 本仕様書は実装着手前に作成する。PR #205（可変部品 共通実装ガイド）の計画環流レビューで
> 判明した事項を、フィードバック記録・issue・ガイドの追随修正として確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-14・FR-15
- ユースケース（UC）: —（運用・保守要求）
- 画面（SC）: —
- 関連 ADR: ADR-0018 ／ IADR-0027・IADR-0028・IADR-0029

## 目的・背景

PR #205 で可変部品 共通実装ガイドを新設し、計画側への環流記録
（`feedback/20260709_composable-implementation-guide-upstream.md`）を起票したが、
計画リポジトリへの Issue 起票・記録コピーは未実施だった。本作業でその伝達を完了させるとともに、
環流レビューで新たに判明した以下 2 点を追加でフィードバックする。

1. 上流 `10_composability-design`「リスク・未決事項」6 項目のうち 3 項目が実装（IADR-0028・
   IADR-0029）で確定済みであり、上流に未反映である。
2. 上流 §3（共通エンベロープ・CI 契約テスト）・§5（ステージング→本番の適用順序）のうち
   3 点が実装未整備で、追跡 issue も消失していた（issue #206・#207 で追跡復活）。

## 対象範囲

- 対象:
  - `feedback/20260709_composability-open-items-resolved.md`（未決事項の確定環流）の起票
  - `feedback/20260709_composability-safety-net-gaps.md`（安全弁・契約標準の未整備分）の起票
  - `docs/tech/composable-component-guide.md` の追随修正 2 点
    （§1.1 に共通エンベロープ・契約テストの未実装注記、§2.1 に段のステートレス原則）
  - 計画リポジトリへの伝達（`draft/feedback/` への記録コピー PR ＋ plan-feedback Issue 起票）
- 対象外: コード変更（本作業はドキュメントのみ）・#206/#207 の実装対応

## 受け入れ基準

- [x] 2 件の記録が `feedback/TEMPLATE.md` 準拠で作成され、事実と提案が分離されている
- [x] ガイドの追随修正が原典（上流 §2〜§3・IADR-0028）と矛盾しない
- [x] 実装側の未整備分に追跡 issue（#206・#207）が存在する
- [x] 計画側へ両経路（記録コピー PR / plan-feedback Issue）で伝達されている

## テスト方針

ドキュメントのみの変更のためビルド・テストへの影響はない。リンク・フロントマター整合の確認に代える。

---
title: 可変部品（Composable コンポーネント）共通実装ガイドの新設と計画側フィードバック
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
  - "../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md"
---

# 仕様書: 可変部品（Composable コンポーネント）共通実装ガイドの新設と計画側フィードバック

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-14（宣言的構成とプラグイン追加のみで組み替え可能）・FR-15（構成情報 API）
- ユースケース（UC）: —（運用・保守要求）
- 画面（SC）: —
- 関連 ADR: ADR-0018（コンポーザブルアーキテクチャ）／ IADR-0027・IADR-0028
- 計画書リンク: 上記 `plan_refs` 参照

## 目的・背景

本リポジトリは固定（Foundation＝基盤）／可変（Composable＝組み替え可能部品）の区分を採用済みだが、
**可変部品を新規に実装する側**（プラグイン開発者・新サービス追加者）向けの共通仕様・実装指示は
以下のとおり複数文書に**分散**しており、単一の入口となるガイドが存在しない。

- [IADR-0027](../adr/IADR-0027_composability-folder-structure.md): フォルダ・名前空間・依存方向規約
- [IADR-0028](../adr/IADR-0028_declarative-pipeline-config.md): 宣言的パイプライン構成・fail-fast 照合
- [src/Services/README.md](../../src/Services/README.md): サービスユニット規約・サブモジュール追加手順
- [deploy/helm/knowledge-platform/files/README.md](../../deploy/helm/knowledge-platform/files/README.md):
  段追加の 3 手順（構成変更運用の一部として記載）
- [固定/可変区分表](../tech/composability-classification.md): 既存コードの棚卸し（現状の写像であり手順書ではない）

このため「基盤に接続する可変部品は何を実装し、どこに置き、どの宣言を更新し、何を満たせば受け入れ
られるか」を一気通貫で示す**共通実装ガイド**を `docs/tech/` に新設する。あわせて、上流仕様
（`10_composability-design` §2〜§5）との相互参照追加・整合確認を `/plan-feedback` 経路で環流する。

## 対象範囲

- 対象:
  - `docs/tech/composable-component-guide.md`（可変部品 共通実装ガイド）の新設
  - 既存分散文書への相互リンク追記は行わず、ガイド側からの一方向参照とする（既存文書の改変なし）
  - `feedback/20260709_composable-implementation-guide-upstream.md`（計画側フィードバック記録）の起票
- 対象外:
  - コード変更（本作業はドキュメントのみ）
  - データソースコネクタ SDK 等の未実装領域の設計（計画 `09_datasource-connectors` の実装着手時に別途）
  - 計画リポジトリ本体への反映（計画側 `/triage-feedback` の判断に委ねる）

## 設計

ガイドは次の構成とする。

1. **前提**: 固定/可変の定義（ADR-0018）と参照文書の地図
2. **基盤が可変部品に提供するもの**: `Shared.Contracts`（契約）・`Shared.Infrastructure`
   （認証・可観測性・メッセージ基盤・ストレージポート・`IPipelineStep`）・宣言的構成の読み込み
3. **部品種別ごとの実装手順**: パイプライン段／ポートアダプタ／LLM・埋め込みプロバイダ／
   データソースコネクタ（予約）／新サービスユニット／フロントエンド feature
4. **共通ルール**: 依存方向・合成ルート・トレーサビリティ・必須仕様書・テスト・検証（/verify・DoD）
5. **受け入れチェックリスト**: PR 前の自己点検項目

## 受け入れ基準

- [x] 可変部品の全種別（段・アダプタ・プロバイダ・コネクタ・サービスユニット・フロント feature）に
      ついて、実装手順と接続点（実装すべき抽象・更新すべき宣言・登録箇所）が 1 文書で辿れる
- [x] 既存規約（IADR-0027/0028・src/Services/README.md・Helm README）と矛盾しない（新規規約を発明しない）
- [x] 起点 ID・計画書リンク・関連仕様書リンクをフロントマターに備える
- [x] 計画側への環流が `feedback/` の規定形式（TEMPLATE.md 準拠）で起票されている

## テスト方針

ドキュメントのみの変更のためビルド・テストへの影響はない。`/verify` はリンク切れ・
フロントマター規約（`check-impl.js` の警告）が出ないことの確認に代える。

## 計画書との差異

- 差異: なし（計画書の不足ではない）。着手時は planning サブモジュール未取得のため
  「プラグイン提供者向け共通仕様の上流不足の疑い」として起票したが、PR #205 の AI レビュー
  （planning 取得済み環境）により、`10_composability-design` §2〜§5（プラグイン規約・イベント契約の
  標準化・差し替えポイント・安全弁）が当該共通仕様を既にカバーしていることが確認された。
  フィードバックは**相互参照の追加・整合確認の提案**へ縮退した
  （`feedback/20260709_composable-implementation-guide-upstream.md`）。

## 未決事項

- なし（コネクタ SDK の詳細は実装着手時の別仕様書で扱う）

---
title: ABAC 文書アクセス制御 機能仕様書
type: functional-spec
status: draft
related_ids:
  - FR-05
  - UC-01
  - UC-05
author: claude
created: 2026-06-27
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-05)"
---

# 機能仕様書: ABAC 文書アクセス制御

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05
- ユースケース（UC）: UC-01（横断検索）, UC-05（権限管理）
- 計画書リンク: `02_requirements/01_requirements.md`、`07_adr/ADR-0004`

## 概要

利用者の属性（部署・資格区分・ロール等）と文書の属性／タグ（機密区分・部署・ライフサイクル等）を
ABAC ポリシーで突き合わせ、**アクセス可能な文書のみ**を検索・AI 回答の対象とする。
権限の無い文書は検索結果・AI 回答のいずれにも一切現れない（deny-by-default）。

## 機能詳細

| 項目 | 内容 |
| --- | --- |
| 入力 | 利用者属性（JWT クレーム）, 検索クエリ |
| 処理 | `/authz/scope` で利用者属性 × ポリシーを評価 → アクセス可否（Granted）と多値 allow-list フィルタを解決 → 検索へ伝播 → 候補段階で権限外文書を除外 |
| 出力 | 権限内文書のみの検索結果 / AI 回答＋出典 |
| 業務ルール | ①フィルタ間は AND、許可値集合内は OR。②スコープ対象属性キーを持たない文書は除外。③利用者にマッチするポリシーが無ければアクセス不可（全件遮断）。④文書条件の無いマッチは全件許可。 |

## 主要コンポーネント

- `AbacEvaluator.ResolveScope`（AuthorizationService）: 利用者条件に合致するポリシーの文書条件を集約し、
  `AccessScopeResponse{ AllowedFilters, Granted }` を返す。`Granted` は deny-by-default の判定材料。
- `AccessScope{ Filters, GrantsAccess }`（共有契約）: 検索へ渡す多値 allow-list ＋ アクセス可否。
- `HybridSearchService`（RetrievalService）: `GrantsAccess=false` で即時に空を返し、許可時は多値フィルタを
  ベクトル・全文の両系統へ適用。
- `RagOrchestrator`（AiAnalysisService）: スコープ解決 → 未許可なら検索・LLM を呼ばず縮退 → 許可時は
  スコープを検索へ伝播。

## 例外・代替フロー

- スコープ解決の HTTP 失敗 → `Granted=false` 扱い（フェイルセーフに遮断）。
- 全文インデックス未整備 → 全文側のみ縮退（ベクトル検索へフォールバック。権限フィルタは維持）。

## 受け入れ基準との対応

- 権限の無い文書は検索結果・AI 回答のいずれにも現れない → 多値 allow-list ＋ deny-by-default（二重強制）。
- 1 つの検索窓から権限内を横断検索でき出典が付く → FR-03/FR-04 を継承。
- 個別デプロイ・ロールバック → 契約追加は後方互換、サービス単位に閉じる。

## 権限内属性値の照会（#540 / 計画 ADR-0043）

SC-01 / SC-08 の対象範囲フィルタ「**権限内のタグ／部門／プロジェクトのみ選択可**」のための候補一覧である。
**一般利用者が呼べる**（従前は `/bff/admin/authz` の管理者限定の辞書しか無かった）。

| 口 | 主体 | 返すもの |
| --- | --- | --- |
| `POST /bff/attribute-values` → `POST /search/attribute-values` | 一般利用者 | **到達できる文書に実際に付与された値のみ**（**件数なし**） |

- **辞書を丸ごと返さない**（ADR-0043 決定 1）——権限外の文書にしか付かない値から、その存在が推測できる。
- **件数を返さない**（決定 2）——「12 件だが自分の検索では 8 件」＝**見えない文書が 4 件ある**と分かる。
  **件数は値集合そのものより漏洩力が強い。**
- **検索段と同じ ABAC フィルタ**で Qdrant の facet を呼び、**件数を捨ててから**返す（[[IADR-0151]] 決定 1・2）。
- **スコープ未解決は空配列**（決定 5）——404 にも 403 にもしない。
- **読み取り口は 1 系統**（ADR-0043 決定 4）。**#542 が同じ口へシステム管理者スコープを足す。**

## 関連仕様

- 作業仕様書: `../specs/20260627_FR-05_abac-deny-by-default.md`
- テスト仕様書: `../tests/FR-05_abac-access-control.md`
- 実装 ADR: `../adr/IADR-0004_abac-multivalue-allowlist-deny-by-default.md`

## 未決事項

- 利用者属性の正規ソース（Keycloak クレームマッピング）の確定は後続タスク。

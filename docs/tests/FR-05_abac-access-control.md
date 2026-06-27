---
title: ABAC 文書アクセス制御 テスト仕様書
type: test-spec
status: draft
related_ids:
  - FR-05
  - UC-01
  - UC-05
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-05)"
---

# テスト仕様書: ABAC 文書アクセス制御（deny-by-default）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05
- ユースケース（UC）: UC-01（横断検索）, UC-05（権限管理）
- 受け入れ基準の所在（02_requirements）: `02_requirements/01_requirements.md`
- 関連 ADR: ADR-0004 / 実装 ADR: IADR-0004

## テスト対象・範囲

- 対象: `AbacEvaluator`（スコープ解決・Granted 判定）、`HybridSearchService`／`IVectorStore`
  （多値 allow-list フィルタ・deny-by-default）、検索エンドポイント `/search`。
- 対象外: 反映時間（FR-02/03）、負荷/p95、画面、Keycloak 連携の実装。

## テスト観点

- 正常系: 多値 allow-list で許可文書のみ返る、複数ポリシーの union。
- 境界/異常系: スコープ属性キー欠落文書の除外、許可ポリシー無しでの全件遮断、文書条件無しでの全件許可。
- セキュリティ: 権限外文書が検索結果・AI 回答のいずれにも現れない（受け入れ基準②）。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | 利用者条件に合致するポリシー無し | `AbacEvaluator.ResolveScope` | `Granted=false`・フィルタ空 | 権限外を出さない | 自動 |
| T-02 | 利用者条件に合致＋文書条件あり | 同上 | `Granted=true`・文書条件がフィルタ | 横断検索 | 自動 |
| T-03 | 同一キーの複数ポリシーがマッチ | 同上 | 許可値が union される | 横断検索 | 自動 |
| T-04 | マッチ＋文書条件無し | 同上 | `Granted=true`・フィルタ空（全件可） | 横断検索 | 自動 |
| T-05 | confidentiality ∈ {public,internal} の文書群 | `POST /search` with Scope | public/internal のみ返り confidential 除外 | 権限外を出さない | 自動 |
| T-06 | スコープ属性キーを持たない文書混在 | 同上 | 当該文書は除外される | 権限外を出さない | 自動 |
| T-07 | `GrantsAccess=false` | 同上 | 結果 0 件（deny-by-default） | 権限外を出さない | 自動 |
| T-08 | 単値 `AttributeFilters`（FR-03 後方互換） | `POST /search` | 既存の権限フィルタが従来どおり機能 | 横断検索 | 自動 |

## テストデータ

- `ChunkPayload`（`confidentiality`/`dept` 属性を変えた複数件、属性欠落 1 件）。
- `AbacPolicy`（user/document 条件を変えた複数件）。

## 関連仕様

- 作業仕様書: `../specs/20260627_FR-05_abac-deny-by-default.md`
- 実装 ADR: `../adr/IADR-0004_abac-multivalue-allowlist-deny-by-default.md`

## 未決事項

- 実 Qdrant に対する多値フィルタの E2E は統合環境（Docker）で別途実施。

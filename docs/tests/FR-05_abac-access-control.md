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
updated: 2026-08-10
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
| T-09 | 権限内／権限外の文書にそれぞれ別のタグ（#540） | `POST /search/attribute-values` | **到達できる文書に付いた値だけ**が返る（辞書の全値ではない。ADR-0043 決定 1） | 権限外を出さない | 自動 |
| T-10 | 同じタグが複数文書に付く（#540） | 同上 | **応答に件数が現れない**（生の本文で確認。ADR-0043 決定 2） | 権限外を出さない | 自動 |
| T-11 | 同一スコープで検索と照会を両方引く（#540） | `POST /search` ＋ `POST /search/attribute-values` | **候補は検索に現れる集合と一致**（[[IADR-0151]] 決定 1） | 横断検索 | 自動 |
| T-12 | `GrantsAccess=false` / `Scope=null`（#540） | `POST /search/attribute-values` | **空配列**（404 / 403 にしない。[[IADR-0151]] 決定 5） | 権限外を出さない | 自動 |
| T-13 | タグと ABAC 属性の両方（#540） | 同上 | `tags` と `attributes.<key>` の**両方を同じ口から**引ける | 横断検索 | 自動 |
| T-14 | 未知・空のキー（#540） | 同上 | 空集合へ縮退する | 例外フロー | 自動 |
| T-15 | BFF 経由（#540） | `POST /bff/attribute-values` | **クライアント指定の Scope を信頼しない**。不許可なら後段を呼ばず空配列 | 権限昇格の防止 | 自動 |
| T-16 | 後段が 500 / 400 を返す（#540） | 同上 | **そのステータスを透過する**（200 空配列へ**畳まない**）。縮退が守るのは権限外の存在を示さないことであって、**後段の障害を隠すことではない** | 例外フロー | 自動 |
| T-17 | 全件遮断（マッチするポリシー 0 件）を**直列化する**（#525） | `AccessScopeResponse` の JSON | 本文に **`granted: false`** が載る。`allowedFilters` は空 | 権限外を出さない | 自動 |
| T-18 | 条件なしの全件許可を直列化し、T-17 と**本文を比べる**（#525） | 同上 | `granted: true`。**`allowedFilters` は T-17 と同一（どちらも空）だが本文全体は異なる** | 権限外を出さない | 自動 |

> **T-17 / T-18 は `T-01` / `T-04` と観点が違う。** あちらは `AbacEvaluator` が返す **C# オブジェクト**の
> `Granted` を見ており、**シリアライズを通っていない**。#525 が言っているのは「**契約から**区別できない」
> ことなので、T-17 / T-18 は**本文（JSON）を直接読む**。
>
> **`POST /authz/scope` の応答値は端点越しに固定していない。** `TestWebApplicationFactory` の
> InMemory DB は固定名 `AuthzTest` でプロセス内共有であり、既存テストが**利用者条件の空なポリシー**を
> 作るため（空条件は全利用者にマッチする）、`granted=false` を端点で固定すると実行順に依存して壊れる。
> **端点では「`granted` が本文に在ること」だけを固定する**（値は主張しない）。

## テストデータ

- `ChunkPayload`（`confidentiality`/`dept` 属性を変えた複数件、属性欠落 1 件）。
- `AbacPolicy`（user/document 条件を変えた複数件）。

## 対応するテストクラス

| テストクラス | 担当するケース |
| --- | --- |
| `AbacEvaluatorTests` | T-01〜T-04（スコープ解決の意味論） |
| `AccessScopeContractTests` | **T-17 / T-18**（本文に載る形。#525） |

## 関連仕様

- 作業仕様書: `../specs/20260627_FR-05_abac-deny-by-default.md` ／
  `../specs/20260810_issue-525_access-scope-granted.md`（#525）
- 実装 ADR: `../adr/IADR-0004_abac-multivalue-allowlist-deny-by-default.md` ／
  `../adr/IADR-0159_openapi-dto-drift-checker.md`（#525）

## 未決事項

- 実 Qdrant に対する多値フィルタの E2E は統合環境（Docker）で別途実施。

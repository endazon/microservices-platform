---
title: 文書属性・タグ／ABAC ポリシー管理 機能仕様書
type: functional-spec
status: draft
related_ids:
  - FR-09
  - UC-05
author: claude
created: 2026-07-02
updated: 2026-07-02
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-09)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md"
---

# 機能仕様書: 文書属性・タグ／ABAC ポリシー管理

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-09
- ユースケース（UC）: UC-05（ABAC 権限を管理する）
- 計画書リンク: `02_requirements/01_requirements.md`、`07_adr/ADR-0004`、`06_technical/07_abac-attribute-model.md`

## 概要

システム管理者が、文書に付与する**属性辞書（取りうる値の集合）**と **ABAC ポリシー**
（利用者属性×文書属性→許可アクション）を管理（登録・取得・更新・削除・有効/無効切替）する。
保存前に入力と辞書整合を検証し、矛盾するポリシー・不正な属性値を弾く（UC-05 例外フロー）。

## 機能詳細

| 項目 | 内容 |
| --- | --- |
| 入力 | 属性辞書（key/label/allowedValues/required/scope）, ポリシー（name/action/userConditions/documentConditions）, 文書属性（key→value） |
| 処理 | 属性辞書・ポリシーの CRUD＋有効/無効切替。保存前に `AbacValidation` で入力検証・辞書整合検証。文書属性は `POST /authz/attributes/validate` で辞書整合を検証（副作用なし） |
| 出力 | 管理対象エンティティ（JSON）／検証結果 `{ valid, errors }`／エラー時は RFC7807 `ValidationProblem`（400） |
| 業務ルール | ①属性辞書のキーは同一スコープ内で一意。②Key/Scope は不変。③許可値は非空・重複不可。④ポリシーの action は read/analyze/manage。⑤条件・文書属性は辞書に定義済みキーのみ許可値整合を検証し、未定義キーは許容（段階導入）。⑥必須属性（Required）は文書属性検証で充足を強制。 |

## 主要コンポーネント

- `AttributeDefinition` / `AbacPolicy`（Domain）: `Update` / `SetActive` と `UpdatedAt` を持つ。Key/Scope 不変。
- `AbacValidation`（Services）: 属性辞書・ポリシー・文書属性の検証。エラーを文字列リストで返す。
- `AuthzEndpoints`（Endpoints）: `/authz/policies` `/authz/attributes` の CRUD、`/authz/policies/{id}/active`、
  `/authz/attributes/validate`。検証失敗は `Results.ValidationProblem`。
- `AuthorizationDbContext`: `(Key, Scope)` 一意インデックス。

## 例外・代替フロー

- 不正入力（アクション/スコープ不正・許可値空/重複・キー重複・辞書外の条件値）→ 400 `ValidationProblem`＋errors。
- 存在しない ID の取得/更新/削除/切替 → 404 NotFound。
- 文書属性検証で必須欠落・許可値外 → 200 `{ valid:false, errors:[...] }`（保存側が結果を用いて判断）。

## 受け入れ基準との対応

- 管理者が属性・タグ・ポリシーを設定できる → CRUD＋有効/無効切替 API。
- 矛盾するポリシーは保存前に検証しエラー → `AbacValidation` ＋ `ValidationProblem`（UC-05 例外フロー）。
- 各サービスを個別デプロイ・ロールバック可能 → DocumentService と疎結合（検証 API 提供に留める。IADR-0006）。

## タグ辞書（#634 / [[IADR-0152]]）

**［2026-08-09 追記 / #634］タグ辞書を新設した。** SC-09 が 2026-08-02 に確定した規則
（参照が 1 件でもあるタグは削除拒否・改名は既存文書へ追随・削除前に使用件数を示す）は
**すべて契約側の機能**であり、辞書が無いと 1 つも満たせなかった。
**`AttributeDefinition.AllowedValues` は ABAC 属性の許可値であってタグ辞書ではない**（計画も同じ切り分けをしている）。

- **所有は DocumentService**（knowledge ユニット）である（[[IADR-0152]] 決定 1）。
  使用件数が文書の局所クエリになるため——サービスを跨ぐと、削除拒否の判定のたびに同期呼び出しが要り、
  数え落としが「消してはいけないタグを消せる」事故になる。
- **読み取りは管理者・運用者**（`GET /tags`。SC-05 の裁定 Q18）、**追加はシステム管理者**（`POST /tags`。SC-09）。
- **使用件数は現行版の文書の件数である**（[[IADR-0152]] 決定 2）。
  **版履歴は数えない**——append-only で付け替えられず、数えると一度でも使われたタグを永久に削除できなくなり、
  SC-09 の「使用件数 0 件のときに限り削除できる」が空文になる。
  **アーカイブ済みの文書は数える**——アーカイブ済みでもタグは付け替えられる（実測）ため、管理者は行動できる。
- **読み取り口は `/bff/attribute-values` の 1 系統である**（ADR-0043 決定 4）。
  管理者スコープは `dictionary` フィールドとして足しており、**一般利用者の応答形は #540 から変わらない**。
- **一般利用者へ辞書を返さない**（ADR-0043 決定 1）。一般利用者の候補は Qdrant の facet 経由のままである
  （[[IADR-0152]] 決定 4。**辞書は管理面、facet は利用者面**の 2 経路を意図的に保つ）。

**改名・削除は #635 である。** 保持方式（文書がタグの識別子を参照する）の移行を伴うため分けた。
**#634 の使用件数は表示名の一致で数える暫定である。**

## 関連仕様

- 作業仕様書: `../specs/20260702_FR-09_abac-attribute-policy-management.md`
- テスト仕様書: `../tests/FR-09_abac-attribute-policy-management.md`
- 実装 ADR: `../adr/IADR-0006_abac-management-validation.md`
- 関連: `./FR-05_abac-access-control.md`（スコープ評価は本辞書・ポリシーを消費する）

## 未決事項

- 利用者スコープ属性の取得経路（Keycloak クレームマッピング）の確定。
- 辞書整合を厳格化（未定義キー禁止）へ移行する時期。

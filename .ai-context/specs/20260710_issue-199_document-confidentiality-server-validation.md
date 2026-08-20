---
title: 文書の必須属性（機密区分）のサーバー側検証（Issue #199）
type: spec
status: done
related_ids:
  - FR-05
  - FR-06
  - UC-03
  - SC-05
  - IADR-0047
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/microservices-platform/03_usecases (UC-03 例外フロー)
  - planning:projects/microservices-platform/05_screens (SC-05 機密区分必須)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-05/FR-06)
---

# 仕様書: 文書の必須属性（機密区分）のサーバー側検証（Issue #199）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-06（文書 CRUD・版管理）／FR-05（ABAC 属性）
- ユースケース(UC): UC-03（例外フロー「必須属性が未設定の場合は保存を拒否する」）
- 画面(SC): SC-05（「機密区分（ABAC属性）: 必須。必須属性未設定は保存拒否」）
- 関連 ADR: [IADR-0047](../adr/IADR-0047_document-confidentiality-server-validation.md)（本 PR で作成）、[IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md)（多層防御・サービス最終防衛線）、
  [IADR-0041](../adr/IADR-0041_document-write-bff-abac-scoped.md)（BFF ABAC スコープ・付与属性厳密検証の見送り）、[IADR-0019](../adr/IADR-0019_datasource-default-attributes.md)（データソース既定属性）、
  ADR-0004（Keycloak）
- Issue: #199

## 目的・背景

UC-03 例外フローと SC-05 は「機密区分（`confidentiality` 属性）は必須。未設定なら保存拒否」を定める。
現状この必須検証は**フロントエンドの select 既定値（`internal`）に依存**しており、サーバー側
（BFF/DocumentService）は `attributes` 未指定・`confidentiality` 欠落のリクエストを 201 で受理する。
admin/operator ロールを持つ API 直叩き・別クライアントからは機密区分の無い文書を作成できる。

下流は fail-closed（[IADR-0012](../adr/IADR-0012_retrieval-search-fail-closed-scope.md) 検索除外・[IADR-0021](../adr/IADR-0021_wiki-js-sync-graphql-push.md) `isPrivate=true`）で漏えい方向には倒れないが、
計画の受け入れ条件「保存拒否」を満たさず、属性欠落文書は検索にも Wiki にも出ないため「保存できたのに
見えない」運用混乱を招く。**最終防衛線であるサービス側（DocumentService）で必須検証を強制する**
（[IADR-0044](../adr/IADR-0044_backend-service-authorization-defense-in-depth.md) 多層防御と整合）。

## 対象範囲

- 対象:
  - `DocumentService`: 手動書き込み経路（`POST /documents`・`PUT /documents/{id}`・
    `PATCH /documents/{id}/metadata`）で `attributes.confidentiality` を必須とし、欠落・未知値は 400。
  - 正準値集合＝`public` / `internal` / `confidential` / `restricted`（FR-05・`AttributeDefinition.AllowedValues`
    と一致。静的定数として `DocumentService` に保持。動的辞書照合は [IADR-0047](../adr/IADR-0047_document-confidentiality-server-validation.md) で見送り）。
  - BFF は後段 400 をそのまま透過するため（`RelayAsync`）コード変更不要。透過を回帰テストで担保。
  - テスト: DocumentService 単体/エンドポイントで欠落・未知値 400／正常値 201/200 を検証。既存フィクスチャに
    `confidentiality` を補う。BFF で 400 透過を確認。
  - ドキュメント: 本仕様書・[IADR-0047](../adr/IADR-0047_document-confidentiality-server-validation.md)・`docs/tests/FR-06`。※ `docs/security/security.md` への反映
    （サーバー側の機密区分必須検証を防御層として記載）は **#201（PR #214）** のデータ保護表で実施し、
    本 PR では security.md を変更しない（同一ファイルの重複編集・PR 間コンフリクトを避ける）。
- 対象外:
  - **取り込み（パイプライン）経路** `Document.CreateNormalized` / `ApplyNormalized`: データソース既定属性
    （[IADR-0019](../adr/IADR-0019_datasource-default-attributes.md)）で `confidentiality` が付与される設計のため本 PR では変更しない（イベント駆動の取り込みを
    400 で落とさない）。フェイルセーフの既定補完は follow-up とする。
  - 付与属性が呼び出し者スコープ内かの厳密 ABAC 検証（[IADR-0041](../adr/IADR-0041_document-write-bff-abac-scoped.md) 見送り分。継続 follow-up）。
  - 既存の属性欠落文書の一括バックフィル（後述「移行方針」。任意 ops follow-up）。

## 実装方針

1. `DocumentService.Api.Foundation.Domain` に正準値集合と検証ヘルパー
   `DocumentAttributes.ValidateConfidentiality(attributes) -> (bool ok, string? error)` を追加（単一情報源・テスト可能）。
2. 3 つの手動書き込みエンドポイントで、タイトル必須検証（既存）の直後に機密区分検証を追加し、
   NG は `Results.ValidationProblem({ "confidentiality": [...] })`（400）を返す。
3. 取り込み経路（`CreateNormalized`/`ApplyNormalized`）は変更しない。
4. `docs/adr/IADR-0047` に決定（正準値の静的集合採用・動的辞書照合の見送り・移行方針・400 semantics）を記録。

## 移行方針（既存データ）

- 既存の属性欠落文書はレコードとしては保持する（下流 fail-closed で漏えいはしない）。
- **次回の手動編集時に正しい `confidentiality` の付与を要求する**（`PUT`/`PATCH` も必須検証対象のため、
  補正なしには更新できない＝「修正要求」方式）。[IADR-0019](../adr/IADR-0019_datasource-default-attributes.md) のフェイルセーフ既定（`internal`）に揃えた
  一括バックフィルは任意の ops follow-up とし、本 PR には含めない。

## 受け入れ基準（Issue #199）との対応

- [x] `POST /documents` に `attributes` 未指定 or `confidentiality` 欠落 → 400。
- [x] `POST /documents` に `confidentiality` が未知値（例 `secret`）→ 400。
- [x] `POST /documents` に正準値（`public`/`internal`/`confidential`/`restricted`）→ 201。
- [x] `PUT /documents/{id}` / `PATCH /documents/{id}/metadata` も同様に検証（欠落/未知値 400・正準値 200）。
- [x] 読み取り（GET）・取り込み経路・版管理は回帰なし。
- [x] BFF が後段 400 を透過する（`/bff/documents` POST/PUT）。
- [x] 受け入れ基準を `docs/tests/FR-06` テスト仕様へ写像。
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る。

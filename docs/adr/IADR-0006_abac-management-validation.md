---
title: IADR-0006 ABAC 属性・ポリシー管理の検証と DocumentService 疎結合
type: impl-adr
status: Accepted
related_ids:
  - FR-09
  - UC-05
author: claude
created: 2026-07-02
updated: 2026-07-02
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-09)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-05)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md"
  - "../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md"
---

# IADR-0006: ABAC 属性・ポリシー管理の検証と DocumentService 疎結合

- 状態: Accepted
- 日付: 2026-07-02
- 決定者: claude（実装）
- 関連: ADR-0004（Keycloak + ABAC）、IADR-0001（DocumentService がカタログ正本を所有）、IADR-0004（多値 allow-list）

## コンテキストと課題

FR-09「管理者が文書属性・タグおよび ABAC ポリシーを**設定できる**」を実装するにあたり、
既存の登録のみ API（`POST /authz/attributes` `POST /authz/policies`）に対し、以下の設計判断が必要だった。

1. 属性辞書・ポリシーの検証をどこまで・どの厳格さで行うか（UC-05 例外フロー「矛盾するポリシーは
   保存前に検証しエラーを返す」）。
2. 文書へ付与する属性値の辞書整合を、認可サービスと DocumentService のどちらが・どう担保するか。
   同期結線は受け入れ基準④（各サービスを個別デプロイ・ロールバック可能）と衝突しうる。
3. 属性辞書の Key/Scope を更新可能にするか。

## 検討した選択肢

**検証の厳格さ**
- (A) 定義済みキーのみ許可値整合を検証し、未定義キーは許容（段階導入）。
- (B) 未定義キーを一律禁止（厳格）。

**文書属性の辞書整合の担保**
- (C) DocumentService の保存フローに認可サービスを同期呼び出しで結線する。
- (D) 認可サービスは検証 API（`POST /authz/attributes/validate`）を提供し、DocumentService が保存前に
  疎結合で呼ぶ。
- (E) 属性辞書を DocumentService へ複製し、各サービスがローカル検証する。

## 決定

- 検証は **(A) 段階導入**を採用。定義済みキーのみ許可値整合を検証し、未定義キー（自由タグ・整備途上の属性）は許容する。
- 文書属性の辞書整合は **(D) 検証 API ＋疎結合**を採用。認可サービスは `POST /authz/attributes/validate` を
  提供するに留め、DocumentService への同期結線は行わない。
- 属性辞書の **Key/Scope は不変**とし、更新はラベル・許可値・必須フラグに限定する。DB では `(Key, Scope)` を
  一意インデックスで担保する。
- 検証エラーは RFC7807 `ValidationProblem`（400）で返す。ポリシーの有効/無効は削除せず `SetActive` で切替える。

## 理由

- (A): 属性体系は初期整備が段階的であり（属性体系設計の「未決事項」）、厳格な (B) は初期投入を阻害する。
  機密区分など必須項目は `Required=true` で個別に強制でき、安全性は担保される。
- (D): DocumentService がカタログ正本を所有する（IADR-0001）ため、属性の保存責務は DocumentService にある。
  同期結線 (C) は認可サービス障害が文書保存を巻き込み、個別デプロイ・ロールバック（基準④）を損なう。
  複製 (E) は辞書の二重管理と鮮度ズレを生む。検証 API による疎結合が最も独立性を保つ。
- Key/Scope 不変: 一意制約の基礎であり、変更は実質的に別辞書の新規作成に等しい。誤操作による整合崩れを防ぐ。

## 結果

- 良い影響: UC-05 の「保存前検証」を満たしつつ、サービス独立性（基準④）を維持。属性整備を段階的に進められる。
- 悪い影響・トレードオフ: 未定義キーを許容するため、辞書外タグの混入は防げない（運用ルールで統制）。
  DocumentService 側で検証 API を呼ぶ実装は本作業対象外（後続タスク）。
- フォローアップ: DocumentService の保存フローへの検証 API 組込み、未定義キー禁止（厳格化）への移行判断、
  利用者スコープ属性（clearance 等）の Keycloak クレーム取得経路の確定。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: [20260702_FR-09_abac-attribute-policy-management](../specs/20260702_FR-09_abac-attribute-policy-management.md)
- 機能仕様書: [FR-09_abac-attribute-policy-management](../functional/FR-09_abac-attribute-policy-management.md)

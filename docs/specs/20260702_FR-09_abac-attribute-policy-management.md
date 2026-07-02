---
title: 作業仕様書 — FR-09 文書属性・タグ／ABAC ポリシー管理
type: work-spec
status: in-progress
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
related_specs:
  - ../specs/20260627_FR-05_abac-deny-by-default.md
related_adrs:
  - ADR-0004 (Keycloak + ABAC)
  - IADR-0004 (ABAC フィルタの多値 allow-list 化と deny-by-default)
  - IADR-0006 (本作業で起票: ABAC 属性・ポリシー管理の検証と辞書整合)
---

# 作業仕様書: FR-09 文書属性・タグ／ABAC ポリシー管理

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-09 「管理者が、文書に付与する属性・タグおよび ABAC ポリシー（利用者属性×文書属性）を設定できる」
- ユースケース（UC）: UC-05（ABAC 権限を管理する）
- 画面（SC）: （未設定）
- 関連 ADR: ADR-0004（Keycloak + ABAC）、属性体系 `06_technical/07_abac-attribute-model.md`
- 出典: `02_requirements/01_requirements.md`

## 目的・背景

`AuthorizationService` には ABAC の骨格（属性辞書 `AttributeDefinition`、ポリシー `AbacPolicy`、
スコープ評価 `/authz/scope`）と、登録のみの API（`POST /authz/attributes`、`POST /authz/policies`）が
既に存在する（FR-05 実装）。しかし FR-09「管理者が属性・タグ・ポリシーを**設定できる**」を満たすには
以下が不足していた。

1. **管理操作の欠落**: 取得（個別）・更新・削除・有効/無効切替が無く、登録後の運用ができない。
2. **入力検証の欠落**: 不正なアクション・スコープ、許可値の空/重複、キー重複などを弾けず、
   UC-05 例外フロー「矛盾するポリシーは保存前に検証しエラーを返す」を満たさない。
3. **辞書整合の欠落**: 文書へ付与する属性値・ポリシー条件値が属性辞書（許可値）と整合しているかを
   検証する手段が無い。

## 対象範囲

### 含むもの
- ドメイン: `AttributeDefinition.Update`（Key/Scope 不変）、`AbacPolicy.Update` / `SetActive`、両者に `UpdatedAt`。
- 検証 `Services/AbacValidation.cs`:
  - 属性辞書: key/label 必須・scope 種別・許可値の非空/重複・同一スコープ内キー一意。
  - ポリシー: name 必須・action 種別・条件の値集合非空・**辞書に定義済みキーは許可値整合**（未定義キーは許容）。
  - 文書属性: 必須属性の充足・許可値整合（未定義キーは自由タグとして許容）。
- API `Endpoints/AuthzEndpoints.cs`:
  - ポリシー `GET /policies/{id}` `PUT /policies/{id}` `PATCH /policies/{id}/active` `DELETE /policies/{id}`（＋既存の一覧/登録に検証追加）。
  - 属性辞書 `GET /attributes/{id}` `PUT /attributes/{id}` `DELETE /attributes/{id}`（＋既存の一覧/登録に検証追加）。
  - 文書属性検証 `POST /authz/attributes/validate` → `{ valid, errors }`（副作用なし）。
  - 検証エラーは RFC7807 `ValidationProblem`（400）で返す。
- 永続化: `(Key, Scope)` 一意インデックス、`UpdatedAt`。マイグレーション `AddAbacManagementFields`。
- テスト: 単体 `AbacValidationTests`、結合 `AuthzManagementEndpointTests`。
- 実装 ADR: IADR-0006。

### 含まないもの
- 画面（SC 未設定）。
- DocumentService への属性保存の結線（本作業は検証 API 提供に留める。理由は下記「設計判断」）。
- Keycloak の利用者属性マッピング本実装（利用者スコープ属性は辞書に定義可能だが取得経路は別タスク）。
- 属性変更に伴う再索引（FR-02/FR-03 のインジェスト経路の責務）。

## 設計

### API 一覧（`/authz`）

| メソッド | パス | 用途 |
| --- | --- | --- |
| GET | `/policies` `/policies/{id}` | ポリシー一覧 / 個別取得 |
| POST | `/policies` | 登録（保存前検証） |
| PUT | `/policies/{id}` | 更新（保存前検証） |
| PATCH | `/policies/{id}/active` | 有効/無効切替 |
| DELETE | `/policies/{id}` | 削除 |
| GET | `/attributes` `/attributes/{id}` | 属性辞書一覧 / 個別取得 |
| POST | `/attributes` | 登録（キー重複・許可値検証） |
| PUT | `/attributes/{id}` | 更新（Key/Scope 不変、許可値・重複検証） |
| DELETE | `/attributes/{id}` | 削除 |
| POST | `/attributes/validate` | 文書属性の辞書整合検証（`{valid, errors}`） |

### 辞書整合の検証方針（段階導入）

- ポリシー条件値・文書属性値は、**属性辞書に定義済みのキーのみ**許可値整合を検証する。
- 未定義キー（自由タグ・段階導入中の属性）は許容する。属性体系の初期整備を妨げないため。
- 機密区分など必須の基本属性は `Required=true` の属性辞書として定義し、文書属性検証で充足を強制する。

### 設計判断（→ IADR-0006）

- **DocumentService への同期結線は避け、検証 API 提供に留める**。文書属性の保存責務は DocumentService に
  あり（IADR-0001）、認可サービスが同期呼び出しで結線すると受け入れ基準④（各サービスを個別デプロイ・
  ロールバック可能）に反する。DocumentService は保存前に `POST /authz/attributes/validate` を呼ぶ疎結合とする。
- **Key/Scope は不変**。一意制約 `(Key, Scope)` の基礎であり、変更は実質的な別辞書の新規作成に等しいため
  更新対象から除外する（ラベル・許可値・必須フラグのみ更新可）。

## 受け入れ基準（本作業で満たす範囲）

FR-09 の Issue に転記された受け入れ基準は FR-05（横断検索・deny-by-default・p95）由来の横断項目であり、
本作業（管理機能）で直接満たすのは④の独立デプロイ性。他は FR-05 実装が担保する。

- [ ] 1 つの検索窓から横断検索・出典付与（FR-03/04 の責務。本作業対象外）。
- [ ] 権限外文書を検索・回答に出さない（FR-05 の責務。本作業は属性・ポリシーの整合性で素地を支える）。
- [ ] 文書更新後 15 分以内に反映（インジェスト経路の責務。本作業対象外）。
- [x] 各サービスを個別デプロイ・ロールバック可能（DocumentService と疎結合な検証 API に留める）。
- [ ] p95 レイテンシ目標（負荷試験で別途確認）。

UC-05 の受け入れ（本作業の主眼）:

- [x] 管理者が属性辞書・タグ・ポリシーを登録/取得/更新/削除/有効化できる。
- [x] 矛盾するポリシー（辞書外の値・不正アクション等）は保存前に検証しエラーを返す。

## テスト方針

- **AbacValidation 単体**: 属性辞書（必須欠落・許可値空/重複・スコープ不正・キー重複・更新時の自己除外）、
  ポリシー（アクション不正・辞書外値・未定義キー許容・値集合空）、文書属性（必須欠落・許可値外・自由タグ許容）。
- **管理エンドポイント結合（InMemory）**: 属性の登録→取得/更新/削除、重複キー 400、許可値空 400、
  ポリシーのライフサイクル（登録→更新→無効化→削除）、不正アクション 400、辞書外条件 400、文書属性検証 valid/invalid。

## 計画書との差異

- 差異: なし（ADR-0004・属性体系設計に忠実）。DocumentService との疎結合方針は IADR-0006 に記録。

## 未決事項

- 利用者スコープ属性（clearance 等）の取得経路（Keycloak クレームマッピング）は別タスク。
- 辞書整合を「未定義キー許容（段階導入）」から「厳格（未定義キー禁止）」へ移行する時期は運用開始後に判断。

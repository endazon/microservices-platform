---
title: IADR-0035 フロントエンドのロールベース・ナビゲーションと存在秘匿
type: impl-adr
status: Accepted
related_ids:
  - SC-10
  - SC-09
  - SC-11
  - FR-09
  - FR-10
  - ADR-0004
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md"
---

# IADR-0035: フロントエンドのロールベース・ナビゲーションと存在秘匿

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: SC-09（管理者設定）・SC-10（運用ダッシュボード）・SC-11（構成ビューア）／ FR-09（ABAC・ロール）・FR-10（利用状況ダッシュボード）
- 関連 ADR: ADR-0004（ABAC 認可モデル）／ [[IADR-0009]]（存在秘匿）／ [[IADR-0030]]（運用者ロール・ConfigViewer）／ [[IADR-0031]]（NameClaimType=preferred_username）／ [[IADR-0033]]（フロントエンド SPA 基盤）
- 関連する実装仕様書: `docs/screens/SC-10_operations-dashboard.md`

## コンテキストと課題

SC-09/SC-10/SC-11 は管理者（`platform-admin`）または運用者（`platform-operator`）に限定される画面である。基盤（#126）は認証（`RequireAuth`）のみを備え、UI レベルのロール判定・メニュー出し分け・存在秘匿の仕組みを持たない。各画面が個別にロール判定を実装すると重複と齟齬を生むため、基盤に共通の仕組みを 1 つ用意する。

決めること:
1. SPA がロールを **どこから** 読むか。
2. 権限外ユーザーに対する画面・メニューの扱い（存在秘匿の UI 表現）。
3. サーバ側認可との関係（UI 判定を信頼境界にしない）。

## 検討した選択肢

- **A. アクセストークン（JWT）の `realm_access.roles` をクライアントで復号して読む**（採用）
  - バックエンドがロールを解決している一次情報（`KeycloakRolesClaimsTransformation` が `realm_access.roles` を `ClaimTypes.Role` へ展開）と同一ソース。追加の API 往復が不要。
- B. `user.profile`（id_token / userinfo クレーム）から読む
  - Keycloak の既定では realm ロールは access_token に入り、id_token/userinfo には必ずしも含まれない。マッパー追加の運用依存が生じる。
- C. ロール取得用の BFF エンドポイント（例: `/bff/me`）を新設する
  - 往復が増え、基盤に新規バックエンドを要する。本フェーズのフロント優先方針に反する。

## 決定

1. **ロールの読み取り**: `foundation/auth/roles.ts` で `oidc-client-ts` の `User.access_token`（JWT）のペイロードを復号し `realm_access.roles: string[]` を取得する。復号不能・欠落時は空配列（＝権限なし）として扱う（フェイルクローズ）。`useRoles()` / `useHasAnyRole(...roles)` フックと `RequireRole` ルートガードを提供する。ロール定数は `platform-admin` / `platform-operator`（バックエンド `KnowledgePlatformAuthPolicies` と一致）。
2. **メニューの存在秘匿**: `FeatureModule.nav` に `requiresAnyRole?` を持たせ、`Layout` は権限のある項目のみを描画する。権限外にはメニューを **表示しない**（[[IADR-0009]] の存在秘匿の UI 表現）。
3. **直接遷移時の存在秘匿**: `RequireRole` は権限外の場合 `NotFound`（404 相当）を描画し、画面の存在を示さない・`/login` へも誘導しない。
4. **信頼境界はサーバ**: UI 判定は利便性・存在秘匿のためであり、認可の実効境界は BFF/サービス側（`AdminOnly` は 403、`ConfigViewer` は 404 秘匿）に置く。UI をすり抜けても API が拒否する。

## 理由

- バックエンドと同じ realm ロールを一次情報にするため、UI とサーバの判定が一致しやすい。
- 追加の API・バックエンド変更なしに、SC-09/10/11 で再利用できる共通部品を基盤へ 1 度だけ入れられる。
- クライアント復号は表示制御にのみ用い、改ざん耐性はサーバ側認可が担保するため安全側に倒れている（フェイルクローズ）。

## 結果

- 良い影響: 3 画面（SC-09/10/11）でロール判定・メニュー出し分け・存在秘匿を共通化。SC-11 #140 の存在秘匿要件をそのまま満たす。
- 悪い影響・トレードオフ: access_token をクライアントで復号する（署名検証はしない）。表示制御専用であり許容する。realm ロールのクレーム構造（`realm_access.roles`）に依存する。
- フォローアップ: SC-11（#137/#140）・SC-09（#135）実装時に本部品を再利用する。ConfigViewer 相当は `requiresAnyRole:['platform-admin','platform-operator']` で表現する。

## 関連

- Supersedes: なし
- Superseded by: なし

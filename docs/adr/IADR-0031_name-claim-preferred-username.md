---
title: IADR-0031 送信者名クレームは preferred_username を Identity.Name に解決する
type: impl-adr
status: Accepted
related_ids:
  - FR-08
  - FR-15
  - ADR-0004
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# IADR-0031: 送信者名クレームは preferred_username を Identity.Name に解決する

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: claude（Issue #118 プラットフォーム監査の是正）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: FR-08（フィードバックの送信者特定）・FR-15（構成情報 API の
  監査ログ subject）・ADR-0004（Keycloak OIDC/JWT 認証）
- 関連する実装仕様書: [作業仕様書](../specs/20260708_audit_platform-consistency.md)・
  [IADR-0010](./IADR-0010_feedback-service-and-upsert.md)（フィードバックの `(AnswerId, UserId)` upsert）・
  [IADR-0029](./IADR-0029_config-info-api-placement-and-drift-granularity.md)（構成情報 API・監査ログ）・
  [セキュリティ仕様書](../security/security.md)

## コンテキストと課題

Issue #118 のプラットフォーム監査を compose 実環境で実施したところ、実 Keycloak が発行する
トークンで `HttpContext.User.Identity?.Name` が **null** になる事象を実測した。原因は、ASP.NET Core の
`JwtBearer` 既定の名前クレームマップが `unique_name`（および `name`）のみを `Identity.Name` へ写像する
一方、Keycloak の標準トークンは送信者の識別子を **`preferred_username`** クレームで発行し、既定では
`unique_name` を含まないためである。

これにより計画の 2 箇所が機能不全に陥っていた:

- **FR-08（送信者特定）**: `FeedbackEndpoints` は `http.User.Identity?.Name` を UserId として使用し
  （`?? "anonymous"`）、[IADR-0010] の `(AnswerId, UserId)` 冪等 upsert の一意キーに据える。Name が
  null になると **全利用者の UserId が "anonymous" に潰れ**、利用者単位の upsert が別人同士で衝突・
  上書きし合う（IADR-0010 の設計意図が崩壊する）。
- **FR-15（構成情報 API の監査ログ）**: [IADR-0029] が規定する監査ログの subject が同じ経路で
  **unknown に潰れ**、「誰が実効構成を閲覧したか」を追跡できない。

決める点: (1) 送信者名をどのクレームから解決するか、(2) 解決の実装位置（各サービスか共有基盤か）、
(3) 波及範囲。

## 検討した選択肢

1. **共有 `AuthExtensions` で `NameClaimType = "preferred_username"` を設定する（採用）**
   - 認証基盤（`AddKnowledgePlatformAuth`）は全サービスが共有する単一の合流点であり、ここで名前
     クレームを一度だけ解決すれば FR-08・FR-15 を含む全経路の `Identity.Name` が一貫する。既存の
     `RoleClaimType`／`KeycloakRolesClaimsTransformation` と同じ層で完結し、呼び出し側のコード変更が不要。
2. 各エンドポイントで `User.FindFirst("preferred_username")` を個別参照する
   - 共有基盤を触らずに済むが、送信者を使う箇所（FeedbackService・構成情報 API・将来の監査経路）ごとに
     重複実装が必要で、参照漏れが再び anonymous/unknown を生む。IADR-0010 の一意キーの正しさが
     各呼び出し側の実装に依存してしまう。
3. Keycloak 側のプロトコルマッパーで `unique_name` クレームを追加発行する
   - 標準外のクレームを増やす運用負担があり、レルム設定と実装の乖離が新たな齟齬源になる。ASP.NET Core
     側の既定マップに実装を合わせる（選択肢 1）ほうが標準的で移植性が高い。

## 決定

選択肢 1 を採用する。共有 `AuthExtensions.AddKnowledgePlatformAuth` の
`TokenValidationParameters.NameClaimType` を **`"preferred_username"`** に設定する
（`src/Shared/KnowledgePlatform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs`）。

- **解決方針**: 送信者の表示・追跡用識別子は Keycloak の `preferred_username` を正とし、`Identity.Name`
  へ写像する。UserId としての一意性・不変性が将来問題になる場合（ユーザー名変更等）は `sub`（不変 ID）
  への切替を再考する（下記「再考条件」）。
- **影響範囲**: `AddKnowledgePlatformAuth` を呼ぶ全サービスの `Identity.Name` が実ユーザー名に解決される。
  意図的な波及であり、[IADR-0010]（FR-08 の userId・upsert キー）と [IADR-0029]（FR-15 の監査ログ
  subject）の設計意図をそのまま満たす方向の是正である（既存 ADR の範囲内・巻き戻し不要）。
- **後方互換**: 匿名フォールバック（`?? "anonymous"`）は維持する。無認証・名前クレーム欠落時は従来どおり
  anonymous を用いる。

## 影響・結果

- FR-08 の userId が実ユーザー名（compose 実測で `poc-user`）で記録され、利用者単位の upsert が
  正しく機能する。FR-15 の監査ログ subject も実ユーザーで特定できる。
- 認証基盤の共有変更のため、ロールクレーム展開（`KeycloakRolesClaimsTransformation`）と同様に
  全サービス共通の挙動となる。回帰は既存の認証・フィードバック系テストで担保する。

## 却下した場合の再考条件

- ユーザー名（`preferred_username`）が可変で、リネーム時に過去フィードバックの帰属が壊れる要件が
  顕在化した場合は、不変 ID（`sub`）を UserId に用いる設計へ切り替える（IADR-0010 の一意キー再設計を伴う）。

[IADR-0010]: ./IADR-0010_feedback-service-and-upsert.md
[IADR-0029]: ./IADR-0029_config-info-api-placement-and-drift-granularity.md

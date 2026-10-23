---
title: 作業仕様書 — 無人主体の判定を IdP が付与するクライアント識別に基づかせる（GHSA-94cm-55g9-2967）
type: spec
status: done
related_ids: [NFR-09, ADR-0032, ADR-0036, ADR-0085, IADR-0606, IADR-0420, IADR-0429, IADR-0449, IADR-0410, IADR-0476]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
issue: "GHSA-94cm-55g9-2967"
---

# 作業仕様書 — 無人主体の判定を IdP が付与するクライアント識別に基づかせる

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。判断の記録は **IADR-0606**（新規）に置く。
> 多層防御の強化。参照は GHSA の ID だけとする。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-09**（全 API で OIDC/JWT 認証。エッジ〔BFF〕で担保）。
- 計画 ADR: **ADR-0032**（BFF セッション方式。人の経路は Cookie ＋ CSRF）・**ADR-0036**・**ADR-0085 決定 4**（ユニットの主体の計上）。
  矛盾しない —— 計画は「無人の主体」をどの属性で識別するかを定めていない（実装裁量。IADR-0420）。本件はその実装裁量の中で判定を強める。
- 実装 ADR: **IADR-0606**（新設）。前提: IADR-0420（`MachinePrincipal` を基盤に 1 つ置く）・IADR-0429 決定 3（BFF の Bearer 受理）・
  IADR-0449（`InteractiveUser`）・IADR-0410 / IADR-0426 / IADR-0476（`TrustedUserContextRelay`）。

## 要件（コーディネーター指示 2026-10-23）

1. 無人主体の判定を、利用者名の接頭辞だけでなく、IdP が付与し利用者が改ざんできない属性で行う。
2. `MachinePrincipal` の判定結果を参照する箇所をすべて洗い出し、影響範囲を記す。
3. realm 側の多層防御を検討する（主たる修正はコード側）。
4. 既存の正規の無人主体（各サービスのサービスアカウント）が引き続き無人と判定されることを試験で固定する。

## 使える属性の検討（既存の s2s 認証の設計を読んだ）

| 候補 | 判断 | 理由 |
| --- | --- | --- |
| `azp`（authorized party） | **採る** | Keycloak がトークンを発行したクライアントを書く値で、利用者は変えられない。Keycloak の access token には常に付く。サービスアカウントのトークンでは `azp = <clientId>`、利用者名は `service-account-<clientId 小文字>`（`profile` を持つとき）で、**両者は同じクライアントを指す** |
| `client_id` / `clientId` クレーム | 補助として読む（従前どおり） | 26 の `service_account` スコープの形。realm を宣言から取り込むと載らない（IADR-0524）。載れば `azp` と同値 |
| `sid` の有無（セッションの有無） | 採らない | 26 の client credentials の形を稼働で測っていない。不在を根拠にする判定は、版の差で黙って反転する |
| 専用のロール（`platform-service` 等） | 採らない | サービスアカウントの全員が持つロールは無い（`identity-admin` などはロールなし。`ai-stock-trading-kb-writer` は人と同じ `platform-operator`）。ロールで判定すると IADR-0420 が退けた「構成・付与の一覧」に戻る |
| 構成の許可リスト | 採らない | IADR-0420 の「一覧を構成に持たない」（抜け道になる）を覆す |

## 決定（詳細は IADR-0606）

`MachinePrincipal.IsMachine` を次の形にする:

- **前提**: クライアント識別のクレーム（`azp` → `client_id` → `clientId` の順で最初に在るもの）がある。無ければ無人ではない。
- **腕 A**: 利用者名が `service-account-<そのクライアント識別>` と大小を無視して**一致する**（従前は接頭辞だけ）。
- **腕 B**: 利用者名が無い（従前どおり）。

`ClientIdOf` は変えない（`azp` を第一に、無ければ利用者名から復元。ログ・計器の属性に使う）。`IsMachine` が真のときは常にクレームの値を返す。

## 影響範囲（`git grep -n "MachinePrincipal\.\|IsMachine\b" -- 'src/**/*.cs'`。試験を除く）

| # | 箇所 | 判定の使い方 | 本件での変化 |
| --- | --- | --- | --- |
| 1 | `Platform.Bff/Foundation/Session/BearerCallerPolicy.cs` | BFF の Bearer 受理（無人だけ） | 利用者名と `azp` が一致しない主体は受理しない |
| 2 | `Platform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs`（`InteractiveUser`） | 名簿の読み口（AuthorizationService・BFF の lookup / resolve） | 人として扱う側へ寄る（判定の否定） |
| 3 | `Platform.Shared.Infrastructure/Foundation/Observability/TrustedUserContextRelay.cs` | 本文の利用者文脈を信じる中継者（DocumentService の読み取り・タグ反映・MCP ツール実行、GraphService の近傍・MCP ツール実行、RetrievalService の検索・属性値・MCP ツール実行） | 利用者名だけで `azp` を持たない主体は信じない（実トークンには常に `azp` がある） |
| 4 | `DocumentService/Features/Documents/DocumentManageScope.cs`（`MachineSubject` / `OwnerSubject` / `ForbidUnlessAdminOrMachine`） | 機械クライアントの主体名・入口の門 | 一致しない主体は人として扱う |
| 5 | `DocumentService/Features/Documents/DocumentReadPrincipal.cs`（`FromUser`） | 読み取りの主体（機械は個人資料を読まない） | 同上 |
| 6 | `DocumentService/Common/Observability/UnitProjectMetrics.cs` | ユニットの主体の計上 | 同上（`unknown` の枝は到達しなくなるが防御として残す） |
| 7 | `Knowledge.Bff.Endpoints/Documents/DocumentReadGrpcClient.cs`（`ToUserContext`） | 機械なら利用者文脈を載せない | 同上 |
| 8 | 各 gRPC 面のログ（`ClientIdOf(caller) ?? "(unknown)"`。DocumentService / GraphService / RetrievalService） | 拒否時のログの属性 | 変化なし（`ClientIdOf` は不変） |

接頭辞の定数を**文字列に対して**使う箇所（`ClaimsPrincipal` を見ない。本件の射程外として記録する）:

| 箇所 | 使い方 | 判断 |
| --- | --- | --- |
| `DocumentReadPrincipal.RelayedUser`・各 MCP 実行面の `IsServiceAccount(userId)` | 中継者が本文で運んだ利用者識別子を機械として扱い、個人資料を除く | **変えない**。中継者は許可集合で絞られており、結果は権限を狭める向き（個人資料を返さない）にしか働かない |
| `AstStaleCopyRules.CurrentAccount`・`McpServer/Domain/ToolInvocation`・`DocumentManageScope.MachineSubject` の名前組み立て | サービスアカウント名の組み立て | 変えない（判定ではない） |
| `AuthorizationService/Domain/DepartmentAttributeReconciliation.IsServiceAccount` | 部門の同期の除外 | 本件の射程外（`ClaimsPrincipal` の判定ではない。管理 API の利用者表現を読む）。報告に挙げる |

## 試験（受け入れ基準）

| # | 基準 | 写像先 |
| --- | --- | --- |
| A1 | 接頭辞を持つ利用者名でも、`azp` がそのサービスアカウントのクライアントでなければ無人ではない | `MachinePrincipalTests`（3 例）・`BearerCallerPolicyTests`（3 例）・`TrustedUserContextRelayTests`・`MachineClientOwnDocumentWriteTests`（PATCH / DELETE が 403） |
| A2 | クライアント識別のクレームが無ければ無人ではない | `MachinePrincipalTests`・`TrustedUserContextRelayTests`・`DocumentReadRelayOptionsTests`・`UnitProjectMetricsTests` |
| A3 | realm の宣言のサービスアカウント（`serviceAccountsEnabled` の全クライアント。0 件で緑にしない）は、利用者名あり・なしのどちらの形でも無人 | `MachinePrincipalTests.realmの宣言のサービスアカウントはすべて無人主体と判定する` |
| A4 | realm の宣言の人の利用者は、どのクライアントの `azp` でも無人ではない | `MachinePrincipalTests.realmの宣言の人の利用者はどのクライアントでも無人主体と判定しない` |
| A5 | 既存の s2s の試験（BFF・AuthorizationService・DocumentService・GraphService・RetrievalService・DashboardService・統合試験の MCP）が通る | 下の検証 |
| A6 | 変異（腕 A を接頭辞だけへ戻す／前提を外して従前の実装へ戻す）で A1・A2 が赤になる | 下の検証 |

試験器の追随: 試験用の認証ハンドラ・JWT 発行器（DocumentService の `TestAuthHandler`、各 `GrpcKestrelFactory.IssueToken`）は、
`service-account-<clientId>` を名乗る主体に**実トークンと同じ `azp = <clientId>`** を補う（`azp` を明示したときは補わない）。
BFF・AuthorizationService の試験はサービスアカウントの要求に `azp` のヘッダを明示する。
従前「`azp` なしでもサービスアカウント名から信じる」を陽性に置いていた試験 3 件（DocumentService・GraphService・RetrievalService の中継者の試験）は、
その形を実トークンが取らないので `azp` ありの 1 件に畳んだ。

## realm 側の多層防御（検討）

- 既に在る: `scripts/check-realm-constraints.js` 検査 7（#1589）が、realm の宣言で人の利用者名が `service-account-` で始まることを禁じ、
  自己登録・利用者名の編集・メールアドレスを利用者名にする設定を例外なしでは禁じる（#1596）。宣言の realm は `registrationAllowed=false`・`editUsernameAllowed=false`。
- 採らなかった: 宣言型ユーザープロファイルの `username` へパターン検証を足す。稼働中の realm の再適用（`reconcile-realm.js`）と、
  既存のサービスアカウントの利用者の更新が検証に掛かるかを稼働で測る必要があり、本 PR では稼働クラスタに触れない。
  コード側の判定が利用者名と `azp` の一致を要るようになったので、宣言の外で作られた利用者名だけでは無人と読まれない。

## 母集合（規則 9・10）

- 判定の記述を引いた: `git grep -n "service-account-\` で始まる\|腕 A" -- docs scripts deploy`。
  - **更新**: `docs/functional/FR-06_document-crud-versioning.md`（主体の説明）・`docs/observability/knowledge-health-indicators.md`（主体の識別）。trace ブロックへ IADR-0606・本仕様書を足した。
  - **変えない**: `docs/api/east-west-grpc.md`・`docs/tests/FR-16_mcp-server.md`（本文で運ばれた利用者識別子の扱い＝上の射程外）／
    `docs/screens/SC-17_user-account-management.md`・`docs/tests/SC-17_user-account-management.md`・`scripts/check-realm-constraints.js`（realm の宣言の検査。多層防御として有効なまま）／
    `docs/operations/operations.md`（運用の注意）。
  - `.ai-context/adr/*`・`.ai-context/specs/*` は凍結記録なので書き換えない（IADR-0420 へは IADR-0606 から参照する）。

## 検証

- `dotnet test Platform.Shared.Infrastructure.Tests` → 587 件 pass
- `dotnet test Platform.Bff.Tests` → 1077 pass / 1 skip
- `dotnet test AuthorizationService.Tests` → 599 pass
- `dotnet test DocumentService.Tests` → 1140 pass（新しい 403 の試験 2 件を足す前の全件）・足した後の `MachineClientOwnDocumentWriteTests` 23 pass
- `dotnet test GraphService.Tests` → 855 pass／`RetrievalService.Tests` → 573 pass（いずれも中継者の試験を畳む前の全件。畳んだ後に `--filter TrustedRelay` で 18 / 37 pass）／`DashboardService.Tests` → 98 pass
- `dotnet test Knowledge.IntegrationTests --filter McpTool` → 4 pass
- 変異: 腕 A を接頭辞へ戻す → Shared 5 件・DocumentService 2 件が赤／従前の実装へ戻す → Shared 6 件が赤（いずれも戻して緑）

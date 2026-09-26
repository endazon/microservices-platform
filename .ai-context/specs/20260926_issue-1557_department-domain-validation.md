---
title: 作業仕様書 — SC-06 で明示した部門を、書き込み時に realm の部門グループの値域で検証する（#1557・計画 ADR-0115 決定 5）
type: spec
status: in-progress
related_ids:
  - FR-05
  - FR-01
  - UC-04
  - SC-06
  - ADR-0074
  - ADR-0115
  - ADR-0064
  - IADR-0199
  - IADR-0329
  - IADR-0379
  - IADR-0401
  - IADR-0431
  - IADR-0468
  - IADR-0472
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0115_department-domain-is-realm-group-and-default-from-registrant.md 決定 1（値域＝`/department/<code>` の `<code>`）・決定 5（明示値は書き込み時に値域で検証する。ADR-0074 決定 4 と同じ形）
  - planning:projects/microservices-platform/07_adr/ADR-0074_owner-mapping-table-container-in-sc06.md 決定 4（手で入れる写像は登録時に検証し、通らないものは保存しない。検証は SC-17 側のクライアントで行う）
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md ★未確定表「部門コードの値域」（2026-09-26 確定）
related_specs:
  - 20260926_issue-754_department-from-registrant-group.md
  - 20260903_issue-1194_sc06-owner-mapping-table.md
  - 20260906_issue-1255_east-west-grpc-authz.md
issue: "#1557"
---

# 作業仕様書 — SC-06 で明示した部門を、書き込み時に値域で検証する

## 目的と射程

計画 ADR-0115 決定 5 は「**SC-06 で明示した `department` は、書き込み時に値域（決定 1）で検証する**（ADR-0074 決定 4 と同じ形）」と定めた。
値域は realm の `/department/<code>` の `<code>` の集合である（決定 1）。#1556（IADR-0468）は登録時の導出だけを入れ、この検証を
「新しいサービス間契約が要る」として #1557 へ送った。本作業はその契約を作り、登録（POST）・全置換（PUT）・部分更新（PATCH）の 3 口に検証を張る。

**射程外**: 利用者属性 `department` とグループ所属の一致（ADR-0115 決定 3）は別 PR（別 issue）で扱う。① フォルダ写像は器が未確定のためなお入れない。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 明示した `department` が realm の部門グループのコードなら保存される（POST / PUT / PATCH） | エンドポイント試験（T-60） |
| AC-2 | 値域の外（打ち間違い・`/teams/...` の名前・入れ子のパス・大小文字違い）なら **400**（`errors` に理由）で、**1 項目も保存されない** | エンドポイント試験（T-61） |
| AC-3 | 部門グループを引けなければ **502**（`message` に「確かめられなかった・保存していない」）。「値域の外」と報告しない | エンドポイント試験（T-62） |
| AC-4 | 予約値 `unassigned`・空白・未指定は**値域照会をしない**（照会が落ちていても通る）。登録時は従来どおり登録者の部門で補われ得る | エンドポイント試験（T-63） |
| AC-5 | `defaultAttributes` を送らない PATCH は照会しない（無関係な操作を認可サービスの障害へ道連れにしない） | エンドポイント試験（T-63） |
| AC-6 | 呼び出し先（AuthorizationService）は `/department/<code>` のグループが**序数一致で実在する**ときだけ `exists=true` を返す。s2s（`platform-service`）だけを通し、管理者の利用者トークンは PERMISSION_DENIED | gRPC 試験（T-64） |
| AC-7 | SC-06 の登録・編集フォームの補助文が「入力するなら部門グループのコード。値域外は保存されない」ことを伝える（ja / en カタログ） | Vitest（T-65） |

## 設計（詳細は IADR-0472）

- **読み口**: AuthorizationService の east-west gRPC `platform.authz.v1.UserDirectory` に `CheckDepartmentCodes` を足す（照会形。列挙しない）。
  後段は既存の `IIdentityAdminClient`（`identity-admin`）に足す読み取り `FindGroupByPathAsync`（Keycloak `GET /group-by-path/{path}`）。
  **新しい主体・新しいロール・キャッシュ・構成リストは作らない。**
- **DataSourceService**: ポート `IDepartmentDomainDirectory`（`Available` と「実在するコード」を分ける断面）。gRPC 実装と、
  gRPC 宛先が未宣言の配備の縮退（常に「引けなかった」。IADR-0431 の `UnavailableOwnerRetentionDirectory` と同じ形）。
  REST の兄弟実装は作らない（利用者トークンを転送する形へ戻さない。IADR-0401 決定 2）。
- **検証の位置**: `DepartmentDomainValidation.ValidateAsync`（`OwnerMappingValidation` と同じ集約直下）を 3 端点が呼ぶ。応答の形も同じ
  （400 = `ValidationProblem`、502 = `{ message }`）。
- **`unassigned`**: 予約値の明示は受け付ける（照会しない）。部門コードではなく「未解決の記録」であり（IADR-0199）、`IsUnresolved` と同じ述語で判定する。
- **原則 A（黙って受理・拒否しない）**: 引けないときは 502 で保存しない（ADR-0074 決定 4 の実装 `OwnerMappingValidation` と同じ挙動）。

## 母集合の引き直し（IADR-0141 決定 1）

**軸 1 — 誤りの側（「検証が無い」と書いている箇所）**: `git grep -n "1557\|値域の検証\|値域検証\|候補外\|自由入力のまま"`（`CHANGELOG.md` 除外）。

| 引いた箇所 | 扱い |
| --- | --- |
| `docs/data/data-source.md:116`「値域の検証…もまだ無い」 | **反映** |
| `docs/screens/SC-06_datasource-management.md:193`「値域の制約なし（自由入力）」 | **反映** |
| `src/knowledge/frontend/src/features/sc06-datasources/components/DataSourceForm.tsx:160`（注記） | **反映** |
| `src/knowledge/frontend/src/lib/abac/department.ts:20`（注記） | **反映** |
| `.ai-context/adr/IADR-0468_*`・`.ai-context/specs/20260926_issue-754_*` | **除外**（凍結記録。IADR-0468 は Accepted で本文を書き換えない。後続は IADR-0472 が引く） |
| `IADR-0278` / `doc_scope` / `FR-11` / `LlmRouterTests` / `NotificationDeliveryMetrics` 等の「値域検証」「候補外」 | **除外**（別属性・別機能の語。部門と無関係） |

**軸 2 — 部門グループ・明示部門を語る箇所**: `git grep -n "部門グループ\|department.*検証\|明示.*部門\|IADR-0468"`。

| 引いた箇所 | 扱い |
| --- | --- |
| `docs/api/openapi.yaml` の `CreateDataSourceRequest` / `UpdateDataSourceRequest` / `PatchDataSourceRequest` の `defaultAttributes`、登録・更新操作の説明 | **反映**（手書きの契約文書。orval の生成物を再生成する） |
| `docs/tests/FR-01_data-source-catalog.md`（T-53〜T-59 の続き） | **反映**（T-60〜T-65 を足す） |
| `docs/api/east-west-grpc.md` の `UserDirectory` 節（rpc 一覧） | **反映**（軸 3 で引いた） |
| `src/platform/frontend/src/locales/{ja,en}/messages.po`・`messages.ts` | **反映**（`pnpm run i18n` で再生成） |
| `DataSource.cs` / `RegistrantDepartment.cs` / `Create/Endpoint.cs` の IADR-0468 注記 | **除外**（導出の記述であり、検証の有無を語っていない。誤りにならない） |
| `deploy/keycloak/microservices-platform-realm.json`・`docs/security/security.md` | **除外**（クレームの説明。検証の有無を語っていない） |
| `scripts/chunk-budget-baseline.json:300` | **除外**（過去の計測記録。フロント成果物が増えたら CI の指示に従い床を更新する） |

**軸 3 — 契約の面（パスから引く）**: `git grep -ln "CheckUsernames"` → proto・`GrpcService.cs`・`UserDirectoryGrpcClient.cs`・`docs/api/east-west-grpc.md`・
DataSourceService の gRPC 実装と試験。rpc を足す側はこの集合で全部である（McpServer・DocumentService は `GetUserAttributes` だけを読む）。
`git grep -ln "IIdentityAdminClient"` → 実装 2 本（Keycloak / InMemory）＋試験の装飾 `TestIdentityDirectory.StubIdentityAdminClient` ＋契約試験
`IdentityAdminContractTests`（メソッド名の陽性対照）。**4 つとも更新が要る**（装飾は素通し、契約試験は名前を足す）。

**軸 4 — 自分の変更で新たに誤りになる記述（規則 10）**: 「PATCH は写像表を送らなければ名簿を引かない」（`Patch/Endpoint.cs`）は、
部門の照会を足しても写像表の照会についてはなお正しい。`StubPlatformUserDirectory` の注記「本番の実装は /authz/users を叩く」は既に古いが本作業の射程外（触らない）。

## タスク

1. 契約: proto に `CheckDepartmentCodes`、`UserDirectoryGrpcClient.CheckDepartmentCodesAsync`
2. AuthorizationService: `IIdentityAdminClient.FindGroupByPathAsync`（Keycloak / InMemory / 試験装飾 / 契約試験）、`UserDirectoryGrpcService.CheckDepartmentCodes`
3. DataSourceService: ポート・gRPC 実装・縮退実装・`DepartmentDomainValidation`・3 端点・Program.cs・試験スタブ
4. フロント: 補助文 2 箇所・カタログ再生成・試験
5. 文書: openapi.yaml（＋ orval 再生成）・east-west-grpc.md・data-source.md・SC-06 画面仕様・FR-01 テスト仕様・IADR-0472・README 索引
6. 検証: `dotnet build/test`（両ユニット）・`dotnet format --verify-no-changes`・`pnpm run lint/typecheck/test`・検査器

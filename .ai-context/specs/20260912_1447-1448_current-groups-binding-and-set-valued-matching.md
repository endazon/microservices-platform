---
title: "`${current_groups}` の束縛と共有先ベースの分岐の認可配線、文書側集合値の交差突合、利用者・グループ検索の人限定（#1447 / #1448 / ADR-0100 フォローアップ 2）"
type: spec
status: in-progress
related_ids: [FR-05, FR-19, UC-11, SC-19, SC-20, ADR-0036, ADR-0080, ADR-0088, ADR-0098, ADR-0099, ADR-0100, IADR-0131, IADR-0139, IADR-0253, IADR-0301, IADR-0385, IADR-0396, IADR-0401, IADR-0445, IADR-0446, IADR-0447, IADR-0448, IADR-0449]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0098_share-target-is-keycloak-group-and-ui-waits-for-binding.md
  - planning:projects/microservices-platform/07_adr/ADR-0100_user-lookup-reach-is-all-users.md
  - planning:projects/microservices-platform/10_feedback/20260912_audit-log-storage-and-user-lookup.md
---

# 仕様書: `${current_groups}` の束縛と共有先ベースの分岐の認可配線 —— ADR-0098 フォローアップ 1・2 と ADR-0100 フォローアップ 2

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-19`（個人資料の共有）/ `FR-05`（ABAC 認可）
- 非機能要件（NFR）: なし（製品機能の実装。個別番号を当てない）
- ユースケース（UC）: `UC-11`
- 画面（SC）: `SC-19`（主要素 3 公開範囲の指定先＝グループ指定の解禁）/ `SC-20`（主要素 6 同期履歴の空状態の読み分け）
- 関連 ADR: `ADR-0036`（D-03 束縛変数は `${current_user}` と `${current_groups}` の 2 つ／D-06 共有の単位は個人とグループ／
  D-08 管理者は個人資料を閲覧できない）/ `ADR-0080`（決定 2 集合値は交差で判定）/ `ADR-0088`（決定 1 属性は IdP から引き直す。
  本文の主張を評価に用いない）/ `ADR-0098`（決定 1 共有先は Keycloak グループ ID・個人は利用者識別子／決定 2 グループ UI は
  配線まで描かない〔本作業で解除〕／決定 3 グループ木は管理者が Keycloak で作る／フォローアップ 1〜3）/ `ADR-0099`（§残るもの
  「配備前の同期は履歴に無い」の帰結）/ `ADR-0100`（決定 1・2 利用者検索の到達範囲と面／フォローアップ 2 サービスアカウントの経路）/
  `IADR-0253`（決定 3 束縛変数は 1 つ〔本作業で部分 supersede〕・決定 4 共有台帳）/ `IADR-0385`（集合値の線上表現）/
  `IADR-0396`（個人資料の露出と索引。`shared_with` をリスト項目で運ぶ）/ `IADR-0401`（名簿の列挙を s2s の面へ出さない）/
  `IADR-0445`（利用者検索の面）/ `IADR-0446`（同期監査ログの貯蔵）
- 計画書リンク: `07_abac-attribute-model.md` §動的束縛（判定規則 `doc.shared_with ∩ ({${current_user}} ∪ ${current_groups}) ≠ ∅`）、
  `05_screens/01_screens.md` §SC-19「供給元と契約の現状」、`10_feedback/20260912_audit-log-storage-and-user-lookup.md` §残るもの

## 目的・背景

ADR-0098（planning#618）が実測して確定した穴 2 つ（#1447 / #1448）と、ADR-0100（planning#621）が実装側へ戻した 1 つ、
ADR-0099 の環流応答が残した帰結 1 つを、**同じ資源（個人資料の共有と認可スコープ）に対する同型の変更として 1 PR に束ねる**
（IADR-0139 決定 1）。

| # | 現状（2026-09-12・`origin/develop` `da485a1`） | 本作業 |
| --- | --- | --- |
| #1447 | `AbacEvaluator` の束縛変数は `${current_user}` の 1 つ。`${current_groups}` は実装全体で 0 件。`DocumentShareEndpoints` は「共有先ベースの分岐を認可スコープへ載せる配線は別段」と注記。**グループ共有は台帳に入るが誰にも何も許可しない** | `${current_groups}` を IdP の所属照会で束縛し、共有先ベースの分岐（選言の第 3 節）を BFF・検索・グラフの 4 面で効かせる。SC-19 にグループ指定を描く（決定 2 の暫定手段の解除） |
| #1448 | `BffScopeResolver.MatchesAll` / `AbacNodeFilter` / `AbacPageFilter` が属性値を**単一文字列**で突合。集合値の `shared_with` は 1 件も一致しない。検索側（Qdrant / InMemory）だけが交差 | 述語を契約側の 1 か所（`AttributeFilterMatch`）へ寄せ、集合値キー（`shared_with` / `tags`）は交差、単一値は従前どおり |
| ADR-0100 FU2 | `/authz/users/lookup`・`/resolve` は `RequireAuthorization()` のみ。realm のサービスアカウント（`platform-service`）も認証済みなので到達できる | 「人の主体だけ」を表す認可ポリシーを新設し、利用者・グループの名簿の読み口に課す |
| ADR-0099 帰結 | SC-20 の同期履歴は 0 件を「同期の記録はまだありません」とだけ出す。配備前に同期していた利用者には「記録されていない」のか「同期していない」のかが読み分けられない | 端末の最終同期があるのに履歴が空なら「配備前の同期は記録に無い」と読める文言へ切り替える |

## 実装の現状（着手前の実測）

- `AbacEvaluator.cs:8-10`: `CurrentUserPlaceholder = "${current_user}"` のみ。`BindPlaceholders` は `DocumentConditions` にだけ効き、`AllowedFilters`（キー単位 union）では束縛しない（リテラルのまま deny 側）。
- `ScopeUserAttributeSource.ResolveAsync`: `IIdentityAdminClient.FindByUsernameAsync` で属性を引き直す唯一の点（ADR-0088 決定 1）。REST `/authz/scope` と gRPC `AuthzScope/Resolve` の両面が通る。**グループを引く口は `IIdentityAdminClient` に無い**（9 口のいずれも group を触らない）。
- Keycloak realm（`deploy/keycloak/microservices-platform-realm.json`）: `groups` クレーム（`oidc-group-membership-mapper`・`full.path=false`）は人のトークンに載るが、**読む C# コードは 0 件**。既存グループは `/clearance/*`・`/department/*` の属性運搬用だけ（ADR-0098 決定 3 の領域）。
- `DocumentUpdated.SharedWith`（`DocumentEndpoints.PublishUpdatedAsync`）が共有台帳の `SubjectId` を**種別を落として**索引へ載せる。Qdrant はリスト項目 `shared_with`・`Match.Keywords`（交差）。`DocumentDto` は共有先を運ばない → BFF の単体判定（`DocumentBffEndpoints.FetchAuthorizedAsync` → `BffScopeResolver.Matches(doc.Attributes, scope)`）は共有先に到達できない。GraphService の `GraphDocument.Attributes` も同様。
- `deploy/local/abac-seed/policies.json`: `owner` を条件に持つのは write の 1 本だけ。`shared_with` を条件に持つポリシーは無い。
- 人とサービスアカウントの区別: `MachinePrincipal.IsMachine`（`Platform.Shared.Infrastructure/Foundation/Observability/`。腕 A `service-account-` 接頭辞／腕 B 利用者名無し＋`azp`/`client_id`）と `PlatformAuthPolicies`（`AdminOnly` / `ConfigViewer` / `ServiceCaller` の 3 つ。**「人だけ」のポリシーは無い**）。
- キャッシュ: `BffScopeResolver.ResolveAsync`・`/authz/scope` のいずれにも `IMemoryCache` 等のキャッシュは無い（grep 実測 0 件）。したがって鮮度＝IdP の所属照会の鮮度（判定ごとに引く）。

## 設計（決定。詳細は IADR-0447 / IADR-0448 / IADR-0449）

### A. `${current_groups}` の束縛（IADR-0447。IADR-0253 決定 3 を部分 supersede）

1. **供給元は IdP の所属照会**（Keycloak Admin API `GET /admin/realms/{realm}/users/{id}/groups`）。**トークンの `groups` クレームは使わない** —— `/authz/scope` は本文もトークンも信じず IdP から引き直す（ADR-0088 決定 1）ため、同じ点（`ScopeUserAttributeSource`）で所属も引く。`IIdentityAdminClient.GetUserGroupsAsync(IdentityUser)`（Keycloak / InMemory の 2 実装）を新設し、`ScopeUserAttributeSource.Result` に `Groups`（グループ ID の集合）を足す。
2. **値は Keycloak のグループ ID**（ADR-0098 決定 1「識別子」。改名・移動で共有が外れない）。共有台帳のグループ `subjectId` と同じ名前空間。
3. `AbacEvaluator.ResolveScope` に `IReadOnlySet<string> groups` を渡し、`DocumentConditions` の値 `${current_groups}` を**集合へ展開**する（`${current_user}` は 1 値、`${current_groups}` は 0..N 値）。展開後に `AllowedValues` が空になるフィルタを持つ分岐は**分岐ごと落とす**（空の許可集合は「何にも一致しない」であり、消費側がどう読んでも許可へ倒れないようにする）。`AllowedFilters` では束縛しない（従前どおり）。
4. 契約（`AccessScopeRequest`・proto）は変えない —— 束縛は評価器の内側で完結する。
5. 共有先ベースの分岐は**ポリシー**として表す（IADR-0253 決定 1「1 ポリシー＝1 分岐」の既存の形）: `documentConditions: { "shared_with": ["${current_user}", "${current_groups}"] }`（`action: read`）。dev seed（`deploy/local/abac-seed/policies.json`）へ 1 本足す。**配備環境ではポリシー投入が統制の実現手段である**（3 点セット: 統制＝ADR-0036 D-06／実現手段＝本ポリシーの投入／投入までの暫定＝共有は誰にも効かない＝従前と同じ fail-closed）。
6. 消費側の到達（DB per Service の越境の解決）: **共有先は DocumentService が自分の応答とイベントに載せ、消費側は台帳へ到達しない。** `DocumentDto.SharedWith`（`DocumentUpdated.SharedWith` と同じ解決点・同じ値）を足し、BFF は `DocumentAttributeEncoding.WithSharedWith(doc.Attributes, doc.SharedWith)` の像で `Matches` する。GraphService は `DocumentUpdated.SharedWith` を `GraphDocument.Attributes["shared_with"]`（カンマ連結）へ写す。RetrievalService は既に到達済み（変更なし）。WikiService には個人資料が流れないため述語の統一のみ。
7. 鮮度（ADR-0098 フォローアップ 3）: キャッシュが無いため**判定ごとに IdP へ所属を引く**。所属変更の反映遅延は Keycloak Admin API の読み取り一貫性のみ（実質即時）。往復が 1 判定あたり 1 つ増える（`FindByUsernameAsync` ＋ 所属照会）。実測値を IADR と planning への環流に書く。

### B. 集合値の交差突合（IADR-0448。#1448）

1. `Platform.Shared.Contracts.Dtos.DocumentAttributeEncoding`（集合値キー `shared_with` / `tags`・`WithSharedWith`）と `AttributeFilterMatch.MatchesAll / MatchesOne`（集合値キーは交差、単一値は値一致）を契約に置く（**書き込み済み**）。
2. `BffScopeResolver.MatchesAll`・`AbacNodeFilter.MatchesAll`・`AbacPageFilter.MatchesAll` の 3 か所を `AttributeFilterMatch.MatchesAll` へ寄せる。`Knowledge.Contracts.AttributeValueKeys.ListValuedKeys` は `DocumentAttributeEncoding.SetValuedKeys` を参照する（語彙を 2 つ持たない）。
3. 陽性対照: 単一値の一致・不一致は不変（既存テストが緑のまま）。集合値は `"a,b"` が `{b}` に一致・`{c}` に不一致・空文字は不一致。3 面（BFF / Graph / Wiki）で同じ入力に同じ答え（`AbacNodeFilterTests` の 3 面一致テストへ集合値の行を足す）。

### C. 名簿の読み口は人の主体だけ（IADR-0449。ADR-0100 フォローアップ 2）

1. `PlatformAuthPolicies.InteractiveUser`（認証済み **かつ** `MachinePrincipal.IsMachine` が偽）を `AuthExtensions` に登録する。
2. `/authz/users/lookup`・`/authz/users/resolve`（既存）と `/authz/groups/lookup`・`/authz/groups/resolve`（新設）の群に課す。BFF の `/bff/users/*`・`/bff/groups/*` にも同じポリシーを課す（実施点は後段、BFF は多層防御）。
3. サービスアカウント（`service-account-*`・`platform-service`）は 403。`AdminOnly` を持つサービスアカウント（`abac-seeder`）も 403（ロールではなく主体の種別で分ける）。人は従前どおり。

### D. グループ検索の読み口（IADR-0447 の一部）

- AuthorizationService `GET /authz/groups/lookup?q=&limit=`（2 文字以上・上限 50・既定 20。グループ木を平坦化し `name` の部分一致・パス順）と `POST /authz/groups/resolve`（1〜100 件の ID → `GroupSummaryDto`。無い ID は落ちる）。`IIdentityAdminClient.SearchGroupsAsync(query, max)` / `GetGroupsByIdAsync(ids)` を新設（Keycloak: `GET /groups?search=&briefRepresentation=true` を再帰で平坦化・`GET /groups/{id}`。InMemory: 固定のグループ木を持つ）。
- 面は `GroupSummaryDto(Id, DisplayName, Path)` の 3 項目に閉じる（所属者・属性を出さない）。
- BFF `/bff/groups/lookup`・`/bff/groups/resolve`（透過中継。`UserLookupBffEndpoints` と同型）。契約は `docs/api/openapi.yaml`（**書き込み済み**）。

### E. 画面

- **SC-19**: `ShareTargetsDialog` に指定先の種別（個人／グループ）の切替を足す。グループは `/bff/groups/lookup` で検索し、既存のグループ共有は `/bff/groups/resolve` で表示名（＋パス）へ引く。**識別子は出さない**（ADR-0098 決定 1）。台帳に無いグループ（削除済み）は「見つからないグループ」として取り消しだけできる。「グループ共有がある資料は本画面で変更できません」の告知は撤去する（暫定手段の解除）。
- **SC-20**: `SyncHistoryPanel` に `hasPriorSync`（接続端末のいずれかに `lastSyncAt` がある）を渡し、履歴が 0 件のとき ①`hasPriorSync=true` → 「同期履歴の記録は本機能の配備後の同期から残ります。配備前の同期は表示されません。」 ②`false` → 従前の「Obsidian プラグインから同期すると、ここに結果が並びます。」を出す。

## 影響範囲（専有領域）

| 担当 | 領域 |
| --- | --- |
| 親（契約） | `docs/api/openapi.yaml`、`Platform.Shared.Contracts/Dtos/{GroupLookupDto,DocumentAttributeEncoding}.cs`、`Knowledge.Contracts/Dtos/DocumentDto.cs`、orval 生成物、`scripts/contract-schema-baseline.json`、本仕様書、IADR-0447〜0449、`docs/authz/FR-19_share-target-authorization.md` |
| backend-platform | `AuthorizationService`（`AbacEvaluator`・`ScopeUserAttributeSource`・`IIdentityAdminClient` と 2 実装・`Features/Groups/Lookup/*`・`Features/Users/Lookup/*` のポリシー・テスト）、`Platform.Shared.Infrastructure`（`AuthExtensions`・`PlatformAuthPolicies`・`BffScopeResolver`・テスト）、`Platform.Bff`（`GroupLookupBffEndpoints`・`UserLookupBffEndpoints`・合成点・テスト）、`deploy/keycloak/microservices-platform-realm.json`（例示のグループ木は足さない。決定 3 は管理者の作業） |
| backend-knowledge | `DocumentService`（`DocumentDto.SharedWith` の解決・`DocumentShareEndpoints` の注記更新・テスト）、`Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs`（像の合成）、`GraphService`（`AbacNodeFilter`・`DocumentUpdated` 消費で `shared_with` を写す・テスト）、`WikiService/Domain/AbacPageFilter.cs`、`Knowledge.Contracts/Dtos/AttributeValueDto.cs`（語彙の参照）、`deploy/local/abac-seed/policies.json`、`docs/api/BFF_bff-surface.md` |
| frontend | `sc19-private-notes/**`（`ShareTargetsDialog`・`api/useGroupLookup.ts`・テスト）、`sc20-obsidian-settings/**`（`SyncHistoryPanel`・`ObsidianSettingsPage`・テスト）、`platform/frontend/e2e/sc19-*`・`sc20-*`、locales、`docs/screens/SC-19_*`・`SC-20_*`、`docs/tests/SC-19_*`・`SC-20_*`、`scripts/chunk-budget-baseline.json` |

## 受け入れ基準（テストへの写像）

1. `AbacEvaluator`: `${current_groups}` が所属の集合へ展開される／所属が無いとき `shared_with` の分岐は `${current_user}` だけになる／`${current_groups}` しか無い条件で所属が空なら分岐が落ちる（陰性対照: `${current_department}` はそのまま残る）。
2. `/authz/scope`（REST・gRPC）: 所属照会が失敗したら 503 / `UNAVAILABLE`（属性と同じ扱い。「引けなかった」を deny に畳まない）。
3. `AttributeFilterMatch`: 単一値の一致・不一致は不変／集合値は交差／空集合は不一致。BFF・Graph・Wiki の 3 面で同じ答え。
4. BFF `GET /bff/documents/{id}`: 共有先に自分の利用者名またはグループ ID を含む個人資料が、`shared_with` 分岐を持つスコープで読める。含まなければ 404（存在秘匿。従前どおり）。
5. `DocumentDto.SharedWith` は `DocumentUpdated.SharedWith` と同じ値（同じ解決点）。共有が無ければ null。
6. `/authz/users/*`・`/authz/groups/*`・`/bff/users/*`・`/bff/groups/*`: 人は 200、サービスアカウントは 403（`platform-admin` を持つサービスアカウントも 403）、未認証は 401。
7. `/authz/groups/lookup`: 2 文字未満 400・上限 50・面は 3 項目。`/resolve`: 0 件 400・101 件 400・無い ID は落ちる。
8. SC-19: グループ指定の検索・追加・取り消しができ、識別子は画面に出ない（陰性対照）。既存のグループ共有が表示名で見える。
9. SC-20: 端末に最終同期があり履歴が空のときだけ「配備前の同期は表示されません」が出る（陰性対照: 最終同期が無ければ従前の文言）。
10. 既存ゲート全緑（backend 両 slnx build / test / format、frontend typecheck / lint / format / knip / i18n / build / chunk / coverage / E2E、文書検査）。

## 計画書との差異・未決事項

- **共有先ベースの分岐をポリシー（配備データ）で表す**ため、配備環境ではポリシーを投入するまで統制は働かない（従前と同じ fail-closed）。投入手順は `docs/authz/FR-19_share-target-authorization.md` に置く。**構造的（評価器が常に分岐を出す）にしなかった理由**は IADR-0447 に書く（IADR-0253 決定 1 の「1 ポリシー＝1 分岐」を崩さない。owner 分岐も同じ形である）。
- 鮮度の実測（判定ごとに IdP へ引く。キャッシュ無し）は ADR-0098 フォローアップ 3 として planning へ環流する。
- Keycloak の realm export に共有用のグループ木は足さない（ADR-0098 決定 3。管理者の作業）。開発の E2E は InMemory のグループ木で通す。

## 検証記録

実測 2026-09-12（ローカル .NET 10.0.400 / Node 22。3 エージェント〔platform backend / knowledge backend / frontend〕の成果を親が統合して再実行）。

| 面 | 結果 |
| --- | --- |
| 契約 | `check-openapi-dto-drift` OK / `check-contract-schema` OK（非破壊 5 件＋`AttributeValueKeys` の定数委譲による `constValueChanged` 2 件を allowlist で承認→消費。値は不変）/ `check-bff-authz-docs` 105 端点 OK（前 103）/ `check-bff-downstreams` OK / orval 再生成差分なし |
| 後段 | `dotnet build` platform 0 warning / knowledge 2 warning（既存の Testcontainers CS0618）・0 error / `dotnet test --filter "Category!=Integration"` 19 アセンブリ全緑: AuthorizationService 293 → **348**、Platform.Shared.Infrastructure 343 → **380**、Platform.Bff 590 → **633**（skip 1 は既存）、DocumentService 546 → **551**、GraphService 609 → **631**、WikiService 102 → **111**、他は不変 / `dotnet format --verify-no-changes` 両 slnx 差分なし |
| 画面 | typecheck 6 プロジェクト Done / lint 0 error（warning 12 は既存）/ format OK / knip 床どおり 36 / i18n 差分なし・未訳 0（msgid 937 → 945）/ codegen 差分なし / build OK / chunk 初期ロード 594,006 → **594,636 B**（+630 B。A/B: カタログ純増 8 文言 × 2 ロケール＝+630、コード側 0）/ static-egress OK / route-manifest 17 画面 OK / `test:coverage` 1,708 → **1,715 件** 98.30 / 93.27 / 95.02 / 98.30 / **E2E 70 passed**（前 67。sc19 6 → 7、sc20 7 → 9） |
| 文書・scripts | trace-blocks / doc-links / cross-repo-refs / plan-id / adr-numbering / doc-type-vocabulary / doc-status-vocabulary / reading-budget / test-spec-coverage / test-name-references / test-traceability / unit-dependencies / nul-bytes / xunit1051 / knowledge-graph OK / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 771 件合格 |

受け入れ基準の写像:

1. `AbacEvaluatorTests`・`CurrentGroupsBindingTests`（展開・所属なし・分岐落ち・`${current_department}` 不変）
2. `Endpoint`/`GrpcResolveScopeTests`（所属照会失敗 → 503 / `UNAVAILABLE`）。**鮮度の実測**: キャッシュ無し（grep で ABAC 経路に 0 件）、1 判定あたりの IdP 往復 1 → **2**（admin トークン取得は 1 回のまま）。`KeycloakIdentityAdminClientTests.One_abac_decision_costs_two_admin_gets_and_one_token_fetch` / `CurrentGroupsBindingTests.One_decision_costs_exactly_two_identity_provider_round_trips` で固定
3. `AttributeFilterMatch` の単体・`BffScopeResolverTests`・`AbacNodeFilterTests`（3 面一致に集合値の行）・`AbacPageFilterTests`
4. `BffSharedDocumentReadTests`（`shared_with` 分岐で共有先の個人資料が `GET /bff/documents/{id}` で読める／`bob` は 404／`null` は 404。SC-05 一覧の一律除外は不変）
5. `DocumentSharedWithResponseTests`（`DocumentUpdated.SharedWith` と同じ値・null・一覧の分配）
6. `UserLookup`/`GroupLookup` の陰性対照（サービスアカウント 403・`platform-admin` を持つサービスアカウント 403・未認証 401）と BFF 側の同型
7. `LookupGroups`/`ResolveGroups` の値域（2 文字未満 400・上限 50・0 件 / 101 件 400・無い ID は落ちる）
8. `ShareTargetsDialog.test.tsx` 12 → 16（種別切替・グループ検索・追加・取り消し・識別子非表示の陰性対照を 2 名前空間で）・E2E sc19
9. `SyncHistoryPanel.test.tsx` 7 → 10（`hasPriorSync` 両方向）・E2E sc20
10. 上表

計画（親の指示）との差異: SC-19 の `initialFocus` は本リポの既存実装どおり検索入力のまま（取消側ではない。a11y E2E が固定している）。

## 監査記録

2026-09-12・別エージェント（sonnet）が diff（`origin/develop...939de24`・101 ファイル）と §受け入れ基準 だけを渡されて監査。**条件付き合格**（🔴 なし・🟡 2 件）。証跡: 観点別の `dotnet test --filter`（Shared.Infrastructure 39 / AuthorizationService 27+20+13 / Platform.Bff 44+16+14 / Graph 34 / Wiki 25 / DocumentService 5。すべて Failed 0）、`vitest run`（sc19 / sc20 169 件）、`check-trace-blocks` / `check-cross-repo-refs` / `check-commit-messages`（3 件適合）/ `check-openapi-dto-drift` / `check-contract-schema` / `check-bff-authz-docs`（105 端点）/ `check-bff-downstreams`。shallow clone のため `git log` は出典に用いていない。

| 指摘 | 対応 |
| --- | --- |
| 🟡 IADR-0447 決定 4 の副作用（`owner` 分岐で所有者自身の個人資料が `GET /bff/documents/{id}` で読める）に専用の陽性テストが無い | `BffSharedDocumentReadTests.所有者分岐が一致する自分の個人資料は読める`（`owner` 一致 → 200・別人 → 404 の Theory）を追加 |
| 🟡 「共有された相手が `sharedWith` から他の共有先の識別子を読める」の planning への環流の着手状況が本文に無い | 環流は本 PR のマージ後に planning へ別 PR で行う（ADR-0098 決定 2 の暫定手段解除・鮮度と同じ PR。§計画書との差異・未決事項 と IADR-0447 §フォローアップ 1・2 に記載済み）。起票済みの issue 番号は無い —— 記録として計画 ADR-0098 §結果 へ追記する形を採る |

併せて CodeQL（`cs/user-controlled-bypass`・high）が `KeycloakIdentityAdminClient.SearchGroupsAsync` の冒頭の早期 return を指摘 → 新設 3 メソッドとも認可済みクライアントの取得を先に、入力のガードを後にした（`eafb816`）。

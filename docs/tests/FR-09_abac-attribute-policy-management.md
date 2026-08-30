---
title: 文書属性・タグ／ABAC ポリシー管理 テスト仕様書
type: test-spec
status: draft
created: 2026-07-02
updated: 2026-08-30
author: claude
---
<!-- trace:
ids: [FR-09, SC-05, SC-09, UC-05]
adrs: [ADR-0043]
iadrs: [IADR-0006, IADR-0152, IADR-0153]
specs: []
issues: [#634, #635]
-->

# テスト仕様書: 文書属性・タグ／ABAC ポリシー管理

## 起点となる計画書（トレーサビリティ）

- 機能要求: 文書属性・タグおよび ABAC ポリシーの管理
- ユースケース: ABAC 権限を管理する
- 関連仕様: `../../.ai-context/specs/20260702_FR-09_abac-attribute-policy-management.md`、`../functional/FR-09_abac-attribute-policy-management.md`

## テスト対象

- `AbacValidation`（属性辞書・ポリシー・文書属性の検証ロジック）
- `AuthzEndpoints`（属性辞書・ポリシー管理 API、文書属性検証 API）
- `KeycloakRolesClaimsTransformation`（Keycloak `realm_access.roles` → `ClaimTypes.Role` 展開）

## 単体テスト（`AbacValidationTests`）

| # | ケース | 期待 |
| --- | --- | --- |
| 1 | 正常な属性辞書 | エラー無し |
| 2 | key 未指定 | エラー（key 必須） |
| 3 | 許可値が空 | エラー（allowedValues） |
| 4 | 許可値の重複（大小無視） | エラー（重複） |
| 5 | 不正なスコープ | エラー（scope） |
| 6 | 同一スコープでキー重複 | エラー（既に定義済み） |
| 7 | 別スコープの同名キー | エラー無し |
| 8 | 更新時の自己除外 | エラー無し |
| 9 | 定義済みキーに整合するポリシー | エラー無し |
| 10 | 不正なアクション | エラー（action） |
| 11 | 辞書外の文書条件値 | エラー（辞書外） |
| 12 | 未定義キーの条件 | 許容（エラー無し） |
| 13 | 条件の値集合が空 | エラー（空にできません） |
| 14 | 必須属性を満たす文書属性 | エラー無し |
| 15 | 必須属性の欠落 | エラー（必須属性） |
| 16 | 許可値外の属性値 | エラー（許可値に含まれません） |
| 17 | 未定義キー（自由タグ） | 許容（エラー無し） |
| 18 | 条件 null でポリシー生成 | 空辞書として保存（null を保持しない） |
| 19 | ポリシーの属性参照判定（scope 一致のみ） | 一致 true / 別スコープ・未使用 false |

### ロールクレーム展開（`KeycloakRolesClaimsTransformationTests`）

| # | ケース | 期待 |
| --- | --- | --- |
| R1 | `realm_access.roles` にロード | `IsInRole("platform-admin")`/`user` が true、未定義は false |
| R2 | `realm_access` 無し | ロール付与なし |
| R3 | `realm_access` が不正 JSON | ロール付与なし（fail-closed） |
| R4 | 二重実行（冪等性） | `platform-admin` クレームは重複しない |

## 結合テスト（`AuthzManagementEndpointTests`, InMemory）

| # | ケース | 期待 |
| --- | --- | --- |
| 1 | 属性登録→個別取得 | 201 → 200、許可値往復 |
| 2 | 許可値空で登録 | 400 |
| 3 | 同一スコープでキー重複登録 | 2 件目 400 |
| 4 | 属性更新（許可値差替） | 200、許可値・必須が反映 |
| 5 | 属性削除→取得 | 204 → 404 |
| 6 | ポリシー ライフサイクル（登録→更新→無効化→削除） | 201→200→200(IsActive=false)→204 |
| 7 | 不正アクションのポリシー | 400 |
| 8 | 辞書外の文書条件値ポリシー | 400 |
| 9 | 文書属性検証（整合） | 200 `{valid:true}` |
| 10 | 文書属性検証（辞書外の値） | 200 `{valid:false, errors:[...]}` |
| 11 | 存在しないポリシー ID の取得／更新／無効化／削除 | いずれも 404 |
| 12 | 条件を省略したポリシー登録→ `/scope` 呼び出し | 201 → 200（500 で落ちない） |
| 13 | 管理者ロール無しで管理系呼び出し | 403 |
| 14 | ポリシー参照中の属性辞書削除 | 409 |

## 受け入れ基準の写像

- 管理者が属性・タグ・ポリシーを設定できる → 結合 #1・#4・#5・#6。管理者のみ許可 → 結合 #13＋単体 R1〜R4
  （実 Keycloak トークンでロールが `RequireRole` に届くことの担保）。
- 矛盾するポリシーは保存前に検証しエラー → 単体 #10〜#13、結合 #7・#8。
- 辞書整合の文書属性検証 → 単体 #14〜#17、結合 #9・#10。
- 認可解決の堅牢性（条件 null で `/scope` が落ちない）→ 単体 #18、結合 #12。
- 参照整合（参照中の属性辞書は削除不可）→ 単体 #19、結合 #14。

## ポリシーの dry-run 検証（#535 / 裁定 Q23）

**実装は `AuthorizationService.Tests/PolicyDryRunValidationTests.cs`（5 件）と
`Platform.Bff.Tests/BffAuthzEndpointTests.cs`（2 件）。**

| # | 確かめること | 実装 |
| --- | --- | --- |
| T-50 | 妥当なポリシーは `valid: true`（**200**。矛盾の有無は要求の成否と別） | `Validate_ValidPolicy_ReturnsValid` |
| T-51 | 矛盾は `valid: false` ＋ 理由（**200 のまま**） | `Validate_InvalidPolicy_ReturnsErrorsWithOk` |
| T-52 | ★ **何も保存されない**（件数が変わらず、名前も現れない） | `Validate_DoesNotPersistAnything` |
| T-53 | ★★ **dry-run と保存が同じ結果を出す**（矛盾する入力で、`errors` が一致） | `Validate_AgreesWithSave_OnTheSameInput` |
| T-54 | 妥当な入力でも一致する（dry-run が通れば保存も通る） | `Validate_AgreesWithSave_WhenInputIsValid` |
| T-55 | BFF が中継する（200） | `ValidatePolicy_AsAdmin_Returns200` |
| T-56 | **運用者は 403**（検証も管理操作である） | `ValidatePolicy_AsNonAdmin_IsForbidden` |

**T-53 が本 issue の中心である。** 計画は「検証は通ったのに保存で矛盾が出る」形を名指しで禁じた。
実装は 3 経路が同じ関数を呼ぶことで守っているが、**将来それが割れたらここが落ちる**。
空同士の一致で通してしまわないよう、**`errors` が空でないこと**も併せて固定している。

## タグ辞書

| # | 確かめること | 実装 |
| --- | --- | --- |
| T-20 | 辞書の値集合を**管理者・運用者**が引ける | `List_AdminOrOperator_IsAllowed`（`[Theory]`） |
| T-21 | **一般利用者は辞書を引けない**（スコープ付き属性値ルックアップの決定による） | `List_GeneralUser_IsForbidden` |
| T-22 | 追加は**システム管理者のみ**（運用者は読めるが書けない） | `Create_NonAdmin_IsForbidden`（`[Theory]`） |
| T-23 | 追加直後の使用件数は 0 である | `Create_Admin_AddsTagWithZeroUsage` |
| T-24 | 名前の重複は 409。**前後の空白だけの違いは同一とみなす** | `Create_DuplicateName_Returns409_IgnoringSurroundingWhitespace` |
| T-25 | 空・空白のみの名前は拒否する | `Create_BlankName_IsRejected`（`[Theory]`） |
| T-26 | 使用件数は**現行版の文書の件数**である | `UsageCount_CountsDocumentsOnCurrentVersion` |
| T-27 | **版履歴だけが参照するタグは 0 件である**（数えると付け替えても削除できなくなる） | `UsageCount_DoesNotCountVersionHistory` |
| T-28 | **アーカイブ済みの文書も数える**（付け替えられるため） | `UsageCount_CountsArchivedDocuments` |
| T-29 | 同じ文書に同じタグが 2 度あっても 1 件と数える | `UsageCount_CountsEachDocumentOnce` |
| T-30 | 誰も使っていないタグも辞書には出る（管理面と利用者面の集合は一致しない） | `List_IncludesUnusedTags` |
| T-31 | BFF: **管理者・運用者には `dictionary` が付く**。口は 1 系統のまま | `PostAttributeValues_DictionaryReader_GetsDictionary`（`[Theory]`） |
| T-32 | BFF: **一般利用者には `dictionary` が付かず、後段も呼ばない** | `PostAttributeValues_GeneralUser_GetsNoDictionary_AndDownstreamNotCalled` |
| T-33 | BFF: `tags` 以外のキーでは辞書を引かない | `PostAttributeValues_NonTagKey_GetsNoDictionary` |

**T-27 / T-28 はエンドポイント経由では作れない状態を検証するため、DB を直接組み立てている**
（版履歴だけが参照する状態は、API からは作れない）。

## タグの識別子保持・改名・削除

**実装は `DocumentService.Tests/TagIdentityTests.cs`。**

| # | 確かめること | 実装 |
| --- | --- | --- |
| T-34 | **辞書に無い名前は保存できない**（作成。手入力は自動登録しない） | `Create_WithUnknownTag_Returns400` |
| T-35 | 同上（メタデータ更新） | `PatchMetadata_WithUnknownTag_Returns400` |
| T-36 | **契約は表示名のままである**（要求も応答も表示名で行き来する） | `Roundtrip_RequestAndResponse_UseDisplayNames` |
| T-37 | ★ **改名すると既存文書の表示が追随し、版は増えない**。`DocumentUpdated` が新しい名前で再発行される | `Rename_ExistingDocumentsFollow_WithoutVersionBump` |
| T-38 | **改名したタグを使っていない文書は再発行しない**（索引全体を作り直さない） | `Rename_DoesNotRepublishUnrelatedDocuments` |
| T-39 | **過去版も新しい名前で表示される** | `Rename_VersionHistoryAlsoShowsNewName` |
| T-40 | 既存値への改名は 409。**自分自身への改名（no-op）は許す** | `Rename_ToExistingName_Returns409` / `Rename_ToSameName_IsAllowed` |
| T-41 | 改名・削除は**システム管理者限定**（運用者も不可） | `Rename_NonAdmin_IsForbidden` / `Delete_NonAdmin_IsForbidden`（`[Theory]`） |
| T-42 | 使用件数 0 件のタグは削除できる | `Delete_UnusedTag_Succeeds` |
| T-43 | **参照 1 件以上の削除は件数を添えて 409** | `Delete_UsedTag_Returns409_WithUsageCount` |
| T-44 | **削除の判定と一覧の使用件数は同じ母集合**（版履歴だけが参照するタグは削除できる） | `Delete_TagOnlyInVersionHistory_Succeeds` |
| T-45 | 存在しない識別子の改名・削除は 404 | `Rename_UnknownId_Returns404` / `Delete_UnknownId_Returns404` |
| T-46 | 移行後も**取り込み経路は辞書を増やさない**（#637 の不変条件） | `IngestTagFilterTests`（戻り値が識別子になっても未知タグは落として数える） |

### データ移行（実 PostgreSQL のみ）

**実装は `Knowledge.IntegrationTests/DocumentService/TagIdentityMigrationTests.cs`（`[DockerFact]`）。**

**単体テストでは 1 行も走らない** —— `DocumentService.Tests` は EF InMemory を使っており、
**InMemory プロバイダはマイグレーションの SQL を実行しない**
（#634 の一意インデックスが InMemory で強制されないのと同じ型の限界である）。

| # | 確かめること | 実装 |
| --- | --- | --- |
| T-47 | 現行版・**版履歴の双方**の表示名が辞書へ登録され、配列が識別子へ書き換わる。**並び・重複は保つ** | `Migration_RewritesDisplayNamesToIdentifiers` |
| T-48 | 全角空白で囲まれた名前も同じタグへ解決される（`btrim` の既定では落ちない） | 同上 |
| T-49 | 解決できない要素しか無い行も表示名のまま取り残さない（`[]` になる） | 同上 |
| T-50 | 辞書に既に在るものは重複登録されない。未改名のタグは `UpdatedAt` == `CreatedAt` | 同上 |
| T-51 | **下りで表示名へ戻る**（巻き戻せないものを「巻き戻せる」と称して置かない） | `Migration_Down_RestoresDisplayNames` |

## 備考

- InMemory DB は同一テストクラス内で共有されるため、各ケースは一意なキー／名前を用い、
  必須属性の累積で結果が揺れないよう文書属性検証は許可値整合で確認する（必須欠落は単体で網羅）。
- 統合テスト（`AbacScopeTests`, 実 PostgreSQL）は管理系（`/authz/policies`）が `AdminOnly` を要求するため、
  `IntegrationTestAuthHandler` で `platform-admin` として認証して DB 挙動を検証する。実 Keycloak トークンでの
  E2E 認可検証は環境依存のためフォローアップ。
- ビルド・テストの実走は CI（`dotnet test`）で行う。本実装環境では `dotnet` が承認制のため未実走。

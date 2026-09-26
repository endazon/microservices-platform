---
title: 作業仕様書 — データソースの既定部門を、登録した管理者の部門グループ所属から導く（#754・利用者裁定 2026-09-26）
type: spec
status: done
related_ids:
  - FR-05
  - FR-01
  - UC-04
  - SC-06
  - ADR-0074
  - ADR-0088
  - ADR-0109
  - IADR-0019
  - IADR-0199
  - IADR-0359
  - IADR-0447
  - IADR-0465
  - IADR-0468
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md §メタデータ・属性マッピング（データソースの既定属性 3 つ）・§システム投入経路での owner / department / lifecycle（解決順 ① → ② → 予約値 unassigned。★未確定表「部門コードの値域」「フォルダ写像表の置き場所（器）」）
  - planning:projects/microservices-platform/07_adr/ADR-0074_owner-mapping-table-container-in-sc06.md 決定 2（department の ① は値域の確定を待つ）・決定 4（登録時の実在検証）
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1（呼び出し元の主張を判定に用いない）
related_specs:
  - 20260815_issue-767_sc06-department-input.md
  - 20260828_issue-752-754_attribute-supply.md
  - 20260903_issue-1194_sc06-owner-mapping-table.md
issue: "#754"
---

# 作業仕様書 — データソースの既定部門を、登録者の部門グループ所属から導く

## 目的と射程

#754 は「`department` が事実上 100% 予約値 `unassigned` へ倒れる」を起点とし、2026-09-05 の棚卸しで
「部門コードの値域（部門マスタ）が組織側で未確定なので、着手できる差分は 0 行」と判定されていた。
2026-09-26 に利用者（オーナー）が #754 のコメントの選択肢 A を採り、次を裁定した（コーディネータ経由の伝達）。

1. **部門コードの値域 ＝ Keycloak realm の `department` グループ**（開発 realm では `/department/engineering`・
   `/department/sales`・`/department/hr`。`deploy/keycloak/microservices-platform-realm.json` の `groups`）。
2. **文書の部門は、登録した利用者の部門グループ所属から導く。**

**射程**: 計画の解決順 **② データソースの既定属性** の中で、SC-06 の**登録**時に `department` が未指定なら
登録した管理者の部門グループから埋める。**新しい段は作らない**（導いた値は既定属性へ保存され、SC-06 で
見えて編集でき、既存の `GetEffectiveAttributes` の経路にそのまま乗る）。

**射程外**:

- **① フォルダパス → 部門コードの写像表**。値域は定まったが、**器（置き場所）は計画でまだ未確定**である
  （★未確定表「フォルダ写像表の置き場所（器）」は「計画（未確定）」のまま）。器が決まるまで実装しない。
- **明示値の値域検証（400）**。後述「値域の検証を今回入れない理由」のとおり、新しいサービス間契約が要るため
  フォローアップの問いとして残す（コーディネータの指示どおり）。
- 受け入れ基準 4（稼働クラスタで `unassigned` の件数が減ることの実測）。利用者のみが行える。

## 実測 —— どのクレームが部門グループの所属を運ぶか

コーディネータ設計の前提（「登録者の資格情報が DataSourceService に届き、グループ所属のクレームを読める」）を実測した。

| 実測 | 結果 |
| --- | --- |
| 資格情報の経路 | BFF の `SessionTokenPropagationMiddleware` がセッションのアクセストークンを `Authorization: Bearer` へ載せ、`DataSourceBffEndpoints.CreateForwardingClient` がそのまま後段へ転送する。DataSourceService は `AddPlatformAuth`（JwtBearer）で**自ら署名・発行元を検証**する（`AdminOnly` の判定も同じトークンの `realm_access.roles` による） |
| `bff` クライアントの既定スコープ | `profile` / `email` / `roles` / **`abac-attributes`** |
| `abac-attributes` の `groups` マッパー | `oidc-group-membership-mapper`・**`full.path: "false"`** —— 載るのは**グループ名だけ**（例 `["restricted", "engineering"]`） |
| `abac-attributes` の `department` マッパー | `oidc-usermodel-attribute-mapper`（利用者属性。無ければグループ属性を**最初の 1 つだけ**引く）。開発 realm の利用者は**利用者属性 `department` を直接持つ** |
| C# で `groups` クレームを読むコード | 0 件（`git grep 'FindAll("groups"\|"groups")' -- '*.cs'`） |
| `groups` の既定クレーム写像（Microsoft.IdentityModel.JsonWebTokens 8.0.1） | 写像表にあるのは `group`（単数）→ `.../claims/Group` だけで、`groups` は写像されない |

🔴 **結論: 現行のトークンでは「部門グループにちょうど 1 つ属する」を判定できない。**

- `groups` は**名前だけ**なので、`/department/sales` と `/teams/sales` を区別できない（AuthorizationService の
  `LookupGroupsEndpoint` の注記自身が「グループは同名が起こりやすい」と書いている）。名前で突き合わせると
  **誤った部門を作る** —— 計画が「誤った写像は裁量制御が意図しない相手に開く。安全側は解決しない」と退けた形である。
- `department` クレームは利用者属性が優先され、グループ由来でも**複数所属を 1 つへ黙って畳む**。「0 個・2 個以上なら導かない」を判定できない。

**したがって realm の `abac-attributes` スコープへ、グループの**フルパス**を運ぶマッパーを 1 本足す**
（`group-paths` → クレーム `group_paths`、`full.path: "true"`、アクセストークンのみ）。既存の `groups` /
`department` クレームは 1 文字も変えない（Wiki.js 等の既存の読み手を動かさない）。
稼働 realm へは `deploy/local/keycloak-setup/reconcile-realm.sh` がクライアントスコープのマッパー差分を当てる
（`reconcile-realm.js` の `planMappers`）。**当たるまではクレームが無い ＝ 導かない ＝ 従来どおり `unassigned`**（安全側）。

新しいサービス間契約（例: `UserDirectory` へ所属照会の RPC を足す）でも導けるが、プラットフォームユニットの契約・
AuthorizationService・両輸送（REST / gRPC）の実装を伴う。トークンは DataSourceService が自ら検証しており、
**登録を許可した `AdminOnly` の判定がすでに同じトークンのクレームに依っている**ので、所属を同じトークンから読んでも
信頼境界は増えない（IADR-0447 が ① を退けた理由 ——「BFF が**本文**へ載せた主張を信じる」「名前しか運ばない」——
はどちらも本件に当たらない）。IADR-0468 に記録する。

## 設計

### 導出規則（`Domain/RegistrantDepartment.cs`）

- 入力: 登録者のトークンの `group_paths`（0..N 個のフルパス）。
- `/department/` で始まるパスについて、**その直後のセグメント（部門コード）**を集める。
  入れ子（`/department/engineering/backend`）は上位の部門コード（`engineering`）として数える
  —— 下位グループの所属者はその部門の所属者でもあり、別の部門として数えると 2 つに見えて導けなくなる。
- **異なる部門コードがちょうど 1 つ**のときだけそのコードを返す。**0 個・2 個以上は null**（導かない）。
- 突き合わせは序数（大小文字を畳まない。Keycloak のグループ名は大小文字を区別する）。
- `/department` そのもの（親グループ）への所属は部門として数えない。

### 適用点（`DataSource.Create` と登録端点）

- `DataSource.Create(..., registrantDepartment)` に任意引数を足す。`defaultAttributes.department` が
  **未解決（欠落・空白・予約値 `unassigned`）のときだけ**導いた値を入れ、その後で従来の失敗安全
  （`WithRequiredAttributeFailsafe`）を通す。**明示値は上書きしない。**
  「未解決」の判定は既存の `IsUnresolved`（#752 段 1 と同じ述語）を使う —— 述語を 2 つ持たない。
- 登録端点（`POST /datasources`）が `HttpContext.User` の `group_paths` から導いて渡す。
- **`Update`（PUT）/ `Patch` は導き直さない。** 更新者は登録者ではなく、計画・裁定とも「登録者」を定めた。
  更新で `department` を空にすれば従来どおり予約値へ倒れる（SC-06 の編集フォームの補助文もそのまま正しい）。
- サービスアカウント（`abac-seeder` 等）はグループを持たないので導かれない（予約値のまま）。

### 値域の検証を今回入れない理由（フォローアップの問い）

コーディネータ設計は「明示値が realm の部門グループのコードでなければ 400」を求めたが、DataSourceService が
**realm の部門グループの一覧**を得る既存の読み口は無い。

| 候補 | 可否 |
| --- | --- |
| `IPlatformUserDirectory`（gRPC `UserDirectory/CheckUsernames`・`GetUserAttributes`） | グループを返さない |
| AuthorizationService の属性辞書の `department.allowedValues` | 値が realm と一致しない（開発 seed は `finance` / `legal` も持つ）。裁定は「realm のグループ」である |
| REST `GET /authz/groups/lookup`（`InteractiveUser`） | 利用者トークンを転送すれば引けるが、IADR-0379 決定 4 / IADR-0401 決定 2 の「east-west に利用者トークンを載せない」と逆向き。gRPC 並走時には使えない |
| 登録者のトークン | 登録者**自身**の所属しか分からない |

→ **新しいサービス間契約（例: `UserDirectory` に「このパスのグループは実在するか」を足す）が要る。**
コーディネータの指示どおり、今回は導出だけを入れ、検証は PR 本文と IADR-0468 にフォローアップの問いとして残す。
画面の自由入力は変えない（補助文と例示だけを直す）。

### 画面（SC-06）

- **登録フォーム**の部門の補助文を「未入力のときは、登録する管理者が属する部門グループ（1 つだけのとき）の
  コードが入ります。決まらないときは予約値 unassigned が入ります。」へ改める。例示（placeholder）を realm の
  コード体系に合わせ `例: engineering` とする。
- **編集フォーム**は導き直さないので補助文は変えない（例示だけ揃える）。
- 予約値を空欄で見せる挙動・未入力ならキーごと送らない挙動は変えない。
- 文言を変えるので `pnpm run i18n` でカタログを再生成し、en の訳を足す（`check-i18n-catalogs`）。

### 契約文書

`docs/api/openapi.yaml` の登録操作と `CreateDataSourceRequest.defaultAttributes` の説明
（「`department`→`unassigned`」）を登録者の部門グループからの補完へ改め、`pnpm run codegen` で orval の生成物を再生成する。

## 母集合（規則 9・10）

「この変更で誤りになる記述」を誤りの側の文字列で走査した（`git grep`。`src/ai-stock-trading` を除く）。

| 走査 | 当たり | 扱い |
| --- | --- | --- |
| `部門コードの値域\|値域が定まるまで\|実装してはならない` | `DataSource.cs:198-199`（①は実装してはならない） | **直す**: 値域は定まった／器は未確定なので①はなお実装しない、へ |
| 同上 | `lib/abac/department.ts:10-15`（値集合は持たない・SC-09 の辞書が管理・①は未裁定） | **直す**: 値域は realm の部門グループ、検証は未実装、①は器が未確定 |
| 同上 | IADR-0359・`specs/20260815_*767*`・`20260816_*796*`・`20260828_*752-754*`・`20260903_*1194*` | 凍結記録。**書き換えない**（IADR-0468 が後継の記録） |
| `未入力のときは予約値` | `DataSourceForm.tsx`（登録）・`DataSourceAttributesForm.tsx`（編集）・`DataSourceManagementPage.test.tsx`・カタログ | 登録側だけ**直す**（編集側は導き直さないので正しい） |
| `department.*unassigned`（`docs/`・契約） | `docs/api/openapi.yaml:1579,4503`（登録）／`1626,4538`（更新） | 登録側だけ**直す**（生成物 `bff.schemas.ts` は codegen で追随） |
| 同上 | `docs/data/data-source.md:65,120-123`（「本欄の値のみ」「入力しなければ unassigned」） | **直す** |
| 同上 | `docs/screens/SC-06_datasource-management.md:193`（値域の制約なし・未入力はサーバが unassigned） | **直す** |
| 同上 | `docs/tests/SC-06_datasource-management.md:66`（4-c）・`docs/tests/FR-01_data-source-catalog.md` | 4-c の期待を直し、FR-01 へ T-53〜T-59 を足す |
| `group_paths` | 0 件（新設） | — |
| `full.path` | realm の `groups` マッパー 1 件・IADR-0447 の表 1 件 | realm は**変えない**（別マッパーを足す）。IADR-0447 は凍結 |

除外: `docs/tests/*` の過去行の日付つき追記は書き換えない。`DataSourceAttributesView.tsx` の「予約値 unassigned が入ります」は
「値が無いとき」の表示であり、登録者から導かれた値が入っていればそれが出るので誤りにならない。

## 受け入れ基準（Given-When-Then）

1. **Given** 部門グループにちょうど 1 つ属する管理者 **When** `department` を指定せずに登録する **Then** 既定属性 `department` がそのコードになる
2. **Given** 部門グループに属さない管理者 **When** 同上 **Then** `unassigned`
3. **Given** 部門グループに 2 つ以上属する管理者 **When** 同上 **Then** `unassigned`（導かない）
4. **Given** 明示値 **When** 登録する **Then** 明示値が保たれる（予約値 `unassigned` を明示した場合は未指定と同じく導く）
5. **Given** 登録済みのソース **When** 別の管理者が PUT / PATCH で `department` を空にする **Then** 導き直さず `unassigned`
6. **Given** 入れ子の所属（`/department/engineering` と `/department/engineering/backend`）**Then** `engineering`
7. **Given** Keycloak が発行する形（`group_paths` の JSON 配列）の JWT **Then** 複数のクレームとして読める
8. SC-06 の登録フォームが補完の規則を補助文で伝える
9. ①は実装されていない（フォルダ名から推定しない）

## テスト

| ID | 内容 | 置き場所 |
| --- | --- | --- |
| T-53 | ちょうど 1 つ → そのコード | `Tests/Domain/RegistrantDepartmentTests.cs` |
| T-54 | 0 個・無関係なグループのみ・親 `/department` のみ → null | 同上 |
| T-55 | 2 つ以上 → null（変異: 「複数でも先頭を採る」にすると赤） | 同上 |
| T-56 | 明示値は上書きしない／`unassigned` の明示・空白は導く | `Tests/Domain/DataSourceTests.cs`・端点 |
| T-57 | PUT / PATCH は導き直さない | 端点 |
| T-58 | 入れ子は上位コードに畳む（同じ部門の入れ子は 1 つ、異なる部門は 2 つ） | `RegistrantDepartmentTests` |
| T-59 | Keycloak 形の JWT（配列クレーム）を検証すると `group_paths` が複数クレームで読める | `RegistrantDepartmentTests` |
| SC-06 4-c | 登録フォームの補助文 | `DataSourceManagementPage.test.tsx` |

テストは TestServer（メモリ内）で走り、ソケットを開かない。

## 計画との差異

- **計画本文の更新が要る**（環流はコーディネータが起票する）: ★未確定表の「部門コードの値域」行（組織・未確定 → realm の
  `department` グループ）、§システム投入経路 の `department` の解決順（② の中で登録者の部門グループから補う）。
- 値域の検証（ADR-0074 決定 4 と同型）はフォローアップの問いとして残す。

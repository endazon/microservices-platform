---
title: IADR-0468 データソースの既定部門は、登録した管理者のトークンが運ぶ所属グループのフルパスから `/department/<コード>` がちょうど 1 つのときだけ導く（解決順②の中で・登録時だけ・値域の検証はフォローアップ）
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-01, UC-04, SC-06, ADR-0074, ADR-0088, ADR-0109, IADR-0019, IADR-0199, IADR-0359, IADR-0379, IADR-0401, IADR-0447, IADR-0465]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md §メタデータ・属性マッピング／§システム投入経路での owner / department / lifecycle（★未確定表「部門コードの値域」「フォルダ写像表の置き場所（器）」）
  - planning:projects/microservices-platform/07_adr/ADR-0074_owner-mapping-table-container-in-sc06.md 決定 2・4
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1
  - planning:projects/microservices-platform/07_adr/ADR-0109_bff-user-credential-relay-is-edge.md 決定 3
related_specs:
  - ../specs/20260926_issue-754_department-from-registrant-group.md
---

# IADR-0468: データソースの既定部門を、登録した管理者の部門グループから導く（#754）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（#754。利用者裁定 2026-09-26 の実装）

## 起点・関連

- 関連する計画書 ID: FR-05（ABAC 文書属性）・FR-01 / UC-04（データソース登録）・SC-06（データソース管理）
- 関連する計画 ADR: ADR-0074 決定 2（`department` の ① は値域の確定を待つ。器と値域は別の条件）・決定 4（手で入れる写像は登録時に実在を検証する）／ADR-0088 決定 1（呼び出し元の主張を判定に用いない）／ADR-0109 決定 3（エッジの後段は中継された利用者の資格情報を自ら検証する）
- 関連する実装 ADR: IADR-0019（必須属性の失敗安全）・IADR-0199（予約値は解決できなかったことの記録）・IADR-0359（`owner` の写像表と解決器）・IADR-0447（`${current_groups}` を IdP の所属照会で束縛し、トークンの `groups` クレームを退けた記録）・IADR-0379 決定 4 / IADR-0401 決定 2（east-west の面に利用者トークンを載せない）・IADR-0465（中継された利用者の資格情報を後段が自ら検証する先例）
- 作業仕様書: `../specs/20260926_issue-754_department-from-registrant-group.md`

## コンテキストと課題

#754 は「部門コードの値域（部門マスタ）が組織側で未確定」のため、計画の「値域が定まるまで `department` の写像は行わない」に従って
着手できる差分が 0 行と判定されていた。2026-09-26、利用者が #754 のコメントの選択肢 A を採り、次を裁定した（コーディネータ経由）。

1. **部門コードの値域は Keycloak realm の `department` グループ**（`/department/<コード>`。開発 realm では `engineering` / `sales` / `hr`）。
2. **文書の部門は、登録した利用者の部門グループ所属から導く。**

決めるべき点は 4 つあった。**(a) 登録者の所属をどこから読むか**、**(b) 導く規則（何個なら導くか・入れ子）**、
**(c) どの段に置くか（新しい段か、② の中か）・いつ導くか**、**(d) 明示値の値域検証をどう扱うか**。

## 実測

- 登録者の資格情報は BFF が中継し（`SessionTokenPropagationMiddleware` → `DataSourceBffEndpoints.CreateForwardingClient`）、
  DataSourceService が `AddPlatformAuth`（JwtBearer）で**自ら署名・発行元を検証**している。登録端点の `AdminOnly` も同じトークンの
  `realm_access.roles` で判定している。
- `bff` クライアントの既定スコープに `abac-attributes` があり、そこに 2 つの関連マッパーがある。
  - `groups`: `oidc-group-membership-mapper`・**`full.path: "false"`** —— 載るのはグループ**名**だけ。
  - `department`: `oidc-usermodel-attribute-mapper` —— **利用者属性が優先**され、無ければグループ属性を最初の 1 つだけ引く。
    開発 realm の利用者は利用者属性 `department` を直接持つ。
- C# で `groups` クレームを読むコードは 0 件。Microsoft.IdentityModel.JsonWebTokens 8.0.1 の既定受信写像は `group`（単数）だけを写し、
  `groups` / `group_paths` は名前が変わらない。
- DataSourceService が realm の**グループ一覧**を得る既存の読み口は無い（`IPlatformUserDirectory` は利用者名の実在と属性だけ。
  REST `GET /authz/groups/lookup` は `InteractiveUser` で、利用者トークンの転送を要する）。

## 検討した選択肢

### (a) 登録者の所属をどこから読むか

1. **トークンの `groups` クレーム（名前）** —— 却下。`/department/sales` と `/teams/sales` を区別できない。
   名前で突き合わせると**誤った部門を作る**（計画「誤った写像は裁量制御が意図しない相手に開く。安全側は解決しない」）。
   名前から部門を見分けるには値域の一覧が要るが、(d) のとおりそれを引く口が無い。
2. **トークンの `department` クレーム** —— 却下。利用者属性が優先され、グループ由来でも**複数所属を 1 つへ黙って畳む**ので
   「ちょうど 1 つ」を判定できない。裁定は「グループ所属から」であって利用者属性からではない。
3. **サーバ側の所属照会**（`UserDirectory` gRPC に所属の RPC を足し、AuthorizationService が `GetUserGroupsAsync` で引く）——
   却下（今回は）。プラットフォームユニットの契約・AuthorizationService・両輸送（REST / gRPC）の実装を伴う新しいサービス間契約になる。
4. **realm の `abac-attributes` スコープへ、フルパスを運ぶマッパーを 1 本足す**（`group-paths` → クレーム `group_paths`・
   `full.path: "true"`・アクセストークンのみ）—— **採用。**

   - **信頼境界は増えない。** トークンは本サービスが自ら検証しており（ADR-0109 決定 3 の形）、**登録を許可した `AdminOnly` の判定が
     すでに同じトークンのクレームに依っている。** IADR-0447 が ① を退けた理由（BFF が**本文**へ載せた主張を信じる／名前しか運ばない）は
     どちらも本件に当たらない —— ここで読むのは署名検証済みのトークンそのものであり、運ぶのはフルパスである。ADR-0088 決定 1
     （呼び出し元が本文で送った属性を評価に用いない）とも衝突しない。
   - **既存のクレームは 1 文字も変えない**（`groups` を `full.path: true` へ変えると Wiki.js 等の既存の読み手が動く）。
   - **配備**: 稼働 realm へは `reconcile-realm.sh` がクライアントスコープのマッパー差分を当てる。**当たるまではクレームが無い ＝ 導かない ＝
     従来どおり `unassigned`**（安全側に倒れ、黙って誤った値を作らない）。組織の本番 realm にも同じマッパーが要る（無ければ導かれないだけ）。
   - 鮮度はトークンの寿命に従う（登録操作の時点のトークン）。登録の瞬間に 1 回読むだけなので、キャッシュの問題は生じない。

### (b) 導く規則

- `/department/` で始まるパスの**直後のセグメント**を部門コードとして集め、**異なるコードがちょうど 1 つ**のときだけ返す。
  **0 個・2 個以上は導かない。** 2 部門の人の登録を片方へ寄せるのは推測であり、計画の「安全側は解決しない」に反する。
- **入れ子は上位のコードに畳む**（`/department/engineering/backend` → `engineering`）。下位グループの所属者はその部門の所属者でもあり、
  別に数えると同じ部門の入れ子だけで「2 つ」に見えて導けなくなる。
- 照合は序数（Keycloak のグループ名は大小文字を区別する）。親 `/department` 自体・直下が空のパスは部門として数えない。
- 部門コードは**グループ名**である。開発 realm では各部門グループの属性 `department` がグループ名と一致しており、利用者側の
  ABAC 属性（`department` クレーム）と同じ体系になる。

### (c) 段と時点

1. **新しい段（①と②の間）を作る** —— 却下。取り込みのたびに誰の所属を読むのかが定まらず（取り込みに人は居ない）、
   保存されない値は SC-06 から見えも直せもしない。
2. **② データソースの既定属性の中で、登録時に補う** —— **採用。** 導いた値は `DefaultAttributes` に保存され、SC-06 で見えて編集でき、
   既存の `GetEffectiveAttributes` の経路にそのまま乗る。
   - **明示値は上書きしない。** 埋めるのは `department` が**未解決**（欠落・空白・予約値 `unassigned`）のときだけで、判定は取り込み経路の
     上書き（#752 段 1）と同じ述語 `IsUnresolved` を使う（「未解決」の定義を 2 つ持たない）。
   - **更新（PUT / PATCH）では導き直さない。** 裁定は「**登録した**利用者」であり、更新した管理者の所属へ書き換わると同じソースの部門が
     最後に触った人で揺れる。更新で空にすれば従来どおり予約値へ倒れる。
   - サービスアカウント（`abac-seeder` 等）はグループを持たないので導かれない。

### (d) 明示値の値域検証

コーディネータ設計は「明示値が realm の部門グループのコードでなければ 400」（ADR-0074 決定 4 と同型の書き込み時検証）を求めた。
**今回は入れない。** DataSourceService が realm の部門グループの一覧（または「このパスのグループは実在するか」）を得る既存の口が無く、
入れるには新しいサービス間契約が要る（コーディネータの指示どおり、大きな契約を発明せずフォローアップの問いとして残す）。
**追跡は #1557**（2026-09-26 起票）。

| 候補 | 可否 |
| --- | --- |
| `IPlatformUserDirectory`（`CheckUsernames` / `GetUserAttributes`） | グループを返さない |
| AuthorizationService の属性辞書 `department.allowedValues` | realm と一致しない（開発 seed は `finance` / `legal` も持つ）。裁定は realm のグループ |
| REST `GET /authz/groups/lookup` に利用者トークンを転送 | IADR-0379 決定 4 / IADR-0401 決定 2 と逆向き。gRPC 並走時は使えない |
| 登録者のトークン | 登録者**自身**の所属しか分からない |

画面は自由入力のまま（補助文と例示だけを直す）。**導かれる値は構成上つねに realm の部門グループのコード**なので、本決定が新しく
値域外の値を作ることは無い。

## 決定

1. **登録者の所属は、realm の `abac-attributes` スコープに足した `group-paths` マッパー（クレーム `group_paths`・フルパス・アクセストークンのみ）から読む。**
   既存の `groups` / `department` クレームは変えない。
2. **`/department/<コード>` の異なるコードがちょうど 1 つのときだけ導く。** 0 個・2 個以上は導かない（予約値 `unassigned`）。入れ子は上位のコードに畳む。
3. **解決順 ② の中で、登録（`POST /datasources` → `DataSource.Create`）のときだけ、未解決の `department` を補う。** 明示値は上書きしない。更新では導き直さない。
4. **① フォルダ → 部門の写像は引き続き実装しない。** 値域は定まったが、計画の★未確定表で**写像表の置き場所（器）が「計画（未確定）」のまま**である
   （ADR-0074 決定 2 は器と値域を別の条件として分けた）。フォルダ名からの推定も入れない。
5. **明示値の値域検証はフォローアップとする**（新しいサービス間契約が要る。追跡は #1557）。
6. ［2026-09-26 追記 / #754 監査］**導けなかった理由を区別してログに残す**（1 回の登録につき 1 行。`department` を明示した登録では出さない）。
   - **`group_paths` クレーム自体が無い → Warning。** realm の `group-paths` マッパーが未適用（`reconcile-realm.sh` 前）の疑いを名指しする。
     🔴 **Keycloak は所属が空の利用者についてこのクレームを省き得る**（空の複数値を発行しない挙動。稼働 realm では未確認）ので、
     トークンだけでは「マッパー未適用」と「所属 0」を区別できない。**文言に両方の原因を書く。**
   - **クレームはあるが部門グループがちょうど 1 つではない → Information。** 設計どおりの「導かない」であり、運用者の対処は要らない。
   - 保存される値はどちらも予約値 `unassigned` のまま（ログは記録であって値を変えない）。利用者識別子・グループ名は載せない（件数だけ）。
   - 理由: 両者は同じ `unassigned` として見えるが**原因と直し方が違う**。マッパーの当て忘れを黙って `unassigned` へ畳むと、
     「計画どおり導かなかった」と区別がつかず、稼働 realm へのマッパー適用漏れに誰も気づかない。

## 結果

- 部門グループにちょうど 1 つ属する管理者が SC-06 で部門を空のまま登録したソースは、以後の取り込みで `department` にそのコードが載る。
  **既存の登録済みソースは遡って値を得ない**（導くのは登録時だけ）。
- `DataSource.cs` の「①は実装してはならない」の注記を「値域は定まった／器は未確定なので①はなお入れない」へ追記した。
- SC-06 の登録フォームの補助文を 2 段（登録者の部門グループ → 予約値）へ改め、例示を `例: engineering` とした。
- 契約文書（`docs/api/openapi.yaml` の登録操作・`CreateDataSourceRequest.defaultAttributes`）の説明を改め、orval の生成物を再生成した。

## 残るもの（フォローアップの問い）

1. **明示値の値域検証**（候補外の部門コードの拒否）。`UserDirectory` に「このパスのグループは実在するか」を足す等、新しい契約の要否を裁定に諮る。**#1557 が追う。**
2. **① フォルダ → 部門の写像表の器**（計画の未確定行）。
3. **計画本文の追随**（コーディネータが環流を起票する）: ★未確定表「部門コードの値域」行、§システム投入経路 の `department` の解決順（② の中で登録者の部門グループから補う）。
4. **受け入れ基準 4**（稼働クラスタで `unassigned` の件数が減ることの実測）は利用者のみが行える。稼働 realm へ `group-paths` マッパーを当てる
   （`reconcile-realm.sh`）ことが前提である。

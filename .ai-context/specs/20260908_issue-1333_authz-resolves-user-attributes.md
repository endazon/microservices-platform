---
title: 認可サービスが利用者属性を自ら引き直し、REST の /authz/scope に認可を掛ける（ADR-0088 の是正）
type: spec
status: done
related_ids: [FR-05, FR-09, FR-16, NFR-09, UC-05, UC-09, SC-12, SC-17, ADR-0004, ADR-0062, ADR-0080, ADR-0084, ADR-0086, ADR-0087, ADR-0088, IADR-0301, IADR-0329, IADR-0379, IADR-0385, IADR-0398, IADR-0401, IADR-0411, IADR-0412, IADR-0413]
author: Claude（実装）
created: 2026-09-08
updated: 2026-09-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md
  - planning:projects/microservices-platform/07_adr/ADR-0084_nfr09-judgment-unit-and-interim-clause-release.md
---

# 仕様書: 認可サービスが利用者属性を自ら引き直す（#1333 / `ADR-0088`）

## 起点となる計画書（トレーサビリティ）

- 非機能: **NFR-09**（認可。恒久条項に含まれる —— `ADR-0088` 決定 4）
- 機能要求: FR-05（ABAC）／FR-16・SC-12（登録者属性）／FR-09・SC-17（属性割当）
- 計画 ADR: **ADR-0088**（本 issue の全決定）／`ADR-0084` 決定 1・3（判定単位と経路ごとの解除）／
  `ADR-0086` 決定 2・4（起点）／`ADR-0087` 決定 2（先行条件。本作業の着地が解く）／
  `ADR-0004`（ABAC の基礎）／`ADR-0062` 決定 3（属性の部分集合統制）／`ADR-0080`（集合値属性）
- 実装 ADR: [[IADR-0301]]（`IIdentityAdminClient` の抽象）／[[IADR-0329]] 決定 1（`view-users` を持つ主体は 1 つ）／
  [[IADR-0379]] 決定 4・5（s2s と並走の正）／[[IADR-0385]] 決定 2（属性の線上表現）／
  [[IADR-0398]] 決定 1(b)（値域は validator が持つ）／[[IADR-0401]] 決定 2（狭い読み口。**本件を残余リスクとして自認していた**）／
  [[IADR-0411]]（抽出点を 1 つへ）／[[IADR-0412]]（**共有名前つきクライアントの危険。本作業で 2 回目**）／
  [[IADR-0413]]（本 PR で新設）
- issue: #1333

## 射程

**`ResolveScope` の 1 経路**（REST `POST /authz/scope` と gRPC `AuthzScope/Resolve`）。

🔴 **決定 1（引き直し）と決定 2（REST 面の認可）は同じ着地に含める**（`ADR-0088` 決定 2）——
**引き直しだけを先に入れると、この端点は今より危険になる**（同 実測 6）。

## 母集合（自分で引き直した走査。規則 1〜10）

基点 `develop` `1357c320`。`git rev-parse --is-shallow-repository` = **`false`**。

| 軸 | 検索語 | 件数 | 内訳 |
| --- | --- | --- | --- |
| 1 | `authz/scope` ＋ `"/scope\""`（全形） | **28 行** | 非テストの呼び出し **4** ／ 受け口 **2**（REST・gRPC）／ 残りは注記と試験 |
| 2 | `"AuthorizationService"`（名前つきクライアント） | **12 行** | 登録 **4** ／ 利用 **6** ／ 定数 **2** |
| 3 | `AddPlatformServiceToken` の保持者 | **9 ファイル** | 4 呼び出し元はすべて到達可能 |
| 4 | helm / compose の `serviceToken` ・`ServiceToken__ClientId` | **9 / 9** | 4 呼び出し元は**すべて配備済み** |
| 5 | realm の service account ＋ `platform-service` | **13** | 4 呼び出し元は**すべて保有** |

### 🔴 実測 1 —— REST `POST /authz/scope` は認可を 1 つも掛けていない

`AuthzEndpoints.cs` はグループ `/authz` を作り、**管理系だけを `AdminOnly` サブグループへ入れている**。
`g.MapResolveScope()` は**そのサブグループの外**で、本文が
「**サービス間呼び出しのため管理者限定にしない**」と明記している。
`[Authorize(Policy = ServiceCaller)]` を持つのは gRPC 面（`AuthzScopeGrpcService`）だけである。

🔴 **そして [[IADR-0379]] 決定 5 の並走中の正は REST である。gRPC 面だけを直しても正の側が開いたままになる。**

### 実測 2 —— REST の呼び出し元は 4 つ（非テスト）。**全部が非 2xx を deny へ縮退している**

| # | 呼び出し元 | 位置 | 非 2xx のとき |
| --- | --- | --- | --- |
| 1 | `BffScopeResolver.ResolveAsync` | `Platform.Shared.Infrastructure/…/BffScopeResolver.cs:45` | `null`（＝閲覧可能なし） |
| 2 | `RagOrchestrator.ResolveScopeAsync` | `AiAnalysisService/…/RagOrchestrator.cs:325` | `Granted=false` |
| 3 | `GraphAccessResolver` | `GraphService/…/GraphAccessResolver.cs:96` | `Granted=false` |
| 4 | `WikiAccessResolver` | `WikiService/…/WikiAccessResolver.cs:59` | `Granted=false` |

🔴 **この 4 つが揃って非 2xx を deny へ倒すことが、決定 3（下記）の前提である。**

### 🔴 実測 3 —— `ADR-0088` 実測 4 の「McpServer は `ResolveScope` の呼び出し元」は誤りである

McpServer と DataSourceService は名前つきクライアント `"AuthorizationService"` を使うが、
**行き先は利用者名簿（`/authz/users`）であって `/scope` ではない**
（`AuthorizationServiceRegistrarAttributes` / `AuthorizationServiceUserDirectory`）。
**環流する**（判定を変えるものではない —— 呼び出し元の数が 5 ではなく 4 だったという実測の訂正である）。

### 🔴 実測 4 —— s2s の資格情報は 4 つとも**既に配備済み**。deploy は 1 行も要らない

| サービス | compose | helm | realm の service account |
| --- | --- | --- | --- |
| bff | `ServiceToken__ClientId: bff`（`:876`） | `serviceToken.clientId: bff` | `platform-service` ✅ |
| aianalysis | `aianalysis-service`（`:440`） | 同左 | `platform-service` ✅ |
| wiki | `wiki-service`（`:542`） | 同左 | `platform-service` ✅ |
| graph | `graph-service`（`:707`） | 同左 | `platform-service` ✅ |

**これが「REST 面に `ServiceCaller` を掛ける」を最小の手段にしている** ——
Istio `AuthorizationPolicy` を置く案（`ADR-0088` 決定 2 が許す別解）は、
**メッシュの外（compose・`ISTIO=0`）で統制が消える**うえ、gRPC 面と**違う鍵**になる。

### 🔴 実測 5 —— 名前つきクライアント `"AuthorizationService"` は意味論の逆な 2 用途で共有されている

**[[IADR-0412]] が PR #1332 で解いたのと同型であり、これで 2 回目である**（[[IADR-0141]] の条件を満たす）。

| 用途 | 資格情報 | 呼び出し先 |
| --- | --- | --- |
| スコープ解決 | **何も付けない** | `/authz/scope`（**無認可**） |
| 利用者名簿（REST 経路） | **利用者の `Authorization` を転送** | `/authz/users`（`AdminOnly`） |

🔴 **共有クライアントへ s2s トークンを付ける形は採れない**（名簿側の転送と混ざる）。

### 🔴 実測 6 —— 現在の属性の引き方は全件列挙であり、**1000 件で黙って切れる**

`KeycloakIdentityAdminClient.ListUsersAsync`:
`admin/realms/{realm}/users?briefRepresentation=false&max=1000`。
しかも**ユーザー 1 人ごとに realm ロールの往復を追加で 1 回**行う（`RealmRolesAsync`）。
`AuthorizationService` にキャッシュは **0 件**（`IMemoryCache` / `IDistributedCache` の参照なし）。

**このまま決定 1 を実装すると、ABAC の判定 1 回ごとに「全利用者列挙 ＋ 人数分のロール照会」が走る。**
🔴 **そして利用者が 1000 人を超えると、1001 人目以降は `found=false` になる** ——
決定 1 の下でそれは **deny** である。**実在する利用者が、人数が増えただけで締め出される。**

### 陽性対照

- **PC-1**: gRPC 面 `AuthzScopeGrpcService` が走査に現れ「`ServiceCaller` を持つ側」へ落ちる ✅
- **PC-2**: `UserDirectoryGrpcService.GetUserAttributes` が現れ「**同じ全件列挙の上に載っている**」へ落ちる ✅
- **PC-3（陰性）**: `/authz/attributes/validate` は**現れない**（文書属性の検証であり利用者を読まない）✅

### 除外（理由つき）

- **`/authz/attributes/validate`**: 無認可のまま残す。`ADR-0088` の射程は `/scope` だけであり、
  **同じ PR で射程を広げない**（別の判断が要る。記録に留める・1 回目）。
- **`/authz/users`（`AdminOnly`）**: 認可は既に掛かっている。実測 5 の共有は解くが**転送の是非は触らない**。
- **契約（proto / DTO）からの `user_attributes` の削除**: `ADR-0088` 決定 1 が実装側の裁量とし、
  [[IADR-0379]] 決定 2 が削除を破壊的変更（v2 並走）と定める。**本 PR は「評価に用いない」だけを保証する。**

## 決定（実装方針）

### 決定 1: 引き直しは**評価器の手前に 1 つだけ**置く

`AuthorizationService/Features/Authz/ResolveScope/` に `ScopeUserAttributeSource` を置き、
**REST 端点と gRPC 面の両方がそれを通してから** `AbacEvaluator.ResolveScope` を呼ぶ。

🔴 **面ごとに書かない。** 片方だけが古くなる形は本リポジトリが繰り返し踏んでいる
（直近 3 例: [[IADR-0412]] 決定 6 / #1330 の 5 巡 / [[IADR-0411]] の抽出点 6 か所）。

**評価器へ渡す `AccessScopeRequest.UserAttributes` は、引き直した値で置き換える。**
本文で届いた `user_attributes` は**読まずに捨てる**。

### 決定 2: 🔴 「居ない」は**応答**、「引けなかった」は **status**。どちらも deny へ倒れる

`ADR-0088` 決定 1 は「引けなかったら fail-closed」と「居ない／引けなかったを分ける」を**同時に**求める。
実測 2 が示すとおり、**4 つの呼び出し元はすべて非 2xx を deny へ縮退する**ので、両立する。

| 事象 | REST | gRPC | 呼び出し元に届く結果 |
| --- | --- | --- | --- |
| 引けた | 200 ＋ 判定 | 応答 | 判定どおり |
| **利用者が名簿に居ない** | **200 ＋ `granted=false`** | **応答 `granted=false`** | deny |
| **IdP へ届かない・失敗した** | **503** | **`UNAVAILABLE`** | **deny**（4 つとも縮退する） |

🔴 **「引けなかった」を 200 `granted=false` にしない。** `ADR-0088` が名指しした
「**後段が落ちていることを『その利用者に権限が無い』と記録するのは嘘である**」がそれである。
[[IADR-0401]] が `GetUserAttributes` で既に採っている型（居ないのは応答・引けなかったのは status）に揃える。

🔴 **新しい枝を作らない。** deny の値は現行と同一（`AccessScopeResponse(userId, [], false)`）である。

### 決定 3: REST `/scope` に **`ServiceCaller`** を掛ける（gRPC と**同じ 1 つのポリシー**）

`ADR-0088` 決定 2 は形を問わないが、**gRPC 面と同じ鍵にする**ことを選ぶ ——
2 つの面が違う鍵で守られていると、**どちらが正かを読む人が判断できなくなる**。
実測 4 のとおり**配備の追加は 1 行も要らない**。

🔴 Istio `AuthorizationPolicy` は**採らない**（メッシュの外で統制が消える。compose・`ISTIO=0`）。

### 決定 4: s2s トークンは**スコープ解決専用のクライアント**に付ける（共有クライアントへは付けない）

`Platform.Shared.Infrastructure.Foundation.Authz` に `AuthzScopeHttpClient` を置き、
**登録も 1 か所**（`AddPlatformAuthzScopeHttpClient(config)`）にする。4 呼び出し元はこれを使う。

🔴 **実測 5 の共有はこれで解ける。** 利用者トークンを転送する名簿の経路と、
s2s を付けるスコープの経路が、**別のクライアントになる。**

### 決定 5: 🔴 **by-username の口をポートへ足す。キャッシュは置かない**

`IIdentityAdminClient.FindByUsernameAsync` を新設し、Keycloak 実装は
`admin/realms/{realm}/users?username={u}&exact=true&briefRepresentation=false` を引く。

**これで実測 6 の 2 つの問題が同時に消える。**

| | 今日 | 本 PR の後 |
| --- | --- | --- |
| 判定 1 回あたりの IdP 往復 | **1 ＋ 利用者数**（列挙 ＋ 人数分のロール照会） | **1**（ロールは要らない —— 引くのは属性だけ） |
| 1000 人を超えた利用者 | 🔴 **`found=false` ＝ deny** | **引ける** |

🔴 **キャッシュを置かない。** `ADR-0088` 決定 3 の制約
「**キャッシュを置くなら最大遅延を IADR に明記すること**」は、**置かないので発生しない** ——
属性の変更（SC-17 の割当・`ADR-0062` の部分集合統制）は**常に次の判定から効く**。
**これは「測っていないから決めない」ではなく、「1 往復で足りるので要らない」である。**

🔴 **`UserDirectoryGrpcService.GetUserAttributes` も同じ口へ移す** ——
同じ全件列挙の上に載っており（陽性対照 PC-2）、**同じ欠陥を 2 か所に残さない**。
照合規則は変えない（`OrdinalIgnoreCase`。`exact=true` の結果を同じ規則で絞る）。

### 決定 6: `user_id` の詐称は**閉じない**。閉じたつもりの記録を残さない

`ADR-0088` 決定 4 のとおり、閉じるのは**強いほうの半分**である。
**残る半分（他人の `user_id` を名乗る）は token exchange でしか閉じない**（`ADR-0086` 決定 2）。
🔴 **決定 3 の認可により、詐称できる主体は `platform-service` を持つサービスに限られ、gRPC 面と同じ水準に揃う。**

## 触るファイル（見込み）

| 面 | ファイル | 内容 |
| --- | --- | --- |
| ポート | `AuthorizationService/Domain/Ports/IIdentityAdminClient.cs` | `FindByUsernameAsync` を追加（決定 5） |
| 実装 | `.../ExternalServices/KeycloakIdentityAdminClient.cs` | `exact=true` の 1 往復 |
| 実装 | `.../ExternalServices/InMemoryIdentityAdminClient.cs` | 同上（開発・テスト） |
| 判定 | `.../Features/Authz/ResolveScope/ScopeUserAttributeSource.cs`（新規） | **引き直しの唯一の点**（決定 1・2） |
| 受け口 | `.../Features/Authz/ResolveScope/Endpoint.cs` | 引き直しを通す ＋ 503 |
| 受け口 | `.../Features/Authz/ResolveScope/GrpcService.cs` | 引き直しを通す ＋ `UNAVAILABLE` |
| 認可 | `.../Features/Authz/AuthzEndpoints.cs` | `/scope` を `ServiceCaller` のサブグループへ（決定 3） |
| 名簿 | `.../Features/Users/Directory/GrpcService.cs` | `GetUserAttributes` を by-username へ（決定 5） |
| 客体 | `Platform.Shared.Infrastructure/Foundation/Authz/AuthzScopeHttpClient.cs`（新規） | 専用クライアント ＋ 登録（決定 4） |
| 客体 | `BffScopeResolver.cs` / `RagOrchestrator.cs` / `GraphAccessResolver.cs` / `WikiAccessResolver.cs` | 専用クライアントへ差し替え |
| 客体 | 4 サービスの `Program.cs` | `AddPlatformAuthzScopeHttpClient` |
| 記録 | `.ai-context/adr/IADR-0413_….md` ＋ 索引行 | 決定 1〜6 |
| 文書 | `docs/api/east-west-grpc.md` ほか | 参照実装の節（利用者文脈の扱いが変わる） |

**deploy / helm / compose / realm / secret は変更なし**（実測 4）。

## テスト（受け入れ基準）

- [x] 🔴 **本文の `user_attributes` が判定に影響しない**（REST・gRPC の両面で。**陽性・陰性を対で**）
      —— 偽の属性を主張しても、引き直した属性で判定される
- [x] 利用者が名簿に**居ない** → `granted=false` の**応答**（REST 200 / gRPC 応答）
- [x] IdP が**引けない** → **REST 503 / gRPC `UNAVAILABLE`**（**200 `granted=false` にしない**）
- [x] 🔴 上の 2 つが**ログで区別できる**（`ScopeUserAttributeSource` が `LogInformation` と `LogError` で分ける）
- [x] REST `POST /authz/scope` が**資格情報なしで 403**（★ 訂正: `TestAuthHandler` は常に認証を通すため、観測できるのは「認証済みだが `platform-service` を持たない」＝ 403 である。実配備では資格情報なしは 401）・**管理者の利用者トークンで 403**
- [x] 4 呼び出し元が REST 経路で**引き続き通る**（専用クライアントが s2s を付ける）
- [x] 🔴 **名簿の経路（`/authz/users`）に s2s トークンが付かない**（別クライアントである）（実測 5 の分離の対）
- [x] `FindByUsernameAsync` が **1000 人を超える名簿でも引ける**（`max=1000` の口を引かないことを試験で固定）（実測 6 の打ち切りの回帰試験）
- [x] `GetUserAttributes` の照合規則が変わっていない（`OrdinalIgnoreCase`）
- [x] 集合値属性（`tags`）が線上表現のまま届き、交差判定が効く（`ADR-0080` / [[IADR-0411]] の回帰）
- [x] `IdentityAdminContractTests` の陽性対照を更新（口が 1 つ増える）
- [x] 変異試験で赤を実測する（下記）

## 変異試験（実出力。すべてビルドし直して実走し、着地は `grep` で確認した）

| # | 変異 | 位置 | 赤になった試験 |
| --- | --- | --- | --- |
| M-1 | 評価器へ**本文の主張**を渡す（両面） | `Endpoint.cs` / `GrpcService.cs` | **4** |
| M-2 | 「引けなかった」を 200 `granted=false` へ倒す（両面） | 同上 | **2** |
| M-3 | REST の `ServiceCaller` を外す | `AuthzEndpoints.cs` | **2** |
| M-4 | `exact=true` を落として前方一致へ戻す | `KeycloakIdentityAdminClient.cs` | **6** |
| M-5 | 専用クライアントから `ServiceTokenHandler` を外す | `AuthzScopeHttpClient.cs` | **3** |
| M-6 | トークン取得失敗を素の例外で通す | 同上 | **1** |

M-1 の内訳: `The_attributes_the_idp_holds_decide_the_scope`（陽性）/
`A_claimed_attribute_does_not_grant_anything`（陰性）/
`Claimed_user_attributes_do_not_grant_anything_over_grpc` / `ResolveScopeEndpoint_RoutesActionToEvaluator`。

🔴 **M-1 が陽性側も殺すことが重要である。** 陰性（主張しても通らない）だけでは
**評価器が常に deny を返す実装**でも緑になる。**陽性と陰性を対で置いて初めて
「判定が起きたうえで主張が捨てられている」と言える。**

🔴 変異はすべて戻し、**戻したことは「試験が緑に戻った」で確かめた**（`git diff` では確かめない。#1324）。

★［2026-09-08 追記 / #1333］🔴 **M-7 —— レビューが見つけた私の欠陥**（PR #1334 のレビュー / CodeQL）。

引き直しの点で `userId` を**生のままログへ出していた**。`userId` は**呼び出し元が本文で渡す
検証されていない値**であり（`ResolveScopeValidator` は `action` の値域しか持たず、gRPC 面は
validator を通らない）、CR/LF を混ぜると**この PR が強化しようとしている deny の監査ログへ
偽の行を混ぜられる**。

🔴 **本 PR の前提そのものが「呼び出し元の本文を信じない」であるのに、その値を素通しでログへ流していた。**
判定に使う値は生のまま（IdP へ問い合わせる鍵であり、妙な値なら「居ない」で deny へ倒れる）、
**ログへ出す表現だけ**を削る（`ForLog`：制御文字を落とし 256 字で切る）。

🔴 **値域を validator へ足す形は採らなかった** —— 制約が**面ごとに 2 か所**（REST の validator と
gRPC の手書き検証）へ要ることになり、引き直しの点を 1 つにした決定 1 と逆向きになる。

| # | 変異 | 赤 |
| --- | --- | --- |
| M-7 | `ForLog` を素通し（`var cleaned = userId`）へ戻す | **6**（改行 3 種 / TAB / 長さ / 読めない値） |

`ScopeUserAttributeLogSafetyTests` 11 本を追加（陽性対照つき）。`AuthorizationService.Tests` 230 → **241**。

★［2026-09-08 追記 / #1333］**レビュー 2 巡目の 🟢 も直した。** `exact=true` の候補が 2 人以上のとき
`FirstOrDefault` で先頭を採っており、**どちらの属性で判定したかが応答順しだい**だった
（同じ要求が日によって違う判定を返す）。**選ばずに落とす**形へ変えた（deny へ倒れる）。
`FindByUsername_refuses_to_choose_when_the_name_is_not_unique` が固定する。**242 本**。

## 実測（数字）

| | 前 | 後 |
| --- | --- | --- |
| `AuthorizationService.Tests` | 213 | **230**（+17） |
| `Platform.Shared.Infrastructure.Tests` | 325 | **330**（+5） |
| platform / knowledge 全体 | 失敗 0 | **失敗 0**（スキップの増減なし） |
| ABAC 判定 1 回あたりの IdP 往復 | **1 ＋ 利用者数** | **1** |
| 1000 人を超えた利用者の判定 | 🔴 **deny**（打ち切りで「居ない」に見える） | **正しく引ける** |

🔴 **`Platform.Bff.Tests` の 55 件と `AuthorizationService.Tests` の 18 件は、
着手直後に「意図した挙動変更」として一度赤くなった。** 器の側を直して緑へ戻したのであって、
主張を弱めてはいない —— 属性の出所が**要求本文から IdP へ移った**ぶん、
試験も「属性をどこへ置くか」を移した（`TestIdentityDirectory`）。

## やらないこと

- **`user_id` の詐称を閉じること**（決定 6。token exchange が要る）
- **契約から `user_attributes` を削除すること**（射程外。破壊的変更）
- **`/authz/attributes/validate` に認可を掛けること**（`ADR-0088` の射程外）
- **キャッシュを置くこと**（決定 5。要らない）
- **deploy / realm / secret を触ること**（実測 4）
- **#1255 の残りスライスへ着手すること**（本作業の着地が先行条件）

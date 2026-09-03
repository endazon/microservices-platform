---
title: 作業仕様書 — owner の写像表（データソース単位）を SC-06 の登録・更新フォームへ置き、写像先の実在を view-users で検証してから取り込みへ効かせる
type: spec
status: done
related_ids:
  - FR-01
  - FR-05
  - UC-04
  - SC-06
  - SC-17
  - ADR-0036
  - ADR-0064
  - ADR-0074
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - "ADR-0074 決定 1（写像表の器は SC-06 の登録・更新フォームが持つ。既定属性 3 つと同じ面・同じ権限）"
  - "ADR-0074 決定 3（器を入れても予約値は減らない。件数を完了判定に使わない）"
  - "ADR-0074 決定 4（写像先は登録時に実在検証する。通らない対は保存しない。検証は SC-17 側のクライアント〔view-users〕）"
  - "ADR-0074 決定 5（db コネクタへの値搭載は解決器の配備より後）"
  - "06_technical/09_datasource-connectors.md §システム投入経路（解決順 ① Keycloak 検索 → ② 写像表 → 予約値 system・「推測で埋めない」）"
related_adrs:
  - IADR-0364
  - IADR-0019
  - IADR-0051
  - IADR-0122
  - IADR-0128
  - IADR-0148
  - IADR-0199
  - IADR-0295
  - IADR-0301
  - IADR-0329
issue: "#1194"
---

# 作業仕様書: owner の写像表（データソース単位）

## 起点

planning#518 の環流を ADR-0074（Accepted・2026-09-03）が裁定し、**`owner` の解決順 ② に当たる
「データソース単位の写像表」の器を SC-06 の登録・更新フォームへ置くこと**が確定した。受け皿は #1194。

**#752（コネクタ契約 `SourceItem` に更新者を運ばせる）は射程外**である。同 issue は
`blocked:env` / `blocked:human` を持ち、ADR-0074 決定 5 が「`db` への値搭載は解決器の後」と
先後を定めた。**本 issue は器と解決器だけを作る**（実環境なしで実装・テストできる範囲）。

## 母集合（着手前に自分で引いた。issue 本文からは転記していない）

前例は **`department` の ②（データソースの既定属性 → SC-06 の入力欄。裁定は planning#372、実装は本リポの #767 と #1021。
[[IADR-0019]]）** である。「同じ器に載せる」ためには、既定属性が**登録・保存・適用の 3 面で
どこを通っているか**を先に引く必要がある。誤りの側（＝写像表が無いこと）からも引く。

### 走査 1 — 既定属性の器はどこにあるか（陽性対照側。全走査）

```console
$ git grep -rln "DefaultAttributes\|defaultAttributes" -- .   # 46 ファイル
$ git grep -n "DefaultAttributes" -- src/knowledge/backend | sed 's/:.*//' | sort | uniq -c | sort -rn
     30 .../DataSourceService/Tests/Domain/DataSourceTests.cs
     12 .../DataSourceService/Domain/DataSource.cs
      6 .../Knowledge.Contracts/Dtos/DataSourceDto.cs
      3 .../Migrations/20260705120000_AddDataSourceDefaultAttributes.cs
      3 .../Migrations/20260705120000_AddDataSourceDefaultAttributes.Designer.cs
      2 .../Tests/Features/DataSources/Sync/DataSourceSyncEndpointTests.cs
      2 .../Features/DataSources/Update/Endpoint.cs
      2 .../Features/DataSources/Update/Command.cs
      1 .../Migrations/DataSourceDbContextModelSnapshot.cs
      1 .../Migrations/20260808185210_AddDataSourceSyncHealth.Designer.cs
      1 .../Infrastructure/Persistence/DataSourceDbContext.cs
      1 .../Features/DataSources/Patch/Endpoint.cs
      1 .../Features/DataSources/Patch/Command.cs
      1 .../Features/DataSources/DataSourceEndpoints.cs
      1 .../Features/DataSources/Create/Endpoint.cs
      1 .../Features/DataSources/Create/Command.cs
```

**器は 9 面で構成されている。写像表も同じ 9 面へ載せる**（1 面でも欠けると「登録できるが更新で消える」
「保存できるが応答に出ない」という気づきにくい壊れ方になる —— `DefaultAttributes` の理由書きが
既にそう書いている）。

| # | 面 | 既定属性での実体 |
| --- | --- | --- |
| 1 | ドメインの器 | `DataSource.DefaultAttributes`（`Dictionary<string,string>`） |
| 2 | 生成・全置換・部分更新 | `Create` / `Update` / `Patch` の 3 メソッド |
| 3 | 永続化 | `DataSourceDbContext` の jsonb 変換 ＋ `ValueComparer`（内容ベース） |
| 4 | マイグレーション | `20260705120000_AddDataSourceDefaultAttributes`（＋ Designer ＋ スナップショット） |
| 5 | サービスの入力型 | `Features/DataSources/{Create,Update,Patch}/Command.cs` |
| 6 | 応答投影 | `DataSourceEndpoints.ToResponse` |
| 7 | BFF 契約 | `Knowledge.Contracts.Dtos` の 4 レコード ＋ `DataSourceBffEndpoints` の透過中継 |
| 8 | OpenAPI ＋ orval 生成物 | `docs/api/openapi.yaml` / `src/platform/frontend/src/lib/api/generated/**` |
| 9 | 画面 | `DataSourceForm.tsx`（登録）/ `DataSourceAttributesForm.tsx`（更新） |

**保存の意味論も引いた** —— `DefaultAttributes` は **PATCH でも「指定したときは全置換」**であり
（`DataSource.Patch`）、更新フォームは**既存の地図を土台にして自分の 3 キーだけを重ねた完全な地図**を
送っている（`DataSourceAttributesForm.tsx:100-116`）。**この意味論に写像表を混ぜてはならない**
（#1194 やること 1 の理由。片方の更新がもう片方を消す）。

### 走査 2 — 写像表の器（陰性。陽性対照つき）

```console
$ git grep -rniE "ownerMapping|identityMapping|principalMapping|userMapping" -- .
docs/operations/local-sso-recovery-runbook.md:83  ← Discord 通知の UserMapping。無関係
$ git grep -rln "DefaultAttributes\|defaultAttributes" -- . | wc -l    # 陽性対照
46
```

**器は無い。** 検索式そのものは 46 ファイルを返す語（`DefaultAttributes`）で機能することを確かめた。

### 走査 3 — 取り込み経路の解決段（陰性。陽性対照つき）

`DataSourceSyncService.PerItemAttributes`（:56-59）は `item.UpdatedBy` を**そのまま `owner` へ写す**。
コメント自身が「🔴 **ここには解決段が無い**」と述べている。

```console
$ git grep -n "UpdatedBy" -- .../DataSourceService/Infrastructure/ExternalServices/ | wc -l
0
$ git grep -n "new SourceItem" -- .../DataSourceService/Infrastructure/ExternalServices/ | wc -l
4          # 陽性対照。4 実装とも SourceItem を作るが UpdatedBy は 1 つも載せない
```

**したがって今日は無害だが、#752 が値を載せた瞬間に生の識別子が `owner` になる。**
本 issue は**その前に解決段を入れる**（ADR-0074 決定 5 の先後そのもの）。

### 走査 4 — 実在検証の後段（陽性）

```console
$ git grep -n "authz/users" -- src | grep -v Tests
platform/.../Platform.Bff/Foundation/Endpoints/UserAdminBffEndpoints.cs:32,37,42,47,52,56
platform/.../AuthorizationService/Features/Users/UserAdminEndpoints.cs:24,34
platform/.../Platform.Shared.Contracts/Dtos/UserAdminDto.cs:4
$ git grep -n "authz/scope" -- src | grep -v Tests | wc -l     # 陽性対照（サービス間で叩かれている口）
14
```

**`/authz/users` を叩いているサービスは 1 つも無い**（叩いているのは BFF の透過中継だけ）。
`/authz/scope` は 14 箇所から叩かれているので、「サービス間 HTTP でここを叩く」形自体は既存である
（`GraphAccessResolver` / `WikiAccessResolver` / `RagOrchestrator`）。**本 issue が
`/authz/users` の最初のサービス間呼び出しになる。**

### 走査 5 — 「利用者識別子」とは何か（値の同定。**ここを外すと写像が効かない**）

```console
$ git grep -n "NameClaimType" -- src | grep -v Tests
platform/.../Foundation/Extensions/AuthExtensions.cs:72        = "preferred_username"
platform/.../Foundation/Session/BffSessionExtensions.cs:160     = "preferred_username"
```

- `AbacEvaluator` は `${current_user}` を **`AccessScopeRequest.UserId`** へ束縛し、その値は
  `BffScopeResolver` の `http.User.Identity?.Name`（＝ `preferred_username`）である。
- `DocumentBodyIntake.WithOwner` は人が投入する経路の `owner` に**主体（同じ `preferred_username`）**を
  入れ、`CanWrite` は `StringComparison.Ordinal` で突き合わせる。

🔴 **したがって写像表の写像先は Keycloak の `username` である**（`IdentityUser.Username`。
内部 ID（`IdentityUser.Id`＝UUID）ではない）。**ここを ID にすると、保存も検証も通るのに
`owner` として一致しない**（静かに壊れる形）。

### 走査 6 — 後段の到達性（配備側）

```console
$ grep -rn "Services__AuthorizationService" deploy/
docker-compose.yml:377,463,602,740       → http://authorization-service:8080
helm/.../values.yaml:355,428,498,686     → （aianalysis / graph / wiki / bff の extraEnv）
```

**`datasource` の値には無い。** 既存 4 サービスのコード既定は `http://authorization-service:5005`
（**compose も k8s も 8080 で上書きしている。既定値のほうが古い**）。
本サービスは**新規に口を開く側なので、コード既定を最初から 8080 にする**（配備の上書き漏れが
「名前解決は通るがポートが無い」形で沈黙するのを避ける。values.yaml の `bff` に同型の実測コメントがある）。
あわせて compose / helm にも明示の env を足す。

### 除外したものとその理由

| 除外 | 理由 |
| --- | --- |
| `src/knowledge/backend/Services/IngestionService/**` | `git grep -n "owner" -- .../IngestionService \| grep -v Tests` が **0 件**。属性は `RawDocumentFetched.Attributes` を素通しする。**#1193 と衝突しない** |
| コネクタ 4 実装（`FileSystemConnector` ほか） | **#752 の射程**（ADR-0074 決定 5 の先後で、本 issue より後）。1 行も触らない |
| `department` の ①（フォルダ → 部門の写像） | **#754 の射程**、かつ planning#372 が「値域が定まるまで写像を行わない」と禁じている |
| `AuthorizationService/Features/Users/**` | **既存の `GET /authz/users` で足りる**ため触らない（#1185 が同領域を宣言している。衝突を避ける） |
| `POST /authz/users` 系の新設 | `IIdentityAdminClient` は**新規作成の口を型で持たない**（計画 SC-17。`IdentityAdminContractTests` が反射で固定） |

## 決めること（実装 ADR: [[IADR-0364]]）

1. **写像表を `Config` / `DefaultAttributes` と別の器にする**（`OwnerMappings`）。
2. **写像先は `username` であり、実在検証は `GET /authz/users`（`view-users` の後段）で行う。**
3. **DataSourceService → AuthorizationService のユニット跨ぎ HTTP を張る**（呼び出し元の
   `Authorization` を転送する）。**検証できなかったときは保存しない**（`400` と `502` を分ける）。
4. **PUT（全置換）で `ownerMappings` を省略したら現状維持にする**（非破壊のための意図的な非対称）。
5. **取り込み経路は写像表を引いた結果だけを `owner` にする。生の `UpdatedBy` を素通ししない。**

## 設計

### 1. ドメイン: `DataSource.OwnerMappings`

```csharp
public Dictionary<string, string> OwnerMappings { get; private set; } = [];
```

- **キー** = ソース側の利用者識別子（DB の列値・Wiki のアカウント名など。名前空間はソース側）。
- **値** = 基盤の利用者識別子（`preferred_username`）。
- **突合は `Ordinal`（完全一致）**。大小文字の畳み込み・部分一致・推測をしない
  （09_datasource-connectors「推測で埋めない」／ADR-0036「誤った写像は偽の所有者を作る」）。
- **正規化はキー・値の前後空白の除去だけ**にする。空キー・空値の対は**捨てずに 400 で拒否する**
  （黙って捨てると「入れたのに効かない」になる）。
- **件数の上限は設けない。** ADR-0074 §残るもの が「規模の上限を定めていない」と明記しており、
  `Config` / `DefaultAttributes` も上限を持たない（前例に合わせる。計画外の統制を足さない）。

解決器はドメインに置く（HTTP も EF も要らない純粋な関数）。

```csharp
// null / 空白 / 写像なし → null（＝上書きしない＝予約値 system へ倒れる）
public string? ResolveOwner(string? sourceUpdatedBy)
```

### 2. 保存の意味論（**既定属性と混ぜない**）

| 操作 | `defaultAttributes` | `ownerMappings` |
| --- | --- | --- |
| POST（登録） | 未指定は既定・予約値で補完 | 未指定は空辞書 |
| PUT（全置換） | **省略は 400**（既存規約） | **省略（null）は現状維持。`{}` で明示的に空にする** |
| PATCH（部分更新） | null は現状維持／指定時は全置換 | null は現状維持／指定時は全置換 |

🔴 **PUT の非対称は意図的である。** `config` / `defaultAttributes` の 400 は**契約の初期から**あり、
`ownerMappings` は**後から足す項目**である。ここで必須にすると**既存の PUT クライアントが一斉に 400 になる**
（契約の破壊）。「送り忘れで消える」ことを防ぐという 400 の目的は、**現状維持にすることでも同じだけ果たせる**。
`{}` を送れば消せる（明示は残る）。

### 3. 実在検証（サーバ側。**画面だけの検証にしない**）

- ポート `Domain/Ports/IPlatformUserDirectory`:
  `Task<PlatformUserDirectorySnapshot> ListUsernamesAsync(CancellationToken ct)`。
  `PlatformUserDirectorySnapshot(bool Available, IReadOnlySet<string> Usernames)`。
- アダプタ `Infrastructure/ExternalServices/AuthorizationServiceUserDirectory`:
  名前付き `HttpClient("AuthorizationService")` で `GET /authz/users` を叩き、
  `IHttpContextAccessor` から**呼び出し元の `Authorization` を転送する**。
  非 2xx・不達は `Available=false`（**空集合と区別する**）。
- 検証は `Domain/OwnerMappingValidator`（純粋）が行い、3 つの結果を返す。

| 結果 | 応答 | 理由 |
| --- | --- | --- |
| 書式違反（空キー・空値） | **400** | 後段に問い合わせるまでもない |
| 実在しない写像先がある | **400**（**どの値が実在しないかを返す**） | SC-06 は**管理者限定**の面であり、その管理者は SC-17 で利用者一覧を丸ごと見られる。**理由を伏せても隠せる情報が無い**（ADR-0074 決定 4 は「保存しない」だけを課しており、存在秘匿は課していない） |
| 名簿を引けなかった | **502** | 🔴 **「実在しない」と言わない。**「確かめられなかった」を「存在しない」と報告するのは嘘である（**どちらも保存しないので安全側は同じ**） |

**検証を走らせるのは「写像表が要求に含まれ、かつ空でない」ときだけ**である
（写像表を触らない PATCH が認可サービスの障害で落ちない）。

### 4. 取り込み経路（**1 箇所だけ触る**）

`DataSourceSyncService.PerItemAttributes(item)` → `PerItemAttributes(source, item)` にし、
中身を `source.ResolveOwner(item.UpdatedBy)` の結果だけにする。

- 写像が当たる → `owner` にその値（`GetEffectiveAttributes(perItem)` の 2 段目として重なる）。
- 当たらない・`UpdatedBy` が無い → **null を返す**（＝上書き無し＝予約値 `system`）。
- 🔴 **生の `UpdatedBy` を `owner` へ入れる経路は消える。** これが本 issue の安全側の本体である。

### 5. 画面（SC-06。新しい画面 ID も新しい権限も作らない）

- 共有コンポーネント `OwnerMappingTable.tsx` を作り、**登録フォームと既定属性フォームの両方**で使う
  （2 つ書くと片方が古くなる —— `department` の登録／更新で実際に起きた）。
- 行の追加・削除・編集。空行は送らない。
- **更新フォームの送信は `defaultAttributes` と `ownerMappings` を独立に送る**（片方を触っても
  もう片方が消えない。受け入れ基準そのもの）。
- サーバの 400 の本文（`{ error }`）を**そのまま表示する**（どの写像先が実在しないかが判る）。
- 権限は既定属性 3 つと同じ —— 画面は admin/operator が開けるが、**フォームを開く導線は admin だけ**
  （既存の `DataSourceManagementPage` の分岐に載せる）。

## 影響ファイル（宣言領域）

- `src/knowledge/backend/Services/DataSourceService/**`
- `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/DataSourceDto.cs`
- `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DataSourceBffEndpoints.cs`（透過中継のため変更なしの見込み）
- `src/knowledge/frontend/src/features/sc06-datasources/**`
- `docs/api/openapi.yaml` ＋ `src/platform/frontend/src/lib/api/generated/**`
- `scripts/contract-schema-baseline.json`
- `deploy/docker-compose.yml` / `deploy/helm/microservices-platform/values.yaml`（後段の宛先）
- `docs/screens/SC-06_*` / `docs/data/data-source.md` / `docs/tests/SC-06_*` / `docs/tests/FR-01_*`
- `.ai-context/adr/IADR-0364_*.md` ＋ `.ai-context/adr/README.md`

## 受け入れ基準（#1194 の 11 件の写像）

| # | 基準 | 写像先 |
| --- | --- | --- |
| 1 | 実在する写像先を登録 → 保存され再読込で残る | xUnit（Create）＋ Vitest ＋ 稼働 k3s の実測 |
| 2 | 実在しない写像先 → 保存されず理由が出る（**API 直叩き**） | xUnit（Create/Update/Patch）＋ 実測 |
| 3 | 運用者は閲覧できるが更新できない | xUnit（`X-Test-Roles: platform-operator` → 403） |
| 4 | 写像を持つソースの同期 → `owner` が写像先になる | xUnit（Sync。**陽性**） |
| 5 | 写像に無い識別子 → `owner` は `system` | xUnit（Sync。**陰性**。生の値が入らないことも固定する） |
| 6 | 写像表が空 → 従来と同一の属性 | xUnit（**陽性対照**。既存挙動の不変） |
| 7 | 片方だけ PATCH → もう片方が消えない | xUnit（両向き） |
| 8 | `pnpm run codegen` に差分が出ない | `/verify` |
| 9 | `dotnet build/test knowledge/backend/backend.slnx` | `/verify` |
| 10 | `pnpm run lint` / `typecheck` / `test` | `/verify` |
| 11 | 🔴 `owner=system` の件数は**減らなくてよい** | **測らない**（ADR-0074 決定 3。完了判定に使わない） |

## 変異試験（M1）

**「生の更新者を素通ししない」ことを、試験が本当に見ているか**を確かめた。

```console
# 変異: 写像が当たらないときに生の識別子へ倒す（本 issue が消した挙動そのもの）
-        var owner = source.ResolveOwner(item.UpdatedBy);
+        var owner = source.ResolveOwner(item.UpdatedBy) ?? item.UpdatedBy;  // MUTANT M1

$ dotnet test .../DataSourceService.Tests.csproj --no-build
  Sync_WhenTheMappingTableMisses_OwnerFallsBackToReservedValue_NeverTheRawIdentifier [FAIL]
失敗!   -失敗: 1、合格: 184、合計: 185
```

**殺せた。** 変異を戻して 185/185 が緑であることも確認済みである。

## 実測（稼働 k3s。2026-09-03 実施）

**差し替えたのは `datasource-service` のイメージだけ**である（`k3d-local/microservices-platform/datasource-service:issue1194`。
`kubectl set image` → `rollout status` 成功）。マイグレーションは起動時に適用され、
ログの SELECT に `d."OwnerMappings"` が現れることで確認した。

**認可の口は port-forward で直接叩いた**（`svc/datasource-service`）。#1194 の受け入れ基準が
「**サーバ側が拒否することを API 直叩きのテストで固定する**」と要求しているためである。
一時利用者 `msp1194-probe`（`platform-admin`）と一時クライアント `msp1194-probe-client` を
Admin REST API で作り、**終了時に両方削除した**（Keycloak の pod で `kcadm.sh` は実行していない）。

### 生出力

```console
=== 2) 利用者トークン（password grant） ===
preferred_username = msp1194-probe / iss = https://keycloak.localhost/realms/platform
  / realm roles = ['offline_access', 'platform-admin', 'uma_authorization', 'default-roles-platform']

=== 3) 疎通（陰性対照つき） ===
GET /datasources （トークンあり）=> 200
GET /datasources （トークンなし）=> 401  ← 陰性対照

=== 4) 陽性: 実在する利用者（developer）への写像を登録する ===
{"id":"4819eee1-…","name":"msp1194 probe positive","sourceType":"db",…,
 "defaultAttributes":{"confidentiality":"internal","department":"unassigned","owner":"system",
                      "lifecycle":"active","doc_scope":"organization"},
 "ownerMappings":{"hr_system:probe":"developer"},…}
HTTP 201

=== 5) 陰性: 実在しない利用者（msp1194-no-such-user）への写像を登録する ===
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1",
 "title":"One or more validation errors occurred.","status":400,
 "errors":{"errors":["写像先の利用者が存在しません: msp1194-no-such-user。利用者識別子（ログイン名）で指定してください。"]}}
HTTP 400

=== 6) 再読込 ===
msp1194 probe で始まる行 = 1 件           ← **400 の側は 1 行も保存されていない**
   msp1194 probe positive | ownerMappings = {"hr_system:probe": "developer"}
全ソース:
   filesystem pandoc-1097            | ownerMappings = {}   ← 陽性対照（既存ソースは空のまま）
   db         msp1194 probe positive | ownerMappings = {"hr_system:probe": "developer"}

=== 後片付け ===
disable 4819eee1-… => 204 / delete user => 204 / delete client => 204
user remaining = 0 / client remaining = 0
陽性対照: realm の利用者数 = 4 ['admin','developer','poc-operator','poc-user']
```

**実在検証が本当に Keycloak まで届いていることは、201 と 400 が分かれたこと自体が示している** ——
`developer` は realm に実在し、`msp1194-no-such-user` は実在しない。
経路は DataSourceService →（`Services:AuthorizationService` 既定 `:8080`）→ AuthorizationService
→ `IIdentityAdminClient` → 実 Keycloak である。**helm の env を当てずにイメージ差し替えだけで通った**
ことが、決定 3 の「コード既定を 8080 にする」判断の実証になっている。

### 予約値の件数（**完了判定に使わない**）

```console
$ node scripts/measure-abac-combinations.js
  owner       system      予約値  3 件 / 解決済み  5 件（予約値の割合 37.5%）
  department  unassigned  予約値  0 件 / 解決済み  8 件（予約値の割合  0.0%）
```

🔴 **減っていない。それでよい**（ADR-0074 決定 3）。器と解決器を入れても
`filesystem` 由来の文書は構造上更新者を運べない。**この数字を受け入れ基準にしない。**

### 測れなかったこと（正直に残す）

| 測れなかったもの | 理由 |
| --- | --- |
| **取り込みで `owner` が写像先へ写ること（陽性）** | 🔴 **稼働クラスタでは原理的に測れない。** 4 コネクタのいずれも `SourceItem.UpdatedBy` に値を載せないため、写像表を引く入口が無い（ADR-0074 実測 2・決定 3）。値の搭載は #752 の射程であり、決定 5 が**解決器の後**と定めている。**xUnit の `Sync_WhenTheMappingTableHits_OwnerBecomesTheMappedUser` が固定している。** |
| **SC-06 の画面から登録する経路** | 稼働クラスタの `bff-service` が**別セッションのイメージ（`:issue1199`）で 11 分前に転がっており**、差し替えると相手の実測を壊す。旧 BFF は `DataSourceDto` を再直列化するので `ownerMappings` を落とす —— **配備時は BFF のイメージも焼き直す必要がある**（本 PR の変更は契約に及ぶ）。画面側は Vitest が固定している。 |
| **運用者が更新できないこと（403）** | 一時利用者をもう 1 名増やす必要があり、実クラスタの変更を最小に留めた。`OwnerMappingEndpointTests.Operator_CanReadMappings_ButCannotWriteThem` が固定している。 |
| **名簿を引けないときの 502** | 稼働 AuthorizationService を落とす必要があり、他セッションを巻き込む。`Post_WhenDirectoryUnavailable_Returns502_NotBadRequest` が固定している。 |

### クラスタに残したもの

- `datasource-service` は **`:issue1194` のまま**である（merge train が `latest` へ戻す）。
- 探査用データソース `msp1194 probe positive` は**無効化した**（`DELETE` は論理削除であり、
  物理削除の口は無い）。**接続先は実在しないホストなので、無効化しないと同期が失敗し続ける。**

## 検証（`/verify` 相当）

| 検査 | 結果 |
| --- | --- |
| `dotnet build` / `dotnet test`（knowledge / platform 両ユニット） | **緑**（DataSourceService.Tests 185 件を含む） |
| `dotnet format --verify-no-changes`（両ユニット） | **緑** |
| `pnpm run typecheck` / `lint` / `format:check` | **緑**（lint は既存の warning 10 件のみ・error 0） |
| `pnpm run test` | 🟡 **1 件だけ赤**。`orvalMutator.test.ts` の `res.data.arrayBuffer is not a function` は**ローカル Node 24 のみの既知の赤**（CI は Node 22。当該ファイルは本 PR で 1 行も触っていない） |
| `pnpm run test:coverage` | **緑**（上記 1 件を除いて 1380 件・97.99% lines。しきい値は据え置いた —— `src/vitest.config.ts` は並行 PR と共有する面であり、床を上げると他の PR を巻き込む） |
| `pnpm run i18n` / `pnpm run codegen` | **緑**（en の未翻訳 8 件を埋めた。codegen は 2 回流して差分なし） |
| `node scripts/check-*.js` 一式 | **緑**（`check-contract-schema --update` / `check-test-spec-coverage --update` の床更新をコミットに含む） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 🔴 **`check-adr-numbering` だけ赤。** IADR 番号 **0364** はオーケストレータの割り当てであり、現在の最大は 0353 なので **0354〜0363 が欠番**になる。**マージ時の改番で解消する**（指示どおり）。ほかの判定はすべて緑。 |
| `node scripts/check-deploy-manifests.js` | ⚪ **ローカルでは走らせられない**（`kubeconform` が無い）。`helm template` で `datasource-service` に `Services__AuthorizationService` が描画されることは確認した。CI が本検査を持つ。 |
| `node scripts/check-knip.js --require` | ⚪ ラッパはローカルで `.bin/knip` を起動できない（Windows のシム）。**素の `knip` を直接流して区分ごとの件数が床（devDependencies 4 / exports 16 / types 16 / unlisted 1）と一致することを確認した。** 途中 `exports` が 17 になったので、使い道の無い `OWNER_KEY` を落として 16 へ戻した。 |

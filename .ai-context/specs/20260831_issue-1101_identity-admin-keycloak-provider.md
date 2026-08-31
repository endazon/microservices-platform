---
title: 稼働クラスタの IdentityAdmin を実プロバイダ（Keycloak Admin REST）へ切り替える
type: spec
status: done
related_ids: [FR-05, FR-09, UC-05, SC-17, NFR-09, ADR-0004, ADR-0026, ADR-0036, IADR-0301, IADR-0321]
author: implementation-agent
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0004_authn-authz-platform.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
---

# 仕様書: #1101 IdentityAdmin を稼働クラスタで実プロバイダにする

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（ABAC による可視範囲）/ FR-09（**システム管理者が利用者へのロール・ABAC 属性の
  割当とアカウントの無効化を行える**）
- ユースケース（UC）: UC-05
- 画面（SC）: SC-17 ユーザーアカウント管理
- 関連 ADR: ADR-0004（Keycloak）/ ADR-0026（認証 UX とアカウント管理）/ ADR-0036（ABAC）
- 実装 ADR: IADR-0301（SC-17 の抽象・権限範囲・provider 選択）/ 本作業で IADR-0321 を起票
- issue: #1101（親 #438・関連 #439 / #1088）

### 計画の受け入れ基準（逐語）

`05_screens/01_screens.md` §SC-17 より:

> - 目的: ユーザーアカウントの権限管理（ロール割当・ABAC属性割当・無効化）。**Keycloak Admin API と
>   属性ストアへ反映する。**
> - アクション: 保存→Keycloak・属性ストアへ反映し認可判定へ即時反映。無効化→全セッション即時失効。
> - 入力 / バリデーション: ロール割当＝定義済みロールのみ（併任可）／ ABAC 属性（部門・機密区分上限）＝
>   SC-09 の属性体系に定義済みの値のみ

`02_requirements/01_requirements.md` FR-09 より:

> 管理者が、文書に付与する属性・タグおよびABACポリシー（利用者属性×文書属性）を設定できる。また
> **システム管理者が、利用者へのロール・ABAC属性の割当とアカウントの無効化を行える**

**「Keycloak Admin API へ反映する」は計画の逐語である。** 偽の身元プロバイダで配備している限り、
この一文は配備状態では 1 つも成立しない。

## 着手前の実測（3 点）

**測定日 2026-08-31・稼働 k3s（`endazon-pc` / v1.35.4+k3s1）・リポジトリ `develop` `9a4d1a9a`。**

### ① 実プロバイダの実装は「ある」

```console
$ git grep -Iln "IdentityAdmin" -- . ':!src/ai-stock-trading'
src/platform/backend/Services/AuthorizationService/Infrastructure/ExternalServices/KeycloakIdentityAdminClient.cs
...
$ wc -l .../KeycloakIdentityAdminClient.cs
267
```

`KeycloakIdentityAdminClient`（Admin REST・client_credentials・6 操作すべて実装）と
`IdentityAdminRegistration`（`IdentityAdmin:Provider` の値域 `keycloak` / `in-memory`・既定なし）が
`develop` に在る。**したがって射程は「実装を作る」ではなく「配備の資格を用意して切り替え、実測する」である。**
ただし同ファイル冒頭は「**実 Keycloak との疎通は未検証**」と自認しており、スタブ済み
`HttpMessageHandler` に対してしか回っていない。

### ② `realm-management` クライアントは realm に**在る**（棚卸しの記述は誤り）

issue #1101 は「稼働 realm に `realm-management` クライアントが無い」と書くが、これは realm **export
JSON の `clients` 配列**を数えた結果であって、稼働 realm を数えた結果ではない。稼働 realm を直接数えると
`realm-management` は存在する（Keycloak が realm ごとに自動生成する組み込みクライアントである）。

```console
$ kubectl -n platform-infra exec deploy/keycloak -- sh -c '.../kcadm.sh config credentials ... \
    && .../kcadm.sh get clients -r platform --fields id,clientId,serviceAccountsEnabled'
... { "id": "51085298-14a2-46eb-b329-5ef0caaeb31f", "clientId" : "realm-management", ... }
```

**欠けているのは `realm-management` そのものではなく、その 3 ロールを service account に持つ機密
クライアントである。** 稼働 realm でサービスアカウントを持つのは `abac-seeder`（realm ロール
`platform-admin`）と `ai-stock-trading-kb-writer`（`platform-operator`）の 2 つだけで、どちらも
`realm-management` のクライアントロールを 1 つも持たない。

→ **作業は「realm JSON に機密クライアント 1 つと、その service account へのクライアントロール割当を
足す」である。**「既にあるが無効」ではない。

### ③ `in-memory` のままだと何が壊れているか — **静かな縮退である**

```console
$ kubectl -n microservices-platform get deploy authorization-service \
    -o jsonpath='{range .spec.template.spec.containers[0].env[*]}{.name}={.value}{"\n"}{end}'
...
IdentityAdmin__Provider=in-memory
```

`InMemoryIdentityAdminClient` は起動時に警告ログを 1 行出すが、**それ以外はすべて正常に振る舞う**:

- `GET /bff/authz/users` は 200 を返し、**Keycloak に実在しない 4 名**（`tanaka.taro` / `sato.hanako` /
  `suzuki.ichiro` / `takahashi.jiro`）を描く。稼働 realm の実在利用者（`admin` / `poc-user` /
  `poc-operator` / `developer`）は 1 人も出ない。
- 保存・無効化は 200 を返し、画面はリロードしても変更が残って見える（プロセス内に残るため）。
  **Pod が再起動した瞬間に全部消える。**
- したがって「反映したつもりで消える」— #972 / #992 / #1097 が潰したのと同型の穴である。
  **これ自体が欠陥であり、切り替えと同時に「本番相当で偽物を選べない」ようにする。**

## 対象範囲

- **対象**
  1. realm JSON へ機密クライアント `identity-admin`（service account 有効・`realm-management` の
     `view-users` / `manage-users` / `view-realm` のみ）を追加する。
  2. その client secret を **Vault → ExternalSecret → k8s Secret** で供給する（`bff-oidc` と同型）。
     realm JSON には dev 専用の既知プレースホルダしか置かない（既存 9 クライアントと同じ作法）。
  3. helm values / compose の `authorization` を `IdentityAdmin__Provider=keycloak` ＋
     `IdentityAdmin__Keycloak__{BaseUrl,Realm,ClientId,ClientSecret}` へ切り替える。
  4. **`in-memory` を非配備ホスト以外で選べなくする**（起動失敗。受け入れ基準 6 への回答）。
  5. 単体テスト（fail-closed の陽性・陰性対照つき）。
  6. 稼働クラスタでの実測（陽性対照と陰性対照を対で）。
  7. 文書追随（画面仕様 SC-17 / テスト仕様 SC-17 / セキュリティ）と IADR-0321。
- **対象外（理由つき）**
  - **人事システム連携そのもの**（SC-17 は「連携の結果」を表示するだけ。方式未確定）。
  - **ロールの 4 分割**（ADR-0026 のフォローアップ。realm に 2 ロールしか無い）。
  - **無効化した利用者の既存セッションが次要求で 401 になることの実測**は #439 の要求側であり、
    実測できたら書き、できなければ「できなかった」と書いて #439 へ残す（issue #1101 の指示どおり）。
  - **`PERSIST=1` での realm 反映**（#1088 の射程）。本作業は `PERSIST` 未設定＝毎回 re-import の
    稼働構成で実測する。

## 母集合（自分で引き直した結果・[[IADR-0141]] 決定 1 / traceability.repo.md 規則 9・10）

**誤りの側から引く。** 検索語は「`in-memory` が既定である」「`realm-management` を持つ機密クライアントが
**まだ無い**」「実 Keycloak との疎通は**未検証**」という**現に誤りになる記述**の側から採った。
**拡張子で絞らず、パスの除外のみ**で引いた（`src/ai-stock-trading` は submodule なので除外）。

軸 1: `git grep -In "IdentityAdmin" -- . ':!src/ai-stock-trading'` → 25 ファイル
軸 2: `git grep -In "realm-management" -- . ':!src/ai-stock-trading'` → 8 ファイル
軸 3: `git grep -Iln "abac-seeder"`（＝realm クライアントを列挙している文書の側）→ 7 ファイル
軸 4: `git grep -Iln "SC-17" -- docs/ .ai-context/adr/ scripts/ deploy/` → 16 ファイル

| # | 反映先 | 何を直すか |
| --- | --- | --- |
| 1 | `deploy/keycloak/microservices-platform-realm.json` | クライアント `identity-admin` ＋ service account の clientRoles |
| 2 | `deploy/helm/microservices-platform/values.yaml` | provider を `keycloak` へ。暫定コメントを解消記述へ |
| 3 | `deploy/docker-compose.yml` | 同上（**併せて誤 ID `IADR-0298` → `IADR-0301` を是正**） |
| 4 | `deploy/local/vault/eso/externalsecret-identity-admin-oidc.yaml` | 新規（`bff-oidc` と同型） |
| 5 | `deploy/local/vault/eso/bootstrap.sh` | Vault へ seed |
| 6 | `scripts/k8s-local-up.sh` | `ESO != 1` の手動 apply ／ ESO=1 の ES apply ／ eso_wait ／ 案内行 |
| 7 | `scripts/k8s-local-up.test.js` | 上の 4 点の回帰試験 |
| 8 | `.../IdentityAdminRegistration.cs` | 「まだ realm に無い」の根拠更新＋ fail-closed |
| 9 | `.../KeycloakIdentityAdminClient.cs` | 「実 Keycloak との疎通は未検証」の是正 |
| 10 | `.../Tests/KeycloakIdentityAdminClientTests.cs` | 同上 |
| 11 | `.../Tests/TestDatabaseConfiguration.cs` | fail-closed の許可集合との対応を注記（**環境の宣言は器が持つので足さない**） |
| 12 | `src/knowledge/.../Fixtures/IntegrationTestFactory.cs` | 同上（基底が `Integration` を宣言済み） |
| 13 | `docs/screens/SC-17_user-account-management.md` | 「配備の既定が偽の身元プロバイダ」の是正・未決事項 2 の解消 |
| 14 | `docs/tests/SC-17_user-account-management.md` | fail-closed のケースを追加 |
| 15 | `docs/security/security.md` | dev 平文シークレットの列挙へ新クライアントを追加 |
| 16 | `.ai-context/adr/IADR-0321_*.md` | 新規（決定の記録） |

**除外したもの（理由つき。黙って落とさない）**

| 除外 | 理由 |
| --- | --- |
| `.ai-context/adr/IADR-0301_*.md` の本文（97 / 101 / 162 行） | **凍結記録の本文プロズは書き換えない**（`CLAUDE.md` の主従）。決定 3 の暫定が解けたことは IADR-0321 に書き、IADR-0301 には日付つき追記ブロックで**後継 ID を隣に置く**だけにする |
| `.ai-context/specs/20260829_issue-452_*.md`・`20260829_issue-1044_*.md`・`20260828_issue-438_*.md` | 確定済みの作業仕様書。当時の実測として正しい。追記は不要（本書が後継） |
| `src/.../Features/Users/*/Endpoint.cs`（各 1 件） | `IdentityAdmin` の語は DI 越しの利用であり、暫定の記述を持たない |
| `src/.../Tests/IdentityAdminContractTests.cs`（31 件） | 抽象の形（新規作成の口が無いこと）を固定する試験。provider 選択とは独立 |
| `scripts/seed-abac-policies.js` / `seed-search-documents.js` | `abac-seeder` を使う投入器。新クライアントとは別主体（IADR-0301 決定 2「取り込み経路とは別のクライアントにする」） |
| `docs/api/openapi.yaml` / `docs/api/BFF_bff-surface.md` | `/bff/authz/users*` の**契約**は変わらない（provider 差し替えは後段の実装差） |
| AST（`src/ai-stock-trading`） | submodule。本リポジトリの規約の対象外 |

## 設計

### 1. realm クライアント `identity-admin`

```jsonc
{
  "clientId": "identity-admin",
  "publicClient": false,
  "standardFlowEnabled": false,      // 人はログインしない
  "directAccessGrantsEnabled": false, // #438 検査 5（MFA 迂回の禁止）
  "serviceAccountsEnabled": true,
  "secret": "identity-admin-dev-secret-change-me", // dev 専用の既知プレースホルダ
  "defaultClientScopes": ["roles"]
}
```

service account へは **`realm-management` のクライアントロール 3 つだけ**を与える
（IADR-0301 決定 2 が既に確定済み。realm ロールは 1 つも与えない）。

```jsonc
{
  "username": "service-account-identity-admin",
  "serviceAccountClientId": "identity-admin",
  "clientRoles": { "realm-management": ["view-users", "manage-users", "view-realm"] }
}
```

**必要最小である根拠（受け入れ基準 3）** —— コードが叩く端点との対応:

| 操作 | 端点 | 要るロール |
| --- | --- | --- |
| 一覧・ロールマッピングの読み | `GET /users`, `GET /users/{id}/role-mappings/realm` | `view-users` |
| 割当可能ロールの列挙 | `GET /roles` | `view-realm` |
| 属性更新・`enabled` 切替・ロール付け外し・セッション失効 | `PUT /users/{id}`, `POST|DELETE /users/{id}/role-mappings/realm`, `POST /users/{id}/logout` | `manage-users` |

`manage-realm` / `manage-clients` / `create-client` / `impersonation` / `manage-authorization` は
**与えない**。与えていないことは稼働クラスタで陰性対照として実測する（クライアント作成が 403 になること）。

🔴 **`authenticationFlows` を宣言しない**（IADR-0197 / #438 が踏んだ罠）。

### 2. シークレットの供給（受け入れ基準 4）

`bff-oidc`（#1114 / #1107）と同型:

- Vault `secret/msp/identity-admin-oidc`（キー `client-secret`）
- `ExternalSecret`（`creationPolicy: Owner`・MSP ns・target `identity-admin-oidc`）
- `ESO != 1` のときだけ `k8s-local-up.sh` が手動 apply（二重所有回避）
- helm は **非 optional** な `secretKeyRef` で読む → 注入漏れは Pod 起動失敗になる

### 3. fail-closed（受け入れ基準 6 への回答）

**`in-memory` を選べるのは非配備ホストのときだけにする**（許可集合 `{Development, Testing,
Integration}`）。それ以外 —— 環境変数を与えない配備が必ずなる `Production` を含む —— は起動時例外。

- **なぜ「警告ログの強化」ではないか**: 警告 1 行は運用が見落とす（#1101 の指摘）。実害は
  「保存が成功したように見えて認可が変わらない」であり、**気づけない**ことが欠陥の本体である。
- **なぜ許可集合（deny by default）か**: 否定形（Production / Staging を弾く）にすると、環境名を
  `Prod` などと書いた配備が素通りする。
- **なぜ dev の利便を壊さないか**: `dotnet run` は Development、各サービスの
  `TestWebApplicationFactory` は `Testing`、`Knowledge.IntegrationTests` の器は `Integration` を
  **既に宣言済み**であり、3 つとも許可集合に載る（**器へ 1 行も足さない**。3 つすべてで通ることを
  陽性対照で固定する）。
- **compose / k8s は `keycloak` へ移す**ので、この規則に触れない。

### 4. 稼働クラスタでの検証手順（受け入れ基準 1・2）

1. realm ConfigMap を再作成 → Keycloak を rollout restart（`PERSIST` 未設定なので毎回 re-import）。
   **届いたことを `kcadm get clients` / `get users -q username=service-account-identity-admin` で実測する。**
2. `identity-admin-oidc` Secret を作り、helm を `IdentityAdmin__Provider=keycloak` で upgrade。
3. **陽性対照**: `abac-seeder` の client_credentials トークン（`platform-admin`）で
   `GET /authz/users` を呼び、**稼働 realm の実在利用者**が返ることを見る。
4. **陰性対照**: 実在しない利用者 ID へ `PUT /authz/users/<bogus>/attributes` → **404**。
   （陽性だけでは「常に空を返す」実装と区別できない。逆に陰性だけでは「常に 404」と区別できない。）
5. **書き込みの陽性対照**: `POST /authz/users/{id}/disable` → Keycloak 側で `enabled=false` を
   `kcadm` で確認 → `enable` で戻す。

## 受け入れ基準（issue #1101 の転記と対応）

| # | 基準 | 満たし方 |
| --- | --- | --- |
| 1 | realm に `realm-management` 3 ロールを持つ機密クライアントが 1 つある | realm JSON ＋ 稼働 realm の `kcadm` 出力 |
| 2 | シークレットが Vault → ExternalSecret → Secret に乗る | `externalsecret-identity-admin-oidc.yaml`（realm JSON へ平文の本番値を書かない） |
| 3 | 配備の env が `keycloak` ＋ 4 キー | helm values ＋ 稼働 Pod の env 出力 |
| 4 | SC-17 から無効化 → Keycloak 側で `enabled=false` | 実測出力を PR に貼る |
| 5 | 無効化した利用者の既存セッションが 401 | 実測できなければ「できなかった」と書き #439 へ残す |
| 6 | `in-memory` を本番相当で起動失敗にするか判断 | **する**（§設計 3）。dev は Development 宣言で従来どおり |
| 7 | `check-realm-constraints.js` が通る | 実行して確認（サービスアカウントは検査 5 の除外に載る） |
| 8 | `dotnet build` / `dotnet test` | 実行して確認（skip 件数は内訳を出す） |

## テスト

| ID | 前提 | 操作 | 期待 |
| --- | --- | --- | --- |
| T-1 | provider=`in-memory` ＋ 非配備ホスト 3 種 | DI を組む | 偽物が解決される（従来どおり。陽性対照） |
| T-2 | provider=`in-memory` ＋ 配備ホスト（Production / Staging / `Prod`） | DI を組む | **起動時例外**（陰性対照。T-1 と対） |
| T-3 | provider=`keycloak` ＋ 環境 Production ＋ 4 キー | DI を組む | 実プロバイダが解決される |
| T-4 | realm JSON | クライアントを引く | `identity-admin` が service account 有効・`realm-management` 3 ロール・`directAccessGrantsEnabled` 偽 |
| T-5 | realm JSON | 同上 | **過剰権限（`manage-realm` 等）を 1 つも持たない**（陰性対照） |
| T-6 | `k8s-local-up.sh` | ESO=1 / 未設定 | 供給元がちょうど 1 つになる |

## 実測して分かった追加事項（着手後・稼働クラスタ）

**スタブでは絶対に出ない罠が 3 つ出た。** いずれも「成功を返して静かに壊す」型である。
決定と論拠は IADR-0321 決定 2〜4。

| # | 症状（実測） | 直し方 |
| --- | --- | --- |
| A | `realm-management` の 3 ロールを service account に持たせても `GET /admin/realms/platform/users` が **403**。トークンに `resource_access` が無い。本 realm は `clientScopes` を明示宣言しており（Issue #88 の経緯）**組み込みスコープが生成されない**ため、宣言済みの `roles` スコープは realm ロールしか載せない | クライアントスコープ `realm-management-roles` を新設し、`identity-admin` にだけ付ける（既存 `roles` は触らない＝他クライアントのトークンを太らせない） |
| B | `PUT /users/{id}` に `{"enabled": false}` だけを送ると **`firstName` / `lastName` / `email` が実際に消えた**（204 が返るので気付けない） | `UpdateAndReloadAsync` を read-modify-write にする。サーバ計算の派生値（`access` / `disableableCredentialTypes` / `userProfileMetadata`）は送り返さない |
| C | ABAC 属性の書き込みが **204 を返しながら no-op**。`clearance` / `department` は user profile の unmanaged 属性で、Keycloak 24 の既定では書き込みが捨てられる | realm へ `unmanagedAttributePolicy: ADMIN_EDIT` を宣言（`components`）。さらに**書き戻して読み直し、食い違ったら例外**にする（`EnsureAttributesWereApplied`） |

🔴 **`components` を宣言しても署名鍵の自動生成は止まらないことを実測で確かめた**
（`clientScopes` / `authenticationFlows` と同じ罠を疑って測った。JWKS は生成され、
client_credentials も通った）。

**分割起票はしなかった。** A〜C を分けて片方だけ出すと、`in-memory` より悪い状態
（利用者の姓名とメールが消える／属性が黙って捨てられる）を配備することになるためである。

## リスク

- **Keycloak の再起動**で稼働中のセッションが失効する（dev なので許容。`PERSIST` 未設定＝もともと再起動で消える）。
- **`kcadm` の多重 exec は OOM を招く**（前科あり）。1 回の exec に複数コマンドを束ね、回数を絞る。

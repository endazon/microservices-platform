---
title: 作業仕様書 — BFF セッションの構成を配備へ落とし、稼働クラスタでログインを成立させる（#1107）
type: spec
status: done
related_ids:
  - NFR
  - SC-13
  - ADR-0026
  - ADR-0032
  - IADR-0251
  - IADR-0273
  - IADR-0316
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
related_specs:
  - "20260822_issue-439_bff-session-token-handler.md"
  - "20260823_issue-439_bff-session-completion.md"
issue: "#1107"
---

# 作業仕様書 — BFF セッションの構成を配備へ落とす（#1107）

## 目的と射程

`IADR-0251` / `IADR-0273` が実装した BFF セッション（Token Handler）は、**構成が配備へ 1 行も
落ちていない**。`BffSessionOptions.ClientSecret` の既定は空文字なので、BFF はコンフィデンシャル
クライアントとして認証できず、`GET /bff/auth/login` が 500 で落ちる。**コードは在るが配備が無い。**

射程は**配備の構成だけ**である。`Platform.Bff` の製品コードは変えない（変える必要が出たら理由を
PR へ書く）。realm JSON も変えない（#1088 とファイル領域が交差するため。かつ**変える必要が無い**
ことを下の実測で確かめた）。

## 着手前の実測（稼働 k3s・2026-08-30・`develop` `6907d145`）

エッジは Istio Ingress Gateway（#1109 がクラスタへ適用済み・develop へは未着地）。**`-k` を使わず、
クラスタの CA（`cert-manager/local-edge-root-ca`）を `--cacert` に与えて測った**（Windows の curl は
schannel なので失効確認だけ `--ssl-revoke-best-effort` で緩めた。**チェーンとホスト名の検証は有効**）。

| 測ったこと | 結果 |
| --- | --- |
| `GET https://localhost/bff/auth/login` | **500**（`server: istio-envoy`） |
| `GET https://localhost/bff/auth/me` | 401（セッション無しの正しい応答） |
| `POST https://localhost/bff/auth/backchannel-logout` | **404** |
| BFF ログ | `OpenIdConnectProtocolException` … `GetPushedAuthorizationRequestUri` … `'Authentication failed.'` 401 |
| `bff-service` の env | `BffSession__*` が **0 件**（`envFrom` も無し） |
| `kubectl get secret -n microservices-platform` | BFF の client secret に当たる Secret が**無い** |

### 受け皿の側（欠けているのは配線だけ）

| 確かめたこと | 実測 |
| --- | --- |
| 稼働 realm の `bff` クライアント | `publicClient=false` / `secret=bff-dev-secret-change-me` / `redirectUris` に `https://localhost/bff/auth/callback` / `backchannel.logout.url=https://localhost/bff/auth/backchannel-logout`（**realm 側は完全。手を入れる必要が無い**） |
| Redis | `platform-infra/redis` 稼働。MSP ns に ExternalName `redis` があり、**BFF pod から `redis:6379` へ TCP 到達できた**（コード既定 `redis:6379` がそのまま効く） |
| Keycloak の discovery（**in-cluster から引いたとき**） | `issuer` / `authorization_endpoint` / `end_session_endpoint` は **`https://keycloak.localhost/...`**（ブラウザ到達先）、`token` / `par` / `userinfo` / `jwks` は **`http://keycloak:8080/...`**（サーバ間）。`KC_HOSTNAME_URL=https://keycloak.localhost` がこの分離を作っている |
| realm のセッション寿命 | `rememberMe=true` / `ssoSessionIdleTimeoutRememberMe=2592000` / `ssoSessionMaxLifespanRememberMe=2592000`（**コード既定 `SessionLifetimeSeconds=2592000` と一致**。稼働 realm でも同値） |

🔴 **この discovery の分離のおかげで、metadata を in-cluster の http で引くだけで「ブラウザは
エッジの https へ、BFF はクラスタ内の http へ」が自動的に成立する。** BFF コンテナはローカル
エッジ CA を信頼していない（`ca-certificates.crt` に不在を確認）が、**サーバ間の口が http なので
問題にならない**。

## 母集合の引き直し（[[IADR-0141]] 決定 1 / `traceability.repo.md` 規則 9・10）

issue 本文の「宣言ファイル領域」は母集合として使わない。**誤りの側から、軸を変えて 4 本引いた。**

| 軸 | 引き方 | 結果 |
| --- | --- | --- |
| 1（誤りの側） | `grep -rn "BffSession"`（`src/` と `.ai-context/` を除く全ファイル・拡張子で絞らない） | **0 件**（= 配備に 1 つも無い） |
| 2（正しく配備されている兄弟） | `Auth__Authority` / `authority:` を `deploy/` `scripts/` から | `deploy/docker-compose.yml`（`x-common-env`） / `deploy/helm/.../templates/deployment.yaml` / `deploy/helm/.../values.yaml` / `deploy/local/values-local.yaml` |
| 3（secret の供給経路） | `dev-secret-change-me` を持つ全ファイル | `scripts/k8s-local-up.sh` / `deploy/local/vault/eso/bootstrap.sh` / `deploy/keycloak/*-realm.json` ＋ 文書 6 件 |
| 4（パスから） | `deploy/**` のうち BFF へ env を与えるもの | helm values（`services.bff.extraEnv`） / helm template / values-local / compose |

**軸 2 が `deploy/docker-compose.yml` を拾ったことが効いた** —— issue の宣言ファイル領域に compose は
無いが、**compose も keycloak と realm を持つ配備経路**であり、そこも同じ理由で login が 500 になる。
**compose を母集合へ入れる。**

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `deploy/keycloak/microservices-platform-realm.json` | 稼働 realm を実測して**変更不要**と判った（secret の置き場・redirectUris・backchannel URL がすべて揃っている）。かつ #1088 とファイル領域が交差するので触らない |
| `src/platform/backend/Bff/**`（製品コード） | 欠けているのは配備であってコードではない。`BffSessionOptions` の既定は正しく働く |
| `src/ai-stock-trading/**` | submodule。MSP の BFF とは別 |
| `deploy/local/edge-istio/**` | develop に存在しない（#1109 の未着地分）。エッジの形は本件の原因ではない |
| frontend の `oidc.authority` / `clientId: platform-spa` | SPA 側の旧 public client 構成。ADR-0032 では SPA は OIDC をしないので**別件の残骸**であり、本件の射程外（#439 の残り） |

## 決めること / 決めたこと

### 何を注入し、何を既定へ委ねるか

| 構成値 | 判断 | 理由 |
| --- | --- | --- |
| `BffSession__ClientSecret` | **注入する**（Secret 経由） | 既定は空文字。**これが無いと必ず 500 になる**（本件の直接原因） |
| `BffSession__ClientId` | **注入する** | secret と対で「どの realm クライアントか」を配備の側に見せる。片方だけ在ると読み手が realm を探しに行くことになる |
| `BffSession__Authority` / `MetadataAddress` / `ValidIssuers` | **注入する**（`global.auth` から描画） | 兄弟の `Auth__*` と**同じ 1 か所**から描く。#780 で issuer をエッジ host へ移すとき、片方だけ動くことを防ぐ |
| `BffSession__RequireHttpsMetadata` | **注入しない** | プラットフォーム全体の姿勢が `RequireHttpsMetadata=false`（`Platform.Shared.Infrastructure/AuthExtensions.cs` が固定し `AuthExtensionsTests` が「クラスタ内は HTTP」として試験で留めている）。**コード既定 `false` がその姿勢と一致する。** ここだけ配備で別の値を持つと 2 つ目の真実ができる |
| `BffSession__RedisConnectionString` | **注入しない** | コード既定 `redis:6379` が k8s（ExternalName `redis`）でも compose（サービス名 `redis`）でも正しい。**BFF pod から実際に TCP 到達を確認した。** chart に `global.redis` は無く、注入すると接続先の 2 つ目の置き場を作ることになる |
| `BffSession__CookieName` | **注入しない** | コード既定 `__Host-msp-session` が `IADR-0251` / `docs/authz/bff-session-design.md` の要求そのもの。配備で変えられる値ではない（`__Host-` 接頭辞の条件と結びついている） |
| `BffSession__SessionLifetimeSeconds` | **注入しない** | 🔴 `IADR-0251` 決定 6 が「**数値を散文へ書き写さない。realm が唯一の情報源**」と決めている。**values.yaml へ書くことはまさにその複写である。** コード既定 2592000 が稼働 realm の remember-me 値と一致することを実測した |

### client secret の供給経路

**既存パターン（`minio-oidc` / `grafana-oidc` / `vault-oidc` / `headlamp-oidc`）と同型にする。**
新しいパターンを作らない（#1101 と解の形を揃えるという issue の制約）。

```
Vault secret/msp/bff-oidc (client-secret)
   → ExternalSecret bff-oidc (ESO=1)          … deploy/local/vault/eso/externalsecret-bff-oidc.yaml
   → k8s Secret microservices-platform/bff-oidc
   → env BffSession__ClientSecret             … helm templates/deployment.yaml
```

`ESO` 未使用のときは `scripts/k8s-local-up.sh` が同名 Secret を手で作る（他の `*-oidc` と同じ分岐）。
**dev 既定は realm の置き場と同値 `bff-dev-secret-change-me`**（realm と注入値が一致しないと PAR が
同じ 401 を返す）。実値の上書きは `BFF_OIDC_CLIENT_SECRET` env で行い、**リポジトリには置かない。**

### 再発検査

`CLAUDE.md`「同型の事故が 2 回起きたら検査を足す」の条件を満たす（1 回目 = #1025「実装済みだが
compose / helm / image-mapping のどれにも載っていない」）。**検査を足す。**

`scripts/check-secret-injected-options.js`（新規・外部依存ゼロ）:

- 母集合を**列挙で持たない**。backend の `*Options.cs` を走査し、**XML doc に
  「k8s Secret から環境変数で注入する」と自分で宣言しているプロパティ**を集める。
- 各プロパティについて `<SectionName>__<Property>` が **helm の values / template** と
  **compose** の両方に現れることを要求する。
- **0 件走査は fail-closed**（宣言の書式が変わって母集合が空になったら緑を返さない）。
- `--self-test` を持ち、CI（`static-checks`）で本体と共に走らせる。

## 変更するファイル

| ファイル | 変更 |
| --- | --- |
| `deploy/helm/microservices-platform/values.yaml` | `services.bff.session`（clientId / existingSecret / clientSecretKey）を追加 |
| `deploy/helm/microservices-platform/templates/deployment.yaml` | `session` を持つサービスへ `BffSession__*` を描画 |
| `deploy/docker-compose.yml` | `bff` の env へ `BffSession__ClientId` / `ClientSecret` / `MetadataAddress` |
| `deploy/local/vault/eso/bootstrap.sh` | `secret/msp/bff-oidc` の seed |
| `deploy/local/vault/eso/externalsecret-bff-oidc.yaml` | 新規（`minio-oidc` と同型） |
| `scripts/k8s-local-up.sh` | 非 ESO の `apply_secret` ／ ESO の `kubectl apply` |
| `scripts/check-secret-injected-options.js` | 新規（再発検査） |
| `scripts/README.md` / `.github/workflows/ci.yml` | 検査の登録 |
| `.ai-context/adr/IADR-0316_*.md` | 決定の記録 |
| `docs/authz/bff-session-design.md` | 「構成の供給」節を足す（値は書かない。**所在だけ**） |

## 受け入れ基準（実測で満たす）

- [ ] `curl https://<edge>/bff/auth/login` が **302** で Keycloak の認可端点へ向かう（500 でない）
- [ ] ログイン往復が完走し、`__Host-msp-session` が付き `/bff/auth/me` が 200 で身元を返す
- [ ] その応答のヘッダにもボディにも**トークンが現れない**（`IADR-0273` 決定 4 の否定形）
- [ ] `POST /bff/auth/backchannel-logout` が **404 でない**
- [ ] 上記が `scripts/k8s-local-up.sh` の経路で再現する（手 apply で直したことにしない）
- [ ] `node scripts/check-deploy-manifests.js` ほか検査群が緑
- [ ] シークレットの実値がコミットに現れない

## 実測（変更後・同じ稼働クラスタ・`scripts/k8s-local-up.sh` と同じコマンド列）

**適用**: `kubectl create secret generic bff-oidc … | kubectl apply -f -`（`apply_secret` と同じ）
→ `helm upgrade --install msp deploy/helm/microservices-platform -n microservices-platform -f deploy/local/values-local.yaml`
（[6/7] と同じ）。**手で `kubectl set env` して直したものは、この helm upgrade で上書きされ、
チャートだけで同じ結果が出ることを確かめてある。**

| 段 | 実測 |
| --- | --- |
| `GET /bff/auth/login` | **302** → `https://keycloak.localhost/…/auth?client_id=bff&request_uri=urn:ietf:params:oauth:request_uri:…` |
| 認可画面 | 200（Keycloak のログインフォーム） |
| 資格情報 POST | 302 → `…/login-actions/required-action?execution=CONFIGURE_TOTP&client_id=bff` |
| MFA（TOTP） | 302 → `https://localhost/bff/auth/callback?state=…&code=…` |
| `GET /bff/auth/callback` | **302 → `/`** ＋ `Set-Cookie: __Host-msp-session`。**応答ヘッダにトークン系の語 0 件** |
| `GET /bff/auth/me` | **200** `{"name":"poc-user","subject":"…","roles":[],"logoutUrl":"/bff/auth/logout?sid=…"}`。**ヘッダ＋ボディにトークン系の語 0 件** |
| `POST /bff/auth/backchannel-logout`（form ＋ 不正 `logout_token`） | **400**（BFF ログ「Backchannel logout rejected: token validation failed.」＝受け口が解決されている） |
| 同（空ボディ） | 404。OIDC ハンドラは「サインアウト要求の形をしていない」要求を掴まないためで、**Keycloak は必ず form POST で送る**（上の行が実経路） |

### 無効化 → 401（#439 の芯）を稼働クラスタで初めて測れた

| 時刻(UTC) | 出来事 |
| --- | --- |
| 14:28 | ログイン成立。`/bff/auth/me` = **200** |
| 14:29:18 | 管理者が当該利用者を無効化し、全セッションログアウトを実行 |
| 14:29:21 | `/bff/auth/me` = **200**（まだ生きている） |
| 14:33:05 | `/bff/auth/me` = **401**。BFF ログ: `Token refresh refused by the authorization server (400).` / `BFF session terminated: token endpoint refused the refresh.` |

**効いたのは refresh 拒否の経路（IADR-0273 決定 3）である。バックチャネルは届いていない。**
Keycloak 側のログ:

```
KC-SERVICES0057: Logout for client 'bff' failed:
  HttpHostConnectException: Connect to localhost:443 [localhost/127.0.0.1] failed: Connection refused
```

🔴 **realm の `backchannel.logout.url` はエッジ host（`localhost`）だが、pod の中では `localhost` は
pod 自身である。** `deploy/local/aliases/coredns-edge-hosts.yaml` の書き換えは `*.localhost` の
regex であり、**ドット無しの `localhost` は対象外**。**受け口は直ったが送り手が届かない。**
本件の射程外（realm JSON は #1088 と交差し、エッジ host の解決は #780 の着地形に依存する）。
**別 issue として起票する。**

### 測れなかったもの

| 何を | なぜ |
| --- | --- |
| `developer` でのログイン往復 | **段 3（資格情報）までは通った**（`<input name="otp">` の画面へ到達）。この利用者には別セッションの `verify-oidc-edge-flow.sh` が登録した TOTP 資格情報（`userLabel=verify-oidc-edge-flow`・当日作成）が既に在り、**2 回目以降の画面はシークレットを出さない**。稼働 Keycloak は H2 インメモリで、admin API も `secretData` を返さないためシークレットを復元できなかった。**代わりに `poc-user`（TOTP 未登録・`CONFIGURE_TOTP` 保持＝README が言う「初回ログイン」の状態）で往復を完走させた。** 測定後、作った OTP 資格情報を削除し `requiredActions` を戻して元の状態へ復元した |
| `node scripts/check-deploy-manifests.js` | `kubeconform` が PATH に無い（本検査は fail-closed）。**緑と言わない** |
| バックチャネル経由の即時失効 | 上記のとおり送り手が届かない。**「満たす」に丸めない** |

## 測れないかもしれないもの（丸めない）

- **「無効化 → 次のリクエストで 401」** は #439 の芯だが、本件の射程はログインの成立である。
  測れたら書き、測れなければ「測れなかった」と書いて #439 へ残す。**「満たす」に丸めない。**
- `kubeconform` が PATH に無ければ `check-deploy-manifests.js` は fail-closed になる。
  導入できなければ「測れなかった」と書く。

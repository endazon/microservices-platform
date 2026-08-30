---
title: IADR-0313 BFF セッションの構成を配備へ落とす — 注入するもの／既定へ委ねるもの、転送ヘッダ、注入漏れの検査
type: impl-adr
status: Accepted
related_ids: [NFR, SC-13, ADR-0021, ADR-0026, ADR-0032, IADR-0098, IADR-0251, IADR-0273]
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
related_specs:
  - ../specs/20260830_issue-1107_bff-session-deploy-config.md
---

# IADR-0313: BFF セッションの構成を配備へ落とす（#1107）

> 実装リポジトリ内の意思決定記録。[IADR-0251](./IADR-0251_bff-session-token-handler.md)（3a の内部設計）と
> [IADR-0273](./IADR-0273_bff-session-completion.md)（失効経路）が作った受け皿を、**稼働する配備へ載せる**
> ための決定を記録する。運用手順は [`docs/authz/bff-session-design.md`](../../docs/authz/bff-session-design.md) が持つ。

- 状態: Accepted
- 日付: 2026-08-30
- 決定者: Claude（実装）

## 前提の実測（稼働 k3s・`develop` `6907d145`）

**コードは在るが配備が無い。** 稼働クラスタで測った結果は次のとおりだった。

| 測ったこと | 結果 |
| --- | --- |
| `GET https://localhost/bff/auth/login` | **500** |
| BFF ログ | `OpenIdConnectProtocolException` … `GetPushedAuthorizationRequestUri` … `'Authentication failed.'` 401 |
| `bff-service` の `BffSession__*` | **0 件** |
| `POST /bff/auth/backchannel-logout` | **404**（ハンドラが起動していないのでパスが解決されない） |

🔴 **単体テストは構成を自分で与えて走るので、この欠落では絶対に落ちない**（`BffSessionConfigurationTests` /
`BffSessionFlowTests` はどちらも `["BffSession:ClientSecret"] = "test-secret"` を置く）。

## 決定 1: 注入するのは 4 値だけにし、残りはコード既定に委ねる

**注入する**: `ClientSecret`（Secret 経由）/ `ClientId` / `Authority` / `MetadataAddress` / `ValidIssuers`
（後 3 者は `Auth__*` と**同じ `global.auth` から描く**）。

**注入しない**ものと理由を、値ごとに残す。**「既定で良さそう」では済ませない** —— IADR-0251 は
「ここで上書きしている既定値は、すべて実測して既定では要件を満たさないと判ったものである」と書いている。
その裏返しとして、**既定のままにする判断にも実測を要求する。**

| 値 | 委ねてよい理由（実測） |
| --- | --- |
| `RequireHttpsMetadata` | プラットフォーム全体の姿勢が `false` である。`Platform.Shared.Infrastructure` の `AuthExtensions` が JwtBearer 側でこれを固定し、`AuthExtensionsTests`「クラスタ内は HTTP」が試験で留めている。**ここだけ配備で別に持つと 2 つ目の真実ができる** |
| `RedisConnectionString` | 既定 `redis:6379` が k8s（MSP ns の ExternalName `redis`）でも compose（サービス名 `redis`）でも正しい。**BFF pod から実際に TCP 到達を確認した。** chart に `global.redis` は無く、注入すると接続先の 2 つ目の置き場を作る |
| `CookieName` | 既定 `__Host-msp-session` が IADR-0251 の要求そのもの。`__Host-` 接頭辞は Secure ＋ Path=/ ＋ Domain 無しと結びついており、**配備で差し替えてよい値ではない** |
| `SessionLifetimeSeconds` | 🔴 **IADR-0251 決定 6 が「数値を散文へ書き写さない。realm が唯一の情報源」と決めている。`values.yaml` へ書くことは、まさにその複写である。** 稼働 realm の `ssoSessionIdleTimeoutRememberMe` / `ssoSessionMaxLifespanRememberMe` が既定 2592000 と一致することを実測した |

## 決定 2: 転送ヘッダを解釈させる。**client secret を入れただけでは往復は通らない**

🔴 **実測（本 issue で最も重要な発見）。** client secret を注入した直後、PAR の応答は
**401（client 認証失敗）から 400（`invalid_request: Invalid parameter: redirect_uri`）へ変わった。
500 のままである。**

原因は scheme である。エッジ（Istio Ingress Gateway）が TLS を終端し、BFF へは平文 http で渡る。
ASP.NET は受信リクエストの scheme から `redirect_uri` を組むため `http://<edge>/bff/auth/callback` を送る。
realm に登録があるのは **https** の方なので Keycloak が弾く。**同じ 500 の裏に、別の原因が 2 段重なっていた。**

切り分けの実測（in-cluster から PAR を直接叩いた）:

```
http://localhost/bff/auth/callback  -> 400 {"error":"invalid_request","error_description":"Invalid parameter: redirect_uri"}
https://localhost/bff/auth/callback -> 201 {"request_uri":"urn:ietf:params:oauth:request_uri:…","expires_in":60}
```

**採った解**: `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` を **BFF にだけ**与える。ASP.NET Core が
標準で持つ口で、`X-Forwarded-Proto` / `-For` を解釈し、既定の loopback 限定 `KnownProxies` を外す。

**採らなかった案**:

| 案 | 却下の理由 |
| --- | --- |
| realm へ `http://localhost/bff/auth/callback` を足す | **平文の callback を認可サーバに許すことになる。** ADR-0032 は Cookie に Secure を要求しており逆行する。かつ realm JSON は #1088 と交差する |
| BFF のコードで `redirect_uri` を固定する | 配備ごとにエッジ host が変わる。**構成の問題をコードへ押し込むことになる** |
| 全サービスへ転送ヘッダを与える | 絶対 URL を組んでブラウザへ返すのは BFF だけである。**転送ヘッダの信頼範囲を理由なく広げない** |

**信頼範囲についての明示**: `KnownProxies` を外すのは「メッシュ内から `X-Forwarded-Proto` を詐称できる」
ことを許すのと同義である。それでも採るのは、**詐称できても `redirect_uri` は realm の許可リストで
止まる**ためで、影響は「ログイン開始が失敗する」に留まる。ネットワーク側の制限（NetworkPolicy /
`edge.bff` のルート）が本来の壁であり、本 env はその壁を置き換えるものではない。

## 決定 3: client secret の供給は `*-oidc` の既存パターンへ寄せる。新しい形を作らない

`Vault secret/msp/bff-oidc` → `ExternalSecret bff-oidc`（ESO=1）→ k8s Secret → env、
`ESO` 未使用なら `scripts/k8s-local-up.sh` が同名 Secret を作る。**IADR-0098 の
`minio-oidc` / `grafana-oidc` / `vault-oidc` / `headlamp-oidc` と同型で、分岐の形も揃える。**

dev 既定は realm import の置き場と同値（`bff-dev-secret-change-me`）である。**ズレると PAR が同じ
401 を返す**ので、「realm 側の置き場」と「注入値」は同じ既定を共有させ、上書きは
`BFF_OIDC_CLIENT_SECRET` の 1 本で両方へ効かせる（bootstrap の seed と手動 apply が同じ env を読む）。

**helm では `secretKeyRef` を optional にしない。** 注入漏れが「空 secret で起動して、ログインの時だけ
500」へ倒れると、Pod は Ready のままで誰も気づけない —— #1012 / #1022 が DB とブローカで採ったのと
同じ姿勢である（**注入漏れは起動失敗にする**）。

## 決定 4: 注入漏れの再発は、コード側の宣言と配備の突合で止める

`CLAUDE.md`「同型の事故が 2 回起きたら検査を足す」の条件を満たす（1 回目 #1025・2 回目 本件）。
`scripts/check-secret-injected-options.js` を置く。

**列挙を持たない。** 母集合は **コードの側の宣言**から導く —— `*Options.cs` の XML doc に
「実値は k8s Secret から環境変数で注入する」と書かれたプロパティを集め、`<Section>__<Property>` が
helm（`secretKeyRef` 由来）と compose（`${...}` 展開）の**両方**に在ることを要求する。
**0 件走査は fail-closed**（宣言の語が変わって母集合が空になったら緑を返さない）。

**なぜ「両方」か。** 母集合を引き直したとき、issue 本文の「宣言ファイル領域」に compose が無かった。
helm だけ直すと **compose の BFF は同じ 500 のまま残る**（compose も同じ realm を import している）。

**この検査が捕まえないもの**（IADR-0249 と同じく「捕まえないこと」を書く）: 値の**正しさ**は見ない。
realm の secret と注入値がズレていても検査は緑である（realm 側は稼働 Keycloak にしか無く、静的には
突合できない）。**ズレは PAR の 401 として現れるので、疎通の側で見る。**

## 実測（この変更の後・同じクラスタ）

| 段 | 実測 |
| --- | --- |
| `GET /bff/auth/login` | **302** → `https://keycloak.localhost/…/auth?client_id=bff&request_uri=urn:ietf:params:oauth:request_uri:…`（PAR 成立） |
| 認可画面 → 資格情報 → MFA（TOTP） | 302 → `https://localhost/bff/auth/callback?state=…&code=…` |
| `GET /bff/auth/callback` | 302 → `/`、`Set-Cookie: __Host-msp-session` |
| `GET /bff/auth/me` | **200**（`name` / `subject` / `roles` / `logoutUrl`）。**ヘッダにもボディにもトークンは現れない**（IADR-0273 決定 4 の否定形を実物で確認） |
| `POST /bff/auth/backchannel-logout`（form ＋ 不正 `logout_token`） | **400**（`BackchannelLogoutProcessor` が「token validation failed」で拒否＝**受け口が解決されている**） |

## 申し送り: バックチャネル失効は「受け口」は直ったが「送り手」が届かない

realm の `backchannel.logout.url` は `https://localhost/bff/auth/backchannel-logout` である。
利用者を無効化して全セッションログアウトさせたときの Keycloak のログ:

```
KC-SERVICES0057: Logout for client 'bff' failed:
  HttpHostConnectException: Connect to localhost:443 [localhost/127.0.0.1] failed: Connection refused
```

🔴 **pod の中では `localhost` は pod 自身である。** エッジ host を pod から解決させる仕組み
（`deploy/local/aliases/coredns-edge-hosts.yaml`）は `*.localhost` の **regex 書き換え**であり、
**ドット無しの `localhost` は対象外**である。したがって受け口を直しても、送り手が届かない。

**本 IADR では直さない。** realm JSON の変更は #1088（realm の変更が稼働クラスタへ届かない）と
ファイル領域が交差しており、かつ「エッジ host を pod からどう解決させるか」は #780 の着地形に依存する。
**別 issue として起票し、#439 の「無効化 → 全セッション即時失効」の実測はそこへ残す。**

なお、失効の第 2 経路（refresh 拒否 → セッション破棄。IADR-0273 決定 3）は
バックチャネルが届かなくても効くため、**無効化の反映はアクセストークンの寿命（realm 実測 300 秒）
以内には起きる**。ここは実測で確かめる（結果は作業仕様書と PR に載せる）。

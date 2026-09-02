---
title: バックチャネルログアウトの宛先を pod から届く形へ与える（realm の裸 localhost を是正する）
type: spec
status: draft
related_ids: [NFR, SC-13, ADR-0026, ADR-0032, IADR-0076, IADR-0103, IADR-0227, IADR-0251, IADR-0273, IADR-0307, IADR-0317]
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_security-requirements.md
---

# 仕様書: issue #1115 — バックチャネルログアウトの「送り手」の宛先

> 受け口（`RemoteSignOutPath` ＋ `OnRemoteSignOut`）は #1107 / #1114 で解決済みである。
> 壊れているのは **Keycloak 側 realm が持つ宛先だけ**であり、`Platform.Bff` のコードは変えない。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: —（NFR: セキュリティ / セッション管理）
- ユースケース（UC）: —
- 画面（SC）: SC-13
- 関連 ADR: ADR-0026（監査・セキュリティ要求）, ADR-0032（SPA 認証 = BFF セッション）, ADR-0005（mTLS）, ADR-0021（入口 = Istio Ingress Gateway）
- 実装 ADR: IADR-0251 / IADR-0273（BFF セッション）, IADR-0076・IADR-0227（エッジ host）, IADR-0103（誤解決の遮断）, IADR-0307・IADR-0317（Istio / STRICT mTLS）, IADR-0328（エッジ issuer 追随）

## 目的・背景

realm の `bff` クライアントは `backchannel.logout.url = https://localhost/bff/auth/backchannel-logout`
を持つ。**pod の中では `localhost` は pod 自身**であり、Keycloak は自分の :443 へ POST して
`Connection refused` になる。結果として ADR-0032 が要求する **即時**失効（無効化・退職時の全セッション
失効）が配備した状態では一度も成立せず、実際に効いているのは refresh 拒否の第 2 経路
（IADR-0273 決定 3）だけ ——**アクセストークンの寿命（realm 実測 300 秒）ぶん遅れる**。

## 対象範囲

- 対象: realm の `backchannel.logout.url`、その値を稼働 realm へ実際に反映する経路、
  Keycloak（メッシュ外）から BFF（STRICT mTLS 下）へ**到達させる**ための境界の宣言、静的検査。
- 対象外: `Platform.Bff` のコード（issue の明示制約）。エッジ host の再設計（#780 は着地済み）。
  Keycloak の永続化（#1088）。CoreDNS 書き換え規則の拡大（後述のとおり**採らない**）。

## 母集合（規則 9・10 —— 誤りの側の語で走査した結果と除外理由）

走査は追跡下のファイル（`git ls-files`）に対して行った。submodule（`src/ai-stock-trading`）は
別リポジトリの realm を持つため対象外。

### 走査 1: `backchannel`（大小無視）

```console
$ git ls-files -z | xargs -0 grep -ril "backchannel"
.ai-context/adr/IADR-0251_bff-session-token-handler.md
.ai-context/adr/IADR-0273_bff-session-completion.md
.ai-context/adr/IADR-0301_sc17-identity-admin-abstraction.md
.ai-context/adr/IADR-0316_bff-session-deploy-config.md
.ai-context/specs/20260823_issue-439_bff-session-completion.md
.ai-context/specs/20260828_issue-439_sc16-account-settings.md
.ai-context/specs/20260829_issue-452_sc17-user-account-management.md
.ai-context/specs/20260830_issue-1107_bff-session-deploy-config.md
deploy/keycloak/microservices-platform-realm.json
docs/authz/bff-session-design.md
src/platform/backend/Bff/Platform.Bff.Tests/BackchannelLogoutTests.cs
src/platform/backend/Bff/Platform.Bff/Foundation/Session/BackchannelLogoutProcessor.cs
src/platform/backend/Bff/Platform.Bff/Foundation/Session/BffSessionExtensions.cs
src/platform/backend/Services/AuthorizationService/Domain/Ports/IIdentityAdminClient.cs
src/platform/backend/Services/AuthorizationService/Features/Users/DisableUser/Endpoint.cs
src/platform/backend/Services/AuthorizationService/Infrastructure/ExternalServices/KeycloakIdentityAdminClient.cs
```

| ファイル | 扱い |
| --- | --- |
| `deploy/keycloak/microservices-platform-realm.json` | **直す**（宛先の唯一の宣言） |
| `docs/authz/bff-session-design.md` | **直す**（運用手順の正本。宛先と境界の注記を足す） |
| `.ai-context/adr/*`（4 件） | 触らない（凍結記録。本件は新 IADR で継ぐ） |
| `.ai-context/specs/*`（4 件） | 触らない（確定済み記録） |
| `src/platform/backend/Bff/**`（3 件） | 触らない（受け口。issue の明示制約） |
| `src/.../AuthorizationService/**`（3 件） | 触らない（無効化 API 側。送り手は Keycloak であり本件の射程外） |

### 走査 2: 裸の `localhost` を宛先に持つ**サーバ間**の口

`https?://localhost` は追跡下に多数ある（README・仕様書・compose・realm の redirect URI 等）。
そのほとんどは**ブラウザが開く URL**であり、裸の `localhost` が正しい。誤りなのは
「**pod / コンテナの中の何かが接続先として使う** URL」だけなので、realm 側では
Keycloak がサーバとして自ら叩く属性に絞って走査した。

```console
$ grep -n "adminUrl\|rootUrl\|baseUrl\|managementUrl" deploy/keycloak/microservices-platform-realm.json
（0 件）
$ grep -n "backchannel.logout.url" deploy/keycloak/microservices-platform-realm.json
335:        "backchannel.logout.url": "https://localhost/bff/auth/backchannel-logout",
```

**陽性対照**: 同じ走査語で `redirectUris` / `webOrigins` / `post.logout.redirect.uris` は 20 行以上
当たる（＝走査そのものは効いている）。それらは**ブラウザ向けで是正対象ではない**。
したがって realm におけるサーバ間の口は **1 行**（335 行目）である。

`deploy/docker-compose.yml` は同じ realm ファイルを import する**第 2 の配備経路**であり、
そこでも同じ 1 行が使われる（compose の BFF は `bff:8080`）。**片方だけ直すと compose 側に穴が残る**
ため、両経路で解決できる値を選ぶ（設計 決定 1）。

## 着手前の実測（2026-09-02・稼働 k3s / Rancher Desktop 内蔵・Istio STRICT）

`kubectl` は `C:/Program Files/Rancher Desktop/resources/resources/win32/bin/kubectl`。
HTTPS はクラスタ CA（`cert-manager/local-edge-root-ca`）を `--cacert` に与えて測った（`-k` は使わない）。

### (a) 稼働 realm の現在値

```console
$ kubectl -n platform-infra exec deploy/keycloak -- ./kcadm.sh get clients/<bff の id> -r platform
  "attributes" : {
    "post.logout.redirect.uris" : "https://localhost/*##http://localhost:3100/*##http://localhost:5000/*",
    "backchannel.logout.url" : "https://localhost/bff/auth/backchannel-logout",
    "backchannel.logout.session.required" : "true"
  },
```

### (b) Keycloak pod から見た名前解決 —— 🔴 **issue の想定より 1 段深い**

```console
$ kubectl -n platform-infra exec deploy/keycloak -- cat /etc/hosts
127.0.0.1	localhost
::1	localhost ip6-localhost ip6-loopback
...
$ kubectl -n platform-infra exec deploy/keycloak -- sh -c "getent ahostsv4 localhost"
127.0.0.1       STREAM localhost
```

**裸の `localhost` は `/etc/hosts` で決まる。** CoreDNS には一度も届かないので、
**書き換え規則をどう広げても裸の `localhost` は pod 自身のままである**（issue の選択肢 2 が
「危険」なのではなく、**そもそも効かない**）。

さらに `*.localhost` も Keycloak からは引けない:

```console
$ kubectl -n platform-infra exec deploy/keycloak -- sh -c \
    "for n in keycloak.localhost grafana.localhost app.localhost; do printf '%-24s ' $n; getent hosts $n || echo '(NXDOMAIN)'; done"
keycloak.localhost       (NXDOMAIN)
grafana.localhost        (NXDOMAIN)
app.localhost            (NXDOMAIN)
```

**陽性対照（同じ resolv.conf・同じ CoreDNS）:**

```console
$ kubectl -n platform-infra exec deploy/keycloak -- sh -c "getent ahostsv4 bff-service.microservices-platform.svc.cluster.local"
10.43.118.140   STREAM bff-service.microservices-platform.svc.cluster.local
$ kubectl -n platform-infra exec deploy/grafana -- sh -c "getent hosts keycloak.localhost"   # alpine(musl)
10.43.255.240     keycloak.localhost  keycloak.localhost
```

**決定的な対照 —— 同じ pod の同じ netns で libc だけを替える**（ephemeral container を
Keycloak pod へ足して測った。pod は再起動していない）:

```console
$ kubectl -n platform-infra debug pod/keycloak-… --image=grafana/grafana:11.0.0 --target=keycloak --container=probe1115 -- sleep 900
$ kubectl -n platform-infra exec keycloak-… -c probe1115 -- sh -c "getent hosts keycloak.localhost; getent hosts app.localhost"
10.43.255.240     keycloak.localhost  keycloak.localhost
10.43.255.240     app.localhost  app.localhost
```

🔴 **CoreDNS は正しく答えている。引けないのは Keycloak イメージの libc（UBI9 / glibc 2.34）である。**
同 netns の musl は同じ名前を解決する。**したがって「エッジ host をドット付きにすれば届く」
（issue の選択肢 3・#780 の着地待ち）も Keycloak には効かない。**

### (c) メッシュ境界 —— in-cluster の宛先でも平文では入れない

```console
$ kubectl -n platform-infra exec deploy/grafana -- curl -sS -X POST --max-time 10 \
    http://bff-service.microservices-platform.svc.cluster.local:8080/bff/auth/backchannel-logout
curl: (56) Recv failure: Connection reset by peer
```

`microservices-platform` の `PeerAuthentication` は `STRICT`（helm `mesh.mtlsMode` の宣言どおり）で、
**platform-infra はメッシュ外（サイドカー無し・1/1）**である。Envoy が平文流入を落とす。
**issue の選択肢 1 は「宛先を書き換えるだけ」では成立しない。**

### (d) 陰性（現状の失効） —— 全セッションログアウトで BFF セッションは死なない

```console
[2026-09-02 10:05:38Z] GET  /bff/auth/login       -> 302
[2026-09-02 10:06:09Z] GET  /bff/auth/me          -> 200 {"name":"poc-user","subject":"b749c695-…","roles":[],"logoutUrl":"/bff/auth/logout?sid=4e03cfd1-…"}
[2026-09-02 10:06:43Z] disable user b749c695-…
[2026-09-02 10:06:45Z] logout all sessions
[2026-09-02 10:06:48.456Z] GET /bff/auth/me -> 200 {"name":"poc-user",…}   ← 3 秒後もまだ生きている
```

```console
$ kubectl -n platform-infra logs deploy/keycloak -c keycloak --since=3m | grep -A3 KC-SERVICES0057
2026-09-02 10:06:48,216 WARN  [org.keycloak.services] (executor-thread-1) KC-SERVICES0057: Logout for client 'bff' failed:
  org.apache.http.conn.HttpHostConnectException: Connect to localhost:443 [localhost/127.0.0.1, localhost/0:0:0:0:0:0:0:1] failed: Connection refused
```

BFF 側のログは同時刻に **0 行**。**陽性対照**（ログが観測可能であることの担保）:

```console
$ curl --cacert <root CA> -X POST -d 'logout_token=not-a-jwt' https://localhost/bff/auth/backchannel-logout
400
$ kubectl -n microservices-platform logs deploy/bff-service -c bff-service --since=60s | grep -i backchannel
warn: Platform.Bff.Foundation.Session.BackchannelLogoutProcessor[0]
      Backchannel logout rejected: token validation failed.
```

## 設計

### 決定 1: 宛先は **in-cluster の素のサービス名** `http://bff-service:8080/bff/auth/backchannel-logout`

- エッジ host（裸でもドット付きでも）は **Keycloak からは引けない**（実測 (b)）。残る選択肢は
  クラスタ内の名前だけである。
- **素の名前**にするのは、同じ realm ファイルを compose も import するためである（母集合 走査 2）。
  IADR-0066 が既に採っている形（設定は素のサービス名で書き、足りない側に ExternalName エイリアスを置く）
  をそのまま使う:
  - k8s: `platform-infra` に `bff-service` の **ExternalName エイリアス**を置く
    （→ `bff-service.microservices-platform.svc.cluster.local`）。
  - compose: `bff` サービスへネットワーク別名 `bff-service` を足す。
- **https ではなく http** にする。エッジの TLS 終端はメッシュの外側の話で、in-cluster の一区間は
  Istio の mTLS が包む（決定 2）。Keycloak に CA truststore を積む必要が無く、**Keycloak の再起動が要らない**
  （#1088 のため再起動は realm を失う）。

**ブラウザ向けの口とサーバ間の口は別系統である。** realm 上でどちらがどちらかを取り違えないよう、
`bff` クライアントの `description` に明記する（realm JSON は注釈を持てないため。`description` は
`check-realm-constraints.js` が 255 文字上限を検査している欄である）。

### 決定 2: メッシュ境界は **BFF ワークロードの 1 ポートを PERMISSIVE にし、平文で通せる URI を
バックチャネルの 1 本だけに絞る**（namespace の STRICT は下げない）

- namespace 全体の `STRICT` は**変えない**（ADR-0005 / IADR-0317）。BFF ワークロードだけに
  `PeerAuthentication`（selector 付き）を置き、`portLevelMtls` で 8080 を `PERMISSIVE` にする。
- そのままでは BFF の全 API が平文で叩けてしまうため、**`AuthorizationPolicy`（DENY）で
  「principal を持たない要求（＝平文）」を `/bff/auth/backchannel-logout` 以外へは通さない**。
  エッジ（istio-ingressgateway）からの要求はメッシュ内の principal を持つので影響を受けない。
- 開ける口の安全性は**署名済み `logout_token` の検証**が担う（IADR-0273 決定 1 が iss / aud / exp /
  events / nonce 不在 / sub まで検証済み）。バックチャネル端点は本来インターネットに面する口であり、
  平文の到達可能性そのものは資格ではない。
- 代替案とその棄却理由:
  - Keycloak をメッシュへ入れる: `platform-infra` に injection ラベルを貼ると **Vault / ESO / 観測系まで
    次の再起動で巻き込む**。射程が違う。
  - エッジ（ingressgateway）経由で入れる: 80 は 301（NFR-11）、443 は SAN と CA truststore が要り、
    Keycloak の再起動を伴う（#1088 と衝突）。**glibc が `*.localhost` を引けない**ので host 名も作れない。
  - CoreDNS で裸の `localhost` を書き換える: `/etc/hosts` が先に当たるため**効かない**（実測 (b)）。

### 決定 3: 稼働 realm へは **kcadm で冪等に当てる**（再インポートでは届かない）

Keycloak は `start-dev --import-realm` で立っており、**既存 realm があるとインポートは黙って飛ばされる**
（`IGNORE_EXISTING`）。したがって **realm JSON を直しただけでは稼働クラスタに反映されない**（#1088 の芯）。
Wiki.js の bootstrap（IADR-0327）と同型の**冪等な後追いスクリプト**を置き、`scripts/k8s-local-up.sh` から
best-effort で呼ぶ。値が既に一致していれば何もしない。

## 受け入れ基準

- [ ] Given ログイン済みセッション / When 管理者が利用者を無効化して全セッションログアウト /
      Then Keycloak のログに `KC-SERVICES0057` が**出ない**
- [ ] Given 同上 / When BFF のログを読む / Then `BackchannelLogoutProcessor` が `logout_token` を**受理**している
- [ ] Given 同上 / When 直後に `/bff/auth/me` を叩く / Then **アクセストークンの寿命を待たず 401**
- [ ] Given realm / When 読む / Then ブラウザ向けの口とサーバ間の口が**注記で区別されている**
- [ ] `node scripts/check-realm-constraints.js` が成功する（検査を足す。下記テスト方針）
- [ ] `docs/` の表示テキストへ計画 ID / IADR / 仕様書名を書いていない（trace ブロックへ）

## テスト方針

**「realm に書いた URL が実際に到達可能か」は静的には測れない**（DNS もメッシュも実行時の性質である）。
静的に測れるのは「**その URL が到達し得ない形をしていないか**」であり、そこに検査を足す。

`scripts/check-realm-constraints.js` に**検査 6** を追加する:

1. `clients[].attributes["backchannel.logout.url"]` の host が **裸の `localhost` / `127.0.0.1` / `::1`**
   でないこと（pod の `/etc/hosts` が必ず自分自身へ向ける名前は、サーバ間の宛先になり得ない）。
2. 同 host が **`*.localhost`（エッジ host）でない**こと（Keycloak の glibc が引けない。実測 (b)）。
3. ブラウザ向けの欄（`redirectUris` / `webOrigins` / `post.logout.redirect.uris`）は**対象にしない**
   （裸の `localhost` が正しい欄である）。

自己試験は既存の `--self-test` に足し、`scripts/scripts.test.js`（`REQUIRE_REPO_TESTS=1`）で回す。
**xUnit のテストは足さない** —— issue の制約により `Platform.Bff` を変えないため、
C# 側に新しく成立/失敗する振る舞いが無い（受け口の単体テストは `BackchannelLogoutTests.cs` に既にある。
#1063 の Tests 移送中でもあり、既存テストファイルは触らない）。

実クラスタでの陽性・陰性は上記「着手前の実測」と PR 本文の実測表で対にして示す。

## 計画書との差異

- 差異: あり。**issue #1115 が挙げた 3 つの選択肢のうち、2 と 3 は「危険/待ち」ではなく
  実測上「効かない」**（裸の `localhost` は `/etc/hosts`、`*.localhost` は Keycloak の glibc）。
  選択肢 1 も宛先の書き換えだけでは STRICT mTLS に阻まれる。**この 3 点は issue の前提の訂正**であり、
  実装 ADR に残す（計画書自体の誤りではないため planning への環流は行わない）。

## 未決事項

- `platform-infra` をメッシュへ入れる恒久像（決定 2 の代替案 1）は本件の射程外。入れれば
  `PeerAuthentication` の例外は不要になる。別 issue として起票するか判断する。
- #1088（PERSIST=1 で立っていない）が解決すると、kcadm の後追い（決定 3）は
  「realm JSON の変更を既存クラスタへ届ける」目的では引き続き必要である（IGNORE_EXISTING は永続化とは別問題）。

# Wiki.js の Keycloak OIDC(SSO)（IADR-0095・#353）

> 起点: [IADR-0095](../../../.ai-context/adr/IADR-0095_wikijs-keycloak-oidc.md) /
> 作業仕様書 [`.ai-context/specs/20260721_issue-353_wikijs-keycloak-oidc.md`](../../../.ai-context/specs/20260721_issue-353_wikijs-keycloak-oidc.md)

realm には `wiki-js` client が既存（[IADR-0020](../../../.ai-context/adr/IADR-0020_wiki-js-deployment-abac-gateway.md)）。
**集約後 URL `wiki.localhost:50000`（#357/edge）への redirect は realm に登録済み**で、edge にも wiki route がある
（#353。非 edge の port-forward 用 `http://localhost:3300/*` も登録済み＝#385/PR #401）。
**Wiki.js の OIDC 設定は DB/管理UI 保持**（"Generic OpenID Connect" ストラテジ）で manifest 自動化できないため、下記の
**管理UI 手順**（realm import と同型の runtime 設定）で入れる。ローカルログインは既定無効の OIDC 単一経路（IADR-0020）。

## 前提: Wiki.js のセットアップが済んでいること（#1108）

🔴 **本ページの手順は、Wiki.js の初期セットアップが完了していることが前提である。**
未完了だと Wiki.js は setup モードのままで、管理 UI（`/a`）も `/graphql` も存在しない
（`server/setup.js` の catch-all が `/healthz` を含む全 URL に 200 を返すので、
**Pod は `2/2 Running` に見える**）。

セットアップと Site URL（`settings.host`）の設定は
[`../wikijs-setup/`](../wikijs-setup/README.md)（`scripts/k8s-local-up.sh` が既定で呼ぶ）が入れる。

## 🔴 いまは自動である（#1127・IADR-0342）

**OIDC ストラテジ（`authentication` 行）の投入も `settings.host` の突き合わせも、
[`../wikijs-setup/bootstrap.sh`](../wikijs-setup/README.md) の段 8 が冪等に行う。**
以前は本ページの手作業 SQL が正本だったが（#397 が未実装のまま close されていた）、
それを自動化したのが #1127 である。**手で SQL を流す必要は無い。**

```sh
# 既定オフの opt-in。エッジ（LOCALEDGE=1）で立てたスタックに対して使う。
WIKIJS_OIDC=1 bash scripts/k8s-local-up.sh          # up ごと（推奨）
WIKIJS_OIDC=1 bash deploy/local/wikijs-setup/bootstrap.sh   # 既に立っているスタックへ後から
```

- **冪等**。2 回目は「変更なし」を報告し、`wiki-js` を再起動しない。
- **潰さない**。`local` ストラテジ・既存利用者・発行済みの API キーに触らない。
  既存の oidc 行があればその `key` を再利用する（`users.providerKey` の外部キーが切れない）。
- client secret は **Vault/ESO 経路**（`secret/msp/wikijs-oidc` → Secret `wikijs-oidc` の
  `client-secret`）か env `WIKIJS_OIDC_CLIENT_SECRET` から取る。**取れなければ既存設定に触らずに終わる**
  （空で上書きして動いているログインを壊さない）。
- 既定オフである理由: endpoint は**エッジ host を前提にする**ので、`LOCALEDGE` 抜きで既定 ON にすると
  「押せるが 502 になるボタン」を作ることになる。既定では `local` ログインだけが残る。

以下は **中身を知るため**と、**自動化が効かないときに手で当てるため**の説明である。

## 到達（集約後 URL・#357/edge）

```sh
# edge 集約を有効化（k3d はポート再作成が必要・破壊操作はユーザー実行）
k3d cluster delete msp-ast-dev
LOCALEDGE=1 bash scripts/k8s-local-up.sh
#   → https://wiki.localhost:50000  （admin-ingress-wiki.yaml が wiki-js:3000 へ）
# 従来の port-forward も併用可: kubectl -n microservices-platform port-forward svc/wiki-js 3300:3000 → http://localhost:3300
```

## OIDC 設定手順（管理UI・**client secret は非コミット**）

Wiki.js 管理コンソール（`/a`）→ **Authentication** → **+ Add Strategy** → **Generic OpenID Connect**:

| 項目 | 値 |
| --- | --- |
| Client ID | `wiki-js` |
| Client Secret | realm の `wiki-js` client secret（dev プレースホルダ `wiki-js-dev-secret-change-me`・**本番は変更**・**UI 入力＝リポジトリに平文コミットしない**） |
| Authorization Endpoint URL | **`https://keycloak.localhost/realms/platform/protocol/openid-connect/auth`**（ブラウザが開く） |
| Token Endpoint URL | `http://keycloak:8080/realms/platform/protocol/openid-connect/token`（Wiki.js pod が叩く） |
| User Info Endpoint URL | `http://keycloak:8080/realms/platform/protocol/openid-connect/userinfo`（同上） |
| Issuer | **`https://keycloak.localhost/realms/platform`**（token の `iss` と一致させる） |
| Logout URL（任意） | **`https://keycloak.localhost/realms/platform/protocol/openid-connect/logout`**（ブラウザが開く） |

> 🔴 **［2026-08-31 / #780・IADR-0243］issuer を https のエッジ host へ移した。**
> 従前は 5 つとも in-cluster 名 `http://keycloak:8080/...` だったが、**ブラウザはそれを解決できない。**
>
> **5 つを揃えないのは役割が違うからである**（Grafana と同じ分離。IADR-0086 の一般化）:
> **ブラウザが開く 3 つ（Authorization / Issuer / Logout）はエッジ host**、
> **Wiki.js pod がサーバ側で叩く 2 つ（Token / UserInfo）は in-cluster** にする。
> `Issuer` は id_token の `iss` と突き合わせるのでエッジ側でなければならない。
> in-cluster を残すのは、**ローカル CA（`local-edge-ca`）を Wiki.js コンテナへ配らずに済ませる**ためである
> （揃えたい場合は `NODE_EXTRA_CA_CERTS` で `edge-tls` の `ca.crt` を渡す）。

- **Site URL（重要・Administration → General）**: **利用する経路の到達 URL と一致させる**。値は下の
  **次節「Site URL は経路と一致させる」**を参照（edge=`https://wiki.localhost:50000` /
  port-forward 単独=`http://localhost:3300`）。
- **claim / group マッピング（fail-safe）**: strategy の **Map Groups** を有効化し、`groups`（realm の abac-attributes /
  roles スコープ由来）を Wiki.js グループへ対応づける。**未マッピングのユーザーは最小権限グループ（Guests 相当）に割当**
  （deny-by-default 寄り。管理権限は `platform-admin` 等のグループにのみ付与）。
- **issuer 整合（#780・IADR-0243）**: Wiki.js server（microservices-platform ns）も browser も
  `https://keycloak.localhost` を使う。pod からの解決は `coredns-custom`（IADR-0227）が担う。
  **hosts 追記も port-forward も不要。**

### Site URL は経路と一致させる（#385）

Wiki.js は **コールバックを `{Site URL}/login/{strategyKey}/callback`** で組み立てる（strategyKey はストラテジ作成時に
生成）。したがって **Site URL が、実際にブラウザで開いている URL と一致していないと `invalid_redirect_uri` になる**
（realm に登録があっても合わない）。realm 側はいずれもワイルドカードで受けるので、**経路ごとに次の値を選ぶ**:

| 経路 | Site URL に設定する値 | realm `wiki-js` の対応 redirect |
| --- | --- | --- |
| **edge 集約（`LOCALEDGE=1`・既定の正規経路）** | `https://wiki.localhost:50000` | `https://wiki.localhost:50000/*` |
| **k8s の port-forward 単独（非 edge）** | `http://localhost:3300` | `http://localhost:3300/*`（#385） |

以降の手順は **edge 経路を既定**として記述する。port-forward 単独で使う場合は Site URL を `http://localhost:3300` に
読み替える（他の項目＝endpoint / issuer は in-cluster 名のままで変わらない）。`values-local.yaml` の
`WIKI_BASE_URL`（SPA の「Wiki を開く」導線）とは**別物**なので両方を揃えること。経路別の port topology は
[IADR-0095 の「追記（2026-07-26・Issue #385）」](../../../.ai-context/adr/IADR-0095_wikijs-keycloak-oidc.md)が単一情報源。

## DB seed で入れる（手で当てるときの手順・IADR-0103 / IADR-0342）

> **通常は上の自動化（`WIKIJS_OIDC=1`）を使うこと。** 本節は、bootstrap を実行できない状況で
> 手で当てるときと、段 8 が何をしているかを読むためのものである。

管理UI を開けない場合は、Wiki.js の `authentication` テーブルへ直接投入する（設定は DB 保持）。
**前提として次の 2 つが realm 側に必要**（`realm.json` に恒久化済み）:

- `wiki-js` client の **`groups` claim mapper**（`wikijs-realm-roles`）— 無いと Map Groups が効かない
- realm ロール **`Administrators`**（Wiki.js のグループ名と**文字列一致**させるため。Wiki.js は自前グループ管理なので
  名前一致が唯一の接点）。`admin` ユーザーに付与済み。

```sh
# client secret は realm から取得し、リポジトリにもログにも平文を残さない
KCADM=http://localhost:8080; R=platform
T=$(curl -s $KCADM/realms/master/protocol/openid-connect/token -d grant_type=password \
  -d client_id=admin-cli -d username=admin \
  -d password="$(kubectl -n platform-infra get secret keycloak-admin -o jsonpath='{.data.password}' | base64 -d)" | jq -r .access_token)
WSEC=$(curl -s -H "Authorization: Bearer $T" "$KCADM/admin/realms/$R/clients?clientId=wiki-js" | jq -r '.[0].secret')

# IADR-0243 / #780: ブラウザが開く URL はエッジ host、Wiki.js pod が叩く URL は in-cluster（上表の注記）
KC_BROWSER=https://keycloak.localhost/realms/platform
KC_SERVER=http://keycloak:8080/realms/platform
# Site URL は経路と一致させる（#385）。既定＝edge 集約。port-forward 単独なら SITE_URL=http://localhost:3300
SITE_URL="${SITE_URL:-https://wiki.localhost:50000}"
CFG=$(jq -cn --arg s "$WSEC" --arg b "$KC_BROWSER" --arg i "$KC_SERVER" '{
  clientId:"wiki-js", clientSecret:$s,
  authorizationURL:($b+"/protocol/openid-connect/auth"),
  tokenURL:($i+"/protocol/openid-connect/token"),
  userInfoURL:($i+"/protocol/openid-connect/userinfo"),
  skipUserProfile:false, issuer:$b,
  emailClaim:"email", displayNameClaim:"preferred_username", pictureClaim:"picture",
  mapGroups:true, groupsClaim:"groups",
  logoutURL:($b+"/protocol/openid-connect/logout"), acrValues:""}')

# 🔴 DELETE→INSERT にしない（#1127 で実測）。`users.providerKey` が `authentication.key` を参照する
#    外部キー `users_providerkey_foreign` があるため、**誰か 1 人でも OIDC でログインした後は
#    DELETE が必ず落ちる**（"violates foreign key constraint"）。既存行があればその key を再利用して
#    UPSERT する ——「冪等」を名乗るならこちらでなければならない。
cat > /tmp/wiki_oidc.sql <<SQL
BEGIN;
UPDATE settings SET value='{"v":"$SITE_URL"}', "updatedAt"=now()::text WHERE key='host';
INSERT INTO authentication (key,"isEnabled",config,"selfRegistration","domainWhitelist","autoEnrollGroups","order","strategyKey","displayName")
SELECT COALESCE((SELECT a.key FROM authentication a WHERE a."strategyKey"='oidc' ORDER BY a."order", a.key LIMIT 1),
                '7c1f6f2e-9d3a-4b5c-8e10-000000000001'),
       true, \$json\$$CFG\$json\$::json, true, '{"v":[]}'::json,
       (SELECT json_build_object('v', COALESCE(json_agg(g.id ORDER BY g.id), '[]'::json)) FROM groups g WHERE g.name='Guests'),
       1, 'oidc', 'Keycloak'
ON CONFLICT (key) DO UPDATE SET
  "isEnabled"=EXCLUDED."isEnabled", config=EXCLUDED.config, "selfRegistration"=EXCLUDED."selfRegistration",
  "domainWhitelist"=EXCLUDED."domainWhitelist", "autoEnrollGroups"=EXCLUDED."autoEnrollGroups",
  "order"=EXCLUDED."order", "strategyKey"=EXCLUDED."strategyKey", "displayName"=EXCLUDED."displayName";
COMMIT;
SQL
kubectl -n platform-infra exec -i deploy/postgres -- psql -U kp -d wikijs -q -f - < /tmp/wiki_oidc.sql
rm -f /tmp/wiki_oidc.sql
kubectl -n microservices-platform rollout restart deploy/wiki-js
```

- **`settings.host`（Site URL）は上表の経路別の値**にする（`SITE_URL` 変数。既定＝edge 集約 `wiki.localhost:50000`、
  port-forward 単独なら `SITE_URL=http://localhost:3300` を付けて実行する）。Wiki.js は callback を
  `{Site URL}/login/{key}/callback` で組むため、ここが経路と不一致だと realm 側に登録があっても redirect が合わない。
  なお `values-local.yaml` の `WIKI_BASE_URL`（SPA の「Wiki を開く」導線）とは**別物**なので両方を揃える。
- `autoEnrollGroups` は **Guests**＝**最小権限の床**（deny-by-default 寄り）。Guests は既定で
  `read:pages`/`read:assets`/`read:comments` と全パスの pageRule を持つため**追加付与は不要**。
  id を `2` と決め打たず、**名前 `Guests` から引く**（上の SQL / 段 8 とも）—— DB を作り直した環境で
  採番が変わると、決め打ちは黙って別グループへ auto-enroll する。
- `selfRegistration: true` が無いと、Wiki.js に未登録の OIDC ユーザー（`admin` 等）がログインできない。

成功確認:

```sh
kubectl -n microservices-platform logs deploy/wiki-js --tail=40 | grep "Authentication Strategy Keycloak"
#   → Authentication Strategy Keycloak: [ OK ]
# IADR-0220 (#841): admin(50000) は TLS 終端。selfsigned CA なので --cacert でルート CA を渡す:
#   kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\.crt}' | base64 -d > ca.crt
curl -s --cacert ca.crt --resolve wiki.localhost:50000:127.0.0.1 -X POST https://wiki.localhost:50000/graphql \
  -H 'Content-Type: application/json' \
  -d '{"query":"{authentication{activeStrategies(enabledOnly:true){key strategy{key} displayName}}}"}' | jq -c '.data'
curl -s -o /dev/null -w '%{http_code}\n' --cacert ca.crt --resolve wiki.localhost:50000:127.0.0.1 \
  https://wiki.localhost:50000/login/7c1f6f2e-9d3a-4b5c-8e10-000000000001   # → 302（Keycloak へ）
```

## 注意

- **port-forward 単独（`LOCALEDGE` 未使用）**: Site URL を集約 URL のままにしていると、コールバックが
  `wiki.localhost:50000` を指すため edge 未起動だと OIDC が完了しない（Grafana PR-2/IADR-0090・MinIO/IADR-0093 と
  同性質）。この場合は Site URL を `http://localhost:3300`（＝`port-forward svc/wiki-js 3300:3000` と同値）へ切り替える
  ＝上の**「Site URL は経路と一致させる」節**の表のとおり。realm の `wiki-js` client には
  `http://localhost:3300/*` を登録済み（#385）。
- **redirect の port topology（取り違え注意・#385）**: `wiki-js` client に登録済みの redirect は経路ごとに別物。
  **edge 集約＝`https://wiki.localhost:50000/*`** / **k8s の port-forward＝`http://localhost:3300/*`** /
  **compose(dev) の host 公開＝`http://localhost:3001/*`**（[IADR-0032](../../../.ai-context/adr/IADR-0032_wikijs-dev-exposure-opt-in.md)
  の `ports: 3001:3000`）/ in-cluster＝`http://wiki-js:3000/*`。k8s の port-forward に `3001` は使わない。
- **realm 反映**: `wiki-js` client の redirect 追加は realm 再インポートで反映（永続化時は管理コンソール追加 or 再作成）。
- **dev の Wiki.js DB**: OIDC ストラテジは DB 保持なので、DB を作り直すと消える。**復旧は
  `WIKIJS_OIDC=1 bash deploy/local/wikijs-setup/bootstrap.sh` の 1 本**（#1127・IADR-0342。
  realm import や Vault bootstrap と同じ「runtime 設定の冪等な再適用」）。手で SQL を流す必要は無い。
- CLI/一部 OS で `*.localhost` 未解決なら hosts 追記 or `*.nip.io`。

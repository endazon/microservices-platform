# Wiki.js の Keycloak OIDC(SSO)（IADR-0095・#353）

> 起点: [IADR-0095](../../../docs/adr/IADR-0095_wikijs-keycloak-oidc.md) /
> 作業仕様書 [`docs/specs/20260721_issue-353_wikijs-keycloak-oidc.md`](../../../docs/specs/20260721_issue-353_wikijs-keycloak-oidc.md)

realm には `wiki-js` client が既存（[IADR-0020](../../../docs/adr/IADR-0020_wiki-js-deployment-abac-gateway.md)）。
本 PR で **集約後 URL `wiki.localhost:50000`（#357/edge）への redirect を realm に追加**し、edge に wiki route を足した。
**Wiki.js の OIDC 設定は DB/管理UI 保持**（"Generic OpenID Connect" ストラテジ）で manifest 自動化できないため、下記の
**管理UI 手順**（realm import と同型の runtime 設定）で入れる。ローカルログインは既定無効の OIDC 単一経路（IADR-0020）。

## 到達（集約後 URL・#357/edge）

```sh
# edge 集約を有効化（k3d はポート再作成が必要・破壊操作はユーザー実行）
k3d cluster delete msp-ast-dev
LOCALEDGE=1 bash scripts/k8s-local-up.sh
#   → http://wiki.localhost:50000  （admin-ingress-wiki.yaml が wiki-js:3000 へ）
# 従来の port-forward も併用可: kubectl -n microservices-platform port-forward svc/wiki-js 3300:3000 → http://localhost:3300
```

## OIDC 設定手順（管理UI・**client secret は非コミット**）

Wiki.js 管理コンソール（`/a`）→ **Authentication** → **+ Add Strategy** → **Generic OpenID Connect**:

| 項目 | 値 |
| --- | --- |
| Client ID | `wiki-js` |
| Client Secret | realm の `wiki-js` client secret（dev プレースホルダ `wiki-js-dev-secret-change-me`・**本番は変更**・**UI 入力＝リポジトリに平文コミットしない**） |
| Authorization Endpoint URL | `http://keycloak:8080/realms/microservices-platform/protocol/openid-connect/auth` |
| Token Endpoint URL | `http://keycloak:8080/realms/microservices-platform/protocol/openid-connect/token` |
| User Info Endpoint URL | `http://keycloak:8080/realms/microservices-platform/protocol/openid-connect/userinfo` |
| Issuer | `http://keycloak:8080/realms/microservices-platform` |
| Logout URL（任意） | `http://keycloak:8080/realms/microservices-platform/protocol/openid-connect/logout` |

- **Site URL（重要・Administration → General）**: `http://wiki.localhost:50000` に設定する。Wiki.js は
  **コールバックを `{Site URL}/login/{strategyKey}/callback`** で組み立てるため、これが集約 URL でないと redirect が
  一致しない（strategyKey はストラテジ作成時に生成。realm 側は `http://wiki.localhost:50000/*` の**ワイルドカード**で受ける）。
- **claim / group マッピング（fail-safe）**: strategy の **Map Groups** を有効化し、`groups`（realm の abac-attributes /
  roles スコープ由来）を Wiki.js グループへ対応づける。**未マッピングのユーザーは最小権限グループ（Guests 相当）に割当**
  （deny-by-default 寄り。管理権限は `platform-admin` 等のグループにのみ付与）。
- **issuer 整合（#284 手順A）**: Wiki.js server（microservices-platform ns）は ExternalName alias `keycloak` で in-cluster の
  endpoint に到達する。browser も `keycloak:8080` を解決できるよう hosts 追記＋`port-forward svc/keycloak 8080:8080`。

## DB seed で入れる（管理UI を使わない手順・IADR-0103）

管理UI を開けない/自動化したい場合は、Wiki.js の `authentication` テーブルへ直接投入する（設定は DB 保持）。
**前提として次の 2 つが realm 側に必要**（`realm.json` に恒久化済み）:

- `wiki-js` client の **`groups` claim mapper**（`wikijs-realm-roles`）— 無いと Map Groups が効かない
- realm ロール **`Administrators`**（Wiki.js のグループ名と**文字列一致**させるため。Wiki.js は自前グループ管理なので
  名前一致が唯一の接点）。`admin` ユーザーに付与済み。

```sh
# client secret は realm から取得し、リポジトリにもログにも平文を残さない
KCADM=http://localhost:8080; R=microservices-platform
T=$(curl -s $KCADM/realms/master/protocol/openid-connect/token -d grant_type=password \
  -d client_id=admin-cli -d username=admin \
  -d password="$(kubectl -n platform-infra get secret keycloak-admin -o jsonpath='{.data.password}' | base64 -d)" | jq -r .access_token)
WSEC=$(curl -s -H "Authorization: Bearer $T" "$KCADM/admin/realms/$R/clients?clientId=wiki-js" | jq -r '.[0].secret')

KC=http://keycloak:8080/realms/microservices-platform
CFG=$(jq -cn --arg s "$WSEC" --arg kc "$KC" '{
  clientId:"wiki-js", clientSecret:$s,
  authorizationURL:($kc+"/protocol/openid-connect/auth"),
  tokenURL:($kc+"/protocol/openid-connect/token"),
  userInfoURL:($kc+"/protocol/openid-connect/userinfo"),
  skipUserProfile:false, issuer:$kc,
  emailClaim:"email", displayNameClaim:"preferred_username", pictureClaim:"picture",
  mapGroups:true, groupsClaim:"groups",
  logoutURL:($kc+"/protocol/openid-connect/logout"), acrValues:""}')

cat > /tmp/wiki_oidc.sql <<SQL
BEGIN;
UPDATE settings SET value='{"v":"http://wiki.localhost:50000"}' WHERE key='host';
DELETE FROM authentication WHERE "strategyKey"='oidc';
INSERT INTO authentication (key,"isEnabled",config,"selfRegistration","domainWhitelist","autoEnrollGroups","order","strategyKey","displayName")
VALUES ('7c1f6f2e-9d3a-4b5c-8e10-000000000001', true, \$json\$$CFG\$json\$, true, '{"v":[]}', '{"v":[2]}', 1, 'oidc', 'Keycloak');
COMMIT;
SQL
kubectl -n platform-infra exec -i deploy/postgres -- psql -U postgres -d wikijs -q -f - < /tmp/wiki_oidc.sql
rm -f /tmp/wiki_oidc.sql
kubectl -n microservices-platform rollout restart deploy/wiki-js
```

- **`settings.host`（Site URL）は必ず edge 集約 URL** にする。Wiki.js は callback を `{Site URL}/login/{key}/callback`
  で組むため、ここが不一致だと realm 側に登録があっても redirect が合わない。
  なお `values-local.yaml` の `WIKI_BASE_URL`（SPA の「Wiki を開く」導線）とは**別物**なので両方を揃える。
- `autoEnrollGroups: {"v":[2]}` は Guests（id=2）＝**最小権限の床**（deny-by-default 寄り）。Guests は既定で
  `read:pages`/`read:assets`/`read:comments` と全パスの pageRule を持つため**追加付与は不要**。
- `selfRegistration: true` が無いと、Wiki.js に未登録の OIDC ユーザー（`admin` 等）がログインできない。

成功確認:

```sh
kubectl -n microservices-platform logs deploy/wiki-js --tail=40 | grep "Authentication Strategy Keycloak"
#   → Authentication Strategy Keycloak: [ OK ]
curl -s --resolve wiki.localhost:50000:127.0.0.1 -X POST http://wiki.localhost:50000/graphql \
  -H 'Content-Type: application/json' \
  -d '{"query":"{authentication{activeStrategies(enabledOnly:true){key strategy{key} displayName}}}"}' | jq -c '.data'
curl -s -o /dev/null -w '%{http_code}\n' --resolve wiki.localhost:50000:127.0.0.1 \
  http://wiki.localhost:50000/login/7c1f6f2e-9d3a-4b5c-8e10-000000000001   # → 302（Keycloak へ）
```

## 注意

- **port-forward 単独（`LOCALEDGE` 未使用）**: Site URL を集約 URL にしていると、コールバックが `wiki.localhost:50000` を
  指すため edge 未起動だと OIDC が完了しない（Grafana PR-2/IADR-0090・MinIO/IADR-0093 と同性質）。port-forward で OIDC を
  使う場合は Site URL を `http://localhost:3300` にする（realm には旧 redirect も登録済み）。
- **realm 反映**: `wiki-js` client の redirect 追加は realm 再インポートで反映（永続化時は管理コンソール追加 or 再作成）。
- **dev の Wiki.js DB**: OIDC ストラテジは DB 保持。DB を作り直すと再設定が必要（realm import と同様の runtime 手順）。
- CLI/一部 OS で `*.localhost` 未解決なら hosts 追記 or `*.nip.io`。

---
title: 経路B SSO 復旧 Runbook（揮発 live 設定の再適用手順）
type: runbook
status: active
created: 2026-07-25
updated: 2026-09-03
author: claude
---
<!-- trace:
ids: [NFR-09]
adrs: []
iadrs: [IADR-0084, IADR-0091, IADR-0095, IADR-0096, IADR-0103, IADR-0220, IADR-0327, IADR-0328, IADR-0342, IADR-0361]
specs: [20260902_issue-1127_wikijs-oidc-strategy-seed, 20260903_issue-1163_tool-oidc-login-verifier]
issues: [#328, #388, #841, #1127, #1163, AST#245]
-->

# 経路B SSO 復旧 Runbook

経路 B の SSO を再構築後も自動復旧させる実装 ADR により realm・スクリプト側は恒久化したため、**通常は STEP 0 のみで全 SSO が成立する**。
本 runbook は「それでも揮発する残りの設定」を復旧するための手順書である。

## 揮発マトリクス（何が・いつ消えるか）

| 設定 | 消える条件 | 復旧 |
| --- | --- | --- |
| Keycloak realm 全体（`admin` ユーザー・mapper・client ロール・redirect） | **realm 再インポート**（`keycloak-data` PVC 削除／新規クラスタ） | **STEP 0 で自動**（`realm.json` に恒久化済み） |
| Vault dev の全状態（ESO seed・`auth/oidc`・policy・external group） | **vault Pod 再起動**（インメモリ）・クラスタ再構築 | STEP 0 で seed は自動。**OIDC は STEP 2 が手動** |
| Wiki.js の OIDC ストラテジ・Site URL | **`postgres-data` PVC 削除**／wikijs DB 再作成 | **STEP 3**（`WIKIJS_OIDC=1` で bootstrap を 1 本。手動 SQL は退役） |
| Pod の env に載った secret 値 | ESO が Secret を作る前に Pod が起動 | **STEP 0 で自動**（`ESO=1` 末尾の rollout） |
| `argocd` ns の `keycloak` エイリアス | クラスタ再構築 | **STEP 0 で自動**（`ARGOCD=1` が適用） |
| `ast-secrets` の実鍵 | `k8s-local-deploy.sh` を鍵未 export で実行 | STEP 1（鍵を export して再実行） |

`PERSIST=1` を維持し vault Pod を再起動していなければ、**STEP 2・3 はスキップ可**。

---

## STEP 0: クラスタ起動（これだけで SSO が揃う）

```sh
cd <repo>
git checkout develop && git pull --ff-only origin develop && git submodule update --init --recursive

read -rs ANTHROPIC_API_KEY; export ANTHROPIC_API_KEY
LOCALEDGE=1 ESO=1 VAULT=1 OBSERVABILITY=1 HEADLAMP=1 PERSIST=1 ARGOCD=1 \
  bash scripts/k8s-local-up.sh
```

> `ESO=1` は `VAULT=1` 必須（未併記なら fail-fast）。この起動で ①ESO seed 投入 ②ESO 供給後の rollout
> ③`argocd` ns の keycloak エイリアス が自動で入る。

成功確認:

```sh
kubectl get pods -A | grep -vE "Running|Completed"                       # 空
kubectl get externalsecret -A --no-headers \
  -o custom-columns='R:.status.conditions[?(@.type=="Ready")].status' | sort | uniq -c   # 11 True
kubectl get clustersecretstore -o custom-columns='N:.metadata.name,R:.status.conditions[?(@.type=="Ready")].status'
```

## STEP 1: AST デプロイ（鍵の export が必須）

> ⚠️ `k8s-local-deploy.sh` は `ast-secrets` を env から作り直して `apply` する。**export せずに実行すると
> 既存の実鍵が空で上書きされ ①時価/③KB が no-op 化する。**

```sh
read -rs FINNHUB; export FINNHUB_API_KEY="$FINNHUB"; export MARKETDATA_FINNHUB_API_KEY="$FINNHUB"; unset FINNHUB
export KB_AUTH_CLIENTSECRET="ai-stock-trading-kb-writer-dev-secret-change-me"
read -rs DISCORD_BOT_TOKEN; export DISCORD_BOT_TOKEN
export DISCORD_BOT_KILLSWITCH_PHRASE="CONFIRM-KILL"
bash src/ai-stock-trading/scripts/k8s-local-deploy.sh
```

成功確認（①②③＋価格文脈と鍵の非空）:

```sh
kubectl -n ai-stock-trading set env deploy/trade-decision-service --list \
  | grep -E "MarketData__Provider|MaxQuoteStaleness|LlmGateway__BaseUrl"     # finnhub / 300 / llmgateway-service...
for k in finnhub-api-key marketdata-finnhub-api-key kb-auth-client-secret; do
  printf "%s len=%s\n" "$k" "$(kubectl -n ai-stock-trading get secret ast-secrets -o jsonpath="{.data.$k}" | base64 -d | wc -c)"
done                                                                          # すべて 0 でないこと
```

> Discord の環境固有 ID（GuildId/ChannelId/AllowedUserIds/UserMapping）を `kubectl set env` で入れると、
> 次回の `helm upgrade` が `conflict with "kubectl-set"` で失敗する。復旧時は**先に当該 env を
> `KEY-` で削除**してから helm を通す。

## STEP 2: Vault OIDC（**vault Pod 再起動時のみ**）

ESO seed は STEP 0 で自動投入される。**OIDC 設定だけは手動**。ホストに `vault` CLI が無い場合は
**vault Pod 内 CLI** を使う（手順の全文は [`deploy/local/vault/oidc/README.md`](../../deploy/local/vault/oidc/README.md)
の「経路2」）。

成功確認:

```sh
# IADR-0220 (#841): admin(50000) は TLS 終端。selfsigned CA なので --cacert でルート CA を渡す:
#   kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\.crt}' | base64 -d > ca.crt
curl -s --cacert ca.crt --resolve vault.localhost:50000:127.0.0.1 \
  https://vault.localhost:50000/v1/sys/internal/ui/mounts | jq -c '.data.auth|keys'   # → ["oidc/"]
```

## STEP 3: Wiki.js OIDC（**wikijs DB 再作成時のみ**・コマンド 1 本）

**手で SQL を流す手順は退役した。** 既定オフの opt-in を立てて bootstrap を走らせる。冪等で、
2 回目は「変更なし」を報告して `wiki-js` を再起動しない。ローカルログインも発行済みの API キーも潰さない。

```sh
# 既に立っているスタックへ後から入れる（エッジ経路が既定）
WIKIJS_OIDC=1 bash deploy/local/wikijs-setup/bootstrap.sh
# 非 edge（port-forward 単独）で使うときは Site URL も経路に揃える
WIKIJS_OIDC=1 WIKIJS_SITE_URL=http://localhost:3300 bash deploy/local/wikijs-setup/bootstrap.sh
```

client secret は Secret `microservices-platform/wikijs-oidc`（key `client-secret`）か
env `WIKIJS_OIDC_CLIENT_SECRET` から取る。**どちらも空だと、既存設定に触らずに終わる**
（空で上書きして動いているログインを壊さないため）。`ESO=1` で立てたスタックでは
`WIKIJS_OIDC=1` を付けて `up` すれば Vault からこの Secret が供給される。

成功確認:

```sh
kubectl -n microservices-platform logs deploy/wiki-js --tail=40 | grep "Authentication Strategy Keycloak"
#   → Authentication Strategy Keycloak: [ OK ]
kubectl -n platform-infra exec -i deploy/postgres -- \
  psql -U kp -d wikijs -c 'SELECT key, "strategyKey", "displayName" FROM authentication ORDER BY "order";'
#   → local と oidc(Keycloak) の 2 行（local が消えていないこと）
```

## STEP 4: 総合検証

```sh
# ブラウザ OIDC を持つツール 7 件のログイン開始をまとめて測る（段 15 本・読み取り専用）。
# ルート CA はクラスタから自動で取り出し、**TLS 検証は切らない**（-k を持たない）。
# 終了コード: 0=全 PASS / 1=導線の失敗（落ちたクライアントを名指しする） / 2=前提未整備。
bash scripts/verify-tool-oidc-logins.sh

# 実弾 OFF（最重要・不変であること）
kubectl -n ai-stock-trading set env deploy/order-execution-service --list | grep Broker__Provider   # paper
kubectl -n ai-stock-trading get deploy | grep -c opend                                              # 0
```

### ログイン一覧（復旧完了後の期待状態）

| ツール | URL | 資格情報 | 管理者への解決経路 |
| --- | --- | --- | --- |
| SPA/BFF | `https://localhost/` | `developer`/`Developer-2026` ＋ **TOTP**（#438） | `realm_access.roles` |
| Grafana | `grafana.localhost:50000` | `admin`/`admin` | claim `roles` → `platform-admin` → Admin |
| ArgoCD | `argocd.localhost:50000` | `admin`/`admin` | claim `groups` → `g, platform-admin, role:admin` |
| MinIO | `minio.localhost:50000` | `admin`/`admin` | claim `policy` = `["consoleAdmin"]`（client ロール） |
| Vault | `vault.localhost:50000`（**http**） | `admin`/`admin`（Method=OIDC・role=default） | claim `groups` → external group → policy `admin` |
| Wiki.js | `wiki.localhost:50000` | `admin`/`admin` | claim `groups` の `Administrators` → Map Groups |
| Headlamp | `headlamp.localhost:50000` | **SA トークン**（下記） | `headlamp-viewer` SA = cluster-admin |
| Qdrant | `qdrant.localhost:50000/dashboard` | 認証なし | — |

**ブラウザ側の前提（全 OIDC ツール共通）**: 各ツールの飛び先は **`https://keycloak.localhost`（エッジ host）**であり、
`hosts` への `keycloak` 追記も port-forward も要らない（上の検証器が 7 件とも実測して確かめる）。
**admin entrypoint (50000) は TLS 終端である**（#841。計画 `NFR-11`・`ADR-0047`）ため、
各ツールは **`https://`** で開く。平文 `http://<tool>.localhost:50000` は TLS ハンドシェイクに失敗する。
ルート CA を信頼ストアへ入れるまでブラウザ警告が出る（取り出し手順は [edge README](../../deploy/local/edge/README.md)）。

**Headlamp**（現行 k8s では OIDC 不可・token 方式が正式手順。apiserver の OIDC 配線を定めた実装 ADR の追記／#328 は wontfix・#388 へ統合）:

```sh
kubectl -n platform-infra create token headlamp-viewer --duration=24h
```

`headlamp-viewer` SA と cluster-admin bind は **overlay に含まれていない**（クラスタ側の手作りに依存）。`NotFound`
になる場合は先に作成する（コマンドは [`deploy/local/README.md`](../../deploy/local/README.md) の「Headlamp」参照）。
発行したトークンは Headlamp UI の **Token** 方式に貼る。

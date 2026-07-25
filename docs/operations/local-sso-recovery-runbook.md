---
title: 経路B SSO 復旧 Runbook（揮発 live 設定の再適用手順）
type: runbook
status: active
related_ids:
  - IADR-0103
  - IADR-0084
  - IADR-0091
  - IADR-0096
author: claude
created: 2026-07-25
updated: 2026-07-25
related_specs:
  - "../adr/IADR-0103_local-sso-persistence-and-claim-design.md"
  - "../adr/IADR-0084_headlamp-oidc-apiserver-flags.md"
  - "../../deploy/local/README.md"
  - "../../deploy/local/vault/oidc/README.md"
  - "../../deploy/local/wiki-oidc/README.md"
---

# 経路B SSO 復旧 Runbook

[[IADR-0103]] で realm・スクリプト側は恒久化したため、**通常は STEP 0 のみで全 SSO が成立する**。
本 runbook は「それでも揮発する残りの設定」を復旧するための手順書である。

## 揮発マトリクス（何が・いつ消えるか）

| 設定 | 消える条件 | 復旧 |
| --- | --- | --- |
| Keycloak realm 全体（`admin` ユーザー・mapper・client ロール・redirect） | **realm 再インポート**（`keycloak-data` PVC 削除／新規クラスタ） | **STEP 0 で自動**（`realm.json` に恒久化済み・IADR-0103） |
| Vault dev の全状態（ESO seed・`auth/oidc`・policy・external group） | **vault Pod 再起動**（インメモリ）・クラスタ再構築 | STEP 0 で seed は自動。**OIDC は STEP 2 が手動** |
| Wiki.js の OIDC ストラテジ・Site URL | **`postgres-data` PVC 削除**／wikijs DB 再作成 | **STEP 3**（手動・DB seed） |
| Pod の env に載った secret 値 | ESO が Secret を作る前に Pod が起動 | **STEP 0 で自動**（`ESO=1` 末尾の rollout・IADR-0103） |
| `argocd` ns の `keycloak` エイリアス | クラスタ再構築 | **STEP 0 で自動**（`ARGOCD=1` が適用・IADR-0103） |
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
> ③`argocd` ns の keycloak エイリアス が自動で入る（IADR-0103）。

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
> 次回の `helm upgrade` が `conflict with "kubectl-set"` で失敗する（AST #245）。復旧時は**先に当該 env を
> `KEY-` で削除**してから helm を通す。

## STEP 2: Vault OIDC（**vault Pod 再起動時のみ**）

ESO seed は STEP 0 で自動投入される。**OIDC 設定だけは手動**。ホストに `vault` CLI が無い場合は
**vault Pod 内 CLI** を使う（手順の全文は [`deploy/local/vault/oidc/README.md`](../../deploy/local/vault/oidc/README.md)
の「経路2」）。

成功確認:

```sh
curl -s --resolve vault.localhost:50000:127.0.0.1 \
  http://vault.localhost:50000/v1/sys/internal/ui/mounts | jq -c '.data.auth|keys'   # → ["oidc/"]
```

## STEP 3: Wiki.js OIDC（**wikijs DB 再作成時のみ**）

DB seed 手順の全文は [`deploy/local/wiki-oidc/README.md`](../../deploy/local/wiki-oidc/README.md)「DB seed で入れる」。

成功確認:

```sh
kubectl -n microservices-platform logs deploy/wiki-js --tail=40 | grep "Authentication Strategy Keycloak"
```

## STEP 4: 総合検証

```sh
# 各ツールの auth 開始
curl -s -o /dev/null -w 'argocd  %{http_code}\n' --resolve argocd.localhost:50000:127.0.0.1  http://argocd.localhost:50000/auth/login            # 303
curl -s -o /dev/null -w 'grafana %{http_code}\n' --resolve grafana.localhost:50000:127.0.0.1 http://grafana.localhost:50000/login/generic_oauth  # 302
curl -s --resolve minio.localhost:50000:127.0.0.1 http://minio.localhost:50000/api/v1/login | jq -r .loginStrategy                              # redirect
curl -s --resolve vault.localhost:50000:127.0.0.1 http://vault.localhost:50000/v1/sys/internal/ui/mounts | jq -c '.data.auth|keys'               # ["oidc/"]

# 実弾 OFF（最重要・不変であること）
kubectl -n ai-stock-trading set env deploy/order-execution-service --list | grep Broker__Provider   # paper
kubectl -n ai-stock-trading get deploy | grep -c opend                                              # 0
```

### ログイン一覧（復旧完了後の期待状態）

| ツール | URL | 資格情報 | 管理者への解決経路 |
| --- | --- | --- | --- |
| SPA/BFF | `http://localhost/` | `developer`/`developer` | `realm_access.roles` |
| Grafana | `grafana.localhost:50000` | `admin`/`admin` | claim `roles` → `platform-admin` → Admin |
| ArgoCD | `argocd.localhost:50000` | `admin`/`admin` | claim `groups` → `g, platform-admin, role:admin` |
| MinIO | `minio.localhost:50000` | `admin`/`admin` | claim `policy` = `["consoleAdmin"]`（client ロール） |
| Vault | `vault.localhost:50000`（**http**） | `admin`/`admin`（Method=OIDC・role=default） | claim `groups` → external group → policy `admin` |
| Wiki.js | `wiki.localhost:50000` | `admin`/`admin` | claim `groups` の `Administrators` → Map Groups |
| Headlamp | `headlamp.localhost:50000` | **SA トークン**（下記） | `headlamp-viewer` SA = cluster-admin |
| Qdrant | `qdrant.localhost:50000/dashboard` | 認証なし | — |

**ブラウザ側の前提（全 OIDC ツール共通）**: 各ツールは `http://keycloak:8080/...` へリダイレクトするため
`hosts` に `127.0.0.1 keycloak` ＋ `kubectl -n platform-infra port-forward svc/keycloak 8080:8080` が必要（手順A）。
`https://<tool>.localhost:50000` は **404**（admin entrypoint は平文 http のみ・IADR-0103）。

**Headlamp**（現行 k8s では OIDC 不可・[[IADR-0084]] 追記／#388）:

```sh
kubectl -n platform-infra create token headlamp-viewer --duration=24h
```

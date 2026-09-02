#!/usr/bin/env bash
# FR-13, UC-07, SC-04, ADR-0011, IADR-0020/IADR-0021, IADR-0327 (#1108):
# Wiki.js の **初期セットアップ**と **WikiService 用 API キー**を入れる冪等な runtime bootstrap。
#
#   bash deploy/local/wikijs-setup/bootstrap.sh
#
# ## なぜ要るか —— 「Running なのに使えない」を既定の経路で潰す
#
# Wiki.js 2.x は初期セットアップが終わるまで本体のルータ（`/graphql` を含む）を載せない。
# 未セットアップの間 `server/setup.js` の catch-all（`app.get('*')`）が**すべての URL に 200 を返す**ため、
# **`/healthz` の readinessProbe は通り、Pod は `Running` のまま「使えない」**（#1108 実測）。
# その状態で WikiService が同期しようとすると GraphQL が 404 を返し、
# `DocumentUpdated` / `DocumentDeleted` が**全件エラーキューへ落ちる。しかも画面には何も出ない。**
#
# セットアップ状態は Wiki.js の DB（`settings` テーブル）に載る。DB は platform-infra の共有 Postgres で、
# 既定（`PERSIST` 未設定）では **emptyDir**＝ postgres Pod を作り直すと `wikijs` DB ごと消える。
# つまり **manifest だけでは復元できない runtime 状態**であり、`deploy/local/vault/eso/bootstrap.sh`
# （Vault の runtime 設定）や realm import と同じ「冪等な再適用」で面倒を見る種類のものである。
#
# ## 作法
#
# - **冪等**: セットアップ済みなら finalize を飛ばし、有効な API キーが在るなら再発行しない。
# - **HTTP は wiki-js コンテナ内の loopback へ出す**（`kubectl exec ... curl http://127.0.0.1:3000`）。
#   エッジ（Traefik / Istio Ingress Gateway）にも port-forward にも STRICT mTLS（#1109）にも依存しない。
# - **秘密をリポジトリにもログにも書かない**: 管理者パスワードは Secret `wikijs-admin`、API キーは
#   Secret `wikijs-sync`（既存キー名 `apiKey`・消費側は無改変）。**標準出力へは長さしか出さない。**
#   なお curl の引数はコンテナ内のプロセス表に一瞬現れる（dev 専用・ローカル単一ノード前提で許容する）。
# - **best-effort**: 失敗しても `k8s-local-up.sh` を止めない。**fail-closed の門は
#   `scripts/check-stack-ready.js` の G7**（setup モードを検知して落とす）に置く。
#
# 環境変数（すべて任意）:
#   WIKIJS_ADMIN_EMAIL      既定 admin@example.com
#   WIKIJS_ADMIN_PASSWORD   未指定なら Secret `wikijs-admin` を再利用し、それも無ければ**乱数を生成**する
#                           （dev 既定のパスワード文字列は置かない —— ここはエッジに露出する実ログイン口である）
#   WIKIJS_SITE_URL         既定 https://wiki.localhost:50000（LOCALEDGE=1 の集約 URL。#385 と同じ値）
#   WIKIJS_API_KEY_NAME     既定 wiki-service-sync
#   WIKIJS_API_KEY_TTL      既定 1y
#
# 段 8（Keycloak OIDC ストラテジ）は **opt-in・既定オフ**（IADR-0342 / #1127）。env は段 8 の直前に列挙する。
set -euo pipefail

MSP_NS="${MSP_NS:-microservices-platform}"
INFRA_NS="${INFRA_NS:-platform-infra}"
WIKI_DEPLOY="wiki-js"
WIKI_CONTAINER="wiki-js"
SYNC_DEPLOY="wiki-service"
ADMIN_SECRET="wikijs-admin"
SYNC_SECRET="wikijs-sync"
OIDC_SECRET="wikijs-oidc"
WIKI_URL="http://127.0.0.1:3000"

ADMIN_EMAIL="${WIKIJS_ADMIN_EMAIL:-admin@example.com}"
SITE_URL="${WIKIJS_SITE_URL:-https://wiki.localhost:50000}"
API_KEY_NAME="${WIKIJS_API_KEY_NAME:-wiki-service-sync}"
API_KEY_TTL="${WIKIJS_API_KEY_TTL:-1y}"

log()  { echo "    [wikijs-setup] $*"; }
warn() { echo "    [wikijs-setup] WARN: $*" >&2; }

# ---------------------------------------------------------------- 低水準ヘルパ

# wiki-js コンテナ内 loopback へ POST する。stdin = リクエストボディ。
# 出力は「ボディ ＋ 改行 ＋ HTTP ステータス」。呼び出し側が最終行を切り出す。
wiki_post() { # $1=path  $2=bearer(任意)  stdin=body
  local path="$1" bearer="${2:-}" args
  args=(curl -sS --max-time 60 -w '\n%{http_code}' -X POST "${WIKI_URL}${path}"
        -H 'Content-Type: application/json')
  # 🔴 `[ ... ] && args+=(...)` と書かない —— 偽のとき終了ステータス 1 が返り、`set -e` が
  #   script ごと落とす（しかも「Wiki.js が壊れている」ように見える無関係な失敗になる）。
  if [ -n "$bearer" ]; then args+=(-H "Authorization: Bearer ${bearer}"); fi
  args+=(--data-binary @-)
  kubectl -n "$MSP_NS" exec -i "deploy/${WIKI_DEPLOY}" -c "$WIKI_CONTAINER" -- "${args[@]}" 2>/dev/null
}

http_status() { printf '%s' "$1" | tail -n 1; }
http_body()   { printf '%s' "$1" | sed '$d'; }

# GraphQL を 1 本投げる。$1=query, $2=bearer(任意)。variables は使わない（クエリは本 script が持つ）。
graphql() { # $1=query  $2=bearer
  printf '{"query":%s}' "$(json_string "$1")" | wiki_post /graphql "${2:-}"
}

# 最小の JSON 文字列エスケープ（本 script が組み立てる値だけを通す）。
json_string() { printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr '\n' ' ')"; }

# Secret の 1 キーを取り出す（不在なら空文字）。
read_secret() { # $1=name $2=key
  local raw
  raw="$(kubectl -n "$MSP_NS" get secret "$1" -o "jsonpath={.data.$2}" 2>/dev/null || true)"
  [ -n "$raw" ] || { printf ''; return 0; }
  printf '%s' "$raw" | base64 -d 2>/dev/null || printf ''
}

# Secret の 1 キーを書く（在れば patch・無ければ create）。**値は表示しない。**
write_secret() { # $1=name $2=key $3=value
  if kubectl -n "$MSP_NS" get secret "$1" >/dev/null 2>&1; then
    kubectl -n "$MSP_NS" patch secret "$1" \
      -p "{\"stringData\":{\"$2\":\"$3\"}}" >/dev/null
  else
    kubectl -n "$MSP_NS" create secret generic "$1" --from-literal="$2=$3" \
      --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  fi
}

# ---------------------------------------------------------------- 判定

# `/graphql` が 404 なら setup モード（Wiki.js 2.x は setup 完了まで本体ルータを載せない）。
graphql_status() {
  local out
  out="$(printf '{"query":"{pages{list(orderBy:ID){id}}}"}' \
        | wiki_post /graphql || true)"
  http_status "$out"
}

# ---------------------------------------------------------------- 0) 前提

if ! kubectl -n "$MSP_NS" get deploy "$WIKI_DEPLOY" >/dev/null 2>&1; then
  log "${MSP_NS}/${WIKI_DEPLOY} が無いので何もしない（wikijs.enabled=false 相当）"
  exit 0
fi
kubectl -n "$MSP_NS" rollout status "deploy/${WIKI_DEPLOY}" \
  --timeout="${WIKIJS_ROLLOUT_TIMEOUT:-180s}" >/dev/null 2>&1 \
  || { warn "${WIKI_DEPLOY} が Ready にならない。Ready 後に本 script を再実行すること"; exit 1; }

status="$(graphql_status)"
case "$status" in
  '' | *[!0-9]* )
    warn "wiki-js の /graphql を叩けなかった（status='${status}'）。判定不能なので何もしない"
    exit 1
    ;;
esac
log "/graphql の現在の応答: HTTP ${status}"

# ---------------------------------------------------------------- 1) 管理者資格情報

admin_password="${WIKIJS_ADMIN_PASSWORD:-}"
if [ -z "$admin_password" ]; then admin_password="$(read_secret "$ADMIN_SECRET" password)"; fi
if [ -z "$admin_password" ]; then
  # 乱数はコンテナ内の node で作る（ホストに openssl を要求しない）。
  admin_password="$(kubectl -n "$MSP_NS" exec "deploy/${WIKI_DEPLOY}" -c "$WIKI_CONTAINER" -- \
    node -e "process.stdout.write(require('crypto').randomBytes(24).toString('base64url'))" 2>/dev/null || true)"
  [ -n "$admin_password" ] || {
    warn "管理者パスワードを生成できなかった（node を実行できない）"; exit 1; }
  log "管理者パスワードを新規生成した（値は Secret ${ADMIN_SECRET} にだけ入る）"
fi
case "$admin_password" in
  *[!A-Za-z0-9_.@:+=-]* )
    warn "WIKIJS_ADMIN_PASSWORD に JSON/シェルで扱えない文字が含まれる。英数と _.@:+=- のみにすること"
    exit 1
    ;;
esac
existing_email="$(read_secret "$ADMIN_SECRET" email)"
if [ -n "$existing_email" ] && [ -z "${WIKIJS_ADMIN_EMAIL:-}" ]; then ADMIN_EMAIL="$existing_email"; fi

# **finalize より先に**保存する。途中で落ちても資格情報が迷子にならないようにする。
write_secret "$ADMIN_SECRET" password "$admin_password"
write_secret "$ADMIN_SECRET" email "$ADMIN_EMAIL"

# ---------------------------------------------------------------- 2) セットアップの finalize

if [ "$status" = "404" ]; then
  log "setup モードを検出した。POST /finalize でセットアップを完了させる（siteUrl=${SITE_URL}）"
  # telemetry は false 固定（外部送信を既定で持ち込まない）。
  body="$(printf '{"adminEmail":%s,"adminPassword":%s,"adminPasswordConfirm":%s,"siteUrl":%s,"telemetry":false}' \
    "$(json_string "$ADMIN_EMAIL")" "$(json_string "$admin_password")" \
    "$(json_string "$admin_password")" "$(json_string "$SITE_URL")")"
  out="$(printf '%s' "$body" | wiki_post /finalize || true)"
  case "$(http_body "$out")" in
    *'"ok":true'*) log "finalize に成功した。本体の起動を待つ" ;;
    *) warn "finalize が失敗した: $(http_body "$out" | head -c 300)"; exit 1 ;;
  esac

  # Wiki.js は finalize 後に setup サーバを落として本体を起動し直す。**その間は接続不能である。**
  ready=0
  for _ in $(seq 1 40); do
    sleep 3
    s="$(graphql_status)"
    case "$s" in ''|*[!0-9]*|404) continue ;; *) ready=1; break ;; esac
  done
  [ "$ready" = "1" ] || { warn "finalize 後も /graphql が立ち上がらなかった"; exit 1; }
  log "/graphql が立ち上がった"
else
  log "セットアップ済み（/graphql が 404 ではない）。finalize は飛ばす"
fi

# ---------------------------------------------------------------- 3) 本文の locale を用意する
#
# 🔴 **setup を終えただけでは同期はまだ成立しない。** `POST /finalize` が入れる locale は
# `en` **ただ 1 つ**（`server/setup.js` が `locales` を `code != 'x'` で全削除してから `en` を 1 行入れる）。
# 一方 WikiService は本文の locale を `ja` 固定で push する（[IADR-0021]・`WikiJsGraphQlClient.Locale`）。
# そのため `pages.create` が **`pages_localecode_foreign` の外部キー違反**で落ちる —— しかも
# Wiki.js は GraphQL 200 を返し、失敗は WikiService 側のエラーキューにしか残らない（#1108 実測）。
#
# **値は WikiService の実装から引く**（ここへ書き写すと、次に片方だけ変わったとき静かに割れる）。
# 追加は `wikijs` DB へ直接入れる —— Wiki.js の `downloadLocale` は `graph.requarks.io` からの
# ダウンロードであり、閉域前提と両立しない。UI の locale 切替は使わない（`namespacing: false`）ので
# `name` / `nativeName` は表示に出ない。
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
WIKI_CLIENT_SRC="$ROOT/src/knowledge/backend/Services/WikiService/Infrastructure/ExternalServices/WikiJsGraphQlClient.cs"
CONTENT_LOCALE="${WIKIJS_CONTENT_LOCALE:-}"
if [ -z "$CONTENT_LOCALE" ] && [ -f "$WIKI_CLIENT_SRC" ]; then
  CONTENT_LOCALE="$(sed -n 's/.*private const string Locale = "\([^"]*\)".*/\1/p' "$WIKI_CLIENT_SRC" | head -n 1)"
fi
if [ -z "$CONTENT_LOCALE" ]; then
  warn "WikiService が使う locale を実装から読めなかった。WIKIJS_CONTENT_LOCALE で指定すること"
else
  if kubectl -n "$INFRA_NS" get deploy postgres >/dev/null 2>&1; then
    kubectl -n "$INFRA_NS" exec -i deploy/postgres -- \
      psql -U "${WIKIJS_DB_USER:-kp}" -d "${WIKIJS_DB_NAME:-wikijs}" -v ON_ERROR_STOP=1 -q -f - >/dev/null <<SQL \
      && log "本文 locale '${CONTENT_LOCALE}' を Wiki.js に用意した（冪等）" \
      || warn "locale '${CONTENT_LOCALE}' を入れられなかった。同期は外部キー違反で落ち続ける"
INSERT INTO locales (code, strings, "isRTL", name, "nativeName", availability, "createdAt", "updatedAt")
VALUES ('${CONTENT_LOCALE}', '{}', false, '${CONTENT_LOCALE}', '${CONTENT_LOCALE}', 0, now()::text, now()::text)
ON CONFLICT (code) DO NOTHING;
SQL
  else
    warn "${INFRA_NS}/postgres が無いので locale を用意できない"
  fi
fi

# ---------------------------------------------------------------- 4) 管理者ログイン

login_out="$(printf '{"query":"mutation($u:String!,$p:String!){authentication{login(username:$u,password:$p,strategy:\\"local\\"){jwt responseResult{succeeded message}}}}","variables":{"u":%s,"p":%s}}' \
  "$(json_string "$ADMIN_EMAIL")" "$(json_string "$admin_password")" \
  | wiki_post /graphql || true)"
jwt="$(http_body "$login_out" | grep -o '"jwt":"[^"]*"' | head -n 1 | sed -e 's/^"jwt":"//' -e 's/"$//')"
if [ -z "$jwt" ]; then
  warn "管理者ログインに失敗した（${ADMIN_EMAIL}）。Secret ${ADMIN_SECRET} のパスワードと Wiki.js の DB が"
  warn "食い違っている可能性がある。復旧手順は deploy/local/wikijs-setup/README.md を参照すること"
  exit 1
fi
log "管理者ログインに成功した"

# ---------------------------------------------------------------- 5) API を有効化する

api_out="$(graphql 'mutation{authentication{setApiState(enabled:true){responseResult{succeeded message}}}}' "$jwt" || true)"
case "$(http_body "$api_out")" in
  *'"succeeded":true'*) log "GraphQL API を有効化した（冪等）" ;;
  *) warn "setApiState に失敗した: $(http_body "$api_out" | head -c 300)"; exit 1 ;;
esac

# ---------------------------------------------------------------- 6) 外部への定期取得を止める
#
# `POST /finalize` は `lang.autoUpdate: true` を焼き込むため、Wiki.js は起動のたびに
# `https://graph.requarks.io` から locale を取りに行く（実測ログ:
# `Syncing locales with Graph endpoint: [ COMPLETED ]`）。telemetry は finalize で false にしているが、
# **これは別経路である。** 閉域前提（外部 CDN・analytics を使わない）に合わせて落とす。冪等。
loc_out="$(graphql 'mutation{localization{updateLocale(locale:"en",autoUpdate:false,namespacing:false,namespaces:["en"]){responseResult{succeeded message}}}}' "$jwt" || true)"
case "$(http_body "$loc_out")" in
  *'"succeeded":true'*) log "locale の自動更新（graph.requarks.io への定期取得）を無効化した" ;;
  *) warn "locale の自動更新を無効化できなかった（外部取得が残る）: $(http_body "$loc_out" | head -c 200)" ;;
esac

# ---------------------------------------------------------------- 7) API キーを用意する

current_key="$(read_secret "$SYNC_SECRET" apiKey)"
key_ok=0
if [ -n "$current_key" ]; then
  probe="$(graphql '{authentication{apiState}}' "$current_key" || true)"
  case "$(http_body "$probe")" in *'"apiState":'*) key_ok=1 ;; esac
fi

if [ "$key_ok" = "1" ]; then
  log "既存の ${SYNC_SECRET}.apiKey は有効（長さ ${#current_key}）。再発行しない"
else
  log "有効な API キーが無いので発行する（name=${API_KEY_NAME} ttl=${API_KEY_TTL}）"
  mint_out="$(graphql "mutation{authentication{createApiKey(name:\"${API_KEY_NAME}\",expiration:\"${API_KEY_TTL}\",fullAccess:true){key responseResult{succeeded message}}}}" "$jwt" || true)"
  new_key="$(http_body "$mint_out" | grep -o '"key":"[^"]*"' | head -n 1 | sed -e 's/^"key":"//' -e 's/"$//')"
  [ -n "$new_key" ] || { warn "createApiKey に失敗した: $(http_body "$mint_out" | head -c 300)"; exit 1; }
  log "API キーを発行した（長さ ${#new_key}・値は表示しない）"

  # Vault が居るなら **そちらにも書く**。ESO（creationPolicy: Owner）が復旧したときに
  # 空文字で上書きされて静かに壊れるのを防ぐ。#458 の供給経路と同じ場所（secret/msp/wikijs-sync）で、
  # **新しいパターンを増やさない**。
  if kubectl -n "$INFRA_NS" get deploy vault >/dev/null 2>&1; then
    kubectl -n "$INFRA_NS" exec -i deploy/vault -- sh -c \
      'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"; read -r K; vault kv put secret/msp/wikijs-sync apiKey="$K" >/dev/null' \
      <<< "$new_key" 2>/dev/null \
      && log "Vault secret/msp/wikijs-sync も更新した" \
      || warn "Vault への書き込みに失敗した（ESO 経路を使っていないなら無害）"
  fi

  write_secret "$SYNC_SECRET" apiKey "$new_key"
  log "Secret ${MSP_NS}/${SYNC_SECRET}.apiKey を更新した"

  # 消費側は env（secretKeyRef）なので **Pod を作り直さないと新しい値を読まない。**
  if kubectl -n "$MSP_NS" get deploy "$SYNC_DEPLOY" >/dev/null 2>&1; then
    kubectl -n "$MSP_NS" rollout restart "deploy/${SYNC_DEPLOY}" >/dev/null 2>&1 || true
    kubectl -n "$MSP_NS" rollout status "deploy/${SYNC_DEPLOY}" --timeout=180s >/dev/null 2>&1 \
      || warn "${SYNC_DEPLOY} の再起動が時間内に終わらなかった"
    log "${SYNC_DEPLOY} を再起動して新しい API キーを読ませた"
  fi
fi

# ---------------------------------------------------------------- 8) Keycloak OIDC ストラテジ（opt-in）
#
# NFR-09, IADR-0095/IADR-0103/IADR-0328, IADR-0342 (#1127・#397 の再起票):
# Wiki.js の OIDC 設定は **Wiki.js の DB（`authentication` テーブル）保持**であり、manifest にも Helm
# values にも無い。したがって up でスタックを起こしただけでは OIDC ログインは**存在しない**し、
# wikijs DB を作り直すたびに消える。ここで冪等に再適用する（realm import・Vault bootstrap と同じ種類の
# 「runtime 設定の再適用」であり、段 3 の locale 投入と同じ経路を使う）。
#
# ## 既定オフである理由
#
# endpoint は **エッジ host（`https://keycloak.localhost`）を前提にする**（IADR-0328）。`LOCALEDGE` 抜きの
# スタックで既定 ON にすると「押せるが 502 になるボタン」を作ることになる。既定オフなら `local` ログイン
# だけが残る（fail-safe）。有効化は `WIKIJS_OIDC=1`。
#
# ## 🔴 DELETE→INSERT にしない
#
# `deploy/local/wiki-oidc/README.md` が載せてきた手作業は `DELETE FROM authentication WHERE
# "strategyKey"='oidc'` → `INSERT` だが、これは **誰か 1 人でも OIDC でログインした後は必ず落ちる**。
# `users.providerKey` が `authentication.key` を参照する外部キー `users_providerkey_foreign` があるためで、
# 稼働クラスタで実測した（#1127）。**行を保存したままの UPSERT** にして、既存行があればその `key` を
# 再利用する（外部キーが切れず、管理 UI 由来の乱数 key の残骸に二重行を作らない）。
#
# ## 🔴 GraphQL の `authentication.updateStrategies` を使わない
#
# 再起動が要らない魅力はあるが、**渡さなかったストラテジを全部削除する**
# （`server/graph/resolvers/authentication.js` の `_.differenceBy(previousStrategies, args.strategies)`）。
# oidc だけを渡すと `local` を消しにいき、トランザクションが無いので oidc を patch した後で例外になる。
# 安全に使うには全ストラテジの往復が要るが、**読みと書きで config の形が違う**（読み `{...propDef,value}` /
# 書き `{v:value}`）ため、往復の実装ミスが `local` の設定を静かに消す。「潰さない」を優先する。
#
# 環境変数（すべて任意）:
#   WIKIJS_OIDC                   `1` のときだけ本段を実行する（既定オフ）
#   WIKIJS_OIDC_CLIENT_SECRET     client secret。未指定なら Secret `wikijs-oidc`（key `client-secret`）を読む
#   WIKIJS_OIDC_CLIENT_ID         既定 wiki-js（realm の client）
#   WIKIJS_OIDC_BROWSER_ISSUER    既定 https://keycloak.localhost/realms/platform（ブラウザが開く 3 つ）
#   WIKIJS_OIDC_SERVER_ISSUER     既定 http://keycloak:8080/realms/platform（wiki-js pod が叩く 2 つ）
#   WIKIJS_OIDC_STRATEGY_KEY      既存行が無いときに使う正準 key
#   WIKIJS_OIDC_DISPLAY_NAME      既定 Keycloak（ログイン画面のボタン文言）
#   WIKIJS_OIDC_AUTOENROLL_GROUP  既定 Guests（未マッピング利用者が落ちる最小権限の床）
oidc_secret=""
if [ "${WIKIJS_OIDC:-}" != "1" ]; then
  log "OIDC ストラテジの投入は既定オフ（有効化は WIKIJS_OIDC=1）。authentication テーブルには触らない"
else
  OIDC_CLIENT_ID="${WIKIJS_OIDC_CLIENT_ID:-wiki-js}"
  OIDC_BROWSER="${WIKIJS_OIDC_BROWSER_ISSUER:-https://keycloak.localhost/realms/platform}"
  OIDC_SERVER="${WIKIJS_OIDC_SERVER_ISSUER:-http://keycloak:8080/realms/platform}"
  OIDC_KEY="${WIKIJS_OIDC_STRATEGY_KEY:-7c1f6f2e-9d3a-4b5c-8e10-000000000001}"
  OIDC_DISPLAY="${WIKIJS_OIDC_DISPLAY_NAME:-Keycloak}"
  OIDC_GROUP="${WIKIJS_OIDC_AUTOENROLL_GROUP:-Guests}"

  # client secret: 明示 env ＞ Secret `wikijs-oidc` ＞ 断念。**取れなければ既存行に触らない**
  # ——空の clientSecret で上書きすると、動いていたログインを黙って壊す。
  # key に `-` を含むので jsonpath はブラケット記法で引く（`.data.client-secret` は解釈が割れる）。
  oidc_secret="${WIKIJS_OIDC_CLIENT_SECRET:-}"
  if [ -z "$oidc_secret" ]; then
    oidc_secret="$(kubectl -n "$MSP_NS" get secret "$OIDC_SECRET" \
      -o "jsonpath={.data['client-secret']}" 2>/dev/null | base64 -d 2>/dev/null || true)"
  fi
fi

if [ "${WIKIJS_OIDC:-}" = "1" ] && [ -z "$oidc_secret" ]; then
  warn "client secret を取得できない（env WIKIJS_OIDC_CLIENT_SECRET も Secret ${MSP_NS}/${OIDC_SECRET} も空）。"
  warn "既存の OIDC 設定には触らずに終了する。供給経路は deploy/local/wiki-oidc/README.md を参照すること"
elif [ "${WIKIJS_OIDC:-}" = "1" ]; then
  # 値は SQL の単一引用符文字列と JSON へそのまま埋める。**危険な文字を弾いてから埋める**
  # （エスケープを書くより、通す文字を絞るほうが破れにくい）。
  for v in "$oidc_secret" "$OIDC_CLIENT_ID" "$OIDC_BROWSER" "$OIDC_SERVER" "$OIDC_KEY" \
           "$OIDC_DISPLAY" "$OIDC_GROUP" "$SITE_URL"; do
    case "$v" in
      *[!A-Za-z0-9_.:/@+=-]* )
        warn "OIDC の設定値に SQL/JSON へ素で埋められない文字が含まれる。英数と _.:/@+=- のみにすること"
        exit 1
        ;;
    esac
  done

  # config の 14 プロパティは wiki-js の `modules/authentication/oidc/definition.yml` と 1 対 1。
  # 🔴 **5 つの URL を揃えない**（IADR-0328・#780）。ブラウザが開く 3 つ（authorization / issuer /
  #   logout）はエッジ host、wiki-js pod がサーバ側で叩く 2 つ（token / userinfo）は in-cluster にする。
  #   揃えるとローカル CA を wiki-js コンテナへ配る必要が出る。
  oidc_config="$(printf '%s' "{\
\"clientId\":\"${OIDC_CLIENT_ID}\",\
\"clientSecret\":\"${oidc_secret}\",\
\"authorizationURL\":\"${OIDC_BROWSER}/protocol/openid-connect/auth\",\
\"tokenURL\":\"${OIDC_SERVER}/protocol/openid-connect/token\",\
\"userInfoURL\":\"${OIDC_SERVER}/protocol/openid-connect/userinfo\",\
\"skipUserProfile\":false,\
\"issuer\":\"${OIDC_BROWSER}\",\
\"emailClaim\":\"email\",\
\"displayNameClaim\":\"preferred_username\",\
\"pictureClaim\":\"picture\",\
\"mapGroups\":true,\
\"groupsClaim\":\"groups\",\
\"logoutURL\":\"${OIDC_BROWSER}/protocol/openid-connect/logout\",\
\"acrValues\":\"\"}")"

  if ! kubectl -n "$INFRA_NS" get deploy postgres >/dev/null 2>&1; then
    warn "${INFRA_NS}/postgres が無いので OIDC ストラテジを投入できない"
  else
    # 差分があった行数だけを返す。**0 なら何も変わっていない**＝再起動しない（＝2 回目は no-op）。
    # `settings.host`（Site URL）も同じトランザクションで突き合わせる —— Wiki.js はコールバックを
    # `{Site URL}/login/{key}/callback` で組むため、ここがズレると realm に登録があっても
    # `invalid_redirect_uri` になる。**`/finalize` は初回しか通らない**ので、経路を変えたときに
    # Site URL を動かせるのはこの段だけである。
    oidc_out="$(kubectl -n "$INFRA_NS" exec -i deploy/postgres -- \
      psql -U "${WIKIJS_DB_USER:-kp}" -d "${WIKIJS_DB_NAME:-wikijs}" -v ON_ERROR_STOP=1 -q -At -f - 2>&1 <<SQL
BEGIN;
CREATE TEMP TABLE _wanted ON COMMIT DROP AS
SELECT
  COALESCE(
    (SELECT a.key FROM authentication a WHERE a."strategyKey" = 'oidc' ORDER BY a."order", a.key LIMIT 1),
    '${OIDC_KEY}') AS key,
  true AS "isEnabled",
  '${oidc_config}'::json AS config,
  true AS "selfRegistration",
  '{"v":[]}'::json AS "domainWhitelist",
  (SELECT json_build_object('v', COALESCE(json_agg(g.id ORDER BY g.id), '[]'::json))
     FROM groups g WHERE g.name = '${OIDC_GROUP}') AS "autoEnrollGroups",
  1 AS "order",
  'oidc' AS "strategyKey",
  '${OIDC_DISPLAY}' AS "displayName";
WITH up AS (
  INSERT INTO authentication AS t
    (key, "isEnabled", config, "selfRegistration", "domainWhitelist", "autoEnrollGroups", "order", "strategyKey", "displayName")
  SELECT key, "isEnabled", config, "selfRegistration", "domainWhitelist", "autoEnrollGroups", "order", "strategyKey", "displayName"
    FROM _wanted
  ON CONFLICT (key) DO UPDATE SET
    "isEnabled"        = EXCLUDED."isEnabled",
    config             = EXCLUDED.config,
    "selfRegistration" = EXCLUDED."selfRegistration",
    "domainWhitelist"  = EXCLUDED."domainWhitelist",
    "autoEnrollGroups" = EXCLUDED."autoEnrollGroups",
    "order"            = EXCLUDED."order",
    "strategyKey"      = EXCLUDED."strategyKey",
    "displayName"      = EXCLUDED."displayName"
  WHERE ROW(t."isEnabled", t.config::jsonb, t."selfRegistration", t."domainWhitelist"::jsonb,
            t."autoEnrollGroups"::jsonb, t."order", t."strategyKey", t."displayName")
        IS DISTINCT FROM
        ROW(EXCLUDED."isEnabled", EXCLUDED.config::jsonb, EXCLUDED."selfRegistration",
            EXCLUDED."domainWhitelist"::jsonb, EXCLUDED."autoEnrollGroups"::jsonb,
            EXCLUDED."order", EXCLUDED."strategyKey", EXCLUDED."displayName")
  RETURNING 1
), host_up AS (
  UPDATE settings SET value = json_build_object('v', '${SITE_URL}'), "updatedAt" = now()::text
   WHERE key = 'host'
     AND value::jsonb IS DISTINCT FROM jsonb_build_object('v', '${SITE_URL}')
  RETURNING 1
)
SELECT (SELECT count(*) FROM up) + (SELECT count(*) FROM host_up);
COMMIT;
SQL
    )" || true
    oidc_changed="$(printf '%s' "$oidc_out" | tail -n 1)"
    case "$oidc_changed" in
      '' | *[!0-9]* )
        warn "OIDC ストラテジの投入に失敗した: $(printf '%s' "$oidc_out" | tail -c 400)"
        ;;
      0 )
        log "OIDC ストラテジと Site URL は既に一致している（変更なし・wiki-js は再起動しない）"
        ;;
      * )
        log "OIDC ストラテジ / Site URL を ${oidc_changed} 件更新した（siteUrl=${SITE_URL}・secret は表示しない）"
        # Wiki.js は**起動時に**ストラテジを読む。DB だけ書いても反映されないので作り直す。
        # 🔴 変わったときだけ再起動する —— 無条件に打つと、up の再実行や並行作業のたびに
        #   wiki-js が落ちる（同じクラスタで別の検証が走っている）。
        kubectl -n "$MSP_NS" rollout restart "deploy/${WIKI_DEPLOY}" >/dev/null 2>&1 || true
        kubectl -n "$MSP_NS" rollout status "deploy/${WIKI_DEPLOY}" --timeout=180s >/dev/null 2>&1 \
          || warn "${WIKI_DEPLOY} の再起動が時間内に終わらなかった"
        log "${WIKI_DEPLOY} を再起動して OIDC ストラテジを読ませた"
        ;;
    esac

    # 反映確認（値は出さない）。ここが空なら、ログイン画面に Keycloak のボタンは出ない。
    act_out="$(graphql '{authentication{activeStrategies(enabledOnly:true){key strategy{key} displayName}}}' \
      "$jwt" || true)"
    case "$(http_body "$act_out")" in
      *'"key":"oidc"'*) log "activeStrategies に oidc が出ている（ログイン画面に ${OIDC_DISPLAY} が並ぶ）" ;;
      *) warn "activeStrategies に oidc が見えない。wiki-js のログを確認すること" ;;
    esac
  fi
fi

log "完了。Wiki.js は setup モードを抜けており、同期 API キーが供給されている"

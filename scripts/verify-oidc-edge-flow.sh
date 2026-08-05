#!/usr/bin/env bash
# NFR / FR-05, Issue #466: エッジ経由の OIDC 認証導線（認可コード + PKCE）を実機で通し切る。
#
# 背景:
#   現行の E2E は「バックエンド不要のスモーク」だけで、`/login` への誘導など**認証前**の導線しか
#   見ていない。Keycloak を通した認証後の導線（SPA → 認可 → コード → トークン → BFF）は
#   一度も検証されていない（#466）。本スクリプトはその導線を curl だけで通し切り、
#   どこで壊れているかを名指しできるようにする。ブラウザを使わないため、#466 が目指す
#   CI 実行の土台にもなる。
#
#   経路B のエッジは **Traefik**（k3s 内蔵。Istio ではない。IADR-0091）で、
#   `/bff` → bff-service、catch-all → frontend-service に振っている。
#   issuer は最小案 `http://keycloak:8080` を維持する決定（IADR-0076・deploy/local/edge/README.md）
#   のため、**Keycloak はエッジに出ていない**。したがって手順A（hosts + port-forward）が前提になる。
#
# 実行方法:
#   1) 経路B を起動し、エッジを有効にする（LOCALEDGE=1 / Rancher Desktop は overlay 適用のみ）。
#   2) 手順A を用意する:
#        hosts に `127.0.0.1 keycloak` を追記
#        kubectl -n platform-infra port-forward svc/keycloak 8080:8080
#   3) 本スクリプトを実行する:
#        bash scripts/verify-oidc-edge-flow.sh
#
# 終了コード: 0=全項目 PASS / 1=導線の失敗（FAIL あり） / 2=前提未整備（SKIP。失敗と区別する）
#
# 依存: bash / curl / openssl / node（JWT クレームと JSON の読み取りに使う）。
#
# 副作用: **読み取り専用**。書き込み系エンドポイントは「無トークンで 401 になること」の確認だけを行い、
#         成功する書き込みは一切発行しない。code_verifier は固定値で乱数を使わない（再現可能）。

set -uo pipefail

EDGE_URL="${EDGE_URL:-http://localhost}"
KC_URL="${KC_URL:-http://keycloak:8080}"
REALM="${OIDC_REALM:-microservices-platform}"
CLIENT_ID="${OIDC_CLIENT_ID:-spa-web}"
REDIRECT_URI="${OIDC_REDIRECT_URI:-${EDGE_URL}/callback}"
OIDC_USER="${OIDC_USER:-developer}"
OIDC_PASSWORD="${OIDC_PASSWORD:-developer}"
# 固定の code_verifier（再現可能性のため乱数を使わない。dev 専用の検証値であり秘密ではない）。
CODE_VERIFIER="${OIDC_CODE_VERIFIER:-msp-verify-oidc-edge-flow-fixed-code-verifier-0123456789}"

PASS=0
FAIL=0
hr() { printf -- '----------------------------------------------------------------------\n'; }
pass() { PASS=$((PASS + 1)); printf '  PASS  %s\n' "$*"; }
fail() { FAIL=$((FAIL + 1)); printf '  FAIL  %s\n' "$*"; }
info() { printf '        %s\n' "$*"; }
step() { printf '\n[%s] %s\n' "$1" "$2"; }

for cmd in curl openssl node; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    printf 'ERROR: %s が必要です。\n' "$cmd" >&2
    exit 2
  fi
done

# JSON の 1 フィールドを取り出す（jq を前提にしない）。
json_field() { node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{console.log(JSON.parse(s)[process.argv[1]]??'')}catch{console.log('')}})" "$1"; }

hr
printf 'エッジ経由 OIDC 認証導線の検証（Issue #466 / NFR・FR-05）\n'
printf '  edge   : %s\n' "$EDGE_URL"
printf '  issuer : %s（realm %s / client %s）\n' "$KC_URL" "$REALM" "$CLIENT_ID"
hr

# ---- 前提の確認（未整備は SKIP=2 で終える。導線の失敗と区別する） -----------------
step "前提" "エッジと Keycloak への到達性"
if ! curl -s -o /dev/null -m 5 "$EDGE_URL/"; then
  info "エッジ（$EDGE_URL）へ到達できません。"
  info "経路B のエッジを有効にしてください（deploy/local/edge/README.md）。"
  exit 2
fi
info "エッジ: 到達"
DISCOVERY="$KC_URL/realms/$REALM/.well-known/openid-configuration"
if ! curl -s -o /dev/null -m 5 "$DISCOVERY"; then
  info "Keycloak（$KC_URL）へ到達できません。"
  info "issuer は最小案 $KC_URL を維持する決定のため（IADR-0076）、手順A が要ります:"
  info "  1) hosts に  127.0.0.1 keycloak  を追記"
  info "  2) kubectl -n platform-infra port-forward svc/keycloak 8080:8080"
  info "※ CI でこの前提を用意できないことが #466（E2E の CI 実行）の障害である。"
  exit 2
fi
ISSUER=$(curl -s -m 5 "$DISCOVERY" | json_field issuer)
info "Keycloak: 到達（issuer=$ISSUER）"

# ---- 1) SPA がエッジから配信されるか -------------------------------------------
step "1/9" "エッジから SPA を取得する"
SPA=$(curl -s -m 10 "$EDGE_URL/")
if printf '%s' "$SPA" | grep -qi '<!doctype html'; then
  pass "SPA の HTML が返る"
else
  fail "SPA の HTML が返らない"
fi

# ---- 2) 実行時 config が注入されているか ---------------------------------------
step "2/9" "実行時 config（config.js）の OIDC 設定を読む"
CONFIG_JS=$(curl -s -m 10 "$EDGE_URL/config.js")
CONFIG_AUTHORITY=$(printf '%s' "$CONFIG_JS" | grep -o 'authority: *"[^"]*"' | head -1 | sed 's/.*"\(.*\)"/\1/')
if [ -n "$CONFIG_AUTHORITY" ]; then
  pass "config.js に authority がある（$CONFIG_AUTHORITY）"
  # SPA が使う authority と、本検証が叩く issuer がずれていれば実ブラウザの導線は別物になる。
  case "$CONFIG_AUTHORITY" in
    "$KC_URL"*) info "SPA の authority は検証対象の issuer と一致" ;;
    *) fail "SPA の authority（$CONFIG_AUTHORITY）が検証対象（$KC_URL）と一致しない" ;;
  esac
else
  fail "config.js から authority を読めない（実行時 config が注入されていない）"
fi

# ---- 3) 認可エンドポイント -------------------------------------------------------
step "3/9" "認可エンドポイントへ GET（ログイン画面）"
JAR=$(mktemp)
CHALLENGE=$(printf '%s' "$CODE_VERIFIER" | openssl dgst -binary -sha256 | openssl base64 -A | tr '+/' '-_' | tr -d '=')
AUTH_URL="$KC_URL/realms/$REALM/protocol/openid-connect/auth?client_id=$CLIENT_ID&response_type=code&scope=openid%20profile%20email&redirect_uri=$REDIRECT_URI&state=verify-oidc-edge-flow&code_challenge=$CHALLENGE&code_challenge_method=S256"
LOGIN_HTML=$(curl -s -c "$JAR" -b "$JAR" -m 15 "$AUTH_URL")
FORM_ACTION=$(printf '%s' "$LOGIN_HTML" | grep -o 'action="[^"]*"' | head -1 | sed 's/action="//; s/"$//; s/&amp;/\&/g')
if [ -n "$FORM_ACTION" ]; then
  pass "ログインフォームが返る"
else
  fail "ログインフォームを取得できない（redirect_uri が realm に未登録の可能性）"
  info "$(printf '%s' "$LOGIN_HTML" | head -c 200)"
  rm -f "$JAR"
  hr; printf '結果: PASS %d / FAIL %d\n' "$PASS" "$FAIL"; exit 1
fi

# ---- 4) 資格情報の POST → 認可コード ---------------------------------------------
step "4/9" "資格情報を POST し、redirect の認可コードを取る"
LOCATION=$(curl -s -c "$JAR" -b "$JAR" -m 15 -o /dev/null -D - -X POST "$FORM_ACTION" \
  --data-urlencode "username=$OIDC_USER" --data-urlencode "password=$OIDC_PASSWORD" \
  | grep -i '^location:' | tail -1 | tr -d '\r' | sed 's/^[Ll]ocation: //')
CODE=$(printf '%s' "$LOCATION" | sed -n 's/.*[?&]code=\([^&]*\).*/\1/p')
rm -f "$JAR"
if [ -n "$CODE" ]; then
  pass "認可コードを取得（redirect 先: ${LOCATION%%\?*}）"
else
  fail "認可コードを取得できない（ログイン失敗、または redirect_uri 不一致）"
  info "Location: ${LOCATION:-（無し）}"
  hr; printf '結果: PASS %d / FAIL %d\n' "$PASS" "$FAIL"; exit 1
fi

# ---- 5) トークン交換（PKCE） -----------------------------------------------------
step "5/9" "トークンエンドポイントでコードを交換する（PKCE 検証）"
TOKEN_JSON=$(curl -s -m 15 -X POST "$KC_URL/realms/$REALM/protocol/openid-connect/token" \
  -d "grant_type=authorization_code" -d "client_id=$CLIENT_ID" -d "code=$CODE" \
  --data-urlencode "redirect_uri=$REDIRECT_URI" -d "code_verifier=$CODE_VERIFIER")
ACCESS=$(printf '%s' "$TOKEN_JSON" | json_field access_token)
if [ -n "$ACCESS" ]; then
  pass "access_token を取得（PKCE 検証が通った）"
else
  fail "トークン交換に失敗"
  info "$(printf '%s' "$TOKEN_JSON" | head -c 200)"
  hr; printf '結果: PASS %d / FAIL %d\n' "$PASS" "$FAIL"; exit 1
fi

# ---- 6) クレーム（ABAC の入力） --------------------------------------------------
step "6/9" "トークンのクレームを確認する（ABAC の入力が載っているか）"
CLAIMS=$(printf '%s' "$ACCESS" | cut -d. -f2 | tr '_-' '/+' | base64 -d 2>/dev/null)
for claim in iss preferred_username clearance department; do
  value=$(printf '%s' "$CLAIMS" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{console.log(JSON.parse(s.endsWith('}')?s:s+'}')[process.argv[1]]??'')}catch{console.log('')}})" "$claim")
  if [ -n "$value" ]; then
    pass "クレーム $claim = $value"
  else
    # clearance / department は ABAC の判定入力（BffScopeResolver.ExtractUserAttributes）。
    fail "クレーム $claim が無い"
  fi
done

# ---- 7) エッジ経由で BFF（認証あり） ----------------------------------------------
step "7/9" "エッジ経由で BFF を叩く（認証後の実導線）"
for path in /bff/documents /bff/dashboard/summary /bff/datasources; do
  body_file=$(mktemp)
  code=$(curl -s -m 20 -o "$body_file" -w '%{http_code}' -H "Authorization: Bearer $ACCESS" "$EDGE_URL$path")
  if [ "$code" = "200" ]; then
    pass "$path → 200 $(head -c 60 "$body_file")"
  else
    fail "$path → $code"
  fi
  rm -f "$body_file"
done

# ---- 8) 無トークンの読み取り（現行の設計を測る） ------------------------------------
step "8/9" "無トークンで読み取り系を叩く（現行の設計を測る）"
code=$(curl -s -m 15 -o /dev/null -w '%{http_code}' "$EDGE_URL/bff/documents")
if [ "$code" = "200" ]; then
  pass "GET /bff/documents（無トークン）→ 200"
  info "読み取り系は匿名を許容する現行設計（DocumentBffEndpoints.cs「読み取りは SC-02/03 用に無制限」）。"
  info "データは ABAC の deny-by-default で空に縮退するため漏洩はしないが、"
  info "計画 NFR「全 API OIDC/JWT」との差は #458 が扱う。"
else
  # 401 になっているなら #458 が適用済み＝本スクリプトの想定を更新すること。
  fail "GET /bff/documents（無トークン）→ $code（現行設計は 200。#458 適用済みなら本判定を更新する）"
fi

# ---- 9) 無トークンの書き込み（認証必須の確認） --------------------------------------
step "9/9" "無トークンで書き込み系を叩く（401 になること）"
code=$(curl -s -m 15 -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' \
  -d '{"title":"verify-oidc-edge-flow probe (should be rejected)"}' "$EDGE_URL/bff/documents")
if [ "$code" = "401" ]; then
  pass "POST /bff/documents（無トークン）→ 401"
else
  fail "POST /bff/documents（無トークン）→ $code（401 でなければ認証が効いていない）"
fi

hr
printf '結果: PASS %d / FAIL %d\n' "$PASS" "$FAIL"
if [ "$FAIL" -gt 0 ]; then
  printf '導線に失敗があります。\n'
  exit 1
fi
printf '認証導線は最後まで通りました。\n'
printf '注: 応答が空になるのは ABAC ポリシーが 0 件（deny-by-default）のためです（#517）。\n'
exit 0

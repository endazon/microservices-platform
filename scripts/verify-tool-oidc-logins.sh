#!/usr/bin/env bash
# NFR-09（セキュリティ｜認証・認可）/ NFR-11 / Issue #1163:
#   **ツール側 7 クライアントの OIDC ログイン開始**を機械で測る。
#
# なぜ要るか:
#   `scripts/verify-oidc-edge-flow.sh` は **SPA → BFF の 1 経路専用**である。経路B には
#   ブラウザ OIDC を持つクライアントが他にもあり（bff / grafana / argocd / headlamp /
#   minio / vault / wiki-js）、**そのログイン導線を測る検証器が無かった**。
#   IADR-0328 §実測 の「7 クライアントすべてで通した」は **2026-08-31 に人が手で curl した結果**で、
#   再現も回帰検知もできない。同型の縮退（ストラテジが消える / issuer がずれる /
#   Site URL と realm の redirect が食い違う）は **Pod が Running のまま**表面化しない。
#
#   🔴 実際、本スクリプトの着手時点（2026-09-03）で **Vault の OIDC は既に落ちていた** ——
#      dev Vault はインメモリで、Pod 再起動で `auth/oidc` ごと消える。UI は 200 を返し続ける。
#      **「7/7 で通る」は 4 日で偽になっていた。**
#
# 何を測るか（ブラウザを使わず curl で完結する。**ログインは完了させない**）:
#   (a) ツールのログイン開始が **エッジの Keycloak の認可端点**へ向くこと
#       —— 期待値は **discovery（`authorization_endpoint`）から引く**。列挙で持たない。
#   (b) その認可 URL で **Keycloak のログインフォームが返る**こと
#       —— `redirect_uri` が realm の当該 client に登録済みであることを **Keycloak 自身に判定させる**。
#   (c) **陰性対照**: 未登録の `redirect_uri` は 400 で拒まれること
#       —— これが無いと (b) の PASS は「Keycloak が何でも通している」場合と区別できない。
#
# 🔴 **検証器が検証を切らない**（#1074 / IADR-0328 決定 3）。
#    本スクリプトは **`-k` / `--insecure` を一切持たない**。CA を解決できなければ
#    **測らずに exit 2** で終える。既存スクリプトの「CA が無ければ `-k` へ落ちる」fail-safe は
#    引き継がない —— こちらは **TLS 検証そのものが測定対象の一部**だからである。
#
# 🔴 **段が消えていないことを最後に見る**（#466 / IADR-0255 と同じ形）。
#    到達できないツールは **skip で段を飛ばさず、段を消費して結果を SKIP と記録する**。
#    段数の単一情報源は `scripts/lib/tool-oidc-login.js` の `TOOLS`（TOTAL = 2 × 件数 + 1）。
#
# 実行方法:
#   bash scripts/verify-tool-oidc-logins.sh
#
# 終了コード: 0=全項目 PASS / 1=導線の失敗（FAIL あり） / 2=前提未整備（SKIP。失敗と区別する）
#
# 依存: bash / curl / node。`kubectl` は任意（CA の自動取得とエッジ host の宣言確認に使う）。
#
# 副作用: **読み取り専用**。利用者を作らず、資格情報を送らず、ログインを完了させない。
#   認可 URL の GET は Keycloak に認証セッションを 1 本作るが、放置すれば期限切れで消える。
#   Vault の `auth_url` は UI がログイン画面の描画時に呼ぶのと同じ読み取り操作である。

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LIB="$SCRIPT_DIR/lib/tool-oidc-login.js"

KC_URL="${KC_URL:-https://keycloak.localhost}"
REALM="${OIDC_REALM:-platform}"
EDGE_URL="${EDGE_URL:-https://localhost}"
# 管理 entrypoint（IADR-0091 / IADR-0220。50000 は TLS 終端）。**1 箇所だけで持つ** ——
# ツールごとに URL を並べると、ポート topology が変わったとき片方だけ取り残される。
ADMIN_ORIGIN_FMT="${TOOLS_ADMIN_ORIGIN_FMT:-https://%s.localhost:50000}"

for cmd in curl node; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    printf 'ERROR: %s が必要です。\n' "$cmd" >&2
    exit 2
  fi
done
if [ ! -r "$LIB" ]; then
  printf 'ERROR: 判定ロジック %s を読めません。\n' "$LIB" >&2
  exit 2
fi

# ---- TLS（`-k` を持たない。CA を解決できなければ測らない） --------------------------
CA_BUNDLE="${OIDC_CA_BUNDLE:-}"
if [ -z "$CA_BUNDLE" ] && command -v kubectl >/dev/null 2>&1; then
  CA_BUNDLE="${TMPDIR:-/tmp}/msp-verify-tool-oidc-ca.pem"
  kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\.crt}' 2>/dev/null \
    | base64 -d > "$CA_BUNDLE" 2>/dev/null || true
  [ -s "$CA_BUNDLE" ] || CA_BUNDLE=""
fi
if [ -z "$CA_BUNDLE" ] || [ ! -s "$CA_BUNDLE" ]; then
  printf 'SKIP: ローカル CA を解決できません（cert-manager/local-edge-root-ca）。\n' >&2
  printf '      OIDC_CA_BUNDLE でファイルを与えるか、クラスタへ接続してください。\n' >&2
  printf '      🔴 検証を切って（-k で）測るくらいなら測りません（#1074）。\n' >&2
  exit 2
fi
# Windows の curl は schannel なので私有 CA では失効確認が unknown になり接続ごと落ちる。
# `--ssl-revoke-best-effort` が緩めるのは**失効確認だけ**で、チェーン検証とホスト名照合は
# 有効なまま残る（`--insecure` とは別物である）。対応しているときだけ付ける。
CURL_TLS=(--cacert "$CA_BUNDLE")
if curl --help all 2>/dev/null | grep -q -- '--ssl-revoke-best-effort'; then
  CURL_TLS+=(--ssl-revoke-best-effort)
fi

# ---- 集計 ---------------------------------------------------------------------------
PASS=0
FAIL=0
SKIP=0
STEPS=0
FAILED_TOOLS=""
SKIPPED_TOOLS=""

# 🔴 判定ロジックが返す複合値の区切りは **US（\x1f）であって TAB ではない**。
#    TAB は IFS の空白類なので、`read` が連続する区切り（＝空フィールド）を 1 つに畳み、
#    値が 1 つずつ手前へずれる。**実測（本 PR の実走 1 回目）**: PAR の bff が
#    `redirect_uri=par` と表示され、vault の FAIL が理由なしで出た。
#    母集合の TSV（`node "$LIB" tools`）のほうは**空フィールドを持たない**ので TAB でよい。
FS=$'\x1f'

hr() { printf -- '----------------------------------------------------------------------\n'; }
pass() { PASS=$((PASS + 1)); printf '  PASS  %s\n' "$*"; }
fail() { FAIL=$((FAIL + 1)); printf '  FAIL  %s\n' "$*"; }
skip() { SKIP=$((SKIP + 1)); printf '  SKIP  %s\n' "$*"; }
info() { printf '        %s\n' "$*"; }
step() { STEPS=$((STEPS + 1)); printf '\n[%s/%s] %s\n' "$STEPS" "$TOTAL" "$1"; }

# ---- 母集合（判定ロジック側が単一情報源） --------------------------------------------
TOOLS_TSV="$(node "$LIB" tools)"
TOOL_COUNT=$(printf '%s\n' "$TOOLS_TSV" | grep -c .)
if [ "$TOOL_COUNT" -lt 1 ]; then
  printf 'ERROR: 母集合が空です（%s の TOOLS が壊れている）。\n' "$LIB" >&2
  exit 2
fi
# 🔴 TOTAL は「本来走るべき段数」の単一情報源である。**件数から導く** ——
#    固定値を書くと、ツールを 1 件足したときに門が誤発火する（#1124 と同型）。
#    内訳: ツール 1 件につき段 (a) と段 (b) の 2 本 ＋ 末尾の陰性対照 1 本。
TOTAL=$((TOOL_COUNT * 2 + 1))

tool_origin() {
  case "$1" in
    edge) printf '%s' "$EDGE_URL" ;;
    # shellcheck disable=SC2059  # 書式は env で差し替える宣言であり、意図的に format として使う。
    *) printf "$ADMIN_ORIGIN_FMT" "$1" ;;
  esac
}

hr
printf 'ツール側 OIDC ログイン開始の検証（Issue #1163 / NFR-09・NFR-11）\n'
printf '  issuer : %s（realm %s）\n' "$KC_URL" "$REALM"
printf '  TLS    : 検証する（CA=%s）。-k は持たない\n' "$CA_BUNDLE"
printf '  母集合 : %s クライアント（%s の TOOLS が単一情報源）\n' "$TOOL_COUNT" "scripts/lib/tool-oidc-login.js"
hr

# ---- 前提: discovery から認可端点を引く（**期待値の唯一の出所**） ---------------------
printf '\n[前提] Keycloak の discovery から認可端点を引く\n'
DISCOVERY="$KC_URL/realms/$REALM/.well-known/openid-configuration"
DISCOVERY_BODY="$(curl -sS "${CURL_TLS[@]}" -m 10 "$DISCOVERY" 2>&1)"
AUTHZ_ENDPOINT="$(printf '%s' "$DISCOVERY_BODY" | node "$LIB" authorization-endpoint)"
if [ -z "$AUTHZ_ENDPOINT" ]; then
  info "discovery を引けません: $DISCOVERY"
  info "$(printf '%s' "$DISCOVERY_BODY" | head -c 200)"
  info "エッジ issuer（IADR-0243）が有効か、LOCALEDGE=1 で経路B を起動したか確認してください。"
  exit 2
fi
info "authorization_endpoint = $AUTHZ_ENDPOINT"

# ---- 前提: エッジが宣言している host（ゲート未有効と経路欠落を分けるため） -------------
#
# 🔴 「到達できない」を一律 SKIP にすると、**エッジのルートが消えた事故まで緑になる**。
#    エッジが host を宣言しているのに到達できないなら、それは配備事故＝FAIL である。
#    宣言そのものはクラスタから引く（スクリプトへ host を列挙しない）。
EDGE_HOSTS=""
if command -v kubectl >/dev/null 2>&1; then
  EDGE_HOSTS="$(
    kubectl get ingress -A -o jsonpath='{range .items[*]}{range .spec.rules[*]}{.host}{"\n"}{end}{end}' 2>/dev/null
    kubectl get virtualservice -A -o jsonpath='{range .items[*]}{range .spec.hosts[*]}{@}{"\n"}{end}{end}' 2>/dev/null
  )"
fi
if [ -n "$EDGE_HOSTS" ]; then
  printf '[前提] エッジが宣言している host: %s 件\n' "$(printf '%s\n' "$EDGE_HOSTS" | grep -c .)"
else
  printf '[前提] 🔴 エッジの host 宣言を読めません（kubectl 不在等）。\n'
  printf '        到達できないツールを「未配備」と「経路欠落」に分けられないため、\n'
  printf '        すべて SKIP 側に倒れます（見逃しの可能性を残す）。\n'
fi

# 🔴 **catch-all（host が `*` のルート）を「宣言あり」に数えない。**
#    エッジには `*` のルートが常に 1 本ある（platform frontend）。これを数えると
#    **どのホスト名でも「宣言あり」になり、未配備のツールまで FAIL になる**（本 PR の実走で実測:
#    存在しない `*.invalid` を 7 件とも FAIL と報告した）。`*` は「そのツールを配備した」の
#    証拠にならないので、**完全一致だけを宣言と数える**。
host_declared() { # $1=hostname → 0:宣言あり 1:宣言なし 2:判定不能
  [ -z "$EDGE_HOSTS" ] && return 2
  printf '%s\n' "$EDGE_HOSTS" | grep -Fxq "$1" && return 0
  return 1
}

# ---- ツールごとの「ログイン開始 URL」の取り出し --------------------------------------
#
# 取り方はツールの実装で決まる（redirect / JSON / GraphQL）。**期待値ではなく入力**である。
start_location() { # $1=tool $2=origin $3=kind → 標準出力に URL（取れなければ空）
  local tool="$1" origin="$2" kind="$3" path
  path="$(node "$LIB" start-path "$tool")"
  case "$kind" in
    redirect)
      curl -sS "${CURL_TLS[@]}" -o /dev/null -D - -m 15 "$origin$path" 2>/dev/null \
        | grep -i '^location:' | tail -1 | tr -d '\r' | sed 's/^[Ll]ocation: *//'
      ;;
    json-get)
      curl -sS "${CURL_TLS[@]}" -m 15 "$origin$path" 2>/dev/null | node "$LIB" minio-redirect
      ;;
    json-post)
      # Vault の UI がログイン画面の描画時に呼ぶのと同じ読み取り操作。role は config の
      # `default_role` に合わせる。redirect_uri は **ツール自身の origin から組む**
      # （Vault は allowed_redirect_uris と突き合わせる＝ここも Vault 自身に判定させている）。
      curl -sS "${CURL_TLS[@]}" -m 15 -X POST "$origin$path" \
        -H 'Content-Type: application/json' \
        -d "{\"role\":\"${VAULT_OIDC_ROLE:-default}\",\"redirect_uri\":\"$origin/ui/vault/auth/oidc/oidc/callback\"}" \
        2>/dev/null | node "$LIB" vault-auth-url
      ;;
    wikijs)
      # 🔴 ストラテジのキーは seed が既存値を再利用するため環境ごとに違う。**Wiki.js から引く。**
      local key
      key="$(curl -sS "${CURL_TLS[@]}" -m 15 -X POST "$origin/graphql" \
        -H 'Content-Type: application/json' \
        -d '{"query":"{authentication{activeStrategies(enabledOnly:true){key strategy{key}}}}"}' \
        2>/dev/null | node "$LIB" wikijs-oidc-key)"
      [ -z "$key" ] && return 0
      curl -sS "${CURL_TLS[@]}" -o /dev/null -D - -m 15 "$origin/login/$key" 2>/dev/null \
        | grep -i '^location:' | tail -1 | tr -d '\r' | sed 's/^[Ll]ocation: *//'
      ;;
  esac
}

# ---- 段 (a): ログイン開始が認可端点へ向くか ------------------------------------------
#
# 結果は段 (b) が使うので、ツールごとに認可 URL を控える（連想配列は bash 4 前提だが、
# 本リポジトリの検証器は既に bash 4 の機能を使っている）。
declare -A AUTH_URL_OF=()
declare -A CLIENT_ID_OF=()
FIRST_CLIENT_ID=""

while IFS=$'\t' read -r tool host kind probe; do
  [ -z "${tool:-}" ] && continue
  origin="$(tool_origin "$host")"
  step "$tool: ログイン開始がエッジ Keycloak の認可端点へ向く"
  info "origin=$origin"

  probe_code="$(curl -sS "${CURL_TLS[@]}" -o /dev/null -m 10 -w '%{http_code}' "$origin$probe" 2>/dev/null)"
  case "${probe_code:-000}" in
    2??|3??) reachable=1 ;;
    *) reachable=0 ;;
  esac

  if [ "$reachable" = "0" ]; then
    hostname="${origin#https://}"; hostname="${hostname%%:*}"; hostname="${hostname%%/*}"
    host_declared "$hostname"; declared=$?
    if [ "$declared" = "0" ]; then
      # 🔴 エッジが host を宣言しているのに応答しない＝配備事故。**SKIP にしない。**
      fail "$tool: エッジが host '$hostname' を宣言しているのに $origin$probe が HTTP ${probe_code:-000}"
      FAILED_TOOLS="$FAILED_TOOLS $tool"
    else
      skip "$tool: 未配備とみなす（$origin$probe が HTTP ${probe_code:-000}・host 宣言 $( [ "$declared" = 1 ] && printf 'なし' || printf '判定不能' )）"
      SKIPPED_TOOLS="$SKIPPED_TOOLS $tool"
    fi
    continue
  fi

  loc="$(start_location "$tool" "$origin" "$kind")"
  result="$(printf '%s' "$loc" | node "$LIB" classify-start "$AUTHZ_ENDPOINT" "$origin")"
  IFS="$FS" read -r st cid ruri par reason <<< "$result"
  if [ "$st" = "ok" ]; then
    AUTH_URL_OF["$tool"]="$loc"
    CLIENT_ID_OF["$tool"]="$cid"
    [ -z "$FIRST_CLIENT_ID" ] && FIRST_CLIENT_ID="$cid"
    if [ "$par" = "par" ]; then
      pass "$tool: client_id=$cid → $AUTHZ_ENDPOINT（PAR。redirect_uri は request_uri へ押し込まれている）"
    else
      pass "$tool: client_id=$cid / redirect_uri=$ruri → $AUTHZ_ENDPOINT"
    fi
  else
    fail "$tool: $reason"
    FAILED_TOOLS="$FAILED_TOOLS $tool"
  fi
done <<< "$TOOLS_TSV"

# ---- 段 (b): 認可 URL で Keycloak のログインフォームが返るか --------------------------
while IFS=$'\t' read -r tool host kind probe; do
  [ -z "${tool:-}" ] && continue
  step "$tool: 認可 URL で Keycloak のログインフォームが返る（redirect_uri が realm に登録済み）"
  url="${AUTH_URL_OF[$tool]:-}"
  if [ -z "$url" ]; then
    skip "$tool: 段 (a) で認可 URL を得られていないため測れない（上の判定が原因を持つ）"
    continue
  fi
  body_file="$(mktemp)"
  code="$(curl -sS "${CURL_TLS[@]}" -m 20 -o "$body_file" -w '%{http_code}' "$url" 2>/dev/null)"
  verdict="$(node "$LIB" classify-form "${code:-000}" < "$body_file")"
  rm -f "$body_file"
  IFS="$FS" read -r fst freason <<< "$verdict"
  if [ "$fst" = "form" ]; then
    pass "$tool: ログインフォームが返る（HTTP $code・client=${CLIENT_ID_OF[$tool]:-?}）"
  else
    fail "$tool: $freason"
    FAILED_TOOLS="$FAILED_TOOLS $tool"
  fi
done <<< "$TOOLS_TSV"

# ---- 陰性対照: 未登録の redirect_uri は拒まれるか -------------------------------------
#
# 🔴 これが無いと段 (b) の PASS に意味が無い。**「Keycloak が何でも通している」ときも
#    ログインフォームは返る**からである（#972 / IADR-0252 と同じ型の対照）。
step "陰性対照: 未登録の redirect_uri は Keycloak が 400 で拒む"
if [ -z "$FIRST_CLIENT_ID" ]; then
  # 🔴 「対照を組めない」の理由を 2 つに分ける。**1 件も配備されていないだけ**なら前提未整備
  #    （末尾で exit 2 になる）であり、**配備済みなのに 1 件も通らなかった**なら失敗である。
  #    区別せず FAIL に倒すと、ツールを何も立てていない環境で「導線の失敗」を報告してしまう。
  if [ "$FAIL" -eq 0 ] && [ "$PASS" -eq 0 ]; then
    skip "測れたクライアントが 1 件も無いため対照を組めない（全件が未配備）"
  else
    fail "段 (a) を 1 件も通せていないため対照を組めない（段 (b) の判定は根拠を持たない）"
  fi
else
  neg_url="$(node "$LIB" negative-control-url "$AUTHZ_ENDPOINT" "$FIRST_CLIENT_ID")"
  neg_body="$(mktemp)"
  neg_code="$(curl -sS "${CURL_TLS[@]}" -m 20 -o "$neg_body" -w '%{http_code}' "$neg_url" 2>/dev/null)"
  neg_verdict="$(node "$LIB" classify-negative "${neg_code:-000}" < "$neg_body")"
  rm -f "$neg_body"
  IFS="$FS" read -r nst nreason <<< "$neg_verdict"
  if [ "$nst" = "ok" ]; then
    pass "未登録の redirect_uri は HTTP $neg_code で拒まれた（client=$FIRST_CLIENT_ID・登録検査は効いている）"
  else
    fail "$nreason"
  fi
fi

hr
# 🔴 段が消えていないことを最後に見る（#466 / IADR-0255）。ここが無いと、段を削っても
#    if ガードで静かに飛ばしても PASS が減るだけで EXIT=0（緑）になる。
if [ "$STEPS" -ne "$TOTAL" ]; then
  fail "実行した段が $STEPS 本で、宣言（TOTAL=$TOTAL）と一致しません。段が消えたか、静かに飛ばされています"
fi
printf '結果: PASS %d / FAIL %d / SKIP %d（段 %d/%d）\n' "$PASS" "$FAIL" "$SKIP" "$STEPS" "$TOTAL"
[ -n "$FAILED_TOOLS" ] && printf '落ちたクライアント:%s\n' "$FAILED_TOOLS"
[ -n "$SKIPPED_TOOLS" ] && printf '未配備とみなしたクライアント:%s\n' "$SKIPPED_TOOLS"

# 🔴 **何も測っていない実行を「緑」と呼ばせない。** 全件が未配備なら前提未整備（2）である。
if [ "$FAIL" -eq 0 ] && [ "$PASS" -eq 0 ]; then
  printf '1 件も測れていません（全クライアントが未配備）。前提未整備として終えます。\n'
  exit 2
fi
if [ "$FAIL" -gt 0 ]; then
  printf 'ツール側 OIDC のログイン開始に失敗があります。\n'
  exit 1
fi
printf 'ツール側 OIDC のログイン開始はすべて成立しています。\n'
printf '注: ログインの**完了**（資格情報 POST → callback → セッション確立）は測っていません（#1163 §射程外）。\n'
exit 0

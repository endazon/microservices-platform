#!/usr/bin/env bash
# NFR（セキュリティ｜認証・認可）/ FR-05, Issue #466:
#   エッジ経由の OIDC 認証導線（認可コード + PKCE）を実機で通し切る。
#   起点 ID の帰属: **認証そのものは非機能要件**（02_requirements §セキュリティ「恒久: 全 API で
#   OIDC/JWT 認証」）であり、FR-05 は ABAC（利用者属性 × 文書属性による絞り込み）の要求である。
#   本スクリプトが FR-05 に触れるのは、その**判定入力**（clearance / department クレーム）が
#   認証を通じて載ることを確認する範囲に限る。
#
#   ⚠️ 本スクリプトが通す経路は ADR-0032（BFF セッション方式 / Token Handler・#439）の移行で無くなる。
#      移行後は BFF が confidential client として交換を行いトークンをブラウザへ渡さないため、
#      手順 3〜6 を Cookie 経由の検証へ書き換えること（作業仕様書 §未決事項）。
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
#   issuer はエッジ host `https://keycloak.localhost` へ移した（IADR-0076 決定3 手順B・IADR-0243・
#   #780 第2段）。pod からの到達は IADR-0227（coredns-custom）が既に可能にしているため、
#   **hosts 追記・port-forward（手順A）は前提にしない**。TLS はローカル CA（local-edge-ca。
#   IADR-0206）の自己署名であり検証しない（`curl -k`。dev 専用の検証スクリプトのため）。
#
# 実行方法:
#   1) 経路B を起動し、エッジを有効にする（LOCALEDGE=1 / Rancher Desktop は overlay 適用のみ）。
#   2) 本スクリプトを実行する:
#        bash scripts/verify-oidc-edge-flow.sh
#
# 終了コード: 0=全項目 PASS / 1=導線の失敗（FAIL あり） / 2=前提未整備（SKIP。失敗と区別する）
#
# 依存: bash / curl / openssl / node（JWT クレームと JSON の読み取りに使う）。
#
# 副作用: **既定では読み取り専用**。書き込み系エンドポイントは「無トークンで 401 になること」の
#         確認だけを行い、成功する書き込みは一切発行しない。code_verifier は固定値で乱数を使わない。
#
#   🔴 例外は `ABAC_POSITIVE=1` を明示したときだけである（既定オフ。#972）。このとき段 10〜13 が
#      有効になり、**文書を 1 件作成する**（正の対照を作るため）。**使い捨てのスタック専用**であり、
#      残しておきたいクラスタに対して立てないこと。
#
#   なぜ書き込みが要るか: ABAC が deny へ縮退すると一覧は「200 ＋ 空リスト」を返す
#   （`BffScopeResolver` が null → `Results.Ok(new List<DocumentDto>())`）。これは
#   「文書が 1 件も無い」とまったく同じ応答であり、**状態コードでは区別できない**。
#   新規スタックには文書が 1 件も無い（初期投入経路が無い）ため、
#   **非空を観測するには作るしかない。** 作らずに済ませると「空で緑」を追認することになる。

set -uo pipefail

EDGE_URL="${EDGE_URL:-https://localhost}"
KC_URL="${KC_URL:-https://keycloak.localhost}"
# エッジ（EDGE_URL・KC_URL とも）はローカル CA の自己署名（local-edge-ca・IADR-0206）。dev 専用の
# 検証スクリプトのため証明書チェーンは検証しない。http:// へ差し替えた場合は無害（curl が -k を無視する）。
CURL_K="-k"
REALM="${OIDC_REALM:-platform}"
CLIENT_ID="${OIDC_CLIENT_ID:-platform-spa}"
REDIRECT_URI="${OIDC_REDIRECT_URI:-${EDGE_URL}/callback}"
OIDC_USER="${OIDC_USER:-developer}"
OIDC_PASSWORD="${OIDC_PASSWORD:-Developer-2026}"
# 固定の code_verifier（再現可能性のため乱数を使わない。dev 専用の検証値であり秘密ではない）。
CODE_VERIFIER="${OIDC_CODE_VERIFIER:-msp-verify-oidc-edge-flow-fixed-code-verifier-0123456789}"

# #972: ABAC の正常系（許可 → 非空）を測る段。既定オフ。**有効にすると文書を 1 件作る。**
ABAC_POSITIVE="${ABAC_POSITIVE:-}"
# 負の対照。realm ロール platform-operator を持つ（＝一覧の RBAC は通る）が、
# ABAC 属性を 1 つも持たないためどのポリシーにもマッチせず deny へ倒れる利用者。
# 🔴 RBAC で落ちる利用者を対照にすると「何が効いたのか」が分からなくなるので、この選び方が要る。
DENY_USER="${ABAC_DENY_USER:-poc-operator}"
DENY_PASSWORD="${ABAC_DENY_PASSWORD:-PocOperator-2026}"

if [ "$ABAC_POSITIVE" = "1" ]; then TOTAL=13; else TOTAL=9; fi

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
if ! curl -s $CURL_K -o /dev/null -m 5 "$EDGE_URL/"; then
  info "エッジ（$EDGE_URL）へ到達できません。"
  info "経路B のエッジを有効にしてください（deploy/local/edge/README.md）。"
  exit 2
fi
info "エッジ: 到達"
DISCOVERY="$KC_URL/realms/$REALM/.well-known/openid-configuration"
if ! curl -s $CURL_K -o /dev/null -m 5 "$DISCOVERY"; then
  info "Keycloak（$KC_URL）へ到達できません。"
  info "エッジ issuer（IADR-0243・#780 第2段）が有効か、LOCALEDGE=1 で経路B を起動したか確認してください。"
  exit 2
fi
ISSUER=$(curl -s $CURL_K -m 5 "$DISCOVERY" | json_field issuer)
info "Keycloak: 到達（issuer=$ISSUER）"

# ---- 1) SPA がエッジから配信されるか -------------------------------------------
step "1/$TOTAL" "エッジから SPA を取得する"
SPA=$(curl -s $CURL_K -m 10 "$EDGE_URL/")
if printf '%s' "$SPA" | grep -qi '<!doctype html'; then
  pass "SPA の HTML が返る"
else
  fail "SPA の HTML が返らない"
fi

# ---- 2) 実行時 config が注入されているか ---------------------------------------
step "2/$TOTAL" "実行時 config（config.js）の OIDC 設定を読む"
CONFIG_JS=$(curl -s $CURL_K -m 10 "$EDGE_URL/config.js")
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

# ---- 認可コード + PKCE でアクセストークンを取る（利用者ごとに呼べる） ----------------
#
# #972 で負の対照（別の利用者）が要るようになったため、段 3〜5 の手順を関数へ切り出した。
# 🔴 **コマンド置換（`$( )`）で呼ばないこと。** サブシェルになると PASS / FAIL の集計が失われる。
#    結果は ACQUIRED_TOKEN / ACQUIRE_ERR というグローバルへ入れる。
#
# $1=利用者 $2=パスワード $3=verbose（1 なら段 3〜5 として PASS を刻む。0 なら黙って取る）
# 戻り値: 0=取得できた / 1=取得できなかった（ACQUIRE_ERR に理由）
ACQUIRED_TOKEN=""
ACQUIRE_ERR=""
acquire_token() {
  local user="$1" password="$2" verbose="$3"
  local jar challenge auth_url login_html form_action location code token_json
  ACQUIRED_TOKEN=""
  ACQUIRE_ERR=""

  [ "$verbose" = "1" ] && step "3/$TOTAL" "認可エンドポイントへ GET（ログイン画面）"
  jar=$(mktemp)
  challenge=$(printf '%s' "$CODE_VERIFIER" | openssl dgst -binary -sha256 | openssl base64 -A | tr '+/' '-_' | tr -d '=')
  auth_url="$KC_URL/realms/$REALM/protocol/openid-connect/auth?client_id=$CLIENT_ID&response_type=code&scope=openid%20profile%20email&redirect_uri=$REDIRECT_URI&state=verify-oidc-edge-flow&code_challenge=$challenge&code_challenge_method=S256"
  login_html=$(curl -s $CURL_K -c "$jar" -b "$jar" -m 15 "$auth_url")
  form_action=$(printf '%s' "$login_html" | grep -o 'action="[^"]*"' | head -1 | sed 's/action="//; s/"$//; s/&amp;/\&/g')
  if [ -z "$form_action" ]; then
    ACQUIRE_ERR="ログインフォームを取得できない（redirect_uri が realm に未登録の可能性）: $(printf '%s' "$login_html" | head -c 160)"
    rm -f "$jar"
    return 1
  fi
  [ "$verbose" = "1" ] && pass "ログインフォームが返る"

  [ "$verbose" = "1" ] && step "4/$TOTAL" "資格情報を POST し、redirect の認可コードを取る"
  location=$(curl -s $CURL_K -c "$jar" -b "$jar" -m 15 -o /dev/null -D - -X POST "$form_action" \
    --data-urlencode "username=$user" --data-urlencode "password=$password" \
    | grep -i '^location:' | tail -1 | tr -d '\r' | sed 's/^[Ll]ocation: //')
  code=$(printf '%s' "$location" | sed -n 's/.*[?&]code=\([^&]*\).*/\1/p')
  rm -f "$jar"
  if [ -z "$code" ]; then
    ACQUIRE_ERR="認可コードを取得できない（ログイン失敗、または redirect_uri 不一致）: Location=${location:-（無し）}"
    return 1
  fi
  [ "$verbose" = "1" ] && pass "認可コードを取得（redirect 先: ${location%%\?*}）"

  [ "$verbose" = "1" ] && step "5/$TOTAL" "トークンエンドポイントでコードを交換する（PKCE 検証）"
  token_json=$(curl -s $CURL_K -m 15 -X POST "$KC_URL/realms/$REALM/protocol/openid-connect/token" \
    -d "grant_type=authorization_code" -d "client_id=$CLIENT_ID" -d "code=$code" \
    --data-urlencode "redirect_uri=$REDIRECT_URI" -d "code_verifier=$CODE_VERIFIER")
  ACQUIRED_TOKEN=$(printf '%s' "$token_json" | json_field access_token)
  if [ -z "$ACQUIRED_TOKEN" ]; then
    ACQUIRE_ERR="トークン交換に失敗: $(printf '%s' "$token_json" | head -c 160)"
    return 1
  fi
  [ "$verbose" = "1" ] && pass "access_token を取得（PKCE 検証が通った）"
  return 0
}

# ---- 3〜5) 主たる利用者のトークンを取る -------------------------------------------
if ! acquire_token "$OIDC_USER" "$OIDC_PASSWORD" 1; then
  fail "$ACQUIRE_ERR"
  hr; printf '結果: PASS %d / FAIL %d\n' "$PASS" "$FAIL"; exit 1
fi
ACCESS="$ACQUIRED_TOKEN"

# ---- 6) クレーム（ABAC の入力） --------------------------------------------------
step "6/$TOTAL" "トークンのクレームを確認する（ABAC の入力が載っているか）"
# JWT ペイロードは base64url かつパディング無しのため、GNU coreutils の base64 -d は
# stderr に `invalid input` を出して exit 1 を返すが、**stdout には正しくデコード結果を書く**（実測）。
# したがって終了コードは見ず、stderr のみ捨てる（エラーの握り潰しではなく既知の挙動への対応）。
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
step "7/$TOTAL" "エッジ経由で BFF を叩く（認証後の実導線）"
for path in /bff/documents /bff/dashboard/summary /bff/datasources; do
  body_file=$(mktemp)
  code=$(curl -s $CURL_K -m 20 -o "$body_file" -w '%{http_code}' -H "Authorization: Bearer $ACCESS" "$EDGE_URL$path")
  if [ "$code" = "200" ]; then
    pass "$path → 200 $(head -c 60 "$body_file")"
  else
    fail "$path → $code"
  fi
  rm -f "$body_file"
done

# ---- 8) 無トークンの読み取り（認証必須の確認） --------------------------------------
#
# 🔴 想定を 200 → 401 へ更新した（#972 で実測）。
#    従前ここは「読み取り系は匿名を許容する現行設計」を前提に **200 を PASS** としていた。
#    しかし #659（2026-08-10）が無認証で到達できた BFF 9 端点を塞ぎ、読み取り群にも
#    `RequireAuthorization()` が付いている（`DocumentBffEndpoints.cs`）。
#    **本スクリプトは自動実行されていなかったため、この陳腐化が 12 日間気づかれなかった。**
#    ——「走っていない検査は、正しさを保証しない」ことの実例なので、経緯ごと残す。
#    （#458 は全 API OIDC/JWT・mTLS・Vault への移行を扱う親 issue で、今も OPEN である。
#      読み取り群の穴だけが #659 で先に閉じた。）
step "8/$TOTAL" "無トークンで読み取り系を叩く（401 になること）"
code=$(curl -s $CURL_K -m 15 -o /dev/null -w '%{http_code}' "$EDGE_URL/bff/documents")
if [ "$code" = "401" ]; then
  pass "GET /bff/documents（無トークン）→ 401（#659 で読み取り群にも認証が要る）"
elif [ "$code" = "200" ]; then
  fail "GET /bff/documents（無トークン）→ 200（#659 で塞いだはずの無認証読み取りが復活している）"
else
  fail "GET /bff/documents（無トークン）→ $code（401 を期待）"
fi

# ---- 9) 無トークンの書き込み（認証必須の確認） --------------------------------------
step "9/$TOTAL" "無トークンで書き込み系を叩く（401 になること）"
code=$(curl -s $CURL_K -m 15 -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' \
  -d '{"title":"verify-oidc-edge-flow probe (should be rejected)"}' "$EDGE_URL/bff/documents")
if [ "$code" = "401" ]; then
  pass "POST /bff/documents（無トークン）→ 401"
else
  fail "POST /bff/documents（無トークン）→ $code（401 でなければ認証が効いていない）"
fi

# ---- 10〜13) ABAC の正常系（#972。ABAC_POSITIVE=1 のときだけ） ----------------------
#
# 🔴 ここが本題である。段 7 は状態コードしか見ないので、認可が deny へ縮退して
#    「200 ＋ 空リスト」になっても PASS する。**直っていても壊れていても同じ緑**になる。
#    正の対照（許可されたら非空）と負の対照（権限が無ければ 0 件）を**対で**置いて区別する。
if [ "$ABAC_POSITIVE" = "1" ]; then

  # ---- 10) 負の対照用のトークン --------------------------------------------------
  step "10/$TOTAL" "負の対照の利用者（$DENY_USER）のトークンを取る"
  if acquire_token "$DENY_USER" "$DENY_PASSWORD" 0; then
    DENY_ACCESS="$ACQUIRED_TOKEN"
    pass "$DENY_USER の access_token を取得"
  else
    DENY_ACCESS=""
    fail "$DENY_USER のトークンを取得できない: $ACQUIRE_ERR"
  fi

  # ---- 11) 正の対照: 許可された主体は作成できる ------------------------------------
  # POST は deny なら Results.Forbid()（403）を返す（DocumentBffEndpoints）。
  # 🔴 したがって 403 か否かは、文書が 1 件も無くても「許可が出たか」を示す。
  step "11/$TOTAL" "許可のある利用者で文書を作成する（deny なら 403 になる）"
  PROBE_TITLE="abac-positive-probe-$$"
  # 属性は abac-seed のポリシーが見る軸（confidentiality）と、利用者側の department を両方入れる。
  # 片方しか入れないと、スコープのフィルタにもう一方の鍵が含まれる構成で不可視になる。
  #
  # 🔴 tags は付けない。DocumentService は辞書に無いタグを 400 にする（SC-05 / #635）ので、
  #    探針が独自のタグを付けると **ABAC とは無関係の 400** で落ち、正の対照が測れなくなる。
  #    実測でこれを踏んだ（`辞書に無いタグです: abac-probe`）。
  #    なお **400 は「ABAC が許可した」ことの証拠でもある** —— deny なら後段へ届かず 403 になる。
  create_body=$(printf '{"title":"%s","originalUri":null,"contentType":"text/plain","attributes":{"confidentiality":"public","department":"engineering"}}' "$PROBE_TITLE")
  body_file=$(mktemp)
  code=$(curl -s $CURL_K -m 30 -o "$body_file" -w '%{http_code}' -X POST \
    -H "Authorization: Bearer $ACCESS" -H 'Content-Type: application/json' \
    -d "$create_body" "$EDGE_URL/bff/documents")
  case "$code" in
    2*)
      pass "POST /bff/documents → $code（ABAC が許可を返した）"
      ;;
    403)
      fail "POST /bff/documents → 403（ABAC が deny へ倒れている。ポリシーが投入されていない疑い）"
      info "$(head -c 200 "$body_file")"
      ;;
    000)
      # 🔴 curl が応答を得られなかった（タイムアウト）。実測でこの形を踏んだ（#972 の変異 M1）。
      #    k8s では ClusterIP に無いポートへ繋ぐと RST が返らず **ハングする**ため、
      #    上流の宛先が不達だと deny へ縮退する前に呼び出し側が待たされる。
      fail "POST /bff/documents → 応答なし（タイムアウト）。上流の宛先が不達の疑い（#342 / #958 の形）"
      ;;
    *)
      fail "POST /bff/documents → $code（作成できない。ABAC 以外の理由の可能性）"
      info "$(head -c 200 "$body_file")"
      ;;
  esac
  rm -f "$body_file"

  # ---- 12) 🔴 正の対照: 一覧が非空であること ----------------------------------------
  # **これが本 issue の中心である。** 「200 ＋ 空リスト」を PASS にしない。
  step "12/$TOTAL" "許可のある利用者の一覧が非空であること（200 ＋ 空リストを PASS にしない）"
  body_file=$(mktemp)
  code=$(curl -s $CURL_K -m 30 -o "$body_file" -w '%{http_code}' \
    -H "Authorization: Bearer $ACCESS" "$EDGE_URL/bff/documents")
  count=$(node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const a=JSON.parse(s);console.log(Array.isArray(a)?a.length:-1)}catch{console.log(-1)}})" < "$body_file")
  has_probe=$(grep -c "$PROBE_TITLE" "$body_file" 2>/dev/null || printf '0')
  if [ "$code" = "000" ]; then
    fail "GET /bff/documents（許可あり）→ 応答なし（タイムアウト）。上流の宛先が不達の疑い（#342 / #958 の形）"
  elif [ "$code" != "200" ]; then
    fail "GET /bff/documents（許可あり）→ $code"
  elif [ "$count" -lt 1 ] 2>/dev/null; then
    fail "GET /bff/documents（許可あり）→ 200 だが $count 件（空）。ABAC が deny へ縮退している疑い"
    info "🔴 状態コードだけを見る判定では、これが PASS になる。それが本判定の存在理由である。"
  elif [ "$has_probe" = "0" ]; then
    fail "一覧は $count 件だが、作成した文書（$PROBE_TITLE）が含まれない"
  else
    pass "GET /bff/documents（許可あり）→ 200・$count 件・作成した文書を含む"
  fi
  rm -f "$body_file"

  # ---- 13) 負の対照: 権限が無ければ 0 件 -------------------------------------------
  # 🔴 これが無いと「全開放でも緑」になる。正の対照だけを足すと逆向きの穴が開く。
  #    $DENY_USER は platform-operator を持つので **RBAC は通る**。落ちるのは ABAC だけである。
  step "13/$TOTAL" "ABAC 属性を持たない利用者の一覧が 0 件であること（全開放を検出する）"
  if [ -z "$DENY_ACCESS" ]; then
    fail "負の対照のトークンが無いため判定できない（段 10 を参照）"
  else
    body_file=$(mktemp)
    code=$(curl -s $CURL_K -m 30 -o "$body_file" -w '%{http_code}' \
      -H "Authorization: Bearer $DENY_ACCESS" "$EDGE_URL/bff/documents")
    deny_count=$(node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const a=JSON.parse(s);console.log(Array.isArray(a)?a.length:-1)}catch{console.log(-1)}})" < "$body_file")
    if [ "$code" = "403" ]; then
      pass "GET /bff/documents（$DENY_USER）→ 403（RBAC で落ちている。ABAC の対照にはならない点に注意）"
    elif [ "$code" != "200" ]; then
      fail "GET /bff/documents（$DENY_USER）→ $code（200 か 403 を期待）"
    elif [ "$deny_count" = "0" ]; then
      pass "GET /bff/documents（$DENY_USER）→ 200・0 件（deny-by-default が効いている）"
    else
      fail "GET /bff/documents（$DENY_USER）→ 200・$deny_count 件（属性が無いのに見えている＝全開放）"
    fi
    rm -f "$body_file"
  fi
fi

hr
printf '結果: PASS %d / FAIL %d\n' "$PASS" "$FAIL"
if [ "$FAIL" -gt 0 ]; then
  printf '導線に失敗があります。\n'
  exit 1
fi
printf '認証導線は最後まで通りました。\n'
if [ "$ABAC_POSITIVE" = "1" ]; then
  printf 'ABAC は正の対照（許可 → 非空）と負の対照（属性なし → 0 件）の両方で確認しました（#972）。\n'
else
  printf '注: 応答が空になるのは ABAC ポリシーが 0 件（deny-by-default）のためです（#517）。\n'
  printf '    空か否かは検査していません。正常系を測るには ABAC_POSITIVE=1 が要ります（#972）。\n'
fi
exit 0

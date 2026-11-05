#!/usr/bin/env bash
# NFR, IADR-0573（Refs GHSA-pvmh-cvmf-pvcp）: ブローカ（platform-infra の RabbitMQ）に、サービスごとのユーザーと権限を
# 宣言（users.txt）どおりに作る・合わせる。**冪等**（何度回しても同じ状態になる）。
#
#   bash deploy/local/rabbitmq-setup/reconcile-users.sh            # 作る・合わせる
#   bash deploy/local/rabbitmq-setup/reconcile-users.sh --check    # 読むだけ（宣言のユーザーが在り、値で認証できるか）
#   bash deploy/local/rabbitmq-setup/reconcile-users.sh --permissions-report   # 読むだけ（権限の絞り込みの差分と、ブローカに在る
#                                                                  #   キュー・exchange・束ねが絞った権限で届くか）。届かないものがあれば exit 1
#
# NFR-18, IADR-0681: 権限の範囲は RABBITMQ_PERMISSIONS で段を選ぶ（取り違えると通信が止まり得るので段に分ける）:
#   vhost  （既定・段 1）: vhost 全体（'.*' '.*' '.*'）のまま。絞り込みは --permissions-report で差分を見るだけ。
#   narrow （段 2）      : permissions.txt（コードから導いた生成物。scripts/gen-rabbitmq-permissions.js）の行を当てる。
#                          当てる前に、ブローカに在るものが絞った権限で届くかを確かめ、届かないものがあれば何も変えずに止める
#                          （RABBITMQ_PERMISSIONS_ALLOW_GAPS=1 で押し切れる）。戻すときは RABBITMQ_PERMISSIONS=vhost で回し直す。
#   どちらの段で当てたかと permissions.txt は ConfigMap rabbitmq-permissions に写し、ブローカの起動の直後の復元が同じ段で作り直す。
#   写すのは 2 回（当てる前に applying、当て終えて確定の段）。途中で止まると applying が残り、復元は vhost 全体で作り直す。
#
# 前提: Secret `rabbitmq-users`（platform-infra）が在る。ESO=1 は externalsecret-rabbitmq-users.yaml が Vault の 2 つの KV から同期し
# （bootstrap.sh が無いときだけ乱数で種を入れる）、ESO を使わない経路は k8s-local-up.sh が既存の値＞乱数で作る（IADR-0577）。
# 本スクリプトは値を作らない。
#
# 手順（ユーザーごと）:
#   1. 在れば change_password、無ければ add_user（パスワードは**標準入力**で渡す。コマンドの引数に載せない）
#   2. set_permissions -p <vhost> <user> <configure> <write> <read>（vhost は 1 つのまま。IADR-0573 決定 2。値は上の段で決まる）
#   3. set_user_tags <user>（タグなし＝管理 UI・管理 API へ入れない）
#   4. authenticate_user で、その値で認証できることを確かめる（できなければ止める）
# 🔴 既定のユーザー（Secret `rabbitmq` の username）と宣言の外のユーザーには触れない（無効化は disable-default-user.sh が
#    切り替えを確かめてから行う。IADR-0577）。
# 🔴 値はどこにも出さない（復号した値は kubectl exec -i の標準入力へ流すだけで、変数にも置かない）。
set -euo pipefail
# 途中で失敗したら、どこで止まったかだけを出す（値は出さない）。
trap 'echo "ERROR: reconcile-users.sh が ${LINENO} 行目で失敗した（値は表示しない）" >&2' ERR

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
INFRA_NS="${INFRA_NS:-platform-infra}"
USERS_FILE="${RABBITMQ_USERS_FILE:-$ROOT/deploy/local/rabbitmq-setup/users.txt}"
SECRET="${RABBITMQ_USERS_SECRET:-rabbitmq-users}"
VHOST="${RABBITMQ_USERS_VHOST:-/}"
TIMEOUT="${RABBITMQ_USERS_TIMEOUT:-180}"
PERMS_FILE="${RABBITMQ_PERMISSIONS_FILE:-$ROOT/deploy/local/rabbitmq-setup/permissions.txt}"
SCOPE="${RABBITMQ_PERMISSIONS:-vhost}"
PERMS_CM="${RABBITMQ_PERMISSIONS_CONFIGMAP:-rabbitmq-permissions}"
MODE=apply
case "${1:-}" in
  "") ;;
  --check) MODE=check ;;
  --permissions-report) MODE=report ;;
  *) echo "ERROR: 未知の引数: $1（受け付けるのは --check と --permissions-report だけ）" >&2; exit 2 ;;
esac
case "$SCOPE" in
  vhost|narrow) ;;
  *) echo "ERROR: RABBITMQ_PERMISSIONS は vhost（段 1・既定）か narrow（段 2）: ${SCOPE}" >&2; exit 2 ;;
esac

# 宣言のユーザー名（コメント・空行を除く 1 列目）。
users() { grep -v '^[[:space:]]*#' "$USERS_FILE" | awk 'NF >= 1 { print $1 }'; }

# 1 回の呼び出しの上限（秒）。届かない・応答しないノードで起動器ごと止まらないよう、上限で切って名指して止める。
CTL_TIMEOUT="${RABBITMQ_CTL_TIMEOUT:-60}"
ctl() {
  if command -v timeout >/dev/null 2>&1; then
    timeout "$CTL_TIMEOUT" kubectl -n "$INFRA_NS" exec -i deploy/rabbitmq -- rabbitmqctl "$@"
  else
    kubectl -n "$INFRA_NS" exec -i deploy/rabbitmq -- rabbitmqctl "$@"
  fi
}
rmqctl() { ctl -q "$@"; }
# パスワードを標準入力で受けるコマンド（add_user / change_password / authenticate_user）。🔴 **引数は「コマンドとユーザー名」の
#   2 つだけにする（-q も付けない）。** rabbitmqctl（3.13）の起動スクリプトは、引数がちょうど 2 つのときだけ標準入力を開き、
#   それ以外は Erlang を -noinput で起こす。そのとき読み込みは返らず、rabbitmqctl は止まる（統合スタックの実測）。
rmqctl_stdin() { ctl "$1" "$2"; }

# Secret のキーの値（base64 のまま。キーが無ければ空）。
secret_b64() {
  kubectl -n "$INFRA_NS" get secret "$SECRET" -o "go-template={{with index .data \"$1\"}}{{.}}{{end}}" 2>/dev/null
}

# rabbitmqctl へ渡すパスワード（標準入力の 1 行）。🔴 **末尾の改行を必ず付ける。** rabbitmqctl はパスワードを
#   「1 行」（改行か EOF まで）読み、前後の空白を落とす。改行が無いと EOF を待つが、kubectl exec -i は手元の EOF を
#   コンテナの標準入力の終わりとして届けないことがあり、そのとき rabbitmqctl は読み終わらずに止まる（統合スタックの実測）。
password_line() { secret_b64 "$1" | base64 -d; printf '\n'; }

# NFR-18, IADR-0681: 絞った権限（permissions.txt の 1 行 = <ユーザー> <configure> <write> <read>）。無ければ空。
declared_perms() { awk -v u="$1" '!/^[[:space:]]*#/ && $1 == u && NF == 4 { print $2 "\t" $3 "\t" $4; exit }' "$PERMS_FILE" 2>/dev/null; }

# 絞った権限で、ブローカに在るもの（キュー・exchange・束ね）が届くかを確かめる。届かないものを 1 行ずつ出す（無ければ何も出さない）。
#   キュー: 宣言（configure）と購読（read）ができるユーザーが居る。exchange: 宣言（configure）できるユーザーが居る。
#   束ね（exchange → キュー）: exchange を読め（read）キューへ書ける（write）ユーザーが居る。既定の exchange（名前が空）と amq.* は見ない。
#   名前の照合は awk の正規表現（ERE）で行う。生成物の正規表現は ^( … | … )$・\.・.* だけで書くので、RabbitMQ（PCRE）と同じに読める。
coverage_gaps() {
  local objects
  objects="$( { rmqctl list_queues --no-table-headers name | awk '{ print "Q\t" $0 }'
                rmqctl list_exchanges --no-table-headers name | awk '{ print "E\t" $0 }'
                rmqctl list_bindings --no-table-headers source_name destination_name destination_kind | awk -F'\t' '$3 == "queue" { print "B\t" $1 "\t" $2 }'
              } )" || { echo "(ブローカの一覧を読めない)"; return 0; }
  awk -F'\t' -v users="$(users | tr '\n' ' ')" '
    BEGIN { n = split(users, us, " "); for (i = 1; i <= n; i++) want[us[i]] = 1 }
    FNR == NR { if ($0 ~ /^[[:space:]]*#/ || NF == 0) next; split($0, f, /[[:space:]]+/); if (f[1] in want) { k++; C[k] = f[2]; W[k] = f[3]; R[k] = f[4] } next }
    function sys(x) { return x == "" || x ~ /^amq\./ }
    $1 == "Q" && !sys($2) { ok = 0; for (i = 1; i <= k; i++) if ($2 ~ C[i] && $2 ~ R[i]) { ok = 1; break } if (!ok) print "キュー " $2 "（宣言・購読できるユーザーが居ない）" }
    $1 == "E" && !sys($2) { ok = 0; for (i = 1; i <= k; i++) if ($2 ~ C[i]) { ok = 1; break } if (!ok) print "exchange " $2 "（宣言できるユーザーが居ない）" }
    $1 == "B" && !sys($2) { ok = 0; for (i = 1; i <= k; i++) if ($2 ~ R[i] && $3 ~ W[i]) { ok = 1; break } if (!ok) print "束ね " $2 " → " $3 "（束ねられるユーザーが居ない）" }
  ' "$PERMS_FILE" - <<<"$objects"
}

# 宣言の全ユーザーに絞った権限の行が在ることを、何かを変える前に確かめる（欠けたまま当てると、そのサービスだけ黙って止まる）。
if [ "$SCOPE" = "narrow" ] || [ "$MODE" = "report" ]; then
  [ -f "$PERMS_FILE" ] || { echo "ERROR: 絞った権限の生成物が無い: ${PERMS_FILE}（node scripts/gen-rabbitmq-permissions.js --write）" >&2; exit 1; }
  missing_perms=""
  for u in $(users); do
    [ -n "$(declared_perms "$u")" ] || missing_perms="${missing_perms} ${u}"
  done
  if [ -n "$missing_perms" ]; then
    echo "ERROR: ${PERMS_FILE} に次のユーザーの行が無い（何も変えていない）:${missing_perms}" >&2
    echo "       作り直す: node scripts/gen-rabbitmq-permissions.js --write" >&2
    exit 1
  fi
fi

# 読むだけ: ユーザーごとの差分（今の権限 → 段 2 の権限）と、ブローカに在るものが段 2 の権限で届くか。
if [ "$MODE" = "report" ]; then
  same=0
  changes=""
  for u in $(users); do
    now="$(rmqctl list_user_permissions --no-table-headers "$u" 2>/dev/null | awk -F'\t' -v v="$VHOST" '$1 == v { print $2 "\t" $3 "\t" $4; exit }')" || now=""
    if [ "$now" = "$(declared_perms "$u")" ]; then same=$((same + 1)); else changes="${changes} ${u}"; fi
  done
  echo "    段 2（RABBITMQ_PERMISSIONS=narrow）の権限と同じユーザー: ${same} 件"
  [ -n "$changes" ] && echo "    段 2 で権限が変わるユーザー:${changes}"
  gaps="$(coverage_gaps)"
  if [ -n "$gaps" ]; then
    echo "ERROR: 段 2 の権限では、ブローカに在る次のものに届かない（このまま当てると通信が止まり得る）:" >&2
    sed 's/^/       /' <<<"$gaps" >&2
    echo "       導出を確かめる: node scripts/gen-rabbitmq-permissions.js --explain" >&2
    exit 1
  fi
  echo "    ブローカに在るキュー・exchange・束ねは、段 2 の権限ですべて届く"
  exit 0
fi

# ESO の同期を待つ（宣言の全ユーザーのキーが揃うまで）。
deadline=$((SECONDS + TIMEOUT))
while :; do
  missing=""
  for u in $(users); do
    [ -n "$(secret_b64 "$u")" ] || missing="${missing} ${u}"
  done
  [ -z "$missing" ] && break
  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "ERROR: ${INFRA_NS}/${SECRET} に次のユーザーの値が ${TIMEOUT}s 以内に揃わない:${missing}" >&2
    echo "       調べる: kubectl -n ${INFRA_NS} describe externalsecret ${SECRET}（Vault の msp/rabbitmq-users・ai-stock-trading/rabbitmq-users）" >&2
    echo "       種は deploy/local/vault/eso/bootstrap.sh が無いときだけ入れる（値は表示しない）" >&2
    exit 1
  fi
  sleep 5
done

existing="$(rmqctl list_users --formatter json | tr ',' '\n' | sed -n 's/.*"user":"\([^"]*\)".*/\1/p')"

# 段 2 は、当てる前にブローカに在るものが届くかを確かめる（届かなければ何も変えずに止める）。
if [ "$MODE" = "apply" ] && [ "$SCOPE" = "narrow" ]; then
  gaps="$(coverage_gaps)"
  if [ -n "$gaps" ] && [ "${RABBITMQ_PERMISSIONS_ALLOW_GAPS:-0}" != "1" ]; then
    echo "ERROR: 段 2 の権限では、ブローカに在る次のものに届かない（何も変えていない）:" >&2
    sed 's/^/       /' <<<"$gaps" >&2
    echo "       押し切るときは RABBITMQ_PERMISSIONS_ALLOW_GAPS=1（届かないものを使うサービスは止まる）" >&2
    exit 1
  fi
fi

# NFR-18, IADR-0681: 段の記録（ConfigMap。値は持たない）。当てる前に「applying」と書き、全ユーザーを当て終えてから確定の段へ書き換える。
#   途中で失敗・中断すると applying が残り、ブローカの起動の直後の復元は広い側（vhost 全体）で作り直す ——
#   絞る途中で止まっても、広げる途中で止まっても、再起動の後は通信を止めない側に倒れる（段 2 は合わせを回し直して確定させる）。
#   値は引数（--from-literal）に載せず、ファイル（--from-file）で渡す（起動器の約束。段の値は秘密ではないが同じ形に揃える）。
SCOPE_DIR=""
trap 'rm -rf -- "${SCOPE_DIR:-/nonexistent-scope-dir}"' EXIT
record_scope() {
  [ -n "$SCOPE_DIR" ] || SCOPE_DIR="$(mktemp -d)"
  printf '%s' "$1" > "$SCOPE_DIR/scope"
  if [ -f "$PERMS_FILE" ]; then
    kubectl -n "$INFRA_NS" create configmap "$PERMS_CM" --from-file=scope="$SCOPE_DIR/scope" --from-file=permissions.txt="$PERMS_FILE" \
      --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  else
    kubectl -n "$INFRA_NS" create configmap "$PERMS_CM" --from-file=scope="$SCOPE_DIR/scope" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  fi
}
[ "$MODE" = "apply" ] && record_scope applying

n=0
fail=""
# NFR, #2200: pipefail の下で「… | grep -q」は使わない（grep が一致で読むのをやめ、書き手が SIGPIPE で落ちて判定が反転する）。
#   在るかどうかは here-string で grep へ渡す（パイプを使わない）。
for u in $(users); do
  if [ "$MODE" = "apply" ]; then
    if grep -Fxq -- "$u" <<<"$existing"; then
      password_line "$u" | rmqctl_stdin change_password "$u" >/dev/null
    else
      password_line "$u" | rmqctl_stdin add_user "$u" >/dev/null
    fi
    # NFR, #2194: Windows の Git Bash（MSYS2）は、ネイティブの kubectl.exe へ渡す引数のうち `/` で始まるもの（vhost の `/`）を
    #   Git の導入先のパス（`C:/Program Files/Git/`）へ書き換える。この呼び出しだけ書き換えを止める（他の環境では効かない変数）。
    #   起動器全体に付けないのは、起動器が一時ファイルのパスを kubectl へ渡すため（そちらは書き換えが要る）。
    # NFR-18, IADR-0681: 段 1（vhost）は vhost 全体、段 2（narrow）は permissions.txt の行（上で全ユーザーの行が在ることを確かめた）。
    if [ "$SCOPE" = "narrow" ]; then
      IFS=$'\t' read -r p_conf p_write p_read <<<"$(declared_perms "$u")"
    else
      p_conf='.*'; p_write='.*'; p_read='.*'
    fi
    MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' rmqctl set_permissions -p "$VHOST" "$u" "$p_conf" "$p_write" "$p_read" >/dev/null
    rmqctl set_user_tags "$u" >/dev/null
  elif ! grep -Fxq -- "$u" <<<"$existing"; then
    fail="${fail} ${u}(無い)"
    continue
  fi
  if ! password_line "$u" | rmqctl_stdin authenticate_user "$u" >/dev/null 2>&1; then
    fail="${fail} ${u}(認証できない)"
    continue
  fi
  n=$((n + 1))
done

if [ -n "$fail" ]; then
  echo "ERROR: ブローカのユーザーが宣言と合わない（値は表示しない）:${fail}" >&2
  [ "$MODE" = "check" ] && echo "       合わせる: bash deploy/local/rabbitmq-setup/reconcile-users.sh" >&2
  exit 1
fi
# NFR-18, IADR-0681: 全ユーザーを当て終えたので、段を確定させる（復元はこの段で作り直す）。
if [ "$MODE" = "apply" ]; then
  if [ "$SCOPE" = "narrow" ] && [ ! -f "$PERMS_FILE" ]; then echo "ERROR: 生成物が無い（段を確定できない）" >&2; exit 1; fi
  record_scope "$SCOPE"
  if [ "$SCOPE" = "vhost" ]; then
    echo "    権限は段 1（vhost 全体）。段 2 への切り替えの点検: bash deploy/local/rabbitmq-setup/reconcile-users.sh --permissions-report"
  fi
fi
echo "    ブローカのサービスごとのユーザー: ${n} 件（${MODE}。vhost ${VHOST}。権限 ${SCOPE}。値は表示しない）"

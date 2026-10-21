#!/usr/bin/env bash
# NFR, IADR-0573（Refs GHSA-pvmh-cvmf-pvcp）: ブローカ（platform-infra の RabbitMQ）に、サービスごとのユーザーと権限を
# 宣言（users.txt）どおりに作る・合わせる。**冪等**（何度回しても同じ状態になる）。
#
#   bash deploy/local/rabbitmq-setup/reconcile-users.sh            # 作る・合わせる
#   bash deploy/local/rabbitmq-setup/reconcile-users.sh --check    # 読むだけ（宣言のユーザーが在り、値で認証できるか）
#
# 前提: Secret `rabbitmq-users`（platform-infra）が在る。ESO=1 は externalsecret-rabbitmq-users.yaml が Vault の 2 つの KV から同期し
# （bootstrap.sh が無いときだけ乱数で種を入れる）、ESO を使わない経路は k8s-local-up.sh が既存の値＞乱数で作る（IADR-0577）。
# 本スクリプトは値を作らない。
#
# 手順（ユーザーごと）:
#   1. 在れば change_password、無ければ add_user（パスワードは**標準入力**で渡す。コマンドの引数に載せない）
#   2. set_permissions -p <vhost> <user> '.*' '.*' '.*'（vhost は 1 つのまま。IADR-0573 決定 2）
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
MODE=apply
case "${1:-}" in
  "") ;;
  --check) MODE=check ;;
  *) echo "ERROR: 未知の引数: $1（受け付けるのは --check だけ）" >&2; exit 2 ;;
esac

# 宣言のユーザー名（コメント・空行を除く 1 列目）。
users() { grep -v '^[[:space:]]*#' "$USERS_FILE" | awk 'NF >= 1 { print $1 }'; }

rmqctl() { kubectl -n "$INFRA_NS" exec -i deploy/rabbitmq -- rabbitmqctl -q "$@"; }

# Secret のキーの値（base64 のまま。キーが無ければ空）。
secret_b64() {
  kubectl -n "$INFRA_NS" get secret "$SECRET" -o "go-template={{with index .data \"$1\"}}{{.}}{{end}}" 2>/dev/null
}

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

n=0
fail=""
for u in $(users); do
  if [ "$MODE" = "apply" ]; then
    if printf '%s\n' "$existing" | grep -Fxq -- "$u"; then
      secret_b64 "$u" | base64 -d | rmqctl change_password "$u" >/dev/null
    else
      secret_b64 "$u" | base64 -d | rmqctl add_user "$u" >/dev/null
    fi
    rmqctl set_permissions -p "$VHOST" "$u" '.*' '.*' '.*' >/dev/null
    rmqctl set_user_tags "$u" >/dev/null
  elif ! printf '%s\n' "$existing" | grep -Fxq -- "$u"; then
    fail="${fail} ${u}(無い)"
    continue
  fi
  if ! secret_b64 "$u" | base64 -d | rmqctl authenticate_user "$u" >/dev/null 2>&1; then
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
echo "    ブローカのサービスごとのユーザー: ${n} 件（${MODE}。vhost ${VHOST}。値は表示しない）"

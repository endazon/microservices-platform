#!/usr/bin/env bash
# NFR-18, IADR-0577（Refs GHSA-rh44-px44-p48q）: ブローカの既定のユーザー（Secret `rabbitmq` の username）を無効化する
# （IADR-0573 の移行の段 3 の最後）。**全サービスがサービスごとのユーザーへ切り替わったことを確かめてからだけ**消す。
#
#   bash deploy/local/rabbitmq-setup/disable-default-user.sh           # 確かめて、消す（冪等）
#   bash deploy/local/rabbitmq-setup/disable-default-user.sh --check   # 読むだけ（既定のユーザーが残っていれば 1）
#
# 手順:
#   1. 宣言（users.txt）の全ユーザーが在り、その値で認証できる（reconcile-users.sh --check）。できなければ止める（exit 1）。
#   2. 既定のユーザーで張られている接続を数える。1 本でも在れば消さない（exit 3。切り替わっていないサービスがある）。
#   3. 無効化の印（ConfigMap `rabbitmq-default-user` の disabled=true）を**先に**置き、それから既定のユーザーを消す。
#      ブローカは永続化しておらず、再起動のたびに既定のユーザーを Secret から作り直す。印があれば、起動の直後の復元
#      （deploy/local/infra/rabbitmq.yaml の postStart）がサービスごとのユーザーを揃えた後に消し直す。
# 戻し方（既定のユーザーが要るとき）: kubectl -n platform-infra delete configmap rabbitmq-default-user のあと
#   kubectl -n platform-infra rollout restart deploy/rabbitmq（全接続が一度切れる）。
# 🔴 値はどこにも出さない（既定のユーザーのパスワードは読まない）。
set -euo pipefail
trap 'echo "ERROR: disable-default-user.sh が ${LINENO} 行目で失敗した（値は表示しない）" >&2' ERR

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
INFRA_NS="${INFRA_NS:-platform-infra}"
FLAG_CM="${RABBITMQ_DEFAULT_USER_FLAG:-rabbitmq-default-user}"
MODE=apply
case "${1:-}" in
  "") ;;
  --check) MODE=check ;;
  *) echo "ERROR: 未知の引数: $1（受け付けるのは --check だけ）" >&2; exit 2 ;;
esac

rmqctl() { kubectl -n "$INFRA_NS" exec -i deploy/rabbitmq -- rabbitmqctl -q "$@"; }

default_user="$(kubectl -n "$INFRA_NS" get secret rabbitmq -o 'go-template={{with .data.username}}{{.}}{{end}}' 2>/dev/null | base64 -d 2>/dev/null || true)"
if [ -z "$default_user" ]; then
  echo "ERROR: ${INFRA_NS}/rabbitmq の username を読めない（既定のユーザーの名前が分からないので何もしない）" >&2
  exit 1
fi

present=0
if rmqctl list_users --formatter json | tr ',' '\n' | sed -n 's/.*"user":"\([^"]*\)".*/\1/p' | grep -Fxq -- "$default_user"; then
  present=1
fi

if [ "$MODE" = "check" ]; then
  if [ "$present" = "1" ]; then
    echo "    既定のユーザー: 残っている（消す: bash deploy/local/rabbitmq-setup/disable-default-user.sh）"
    exit 1
  fi
  echo "    既定のユーザー: 無い（無効化済み）"
  exit 0
fi

# 1. サービスごとのユーザーが揃っていること（揃っていなければ、消すと切り替えた分まで繋がらなくなる）。
if ! RABBITMQ_USERS_TIMEOUT="${RABBITMQ_USERS_TIMEOUT:-60}" bash "$ROOT/deploy/local/rabbitmq-setup/reconcile-users.sh" --check; then
  echo "ERROR: サービスごとのユーザーが揃っていないので、既定のユーザーを消さない" >&2
  exit 1
fi

# 2. 既定のユーザーで張られている接続（切り替わっていないサービス）。
conns="$(rmqctl list_connections user --formatter json | tr ',' '\n' | sed -n 's/.*"user":"\([^"]*\)".*/\1/p' | grep -Fxc -- "$default_user" || true)"
if [ "${conns:-0}" -gt 0 ]; then
  echo "WARN: 既定のユーザーの接続が ${conns} 本ある。切り替わっていないサービスがあるので消さない" >&2
  echo "      調べる: kubectl -n ${INFRA_NS} exec deploy/rabbitmq -- rabbitmqctl list_connections user peer_host" >&2
  exit 3
fi

# 3. 印を先に置く（消した後にブローカが再起動しても、起動の直後の復元が消し直す）。
kubectl -n "$INFRA_NS" create configmap "$FLAG_CM" --from-literal=disabled=true --dry-run=client -o yaml \
  | kubectl apply -f - >/dev/null
if [ "$present" = "1" ]; then
  rmqctl delete_user "$default_user" >/dev/null
  echo "    既定のユーザーを消した（印 ${INFRA_NS}/${FLAG_CM} を置いた。戻し方はこのスクリプトの冒頭）"
else
  echo "    既定のユーザーは既に無い（印 ${INFRA_NS}/${FLAG_CM} を置いた）"
fi

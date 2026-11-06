#!/usr/bin/env bash
# NFR-18, ADR-0124 決定 1, IADR-0672 (#2399): **稼働中の基盤の資格情報が、宣言の開発用の値のままかを確かめる**（読むだけ）。
#
#   bash scripts/check-infra-credentials.sh --live
#
# 見るもの（名前だけを出す。🔴 値は出さない・どのプロセスの引数にも載せない）:
#   1. Secret の値 —— IADR-0574 の 2 つ（Keycloak の管理者・ブローカ）と IADR-0672 の段階の対象を、開発用の値と比べる。
#      対象と開発用の値は scripts/lib/infra-credentials.sh が持つ（ここへ複写しない）。
#   2. ストア側 —— Secret を変えてもストアの値は変わらないことがある（DB 利用者は空の PVC の初回にだけ作られる）。
#      - DB 利用者 postgres・kp・ai: Pod の IP へ開発用の値でログインできるか（127.0.0.1 は認証しない設定があり得るので使わない）。
#        先に誤った値でも試し、誤った値でもログインできるなら「確かめられない」とする（認証しない設定）。
#      - 秘匿管理の固定の管理者トークン（Vault を置いているときだけ）: 開発用の値で通るか。
#      比べる値は Pod の中の sh へ stdin で渡す（引数に載せない）。
#
# 終了コード: 0＝開発用の値のものは無い / 1＝開発用の値のものがある / 2＝確かめられないものがある（1 が優先） / 3＝--live が無い。
# 作り直しは scripts/rotate-infra-credentials.sh --live（値は表示しない）。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"
live_opt_in_require "check-infra-credentials.sh"
if [ "$#" -gt 0 ]; then
  echo "ERROR: 未知の引数: $*（受け付けるのは --live だけ）" >&2
  exit 2
fi

# shellcheck source=scripts/lib/dev-client-secret-guard.sh
. "$ROOT/scripts/lib/dev-client-secret-guard.sh"
# shellcheck source=scripts/lib/infra-credentials.sh
. "$ROOT/scripts/lib/infra-credentials.sh"

INFRA_NS="${INFRA_NS:-platform-infra}"
MSP_NS="${MSP_NS:-microservices-platform}"
found=""
unknown=""
add_found() { found="${found:+$found, }$1"; }
add_unknown() { unknown="${unknown:+$unknown, }$1"; }

# secret_value <名前空間> <Secret 名> <キー> → 値（無ければ空・終了コード 1）。値は呼び出し側の変数へ受ける。
secret_value() {
  local raw
  raw="$(kubectl -n "$1" get secret "$2" -o "jsonpath={.data.$3}" 2>/dev/null)" || return 1
  [ -n "$raw" ] || return 1
  printf '%s' "$raw" | base64 -d 2>/dev/null
}

echo "==> 1. Secret の値（開発用の値と比べる。値は表示しない）"
check_secret() { # <表示名> <名前空間> <Secret 名> <キー> <開発用の値>
  local v
  if ! v="$(secret_value "$2" "$3" "$4")"; then
    echo "    -  $1（Secret $2/$3/$4）: 無い（作っていない構成か、名前空間が違う）"
    return 0
  fi
  if [ "$v" = "$5" ]; then
    echo "    !! $1（Secret $2/$3/$4）: 開発用の値のまま"
    add_found "$1"
  else
    echo "    ok $1（Secret $2/$3/$4）"
  fi
}
for name in $INFRA_CRED_TARGETS; do
  loc="$(infra_cred_secret_of "$name")"
  check_secret "$name" "$INFRA_NS" "${loc% *}" "${loc#* }" "$(infra_cred_dev_value "$name")"
done
for name in $(VAULT=1 infra_cred_staged_names); do
  loc="$(infra_cred_staged_secret_of "$name")"
  read -r kind sname skey <<< "$loc"
  ns="$INFRA_NS"
  [ "$kind" = "msp" ] && ns="$MSP_NS"
  check_secret "$name" "$ns" "$sname" "$skey" "$(infra_cred_staged_dev_value "$name")"
done

# NFR-18, IADR-0676 決定 3 (#2407): Grafana の内蔵の管理者（Grafana を置いているときだけ）。Secret が無ければ、製品は
#   リポジトリが与えない値で管理者を作るので名指す。値は起動のたびに Secret から DB へ合わせる（永続化の overlay）。
if kubectl -n "$INFRA_NS" get deploy grafana -o name >/dev/null 2>&1; then
  if v="$(secret_value "$INFRA_NS" grafana-admin password)"; then
    if [ "$v" = "admin" ]; then
      echo "    !! GRAFANA_ADMIN_PASSWORD（Secret $INFRA_NS/grafana-admin/password）: 開発用の値のまま"
      add_found "GRAFANA_ADMIN_PASSWORD"
    else
      echo "    ok GRAFANA_ADMIN_PASSWORD（Secret $INFRA_NS/grafana-admin/password）"
    fi
  else
    echo "    !! GRAFANA_ADMIN_PASSWORD（Secret $INFRA_NS/grafana-admin/password）: 無い（Grafana が在るのに管理者の値を与えていない）"
    add_found "GRAFANA_ADMIN_PASSWORD"
  fi
else
  echo "    -  GRAFANA_ADMIN_PASSWORD（deploy/grafana が無い）"
fi

echo "==> 2. ストア側（DB 利用者のログイン・秘匿管理のトークン。値は表示しない）"
# DB 利用者 → 開発用の値。postgres・kp は段階の対象と同じ値、ai は deploy/local/infra/postgres.yaml の初期化が作る値（取引ユニット）。
pg_roles="postgres:$(infra_cred_staged_dev_value PG_PASSWORD) kp:$(infra_cred_staged_dev_value APP_DB_PASSWORD) ai:ai"
if kubectl -n "$INFRA_NS" get deploy postgres -o name >/dev/null 2>&1; then
  # Pod の中の sh へ stdin でスクリプトを渡す（値は stdin にだけ載る）。各行「<利用者> <dev|ok|unknown>」を返す。
  # 誤った値は乱数で作る（固定の値にしない）。
  wrong="wrong-$(od -An -N8 -tx1 /dev/urandom | tr -d ' \n')"
  if pg_out="$(
    {
      echo 'set -u'
      echo 'h="$(hostname -i 2>/dev/null | cut -d" " -f1)"; [ -n "$h" ] || { echo "host unknown"; exit 0; }'
      echo 'try() { PGPASSWORD="$2" PGCONNECT_TIMEOUT=5 psql -h "$h" -U "$1" -d postgres -w -tAq -c "select 1" >/dev/null 2>&1; }'
      for pair in $pg_roles; do
        role="${pair%%:*}"
        printf "if try '%s' '%s'; then echo '%s unknown'; elif try '%s' '%s'; then echo '%s dev'; else echo '%s ok'; fi\n" \
          "$role" "$wrong" "$role" "$role" "${pair#*:}" "$role" "$role"
      done
    } | kubectl -n "$INFRA_NS" exec -i deploy/postgres -c postgres -- sh -s 2>/dev/null
  )"; then
    for pair in $pg_roles; do
      role="${pair%%:*}"
      state="$(printf '%s\n' "$pg_out" | awk -v r="$role" '$1 == r { print $2; exit }')"
      case "$state" in
        dev) echo "    !! DB 利用者 ${role}: 開発用の値でログインできる"; add_found "DB 利用者 ${role}" ;;
        ok) echo "    ok DB 利用者 ${role}" ;;
        *) echo "    ?? DB 利用者 ${role}: 確かめられない（誤った値でもログインできる・Pod の IP が読めない・応答が無い）"; add_unknown "DB 利用者 ${role}" ;;
      esac
    done
  else
    echo "    ?? DB: Pod の中で確かめられない（kubectl exec が失敗した）"
    add_unknown "DB"
  fi
else
  echo "    -  DB（deploy/postgres が無い）"
fi
if kubectl -n "$INFRA_NS" get deploy vault -o name >/dev/null 2>&1; then
  if v_out="$(
    printf "if VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN='%s' vault token lookup >/dev/null 2>&1; then echo dev; else echo ok; fi\n" \
      "$(infra_cred_staged_dev_value VAULT_DEV_ROOT_TOKEN)" \
      | kubectl -n "$INFRA_NS" exec -i deploy/vault -- sh -s 2>/dev/null
  )"; then
    case "$v_out" in
      dev) echo "    !! 秘匿管理の管理者トークン: 開発用の値で通る"; add_found "秘匿管理の管理者トークン" ;;
      ok) echo "    ok 秘匿管理の管理者トークン" ;;
      *) echo "    ?? 秘匿管理の管理者トークン: 確かめられない"; add_unknown "秘匿管理の管理者トークン" ;;
    esac
  else
    echo "    ?? 秘匿管理: Pod の中で確かめられない（kubectl exec が失敗した）"
    add_unknown "秘匿管理の管理者トークン"
  fi
else
  echo "    -  秘匿管理（deploy/vault が無い）"
fi

if [ -n "$found" ]; then
  echo "NG: 開発用の値のままのものがある: ${found}" >&2
  echo "    作り直す: bash scripts/rotate-infra-credentials.sh --live <postgres-admin|app-db|object-storage|vault-root-token|grafana-admin>（値は表示しない）。" >&2
  echo "    Keycloak の管理者・ブローカは docs/operations/secret-rotation-runbook.md B-1 の手順で回す。" >&2
  [ -n "$unknown" ] && echo "    確かめられなかったもの: ${unknown}" >&2
  exit 1
fi
if [ -n "$unknown" ]; then
  echo "WARN: 確かめられなかったものがある: ${unknown}" >&2
  exit 2
fi
echo "OK: 開発用の値のままのものは見つからない"

#!/usr/bin/env bash
# NFR-18, ADR-0124 決定 1, IADR-0517 (#1830):
# **dev 以外のクラスタで、レルム管理のロールを持つ機密クライアントの secret を公知の dev の値で作らせない**判定の唯一の口。
# `source` して使う。呼び出し元は scripts/k8s-local-up.sh（手動の Secret）・deploy/local/vault/eso/bootstrap.sh（Vault の種）・
# deploy/local/keycloak-setup/reconcile-realm.sh（realm の後追い Job へ判定を env で渡す）の 3 本。**判定をここ以外へ複写しない。**
#
# ## なぜ要るか
#
# 3 本は env（`*_CLIENT_SECRET`）が無ければ realm の宣言と同じ dev の値（`<client>-dev-secret-change-me`）を入れる。
# リポジトリは公開なので、dev 以外のクラスタ（共有 PoC など）でこれを使うと、Keycloak のトークン端点に届く誰でも
# そのクライアントのトークンを得られる。対象の 3 クライアントはレルム管理のロールを持つ（`mcp-client-admin` は
# 全クライアントの secret を読めるので、レルムの全権に等しい）。
#
# ## 判定（IADR-0517 決定 1・2・3）
#
# - **入口は kube context の許可集合**: `k3d-*` / `kind-*` / `rancher-desktop` / `docker-desktop` を dev とみなす。
#   それ以外（**読めない・空を含む**）は dev ではない —— 未知のクラスタで既定が安全側に倒れる。
# - dev ではない context で、対象のクライアントの値が **空（未設定）か dev の値と同じ**なら止める。
# - 明示の上書き `ALLOW_DEV_CLIENT_SECRETS=1`（`1` だけ。`true` 等は上書きにならない）で通す。通すときは大きく警告する。
#
# 🔴 値はこの関数の引数（bash の関数呼び出し＝exec しない）と比較にしか使わない。**どこにも出力しない。**

# 判定の対象（レルム管理のロールを持つ機密クライアント）。reconcile-realm.js の DEV_SECRET_GUARDED_CLIENTS と同じ集合
# （keycloak-realm-reconcile.test.js が突き合わせる）。呼び出し元は各自の文脈で対象を選んで渡す（k8s-local-up.sh は ESO=1 で reset-gate だけ）。
# shellcheck disable=SC2034  # 読み手は試験（集合の突合）。source した側の参照用に残す
DEV_CLIENT_SECRET_GUARDED="identity-admin reset-gate mcp-client-admin"

# dev_client_secret_context_is_dev <context> → 0＝dev の許可集合に入る
dev_client_secret_context_is_dev() {
  case "$1" in
    k3d-?* | kind-?* | rancher-desktop | docker-desktop) return 0 ;;
  esac
  return 1
}

# dev_client_secret_dev_value <client> → realm の宣言と同じ dev の値（keycloak-realm-reconcile.test.js が宣言との一致を固定する）
dev_client_secret_dev_value() { printf '%s-dev-secret-change-me' "$1"; }

# dev_client_secret_env_name <client> → 上書きに使う環境変数の名前（表示用）
dev_client_secret_env_name() {
  case "$1" in
    identity-admin) printf 'IDENTITY_ADMIN_CLIENT_SECRET' ;;
    reset-gate) printf 'RESET_GATE_CLIENT_SECRET' ;;
    mcp-client-admin) printf 'MCP_CLIENT_ADMIN_CLIENT_SECRET' ;;
    *) printf '%s' "$1" ;;
  esac
}

# dev_client_secret_decide <context> <override> [<client>=<value> ...]  —— **純関数**（外部コマンドを呼ばない）
#   標準出力: 1 行目に判定 —— dev（許可集合）/ clean（dev の値で作るものが無い）/ override（上書きで通す）/ deny（止める）。
#             override / deny のときは 2 行目以降に dev の値で作られるクライアント名（値は出さない）。
#   終了コード: 0＝進む（dev / clean / override）、1＝止まる（deny）。
dev_client_secret_decide() {
  local ctx="$1" override="$2" kv client value hits=""
  shift 2
  if dev_client_secret_context_is_dev "$ctx"; then
    echo dev
    return 0
  fi
  for kv in "$@"; do
    client="${kv%%=*}"
    value="${kv#*=}"
    if [ -z "$value" ] || [ "$value" = "$(dev_client_secret_dev_value "$client")" ]; then
      hits="${hits}${client}"$'\n'
    fi
  done
  if [ -z "$hits" ]; then
    echo clean
    return 0
  fi
  if [ "$override" = "1" ]; then echo override; else echo deny; fi
  printf '%s' "$hits"
  [ "$override" = "1" ]
}

# dev_client_secret_create_allowed <context> <override> → 0＝宣言の dev の値で作ってよい（dev の context か上書き）
#   realm の後追い Job（context を持たない）へ渡す判定。値を見ない（Job が作るのは常に宣言の値＝dev の値である）。
dev_client_secret_create_allowed() {
  dev_client_secret_context_is_dev "$1" || [ "$2" = "1" ]
}

# 現在の kube context（KUBECONFIG は kubectl が尊重する）。読めなければ空 ＝ dev ではない。
dev_client_secret_current_context() { kubectl config current-context 2>/dev/null || true; }

# dev_client_secret_guard <呼び出し元> [<client>=<value> ...]
#   現在の context で判定し、止めるなら理由を名指しして 1 を返す（呼び出し側が exit する）。上書きで通すときは警告して 0。
dev_client_secret_guard() {
  local caller="$1" ctx out rc=0 verdict names="" envs="" n
  shift
  ctx="$(dev_client_secret_current_context)"
  out="$(dev_client_secret_decide "$ctx" "${ALLOW_DEV_CLIENT_SECRETS:-}" "$@")" || rc=$?
  verdict="${out%%$'\n'*}"
  case "$verdict" in
    dev | clean) return 0 ;;
  esac
  while IFS= read -r n; do
    [ -n "$n" ] || continue
    names="${names:+$names, }$n"
    envs="${envs:+$envs, }$(dev_client_secret_env_name "$n")"
  done <<< "${out#*$'\n'}"
  if [ "$verdict" = "override" ] && [ "$rc" -eq 0 ]; then
    {
      echo "!!! WARNING: ${caller}: ALLOW_DEV_CLIENT_SECRETS=1 により、dev ではない kube context '${ctx:-（読めない）}' で"
      echo "!!!          レルム管理の権限を持つ機密クライアントを公知の dev の値で作る: ${names}"
      echo "!!!          リポジトリは公開である。トークン端点に届く誰でもこれらの権限のトークンを得られる。"
      echo "!!!          dev のクラスタでないなら、起動の直後に回す（docs/operations/paired-secret-rotation-runbook.md）。"
    } >&2
    return 0
  fi
  {
    echo "ERROR: ${caller}: kube context '${ctx:-（読めない）}' は dev の許可集合（k3d-* / kind-* / rancher-desktop / docker-desktop）に無い。"
    echo "       次の機密クライアントの secret が公知の dev の値（リポジトリに公開されている）で作られるため止める: ${names}"
    echo "       理由: レルム管理のロールを持つクライアントである。環境変数（${envs}）が未設定か、dev の値と同じ。"
    echo "       対処: それぞれに dev 以外の値を与えて再実行する（認証基盤の側と対で書く。docs/operations/paired-secret-rotation-runbook.md）。"
    echo "       dev のクラスタだと分かっているときだけ ALLOW_DEV_CLIENT_SECRETS=1 で通せる（警告を出す。#1830）。"
  } >&2
  return 1
}

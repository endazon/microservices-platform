#!/usr/bin/env bash
# NFR-18, ADR-0124 決定 1, IADR-0517 (#1830), IADR-0572 (GHSA-rh44-px44-p48q):
# **dev 以外のクラスタで、サービスアカウントを持つ機密クライアントの secret を公知の dev の値で作らせない**判定の唯一の口。
# `source` して使う。呼び出し元は scripts/k8s-local-up.sh（手動の Secret）・deploy/local/vault/eso/bootstrap.sh（Vault の種）・
# deploy/local/keycloak-setup/reconcile-realm.sh（realm の後追い Job へ判定を env で渡す）の 3 本。**判定をここ以外へ複写しない。**
# Keycloak の初回の取り込み（`--import-realm`）へ env の値を渡す差し替え（dev_client_secret_realm_for_import。IADR-0518 / #1834）も
# 同じ対象集合・env の名前・宣言の値を使うのでここに置く（呼び出し元は k8s-local-up.sh の [3/7]）。
#
# ## なぜ要るか
#
# 3 本は env（`*_CLIENT_SECRET`）が無ければ realm の宣言と同じ dev の値（`<client>-dev-secret-change-me`）を入れる。
# リポジトリは公開なので、dev 以外のクラスタ（共有 PoC など）でこれを使うと、Keycloak のトークン端点に届く誰でも
# そのクライアントのトークンを得られる。当初の対象はレルム管理のロールを持つ 3 クライアントだった（IADR-0517）。
# ［2026-10-22 / IADR-0572］サービスアカウントを持つ機密クライアントはどれもロールを持つ主体なので、多層防御として
# 対象を全部へ広げた。対象は realm の宣言から導出する（下の dev_client_secret_load_targets）。
#
# ## 判定（IADR-0517 決定 1・2・3）
#
# - **入口は kube context の許可集合**: `k3d-*` / `kind-*` / `rancher-desktop` / `docker-desktop` を dev とみなす。
#   それ以外（**読めない・空を含む**）は dev ではない —— 未知のクラスタで既定が安全側に倒れる。
# - dev ではない context で、対象のクライアントの値が **空（未設定）か dev の値と同じ**なら止める。
# - 明示の上書き `ALLOW_DEV_CLIENT_SECRETS=1`（`1` だけ。`true` 等は上書きにならない）で通す。通すときは大きく警告する。
#
# 🔴 値はこの関数の引数（bash の関数呼び出し＝exec しない）と比較にしか使わない。**どこにも出力しない。**

# 判定の対象。［2026-10-22 / GHSA-rh44-px44-p48q / IADR-0572］**手で並べず realm の宣言から導出する** —— サービスアカウントを持つ
# 機密クライアントのうち、宣言が secret を持つもの全部（述語の正は reconcile-realm.js の isDevSecretGuardedClient。
# scripts/lib/dev-client-secret-targets.js が読んで「clientId・env の名前・宣言の値」の表にする）。新しいクライアントを宣言に
# 足せば、ここを触らずに対象へ入る。呼び出し元は dev_client_secret_load_targets を 1 度呼んでから下の関数を使う。
#   DEV_CLIENT_SECRET_GUARDED  対象の clientId（空白区切り・昇順）
#   DEV_CLIENT_SECRET_TABLE    1 行 1 対象の「clientId<TAB>env の名前<TAB>宣言の値」（宣言の値はリポジトリに公開されている dev の値）
# shellcheck disable=SC2034  # 読み手は source した側（bootstrap.sh・k8s-local-up.sh）と試験
DEV_CLIENT_SECRET_GUARDED=""
DEV_CLIENT_SECRET_TABLE=""
DEV_CLIENT_SECRET_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# 既定の realm ファイル（MSP の宣言。起動器の [3/7] が取り込み元・ConfigMap を作るのと同じファイル）。
DEV_CLIENT_SECRET_REALM_DEFAULT="${DEV_CLIENT_SECRET_LIB_DIR}/../../deploy/keycloak/microservices-platform-realm.json"

# dev_client_secret_load_targets [<realm ファイル>] → 0＝表を読んだ / 1＝読めない（呼び出し側は止まる。守れないまま進まない）
#   node で宣言を読む（値は宣言の dev の値だけ。env の値は渡さない）。
dev_client_secret_load_targets() {
  local realm="${1:-$DEV_CLIENT_SECRET_REALM_DEFAULT}" out line names=""
  if ! out="$(node "${DEV_CLIENT_SECRET_LIB_DIR}/dev-client-secret-targets.js" "$realm")" || [ -z "$out" ]; then
    echo "ERROR: dev の値の守りの対象を realm の宣言（${realm}）から導出できない。node と宣言を確かめる（守れないまま進まない）" >&2
    return 1
  fi
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    names="${names:+$names }${line%%$'\t'*}"
  done <<< "$out"
  DEV_CLIENT_SECRET_TABLE="$out"
  DEV_CLIENT_SECRET_GUARDED="$names"
}

# dev_client_secret_lookup <client> <env|dev> → 表の列を出す。表に無ければ 1（何も出さない）。外部コマンドを呼ばない。
dev_client_secret_lookup() {
  local c e v
  while IFS=$'\t' read -r c e v; do
    [ "$c" = "$1" ] || continue
    case "$2" in
      env) printf '%s' "$e" ;;
      dev) printf '%s' "$v" ;;
    esac
    return 0
  done <<< "$DEV_CLIENT_SECRET_TABLE"
  return 1
}

dev_client_secret_context_is_dev() {
  case "$1" in
    k3d-?* | kind-?* | rancher-desktop | docker-desktop) return 0 ;;
  esac
  return 1
}

# dev_client_secret_dev_value <client> → realm の宣言の secret（公知の dev の値）。表に無ければ 1。
dev_client_secret_dev_value() { dev_client_secret_lookup "$1" dev; }

# dev_client_secret_env_name <client> → 上書きに使う環境変数の名前（規則は dev-client-secret-targets.js の envNameOf）
dev_client_secret_env_name() { dev_client_secret_lookup "$1" env || printf '%s' "$1"; }

# dev_client_secret_decide <context> <override> [<client>=<value> ...]  —— **純関数**（外部コマンドを呼ばない）
#   標準出力: 1 行目に判定 —— dev（許可集合）/ clean（dev の値で作るものが無い）/ override（上書きで通す）/ deny（止める）。
#             override / deny のときは 2 行目以降に dev の値で作られるクライアント名（値は出さない）。
#   終了コード: 0＝進む（dev / clean / override）、1＝止まる（deny）。
dev_client_secret_decide() {
  local ctx="$1" override="$2" kv client value dv hits=""
  shift 2
  if dev_client_secret_context_is_dev "$ctx"; then
    echo dev
    return 0
  fi
  for kv in "$@"; do
    client="${kv%%=*}"
    value="${kv#*=}"
    # 表に無いクライアント（宣言の値が分からない）・表を読んでいないときは、値を比べられないので止める側に倒す。
    if [ -z "$value" ] || ! dv="$(dev_client_secret_dev_value "$client")" || [ "$value" = "$dv" ]; then
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

# NFR-18, ADR-0124 決定 1, IADR-0518 (#1834):
# dev_client_secret_realm_for_import <realm ファイル>
#   Keycloak の `--import-realm` へ渡す realm JSON を標準出力へ書く。対象のクライアント（このファイルの宣言から導出する。IADR-0572）のうち
#   **env（*_CLIENT_SECRET 等。名前は dev_client_secret_env_name）が空でない
#   もの**だけ、宣言の `"secret": "<dev の値>"` を `"secret": "<env の値>"` へ置き換える（どの context でも env が勝つ。env が無ければ宣言のまま）。
#   これが無いと、dev 以外のクラスタの初回の取り込みで Keycloak 側だけ公知の dev の値になり、env の値を持つ消費側と食い違う（invalid_client）。
#   - 対象・env の名前・宣言の値は上の 3 つ（DEV_CLIENT_SECRET_GUARDED・dev_client_secret_env_name・dev_client_secret_dev_value）から引く。
#   - 宣言の該当箇所が**ちょうど 1 か所**でなければ止める（黙って差し替えずに進むと食い違いが戻る）。
#   - 値に `"`・`\`・制御文字があれば止める（JSON を壊さない）。
#   - 値に `${` があれば止める（Keycloak の起動時の取り込みは `${...}` を置き換えるので、消費側と食い違う。PR #1836 監査 🟢2）。
#   - 外部コマンドを使わない（値はどのプロセスの引数にも載らない）。差し替えたクライアント名だけを標準エラーへ出す。**値は出さない。**
#   終了コード: 0＝書いた、1＝止める（標準出力は使わないこと）。
dev_client_secret_realm_for_import() (
  # bash 5.2 の patsub_replacement は置換先の `&` を一致部分へ展開する。値の `&` をそのまま置くため切る（古い bash には無い）。
  shopt -u patsub_replacement 2>/dev/null || true
  file="$1"
  if [ ! -r "$file" ]; then
    echo "ERROR: realm の取り込み元を作れない: ${file} を読めない" >&2
    return 1
  fi
  # 対象はこのファイルの宣言から導出する（IADR-0572）。導出できなければ止める（差し替えずに進むと食い違いが戻る）。
  dev_client_secret_load_targets "$file" || return 1
  content="$(< "$file")"
  done_names=""
  for client in $DEV_CLIENT_SECRET_GUARDED; do
    env_name="$(dev_client_secret_env_name "$client")"
    value="${!env_name:-}"
    [ -n "$value" ] || continue
    case "$value" in
      *[\"\\]* | *[[:cntrl:]]*)
        echo "ERROR: ${env_name} に JSON の引用符・バックスラッシュ・制御文字が含まれる。realm の取り込み元に置けない（値は表示しない）" >&2
        return 1
        ;;
      *\$\{*)
        echo "ERROR: ${env_name} に \${ が含まれる。Keycloak の取り込みが置き換えるので realm の取り込み元に置けない（値は表示しない）" >&2
        return 1
        ;;
    esac
    pattern="\"secret\": \"$(dev_client_secret_dev_value "$client")\""
    stripped="${content//"$pattern"/}"
    count=$(((${#content} - ${#stripped}) / ${#pattern}))
    if [ "$count" -ne 1 ]; then
      echo "ERROR: ${file} の ${client} の宣言の secret（dev の値）が 1 か所でない（${count} か所）。${env_name} を取り込み元へ渡せないので止める" >&2
      return 1
    fi
    replacement="\"secret\": \"${value}\""
    content=${content/"$pattern"/$replacement}
    done_names="${done_names:+$done_names, }${client}"
  done
  printf '%s\n' "$content"
  if [ -n "$done_names" ]; then
    echo "    realm の取り込み元: env の値で secret を渡す: ${done_names}（値は表示しない）" >&2
  fi
)

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
      echo "!!!          サービスアカウントを持つ機密クライアントを公知の dev の値で作る: ${names}"
      echo "!!!          リポジトリは公開である。トークン端点に届く誰でもこれらの権限のトークンを得られる。"
      echo "!!!          dev のクラスタでないなら、起動の直後に回す（docs/operations/paired-secret-rotation-runbook.md）。"
      echo "!!!          稼働中の Keycloak に dev の値が残っていないかは bash deploy/local/keycloak-setup/reconcile-realm.sh --check-dev-secrets で確かめる。"
    } >&2
    return 0
  fi
  {
    echo "ERROR: ${caller}: kube context '${ctx:-（読めない）}' は dev の許可集合（k3d-* / kind-* / rancher-desktop / docker-desktop）に無い。"
    echo "       次の機密クライアントの secret が公知の dev の値（リポジトリに公開されている）で作られるため止める: ${names}"
    echo "       理由: サービスアカウントを持つ機密クライアントである（IADR-0572）。環境変数（${envs}）が未設定か、dev の値と同じ。"
    echo "       対処: それぞれに dev 以外の値を与えて再実行する（認証基盤の側と対で書く。docs/operations/paired-secret-rotation-runbook.md）。"
    echo "       dev のクラスタだと分かっているときだけ ALLOW_DEV_CLIENT_SECRETS=1 で通せる（警告を出す。#1830）。"
    echo "       確認: 稼働中の Keycloak に dev の値が残っていないかは bash deploy/local/keycloak-setup/reconcile-realm.sh --check-dev-secrets で確かめる"
    echo "             （dev の値のクライアントを名指して非 0。読むだけで値は出さない。残っていれば上の手順書で対に回す。#1834）。"
  } >&2
  return 1
}

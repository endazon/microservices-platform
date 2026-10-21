# shellcheck shell=bash
# NFR-18, ADR-0124 決定 1, IADR-0574 (GHSA-rh44-px44-p48q):
# **基盤の資格情報（Keycloak の管理者・RabbitMQ のブローカ）を公知の開発用の値で作らない**判定の唯一の口（fail-closed）。
# `source` して使う。呼び出し元は scripts/k8s-local-up.sh（[1/7] の直後・Secret を 1 つも書く前）と
# deploy/local/vault/eso/bootstrap.sh（Vault の種。まだ無い KV だけ）の 2 本。**判定をここ以外へ複写しない。**
#
# ## 選び方
#
# - 明示（env）＞ 既存の値（k8s-local-up.sh は Secret、bootstrap.sh は Vault の KV。在れば触らない）＞ 止める。
# - 明示・既存の値が**公知の開発用の値と同じ**なら、指定が無いのと同じに扱う（止める）。
# - 明示の opt-in **`ALLOW_DEV_INFRA_CREDENTIALS=1`**（`1` だけ。`true` 等は opt-in にならない）でだけ、開発用の値で作る。
#   用途は**使い捨ての開発環境だけ**（手元の検証・CI の使い捨てスタック）。通すときは大きく警告する。
#
# 🔴 値は比較と変数への代入にしか使わない。**どこにも出力しない。** 外部コマンドを呼ばない（判定の関数）。

# 対象（env の名前 → 開発用の値・作る先）。利用者名（KEYCLOAK_ADMIN_USER / RABBITMQ_USER）は識別子なので対象外。
INFRA_CRED_TARGETS="KEYCLOAK_ADMIN_PASSWORD RABBITMQ_PASSWORD"

# infra_cred_dev_value <env の名前> → 公知の開発用の値（従来の既定値。deploy/docker-compose.yml の手元の経路と同じ）
infra_cred_dev_value() {
  case "$1" in
    KEYCLOAK_ADMIN_PASSWORD) printf '%s' 'admin' ;;
    RABBITMQ_PASSWORD) printf '%s' 'guest' ;;
    *) return 1 ;;
  esac
}

# infra_cred_secret_of <env の名前> → 「<platform-infra の Secret 名> <キー>」（k8s-local-up.sh が既存の値を読む先・案内に使う）
infra_cred_secret_of() {
  case "$1" in
    KEYCLOAK_ADMIN_PASSWORD) printf '%s' 'keycloak-admin password' ;;
    RABBITMQ_PASSWORD) printf '%s' 'rabbitmq password' ;;
    *) return 1 ;;
  esac
}

infra_cred_allow_dev() { [ "${ALLOW_DEV_INFRA_CREDENTIALS:-}" = "1" ]; }

# infra_cred_decide <env の名前> <明示の値> <既存の値>  —— **純関数**
#   標準出力: explicit（明示を使う）/ existing（既存を使う）/ dev（opt-in で開発用の値を使う）/ deny（止める）
#   終了コード: 0＝進む、1＝止める（deny）。
infra_cred_decide() {
  local dev
  dev="$(infra_cred_dev_value "$1")" || { echo deny; return 1; }
  if [ -n "$2" ] && [ "$2" != "$dev" ]; then echo explicit; return 0; fi
  if [ -z "$2" ] && [ -n "$3" ] && [ "$3" != "$dev" ]; then echo existing; return 0; fi
  if infra_cred_allow_dev; then echo dev; return 0; fi
  echo deny
  return 1
}

# infra_cred_resolve <呼び出し元> <既存の値を読む関数>
#   INFRA_CRED_TARGETS のそれぞれについて判定し、進むなら env の変数へ選んだ値を入れて export する（子の bootstrap.sh・
#   [5/7] の rabbitmq-app が同じ値を使う）。<既存の値を読む関数> は「<Secret 名> <キー>」を受けて値を標準出力へ書く（無ければ空）。
#   止めるものが 1 つでもあれば、全部を名指して 1 を返す（呼び出し側が exit する）。値は出さない。
infra_cred_resolve() {
  local caller="$1" reader="$2" name explicit existing verdict loc denied="" warned=""
  for name in $INFRA_CRED_TARGETS; do
    explicit="${!name:-}"
    existing=""
    if [ -z "$explicit" ]; then
      loc="$(infra_cred_secret_of "$name")"
      # shellcheck disable=SC2086  # loc は「Secret 名 キー」の 2 語
      existing="$("$reader" $loc)" || existing=""
    fi
    verdict="$(infra_cred_decide "$name" "$explicit" "$existing")" || true
    case "$verdict" in
      explicit) ;;
      existing) printf -v "$name" '%s' "$existing" ;;
      dev)
        printf -v "$name" '%s' "$(infra_cred_dev_value "$name")"
        warned="${warned:+$warned, }${name}"
        ;;
      *) denied="${denied:+$denied, }${name}（Secret $(infra_cred_secret_of "$name" | tr ' ' '/')）" ;;
    esac
    export "${name?}"
  done
  infra_cred_report "$caller" "$denied" "$warned"
}

# infra_cred_guard <呼び出し元> [<env の名前> ...]
#   既存の値を持たない作る口（bootstrap.sh の、まだ無い KV）向け。渡した env のそれぞれを、明示の値だけで判定する。
#   変数は書き換えない（作る側は ${NAME:-<開発用の値>} のまま。opt-in のときだけ開発用の値になる）。
infra_cred_guard() {
  local caller="$1" name verdict denied="" warned=""
  shift
  for name in "$@"; do
    verdict="$(infra_cred_decide "$name" "${!name:-}" "")" || true
    case "$verdict" in
      explicit) ;;
      dev) warned="${warned:+$warned, }${name}" ;;
      *) denied="${denied:+$denied, }${name}" ;;
    esac
  done
  infra_cred_report "$caller" "$denied" "$warned"
}

# infra_cred_report <呼び出し元> <止めるもの> <警告するもの> → 止めるものがあれば名指して 1（値は出さない）
infra_cred_report() {
  local caller="$1" denied="$2" warned="$3"
  if [ -n "$denied" ]; then
    {
      echo "ERROR: ${caller}: 基盤の資格情報が指定されていないか、公知の開発用の値と同じなので止める（何も作らない）: ${denied}"
      echo "       対処: 環境変数に開発用ではない値を与える（既に稼働中なら稼働中の値と同じ値。値は表示しない）。"
      echo "             既存の Secret・Vault の KV が開発用ではない値を持っていれば、指定しなくてもその値を使う（KV は触らない）。"
      echo "       使い捨ての開発環境（手元の検証・CI の使い捨てスタック）だけ ALLOW_DEV_INFRA_CREDENTIALS=1 で開発用の値を許す（警告を出す。IADR-0574）。"
    } >&2
    return 1
  fi
  if [ -n "$warned" ]; then
    {
      echo "!!! WARNING: ${caller}: ALLOW_DEV_INFRA_CREDENTIALS=1 により、基盤の資格情報を公知の開発用の値で作る: ${warned}"
      echo "!!!          使い捨ての開発環境に限る。残すクラスタでは開発用ではない値を与えて作り直す（docs/operations/paired-secret-rotation-runbook.md）。"
    } >&2
  fi
  return 0
}

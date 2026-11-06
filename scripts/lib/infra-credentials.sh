# shellcheck shell=bash
# NFR-18, ADR-0124 決定 1, IADR-0574 (非公開の指摘（MSP）):
# **基盤の資格情報（Keycloak の管理者・RabbitMQ のブローカ）を公知の開発用の値で作らない**判定の唯一の口（fail-closed）。
# `source` して使う。呼び出し元は scripts/k8s-local-up.sh（[1/7] の直後・Secret を 1 つも書く前）と
# deploy/local/vault/eso/bootstrap.sh（Vault の種。まだ無い KV だけ）の 2 本。**判定をここ以外へ複写しない。**
# ［2026-11-05 / #2399・IADR-0672］段階の対象（下半分）を足した。対象と開発用の値は検査（scripts/check-infra-credentials.sh）もここから引く。
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
#   ブローカの既定のユーザーの Secret が同じ値を使う）。<既存の値を読む関数> は「<Secret 名> <キー>」を受けて値を標準出力へ書く（無ければ空）。
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

# ------------------------------------------------------------------------------------------------------------------------
# NFR-18, ADR-0124 決定 1, IADR-0672 (#2399): **段階の対象**（IADR-0574 §残余 1 の続き。DB・オブジェクトストレージ・秘匿管理の
# 管理者トークン）。選び方は上と同じ「明示 ＞ 既存の値（開発用でない）」。どちらも無いときの扱いだけが違う:
#   - kube context が dev の許可集合（dev_client_secret_context_is_dev。IADR-0517）→ dev（従前どおり開発用の値）
#   - dev ではない・段 1（既定）              → report（開発用の値で作り、名指して警告する）
#   - dev ではない・段 2（INFRA_CREDENTIALS_ENFORCE=1。`1` だけ）→ deny（止める）。ALLOW_DEV_INFRA_CREDENTIALS=1 なら override
# 段 2 の既定化は、利用者が check-infra-credentials.sh / rotate-infra-credentials.sh で作り直した後に別の PR で入れる。
# 🔴 dev_client_secret_context_is_dev を使うので、呼び出し元は scripts/lib/dev-client-secret-guard.sh も source しておく。
# 🔴 値は比較と変数への代入にしか使わない。**どこにも出力しない。**

# infra_cred_staged_names → 段階の対象の env の名前（空白区切り）。秘匿管理の管理者トークンは VAULT=1 のときだけ作るので、そのときだけ含む。
#   利用者名・アクセスキーの ID は識別子なので対象外（IADR-0574 決定 1 と同じ）。
infra_cred_staged_names() {
  local names="PG_PASSWORD APP_DB_PASSWORD WIKIJS_DB_PASSWORD OBJECT_STORAGE_SECRET_KEY"
  [ "${VAULT:-}" = "1" ] && names="$names VAULT_DEV_ROOT_TOKEN"
  printf '%s' "$names"
}

# infra_cred_staged_dev_value <env の名前> → 宣言の開発用の値（起動器・Vault の種の従来の既定値）
infra_cred_staged_dev_value() {
  case "$1" in
    PG_PASSWORD) printf '%s' 'postgres' ;;
    APP_DB_PASSWORD | WIKIJS_DB_PASSWORD) printf '%s' 'kp' ;;
    OBJECT_STORAGE_SECRET_KEY) printf '%s-%s-%s' 'objectstorage' 'dev' 'secret' ;;
    VAULT_DEV_ROOT_TOKEN) printf '%s' 'devroot' ;;
    *) return 1 ;;
  esac
}

# infra_cred_staged_secret_of <env の名前> → 「<infra|msp> <Secret 名> <キー>」（既存の値を読む先・案内に使う）
infra_cred_staged_secret_of() {
  case "$1" in
    PG_PASSWORD) printf '%s' 'infra postgres password' ;;
    APP_DB_PASSWORD) printf '%s' 'msp postgres-app password' ;;
    WIKIJS_DB_PASSWORD) printf '%s' 'msp wikijs-db password' ;;
    OBJECT_STORAGE_SECRET_KEY) printf '%s' 'msp object-storage-credentials secretKey' ;;
    VAULT_DEV_ROOT_TOKEN) printf '%s' 'infra vault-dev-token token' ;;
    *) return 1 ;;
  esac
}

infra_cred_enforced() { [ "${INFRA_CREDENTIALS_ENFORCE:-}" = "1" ]; }

# infra_cred_staged_decide <env の名前> <明示の値> <既存の値> <kube context>  —— **純関数**（外部コマンドを呼ばない）
#   標準出力: explicit / existing / dev / report / override / deny。終了コード: 0＝進む、1＝止める（deny）。
infra_cred_staged_decide() {
  local dev
  dev="$(infra_cred_staged_dev_value "$1")" || { echo deny; return 1; }
  if [ -n "$2" ] && [ "$2" != "$dev" ]; then echo explicit; return 0; fi
  if [ -z "$2" ] && [ -n "$3" ] && [ "$3" != "$dev" ]; then echo existing; return 0; fi
  if dev_client_secret_context_is_dev "$4"; then echo dev; return 0; fi
  if ! infra_cred_enforced; then echo report; return 0; fi
  if infra_cred_allow_dev; then echo override; return 0; fi
  echo deny
  return 1
}

# infra_cred_staged_resolve <呼び出し元> <既存の値を読む関数> <kube context>
#   段階の対象のそれぞれを判定し、進むなら env の変数へ選んだ値を入れて export する（Secret の作成・ESO の段の bootstrap.sh が
#   同じ値を使う）。<既存の値を読む関数> は「<infra|msp> <Secret 名> <キー>」を受けて値を標準出力へ書く（無ければ空）。
#   止めるものが 1 つでもあれば、全部を名指して 1 を返す。値は出さない。
infra_cred_staged_resolve() {
  local caller="$1" reader="$2" ctx="$3" name explicit existing verdict loc denied="" reported="" overridden=""
  for name in $(infra_cred_staged_names); do
    explicit="${!name:-}"
    existing=""
    if [ -z "$explicit" ]; then
      loc="$(infra_cred_staged_secret_of "$name")"
      # shellcheck disable=SC2086  # loc は「名前空間 Secret 名 キー」の 3 語（上の case が返す固定の字面）
      existing="$("$reader" $loc)" || existing=""
    fi
    verdict="$(infra_cred_staged_decide "$name" "$explicit" "$existing" "$ctx")" || true
    case "$verdict" in
      explicit) ;;
      existing) printf -v "$name" '%s' "$existing" ;;
      dev | report | override)
        printf -v "$name" '%s' "$(infra_cred_staged_dev_value "$name")"
        [ "$verdict" = report ] && reported="${reported:+$reported, }${name}"
        [ "$verdict" = override ] && overridden="${overridden:+$overridden, }${name}"
        ;;
      *) denied="${denied:+$denied, }${name}" ;;
    esac
    export "${name?}"
  done
  infra_cred_staged_report "$caller" "$ctx" "$denied" "$reported" "$overridden"
}

# infra_cred_staged_guard <呼び出し元> <kube context> [<env の名前> ...]
#   既存の値を持たない作る口（bootstrap.sh の、まだ無い KV）向け。渡した env を明示の値と context だけで判定する。
#   変数は書き換えない（作る側は ${NAME:-<開発用の値>} のまま。dev / report / override のときだけ開発用の値になる）。
infra_cred_staged_guard() {
  local caller="$1" ctx="$2" name verdict denied="" reported="" overridden=""
  shift 2
  for name in "$@"; do
    verdict="$(infra_cred_staged_decide "$name" "${!name:-}" "" "$ctx")" || true
    case "$verdict" in
      explicit | dev) ;;
      report) reported="${reported:+$reported, }${name}" ;;
      override) overridden="${overridden:+$overridden, }${name}" ;;
      *) denied="${denied:+$denied, }${name}" ;;
    esac
  done
  infra_cred_staged_report "$caller" "$ctx" "$denied" "$reported" "$overridden"
}

# infra_cred_staged_report <呼び出し元> <context> <止めるもの> <報告するもの> <上書きで通すもの> → 止めるものがあれば 1（値は出さない）
infra_cred_staged_report() {
  local caller="$1" ctx="${2:-（読めない）}" denied="$3" reported="$4" overridden="$5"
  if [ -n "$denied" ]; then
    {
      echo "ERROR: ${caller}: dev ではない kube context '${ctx}' で、基盤の資格情報（段階の対象）が指定されていないか開発用の値と同じなので止める（何も作らない）: ${denied}"
      echo "       INFRA_CREDENTIALS_ENFORCE=1（強制の段）による。対処: 環境変数に開発用ではない値を与える（既に稼働中なら稼働中の値と同じ値）。"
      echo "             既存の Secret が開発用ではない値を持っていれば、指定しなくてもその値を使う。値は表示しない。"
      echo "             稼働中の値は bash scripts/check-infra-credentials.sh --live で確かめ、bash scripts/rotate-infra-credentials.sh --live で作り直す。"
      echo "       使い捨ての開発環境だけ ALLOW_DEV_INFRA_CREDENTIALS=1 で開発用の値を許す（警告を出す。IADR-0672）。"
    } >&2
    return 1
  fi
  if [ -n "$reported" ]; then
    {
      echo "WARNING: ${caller}: dev ではない kube context '${ctx}' で、基盤の資格情報（段階の対象）を開発用の値で作る: ${reported}"
      echo "         段 1（報告だけ）なので止めない。稼働中の値を bash scripts/check-infra-credentials.sh --live で確かめ、"
      echo "         bash scripts/rotate-infra-credentials.sh --live で作り直す（値は表示しない）。"
      echo "         作り直した後は INFRA_CREDENTIALS_ENFORCE=1 で、開発用の値で作るのを止められる（IADR-0672）。"
    } >&2
  fi
  if [ -n "$overridden" ]; then
    {
      echo "!!! WARNING: ${caller}: ALLOW_DEV_INFRA_CREDENTIALS=1 により、dev ではない kube context '${ctx}' で基盤の資格情報（段階の対象）を開発用の値で作る: ${overridden}"
      echo "!!!          使い捨ての開発環境に限る。残すクラスタでは bash scripts/rotate-infra-credentials.sh --live で作り直す。"
    } >&2
  fi
  return 0
}

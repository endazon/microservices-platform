#!/usr/bin/env bash
# IADR-0066: MSP+AST 連結ローカル k8s(k3d) dev 環境の起動オーケストレーション。
# 冪等（再実行可）。fail-safe: 機密は未設定なら dev 既定/空（no-op）で作成する。
#   ただし dev 以外の kube context では、レルム管理のロールを持つ 3 クライアント（identity-admin / reset-gate / mcp-client-admin）の
#   secret を dev の値で作らずに止まる（IADR-0517 / #1830。上書きは ALLOW_DEV_CLIENT_SECRETS=1）。
#
#   bash scripts/k8s-local-up.sh --live [cluster-name]   # --live か LIVE=1 が無ければ何もしない（#1550）
#
# 前提ツール: docker / k3d / kubectl / helm（scripts/README や docs/operations 参照）。
# 機密の上書きは環境変数で: PG_PASSWORD / RABBITMQ_PASSWORD / KEYCLOAK_ADMIN_PASSWORD /
#   OBJECT_STORAGE_ACCESS_KEY / OBJECT_STORAGE_SECRET_KEY（IADR-0461。旧 MINIO_*）/ WIKIJS_DB_PASSWORD / WIKIJS_SYNC_APIKEY / ANTHROPIC_API_KEY / VOYAGE_API_KEY（#1764。埋め込み） /
#   RABBITMQ_USER（#1022。helm の global.messaging.user と揃えること）/
#   WIKIJS_OIDC_CLIENT_SECRET（#1127。WIKIJS_OIDC=1 のときだけ使う。realm の wiki-js client と揃えること）/
#   KEYCLOAK_ADMIN_USER（IADR-0369。既定 admin。Keycloak と realm 後追い Job が同じ Secret から読む）/
#   SYNTHETIC_MONITOR_CLIENT_SECRET（#1287。SYNTHETIC=1 のときだけ使う。realm の synthetic-monitor client と揃えること）
#   RESET_GATE_CLIENT_SECRET / IDENTITY_ADMIN_CLIENT_SECRET / MCP_CLIENT_ADMIN_CLIENT_SECRET（#1830。dev 以外の kube context では必須）
#   SESSION_STORE_PASSWORD（#1839。セッションストア Valkey の認証。未指定なら既存の Secret の値、無ければ初回だけ乱数で作る）
# 永続化（Keycloak/Postgres/Qdrant ＋ OBSERVABILITY=1 の可観測性 4 種の PVC）は **既定オン**（IADR-0369 / #1088）。
#   使い捨てスタックでだけ PERSIST=0 で外す。
# 永続化と一緒に、Postgres / Vault の日次バックアップ CronJob（age 暗号化・クラスタ外 2 か所。IADR-0471 / #1560）も入る。
#   受取人（age の公開鍵）は BACKUP_AGE_RECIPIENTS_FILE=<ファイル> を与えたときだけ ConfigMap にする（既定の再実行で上書きしない）。
# リセット申請の床（SC-15）は **既定オン**（ADR-0097 決定 2 / #1500）。器は infra と一緒に必ず立ち、
#   経路は ISTIO=1 ＋ LOCALEDGE=1 のエッジ（istio-edge-up.sh）が足す。外すときだけ RESET_FLOOR=0
#   （検証で床の有無を比べる用途に限る。本番の退路に使わない。ADR-0111 決定 3 / #1543）。
#   器は 2 レプリカ ＋ PodDisruptionBudget（ADR-0111 決定 1）。下の rollout 待ちは 2 つとも ready になるまで待つ。
set -euo pipefail

# NFR, #1550: クラスタを作り、稼働クラスタへ helm / kubectl で書き込む。明示の指定（--live か LIVE=1）が無ければ
# 何もせずに終わる（判定は副作用より前に置く）。指定は LIVE=1 として export され、中から呼ぶ
# k8s-local-images.sh / istio-edge-up.sh / seed-*.js は親の指定を引き継ぐ。
. "$(dirname "$0")/lib/live-opt-in.sh" || exit 3   # 判定器が読めなければ守れない —— 黙って続けず止める
live_opt_in_scan "$@"; set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"
live_opt_in_require "k8s-local-up.sh"

# SC-15 / ADR-0097 決定 2 (#1500): RESET_FLOOR は末尾の istio-edge-up.sh が読む。そこでも 0 / 1 以外を拒むが、
# 🔴 **長い起動の最後で落ちるより、最初に落とす**（監査 #1518）。空（未設定と同じ＝既定 1）・0・1 だけを受け付ける。
case "${RESET_FLOOR:-}" in
  ''|0|1) ;;
  *) echo "ERROR: RESET_FLOOR は 0（床の経路を外す）か 1（入れる。既定）のどちらかです: '${RESET_FLOOR}'" >&2; exit 1 ;;
esac
# 床の経路を足すのは Istio のエッジだけである（ISTIO=1 ＋ LOCALEDGE=1 で istio-edge-up.sh が走るとき）。
# それ以外で RESET_FLOOR を与えても何も変わらない —— 黙って無視せず、効かないことを告げる。
#   ［2026-10-02 / #1713］告げるのは [2/7] の前（ISTIO の引き継ぎの判定の後）へ移した。ISTIO 未指定は現行のメッシュを
#   引き継げば ISTIO=1 になり床が効くので、ここ（クラスタを読む前）では効くかどうかが決まらない（IADR-0488）。

# NFR-16, IADR-0488 (#1713): ISTIO は 3 値 —— 1＝メッシュを入れる / 0＝外す（明示）/ 未指定（空）＝現行の helm の宣言を引き継ぐ。
#   未指定に意味ができたので、それ以外の値（true・yes 等）を黙って「外す」と読まない。RESET_FLOOR と同じく最初に落とす。
case "${ISTIO:-}" in
  ''|0|1) ;;
  *) echo "ERROR: ISTIO は 1（メッシュを入れる）・0（外す）・未指定（現行を引き継ぐ。初回は入れない）のいずれかです: '${ISTIO}'" >&2; exit 1 ;;
esac

# NFR, 計画 ADR-0135 決定 2, IADR-0519 (#1843): Argo CD は**版のタグの URL**から入れる（`stable` ブランチを直接 apply しない）。
#   既定は `stable` が 2026-10-09 に指していた版（v3.5.4。両 URL の install.yaml はバイト一致を実測）。上書きは ARGOCD_VERSION。
#   🔴 **版のタグ（vX.Y.Z）以外は拒否する** —— `stable` / `master` を与えると固定が外れる。RESET_FLOOR と同じく最初に落とす。
ARGOCD_VERSION="${ARGOCD_VERSION:-v3.5.4}"
if [ "${ARGOCD:-}" = "1" ] && ! [[ "$ARGOCD_VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "ERROR: ARGOCD_VERSION は版のタグ（例 v3.5.4）で与えてください（ブランチ名では版が固定されない。IADR-0519）: '${ARGOCD_VERSION}'" >&2
  exit 1
fi

CLUSTER="${1:-msp-ast-dev}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
INFRA_NS="platform-infra"
MSP_NS="microservices-platform"

# NFR-18 (#1793): **値を kubectl の引数（`ps`・`/proc/*/cmdline`）へ載せない**（従前は `--from-literal=<key>=<value>`）。
#   値は 0700 の一時ディレクトリの 0600 のファイルへ組み込みの printf で書き、`--from-file=<key>=<file>` にパスだけを渡す
#   （Secret の中身は `--from-literal` と同じ）。サブシェルの関数にして EXIT trap で必ず消す（呼び出し側の trap を汚さない）。
# Secret の patch 用（#1793）。
json_str() { # <value> → JSON 文字列（引用符つき）。組み込みの置換だけで組む（値をどのプロセスの引数にも載せない）
  local s="$1"
  s="${s//\\/\\\\}"; s="${s//\"/\\\"}"
  s="${s//$'\n'/\\n}"; s="${s//$'\r'/\\r}"; s="${s//$'\t'/\\t}"
  # 上の 5 種以外の制御文字は JSON にそのまま置けない。壊れた JSON を書くより止める（値は表示しない）。
  case "$s" in *[[:cntrl:]]*) echo "error: JSON に置けない制御文字を含む値がある（値は表示しない）" >&2; return 1 ;; esac
  printf '"%s"' "$s"
}

apply_secret() ( # ns name key=val [key=val...]
  ns="$1"; name="$2"; shift 2
  umask 077
  d="$(mktemp -d)"
  trap 'rm -rf "$d"' EXIT
  args=(); i=0
  for kv in "$@"; do
    i=$((i + 1))
    printf '%s' "${kv#*=}" > "$d/$i"
    args+=(--from-file="${kv%%=*}=$d/$i")
  done
  kubectl create secret generic "$name" -n "$ns" "${args[@]}" \
    --dry-run=client -o yaml | kubectl apply -f -
)

echo "==> [1/7] cluster"
# ランタイム自動判定: Rancher Desktop（内蔵 k3s・nerdctl）か、docker+k3d か。
RUNTIME="${K8S_LOCAL_RUNTIME:-auto}"
if [ "$RUNTIME" = "auto" ]; then
  if command -v nerdctl >/dev/null 2>&1; then RUNTIME="rancher";
  elif command -v k3d >/dev/null 2>&1 && command -v docker >/dev/null 2>&1; then RUNTIME="k3d";
  else echo "ERROR: Rancher Desktop(containerd) か docker+k3d が必要です。" >&2; exit 1; fi
fi
export K8S_LOCAL_RUNTIME="$RUNTIME"
echo "    runtime: $RUNTIME"
if [ "$RUNTIME" = "k3d" ]; then
  # IADR-0105 (#399): apiserver への OIDC 検証フラグ付与は行わない。k8s 1.30+ はレガシー --oidc-* を
  # 構造化認証設定（jwt[0]）へ変換し issuer.url に https を強制するが、経路B の Keycloak は
  # KC_HOSTNAME_URL=http://keycloak:8080 で token の iss が http 固定のため両立せず、フラグを付けると
  # apiserver が起動できずクラスタが停止する（IADR-0084 の「⚠️ 2026-07-25 追記」で実測）。旧 #328 の
  # HEADLAMP_OIDC_APISERVER 分岐（HEADLAMP 追従）は本 issue で除去した＝HEADLAMP=1 は Headlamp の
  # デプロイのみを行い、ログインは token 方式（deploy/local/README.md「Headlamp」）。OIDC 化は #388。
  # IADR-0091 (#356): LOCALEDGE=1 でローカルエッジ集約用のポートへ切替える。platform フロント=80/443
  # (Traefik web/websecure)、管理ツール=50000(Traefik 追加 entrypoint admin)。既定(未設定)は現行 8080/8443 で
  # バイト等価(後方互換・fail-safe)。ポートは cluster 作成時固定のため既存クラスタは delete→再作成が必要
  # (deploy/local/README.md のユーザー手順・破壊操作はユーザーが実行)。Rancher Desktop 経路は本 -p を使わず
  # (内蔵 k3s の LB がポート公開)、overlay 適用のみ(下の LOCALEDGE ブロック参照)。
  # bind は loopback (127.0.0.1) に固定する: 50000 には認証なしの Qdrant も集約されるため、既定で同一 LAN の
  # 第三者へ露出させない(閉域前提をコード側で担保)。LAN 公開が必要なら利用者が明示的に host を広げる。
  if [ "${LOCALEDGE:-}" = "1" ]; then
    CREATE_ARGS=(--agents 1 -p "127.0.0.1:80:80@loadbalancer" -p "127.0.0.1:443:443@loadbalancer" -p "127.0.0.1:50000:50000@loadbalancer")
  else
    CREATE_ARGS=(--agents 1 -p "8080:80@loadbalancer" -p "8443:443@loadbalancer")
  fi
  # NFR, Issue #783 (#442 子 5): K3S_IMAGE で k3s のイメージを固定する。
  # ［2026-10-09 / #1843・計画 ADR-0135 決定 2・IADR-0519］**既定で固定する**（従前は与えたときだけ固定し、既定は k3d 同梱の版
  # だった）。版の情報源はこの既定 1 か所であり、CI のワークフローは値を持たない（同じ値を 2 か所に持つと片側だけ動く）。
  # 上書きは K3S_IMAGE（空でも既定へ戻る＝浮動にはできない）。Rancher Desktop 経路は内蔵 k3s の版を使う（下の else）。
  # **理由は「バージョンを揃えたいから」ではない。揃っていないことが静かに素通りするからである。**
  # k3d の既定 k3s（5.7.4 では v1.30.4）が同梱する traefik chart は 25.0.3 で、そこでは `expose` が bool
  # であり、deploy/local/edge/traefik-entrypoint.yaml の map 形式（chart 26 以降）は型不一致で reconcile に
  # 失敗する。ところが `kubectl apply` は成功するため **admin(50000) が立たないまま本スクリプトは EXIT=0 で
  # 返る**（実測: GitHub ホストランナー / run 32554867883）。構造そのもの（reconcile 失敗が伝わらない）は
  # #953 で別途扱う。ここは「pin が外れたことに気づける」ための口である。
  K3S_IMAGE="${K3S_IMAGE:-rancher/k3s:v1.35.4-k3s1}"
  CREATE_ARGS+=(--image "$K3S_IMAGE")
  if ! k3d cluster list "$CLUSTER" >/dev/null 2>&1; then
    k3d cluster create "$CLUSTER" "${CREATE_ARGS[@]}"
  else
    echo "    cluster '$CLUSTER' exists — reuse"
  fi
else
  # Rancher Desktop: 内蔵 k3s を使う（Preferences → Kubernetes を有効化しておくこと）。
  if ! kubectl cluster-info >/dev/null 2>&1; then
    echo "ERROR: k8s に到達できません。Rancher Desktop の Kubernetes を有効化し、" >&2
    echo "       kubectl の context を rancher-desktop にしてください。" >&2
    exit 1
  fi
  echo "    Rancher Desktop 内蔵 k3s を使用（context: $(kubectl config current-context))"
fi

# NFR-18, ADR-0124 決定 1, IADR-0517 (#1830): **dev 以外の kube context で、レルム管理のロールを持つ機密クライアントの secret を
#   公知の dev の値で作らない。** 判定は scripts/lib/dev-client-secret-guard.sh の 1 本（bootstrap.sh・reconcile-realm.sh と共有）。
#   置き場所は context が確定した直後（k3d は cluster create で `k3d-<cluster>` へ切り替える）・Secret を 1 つも書く前。
#   対象: 3 つとも **ESO の有無によらず**見る。［2026-10-09 / #1834 / IADR-0518］[3/7] の realm の取り込み元（Secret keycloak-realm-import）が
#   ESO の有無によらず 3 つの env から作られ、空の PVC の Keycloak は [4/7] でそれを取り込む（＝4 つ目の作る口）。従来は ESO=1 で
#   identity-admin・mcp-client-admin を Vault の種（bootstrap.sh）の判定に任せていたが、bootstrap は [4/7] より後に走るので、
#   止まる前に Keycloak が dev の値で 2 つを作っていた。
# shellcheck source=scripts/lib/dev-client-secret-guard.sh
. "$ROOT/scripts/lib/dev-client-secret-guard.sh"
dev_secret_args=("identity-admin=${IDENTITY_ADMIN_CLIENT_SECRET:-}" "reset-gate=${RESET_GATE_CLIENT_SECRET:-}"
  "mcp-client-admin=${MCP_CLIENT_ADMIN_CLIENT_SECRET:-}")
dev_client_secret_guard "k8s-local-up.sh" "${dev_secret_args[@]}" || exit 1
unset dev_secret_args

# NFR, ADR-0021, IADR-0317 (#1691): **入口がすでに Istio Ingress Gateway へ移っているかを、クラスタの状態で読む。**
#   前回の istio-edge-up.sh が HelmChartConfig kube-system/traefik を `service.enabled: false`
#   （deploy/local/edge-istio/traefik-service-off.yaml）にしていれば移行済みである。**フラグ（ISTIO）では判定しない** ——
#   フラグは「今回の意図」であって「クラスタの現状」ではない。HelmChartConfig が無い・読めない・Service ありの形なら未移行。
#   🔴 移行済みのクラスタへ LOCALEDGE の段が `apply -k deploy/local/edge` を当てると、HelmChartConfig が Service ありへ戻り、
#   helm-controller の入れ直しが `wait svc/traefik`（180 秒）より遅くて rc=1 で止まる。作り直された svclb-traefik は
#   80/443/50000 を istio-ingressgateway と取り合って Pending のまま残る（#1691。2026-09-14 に続き 2 回目）。
#   判定は k8s-local-down.sh と共有する単一の口（scripts/lib/edge-state.sh）。ここへ複写しない（監査 #1694）。
# shellcheck source=scripts/lib/edge-state.sh
. "$ROOT/scripts/lib/edge-state.sh"
edge_on_istio() { edge_traefik_service_off kubectl; }
EDGE_ON_ISTIO=0
if [ "${LOCALEDGE:-}" = "1" ] && edge_on_istio; then
  EDGE_ON_ISTIO=1
  # 🔴 移行済みで ISTIO を指定しない再実行は、**何も書き換えないうちに止める**（fail-closed）。
  #   Istio のままのつもりで付け忘れたのか、Traefik へ戻したいのかは読めない。どちらでも従来の Traefik 経路は誤りである ——
  #   istio-ingressgateway を撤去しないまま Traefik を戻すと hostPort を取り合い、[6/7] は mesh の --set 無しで
  #   メッシュ設定を黙って外す。戻す正規の手段は istio-edge-down.sh の 1 コマンドである（IADR-0317 決定 7 の順序を持つ）。
  if [ "${ISTIO:-}" != "1" ]; then
    echo "ERROR: 入口はすでに Istio Ingress Gateway へ移っています（HelmChartConfig kube-system/traefik が service.enabled: false）。" >&2
    echo "       LOCALEDGE=1 だけ（ISTIO 未指定・ISTIO=0）では再実行できません（Traefik へ戻す処理が Istio の入口とポートを取り合います。#1691）。どちらかを選んでください:" >&2
    # NFR-16, IADR-0487 (#1710): mTLS を STRICT で使っているなら、そのまま写して実行できる形で告げる。
    #   ISTIO_MTLS_MODE を省いても [6/7] は現行の mesh.mtlsMode を引き継ぐ（下の判定）が、意図を明示した形を勧める。
    echo "       - Istio の入口のまま再実行する: ISTIO=1 LOCALEDGE=1 ISTIO_MTLS_MODE=STRICT bash scripts/k8s-local-up.sh --live" >&2
    echo "         （PERMISSIVE で使っているなら ISTIO_MTLS_MODE=PERMISSIVE。省くと現行の mesh.mtlsMode を引き継ぐ）" >&2
    echo "       - Traefik へ戻す: 先に bash scripts/istio-edge-down.sh --live を実行してから LOCALEDGE=1 で再実行する" >&2
    exit 1
  fi
  echo "    入口は Istio Ingress Gateway へ移し済み（HelmChartConfig traefik: service.enabled: false）。Traefik へ戻す段と待ちは飛ばす（#1691）"
fi

# NFR-16, ADR-0005, IADR-0488 (#1713) / IADR-0487 (#1710): **付けなかった ISTIO・ISTIO_MTLS_MODE は、現行の helm の宣言を引き継ぐ。**
#   [6/7] は --reuse-values 無しで helm upgrade する。以前は ISTIO 未指定を「メッシュ無し」と読み、values-local.yaml の
#   mesh.enabled: false が当たって、メッシュで動いているクラスタの宣言（PeerAuthentication・AuthorizationPolicy・注入）を
#   再実行のたびに**黙って**外していた（#1713）。ISTIO_MTLS_MODE を付けない再実行が STRICT を PERMISSIVE へ戻したのと同じ型（#1710）。
#   選び方はどちらも 明示（env）＞ 現行（helm get values msp の mesh.*）＞ 初回の既定（ISTIO はメッシュ無し・mTLS は PERMISSIVE）。
#   - ISTIO 未指定 × メッシュ宣言あり → ISTIO=1 として以降を進める（明示と同じ段: コントロールプレーン・注入・LOCALEDGE なら Istio の入口）。
#     ISTIO_MTLS_MODE も未指定なら同じ読みからモードを引き継ぐ。外すのは ISTIO=0 の明示だけである。
#   - ISTIO=0 は読まない（従来の「ISTIO 無し」と同じ経路。既定のバイト等価はこちらへ移した）。
#   🔴 **読めないときは止める（fail-closed）。** 外す側へ倒すと #1713 を黙って起こし、入れる側へ倒すと ISTIO 無しで立てたクラスタへ
#   推測でメッシュを入れる（全 Pod の作り直し）。どちらも推測である。読めない理由（helm に届かない・mesh.enabled: true なのに
#   mtlsMode が壊れている）は終了コードで分けていないので、ISTIO_MTLS_MODE を明示していても ISTIO 未指定なら止める（ISTIO=1 を足せば進む）。
#   **副作用より前**（[2/7] の前）に置き、止めるときは何も書き換えていない。読むのは 1 回だけ（両方の判定に使う）。
#   移行済みの入口で ISTIO が 1 でない再実行は、この前（#1691 の拒否）で既に止まっている。
if [ -z "${ISTIO:-}" ] || { [ "$ISTIO" = "1" ] && [ -z "${ISTIO_MTLS_MODE:-}" ]; }; then
  # shellcheck source=scripts/lib/mesh-mtls-mode.sh
  . "$ROOT/scripts/lib/mesh-mtls-mode.sh"
  mesh_rc=0
  mesh_current="$(current_mesh_mtls_mode)" || mesh_rc=$?
  if [ "$mesh_rc" -ge 2 ]; then
    if [ -z "${ISTIO:-}" ]; then
      echo "ERROR: 現行のメッシュ宣言（mesh.enabled / mesh.mtlsMode）を helm リリース ${MSP_HELM_RELEASE:-msp} から読めませんでした（helm get values ${MSP_HELM_RELEASE:-msp} -n $MSP_NS）。" >&2
      echo "       ISTIO を付けない再実行は現行のメッシュ宣言を引き継ぎます。読めないまま進むと、メッシュを黙って外すか推測で入れることになるため、止めます。" >&2
      echo "       どちらかを明示して再実行してください（前回と同じ他の指定〔LOCALEDGE 等〕も付ける）:" >&2
      echo "         - メッシュを保つ: ISTIO=1 ISTIO_MTLS_MODE=STRICT bash scripts/k8s-local-up.sh --live（PERMISSIVE で使っているなら PERMISSIVE）" >&2
      echo "         - メッシュを外す: ISTIO=0 bash scripts/k8s-local-up.sh --live" >&2
    else
      echo "ERROR: 現行の mesh.mtlsMode を helm リリース ${MSP_HELM_RELEASE:-msp} から読めませんでした（helm get values ${MSP_HELM_RELEASE:-msp} -n $MSP_NS）。" >&2
      echo "       ISTIO_MTLS_MODE を付けない再実行は現行のモードを引き継ぎます。読めないまま PERMISSIVE で入ると STRICT を黙って緩めるため、止めます。" >&2
      echo "       モードを明示して再実行してください（前回と同じ他の指定〔LOCALEDGE 等〕も付ける。緩めるなら PERMISSIVE）:" >&2
      echo "         ISTIO_MTLS_MODE=STRICT ISTIO=1 bash scripts/k8s-local-up.sh --live" >&2
    fi
    exit 1
  fi
  if [ -z "${ISTIO:-}" ] && [ "$mesh_rc" = "0" ]; then
    ISTIO=1
    echo "    INFO: メッシュ（mesh.enabled: true）は現行を引き継ぎます（ISTIO 未指定。外すなら ISTIO=0）"
  fi
  # mesh_rc=1（リリースが無い・メッシュ未宣言＝初回）は何もしない: ISTIO 未指定はメッシュ無し、ISTIO=1 は PERMISSIVE で入る（下の [6/7]）。
  if [ "${ISTIO:-}" = "1" ] && [ -z "${ISTIO_MTLS_MODE:-}" ] && [ "$mesh_rc" = "0" ]; then
    ISTIO_MTLS_MODE="$mesh_current"
    echo "    INFO: mesh.mtlsMode は現行の ${mesh_current} を引き継ぎます（ISTIO_MTLS_MODE 未指定）"
  fi
fi

# SC-15, ADR-0097 決定 2 (#1500) / IADR-0488 (#1713): RESET_FLOOR が効かないことを告げる（値域の検査は冒頭）。ISTIO の引き継ぎの後で判定する。
if [ -n "${RESET_FLOOR:-}" ] && { [ "${ISTIO:-}" != "1" ] || [ "${LOCALEDGE:-}" != "1" ]; }; then
  echo "WARN: RESET_FLOOR=${RESET_FLOOR} は ISTIO=1 ＋ LOCALEDGE=1（Istio のエッジ）のときだけ効きます。この起動では効きません（床の器は常に立ちます）。" >&2
fi

echo "==> [2/7] build & import images"
bash "$ROOT/scripts/k8s-local-images.sh" "$CLUSTER"

echo "==> [3/7] infra namespace, secrets, realm ConfigMap & realm import Secret (dev 既定; env で上書き可)"
kubectl create namespace "$INFRA_NS" --dry-run=client -o yaml | kubectl apply -f -
# IADR-0099 (#310) PR-4: 基盤 secret（postgres/rabbitmq/keycloak-admin）は下の [4/7] infra rollout（ブロッキング）で
# **非 optional** に消費されるため、Vault/ESO がまだ存在しないこの時点で手動作成が必須（bootstrap）。よって PR-1〜3 と
# 異なり **ESO=1 でも手動 apply をスキップしない**。ESO はこの後の ESO ブロックで `creationPolicy: Merge` の
# ExternalSecret を適用し、既存 Secret に **同一値を上書きするだけ**（所有・再作成しない）で本番同等の供給経路を配線する。
apply_secret "$INFRA_NS" postgres        "password=${PG_PASSWORD:-postgres}"
# NFR, #1022: ブローカ自身の資格情報（利用者名・パスワード）を Secret 由来にする。
# deploy/local/infra/rabbitmq.yaml が RABBITMQ_DEFAULT_USER/PASS を**非 optional** に参照する。
# ⚠️ RABBITMQ_USER を変えるときは helm の global.messaging.user も併せて上書きすること
#    （app 側の接続文字列は chart が組む）。AST chart は自前の guest:guest を持つ（#1022 §申し送り）。
apply_secret "$INFRA_NS" rabbitmq        "username=${RABBITMQ_USER:-guest}" "password=${RABBITMQ_PASSWORD:-guest}"
# IADR-0369 (#1088): 管理者名も Secret に持つ。deploy/local/infra/keycloak.yaml（KEYCLOAK_ADMIN）と
# realm 後追い Job（deploy/local/keycloak-setup/realm-reconcile-job.yaml の KC_ADMIN_USER）が同じキーを読む
# ＝管理者名の単一情報源。ESO の externalsecret-keycloak-admin.yaml は Merge なので password だけ供給しても壊れない。
apply_secret "$INFRA_NS" keycloak-admin  "username=${KEYCLOAK_ADMIN_USER:-admin}" "password=${KEYCLOAK_ADMIN_PASSWORD:-admin}"
# NFR-18, ADR-0131 決定 4 の 2, IADR-0522 (#1839): キャッシュ・セッションストア（Valkey）の認証パスワード。
# deploy/local/infra/valkey.yaml が**非 optional** に読み、空なら起動しない（fail-closed）。BFF も同じ値を読む
# （[5/7] で MSP ns へ同じ値を置く。helm の services.bff.session.storeExistingSecret）。ESO=1 でも手で置く
# （[4/7] の rollout が消費する bootstrap であり、Vault の KV を持たない。postgres / rabbitmq と同じ扱い）。
# 🔴 **公知の dev 既定値を持たない。** 明示指定（env）＞ 既存の Secret の値 ＞ 初回だけ乱数。既存値を使い回すのは、
#    Valkey が起動時にしか設定を読まないため —— up のたびに変えると、走っている Valkey と BFF が食い違う。
#    値を変えたら `kubectl -n platform-infra rollout restart deploy/valkey` と BFF の作り直しが要る。
# 🔴 値は設定ファイルの引用符の中へ入るので、`"`・`\`・空白を含む値は拒む（乱数は 16 進のみ）。
session_store_password="${SESSION_STORE_PASSWORD:-}"
if [ -z "$session_store_password" ]; then
  session_store_password="$(kubectl -n "$INFRA_NS" get secret session-store-credentials \
    -o jsonpath='{.data.password}' 2>/dev/null | base64 -d 2>/dev/null || true)"
fi
if [ -z "$session_store_password" ]; then
  session_store_password="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
fi
case "$session_store_password" in
  '' | *[\"\\[:space:]]*)
    echo "ERROR: セッションストアのパスワードが空か、使えない文字（\" \\ 空白）を含みます（SESSION_STORE_PASSWORD を確かめてください）。" >&2
    exit 1 ;;
esac
apply_secret "$INFRA_NS" session-store-credentials "password=$session_store_password"

# SC-15, ADR-0045 決定 2-b/5/9 ＋ ADR-0078 決定 2, IADR-0332 / IADR-0404 (#438 / #1102 / #1245):
# 近接 MTA（deploy/mail-relay/）が **env(secretKeyRef) で読む**上流の接続条件。
# 🔴 **#1245 まで、この Secret を env で読む Pod は 1 つも無かった**（読み手は runbook の kcadm ＝ 人間）。
#    近接 MTA ができて読み手が生まれたので、**ESO の有無によらず必ず存在させる** ——
#    非 optional な secretKeyRef であり、無いと relay が起動せず [4/7] の rollout で止まる。
#    （非 optional なのは意図である。RELAYHOST が空だと Postfix は宛先の MX へ**直接**配送する＝外へ出る。）
# 既定は **Vault seed（deploy/local/vault/eso/bootstrap.sh）の導出と同じ**にする ——
#   宛先が捕捉用 MTA なら 1025 / STARTTLS 無し、それ以外なら計画 ADR の確定値 587 / STARTTLS 必須。
#   from / user / password は**空**（実環境の値が供給されるまでの fail-safe。runbook §2 の長さ判定が依る）。
# ESO=1 のときは externalsecret-keycloak-smtp.yaml が creationPolicy: Merge で同じ Secret へ Vault の値を
# 上書きする（keycloak-admin と同じ形。手動 apply を保持したまま実値へ差し替わる）。
SMTP_CAPTURE_HOST='mailpit.platform-infra.svc.cluster.local'
smtp_host="${SMTP_HOST:-$SMTP_CAPTURE_HOST}"
if [ "$smtp_host" = "$SMTP_CAPTURE_HOST" ]; then
  smtp_port_default='1025'; smtp_starttls_default='false'
else
  smtp_port_default='587';  smtp_starttls_default='true'
fi
apply_secret "$INFRA_NS" keycloak-smtp \
  "host=$smtp_host" \
  "port=${SMTP_PORT:-$smtp_port_default}" \
  "starttls=${SMTP_STARTTLS:-$smtp_starttls_default}" \
  "from=${SMTP_FROM:-}" "user=${SMTP_USER:-}" "password=${SMTP_PASSWORD:-}"

# SC-15, ADR-0078 決定 4, IADR-0404 (#1245 PR-C):
# 門（deploy/mail-relay/reset-gate.yaml）が Admin REST を叩くための機密クライアントの secret。
# 🔴 **keycloak-smtp と同じく ESO の有無によらず作る** —— 門は **非 optional** な secretKeyRef で読み、
#    無いと [4/7] の rollout で止まる。資格情報が無い門は 401 を打ち続けるだけで、
#    **窓（実在利用者だけ 500）は開いたまま**になる。起動しないほうが気付ける。
# realm 側の宣言値（deploy/keycloak/microservices-platform-realm.json の reset-gate.secret）と
# **同じ既定**にする。ズレると Keycloak の token 端点が invalid_client を返す（wikijs-oidc と同じ罠）。
apply_secret "$INFRA_NS" reset-gate-oidc \
  "client-secret=${RESET_GATE_CLIENT_SECRET:-reset-gate-dev-secret-change-me}"

# 門のスクリプト本体（deploy/mail-relay/reset-gate.js）。テーマ・realm reconcile と同型で
# **--from-file の ConfigMap** にする（kustomize は root 外ファイルを参照できないため）。
# 毎回上書き＝リポジトリの版が正。🔴 [4/7] の apply より**前**に作る（Pod が起動時にマウントする）。
kubectl create configmap reset-gate-script -n "$INFRA_NS" \
  --from-file=reset-gate.js=deploy/mail-relay/reset-gate.js \
  --dry-run=client -o yaml | kubectl apply -f -

# SC-10, ADR-0078 決定 3, IADR-0421 (#1245 PR-B): 近接 MTA のキューを Prometheus 形式で出す
# サイドカー exporter の本体（deploy/mail-relay/mail-queue-exporter.js）。門と同型で --from-file にする。
# 🔴 [4/7] の apply より**前**に作る（mail-relay Pod が起動時にマウントする。無いと Pod が起動せず
#    rollout で止まる）。
kubectl create configmap mail-queue-exporter-script -n "$INFRA_NS" \
  --from-file=mail-queue-exporter.js=deploy/mail-relay/mail-queue-exporter.js \
  --dry-run=client -o yaml | kubectl apply -f -

# SC-15, NFR-13, ADR-0097 決定 2, IADR-0432 (#1500): リセット申請の床の器（deploy/mail-relay/reset-floor.js）。
# 門と同型で --from-file にする。［2026-09-26］器は deploy/mail-relay が**既定で**取り込むようになった
# （旧: RESET_FLOOR=1 のときだけ istio-edge-up.sh が作っていた）。🔴 [4/7] の apply より**前**に作る
# （Pod が起動時にマウントする。無いと Pod が起動せず rollout で止まる）。経路は istio-edge-up.sh が足す。
kubectl create configmap reset-floor-script -n "$INFRA_NS" \
  --from-file=reset-floor.js=deploy/mail-relay/reset-floor.js \
  --dry-run=client -o yaml | kubectl apply -f -

# realm の**宣言**の ConfigMap（実 realm ファイル＝単一情報源）。読み手は realm の後追い Job（期待値・--check-dev-secrets の比較元）と
# 申請の門（宣言の resetPasswordAllowed）。🔴 ［2026-10-09 / #1834 / IADR-0518］Keycloak の取り込み元は下の Secret に分けた ——
#   ここへ env の値を入れると、実の secret が平の ConfigMap に載り、--check-dev-secrets の比較元（宣言の dev の値）も壊れる。
# AST realm（submodule）が存在すれば同一 Keycloak へ併せて import する（MSP+AST 連結）。
realm_args=(--from-file=microservices-platform-realm.json=deploy/keycloak/microservices-platform-realm.json)
ast_realm="src/ai-stock-trading/infra/keycloak/realm-export.json"
if [ -f "$ast_realm" ]; then
  realm_args+=(--from-file=ai-stock-trading-realm.json="$ast_realm")
  echo "    + AST realm を同梱 import します"
fi
kubectl create configmap keycloak-realms -n "$INFRA_NS" "${realm_args[@]}" \
  --dry-run=client -o yaml | kubectl apply -f -

# NFR-18, ADR-0124 決定 1, IADR-0518 (#1834): Keycloak の `--import-realm`（空の PVC の初回・realm を消した後の再起動）の**取り込み元**。
#   deploy/local/infra/keycloak.yaml が /opt/keycloak/data/import へマウントする。中身は上と同じ realm ファイルだが、レルム管理のロールを
#   持つ 3 クライアント（identity-admin / reset-gate / mcp-client-admin）は、env（*_CLIENT_SECRET）を与えたものだけ宣言の dev の値を
#   env の値へ差し替える（判定器 dev_client_secret_realm_for_import。対象・env の名前・宣言の値の単一情報源）。env が無ければ宣言のまま
#   （dev の context の既定は従来と同じ中身）。🔴 実の secret を含み得るので **ConfigMap ではなく Secret** にする。値は apply_secret が
#   0700 の一時ディレクトリの 0600 のファイル経由で渡し、必ず消す（#1793。どのプロセスの引数にも載らない）。
realm_import_mp="$(dev_client_secret_realm_for_import deploy/keycloak/microservices-platform-realm.json)" || exit 1
realm_import_args=("microservices-platform-realm.json=${realm_import_mp}")
if [ -f "$ast_realm" ]; then
  realm_import_args+=("ai-stock-trading-realm.json=$(< "$ast_realm")")
fi
apply_secret "$INFRA_NS" keycloak-realm-import "${realm_import_args[@]}"
unset realm_import_mp realm_import_args

# IADR-0261 (#438): realm.json の loginTheme/accountTheme=platform を解決するテーマ実体
# （deploy/keycloak/themes/platform/）を ConfigMap 化する。deploy/local/infra/keycloak.yaml 側は
# `optional: true` の fail-safe 参照のため、この ConfigMap が無くても Pod は起動するが、その場合
# ログイン画面が「テーマが見つからない」500 になる（従来は deploy/local/README.md「手動でステップ
# 実行する場合」の手動コマンドが必須だった。本行で自動配線し、手動手順の必要を無くす）。
# キー名・items の対応は keycloak.yaml のマウント定義と一致させる（単一情報源はテーマ実ファイル）。
kubectl create configmap keycloak-theme-platform -n "$INFRA_NS" \
  --from-file=login-theme-properties=deploy/keycloak/themes/platform/login/theme.properties \
  --from-file=login-css=deploy/keycloak/themes/platform/login/resources/css/platform.css \
  --from-file=account-theme-properties=deploy/keycloak/themes/platform/account/theme.properties \
  --from-file=account-css=deploy/keycloak/themes/platform/account/resources/css/platform.css \
  --dry-run=client -o yaml | kubectl apply -f -

# NFR-21, IADR-0471 (#1560): 日次バックアップ（deploy/local/platform-backup。永続化 overlay が取り込む）の受取人＝age の**公開鍵**。
# 🔴 kustomize の外に置き、**与えられたときだけ**作り直す —— 既定の再実行で占位へ戻さないため（CronJob 側は optional で、
#    無い・占位のままなら何も書かずに失敗する）。公開鍵は秘密ではないが、秘密鍵はクラスタに置かない（手順書参照）。
if [ -n "${BACKUP_AGE_RECIPIENTS_FILE:-}" ]; then
  if [ ! -r "$BACKUP_AGE_RECIPIENTS_FILE" ]; then
    echo "ERROR: BACKUP_AGE_RECIPIENTS_FILE を読めません: $BACKUP_AGE_RECIPIENTS_FILE" >&2
    exit 1
  fi
  kubectl create configmap platform-backup-age-recipients -n "$INFRA_NS" \
    --from-file=recipients.txt="$BACKUP_AGE_RECIPIENTS_FILE" \
    --dry-run=client -o yaml | kubectl apply -f -
fi

echo "==> [4/7] apply in-cluster infra"
# IADR-0082 (#324) / IADR-0210 (#787) → IADR-0369 (#1088): 永続化オーバーレイ（Keycloak/Postgres/Qdrant を
# local-path PVC 化）は **既定オン**である。opt-out は `PERSIST=0`（使い捨てスタック専用）。
#
# 🔴 IADR-0082 決定 1 は「provisioner 不在クラスタで Pod が Pending になる」を理由に opt-in を選んだが、
#    本スクリプトが受け付けるランタイム（Rancher Desktop 内蔵 k3s / k3d）はどちらも local-path を同梱する。
#    その fail-safe が守った環境は実在せず、代わりに**常用クラスタが誰にも気付かれず非永続で立っていた**
#    （#1088。TOTP 資格情報が Pod 再作成で消え、#1114 の実測が代替に倒れた）。既定を永続へ返す。
# 🔴 黙って emptyDir へ落とさない。StorageClass が無ければ止める —— 「非永続で立っている」ことに気付けないのが
#    #1088 の本体であり、静かな fallback はそれを作り直すことになる。
INFRA_KUSTOMIZE="deploy/local/infra-persistence"
if [ "${PERSIST:-1}" = "0" ]; then
  INFRA_KUSTOMIZE="deploy/local/infra"
  echo "    [PERSIST=0] 永続化オーバーレイを外す（emptyDir。使い捨てスタック専用）"
else
  if ! kubectl get storageclass local-path >/dev/null 2>&1; then
    echo "ERROR: StorageClass 'local-path' が無く、永続化オーバーレイ（既定）を当てられません。" >&2
    echo "       provisioner を入れるか、使い捨てなら PERSIST=0 を明示してください（黙って非永続にはしない・#1088）。" >&2
    exit 1
  fi
  echo "    [PERSIST 既定] Keycloak(realm+runtime state)/Postgres/Qdrant(embeddings) を PVC 永続化（local-path）"
fi
kubectl apply -k "$INFRA_KUSTOMIZE"
# NFR-18, ADR-0131, IADR-0522 (#1839): 旧 Redis（認証なし）を消す。kustomize の apply は宣言から消えたリソースを
# 刈らないので、ここで消さないと既存クラスタに**認証なしのストアが残り続ける**（基準 D の穴が塞がらない）。
kubectl -n "$INFRA_NS" delete deployment/redis service/redis --ignore-not-found
echo "    waiting for infra to become Ready..."
# IADR-0100 (#354 障害2): アプリ Pod（[6/7] MSP・後続 AST）が起動する前にノードの inotify 上限を引き上げておく
# （inotify 枯渇による FileSystemWatcher クラッシュ＝広範 CrashLoopBackOff を防ぐ）。best-effort: busybox pull 等の
# 一時失敗で up 全体を止めない（pipefail 下でも `|| echo WARN` で握る。DaemonSet 自体は infra kustomize で適用済み）。
kubectl -n "$INFRA_NS" rollout status ds/inotify-sysctl --timeout=120s \
  || echo "    WARN: inotify-sysctl DaemonSet が未 Ready（best-effort・後追いで適用される）" >&2
kubectl -n "$INFRA_NS" rollout status deploy/postgres --timeout=180s
kubectl -n "$INFRA_NS" rollout status deploy/rabbitmq --timeout=180s
kubectl -n "$INFRA_NS" rollout status deploy/valkey --timeout=120s
kubectl -n "$INFRA_NS" rollout status deploy/keycloak --timeout=300s
kubectl -n "$INFRA_NS" rollout status deploy/qdrant --timeout=120s
kubectl -n "$INFRA_NS" rollout status deploy/otel-collector --timeout=120s
# SC-15, FR-22, ADR-0045 決定 9 (#1144): 捕捉用 MTA。**opt-in ゲートを持たない**（決定 9 は無条件）。
# ［2026-09-06 / #1245］ADR-0078 決定 2 以後、ここは Keycloak の送出先ではなく**近接 MTA の上流**である。
kubectl -n "$INFRA_NS" rollout status deploy/mailpit --timeout=120s
# SC-15, ADR-0078 決定 2, IADR-0404 (#1245): 近接 MTA（キュー付き Postfix）。**opt-in ゲートを持たない**。
# realm の smtpServer がここを指すので、**Keycloak より後に立つと最初の申請が送出に失敗する**
# （＝実在する利用者名だけ 500。#1143 の状態 C そのもの）。上の mailpit と同じ理由でここで待ち合わせる。
kubectl -n "$INFRA_NS" rollout status deploy/mail-relay --timeout=120s
# SC-15, ADR-0078 決定 4, IADR-0404 (#1245 PR-C): 近接 MTA へ投函できないときに申請を閉じる門。
# **opt-in ゲートを持たない**（決定 4 も無条件である）。近接 MTA の**後**に待ち合わせる ——
# 門は relay へ SMTP 取引を打つので、relay が立つ前に測ると 1 周期ぶん誤って閉じる。
kubectl -n "$INFRA_NS" rollout status deploy/reset-gate --timeout=120s
# SC-15, ADR-0097 決定 2, IADR-0432 (#1500): リセット申請の床の器。**opt-in ゲートを持たない**（既定 ON）。
# 器の readiness は上流（Keycloak）を映さない口なので、Keycloak の起動を待たずに Ready になる。
kubectl -n "$INFRA_NS" rollout status deploy/reset-floor --timeout=120s

echo "==> [5/7] MSP namespace & app secrets (dev 既定; fail-safe 空 = no-op)"
kubectl create namespace "$MSP_NS" --dry-run=client -o yaml | kubectl apply -f -
# ［IADR-0461 決定 5 / #1499］MinIO Console の OIDC client secret（minio-oidc。IADR-0093）の作成はここにあったが
# 撤去した。オブジェクトストレージは SeaweedFS へ差し替え、管理 Console を持たない（ADR-0106 決定 6）。
# NFR, SC-13, ADR-0026/ADR-0032, IADR-0251/IADR-0273/IADR-0316 (#1107): BFF セッション（Token Handler）の
# client secret。helm の deployment.yaml が **非 optional** な secretKeyRef（services.bff.session.existingSecret）で
# 参照するため、これが無いと bff-service Pod は起動できない（注入漏れが「空 secret で起動して login だけ 500」へ
# 倒れない）。dev 既定は realm import の置き場と同値 —— **ズレると Keycloak の PAR 端点が 401 を返し、
# `GET /bff/auth/login` が 500 になる**。ESO=1 のときは Vault→ExternalSecret 供給へ委譲する（二重所有回避）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" bff-oidc "client-secret=${BFF_OIDC_CLIENT_SECRET:-bff-dev-secret-change-me}"
fi
# NFR-18, ADR-0131 決定 4 の 2, IADR-0522 (#1839): BFF が読むセッションストアのパスワード（[3/7] と同じ値）。
# helm の deployment.yaml が**非 optional** な secretKeyRef で読むので、無ければ bff-service は起動しない。
# ESO の有無によらず置く（[3/7] の platform-infra 側と同じ bootstrap）。旧名の ExternalName `redis` は消す。
apply_secret "$MSP_NS" session-store-credentials "password=$session_store_password"
kubectl -n "$MSP_NS" delete service/redis --ignore-not-found
# FR-05, FR-09, SC-17, ADR-0004/ADR-0026, IADR-0301/IADR-0329 (#1101): SC-17（利用者アカウント管理）の
# 変更を Keycloak Admin REST へ反映する機密クライアント `identity-admin` の client secret。
# helm の deployment.yaml が **非 optional** な secretKeyRef で参照するため、これが無いと
# authorization-service Pod は起動できない —— 注入漏れが「偽の身元プロバイダで起動し、SC-17 の
# 変更がプロセス内にしか残らない」という静かな縮退へ倒れないようにするためである（#1101）。
# dev 既定は realm import の置き場と同値。ズレると client_credentials が 401 になり SC-17 が落ちる。
# ESO=1 のときは Vault→ExternalSecret 供給へ委譲する（二重所有回避）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" identity-admin-oidc \
    "client-secret=${IDENTITY_ADMIN_CLIENT_SECRET:-identity-admin-dev-secret-change-me}"
fi
# FR-16, SC-12, ADR-0123 決定 2, IADR-0516 決定 2 (#1817): SC-12 の無人の登録・属性の差し替えで McpServer が
# Keycloak Admin REST へ書く機密クライアント `mcp-client-admin`（manage-clients / manage-users）の client secret。
# helm の deployment.yaml が **非 optional** な secretKeyRef で参照するため、これが無いと mcp-service Pod は
# 起動できない —— 注入漏れが「書き込み口の無いまま起動し、無人の登録・差し替えが 503 を返し続ける」静かな縮退へ
# 倒れないようにするためである（identity-admin-oidc〔#1101〕と同型）。dev 既定は realm import の置き場と同値。
# ズレると client_credentials が 401 になり、無人の登録・差し替えが 502 になる。
# ESO=1 のときは Vault→ExternalSecret 供給へ委譲する（二重所有回避）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" mcp-client-admin-oidc \
    "client-secret=${MCP_CLIENT_ADMIN_CLIENT_SECRET:-mcp-client-admin-dev-secret-change-me}"
fi
# FR-02, FR-03, NFR-09, NFR-16, ADR-0029/ADR-0075, IADR-0379 決定 4 / IADR-0397 (#1255):
# east-west gRPC の**呼び出し側**が名乗る資格情報（realm の confidential client
# `retrieval-service` / `ingestion-service`。service account ＋ realm ロール platform-service のみ）。
# helm の deployment.yaml が **非 optional** な secretKeyRef（services.<name>.serviceToken）で
# 参照するため、これが無いと当該 Pod は起動できない —— 注入漏れが「匿名で呼んで
# UNAUTHENTICATED を食い続ける」静かな縮退へ倒れないようにするためである（#1107 と同型）。
# dev 既定は realm import の置き場と同値。ズレると client_credentials が 401 になる。
# ESO=1 のときは Vault→ExternalSecret 供給へ委譲する（二重所有回避）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" retrieval-service-token \
    "client-secret=${RETRIEVAL_SERVICE_CLIENT_SECRET:-retrieval-service-dev-secret-change-me}"
  apply_secret "$MSP_NS" ingestion-service-token \
    "client-secret=${INGESTION_SERVICE_CLIENT_SECRET:-ingestion-service-dev-secret-change-me}"
  # FR-04 / FR-12 / FR-18, IADR-0400 (#1255): テキスト生成（/complete 系）の呼び出し側 3 サービス。
  # 埋め込みの 2 つと同型・同じ理由（helm は非 optional な secretKeyRef で参照するので、
  # 欠けたら Pod が起動しない ＝ 注入漏れが静かな縮退へ倒れない）。
  apply_secret "$MSP_NS" aianalysis-service-token \
    "client-secret=${AIANALYSIS_SERVICE_CLIENT_SECRET:-aianalysis-service-dev-secret-change-me}"
  apply_secret "$MSP_NS" graph-service-token \
    "client-secret=${GRAPH_SERVICE_CLIENT_SECRET:-graph-service-dev-secret-change-me}"
  apply_secret "$MSP_NS" conversion-service-token \
    "client-secret=${CONVERSION_SERVICE_CLIENT_SECRET:-conversion-service-dev-secret-change-me}"
  # FR-05 / FR-13 / FR-16, IADR-0401 (#1255): 認可サービス（ABAC スコープ解決・利用者名簿の狭い
  # 読み口）の呼び出し側 3 サービス。上と同型・同じ理由。
  # 🔴 wiki / datasource / mcp-server は**利用者トークンの転送をやめる**ための資格情報である。
  apply_secret "$MSP_NS" wiki-service-token \
    "client-secret=${WIKI_SERVICE_CLIENT_SECRET:-wiki-service-dev-secret-change-me}"
  apply_secret "$MSP_NS" datasource-service-token \
    "client-secret=${DATASOURCE_SERVICE_CLIENT_SECRET:-datasource-service-dev-secret-change-me}"
  apply_secret "$MSP_NS" mcp-server-token \
    "client-secret=${MCP_SERVER_CLIENT_SECRET:-mcp-server-dev-secret-change-me}"
  # FR-19 / FR-20 / FR-21 / FR-22, ADR-0004, IADR-0419 (#1255): 個人資料の通知の受け付け
  # （NotificationService）の呼び出し側。上と同型・同じ理由である。
  # 🔴 **DocumentService が呼び出し元になるのはここが最初である**（従前は受け口だけを持っていた）。
  # 送出は fail-open なので、資格情報だけが欠けた状態で起動できると通知が静かに 1 件も届かなくなる
  # —— 非 optional な secretKeyRef で「起動しない」へ倒すのが安全側である。
  apply_secret "$MSP_NS" document-service-token \
    "client-secret=${DOCUMENT_SERVICE_CLIENT_SECRET:-document-service-dev-secret-change-me}"
fi
# NFR-09, ADR-0011/ADR-0026, IADR-0095/IADR-0328/IADR-0342 (#1127): Wiki.js の Keycloak OIDC
# ストラテジ（Wiki.js の DB 保持・manifest 化できない）を冪等に投入する
# `deploy/local/wikijs-setup/bootstrap.sh` 段 8 が読む client secret。
# 🔴 **この Secret を env で読む Pod は 1 つも無い**（読み手は bootstrap）。したがって ESO 後段の
# rollout 対象にも入れない —— 作るところまでが本行の責務である（keycloak-smtp と同じ性質）。
# 供給は **段 8 と同じ opt-in（WIKIJS_OIDC=1）に揃える** ——機能オフのときに未使用 Secret を残さない
# （grafana-oidc / headlamp-oidc と同じゲート意味論）。dev 既定は realm import の置き場と同値。
if [ "${WIKIJS_OIDC:-}" = "1" ] && [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" wikijs-oidc \
    "client-secret=${WIKIJS_OIDC_CLIENT_SECRET:-wiki-js-dev-secret-change-me}"
fi
# IADR-0097 (#310) PR-2: object-storage-credentials/wikijs-db/wikijs-sync は ESO=1 のとき Vault→ExternalSecret 供給へ委譲し
# 手動 apply をスキップする（二重所有回避）。既定（ESO 未設定）は従来どおり手動 apply（バイト等価）。
# IADR-0461 決定 3 (#1499): オブジェクトストレージ（SeaweedFS）の S3 資格情報。旧名 minio-credentials から製品名を外した
# （サーバとクライアント双方が読む Secret であり、次に製品が替わっても名前を変えずに済むようにする）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" object-storage-credentials \
    "accessKey=${OBJECT_STORAGE_ACCESS_KEY:-objectstorage-dev}" "secretKey=${OBJECT_STORAGE_SECRET_KEY:-objectstorage-dev-secret}"
  # NFR, ADR-0002, #1012: サービス DB のパスワード。**appsettings.json から接続文字列を撤去した**ため、
  # これが無いと各サービスは起動時に落ちる（注入漏れが既定資格情報で成功へ倒れない）。
  # dev 既定は init スクリプトが作る `kp`（deploy/local/infra/postgres.yaml）。env で上書きする。
  apply_secret "$MSP_NS" postgres-app "password=${APP_DB_PASSWORD:-kp}"
  # NFR, ADR-0027, #1022: ブローカのパスワード（app 側）。**appsettings.json から接続文字列を撤去した**ため、
  # これが無いと RabbitMQ を使う 7 サービスは起動時に落ちる（helm の deployment.yaml が
  # global.messaging.existingSecret を **非 optional** な secretKeyRef で参照する）。
  # dev 既定は step 3 のブローカ側と同値（env RABBITMQ_PASSWORD で両方を上書きする）。
  apply_secret "$MSP_NS" rabbitmq-app "password=${RABBITMQ_PASSWORD:-guest}"
  apply_secret "$MSP_NS" wikijs-db "password=${WIKIJS_DB_PASSWORD:-kp}"
  # FR-13, IADR-0327 (#1108): 🔴 **発行済みの API キーを空で潰さない。**
  # Wiki.js のセットアップ後、`deploy/local/wikijs-setup/bootstrap.sh` がここへ実キーを書き戻す。
  # 素直に `apiKey=${WIKIJS_SYNC_APIKEY:-}` で apply すると、**up を再実行するたびに空へ戻り**、
  # 次に wiki-service の Pod が作り直された瞬間に同期が全件エラーキューへ落ちる（#1108 の再来）。
  # しかも Pod が作り直されるまで表面化しないので、原因と結果が時間的に離れる。
  # 明示指定（env）＞ 既存値 ＞ 空 の順で選ぶ（既定は従来どおり空＝fail-safe）。
  wikijs_apikey_existing="$(kubectl -n "$MSP_NS" get secret wikijs-sync \
    -o jsonpath='{.data.apiKey}' 2>/dev/null | base64 -d 2>/dev/null || true)"
  apply_secret "$MSP_NS" wikijs-sync "apiKey=${WIKIJS_SYNC_APIKEY:-${wikijs_apikey_existing}}"
fi
# fail-safe: 空=外部 LLM を呼ばない（ADR-0010 ルーティングは明示設定時のみ有効）。
# IADR-0096 (#310): ESO=1 のときは llm-provider-credentials を Vault→ExternalSecret 供給に委譲し、手動 apply は
# スキップする（ExternalSecret が Secret を所有＝二重所有回避）。既定（ESO 未設定）は従来どおり手動 apply（バイト等価）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" llm-provider-credentials \
    "anthropic-api-key=${ANTHROPIC_API_KEY:-}" "openai-api-key=${OPENAI_API_KEY:-}" \
    "voyage-api-key=${VOYAGE_API_KEY:-}"
fi

# ADR-0005, #782: サービスメッシュ（Istio）。opt-in（既定オフ・fail-safe）。
#
# 🔴 **[6/7] より前に置く。** アプリチャートは PeerAuthentication / DestinationRule を
#   レンダリングするので、CRD が無いまま helm upgrade すると apply がその時点で失敗する。
#
# 🔴 **初回は PERMISSIVE で入る。** STRICT へは ISTIO_MTLS_MODE=STRICT で明示的に移す。
#   ［2026-10-01 / #1710］ISTIO_MTLS_MODE を付けない**再実行**は現行の mesh.mtlsMode を引き継ぐ（[2/7] の前の判定。IADR-0487）。
#   PERMISSIVE へ戻すのも ISTIO_MTLS_MODE=PERMISSIVE の明示だけである。
#   いきなり STRICT にすると、サイドカーの入っていない platform-infra（postgres / keycloak /
#   rabbitmq / qdrant / valkey …）との通信と、注入前の Pod からの通信が**同時に**壊れる。
#   段取りは「注入 → 全 Pod Ready → PERMISSIVE で疎通確認 → STRICT」である。
ISTIO_MESH_ARGS=""
if [ "${ISTIO:-}" = "1" ]; then
  echo "==> [opt-in] Istio service mesh (control plane)"
  helm repo add istio https://istio-release.storage.googleapis.com/charts >/dev/null 2>&1 || true
  helm repo update istio >/dev/null
  # base = CRD とクラスタ共通のリソース。istiod = コントロールプレーン。
  helm upgrade --install istio-base istio/base \
    -n istio-system --create-namespace --version "${ISTIO_VERSION:-1.30.4}" --wait --timeout 5m
  helm upgrade --install istiod istio/istiod \
    -n istio-system --version "${ISTIO_VERSION:-1.30.4}" \
    -f deploy/istio/istiod-values-local.yaml --wait --timeout 10m
  # CRD が Established になるまで待つ（apply の競合を避ける。cert-manager と同じ作法）。
  for crd in peerauthentications.security.istio.io destinationrules.networking.istio.io \
             authorizationpolicies.security.istio.io \
             gateways.networking.istio.io virtualservices.networking.istio.io; do
    kubectl wait --for=condition=Established "crd/$crd" --timeout=120s
  done
  # アプリチャート側のメッシュ宣言を有効化する（values-local.yaml は既定 false）。
  # #1115: 経路B の Keycloak は **platform-infra（メッシュ外）**に居る。STRICT のままだと
  # バックチャネルログアウトの POST が Envoy に落とされ、BFF へ一度も届かない（失効が
  # アクセストークンの寿命ぶん遅れる）。ここだけを通す 2 枚組を有効にする
  # （範囲は「principal 無し × /bff/auth/backchannel-logout 以外は DENY」。istio-mtls.yaml の注記参照）。
  #
  # #1159 / IADR-0377: **STRICT は入口を Istio Ingress Gateway へ移した後でしか宣言しない。**
  #   LOCALEDGE=1 のときは、この [6/7] では PERMISSIVE を宣言し、末尾の istio-edge-up.sh が
  #   エッジを移した**後**で helm 経由で STRICT へ上げる（IADR-0307 決定 4 の段取り
  #   「注入 → 全 Pod Ready → PERMISSIVE で疎通確認 → STRICT」そのもの）。
  #   ここで先に STRICT を宣言すると、エッジがまだ kube-system の Traefik（メッシュ外）なので
  #   入口が 502 のまま残りの段が進む（#1072 実測）。
  #   🔴 **昇格も helm で行う。`kubectl patch` では書かない** —— Helm 4 はサーバサイド apply なので
  #   patch が field manager を奪い、以後の helm upgrade が conflict で恒久的に失敗する
  #   （#1159 の「手動 patch によるドリフト」の正体。scripts/lib/mesh-mtls-mode.sh 冒頭に実測を置いた）。
  #   #1691: **入口がすでに Istio へ移っている再実行では降格しない**（EDGE_ON_ISTIO。[1/7] の後でクラスタの状態から読む）。
  #   入口は既に Envoy であり、降格の理由（入口がメッシュ外の Traefik）が無い。再実行のたびに STRICT → PERMISSIVE → STRICT と緩めない。
  #   #1710: ↑ が成り立つのは ISTIO_MTLS_MODE が決まっているときである。未指定の再実行は [2/7] の前の判定が
  #   現行の mesh.mtlsMode を ISTIO_MTLS_MODE へ引き継ぐので、ここで空が残るのはリリースが無い・メッシュ未宣言（初回）だけ
  #   ＝ 下の既定 PERMISSIVE は**初回の値**であって、再実行の降格ではない（IADR-0487）。
  ISTIO_MTLS_MODE_AT_INSTALL="${ISTIO_MTLS_MODE:-PERMISSIVE}"
  if [ "${LOCALEDGE:-}" = "1" ] && [ "${ISTIO_MTLS_MODE:-}" = "STRICT" ] && [ "$EDGE_ON_ISTIO" != "1" ]; then
    ISTIO_MTLS_MODE_AT_INSTALL="PERMISSIVE"
  fi
  ISTIO_MESH_ARGS="--set mesh.enabled=true --set mesh.mtlsMode=${ISTIO_MTLS_MODE_AT_INSTALL} --set namespace.istioInjection=true --set mesh.backchannelLogout.fromOutsideMesh=true"
  # 🔴 経路B は values-local.yaml が namespace.create=false のため、**Helm は Namespace を作らない**
  #   ＝ istioInjection=true にしても注入ラベルが誰にも適用されない。ここで明示的に貼る。
  #   （本番像は namespace.create=true なのでチャートが貼る。同じ結果を 2 経路で担保する。）
  kubectl label namespace "$MSP_NS" istio-injection=enabled --overwrite
  echo "    mTLS モード: ${ISTIO_MTLS_MODE:-PERMISSIVE}（STRICT へ移すには ISTIO_MTLS_MODE=STRICT で再実行）"
  if [ "$ISTIO_MTLS_MODE_AT_INSTALL" != "${ISTIO_MTLS_MODE:-PERMISSIVE}" ]; then
    echo "    （この段では ${ISTIO_MTLS_MODE_AT_INSTALL}。STRICT への昇格は入口を移した後 = istio-edge-up.sh の [5/5]）"
  fi
fi
# FR-02, FR-03, #992 案 2, IADR-0313: 決定的ローカル埋め込み（ティアA・プロセス内計算）。opt-in。
#
# 🔴 **文書を索引可能にするための最後の 1 ピースである。** SEARCHSEED=1 が本文つき文書を投入しても、
#   埋め込みが得られなければ取り込みは Embedded=false のチャンクを索引しない（fail-closed）。
#   索引に 1 点も入らないので `POST /bff/search` は「検索が壊れている」ときと同じ `200 ＋ 空` を返し、
#   **壊れていても緑になる**（#992 / IADR-0255）。
#
# 🔴 **越境判定（機密区分 × ティア）は緩めていない。** 本経路は HTTP を行わずプロセス内で計算するため、
#   ティアA（社外送信なし）の定義をそのまま満たす。confidential/restricted が外部へ出ないことは不変。
#
# 🔴 **使い捨てのスタック専用である。** 検索品質は保証されない（表層の文字 3-gram のみ）。
#   残しておきたいクラスタに対して立てないこと。既定（未設定）は --set を 1 バイトも足さない。
LOCALEMBED_ARGS=""
if [ "${LOCALEMBED:-}" = "1" ]; then
  echo "==> [opt-in] deterministic local embedding (tier A, in-process; 使い捨てスタック専用・#992)"
  LOCALEMBED_ARGS="--set embedding.deterministicLocal.enabled=true"
fi
echo "==> [6/7] helm upgrade --install (values-local)"
# NFR, IADR-0210 決定 7, #1688: **後から Recreate へ変えた Deployment は、既存リリースに RollingUpdate の既定値
#   （spec.strategy.rollingUpdate）が残っている。** Helm 4 の SSA はそれを消さず（チャートの `rollingUpdate: null` でも
#   消えないことを実測）、`type: Recreate` と同居して upgrade 全体が落ちる。helm の前に冪等な patch で寄せる。
#   列挙はチャートで `type: Recreate` を宣言する Deployment の全件（k8s-local-up.test.js がチャートと突き合わせる）。
#   新規クラスタ・移行済みでは何もせず、何も出さない（lib 冒頭）。
RECREATE_DEPLOYMENTS="seaweedfs wiki-js"
. "$ROOT/scripts/lib/recreate-strategy.sh"
# shellcheck disable=SC2086  # RECREATE_DEPLOYMENTS は空白区切りの名前の列。意図的に分割する。
reconcile_recreate_strategy "$MSP_NS" $RECREATE_DEPLOYMENTS
# shellcheck disable=SC2086  # ISTIO_MESH_ARGS / LOCALEMBED_ARGS は空か複数フラグ。意図的に分割する。
helm upgrade --install msp deploy/helm/microservices-platform \
  -n "$MSP_NS" -f deploy/local/values-local.yaml $ISTIO_MESH_ARGS $LOCALEMBED_ARGS

# #782: サイドカーは**既存 Pod には後から入らない**。注入ラベルを付けたあとに作り直す。
# helm upgrade だけでは Pod テンプレートが変わらないサービスが残るため、明示的に restart する。
if [ "${ISTIO:-}" = "1" ]; then
  # #1316: **別名を注入の前に当てる。** 注入の rollout restart で作り直された Pod は、
  # 依存（postgres / rabbitmq / valkey …）の ExternalName 別名がまだ無い状態で起動する。
  # 実測（run 34037589847）: 14:05:33 注入 → 14:06:07 `connection refused` → 14:18:33 別名（**12 分後**）。
  # health が 500 を返して probe が落ち、**8 回まで再起動**し、rollout status は 10 分の空待ちで WARN を出す。
  # 🔴 **ISTIO 無しでは表面化しない** —— 作り直しが起きないので、別名が [7/7] で当たるまで Pod は待てる。
  # 下の [7/7] は**残す**（kubectl apply は冪等であり、二重適用は no-op である）。
  # 🔴 **[7/7] を前倒ししない** —— 段番号の echo は既定経路の出力に含まれており、
  # 動かすと k8s-local-up.test.js の「既定バイト等価」が崩れる。**opt-in の側だけを足す。**
  echo "==> [opt-in] ExternalName aliases（サイドカー注入の前に当てる / #1316）"
  kubectl apply -f deploy/local/aliases/microservices-platform-externalnames.yaml
  kubectl apply -f deploy/local/aliases/platform-infra-externalnames.yaml

  echo "==> [opt-in] Istio sidecar injection (rollout restart)"
  kubectl -n "$MSP_NS" rollout restart deployment
  # #1088 実測（空の namespace から ISTIO=1 ＋ ESO=1 で立てたとき）: ESO が供給する Secret（postgres-app /
  # rabbitmq-app / bff-oidc / identity-admin-oidc …）は**この後**の ESO ブロックで初めて作られるため、ここで
  # Pod の Ready を待つと CreateContainerConfigError のまま 10 分待って up 全体が落ちる。既存クラスタへの
  # 再実行（Secret が残っている）では表面化しなかった。待ちは best-effort にし、**fail-closed の門は
  # check-stack-ready.js の G1**（Deployment の available と Pod の Ready）に置く。
  kubectl -n "$MSP_NS" rollout status deployment --timeout="${ISTIO_ROLLOUT_TIMEOUT:-10m}" \
    || echo "    WARN: サイドカー注入後の rollout が期限内に Ready にならない（ESO=1 なら Secret 供給後に Ready になる。判定は check-stack-ready.js の G1）" >&2
  echo "    注入の確認: kubectl -n $MSP_NS get pods -o custom-columns=NAME:.metadata.name,CONTAINERS:.spec.containers[*].name"
fi

echo "==> [7/7] ExternalName aliases (素のサービス名 -> platform-infra FQDN)"
kubectl apply -f deploy/local/aliases/microservices-platform-externalnames.yaml
# #1115: 逆向き（platform-infra -> microservices-platform）。Keycloak がバックチャネルログアウトを
# 素のサービス名 `bff-service` で叩けるようにする。理由はファイル冒頭の注記を参照。
kubectl apply -f deploy/local/aliases/platform-infra-externalnames.yaml

# FR-05, NFR-09, ADR-0004/ADR-0026, IADR-0369 (#1088 / #324): realm JSON の差分を稼働 realm へ当てる。
# 🔴 **`--import-realm` は既存 realm があると黙って飛ばす（IGNORE_EXISTING）。永続化（既定）で realm が
#    PVC に残るようになった瞬間から、realm JSON を直しても稼働 realm は変わらない。** ここが唯一の反映経路である
#    （旧 reconcile-backchannel-logout.sh の 1 値だけの後追い（IADR-0336 決定 3）を、宣言全体の差分へ一般化した）。
# 🔴 pod 内で kcadm.sh を exec しない（本体が OOMKilled になる）。同じ namespace の Job が Admin REST API を叩く。
# best-effort: 失敗しても up 全体は止めない（再実行は冪等）。**fail-closed の門は check-stack-ready.js の G9。**
echo "==> Keycloak realm の追随（宣言との差分を Job で当てる / 冪等 / IADR-0369）"
bash "$ROOT/deploy/local/keycloak-setup/reconcile-realm.sh" \
  || echo "    WARN: realm の追随に失敗（best-effort）。bash deploy/local/keycloak-setup/reconcile-realm.sh で再実行できる" >&2

# ADR-0006, IADR-0077 (AST#24): opt-in オーバーレイ（既定オフ・fail-safe）。
# 既定（env 未設定）では以下は一切実行されず、上記 [1/7]..[7/7] の挙動は不変。
if [ "${OBSERVABILITY:-}" = "1" ]; then
  echo "==> [opt-in] observability stack (Prometheus/Loki/Tempo/Grafana)"
  # IADR-0090 (#353): Grafana は Keycloak OIDC(generic OAuth) で認証する（匿名 Admin は廃止）。
  # client secret は平文で manifest に置かず Secret 経由（dev 既定 or env 上書き・headlamp-oidc と同型）。
  # grafana.yaml は optional 参照のため Secret 不在でも Pod は起動し local admin へフォールバックする（fail-safe）。
  # IADR-0098 (#310) PR-3: ESO=1 のときは grafana-oidc も Vault→ExternalSecret 供給へ委譲し手動 apply をスキップ（二重所有回避）。
  if [ "${ESO:-}" != "1" ]; then
    apply_secret "$INFRA_NS" grafana-oidc \
      "client-secret=${GRAFANA_OIDC_CLIENT_SECRET:-grafana-dev-secret-change-me}"
  fi
  # IADR-0210 (#787) → IADR-0369 (#1088): 可観測性側も永続化オーバーレイが**既定**（INFRA_KUSTOMIZE と同じ意味論）。
  # **OBSERVABILITY=1 のときだけ効く**（永続化単独ではスタック自体が立たない）。opt-out は PERSIST=0。
  OBS_KUSTOMIZE="deploy/local/observability-persistence"
  if [ "${PERSIST:-1}" = "0" ]; then
    OBS_KUSTOMIZE="deploy/local/observability"
    echo "    [PERSIST=0] 可観測性 4 種も emptyDir（使い捨てスタック専用）"
  else
    echo "    [PERSIST 既定] Prometheus/Loki/Tempo/Grafana を PVC 永続化（local-path）"
  fi
  kubectl apply -k "$OBS_KUSTOMIZE"
  # otel-collector を forwarding 構成（debug-only から切替）へ反映。
  kubectl -n "$INFRA_NS" rollout restart deploy/otel-collector
  echo "    Grafana: kubectl -n $INFRA_NS port-forward svc/grafana 3000:3000  # http://localhost:3000"
fi

if [ "${VAULT:-}" = "1" ]; then
  echo "==> [opt-in] Vault dev + ClusterSecretStore (要 External Secrets Operator CRD)"
  # dev root トークン（dev 既定 or env 上書き・平文は Git に載せない）。
  apply_secret "$INFRA_NS" vault-dev-token "token=${VAULT_DEV_ROOT_TOKEN:-devroot}"
  # IADR-0094 (#353): Keycloak OIDC の client secret（平文コミットしない・dev 既定 or env 上書き）。
  # Vault OIDC は runtime 設定のため vault-dev.yaml へは注入せず、bootstrap（deploy/local/vault/oidc/bootstrap.sh）が
  # 本 Secret を読んで `vault write auth/oidc/config` へ渡す。
  # IADR-0098 (#310) PR-3: ESO=1 のときは vault-oidc も Vault→ExternalSecret 供給へ委譲し手動 apply をスキップ（二重所有回避）。
  if [ "${ESO:-}" != "1" ]; then
    apply_secret "$INFRA_NS" vault-oidc "client-secret=${VAULT_OIDC_CLIENT_SECRET:-vault-dev-secret-change-me}"
  fi
  # IADR-0457 (#1479): Vault の永続化は **既定オン**（file ストレージを PVC に置き、Pod 内ラッパーが init / unseal /
  # 固定 root トークン / kv-v2 mount を毎回行う）。-dev（インメモリ）は k3s 再起動で全状態（k8s auth・policy・KV・
  # OIDC・画面 SC-22 で入れた秘密）を失い、ESO の store が InvalidProviderConfig に倒れた（2026-09-16 実測）。
  # opt-out は上の [4/7] と同じ PERSIST=0（使い捨てスタック専用・従来の deploy/local/vault とバイト等価）。
  # StorageClass の不在は [4/7] のガードが先に止める。
  VAULT_KUSTOMIZE="deploy/local/vault-persistence"
  if [ "${PERSIST:-1}" = "0" ]; then
    VAULT_KUSTOMIZE="deploy/local/vault"
    echo "    [PERSIST=0] Vault は -dev（インメモリ・再起動で揮発）"
  else
    echo "    [PERSIST 既定] Vault を file ストレージ＋PVC で永続化（Pod 内ラッパーが init / unseal / 固定トークンを自動化）"
  fi
  if kubectl get crd clustersecretstores.external-secrets.io >/dev/null 2>&1; then
    kubectl apply -k "$VAULT_KUSTOMIZE"
  else
    echo "    WARN: external-secrets.io CRD 未導入のため ClusterSecretStore/Vault は skip。" >&2
    echo "          先に ESO を導入する（deploy/local/vault/README.md）。Vault dev のみ適用（非永続・再起動で揮発）:" >&2
    kubectl apply -f deploy/local/vault/vault-dev.yaml
  fi
  # 永続化版は readinessProbe（vault status＝unseal 済み）を持つ。ここで待たないと、後段の ESO bootstrap が
  # unseal 前に `kubectl exec` して "Vault is sealed" で落ちる。-dev（PERSIST=0）は probe が無く即座に返る。
  kubectl -n "$INFRA_NS" rollout status deploy/vault --timeout=180s
fi

# IADR-0096 (#310): Vault＋ESO で secret を Pod へ自動供給する（本番同等・k8s auth）。opt-in（既定オフ・fail-safe）。
# 既定（ESO 未設定）では本ブロックは実行されず、手動 apply_secret のままバイト等価。VAULT=1 併用が前提
# （dev Vault が起動済みであること）。PR-1 は llm-provider-credentials 1本で end-to-end 疎通する。
if [ "${ESO:-}" = "1" ]; then
  echo "==> [opt-in] External Secrets Operator + Vault k8s auth (secret 自動供給・#310)"
  # 早期ガード: ESO=1 は dev Vault（VAULT=1）を前提とする。bootstrap は `kubectl exec deploy/vault` を使うため、
  # Vault Deployment が無いと分かりにくいエラーで中断する。明示的に案内して止める（fail-fast）。
  if ! kubectl -n "$INFRA_NS" get deploy vault >/dev/null 2>&1; then
    echo "ERROR: ESO=1 は VAULT=1 と併用してください（dev Vault が必要）。例: VAULT=1 ESO=1 bash scripts/k8s-local-up.sh --live" >&2
    exit 1
  fi
  # ESO 本体（idempotent・CRD 同梱）。webhook 準備を待つ。
  # #310 フォローアップ（本 fix）: chart 版を **pin** する。latest 追従だと、v1beta1 の提供を停止し v1 を GA と
  # する版（例 2.x）を掴んだ瞬間、deploy/local/vault/eso/*.yaml（本 fix で external-secrets.io/v1 へ移行済み）が
  # 参照する API と CRD の served バージョンが乖離し、"no matches for kind ... in version" で apply が失敗する。
  # 既定は v1 を GA 提供する安定版（動作実証済み）。上書きは ESO_CHART_VERSION で可能（同じく v1 提供版を選ぶこと）。
  ESO_CHART_VERSION="${ESO_CHART_VERSION:-2.8.0}"
  helm repo add external-secrets https://charts.external-secrets.io >/dev/null 2>&1 || true
  helm repo update external-secrets >/dev/null 2>&1 || true
  helm upgrade --install external-secrets external-secrets/external-secrets \
    --version "$ESO_CHART_VERSION" \
    -n external-secrets --create-namespace --set installCRDs=true --wait
  # Vault の SA に TokenReview 権限（k8s auth の reviewer）。
  kubectl apply -f deploy/local/vault/eso/vault-auth-rbac.yaml
  # SC-22, ADR-0095 決定 3, IADR-0456 決定 4・5 (#1477): 画面（/admin/secrets）で書いた値を待たずに Pod へ届ける部品。
  # (1) AST の名前空間を冪等に作る —— 下の BFF の Role と Reloader の scoped RBAC がこの名前空間に置かれる。
  #     AST の配備（src/ai-stock-trading/scripts/k8s-local-deploy.sh）は後から走り、同じ冪等作成をする。
  kubectl create namespace ai-stock-trading --dry-run=client -o yaml | kubectl apply -f -
  # (2) BFF（SA microservices-platform/bff）が platform-infra / ai-stock-trading の ExternalSecret へ force-sync を付ける Role。
  #     MSP の名前空間の Role はチャート（services.bff.externalSecretSync）が描く。resourceNames は SC-22 の items[] と一致
  #     （Platform.Bff.Tests の SecretItemExternalSecretRbacTests が固定）。
  kubectl apply -f deploy/local/vault/eso/rbac-bff-externalsecret-sync.yaml
  # (3) Stakater Reloader: Secret が変わったら、注釈（secret.reloader.stakater.com/reload）を持つ Deployment を作り直す
  #     （env の secretKeyRef は Pod 起動時に 1 度だけ解決される。IADR-0103）。**chart と image を pin する**（ESO と同じ理由）。
  #     🔴 見る名前空間を 3 つに限る（watchGlobally=false ＋ namespaces ＝ 各名前空間の Role。クラスタ全体の Secret を読ませない）。
  #     上書きは RELOADER_CHART_VERSION / RELOADER_IMAGE_TAG（chart の既定の image と合わせること）。
  RELOADER_CHART_VERSION="${RELOADER_CHART_VERSION:-2.2.17}"
  RELOADER_IMAGE_TAG="${RELOADER_IMAGE_TAG:-v1.4.22}"
  helm repo add stakater https://stakater.github.io/stakater-charts >/dev/null 2>&1 || true
  helm repo update stakater >/dev/null 2>&1 || true
  helm upgrade --install reloader stakater/reloader \
    --version "$RELOADER_CHART_VERSION" \
    -n reloader --create-namespace \
    --set reloader.watchGlobally=false \
    --set "reloader.namespaces={$MSP_NS,$INFRA_NS,ai-stock-trading}" \
    --set image.tag="$RELOADER_IMAGE_TAG" \
    --wait
  # IADR-0457 (#1479): 🔴 新規クラスタでは上の VAULT ブロックの時点で ESO の CRD が無く、Vault は -dev（非永続）の
  # フォールバックで立っている。ESO を入れた「後」にここで永続化オーバーレイを当て直し、unseal を待ってから seed する
  # （当て直さないと初回 run の seed と画面の値がインメモリに入り、2 回目の run で消える）。既に永続化版なら unchanged。
  if [ "${PERSIST:-1}" != "0" ]; then
    kubectl apply -k deploy/local/vault-persistence
    kubectl -n "$INFRA_NS" rollout status deploy/vault --timeout=180s
  fi
  # Vault k8s auth の enable/config＋policy＋role `eso`＋seed（runtime・kubectl exec 経由・平文非コミット・再実行可）。
  bash deploy/local/vault/eso/bootstrap.sh
  # 上で k8s auth backend/role を設定した「後に」store を kubernetes 認証へ上書きする（同名 vault-backend）。
  # 既定（VAULT=1 単独）は token 認証の store（deploy/local/vault/clustersecretstore.yaml）のままで既存フロー不変。
  kubectl apply -f deploy/local/vault/eso/clustersecretstore-k8s.yaml
  # ExternalSecret で secret を Vault→Secret 供給する（PR-1: llm、PR-2: object-storage-credentials/wikijs-db/wikijs-sync、
  # PR-3: OIDC client secret 群）。
  kubectl apply -f deploy/local/vault/eso/externalsecret-llm.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-object-storage.yaml
  # NFR, ADR-0002 (#1012): サービス DB のパスワード。手動 apply は上の `ESO != 1` ブロックで
  # スキップされるので、**これが唯一の供給元**である（欠けると DB を持つ全サービスが起動しない）。
  kubectl apply -f deploy/local/vault/eso/externalsecret-postgres-app.yaml
  # NFR, ADR-0027 (#1022): ブローカのパスワード（app 側）。手動 apply は上の `ESO != 1` ブロックで
  # スキップされるので、**これが唯一の供給元**である（欠けると RabbitMQ を使う 7 サービスが起動しない）。
  kubectl apply -f deploy/local/vault/eso/externalsecret-rabbitmq-app.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-wikijs-db.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-wikijs-sync.yaml
  # IADR-0098 (#310) PR-3: OIDC client secret 群。grafana/vault/headlamp-oidc は platform-infra ns（MSP ns の minio-oidc は IADR-0461 で撤去）。
  # ExternalSecret は namespaced だが ClusterSecretStore は cluster-scoped のため両 ns から同名 store を参照できる。
  # 元の手動 apply のゲート意味論に合わせて供給する（機能オフ時に未使用 Secret を残さない＝元の条件付き apply と対称）:
  #  - vault-oidc: VAULT 前提（ESO=1 は VAULT 併用ガード下＝ここでは常に真）で常時
  #  - grafana-oidc / headlamp-oidc: 各機能（OBSERVABILITY / HEADLAMP）が有効なときだけ供給
  # #1107: BFF セッションの client secret。手動 apply は上の `ESO != 1` ブロックでスキップされるので、
  # **これが唯一の供給元**である（欠けると bff-service Pod が起動しない）。常時供給。
  kubectl apply -f deploy/local/vault/eso/externalsecret-bff-oidc.yaml
  # #1101: SC-17 の Keycloak Admin REST 反映に使う client secret。手動 apply は上の `ESO != 1`
  # ブロックでスキップされるので、**これが唯一の供給元**である（欠けると authorization-service Pod が
  # 起動しない）。常時供給。
  kubectl apply -f deploy/local/vault/eso/externalsecret-identity-admin-oidc.yaml
  # #1817: SC-12 の無人の登録・差し替えで Keycloak Admin REST へ書く client secret。手動 apply は上の `ESO != 1`
  # ブロックでスキップされるので、**これが唯一の供給元**である（欠けると mcp-service Pod が起動しない）。常時供給。
  kubectl apply -f deploy/local/vault/eso/externalsecret-mcp-client-admin-oidc.yaml
  # #1255: east-west gRPC の呼び出し側 s2s 資格情報。手動 apply は上の `ESO != 1` ブロックで
  # スキップされるので、**これが唯一の供給元**である（欠けると retrieval / ingestion Pod が起動しない）。常時供給。
  kubectl apply -f deploy/local/vault/eso/externalsecret-retrieval-service-token.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-document-service-token.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-ingestion-service-token.yaml
  # #1255（テキスト生成の 3 呼び出し元）。上と同型・同じ理由で常時供給。
  kubectl apply -f deploy/local/vault/eso/externalsecret-aianalysis-service-token.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-graph-service-token.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-conversion-service-token.yaml
  # #1255 第 3 スライス（認可サービスの 3 呼び出し元）。上と同型・同じ理由で常時供給。
  kubectl apply -f deploy/local/vault/eso/externalsecret-wiki-service-token.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-datasource-service-token.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-mcp-server-token.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-vault-oidc.yaml
  # #1127: Wiki.js の OIDC ストラテジ seed が読む client secret。段 8 と同じ opt-in に揃える
  # （機能オフのときに未使用 Secret を残さない）。**env で読む Pod は無い**ので rollout 対象外。
  if [ "${WIKIJS_OIDC:-}" = "1" ]; then
    kubectl apply -f deploy/local/vault/eso/externalsecret-wikijs-oidc.yaml
  fi
  if [ "${OBSERVABILITY:-}" = "1" ]; then
    kubectl apply -f deploy/local/vault/eso/externalsecret-grafana-oidc.yaml
  fi
  if [ "${HEADLAMP:-}" = "1" ]; then
    kubectl apply -f deploy/local/vault/eso/externalsecret-headlamp-oidc.yaml
  fi
  # IADR-0099 (#310) PR-4: 基盤 secret（postgres/rabbitmq/keycloak-admin）。手動 apply は step 3 で保持済み（bootstrap
  # 必須）。ここでは creationPolicy: Merge の ExternalSecret を適用し、既存 Secret へ Vault の同一値をマージするのみ
  # （所有・再作成しない）。値は seed=step3 と完全一致のため Pod 再起動や PVC 初期化済み DB の不整合は起きない。常時供給。
  kubectl apply -f deploy/local/vault/eso/externalsecret-postgres.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-rabbitmq.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-keycloak-admin.yaml
  # SC-15, FR-22, ADR-0026/ADR-0045 決定 6 ＋ ADR-0078 決定 2, IADR-0332 / IADR-0404 (#1102 / #1245):
  # 近接 MTA が読む上流の接続条件。**手動 apply は step [3/7] で保持済み**（dev 既定。ESO の有無によらず作る）。
  # ここでは creationPolicy: Merge の ExternalSecret を適用し、既存 Secret へ Vault の値をマージするのみ。
  # 🔴 **#1245 で読み手が人間から Pod へ変わった。** 旧: runbook の `kcadm` 手順（人間）が読み realm の
  # 実行時状態へ反映する。現: **近接 MTA（platform-infra/mail-relay）が env(secretKeyRef) で読む**。
  # したがって下の同期待ちと rollout の対象に**入れる**（IADR-0332 決定 2 の前提が外れた）。
  # 既定では `from`/`user`/`password` が空（bootstrap.sh の fail-safe）。relay は空の user/password を
  # 「認証なし」として扱い、空の from では外向きの差出人写像を張らない（どちらも dev の正しい姿である）。
  kubectl apply -f deploy/local/vault/eso/externalsecret-keycloak-smtp.yaml
  # SC-15, ADR-0078 決定 4, IADR-0404 (#1245 PR-C): 門（reset-gate）の client secret。
  # **手動 apply は step [3/7] で保持済み**（ESO の有無によらず作る。無いと門が起動しない）。
  # ここでは creationPolicy: Merge の ExternalSecret を適用し、既存 Secret へ Vault の値をマージするのみ。
  kubectl apply -f deploy/local/vault/eso/externalsecret-reset-gate-oidc.yaml
  # NFR-02, NFR-21, ADR-0076 決定 4, ADR-0079 決定 1 (#1287): 合成監視のプローブが client_credentials で
  # 名乗るための client secret。**SYNTHETIC=1 のときだけ apply する**（wikijs-oidc と同じ形。
  # 立てていないゲートの ExternalSecret を作ると `get secret` が NotFound を返して運用者を惑わす）。
  # 種は bootstrap.sh が**無条件に**入れてある（他の secret と同じ。立て直しの順序に依存させない）。
  if [ "${SYNTHETIC:-}" = "1" ]; then
    kubectl apply -f deploy/local/vault/eso/externalsecret-synthetic-monitor-oidc.yaml
  fi
  # 確認コマンドは実際に apply した ExternalSecret のみ列挙する（無効ゲートの secret を挙げて NotFound で
  # 誤解させない）。MSP ns は常時 18 本（#1022 で rabbitmq-app、#1107 で bff-oidc、#1101 で identity-admin-oidc、#1290 で retrieval-service-token / ingestion-service-token、#1255 の第 2 スライスで aianalysis / graph / conversion の 3 本、第 3 スライスで wiki / datasource / mcp-server の 3 本、通知の面（IADR-0419）で document-service-token の 1 本を追加し 6 → 7 → 8 → 9 → 11 → 14 → 17 → 18 へ、IADR-0461（#1499）で minio-oidc を撤去し 17 へ、#1817 で mcp-client-admin-oidc を追加し 18 へ数え直した。**値は上の msp_es を数え直して出す** —— 継ぎ足すと必ずずれる）＋有効ゲートの wikijs-oidc（#1127）と synthetic-monitor-oidc（#1287）。infra ns は基盤 3 本＋vault-oidc/keycloak-smtp 常時（#1102 で keycloak-smtp を追加し 4 → 5、#1245 で reset-gate-oidc を追加し 5 → 6 へ数え直した）＋有効ゲートの grafana/headlamp-oidc。
  msp_es="llm-provider-credentials object-storage-credentials postgres-app rabbitmq-app wikijs-db wikijs-sync bff-oidc identity-admin-oidc mcp-client-admin-oidc retrieval-service-token ingestion-service-token aianalysis-service-token graph-service-token conversion-service-token wiki-service-token datasource-service-token mcp-server-token document-service-token"
  [ "${WIKIJS_OIDC:-}" = "1" ] && msp_es="$msp_es wikijs-oidc"
  [ "${SYNTHETIC:-}" = "1" ] && msp_es="$msp_es synthetic-monitor-oidc"
  infra_es="postgres rabbitmq keycloak-admin vault-oidc keycloak-smtp reset-gate-oidc"
  [ "${OBSERVABILITY:-}" = "1" ] && infra_es="$infra_es grafana-oidc"
  [ "${HEADLAMP:-}" = "1" ] && infra_es="$infra_es headlamp-oidc"
  echo "    ESO: llm/object-storage-credentials/postgres-app/rabbitmq-app/wikijs-db/wikijs-sync（MSP ns 常時）＋ 基盤 postgres/rabbitmq/keycloak-admin"
  echo "         （infra ns・Merge・手動 apply 保持）＋ vault-oidc/keycloak-smtp/reset-gate-oidc、および有効ゲートの grafana/headlamp-oidc（infra ns）と wikijs-oidc（MSP ns）を"
  echo "         Vault(secret/msp/...)→ExternalSecret 供給（基盤以外の手動 apply はスキップ済み）。"
  echo "         確認(MSP):   kubectl -n $MSP_NS get externalsecret,secret $msp_es"
  echo "         確認(infra): kubectl -n $INFRA_NS get externalsecret,secret $infra_es"

  # IADR-0103 (#354): env の `secretKeyRef` は **Pod 起動時に一度だけ解決され、その後の Secret 更新は
  # 既存 Pod の env へ反映されない**。ESO が Secret を作る/上書きするのは Pod 起動より後になるため、対象 Pod は
  # 「空」または「旧値」の env を保持し続ける。実害として MinIO（当時。IADR-0461 で撤去）=`unauthorized_client / Invalid client credentials`
  # （client_secret 空）、Grafana=OIDC client_secret 空、LlmGateway=`API key is invalid`（旧鍵保持）が発生した。
  # ESO 供給後に対象 Deployment を rollout し直して env を作り直す。
  # best-effort（未デプロイ・未有効ゲート・同期遅延で `up` を止めない）。
  echo "    ESO 供給後の rollout（secretKeyRef の env を供給後の値で作り直す）"

  # 1) 先に **SecretSynced を待つ**。待たずに restart すると新 Pod もまだ供給前の Secret を掴んで同じ状態で
  #    固定され、rollout が無駄打ちになる（ESO の初回同期は helm/apply 直後には完了していない）。
  #    ExternalSecret の `condition=Ready` が ESO の SecretSynced（status=True・reason=SecretSynced）に対応する。
  eso_wait() { # ns externalsecret-name [name...]
    local ns="$1"; shift
    for es in "$@"; do
      kubectl -n "$ns" wait --for=condition=Ready "externalsecret/$es" \
        --timeout="${ESO_SYNC_TIMEOUT:-90s}" >/dev/null 2>&1 \
        && echo "      synced $ns/$es" \
        || echo "      warn: $ns/$es が SecretSynced になりません（rollout は継続）"
    done
  }
  # #1255: retrieval / ingestion の s2s 資格情報。**env(secretKeyRef) で読む Pod がある**ので
  # rollout の前に同期を待つ（待たずに restart すると新 Pod も供給前の Secret を掴む。IADR-0103）。
  # #1817: mcp-client-admin-oidc も同じ理由で待つ（mcp-service が env で読む。下の rollout の対象に既に居る）。
  msp_sync="llm-provider-credentials object-storage-credentials wikijs-db wikijs-sync retrieval-service-token ingestion-service-token aianalysis-service-token graph-service-token conversion-service-token wiki-service-token datasource-service-token mcp-server-token mcp-client-admin-oidc document-service-token"
  # #1127: wikijs-oidc を待つ理由は **rollout ではない**（env で読む Pod が無い）。`up` の後段で走る
  # deploy/local/wikijs-setup/bootstrap.sh の段 8 がこの Secret を読むためである。同期前だと段 8 は
  # 「client secret を取得できない」で何もせずに終わり、**OIDC ログインが入らないまま up は緑で終わる。**
  [ "${WIKIJS_OIDC:-}" = "1" ] && msp_sync="$msp_sync wikijs-oidc"
  # #1287: synthetic-monitor-oidc は **プローブの rollout のために待つ**。プローブ Deployment は
  # 本スクリプトの最後（SYNTHETIC ゲート）で作られるので、ここで同期を終えていないと
  # **最初の Pod が空の client secret を掴んで 401 を打ち続ける**（IADR-0103）。
  [ "${SYNTHETIC:-}" = "1" ] && msp_sync="$msp_sync synthetic-monitor-oidc"
  # shellcheck disable=SC2086
  eso_wait "$MSP_NS" $msp_sync
  # #1102 → #1245: keycloak-smtp は **rollout のために待つ**（近接 MTA が env で読む。上の apply の注記参照）。
  # 待たずに restart すると新しい relay Pod も供給前の Secret を掴んで同じ状態で固定される（IADR-0103）。
  # 併せて、`up` の直後に運用者が案内文どおり
  # `kubectl -n platform-infra get externalsecret,secret keycloak-smtp` を打ったときに
  # **Secret が「まだ作られていない」状態で NotFound を返さない**ことも担保する。
  # 他と同じく best-effort（warn を出して継続。実値が空でも同期自体は成立する）。
  # #1245 PR-C: reset-gate-oidc も **rollout のために待つ**（門が env で読む。同期前に restart すると
  # 新しい門も供給前の値をつかみ、401 を打ち続ける＝**窓が開いたまま**になる。IADR-0103）。
  infra_sync="keycloak-smtp reset-gate-oidc"
  [ "${OBSERVABILITY:-}" = "1" ] && infra_sync="$infra_sync grafana-oidc"
  [ "${HEADLAMP:-}" = "1" ] && infra_sync="$infra_sync headlamp-oidc"
  # shellcheck disable=SC2086
  [ -n "$infra_sync" ] && eso_wait "$INFRA_NS" $infra_sync

  # 2) 供給後の値で env を作り直す。対象＝**ESO 管理 Secret を env(secretKeyRef) で参照する Deployment**。
  #      seaweedfs         : object-storage-credentials（S3 の管理者資格情報。IADR-0461）
  #      llmgateway-service: llm-provider-credentials（Llm__ApiKey・Embedding__Voyage__ApiKey）
  #      wiki-service      : wikijs-sync（WikiJs__ApiKey）
  #      wiki-js           : wikijs-db（DB_PASS）
  #      retrieval-service : retrieval-service-token（ServiceToken__ClientSecret。#1255）
  #      ingestion-service : ingestion-service-token（ServiceToken__ClientSecret。#1255）
  #      aianalysis-service: aianalysis-service-token（ServiceToken__ClientSecret。#1255）
  #      graph-service     : graph-service-token（ServiceToken__ClientSecret。#1255）
  #      conversion-service: conversion-service-token（ServiceToken__ClientSecret。#1255）
  #      wiki-service      : wiki-service-token（ServiceToken__ClientSecret。#1255。wikijs-sync と 2 本読む）
  #      datasource-service: datasource-service-token（ServiceToken__ClientSecret。#1255）
  #      mcp-service       : mcp-server-token（ServiceToken__ClientSecret。#1255。🔴 Deployment 名は `mcp-service`）
  #                          mcp-client-admin-oidc（McpClientProvisioning__Keycloak__ClientSecret。#1817）
  #      document-service  : document-service-token（ServiceToken__ClientSecret。#1255。IADR-0419）
  #    対象外: postgres / rabbitmq / keycloak-admin は creationPolicy: Merge で seed（step 3）と**同一値**のため
  #    env は変化せず、再起動は DB/broker を無用に落とすだけ（IADR-0099）。vault-oidc は env 参照が無く
  #    bootstrap が CLI で読むため rollout 不要。
  for d in seaweedfs llmgateway-service wiki-service wiki-js retrieval-service ingestion-service \
           aianalysis-service graph-service conversion-service datasource-service mcp-service \
           document-service; do
    kubectl -n "$MSP_NS" rollout restart "deploy/$d" >/dev/null 2>&1 \
      && echo "      restarted $MSP_NS/$d" || echo "      skip $MSP_NS/$d（未デプロイ）"
  done
  # SC-15, ADR-0078 決定 2, IADR-0404 (#1245): 近接 MTA は keycloak-smtp を env(secretKeyRef) で読む。
  # ESO が実値を供給した後は**必ず作り直す** —— さもないと relay は「空の from / 捕捉用 MTA 宛」のまま動き、
  # 運用者は Vault へ実値を入れたのに 1 通も外へ出ない（静かな縮退）。
  kubectl -n "$INFRA_NS" rollout restart deploy/mail-relay >/dev/null 2>&1 \
    && echo "      restarted $INFRA_NS/mail-relay" || echo "      skip $INFRA_NS/mail-relay（未デプロイ）"
  # SC-15, ADR-0078 決定 4, IADR-0404 (#1245 PR-C): 門は reset-gate-oidc を env(secretKeyRef) で読む。
  # 実値が供給された後は必ず作り直す —— 古い secret のままだと門は 401 を打ち続け、
  # **投函できない状態を検知できない**（窓が開いたままなのに、誰も落ちない）。
  kubectl -n "$INFRA_NS" rollout restart deploy/reset-gate >/dev/null 2>&1 \
    && echo "      restarted $INFRA_NS/reset-gate" || echo "      skip $INFRA_NS/reset-gate（未デプロイ）"
  if [ "${OBSERVABILITY:-}" = "1" ]; then
    kubectl -n "$INFRA_NS" rollout restart deploy/grafana >/dev/null 2>&1 \
      && echo "      restarted $INFRA_NS/grafana" || echo "      skip $INFRA_NS/grafana（未デプロイ）"
  fi
  if [ "${HEADLAMP:-}" = "1" ]; then
    kubectl -n "$INFRA_NS" rollout restart deploy/headlamp >/dev/null 2>&1 \
      && echo "      restarted $INFRA_NS/headlamp" || echo "      skip $INFRA_NS/headlamp（未デプロイ）"
  fi
fi

if [ "${ARGOCD:-}" = "1" ]; then
  echo "==> [opt-in] ArgoCD bootstrap (手順は deploy/local/argocd/README.md)"
  kubectl create namespace argocd --dry-run=client -o yaml | kubectl apply -f -
  # IADR-0103 (#354): argocd namespace に keycloak の ExternalName エイリアスを張る。無いと DNS がノードの
  # リゾルバへフォールスルーし、手順A の hosts エントリ `127.0.0.1 keycloak` を拾って argocd-server が
  # **自分自身の :8080** へ discovery を投げ 404 になる（OIDC ログイン不能）。
  # ※ #780 で issuer が `https://keycloak.localhost` へ移ったため OIDC はこのエイリアスを使わなくなった。
  #   それでも残すのは、**エイリアスが無い状態でノードの hosts へフォールスルーする経路自体は生きている**
  #   ためである（argocd ns の他の何かが `keycloak` を引いたときに同じ罠を踏む）。撤去は別途測ってから。
  kubectl apply -f deploy/local/aliases/argocd-externalnames.yaml
  # IADR-0077 (#348): ArgoCD 公式 install manifest は巨大な CRD（applicationsets.argoproj.io 等）を含み、
  # client-side apply では manifest 全体が last-applied-configuration annotation に載って 262144 バイト
  # 上限を超過し失敗する（既知問題）。server-side apply は annotation を作らず managed fields で差分管理する
  # ため大 CRD が通る。--force-conflicts は旧 client-side 実行済みクラスタ再実行時の field 所有権競合を
  # server-side manager が奪取して冪等・再実行安全にする（本ブートストラップは install manifest の再適用を
  # 前提とし、ArgoCD 自身が同 CRD を自己管理下に置いた後の再実行は想定しない）。
  # IADR-0519 (#1843): URL は版のタグ（ARGOCD_VERSION。冒頭で形を検査済み）。従前の `stable` は取得のたびに中身が変わり得た。
  echo "    -> Argo CD ${ARGOCD_VERSION} (IADR-0519)"
  kubectl apply --server-side --force-conflicts -n argocd -f "https://raw.githubusercontent.com/argoproj/argo-cd/${ARGOCD_VERSION}/manifests/install.yaml"
  kubectl apply -f deploy/argocd/appproject.yaml -f deploy/argocd/application.yaml
  if [ -d src/ai-stock-trading/deploy/argocd ]; then
    kubectl apply -f src/ai-stock-trading/deploy/argocd/appproject.yaml -f src/ai-stock-trading/deploy/argocd/application.yaml
  fi
  # IADR-0092 (#353): ArgoCD を Keycloak OIDC(SSO) へ配線する。dex は使わず oidc.config を直接指定。
  # 集約後 URL（argocd.localhost:50000・ホスト名ベース・#357/IADR-0091）で登録する。
  # IADR-0220 (#841): エッジは https で終端する（NFR-11）。server.insecure=true は据え置く ——
  # TLS を終端するのはエッジ（IADR-0091 の Traefik ／ ISTIO=1 では IADR-0317 の Istio Ingress Gateway）
  # であり、そこから argocd-server への転送は in-cluster の平文（あるいは mTLS）のままだからである
  # （insecure を外すと argocd-server 自身が http→https リダイレクトを返し、エッジ経由が二重終端で壊れる）。
  # fail-safe: local admin は残す（OIDC は追加・未マッピングは policy.default='' で
  # no-access）。install が作成した ConfigMap/Secret へ merge patch で「追加のみ」適用し既存キー（server.secretkey 等）
  # を保持する（apply による全置換はしない）。client secret は平文で置かず argocd-secret に merge patch。
  # IADR-0243 決定 3 / #780: argocd-cm の oidc.config は issuer を **エッジ host** に取るため、
  # サーバ側の TLS 検証にローカル CA が要る。PEM は cert-manager が実行時に作るものであり
  # リポジトリに焼き込めないので、patch を当てる直前にプレースホルダを live の値へ置換する。
  #
  # 🔴 **CA が取れないときは rootCA の 2 行ごと落とす**（fail-safe）。プレースホルダのまま
  #    patch すると argocd-server が「不正な PEM」で OIDC を初期化できず、**local admin まで
  #    巻き添えで落ちる**。CA が無い＝OIDC が成立しないだけに留める。
  # 🔴 **ファイル名は `argocd-cm-patch.yaml` のままにする。** 一時ディレクトリへ置くが、
  #    basename を変えると `k8s-local-up.test.js` の「各 opt-in トークンが単独で検出力を持つ」検査が
  #    このトークンを dead と判定する（実際に踏んだ）。**描画結果は同じ patch なので、名前も同じにする。**
  ARGOCD_TMPDIR="$(mktemp -d)"
  ARGOCD_CM_PATCH="$ARGOCD_TMPDIR/argocd-cm-patch.yaml"
  ARGOCD_CA_FILE="$ARGOCD_TMPDIR/edge-root-ca.pem"
  kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\.crt}' 2>/dev/null \
    | base64 -d 2>/dev/null | sed 's/^/      /' > "$ARGOCD_CA_FILE" || true
  if [ -s "$ARGOCD_CA_FILE" ]; then
    # oidc.config はブロックスカラーなので、PEM の各行を 6 スペースでインデントして差し込む。
    sed -e "/__LOCAL_EDGE_ROOT_CA_PEM__/{r $ARGOCD_CA_FILE" -e "d}" \
      deploy/local/argocd/oidc/argocd-cm-patch.yaml > "$ARGOCD_CM_PATCH"
  else
    echo "    warn: cert-manager/local-edge-root-ca が読めない。argocd の oidc.config から rootCA を落とす（OIDC は成立しない・local admin は残る）"
    grep -v -e 'rootCA: |' -e '__LOCAL_EDGE_ROOT_CA_PEM__' \
      deploy/local/argocd/oidc/argocd-cm-patch.yaml > "$ARGOCD_CM_PATCH"
  fi
  kubectl -n argocd patch configmap argocd-cm --type merge --patch-file "$ARGOCD_CM_PATCH"
  rm -f "$ARGOCD_CM_PATCH" "$ARGOCD_CA_FILE"; rmdir "$ARGOCD_TMPDIR" 2>/dev/null || true
  kubectl -n argocd patch configmap argocd-rbac-cm --type merge --patch-file deploy/local/argocd/oidc/argocd-rbac-cm-patch.yaml
  kubectl -n argocd patch configmap argocd-cmd-params-cm --type merge --patch-file deploy/local/argocd/oidc/argocd-cmdparams-patch.yaml
  # NFR-18 (#1793): client secret を kubectl の引数へ載せない（従前は `-p '{"stringData":…}'`）。0600 の patch ファイル経由で渡す。
  #   サブシェルの EXIT trap で必ず消す（apply_secret と同じ形）。
  ( umask 077
    d="$(mktemp -d)"
    trap 'rm -rf "$d"' EXIT
    v="$(json_str "${ARGOCD_OIDC_CLIENT_SECRET:-argocd-dev-secret-change-me}")" || exit 1
    printf '{"stringData":{"oidc.keycloak.clientSecret":%s}}' "$v" > "$d/argocd-secret-patch.json"
    kubectl -n argocd patch secret argocd-secret --type merge --patch-file "$d/argocd-secret-patch.json" )
  # server.insecure（cmd-params）と oidc の反映のため argocd-server を再起動する（CM は live 反映だが params は要再起動）。
  kubectl -n argocd rollout restart deploy/argocd-server >/dev/null 2>&1 || true
  echo "    ArgoCD OIDC: https://argocd.localhost:50000 (LOCALEDGE=1) — Keycloak でログイン（local admin は break-glass）。"
fi

# IADR-0080 (#271): Headlamp（k8s 管理 UI・Keycloak OIDC）。opt-in（既定オフ・fail-safe）。
if [ "${HEADLAMP:-}" = "1" ]; then
  echo "==> [opt-in] Headlamp (k8s management UI, Keycloak OIDC)"
  # OIDC client secret（dev 既定 = realm import の dev 値・env で上書き可・manifest に平文で置かない）。
  # IADR-0098 (#310) PR-3: ESO=1 のときは headlamp-oidc も Vault→ExternalSecret 供給へ委譲し手動 apply をスキップ（二重所有回避）。
  if [ "${ESO:-}" != "1" ]; then
    apply_secret "$INFRA_NS" headlamp-oidc \
      "client-secret=${HEADLAMP_OIDC_CLIENT_SECRET:-headlamp-dev-secret-change-me}"
  fi
  kubectl apply -k deploy/local/headlamp
  echo "    Headlamp: kubectl -n $INFRA_NS port-forward svc/headlamp 4466:80  # http://localhost:4466"
  # IADR-0105 (#399): 本 opt-in は Headlamp のデプロイのみを行い、apiserver には一切触れない（[1/7] 参照）。
  # ローカルのログインは token 方式が正式手順（OIDC は #388 の HTTPS 化と同時にのみ成立・IADR-0084 追記）。
  # IADR-0108 (#398): token ログイン用 SA `headlamp-viewer` と閲覧専用 RBAC は overlay に収録済みのため、
  # 上の apply -k で作成される（手動の kubectl create serviceaccount/clusterrolebinding は不要）。
  echo "    ログインは token 方式: kubectl -n $INFRA_NS create token headlamp-viewer --duration=24h"
  echo "    権限は閲覧専用（get/list/watch）。手順の詳細は deploy/local/README.md の「Headlamp」参照。"
fi


# NFR, #781 / IADR-0105 の再導入: apiserver の OIDC 検証。opt-in（既定オフ・fail-safe）。
#
# 🔴 **この経路は経路B（Rancher Desktop 内蔵 k3s）専用である。** k3d では apiserver の
#   設定投入方法が違うため何もしない。
#
# 🔴 **`/etc/hosts` の追記が本体である。** k8s 1.30+ は issuer に https を強制するので
#   issuer は `https://keycloak.localhost/...` になるが、**apiserver（Go）は
#   `.localhost` を特別扱いしない。**
#
#     curl / musl  … `.localhost` を 127.0.0.1 へ解決する（RFC 6761 の特例）
#     Go のリゾルバ … 特例を持たず /etc/resolv.conf を引く → NXDOMAIN
#
#   **`curl` で到達性を確認しても、apiserver では通らない**（2026-08-30 に実測。
#   apiserver ログは `lookup keycloak.localhost: no such host` →
#   `oidc: authenticator not initialized` を 10 秒ごとに繰り返していた）。
#   **WSL は起動のたびに /etc/hosts を再生成するため、本スクリプトを再実行して復旧する。**
if [ "${APISERVER_OIDC:-}" = "1" ]; then
  if [ "$RUNTIME" != "rancher" ]; then
    echo "==> [opt-in] apiserver OIDC: skip（RUNTIME=$RUNTIME。経路B 専用）"
  elif ! command -v rdctl >/dev/null 2>&1; then
    echo "==> [opt-in] apiserver OIDC: skip（rdctl が無い）" >&2
  else
    echo "==> [opt-in] apiserver OIDC (issuer を https エッジへ・#781)"
    ISSUER_HOST="$(echo "${OIDC_ISSUER_URL:-https://keycloak.localhost/realms/platform}" | sed -E 's#^https?://##; s#/.*##')"

    # 1. apiserver（Go）が issuer host を解決できるようにする。
    rdctl shell sh -c "grep -q '[[:space:]]${ISSUER_HOST}\$' /etc/hosts || echo '127.0.0.1	${ISSUER_HOST}' >> /etc/hosts"
    echo "    /etc/hosts: ${ISSUER_HOST} -> 127.0.0.1"

    # 2. エッジ証明書を検証できる CA を置く（apiserver は oidc-ca-file で読む）。
    #    cert-manager のローカル root CA をクラスタから直接取り出す。
    rdctl shell sh -c "k3s kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\.crt}' | base64 -d > /etc/rancher/k3s/oidc-ca.crt"

    # 3. drop-in 設定を置く。**既存の config.yaml.d は壊さない。**
    rdctl shell sh -c "cat > /etc/rancher/k3s/config.yaml.d/oidc.yaml" <<EOF
kube-apiserver-arg:
  - "oidc-issuer-url=${OIDC_ISSUER_URL:-https://keycloak.localhost/realms/platform}"
  - "oidc-client-id=${OIDC_CLIENT_ID:-headlamp}"
  - "oidc-ca-file=/etc/rancher/k3s/oidc-ca.crt"
  - "oidc-username-claim=preferred_username"
  - "oidc-username-prefix=oidc:"
  - "oidc-groups-claim=groups"
  - "oidc-groups-prefix=oidc:"
EOF

    # 4. 切り戻しを先に置く。**apiserver の起動失敗はクラスタ停止を意味する**（#388 の前科）。
    rdctl shell sh -c "printf '#!/bin/sh\nrm -f /etc/rancher/k3s/config.yaml.d/oidc.yaml\nrc-service k3s restart\n' > /root/rollback-oidc.sh && chmod +x /root/rollback-oidc.sh"

    echo "    切り戻し: rdctl shell /root/rollback-oidc.sh"
    echo "    🔴 反映には k3s の再起動が要る: rdctl shell rc-service k3s restart"
    echo "       再起動後 180 秒たっても kubectl get nodes が返らなければ上の切り戻しを実行すること。"
  fi
fi

# IADR-0091 (#356): ローカルエッジ集約（opt-in・既定オフ・fail-safe）。Traefik 追加 entrypoint admin:50000 ＋
# platform フロント(80/443)/管理ツール(50000・ホスト名ベース)の Ingress を適用する。既定(env 未設定)では何も
# 実行されず挙動不変。k3d は上の cluster create(LOCALEDGE=1)で 80/443/50000 を公開済みが前提。Rancher Desktop は
# 内蔵 k3s の LB がポート公開するため cluster 再作成は不要（overlay 適用のみ）。#355 と競合する grafana.yaml/
# realm.json は触らない（redirect 追記・root_url は #355 マージ後の PR-2）。
if [ "${LOCALEDGE:-}" = "1" ]; then
  echo "==> [opt-in] local edge aggregation (Traefik admin:50000 + Ingress, IADR-0091)"
  # NFR, IADR-0317 (#1691): 入口がすでに Istio へ移っていれば（EDGE_ON_ISTIO。[1/7] の後でクラスタの状態から読んだ）、
  #   **Traefik を前提とする 4 つの処理を飛ばす** —— HelmChartConfig を Service ありへ戻す apply・その反映待ち（#953 の門）・
  #   pod 側の *.localhost を Traefik へ向ける CoreDNS・Traefik しか読まない argocd-ingress。戻すと 180 秒の待ちで rc=1 になり、
  #   svclb-traefik が istio-ingressgateway と 80/443/50000 を取り合う（#1691）。CoreDNS と経路は下の istio-edge-up.sh が当て直し、
  #   Traefik 側の宣言は切り戻し（istio-edge-down.sh）が当て直す。エッジ TLS（cert-manager）は Istio の入口も使うので飛ばさない。
  if [ "$EDGE_ON_ISTIO" = "1" ]; then
    echo "    -> 入口は Istio へ移し済み: Traefik の HelmChartConfig・反映待ち・CoreDNS（Traefik 向け）・argocd-ingress を飛ばす (#1691)"
  else
    kubectl apply -k deploy/local/edge

    # IADR-0258 (#953): ★ **HelmChartConfig の反映を待つ。来なければ落とす（fail-closed）。**
    #
    # `deploy/local/edge` の先頭資源 traefik-entrypoint.yaml は `kind: HelmChartConfig` であり、その効果
    # （Traefik Service に admin=50000 が生えること）は **k3s の helm-controller が非同期に**実現する。
    # `kubectl apply` が見るのは「オブジェクトを置けたか」だけで、後段の `helm upgrade` が values スキーマの
    # 型不一致（`error calling eq: incompatible types for comparison`）で落ちても **呼び出し側へは伝わらない**。
    # 実測では admin(50000) が立たないまま本スクリプトが EXIT=0 で返った（GitHub ホストランナー・
    # run 32554867883・k3s v1.30.4 同梱の traefik chart 25.0.3）。#783 の K3S_IMAGE pin は**回避**であって
    # 解決ではない —— pin が外れれば同じ穴へ落ちる。
    #
    # 🔴 **警告を出して続行してはならない。** それは EXIT=0 と同じであり、#953 が塞ごうとしている穴そのものである。
    # 待ちは `kubectl wait`（下の certificate/edge-tls 待ちと同じ形。条件だけ jsonpath である）。reconcile が
    # 失敗すると helm-controller は Service を更新しないので、条件はタイムアウトまで満たされない＝非 0 で終わる。
    # 見るのは **宣言の status ではなく観測可能な結果（Service の port）** である —— HelmChart の status に
    # 何が載るかは k3s のバージョン依存であり、**バージョン依存を塞ぐ門をバージョン依存の識別子で書かない**。
    #
    # 既知の限界（隠さない）: **既存クラスタへの再実行では、新たに壊した宣言を捕まえられない**。前回の
    # reconcile が成功していれば Service は admin=50000 を保持し続けるためである。確実に効くのは
    # クラスタ作成直後。job レベル（helm-install-traefik の Complete）まで見れば塞げるが、job 名・ラベルが
    # k3s のバージョン依存であり、**バージョン依存を塞ぐ門をバージョン依存の識別子で書くこと**になる（IADR-0258 決定 3）。
    echo "    -> HelmChartConfig の反映を待つ: kube-system/traefik svc に admin=50000 が生えること (#953)"
    if ! kubectl -n kube-system wait --for=jsonpath='{.spec.ports[?(@.name=="admin")].port}'=50000 \
         svc/traefik --timeout=180s; then
      echo "ERROR: HelmChartConfig(traefik) の反映が確認できません。admin(50000) entrypoint が立っていません。" >&2
      echo "       **kubectl apply は成功していても reconcile は失敗し得ます**（#953）。以下を確認してください:" >&2
      echo "       - traefik chart の values スキーマは chart バージョンで変わります（deploy/local/edge/traefik-entrypoint.yaml の注記）" >&2
      echo "       - k3s の版: ${K3S_IMAGE:-Rancher Desktop の内蔵 k3s（版は Rancher Desktop の設定が決める）}。k3d 経路の既定は本スクリプトの K3S_IMAGE（IADR-0519）" >&2
      echo "--- kube-system/traefik svc の実ポート ---" >&2
      kubectl -n kube-system get svc traefik \
        -o jsonpath='{range .spec.ports[*]}{.name}={.port}{"\n"}{end}' >&2 || true
      echo "--- helm-controller の宣言と状態 ---" >&2
      kubectl -n kube-system get helmchartconfig,helmchart traefik >&2 || true
      echo "--- helm-install-traefik の直近ログ（reconcile の失敗理由）---" >&2
      kubectl -n kube-system logs job/helm-install-traefik --tail=40 >&2 || true
      exit 1
    fi

    # IADR-0227 (#780): エッジ host（*.localhost）を **pod からも** 解決できるようにする。
    # k3s の CoreDNS は Corefile 末尾に import /etc/coredns/custom/*.server を持ち、coredns Deployment は
    # coredns-custom ConfigMap を optional で既にマウントしている。置けば効き、消せば元に戻る（fail-safe）。
    # 非 .NET の OIDC クライアント（Grafana/ArgoCD/Vault/Headlamp/Wiki.js）は IADR-0086 の
    # metadata/issuer 分離が使えず、pod から issuer host を実際に引く必要がある。
    # ★ import 先の追加は Corefile 自体の変更ではないため reload プラグインが拾わない。rollout restart で確実に反映する。
    kubectl apply -f deploy/local/aliases/coredns-edge-hosts.yaml
    kubectl -n kube-system rollout restart deploy/coredns
    kubectl -n kube-system rollout status deploy/coredns --timeout=120s
    # argocd namespace が存在するときのみ、argocd 用の管理ツール Ingress を追加適用する
    # （ns 不在時に失敗させない fail-safe。ArgoCD は ARGOCD=1 の別 opt-in で作成される）。
    if kubectl get namespace argocd >/dev/null 2>&1; then
      kubectl apply -f deploy/local/edge/argocd-ingress.yaml
    fi
  fi

  # IADR-0206 (#779): エッジ TLS 終端。cert-manager を導入し、selfsigned→CA の 2 段で
  # ルート CA（Secret cert-manager/local-edge-root-ca）と葉証明書（Secret edge-tls）を作る。
  # --server-side は大 CRD の annotation 262144B 上限を避けるため（IADR-0088 が ArgoCD で是正した先例）。
  # 順序が要る: CRD が Established になる前に tls/ を apply すると "no matches for kind Certificate" で落ちる。
  # バージョンは固定する（IADR-0088: 浮動タグは再デプロイのたびに中身が変わり得る）。ESO と同じく
  # env で上書きできるが、既定は動作を実測した版を置く。上書き時も CRD の apiVersion 差に注意すること。
  CERT_MANAGER_VERSION="${CERT_MANAGER_VERSION:-v1.21.1}"
  echo "    -> cert-manager ${CERT_MANAGER_VERSION} (edge TLS, IADR-0206)"
  kubectl apply --server-side --force-conflicts -f "https://github.com/cert-manager/cert-manager/releases/download/${CERT_MANAGER_VERSION}/cert-manager.yaml"
  kubectl wait --for=condition=Established --timeout=120s \
    crd/certificates.cert-manager.io crd/clusterissuers.cert-manager.io
  kubectl -n cert-manager rollout status deploy/cert-manager --timeout=180s
  kubectl -n cert-manager rollout status deploy/cert-manager-webhook --timeout=180s
  # webhook が Ready でも数秒は TLS ハンドシェイクを拒むことがあるため、apply は数回試す（冪等）。
  for attempt in 1 2 3 4 5; do
    kubectl apply -k deploy/local/edge/tls && break
    echo "    WARN: tls overlay の apply に失敗（cert-manager webhook 待ち・試行 ${attempt}/5）" >&2
    sleep 5
  done
  # IADR-0220 (#841): argocd namespace の葉証明書は ns 存在時のみ当てる（argocd-ingress.yaml と同じ fail-safe。
  # tls/kustomization.yaml に含めると ns 不在の環境で tls overlay 全体が落ちる）。CRD は上で Established 済み。
  if kubectl get namespace argocd >/dev/null 2>&1; then
    kubectl apply -f deploy/local/edge/tls/argocd-certificate.yaml
  fi
  kubectl -n "$MSP_NS" wait --for=condition=Ready --timeout=120s certificate/edge-tls
  # IADR-0220 (#841): admin(50000) も TLS 終端になったため、そこに載る管理ツールの namespace にも葉証明書が要る
  # （spec.tls.secretName は同 namespace の Secret しか参照できない）。
  kubectl -n "$INFRA_NS" wait --for=condition=Ready --timeout=120s certificate/edge-tls
  if kubectl get namespace argocd >/dev/null 2>&1; then
    kubectl -n argocd wait --for=condition=Ready --timeout=120s certificate/edge-tls
  fi

  echo "    platform フロント: https://localhost/ (443・cert-manager 発行 edge-tls。80 は https へ恒久リダイレクト)"
  echo "    ルート CA の取り出し: kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\.crt}' | base64 -d"
  echo "    管理ツール(50000・https): https://grafana.localhost:50000 / headlamp.localhost / vault.localhost / qdrant.localhost"
  echo "    ホスト名解決・TLS・k3d 再作成手順は deploy/local/edge/README.md 参照。"

  # ADR-0021, #782: エッジを Istio Ingress Gateway へ移す（ISTIO=1 と併用したときだけ）。
  #
  # 🔴 **STRICT mTLS の前提である。** kube-system の Traefik はメッシュの外にあり、そこから
  #   mesh 内の 3 Service（frontend / bff / wiki-js。MinIO Console は IADR-0461 で撤去）へ平文で入っている。名前空間全体へ
  #   STRICT を掛けると Envoy がその平文を拒否し、**入口だけが 502 になる**（#1072 実測）。
  #   計画 ADR-0021 はこの境界問題を理由に「入口＝Istio Ingress Gateway・Traefik は無効化」と定めている。
  #
  # ここに置く理由: cert-manager と ClusterIssuer local-edge-ca（直上）が要る。
  # ISTIO が 1 でなければ実行されない＝従来どおり Traefik がエッジである。［2026-10-02 / #1713］ISTIO 未指定は、
  #   メッシュを宣言したクラスタの再実行では上の判定で 1 を引き継ぐ（IADR-0488）。ISTIO=0・初回・メッシュ未宣言なら実行されない。
  # #1691: **移行済みの再実行でも必ず呼ぶ**（上で飛ばすのは Traefik 側だけ）。istio-edge-up.sh は冪等で、
  #   [2/5] は Service が既に無いので即座に通り、Gateway・経路・CoreDNS・mTLS を当て直して現状を確かめる。
  if [ "${ISTIO:-}" = "1" ]; then
    echo "==> [opt-in] エッジを Istio Ingress Gateway へ移す (ADR-0021 / #782)"
    ISTIO_MTLS_MODE="${ISTIO_MTLS_MODE:-}" bash "$ROOT/scripts/istio-edge-up.sh"
    echo "    切り戻し（1 コマンド）: bash scripts/istio-edge-down.sh --live"
  fi
fi

# 🔴 ISTIO=1 だけで LOCALEDGE=1 が無いと、エッジは port-forward のままである。
#   その状態で ISTIO_MTLS_MODE=STRICT にすると mesh 内へ入る経路が無くなる（気付きにくい）。
if [ "${ISTIO:-}" = "1" ] && [ "${LOCALEDGE:-}" != "1" ]; then
  echo "WARN: ISTIO=1 ですが LOCALEDGE=1 がありません。エッジは Istio Ingress Gateway へ移りません。" >&2
  echo "      STRICT へ上げる前に LOCALEDGE=1 を併用してください（ADR-0021 / #782）。" >&2
fi

# IADR-0133 (#517): ABAC の属性辞書とポリシーを dev 既定値で投入する。ポリシーが 0 件だと
# AuthorizationService は deny-by-default で縮退し、**認証を通しても文書一覧・横断検索が常に空**になる
# （仕様どおりだが「壊れている」のと区別が付かない）。既定（env 未設定）は投入せず挙動不変＝バイト等価で、
# 本番 values には一切影響しない（投入先は経路B の稼働中サービスであり、chart ではない）。
# best-effort: 投入の失敗で up 全体を止めない（クラスタ自体は使えるため。再実行は冪等）。
# FR-13, UC-07, SC-04, ADR-0011, IADR-0327 (#1108): Wiki.js の初期セットアップ・同期 API キー・
# 本文 locale を入れる。**opt-in ではない。**
#
# 🔴 **既定の経路が「Running なのに使えない Wiki.js」を残すことが #1108 そのものである。**
# Wiki.js 2.x はセットアップが済むまで `/graphql` を載せず、その間 `server/setup.js` の catch-all が
# `/healthz` を含む全 URL に 200 を返す ——**probe は通り、Pod は Running のまま、同期だけが
# 全件エラーキューへ落ちる。画面にも SC-10 にも何も出ない。** opt-in にすると、この状態が
# 既定のままになる（ABACSEED / SEARCHSEED とはここが違う。あちらは**文書を作る副作用**が
# あるので既定オフだが、こちらは配備の初期化であって副作用ではない）。
#
# 冪等（セットアップ済みなら finalize を飛ばし、有効なキーが在れば再発行しない）。
# best-effort: 失敗しても up 全体は止めない。**fail-closed の門は `scripts/check-stack-ready.js` の
# G7** に置いてある（そちらが setup モード・空 apiKey・locale 欠落を落とす）。
#
# NFR-09, IADR-0342 (#1127): 同じ bootstrap の **段 8** が Keycloak OIDC ストラテジ
# （`authentication` テーブル＝これも manifest 化できない runtime 状態）を冪等に投入する。
# こちらは **opt-in（`WIKIJS_OIDC=1`・既定オフ）** —— endpoint がエッジ host を前提にするため、
# `LOCALEDGE` 抜きで既定 ON にすると「押せるが 502 になるボタン」を作ることになる。
# 既定では `local` ログインだけが残る（fail-safe）。**新しい入口は増やさない**（段として相乗りする）。
wikijs_oidc_note=""
[ "${WIKIJS_OIDC:-}" = "1" ] && wikijs_oidc_note=" ＋ [opt-in] OIDC ストラテジ投入（IADR-0342）"
echo "==> Wiki.js 初期セットアップ（冪等 / IADR-0327）${wikijs_oidc_note}"
bash "$ROOT/deploy/local/wikijs-setup/bootstrap.sh" \
  || echo "    WARN: Wiki.js の初期化に失敗（best-effort）。bash deploy/local/wikijs-setup/bootstrap.sh で再実行できる" >&2

if [ "${ABACSEED:-}" = "1" ]; then
  echo "==> [opt-in] ABAC 初期投入（属性辞書・ポリシー / IADR-0133）"
  node "$ROOT/scripts/seed-abac-policies.js" \
    || echo "    WARN: ABAC 初期投入に失敗（best-effort）。node scripts/seed-abac-policies.js --live で再実行できる" >&2
fi

# IADR-0284 (#992): 検索検証用の文書を投入する。**本文を持つ文書**でないと索引に一度も入らない
# （IngestionService の DocumentUpdatedConsumer は MarkdownUri が null の文書を早期 return で捨てる）。
# 文書が 1 件も無いスタックでは「検索が壊れている」と「該当が無い」が区別できず、#992 が塞ぎたい穴が残る。
# 既定（env 未設定）は投入せず挙動不変＝バイト等価で、本番 values には一切影響しない
# （投入先は経路B の稼働中サービスであり、chart ではない）。ABACSEED とまったく同じ形である。
#
# 🔴 **文書を作る（副作用）。使い捨てのスタック専用**であり、残しておきたいクラスタに対して立てないこと。
# best-effort: 投入の失敗で up 全体を止めない（クラスタ自体は使えるため。再実行は冪等）。
if [ "${SEARCHSEED:-}" = "1" ]; then
  echo "==> [opt-in] 検索検証用文書の初期投入（本文つき / IADR-0284）"
  node "$ROOT/scripts/seed-search-documents.js" \
    || echo "    WARN: 検索用文書の投入に失敗（best-effort）。node scripts/seed-search-documents.js --live で再実行できる" >&2
fi

# FR-06, FR-09, SC-05, SC-09 (#1359): タグ辞書へ**外部ユニットが送ってくる静的タグ**を初期投入する。
# 辞書に無いタグは POST /documents が 400 で弾く（#635）。一方で辞書へ行を入れる口は POST /tags だけで、
# **初期投入の仕組みが無かった**。結果として外部ユニットの文書は 100% 失敗していた（実測 既存 0 件）。
# 送り手は自分では登録できない —— POST /tags は AdminOnly で、送り手の service account は
# platform-operator しか持たない（IADR-0075 が platform-admin の付与を断っている）。
#
# 既定（env 未設定）は投入せず挙動不変。ABACSEED / SEARCHSEED とまったく同じ形である。
# **文書を作らない**（辞書へ値を足すだけ）ので、SEARCHSEED の「使い捨てスタック専用」の但し書きは付けない。
# best-effort: 投入の失敗で up 全体を止めない（再実行は冪等）。
if [ "${TAGSEED:-}" = "1" ]; then
  echo "==> [opt-in] タグ辞書の初期投入（外部ユニットの静的タグ / #1359）"
  node "$ROOT/scripts/seed-tag-dictionary.js"     || echo "    WARN: タグ辞書の投入に失敗（best-effort）。node scripts/seed-tag-dictionary.js --live で再実行できる" >&2
fi

# NFR-02, NFR-21, ADR-0076 決定 3・4, ADR-0079 決定 1, IADR-0378 (#1287): 合成監視（synthetic）の常駐プローブ。
# **60 秒間隔・LLM を呼ばない**（ADR-0079 決定 1 の確定値。`AllowLlmEgress` はここでも設定しない）。
#
# 🔴 **既定はオフである。既定 ON を採らなかった理由をここに置く**（ADR-0079 §フォローアップ 1 は
#   「常駐プローブを**本番構成へ**投入する」と課しており、本番像は `deploy/helm/microservices-platform`
#   である ——「既定の起動器」は本番像を指す語であって、ローカルの `k8s-local-up.sh` のことではない。
#   🔵 ［2026-09-26 / #1287・IADR-0469］本番像には既定オフの口 `syntheticMonitor.enabled` を用意した。
#   **この門と同じ 3 サービス・同じ主体名・同じプローブ**であることを scripts/helm-synthetic-monitor.test.js が
#   描画結果で突き合わせる（ここの `for d in ...` の集合を変えたら、チャートの _synthetic-monitor.tpl も変えること）。
#   チャート側で有効にしたクラスタではこの門を使わない（同名の資源になる））。
#   加えてローカルで既定 ON にすると、**捨てるつもりの dev クラスタが常に `/analysis/ask` 系を叩き続ける**（検索までは走るので Qdrant / Postgres への負荷と利用イベントが
#   常時立つ）。**利用者が既定 ON を望むなら、下の 1 行の既定値を `1` にするだけで足りる。**
SYNTHETIC_DEFAULT="0" # ← 既定 ON にするならここを "1" にする（#1287。他は 1 行も変えなくてよい）
if [ "${SYNTHETIC:-$SYNTHETIC_DEFAULT}" = "1" ]; then
  echo "==> [opt-in] 合成監視（synthetic・60 秒・LLM を呼ばない / ADR-0079 決定 1 / #1287）"
  # 🔴 **ADR-0076 決定 4「標識と除外は同時に入れる。除外できない構成では配備しない」を満たす順で行う。**
  #   ローカルはイメージを `scripts/k8s-local-images.sh` が**この作業ツリーのソースから**作る（[2/7]）ため、
  #   「除外規則が入ったイメージであること」は構造的に満たされる —— ADR-0079 §フォローアップ 1 が
  #   🔴 と付けた「イメージの再ビルド」の依存は、**ローカルに限っては起動器の内側で解決している。**
  #   稼働クラスタ（本番像）はレジストリのタグを引くので、この根拠は移せない。

  # (a) 標識の許可集合を除外する 3 サービスへ与える。**空だと fail-closed で除外が 1 件も効かない**
  #     （SyntheticTraffic.IsSyntheticPrincipal は Subjects が空なら常に false を返す）。
  #     🔴 helm の values ではなく live の Deployment を触るのは、`services.<name>.extraEnv` が**リストで
  #     あり `--set` で足すと既存要素ごと置き換わる**ためである（BFF の Services__* が消える）。
  #     結果として helm の再実行はこの env を戻すが、**本ゲートは毎回 up の後段で当て直す**ので収束する。
  #     （ARGOCD=1 で ArgoCD に同期させている場合は、ArgoCD が chart の姿へ戻す。README に明記。）
  for d in bff dashboard aianalysis; do
    kubectl -n "$MSP_NS" set env "deploy/$d-service" SyntheticMonitoring__Subjects__0=synthetic-monitor
  done

  # (b) プローブの client secret。ESO=1 のときは Vault→ExternalSecret 供給へ委譲する（二重所有回避）。
  #     dev 既定は realm JSON の置き値（他の OIDC client secret と同じ扱い・env で上書き可）。
  if [ "${ESO:-}" != "1" ]; then
    apply_secret "$MSP_NS" synthetic-monitor-oidc \
      "client-secret=${SYNTHETIC_MONITOR_CLIENT_SECRET:-synthetic-monitor-dev-secret-change-me}"
  fi

  # (c) **除外が効く Pod が揃うまでプローブを配備しない**（ADR-0076 決定 4 の fail-closed）。
  #     (a) の env は Pod を作り直させるので、ここで揃うのは「新しい env を持つ Pod」である。
  #     🔴 **警告を出して続行してはならない** —— それは「除外できない構成へ合成を配備した」ことと同じであり、
  #     利用実績・費用・検索傾向が静かに汚れる。LOCALEDGE の HelmChartConfig の門（#953）と同じ形で落とす。
  synthetic_exclusion_ready=1
  for d in bff dashboard aianalysis; do
    kubectl -n "$MSP_NS" rollout status "deploy/$d-service" \
      --timeout="${SYNTHETIC_ROLLOUT_TIMEOUT:-180s}" || synthetic_exclusion_ready=0
  done
  if [ "$synthetic_exclusion_ready" != "1" ]; then
    echo "ERROR: 除外の 3 サービス（bff / dashboard / aianalysis）が新しい env で揃いません。" >&2
    echo "       **合成監視は配備しません**（ADR-0076 決定 4「除外できない構成では配備しない」）。" >&2
    echo "       kubectl -n $MSP_NS get pods / describe deploy で原因を見てから再実行してください。" >&2
    exit 1
  fi

  # (d) プローブ本体。README の手順 4 と同じ overlay を当てる（手で当てるのと同じ 1 コマンド）。
  kubectl apply -k deploy/local/synthetic-monitor
  kubectl -n "$MSP_NS" rollout status deploy/synthetic-monitor \
    --timeout="${SYNTHETIC_ROLLOUT_TIMEOUT:-180s}"
  echo "    プローブのログ: kubectl -n $MSP_NS logs deploy/synthetic-monitor --tail=20"
  echo "    停止（次の間隔を待たない）: kubectl -n $MSP_NS scale deploy/synthetic-monitor --replicas=0"
  echo "    🔴 LLM を呼ぶ 60 分側（ADR-0079 決定 2・課金の承認が要る）は**別の配備単位**であり、ここには無い。"
fi

# NFR-21, IADR-0471 (#1699): バックアップの CronJob は**前提が揃うまで suspend で置き、揃った再実行で有効へ戻す**。
# 前提は「CronJob のイメージがランタイムに在る」と「age の受取人が CronJob の中の検査を通る」の 2 つ（判定は lib に閉じる）。
# 🔴 欠けたまま有効にしておくと、毎日の回が ImagePullBackOff / 失敗の Pod を残し、「バックアップが動いている」と誤読させる。
# 🔴 suspend は毎回**明示的に**書く（true も false も）。apply -k はマニフェストに無い suspend を触らないので、
#    書かなければ一度止めた CronJob が前提が揃っても止まったままになる。
# 永続化を外した起動（PERSIST=0）は CronJob を置かないので何もしない。
if [ "${PERSIST:-1}" != "0" ]; then
  # 🔴 読み込みは $ROOT 基準（上で cd "$ROOT" 済み。$(dirname "$0") は起動したときの相対パスのままなので、
  #    scripts/ の中から `bash k8s-local-up.sh` と起動すると見つからない。PR #1700 の監査 🔴1）。
  . "$ROOT/scripts/lib/backup-cronjob-gate.sh" || exit 3   # 判定器が読めなければ守れない —— 黙って続けず止める
  backup_recipients_tmp="$(mktemp)"
  kubectl -n "$INFRA_NS" get configmap platform-backup-age-recipients \
    -o 'jsonpath={.data.recipients\.txt}' > "$backup_recipients_tmp" 2>/dev/null || : > "$backup_recipients_tmp"
  backup_missing_image=0
  backup_missing_recipients=0
  backup_patch_failed=0
  for backup_cj in platform-backup-postgres platform-backup-vault; do
    kubectl -n "$INFRA_NS" get cronjob "$backup_cj" >/dev/null 2>&1 || continue
    backup_ref="$(kubectl -n "$INFRA_NS" get cronjob "$backup_cj" \
      -o 'jsonpath={.spec.jobTemplate.spec.template.spec.containers[0].image}' 2>/dev/null || true)"
    backup_image=0
    backup_image_present "$RUNTIME" "$CLUSTER" "$backup_ref" && backup_image=1
    mapfile -t backup_decision < <(backup_cronjob_suspend "$backup_image" "$backup_recipients_tmp")
    # 🔴 patch の失敗（API・RBAC・競合）で長い起動の最後を落とさない。落とさない代わりに、止められなかったことを名指しする。
    if ! kubectl -n "$INFRA_NS" patch cronjob "$backup_cj" --type=merge -p "{\"spec\":{\"suspend\":${backup_decision[0]}}}"; then
      backup_patch_failed=1
      echo "WARN: バックアップの CronJob ${backup_cj} の suspend を書けませんでした（前提: ${backup_decision[*]:1}）。kubectl -n $INFRA_NS get cronjob で状態を確かめてください。" >&2
      continue
    fi
    if [ "${backup_decision[0]}" = "true" ]; then
      for backup_reason in "${backup_decision[@]:1}"; do
        case "$backup_reason" in
          image) backup_missing_image=1 ;;
          recipients) backup_missing_recipients=1 ;;
        esac
      done
      backup_missing="${backup_decision[*]:1}"
      echo "WARN: バックアップの CronJob ${backup_cj} を停止（suspend）で置きました。欠けている前提: ${backup_missing// /, }" >&2
    else
      echo "    バックアップの CronJob ${backup_cj} を有効にしました（イメージと age の受取人が揃っている）"
    fi
  done
  rm -f "$backup_recipients_tmp"
  # 直し方は、実際に欠けていた前提についてだけ 1 回案内する（欠けていない前提を「無い」と言わない。監査 🟡2）。
  if [ "$backup_missing_image" = "1" ]; then
    echo "WARN:   image … バックアップのイメージがありません（[2/7] のビルドの WARN を参照）。" >&2
  fi
  if [ "$backup_missing_recipients" = "1" ]; then
    echo "WARN:   recipients … ConfigMap platform-backup-age-recipients に age の公開鍵がありません（占位・不在・不正な行）。" >&2
  fi
  if [ "$backup_missing_image" = "1" ] || [ "$backup_missing_recipients" = "1" ] || [ "$backup_patch_failed" = "1" ]; then
    echo "WARN:   前提を揃えて再実行すると有効に戻ります（docs/operations/platform-infra-backup-runbook.md）。" >&2
  fi
fi

echo ""
echo "done. 状態確認:"
echo "  kubectl get pods -A"
echo "  kubectl -n $MSP_NS port-forward svc/bff-service 5080:8080   # http://localhost:5080/health"
# ADR-0045 決定 9 (#1144): 捕捉用 MTA の閲覧 UI。**エッジへは出していない**（UI は認証を持たず、中身は
# リセットリンク＝認証資格である）。運用者が明示的に開く。
echo "  kubectl -n $INFRA_NS port-forward svc/mailpit 8025:8025     # http://localhost:8025 （開発環境の捕捉用 MTA）"
echo "AST 連結は AST chart(AST#122) 適用後に scripts/... で行う。"

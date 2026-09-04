#!/usr/bin/env bash
# IADR-0066: MSP+AST 連結ローカル k8s(k3d) dev 環境の起動オーケストレーション。
# 冪等（再実行可）。fail-safe: 機密は未設定なら dev 既定/空（no-op）で作成する。
#
#   bash scripts/k8s-local-up.sh [cluster-name]
#
# 前提ツール: docker / k3d / kubectl / helm（scripts/README や docs/operations 参照）。
# 機密の上書きは環境変数で: PG_PASSWORD / RABBITMQ_PASSWORD / KEYCLOAK_ADMIN_PASSWORD /
#   MINIO_ACCESS_KEY / MINIO_SECRET_KEY / WIKIJS_DB_PASSWORD / WIKIJS_SYNC_APIKEY / ANTHROPIC_API_KEY /
#   RABBITMQ_USER（#1022。helm の global.messaging.user と揃えること）/
#   WIKIJS_OIDC_CLIENT_SECRET（#1127。WIKIJS_OIDC=1 のときだけ使う。realm の wiki-js client と揃えること）/
#   KEYCLOAK_ADMIN_USER（IADR-0369。既定 admin。Keycloak と realm 後追い Job が同じ Secret から読む）
# 永続化（Keycloak/Postgres/Qdrant ＋ OBSERVABILITY=1 の可観測性 4 種の PVC）は **既定オン**（IADR-0369 / #1088）。
#   使い捨てスタックでだけ PERSIST=0 で外す。
set -euo pipefail

CLUSTER="${1:-msp-ast-dev}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
INFRA_NS="platform-infra"
MSP_NS="microservices-platform"

apply_secret() { # ns name key=val [key=val...]
  local ns="$1"; local name="$2"; shift 2
  local args=(); for kv in "$@"; do args+=(--from-literal="$kv"); done
  kubectl create secret generic "$name" -n "$ns" "${args[@]}" \
    --dry-run=client -o yaml | kubectl apply -f -
}

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
  # NFR, Issue #783 (#442 子 5): K3S_IMAGE で k3s のイメージを固定できるようにする（opt-in・既定は不変）。
  # **理由は「バージョンを揃えたいから」ではない。揃っていないことが静かに素通りするからである。**
  # k3d の既定 k3s（5.7.4 では v1.30.4）が同梱する traefik chart は 25.0.3 で、そこでは `expose` が bool
  # であり、deploy/local/edge/traefik-entrypoint.yaml の map 形式（chart 26 以降）は型不一致で reconcile に
  # 失敗する。ところが `kubectl apply` は成功するため **admin(50000) が立たないまま本スクリプトは EXIT=0 で
  # 返る**（実測: GitHub ホストランナー / run 32554867883）。構造そのもの（reconcile 失敗が伝わらない）は
  # #953 で別途扱う。ここは「pin が外れたことに気づける」ための口である。
  # 未設定なら引数を 1 バイトも足さない（既定はバイト等価・fail-safe）。
  if [ -n "${K3S_IMAGE:-}" ]; then
    CREATE_ARGS+=(--image "$K3S_IMAGE")
  fi
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

echo "==> [2/7] build & import images"
bash "$ROOT/scripts/k8s-local-images.sh" "$CLUSTER"

echo "==> [3/7] infra namespace, secrets & realm ConfigMap (dev 既定; env で上書き可)"
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

# Keycloak realm import 用 ConfigMap（実 realm ファイル＝単一情報源）。
# AST realm（submodule）が存在すれば同一 Keycloak へ併せて import する（MSP+AST 連結）。
realm_args=(--from-file=microservices-platform-realm.json=deploy/keycloak/microservices-platform-realm.json)
ast_realm="src/ai-stock-trading/infra/keycloak/realm-export.json"
if [ -f "$ast_realm" ]; then
  realm_args+=(--from-file=ai-stock-trading-realm.json="$ast_realm")
  echo "    + AST realm を同梱 import します"
fi
kubectl create configmap keycloak-realms -n "$INFRA_NS" "${realm_args[@]}" \
  --dry-run=client -o yaml | kubectl apply -f -

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
echo "    waiting for infra to become Ready..."
# IADR-0100 (#354 障害2): アプリ Pod（[6/7] MSP・後続 AST）が起動する前にノードの inotify 上限を引き上げておく
# （inotify 枯渇による FileSystemWatcher クラッシュ＝広範 CrashLoopBackOff を防ぐ）。best-effort: busybox pull 等の
# 一時失敗で up 全体を止めない（pipefail 下でも `|| echo WARN` で握る。DaemonSet 自体は infra kustomize で適用済み）。
kubectl -n "$INFRA_NS" rollout status ds/inotify-sysctl --timeout=120s \
  || echo "    WARN: inotify-sysctl DaemonSet が未 Ready（best-effort・後追いで適用される）" >&2
kubectl -n "$INFRA_NS" rollout status deploy/postgres --timeout=180s
kubectl -n "$INFRA_NS" rollout status deploy/rabbitmq --timeout=180s
kubectl -n "$INFRA_NS" rollout status deploy/redis --timeout=120s
kubectl -n "$INFRA_NS" rollout status deploy/keycloak --timeout=300s
kubectl -n "$INFRA_NS" rollout status deploy/qdrant --timeout=120s
kubectl -n "$INFRA_NS" rollout status deploy/otel-collector --timeout=120s
# SC-15, FR-22, ADR-0045 決定 9 (#1144): 捕捉用 MTA。**opt-in ゲートを持たない**（決定 9 は無条件）。
# realm の dev 既定 smtpServer がここを指すので、Keycloak より後に立つと最初の申請が送出に失敗する。
kubectl -n "$INFRA_NS" rollout status deploy/mailpit --timeout=120s

echo "==> [5/7] MSP namespace & app secrets (dev 既定; fail-safe 空 = no-op)"
kubectl create namespace "$MSP_NS" --dry-run=client -o yaml | kubectl apply -f -
# IADR-0093 (#353): MinIO Console の Keycloak OIDC client secret（平文コミットしない・dev 既定 or env 上書き）。
# minio.yaml は minio.oidc.enabled 時に optional 参照で注入する（未作成でも Pod 起動＝root ログインへフォールバック）。
# IADR-0098 (#310) PR-3: minio-oidc は ESO=1 のとき Vault→ExternalSecret 供給へ委譲し手動 apply をスキップ（二重所有回避）。
# 既定（ESO 未設定）は従来どおり手動 apply（バイト等価）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" minio-oidc "client-secret=${MINIO_OIDC_CLIENT_SECRET:-minio-dev-secret-change-me}"
fi
# NFR, SC-13, ADR-0026/ADR-0032, IADR-0251/IADR-0273/IADR-0316 (#1107): BFF セッション（Token Handler）の
# client secret。helm の deployment.yaml が **非 optional** な secretKeyRef（services.bff.session.existingSecret）で
# 参照するため、これが無いと bff-service Pod は起動できない（注入漏れが「空 secret で起動して login だけ 500」へ
# 倒れない）。dev 既定は realm import の置き場と同値 —— **ズレると Keycloak の PAR 端点が 401 を返し、
# `GET /bff/auth/login` が 500 になる**。ESO=1 のときは Vault→ExternalSecret 供給へ委譲する（二重所有回避）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" bff-oidc "client-secret=${BFF_OIDC_CLIENT_SECRET:-bff-dev-secret-change-me}"
fi
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
# IADR-0097 (#310) PR-2: minio-credentials/wikijs-db/wikijs-sync は ESO=1 のとき Vault→ExternalSecret 供給へ委譲し
# 手動 apply をスキップする（二重所有回避）。既定（ESO 未設定）は従来どおり手動 apply（バイト等価）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" minio-credentials \
    "accessKey=${MINIO_ACCESS_KEY:-minioadmin}" "secretKey=${MINIO_SECRET_KEY:-minioadmin}"
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
    "anthropic-api-key=${ANTHROPIC_API_KEY:-}" "openai-api-key=${OPENAI_API_KEY:-}"
fi

# ADR-0005, #782: サービスメッシュ（Istio）。opt-in（既定オフ・fail-safe）。
#
# 🔴 **[6/7] より前に置く。** アプリチャートは PeerAuthentication / DestinationRule を
#   レンダリングするので、CRD が無いまま helm upgrade すると apply がその時点で失敗する。
#
# 🔴 **既定は PERMISSIVE で入る。** STRICT へは ISTIO_MTLS_MODE=STRICT で明示的に移す。
#   いきなり STRICT にすると、サイドカーの入っていない platform-infra（postgres / keycloak /
#   rabbitmq / qdrant / redis …）との通信と、注入前の Pod からの通信が**同時に**壊れる。
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
  ISTIO_MESH_ARGS="--set mesh.enabled=true --set mesh.mtlsMode=${ISTIO_MTLS_MODE:-PERMISSIVE} --set namespace.istioInjection=true --set mesh.backchannelLogout.fromOutsideMesh=true"
  # 🔴 経路B は values-local.yaml が namespace.create=false のため、**Helm は Namespace を作らない**
  #   ＝ istioInjection=true にしても注入ラベルが誰にも適用されない。ここで明示的に貼る。
  #   （本番像は namespace.create=true なのでチャートが貼る。同じ結果を 2 経路で担保する。）
  kubectl label namespace "$MSP_NS" istio-injection=enabled --overwrite
  echo "    mTLS モード: ${ISTIO_MTLS_MODE:-PERMISSIVE}（STRICT へ移すには ISTIO_MTLS_MODE=STRICT で再実行）"
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
# shellcheck disable=SC2086  # ISTIO_MESH_ARGS / LOCALEMBED_ARGS は空か複数フラグ。意図的に分割する。
helm upgrade --install msp deploy/helm/microservices-platform \
  -n "$MSP_NS" -f deploy/local/values-local.yaml $ISTIO_MESH_ARGS $LOCALEMBED_ARGS

# #782: サイドカーは**既存 Pod には後から入らない**。注入ラベルを付けたあとに作り直す。
# helm upgrade だけでは Pod テンプレートが変わらないサービスが残るため、明示的に restart する。
if [ "${ISTIO:-}" = "1" ]; then
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
  if kubectl get crd clustersecretstores.external-secrets.io >/dev/null 2>&1; then
    kubectl apply -k deploy/local/vault
  else
    echo "    WARN: external-secrets.io CRD 未導入のため ClusterSecretStore/Vault は skip。" >&2
    echo "          先に ESO を導入する（deploy/local/vault/README.md）。Vault dev のみ適用:" >&2
    kubectl apply -f deploy/local/vault/vault-dev.yaml
  fi
fi

# IADR-0096 (#310): Vault＋ESO で secret を Pod へ自動供給する（本番同等・k8s auth）。opt-in（既定オフ・fail-safe）。
# 既定（ESO 未設定）では本ブロックは実行されず、手動 apply_secret のままバイト等価。VAULT=1 併用が前提
# （dev Vault が起動済みであること）。PR-1 は llm-provider-credentials 1本で end-to-end 疎通する。
if [ "${ESO:-}" = "1" ]; then
  echo "==> [opt-in] External Secrets Operator + Vault k8s auth (secret 自動供給・#310)"
  # 早期ガード: ESO=1 は dev Vault（VAULT=1）を前提とする。bootstrap は `kubectl exec deploy/vault` を使うため、
  # Vault Deployment が無いと分かりにくいエラーで中断する。明示的に案内して止める（fail-fast）。
  if ! kubectl -n "$INFRA_NS" get deploy vault >/dev/null 2>&1; then
    echo "ERROR: ESO=1 は VAULT=1 と併用してください（dev Vault が必要）。例: VAULT=1 ESO=1 bash scripts/k8s-local-up.sh" >&2
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
  # Vault k8s auth の enable/config＋policy＋role `eso`＋seed（runtime・kubectl exec 経由・平文非コミット・再実行可）。
  bash deploy/local/vault/eso/bootstrap.sh
  # 上で k8s auth backend/role を設定した「後に」store を kubernetes 認証へ上書きする（同名 vault-backend）。
  # 既定（VAULT=1 単独）は token 認証の store（deploy/local/vault/clustersecretstore.yaml）のままで既存フロー不変。
  kubectl apply -f deploy/local/vault/eso/clustersecretstore-k8s.yaml
  # ExternalSecret で secret を Vault→Secret 供給する（PR-1: llm、PR-2: minio-credentials/wikijs-db/wikijs-sync、
  # PR-3: OIDC client secret 群）。
  kubectl apply -f deploy/local/vault/eso/externalsecret-llm.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-minio.yaml
  # NFR, ADR-0002 (#1012): サービス DB のパスワード。手動 apply は上の `ESO != 1` ブロックで
  # スキップされるので、**これが唯一の供給元**である（欠けると DB を持つ全サービスが起動しない）。
  kubectl apply -f deploy/local/vault/eso/externalsecret-postgres-app.yaml
  # NFR, ADR-0027 (#1022): ブローカのパスワード（app 側）。手動 apply は上の `ESO != 1` ブロックで
  # スキップされるので、**これが唯一の供給元**である（欠けると RabbitMQ を使う 7 サービスが起動しない）。
  kubectl apply -f deploy/local/vault/eso/externalsecret-rabbitmq-app.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-wikijs-db.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-wikijs-sync.yaml
  # IADR-0098 (#310) PR-3: OIDC client secret 群。minio-oidc は MSP ns、grafana/vault/headlamp-oidc は platform-infra ns。
  # ExternalSecret は namespaced だが ClusterSecretStore は cluster-scoped のため両 ns から同名 store を参照できる。
  # 元の手動 apply のゲート意味論に合わせて供給する（機能オフ時に未使用 Secret を残さない＝元の条件付き apply と対称）:
  #  - minio-oidc: 常時（step 5 相当・元も無条件）
  #  - vault-oidc: VAULT 前提（ESO=1 は VAULT 併用ガード下＝ここでは常に真）で常時
  #  - grafana-oidc / headlamp-oidc: 各機能（OBSERVABILITY / HEADLAMP）が有効なときだけ供給
  kubectl apply -f deploy/local/vault/eso/externalsecret-minio-oidc.yaml
  # #1107: BFF セッションの client secret。手動 apply は上の `ESO != 1` ブロックでスキップされるので、
  # **これが唯一の供給元**である（欠けると bff-service Pod が起動しない）。常時供給。
  kubectl apply -f deploy/local/vault/eso/externalsecret-bff-oidc.yaml
  # #1101: SC-17 の Keycloak Admin REST 反映に使う client secret。手動 apply は上の `ESO != 1`
  # ブロックでスキップされるので、**これが唯一の供給元**である（欠けると authorization-service Pod が
  # 起動しない）。常時供給。
  kubectl apply -f deploy/local/vault/eso/externalsecret-identity-admin-oidc.yaml
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
  # SC-15, FR-22, ADR-0026/ADR-0045 決定 6, IADR-0261 決定 2 / IADR-0332 (#1102): SMTP リレーの資格情報。
  # **手動 apply の対になる `ESO != 1` ブロックを持たない**（postgres/rabbitmq/keycloak-admin と違い
  # step [3/7] の bootstrap 対象でもない）ので、**これが唯一の供給元**である。常時供給。
  # 🔴 **Pod は 1 つもこの Secret を env で読まない。** 読むのは runbook の `kcadm` 手順（人間）であり、
  # 反映先は realm の実行時状態である（`realm.json` へは書かない＝秘匿値を非コミットに保つため）。
  # したがって下の rollout 対象にも入れない —— **作るところまでが本行の責務**である。
  # 既定では `from`/`user`/`password` が空（bootstrap.sh の fail-safe）。**空のまま kcadm を打たない**
  # ことは runbook 側の前提であり、ここでは空の Secret を作るだけで実害は無い（秘匿値は 1 つも増えない）。
  kubectl apply -f deploy/local/vault/eso/externalsecret-keycloak-smtp.yaml
  # 確認コマンドは実際に apply した ExternalSecret のみ列挙する（無効ゲートの secret を挙げて NotFound で
  # 誤解させない）。MSP ns は常時 9 本（#1022 で rabbitmq-app、#1107 で bff-oidc、#1101 で identity-admin-oidc を追加し 6 → 7 → 8 → 9 へ数え直した）＋有効ゲートの wikijs-oidc（#1127）。infra ns は基盤 3 本＋vault-oidc/keycloak-smtp 常時（#1102 で keycloak-smtp を追加し 4 → 5 へ数え直した）＋有効ゲートの grafana/headlamp-oidc。
  msp_es="llm-provider-credentials minio-credentials postgres-app rabbitmq-app wikijs-db wikijs-sync minio-oidc bff-oidc identity-admin-oidc"
  [ "${WIKIJS_OIDC:-}" = "1" ] && msp_es="$msp_es wikijs-oidc"
  infra_es="postgres rabbitmq keycloak-admin vault-oidc keycloak-smtp"
  [ "${OBSERVABILITY:-}" = "1" ] && infra_es="$infra_es grafana-oidc"
  [ "${HEADLAMP:-}" = "1" ] && infra_es="$infra_es headlamp-oidc"
  echo "    ESO: llm/minio-credentials/postgres-app/rabbitmq-app/wikijs-db/wikijs-sync/minio-oidc（MSP ns 常時）＋ 基盤 postgres/rabbitmq/keycloak-admin"
  echo "         （infra ns・Merge・手動 apply 保持）＋ vault-oidc/keycloak-smtp、および有効ゲートの grafana/headlamp-oidc（infra ns）と wikijs-oidc（MSP ns）を"
  echo "         Vault(secret/msp/...)→ExternalSecret 供給（基盤以外の手動 apply はスキップ済み）。"
  echo "         確認(MSP):   kubectl -n $MSP_NS get externalsecret,secret $msp_es"
  echo "         確認(infra): kubectl -n $INFRA_NS get externalsecret,secret $infra_es"

  # IADR-0103 (#354): env の `secretKeyRef` は **Pod 起動時に一度だけ解決され、その後の Secret 更新は
  # 既存 Pod の env へ反映されない**。ESO が Secret を作る/上書きするのは Pod 起動より後になるため、対象 Pod は
  # 「空」または「旧値」の env を保持し続ける。実害として MinIO=`unauthorized_client / Invalid client credentials`
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
  msp_sync="llm-provider-credentials minio-credentials minio-oidc wikijs-db wikijs-sync"
  # #1127: wikijs-oidc を待つ理由は **rollout ではない**（env で読む Pod が無い）。`up` の後段で走る
  # deploy/local/wikijs-setup/bootstrap.sh の段 8 がこの Secret を読むためである。同期前だと段 8 は
  # 「client secret を取得できない」で何もせずに終わり、**OIDC ログインが入らないまま up は緑で終わる。**
  [ "${WIKIJS_OIDC:-}" = "1" ] && msp_sync="$msp_sync wikijs-oidc"
  # shellcheck disable=SC2086
  eso_wait "$MSP_NS" $msp_sync
  # #1102: keycloak-smtp は **rollout のために待つのではない**（env で読む Pod が無い。上の apply の注記参照）。
  # 待つのは、`up` の直後に運用者が案内文どおり
  # `kubectl -n platform-infra get externalsecret,secret keycloak-smtp` を打ったとき、また runbook
  # （docs/operations/keycloak-smtp-relay-setup-runbook.md）の kcadm 手順へ進むときに、
  # **Secret が「まだ作られていない」状態で NotFound を返さない**ようにするためである。
  # 他と同じく best-effort（warn を出して継続。実値が空でも同期自体は成立する）。
  infra_sync="keycloak-smtp"
  [ "${OBSERVABILITY:-}" = "1" ] && infra_sync="$infra_sync grafana-oidc"
  [ "${HEADLAMP:-}" = "1" ] && infra_sync="$infra_sync headlamp-oidc"
  # shellcheck disable=SC2086
  [ -n "$infra_sync" ] && eso_wait "$INFRA_NS" $infra_sync

  # 2) 供給後の値で env を作り直す。対象＝**ESO 管理 Secret を env(secretKeyRef) で参照する Deployment**。
  #      minio             : minio-credentials（root）/ minio-oidc（client secret）
  #      llmgateway-service: llm-provider-credentials（Llm__ApiKey）
  #      wiki-service      : wikijs-sync（WikiJs__ApiKey）
  #      wiki-js           : wikijs-db（DB_PASS）
  #    対象外: postgres / rabbitmq / keycloak-admin は creationPolicy: Merge で seed（step 3）と**同一値**のため
  #    env は変化せず、再起動は DB/broker を無用に落とすだけ（IADR-0099）。vault-oidc は env 参照が無く
  #    bootstrap が CLI で読むため rollout 不要。
  for d in minio llmgateway-service wiki-service wiki-js; do
    kubectl -n "$MSP_NS" rollout restart "deploy/$d" >/dev/null 2>&1 \
      && echo "      restarted $MSP_NS/$d" || echo "      skip $MSP_NS/$d（未デプロイ）"
  done
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
  # 前提とし、ArgoCD 自身が同 CRD を自己管理下に置いた後の再実行は想定しない）。URL/バージョンは不変。
  kubectl apply --server-side --force-conflicts -n argocd -f https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml
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
  kubectl -n argocd patch secret argocd-secret --type merge \
    -p "{\"stringData\":{\"oidc.keycloak.clientSecret\":\"${ARGOCD_OIDC_CLIENT_SECRET:-argocd-dev-secret-change-me}\"}}"
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
    echo "       - k3s のバージョンは K3S_IMAGE で固定できます（例: K3S_IMAGE=rancher/k3s:v1.35.4-k3s1）" >&2
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
  # 非 .NET の OIDC クライアント（Grafana/ArgoCD/Vault/MinIO/Headlamp/Wiki.js）は IADR-0086 の
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
  #   mesh 内の 4 Service（frontend / bff / minio / wiki-js）へ平文で入っている。名前空間全体へ
  #   STRICT を掛けると Envoy がその平文を拒否し、**入口だけが 502 になる**（#1072 実測）。
  #   計画 ADR-0021 はこの境界問題を理由に「入口＝Istio Ingress Gateway・Traefik は無効化」と定めている。
  #
  # ここに置く理由: cert-manager と ClusterIssuer local-edge-ca（直上）が要る。
  # ISTIO 未設定なら実行されない＝既定はバイト等価（従来どおり Traefik がエッジである）。
  if [ "${ISTIO:-}" = "1" ]; then
    echo "==> [opt-in] エッジを Istio Ingress Gateway へ移す (ADR-0021 / #782)"
    ISTIO_MTLS_MODE="${ISTIO_MTLS_MODE:-}" bash "$ROOT/scripts/istio-edge-up.sh"
    echo "    切り戻し（1 コマンド）: bash scripts/istio-edge-down.sh"
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
    || echo "    WARN: ABAC 初期投入に失敗（best-effort）。node scripts/seed-abac-policies.js で再実行できる" >&2
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
    || echo "    WARN: 検索用文書の投入に失敗（best-effort）。node scripts/seed-search-documents.js で再実行できる" >&2
fi

echo ""
echo "done. 状態確認:"
echo "  kubectl get pods -A"
echo "  kubectl -n $MSP_NS port-forward svc/bff-service 5080:8080   # http://localhost:5080/health"
# ADR-0045 決定 9 (#1144): 捕捉用 MTA の閲覧 UI。**エッジへは出していない**（UI は認証を持たず、中身は
# リセットリンク＝認証資格である）。運用者が明示的に開く。
echo "  kubectl -n $INFRA_NS port-forward svc/mailpit 8025:8025     # http://localhost:8025 （開発環境の捕捉用 MTA）"
# IADR-0093 (#353): MinIO Console SSO は集約 URL 前提（LOCALEDGE=1）＋ポリシー適用が必要。
echo "MinIO Console SSO(#353): https://minio.localhost:50000 (要 LOCALEDGE=1)。ポリシー適用と port-forward 単独時の"
echo "  制約（OIDC 未成立→root フォールバック）は deploy/local/minio-oidc/README.md を参照。"
echo "AST 連結は AST chart(AST#122) 適用後に scripts/... で行う。"

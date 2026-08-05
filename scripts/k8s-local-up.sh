#!/usr/bin/env bash
# IADR-0066: MSP+AST 連結ローカル k8s(k3d) dev 環境の起動オーケストレーション。
# 冪等（再実行可）。fail-safe: 機密は未設定なら dev 既定/空（no-op）で作成する。
#
#   bash scripts/k8s-local-up.sh [cluster-name]
#
# 前提ツール: docker / k3d / kubectl / helm（scripts/README や docs/operations 参照）。
# 機密の上書きは環境変数で: PG_PASSWORD / RABBITMQ_PASSWORD / KEYCLOAK_ADMIN_PASSWORD /
#   MINIO_ACCESS_KEY / MINIO_SECRET_KEY / WIKIJS_DB_PASSWORD / WIKIJS_SYNC_APIKEY / ANTHROPIC_API_KEY
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
apply_secret "$INFRA_NS" rabbitmq        "password=${RABBITMQ_PASSWORD:-guest}"
apply_secret "$INFRA_NS" keycloak-admin  "password=${KEYCLOAK_ADMIN_PASSWORD:-admin}"

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

echo "==> [4/7] apply in-cluster infra"
# IADR-0082 (#324): PERSIST=1 で永続化オーバーレイ（Keycloak/Postgres を local-path PVC 化）を選ぶ。
# 既定（未設定）は base（emptyDir）＝従来挙動不変・fail-safe（provisioner 不在クラスタで Pod Pending 化させない）。
INFRA_KUSTOMIZE="deploy/local/infra"
if [ "${PERSIST:-}" = "1" ]; then
  INFRA_KUSTOMIZE="deploy/local/infra-persistence"
  echo "    [PERSIST=1] Keycloak(realm+runtime state)/Postgres を PVC 永続化（local-path）"
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

echo "==> [5/7] MSP namespace & app secrets (dev 既定; fail-safe 空 = no-op)"
kubectl create namespace "$MSP_NS" --dry-run=client -o yaml | kubectl apply -f -
# IADR-0093 (#353): MinIO Console の Keycloak OIDC client secret（平文コミットしない・dev 既定 or env 上書き）。
# minio.yaml は minio.oidc.enabled 時に optional 参照で注入する（未作成でも Pod 起動＝root ログインへフォールバック）。
# IADR-0098 (#310) PR-3: minio-oidc は ESO=1 のとき Vault→ExternalSecret 供給へ委譲し手動 apply をスキップ（二重所有回避）。
# 既定（ESO 未設定）は従来どおり手動 apply（バイト等価）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" minio-oidc "client-secret=${MINIO_OIDC_CLIENT_SECRET:-minio-dev-secret-change-me}"
fi
# IADR-0097 (#310) PR-2: minio-credentials/wikijs-db/wikijs-sync は ESO=1 のとき Vault→ExternalSecret 供給へ委譲し
# 手動 apply をスキップする（二重所有回避）。既定（ESO 未設定）は従来どおり手動 apply（バイト等価）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" minio-credentials \
    "accessKey=${MINIO_ACCESS_KEY:-minioadmin}" "secretKey=${MINIO_SECRET_KEY:-minioadmin}"
  apply_secret "$MSP_NS" wikijs-db "password=${WIKIJS_DB_PASSWORD:-kp}"
  apply_secret "$MSP_NS" wikijs-sync "apiKey=${WIKIJS_SYNC_APIKEY:-}"
fi
# fail-safe: 空=外部 LLM を呼ばない（ADR-0010 ルーティングは明示設定時のみ有効）。
# IADR-0096 (#310): ESO=1 のときは llm-provider-credentials を Vault→ExternalSecret 供給に委譲し、手動 apply は
# スキップする（ExternalSecret が Secret を所有＝二重所有回避）。既定（ESO 未設定）は従来どおり手動 apply（バイト等価）。
if [ "${ESO:-}" != "1" ]; then
  apply_secret "$MSP_NS" llm-provider-credentials \
    "anthropic-api-key=${ANTHROPIC_API_KEY:-}" "openai-api-key=${OPENAI_API_KEY:-}"
fi

echo "==> [6/7] helm upgrade --install (values-local)"
helm upgrade --install msp deploy/helm/microservices-platform \
  -n "$MSP_NS" -f deploy/local/values-local.yaml

echo "==> [7/7] ExternalName aliases (素のサービス名 -> platform-infra FQDN)"
kubectl apply -f deploy/local/aliases/microservices-platform-externalnames.yaml

# ADR-0006, IADR-0077 (AST #24): opt-in オーバーレイ（既定オフ・fail-safe）。
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
  kubectl apply -k deploy/local/observability
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
  kubectl apply -f deploy/local/vault/eso/externalsecret-wikijs-db.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-wikijs-sync.yaml
  # IADR-0098 (#310) PR-3: OIDC client secret 群。minio-oidc は MSP ns、grafana/vault/headlamp-oidc は platform-infra ns。
  # ExternalSecret は namespaced だが ClusterSecretStore は cluster-scoped のため両 ns から同名 store を参照できる。
  # 元の手動 apply のゲート意味論に合わせて供給する（機能オフ時に未使用 Secret を残さない＝元の条件付き apply と対称）:
  #  - minio-oidc: 常時（step 5 相当・元も無条件）
  #  - vault-oidc: VAULT 前提（ESO=1 は VAULT 併用ガード下＝ここでは常に真）で常時
  #  - grafana-oidc / headlamp-oidc: 各機能（OBSERVABILITY / HEADLAMP）が有効なときだけ供給
  kubectl apply -f deploy/local/vault/eso/externalsecret-minio-oidc.yaml
  kubectl apply -f deploy/local/vault/eso/externalsecret-vault-oidc.yaml
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
  # 確認コマンドは実際に apply した ExternalSecret のみ列挙する（無効ゲートの secret を挙げて NotFound で
  # 誤解させない）。MSP ns は常時 5 本。infra ns は基盤 3 本＋vault-oidc 常時＋有効ゲートの grafana/headlamp-oidc。
  infra_es="postgres rabbitmq keycloak-admin vault-oidc"
  [ "${OBSERVABILITY:-}" = "1" ] && infra_es="$infra_es grafana-oidc"
  [ "${HEADLAMP:-}" = "1" ] && infra_es="$infra_es headlamp-oidc"
  echo "    ESO: llm/minio-credentials/wikijs-db/wikijs-sync/minio-oidc（MSP ns 常時）＋ 基盤 postgres/rabbitmq/keycloak-admin"
  echo "         （infra ns・Merge・手動 apply 保持）＋ vault-oidc および有効ゲートの grafana/headlamp-oidc を"
  echo "         Vault(secret/msp/...)→ExternalSecret 供給（基盤以外の手動 apply はスキップ済み）。"
  echo "         確認(MSP):   kubectl -n $MSP_NS get externalsecret,secret llm-provider-credentials minio-credentials wikijs-db wikijs-sync minio-oidc"
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
  eso_wait "$MSP_NS" llm-provider-credentials minio-credentials minio-oidc wikijs-db wikijs-sync
  infra_sync=""
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
  # **自分自身の :8080** へ discovery を投げ 404 になる（OIDC ログイン不能）。issuer は in-cluster 正準名の
  # ままにしたいので、エイリアスで名前解決だけを正す（issuer/metadata の分離は不要）。
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
  # 集約後 URL（argocd.localhost:50000・ホスト名ベース・#357/IADR-0091）で登録し、edge の平文 http のため
  # server.insecure=true にする。fail-safe: local admin は残す（OIDC は追加・未マッピングは policy.default='' で
  # no-access）。install が作成した ConfigMap/Secret へ merge patch で「追加のみ」適用し既存キー（server.secretkey 等）
  # を保持する（apply による全置換はしない）。client secret は平文で置かず argocd-secret に merge patch。
  kubectl -n argocd patch configmap argocd-cm --type merge --patch-file deploy/local/argocd/oidc/argocd-cm-patch.yaml
  kubectl -n argocd patch configmap argocd-rbac-cm --type merge --patch-file deploy/local/argocd/oidc/argocd-rbac-cm-patch.yaml
  kubectl -n argocd patch configmap argocd-cmd-params-cm --type merge --patch-file deploy/local/argocd/oidc/argocd-cmdparams-patch.yaml
  kubectl -n argocd patch secret argocd-secret --type merge \
    -p "{\"stringData\":{\"oidc.keycloak.clientSecret\":\"${ARGOCD_OIDC_CLIENT_SECRET:-argocd-dev-secret-change-me}\"}}"
  # server.insecure（cmd-params）と oidc の反映のため argocd-server を再起動する（CM は live 反映だが params は要再起動）。
  kubectl -n argocd rollout restart deploy/argocd-server >/dev/null 2>&1 || true
  echo "    ArgoCD OIDC: http://argocd.localhost:50000 (LOCALEDGE=1) — Keycloak でログイン（local admin は break-glass）。"
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

# IADR-0091 (#356): ローカルエッジ集約（opt-in・既定オフ・fail-safe）。Traefik 追加 entrypoint admin:50000 ＋
# platform フロント(80/443)/管理ツール(50000・ホスト名ベース)の Ingress を適用する。既定(env 未設定)では何も
# 実行されず挙動不変。k3d は上の cluster create(LOCALEDGE=1)で 80/443/50000 を公開済みが前提。Rancher Desktop は
# 内蔵 k3s の LB がポート公開するため cluster 再作成は不要（overlay 適用のみ）。#355 と競合する grafana.yaml/
# realm.json は触らない（redirect 追記・root_url は #355 マージ後の PR-2）。
if [ "${LOCALEDGE:-}" = "1" ]; then
  echo "==> [opt-in] local edge aggregation (Traefik admin:50000 + Ingress, IADR-0091)"
  kubectl apply -k deploy/local/edge
  # argocd namespace が存在するときのみ、argocd 用の管理ツール Ingress を追加適用する
  # （ns 不在時に失敗させない fail-safe。ArgoCD は ARGOCD=1 の別 opt-in で作成される）。
  if kubectl get namespace argocd >/dev/null 2>&1; then
    kubectl apply -f deploy/local/edge/argocd-ingress.yaml
  fi
  echo "    platform フロント: http://localhost/ (80) ・ https://localhost/ (443・Traefik 既定自己署名)"
  echo "    管理ツール(50000): http://grafana.localhost:50000 / headlamp.localhost / vault.localhost / qdrant.localhost"
  echo "    ホスト名解決・TLS・k3d 再作成手順は deploy/local/edge/README.md 参照。"
fi

# IADR-0133 (#517): ABAC の属性辞書とポリシーを dev 既定値で投入する。ポリシーが 0 件だと
# AuthorizationService は deny-by-default で縮退し、**認証を通しても文書一覧・横断検索が常に空**になる
# （仕様どおりだが「壊れている」のと区別が付かない）。既定（env 未設定）は投入せず挙動不変＝バイト等価で、
# 本番 values には一切影響しない（投入先は経路B の稼働中サービスであり、chart ではない）。
# best-effort: 投入の失敗で up 全体を止めない（クラスタ自体は使えるため。再実行は冪等）。
if [ "${ABACSEED:-}" = "1" ]; then
  echo "==> [opt-in] ABAC 初期投入（属性辞書・ポリシー / IADR-0133）"
  node "$ROOT/scripts/seed-abac-policies.js" \
    || echo "    WARN: ABAC 初期投入に失敗（best-effort）。node scripts/seed-abac-policies.js で再実行できる" >&2
fi

echo ""
echo "done. 状態確認:"
echo "  kubectl get pods -A"
echo "  kubectl -n $MSP_NS port-forward svc/bff-service 5080:8080   # http://localhost:5080/health"
# IADR-0093 (#353): MinIO Console SSO は集約 URL 前提（LOCALEDGE=1）＋ポリシー適用が必要。
echo "MinIO Console SSO(#353): http://minio.localhost:50000 (要 LOCALEDGE=1)。ポリシー適用と port-forward 単独時の"
echo "  制約（OIDC 未成立→root フォールバック）は deploy/local/minio-oidc/README.md を参照。"
echo "AST 連結は AST chart(#122) 適用後に scripts/... で行う。"

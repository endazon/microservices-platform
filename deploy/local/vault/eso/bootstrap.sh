#!/usr/bin/env bash
# IADR-0096 (#310): Vault の kubernetes 認証を有効化・設定し、ESO 用の policy/role と MSP secret の seed を入れる
# runtime bootstrap（再実行可・[[IADR-0094]] と同型）。Vault は既定で永続化（IADR-0457）＝Pod 再起動では消えない。
# vault-data PVC を消したとき・PERSIST=0（インメモリ）の Vault を再起動したときは再実行する。
#
#   [ANTHROPIC_API_KEY=... OPENAI_API_KEY=...] bash deploy/local/vault/eso/bootstrap.sh
#
# 前提: VAULT=1 で dev Vault が起動済み（platform-infra）。root トークンは vault Pod の env VAULT_DEV_ROOT_TOKEN_ID。
# 全 vault 操作は vault Pod 内で実行する（kubectl exec）。ホストに vault CLI は不要。
# fail-safe: seed 値は env 由来 or 空既定（空＝外部 LLM を呼ばない現行 fail-safe と同値）。平文はリポジトリに置かない。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
INFRA_NS="platform-infra"

# vault Pod 内で root として vault コマンドを実行するヘルパ（stdin を透過＝policy write に使う）。
vexec() { kubectl -n "$INFRA_NS" exec -i deploy/vault -- sh -c \
  'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"; '"$1"; }

echo "==> kubernetes 認証を有効化（未有効時のみ・冪等）"
vexec 'vault auth list -format=json 2>/dev/null | grep -q "\"kubernetes/\"" || vault auth enable kubernetes'

echo "==> auth/kubernetes/config（in-cluster local mode: Vault 自身の SA を reviewer に使う）"
vexec 'vault write auth/kubernetes/config kubernetes_host=https://kubernetes.default.svc'

echo "==> policy: eso-read（MSP＋AST の read・store は両者共有のため両 path を許可）"
vexec 'vault policy write eso-read -' < "$ROOT/deploy/local/vault/eso/policy-eso-read.hcl"

echo "==> role: eso（ESO の SA external-secrets/external-secrets に束縛）"
vexec 'vault write auth/kubernetes/role/eso bound_service_account_names=external-secrets bound_service_account_namespaces=external-secrets policies=eso-read ttl=1h'

# SC-22, ADR-0095 決定 3, IADR-0433 決定 1・4, IADR-0453 決定 9 (#1411): 画面（/admin/secrets）から秘密情報を
# 投入する BFF の書き込み権限。**画面と同じ PR で配備する**（先に権限だけを作らない。ADR-0095 §統制の表）。
# 🔴 policy は SC-22 の項目ごとの完全一致パスだけ（ワイルドカードなし・data の read なし）。項目集合の単一情報源は
#    deploy/bootstrap/sc22-secret-items.json の items[] で、HCL との一致は Platform.Bff.Tests が固定する。
# 🔴 role は BFF 専用の ServiceAccount `bff`（helm が作る）にだけ束縛する。**`default` に束縛しない** ——
#    名前空間の全 Pod が秘密を書けるようになる。**`eso` role へ足さない**（ESO は読み取り専用のまま。決定 5）。
echo "==> policy: bff-secret-write（SC-22 の項目だけ・完全一致パス・data の read なし）"
vexec 'vault policy write bff-secret-write -' < "$ROOT/deploy/local/vault/eso/policy-bff-secret-write.hcl"

echo "==> role: bff-secret-writer（BFF 専用 SA microservices-platform/bff に束縛）"
vexec 'vault write auth/kubernetes/role/bff-secret-writer bound_service_account_names=bff bound_service_account_namespaces=microservices-platform policies=bff-secret-write ttl=1h'

# FR-01, UC-04, NFR-18, IADR-0495 決定 1 (#458 段 S1): datasource-service がコネクタの資格情報（`vault:datasource/…#<key>`）を
# 実行時に読む権限。🔴 policy は専用接頭辞 `secret/data/datasource/*` の read だけ（ESO の `msp/*` の外。書き込み・削除なし）。
# 🔴 role は datasource-service 専用の ServiceAccount `datasource-service`（helm が作る）にだけ束縛する。**`default` に束縛しない**、
#    **`eso` role へ足さない**（ESO が接頭辞を読めると、コネクタの資格情報が k8s Secret へ材料化され得る）。
# 読み手の Pod は helm の services.datasource.vault.address が空でないときだけ Vault を引く（空なら S0 と同じ素通し）。
echo "==> policy: datasource-connector-read（secret/data/datasource/* の read だけ）"
vexec 'vault policy write datasource-connector-read -' < "$ROOT/deploy/local/vault/eso/policy-datasource-connector-read.hcl"

echo "==> role: datasource-connector-reader（datasource-service 専用 SA microservices-platform/datasource-service に束縛）"
vexec 'vault write auth/kubernetes/role/datasource-connector-reader bound_service_account_names=datasource-service bound_service_account_namespaces=microservices-platform policies=datasource-connector-read ttl=1h'

# SC-22, ADR-0095 決定 4, IADR-0456 決定 6 (#1477): **画面（/admin/secrets）が書く KV は無いときだけ作る。**
# 従前は毎回 `vault kv put`（全置換）していたため、`k8s-local-up.sh` を再実行するたびに**画面で入れた値が env の既定（空）で消えた**。
# 対象は deploy/bootstrap/sc22-secret-items.json の items[] のうち seed するもの。
# 🔴 作成は `-cas=0`（Vault 側でも「無いときだけ」）。既に在る KV は、env が**空でない**プロパティだけを部分更新する。
#    この形は Platform.Bff.Tests の SecretItemBootstrapSeedTests が固定する（無条件の put へ戻すと落ちる）。
vkv_exists() { vexec "vault kv metadata get secret/$1 >/dev/null 2>&1"; }
# NFR-18, IADR-0494 (#1728): **この実行で Vault に書いた KV を覚えておき、最後にそれを読む ExternalSecret の同期を促す。**
# ExternalSecret は refreshInterval（1h）でしか Vault を読み直さないので、在る KV へ足したキーは次の refresh まで
# 消費側の Secret に入らない（PoC 2026-10-03: kb-reader-auth-client-* を patch したのに ast-secrets が古いまま）。
# CHANGED_PATHS ＝ 書いた KV のパス（前後に空白・重複なし）。ADDED_PROPS ＝ 在る KV へ書いたプロパティ（「パス プロパティ」の行）。
CHANGED_PATHS=" "
ADDED_PROPS=""
mark_changed() { # <path> [property]
  case "$CHANGED_PATHS" in *" $1 "*) ;; *) CHANGED_PATHS="${CHANGED_PATHS}$1 " ;; esac
  if [ -n "${2:-}" ]; then ADDED_PROPS="${ADDED_PROPS}$1 $2"$'\n'; fi
}
# 既に在る KV のプロパティを 1 つだけ部分更新する。**値が空なら何もしない**（未指定の env で画面の値を消さない）。
# 値は stdin で渡す（`キー=-`）。現在版が削除されている等で失敗しても bootstrap は止めない（画面・Runbook で直す）。
vkv_patch_nonempty() { # <path> <property> <value>
  [ -n "$3" ] || return 0
  if printf '%s' "$3" | vexec "vault kv patch -method=patch secret/$1 $2=- >/dev/null"; then
    mark_changed "$1" "$2"
  else
    echo "    WARN: secret/$1 の $2 を更新できない（現在版が削除されている等）。画面または Runbook の手順で直す" >&2
  fi
}
# NFR-18, IADR-0494 決定 3 (#1728): 構成値（秘密ではない。keycloak-smtp の host / port / starttls）を**今の値と違うときだけ**書く。
# 従前は毎回 patch していたため、再実行のたびに「書いた KV」になり、同期を促す対象から外れなかった（値は同じなのに毎回待つ）。
vkv_patch_config() { # <path> <property> <value>
  [ -n "$3" ] || return 0
  local cur
  cur="$(vexec "vault kv get -field=$2 secret/$1" 2>/dev/null || true)"
  [ "$cur" = "$3" ] && return 0
  vkv_patch_nonempty "$1" "$2" "$3"
}
# 既に在る KV に、プロパティが**無いときだけ**値を足す（在れば空文字でも触らない）。値は stdin で渡す。
# 画面が先に 1 プロパティだけ書いた KV（BFF は KV が無いと cas=0 でそのプロパティだけの KV を作る）へ、
# 画面から書けない構成値（realm と対の *-auth-client-*）を補うために使う。
vkv_patch_if_missing() { # <path> <property> <value>
  vexec "vault kv get -field=$2 secret/$1 >/dev/null 2>&1" && return 0
  if printf '%s' "$3" | vexec "vault kv patch -method=patch secret/$1 $2=- >/dev/null"; then
    mark_changed "$1" "$2"
  else
    echo "    WARN: secret/$1 に $2 を足せない（現在版が削除されている等）。Runbook の手順で直す" >&2
  fi
}

# SC-22, NFR-18, ADR-0124 決定 1, IADR-0485 (#1682): **対になる秘密は無いときだけ作る。在れば触らない。**
# 対になる秘密 ＝ 相手（認証基盤の realm・データストア）と同時に変えないと成立しない秘密。許可リスト
# deploy/bootstrap/sc22-secret-items.json の deferred[]（OIDC クライアントシークレット・s2s 資格情報）と
# excluded[]（データストアの資格情報）のパスがこれに当たる。従前は毎回 `vault kv put`（全置換）で env か開発用既定値へ戻したため、
# **相手と Vault を対で回した値を、次の `k8s-local-up.sh` が Vault 側だけ戻して片側だけ書いた状態を作っていた。**
# 🔴 env はここでは**作るときだけ**効く（既に在る KV を env で書き換えない）。回すのは
#    docs/operations/paired-secret-rotation-runbook.md の手順（相手と Vault を対で書く）である。
# 作成は `-cas=0`（Vault 側でも「無いときだけ」）。この形は Platform.Bff.Tests の SecretItemBootstrapSeedTests が固定する。
vkv_create_if_absent() { # <path> <key>='<value>' [...]
  local path="$1"; shift
  if vkv_exists "$path"; then
    echo "    keep: secret/$path は在るので触らない（対になる秘密。回すときは paired-secret-rotation-runbook.md）"
    return 0
  fi
  vexec "vault kv put -cas=0 secret/$path $*"
  mark_changed "$path"
}

echo "==> seed: secret/msp/*（env 由来 or dev 既定・平文の実 secret は非コミット）"
# 値は現行 apply_secret の既定と同一（objectstorage-dev/kp/空）。env で上書き可。
# SC-22 の項目（IADR-0456 決定 6）: 無いときだけ作る。在れば env が空でないキーだけ差し替える。
if vkv_exists msp/llm-provider-credentials; then
  vkv_patch_nonempty msp/llm-provider-credentials anthropic-api-key "${ANTHROPIC_API_KEY:-}"
  vkv_patch_nonempty msp/llm-provider-credentials openai-api-key "${OPENAI_API_KEY:-}"
else
  vexec "vault kv put -cas=0 secret/msp/llm-provider-credentials anthropic-api-key='${ANTHROPIC_API_KEY:-}' openai-api-key='${OPENAI_API_KEY:-}'"
  mark_changed msp/llm-provider-credentials
fi
# IADR-0097 (#310) PR-2: object-storage-credentials / wikijs-db / wikijs-sync。
# IADR-0461 決定 3 (#1499): オブジェクトストレージ（SeaweedFS）の S3 資格情報。旧 msp/minio-credentials。
vkv_create_if_absent msp/object-storage-credentials "accessKey='${OBJECT_STORAGE_ACCESS_KEY:-objectstorage-dev}' secretKey='${OBJECT_STORAGE_SECRET_KEY:-objectstorage-dev-secret}'"
# NFR, ADR-0002 (#1012): サービス DB のパスワード。appsettings.json から接続文字列を撤去したため、
# これが無いと ESO=1 では DB を持つ全サービスが起動できない。dev 既定は init スクリプトが作る `kp`。
vkv_create_if_absent msp/postgres-app "password='${APP_DB_PASSWORD:-kp}'"
# NFR, ADR-0027 (#1022): ブローカのパスワード（app 側）。appsettings.json から接続文字列を撤去したため、
# これが無いと ESO=1 では RabbitMQ を使う 7 サービスが起動できない。★値は step 3 の基盤 secret
# `rabbitmq` と**同値**にすること（同じ env RABBITMQ_PASSWORD から作る。ズレると認証破壊）。
vkv_create_if_absent msp/rabbitmq-app "password='${RABBITMQ_PASSWORD:-guest}'"
vkv_create_if_absent msp/wikijs-db "password='${WIKIJS_DB_PASSWORD:-kp}'"
# SC-22 の項目（IADR-0456 決定 6）。Wiki.js が発行した鍵の書き戻し（deploy/local/wikijs-setup/bootstrap.sh）は別の経路である。
if vkv_exists msp/wikijs-sync; then
  vkv_patch_nonempty msp/wikijs-sync apiKey "${WIKIJS_SYNC_APIKEY:-}"
else
  vexec "vault kv put -cas=0 secret/msp/wikijs-sync apiKey='${WIKIJS_SYNC_APIKEY:-}'"
  mark_changed msp/wikijs-sync
fi
# IADR-0098 (#310) PR-3: OIDC client secret 群（grafana/vault/headlamp）。既定は各 <tool>-dev-secret-change-me
# （現行 apply_secret の env 既定と同値）。env は作るときだけ効く（#1682）。realm import の dev client secret と一致させること。
# ［IADR-0461 決定 5 / #1499］msp/minio-oidc（MinIO Console の SSO）は撤去した。
# NFR, SC-13, ADR-0032, IADR-0251/IADR-0273/IADR-0316 (#1107): BFF がコンフィデンシャルクライアントとして
# Keycloak と通信するための client secret。**空だと `GET /bff/auth/login` が 500 で落ちる**（PAR が 401）。
# 既定は realm の置き場と同値（一致しないと PAR が同じ 401 を返す）。env は作るときだけ効く（#1682）。
vkv_create_if_absent msp/bff-oidc "client-secret='${BFF_OIDC_CLIENT_SECRET:-bff-dev-secret-change-me}'"
# FR-05, FR-09, SC-17, IADR-0301/IADR-0329 (#1101): AuthorizationService が Keycloak Admin REST へ
# SC-17 の変更を反映するための client secret（realm の機密クライアント `identity-admin`）。
# **空だと authorization-service Pod が起動しない**（helm は非 optional な secretKeyRef で読む）。
# 既定は realm import の置き場と同値（ズレると client_credentials が 401 になり SC-17 が 500 になる）。
vkv_create_if_absent msp/identity-admin-oidc "client-secret='${IDENTITY_ADMIN_CLIENT_SECRET:-identity-admin-dev-secret-change-me}'"
# FR-02, FR-03, NFR-09, NFR-16, ADR-0029/ADR-0075, IADR-0379 決定 4 / IADR-0397 (#1255): east-west gRPC の
# **呼び出し側**が名乗る資格情報（realm の機密クライアント `retrieval-service` / `ingestion-service`）。
# **空だと当該 Pod が起動しない**（helm は非 optional な secretKeyRef で読む）。
# 既定は realm import の置き場と同値（ズレると client_credentials が 401 になり、埋め込みが 1 件も通らない）。
vkv_create_if_absent msp/retrieval-service-token "client-secret='${RETRIEVAL_SERVICE_CLIENT_SECRET:-retrieval-service-dev-secret-change-me}'"
vkv_create_if_absent msp/ingestion-service-token "client-secret='${INGESTION_SERVICE_CLIENT_SECRET:-ingestion-service-dev-secret-change-me}'"
# FR-04, FR-12, FR-18, NFR-09, NFR-16, ADR-0029/ADR-0075, IADR-0379 決定 4 / IADR-0400 (#1255):
# テキスト生成（/complete 系）の呼び出し側 3 サービス。埋め込みの 2 つと同型・同じ理由である。
vkv_create_if_absent msp/aianalysis-service-token "client-secret='${AIANALYSIS_SERVICE_CLIENT_SECRET:-aianalysis-service-dev-secret-change-me}'"
vkv_create_if_absent msp/graph-service-token "client-secret='${GRAPH_SERVICE_CLIENT_SECRET:-graph-service-dev-secret-change-me}'"
vkv_create_if_absent msp/conversion-service-token "client-secret='${CONVERSION_SERVICE_CLIENT_SECRET:-conversion-service-dev-secret-change-me}'"
# FR-05, FR-13, FR-16, UC-04, UC-09, SC-06, SC-12, NFR-09, NFR-16, ADR-0029/ADR-0075,
# IADR-0379 決定 4 / IADR-0401 (#1255): 認可サービス（ABAC スコープ解決・利用者名簿の狭い読み口）の
# 呼び出し側 3 サービス。🔴 wiki / datasource / mcp-server の 3 つは
# **利用者トークンの転送をやめる**ための資格情報である（呼び出し先の読み口を狭めてある）。
vkv_create_if_absent msp/wiki-service-token "client-secret='${WIKI_SERVICE_CLIENT_SECRET:-wiki-service-dev-secret-change-me}'"
vkv_create_if_absent msp/datasource-service-token "client-secret='${DATASOURCE_SERVICE_CLIENT_SECRET:-datasource-service-dev-secret-change-me}'"
vkv_create_if_absent msp/mcp-server-token "client-secret='${MCP_SERVER_CLIENT_SECRET:-mcp-server-dev-secret-change-me}'"
# FR-19, FR-20, FR-21, FR-22, NFR-09, NFR-16, ADR-0004/ADR-0029/ADR-0075,
# IADR-0379 決定 4 / IADR-0419 (#1255): 通知の受け付け（NotificationService）の呼び出し側。
# 🔴 **DocumentService が east-west gRPC の呼び出し元になるのはここが最初である**
# （従前は受け口だけを持っていた）。空だと Pod が起動しない —— 送出は fail-open なので、
# 資格情報だけが欠けた状態で起動できると**個人資料の通知が静かに 1 件も届かなくなる**。
vkv_create_if_absent msp/document-service-token "client-secret='${DOCUMENT_SERVICE_CLIENT_SECRET:-document-service-dev-secret-change-me}'"
# NFR-09, IADR-0095/IADR-0342 (#1127): Wiki.js の OIDC ストラテジ（DB 保持・manifest 化できない runtime 状態）
# を冪等に再適用する `deploy/local/wikijs-setup/bootstrap.sh` 段 8 が読む client secret。
# **Pod は誰も env で読まない**（読み手は bootstrap）。既定は realm import の置き場と同値 ——
# ズレると Keycloak の token 端点が `invalid_client` を返し、認可までは進むのに callback で落ちる。
vkv_create_if_absent msp/wikijs-oidc "client-secret='${WIKIJS_OIDC_CLIENT_SECRET:-wiki-js-dev-secret-change-me}'"
vkv_create_if_absent msp/grafana-oidc "client-secret='${GRAFANA_OIDC_CLIENT_SECRET:-grafana-dev-secret-change-me}'"
vkv_create_if_absent msp/vault-oidc "client-secret='${VAULT_OIDC_CLIENT_SECRET:-vault-dev-secret-change-me}'"
vkv_create_if_absent msp/headlamp-oidc "client-secret='${HEADLAMP_OIDC_CLIENT_SECRET:-headlamp-dev-secret-change-me}'"
# SC-15, ADR-0078 決定 4, IADR-0404 (#1245 PR-C): 近接 MTA へ投函できないときに申請を閉じる門
# （platform-infra/reset-gate）が Admin REST を叩く機密クライアントの secret。
# **既定は realm import の置き場と同値**にする —— ズレると Keycloak の token 端点が invalid_client を返し、
# 門は 401 を打ち続けるだけになる（窓は開いたまま。wikijs-oidc と同じ罠）。
vkv_create_if_absent msp/reset-gate-oidc "client-secret='${RESET_GATE_CLIENT_SECRET:-reset-gate-dev-secret-change-me}'"
# NFR-02, NFR-21, ADR-0076 決定 4, ADR-0079 決定 1, IADR-0378 (#1287): 合成監視のプローブが
# client_credentials で名乗る機密クライアントの secret。**既定は realm import の置き場と同値**にする ——
# ズレると token 端点が invalid_client を返し、プローブは 1 度も BFF へ到達しないまま
# `RagLatencySeriesAbsent` を鳴らす（原因が「評価対象が本当に無い」と区別できない。wikijs-oidc と同じ罠）。
# 種は無条件に入れる。ExternalSecret を apply するのは SYNTHETIC=1 のときだけである（k8s-local-up.sh）。
vkv_create_if_absent msp/synthetic-monitor-oidc "client-secret='${SYNTHETIC_MONITOR_CLIENT_SECRET:-synthetic-monitor-dev-secret-change-me}'"
# IADR-0099 (#310) PR-4: 基盤 secret（postgres/rabbitmq/keycloak-admin）。★値は k8s-local-up.sh step 3 の手動 apply と
# **完全一致**させること（env 由来 or 同じ既定 postgres/guest/admin）。DB/broker/keycloak は既存パスワードで初期化済みのため、
# 値がズレると認証破壊。ExternalSecret は creationPolicy: Merge で同一値を上書きするのみ（値不変＝無害）。
# ［2026-09-28 / #1682］無いときだけ作る（上の vkv_create_if_absent）。回した後は Vault が正であり、手動 apply の Secret は
# 次の同期で Vault の値へ戻る（起動の後に同期を促す手順は paired-secret-rotation-runbook.md）。
vkv_create_if_absent msp/postgres "password='${PG_PASSWORD:-postgres}'"
vkv_create_if_absent msp/rabbitmq "username='${RABBITMQ_USER:-guest}' password='${RABBITMQ_PASSWORD:-guest}'"
vkv_create_if_absent msp/keycloak-admin "password='${KEYCLOAK_ADMIN_PASSWORD:-admin}'"
# #438, ADR-0045 決定 2-b/6: SMTP リレー（go-live では Google Workspace への STARTTLS リレー）の資格情報。
# **実環境の値は未供給のため from/user/password の既定は空文字**（他 secret と同じ fail-safe。runbook §2 の
# 「長さが 0 なら kcadm を打つな」判定はこの空既定に依る）。値の投入手順・Secret の消費方法は
# docs/operations/keycloak-smtp-relay-setup-runbook.md を参照。
#
# ADR-0045 決定 9 (#1144): **宛先の既定はクラスタ内の捕捉用 MTA（Mailpit）である。**
# 決定 9 は「開発環境では実送信しない。本番のメールテナントを開発環境から指さない」と無条件で定めており、
# 既定が `smtp.gmail.com` のままでは **`from` に実値を入れた瞬間に外部の本番リレーへ実送信する**。
# 宛先の単一情報源は deploy/local/infra/mailpit.yaml の Service（`mailpit` / 1025）である。
# 実リレーへ向けるのは `SMTP_HOST` を明示したときだけ。
#
# 🔴 **port / starttls の既定は「宛先から導出する」** —— 素朴に `false` を既定へ書くと、実リレーへ向ける
# ために `SMTP_HOST`/`SMTP_FROM`/… だけを渡した運用者が **STARTTLS 無しで外部へ繋ぐ**（決定 5 の破れ）。
# 捕捉用 MTA 宛のときだけ平文（1025 / false）、**それ以外の宛先なら決定 2-b の確定値（587 / true）**。
SMTP_CAPTURE_HOST='mailpit.platform-infra.svc.cluster.local'
smtp_host="${SMTP_HOST:-$SMTP_CAPTURE_HOST}"
if [ "$smtp_host" = "$SMTP_CAPTURE_HOST" ]; then
  smtp_port_default='1025'; smtp_starttls_default='false'
else
  smtp_port_default='587'; smtp_starttls_default='true'
fi
# SC-22 の項目（IADR-0456 決定 6）: from / user / password は画面が書く秘密なので、在れば env が空でないときだけ差し替える。
# host / port / starttls は構成（env と Git が決める。画面は書けない）なので、在っても毎回その値へ揃える（従前と同じ意味論）。
if vkv_exists msp/keycloak-smtp; then
  vkv_patch_config msp/keycloak-smtp host "$smtp_host"
  vkv_patch_config msp/keycloak-smtp port "${SMTP_PORT:-$smtp_port_default}"
  vkv_patch_config msp/keycloak-smtp starttls "${SMTP_STARTTLS:-$smtp_starttls_default}"
  vkv_patch_nonempty msp/keycloak-smtp from "${SMTP_FROM:-}"
  vkv_patch_nonempty msp/keycloak-smtp user "${SMTP_USER:-}"
  vkv_patch_nonempty msp/keycloak-smtp password "${SMTP_PASSWORD:-}"
else
  vexec "vault kv put -cas=0 secret/msp/keycloak-smtp \
    host='$smtp_host' port='${SMTP_PORT:-$smtp_port_default}' starttls='${SMTP_STARTTLS:-$smtp_starttls_default}' \
    from='${SMTP_FROM:-}' user='${SMTP_USER:-}' password='${SMTP_PASSWORD:-}'"
  mark_changed msp/keycloak-smtp
fi

# SC-22, ADR-0095 決定 1, IADR-0456 決定 6 (#1477): AST が ESO で受ける ai-stock-trading/app-secrets（契約 #1477 の表）。
# **無いときだけ作る。在れば触らない**（画面で入れた外部 API キー・Discord ID を消さない。env での上書きも持たない —— 投入面は画面）。
# *-auth-client-* 10 件（5 組）は realm（deploy/keycloak/microservices-platform-realm.json の機密クライアント）と**同値**の dev 既定
# （#1696 / IADR-0492: KB の読み手 kb-reader-auth-client-* を足した。書き手 kb-auth-client-* とは別の主体である）
# （ズレると client_credentials が invalid_client になる）。画面から書ける 12 件は空文字（未設定＝各連携が no-op）。
# 🔴 ai-stock-trading/moomoo / moomoo-rsa は seed しない（未設定のあいだ OpenD は Secret 不在で待機する＝fail-closed）。
# 値の一致（キー集合＝items[] の書ける 12 ＋ notWritable 10、auth は realm と同値）は SecretItemBootstrapSeedTests が固定する。
if ! vkv_exists ai-stock-trading/app-secrets; then
  vexec "vault kv put -cas=0 secret/ai-stock-trading/app-secrets \
    finnhub-api-key='' marketdata-finnhub-api-key='' fred-api-key='' edinet-subscription-key='' \
    discord-webhook-url='' discord-bot-token='' discord-bot-killswitch-phrase='' \
    discord-bot-guild-id='' discord-bot-channel-id='' discord-bot-allowed-user-ids='' discord-bot-user-mapping='' \
    sec-edgar-user-agent='' \
    service-auth-client-id='ai-stock-trading-svc' service-auth-client-secret='dev-only-service-secret' \
    kb-auth-client-id='ai-stock-trading-kb-writer' kb-auth-client-secret='ai-stock-trading-kb-writer-dev-secret-change-me' \
    kb-reader-auth-client-id='ai-stock-trading-kb-reader' kb-reader-auth-client-secret='ai-stock-trading-kb-reader-dev-secret-change-me' \
    llm-auth-client-id='ai-stock-trading-llm-caller' llm-auth-client-secret='ai-stock-trading-llm-caller-dev-secret-change-me' \
    discord-owner-auth-client-id='ai-stock-trading-owner' discord-owner-auth-client-secret='dev-only-owner-secret'"
  mark_changed ai-stock-trading/app-secrets
else
  # 在る KV にも *-auth-client-* 10 件を**無いものだけ**足す（画面が先に書いて作った KV には auth キーが無く、
  # そのままでは ast-secrets に auth キーが載らず AST のサービス間トークン取得が止まる。PR #1478 監査 D4）。値は上の seed と同値。
  vkv_patch_if_missing ai-stock-trading/app-secrets service-auth-client-id 'ai-stock-trading-svc'
  vkv_patch_if_missing ai-stock-trading/app-secrets service-auth-client-secret 'dev-only-service-secret'
  vkv_patch_if_missing ai-stock-trading/app-secrets kb-auth-client-id 'ai-stock-trading-kb-writer'
  vkv_patch_if_missing ai-stock-trading/app-secrets kb-auth-client-secret 'ai-stock-trading-kb-writer-dev-secret-change-me'
  vkv_patch_if_missing ai-stock-trading/app-secrets kb-reader-auth-client-id 'ai-stock-trading-kb-reader'
  vkv_patch_if_missing ai-stock-trading/app-secrets kb-reader-auth-client-secret 'ai-stock-trading-kb-reader-dev-secret-change-me'
  vkv_patch_if_missing ai-stock-trading/app-secrets llm-auth-client-id 'ai-stock-trading-llm-caller'
  vkv_patch_if_missing ai-stock-trading/app-secrets llm-auth-client-secret 'ai-stock-trading-llm-caller-dev-secret-change-me'
  vkv_patch_if_missing ai-stock-trading/app-secrets discord-owner-auth-client-id 'ai-stock-trading-owner'
  vkv_patch_if_missing ai-stock-trading/app-secrets discord-owner-auth-client-secret 'dev-only-owner-secret'
fi

# NFR-18, IADR-0494 (#1728): **この実行で書いた KV を読む ExternalSecret にだけ同期を促し、同期が終わるのを有限時間で待つ。**
# 対象は、ストア ESO_STORE（既定 vault-backend ＝ deploy/local/vault/eso/clustersecretstore-k8s.yaml）を参照し、
# 書いた KV のパスを `data[].remoteRef.key` か `dataFrom[].extract.key` で読む ExternalSecret（全名前空間。AST の ast-secrets を含む）。
# 名前を書き写さずにクラスタから引く —— AST のチャートが描く ExternalSecret を基盤のリストへ写すと、片方だけ増えて漏れる。
# まだ apply されていない ExternalSecret は対象に入らない（作られたときに初回の同期で読むので、促す必要が無い）。
# 待ちの完了条件 ＝ 促す前と違う status.refreshTime ＋ Ready=True。在る KV へ足したプロパティは、`dataFrom.extract` で読む
# ExternalSecret の同期先 Secret にそのキーが在ることまで確かめる（PoC の症状そのもの）。
# 🔴 待ちが時間内に終わらなければ名指しして非 0 で止める（新しいキーを読むサービスが空の秘密のまま動き続けるため）。
#    ESO_FORCE_SYNC_TIMEOUT=0 は促すだけで待たない。
ESO_STORE="${ESO_STORE:-vault-backend}"
ESO_FORCE_SYNC_TIMEOUT="${ESO_FORCE_SYNC_TIMEOUT:-120}"
ESO_FORCE_SYNC_INTERVAL="${ESO_FORCE_SYNC_INTERVAL:-2}"
eso_force_sync_changed() {
  case "$ESO_FORCE_SYNC_TIMEOUT$ESO_FORCE_SYNC_INTERVAL" in
    *[!0-9]*) echo "ERROR: ESO_FORCE_SYNC_TIMEOUT / ESO_FORCE_SYNC_INTERVAL は 0 以上の整数（秒）で指定する（実際: '$ESO_FORCE_SYNC_TIMEOUT' / '$ESO_FORCE_SYNC_INTERVAL'）" >&2; return 1 ;;
  esac
  # 先頭の 0 は 10 進として読む（`08` を $((…)) が 8 進と読んで落ちる・`00` を「待たない」と読めない、を防ぐ。#1728 独立監査）。
  ESO_FORCE_SYNC_TIMEOUT=$((10#$ESO_FORCE_SYNC_TIMEOUT)); ESO_FORCE_SYNC_INTERVAL=$((10#$ESO_FORCE_SYNC_INTERVAL))
  if [ "$CHANGED_PATHS" = " " ]; then
    echo "==> force-sync: この実行で Vault に書いた KV は無い（同期を促さない）"
    return 0
  fi
  echo "==> force-sync: この実行で書いた KV を読む ExternalSecret の同期を促す（書いた KV:${CHANGED_PATHS% }）"
  local listing
  # 1 行 1 本: 名前空間|名前|ストア|同期先|data のキー（空白区切り）|extract のキー（空白区切り）
  if ! listing="$(kubectl get externalsecret -A -o jsonpath='{range .items[*]}{.metadata.namespace}{"|"}{.metadata.name}{"|"}{.spec.secretStoreRef.name}{"|"}{.spec.target.name}{"|"}{range .spec.data[*]}{.remoteRef.key}{" "}{end}{"|"}{range .spec.dataFrom[*]}{.extract.key}{" "}{end}{"\n"}{end}')"; then
    echo "ERROR: ExternalSecret の一覧を読めない（kubectl get externalsecret -A）。この実行で書いた KV:${CHANGED_PATHS% }" >&2
    echo "       ESO の CRD が在るか確かめ、手で同期を促す: kubectl -n <ns> annotate externalsecret <name> force-sync=\"\$(date +%s)\" --overwrite" >&2
    return 1
  fi
  local ns name store target dkeys xkeys k hit targets="" ts
  while IFS='|' read -r ns name store target dkeys xkeys; do
    [ -n "$name" ] || continue
    [ "$store" = "$ESO_STORE" ] || continue
    hit=""
    for k in $dkeys $xkeys; do
      case "$CHANGED_PATHS" in *" $k "*) hit=1 ;; esac
    done
    [ -n "$hit" ] || continue
    targets="${targets}${ns}|${name}|${target:-$name}|${xkeys}"$'\n'
  done <<< "$listing"
  if [ -z "$targets" ]; then
    echo "    対象なし（書いた KV を読む ExternalSecret はまだ apply されていない。作られたときに初回の同期で読む）"
    return 0
  fi
  # 促す前の refreshTime を控えてから注釈を付ける（同じ秒に付けた注釈でも、値は時刻で毎回変わる）。
  ts="$(date +%s)"
  local before="" t
  while IFS='|' read -r ns name target xkeys; do
    [ -n "$name" ] || continue
    t="$(kubectl -n "$ns" get externalsecret "$name" -o jsonpath='{.status.refreshTime}' </dev/null 2>/dev/null || true)"
    before="${before}${ns}|${name}|${t}"$'\n'
    if ! kubectl -n "$ns" annotate externalsecret "$name" "force-sync=$ts" --overwrite </dev/null >/dev/null; then
      echo "ERROR: $ns/$name に force-sync の注釈を付けられない。手で付ける: kubectl -n $ns annotate externalsecret $name force-sync=\"\$(date +%s)\" --overwrite" >&2
      return 1
    fi
    echo "    force-sync $ns/$name"
  done <<< "$targets"
  if [ "$ESO_FORCE_SYNC_TIMEOUT" = "0" ]; then
    echo "    （ESO_FORCE_SYNC_TIMEOUT=0: 同期の完了は待たない）"
    return 0
  fi
  local deadline=$((SECONDS + ESO_FORCE_SYNC_TIMEOUT)) pending state now ready path prop keys
  while :; do
    pending=""
    while IFS='|' read -r ns name target xkeys; do
      [ -n "$name" ] || continue
      # 名前空間・名前の完全一致で引く（部分一致だと infra と platform-infra を取り違える。#1728 独立監査）。
      t="$(awk -F'|' -v ns="$ns" -v n="$name" '$1 == ns && $2 == n { print $3; exit }' <<< "$before")"
      state="$(kubectl -n "$ns" get externalsecret "$name" -o jsonpath='{.status.refreshTime}|{.status.conditions[?(@.type=="Ready")].status}' </dev/null 2>/dev/null || true)"
      now="${state%%|*}"; ready="${state#*|}"
      if [ -z "$now" ] || [ "$now" = "$t" ] || [ "$ready" != "True" ]; then
        pending="${pending} ${ns}/${name}(refreshTime=${now:-なし} Ready=${ready:-なし})"
        continue
      fi
      # 在る KV へ足したプロパティは、extract で読む同期先 Secret にキーが在ること（値は出さない。キーの有無だけ）。
      # 🔴 引くのはキー名だけ（go-template で 1 行 1 キー）。値（base64）をシェル変数へ入れない ——
      #    `bash -x` やデバッグの echo で秘密がログへ出る（#1728 独立監査）。
      keys=""
      while read -r path prop; do
        [ -n "$prop" ] || continue
        case " $xkeys " in *" $path "*) ;; *) continue ;; esac
        [ -n "$keys" ] || keys="$(kubectl -n "$ns" get secret "$target" -o go-template='{{range $k, $v := .data}}{{$k}}{{"\n"}}{{end}}' </dev/null 2>/dev/null || true)"
        case $'\n'"$keys"$'\n' in *$'\n'"$prop"$'\n'*) ;; *) pending="${pending} ${ns}/${target}(キー ${prop} が無い)" ;; esac
      done <<< "$ADDED_PROPS"
    done <<< "$targets"
    if [ -z "$pending" ]; then
      echo "    synced（${ESO_FORCE_SYNC_TIMEOUT}s 以内）"
      return 0
    fi
    if [ "$SECONDS" -ge "$deadline" ]; then
      echo "ERROR: force-sync した ExternalSecret の同期が ${ESO_FORCE_SYNC_TIMEOUT}s 以内に終わらない:${pending}" >&2
      echo "       Vault には書けている。消費側の Secret が古いままなので、新しいキーを読むサービスは空の秘密で動く。" >&2
      echo "       調べる: kubectl -n <ns> describe externalsecret <name>（Ready の reason / message）・kubectl get clustersecretstore $ESO_STORE" >&2
      echo "       待ちを延ばす: ESO_FORCE_SYNC_TIMEOUT=<秒> で再実行する（bootstrap は再実行可）" >&2
      return 1
    fi
    sleep "$ESO_FORCE_SYNC_INTERVAL"
  done
}
eso_force_sync_changed

echo ""
echo "done. ExternalSecret が Vault→k8s Secret を同期する（refresh 1h。この実行で書いた KV を読むものは上で force-sync した（#1728）。画面 /admin/secrets からの書き込みは BFF が force-sync で即時同期を依頼する）:"
echo "  #1682: 対になる秘密（OIDC / s2s / データストアの資格情報の 24 KV）は無いときだけ作った（在るものは env を渡しても触らない。回すのは docs/operations/paired-secret-rotation-runbook.md）"
echo "  #1477: SC-22 の KV（llm-provider-credentials / wikijs-sync / keycloak-smtp / ai-stock-trading/app-secrets）は無いときだけ作った（在るものは env が空でないキーだけ差し替えた）"
echo "  PR-1: llm-provider-credentials / PR-2: object-storage-credentials, wikijs-db, wikijs-sync"
echo "  PR-3: grafana-oidc, vault-oidc, headlamp-oidc (platform-infra ns)"
echo "  #1107: bff-oidc (MSP ns。BFF セッションの client secret。空だと /bff/auth/login が 500)"
echo "  #1101: identity-admin-oidc (MSP ns。SC-17 の Keycloak Admin REST 反映。空だと authorization-service が起動しない)"
echo "  #1245: reset-gate-oidc (platform-infra ns。SC-15 の申請を閉じる門。空だと門が起動しない＝窓が開いたままになる)"
echo "  #1255: retrieval-service-token, ingestion-service-token, aianalysis-service-token, graph-service-token, conversion-service-token, wiki-service-token, datasource-service-token, mcp-server-token, document-service-token (MSP ns。east-west gRPC の s2s 資格情報。空だと当該 Pod が起動しない)"
echo "  PR-4: postgres, rabbitmq, keycloak-admin (platform-infra ns・creationPolicy: Merge・手動 apply は保持)"
echo "  #438/#1102/#1144: keycloak-smtp (platform-infra ns。from/user/password は空＝実値未供給。宛先の既定はクラスタ内の捕捉用 MTA。k8s-local-up.sh の ESO=1 が常時 apply する。docs/operations/keycloak-smtp-relay-setup-runbook.md 参照)"
echo "  #1127: wikijs-oidc (MSP ns。Wiki.js の OIDC ストラテジ seed が読む。WIKIJS_OIDC=1 のときだけ apply される)"
echo "  #1287: synthetic-monitor-oidc (MSP ns。合成監視のプローブが env で読む。SYNTHETIC=1 のときだけ apply される)"
# 🔴 案内は **実際に apply される名前だけ**を挙げる（#1102: 挙げた名前が作られないと、手順どおり
#    打った人が必ず NotFound を踏む）。grafana-oidc / headlamp-oidc は OBSERVABILITY=1 / HEADLAMP=1 の、
#    wikijs-oidc は WIKIJS_OIDC=1 の、synthetic-monitor-oidc は SYNTHETIC=1 のときだけ apply されるため、
#    無条件の並びからは外して注記に回す。
echo "  確認(MSP): kubectl -n microservices-platform get externalsecret,secret llm-provider-credentials object-storage-credentials postgres-app rabbitmq-app wikijs-db wikijs-sync bff-oidc identity-admin-oidc retrieval-service-token ingestion-service-token aianalysis-service-token graph-service-token conversion-service-token wiki-service-token datasource-service-token mcp-server-token document-service-token"
echo "  確認(infra): kubectl -n platform-infra get externalsecret,secret postgres rabbitmq keycloak-admin vault-oidc keycloak-smtp"
echo "             （grafana-oidc は OBSERVABILITY=1、headlamp-oidc は HEADLAMP=1 のときだけ apply される）"

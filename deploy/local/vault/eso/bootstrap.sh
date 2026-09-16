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

# SC-22, ADR-0095 決定 4, IADR-0456 決定 6 (#1477): **画面（/admin/secrets）が書く KV は無いときだけ作る。**
# 従前は毎回 `vault kv put`（全置換）していたため、`k8s-local-up.sh` を再実行するたびに**画面で入れた値が env の既定（空）で消えた**。
# 対象は deploy/bootstrap/sc22-secret-items.json の items[] のうち seed するもの。
# 🔴 作成は `-cas=0`（Vault 側でも「無いときだけ」）。既に在る KV は、env が**空でない**プロパティだけを部分更新する。
#    この形は Platform.Bff.Tests の SecretItemBootstrapSeedTests が固定する（無条件の put へ戻すと落ちる）。
vkv_exists() { vexec "vault kv metadata get secret/$1 >/dev/null 2>&1"; }
# 既に在る KV のプロパティを 1 つだけ部分更新する。**値が空なら何もしない**（未指定の env で画面の値を消さない）。
# 値は stdin で渡す（`キー=-`）。現在版が削除されている等で失敗しても bootstrap は止めない（画面・Runbook で直す）。
vkv_patch_nonempty() { # <path> <property> <value>
  [ -n "$3" ] || return 0
  printf '%s' "$3" | vexec "vault kv patch -method=patch secret/$1 $2=- >/dev/null" \
    || echo "    WARN: secret/$1 の $2 を更新できない（現在版が削除されている等）。画面または Runbook の手順で直す" >&2
}
# 既に在る KV に、プロパティが**無いときだけ**値を足す（在れば空文字でも触らない）。値は stdin で渡す。
# 画面が先に 1 プロパティだけ書いた KV（BFF は KV が無いと cas=0 でそのプロパティだけの KV を作る）へ、
# 画面から書けない構成値（realm と対の *-auth-client-*）を補うために使う。
vkv_patch_if_missing() { # <path> <property> <value>
  vexec "vault kv get -field=$2 secret/$1 >/dev/null 2>&1" && return 0
  printf '%s' "$3" | vexec "vault kv patch -method=patch secret/$1 $2=- >/dev/null" \
    || echo "    WARN: secret/$1 に $2 を足せない（現在版が削除されている等）。Runbook の手順で直す" >&2
}

echo "==> seed: secret/msp/*（env 由来 or dev 既定・平文の実 secret は非コミット）"
# 値は現行 apply_secret の既定と同一（minioadmin/kp/空）。env で上書き可。
# SC-22 の項目（IADR-0456 決定 6）: 無いときだけ作る。在れば env が空でないキーだけ差し替える。
if vkv_exists msp/llm-provider-credentials; then
  vkv_patch_nonempty msp/llm-provider-credentials anthropic-api-key "${ANTHROPIC_API_KEY:-}"
  vkv_patch_nonempty msp/llm-provider-credentials openai-api-key "${OPENAI_API_KEY:-}"
else
  vexec "vault kv put -cas=0 secret/msp/llm-provider-credentials anthropic-api-key='${ANTHROPIC_API_KEY:-}' openai-api-key='${OPENAI_API_KEY:-}'"
fi
# IADR-0097 (#310) PR-2: minio-credentials / wikijs-db / wikijs-sync。
vexec "vault kv put secret/msp/minio-credentials accessKey='${MINIO_ACCESS_KEY:-minioadmin}' secretKey='${MINIO_SECRET_KEY:-minioadmin}'"
# NFR, ADR-0002 (#1012): サービス DB のパスワード。appsettings.json から接続文字列を撤去したため、
# これが無いと ESO=1 では DB を持つ全サービスが起動できない。dev 既定は init スクリプトが作る `kp`。
vexec "vault kv put secret/msp/postgres-app password='${APP_DB_PASSWORD:-kp}'"
# NFR, ADR-0027 (#1022): ブローカのパスワード（app 側）。appsettings.json から接続文字列を撤去したため、
# これが無いと ESO=1 では RabbitMQ を使う 7 サービスが起動できない。★値は step 3 の基盤 secret
# `rabbitmq` と**同値**にすること（同じ env RABBITMQ_PASSWORD から作る。ズレると認証破壊）。
vexec "vault kv put secret/msp/rabbitmq-app password='${RABBITMQ_PASSWORD:-guest}'"
vexec "vault kv put secret/msp/wikijs-db password='${WIKIJS_DB_PASSWORD:-kp}'"
# SC-22 の項目（IADR-0456 決定 6）。Wiki.js が発行した鍵の書き戻し（deploy/local/wikijs-setup/bootstrap.sh）は別の経路である。
if vkv_exists msp/wikijs-sync; then
  vkv_patch_nonempty msp/wikijs-sync apiKey "${WIKIJS_SYNC_APIKEY:-}"
else
  vexec "vault kv put -cas=0 secret/msp/wikijs-sync apiKey='${WIKIJS_SYNC_APIKEY:-}'"
fi
# IADR-0098 (#310) PR-3: OIDC client secret 群（minio/grafana/vault/headlamp）。既定は各 <tool>-dev-secret-change-me
# （現行 apply_secret の env 既定と同値）。env で上書き可。realm import の dev client secret と一致させること。
vexec "vault kv put secret/msp/minio-oidc client-secret='${MINIO_OIDC_CLIENT_SECRET:-minio-dev-secret-change-me}'"
# NFR, SC-13, ADR-0032, IADR-0251/IADR-0273/IADR-0316 (#1107): BFF がコンフィデンシャルクライアントとして
# Keycloak と通信するための client secret。**空だと `GET /bff/auth/login` が 500 で落ちる**（PAR が 401）。
# 既定は realm の置き場と同値（一致しないと PAR が同じ 401 を返す）。env で上書き可。
vexec "vault kv put secret/msp/bff-oidc client-secret='${BFF_OIDC_CLIENT_SECRET:-bff-dev-secret-change-me}'"
# FR-05, FR-09, SC-17, IADR-0301/IADR-0329 (#1101): AuthorizationService が Keycloak Admin REST へ
# SC-17 の変更を反映するための client secret（realm の機密クライアント `identity-admin`）。
# **空だと authorization-service Pod が起動しない**（helm は非 optional な secretKeyRef で読む）。
# 既定は realm import の置き場と同値（ズレると client_credentials が 401 になり SC-17 が 500 になる）。
vexec "vault kv put secret/msp/identity-admin-oidc client-secret='${IDENTITY_ADMIN_CLIENT_SECRET:-identity-admin-dev-secret-change-me}'"
# FR-02, FR-03, NFR-09, NFR-16, ADR-0029/ADR-0075, IADR-0379 決定 4 / IADR-0397 (#1255): east-west gRPC の
# **呼び出し側**が名乗る資格情報（realm の機密クライアント `retrieval-service` / `ingestion-service`）。
# **空だと当該 Pod が起動しない**（helm は非 optional な secretKeyRef で読む）。
# 既定は realm import の置き場と同値（ズレると client_credentials が 401 になり、埋め込みが 1 件も通らない）。
vexec "vault kv put secret/msp/retrieval-service-token client-secret='${RETRIEVAL_SERVICE_CLIENT_SECRET:-retrieval-service-dev-secret-change-me}'"
vexec "vault kv put secret/msp/ingestion-service-token client-secret='${INGESTION_SERVICE_CLIENT_SECRET:-ingestion-service-dev-secret-change-me}'"
# FR-04, FR-12, FR-18, NFR-09, NFR-16, ADR-0029/ADR-0075, IADR-0379 決定 4 / IADR-0400 (#1255):
# テキスト生成（/complete 系）の呼び出し側 3 サービス。埋め込みの 2 つと同型・同じ理由である。
vexec "vault kv put secret/msp/aianalysis-service-token client-secret='${AIANALYSIS_SERVICE_CLIENT_SECRET:-aianalysis-service-dev-secret-change-me}'"
vexec "vault kv put secret/msp/graph-service-token client-secret='${GRAPH_SERVICE_CLIENT_SECRET:-graph-service-dev-secret-change-me}'"
vexec "vault kv put secret/msp/conversion-service-token client-secret='${CONVERSION_SERVICE_CLIENT_SECRET:-conversion-service-dev-secret-change-me}'"
# FR-05, FR-13, FR-16, UC-04, UC-09, SC-06, SC-12, NFR-09, NFR-16, ADR-0029/ADR-0075,
# IADR-0379 決定 4 / IADR-0401 (#1255): 認可サービス（ABAC スコープ解決・利用者名簿の狭い読み口）の
# 呼び出し側 3 サービス。🔴 wiki / datasource / mcp-server の 3 つは
# **利用者トークンの転送をやめる**ための資格情報である（呼び出し先の読み口を狭めてある）。
vexec "vault kv put secret/msp/wiki-service-token client-secret='${WIKI_SERVICE_CLIENT_SECRET:-wiki-service-dev-secret-change-me}'"
vexec "vault kv put secret/msp/datasource-service-token client-secret='${DATASOURCE_SERVICE_CLIENT_SECRET:-datasource-service-dev-secret-change-me}'"
vexec "vault kv put secret/msp/mcp-server-token client-secret='${MCP_SERVER_CLIENT_SECRET:-mcp-server-dev-secret-change-me}'"
# FR-19, FR-20, FR-21, FR-22, NFR-09, NFR-16, ADR-0004/ADR-0029/ADR-0075,
# IADR-0379 決定 4 / IADR-0419 (#1255): 通知の受け付け（NotificationService）の呼び出し側。
# 🔴 **DocumentService が east-west gRPC の呼び出し元になるのはここが最初である**
# （従前は受け口だけを持っていた）。空だと Pod が起動しない —— 送出は fail-open なので、
# 資格情報だけが欠けた状態で起動できると**個人資料の通知が静かに 1 件も届かなくなる**。
vexec "vault kv put secret/msp/document-service-token client-secret='${DOCUMENT_SERVICE_CLIENT_SECRET:-document-service-dev-secret-change-me}'"
# NFR-09, IADR-0095/IADR-0342 (#1127): Wiki.js の OIDC ストラテジ（DB 保持・manifest 化できない runtime 状態）
# を冪等に再適用する `deploy/local/wikijs-setup/bootstrap.sh` 段 8 が読む client secret。
# **Pod は誰も env で読まない**（読み手は bootstrap）。既定は realm import の置き場と同値 ——
# ズレると Keycloak の token 端点が `invalid_client` を返し、認可までは進むのに callback で落ちる。
vexec "vault kv put secret/msp/wikijs-oidc client-secret='${WIKIJS_OIDC_CLIENT_SECRET:-wiki-js-dev-secret-change-me}'"
vexec "vault kv put secret/msp/grafana-oidc client-secret='${GRAFANA_OIDC_CLIENT_SECRET:-grafana-dev-secret-change-me}'"
vexec "vault kv put secret/msp/vault-oidc client-secret='${VAULT_OIDC_CLIENT_SECRET:-vault-dev-secret-change-me}'"
vexec "vault kv put secret/msp/headlamp-oidc client-secret='${HEADLAMP_OIDC_CLIENT_SECRET:-headlamp-dev-secret-change-me}'"
# SC-15, ADR-0078 決定 4, IADR-0404 (#1245 PR-C): 近接 MTA へ投函できないときに申請を閉じる門
# （platform-infra/reset-gate）が Admin REST を叩く機密クライアントの secret。
# **既定は realm import の置き場と同値**にする —— ズレると Keycloak の token 端点が invalid_client を返し、
# 門は 401 を打ち続けるだけになる（窓は開いたまま。wikijs-oidc と同じ罠）。
vexec "vault kv put secret/msp/reset-gate-oidc client-secret='${RESET_GATE_CLIENT_SECRET:-reset-gate-dev-secret-change-me}'"
# NFR-02, NFR-21, ADR-0076 決定 4, ADR-0079 決定 1, IADR-0378 (#1287): 合成監視のプローブが
# client_credentials で名乗る機密クライアントの secret。**既定は realm import の置き場と同値**にする ——
# ズレると token 端点が invalid_client を返し、プローブは 1 度も BFF へ到達しないまま
# `RagLatencySeriesAbsent` を鳴らす（原因が「評価対象が本当に無い」と区別できない。wikijs-oidc と同じ罠）。
# 種は無条件に入れる。ExternalSecret を apply するのは SYNTHETIC=1 のときだけである（k8s-local-up.sh）。
vexec "vault kv put secret/msp/synthetic-monitor-oidc client-secret='${SYNTHETIC_MONITOR_CLIENT_SECRET:-synthetic-monitor-dev-secret-change-me}'"
# IADR-0099 (#310) PR-4: 基盤 secret（postgres/rabbitmq/keycloak-admin）。★値は k8s-local-up.sh step 3 の手動 apply と
# **完全一致**させること（env 由来 or 同じ既定 postgres/guest/admin）。DB/broker/keycloak は既存パスワードで初期化済みのため、
# 値がズレると認証破壊。ExternalSecret は creationPolicy: Merge で同一値を上書きするのみ（値不変＝無害）。
vexec "vault kv put secret/msp/postgres password='${PG_PASSWORD:-postgres}'"
vexec "vault kv put secret/msp/rabbitmq username='${RABBITMQ_USER:-guest}' password='${RABBITMQ_PASSWORD:-guest}'"
vexec "vault kv put secret/msp/keycloak-admin password='${KEYCLOAK_ADMIN_PASSWORD:-admin}'"
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
  vkv_patch_nonempty msp/keycloak-smtp host "$smtp_host"
  vkv_patch_nonempty msp/keycloak-smtp port "${SMTP_PORT:-$smtp_port_default}"
  vkv_patch_nonempty msp/keycloak-smtp starttls "${SMTP_STARTTLS:-$smtp_starttls_default}"
  vkv_patch_nonempty msp/keycloak-smtp from "${SMTP_FROM:-}"
  vkv_patch_nonempty msp/keycloak-smtp user "${SMTP_USER:-}"
  vkv_patch_nonempty msp/keycloak-smtp password "${SMTP_PASSWORD:-}"
else
  vexec "vault kv put -cas=0 secret/msp/keycloak-smtp \
    host='$smtp_host' port='${SMTP_PORT:-$smtp_port_default}' starttls='${SMTP_STARTTLS:-$smtp_starttls_default}' \
    from='${SMTP_FROM:-}' user='${SMTP_USER:-}' password='${SMTP_PASSWORD:-}'"
fi

# SC-22, ADR-0095 決定 1, IADR-0456 決定 6 (#1477): AST が ESO で受ける ai-stock-trading/app-secrets（契約 #1477 の表）。
# **無いときだけ作る。在れば触らない**（画面で入れた外部 API キー・Discord ID を消さない。env での上書きも持たない —— 投入面は画面）。
# *-auth-client-* 8 件は realm（deploy/keycloak/microservices-platform-realm.json の機密クライアント）と**同値**の dev 既定
# （ズレると client_credentials が invalid_client になる）。画面から書ける 12 件は空文字（未設定＝各連携が no-op）。
# 🔴 ai-stock-trading/moomoo / moomoo-rsa は seed しない（未設定のあいだ OpenD は Secret 不在で待機する＝fail-closed）。
# 値の一致（キー集合＝items[] の書ける 12 ＋ notWritable 8、auth は realm と同値）は SecretItemBootstrapSeedTests が固定する。
if ! vkv_exists ai-stock-trading/app-secrets; then
  vexec "vault kv put -cas=0 secret/ai-stock-trading/app-secrets \
    finnhub-api-key='' marketdata-finnhub-api-key='' fred-api-key='' edinet-subscription-key='' \
    discord-webhook-url='' discord-bot-token='' discord-bot-killswitch-phrase='' \
    discord-bot-guild-id='' discord-bot-channel-id='' discord-bot-allowed-user-ids='' discord-bot-user-mapping='' \
    sec-edgar-user-agent='' \
    service-auth-client-id='ai-stock-trading-svc' service-auth-client-secret='dev-only-service-secret' \
    kb-auth-client-id='ai-stock-trading-kb-writer' kb-auth-client-secret='ai-stock-trading-kb-writer-dev-secret-change-me' \
    llm-auth-client-id='ai-stock-trading-llm-caller' llm-auth-client-secret='ai-stock-trading-llm-caller-dev-secret-change-me' \
    discord-owner-auth-client-id='ai-stock-trading-owner' discord-owner-auth-client-secret='dev-only-owner-secret'"
else
  # 在る KV にも *-auth-client-* 8 件を**無いものだけ**足す（画面が先に書いて作った KV には auth キーが無く、
  # そのままでは ast-secrets に auth キーが載らず AST のサービス間トークン取得が止まる。PR #1478 監査 D4）。値は上の seed と同値。
  vkv_patch_if_missing ai-stock-trading/app-secrets service-auth-client-id 'ai-stock-trading-svc'
  vkv_patch_if_missing ai-stock-trading/app-secrets service-auth-client-secret 'dev-only-service-secret'
  vkv_patch_if_missing ai-stock-trading/app-secrets kb-auth-client-id 'ai-stock-trading-kb-writer'
  vkv_patch_if_missing ai-stock-trading/app-secrets kb-auth-client-secret 'ai-stock-trading-kb-writer-dev-secret-change-me'
  vkv_patch_if_missing ai-stock-trading/app-secrets llm-auth-client-id 'ai-stock-trading-llm-caller'
  vkv_patch_if_missing ai-stock-trading/app-secrets llm-auth-client-secret 'ai-stock-trading-llm-caller-dev-secret-change-me'
  vkv_patch_if_missing ai-stock-trading/app-secrets discord-owner-auth-client-id 'ai-stock-trading-owner'
  vkv_patch_if_missing ai-stock-trading/app-secrets discord-owner-auth-client-secret 'dev-only-owner-secret'
fi

echo ""
echo "done. ExternalSecret が Vault→k8s Secret を同期する（refresh 1h。画面 /admin/secrets からの書き込みは BFF が force-sync で即時同期を依頼する）:"
echo "  #1477: SC-22 の KV（llm-provider-credentials / wikijs-sync / keycloak-smtp / ai-stock-trading/app-secrets）は無いときだけ作った（在るものは env が空でないキーだけ差し替えた）"
echo "  PR-1: llm-provider-credentials / PR-2: minio-credentials, wikijs-db, wikijs-sync"
echo "  PR-3: minio-oidc (MSP ns) / grafana-oidc, vault-oidc, headlamp-oidc (platform-infra ns)"
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
echo "  確認(MSP): kubectl -n microservices-platform get externalsecret,secret llm-provider-credentials minio-credentials postgres-app rabbitmq-app wikijs-db wikijs-sync minio-oidc bff-oidc identity-admin-oidc retrieval-service-token ingestion-service-token aianalysis-service-token graph-service-token conversion-service-token wiki-service-token datasource-service-token mcp-server-token document-service-token"
echo "  確認(infra): kubectl -n platform-infra get externalsecret,secret postgres rabbitmq keycloak-admin vault-oidc keycloak-smtp"
echo "             （grafana-oidc は OBSERVABILITY=1、headlamp-oidc は HEADLAMP=1 のときだけ apply される）"

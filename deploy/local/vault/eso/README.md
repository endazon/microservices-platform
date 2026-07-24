# Vault＋ESO による secret 自動供給（本番同等・k8s auth）— PR-1（IADR-0096・#310）

> 起点: [IADR-0096](../../../../docs/adr/IADR-0096_vault-eso-secret-supply-k8s-auth.md) /
> 作業仕様書 [`docs/specs/20260721_issue-310_vault-eso-secret-supply.md`](../../../../docs/specs/20260721_issue-310_vault-eso-secret-supply.md)

手動 `kubectl create secret`（`apply_secret`）を廃し、**Vault＋External Secrets Operator(ESO)** で secret を Pod へ
自動供給する本番同等構成の**第一歩（PR-1）**。`ESO=1`（`VAULT=1` 併用）で **`llm-provider-credentials` 1本を
end-to-end 疎通**する。認証は **kubernetes auth**（静的 root トークンを store に持たない）。既定（`ESO` 未設定）は
現行の手動 `apply_secret` のままバイト等価。

## 構成

| ファイル | 役割 |
| --- | --- |
| `../clustersecretstore.yaml` | `ClusterSecretStore vault-backend`（**token 認証**＝`VAULT=1` 既定・不変。既存フロー保護） |
| `clustersecretstore-k8s.yaml` | 同名 `vault-backend` の **kubernetes 認証版**（`ESO=1` で bootstrap 後に上書き適用） |
| `vault-auth-rbac.yaml` | vault の**専用 SA `vault`**（vault-dev.yaml で作成）に `system:auth-delegator`（TokenReview）。default SA には付与しない（blast radius 限定） |
| `policy-eso-read.hcl` | Vault policy `eso-read`（**MSP `secret/data/msp/*`＋AST `secret/data/ai-stock-trading/*`** の read・最小権限。store 共有のため両 path を許可） |
| `bootstrap.sh` | k8s auth の enable/config＋policy＋role `eso`＋seed（`kubectl exec`・runtime・再実行可） |
| `externalsecret-llm.yaml` | ExternalSecret（Vault `secret/msp/llm-provider-credentials` → 既存 Secret・同一キー・PR-1） |
| `externalsecret-minio.yaml` | ExternalSecret（`secret/msp/minio-credentials` → `minio-credentials` accessKey/secretKey・PR-2/IADR-0097） |
| `externalsecret-wikijs-db.yaml` | ExternalSecret（`secret/msp/wikijs-db` → `wikijs-db` password・PR-2/IADR-0097） |
| `externalsecret-wikijs-sync.yaml` | ExternalSecret（`secret/msp/wikijs-sync` → `wikijs-sync` apiKey・PR-2/IADR-0097） |
| `externalsecret-minio-oidc.yaml` | ExternalSecret（`secret/msp/minio-oidc` → `minio-oidc` client-secret・**MSP ns**・PR-3/IADR-0098） |
| `externalsecret-grafana-oidc.yaml` | ExternalSecret（`secret/msp/grafana-oidc` → `grafana-oidc` client-secret・**platform-infra ns**・PR-3/IADR-0098） |
| `externalsecret-vault-oidc.yaml` | ExternalSecret（`secret/msp/vault-oidc` → `vault-oidc` client-secret・**platform-infra ns**・PR-3/IADR-0098） |
| `externalsecret-headlamp-oidc.yaml` | ExternalSecret（`secret/msp/headlamp-oidc` → `headlamp-oidc` client-secret・**platform-infra ns**・PR-3/IADR-0098） |

## 有効化（opt-in・`ESO=1`・`VAULT=1` 併用）

```sh
VAULT=1 ESO=1 bash scripts/k8s-local-up.sh
```

`scripts/k8s-local-up.sh` は `ESO=1` のとき: (1) `helm upgrade --install external-secrets`（ESO 本体・CRD 同梱）、
(2) `vault-auth-rbac.yaml` 適用、(3) `bootstrap.sh`（k8s auth＋policy＋role＋seed）、(4) `eso/clustersecretstore-k8s.yaml`（store を kubernetes 認証へ上書き）適用、
(5) `externalsecret-llm.yaml` 適用 を行い、**`llm-provider-credentials` の手動 `apply_secret` はスキップ**する
（ExternalSecret が Secret を所有＝二重所有回避）。

## seed 値（**平文非コミット**）

`bootstrap.sh` の seed は **env 由来 or 空既定**（`ANTHROPIC_API_KEY`/`OPENAI_API_KEY`）:

```sh
ANTHROPIC_API_KEY=sk-... VAULT=1 ESO=1 bash scripts/k8s-local-up.sh
```

空既定＝外部 LLM を呼ばない（現行の空既定と同値・fail-safe）。**リポジトリに平文値は置かない**（gitleaks green）。
dev Vault はインメモリ（Recreate）＝Pod 再起動後は `bash deploy/local/vault/eso/bootstrap.sh` を再実行する。

## 確認 / 挙動

```sh
# PR-1: llm-provider-credentials / PR-2: minio-credentials, wikijs-db, wikijs-sync / PR-3: minio-oidc
kubectl -n microservices-platform get externalsecret,secret \
  llm-provider-credentials minio-credentials wikijs-db wikijs-sync minio-oidc
# PR-3: grafana-oidc / vault-oidc / headlamp-oidc は platform-infra ns（grafana/vault/headlamp と同居）
kubectl -n platform-infra get externalsecret,secret grafana-oidc vault-oidc headlamp-oidc
```

- ESO 同期は helm install（各 Pod 起動）後に走るため、対象 Secret は一時的に未作成で消費側 Pod が数秒
  `CreateContainerConfigError` になりうる（ESO 同期で自己回復）。消費側 `secretKeyRef`（llmgateway=ADR-0010・
  minio/wiki-js/OIDC secret 群も同様に optional 参照）は無改変。
- PR-3 の OIDC client secret 群（`minio-oidc`／`grafana-oidc`／`vault-oidc`／`headlamp-oidc`）は各機能ゲート
  （`OBSERVABILITY`/`VAULT`/`HEADLAMP`・minio-oidc は常時）で使うが、`ESO=1` のときは ExternalSecret が供給する
  ため各ゲート内の手動 apply はスキップする（二重所有回避）。ExternalSecret は namespaced だが
  `ClusterSecretStore` は cluster-scoped のため MSP／platform-infra 両 ns から同名 store を参照できる。
- role/policy 未作成・未 seed のうちは ESO は同期しない（fail-safe＝secret は供給されず外部 LLM 不使用）。
- **本番 `values.yaml`/chart は無改変**。ESO は経路B opt-in オーバーレイに限定（SIMULATE/実弾 OFF 不変）。

## AST の Vault 連携との併用

store `vault-backend` は MSP と **AST**（`externalSecrets.secretStoreRef.name=vault-backend`）で共有する。`ESO=1` で
store を kubernetes 認証（role `eso`）へ上書きしても、policy `eso-read` が **MSP＋AST 両 path**（`secret/data/{msp,
ai-stock-trading}/*`）を read 許可するため、AST の ExternalSecret 同期は壊れない。AST 側の値は AST の runbook に従って
Vault へ seed する（本 policy は read のみ・write は付与しない）。

## 切り戻し（ESO を無効化する場合）

`ESO` 未設定で再実行すると `deploy/local/vault/clustersecretstore.yaml`（token 認証）が再適用され store は token 認証へ戻る。
ただし **各 ExternalSecret（`creationPolicy: Owner`）は残存**し対象 Secret を所有し続けるため、手動 `apply_secret` 経路へ
完全に戻すには先に **全 ExternalSecret** を削除する（二重所有回避）:

```sh
kubectl -n microservices-platform delete externalsecret \
  llm-provider-credentials minio-credentials wikijs-db wikijs-sync minio-oidc
kubectl -n platform-infra delete externalsecret grafana-oidc vault-oidc headlamp-oidc
# 以降 ESO 未設定で再実行すると手動 apply_secret が Secret を作成する。
```

## 段階移行（後続 PR）

- **PR-1（IADR-0096）**: `llm-provider-credentials`（疎通・ESO 基盤）。
- **PR-2（IADR-0097）**: `minio-credentials`／`wikijs-db`／`wikijs-sync`。
- **PR-3（IADR-0098）**: OIDC client secret 群 `minio-oidc`／`grafana-oidc`／`vault-oidc`／`headlamp-oidc`（本 PR）。
- **PR-4 以降**: 基盤 secret（postgres/rabbitmq/keycloak-admin）。
- 除外: `vault-dev-token`（Vault root・chicken-egg）／`argocd-secret`（argocd 所有・merge patch）／AST secrets（AST リポ管轄）。

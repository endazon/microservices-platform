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
| `externalsecret-postgres.yaml` | ExternalSecret（`secret/msp/postgres` → `postgres` password・**platform-infra ns**・**creationPolicy: Merge**・PR-4/IADR-0099） |
| `externalsecret-rabbitmq.yaml` | ExternalSecret（`secret/msp/rabbitmq` → `rabbitmq` password・**platform-infra ns**・**Merge**・PR-4/IADR-0099） |
| `externalsecret-keycloak-admin.yaml` | ExternalSecret（`secret/msp/keycloak-admin` → `keycloak-admin` password・**platform-infra ns**・**Merge**・PR-4/IADR-0099） |

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
# PR-3/PR-4: platform-infra ns。基盤 postgres/rabbitmq/keycloak-admin（PR-4・常時）＋ vault-oidc（常時）＋
# grafana-oidc（OBSERVABILITY=1）／headlamp-oidc（HEADLAMP=1）のときだけ供給（無効ゲートの secret は未作成＝NotFound で正常）。
kubectl -n platform-infra get externalsecret,secret postgres rabbitmq keycloak-admin vault-oidc grafana-oidc headlamp-oidc
```

- ESO 同期は helm install（各 Pod 起動）後に走るため、対象 Secret は一時的に未作成で消費側 Pod が数秒
  `CreateContainerConfigError` になりうる（ESO 同期で自己回復）。消費側 `secretKeyRef`（llmgateway=ADR-0010・
  minio/wiki-js/OIDC secret 群も同様に optional 参照）は無改変。
- PR-3 の OIDC client secret 群（`minio-oidc`／`grafana-oidc`／`vault-oidc`／`headlamp-oidc`）は各機能ゲート
  （`OBSERVABILITY`/`VAULT`/`HEADLAMP`・minio-oidc は常時）で使うが、`ESO=1` のときは ExternalSecret が供給する
  ため各ゲート内の手動 apply はスキップする（二重所有回避）。ExternalSecret の apply も**元のゲート意味論に整合**させる:
  `minio-oidc` は常時、`vault-oidc` は VAULT 前提（`ESO=1` のガード下で常に真）で常時、`grafana-oidc`／`headlamp-oidc`
  は `OBSERVABILITY`／`HEADLAMP` 有効時のみ（機能オフ時に未使用 Secret を残さない）。ExternalSecret は namespaced だが
  `ClusterSecretStore` は cluster-scoped のため MSP／platform-infra 両 ns から同名 store を参照できる。
- **PR-4 基盤 secret（`postgres`／`rabbitmq`／`keycloak-admin`）は PR-1〜3 と扱いが異なる**。これらは step [4/7] の
  infra rollout（ブロッキング）で **非 optional** に消費されるため、Vault/ESO がまだ無いこの時点で手動作成が必須
  （bootstrap）。よって **`ESO=1` でも手動 apply をスキップしない**。ExternalSecret は **`creationPolicy: Merge`** で
  既存 Secret に **同一値を上書きするだけ**（所有・再作成しない）。seed 値は step 3 の手動 apply と**完全一致**
  （`PG_PASSWORD`/`RABBITMQ_PASSWORD`/`KEYCLOAK_ADMIN_PASSWORD` の env 由来 or 既定 `postgres`/`guest`/`admin`）の
  ため、値不変＝**Pod 再起動や PVC 初期化済み DB のパスワード不整合を起こさない**。本番同等の Vault→ESO 供給経路を
  配線しつつローカル bootstrap を壊さない設計。
- role/policy 未作成・未 seed のうちは ESO は同期しない（fail-safe＝secret は供給されず外部 LLM 不使用）。
- **本番 `values.yaml`/chart は無改変**。ESO は経路B opt-in オーバーレイに限定（SIMULATE/実弾 OFF 不変）。

## AST の Vault 連携との併用

store `vault-backend` は MSP と **AST**（`externalSecrets.secretStoreRef.name=vault-backend`）で共有する。`ESO=1` で
store を kubernetes 認証（role `eso`）へ上書きしても、policy `eso-read` が **MSP＋AST 両 path**（`secret/data/{msp,
ai-stock-trading}/*`）を read 許可するため、AST の ExternalSecret 同期は壊れない。AST 側の値は AST の runbook に従って
Vault へ seed する（本 policy は read のみ・write は付与しない）。

## 切り戻し（ESO を無効化する場合）

`ESO` 未設定で再実行すると `deploy/local/vault/clustersecretstore.yaml`（token 認証）が再適用され store は token 認証へ戻る。
ただし PR-1〜3 の **各 ExternalSecret（`creationPolicy: Owner`）は残存**し対象 Secret を所有し続けるため、手動 `apply_secret`
経路へ完全に戻すには先に **それらを削除**する（二重所有回避）。PR-4 の基盤 secret は `creationPolicy: Merge` で Secret を
所有しないため、ExternalSecret を削除しても手動作成済みの Secret は残る（削除は任意・安全）:

```sh
kubectl -n microservices-platform delete externalsecret \
  llm-provider-credentials minio-credentials wikijs-db wikijs-sync minio-oidc
kubectl -n platform-infra delete externalsecret \
  grafana-oidc vault-oidc headlamp-oidc postgres rabbitmq keycloak-admin
# 以降 ESO 未設定で再実行すると手動 apply_secret が Secret を作成する（基盤は元々手動作成のまま）。
```

## 段階移行（後続 PR）

- **PR-1（IADR-0096）**: `llm-provider-credentials`（疎通・ESO 基盤）。
- **PR-2（IADR-0097）**: `minio-credentials`／`wikijs-db`／`wikijs-sync`。
- **PR-3（IADR-0098）**: OIDC client secret 群 `minio-oidc`／`grafana-oidc`／`vault-oidc`／`headlamp-oidc`。
- **PR-4（IADR-0099）**: 基盤 secret `postgres`／`rabbitmq`／`keycloak-admin`（**creationPolicy: Merge**・手動 apply 保持・本 PR）。
  → これで #310 の secret 移行は一巡（PR-1〜4）。
- 除外: `vault-dev-token`（Vault root・chicken-egg）／`argocd-secret`（argocd 所有・merge patch）／AST secrets（AST リポ管轄）。

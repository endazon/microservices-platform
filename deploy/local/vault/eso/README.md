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
| `vault-auth-rbac.yaml` | vault の SA(`platform-infra/default`) に `system:auth-delegator`（TokenReview） |
| `policy-msp-read.hcl` | Vault policy `msp-read`（`secret/data/msp/*` read・最小権限） |
| `bootstrap.sh` | k8s auth の enable/config＋policy＋role `eso`＋seed（`kubectl exec`・runtime・再実行可） |
| `externalsecret-llm.yaml` | ExternalSecret（Vault `secret/msp/llm-provider-credentials` → 既存 Secret・同一キー） |

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
kubectl -n microservices-platform get externalsecret,secret llm-provider-credentials
```

- ESO 同期は helm install（llmgateway 起動）後に走るため、`llm-provider-credentials` は一時的に未作成で
  llmgateway Pod が数秒 `CreateContainerConfigError` になりうる（ESO 同期で自己回復）。消費側 `secretKeyRef`（ADR-0010）は
  無改変。
- role/policy 未作成・未 seed のうちは ESO は同期しない（fail-safe＝secret は供給されず外部 LLM 不使用）。
- **本番 `values.yaml`/chart は無改変**。ESO は経路B opt-in オーバーレイに限定（SIMULATE/実弾 OFF 不変）。

## 段階移行（後続 PR）

PR-2 以降で `minio-credentials`／`wikijs-db`／`wikijs-sync` → OIDC client secret 群 → 基盤（postgres 等）を同パターンで
移行する。除外: `vault-dev-token`（Vault root・chicken-egg）／`argocd-secret`（argocd 所有・merge patch）。

---
title: Vault＋ESO で secret を Pod へ自動供給（本番同等・k8s auth）PR-1: llm-provider-credentials 疎通（Issue #310）
type: spec
status: done
related_ids:
  - ADR-0006
  - ADR-0010
  - IADR-0077
  - IADR-0087
  - IADR-0094
  - IADR-0096
author: claude
created: 2026-07-21
updated: 2026-07-21
related_specs:
  - "../adr/IADR-0096_vault-eso-secret-supply-k8s-auth.md"
  - "../adr/IADR-0077_local-observability-vault-gitops-overlays.md"
  - "../adr/IADR-0094_vault-keycloak-oidc.md"
  - "../../deploy/local/vault/clustersecretstore.yaml"
  - "../../deploy/local/vault/eso/README.md"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: Vault＋ESO 本番同等 secret 供給 PR-1（Issue #310）

## 起点となる計画書（トレーサビリティ）

- ADR: ADR-0006（運用基盤）/ ADR-0010（LLM ルーティング＝llm-provider-credentials の消費元）。Vault dev/ESO の
  opt-in オーバーレイ統括は [[IADR-0077]]、Vault の runtime bootstrap 先例は [[IADR-0094]]、ゲート smoke test は [[IADR-0087]]。
- 決定: 本作業の設計判断は [[IADR-0096]]（ESO＋Vault k8s auth・段階移行・fail-safe）。
- Issue: #310（Vault/ESO 本番同等化・親トラッカー）。

## 背景と問題

現状、MSP の secret はすべて `k8s-local-up.sh` の手動 `apply_secret`（`kubectl create secret`）で供給し、Pod は
`secretKeyRef` で消費する。手動 patch/apply を廃し、**Vault＋External Secrets Operator(ESO)** で secret を Pod へ
自動供給する本番同等構成にする（#310）。ESO の `ClusterSecretStore vault-backend` は既存（[[IADR-0077]]・token 認証）だが、
Vault は未 seed で MSP secret の ExternalSecret も未整備。**PR-1 は 1 secret（`llm-provider-credentials`）で end-to-end 疎通**する。

## 受け入れ基準（PR-1）

1. **opt-in `ESO=1`（`VAULT=1` 併用）**: 既定（`ESO` 未設定）は現行の手動 `apply_secret` のまま**バイト等価**（fail-safe）。
2. `ESO=1` で ESO 本体を install（`helm upgrade --install external-secrets`）し、`ClusterSecretStore vault-backend` を
   **k8s auth**（既存 token 認証から移行）で適用、`llm-provider-credentials` の **ExternalSecret** で Vault
   `secret/msp/llm-provider-credentials` → 既存 Secret 名・**同一キー**（anthropic-api-key/openai-api-key）へ供給する。
3. **Vault k8s auth bootstrap（runtime）**: kubernetes 認証を有効化・config、policy `msp-read`（`secret/data/msp/*` read）、
   role `eso`（ESO の SA `external-secrets`/ns `external-secrets` に束縛）。Vault SA に TokenReview 権限（auth-delegator）。
   [[IADR-0094]] と同型の runtime（再実行可）。
4. **seed（平文非コミット）**: dev 値は env 由来 or **空既定**（`ANTHROPIC_API_KEY`/`OPENAI_API_KEY`）で Vault へ投入
   （空＝外部 LLM を呼ばない＝現行と同じ fail-safe）。リポジトリに平文値を置かない（gitleaks green）。
5. **二重所有回避**: `ESO=1` 時は `llm-provider-credentials` の手動 `apply_secret` を**スキップ**し ExternalSecret に委譲。
6. **本番 byte 等価**: 本番 `values.yaml`/chart・消費側 `secretKeyRef` は無改変（ESO は経路B opt-in オーバーレイに限定）。
7. CI 緑: `k8s-local-up.test.js`（`ESO=1` 分岐＋既定バイト等価）・`doc-links`・`check-image-mapping`(#275)・**gitleaks**。

## 対応方針（変更範囲・PR-1）

- **`deploy/local/vault/clustersecretstore.yaml`**: **token 認証のまま不変**（`VAULT=1` 単独＝既存フロー保護・byte 等価）。
  **`deploy/local/vault/eso/clustersecretstore-k8s.yaml`（新）**: 同名 `vault-backend` の kubernetes 認証版を `ESO=1` で
  bootstrap 後に上書き適用（k8s auth backend 未設定なのに store が k8s auth という不整合＝VAULT=1 単独破壊を回避）。
- **`deploy/local/vault/eso/`（新）**: `externalsecret-llm.yaml`（ExternalSecret）・`vault-auth-rbac.yaml`（vault SA→auth-delegator）・
  `bootstrap.sh`（k8s auth enable/config＋policy＋role＋seed・`kubectl exec` 経由・再実行可・平文非コミット）・`README.md`。
- **`scripts/k8s-local-up.sh`**: `ESO=1` ブロック（helm install ESO＋RBAC＋bootstrap＋ExternalSecret apply）＋
  `llm-provider-credentials` の手動 apply を `ESO=1` 時スキップ。
- **回帰（TDD）**: `k8s-local-up.test.js` に (a) 既定で `llm-provider-credentials` 手動 apply 有・ESO 由来リソース不在、
  (b) `ESO=1` で external-secrets install＋ExternalSecret apply＋手動 apply スキップ。

## 非対象（後続 PR）

- PR-2 以降: minio-credentials / wikijs-db / wikijs-sync → OIDC client secret 群 → 基盤（postgres 等）。
- 除外: `vault-dev-token`（Vault root・chicken-egg）／`argocd-secret`（argocd 所有・merge patch）／AST secrets（AST リポ管轄）。
- 実 secret 同期の live 確認（稼働 k3d/ESO webhook/Vault seed 済み前提）。

## 検証

- `node scripts/k8s-local-up.test.js` / `node scripts/check-doc-links.js` / `node scripts/check-image-mapping.js`
- `bash -n deploy/local/vault/eso/bootstrap.sh` / `kubectl apply --dry-run=client -f`（ExternalSecret/RBAC は CRD 依存のため build 妥当性）
- gitleaks（平文 secret なし）

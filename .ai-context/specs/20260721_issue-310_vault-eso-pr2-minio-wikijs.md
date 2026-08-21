---
title: Vault＋ESO secret 供給 PR-2: minio-credentials/wikijs-db/wikijs-sync を ExternalSecret 化（Issue #310）
type: spec
status: done
related_ids:
  - ADR-0006
  - IADR-0077
  - IADR-0096
  - IADR-0097
author: claude
created: 2026-07-21
updated: 2026-07-21
related_specs:
  - "../adr/IADR-0097_vault-eso-secret-supply-pr2.md"
  - "../adr/IADR-0096_vault-eso-secret-supply-k8s-auth.md"
  - "../../deploy/local/vault/eso/README.md"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: Vault＋ESO secret 供給 PR-2（Issue #310）

## 起点となる計画書（トレーサビリティ）

- ADR: ADR-0006（運用基盤）。ESO 基盤・k8s auth・段階移行は [IADR-0096](../adr/IADR-0096_vault-eso-secret-supply-k8s-auth.md)（PR-1）。opt-in オーバーレイ統括は [IADR-0077](../adr/IADR-0077_local-observability-vault-gitops-overlays.md)。
- 決定: 本作業の設計判断は [IADR-0097](../adr/IADR-0097_vault-eso-secret-supply-pr2.md)（PR-2 対象 secret の ExternalSecret 化・PR-1 設計踏襲）。
- Issue: #310（Vault/ESO 本番同等化）。**stacked PR**（#368/PR-1 のブランチに積む）。

## 背景と問題

[IADR-0096](../adr/IADR-0096_vault-eso-secret-supply-k8s-auth.md)（PR-1）で ESO 基盤（k8s auth・store 上書き・policy `eso-read`・seed/skip）を敷き、`llm-provider-credentials`
1本を疎通した。PR-2 は同じパターンで **`minio-credentials`・`wikijs-db`・`wikijs-sync`** を ExternalSecret 供給へ移行する。

## 受け入れ基準（PR-2）

1. `ESO=1` で 3 secret（`minio-credentials`（accessKey/secretKey）・`wikijs-db`（password）・`wikijs-sync`（apiKey））を
   Vault `secret/msp/<name>` → **既存 Secret 名・同一キー**（消費側 `secretKeyRef` 不変）へ ExternalSecret 供給する。
2. `ESO=1` 時はこれら 3 secret の**手動 `apply_secret` をスキップ**（ExternalSecret が Secret 所有＝二重所有回避）。
   **`VAULT=1` 単独（`ESO` 未設定）は手動 apply のままバイト等価**（PR-1 と同じ fail-safe）。
3. **seed**: `bootstrap.sh` に 3 secret の投入を追加。値は **env 由来 or dev プレースホルダ**（現行 apply_secret と同一既定＝
   `minioadmin`/`kp`/空）で **平文の実 secret を置かない**（gitleaks green）。
4. **policy 充足の自己チェック**: 3 secret は `secret/msp/*` 配下＝PR-1 の policy `eso-read`（`secret/data/msp/*` read）で
   カバー済み（policy 追加不要・AST path も既に許可）。
5. **本番/既存無改変**: 本番 `values.yaml`/chart・消費側 `secretKeyRef`・realm は無改変。store は既定 token 認証のまま
   （`ESO=1` で k8s 上書きは PR-1 のまま）。VAULT=1 単独破壊なし。
6. CI 緑: `k8s-local-up.test.js`（3 ExternalSecret 出現／VAULT=1 単独は手動 apply）・`doc-links`・`check-image-mapping`(#275)・gitleaks。

## 対応方針（変更範囲・PR-2）

- **`deploy/local/vault/eso/externalsecret-{minio,wikijs-db,wikijs-sync}.yaml`（新）**: Vault path→既存 Secret・同一キー。
- **`deploy/local/vault/eso/bootstrap.sh`**: 3 secret の seed（`vault kv put secret/msp/<name> ...`・env/既定・平文非コミット）を追加。
- **`scripts/k8s-local-up.sh`**: `ESO=1` 時に 3 secret の手動 apply をスキップ＋ESO ブロックで 3 ExternalSecret を apply。
- **回帰（TDD）**: `k8s-local-up.test.js` に (a) 既定=3 secret 手動 apply 有、(b) `ESO=1`=3 ExternalSecret apply＋手動 skip。
- **docs**: `eso/README.md`（対象一覧）／IADR-0097＋索引。

## 非対象（後続 PR）

- PR-3: OIDC client secret 群（grafana-oidc/minio-oidc/headlamp-oidc/vault-oidc）。PR-4: 基盤（postgres/rabbitmq/keycloak-admin）。
- 除外: `vault-dev-token`（root）／`argocd-secret`（merge patch）／AST secrets（AST リポ管轄）。

## 検証

- `node scripts/k8s-local-up.test.js` / `node scripts/check-doc-links.js` / `node scripts/check-image-mapping.js`
- `bash -n deploy/local/vault/eso/bootstrap.sh` / gitleaks（平文 secret なし）

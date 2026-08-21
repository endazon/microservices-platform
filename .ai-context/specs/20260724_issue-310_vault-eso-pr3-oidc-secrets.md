---
title: Vault＋ESO secret 供給 PR-3: OIDC client secret 群（minio/grafana/vault/headlamp-oidc）を ExternalSecret 化（Issue #310）
type: spec
status: done
related_ids:
  - ADR-0006
  - IADR-0077
  - IADR-0096
  - IADR-0097
  - IADR-0098
author: claude
created: 2026-07-24
updated: 2026-07-24
related_specs:
  - "../adr/IADR-0098_vault-eso-secret-supply-pr3.md"
  - "../adr/IADR-0097_vault-eso-secret-supply-pr2.md"
  - "../adr/IADR-0096_vault-eso-secret-supply-k8s-auth.md"
  - "../../deploy/local/vault/eso/README.md"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: Vault＋ESO secret 供給 PR-3（Issue #310）

## 起点となる計画書（トレーサビリティ）

- ADR: ADR-0006（運用基盤）。ESO 基盤・k8s auth・段階移行は [IADR-0096](../adr/IADR-0096_vault-eso-secret-supply-k8s-auth.md)（PR-1）／[IADR-0097](../adr/IADR-0097_vault-eso-secret-supply-pr2.md)（PR-2）。opt-in 統括は [IADR-0077](../adr/IADR-0077_local-observability-vault-gitops-overlays.md)。
- 決定: 本作業の設計判断は [IADR-0098](../adr/IADR-0098_vault-eso-secret-supply-pr3.md)（PR-3 対象 secret の ExternalSecret 化・PR-1/PR-2 設計踏襲）。
- 対象 OIDC の各機能連携: MinIO=[IADR-0093](../adr/IADR-0093_minio-keycloak-oidc.md)／Grafana=[IADR-0090](../adr/IADR-0090_grafana-keycloak-oidc-generic-oauth.md)／Vault=[IADR-0094](../adr/IADR-0094_vault-keycloak-oidc.md)／Headlamp=[IADR-0080](../adr/IADR-0080_headlamp-k8s-management-ui.md)。
- Issue: #310（Vault/ESO 本番同等化）。develop 最新（PR-1/PR-2 反映済み）ベース。

## 背景と問題

[IADR-0096](../adr/IADR-0096_vault-eso-secret-supply-k8s-auth.md)（PR-1）で ESO 基盤を敷き、[IADR-0097](../adr/IADR-0097_vault-eso-secret-supply-pr2.md)（PR-2）で minio/wikijs 系を移行した。PR-3 は同じパターンで
**OIDC client secret 群** `minio-oidc`・`grafana-oidc`・`vault-oidc`・`headlamp-oidc`（各キー `client-secret`）を
ExternalSecret 供給へ移行する。

## 受け入れ基準（PR-3）

1. `ESO=1` で 4 secret を Vault `secret/msp/<name>` → **既存 Secret 名・同一キー `client-secret`**（消費側 `secretKeyRef`
   不変）へ ExternalSecret 供給する。`minio-oidc` は MSP ns、`grafana-oidc`／`vault-oidc`／`headlamp-oidc` は
   platform-infra ns（ClusterSecretStore は cluster-scoped のため両 ns から参照可）。
2. `ESO=1` 時は各機能ゲート内の**手動 `apply_secret` をスキップ**（ExternalSecret が Secret 所有＝二重所有回避）。
   **`ESO` 未設定は手動 apply のままバイト等価**（PR-1/PR-2 と同じ fail-safe）。
3. **seed**: `bootstrap.sh` に 4 secret の投入を追加。値は **env 由来 or dev プレースホルダ**（現行 apply_secret と同一既定＝
   `<tool>-dev-secret-change-me`）で **平文の実 secret を置かない**（gitleaks green）。
4. **policy 充足の自己チェック**: 4 secret は `secret/msp/*` 配下＝PR-1 の policy `eso-read`（`secret/data/msp/*` read）で
   カバー済み（policy 追加不要・AST path 無改変）。
5. **`VAULT=1` 単独破壊なしの自己チェック**: `ESO` 未設定では ESO ブロック不実行＝手動 apply のまま（byte 等価）。store 上書き
   （`clustersecretstore-k8s.yaml`）は `ESO=1` のみ。本番 `values.yaml`/chart・消費側 `secretKeyRef`・realm は無改変。
6. CI 緑: `k8s-local-up.test.js`（4 OIDC ExternalSecret 出現／各ゲート有効かつ ESO 未設定では手動 apply）・`doc-links`・
   `check-image-mapping`(#275)・gitleaks。

## 対応方針（変更範囲・PR-3）

- **`deploy/local/vault/eso/externalsecret-{minio,grafana,vault,headlamp}-oidc.yaml`（新）**: Vault path→既存 Secret・同一キー・
  対象 ns（minio-oidc=MSP・他=platform-infra）。
- **`deploy/local/vault/eso/bootstrap.sh`**: 4 secret の seed（`vault kv put secret/msp/<name> client-secret=...`・env/既定・
  平文非コミット）を追加＋完了メッセージを更新。
- **`scripts/k8s-local-up.sh`**: 各機能ゲート内の 4 手動 apply を `ESO=1` でスキップ＋ESO ブロックで 4 ExternalSecret を apply＋
  完了メッセージを更新。
- **回帰（TDD）**: `k8s-local-up.test.js` に (a) `ESO=1`（全ゲート有効）=4 OIDC ExternalSecret apply＋手動 skip、
  (b) 既定（各ゲート有効・ESO 未設定）=4 手動 apply 有＋OIDC ExternalSecret 不在。
- **docs**: `eso/README.md`（対象一覧・確認・切り戻し・段階移行）／IADR-0098＋索引。

## 非対象（後続 PR）

- PR-4: 基盤 secret（postgres/rabbitmq/keycloak-admin）。**PR-3 まで出したら停止しマージを待つ**。
- 除外: `vault-dev-token`（root）／`argocd-secret`（merge patch）／AST secrets（AST リポ管轄）。

## 検証

- `node scripts/k8s-local-up.test.js` / `node scripts/check-doc-links.js` / `node scripts/check-image-mapping.js`
- `bash -n scripts/k8s-local-up.sh` / `bash -n deploy/local/vault/eso/bootstrap.sh` / gitleaks（平文 secret なし）

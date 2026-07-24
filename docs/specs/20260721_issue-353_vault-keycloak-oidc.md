---
title: 経路B Vault(dev) を Keycloak OIDC 認証メソッドで SSO 連携する（Issue #353・SSO その4）
type: spec
status: done
related_ids:
  - ADR-0006
  - IADR-0077
  - IADR-0087
  - IADR-0090
  - IADR-0091
  - IADR-0094
author: claude
created: 2026-07-21
updated: 2026-07-21
related_specs:
  - "../adr/IADR-0094_vault-keycloak-oidc.md"
  - "../adr/IADR-0077_local-observability-vault-gitops-overlays.md"
  - "../adr/IADR-0090_grafana-keycloak-oidc-generic-oauth.md"
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "../../deploy/local/vault/vault-dev.yaml"
  - "../../deploy/local/vault/oidc/README.md"
  - "../../deploy/keycloak/microservices-platform-realm.json"
  - "../../scripts/k8s-local-up.sh"
---

# 仕様書: 経路B Vault(dev) の Keycloak OIDC 連携（Issue #353 子タスク 4）

## 起点となる計画書（トレーサビリティ）

- ADR: ADR-0006（運用基盤）。Vault dev opt-in オーバーレイは [[IADR-0077]]、OIDC 連携先例 [[IADR-0090]]（Grafana）、
  エッジ集約 [[IADR-0091]]、ゲート横断 smoke test [[IADR-0087]]。
- 決定: 本作業の設計判断は [[IADR-0094]]（Vault OIDC auth method・fail-safe policy・runtime bootstrap）。
- Issue: #353（親・enhancement）の子タスク 4（Vault）。あわせて #356（エッジ集約）。

## 背景と問題

経路B の dev Vault（`VAULT=1` の opt-in・`hashicorp/vault -dev`・インメモリ・unseal 不要）は root トークンのみで
Keycloak SSO に載っていない。#353 方針で OIDC 認証メソッドを配線する。Vault は #357 のエッジ集約に**既に含まれ**
（`vault.localhost:50000`→`vault:8200` の Ingress は #357 で追加済み）＝**edge Ingress は無改変**。

## 受け入れ基準

1. Vault UI/CLI が Keycloak OIDC でログインできる（realm client `vault` ＋ Vault `auth/oidc` の config/role）。
2. **集約後 URL・ホスト名ベース**（#357 既存）: redirect は `http(s)://vault.localhost:50000/ui/vault/auth/oidc/oidc/callback`
   （UI・edge admin:50000 は現状 http のため http/https 両登録）＋`http://localhost:8250/oidc/callback`（CLI）。
3. **fail-safe policy**: OIDC role の既定 policy は `default`（最小・secret アクセス無し）。`platform-admin`/`platform-operator`
   は Vault **external group**（realm ロール `groups` クレーム）経由で `admin`/`operator` policy を得る。未マッピングは
   `default` のみ＝no secret access（deny 相当）。root トークンは break-glass として残る。
4. **秘密の非平文**: realm export の secret はプレースホルダ。OIDC client secret は Secret `vault-oidc`（`k8s-local-up.sh`
   が `VAULT=1` 時に dev 既定 or `VAULT_OIDC_CLIENT_SECRET` env で作成）から bootstrap が読む（平文コミットなし）。
5. **opt-in・byte 等価**: OIDC は runtime bootstrap（vault CLI）＝`vault-dev.yaml` は無改変。`VAULT=1` 時に
   `vault-oidc` secret を追加作成するのみ（既定 `VAULT` 未設定時は挙動不変）。grafana/argocd/minio・他 edge route は無改変。
6. **issuer 一致**: `oidc_discovery_url` は `http://keycloak:8080/realms/microservices-platform`（platform-infra 内・
   browser は #284 手順A）。

## 対応方針（変更範囲）

- **realm `vault` client**（confidential・追加のみ）: レルムロールを `groups` クレーム（id_token/userinfo・multivalued）へ
  発行する protocolMapper。redirects（UI http/https ＋ CLI）。
- **`scripts/k8s-local-up.sh`**: `VAULT=1` ブロックで `vault-oidc`（client-secret）secret を作成。
- **`deploy/local/vault/oidc/`（新）**: `bootstrap.sh`（`auth/oidc` enable/config/role＋policy＋external group/alias）、
  `policies/admin.hcl`・`policies/operator.hcl`、`README.md`（runtime 手順）。
- **回帰（TDD）**: `k8s-local-up.test.js` に `VAULT=1` で `vault-oidc` secret 作成を追加。
- vault-dev.yaml は無改変（OIDC は runtime）。

## 非対象

- 他ツール（Wiki.js）OIDC（#353 別子タスク）。本番 Vault 化（unseal/監査/HA・IADR-0077 の Tier 3）。
- 実ブラウザ SSO ログイン疎通（稼働 k3d/edge/手順A・bootstrap 適用済み前提＝live）。

## 検証

- `node scripts/check-realm-constraints.js --self-test && node scripts/check-realm-constraints.js`
- realm JSON 妥当性／`bootstrap.sh` の bash 構文（`bash -n`）／policy HCL 存在
- `node scripts/k8s-local-up.test.js` / `node scripts/check-doc-links.js` / `node scripts/check-image-mapping.js`

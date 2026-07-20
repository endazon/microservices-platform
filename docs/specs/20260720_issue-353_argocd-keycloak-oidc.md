---
title: 経路B ArgoCD を Keycloak OIDC(SSO) 連携する（Issue #353・SSO 一括連携 その2・エッジ集約 URL 前提）
type: spec
status: done
related_ids:
  - ADR-0006
  - IADR-0066
  - IADR-0077
  - IADR-0087
  - IADR-0090
  - IADR-0091
  - IADR-0092
author: claude
created: 2026-07-20
updated: 2026-07-20
related_specs:
  - "../adr/IADR-0092_argocd-keycloak-oidc.md"
  - "../adr/IADR-0077_local-observability-vault-gitops-overlays.md"
  - "../adr/IADR-0090_grafana-keycloak-oidc-generic-oauth.md"
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "../../deploy/local/argocd/README.md"
  - "../../deploy/keycloak/microservices-platform-realm.json"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: 経路B ArgoCD の Keycloak OIDC 連携（Issue #353 子タスク 2）

## 起点となる計画書（トレーサビリティ）

- ADR: ADR-0006（CI/CD・GitOps）。ArgoCD ブートストラップ統括は [[IADR-0077]]、Keycloak OIDC 連携の先例は
  [[IADR-0090]]（Grafana）、エッジ集約（集約後 URL）は [[IADR-0091]]、opt-in ゲート smoke test は [[IADR-0087]]。
- 決定: 本作業の設計判断は [[IADR-0092]]（ArgoCD OIDC・dex 不使用・ホスト名ベース集約 URL・fail-safe local admin）。
- Issue: #353（親・enhancement）「経路B ローカルツールの Keycloak SSO 一括連携」の**子タスク 2（ArgoCD）**。

## 背景と問題

経路B の ArgoCD（`ARGOCD=1` で公式 install.yaml を適用）は現状 **local admin（`argocd-initial-admin-secret`）** のみで
Keycloak SSO に載っていない。#353 方針に従い ArgoCD も realm の OIDC に載せる。エッジ集約（#357/IADR-0091）が
マージ済みのため、**最初から集約後 URL（`http://argocd.localhost:50000`・ホスト名ベース）で登録**する。

## 受け入れ基準

1. ArgoCD が Keycloak(`microservices-platform` realm) の OIDC でログインできる（realm client `argocd` ＋
   `argocd-cm` の `oidc.config`）。**dex は使わない**（`oidc.config` 直接指定＝dex 無効）。
2. **集約後 URL・ホスト名ベース**: redirect は `http://argocd.localhost:50000/auth/callback`（＋ port-forward 用
   `http://localhost:8083/auth/callback` を併記）。`server.rootpath`（サブパス）は**使わない**（#357 の方針）。
   `argocd-cm.url` は `http://argocd.localhost:50000`。edge の平文 http のため `server.insecure=true`。
3. **fail-safe**: local admin を残す（OIDC は追加）。RBAC 未マッピングの OIDC ユーザーは `policy.default=''`＝no-access
   （Admin へ昇格しない）。`platform-admin`→`role:admin`、`platform-operator`→`role:readonly`。
4. **秘密の非平文**: realm export の secret はプレースホルダ（`argocd-dev-secret-change-me`）。ArgoCD へは
   `argocd-secret` に **merge patch**（既存 `server.secretkey` 等を保持）で `oidc.keycloak.clientSecret` を注入
   （dev 既定 or `ARGOCD_OIDC_CLIENT_SECRET` env・平文コミットなし）。
5. **ArgoCD 単体**: realm は `argocd` client の**追加のみ**。`grafana`/他 client・`grafana.yaml`・edge overlay は不変。
6. **issuer 一致**: `http://keycloak:8080/realms/microservices-platform`（手順A）。
7. CI 緑: `check-realm-constraints`（description ≤255）・`k8s-local-up.test.js`（ARGOCD OIDC 配線の回帰）・
   `doc-links`・`check-image-mapping`(#275・image 不変)。

## 対応方針（変更範囲）

`deploy/keycloak/microservices-platform-realm.json`（`argocd` client 追加）・`deploy/local/argocd/oidc/`（新・CM patch）・
`scripts/k8s-local-up.sh`（ARGOCD ブロックで OIDC 配線）・`scripts/k8s-local-up.test.js`（回帰）・README・`docs/adr`
（IADR-0092＋索引）に閉じる。

1. **realm client `argocd`（confidential・追加のみ）**: `standardFlowEnabled`、redirectUris = 集約後 ＋ port-forward、
   レルムロールを `groups` クレーム（id_token/userinfo）へ載せる**クライアント内 protocolMapper**（ArgoCD RBAC 既定の
   `scopes:[groups]` に合わせる。共有 scope は不変）。
2. **`deploy/local/argocd/oidc/`**: `argocd-cm-patch.yaml`（url・oidc.config）・`argocd-rbac-cm-patch.yaml`
   （policy.default 空・scopes・policy.csv）・`argocd-cmdparams-patch.yaml`（server.insecure）を merge patch 用に置く。
3. **`k8s-local-up.sh`**（ARGOCD ブロック・install 後）: 上記 3 つの ConfigMap を `kubectl patch --type merge --patch-file`
   で適用、`argocd-secret` に clientSecret を merge patch、`argocd-server` を rollout restart。
4. **回帰ガード（TDD）**: `k8s-local-up.test.js` に (a) 既定オフで argocd OIDC 由来トークン不在、(b) `ARGOCD=1` で
   argocd-cm/rbac/cmdparams patch ＋ `oidc.keycloak.clientSecret` secret patch ＋ argocd-server rollout restart、を追加。

## 非対象（スコープ外）

- 他ツール（MinIO/Vault/Wiki.js）の OIDC（#353 の別子タスク）。
- ArgoCD local admin パスワードの堅牢化（dev の initial-admin-secret を break-glass として据え置き）。
- 実ブラウザ SSO ログイン疎通（稼働 k3d/Keycloak・edge・手順A 依存＝live）。

## 検証

- `node scripts/check-realm-constraints.js --self-test && node scripts/check-realm-constraints.js`
- `node scripts/k8s-local-up.test.js`
- `node scripts/check-doc-links.js`
- `node scripts/check-image-mapping.js`
- realm JSON 妥当性（`node -e JSON.parse`）／各 patch YAML の妥当性

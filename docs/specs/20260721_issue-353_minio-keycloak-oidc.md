---
title: 経路B MinIO Console を Keycloak OIDC(SSO) 連携し、集約後 URL(minio.localhost:50000) で到達させる（Issue #353・SSO その3）
type: spec
status: done
related_ids:
  - ADR-0017
  - IADR-0024
  - IADR-0066
  - IADR-0090
  - IADR-0091
  - IADR-0093
author: claude
created: 2026-07-21
updated: 2026-07-21
related_specs:
  - "../adr/IADR-0093_minio-keycloak-oidc.md"
  - "../adr/IADR-0090_grafana-keycloak-oidc-generic-oauth.md"
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "../adr/IADR-0024_object-storage-minio-buckets-and-access.md"
  - "../../deploy/helm/microservices-platform/templates/minio.yaml"
  - "../../deploy/local/edge/README.md"
  - "../../deploy/local/minio-oidc/README.md"
  - "../../deploy/keycloak/microservices-platform-realm.json"
  - "../../scripts/k8s-local-up.sh"
---

# 仕様書: 経路B MinIO Console の Keycloak OIDC 連携（Issue #353 子タスク 3）

## 起点となる計画書（トレーサビリティ）

- ADR: ADR-0017（サービス間認証・エッジ）/ [[IADR-0024]]（MinIO バケット設計・Console は既定 Ingress 非公開）。
  Keycloak OIDC 連携の先例は [[IADR-0090]]（Grafana）、エッジ集約は [[IADR-0091]]。
- 決定: 本作業の設計判断は [[IADR-0093]]（MinIO Console OIDC・policy クレーム・集約 URL・fail-safe deny）。
- Issue: #353（親・enhancement）の子タスク 3（MinIO）。あわせて #356（エッジ集約）。

## 背景と問題

経路B の MinIO（`values.yaml` の `minio.enabled=true` で稼働・Console 9001）は root 資格情報のみで Keycloak SSO に
載っていない。また **MinIO は #357 のエッジ集約対象に含まれておらず**（`deploy/local/edge` に minio route なし）、
`minio.localhost:50000` へ到達できない。#353 方針に従い OIDC を配線し、#356 の集約に minio を加える。

## 受け入れ基準

1. MinIO Console が Keycloak OIDC でログインできる（realm client `minio` ＋ `MINIO_IDENTITY_OPENID_*`）。
2. **集約後 URL・ホスト名ベース**: edge overlay に minio 用 Ingress（host `minio.localhost`・entrypoint `admin:50000`
   → Console 9001）を**新規1ファイル**追加。**既存 grafana/argocd 等の route・ファイルは無改変**。
   redirect は `http://minio.localhost:50000/oauth_callback`＋`http://localhost:9001/oauth_callback`（port-forward 併記）。
   `MINIO_BROWSER_REDIRECT_URL` は集約 URL（`http://minio.localhost:50000`）。
3. **fail-safe policy**: MinIO は `policy` クレーム未マッピングのユーザーを **deny（no-access）**。`platform-admin`/
   `platform-operator` に対応する MinIO ポリシー JSON を用意し、`mc admin policy create` の runtime 手順で適用。
4. **秘密の非平文**: realm export の secret はプレースホルダ。MinIO へは Secret `minio-oidc`（`k8s-local-up.sh` が
   dev 既定 or `MINIO_OIDC_CLIENT_SECRET` env で作成・平文コミットなし）から `optional` 参照で注入。
5. **本番バイト等価**: helm の OIDC 配線は opt-in（`minio.oidc.enabled` 既定 false）。本番 values.yaml は不変、
   経路B は values-local で有効化。grafana/argocd の設定・edge の既存 route は無改変。
6. **issuer 一致**: config_url は `http://keycloak:8080/realms/microservices-platform/.well-known/openid-configuration`
   （MSP ns は ExternalName alias `keycloak` で到達・browser は #284 手順A）。
7. CI 緑: `check-realm-constraints`（≤255）・`k8s-local-up.test.js`・`doc-links`・`check-image-mapping`(#275・image 不変)。

## 対応方針（変更範囲）

- **realm `minio` client**（confidential・追加のみ）: レルムロールを `policy` クレーム（id_token/userinfo・multivalued）へ
  発行する protocolMapper。redirects 2 種（集約＋port-forward）。
- **`deploy/local/edge/admin-ingress-minio.yaml`（新）**＋ kustomization に 1 行追加（最小差分）。
- **`templates/minio.yaml`**: `minio.oidc.enabled` で `MINIO_IDENTITY_OPENID_*`＋`MINIO_BROWSER_REDIRECT_URL` を配線。
- **`values.yaml`**: `minio.oidc`（既定 false・byte 等価）。**`values-local.yaml`**: 経路B で有効化。
- **`k8s-local-up.sh`**: app-secrets に `minio-oidc`（client-secret）を作成。
- **`deploy/local/minio-oidc/`（新）**: policy JSON（`platform-admin.json`/`platform-operator.json`）＋ mc 手順 README。
- **回帰（TDD）**: `k8s-local-up.test.js` に既定実行で `minio-oidc` secret が作られること（app-secret・opt-in 非依存）。

## 非対象

- 他ツール（Vault/Wiki.js）OIDC（#353 別子タスク）。本番での Console 公開（IADR-0024 の非公開運用は不変・edge は local 専用）。
- 実ブラウザ SSO ログイン疎通（稼働 k3d/edge/手順A・mc policy 適用済み前提＝live）。

## 検証

- `node scripts/check-realm-constraints.js --self-test && node scripts/check-realm-constraints.js`
- realm JSON 妥当性／policy JSON 妥当性（`node -e JSON.parse`）
- `node scripts/k8s-local-up.test.js` / `node scripts/check-doc-links.js` / `node scripts/check-image-mapping.js`
- `helm template`（minio.oidc.enabled=true で env 描画・既定 false で不在＝本番 byte 等価）
- `kubectl kustomize deploy/local/edge`（minio Ingress 込みで build）

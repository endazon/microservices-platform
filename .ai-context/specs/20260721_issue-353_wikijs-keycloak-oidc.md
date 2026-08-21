---
title: 経路B Wiki.js を Keycloak OIDC(SSO) 集約後 URL に対応させ、50000 集約に載せる（Issue #353・SSO その5・最終）
type: spec
status: done
related_ids:
  - FR-13
  - UC-07
  - ADR-0011
  - IADR-0020
  - IADR-0091
  - IADR-0093
  - IADR-0095
author: claude
created: 2026-07-21
updated: 2026-07-21
related_specs:
  - "../adr/IADR-0095_wikijs-keycloak-oidc.md"
  - "../adr/IADR-0020_wiki-js-deployment-abac-gateway.md"
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "../adr/IADR-0093_minio-keycloak-oidc.md"
  - "../../deploy/local/edge/README.md"
  - "../../deploy/local/wiki-oidc/README.md"
  - "../../deploy/keycloak/microservices-platform-realm.json"
---

# 仕様書: 経路B Wiki.js の Keycloak OIDC 集約対応（Issue #353 子タスク 5・最終）

## 起点となる計画書（トレーサビリティ）

- 計画: FR-13 / UC-07（Wiki 閲覧・編集）。Wiki.js 配備と ABAC ゲートウェイは [IADR-0020](../adr/IADR-0020_wiki-js-deployment-abac-gateway.md)（ADR-0011 追従）。
  エッジ集約は [IADR-0091](../adr/IADR-0091_local-edge-aggregation-traefik.md)、集約 route 追加の先例は [IADR-0093](../adr/IADR-0093_minio-keycloak-oidc.md)（MinIO）。
- 決定: 本作業の設計判断は [IADR-0095](../adr/IADR-0095_wikijs-keycloak-oidc.md)（Wiki.js OIDC 集約 URL・edge route 追加・DB/管理UI 設定手順）。
- Issue: #353（親・enhancement）の子タスク 5（Wiki.js・**最終**）。あわせて #356（エッジ集約）。

## 背景と問題

realm には `wiki-js` client が既存（[IADR-0020](../adr/IADR-0020_wiki-js-deployment-abac-gateway.md)）。しかし Wiki.js の OIDC 設定は **Wiki.js の DB/管理UI 保持**
（"Generic OpenID Connect" ストラテジ）で **manifest 完全自動化不可**（コールバックは `{siteUrl}/login/{strategyKey}/callback`・
strategyKey は作成時に生成）。また **Wiki.js は #357 のエッジ集約に未登録**（`deploy/local/edge` に wiki route なし）で
`wiki.localhost:50000` へ到達できない。#356 の集約に wiki を加え、集約後 URL で OIDC ログインできるようにする。

## 受け入れ基準

1. **集約後 URL・ホスト名ベース**: edge overlay に wiki 用 Ingress（host `wiki.localhost`・entrypoint `admin:50000`
   → `wiki-js:3000`）を**新規1ファイル**追加。**既存 grafana/argocd/minio 等の route・ファイルは無改変**。
2. realm `wiki-js` client の `redirectUris`/`webOrigins` に集約後 URL（`http://wiki.localhost:50000/*` ワイルドカード）を
   **追加**。既存 port-forward 用（`localhost:3001` 等）は**残す**（後方互換）。**realm 差分は `wiki-js` client のみ**。
3. **Wiki.js OIDC 設定手順**（管理UI・DB 保持）を README/spec に: config_url=Keycloak well-known、Authorization/Token/
   UserInfo endpoints、client_id=`wiki-js`、client secret（realm プレースホルダ・**管理UI 入力＝非コミット**）、
   **Site URL=`http://wiki.localhost:50000`**（コールバックの基準）、**group/claim マッピング**。
4. **fail-safe**: OIDC の group/claim マッピングで未マッピングユーザーは Wiki.js の**最小権限グループ**（Guests 相当）に
   割当（deny-by-default 寄り）。ローカルログインは既定無効の OIDC 単一経路（[IADR-0020](../adr/IADR-0020_wiki-js-deployment-abac-gateway.md)）。
5. **本番/既存バイト等価**: 変更は edge overlay への route 追加＋realm への redirect 追加のみ。helm chart・`values.yaml`・
   Wiki.js Deployment は無改変（Ingress 既定 disabled の非公開運用は不変）。edge は経路B opt-in。
6. CI 緑: `check-realm-constraints`（≤255）・`doc-links`・`check-image-mapping`(#275・image 不変)・`k8s-local-up.test.js`
   （edge apply 不変・スクリプト無改変）・gitleaks（平文 secret なし）。

## 対応方針（変更範囲）

- **realm `wiki-js` client**（redirectUris/webOrigins に `http://wiki.localhost:50000/*` 追加のみ）。
- **`deploy/local/edge/admin-ingress-wiki.yaml`（新）**＋ kustomization に 1 行追加（最小差分）。
- **`deploy/local/wiki-oidc/README.md`（新）**: 管理UI の OIDC 設定手順（endpoints・claim/group マッピング・Site URL・
  fail-safe グループ）。
- **`deploy/local/edge/README.md`**: 構成表/アクセス一覧へ wiki を追記（doc 整合）。
- script/smoke は無改変（新 secret なし・edge apply 不変）。

## 非対象

- Wiki.js OIDC 設定の manifest 自動化（DB/管理UI 保持のため不可・手順化で対応）。
- 本番での Wiki.js 公開（[IADR-0020](../adr/IADR-0020_wiki-js-deployment-abac-gateway.md) の非公開運用は不変・edge は local 専用）。frontend SC-04 の `WIKI_BASE_URL`
  （#344）は本 PR では変更しない（edge 併用時は `wiki.localhost:50000` を指すと一貫・任意）。
  → **解消済み（2026-07-25）**: この「任意」の先送り分は [20260725_issue-344_wiki-base-url-edge-alignment](./20260725_issue-344_wiki-base-url-edge-alignment.md) で対応し、
  `values-local.yaml` の `WIKI_BASE_URL` を edge 正規 URL `http://wiki.localhost:50000` へ整合した（#344 spec の
  受け入れ基準・`deploy/local/README.md` も併せて edge 前提へ改訂）。
- 実ブラウザ SSO ログイン疎通（稼働 k3d/edge/手順A・管理UI 設定済み前提＝live）。

## 検証

- `node scripts/check-realm-constraints.js --self-test && node scripts/check-realm-constraints.js`
- realm JSON 妥当性（`node -e JSON.parse`・差分は `wiki-js` client のみ）
- `node scripts/k8s-local-up.test.js` / `node scripts/check-doc-links.js` / `node scripts/check-image-mapping.js`
- `kubectl kustomize deploy/local/edge`（wiki Ingress 込みで build）

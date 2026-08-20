---
title: 経路B Grafana OIDC をエッジ集約後 URL に整合させる（#353 PR-2・IADR-0090/0091 のフォローアップ）
type: spec
status: done
related_ids:
  - IADR-0090
  - IADR-0091
author: claude
created: 2026-07-21
updated: 2026-07-21
related_specs:
  - "../adr/IADR-0090_grafana-keycloak-oidc-generic-oauth.md"
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "../../deploy/keycloak/microservices-platform-realm.json"
  - "../../deploy/local/observability/grafana.yaml"
  - "../../deploy/local/edge/README.md"
---

# 仕様書: Grafana OIDC のエッジ集約後 URL 整合（#353 PR-2）

## 起点となる計画書（トレーサビリティ）

- 決定: [IADR-0090](../adr/IADR-0090_grafana-keycloak-oidc-generic-oauth.md)（Grafana generic OAuth）／[IADR-0091](../adr/IADR-0091_local-edge-aggregation-traefik.md)（ローカルエッジ集約・ホスト名ベース・集約後 URL）。
  本作業は IADR-0091 が「PR-2（#355 マージ後）」として明記した**既定路線の実施**であり、新規の設計判断は無い
  （新 IADR は起票しない）。
- Issue: #353（親・SSO 一括連携）。あわせて #356（エッジ集約）。

## 背景

#355（IADR-0090）で Grafana OIDC を配線した時点では、redirect/root_url は port-forward 前提（`localhost:3000`）
だった。#357（IADR-0091）でエッジ集約（`grafana.localhost:50000`・ホスト名ベース）がマージされたため、Grafana が
edge 経由でも OIDC ログインできるよう、集約後 URL を realm redirect と `GF_SERVER_ROOT_URL` に整合させる。

## 受け入れ基準

1. realm の `grafana` client `redirectUris`/`webOrigins` に集約後 URL（`http://grafana.localhost:50000…`）を**追加**。
   既存 port-forward 用（`localhost:3000`）は**残す**（後方互換）。
2. `grafana.yaml` の `GF_SERVER_ROOT_URL` を `http://grafana.localhost:50000/` にする（redirect_uri 整合）。
   ホスト名ベース＝ルート配信のため `serve_from_sub_path` は使わない。
3. **他ツール非干渉**: `argocd` 他 client・edge overlay・script は不変。Grafana 単体の URL 整合のみ。
4. CI 緑: `check-realm-constraints`（description ≤255・realm 妥当）／`doc-links`／`check-image-mapping`(#275・image 不変)。

## 変更範囲

実装（config）は `deploy/keycloak/microservices-platform-realm.json`（grafana client の redirectUris/webOrigins 追加）と
`deploy/local/observability/grafana.yaml`（`GF_SERVER_ROOT_URL`）の 2 ファイル。純粋な宣言的 config で
スクリプトロジックは不変（新規テストは不要・既存 smoke test の grafana-oidc 配線は無影響）。
あわせて claude-review 反映として、実効ログイン経路（edge 前提・port-forward 単独では OIDC 未成立→local admin）を
`deploy/local/edge/README.md` と `deploy/local/observability/README.md` に正確化する（docs のみ・挙動不変）。

## 非対象

- 他ツール（ArgoCD/MinIO/Vault/Wiki.js）の OIDC。
- 本番 Helm（compose/prod は edge 非対象）・実 TLS。
- 実ブラウザ SSO ログイン疎通（稼働 k3d/edge/手順A 依存＝live）。

## 検証

- `node scripts/check-realm-constraints.js --self-test && node scripts/check-realm-constraints.js`
- realm JSON 妥当性（`node -e JSON.parse`）
- `node scripts/check-doc-links.js` / `node scripts/check-image-mapping.js`
- `kubectl kustomize deploy/local/observability`（grafana.yaml 妥当性）

---
title: 経路B ローカルエッジ集約 — platform フロント 80/443 ＋ 管理ツール 50000（ホスト名ベース・Traefik）PR-1（Issue #356）
type: spec
status: done
related_ids:
  - ADR-0006
  - IADR-0066
  - IADR-0076
  - IADR-0077
  - IADR-0087
  - IADR-0091
author: claude
created: 2026-07-20
updated: 2026-07-20
related_specs:
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md"
  - "../adr/IADR-0077_local-observability-vault-gitops-overlays.md"
  - "../adr/IADR-0087_k8s-local-up-optin-smoke-test.md"
  - "../../deploy/local/edge/README.md"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: 経路B ローカルエッジ集約 PR-1（Issue #356）

## 起点となる計画書（トレーサビリティ）

- ADR: ADR-0006（運用基盤）。経路B k8s dev 統括は [IADR-0066](../adr/IADR-0066_local-k8s-dev-environment.md)、prod エッジ（Istio）ルーティングの設計は
  [IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md)、opt-in オーバーレイの流儀は [IADR-0077](../adr/IADR-0077_local-observability-vault-gitops-overlays.md)、ゲート横断 smoke test は [IADR-0087](../adr/IADR-0087_k8s-local-up-optin-smoke-test.md)。
- 決定: 本作業の設計判断は [IADR-0091](../adr/IADR-0091_local-edge-aggregation-traefik.md)（ローカル Traefik エッジ・ホスト名ベース集約・issuer 最小案）。
- Issue: #356（enhancement）。SSO 親 #353 とは別立て（公開経路の集約）。

## 背景と問題

経路B の platform フロント（SPA/BFF）と管理ツール（Grafana/ArgoCD/Vault/Headlamp/Qdrant）は現状すべて
`kubectl port-forward` 個別到達である。prod は Istio エッジ（`templates/edge.yaml`）だが**ローカルは Istio 未導入**
（`values-local` で `edge.enabled=false`）。一方 k3s は **Traefik を 80/443（ホスト 8080/8443）で待受**しているが
経路B は Ingress を一切作らず未使用。ユーザー要望は「platform フロント=80/443、管理ツール類=単一ポート 50000 集約」。

## 受け入れ基準

1. **opt-in `LOCALEDGE=1`**: 既定オフ時は現行挙動（k3d ポート 8080/8443・エッジ overlay 不適用）と**バイト等価**。
2. **80/443**: `LOCALEDGE=1` で k3d cluster create を `-p 80:80 -p 443:443 -p 50000:50000@loadbalancer` に切替。
   Traefik Ingress で `/bff`→bff-service、catch-all→frontend-service（web/websecure entrypoint）。
3. **50000 集約**: Traefik `HelmChartConfig`（kube-system）で追加 entrypoint `admin:50000`。管理ツールは
   **ホスト名ベース** Ingress（`grafana.localhost` 等・`traefik.ingress.kubernetes.io/router.entrypoints: admin`）。
4. **Qdrant** は SSO 非対応のため素通し公開（注記）。
5. **#355 非干渉**: 本 PR は `grafana.yaml`・`realm.json` を**触らない**（redirect 追記・root_url 設定は #355 マージ後の PR-2）。
6. **runtime 差**: Rancher Desktop（内蔵 k3s）はポート再作成不要で overlay 適用のみ（README 明記）。
7. CI 緑: `k8s-local-up.test.js`（LOCALEDGE ゲート回帰）・`doc-links`・`check-image-mapping`(#275・image 不変)・
   `realm-constraints`（realm 無改変で不変）。

## 対応方針（変更範囲・本 PR-1）

`scripts/k8s-local-up.sh`（`LOCALEDGE` ゲート）・`deploy/local/edge/`（新・overlay）・`scripts/k8s-local-up.test.js`
（回帰）・README・`docs/adr`（IADR-0091＋索引）に閉じる。**`grafana.yaml`・`realm.json` は無改変**。

1. **`k8s-local-up.sh`**: k3d cluster create ポートを `LOCALEDGE=1` で 80/443/50000 に分岐（既定は 8080/8443 でバイト等価）。
   helm install 後の opt-in ブロックで `LOCALEDGE=1` のとき `kubectl apply -k deploy/local/edge`。argocd namespace が
   存在するときのみ argocd 用 Ingress を追加適用（fail-safe: ns 不在で失敗させない）。
2. **`deploy/local/edge/`**:
   - `traefik-entrypoint.yaml`: `HelmChartConfig`（kube-system/traefik）で `admin:50000` を追加。
   - `platform-frontend-ingress.yaml`（ns microservices-platform）: `/bff`→bff-service、`/`→frontend-service（web/websecure）。
   - `admin-ingress-infra.yaml`（ns platform-infra）: grafana/headlamp/vault/qdrant を host 名で `admin` entrypoint へ。
   - `argocd-ingress.yaml`（ns argocd・kustomization 外・条件付き適用）: argocd-server を host 名で `admin` entrypoint へ。
   - `kustomization.yaml`: 上記のうち常在 namespace のリソースを集約。
   - `README.md`: 方式・ホスト名解決・TLS・k3d 再作成手順。
3. **回帰ガード（TDD）**: `k8s-local-up.test.js` に (a) 既定オフで `deploy/local/edge`・`50000` が現れずポートがバイト等価、
   (b) `LOCALEDGE=1` で 80/443/50000 ポート・`deploy/local/edge` 適用、を追加。

## 非対象（#355 マージ後の PR-2 / 別 issue）

- `realm.json` の redirectUris 追加（grafana `…grafana.localhost:50000…`、headlamp `…headlamp.localhost:50000…`）。
- `grafana.yaml` の `GF_SERVER_ROOT_URL=http://grafana.localhost:50000/`。
- ArgoCD/Vault 等の OIDC client 実装（#353・最初から 50000 URL で登録）。
- 実 TLS 証明書（443 は Traefik 既定自己署名）・実ブラウザ疎通（稼働クラスタ依存＝live）。

## 検証

- `node scripts/k8s-local-up.test.js`
- `node scripts/check-doc-links.js`
- `node scripts/check-image-mapping.js`（image 不変）
- `node scripts/check-realm-constraints.js`（realm 無改変・不変）
- `kubectl kustomize deploy/local/edge`（build 妥当性）

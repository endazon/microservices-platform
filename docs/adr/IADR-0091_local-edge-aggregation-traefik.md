---
title: IADR-0091 経路B のローカルエッジは k3s 内蔵 Traefik で構成し、管理ツールを追加 entrypoint admin:50000 にホスト名ベースで集約する
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006
  - IADR-0066
  - IADR-0076
  - IADR-0077
  - IADR-0087
author: claude
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ (ADR-0006 CI/CD・運用基盤)"
---

# IADR-0091: 経路B ローカルエッジ集約（Traefik・80/443 ＋ admin:50000 ホスト名ベース）

- 状態: Accepted
- 日付: 2026-07-20
- 決定者: claude（実装）

## 起点・関連

- 関連 ADR: ADR-0006（運用基盤）。経路B k8s dev 統括 [[IADR-0066]]、prod エッジ（Istio）ルーティング設計
  [[IADR-0076]]、opt-in オーバーレイの流儀 [[IADR-0077]]、ゲート横断 smoke test [[IADR-0087]]。
- 仕様書: `docs/specs/20260720_issue-356_local-edge-aggregation.md`。
- Issue: #356（enhancement）。SSO 親 #353・Grafana OIDC #355 と直列（realm.json/grafana.yaml 競合回避）。

## コンテキストと課題

経路B の platform フロント（SPA/BFF）と管理ツール（Grafana/ArgoCD/Vault/Headlamp/Qdrant）は現状すべて
`port-forward` 個別到達。ユーザー要望は「platform フロント=80/443、管理ツール類=単一ポート 50000 集約」。
prod は Istio エッジだがローカルは Istio 未導入。k3s は Traefik を既定稼働させているが経路B では未使用。

## 決定

### 1. ローカルエッジは k3s 内蔵 Traefik で構成する（Istio は持ち込まない）

`values-local` が `edge.enabled=false`（Istio 未導入）である流儀を維持し、ローカルのエッジは**既に稼働している
k3s Traefik** を使う。エッジ資材は `deploy/local/edge/`（opt-in オーバーレイ）に置き、observability/vault と同じ
「ローカル専用・既定オフ」の位置づけとする。prod（Istio `templates/edge.yaml`）とは別実装。

### 2. opt-in ゲート `LOCALEDGE=1`・既定オフでバイト等価

`scripts/k8s-local-up.sh` に `LOCALEDGE=1` を新設。ON で k3d cluster create を
`-p 80:80 -p 443:443 -p 50000:50000@loadbalancer` に切替え、エッジ overlay を適用する。**既定（未設定）は現行の
8080/8443・overlay 不適用でバイト等価**（fail-safe・後方互換）。ポートは cluster 作成時固定のため既存クラスタは
delete→再作成が必要（README のユーザー手順・破壊操作はユーザーが実行）。

### 3. platform フロントは 80/443（Traefik web/websecure）

Traefik 標準 entrypoint `web`(80)/`websecure`(443) に Ingress を張り、`/bff`→`bff-service:8080`、catch-all
`/`→`frontend-service:8080`（rewrite 無し＝prod `edge.yaml` と同契約）。443 は Traefik 既定の自己署名証明書
（ブラウザ警告・実 TLS は別途）。

### 4. 管理ツールは追加 entrypoint admin:50000 にホスト名ベースで集約する

Traefik `HelmChartConfig`（kube-system/traefik）で `admin:50000` entrypoint を追加し、k3d `-p 50000:50000@loadbalancer`
で公開する。各ツールは**ホスト名ベース**の標準 Ingress（`grafana.localhost` / `argocd.localhost` / `vault.localhost` /
`headlamp.localhost` / `qdrant.localhost`）に `traefik.ingress.kubernetes.io/router.entrypoints: admin` 注釈を付け、
50000 のみに載せる（web:80 の catch-all フロントと衝突しない＝「アプリ面」と「管理面」を分離）。

**ホスト名ベースの根拠**: パスベース（`localhost:50000/grafana` 等）は **Vault UI（`/ui/` 固定）と Qdrant dashboard
（`/dashboard` 固定・静的資産が絶対パス）がサブパス配信を原理的にサポートしない**ため不成立。ホスト名ベースは全ツール
ルート配信で成立し、ツール側追加設定は「外部 URL の通知」に限られる。CLI（argocd/vault）は `*.localhost` を解決しない
ことがあるため hosts 追記または `*.nip.io`/`*.sslip.io` を代替として案内する。

### 5. OIDC issuer は最小案（keycloak:8080 維持）・redirect は集約後 URL を追加

issuer は現行 `http://keycloak:8080`（手順A）を維持し、ツール UI のみ 50000 集約する。集約後 URL への
redirectUris 追加（grafana/headlamp）と `GF_SERVER_ROOT_URL` 設定は **#355（grafana.yaml/realm.json）と競合するため
本 PR-1 では行わず、#355 マージ後の PR-2 に回す**。追加は既存 port-forward 用 URL を残す形（後方互換）。これから足す
ArgoCD/Vault 等の OIDC client は**最初から 50000 URL で登録**する。

### 6. fail-safe な適用順・namespace 条件

エッジ overlay は常在 namespace（platform-infra / microservices-platform / kube-system）のリソースを kustomize で
適用し、argocd namespace の Ingress は **argocd ns が存在するときのみ**追加適用する（ns 不在で失敗させない）。
Qdrant は SSO 非対応のため素通し公開（ネットワーク閉域前提の便宜・注記）。

## 影響・トレードオフ

- `LOCALEDGE` は既定オフのため既存環境に影響しない（smoke test で default バイト等価を固定）。
- 80/443 はホスト権限・既存サービスとの衝突・443 自己署名の制約あり（README 明記）。占有時は LOCALEDGE を使わず
  従来 port-forward を継続できる（フォールバック）。
- 新ホストからの OIDC ログインは PR-2（redirect 追加）まで未成立。その間も port-forward + 既存 redirect で
  ログイン可（フォールバック維持）。
- Traefik `HelmChartConfig` の `expose` 値スキーマは Traefik chart バージョン差がある（新しめは `expose: {default: true}`）。
  値は k3s の Traefik 版に追随させる（コメントで両形を明記）。

## 代替案

- **パスベース集約**: Vault UI/Qdrant がサブパス非対応のため却下（決定4）。
- **専用リバースプロキシを 50000 で新設**: bundled Traefik があるのに二重になるため非推奨（代替として README 言及可）。
- **Istio をローカルにも導入**: values-local の Istio 非導入方針・重量に反するため却下。
- **Keycloak も 50000 集約（issuer 変更）**: IADR-0086 の metadata/issuer 分離が要る。複雑化のため最小案を採用。

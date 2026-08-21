---
title: IADR-0092 経路B ArgoCD を Keycloak OIDC(oidc.config・dex 不使用) で認証し、集約後 URL(argocd.localhost:50000) にホスト名ベースで登録する
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006
  - IADR-0066
  - IADR-0077
  - IADR-0090
  - IADR-0091
  - IADR-0220
author: claude
created: 2026-07-20
updated: 2026-08-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ (ADR-0006 CI/CD・GitOps)
---

# IADR-0092: 経路B ArgoCD の Keycloak OIDC 連携

- 状態: Accepted
- 日付: 2026-07-20
- 決定者: claude（実装）

## 起点・関連

- 関連 ADR: ADR-0006（GitOps）。ArgoCD ブートストラップ統括 [IADR-0077](./IADR-0077_local-observability-vault-gitops-overlays.md)、Keycloak OIDC 連携の先例
  [IADR-0090](./IADR-0090_grafana-keycloak-oidc-generic-oauth.md)（Grafana）、エッジ集約（集約後 URL・ホスト名ベース） [IADR-0091](./IADR-0091_local-edge-aggregation-traefik.md)。
- 仕様書: `docs/specs/20260720_issue-353_argocd-keycloak-oidc.md`。
- Issue: #353（親・enhancement）の子タスク 2（ArgoCD）。番号採番: MSP `docs/adr/` は 0000–0091 が連番で欠番なし
  （0080→0081 の改番は両スロット充足済み・撤回による空き無し）ため最新+1 の **0092** を採番。

## コンテキストと課題

経路B の ArgoCD（`ARGOCD=1` で公式 install.yaml 適用）は local admin のみで Keycloak SSO 未連携。#353 方針で
OIDC に載せる。エッジ集約（#357/[IADR-0091](./IADR-0091_local-edge-aggregation-traefik.md)）がマージ済みのため、**最初から集約後 URL（`argocd.localhost:50000`）**
で登録する。制約は「fail-safe（未設定/未マッピングで無権限に倒す・local admin を残す）」「秘密は非平文」「ArgoCD 単体」。

## 決定

### 1. dex を使わず oidc.config で Keycloak に直接連携する

`argocd-cm.oidc.config` に Keycloak を直接指定する（`dex.config` は設定しない＝dex 経路は無効）。issuer は
`http://keycloak:8080/realms/microservices-platform`（手順A・Grafana/Headlamp と同一）。argocd-server は in-cluster で
OIDC discovery/token 交換を行い、ブラウザは同 issuer の authorize へ（手順A で `keycloak:8080` 解決）。

### 2. 集約後 URL・ホスト名ベース（rootpath 不使用）

`argocd-cm.url = http://argocd.localhost:50000`、redirect は `http://argocd.localhost:50000/auth/callback`。エッジは
[IADR-0091](./IADR-0091_local-edge-aggregation-traefik.md) の Traefik `admin:50000` entrypoint＋`argocd.localhost` Ingress（#357 で追加済み）。**`server.rootpath`
（サブパス配信）は使わない**（#357 のホスト名ベース方針）。edge は平文 http のため **`server.insecure=true`**
（`argocd-cmd-params-cm`）にして argocd-server の https 強制を解く。port-forward 用 `http://localhost:8083/auth/callback`
も realm に併記（フォールバック）。

### 3. fail-safe：local admin を残し、未マッピングは no-access

OIDC は追加で、ArgoCD 組み込みの **local admin（`argocd-initial-admin-secret`）を break-glass として残す**。RBAC は
`policy.default=''`（未マッピングの OIDC ユーザーは無権限＝Admin へ昇格しない）、`platform-admin`→`role:admin`、
`platform-operator`→`role:readonly`。

### 4. レルムロールはクライアント内 protocolMapper で groups クレームへ

ArgoCD RBAC は既定 `scopes:[groups]` で id_token の `groups` クレームを主体グループとして解決する。共有 `roles`
clientScope（access token 限定）は届かないため、**`argocd` client 固有の protocolMapper** でレルムロールを `groups`
クレーム（id_token/userinfo）として発行する（他 client 不変・[IADR-0090](./IADR-0090_grafana-keycloak-oidc-generic-oauth.md) の Grafana と同型）。`requestedScopes` は
`openid/profile/email` のみ（`groups` scope は要求しない＝client mapper が常時付与するため）。

### 5. 秘密は非平文・argocd-secret へ merge patch

realm export の secret はプレースホルダ `argocd-dev-secret-change-me`。ArgoCD へは既存 `argocd-secret` に
**`kubectl patch --type merge`** で `oidc.keycloak.clientSecret` を注入する（`server.secretkey` 等の既存キーを
保持するため apply による全置換はしない）。dev 既定 or `ARGOCD_OIDC_CLIENT_SECRET` env・平文コミットなし。

### 6. install 後の適用順・反映

`ARGOCD=1` ブロックで install（server-side apply・[IADR-0077](./IADR-0077_local-observability-vault-gitops-overlays.md)/#348）後に、3 つの ConfigMap を
`kubectl patch --type merge --patch-file`（`deploy/local/argocd/oidc/`）で適用し、`argocd-secret` を merge patch、
`argocd-server` を rollout restart（`server.insecure`/oidc を反映）。ConfigMap patch は既存キーを保持する（全置換しない）。

## 影響・トレードオフ

- ArgoCD が Keycloak SSO でログイン可能になる。local admin は break-glass として残る（ロックアウト回避）。
- 変更は `ARGOCD=1` opt-in と realm への追加のみ。既定オフ時のスクリプト挙動はバイト等価（smoke test で固定）。
- `server.insecure=true` は dev の edge（平文 http）前提。本番は TLS 終端＋insecure 無効が前提（本オーバーレイ非対象）。
- 実ブラウザ SSO ログインは稼働 k3d/Keycloak・edge・手順A 依存＝live。

> **［2026-08-17 追記 / #841］本 ADR が前提にしていた「エッジ（`admin:50000`）は平文 http」は、もう成り立たない。**
> [IADR-0220](./IADR-0220_admin-entrypoint-tls-and-http-redirect.md) が `--entryPoints.admin.http.tls=true` で **admin(50000) を TLS 終端**にした
> （計画 `NFR-11`「平文 HTTP を残さない」の適用範囲が**環境を問わない**と確定したため。
> 利用者裁定 2026-08-16 / 裁定依頼 planning#383、証明書の発行方式は計画 `ADR-0047`）。
> **本文は当時の記録として書き換えない。** 読み替えは下記のとおり。
>
> | 本文の記述 | 現在 |
> | --- | --- |
> | `argocd-cm.url = http://argocd.localhost:50000`（決定 2） | **`https://argocd.localhost:50000`**（realm の `redirectUris`/`webOrigins` も https へ揃えた） |
> | 「edge は平文 http のため `server.insecure=true`」（決定 2・§影響） | **`server.insecure=true` は据え置く。ただし理由が変わった** —— TLS を終端するのは Traefik であり、**そこから `argocd-server` への in-cluster 転送が平文**だからである。`insecure` を外すと argocd-server 自身が http→https リダイレクトを返し、**エッジ経由が二重終端で壊れる** |
>
> **決定そのもの（`server.insecure=true`）は正しいままである。変わったのは前提の説明だけ**であり、
> `status` は `Accepted` のままとする。port-forward 用の `http://localhost:8083/...` は
> **エッジを経由しない別経路のため据え置き**である。

## 代替案

- **dex 経由（`dex.config` で Keycloak connector）**: 中間コンポーネントが増える。`oidc.config` 直接指定で十分なため却下。
- **`server.rootpath` でパスベース集約**: #357 でホスト名ベースに統一済み（Vault/Qdrant のサブパス非対応が理由）。整合のため不採用。
- **argocd-secret を apply で全置換**: `server.secretkey` 等の既存キーを消すため却下。merge patch に限定。
- **realm 共有 `roles` scope を id_token へ拡張**: backend 前提（access token）と全 client へ波及。client 固有 mapper に閉じる。

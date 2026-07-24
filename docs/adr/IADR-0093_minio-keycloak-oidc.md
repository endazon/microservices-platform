---
title: IADR-0093 経路B MinIO Console を Keycloak OIDC(MINIO_IDENTITY_OPENID) で認証し、policy クレームで RBAC を fail-safe deny 既定にする。集約 URL(minio.localhost:50000) をエッジに追加する
type: impl-adr
status: Accepted
related_ids:
  - ADR-0017
  - IADR-0024
  - IADR-0090
  - IADR-0091
author: claude
created: 2026-07-21
updated: 2026-07-21
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ (ADR-0017 サービス間認証・エッジ)"
---

# IADR-0093: 経路B MinIO Console の Keycloak OIDC 連携とエッジ集約

- 状態: Accepted
- 日付: 2026-07-21
- 決定者: claude（実装）

## 起点・関連

- 関連 ADR: ADR-0017 / [[IADR-0024]]（MinIO・Console 既定非公開）。OIDC 連携先例 [[IADR-0090]]（Grafana）、
  エッジ集約 [[IADR-0091]]。
- 仕様書: `docs/specs/20260721_issue-353_minio-keycloak-oidc.md`。
- Issue: #353 子タスク 3（MinIO）。番号採番: develop 最新の IADR max=0092（#359 マージ済）＋1 の **0093**。

## コンテキストと課題

MinIO Console（経路B で稼働・9001）は root 資格情報のみ。#357 のエッジ集約に MinIO は未登録で
`minio.localhost:50000` に到達できない。#353 方針で OIDC を配線し、#356 集約に minio を加える。制約は
「fail-safe（未マッピングは no-access・root は break-glass）」「秘密は非平文」「本番 byte 等価」「他ツール route 無改変」。

## 決定

### 1. MinIO 内蔵 OIDC（MINIO_IDENTITY_OPENID）で Keycloak に連携する

`MINIO_IDENTITY_OPENID_CONFIG_URL`（Keycloak well-known）＋`CLIENT_ID`/`CLIENT_SECRET`/`SCOPES` で連携する。
config_url は `http://keycloak:8080/realms/microservices-platform/.well-known/openid-configuration`（MSP ns の
ExternalName alias `keycloak` で in-cluster 到達・browser は #284 手順A）。realm に confidential client `minio` を追加。

### 2. 集約 URL・ホスト名ベース（edge に minio route を新規追加）

`MINIO_BROWSER_REDIRECT_URL=http://minio.localhost:50000`（Console 公開 URL＝OIDC redirect の基準）。redirect は
`http://minio.localhost:50000/oauth_callback`。エッジは [[IADR-0091]] の Traefik `admin:50000` に **minio 用 Ingress を
新規1ファイル**（`deploy/local/edge/admin-ingress-minio.yaml`・host `minio.localhost`→Console 9001）追加し、
kustomization に 1 行足す。**既存 grafana/argocd 等の Ingress・ファイルは無改変**（route 追加のみ）。realm には
port-forward 用 `http://localhost:9001/oauth_callback` も併記（`MINIO_BROWSER_REDIRECT_URL` を外した場合の後方互換）。

### 3. policy クレームで RBAC・fail-safe deny 既定

`MINIO_IDENTITY_OPENID_CLAIM_NAME=policy`。`minio` client 固有の protocolMapper でレルムロールを `policy` クレーム
（multivalued・id_token/userinfo）へ発行する。MinIO は claim 値に**名前が一致する MinIO ポリシー**を適用し、
一致が無ければ **deny（no-access）**＝fail-safe。`platform-admin`/`platform-operator` に対応する MinIO ポリシー JSON を
`deploy/local/minio-oidc/policies/` に置き、`mc admin policy create` の runtime 手順で適用する（ポリシー作成は
MinIO の runtime admin 操作でありマニフェスト/env では表現しないため。realm import と同様の runtime 手順）。組み込み
root（`minio-credentials`）は break-glass として残す。

### 4. 本番 byte 等価・opt-in・秘密の非平文

helm の OIDC 配線は `minio.oidc.enabled`（既定 false）でゲートし、本番 `values.yaml` は不変（env 未描画＝byte 等価）。
経路B は `values-local.yaml` で有効化する。client secret は realm export ではプレースホルダ、MinIO へは Secret
`minio-oidc`（`k8s-local-up.sh` が dev 既定 or `MINIO_OIDC_CLIENT_SECRET` env で作成）を **`optional` 参照**で注入
（未作成でも Pod 起動＝root ログインへフォールバック）。[[IADR-0090]] の `grafana-oidc` と同型。

## 影響・トレードオフ

- MinIO Console が Keycloak SSO でログイン可能になり、`minio.localhost:50000` で集約到達できる。root は break-glass。
- 変更は opt-in（`minio.oidc.enabled`）と realm/edge への追加のみ。既定オフ時は本番 byte 等価（helm template で確認）。
- MinIO policy 作成は runtime 手順（mc）。適用前は fail-safe に全 OIDC ユーザーが deny（安全側）。
- `MINIO_BROWSER_REDIRECT_URL` を集約 URL に固定するため、port-forward 単独（edge 未起動）では OIDC redirect が
  集約 URL を指し完了しない→root で入る（[[IADR-0090]] PR-2 の Grafana と同じ性質・README 明記）。
- Console のエッジ公開は **local 専用オーバーレイ**に閉じる。本番 chart の Console 非公開運用（IADR-0024）は不変。

## 代替案

- **パスベース集約**: Vault/Qdrant 同様サブパス非対応の懸念＋#357 のホスト名ベース方針に反するため却下。
- **MINIO_IDENTITY_OPENID_ROLE_POLICY（全 OIDC ユーザーに単一ポリシー）**: 粒度が無く role 踏襲・fail-safe に反するため却下。
- **policy を built-in 名（consoleAdmin 等）で emit**: Keycloak 側で role→別値の条件マッピングが必要（client role/属性）で
  「既存 realm ロール踏襲」から外れるため、role 名の MinIO ポリシーを作る方式を採用。
- **client secret を values/manifest に平文**: 非平文原則に反する。Secret + env 上書きに一元化。

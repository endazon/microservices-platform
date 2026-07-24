---
title: IADR-0094 経路B dev Vault を Keycloak OIDC 認証メソッド(auth/oidc)で SSO 連携し、external group で realm ロール→policy を fail-safe(default のみ)にマップする
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006
  - IADR-0077
  - IADR-0090
  - IADR-0091
author: claude
created: 2026-07-21
updated: 2026-07-21
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ (ADR-0006 運用基盤)"
---

# IADR-0094: 経路B dev Vault の Keycloak OIDC 連携

- 状態: Accepted
- 日付: 2026-07-21
- 決定者: claude（実装）

## 起点・関連

- 関連 ADR: ADR-0006。Vault dev opt-in は [[IADR-0077]]、OIDC 連携先例 [[IADR-0090]]（Grafana）、エッジ集約 [[IADR-0091]]。
- 仕様書: `docs/specs/20260721_issue-353_vault-keycloak-oidc.md`。
- Issue: #353 子タスク 4（Vault）。番号採番: develop 最新 IADR max=0092、0093 は in-flight #365（MinIO）に予約済みのため **0094**。

## コンテキストと課題

dev Vault（`VAULT=1`・`-dev`・インメモリ・unseal 不要）は root トークンのみ。#353 で OIDC 認証メソッドを配線する。
Vault は #357 のエッジ集約に既登録（`vault.localhost:50000`→8200）＝edge Ingress は無改変。制約は「fail-safe
（未マッピングは no secret access・root は break-glass）」「秘密は非平文」「opt-in・既存 byte 等価」。

## 決定

### 1. OIDC は Vault の auth method（auth/oidc）を runtime bootstrap で構成する

Vault の OIDC 設定（`vault auth enable oidc`＋`vault write auth/oidc/config`／`.../role/default`）は API/CLI の
runtime 操作で、manifest/env では表現しない。よって `deploy/local/vault/oidc/bootstrap.sh`（＋policy HCL）を置き、
realm import や MinIO の `mc` と同様の **runtime 手順**とする。`vault-dev.yaml` は無改変。dev はインメモリ（Recreate）で
再起動時に消えるため bootstrap は再実行可能にする。`oidc_discovery_url` は `http://keycloak:8080/realms/...`
（platform-infra 内・browser は #284 手順A）。realm に confidential client `vault` を追加。

### 2. 集約後 URL・ホスト名ベース（edge は既存・無改変）

redirect は `http(s)://vault.localhost:50000/ui/vault/auth/oidc/oidc/callback`（UI）＋`http://localhost:8250/oidc/callback`
（CLI）。edge admin:50000 は現状 http だが、将来の TLS 化に備え http/https 両方を realm と Vault role の
`allowed_redirect_uris` に登録する。`vault.localhost` の Ingress は #357 で追加済み（本 PR では無改変）。

### 3. fail-safe policy：external group で role→policy、未マッピングは default のみ

`vault` client 固有の protocolMapper でレルムロールを `groups` クレーム（multivalued・id_token/userinfo）へ発行する。
Vault OIDC role は `groups_claim=groups`・`token_policies=default`（最小・secret アクセス無し）を既定にする。
`platform-admin`/`platform-operator` は Vault の **external group**（group-alias で OIDC accessor に紐付け）経由で
`admin`/`operator` policy を得る。**external group に無いユーザーは default policy のみ＝no secret access（deny 相当・
fail-safe）**。root トークンは break-glass として残す。

### 4. 秘密の非平文・opt-in・byte 等価

realm export の client secret はプレースホルダ。OIDC client secret は Secret `vault-oidc`（`k8s-local-up.sh` が
`VAULT=1` 時に dev 既定 or `VAULT_OIDC_CLIENT_SECRET` env で作成）に置き、bootstrap が `kubectl get secret` で読む
（平文コミットなし・env 上書き可）。OIDC 配線は runtime のため `vault-dev.yaml`・helm は無改変。`VAULT=1` の追加は
`vault-oidc` secret の作成のみで、`VAULT` 未設定時（既定）は挙動不変（byte 等価）。

## 影響・トレードオフ

- Vault UI/CLI が Keycloak SSO でログイン可能になる。root は break-glass。
- OIDC 設定は runtime bootstrap（vault CLI・要 jq）。適用前・external group 未設定時は全 OIDC ユーザーが default のみ
  ＝secret アクセス不可（安全側）。dev インメモリのため再起動後は再 bootstrap が必要（README 明記）。
- 変更は realm 追加＋`VAULT=1` の secret 追加＋runtime 手順に閉じる。grafana/argocd/minio・edge・vault-dev.yaml は無改変。

## 代替案

- **Vault dev-mode の env で OIDC 設定**: Vault は OIDC を env で設定できない（auth method は API 設定）ため不可。
- **role に直接 policy を固定（全 OIDC ユーザー同一）**: 粒度が無く role 踏襲・fail-safe に反するため却下。external group を採用。
- **client secret を bootstrap に平文**: 非平文原則に反する。Secret + env 上書きに一元化。
- **UI callback を https のみ登録**: edge admin:50000 は現状 http のため http も登録（不一致で OIDC が失敗するのを防ぐ）。

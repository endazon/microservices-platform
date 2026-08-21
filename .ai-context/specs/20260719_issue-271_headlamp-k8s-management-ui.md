---
title: Headlamp を k8s 管理 UI として dev クラスタへ導入し Keycloak OIDC でログインする（Issue #271）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0008
  - IADR-0066
  - IADR-0076
  - IADR-0080
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md (実行基盤 = k3s)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR 運用性・可観測性)
related_specs:
  - "../adr/IADR-0080_headlamp-k8s-management-ui.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md"
  - "../../docs/operations/operations.md"
  - "../../docs/security/security.md"
  - "../../deploy/local/README.md"
---

# 仕様書: Headlamp を k8s 管理 UI として dev クラスタへ導入する（Issue #271）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): なし（運用・開発基盤ツールの追加。プロダクト機能ではない）
- 非機能要件(NFR): 運用性・可観測性（クラスタ状態の把握・トラブルシュートの容易性）
- 関連 ADR（計画）: ADR-0008（計画リポ）（実行基盤 = k3s）
- 実装判断: [IADR-0080](../adr/IADR-0080_headlamp-k8s-management-ui.md)（本 PR: Headlamp 導入方式＝dev 専用 raw manifest の opt-in オーバーレイ／OIDC token passthrough／fail-safe RBAC）／[IADR-0066](../adr/IADR-0066_local-k8s-dev-environment.md)（ローカル k8s dev 環境・`deploy/local/`・`developer` ユーザー）／[IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md)（ブラウザ OIDC issuer 統一の手順A）
- Issue: #271（本 issue）／前提 #266・PR #267（IADR-0066）

## 背景・課題

ローカル k8s dev 環境（[IADR-0066](../adr/IADR-0066_local-k8s-dev-environment.md) / #266 / #267）は `platform-infra` / `microservices-platform` /
`ai-stock-trading` の 3 namespace にワークロードを立てるが、状態把握・障害切り分けの GUI が無く、
操作は `kubectl` / `port-forward`（[`deploy/local/README.md`](../../deploy/local/README.md)）に限られる。
[Headlamp](https://headlamp.dev/)（CNCF Sandbox の k8s UI・OIDC 対応）を dev に導入し、Pod / Deployment /
Service / ログ等をブラウザから可視化・操作できるようにする。

制約（IADR-0066 の既知課題）: dev 環境は Keycloak issuer を in-cluster 正準名 `http://keycloak:8080` に固定して
おり（サービス間 JWT 用）、**ブラウザからの OIDC ログインは hostname/ingress の別途調整が必要**と
`deploy/local/README.md` に明記されている。Headlamp はブラウザ UI から OIDC を行うため、この issuer/hostname
到達性を解く必要がある。#284（[IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md)）が同一課題を **手順A**（hosts＋port-forward で browser/cluster が
同一 issuer を共有）で確立済みで、本 issue はこれに整合させる。

## 受け入れ基準（本 PR＝リポ内で静的に検証完結する範囲）

1. **Headlamp デプロイ資産が dev 専用に存在する**。`deploy/local/headlamp/`（kustomization ＋ manifest）に
   ServiceAccount / Deployment（Headlamp・`-in-cluster` ＋ OIDC 引数）/ Service / RBAC を定義し、
   `kubectl apply -k deploy/local/headlamp --dry-run=client` がエラー無く通る。namespace は `platform-infra`。
2. **opt-in ゲート**。`scripts/k8s-local-up.sh` に `HEADLAMP=1` env ゲートを `OBSERVABILITY`/`VAULT`/`ARGOCD`
   と同型で追加する。既定（env 未設定）では Headlamp 資産を一切適用せず、既存の [1/7]..[7/7] 挙動は不変。
3. **realm に Headlamp 用 OIDC クライアントが単一情報源として追加される**。
   `deploy/keycloak/microservices-platform-realm.json` に client `headlamp`（confidential・standardFlow・
   redirectUris = Headlamp URL）を**追記のみ**で追加し、既存 client（`wiki-js`/`bff`/`spa-web`/
   `ai-stock-trading-kb-writer`）は不変。client の `description` は 255 文字以内で、
   `node scripts/check-realm-constraints.js` が緑。
4. **fail-safe RBAC**。Headlamp の ServiceAccount には広域権限を与えない（OIDC ログイン無しでは
   クラスタ可視化不可）。`developer`（=platform-admin dev スーパーユーザー）向けの ClusterRoleBinding を
   静的に同梱し、live で API server の OIDC 検証フラグが入れば即リソース閲覧/操作できる状態にする。
5. **ブラウザ OIDC 到達性の手順**が [IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md) 手順A に整合する形で `deploy/local/README.md` に記録される
   （hosts に `keycloak` を足し、Keycloak を port-forward して browser/cluster が `http://keycloak:8080` を
   共有する。加えて Headlamp の port-forward と OIDC callback URL を明記）。
6. **本番像 不変**。`deploy/helm` / `deploy/argocd` / `deploy/docker-compose.yml` を一切変更しない。
7. **既存 CI が緑**。`node scripts/check-realm-constraints.js`・`node scripts/check-image-mapping.js`
   （実ファイル＋ `--self-test`）・`node scripts/scripts.test.js`・`helm lint`/`helm template`（本番像・経路B）が
   全て緑（Headlamp は upstream 公開イメージのため image-mapping ドリフトに影響しない）。

## live 依存（本 PR の外・別手順で分離）

- **実ブラウザでの OIDC ログイン・リソース閲覧疎通**は稼働 k3d 依存。特に **k8s API server が OIDC トークンを
  検証**するには、クラスタを OIDC 用 apiserver フラグ（`--oidc-issuer-url` 等）付きで (再)作成する必要があり、
  これは稼働環境の手順。README に手順（手順A ＋ apiserver OIDC フラグ）を記し、本 PR は静的検証で完結させる。
- PR は `Refs #271` とし、live 疎通の完了は本 issue のコメントで追う。フォローアップ（本番導入の是非・
  apiserver OIDC 恒久配線）は優先度ラベル付きで別 issue に起票する。

## 実装方針

- **導入方式＝dev 専用 raw manifest の opt-in オーバーレイ**（Helm chart 非採用）。理由: `deploy/local/` の既存
  opt-in（observability/vault）は raw manifest ＋ kustomize で `k8s-local-up.sh` の env ゲートから適用する形に
  統一されており、Headlamp もこれに倣うのが最小・一貫。Helm chart 依存（repo 追加・values 二重管理）は
  dev 専用ツールには過剰（[IADR-0080](../adr/IADR-0080_headlamp-k8s-management-ui.md) で選定理由）。
- **認証＝OIDC token passthrough**。Headlamp を `-in-cluster` で起動し、ログイン後は利用者の id_token を
  API server の Bearer として委譲する。Headlamp 自身の SA には広域権限を与えず（fail-safe・匿名可視化を防ぐ）、
  authz は利用者トークンが担う。`developer` の OIDC アイデンティティ（`oidc:developer`）に `cluster-admin`
  を bind する ClusterRoleBinding を同梱（developer は既に platform-admin の dev スーパーユーザー。
  権限分離検証は非スコープ＝`poc-*` の役割）。
- **realm client**: `headlamp`（confidential・`standardFlowEnabled`・`publicClient:false`・dev シークレット・
  redirectUris = `http://localhost:4466/*`）。scopes は `openid profile email`（k8s username=`preferred_username`。
  groups claim には依存しない）。
- **manifest 配置**: `platform-infra` namespace（observability/grafana と同位置。`k8s-local-down.sh` の
  k3d 経路はクラスタ削除・Rancher 経路は platform-infra 削除で撤去される）。

## テスト

- `kubectl apply -k deploy/local/headlamp --dry-run=client -o yaml`（構文・参照整合）。
- `node scripts/check-realm-constraints.js`（realm 長さ制約・description ≤255）。
- `node scripts/check-image-mapping.js`（実ファイル）／`--self-test`／`node scripts/scripts.test.js`（ドリフト 0）。
- `helm lint deploy/helm/microservices-platform` ／ `helm template`（本番像・経路B）が緑（本 PR は chart 不変だが回帰確認）。

## 変更ファイル

- 追加: `deploy/local/headlamp/`（`kustomization.yaml`・`headlamp.yaml`）、`docs/adr/IADR-0080_headlamp-k8s-management-ui.md`、本仕様書、`feedback/20260719_headlamp-k8s-management-ui.md`。
- 変更: `deploy/keycloak/microservices-platform-realm.json`（client `headlamp` 追記のみ）、`scripts/k8s-local-up.sh`（`HEADLAMP=1` ゲート）、`deploy/local/README.md`（導線・手順A・OIDC 到達性）、`docs/operations/operations.md`（Headlamp 運用手順）、`docs/security/security.md`（dev-only 平文＝Headlamp client シークレット）。
- 変更なし: `deploy/helm/**`、`deploy/argocd/**`、`deploy/docker-compose.yml`、既存 realm client。

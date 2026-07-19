---
title: IADR-0080 Headlamp を dev 専用 raw manifest の opt-in オーバーレイ（`deploy/local/headlamp/`・`HEADLAMP=1`）で導入し、認証は Keycloak OIDC の token passthrough、RBAC は fail-safe（SA 無権限・`developer` に cluster-admin bind）とする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0008
  - IADR-0066
  - IADR-0076
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md (実行基盤 = k3s)"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR 運用性・可観測性)"
---

# IADR-0080: Headlamp（k8s 管理 UI）の dev 導入・Keycloak OIDC 連携

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）／ endazon（マージ判断）

## 起点・関連

- 関連する計画書 ID（機械追跡）: **NFR**（運用性・可観測性＝クラスタ状態把握とトラブルシュートの容易性）。プロダクト機能（FR/UC/SC）には紐づかない運用基盤ツールの決定。
- 関連 ADR: [ADR-0008](../../planning/projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md)（実行基盤 = k3s）／[[IADR-0066]]（ローカル k8s dev 環境・`deploy/local/` の dev 専用資産・`developer` ユーザー・opt-in オーバーレイの器）／[[IADR-0076]]（ブラウザ OIDC issuer 統一の手順A＝hosts＋port-forward で browser/cluster が同一 issuer を共有）
- Issue: MSP #271（本 issue）／前提 #266・PR #267（IADR-0066）
- 作業仕様書: [`docs/specs/20260719_issue-271_headlamp-k8s-management-ui.md`](../specs/20260719_issue-271_headlamp-k8s-management-ui.md)
- 計画フィードバック: [`feedback/20260719_headlamp-k8s-management-ui.md`](../../feedback/20260719_headlamp-k8s-management-ui.md)

## 背景・課題

ローカル k8s dev 環境（[[IADR-0066]]）はクラスタ状態把握の GUI を持たず、操作は `kubectl`/`port-forward` に
限られる。[Headlamp](https://headlamp.dev/)（CNCF Sandbox の k8s UI・OIDC 対応）を dev へ導入し、Pod/Deployment/
Service/ログ等をブラウザから閲覧・操作できるようにしたい。認証は既存の Keycloak に一元化し、**新たな認証情報を
増やさない**（既存 `developer`/`developer` を流用）。

課題は 3 点:

1. **導入方式**: Helm chart か raw manifest か。`deploy/local/` の既存 opt-in（observability/vault）との一貫性。
2. **k8s API への認証モデル**: Headlamp をどう API server に認証させるか（OIDC 委譲か SA か）。
3. **ブラウザ OIDC issuer 到達性**: issuer は in-cluster 正準名 `http://keycloak:8080` に固定されており（サービス間
   JWT 用）、ブラウザからのログインで `iss` を検証側とそろえられない（IADR-0066 の既知制約）。

## 決定

### 決定1: 導入方式 = dev 専用 raw manifest の opt-in オーバーレイ（Helm chart 非採用）

`deploy/local/headlamp/`（`kustomization.yaml` ＋ `headlamp.yaml`）に ServiceAccount / Deployment / Service /
RBAC を raw manifest で定義し、`scripts/k8s-local-up.sh` の **`HEADLAMP=1`** env ゲート（`OBSERVABILITY`/`VAULT`/
`ARGOCD` と同型）から `kubectl apply -k` する。既定（env 未設定）では一切適用されず、既存 [1/7]..[7/7] 挙動は不変。

- **根拠**: `deploy/local/` の opt-in は既に raw manifest ＋ kustomize ＋ env ゲートで統一されている
  （observability/vault）。Headlamp もこれに倣うのが最小・一貫で、`kubectl apply -k --dry-run` による静的検証も容易。
  Headlamp 公式 Helm chart は存在するが、dev 専用ツールに chart repo 依存・values 二重管理を持ち込むのは過剰。
- namespace は `platform-infra`（observability/grafana と同位置）。`k8s-local-down.sh` の k3d 経路はクラスタ削除、
  Rancher 経路は `platform-infra` 削除で Headlamp も撤去される（cluster-scoped の ClusterRoleBinding のみ残るが
  再 apply 冪等）。

### 決定2: 認証 = Keycloak OIDC の token passthrough（Headlamp SA は無権限＝fail-safe）

Headlamp を `-in-cluster` で起動し、OIDC（`-oidc-client-id`/`-oidc-client-secret`/`-oidc-idp-issuer-url`/
`-oidc-scopes`）を設定する。ログイン後、Headlamp は**利用者の id_token を API server の Bearer として委譲**する
（Headlamp 独自のローカルアカウントを作らない＝アカウントは Keycloak が一元管理）。

- **fail-safe RBAC**: Headlamp の ServiceAccount には**広域権限を bind しない**。OIDC ログイン無しでは
  クラスタ可視化ができない（匿名の SA 経由可視化を防ぐ）。authz は利用者の OIDC トークンが担う。
- **`developer` の RBAC**: OIDC アイデンティティ `oidc:developer`（apiserver フラグ `--oidc-username-prefix=oidc:`・
  `--oidc-username-claim=preferred_username` 前提）に `cluster-admin` を bind する ClusterRoleBinding を同梱。
  `developer` は既に `platform-admin` を束ねた dev スーパーユーザー（IADR-0066）で、1 アカウントで全機能を疎通確認
  する用途。**ロール別の権限分離検証は非スコープ**（それは `poc-*` の役割）。
- **scopes = `openid profile email`**。k8s の username は `preferred_username` を使い、groups claim には依存しない
  （realm に groups マッパーを増やさず、RBAC は user 単位で bind）。realm client は confidential（`publicClient:false`）
  とする。理由: Headlamp backend が authorization code を server-side で交換するため client secret を要する。

### 決定3: ブラウザ OIDC issuer 到達性 = IADR-0076 手順A に整合（realm/manifest 無改変で解く）

ブラウザと cluster が同一の issuer `http://keycloak:8080` を共有するよう、[[IADR-0076]] の**手順A**（hosts に
`127.0.0.1 keycloak` を足し、`kubectl -n platform-infra port-forward svc/keycloak 8080:8080`）に整合させる。
realm の issuer 固定・manifest は改変しない。加えて Headlamp を port-forward（`svc/headlamp 4466:80`）し、
OIDC callback URL（`http://localhost:4466/oidc-callback`）を realm client の redirectUris に含める。

- これは [[IADR-0076]] が SPA/`/bff` 向けに確立した機構を **Headlamp UI にも適用**するもので、既存 dev の issuer 設計を
  変更せずに「ブラウザからの OIDC ログイン」制約を解消する。手順は `deploy/local/README.md` に記録する。

## live 依存（本 IADR の外・別手順で分離）

Headlamp の OIDC token passthrough が実際に API server で受理されるには、**k8s API server が OIDC トークンを検証**
する必要があり、k3d/k3s を OIDC 用 apiserver フラグ（`--oidc-issuer-url=http://keycloak:8080/realms/microservices-platform`
`--oidc-client-id=headlamp`・`--oidc-username-claim=preferred_username`・`--oidc-username-prefix=oidc:`）付きで
(再)作成する必要がある。これは稼働環境の手順（README に記載）で、本 PR は静的検証（`kubectl apply -k --dry-run`/
realm-constraints/helm）で完結させる。実ブラウザログイン・リソース閲覧疎通は #271 のコメントで追う。

## 根拠・トレードオフ

- raw manifest opt-in は既存 dev 資産と一貫し、chart 依存を避ける。反面、Headlamp のバージョン更新は manifest の
  image tag 手動更新になるが、dev ツールとして許容（本番導入は別 issue で chart/GitOps を検討）。
- token passthrough は「アカウントを Keycloak に一元化」する受け入れ基準に忠実で、Headlamp に別の資格情報を
  作らない。反面 apiserver OIDC 設定（live）が前提になるが、これは k8s の OIDC 認証の本質で回避不能。SA へ広域
  権限を寄せて OIDC を UI ゲートだけにする代替案は、匿名可視化リスク・「Keycloak 一元管理」からの逸脱のため不採用。
- `developer` に cluster-admin を bind するのは強い付与だが、opt-in・dev 限定・既知スーパーユーザーで、
  ネットワークもローカルに閉じる（security.md の dev-only 方針に整合）。

## 影響

- 追加: `deploy/local/headlamp/`（`kustomization.yaml`・`headlamp.yaml`）、`docs/specs/20260719_issue-271_headlamp-k8s-management-ui.md`、`feedback/20260719_headlamp-k8s-management-ui.md`。
- 変更: `deploy/keycloak/microservices-platform-realm.json`（client `headlamp` 追記のみ・既存 client 不変）、`scripts/k8s-local-up.sh`（`HEADLAMP=1` ゲート）、`deploy/local/README.md`（導線・手順A・apiserver OIDC 手順）、`docs/operations/operations.md`（Headlamp 運用）、`docs/security/security.md`（dev-only 平文＝Headlamp client シークレット）。
- 変更なし: `deploy/helm/**`、`deploy/argocd/**`、`deploy/docker-compose.yml`。

## 代替案

- **Helm chart で導入**: repo 追加・values 二重管理が dev には過剰。→ 不採用（既存 opt-in と一貫させる）。
- **OIDC を Headlamp UI ゲートのみとし SA へ cluster-admin**: apiserver OIDC 設定不要で live 疎通は楽だが、匿名 SA
  経由の可視化リスク・「アカウントを Keycloak が一元管理」からの逸脱。→ 不採用（fail-safe・受け入れ基準優先）。
- **本番像（helm/argocd）へ同梱**: 公開範囲・RBAC・アクセス制御の設計が別問題。→ 非スコープ（まず dev で確立・
  本番導入は別 issue／計画フィードバックで論点化）。

---
title: k8s-local-up.sh の k3d クラスタ作成に apiserver OIDC 検証フラグを opt-in で配線（Headlamp live 疎通の恒久化）（Issue #328）
type: spec
status: superseded
related_ids:
  - NFR
  - ADR-0004
  - IADR-0066
  - IADR-0076
  - IADR-0080
  - IADR-0084
  - IADR-0104
author: claude
created: 2026-07-19
updated: 2026-07-26
related_specs:
  - "../adr/IADR-0104_headlamp-apiserver-oidc-blocked-on-http-issuer.md"
  - "../specs/20260726_issue-328_headlamp-apiserver-oidc-blocked.md"
  - "../adr/IADR-0084_headlamp-oidc-apiserver-flags.md"
  - "../adr/IADR-0080_headlamp-k8s-management-ui.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md"
  - "../specs/20260719_issue-271_headlamp-k8s-management-ui.md"
  - "../../deploy/local/README.md"
---

# 仕様書: k3d クラスタ作成への apiserver OIDC 検証フラグ opt-in 配線（Issue #328）

> **⚠️ 2026-07-26: 本仕様書は superseded。ここに書かれた手順を適用してはならない。**
> k8s 1.30+ は OIDC issuer に **https を強制**する一方、経路B の issuer は `KC_HOSTNAME_URL` により
> **http 固定**であり、両立し得ない。apiserver に OIDC フラグを付けると **apiserver が起動できず
> クラスタが停止する**（実測: `k3s v1.35.4`）。決定は
> [IADR-0104](../adr/IADR-0104_headlamp-apiserver-oidc-blocked-on-http-issuer.md)、後継の作業仕様書は
> [`20260726_issue-328_headlamp-apiserver-oidc-blocked.md`](20260726_issue-328_headlamp-apiserver-oidc-blocked.md)。
> 現行の正規ログイン手順は **SA トークン方式**、OIDC 化の再開は **#388**（全経路 HTTPS 化）。
> 以下の本文は 2026-07-19 時点の記録としてそのまま残す。

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): なし（運用・dev 環境の配線。プロダクト機能ではない）
- 非機能要件(NFR): 運用性（Headlamp/ブラウザ OIDC の実ログインを再作成手順の暗記なしに再現可能にする）／
  セキュリティ（認証＝Keycloak 一元管理を dev の k8s 認可まで通す）
- 関連 ADR: ADR-0004（認証＝Keycloak）。方式判断は [[IADR-0084]]。既存 [[IADR-0080]]（Headlamp 導入・
  OIDC token passthrough・RBAC＝`oidc:developer` に cluster-admin bind）／[[IADR-0066]]（経路B＝k3d dev 環境）／
  [[IADR-0076]]（issuer ホスト名＝手順A・in-cluster 正準名 `http://keycloak:8080`）。
- Issue: #328（本 issue・運用/dev・priority:should）。#271（PR #327・IADR-0080）のフォローアップ。

## 目的・背景（As-Is）

[[IADR-0080]]（#271）で Headlamp を dev へ opt-in 導入した。認証は **OIDC token passthrough**（Headlamp が利用者の
`id_token` を k8s API server の Bearer へ委譲）で、**実リソース閲覧には API server が OIDC トークンを検証**する必要が
ある。この検証は apiserver フラグ（`--oidc-issuer-url` / `--oidc-client-id` / `--oidc-username-claim` /
`--oidc-username-prefix`）で有効化するが、**これらはクラスタ作成時にしか渡せず、既存クラスタには後付けできない**。

現状これらは `deploy/local/README.md` の「Headlamp」節に **手動の `k3d cluster create` 例**として記載されているだけで、
`scripts/k8s-local-up.sh` の `k3d cluster create`（[1/7]）には配線されていない。このため `HEADLAMP=1` でオーケストレーション
しても実ログインは成立せず、利用者は手動で apiserver フラグ付きの再作成コマンドを組み立てる必要がある。

## スコープ（To-Be）

### 対象（配線する）

`scripts/k8s-local-up.sh` の k3d 経路 `k3d cluster create` に、OIDC apiserver フラグ 4 種を **opt-in** で付与する。k3d は
内蔵 k3s へ引数を委譲するため、各フラグは `--k3s-arg "--kube-apiserver-arg=<name>=<value>@server:0"` として渡す
（`@server:0` は server ノード（apiserver を持つ）へのノードフィルタ）。

| apiserver フラグ | 値 | 根拠 |
| --- | --- | --- |
| `oidc-issuer-url` | `http://keycloak:8080/realms/microservices-platform` | in-cluster 正準名（[[IADR-0076]] 手順A・[[IADR-0066]] と整合） |
| `oidc-client-id` | `headlamp` | realm client `headlamp`（#271 で追加済み・token の `aud`） |
| `oidc-username-claim` | `preferred_username` | 下記 claim マッピング |
| `oidc-username-prefix` | `oidc:` | 下記 claim マッピング |

### claim マッピング（#271 の RBAC に対応させる）

#271 の `deploy/local/headlamp/headlamp.yaml` の `ClusterRoleBinding`（`headlamp-developer-cluster-admin`）は
`subjects: [{ kind: User, name: "oidc:developer" }]` を bind している（**username subject**・group ではない）。
したがって apiserver 側は `username-claim=preferred_username`＋`username-prefix=oidc:` を用い、Keycloak の
`preferred_username=developer` を k8s ユーザー `oidc:developer` にマップして既存 bind に一致させる。

`groups-claim` は #271 が group を一切 bind していないため付与しても **inert**（有効な認可に寄与しない）となる。#271 の
RBAC（realm/manifest）は本 issue の非スコープで無改変とするため、**最小配線として username-claim/prefix のみを付与する**。
ロール/グループ別の権限分離は [[IADR-0080]] 決定2どおり `poc-*` の役割で、`developer` は dev スーパーユーザー。

### 有効化（opt-in・既定オフ・後方互換）

- 新 env `HEADLAMP_OIDC_APISERVER`（既定＝`HEADLAMP` の値に追従）。`HEADLAMP=1` で live 経路を一括有効化でき、
  `HEADLAMP_OIDC_APISERVER=1` 単独で（Headlamp を deploy せずに）フラグのみ付与、`HEADLAMP_OIDC_APISERVER=0` で
  `HEADLAMP=1` でもフラグを付けない escape-hatch（既存クラスタ再利用時など）。
- **既定（両 env 未設定）は `k3d cluster create` が現行とバイト等価**（挙動不変・fail-safe・後方互換）。
- issuer/client は `HEADLAMP_OIDC_ISSUER_URL` / `HEADLAMP_OIDC_CLIENT_ID` で上書き可（既定は上表）。
- **既存クラスタ再利用**時に OIDC 有効化が要求されたら、apiserver フラグは後付け不可のため **再作成を促す WARN** を出す
  （`k3d cluster delete <cluster>` → 再実行）。fail-safe: クラスタは壊さない（削除は利用者判断）。

### Rancher Desktop（内蔵 k3s）経路

`scripts/k8s-local-up.sh` は Rancher の k8s を**作成しない**（既存 context を使う）ため、apiserver フラグはスクリプトから
付与できない。同等手順（k3s の `--kube-apiserver-arg` を Rancher の override 設定 or provisioning で与える）を
`deploy/local/README.md` にドキュメントとして追記する。

## 受け入れ基準（Acceptance Criteria）

1. `HEADLAMP=1`（もしくは `HEADLAMP_OIDC_APISERVER=1`）で新規に k3d クラスタを作成すると、apiserver に上表 4 フラグが
   付与される（`--k3s-arg "--kube-apiserver-arg=...@server:0"`）。
2. **既定（両 env 未設定）は現行 `k3d cluster create` と完全に同一**（後方互換・fail-safe）。
3. issuer は in-cluster 正準名 `http://keycloak:8080/realms/microservices-platform`（[[IADR-0076]] 手順A整合）。
4. claim マッピングが #271 の `ClusterRoleBinding`（`oidc:developer` = User）に一致（username-claim/prefix）。realm.json・
   headlamp manifest は無改変。
5. 既存クラスタ再利用時に OIDC 有効化が要求されたら再作成を促す WARN を出す（クラスタは破壊しない）。
6. ブラウザからの疎通手順（手順A ＋ 再作成）と Rancher Desktop の同等手順を `deploy/local/README.md` に明記。
7. 設計判断（issuer 到達性・claim マッピング・opt-in ゲート）を [[IADR-0084]] に記録。ADR 索引 README は自分の 1 行のみ追記。
8. 検証: `bash -n scripts/k8s-local-up.sh` 構文 OK・lint 緑・`scripts/scripts.test.js` 緑。既存 CI（#275 image-mapping
   ドリフト・doc-links・commit-messages・realm-constraints）を非回帰で緑。

## 非スコープ

- datasource（#305）／ values の他サービスブロック／ frontend・edge／ infra 永続化（#324）／ realm client 定義
  （#271 で追加済み・`realm.json` の client 中身不変）には触れない。
- 本番像（`deploy/helm` / `deploy/argocd` / `deploy/docker-compose.yml`）は不変。
- **実ブラウザでの `developer` OIDC 実ログイン → Pod/Deployment/Service/ログ閲覧の end-to-end 疎通**は稼働 k3d 依存＝
  live。PR で手順を明記し `Refs #328` で残す（#271 の live 受け入れの最終確認も同様）。

## 影響・リスク

- apiserver に OIDC フラグを付けても、issuer（`http://keycloak:8080`）が起動直後は未到達でも **apiserver はブロックせず
  背景で OIDC メタデータ取得をリトライする**（SA トークン認証は不変）。よって OIDC 有効クラスタでも通常運用は成立する。
- フラグはクラスタ作成時のみ有効＝**既存クラスタ再利用では反映されない**（WARN で再作成を案内）。これは k3d/k3s の制約で
  本配線の設計前提。
- 既定オフのため、Headlamp を使わない利用者・CI へは一切影響しない。

---
title: Kubernetes 管理 UI（Headlamp・Keycloak OIDC 認証）を運用設計へ明記し、本番導入是非を論点化する
type: plan-feedback
status: open
category: 新たな制約(ADR要)
related_ids:
  - NFR
  - ADR-0008
source_repo: microservices-platform
source_ref: "Issue #271 / branch feat/issue-271-headlamp-oidc / docs/adr/IADR-0080_headlamp-k8s-management-ui.md"
author: claude
created: 2026-07-19
---

# フィードバック: Kubernetes 管理 UI = Headlamp（Keycloak OIDC）を運用設計へ明記する

## 種別

新たな制約(ADR要)。計画書に存在しない運用ツール（k8s 管理 UI）の導入判断を実装側（[[IADR-0080]]）で行ったため、
計画側の運用設計・運用ツール選定へ環流する。

## 起点となる計画書

- 機能要求（FR）: なし（運用・開発基盤ツール。プロダクト機能ではない）
- 非機能要件（NFR）: 運用性・可観測性（クラスタ状態把握・トラブルシュートの容易性）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: `ADR-0008`（実行基盤 = k3s）／実装側 `IADR-0080`・`IADR-0066`・`IADR-0076`
- 計画書リンク:
  - `planning/projects/microservices-platform/02_requirements/01_requirements.md`（NFR 運用性）
  - `planning/projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md`

## 現状（計画書の記述 / As-Is）

計画書は実行基盤（k3s・ADR-0008）・GitOps（ArgoCD・ADR-0007）・可観測性バックエンド（Grafana/Tempo/Loki 等）は
規定するが、**クラスタの状態把握・操作を行う管理 UI（k8s ダッシュボード）についての運用設計・ツール選定は未記載**。
運用性 NFR を満たす具体手段としての「管理 UI」が計画に存在しない。

## 問題点 / あるべき姿（To-Be）

dev 環境で `kubectl`/`port-forward` のみに依存すると、AI/人間ともにクラスタ状態の把握・障害切り分けのコストが高い。
運用性 NFR の裏付けとして、**k8s 管理 UI を Headlamp（Keycloak OIDC 認証・アカウントは Keycloak が一元管理）で
提供する**方針を運用設計に明記すべき。認証は既存 IdP に一元化し、ツール個別の資格情報を増やさない原則も併記する。

## 実装で判明した経緯

Issue #271 で dev クラスタへ Headlamp を opt-in 導入するにあたり、導入方式（raw manifest の opt-in オーバーレイ）・
認証（OIDC token passthrough）・RBAC（fail-safe・`developer` に cluster-admin bind）を [[IADR-0080]] で決定した。
これは計画に無い運用ツールの選定であり、計画側の運用設計と整合を取る必要がある。あわせて、ブラウザ OIDC の
issuer/hostname 到達性（IADR-0066 の既知制約）を [[IADR-0076]] 手順A で解く前提も明らかになった。

## 提案（計画への反映案）

- 反映先候補: 要求更新（NFR 運用性の具体手段）/ 新 ADR（運用ツール選定）/ その他（運用設計ドキュメント）
- 提案内容:
  1. 運用設計に「**Kubernetes 管理 UI = Headlamp（Keycloak OIDC 認証）**」を明記し、運用性 NFR の裏付けとする。
  2. k8s ランタイム ADR（ADR-0008）との整合を確認し、必要なら「運用ツール選定（k8s 管理 UI）」の計画 ADR を起票する。
  3. **本番環境への Headlamp 導入是非**を計画側の論点として提起する（公開範囲・アクセス制御・RBAC の権限分離・
     監査・ネットワーク露出）。dev は `developer` スーパーユーザー疎通に限定し、本番は別途 RBAC 設計が要る。

## 影響範囲

- 運用設計・運用ツール選定（新規記述）。実装は dev 専用（`deploy/local/`）に閉じ、本番像（helm/argocd/compose）は不変。
- 本番導入を進める場合、Headlamp の配備方式（Helm/GitOps）・OIDC apiserver 恒久配線・RBAC 権限分離が新たな設計対象。
- トレーサビリティ: 本フィードバックのリンクを Issue #271 と `IADR-0080` に残す（相互参照）。

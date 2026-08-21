---
title: 作業仕様書 — 本番実行基盤の段階配備（k3s → Istio mTLS → ArgoCD/Harbor）
type: spec
status: in-progress
related_ids:
  - NFR
  - ADR-0005
  - ADR-0007
  - ADR-0008
author: claude
created: 2026-07-07
updated: 2026-07-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md
  - planning:projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/06_technical/06_migration-roadmap.md
related_specs:
  - ../../docs/tech/tech-requirements.md
  - ../../docs/security/security.md
  - ../../docs/operations/operations.md
  - ../adr/IADR-0017_internal-service-auth-network-isolation.md
  - ../adr/IADR-0026_mesh-mtls-supersedes-network-isolation.md
related_adrs:
  - ADR-0005 (Istio / mTLS)
  - ADR-0007 (ArgoCD + Helm + Harbor)
  - ADR-0008 (Kubernetes / k3s)
  - IADR-0017 (ネットワーク分離を第一防御 — 本作業で Superseded)
  - IADR-0026 (mesh mTLS が IADR-0017 を Supersede)
---

# 作業仕様書: 本番実行基盤の段階配備（k3s → Istio mTLS → ArgoCD/Harbor）

Issue: #100（`[NFR | ADR-0005/0007/0008] 本番実行基盤を配備する`）

## 目的

計画 ADR-0005/0007/0008（いずれも 2026-07-06 Accepted）の確定を受け、本番実行基盤を
**宣言的（Infrastructure as Code / GitOps）に配備可能な構成として整備**する。依存順は
k3s → Istio mTLS → ArgoCD/Harbor。段階2（Istio STRICT mTLS）の適用をもって暫定運用
（IADR-0017: ネットワーク分離を第一防御）を解消し、後継 IADR-0026 で記録する。

## 本作業の成果物の範囲（重要）

本リポジトリの CI/実装環境では**実クラスタ（k3s）を起動できない**。したがって本作業は
「実クラスタへ `kubectl apply` 済みであること」ではなく、**Git を単一の真実源とする配備構成
（マニフェスト・Helm テンプレート・ArgoCD Application）と配備・検証手順、回帰テストの整備**を
成果物とする。実クラスタへの適用は、整備した手順（`deploy/istio/README.md`・
`deploy/argocd/README.md`・`deploy/secrets/README.md`）に従い運用者が実施する。

これは ADR-0007（GitOps: Git を単一の真実源とし ArgoCD が宣言的に同期）の思想と一致する。
「マニフェストが Git 上に宣言的に存在し ArgoCD が同期する」ことが到達目標であり、手動 kubectl
適用を運用の常態にしないことが受け入れ基準である。

## 段階と成果物

### 段階1: k3s（ADR-0008）

- `deploy/helm/knowledge-platform/templates/namespace.yaml`
  — `knowledge-platform` Namespace。`istio-injection: enabled` ラベル（段階2の前提）。
- `deploy/helm/knowledge-platform/templates/networkpolicy.yaml`
  — デフォルト拒否 + 明示許可の NetworkPolicy。IADR-0017 が「helm 追補はフォローアップ」
    としていた ClusterIP + NetworkPolicy（デフォルト拒否）を実体化。mesh 配下でも多層防御として維持。
- `deploy/secrets/`（`README.md` ＋ `*.example.yaml`）
  — LLM API キー・DB 資格情報・レジストリ Pull Secret の投入手順とテンプレート
    （**実値はコミットしない**。プレースホルダのみ）。
- `values.yaml` に `namespace` / `networkPolicy` / `imagePullSecrets` ブロックを追加。

### 段階2: Istio mTLS（ADR-0005）

- `deploy/helm/knowledge-platform/templates/istio-mtls.yaml`
  — `PeerAuthentication`（`mtls.mode: STRICT`、namespace スコープ）＋
    `DestinationRule`（`trafficPolicy.tls.mode: ISTIO_MUTUAL`）。
    サービス間通信を平文なしで暗号化・相互認証する。
- `deploy/istio/README.md`
  — istioctl による Istio 導入、サイドカー注入、Kiali 配備、STRICT mTLS の検証手順。
- `values.yaml` に `mesh`（`enabled` / `mtlsMode`）ブロックを追加。
- **IADR-0017 を Superseded** 化し、後継 **IADR-0026** を起票（mesh mTLS が第一防御）。
- `NetworkIsolationTests` を mTLS 前提の回帰テストへ更新（`MeshMtlsTests` を追加）。

### 段階3: ArgoCD + Harbor（ADR-0007）

- `deploy/argocd/appproject.yaml` — `knowledge-platform` AppProject（配備先・ソースを制約）。
- `deploy/argocd/application.yaml` — 本リポジトリの Helm チャートを同期する Application。
- `deploy/argocd/README.md` — ArgoCD ブートストラップ・Harbor 連携手順。
- Harbor: `values.yaml` の `global.image.registry: harbor.internal` を維持し、
  `imagePullSecrets` により Harbor から Pull する構成を実体化。

## 実装 ID トレーサビリティ

- ブランチ: `claude/issue-100-20260707-1319`（初版・CI 生成）→ `feat/issue-100-production-runtime`（ローカル検証・PR 用）
- コミット: `feat(ADR-0008): ...` / `feat(ADR-0005): ...` / `feat(ADR-0007): ...` 等、段階ごとに起点 ID を付す。
- コード/マニフェスト: 各ファイル冒頭コメントに ADR-ID を残す。

## 受け入れ基準（Issue 準拠）

- [ ] 全サービスが k3s 上で稼働し、サービス単位でデプロイ・ロールバックできる（配備構成＋手順で担保）
- [ ] サービス間通信が STRICT mTLS で暗号化される（`PeerAuthentication STRICT` を Git 上に宣言）
- [ ] IADR-0017 の暫定運用が解消され、後継 IADR-0026 で記録される（計画リポへ環流）
- [ ] ArgoCD 経由のデプロイが Git の状態と同期する構成（Application/AppProject を宣言、手動 kubectl 依存を排す）

## 検証

- `helm lint deploy/helm/knowledge-platform`
- `helm template deploy/helm/knowledge-platform`（PeerAuthentication STRICT / DestinationRule ISTIO_MUTUAL がレンダリングされること）
- `dotnet test`（`MeshMtlsTests` / `NetworkIsolationTests`）
- 実クラスタ検証（運用者）: `istioctl authn tls-check` / `istioctl proxy-config` で平文フォールバックが無いことを確認。

### ローカル検証結果（2026-07-07, ブランチ `feat/issue-100-production-runtime`）

CI ジョブでは `helm`/`dotnet` が未許可のため未実走だった検証を、実装作業リポジトリのローカル環境（Windows / Rancher Desktop / helm・kubectl・dotnet 10.0.301）で実走し、いずれも合格した。

- `helm lint deploy/helm/knowledge-platform` → **成功**（0 failed）。
- `helm template kp deploy/helm/knowledge-platform` → **成功**。以下の宣言的構成のレンダリングを確認:
  - `PeerAuthentication` に `mode: STRICT`（既定）／`--set mesh.mtlsMode=PERMISSIVE` で移行モードへ切替可能。
  - `DestinationRule` に `mode: ISTIO_MUTUAL`。
  - Namespace に `istio-injection: enabled`／`--set mesh.enabled=false` で Istio リソースが消えることを確認。
  - `NetworkPolicy`（デフォルト拒否）2 件。
  - `imagePullSecrets` は既定 `[]` で非出力、`--set imagePullSecrets[0].name=harbor-pull` で各 Deployment に出力されることを確認（Harbor Pull）。
- `dotnet test --filter Category=Deployment` → **合格 6 / 6**（`MeshMtlsTests` 4・`NetworkIsolationTests` 2）。
- ドキュメント整合: IADR-0017 が `status: Superseded` / `superseded_by: IADR-0026`、`docs/adr/README.md` の索引も更新済みであることを確認。
- ArgoCD `Application.spec.source.targetRevision: main` は本プロジェクト規約の安定版ブランチ（`main` 実在を確認）を指し妥当。

> 実クラスタ上での mTLS 到達検証（`istioctl authn tls-check`）と k3s 稼働は、実行基盤を持つ運用者フェーズで実施する（本 Issue の到達目標は「Git 上に宣言的構成が存在し ArgoCD が同期する」こと）。

### レビュー対応（2026-07-07・AI コードレビュー指摘反映）

PR #109 の AI コードレビュー指摘（🟡推奨・🟢軽微）を以下のとおり反映した。いずれも宣言的マニフェスト／ドキュメントの是正で、mTLS・GitOps の受け入れ基準に影響しない。

- **NetworkPolicy `allow-intra-namespace` の意図明確化**（`templates/networkpolicy.yaml`）: `ingress.from` を `namespaceSelector`（汎用 Helm ラベル一致）＋`podSelector: {}` の OR 併記から、`podSelector: {}` 単独へ変更。k8s 仕様上 `podSelector: {}` は「同 Namespace の全 Pod」を意味し意図を満たす。同ラベルを持つ別 Namespace からの ingress を意図せず許可し得る `namespaceSelector` エントリを削除（多層防御の過剰許可を排除）。
- **ArgoCD README に段階順序の前提を明記**（`deploy/argocd/README.md`）: 既定 `mesh.enabled: true` では Istio CRD（`PeerAuthentication`/`DestinationRule`）を要するため、ArgoCD 同期前に段階2（Istio 導入）完了が前提であること、未導入時は `mesh.enabled: false` で無効化する旨を追記。
- **AppProject の許可リソース種別を最小化**（`deploy/argocd/appproject.yaml`）: `namespaceResourceWhitelist` を `group: "*"/kind: "*"` の全許可から、チャートが実際にレンダリングする種別（Deployment/Service/PersistentVolumeClaim/Ingress/NetworkPolicy/PeerAuthentication/DestinationRule）へ限定（最小権限）。
- **IADR 番号の整合**: 本文・コード（`docs/adr/README.md`・`docs/security/security.md`・テストコメント）は `develop` マージ後の採番衝突解消で **IADR-0026** に統一済み（旧 IADR-0024 は develop 側 MinIO ADR に採番済みのため）。PR #109 本文の記述は運用者側で `IADR-0026` へ読み替え。

> 本レビュー対応コミット時点の CI/ヘッドレス実行環境では `helm`/`dotnet` がツール許可外のため再実走できていない。上記変更は既存の宣言的テンプレートの局所修正であり、ローカル環境（helm・dotnet 10.0.301）での `helm lint`/`helm template`/`dotnet test --filter Category=Deployment` 再実走で最終確認することを推奨する。

## リスク・注意事項

- 実クラスタでの Istio 導入時は、STRICT mTLS を一括適用する前に `PERMISSIVE` で移行を確認する運用が安全。
  本チャートは `mesh.mtlsMode` で切替可能とし、既定は `STRICT`（To-Be）とする。
- Secret（LLM/DB/registry）は Git にコミットしない。Sealed Secrets / External Secrets の導入は恒久フェーズの課題。
- 恒久像（全 API OIDC/JWT）への移行は本 Issue の範囲外だが、IADR-0026 と計画 NFR 注記で移行方針を明記する。

## 完了条件（Definition of Done 参照）

`docs/DEFINITION_OF_DONE.md` 準拠。特に helm lint/template 成功・テスト pass・仕様書とトレーサビリティの整備。

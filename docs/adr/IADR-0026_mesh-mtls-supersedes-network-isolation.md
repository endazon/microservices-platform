---
title: IADR-0026 Istio STRICT mTLS をサービス間認証の第一防御とし、IADR-0017（ネットワーク分離）を解消する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-05
  - ADR-0004
  - ADR-0005
  - ADR-0007
  - ADR-0008
author: claude
created: 2026-07-07
updated: 2026-07-07
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md（暫定運用の注記）"
related_specs:
  - ../specs/20260707_issue-100_production-runtime-k3s-istio-argocd.md
  - ../security/security.md
related_adrs:
  - IADR-0017 (ネットワーク分離を第一防御 — 本 IADR で Superseded)
  - IADR-0000 (実装判断を記録する)
supersedes:
  - IADR-0017
---

# IADR-0026: Istio STRICT mTLS をサービス間認証の第一防御とし、IADR-0017 を解消する

- 状態: Accepted
- 日付: 2026-07-07
- 決定者: claude（実装）
- 関連: NFR（機密性）、FR-05、ADR-0005、ADR-0004、ADR-0007、ADR-0008、Issue #100
- Supersedes: **IADR-0017**（mesh 導入までのサービス間認証はネットワーク分離を第一防御とする）

## コンテキストと課題

IADR-0017 は、サービスメッシュ（ADR-0005）が **Proposed（未 Accepted）かつ未配備**であった期間の
**暫定運用**として、「ネットワーク分離（ホスト非公開）を第一防御」とし、サービス間の平文通信を
クラスタ／コンテナネットワーク内に限定して受容していた。その残余リスク（**同一ネットワーク内からの
内部 API への無認証到達**）の恒久的解消は、ADR-0005 の Accepted 化と mTLS の実装に従属していた。

2026-07-06、計画側で ADR-0005（Istio / mTLS）・ADR-0007（GitOps）・ADR-0008（k3s）が
**Accepted に確定**した。これを受け Issue #100 で本番実行基盤を配備し、サービス間通信へ
**STRICT mTLS** を適用できるようになった。よって IADR-0017 が前提としていた「mesh 未配備」という
条件が解消され、暫定運用を恒久運用へ移行する。

## 決定

サービス間認証の**第一防御（primary control）を Istio の STRICT mTLS**（ADR-0005）とし、
IADR-0017 の暫定運用（ネットワーク分離を第一防御）を**解消**する。IADR-0017 は Superseded とする。

### 1. STRICT mTLS を宣言的に強制する（第一防御）

Helm チャート（`deploy/helm/knowledge-platform/templates/istio-mtls.yaml`）で、
`knowledge-platform` Namespace に以下を適用する:

- `PeerAuthentication`（`mtls.mode: STRICT`）— ワークロードが受け付ける接続を mTLS のみに限定。
  平文（plaintext）フォールバックが無く、サイドカー未注入クライアントからの平文到達を拒否する。
- `DestinationRule`（`trafficPolicy.tls.mode: ISTIO_MUTUAL`）— 送信側 TLS をメッシュ証明書での
  相互 TLS に固定する。

サイドカー注入は Namespace ラベル `istio-injection: enabled`（`namespace.yaml`）で自動化する。
これにより、アプリ実装（トークン非保持のバックグラウンドワーカーを含む）を変更せずに、
サービス間通信の**暗号化と相互認証**を横断的に担保する — IADR-0017 が「mTLS 導入で不要になる作業」
として見送った client credentials 実装は、mTLS がワークロード ID を保証することで恒久的に不要となる。

### 2. ネットワーク分離は「多層防御（defense-in-depth）」へ格下げして維持する

IADR-0017 のネットワーク分離（ホスト非公開・ClusterIP）は**第一防御ではなくなる**が、多層防御として維持する:

- Kubernetes: `NetworkPolicy`（デフォルト拒否 + 同 Namespace 許可、`networkpolicy.yaml`）を実体化。
  IADR-0017 が「helm 追補はフォローアップ」としていた項目をここで達成する。
- docker-compose（ローカル開発ランタイム）: 内部アプリサービスは host 非公開（`expose` のみ）を維持し、
  BFF をアプリのエッジ入口として host 公開する（`NetworkIsolationTests` は多層防御の回帰として存続。
  位置づけを「第一防御」から「多層防御」へ更新）。
  - **改定（[IADR-0032](./IADR-0032_wikijs-dev-exposure-opt-in.md)・#124）**: dev の compose に限り、Wiki.js
    管理 UI セットアップ（OIDC 構成・ロケール導入・API キー発行）の便宜のため `wiki-js`(3001) の
    host 公開を許容する（フロントエンド SPA エッジ `frontend`(3100) も同様。IADR-0033・別 PR #126）。
    dev は単一開発者のローカルランタイムであり、ABAC の第一防御は本番系（mesh mTLS /
    ネットワーク分離）が担う。**本番系（Helm）では Wiki.js を Ingress 公開しない**（`wikijs.ingress.enabled: false`）
    ことを `NetworkIsolationTests` が回帰ガードし、迂回到達を機械的に塞ぐ。当初の「BFF のみ host 公開」制約は
    この dev 便宜の範囲で IADR-0032 が改定する。

### 3. mTLS 前提の回帰テストを追加する

`MeshMtlsTests`（`src/Tests/.../Deployment/MeshMtlsTests.cs`）を追加し、Helm の Istio マニフェストが
`PeerAuthentication STRICT` と `DestinationRule ISTIO_MUTUAL` を宣言していること（平文許容 `PERMISSIVE`/
`DISABLE` へ後退していないこと）を回帰として固定する。

### 4. 恒久像（全 API OIDC/JWT）への移行方針

計画 NFR「暫定運用の注記」の恒久像は「全 API で OIDC/JWT 認証 ＋ サービス間 mTLS」である。
本 IADR は**サービス間 mTLS** を達成する。**全 API の OIDC/JWT 認証**（エッジ BFF に加え内部 API での
トークン検証）は、mTLS がワークロード認証を担保する前提の下で段階的に進める別課題とし、
本 IADR の範囲外とする（フォローアップ Issue で追跡）。当面のユーザー認可（FR-05 ABAC）は
エッジ（BFF）＋各サービスの ABAC 強制（IADR-0004/0012 等）で担保される方針を継続する。

## 検討した選択肢

- **A. ネットワーク分離のまま恒久運用**: ADR-0005 が Accepted となった今、暗号化・相互認証を欠く
  平文通信を恒久運用とするのは NFR（機密性）に反する。**不採用**。
- **B. STRICT mTLS を第一防御にする（本決定）**: ADR-0005 の確定・配備を受けた恒久像。
  平文フォールバックを排し、アプリ非依存でサービス間の暗号化・相互認証を徹底できる。**採用**。
- **C. PERMISSIVE mTLS で当面運用**: 平文とmTLSを併存させ移行を容易にするが、平文到達を許すため
  恒久像としては不十分。**移行期の一時措置**（`mesh.mtlsMode` で切替可能）に留め、既定は STRICT とする。

## 結果

- 良い影響: サービス間通信が暗号化・相互認証され、IADR-0017 の残余リスク（同一ネットワーク内からの
  無認証到達）が解消される。宣言的（GitOps/ADR-0007）で、ArgoCD が STRICT mTLS を継続的に同期・自己修復する。
- トレードオフ: サイドカーによるリソース増・運用学習コスト（ADR-0005 既知のトレードオフ）。
  実クラスタ初回導入時は PERMISSIVE→STRICT の段階移行を要する場合がある。
- 影響範囲: Helm テンプレート追加（namespace/istio-mtls/networkpolicy）、values 追加（mesh/namespace/
  networkPolicy/imagePullSecrets）、ArgoCD/Istio/Secret 手順、テスト追加、IADR-0017 の Superseded 化。
  アプリケーションコードの認証挙動は変更しない。

## 計画への環流

IADR-0017 の解消（暫定→恒久移行）と、恒久像（全 API OIDC/JWT）の残課題を計画リポジトリへ環流する
（`feedback/20260707_iadr-0017-superseded-mesh-mtls.md`、`/plan-feedback`）。計画 NFR「暫定運用の注記」の
状態更新（サービス間 mTLS 達成・OIDC/JWT 全 API 化は継続課題）を提案する。

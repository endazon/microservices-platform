---
title: IADR-0017 の暫定運用を解消（Istio STRICT mTLS 配備）— 暫定運用の注記の状態更新
type: plan-feedback
status: open
category: 要求の不足
related_ids:
  - NFR
  - ADR-0005
  - ADR-0007
  - ADR-0008
source_repo: microservices-platform
source_ref: claude/issue-100-20260707-1319 / docs/specs/20260707_issue-100_production-runtime-k3s-istio-argocd.md / Issue #100
author: claude
created: 2026-07-07
---

# フィードバック: IADR-0017 の暫定運用を解消（Istio STRICT mTLS 配備）

## 種別

要求の不足（計画 NFR「暫定運用の注記」の実装状況・移行条件の反映）

## 起点となる計画書

- 機能要求（FR）: なし（NFR: セキュリティ・通信暗号化）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: ADR-0005（Istio / mTLS）、ADR-0007（GitOps）、ADR-0008（k3s）
- 計画書リンク:
  - `projects/microservices-platform/02_requirements/01_requirements.md`（暫定運用の注記）
  - `projects/microservices-platform/06_technical/06_migration-roadmap.md`（実装状況の反映）

## 現状（計画書の記述 / As-Is）

計画 NFR「暫定運用の注記（セキュリティ）」は、次のように記す:

> サービスメッシュ（ADR-0005）導入までの暫定期は、エッジ（BFF）で OIDC/JWT 認証を担保し、
> 内部サービスはネットワーク分離（ホスト非公開）を第一防御とする。（…）恒久（全 API OIDC/JWT・
> サービス間 mTLS）への移行条件は ADR-0005 の確定（Accepted）と実装である。

暫定運用の第一防御は「ネットワーク分離」であり、恒久像への移行条件が「ADR-0005 の確定と実装」とされている。

## 問題点 / あるべき姿（To-Be）

移行条件はすでに満たされた:

1. ADR-0005（Istio / mTLS）は 2026-07-06 に **Accepted 確定**。
2. Issue #100（本実装リポジトリ）で本番実行基盤（k3s → Istio mTLS → ArgoCD/Harbor）を
   **宣言的配備構成として整備**し、サービス間通信へ **STRICT mTLS**（`PeerAuthentication`/
   `DestinationRule ISTIO_MUTUAL`）を適用した。

したがって、計画側でも「暫定運用の注記」の状態を更新すべきである:

- サービス間 mTLS は**達成済み**（暫定運用の第一防御はネットワーク分離から Istio STRICT mTLS へ移行）。
- ネットワーク分離は多層防御（defense-in-depth）として存続。
- 恒久像の残課題は **「全 API の OIDC/JWT 認証」**（内部 API でのトークン検証）に限定される。

## 実装で判明した経緯

- 作業仕様書: `docs/specs/20260707_issue-100_production-runtime-k3s-istio-argocd.md`
- 実装 IADR: `docs/adr/IADR-0026_mesh-mtls-supersedes-network-isolation.md`
  （IADR-0017 を Superseded 化）
- 回帰テスト: `MeshMtlsTests`（STRICT mTLS 宣言の固定）
- 本 Issue が移行ロードマップ上、暫定セキュリティ運用解消の律速であった（`06_migration-roadmap.md`）。

## 提案（計画への反映案）

- 反映先候補: 要求更新（`02_requirements/01_requirements.md`）／ 移行ロードマップ更新（`06_migration-roadmap.md`）
- 提案内容:
  1. 「暫定運用の注記」に、サービス間 mTLS の達成（第一防御を Istio STRICT mTLS へ移行、
     ネットワーク分離は多層防御へ格下げ）と、実装リポジトリ IADR-0026 への参照を追記する。
  2. 恒久像の残課題を「全 API の OIDC/JWT 認証」に限定して明記し、移行ロードマップの
     「実装状況の反映」を更新する（IADR-0017 は Superseded）。
  3. シークレット管理（暫定=k8s Secret/環境変数）の恒久化（Vault/External Secrets）は引き続き恒久課題として残す。

## 影響範囲

- 計画 NFR の記述と移行ロードマップの状態のみ（要求そのものの変更ではなく、実装状況の反映）。
- 恒久像「全 API OIDC/JWT」は別途 Issue で追跡する必要があり、計画側でその要求の粒度・優先度を確認されたい。

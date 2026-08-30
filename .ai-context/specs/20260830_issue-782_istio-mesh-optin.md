---
title: 作業仕様書 — Istio を opt-in で導入し、mTLS を段階移行にする（#782）
type: spec
status: in-progress
related_ids:
  - NFR
  - ADR-0005
  - ADR-0007
  - ADR-0021
  - IADR-0026
  - IADR-0091
  - IADR-0304
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
related_specs: []
issue: "#782"
---

# 作業仕様書 — Istio の opt-in 導入（#782）

## 目的と射程

`ADR-0005`（Accepted）が定めるサービスメッシュを、**実際に動かせる経路**として配線する。
Helm チャートには `PeerAuthentication` / `DestinationRule` / `Gateway` / `VirtualService` が
既に書かれているが、**一度も動かしたことがない**（実測: `istio-system` 無し・Istio CRD 0 件）。

射程は **経路B（ローカル k3s）へ opt-in で入れられるようにするところまで**。
STRICT への実移行は #458 が引き受ける。エッジを Traefik から Istio Ingress Gateway へ
寄せるか（`ADR-0021` と `IADR-0091` の突合）は本作業の射程外。

## 決めたこと

判断の記録は [IADR-0304](../adr/IADR-0304_istio-optin-and-staged-mtls.md)。要点だけ:

1. `ISTIO=1` の opt-in。**未設定なら `helm upgrade` の引数は 1 バイトも変わらない**
2. `istioctl` ではなく Helm（`istio/base` ＋ `istio/istiod`）
3. 🔴 **`[6/7]` の前に置く** —— CRD が無いままアプリチャートを apply すると失敗する
4. 🔴 **既定 PERMISSIVE。** STRICT は `ISTIO_MTLS_MODE=STRICT` で明示
5. 経路B では**スクリプトが注入ラベルを貼る**（`namespace.create: false` のため Helm が貼らない）
6. istiod / proxy の資源要求を dev 向けに絞る（27 Pod 分のサイドカーが 1 ノードに載る）

## 母集合の引き方 —— ArgoCD の許可種別（規則 9: 記憶で挙げない）

本番像でレンダリングして種別を数えた。

```console
$ helm template msp deploy/helm/microservices-platform | (apiVersion のグループ + kind を集計)
(core)  ConfigMap 1 / Namespace 1 / PersistentVolumeClaim 2 / Service 18
apps    Deployment 18
autoscaling  HorizontalPodAutoscaler 10
batch   Job 1
policy  PodDisruptionBudget 10
networking.k8s.io   NetworkPolicy 4
networking.istio.io DestinationRule 1 / Gateway 1 / VirtualService 1
security.istio.io   PeerAuthentication 1
→ 13 種別
```

`deploy/argocd/appproject.yaml` は **7 種別しか許可していなかった**。
欠落は **6 種別**（`ConfigMap` / `HorizontalPodAutoscaler` / `Job` / `PodDisruptionBudget` /
`Gateway` / `VirtualService`）で、**いずれも本番 values でレンダリングされる**。
ArgoCD は許可外を同期しないので、**本番同期はそこで止まる。**

`Gateway` / `VirtualService` が欠けていたのは、`edge.yaml` が経路B では無効で
**誰も本番像で同期を試していなかった**ためである。

**検査器は足さない**（「同型の事故が 2 回起きたら」の規約。これは事故ではなく着手前の走査で
見つけた潜在欠陥であり、1 回目は記録に留める）。代わりに**引き直しの手順をファイル冒頭へ書いた**。

`Ingress` は許可されているがレンダリングされない（経路B の overlay 由来）。**残す**——
経路B で使うため、消すと別の穴になる。

## 受け入れ基準（#782）

- [x] `deploy/istio/` に実マニフェスト（istiod の values）と導入手順がある
- [x] `ISTIO=1` の opt-in ブロックがあり、**未設定時の挙動が不変**である
- [x] `mesh.enabled` が opt-in で有効化され、PERMISSIVE → STRICT の段取りが文書にある
- [x] `deploy/argocd/appproject.yaml` に Istio の 4 種別が載っている
- [ ] 🔴 **sidecar 注入後に全 Pod が Ready・PERMISSIVE で疎通・STRICT へ移して疎通** —— **未実施**
- [ ] 🔴 `PeerAuthentication` の PERMISSIVE 残存ゼロの確認 —— **未実施**

## 🔴 未検証であることの明示

**実クラスタへの適用は行っていない。** `helm upgrade --install` がセッションの権限で拒否されたためである
（読み取り側の `helm template` / `helm lint` / `helm list` は通る）。

したがって本作業で確かめられたのは**レンダリングと構文まで**である:

```console
$ helm template ... -f values-local.yaml                          → mesh リソース 0 件（既定オフが効く）
$ helm template ... --set mesh.enabled=true --set mesh.mtlsMode=PERMISSIVE
    kind: PeerAuthentication / mode: PERMISSIVE
    kind: DestinationRule    / mode: ISTIO_MUTUAL
$ helm template ... --set namespace.create=true --set namespace.istioInjection=true
    istio-injection: enabled                                      → 注入ラベルが出る
$ helm template ... | kubeconform -strict   → Valid: 40, Invalid: 0（Istio CRD 2 件は schema 未取得で skip）
$ helm lint                                 → 0 failed
$ bash -n scripts/k8s-local-up.sh           → OK
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js → 664 passed
```

**「入れれば動く」とは言えない。** 実際に確かめるべきことが 2 つ残っている:

1. **Istio 1.30.4 が k3s v1.35.4 で動くか。** k8s 1.35 は Istio 1.30 のサポート表より新しい可能性がある
2. **27 Pod ぶんのサイドカーが 1 ノードに載るか。** 資源要求は絞ったが実測していない

**この 2 つを確かめるまで #782 は閉じない。**

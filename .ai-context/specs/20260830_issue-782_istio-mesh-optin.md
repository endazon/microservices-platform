---
title: 作業仕様書 — Istio を opt-in で導入し、mTLS を段階移行にする（#782）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0005
  - ADR-0007
  - ADR-0021
  - IADR-0026
  - IADR-0091
  - IADR-0306
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

判断の記録は [IADR-0306](../adr/IADR-0306_istio-optin-and-staged-mtls.md)。要点だけ:

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

## 実測（2026-08-30・稼働クラスタ `rancher-desktop` / k3s `v1.35.4+k3s1`）

**着手時に「確かめるまで閉じない」とした 2 点は両方とも成立した。**

1. **Istio 1.30.4 は k3s v1.35.4 で動く** —— `istiod 1/1 Running`・CRD 4 件が `Established`
2. **15 Pod ぶんのサイドカーは載る** —— CPU requests 43% → 45%（+150m）、memory 43% → 49%（+960Mi）。
   **差は 15 × (10m / 64Mi) と厳密に一致**する（決定 6 で絞った値）

段階移行も実測した。

```console
$ kubectl label namespace microservices-platform istio-injection=enabled --overwrite
$ helm template ... --set mesh.mtlsMode=PERMISSIVE -s templates/istio-mtls.yaml | kubectl apply -f -
$ kubectl -n microservices-platform rollout restart deployment
→ 15/15 が istio-proxy 注入済み・15/15 Running かつ全 container Ready
→ node scripts/check-stack-ready.js  → OK: Deployment 28 件が available
→ 再起動直後のログに 2 件の DbCommand エラー（06:34:5x）。直近 2 分では 4 サービスとも 0 件＝一過性
```

### 🔴 成立しなかったこと —— STRICT は現在のエッジ構成と両立しない

`kube-system` の Traefik には**サイドカーが無い**（`containers: ['traefik']`）。

| `mtls.mode` | `http://localhost/` | `https://localhost/` |
| --- | --- | --- |
| PERMISSIVE | **200** | **200** |
| **STRICT** | 🔴 **502** | 🔴 **502** |
| PERMISSIVE（戻す） | **200** | **200** |

各 3 回。**メッシュ内は STRICT でも健全**（28 Deployment available・アプリログのエラー 0 件）で、
**壊れたのは入口だけ**である。

**帰結**: `ADR-0021`（エッジ＝Istio Ingress Gateway）は STRICT にとって**選択肢ではなく前提**である。
経路B のエッジを Traefik にした `IADR-0091` は名前空間全体の STRICT と両立しない。
**#458 は mTLS を上げる前にエッジをメッシュへ入れる必要がある。**

### 副次的な発見 —— 読み取りの落とし穴 2 件

1. **サイドカーは `containers` ではなく `initContainers` に入る**（Istio 1.30 のネイティブサイドカー）。
   `spec.containers` だけ見る手順は「注入 0 件」と誤答する。**実際そう誤答し、
   ノードの資源要求が決定 6 の値ちょうど増えていたことと矛盾したので気付いた。**
2. **`check-stack-ready.js` は上のエッジ断を捕まえない。** STRICT で 502 の間も
   「エッジ・issuer・admin entrypoint も成立している」と OK を返した。G4 の射程は
   `platform-infra` の `keycloak-edge` と Keycloak discovery であり、SPA / BFF 経路を見ていない。
   **検査器の欠陥ではないが、出力の文言は「エッジ全般」と読める。** #992 / #466 へ申し送る。

### 作業中に自分が壊して直したもの

`kubectl apply -k deploy/local/infra`（Alertmanager 作業で otel-collector のポートを直したとき）で
**33 日前に作られたクラスタへ現在のマニフェストを当てた**ため、`rabbitmq` の Deployment が
`couldn't find key username in Secret platform-infra/rabbitmq` で `CreateContainerConfigError`
になっていた（旧 Secret は `password` しか持たない。現行の `k8s-local-up.sh` は
`username=guest` も作る）。**稼働中の旧 Pod は 33 日間動き続けていたので影響は出ていなかった。**
`username=guest`（稼働 Pod の実値と一致することを `rabbitmqctl list_users` で確認）を足して復旧した。

**これは「稼働クラスタが古いスクリプトで作られており、現行マニフェストと乖離している」型である**
—— otel-collector の 8888 欠落（#546）と同じ根である。

## 受け入れ基準の到達点

- [x] 実マニフェスト・opt-in ブロック・既定不変・ArgoCD の許可種別
- [x] **sidecar 注入後に全 Pod が Ready**（15/15）
- [x] **PERMISSIVE で疎通**（エッジ 200 / スタック 28 available / アプリログ 0 件）
- [x] **STRICT へ移して疎通** —— 🔴 **移せない**ことを実測で確定した（エッジ 502）。**これも実測結果である**
- [ ] `PeerAuthentication` の PERMISSIVE 残存ゼロ —— **#458 の射程。エッジをメッシュへ入れてからでないと成立しない**

## クラスタの後始末

**Istio は導入したまま・`PERMISSIVE` で残してある**（利用者の承認範囲内。全 28 Deployment available）。
撤去手順は [IADR-0306](../adr/IADR-0306_istio-optin-and-staged-mtls.md) §現在のクラスタの状態 に置いた。

---
title: IADR-0304 Istio は opt-in で入れ、mTLS は PERMISSIVE から段階的に STRICT へ移す
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0005
  - ADR-0007
  - ADR-0021
  - IADR-0026
  - IADR-0091
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
---

# IADR-0304: Istio の導入形と段階的 mTLS（#782）

- 状態: Accepted
- 日付: 2026-08-30
- 決定者: claude（実装）／導入の可否は利用者裁定（2026-08-30・「opt-in ゲート付きで入れてよい」）

## 起点・関連

- 計画 ADR: **ADR-0005**（サービスメッシュ＝Istio・`Accepted`）／**ADR-0021**（エッジ＝Istio Ingress Gateway ＋ Caddy）
- 実装 issue: **#782**（#442 の子 4）
- 先行: [IADR-0026](./IADR-0026_mesh-mtls-supersedes-network-isolation.md)（STRICT mTLS が暫定運用を解消する）／
  [IADR-0091](./IADR-0091_local-edge-aggregation-traefik.md)（経路B のエッジは Traefik）
- 作業仕様書: [20260830_issue-782](../specs/20260830_issue-782_istio-mesh-optin.md)

## コンテキストと課題

`ADR-0005` は `Accepted` で、Helm チャートには `PeerAuthentication` / `DestinationRule` /
`Gateway` / `VirtualService` が既に**書かれている**。しかし実クラスタには入っていなかった
（2026-08-30 実測: `istio-system` 名前空間なし・Istio の CRD 0 件）。
`values-local.yaml` は `mesh.enabled: false` / `edge.enabled: false` で経路B から外している。

**つまり「宣言はあるが一度も動かしたことがない」状態だった。**

## 決定

### 決定 1 — `ISTIO=1` の opt-in にし、既定は完全に不変とする

`OBSERVABILITY` / `ARGOCD` / `HEADLAMP` / `LOCALEDGE` と同じ形にする。
`ISTIO` 未設定なら `ISTIO_MESH_ARGS` は空文字で、`helm upgrade` の引数は従来と 1 バイトも変わらない。

**理由**: 1 ノードの dev クラスタに 27 Pod が既に載っており、全 Pod へサイドカーが 1 つずつ増える。
**メッシュが要らない作業の邪魔をしない**ことを既定にする。

### 決定 2 — 導入は `istioctl` ではなく Helm で行う

`istio/base`（CRD）＋ `istio/istiod`（コントロールプレーン）を Helm で入れる。

**理由**: 配布バイナリを増やさない。他の opt-in（External Secrets・cert-manager）と経路を揃える。
`istioctl` は §検証 でだけ便利だが、`kubectl` で代替できる。

### 決定 3 — 🔴 Istio の導入は `[6/7]` の **前** に置く

アプリチャートは `mesh.enabled=true` のとき `PeerAuthentication` / `DestinationRule` を
レンダリングする。**CRD が無い状態で `helm upgrade` すると apply がその時点で失敗する。**
CRD が `Established` になるまで `kubectl wait` してから先へ進む（cert-manager の扱いと同じ作法）。

### 決定 4 — 🔴 既定は PERMISSIVE。STRICT は `ISTIO_MTLS_MODE=STRICT` で明示的に移す

**いきなり STRICT にしない。** 理由は 2 つが**同時に**壊れて切り分けられなくなることである。

| 壊れる経路 | 理由 |
| --- | --- |
| `platform-infra` 宛（postgres / keycloak / rabbitmq / qdrant / redis / minio …） | サイドカーが入っていない。メッシュ外からの平文が拒否される |
| 注入前の Pod からの通信 | `rollout restart` が完了するまでサイドカー無しの Pod が残る |

段取りは **注入 → 全 Pod Ready → PERMISSIVE で疎通確認 → STRICT**。
`deploy/istio/README.md` にも同じ順序を書いた。

**なお本番像（`values.yaml`）の既定は STRICT のままである** —— `MeshMtlsTests` が
「平文許容へ後退していないこと」を回帰として固定しており、そこは変えない。
変えたのは**経路B の導入手順の既定**だけである。

### 決定 5 — 経路B では注入ラベルを**スクリプトが貼る**

`values-local.yaml` は `namespace.create: false` である。**Helm は Namespace を作らないので、
`namespace.istioInjection=true` にしてもラベルが誰にも適用されない。**
`kubectl label namespace ... istio-injection=enabled --overwrite` をスクリプトへ置く。

**本番像は `namespace.create: true` なのでチャートが貼る。** 同じ結果を 2 経路で別々に担保する形になるが、
Namespace の所有者が経路ごとに違う以上これは避けられない。

### 決定 6 — istiod の資源要求を dev 向けに絞る

サイドカーは Pod ごとに 1 つ増える。既定（`100m` / `128Mi` requests）のままだと
**15 Deployment ぶんの要求だけで 1 ノードの割当を食い潰す**。
proxy を `10m` / `64Mi`、istiod を `50m` / `128Mi` に絞る（`deploy/istio/istiod-values-local.yaml`）。
**本番像ではない**ことをファイル冒頭に明記した。

## 配線を確かめて分かったこと —— ArgoCD の許可種別が 6 つ欠けていた

**記憶で挙げず、本番像でレンダリングして数えた**（規則 9）。

```console
$ helm template msp deploy/helm/microservices-platform | (kind を集計)
13 種別
```

`deploy/argocd/appproject.yaml` の `namespaceResourceWhitelist` は **7 種別しか許可していなかった**。
欠けていたのは **`ConfigMap` / `HorizontalPodAutoscaler` / `Job` / `PodDisruptionBudget` /
`Gateway` / `VirtualService`** の 6 つで、**いずれも本番 values でレンダリングされる**。
ArgoCD は許可外の種別を同期しないため、**本番同期はその時点で止まる。**

`Gateway` / `VirtualService` が欠けていたのは、`edge.yaml` が経路B では無効
（`edge.enabled: false`）で、**誰も本番像で同期を試していなかった**ためである。

**検査器は足さない。** 「同型の事故が 2 回起きたら」の規約に従う ——
これは事故ではなく、着手前の走査で見つけた潜在欠陥である（1 回目は記録に留める）。
代わりに**引き直しの手順をファイル冒頭のコメントに書いた**（次に触る人が記憶で足さないように）。

## 実クラスタで確かめたこと（2026-08-30・k3s `v1.35.4+k3s1`）

着手時に「確かめるまで閉じない」とした 2 点は**両方とも成立した**。加えて、**成立しないことが 1 つ分かった。**

### 1. Istio 1.30.4 は k3s v1.35.4 で動く（成立）

```console
$ kubectl -n istio-system get pods
istiod-6f445ddcf7-sr8kx   1/1   Running   0   20s
```

4 つの CRD（`peerauthentications` / `destinationrules` / `gateways` / `virtualservices`）が
いずれも `Established` になった。

### 2. 15 Pod ぶんのサイドカーは 1 ノードに載る（成立）

| | 注入前 | 注入後 | 差 |
| --- | ---: | ---: | ---: |
| CPU requests | 3480m (43%) | 3630m (45%) | **+150m** |
| memory requests | 6740Mi (43%) | 7700Mi (49%) | **+960Mi** |

**差は 15 × (10m / 64Mi) と厳密に一致する**（決定 6 で絞った値）。ノードは 8 CPU / 15.9Gi。

### 3. 🔴 サイドカーは `containers` ではなく `initContainers` に入る（検証手順の落とし穴）

Istio 1.30 は k8s のネイティブサイドカー（`restartPolicy: Always` の initContainer）を使う。

```console
$ ... sample initContainers: ['istio-init', 'istio-proxy']
$ ... sample restartPolicy : [None, 'Always']
```

**`spec.containers` だけを見る確認手順は「注入 0 件」と誤答する。** 実際そう誤答し、
**ノードの資源要求が 15 × 決定 6 の値ちょうど増えていた**ことと矛盾したので気付いた。
`README.md` の確認コマンドは `initContainers` も見る形にした。

### 4. 🔴 **STRICT は現在のエッジ構成と両立しない**（成立しない）

**これが本作業で最も重要な発見である。**

`kube-system` の Traefik には**サイドカーが無い**（`containers: ['traefik']`）。
名前空間全体へ STRICT を掛けると、Traefik から `frontend-service` / `bff-service` への
平文が拒否される。実測（各 3 回）:

| `mtls.mode` | `http://localhost/` | `https://localhost/` |
| --- | --- | --- |
| PERMISSIVE | **200** | **200** |
| **STRICT** | 🔴 **502** | 🔴 **502** |
| PERMISSIVE（戻す） | **200** | **200** |

**メッシュ内は STRICT でも健全だった** —— Deployment 28 件が available のまま、
`bff-service` / `document-service` のアプリログはエラー 0 件。**壊れたのは入口だけ**である。

**帰結**: `ADR-0021`（エッジ＝Istio Ingress Gateway ＋ Caddy）は、STRICT にとって
**選択肢ではなく前提**である。経路B のエッジを Traefik にした `IADR-0091` は、
**名前空間全体の STRICT とは両立しない。** #458（暫定運用の解消）は、
mTLS を STRICT へ上げる前に**エッジをメッシュへ入れる**必要がある。
（回避策として Traefik へサイドカーを注入する／`portLevelMtls` で入口ポートだけ
PERMISSIVE に残す、も理屈上は採れるが、どちらも `ADR-0021` から遠ざかる。）

### 5. `check-stack-ready.js` は上のエッジ断を捕まえない

STRICT でエッジが 502 を返している間も、同スクリプトは
**「エッジ・issuer・admin entrypoint も成立している」と OK を返した。**
G4 が見ているのは `platform-infra` の `keycloak-edge` Ingress と Keycloak の discovery であり、
**`microservices-platform` の SPA / BFF 経路は見ていない**（`platform-infra` に STRICT は掛かっていないので当然通る）。

**検査器の欠陥ではない**（G4 の射程はそう書いてある）が、
**出力の文言は「エッジ全般が成立している」と読める。** #992 / #466 が扱う
「観測できることだけを門にしている」問題と同じ形なので、そちらへ申し送る。

## 現在のクラスタの状態

**Istio は導入したまま・`PERMISSIVE` で残してある**（利用者の承認範囲内）。全 28 Deployment が available。
撤去する場合:

```sh
kubectl -n microservices-platform delete peerauthentication,destinationrule microservices-platform-mtls
kubectl label namespace microservices-platform istio-injection-
kubectl -n microservices-platform rollout restart deployment
helm uninstall istiod -n istio-system && helm uninstall istio-base -n istio-system
```

## 結果

- **良い影響**: `ADR-0005` の宣言が初めて実際に動く経路を持つ。ArgoCD の本番同期が
  種別の欠落で止まる潜在欠陥が消えた。
- **悪い影響 / トレードオフ**:
  - **既定オフなので、放っておくと誰も動かさない。** `IADR-0091`（経路B のエッジは Traefik）との
    二重構成が続く。`ADR-0021` の「エッジ＝Istio Ingress Gateway」へ寄せるのは本 ADR の射程外である。
  - サイドカーぶんの資源消費が増える（27 Pod ぶん）。dev の 1 ノードでは無視できない。
- **フォローアップ**:
  1. **STRICT への移行を実クラスタで通す**（PERMISSIVE で疎通確認したうえで）。#458 が引き受ける
  2. Kiali の配備（`ADR-0005` に付随。未着手）
  3. 経路B のエッジを Traefik から Istio Ingress Gateway へ寄せるかの判断（`ADR-0021` / `IADR-0091` の突合）

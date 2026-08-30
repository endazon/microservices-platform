---
title: IADR-0312 経路B のエッジを Istio Ingress Gateway へ移し、East-West mTLS を STRICT にする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - NFR-11
  - ADR-0005
  - ADR-0021
  - ADR-0023
  - ADR-0047
  - IADR-0091
  - IADR-0206
  - IADR-0220
  - IADR-0227
  - IADR-0258
  - IADR-0307
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md
---

# IADR-0312: エッジの Istio Ingress Gateway 化と STRICT mTLS（#782 残段）

- 状態: Accepted
- 日付: 2026-08-30
- 決定者: claude（実装）。**道の選択は計画 ADR-0021 が既に決めている**（実装側の裁量ではない）

## 起点・関連

- 計画 ADR: **ADR-0021**（エッジ＝Istio Ingress Gateway ＋ Caddy・`Accepted`）／**ADR-0005**（メッシュ＝Istio・`Accepted`）
- 実装 issue: **#782**（#442 の子 4）。開ける先: **#458**
- 先行: [IADR-0307](./IADR-0307_istio-optin-and-staged-mtls.md)（Istio の opt-in 導入・PERMISSIVE まで）／
  [IADR-0091](./IADR-0091_local-edge-aggregation-traefik.md)（経路B のエッジ＝Traefik）
- 作業仕様書: [20260830_issue-782_istio-ingressgateway-strict-mtls](../specs/20260830_issue-782_istio-ingressgateway-strict-mtls.md)

## コンテキストと課題

[IADR-0307](./IADR-0307_istio-optin-and-staged-mtls.md) は Istio を入れ、PERMISSIVE までは成立させた。
残ったのは 1 点だけである —— **STRICT にすると経路B のエッジが 502 になる**（同 ADR §4 の実測）。

原因は `kube-system` の Traefik が**メッシュの外**にあり、mesh 内の 4 Service
（`frontend-service` / `bff-service` / `wiki-js` / `minio`）へ平文で入っていることである。

取りうる道は 3 つあった:

| | 道 | 計画との関係 |
| --- | --- | --- |
| A | **Istio Ingress Gateway を立ててエッジを移す。Traefik を退役させる** | **ADR-0021 §決定 そのもの** |
| B | Traefik をメッシュへ入れる（`kube-system` へ sidecar 注入） | ADR-0021 が「別系統プロキシ」として退けた形 |
| C | `portLevelMtls` で入口ポートだけ PERMISSIVE の穴を残す | ADR-0021 §Istio との関係 が「特別な緩和は不要」と明言 |

## 決定 —— **A を採る。実装側の裁量ではなく計画の定めである**

`ADR-0021`（`Accepted`）§決定 の逐語:

> - **入口・TLS 終端・ルーティング・レート制限**: **Istio Ingress Gateway**（Envoy。Istio 制御面が管理）
> …実行基盤 k3s（ADR-0008）が既定同梱する **Traefik は無効化する**。

同 ADR §コンテキストと課題 は、**#1072 が実測した障害を先に言い当てている**:

> 入口に**別系統のプロキシ**（Traefik / NGINX Ingress 等）を置くと、入口から mesh 内サービスへ
> 引き渡す一点で、**mTLS が STRICT のとき平文流入が Envoy に拒否される境界問題**が生じ…
> **この境界をそもそも発生させない構成が望ましい。**

§Istio（Envoy）との関係 が B・C を明示的に閉じている:

> …入口が mesh ネイティブであるため**構造的に発生しない**。
> **入口境界のための特別な緩和（PERMISSIVE 化）は不要。**

**したがって裁定依頼は起票しない。** `ADR-0047` 決定 1 の「エッジの実体が Istio Ingress Gateway か
経路B の Traefik かは問わない」は**証明書の適用範囲についての文**であり（同 ADR は
`ADR-0023` §決定 の部分改定であると自ら述べ、「本 ADR は経路B のエッジ実装（Traefik）を変更せず」と
射程を限っている）、エッジ製品の決定を差し戻すものではない。

### 決定 1 — `ISTIO=1` かつ `LOCALEDGE=1` のときだけエッジを移す。既定は 1 バイトも変えない

`ISTIO` 未設定なら従来どおり Traefik がエッジである（`IADR-0091`）。`ISTIO=1` を
`LOCALEDGE=1` 無しで指定したときは**警告を出す** —— その組み合わせでは入口が port-forward のままで、
STRICT へ上げると mesh へ入る経路が無くなる。

### 決定 2 — 🔴 Traefik には「ポートを明け渡させる」（削除しない）

**k3s の ServiceLB（klipper）は LoadBalancer Service ごとに hostPort を握る DaemonSet を作る。
80/443/50000 を 2 つの Service が同時に持てない。** これは机上の懸念ではなく、
`svclb-traefik-ad843cc0` が実際に 3 ポートを握っていた（実測）。

`HelmChartConfig` で `service.enabled: false` にする。**Deployment は残る**ので、
戻すのは `deploy/local/edge/traefik-entrypoint.yaml` を当て直すだけである。
`HelmChart` を消す・Rancher Desktop の設定を触る、は**戻し方がディストリビューションごとに違う**ので採らない。

### 決定 3 — 🔴 **3 ポートすべて**を Istio が持つ。「80/443 だけ移す」は成立しない

`admin(50000)` には mesh 内の 2 件（`minio` / `wiki-js`）が載っている。**50000 を Traefik に
残すとその 2 件が STRICT で落ちる。** かといって minio / wiki を 443 へ移すこともできない ——
**7 つの OIDC クライアントの redirect URI が `:50000` 付きで Keycloak に登録済み**である
（`IADR-0092`〜`IADR-0095` / `IADR-0220`）。

結果として、計画の「Traefik を無効化する」と**同じ形にしかならなかった**。
段階的に一部だけ移す案は、調べた結果**存在しない**。

### 決定 4 — Gateway は 2 本に分ける（`msp-edge` = 80/443 ／ `msp-admin-edge` = 50000）

VirtualService は `gateways:` でどの Gateway に載るかを選べるが、**ポートは選べない**
（`match.port` を書かない限り）。1 本にまとめると catch-all（`hosts: "*"`）が admin(50000) も飲み込む。
2 本に分ければ、Traefik の entrypoint（web/websecure と admin）と 1 対 1 に対応する。

### 決定 5 — VirtualService は**すべて `istio-system`** に置く

行き先は FQDN なので namespace を跨げる。アプリ側 namespace に置くと、`argocd` のように
**opt-in でしか存在しない namespace** のために条件付き apply が要る（Traefik 経路では実際に
`argocd-ingress.yaml` だけスクリプトが条件分岐していた）。置き場所を 1 つにすると**条件分岐が消える**。

### 決定 6 — `edge-tls` を `istio-system` にも発行する。**名前と `dnsNames` は変えない**

Istio の Gateway は `credentialName` の Secret を**ingress gateway と同じ namespace**から読む。
`IADR-0206` が 3 つの namespace に葉証明書を分けたのと同じ制約であり、4 枚目になる。

計画 `ADR-0023` の設計要件（`secretName` / `dnsNames` を安定させる・CA 固有設定は `ClusterIssuer` に
閉じ込める・切り替えは `issuerRef` の差し替えのみ）は崩さない。**4 枚の `dnsNames` が一致していることは
`scripts/k8s-local-up.test.js` が機械で固定した**（ずれると SNI が合わず、TLS が張れたつもりで落ちる）。

### 決定 7 — 🔴 切り戻しを 1 コマンドで持ち、**当てる前に走らせて確かめる**

`scripts/istio-edge-down.sh`。冪等で、Istio エッジを一度も当てていない状態で走らせても壊れない。

**順序が命である**（どちらも逆にすると詰む）:

| up | down |
| --- | --- |
| ① Traefik を明け渡す → ② Gateway を立てる | ① mTLS を緩める → ② Gateway を撤去 → ③ Traefik を戻す |

down で mTLS を先に緩めるのは、**入口だけ戻して 502 のままにしない**ためである。
down で Gateway の撤去を先にするのは、**hostPort を空けてからでないと Traefik の svclb が bind に失敗する**ためである。

## 実クラスタで確かめたこと（2026-08-30・k3s `v1.35.4+k3s1` / Istio `1.30.4`）

計測はすべて**証明書検証を有効にした** `curl` である（`-k` は使わない。#1074 の事故）。
Windows の `curl` は schannel なので私有 CA では失効確認が `unknown` になり接続自体が落ちる。
`--ssl-revoke-best-effort` は**失効確認だけ**を best-effort にするもので、チェーン検証と
ホスト名照合は有効なままである（`-k` とは別物）。

### 段ごとの実測（12 エンドポイント × 各 3 回）

| 段 | 状態 | `https://localhost/` | `/bff/auth/me` | `http://localhost/` | keycloak discovery | 管理 7 件（:50000） |
| --- | --- | --- | --- | --- | --- | --- |
| S0 | Traefik / PERMISSIVE（基準線） | 200 ×3 | 401 ×3 | 301 ×3 | 200 ×3 | 全 200 ×3 |
| S4 | **Istio エッジ** / PERMISSIVE | 200 ×3 | 401 ×3 | 301 ×3 | 200 ×3 | 全 200 ×3 |
| S4' | **切り戻し実行後**（Traefik へ復帰） | 200 ×3 | 401 ×3 | 301 ×3 | 200 ×3 | 全 200 ×3 |
| S4'' | 再度 Istio エッジ | 200 ×3 | 401 ×3 | 301 ×3 | 200 ×3 | 全 200 ×3 |
| **S6** | **Istio エッジ / STRICT** | **200 ×3** | **401 ×3** | **301 ×3** | **200 ×3** | **全 200 ×3** |

**S6 が本作業の到達点である** —— #1072 で 502 になった組み合わせが、そのまま基準線と一致した。
`/bff/auth/me` の 401 は「BFF に届いた」ことの証拠である（届かなければ 502 か SPA の 200 になる）。

### STRICT が実際に平文を拒否していること（変異試験）

メッシュ外（`platform-infra`・サイドカー無し）の Pod から mesh 内へ平文で入る:

```console
=== 対照（メッシュ外の宛先 keycloak。STRICT の影響を受けないはず）===
http_code=200
--- mtls.mode=STRICT ---
[frontend-service へ平文] curl: (56) Recv failure: Connection reset by peer / http_code=000
[bff-service へ平文]      curl: (56) Recv failure: Connection reset by peer / http_code=000
--- mtls.mode=PERMISSIVE ---
[frontend-service へ平文] http_code=200
[bff-service へ平文]      http_code=401
--- mtls.mode=STRICT ---
[frontend-service へ平文] http_code=000（Connection reset by peer）
[bff-service へ平文]      http_code=000（Connection reset by peer）
```

**STRICT → PERMISSIVE → STRICT で挙動が往復した。** 宣言があるだけでなく効いている。

### PERMISSIVE 残存ゼロ（`istioctl` は PATH に無いので `kubectl` で測った）

```console
1. 全 PeerAuthentication
   microservices-platform/microservices-platform-mtls  mode=STRICT  portLevelMtls={}  selector=none
2. STRICT 以外の PeerAuthentication      → （0 件）
3. portLevelMtls の穴（port 例外）        → （0 件）
4. root namespace の全体既定              → 無し（注入済み namespace は 2. の名前空間ポリシーが上書きする）
5. DestinationRule                        → *.microservices-platform.svc.cluster.local  tls=ISTIO_MUTUAL
6. 注入されている namespace               → microservices-platform（1 つ）
7. サイドカー                             → injected=18 / total=18
```

さらに **サイドカーの inbound リスナ**（`istioctl proxy-config listener` の代替。
`pilot-agent request GET config_dump`）:

```console
name=virtualInbound-blackhole      transport_socket=NONE  match={"destination_port":15006}
name=virtualInbound-catchall-http  transport_socket=tls   match={"transport_protocol":"tls", ...}
name=virtualInbound                transport_socket=tls   match={"transport_protocol":"tls"}
name=0.0.0.0_8080                  transport_socket=tls   match={"destination_port":8080,"transport_protocol":"tls", ...}
name=0.0.0.0_8080                  transport_socket=tls   match={"destination_port":8080,"transport_protocol":"tls"}
```

🔴 **`transport_socket` を持たない chain が 1 つあるが、これは平文の受け口ではない** ——
`virtualInbound-blackhole`（`destination_port: 15006`）は Envoy 自身の inbound ポートへ直接
来た接続を捨てるための chain である。**アプリへ渡る 4 つの chain はすべて
`transport_protocol: tls` を要求している。** 数だけ見て「平文が 1 件残っている」と読むと誤る。

### 切り戻しが効くこと（当てる前と、当てた後の 2 回）

1. **本番の変更を当てる前**に `bash scripts/istio-edge-down.sh` を走らせた
   → EXIT=0・12 エンドポイントすべて基準線どおり（冪等であり、壊さない）
2. Istio エッジを立てた**後に本当に打った** → Traefik svc が 80/443/50000 を取り戻し、
   12 エンドポイントすべて基準線どおり（S4'）

**2 回目（本当に戻した回）で欠陥が 1 つ見つかった** —— `kubectl wait` は
**対象が存在しないと待たずに即エラーになる**。Service ごと消してから戻すため、
**存在を待つループが先に要る**。Traefik は正しく復活したのにスクリプトだけが非 0 で終わった。
是正して再実行し、EXIT=0 を確認した。

🔴 **1 回目（当てる前の空撃ち）はこの欠陥を出せなかった。** Traefik の Service が消えていないので
`kubectl wait` が即座に成立してしまうためである。**空撃ちは「壊さないこと」しか確かめられない。**
**本当に 1 回戻すところまでやらないと、切り戻しが効くことは確かめたことにならない。**

## 見つけたが直さないこと（記録に留める）

**稼働クラスタの `PeerAuthentication` / `DestinationRule` は Helm ではなく `kubectl apply` が
所有していた**（`meta.helm.sh/release-name` が無く `last-applied-configuration` だけがある）。
`msp` リリースの revision 6（2026-08-30 21:32）は `mesh.enabled: false` である。
つまり **Helm の宣言と現物が食い違っている。**

`IADR-0307` が「宣言はあるが動かしたことがない」を直した裏で、**今度は「動いているが宣言が持っていない」が生まれた**。
本 PR では触らない（所有権の移譲は別の破壊操作であり、#782 の受け入れ基準に含まれない）。
恒久の形は `ISTIO=1 LOCALEDGE=1 ISTIO_MTLS_MODE=STRICT ./scripts/k8s-local-up.sh` を
クラスタ作成から通すことであり、**#458 がそれを引き受ける**。

## ArgoCD の許可種別（受け入れ基準 6・引き直した）

**記憶で挙げず、本番像でレンダリングして数えた**（規則 9）。

```console
$ helm template msp deploy/helm/microservices-platform | (kind を集計)
13 種別（Deployment / Service / ConfigMap / PVC / NetworkPolicy / HPA / PDB / Job /
        PeerAuthentication / DestinationRule / Gateway / VirtualService / Namespace）
$ grep -c 'kind: ' deploy/helm/microservices-platform/templates/*.yaml | (種別で集計)
14 種別（上記 ＋ Ingress。wikijs.ingress.enabled=true でのみ出る）
```

`deploy/argocd/appproject.yaml` は **namespaced 13 種別＋cluster の Namespace** を許可しており、
**欠落は 0 である**（#1072 が 6 種別の欠落を是正済み）。本 PR で追加した Istio 資材は
**`istio-system` の overlay であってチャートの一部ではない**ため、AppProject の対象外である
（`destinations` は `microservices-platform` namespace のみ）。

## 影響・トレードオフ

- **良い影響**: 入口と mesh が同一の Envoy データプレーンに載り、`ADR-0021` が言う境界問題が
  構造的に消えた。**STRICT が実際に動く**（#458 の前提が外れた）。
- **悪い影響**: 経路B の入口が Istio 制御面に依存する（istiod が落ちるとエッジも落ちる）。
  Traefik 経路と Istio 経路の**2 つのエッジ宣言を持つ**ことになり、ルートを足すときは
  両方を触る必要がある（`deploy/local/edge/` と `deploy/local/edge-istio/`）。
  **これは opt-in を保つ代償**であり、既定のバイト等価と引き換えである。
- **Caddy はまだ入っていない。** `ADR-0021` は SPA 配信を Caddy と定めるが、現状は
  `frontend-service`（nginx）である。本 PR の射程外（入口の話と配信 Web サーバの話は別）。
  **#442 の残作業として残る。**

## 代替案

- **B（Traefik へサイドカー注入）**: `kube-system` への注入は k3s の管理物すべてに影響し、
  副作用が読み切れない。`ADR-0021` が退けた形でもある。
- **C（`portLevelMtls` で穴）**: 受け入れ基準「PERMISSIVE 残存ゼロ」と両立しない。
  `ADR-0021` が「特別な緩和は不要」と明言している。
- **Traefik を残したまま Istio Gateway を別ポートで立てる**: `:50000` の OIDC redirect URI が
  動かせない以上、mesh 内の minio / wiki が STRICT で落ちる（決定 3）。

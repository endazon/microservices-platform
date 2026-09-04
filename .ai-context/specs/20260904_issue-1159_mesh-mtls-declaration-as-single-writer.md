---
title: 稼働メッシュ設定のドリフトを断つ（mTLS モードの書き手を helm 単独にし、乖離を門で落とす）
type: spec
status: draft
related_ids: [NFR, NFR-11, ADR-0005, ADR-0021, ADR-0026, IADR-0026, IADR-0307, IADR-0317, IADR-0336, IADR-0369]
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_security-requirements.md
---

# 仕様書: issue #1159 — 稼働メッシュ設定のドリフト

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: —（NFR: セキュリティ / 実行基盤。NFR-11）
- ユースケース（UC）: —／画面（SC）: —
- 関連 ADR: ADR-0005（サービスメッシュ = Istio）, ADR-0021（入口 = Istio Ingress Gateway）, ADR-0026（セキュリティ要求）
- 実装 ADR: IADR-0026（STRICT mTLS が暫定運用を解消する）, IADR-0307（Istio opt-in と段階的 mTLS）,
  IADR-0317（エッジの Istio Ingress Gateway 化と STRICT）, IADR-0336（バックチャネルログアウトのメッシュ境界）,
  IADR-0369（永続化既定と門 G9〜G11）

## 目的・背景

issue #1159 は「稼働 k3s の `PeerAuthentication microservices-platform-mtls` が `kubectl patch` で
STRICT へドリフトし、メッシュ外の `ai-stock-trading` namespace から MSP への平文が全断している」と実測した。
**issue が推測した原因（「おそらく人が手で実行した」）は誤りである** —— 後述のとおり
**ドリフトを起こしているのはリポジトリ内のスクリプトそのもの**であり、しかも Helm 4 の
サーバサイド apply の下では **一度起きると以後の `helm upgrade` が恒久的に失敗する**（自己修復しない）。

## 対象範囲

- 対象: 稼働クラスタの `mesh.mtlsMode` を書く経路の一本化、乖離の検知（`check-stack-ready.js` の門）、
  収束の不変条件（`scripts/k8s-local-up.test.js`）、AST↔MSP の跨 namespace mTLS の扱いの明文化。
- 対象外: `ai-stock-trading` 側のメッシュ参加（AST リポジトリの責務。受け皿は AST#627）。
  本番像 `values.yaml` の `mtlsMode: STRICT`（`MeshMtlsTests` が回帰固定。**変えない**）。
  Istio 本体のバージョン・エッジ構成（IADR-0317 が確定済み）。

## 母集合（規則 9・10 —— 誤りの側の語で走査した結果と除外理由）

走査は追跡下のファイル（`git ls-files`、3060 件）に対して行った。
**submodule（`src/ai-stock-trading`）は別リポジトリなので除外**（同 namespace の chart は AST が所有する）。

```console
### scan: patch[[:space:]]+peerauthentication
scripts/istio-edge-down.sh
scripts/istio-edge-up.sh

### scan: mtlsMode|ISTIO_MTLS_MODE
.ai-context/adr/IADR-0026_mesh-mtls-supersedes-network-isolation.md
.ai-context/adr/IADR-0307_istio-optin-and-staged-mtls.md
.ai-context/adr/IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md
.ai-context/specs/20260707_issue-100_production-runtime-k3s-istio-argocd.md
.ai-context/specs/20260830_issue-782_istio-mesh-optin.md
.ai-context/specs/20260902_issue-1115_backchannel-logout-destination.md
.ai-context/specs/20260904_issue-1088_persist-by-default-and-realm-reconcile.md
deploy/helm/microservices-platform/templates/istio-mtls.yaml
deploy/helm/microservices-platform/values.yaml
deploy/istio/README.md
deploy/local/edge-istio/README.md
scripts/README.md
scripts/istio-edge-up.sh
scripts/k8s-local-up.sh
src/knowledge/backend/Tests/Knowledge.IntegrationTests/Deployment/MeshMtlsTests.cs

### scan: kind:[[:space:]]*(PeerAuthentication|AuthorizationPolicy|DestinationRule)
.ai-context/specs/20260823_issue-953_helmchartconfig-fail-closed.md
deploy/argocd/appproject.yaml
deploy/helm/microservices-platform/templates/istio-mtls.yaml

### scan: backchannelLogoutFromOutsideMesh
deploy/local/aliases/platform-infra-externalnames.yaml
```

除外理由:

- `.ai-context/specs/*` は**確定済みの凍結記録**なので書き換えない（`traceability.repo.md`）。
- `.ai-context/adr/IADR-0026 / 0307 / 0317` は決定そのものを変えないので触らない。本 ADR が追補する。
- `deploy/argocd/appproject.yaml` は許可種別の列挙で、`AuthorizationPolicy` が**欠けている**
  （後述「見つけた欠陥」）。
- `MeshMtlsTests.cs` は本番像の既定 STRICT を固定する回帰であり、本作業は**本番像を変えない**ので不変。

## 稼働クラスタで実測して分かったこと（2026-09-04・k3s `v1.35.4+k3s1` / Istio `1.30.4` / Helm `v4.2.1`）

### 1. #1159 の症状は現在は再現しない（クラスタが作り直されたため）

```console
$ kubectl get peerauthentication,authorizationpolicy,destinationrule -A
microservices-platform  peerauthentication/bff-service-backchannel-logout   PERMISSIVE
microservices-platform  peerauthentication/microservices-platform-mtls      PERMISSIVE
microservices-platform  authorizationpolicy/bff-service-plaintext-only-backchannel  DENY
microservices-platform  destinationrule/microservices-platform-mtls  *.microservices-platform.svc.cluster.local
$ (ai-stock-trading の使い捨て Pod から)
   http://document-service.microservices-platform:8080/health/live   => 200
   http://llmgateway-service.microservices-platform:8080/health/live => 200
   http://keycloak.platform-infra:8080/.../openid-configuration      => 200（対照）
   http://configuration-service:8080/health/live                     => 200（対照・AST ns 内）
```

`managedFields` も `manager: helm / operation: Apply` の 1 件だけで、`kubectl-patch` は無い。
**#1159 の受け入れ基準 1〜3 は現時点で満たされている。** 残るのは「二度と起きない形にすること」と
受け入れ基準 4（AST↔MSP の扱いの明文化）である。

### 2. 🔴 ドリフトを起こしているのは**リポジトリ内のスクリプト**である

`scripts/istio-edge-up.sh` [5/5] と `scripts/istio-edge-down.sh` [1/4] は、helm が所有する
`PeerAuthentication` を **`kubectl patch` で直接書き換える**:

```sh
kubectl -n "$MSP_NS" patch peerauthentication "$MSP_NS-mtls" \
  --type=merge -p '{"spec":{"mtls":{"mode":"STRICT"}}}'
```

`k8s-local-up.sh` は同じ値を **2 経路で** 与えている ——
[6/7] の `helm upgrade --set mesh.mtlsMode=${ISTIO_MTLS_MODE:-PERMISSIVE}`（宣言）と、
末尾から呼ぶ `istio-edge-up.sh` の `kubectl patch`（宣言の外）である。
#1159 が `managedFields` から読み取った「06:42 に PERMISSIVE で apply → 6.5 時間後に STRICT へ patch」は、
**IADR-0317 の実測（2026-08-30）で `istio-edge-up.sh` / `istio-edge-down.sh` を往復させた跡**そのものである。

### 3. 🔴 Helm 4 では、その patch が **release を恒久的に壊す**（自己修復しない）

Helm 4 はサーバサイド apply（`manager: helm` / `operation: Apply`）を使う。
`kubectl patch` は `Update` 操作なので `.spec.mtls.mode` の所有権を奪う。以後の `helm upgrade` は:

```console
$ kubectl -n microservices-platform patch peerauthentication microservices-platform-mtls \
    --type=merge -p '{"spec":{"mtls":{"mode":"STRICT"}}}'
   mode=STRICT gen=2 managers=helm/Apply,kubectl-patch/Update

$ helm upgrade msp deploy/helm/microservices-platform -n microservices-platform --reuse-values
Error: UPGRADE FAILED: conflict occurred while applying object
  microservices-platform/microservices-platform-mtls security.istio.io/v1, Kind=PeerAuthentication:
  Apply failed with 1 conflict: conflict with "kubectl-patch" using security.istio.io/v1: .spec.mtls.mode

$ helm upgrade ... --set mesh.mtlsMode=PERMISSIVE   → 同じ conflict
$ helm upgrade ... --take-ownership                 → 同じ conflict
$ helm upgrade ... --force                          → "cannot use server-side apply and force replace together"
```

**「もう一度 `k8s-local-up.sh` を流せば収束する」は成り立たない。** [6/7] の `helm upgrade` が
そこで落ち、`set -euo pipefail` の下で up 全体が止まる。**復旧には人の手が要る**:

```console
$ kubectl -n microservices-platform delete peerauthentication microservices-platform-mtls
$ helm upgrade msp deploy/helm/microservices-platform -n microservices-platform --reuse-values
   mode=PERMISSIVE gen=1 managers=helm/Apply    ← 収束（本実測で実際に復旧させた）
```

これが本 issue の核心である。**「ドリフトしている」ではなく「宣言的経路を塞ぐ地雷が仕込まれている」。**

## 決定（詳細は IADR-0377）

1. **宣言（helm）を正とし、稼働の `mesh.mtlsMode` を書いてよいのは helm だけにする。**
   `istio-edge-up.sh` / `istio-edge-down.sh` の `kubectl patch` を
   `helm upgrade --reuse-values --set mesh.mtlsMode=<mode>` へ置き換える。
   **順序の制約（入口を Envoy へ移した後でしか STRICT にしない）は変えない** —— 段の中身を
   宣言的経路へ差し替えるだけである。
2. **経路B の既定は PERMISSIVE のまま据え置く。** STRICT は `ISTIO_MTLS_MODE=STRICT` の明示 opt-in で、
   `LOCALEDGE=1`（エッジが Istio Ingress Gateway）が前提である（IADR-0317）。
3. **AST↔MSP の扱いを明文化する**（#1159 受け入れ基準 4）。MSP の namespace 単位 PeerAuthentication は
   **MSP 宛の受信**を支配する。AST はメッシュ外のテナントなので、STRICT の間 AST→MSP の平文は落ちる。
   **これは障害ではなく決定の帰結である。** 恒久像は AST をメッシュへ入れること（AST#627）。
4. **門 G12 を足す**（宣言と稼働の乖離・helm 以外の field manager）。**同型の事故の 2 回目**である
   （1 回目 = 2026-08-30 の `istio-edge-up.sh` 由来のドリフト（#1159 が観測）、
   2 回目 = 2026-09-04 の #1168 計測スクリプトが STRICT を `kubectl apply` して戻さなかった件
   （#1168 の 2026-09-04 コメントが自ら記録している））。
5. **不変条件テスト**で「mode を helm の外から書くスクリプトを持たない」ことを固定する。

## 変更するファイル（宣言ファイル領域）

| ファイル | 変更 |
| --- | --- |
| `scripts/istio-edge-up.sh` | [5/5] の `kubectl patch` → `helm upgrade --reuse-values --set mesh.mtlsMode=STRICT` |
| `scripts/istio-edge-down.sh` | [1/4] の `kubectl patch` → 同上（PERMISSIVE）。release が無い場合は飛ばす |
| `scripts/check-stack-ready.js` | 門 **G12**（メッシュ宣言と稼働の一致・field manager の単独性） |
| `scripts/k8s-local-up.test.js` | 不変条件（patch 禁止・helm 経由・順序） |
| `scripts/README.md` | 2 スクリプトの行の追随 |
| `deploy/local/aliases/platform-infra-externalnames.yaml` | 値名の誤り `mesh.backchannelLogoutFromOutsideMesh` → `mesh.backchannelLogout.fromOutsideMesh`（#1168 が指摘・本 issue で直してよい） |
| `deploy/argocd/appproject.yaml` | 許可種別に `AuthorizationPolicy` を足す（後述） |
| `deploy/istio/README.md` / `deploy/local/edge-istio/README.md` / `deploy/local/README.md` | 手順の追随 |
| `docs/operations/operations.md` | 復旧手順（wedged release）を運用へ |
| `.ai-context/adr/IADR-0377_*.md` ＋ `.ai-context/adr/README.md` | 実装 ADR と索引 |

## 見つけた欠陥（本 PR で直す）

`deploy/argocd/appproject.yaml` の `namespaceResourceWhitelist` は `PeerAuthentication` と
`DestinationRule` を許可するが、**`AuthorizationPolicy` を許可していない**。
`mesh.backchannelLogout.fromOutsideMesh=true` の配備では chart が同種別をレンダリングするため、
**ArgoCD の本番同期はその時点で止まる**。IADR-0307 が同じ形の欠落（6 種別）を直したときの引き直しは
`fromOutsideMesh` が存在しない時点のものであり、#1152 が種別を 1 つ増やしたのに追随していなかった。

## 受け入れ基準（Given-When-Then）

- [ ] Given 稼働クラスタ / When `kubectl get peerauthentication -o yaml --show-managed-fields` を読む /
      Then `spec.mtls.mode` が helm の宣言と一致し、field manager が `helm` の 1 件だけである
- [ ] Given 手で `kubectl patch` した状態 / When `node scripts/check-stack-ready.js` を走らせる /
      Then **G12 が赤になる**（陰性対照）。patch していない状態では緑（陽性対照）
- [ ] Given `ISTIO_MTLS_MODE=STRICT` / When 宣言的経路（helm）で STRICT へ上げる /
      Then `PeerAuthentication` が STRICT になり、**field manager は helm のまま**である
- [ ] Given STRICT / When ai-stock-trading の使い捨て Pod から MSP へ平文で入る /
      Then 接続が RST で落ちる（＝ STRICT が効いていることの陽性対照。#1159 の症状の再現）
- [ ] Given STRICT ＋ 2 枚組あり / When 利用者の全セッションをログアウトさせる /
      Then `KC-SERVICES0057` が出ず、BFF に受理ログが残り、直後の `/bff/auth/me` が秒単位で 401（#1168）
- [ ] Given STRICT ＋ 2 枚組なし / When 同上 / Then 届かない（＝ 2 枚組が効いていることの弁別）
- [ ] Given 一連の計測の後 / When `kubectl get peerauthentication` を読む /
      Then 宣言どおり PERMISSIVE へ戻っており、`kubectl-patch` の manager が残っていない
- [ ] Given `scripts/verify-oidc-edge-flow.sh` / `scripts/verify-tool-oidc-logins.sh` / When 走らせる / Then 通る
- [ ] Given `node --test scripts/k8s-local-up.test.js` / When 走らせる / Then 不変条件が緑

## リスク

- `helm upgrade --reuse-values` は release 全体を再適用する。値が変わるのは `PeerAuthentication` の
  1 フィールドだけなので Pod は作り直されないが、**release が無い状態では失敗する** ——
  `istio-edge-down.sh` は冪等でなければならないので、release の存在を確かめてから呼ぶ。
- STRICT の計測窓の間、AST→MSP と platform-infra からの scrape が落ちる。**窓は短く保ち、
  終わったら宣言的経路で PERMISSIVE へ戻す**（戻したことを実測で示す）。

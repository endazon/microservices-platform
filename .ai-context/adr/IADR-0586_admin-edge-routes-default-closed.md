---
title: IADR-0586 管理ツールの集約入口（admin 50000）の経路は既定で置かず、ADMIN_EDGE=1 の明示の opt-in でだけ置く。Istio は *.localhost の directResponse 403、Traefik は router なしの 404、既定の入り方は各ツールへの port-forward
type: impl-adr
status: Accepted
related_ids: [NFR-11, NFR-18, ADR-0021, IADR-0091, IADR-0220, IADR-0317, IADR-0575]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md（エッジ）
related_specs:
  - ../specs/20261022_j4qv_admin-edge-default-closed.md
---

# IADR-0586: 管理ツールの集約入口の経路は既定で置かない（GHSA-j4qv-2w99-xg5p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: オーナー指示（2026-10-22。既定で到達できる範囲から外す・既存の運用を壊さない・開ける手段は値で明示の opt-in）・claude（形の選択）

## 起点・関連

- 起点: GHSA-j4qv-2w99-xg5p（多層防御の強化。最小の公開面・既定は fail-closed）。
- 計画: **ADR-0021**（エッジ）。要求 **NFR-11**・**NFR-18**。
- 前提: [IADR-0091](./IADR-0091_local-edge-aggregation-traefik.md)（管理ツールを 50000 へ集約）・[IADR-0220](./IADR-0220_admin-entrypoint-tls-and-http-redirect.md)（50000 の TLS 終端）・[IADR-0317](./IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md)（Istio の入口）・[IADR-0575](./IADR-0575_edge-block-keycloak-admin-surface.md)（エッジでの拒否の形）。

## コンテキストと課題

経路B（`LOCALEDGE=1`）は管理ツール 6 件（grafana / headlamp / vault / qdrant / wiki / argocd）を 50000 の 1 つの入口へホスト名で集約し、起動器が既定でその経路を置いてきた。6 件の中には認証を持たないものも含まれる。入口を残したまま、経路を既定で置かない形にしたい。条件は 3 つ: 管理者の入り方（port-forward・SSO つきの URL）を失わせない、開ける手段は値で明示する、Traefik と Istio の両エッジで同じ意味にする。

## 検討した選択肢

1. **送信元のアドレスで絞る（許可リスト。Traefik の `ipAllowList`・Istio の `remoteIpBlocks`）** — 経路B の外からの要求は k3s の ServiceLB（klipper。DNAT と MASQUERADE）・k3d のロードバランサ（TCP の代理）・Rancher Desktop の転送を経てエッジに届き、エッジに見える送信元はどの利用者でも同じ内側のアドレスになる。許可リストは区別に使えず、「絞ったつもり」の設定だけが残る。採らない。
2. **送信元が loopback（エッジの Pod への port-forward）だけを許す** — port-forward は kube の資格情報を要するので強い。ただしホストの 50000 は LoadBalancer の公開（k3d は作成時に固定、Rancher Desktop は内蔵 LB）が握っており、同じホストで `port-forward ... 50000:50000` は衝突する。別のポートにすると OIDC の redirect URI（`:50000` で登録済み）と Grafana の `root_url` が合わない。採らない。
3. **認証の前段を必須にする（oauth2-proxy 等）** — リポジトリに既存の仕組みは無い。新しい製品を足すと、版と digest の固定（計画 ADR-0135）・realm のクライアント・2 つのエッジへの組み込みが要り、6 件のうち 5 件が既に OIDC を持つ現状に対して重い。Istio の `RequestAuthentication` は Bearer のトークンしか検めず、ブラウザの画面には使えない。採らない。
4. **経路を既定で置かず、`ADMIN_EDGE=1` で置く（採用）** — 入口は残し、経路だけを opt-in にする。opt-in の挙動は従来と同一（URL・SSO・TLS とも変わらない）。既定の入り方は各ツールへの `kubectl port-forward`（kube の資格情報を要する）。

## 決定

1. **値**: `ADMIN_EDGE` は `1`（経路を置く）・`0` か未指定（置かない。既定）の 2 値。それ以外は起動器・`istio-edge-up.sh`・`istio-edge-down.sh` とも**クラスタに触る前に止まる**（`scripts/lib/admin-edge.sh` の `admin_edge_validate`）。
2. **Traefik**（`deploy/local/edge`）: `admin-ingress-infra.yaml`・`admin-ingress-wiki.yaml` を kustomization から外す。`admin_edge_traefik` が `ADMIN_EDGE=1` なら 3 ファイル（argocd は ns が在るときだけ）を apply し、それ以外は `delete --ignore-not-found` する（以前の実行で置いた経路も再実行で閉じる。`kubectl apply -k` は消えた資源を消さないため）。既定の 50000 は router が無いので 404。
   **Traefik に既定の拒否（ワイルドカード host の Ingress）は置かない。** Traefik の優先はルールの長さで決まり、`*.localhost` から作られる host の正規表現のルールは `Host(grafana.localhost)` より長く、opt-in の経路を上書きしてしまうためである。
3. **Istio**（`deploy/local/edge-istio`）: `virtualservice-admin.yaml` を kustomization から外し、`virtualservice-admin-deny.yaml`（`msp-admin-deny`。`*.localhost`・`msp-admin-edge`・`directResponse: 403`。IADR-0575 と同じ形）を常に置く。Envoy は host の完全一致を接尾辞のワイルドカードより優先するので、`admin_edge_istio` が opt-in で 6 host の経路を当てるとそちらが選ばれる。既定は 403。切り戻し（`istio-edge-down.sh`）は 6 host の経路を opt-in によらず消す。
4. **既定の入り方**: 各ツールへの `kubectl port-forward`（`deploy/local/edge/README.md`「管理ツールへの入り方」）。Headlamp・Wiki.js・Argo CD・Vault の CLI は port-forward 用の redirect が realm に登録済み。Grafana は `root_url` が 50000 の URL なので port-forward では SSO が完了せず、local admin で入る（従前から README に書かれた制約）。
5. **実測を CI に置く**: `scripts/check-stack-ready.js` の G4c が、6 host（`virtualservice-admin.yaml` から走査）へ `https://<host>:50000/` を投げ、**403 か 404** を要求する。503（経路は在るが後段が居ない）・2xx / 3xx は「経路が置かれている」として落とす。測れないは緑にしない。`ADMIN_EDGE=1` を宣言したときだけ飛ばす。

## 結果

- 良い影響: 既定の経路B で、管理ツールは 50000 から届かない。入口・ポート・証明書・realm の登録は変えないので、opt-in の挙動は従来と同一。再実行で閉じる（置いた経路が残らない）。
- 悪い影響: 50000 の URL（SSO つき）を常用する運用は、再実行のたびに `ADMIN_EDGE=1` を付ける必要がある（付けない再実行は経路を消す）。`scripts/verify-tool-oidc-logins.sh` は `ADMIN_EDGE=1` のクラスタでだけ測れる。

## 残余

1. opt-in で開けた入口は認証を持たないツールを含む。ホストの公開範囲（k3d は loopback 固定、Rancher Desktop は内蔵 LB の設定）に依存する。
2. `deploy/docker-compose.yml`（経路A）の公開ポートは本決定の対象外（エッジの入口ではない）。

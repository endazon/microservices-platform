---
title: 作業仕様書 — 管理ツールの集約入口（admin 50000）の経路を既定で置かず、明示の opt-in（ADMIN_EDGE=1）でだけ置く（GHSA-j4qv-2w99-xg5p）
type: spec
status: done
related_ids: [NFR-11, NFR-18, ADR-0021, IADR-0586, IADR-0091, IADR-0220, IADR-0317, IADR-0575]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md（エッジ）
issue: "GHSA-j4qv-2w99-xg5p"
---

# 作業仕様書 — 管理ツールの集約入口の経路を既定で置かない（GHSA-j4qv-2w99-xg5p）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0586**（新規）に置く。
> 多層防御の強化（最小の公開面・既定は fail-closed）の 6 点目。参照は GHSA の ID だけとする。
> 🔴 稼働中のクラスタには何も実行しない。試験は宣言の模型・起動器のスタブ・`kubectl kustomize` で行い、実測は検査器（G4c）を CI に置く。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR-11**・**NFR-18**。計画 ADR: **ADR-0021**（エッジ。入口の種類と位置は変えない）。矛盾しない。
- 実装 ADR: **IADR-0586**（新設）。前提 **IADR-0091**（50000 への集約）・**IADR-0220**（TLS 終端）・**IADR-0317**（Istio の入口）・**IADR-0575**（エッジでの拒否の形）。

## 要件（オーナー指示 2026-10-22）

1. 管理用の入口（Istio の `msp-admin-edge` と 6 host の経路、Traefik の同等の入口）を、既定で到達できる範囲から外す。
2. 既存の運用（管理者の port-forward・SSO つきの URL）を壊さない形を選び、IADR で比較する。
3. 開ける手段は値（環境変数）で明示の opt-in にする。

## 母集合（規則 9・10）

走査語: `50000`・`admin-edge`・`msp-admin`・`entrypoints: *admin`・`type: *(NodePort|LoadBalancer)`・`nodePort:`・`hostPort:`・`kind: (Ingress|Gateway|HTTPRoute|VirtualService|IngressRoute)`（`git grep`。`src/ai-stock-trading` と `.ai-context/` を除く全体）。

| 場所 | 種別 | 扱い |
| --- | --- | --- |
| `deploy/local/edge-istio/gateway.yaml` の `msp-admin-edge`（50000・`*.localhost`） | Istio Gateway | **残す**（入口。opt-in の経路の載り先） |
| `deploy/local/edge-istio/virtualservice-admin.yaml`（6 VirtualService） | Istio | **既定で置かない**（kustomization から外し、opt-in で当てる・既定で消す） |
| `deploy/local/edge-istio/virtualservice-admin-deny.yaml`（新設） | Istio | 既定の拒否（`*.localhost` に 403） |
| `deploy/local/edge/admin-ingress-infra.yaml`（grafana / headlamp / vault / qdrant）・`admin-ingress-wiki.yaml`・`argocd-ingress.yaml` | Traefik Ingress（admin entrypoint） | **既定で置かない**（同上） |
| `deploy/local/edge/traefik-entrypoint.yaml`（admin:50000 の entrypoint） | Traefik | 残す（router が無ければ 404。opt-in の載り先） |
| `deploy/istio/ingressgateway-values-local.yaml`（`type: LoadBalancer`・50000） | Service | 残す（入口のポート。G5 が在ることを見る） |
| `scripts/k8s-local-up.sh` の k3d `-p 127.0.0.1:50000:50000@loadbalancer` | ホストの公開 | 残す（loopback 固定は既存。作成時に固定のため触らない） |
| `scripts/istio-edge-up.sh`・`istio-edge-down.sh`・`k8s-local-up.sh` の経路の当て方 | 起動器 | `scripts/lib/admin-edge.sh` を通す |
| `deploy/local/edge/keycloak-*.yaml`・`virtualservice-app.yaml`・`platform-frontend-ingress.yaml`（80/443） | 利用者の入口 | 対象外（Keycloak の管理面は IADR-0575 で拒否済み） |
| `deploy/helm/.../templates/edge.yaml`（chart の Gateway・`edge.hosts` の 80） | chart | 対象外（管理ツールを載せていない） |
| `deploy/helm/.../templates/wikijs.yaml` の Ingress | chart | 対象外（既定 `ingress.enabled=false`。IADR-0020） |
| `deploy/argocd/appproject.yaml`（許す kind の一覧） | 宣言 | 対象外（資源を作らない） |
| `deploy/docker-compose.yml` の `ports:` | 経路A | 対象外（エッジの入口ではない。残余として報告する） |
| NodePort / hostPort | — | 該当なし（`nodePort:`・`hostPort:` の宣言は無い。knowledge の試験が NodePort の不在を固定済み） |
| `src/ai-stock-trading`（submodule） | 別リポジトリ | 対象外 |
| 50000 の URL を使う手順（`deploy/local/*/README.md`・`docs/operations/local-sso-recovery-runbook.md`・`scripts/verify-tool-oidc-logins.sh`） | 手順 | 入口の README に opt-in と port-forward を書き、検証器に前提を注記する。各ツールの README の URL は opt-in 時の URL として残す |

## 受け入れ基準と試験の写像

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | 既定（`ADMIN_EDGE` 未指定・0）の `LOCALEDGE=1` は管理ツールの経路を置かず、置かれていれば消す（Traefik・Istio） | `k8s-local-up.test.js`「IADR-0586: LOCALEDGE=1 の既定は…」「Istio のエッジでも…」 |
| AC2 | `ADMIN_EDGE=1` のときだけ経路を置く（従来と同じ 3 ファイル / 6 host）。値は `istio-edge-up.sh` へ渡る | 同「ADMIN_EDGE=1 のときだけ…」「Istio のエッジでも…」・既存の argocd の 2 件 |
| AC3 | 0 / 1 / 未指定 以外はクラスタに触る前に止まる | 同「ADMIN_EDGE が 0 / 1 / 未指定 以外なら…」・「切り戻しも…」（検めが最初の kubectl より前） |
| AC4 | Istio の 50000 には既定の拒否（Gateway と同じ host・403・転送なし）が居て、既定はどの host も拒否に、opt-in は各 host が自分の経路に当たる。Traefik と Istio の host は一致 | 同「管理ツールの経路は kustomization に含めず…」「既定はどの管理ツールの host も…」（Envoy の host の選び方の模型・陰性対照つき） |
| AC5 | 切り戻しは Istio の 6 host の経路を消し、Traefik の経路は ADMIN_EDGE に従う | 同「切り戻し（istio-edge-down.sh）も…」 |
| AC6 | 稼働のエッジで、6 host が 403 / 404（503・2xx・3xx・測れないは赤） | `check-stack-ready.js` G4c（自己試験つき。CI の integration-stack が `--live` で走らせる） |
| AC7 | 宣言はスキーマに適合する | `check-deploy-manifests.js`（kubeconform） |

## 変異

| 変異 | 赤になる試験 |
| --- | --- |
| M1: `admin_edge_traefik` の分岐を外し常に apply する | AC1 |
| M2: edge-istio の kustomization に `virtualservice-admin.yaml` を戻す | AC4 |
| M3: 拒否の host を `grafana.localhost` だけにする | AC4（Gateway の host と違う・他の host が拒否に当たらない） |
| M4: G4c で 503 を「閉じている」と読む | G4c の自己試験 |
| M5: `istio-edge-up.sh` へ `ADMIN_EDGE` を渡さない | AC2（Istio） |

## 配備前の手順（PoC。値は書かない）

PR 本文に順序つきで書く。要点: 50000 の URL を使っている管理者は、配備（起動器の再実行）の前に入り方を port-forward へ切り替えるか、再実行に `ADMIN_EDGE=1` を付ける。

## 残余

- opt-in で開けた入口は認証を持たないツールを含む（ホストの公開範囲に依存）。
- 経路A（docker-compose）の公開ポートは対象外。

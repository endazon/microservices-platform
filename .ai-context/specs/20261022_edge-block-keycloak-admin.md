---
title: 作業仕様書 — エッジで Keycloak の管理面（/admin・/realms/master）を遮断し、利用者の realm の経路は保つ（GHSA-rh44-px44-p48q）
type: spec
status: done
related_ids: [NFR-11, NFR-18, ADR-0021, ADR-0097, IADR-0574, IADR-0227, IADR-0432, IADR-0524, IADR-0076]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md（エッジは Istio Gateway。経路B の Traefik はその前段）
  - planning:projects/microservices-platform/07_adr/ADR-0097_timing-floor-release-default-on-and-periodic-review.md 決定 2（リセット申請の床は既定オン）
issue: "GHSA-rh44-px44-p48q"
---

# 作業仕様書 — エッジで Keycloak の管理面を遮断する（GHSA-rh44-px44-p48q）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0574**（新規）に置く。
> 多層防御の強化（最小の公開面）の 3 本目。参照は GHSA の ID だけとする。
> 🔴 稼働中のクラスタには何も実行しない。試験は宣言の模型・`kubeconform`・`helm template` で行い、実測は検査器（G4b）を CI に置く。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR-11**（外部から到達し得る端点）・**NFR-18**。計画 ADR: **ADR-0021**（エッジ）・**ADR-0097 決定 2**（床は既定オン。床の route の位置だけを変え、床の有無・対象は変えない）。矛盾しない。
- 実装 ADR: **IADR-0574**（新設）。前提 **IADR-0227**（Keycloak をエッジへ出した。管理コンソールは別件で扱うとした）・**IADR-0432**（床）・**IADR-0524**（hostname v2）・**IADR-0076**（chart のエッジ）。

## 要件（オーナー指示 2026-10-22）

1. エッジ（Traefik などの ingress）で、Keycloak の `/admin` と `/realms/master` を外から届かないようにする。
2. 管理者が使う経路（port-forward など）を README に書く。
3. ログイン・トークン・アカウントコンソールなど、利用者が使う realm の経路を壊さないことを試験で固定する。

## 受け入れ基準と試験の写像

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | Traefik のエッジ: `/admin`・`/realms/master` 以下は拒否の Ingress（全拒否の中間処理）が受け、利用者の realm の経路（discovery・auth・token・certs・login-actions・account・resources、別の realm、`/realms/masterpiece` のような別名）は `keycloak-edge` が受ける | `k8s-local-up.test.js`「IADR-0574: Traefik のエッジ」（k8s の Prefix の要素一致 × ルールの長さの優先の模型・陰性対照つき） |
| AC2 | Istio のエッジ: `msp-keycloak-edge` の先頭が 403 の拒否。床の overlay を当てても拒否が先、利用者の realm の申請の POST は床に当たる | `k8s-local-up.test.js`「IADR-0574: Istio のエッジ」・`reset-floor.test.js` 4 |
| AC3 | chart のエッジ（`edge.oidc.enabled`）は `/realms/` の転送より前に master を 403 で拒む | `k8s-local-up.test.js`「chart のエッジ」・`helm template --set edge.enabled=true --set edge.oidc.enabled=true` を kubeconform で検証 |
| AC4 | 管理コンソールは port-forward の先（`KC_HOSTNAME_ADMIN=http://localhost:8080`）で、README に経路がある | `k8s-local-up.test.js`「管理コンソールの URL は port-forward の先」 |
| AC5 | 稼働のエッジで、管理面が 403 / 404・利用者の経路が 2xx / 3xx（測れないは緑にしない） | `check-stack-ready.js` G4b（自己試験つき。CI の integration-stack が `--live` で走らせる） |
| AC6 | 宣言はスキーマに適合する（Traefik の Middleware・Istio の directResponse） | `check-deploy-manifests.js`（kubeconform。CRD のカタログは固定の SHA） |

## 方式

- **Traefik**: Ingress は「除外」を書けないので、同じ host のより長いパス（`/admin`・`/realms/master`。pathType Prefix は要素単位）だけを別の Ingress `keycloak-edge-admin-deny` で受け、中間処理 `keycloak-admin-deny`（`ipAllowList` の送信元を誰にも当たらない 1 アドレスに絞った全拒否。403）を掛ける。Traefik はルールの長い router を優先する。
- **Istio**: `msp-keycloak-edge` の先頭に `directResponse: 403` の route（`/admin` の prefix・`/realms/master` の exact と `/realms/master/` の prefix）を置く。床（`edge-istio-reset-floor`）は `/spec/http/1` へ入れる（床の正規表現は realm 名を問わないので、拒否より前だと master へ届く）。
- **chart**（`edge.oidc.enabled` のときだけ Keycloak へ転送する）: `/realms/` の転送の前に master の拒否を置く。`/admin` はもともと Keycloak へ転送しない（SPA へ落ちる）。
- **管理者の経路**: `kubectl -n platform-infra port-forward svc/keycloak 8080:8080` → `http://localhost:8080/admin/`。`KC_HOSTNAME_ADMIN` を同じ URL にして、管理コンソールがエッジの URL へ誘導されないようにする。

## 母集合（規則 9・10）

走査語: `keycloak.localhost`・`msp-keycloak-edge`・`/spec/http/0`・`/realms/`（deploy・scripts・.github・docs）。

| 場所 | 扱い |
| --- | --- |
| `deploy/local/edge/keycloak-ingress.yaml` | 変えない（`/` のまま。拒否は別の Ingress） |
| `deploy/local/edge-istio/virtualservice-app.yaml`（`msp-keycloak-edge`） | 先頭に拒否 |
| `deploy/local/edge-istio-reset-floor/kustomization.yaml` | 床を `/spec/http/1` へ・注記 |
| `deploy/helm/.../templates/edge.yaml` | master の拒否 |
| `deploy/local/edge-istio/virtualservice-admin.yaml`（50000） | 対象外（Keycloak を転送しない） |
| 各サービスの Admin REST・realm の後追い・検査器（in-cluster の `http://keycloak:8080` か port-forward） | 影響なし（エッジを通らない） |
| `scripts/verify-oidc-edge-flow.sh`・`verify-tool-oidc-logins.sh`（エッジ越しのログインの確かめ） | 利用者の realm だけを使うので影響なし |

## 変異

| 変異 | 赤になる試験 |
| --- | --- |
| M1: kustomization から拒否の Ingress を外す | AC1 |
| M2: Istio の拒否を先頭から catch-all の後ろへ移す | AC2 |
| M3: 床を `/spec/http/0` へ戻す | AC2（reset-floor 4・模型） |
| M4: `/realms/master` の prefix を末尾スラッシュなしにする（`/realms/masterpiece` を巻き込む） | AC1/AC2 の利用者の経路 |
| M5: G4b で 401 を「拒否」と読む・測れないを通す | G4b の自己試験 |

## 配備前の手順（PoC。値は書かない）

PR 本文に順序つきで書く。要点: 管理コンソールを使う手順を port-forward へ切り替えてから配備する。入れ替えの前後どちらでもよい。

## 残余

- Traefik の中間処理の名前（`ipAllowList`）は Traefik v3 のもの。起動器は k3s を v1.35 に固定する。Rancher Desktop で古い k3s（Traefik v2）を使う場合は中間処理が読めず router が無効になり得る —— G4b が落ちて気付ける。

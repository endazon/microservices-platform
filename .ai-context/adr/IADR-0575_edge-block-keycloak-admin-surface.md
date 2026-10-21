---
title: IADR-0575 エッジで Keycloak の管理面（/admin・/realms/master）を 403 で拒む。Traefik は長いパスの拒否の Ingress、Istio と chart は先頭の directResponse、管理者は port-forward（KC_HOSTNAME_ADMIN）
type: impl-adr
status: Accepted
related_ids: [NFR-11, NFR-18, ADR-0021, ADR-0097, IADR-0227, IADR-0432, IADR-0524, IADR-0076]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md（エッジ）
  - planning:projects/microservices-platform/07_adr/ADR-0097_timing-floor-release-default-on-and-periodic-review.md 決定 2（床は既定オン）
related_specs:
  - ../specs/20261022_edge-block-keycloak-admin.md
---

# IADR-0575: エッジで Keycloak の管理面を拒む（GHSA-rh44-px44-p48q）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: オーナー指示（2026-10-22）・claude（エッジごとの拒否の形）

## 起点・関連

- 起点: GHSA-rh44-px44-p48q（多層防御の強化。最小の公開面）。
- 計画: **ADR-0021**・**ADR-0097 決定 2**。要求 **NFR-11**・**NFR-18**。
- 前提: [IADR-0227](./IADR-0227_edge-host-pod-side-resolution.md)（Keycloak を websecure の `keycloak.localhost` へ出した。管理コンソールの除外は別件とした）・[IADR-0432](./IADR-0432_reset-timing-median-judgement-and-response-floor.md)（リセット申請の床）・[IADR-0524](./IADR-0524_keycloak-26-upgrade.md)（hostname v2）・[IADR-0076](./IADR-0076_edge-bff-routing-and-oidc-hostname.md)（chart のエッジ）。

## コンテキストと課題

IADR-0227 は、ブラウザの OIDC を成立させるため Keycloak を `/` で丸ごとエッジへ出し、管理コンソール（`/admin`）の除外を「Traefik の Ingress では除外を書けない」として後回しにした。管理用の realm（`master`）の経路も同じく出ている。利用者が使う realm の経路は保ったまま、管理面だけをエッジで拒む。

## 検討した選択肢

1. **許す経路を列挙する（`/realms/platform`・`/resources` だけ）** — 公開面は最小だが、realm を足すたび（AST の realm を同梱したとき等）にエッジの宣言を直す必要があり、忘れるとログインが壊れる。
2. **管理面だけを拒む（採用）** — 拒む経路は Keycloak の構造で決まり（`/admin`・`/realms/master`）、realm を足しても変わらない。
3. **Keycloak の設定だけで閉じる（`KC_HOSTNAME_ADMIN` による管理コンソールの分離）** — エッジの外の設定に依存し、`/realms/master` の OIDC 端点は残る。エッジの拒否と併用する（決定 4）。

## 決定

1. **Traefik**（`deploy/local/edge/keycloak-admin-deny.yaml`）: 同じ host の `/admin`・`/realms/master`（pathType Prefix＝要素単位）を受ける Ingress `keycloak-edge-admin-deny` に、全拒否の中間処理 `keycloak-admin-deny`（`ipAllowList` の送信元を `255.255.255.255/32` だけにする。403）を掛ける。Traefik はルールの長い router を優先するので、`keycloak-edge` の `/` へ落ちない。中間処理の名前は Traefik v3 のもの（起動器は k3s を v1.35 に固定する）。
2. **Istio**（`msp-keycloak-edge`）: 先頭に `directResponse: 403` の route（`/admin` の prefix・`/realms/master` の exact・`/realms/master/` の prefix）を置く。prefix は文字列の前方一致なので、master は末尾スラッシュつきに分けて別名の realm を巻き込まない。リセット申請の床は `/spec/http/1`（拒否の直後・catch-all の前）へ入れる。床の正規表現は realm 名を問わないため、拒否より前だと master の申請が床を通って届く。
3. **chart**（`edge.oidc.enabled`）: `/realms/` の転送の前に master の拒否（403）を置く。`/admin` は転送していない。
4. **管理者の経路は port-forward**（`kubectl -n platform-infra port-forward svc/keycloak 8080:8080` → `http://localhost:8080/admin/`）。`KC_HOSTNAME_ADMIN=http://localhost:8080` で管理コンソールの URL をそこへ描かせる（エッジへ誘導されない）。各サービスの Admin REST・realm の後追い・検査器は in-cluster の URL か port-forward を使い、影響を受けない。
5. **実測を CI に置く**: `scripts/check-stack-ready.js` の G4b がエッジ越しに管理面（403 / 404）と利用者の realm の経路（2xx / 3xx）を測る。拒否の宣言は、読めないと黙って無効になり得る（Traefik の中間処理）ので、宣言の静的検査だけでは足りない。

## 結果

- 良い影響: エッジから管理面に届かない。realm を足してもエッジの宣言は変わらない。
- 悪い影響: 管理コンソールはエッジから使えない（port-forward が要る）。port-forward のローカルのポートは 8080 に揃える（違えると `KC_HOSTNAME_ADMIN` と食い違う）。

## 残余

1. 古い k3s（Traefik v2）では中間処理の名前が違い、拒否の router が無効になり得る。G4b で気付ける。
2. Keycloak 自身の hostname v2 の挙動（`KC_HOSTNAME_ADMIN` での管理コンソールのログイン）は稼働の PoC で確かめる（配備の手順に書いた）。

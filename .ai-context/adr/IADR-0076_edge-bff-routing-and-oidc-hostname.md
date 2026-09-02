---
title: IADR-0076 エッジ /bff/* ルーティングは Helm の edge ブロック（Istio Gateway/VirtualService・rewrite 無し）で templating し、経路B は values-local で無効化する。ブラウザ OIDC issuer は同一エッジ host での OIDC パススルー機構＋手順で統一する
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - NFR
  - IADR-0017
  - IADR-0026
  - IADR-0066
  - IADR-0070
  - IADR-0071
  - IADR-0072
  - IADR-0086
  - IADR-0227
  - IADR-0243
  - IADR-0317
author: claude
created: 2026-07-19
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# IADR-0076: AST 統合スタック疎通のためのエッジ /bff/* ルーティングとブラウザ OIDC issuer 統一

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）／ endazon（マージ判断）

## 起点・関連

- 関連する計画書 ID（MSP・機械追跡）: **FR-14**（構成変更で完結する疎結合ユニット・合成点）／**NFR**（エッジ集約・サービスメッシュ）
- 関連する計画書 ID（AST・プロジェクト修飾。本 PR は到達性の担保のみ）: **AST/FR-17**（全体前提条件の一元管理）／**AST/UC-06**（設定の閲覧/変更）※ MSP の同番号（FR-17 は不在・UC-06=文書正規化変換）とは別物のため修飾する（cf. #302）
- 関連 ADR: [IADR-0017](./IADR-0017_internal-service-auth-network-isolation.md)（外部入口は BFF に一本化・内部 API は無公開。※本文書は「ネットワーク分離を第一防御」の
  暫定運用部分のみ [IADR-0026](./IADR-0026_mesh-mtls-supersedes-network-isolation.md) に Superseded。**「外部入口を BFF へ一本化」の原則は現行でも有効**で、本 IADR はこの原則を
  エッジ実装に落とす）／[IADR-0026](./IADR-0026_mesh-mtls-supersedes-network-isolation.md)（mTLS で暫定運用を解消）／[IADR-0066](./IADR-0066_local-k8s-dev-environment.md)（ローカル k8s dev＝経路B）／
  [IADR-0070](./IADR-0070_ast-frontend-integration.md)・[IADR-0071](./IADR-0071_ast-risk-controls-bff-integration.md)・[IADR-0072](./IADR-0072_ast-monitor-bff-integration.md)（AST 3 サービスの deploy 登録・`/bff/*` pass-through の先行決定）
- Issue: MSP #284（live 疎通トラッカ）／先行 #283(PR #285)・#287(PR #289)・#288(PR #294)
- 作業仕様書: [`docs/specs/20260719_issue-284-live-integration-wiring.md`](../specs/20260719_issue-284-live-integration-wiring.md)

## 背景・課題

#283/#287/#288 で AST の 3 画面系サービス（Configuration／RiskManagement／MarketMonitor）を MSP へ
in-repo 登録した（deploy 既定 disabled・`/bff/*` pass-through 済み）。残る統合スタック疎通（#284）に必要な
リポ内配線のうち、次の 3 点が未整備だった:

1. **エッジ `/bff/*` ルーティングが chart に無い**。chart の Istio 面は mTLS（`istio-mtls.yaml`）のみで、
   外部からの `/bff/*` を BFF へ通す `Gateway`/`VirtualService` が templating されていない。
2. **経路B で 3 サービスが無効**。fail-safe 既定 disabled のため、経路B で有効化する values 配線が無い。
3. **ブラウザ OIDC の issuer/hostname 不一致**。issuer を in-cluster 正準名 `http://keycloak:8080` に固定
   （サービス間 JWT 用）しており、ブラウザからのログインでは `iss` を検証側（`Auth__Authority`）とそろえられない。

## 決定

### 決定1: エッジは Helm の `edge` ブロックで Istio Gateway/VirtualService を templating（rewrite 無し）

`deploy/helm/microservices-platform/templates/edge.yaml` を新設し `.Values.edge.enabled` でゲートする。
`Gateway`（`edge.gateway.selector` の ingressgateway・`edge.hosts`／`edge.port`）と `VirtualService`
（`/bff`(exact)＋`/bff/`(prefix) → `edge.bff.service:edge.bff.port`）を描画する。

- **rewrite を張らない**理由: フロント nginx（`nginx.default.conf.template`）が `location /bff/ { proxy_pass ${BFF_UPSTREAM}; }`
  で **元 URI を無改変**に上流へ渡しており、BFF は `/bff` プレフィックスを剥がさず受ける。エッジも同じ契約
  （`/bff/...` をそのまま）に統一し、compose とバイト等価な到達経路にする。
- **`mesh.enabled` と同方針**: 本番 values は `edge.enabled: true`（Istio 前提）、経路B（`values-local.yaml`）は
  Istio 未導入のため `edge.enabled: false`。これで `helm template` 既定は本番像を描画し、経路B は別経路（手順）に委ねる。
- **NetworkPolicy との相互作用も配線する**: `networkpolicy.yaml` の default-deny は同 Namespace 内 ingress のみ許可する。
  ingressgateway は通常別 Namespace（`istio-system`）に居るため、素のままでは gateway→`bff-service` が L3/L4 で塞がれ、
  VirtualService を描画しても実到達しない。`edge.enabled` かつ `networkPolicy.enabled` のとき、`edge.gateway.namespace`
  （既定 `istio-system`）から `bff-service:edge.bff.port` への ingress のみを許可する `NetworkPolicy`
  （`allow-edge-ingress-to-bff`）を追加し、多層防御を保ったまま必要最小の穴を開ける。
  許可粒度は **namespace 単位**（`kubernetes.io/metadata.name` で `istio-system` 全体）とする。ingressgateway の Pod
  ラベルは Istio インストールプロファイルにより異なり `edge.gateway.selector` と一致する保証が無いためで、より厳密に
  絞りたい場合は将来 `podSelector`（ingressgateway ラベル）を併記して gateway Pod のみへ限定できる（本 PR は既定を優先）。

### 決定2: 経路B の 3 サービス有効化は values-local の extraEnv 注入（本番 values 不変・postgres.yaml 不変）

`values-local.yaml` で 3 サービスを `enabled: true` にし、`ConnectionStrings__DefaultConnection`（全 3）と
`RabbitMq__ConnectionString`（risk-management／market-monitor）を extraEnv で注入する。

- **接続資格情報は `ai/ai`**: 経路B の `postgres.yaml` は 3 DB を **owner=ai** で作成する（IADR-0066）。よって
  経路B は `ai/ai`＋正 DB 名を注入し、`postgres.yaml` を**無改変**に保つ。compose は自前 init が owner=kp のため
  `kp/kp` を注入しており、**各スタックが内部整合を保つ**（DB owner とアプリの接続ユーザを一致させ 42501 を回避）。
- **本番 values は不変**（3 サービス既定 disabled 維持）: 稼働導入は環境固有の DB/Secret/イメージが前提のため、
  fail-safe 既定を崩さない。有効化は経路B の overlay と CD の `--set` に限定する。

### 決定3: ブラウザ OIDC issuer は「issuer URL を browser/cluster で一致させる」原則で解く（手順A 主・手順B 機構）

> **［2026-08-31 追記 / #780］本決定 3 の「手順A 主／手順B 任意」という主従は
> [IADR-0243](./IADR-0243_keycloak-edge-issuer-migration.md) が Supersede した。**
> **経路B の既定は手順B（同一エッジ host 集約）である。** issuer は
> `https://keycloak.localhost/realms/platform` であり、単一情報源は
> `deploy/local/infra/keycloak.yaml` の `KC_HOSTNAME_URL` である。
>
> 🔴 **#780 の受け入れ基準は「IADR-0076 を改定するか、手順 B の既定化を決める新 IADR を起こすか、
> どちらかを明示的に選ぶ」ことを求めていた。選んだのは後者である**
> —— 新 IADR（[IADR-0243](./IADR-0243_keycloak-edge-issuer-migration.md)、2026-08-22 Accepted）が
> 既に既定化を決めており、**本追記はその決定を旧側から辿れるようにするためのものである**
> （本 IADR の決定文そのものは書き換えない）。
>
> **覆ったのは主従だけで、原則（`iss` と検証側が同一 URL であること）は 1 文字も動いていない。**
> 手順A が前提にしていた「(iii) in-cluster から同 host を解決させる」は
> [IADR-0227](./IADR-0227_edge-host-pod-side-resolution.md)（`coredns-custom`）が、
> .NET 側の追随は [IADR-0086](./IADR-0086_oidc-issuer-metadata-split.md) の
> metadata / issuer 分離が担う。**手順A は経緯として残すが、いま実行すると `iss` が合わない。**
>
> **決定 1（エッジ `/bff/*` のルーティング）と決定 2（経路B の 3 サービス有効化）は動いていない。**
> なお決定 1 が言う「経路B は Istio 未導入なので `edge.enabled: false`」は、`ISTIO=1` の経路に限り
> [IADR-0317](./IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md) が別実装（`deploy/local/edge-istio/`）を与えた。

原則: token の `iss` と検証側 `Auth__Authority` が同一 URL であること。これを 2 手順で満たす。

- **手順A（推奨・realm/keycloak.yaml 無改変で成立）**: ブラウザに **in-cluster 名を解決させる**
  （hosts `127.0.0.1 keycloak` ＋ `port-forward svc/keycloak 8080:8080`）。browser も cluster も
  `http://keycloak:8080` を共有し `iss` が一致する。SPA は既存 origin（localhost:3100）を流用でき、
  `spa-web` の redirectUris/webOrigins は**そのまま**（realm 変更不要）。← 既知制約の実体的な解。
- **手順B（単一エッジ host 集約・任意）**: `VirtualService` に任意の OIDC パススルー（`edge.oidc.enabled`・既定 off）を
  持たせ、有効時 `/realms/`・`/resources/` を `edge.oidc.host:port`（既定 `keycloak:8080`）へ通す。SPA/`/bff`/`/realms` を
  同一エッジ host に集約でき、この場合のみ運用者が edge host を `spa-web` へ手動追記し `Auth__Authority` を上書きする。
  in-cluster から同 host を解決させる部分（CoreDNS or backend の metadata/issuer 分離）は稼働環境依存＝live。

realm・keycloak.yaml は**無改変**（手順A が既存構成で成立するため）。in-repo は原則・機構（chart knob）・手順までとし、
実ブラウザログイン疎通は #284 コメントと follow-up issue へ分離する。

## 却下した代替案

- **エッジで `/bff` を rewrite して剥がす**: BFF が `/bff` プレフィックス前提のため不整合になる（compose 経路と乖離）。却下。
- **経路B postgres.yaml を owner=kp に変更し compose と統一**: AST 標準（POSTGRES_USER=ai）から乖離し、別 AST chart
  （ns ai-stock-trading）の想定と衝突する恐れ。overlay 側で資格情報をそろえる方が影響が局所的。却下。
- **本番 values で 3 サービスを enabled に変更**: 環境固有依存（DB/Secret/実イメージ）を伴い fail-safe を崩す。却下。
- **CoreDNS 書き換えで in-cluster に issuer 名を解決させる**: 侵襲的で dev 環境ごとに壊れやすい。エッジ host 共有
  （同一 VirtualService）の方が宣言的で再現性が高い。dev の手順として CoreDNS 案も README に併記するに留める。

## 影響・可逆性

- 追加のみ（新 template＋values ブロック＋overlay＋docs。realm/compose/MAPPING/postgres.yaml は無変更）。`edge.enabled: false` と 3 サービス disabled で
  既存挙動は不変（後方互換）。`edge` ブロック削除で完全に戻せる。
- CI: MAPPING/compose build 定義に無変更のため #275（image-mapping）は不変。`helm template` で描画検証する。

## 検証

- `helm template`（既定）で Gateway/VirtualService が `/bff` を `bff-service:8080` へ rewrite 無しで描画。OIDC route 非出力。
- `helm template -f values-local.yaml` で edge 非描画・3 サービスの Deployment/Service 描画・env 正当（ai/ai・正 DB 名・RabbitMq）。
- `helm template` 既定で 3 サービス非描画（fail-safe）。`edge.oidc.enabled=true` でのみ OIDC route 描画。既存 Node 検査 緑。

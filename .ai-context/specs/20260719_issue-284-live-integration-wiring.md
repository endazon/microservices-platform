---
title: AST 統合スタック疎通の in-repo 配線（Istio エッジ /bff/* ・経路B サービス有効化・OIDC issuer 統一手順）（Issue #284）
type: spec
status: done
related_ids:
  - FR-14
  - NFR
  - IADR-0066
  - IADR-0070
  - IADR-0071
  - IADR-0072
  - IADR-0076
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-14 構成変更で完結する疎結合ユニット / NFR エッジ集約・メッシュ)
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md (エッジ/メッシュ)
  - AST 側計画（別リポ endazon/ai-stock-trading の planning）: AST/FR-17 全体前提条件の一元管理 / AST/UC-06 設定の閲覧・変更（本 PR は到達性の担保のみ・機械追跡外）
related_specs:
  - "../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../adr/IADR-0070_ast-frontend-integration.md"
  - "../adr/IADR-0071_ast-risk-controls-bff-integration.md"
  - "../adr/IADR-0072_ast-monitor-bff-integration.md"
  - "../../deploy/local/README.md"
---

# 仕様書: AST 統合スタック疎通の in-repo 配線（Issue #284）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR・MSP): FR-14（構成変更で完結する疎結合ユニット・合成点）
- 機能要求／ユースケース(AST・プロジェクト修飾): AST/FR-17（全体前提条件の一元管理）／AST/UC-06（設定の閲覧・変更）
  ※ MSP の同番号（FR-17 は不在・UC-06=文書正規化変換）とは別物のため `AST/` で修飾する（本 PR は到達性の担保のみ。cf. #302）
- 非機能要件(NFR): エッジ集約（外部入口を BFF に一本化・IADR-0017）／サービスメッシュ（ADR-0005）
- 実装判断: [IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md)（本 PR: エッジ /bff/* ルーティングの chart templating・経路B 有効化・OIDC issuer 統一の機構と手順）／
  [IADR-0070](../adr/IADR-0070_ast-frontend-integration.md)・[IADR-0071](../adr/IADR-0071_ast-risk-controls-bff-integration.md)・[IADR-0072](../adr/IADR-0072_ast-monitor-bff-integration.md)（3 サービスの deploy 登録・BFF pass-through の先行決定）／[IADR-0066](../adr/IADR-0066_local-k8s-dev-environment.md)（経路B）
- Issue: #284（本 issue・live 疎通トラッカ・priority:should）／先行 #283(PR #285)・#287(PR #289)・#288(PR #294)

## 目的・背景（As-Is）

#283/#287/#288 で AST の 3 画面系サービス（`ConfigurationService`／`RiskManagementService`／`MarketMonitorService`）の
**MSP 側 in-repo 実装**（submodule pin・SPA 合成・`/bff/*` pass-through・deploy 登録・DB プロビジョニング・trading-owner realm）
を完了した。3 サービスは helm values・compose・`k8s-local-images.sh` の MAPPING・DB 作成スクリプトの全てに
**既定 disabled（fail-safe）** で登録済みである。

残る「稼働 k3d 依存で in-repo では緑にできない疎通・E2E」を #284 が追跡する。本 PR は、その live 疎通に必要な
**リポ内で描画・検証まで完結する配線**を実装し、実ブラウザ／E2E を明確に分離する。As-Is の 3 ギャップ:

1. **エッジ `/bff/*` ルーティングが chart に未 templated**: chart の Istio 面は mTLS（`istio-mtls.yaml`）のみで、
   外部からの `/bff/*` を BFF へ通す `Gateway`/`VirtualService` が無い（#284「2f 現状チャート未templated＝別途整備」）。
2. **経路B で 3 サービスが無効**: 既定 disabled のため、経路B（ローカル k8s）で有効化する values 配線が無い。
3. **ブラウザ OIDC の issuer/hostname 課題**: Keycloak issuer を in-cluster 正準名 `http://keycloak:8080` に固定して
   おり（サービス間 JWT 用）、ブラウザからの OIDC ログインには ingress/hostname 調整が要る（既知制約・README）。

## 調査で確定した事実

- **BFF は `/bff` プレフィックスを剥がさず受ける**: フロント nginx（`src/platform/frontend/nginx.default.conf.template`）は
  `location /bff/ { proxy_pass ${BFF_UPSTREAM}; }`（末尾 URI 無し）で **元 URI を無改変**で上流へ渡す。
  → エッジ `VirtualService` も **rewrite 不要**（`/bff/...` をそのまま BFF へ）。
- **3 サービスの接続情報は env 注入必須**: AST の `appsettings.json` は `ConnectionStrings`／`RabbitMq`／`Auth` を
  持たない（Serilog と AllowedHosts のみ）。compose は env で注入する。`deployment.yaml` は `Auth__Authority` と
  `Otlp__Endpoint` のみ自動注入する。→ 経路B では `ConnectionStrings__DefaultConnection`（全 3）と
  `RabbitMq__ConnectionString`（risk-management／market-monitor）を **values-local の extraEnv** で注入する。
- **経路B postgres の owner は `ai`**: `deploy/local/infra/postgres.yaml` は configuration_svc／risk_management_svc／
  market_monitor_svc を **owner=ai**（`CREATE USER ai`）で作成する（IADR-0066 の意図＝AST 既定 POSTGRES_USER に一致）。
  → 経路B の接続資格情報は **`ai/ai`** を注入し、postgres.yaml を無改変に保つ（compose 側は自前 init が owner=kp のため
  `kp/kp`。各スタックが内部整合を保つ）。DB・イメージ・MAPPING・trading-owner realm・trading-owner 付与済み `developer` は既存で充足。
- **RabbitMQ は guest/guest**（経路B `rabbitmq.yaml`）。compose の `amqp://guest:guest@rabbitmq:5672` と同値を注入する。
- **realm の spa-web redirect/webOrigins は compose ポート（localhost:3100）固定**。経路B の外部 origin は追記が要る。
- CI: #275（image-mapping）は MAPPING↔compose のみを見るため本 PR（MAPPING/compose build 定義に無変更）で不変。
  `check-doc-links.js`（ci.yml）が新規 doc の内部リンクを検査する。`check-commit-messages.js` が件名を検査する。

## 実装方針（To-Be）

### 1. Istio エッジ `/bff/*` ルーティング（chart templating）

`deploy/helm/microservices-platform/templates/edge.yaml` を新設し、`.Values.edge.enabled` でゲートする:

- `Gateway microservices-platform-edge`: `edge.gateway.selector`（既定 `istio: ingressgateway`）のロードバランサで
  `edge.hosts`（既定 `["*"]`）の `edge.port`（既定 80/HTTP）を受ける。
- `VirtualService microservices-platform-edge`: `edge.hosts` 宛の `/bff`(exact)＋`/bff/`(prefix) を
  `edge.bff.service:edge.bff.port`（既定 `bff-service:8080`）へルーティング（rewrite 無し）。
- 任意: `edge.oidc.enabled=true`（既定 off）で `/realms/`・`/resources/` を `edge.oidc.host:port`（既定 `keycloak:8080`）へ
  通し、同一エッジ host でブラウザ OIDC の issuer を統一できる（下記 3 の機構）。

本番 values（`values.yaml`）は `edge.enabled: true`（mesh 前提・`mesh.enabled` と同方針）。
経路B（`values-local.yaml`）は Istio 未導入のため `edge.enabled: false`（別経路。手順は README）。

### 2. 経路B で 3 サービスを有効化（values-local）

`deploy/local/values-local.yaml` の `services` に以下を追加（本番 values は不変・fail-safe 既定を維持）:

- `configuration.enabled: true` ＋ `ConnectionStrings__DefaultConnection`（Host=postgres;Database=configuration_svc;Username=ai;Password=ai）
- `risk-management.enabled: true` ＋ 同上（risk_management_svc）＋ `RabbitMq__ConnectionString: amqp://guest:guest@rabbitmq:5672`
- `market-monitor.enabled: true` ＋ 同上（market_monitor_svc）＋ `RabbitMq__ConnectionString`（同上）

### 3. ブラウザ OIDC の issuer/hostname 統一（原則＋機構＋手順）

**原則**: ブラウザが受け取る token の `iss` と、サービス側の検証基準（`Auth__Authority`）が **同一 URL** で
なければならない。現状 issuer は in-cluster 正準名 `http://keycloak:8080` に固定されている（サービス間 JWT 用）。

- **手順A（推奨・既存 manifest/realm のまま成立）**: ブラウザに **同じ in-cluster 名を解決させる**。
  hosts に `127.0.0.1 keycloak` を足し、`kubectl -n platform-infra port-forward svc/keycloak 8080:8080` する。
  すると browser も cluster も `http://keycloak:8080` を issuer として共有し、**realm も keycloak.yaml も無改変**で
  `iss` が一致する。SPA は既存 origin（`http://localhost:3100`＝compose frontend を BFF port-forward へ向ける）で
  よく、`spa-web` の redirectUris/webOrigins（localhost:3100）を**そのまま流用**できる（realm 変更不要）。
- **手順B（単一エッジ host に集約する場合）**: chart の `edge.oidc.enabled=true` で SPA/`/bff`/`/realms` を同一エッジ
  host へ集約する（本 PR の機構）。この場合のみ運用者が (i) その edge host を `spa-web` の redirectUris/webOrigins へ
  **手動追記**し、(ii) `global.auth.authority` を同 host へ上書きし、(iii) in-cluster から同 host を解決させる
  （CoreDNS or backend の metadata/issuer 分離）。(iii) は稼働環境依存のため live/後続。
- **手順化（live）**: 実ブラウザログイン疎通は稼働 k3d 依存のため、`deploy/local/README.md` の「既知の制約」を
  **手順**（上記 A/B・確認コマンド）へ置き換える。realm/keycloak.yaml は手順A のため**無改変**（機構は chart 側）。

## 受け入れ基準（in-repo で検証）

> 実施記録: 全項目を 2026-07-19 に `helm`（Rancher Desktop 同梱）・Node で実行確認済み（下記 `[x]`）。

- [x] `helm template`（既定）で `edge.enabled: true` の本番既定が Gateway/VirtualService を描画し、`/bff` を
      `bff-service:8080` へ rewrite 無しでルーティングする。`edge.oidc.enabled=false` 既定では OIDC route を出さない。
- [x] `helm template -f values-local.yaml` で edge が描画されず（`edge.enabled: false`）、3 サービスの Deployment/Service が
      描画され、各 env に正しい `ConnectionStrings`（ai/ai・正 DB 名）と RabbitMq（risk/market のみ）が載る。
- [x] `helm template` 既定（3 サービス disabled）で 3 サービスの Deployment/Service が **描画されない**（fail-safe 維持）。
- [x] `edge.oidc.enabled=true` 指定時のみ OIDC route（`/realms`・`/resources` → keycloak）が描画される。
- [x] 既存 Node 検査（`scripts.test.js`＝53 pass・`check-doc-links.js`＝破損 0・`check-image-mapping.js`＝drift 0）が緑（realm/compose/MAPPING 無変更）。`helm lint`（両 values）緑。

## live（稼働 k3d 依存・本 PR 対象外＝分離）

以下は稼働クラスタ必須のため #284 にコメントし、必要分は優先度ラベル付き follow-up として起票する:

- 3 サービスの Pod 実起動・`/health/ready` 緑（実イメージビルド＋DB 疎通）。
- `trading-owner` 実ログイン（`developer`）→ `/settings` 閲覧/変更/履歴反映、非 owner 存在秘匿(404)＋403。
- `access_token` の `realm_access.roles` に `trading-owner` が載ることの確認。
- エッジ経由の `/bff/assumptions`・`/bff/risk-controls/*`・`/bff/monitor/*` 実到達（Istio ingressgateway 導入 or Traefik）。
- AST Playwright E2E を統合スタックに対して実行。

## 影響範囲・非対象

- 非対象: 本番 values の 3 サービス既定（disabled 維持）／compose／MAPPING／postgres.yaml（owner 不変）／
  realm・keycloak.yaml（手順A のため無変更）／SPA の k8s 配信（chart に frontend Deployment は無い＝別課題）／CoreDNS 改変。

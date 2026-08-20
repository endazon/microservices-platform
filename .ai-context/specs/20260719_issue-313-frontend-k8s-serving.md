---
title: SPA(frontend) を k8s chart で配信する（統合スタックの /settings エッジ疎通の前提・Issue #313）
type: spec
status: done
related_ids:
  - FR-14
  - NFR
  - IADR-0017
  - IADR-0056
  - IADR-0068
  - IADR-0076
  - IADR-0078
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-14 構成変更で完結する疎結合ユニット / NFR エッジ集約)
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md (エッジ/メッシュ)
related_specs:
  - "../adr/IADR-0078_frontend-k8s-serving.md"
  - "../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md"
  - "../adr/IADR-0068_image-mapping-drift-check.md"
  - "../adr/IADR-0056_repo-unit-structure-platform-knowledge.md"
  - "../../deploy/local/README.md"
---

# 仕様書: SPA(frontend) を k8s chart で配信する（Issue #313）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR・MSP): FR-14（構成変更で完結する疎結合ユニット・合成点。SPA が SC-01/02/03 を含む合成成果物を配信する入口）
- 非機能要件(NFR): エッジ集約（外部入口を一本化・[IADR-0017](../adr/IADR-0017_internal-service-auth-network-isolation.md)）／サービスメッシュ（ADR-0005）
- 実装判断: [IADR-0078](../adr/IADR-0078_frontend-k8s-serving.md)（本 PR: frontend を専用 template＋トップレベル values ブロックで chart 配信し、エッジに SPA catch-all ルートを追加）／[IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md)（エッジ /bff/* ルーティングの先行決定・本 PR はその上に SPA ルートを重ねる）／[IADR-0068](../adr/IADR-0068_image-mapping-drift-check.md)（#275 image-mapping ドリフト検査・frontend の COMPOSE_ONLY 除外解消）
- Issue: #313（本 issue・priority:should・#284 派生）／親トラッカ #284（live 疎通・PR #312/IADR-0076）

## 背景・課題

#284（PR #312・[IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md)）でエッジ `/bff/*` の Istio ルーティングを chart に templating したが、**SPA(frontend) を配信する Deployment/Service が chart に無い**（compose の `frontend`(nginx) 相当が k8s 側に無い）。このため #284 の受け入れ「`/settings` 表示 → `/bff/assumptions` GET がゲートウェイ経由で到達」のうち **`/settings` の実表示**が統合スタック（本番像・経路B）で成立しない。

現状:
- 本番像 chart: `services.*` は .NET サービス＋インフラのみ。frontend は無い（compose 専用＝`check-image-mapping.js` の `COMPOSE_ONLY=['frontend']` で明示除外）。
- 経路B: `k8s-local-up.sh` は BFF を port-forward する運用で、SPA は compose の frontend を流用していた。

## 受け入れ基準（本 PR＝リポ内で静的に検証完結する範囲）

1. **frontend Deployment＋Service が chart に存在する**。`helm template`（本番像／経路B）で `frontend` Deployment（nginx・containerPort 8080・実行時 config.js 用 env）と ClusterIP Service（8080）が描画される。
2. **実行時 config 注入が配線される**。`BFF_UPSTREAM`（in-cluster bff-service）・`BFF_BASE_URL=/bff`・`OIDC_AUTHORITY`・`OIDC_CLIENT_ID` が env として渡り、compose の frontend と同契約（nginx が `/bff` をプロキシ・`config.js` を envsubst で生成）。
3. **エッジ SPA ルートが追加される**。`edge.enabled` かつ `frontend.enabled` のとき、`edge.yaml` の VirtualService が `/bff`・`/realms`・`/resources` を**先に**、**最後に catch-all `/` → frontend-service** をルーティングする（Istio 先勝ちで BFF と非衝突）。SPA history fallback は frontend pod の nginx `try_files … /index.html` が担う。
4. **default-deny 下でエッジ→frontend が到達可能**。`networkpolicy.yaml` に `allow-edge-ingress-to-bff` と同型の `allow-edge-ingress-to-frontend` を **edge.enabled かつ frontend.enabled のときのみ**追加（無いと SPA ルートが本番で L3/L4 無到達）。
5. **#275 ドリフト検査が緑**。`frontend` を `COMPOSE_ONLY` から外し MAPPING へ追加。`node scripts/check-image-mapping.js`（実ファイル突合）・`--self-test`・`node scripts/scripts.test.js` が全て緑。
6. **後方互換／fail-safe**。`frontend.enabled=false` で Deployment/Service・エッジ SPA ルート・netpol 穴が一切描画されない。既存の本番像 values は SPA を enabled にするが、他サービス・エッジ /bff の描画は不変。
7. `helm lint` が通り、`helm template` が本番像・経路B の双方でエラー無く描画される。

## live 依存（本 PR の外・別手順で分離）

- 実ブラウザでの `/settings`（SC-01/02/03）実表示・OIDC 実ログイン疎通は稼働 k3d 依存。#284 手順A（hosts＋port-forward）に整合する形で `deploy/local/README.md` に SPA 到達手順を追記し、本 PR は静的検証（`helm template`/`lint`/drift）で完結させる。PR は `Refs #313` とし live 疎通の完了は親 #284 で追う。

## 実装方針

- **専用 template（`templates/frontend.yaml`）**を採用する。理由: 汎用 `deployment.yaml`（`range .Values.services`）は .NET サービス前提で `/health/live`・`/health/ready` プローブと `Otlp__Endpoint`/`Auth__Authority` env を注入する。nginx SPA はこれらを持たず（ヘルスは静的配信）、`BFF_UPSTREAM` プロキシ env・`config.js` 注入という別形状のため、`wikijs.yaml`/`minio.yaml` と同じ「非 .NET＝専用 template＋トップレベル values ブロック」に倣う（[IADR-0078](../adr/IADR-0078_frontend-k8s-serving.md)）。
- **values**: トップレベル `frontend:`（本番既定 enabled・イメージ・レプリカ・`bffUpstream`/`bffBaseUrl`/`oidc`・resources）。`values-local.yaml` は末尾に frontend ブロックのみ追記（経路B enabled・ブラウザ到達 OIDC=localhost・#24 の既存行と非重複）。
- **edge**: `edge.frontend`（service/port の既定）を追加し、SPA catch-all ルートを VS の末尾へ。
- **drift**: `COMPOSE_ONLY=[]` へ（frontend を k8s 化）＋ MAPPING に `microservices-platform/frontend`。除外機構の自己試験は `composeOnly` を明示引数化して機構の網羅を維持。

## テスト

- `node scripts/check-image-mapping.js`（実ファイル）／`--self-test`／`node scripts/scripts.test.js`＝ドリフト 0・全緑。
- `helm template msp deploy/helm/microservices-platform`（本番像）／`-f deploy/local/values-local.yaml`（経路B）／`helm lint` が緑。
- `frontend.enabled=false` の `--set` で Deployment/Service・SPA ルート・netpol 穴が消えることを確認（後方互換）。

## 変更ファイル

- 追加: `deploy/helm/microservices-platform/templates/frontend.yaml`、`docs/adr/IADR-0078_frontend-k8s-serving.md`、本仕様書。
- 変更: `deploy/helm/microservices-platform/values.yaml`（`frontend:`／`edge.frontend`）、`deploy/helm/microservices-platform/templates/edge.yaml`（SPA ルート）、`deploy/helm/microservices-platform/templates/networkpolicy.yaml`（frontend 穴）、`deploy/local/values-local.yaml`（frontend ブロック末尾追加）、`deploy/local/README.md`（SPA 到達手順）、`scripts/check-image-mapping.js`（COMPOSE_ONLY／自己試験）、`scripts/k8s-local-images.sh`（MAPPING）、`scripts/scripts.test.js`（自己試験）。

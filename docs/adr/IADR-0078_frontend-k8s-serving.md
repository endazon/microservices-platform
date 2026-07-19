---
title: IADR-0078 SPA(frontend) は専用 Helm template＋トップレベル `frontend:` values ブロックで chart 配信し、エッジ VirtualService に SPA catch-all ルート（/bff 等の後・先勝ち）と default-deny 穴を追加する。#275 ドリフト検査は COMPOSE_ONLY 除外を解消して MAPPING へ載せる
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - NFR
  - IADR-0017
  - IADR-0056
  - IADR-0068
  - IADR-0076
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# IADR-0078: SPA(frontend) の k8s chart 配信とエッジ SPA ルーティング

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）／ endazon（マージ判断）

## 起点・関連

- 関連する計画書 ID（MSP・機械追跡）: **FR-14**（構成変更で完結する疎結合ユニット・合成点。SPA は SC-01/02/03 を含む合成成果物の配信入口）／**NFR**（エッジ集約・[[IADR-0017]]）
- 関連 ADR: [[IADR-0076]]（エッジ /bff/* ルーティング・本 IADR はその VS に SPA ルートを重ねる）／[[IADR-0017]]（外部入口の集約）／[[IADR-0056]]（frontend の unit 構成・platform=アプリホスト）／[[IADR-0068]]（#275 image-mapping ドリフト検査）
- Issue: MSP #313（本 issue・priority:should）／親トラッカ #284（PR #312）
- 作業仕様書: [`docs/specs/20260719_issue-313-frontend-k8s-serving.md`](../specs/20260719_issue-313-frontend-k8s-serving.md)

## 背景・課題

#284（[[IADR-0076]]）でエッジ `/bff/*` を chart に templating したが、**SPA(frontend) を配信する Deployment/Service が chart に無い**（compose の `frontend`(nginx) 相当が k8s 側に欠落）。このため統合スタックで `/settings`（SC-01/02/03）を実表示できず、#284 の受け入れ「`/settings` 表示 → `/bff/*` 到達」が成立しない。frontend は `check-image-mapping.js` の `COMPOSE_ONLY=['frontend']` で「compose 専用・k8s 非デプロイ」として明示除外されていた（同ファイルが将来 k8s 化時の解消手順を明記）。

## 決定

### 決定1: frontend は専用 template＋トップレベル `frontend:` values ブロックで配信する（汎用 `services:` ループに載せない）

汎用 `deployment.yaml`（`range $name,$svc := .Values.services`）は **.NET サービス前提**で、`/health/live`・`/health/ready` の HTTP プローブと `Otlp__Endpoint`/`Auth__Authority` env を無条件に注入する。nginx SPA はこれらの HTTP ヘルスエンドポイントを持たず（静的配信＝`/` が index.html を返すだけ）、必要な env は `BFF_UPSTREAM`（`/bff` プロキシ上流）・`BFF_BASE_URL`・`OIDC_*` と別体系で、起動時に `config.js` を envsubst 生成する。よって既存の `wikijs.yaml`/`minio.yaml`（いずれも非 .NET・専用 template＋トップレベル `wikijs:`/`minio:` ブロック）の**確立パターンに倣い**、`templates/frontend.yaml`＋トップレベル `frontend:` を新設する。

- 帰結: 汎用 `services:` ループ・`scaling.services`（HPA/PDB）・`service.yaml` を一切変更しない（他サービスへの副作用ゼロ）。frontend を `.Values.services` に置くと二重描画（名前衝突）か汎用 template の分岐汚染が必要になるため回避する。
- Issue 本文の「`services.frontend`」表現は「frontend サービス項目を values に追加する」の意で、実体は上記の非 .NET パターンに従いトップレベル `frontend:` とする（本 IADR で明文化）。
- **Service 名/Pod ラベルの単一情報源**: frontend の Service 名・Pod ラベル `app` は `edge.frontend.service`（既定 `frontend-service`）を単一情報源として描画する。エッジ VS の転送先 host（決定3）と NetworkPolicy の podSelector（決定4）も同じ値を参照するため、knob 変更時に netpol/ルートが実体と食い違って default-deny 下でサイレント無到達になるドリフトを防ぐ（既存の `edge.bff.service` は汎用 deployment の `{{ $name }}-service` と手動同期でこの保証が無い＝frontend はより堅牢な形に揃える）。
- **外部ツール導線（opsLinks）は既定で未配線**: `config.js` の `GRAFANA_URL`/`JAEGER_URL`/`KIALI_URL`/`WIKI_BASE_URL`（SC-10 の外部ツール導線）は `frontend.extraEnv` で供給できるが**既定は空**とする。compose は dev の Grafana を直挿しするが、k8s は可観測性/外部 UI を経路B の opt-in オーバーレイ（[[IADR-0077]]・ADR-0006）に委ねる方針のため、既定で導線を出さない（`40-render-config.sh` が未設定を空文字にフォールバック）。配線が要る環境は `extraEnv` で URL を供給する。

### 決定2: ヘルスプローブは静的配信の実体に合わせる

nginx は `location /` の `try_files … /index.html` で `/` を 200 で返す。liveness は `/`（nginx 稼働）、readiness は `/config.js`（`location = /config.js` は `try_files /config.js =404`＝実行時 config 生成の完了を確認）とする。config 生成に失敗すれば readiness が 404 で落ちる fail-safe。

### 決定3: エッジ SPA ルートは VS 末尾の catch-all（`/bff`・`/realms`・`/resources` の後）

Istio VirtualService の HTTP ルートは**先勝ち**。既存の `/bff`（と任意の OIDC `/realms`・`/resources`）ルートを先に評価し、**最後に `prefix: /` → frontend-service** を置く。これで API・OIDC パスは従来どおり、それ以外（`/`・`/settings`・`/assets/*`・`/config.js`）は frontend へ流れる。SPA のクライアントルーティング（history fallback）は**エッジではなく frontend pod の nginx**（`try_files … /index.html`）が担うため、エッジは単に非 `/bff` を frontend へ委譲すればよい。SPA ルートは `frontend.enabled` でガードし、無効時は描画しない（後方互換）。

### 決定4: default-deny NetworkPolicy にエッジ→frontend の穴を開ける（bff と同型）

本番像は `networkPolicy.enabled=true`（default-deny ingress）。エッジ（Istio ingressgateway・通常 `istio-system`）→ frontend-service は既存 `allow-edge-ingress-to-bff` と同様に L3/L4 で塞がれるため、`allow-edge-ingress-to-frontend` を **`edge.enabled` かつ `frontend.enabled` のときのみ**追加する。これが無いと決定3で追加した SPA ルートが本番で無到達になる（＝壊れた経路を出さないための必須の随伴変更）。多層防御は維持し、必要最小の穴のみ開ける。

### 決定5: #275 ドリフト検査の COMPOSE_ONLY 除外を解消し MAPPING に載せる

`check-image-mapping.js` の `COMPOSE_ONLY` から `frontend` を外し（同ファイルが「将来 k8s に載せる場合はここから外し MAPPING＋Helm values＋deployment を追加する」と明記）、`k8s-local-images.sh` の MAPPING に `"microservices-platform/frontend|src/platform/frontend/Dockerfile"`（2 フィールド＝context ルート・args 無し）を追加する。compose の frontend（context `..`＝正規化で `.`）と一致しドリフト 0。除外機構の自己試験（`--self-test`／`scripts.test.js`）は production 既定（空になった `COMPOSE_ONLY`）に依存せず機構自体を検証するよう、`composeOnly` を明示引数化して合成 fixture で網羅を維持する。

## 影響・代替案

- **代替（不採用）**: frontend を `.Values.services` に載せて汎用 template を利用。→ 汎用 template に非 .NET 分岐を足す汚染か二重描画が必要で、他サービスへ影響が波及する。専用 template（決定1）の方が影響局所・既存パターン整合。
- **代替（不採用・edge 側で history fallback）**: エッジで rewrite/error-page を張って SPA fallback を実装。→ 責務が二重化し nginx との契約がずれる。nginx に一任（決定3）。
- **後方互換**: `frontend.enabled=false` で Deployment/Service・SPA ルート・netpol 穴が全て消える。経路B は `edge.enabled=false`（Istio 未導入）のため SPA は port-forward で到達（README 手順）。

## live 依存（本 PR の外）

実ブラウザでの `/settings` 実表示・OIDC 実ログインは稼働 k3d 依存。#284 手順A（hosts＋port-forward）に整合する SPA 到達手順を `deploy/local/README.md` に追記し、live 完了は親 #284 で追う。

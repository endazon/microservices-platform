---
title: 経路B で「Wiki 閲覧」(SC-04) が開けるよう Wiki.js 公開 URL をフロント実行時 config へ配線する（Issue #344）
type: spec
status: done
related_ids:
  - SC-04
  - UC-07
  - FR-13
  - ADR-0011
  - IADR-0020
  - IADR-0066
  - IADR-0076
  - IADR-0078
  - IADR-0091
author: claude
created: 2026-07-20
updated: 2026-07-25
related_specs:
  - "../../docs/screens/SC-04_wiki-access.md"
  - "../adr/IADR-0078_frontend-k8s-serving.md"
  - "../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../adr/IADR-0091_local-edge-aggregation-traefik.md"
  - "./20260725_issue-344_wiki-base-url-edge-alignment.md"
  - "../../deploy/local/README.md"
  - "../../deploy/local/values-local.yaml"
---

# 仕様書: 経路B で「Wiki 閲覧」(SC-04) の Wiki.js 公開 URL をフロント config へ配線（Issue #344）

## 起点となる計画書（トレーサビリティ）

- 画面(SC): SC-04 Wiki 閲覧（社内 Wiki＝Wiki.js への遷移導線）。
- ユースケース(UC): UC-07（Wiki 閲覧）。
- 機能要求(FR): FR-13（Wiki 連携）。
- 関連 ADR: ADR-0011 / [IADR-0020](../adr/IADR-0020_wiki-js-deployment-abac-gateway.md)（Wiki.js の実体・ABAC ゲートウェイ）。実行時 config・k8s 配信の先例は
  [IADR-0078](../adr/IADR-0078_frontend-k8s-serving.md)（frontend の `extraEnv` 経由で config.js 変数を注入）。経路B の割り切りは [IADR-0066](../adr/IADR-0066_local-k8s-dev-environment.md)、
  ブラウザ OIDC issuer 統一（手順A）は [IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md)。
- Issue: #344（live 検証で発見・frontend/Wiki・priority:should）。

## 背景と問題

live 検証で、経路B ローカルの SPA「Wiki 閲覧」画面（SC-04）が **「Wiki の接続先が未設定です。管理者に
連絡してください。」** と表示され、社内 Wiki（Wiki.js）を開けない。s2s の
`Services__WikiService=http://wiki-service:8080` は設定済みだが、これは **BFF 側の同期/API 経路**であり、
本画面（ブラウザから Wiki.js を直接開く導線）とは無関係。

### Wiki URL の取得経路（実コードで特定）

```
WikiAccessPage.tsx        appConfig().wikiBaseUrl（値あり→リンク / 空→「未設定」注意書き）
  → runtimeConfig.ts      loadAppConfig(): window.__APP_CONFIG__.wikiBaseUrl ?? import.meta.env.VITE_WIKI_BASE_URL
  → config.js             40-render-config.sh が envsubst で生成
  → config.js.template    wikiBaseUrl: "${WIKI_BASE_URL}"
```

- 設定キー: env **`WIKI_BASE_URL`** → config.js の `wikiBaseUrl`。
- 「未設定」判定: `runtimeConfig.ts` の `orUndef()` が **空文字/未定義 → undefined** に落とすため、
  `WikiAccessPage` が注意書き（リンク非表示）を出す。
- BFF 経由ではなく **ブラウザからの直リンク**（`<a href={wikiBaseUrl} target="_blank">`）。

### 根本原因（経路B）

`templates/frontend.yaml` は BFF/OIDC の env のみを **直接注入**し、`GRAFANA_URL`/`WIKI_BASE_URL` 等は
**`frontend.extraEnv`（name/value リスト）経由**でしか渡らない。`values-local.yaml` の frontend ブロックは
base の `extraEnv: []`（空）を継承 → `WIKI_BASE_URL` が `40-render-config.sh` で空文字にフォールバック →
`wikiBaseUrl` 未設定 → 「未設定です」。

## 対応方針

frontend の実行時 config・values の frontend ブロック・README に**閉じた純 config 配線**。frontend コード・
nginx テンプレ・config.js.template・realm.json・BFF の `Services__*`・datasource には触れない。

1. **`deploy/local/values-local.yaml`（経路B）**: frontend ブロックに `extraEnv` を追加し
   `WIKI_BASE_URL: http://localhost:3300` を供給する。到達は他コンポーネント（keycloak/bff/frontend）と同じ
   **`wiki-js` の port-forward**（`svc/wiki-js 3300:3000`）に整合させる。経路B は `edge.enabled=false`
   （Istio 未導入）のため、ブラウザ到達は直 port-forward が既定手順（[IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md) / README）。
2. **`deploy/local/README.md`**: 「Wiki 閲覧の到達手順」節を追記
   （`kubectl -n microservices-platform port-forward svc/wiki-js 3300:3000` → `http://localhost:3300`）。
   Wiki.js→Keycloak の SSO issuer 整合は **手順A** に従い、realm の Wiki.js redirectUris 追記・実ブラウザ
   ログイン疎通は **live（分離）**である旨を明記。
3. **本番 `deploy/helm/microservices-platform/values.yaml`**: `frontend.extraEnv: []` 既定を**維持**
   （変更しない）。本番は実 Wiki URL を per-env の extraEnv で供給する **opt-in・後方互換**（既存コメントが
   既に案内済み）。

### スコープ外（並行作業・本 PR では触れない）

- BFF datasource upstream 修正（`Services__DataSourceService`＝BFF values の Services ブロック）。
- spa-web dev redirect（`realm.json`）。

## 実装ADR

純 config 配線（既設計キー [IADR-0078](../adr/IADR-0078_frontend-k8s-serving.md) ＋ 手順A port-forward [IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md)）であり、新規の設計判断は
無いため **IADR は起票しない**（最新は IADR-0088）。

## 受け入れ基準

> **更新（2026-07-25・IADR-0091 edge 集約に伴う edge URL 整合）**: 下記 `WIKI_BASE_URL` の値は当初
> `http://localhost:3300`（port-forward 前提）だったが、その後の [IADR-0091](../adr/IADR-0091_local-edge-aggregation-traefik.md) edge 集約で Wiki.js が
> `wiki.localhost:50000` に公開され、edge をローカルの正規アクセスとする運用へ移行した。これに合わせ
> `WIKI_BASE_URL` を `http://wiki.localhost:50000` へ整合した（[20260725_issue-344_wiki-base-url-edge-alignment](./20260725_issue-344_wiki-base-url-edge-alignment.md)）。
> 非 edge（port-forward）で使う場合は `values-local.yaml` の本値を `http://localhost:3300` へ override する。

- [x] `values-local.yaml` の frontend に `WIKI_BASE_URL`（LOCALEDGE 正規＝`http://wiki.localhost:50000`。
      非 edge 利用時は `http://localhost:3300` へ override）が配線される。
- [x] `helm template -f deploy/local/values-local.yaml` で frontend Deployment の env に
      `WIKI_BASE_URL=http://wiki.localhost:50000` が現れる。
- [x] 本番 `values.yaml` は無改変（`frontend.extraEnv: []`・後方互換）。
- [x] `deploy/local/README.md` に Wiki 閲覧の到達手順（LOCALEDGE 正規＋port-forward override）と SSO＝live 分離が追記される。
- [x] `check-image-mapping.js`（#275 ドリフト）/ `check-doc-links.js` が緑（イメージ・docs リンク不変）。
- [x] frontend 単体テスト（`WikiAccessPage.test.tsx` / `runtimeConfig.test.ts`）は挙動不変で緑。
- [ ] 実ブラウザでの Wiki 閲覧（Wiki.js→Keycloak SSO 実ログイン）疎通は **live**（realm redirectUris・
      稼働 k3d 依存・本 issue の live 分）。

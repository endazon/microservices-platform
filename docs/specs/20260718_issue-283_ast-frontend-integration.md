---
title: AST フロント/設定画面を MSP SPA へ組み込む（Issue #283 / AST #106 T2 の MSP 側実装）
type: spec
status: draft
related_ids:
  - FR-14
  - IADR-0056
  - IADR-0057
  - IADR-0063
  - IADR-0068
  - IADR-0070
author: claude
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md (コンポーザブル)"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md (合成点)"
related_specs:
  - "../adr/IADR-0070_ast-frontend-integration.md"
  - "../../src/ai-stock-trading/docs/integration/20260718_msp-frontend-integration-requirements.md"
  - "../../src/ai-stock-trading/docs/adr/IADR-0080_frontend-settings-screen.md"
---

# 仕様書: AST フロント/設定画面を MSP SPA へ組み込む（Issue #283）

> 本仕様書は実装着手前に作成する。上流は **AST（ai-stock-trading）側の統合要件仕様**
> （`src/ai-stock-trading/docs/integration/20260718_msp-frontend-integration-requirements.md`）。
> 本書は「MSP 側で何をどう実装するか」を確定する作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-14**（構成変更で完結する疎結合ユニット。合成点 1 行での組み込み）
- 実装判断: [[IADR-0056]]（ユニット構成 platform/knowledge）／[[IADR-0057]]（一方向依存）／
  [[IADR-0063]]（BFF 合成点・例外3）／[[IADR-0068]]（image-mapping ドリフト検査）／
  **[[IADR-0070]]（本統合の設計判断・AST 共有 Dockerfile を context/args 対応で載せる）**
- Issue: MSP #283（本 issue）／ AST endodazon/ai-stock-trading#106（T2）
- 上流: AST PR #185（AST フロント第1スライス・設定画面 AST/SC-01・[[IADR-0080]]）

## 目的・背景

AST の設定画面（AST/FR-17 全体前提条件の閲覧/変更・AST/SC-01。他プロジェクト ID はプロジェクト修飾。
`.claude/rules/traceability.md` 参照）を、MSP の**単一 SPA** へ
**ビルド時ソース合成**（`@knowledge` と同形の `@ai-stock-trading`）で載せ、BFF `/bff/assumptions`
経由で AST の ConfigurationService へ到達させる。AST フロントは独立デプロイ物ではなく、
platform SPA の feature として載る（二重定義を避ける・IADR-0056）。

## スコープ（リポ内検証完結を優先／live 依存は分離）

### 本 PR（リポ内で緑）

1. **2b submodule pin 更新**: `src/ai-stock-trading` を AST develop 最新（#185 込み・`frontend/` を含む）へ。
   受け入れ: npm workspaces `*/frontend` が `ai-stock-trading/frontend` を認識。
2. **2a SPA 合成**:
   - `src/platform/frontend/vite.config.ts`: `resolve.alias` に `@ai-stock-trading` を追加。
   - `src/platform/frontend/tsconfig.app.json`: `paths` に `@ai-stock-trading/*` を追加。
   - `src/platform/frontend/src/features/index.ts`: AST features を 1 行合成。
   - `src/vitest.config.ts`: `@ai-stock-trading` alias＋`include`／coverage に `ai-stock-trading/frontend/src/**` を追加。
   - `src/eslint.config.js`: 依存方向ルール（platform は合成点以外で `@ai-stock-trading` 参照禁止／
     AST frontend は `@features` 参照禁止）。
   - 受け入れ: `npm run typecheck` / `npm run lint` / `npm run build` / 横断 `vitest`（AST feature テスト収集）が緑。
3. **2d ConfigurationService デプロイ登録**（IADR-0070）:
   - `deploy/docker-compose.yml`: `configuration-service`（context=`../src/ai-stock-trading`、
     dockerfile=`backend/Dockerfile`、args=`SERVICE_PROJECT`/`SERVICE_DLL`）。
   - `scripts/k8s-local-images.sh`: MAPPING を **context/args 対応**へ拡張し AST エントリを追記。
   - `scripts/check-image-mapping.js`: compose の `context`/`args` と MAPPING の context/args も突合する
     ように拡張（自己試験追加）。**#275 ドリフト検査を緑に保つ**。
   - helm `values.yaml`＋既存 Service/Deployment テンプレ: `configuration` サービスを追加。
   - 受け入れ: `node scripts/check-image-mapping.js` / `--self-test` が緑、`helm template` が
     ConfigurationService の Deployment/Service を描画、`docker compose config` が妥当。
4. **2c BFF `/bff/assumptions` 配線**（pass-through プロキシ・IADR-0070）:
   - `Platform.Bff/Foundation/Endpoints/AssumptionsBffEndpoints.cs` を新設し合成点へ 1 行追加。
   - GET `/bff/assumptions`・GET `/bff/assumptions/history`・PUT `/bff/assumptions` を
     ConfigurationService `/assumptions*` へ委譲。認可は後段（OwnerOrService/OwnerOnly）が強制。
   - 受け入れ（`Platform.Bff.Tests`）: 認証済み GET/PUT が後段 200 を透過、非 owner PUT の後段 403 を透過、
     匿名が 401、後段不達が 502、トークン伝播。
5. **2e Keycloak（宣言分）**: `deploy/keycloak/microservices-platform-realm.json` に realm ロール
   `trading-owner` を追加。

### 後続 issue（live・稼働 k3d 依存・別途起票）

- ConfigurationService の実イメージビルド＋起動・health（実行時 DB/バス）。
- 2f: Istio 経由の `/settings` → `/bff/assumptions` 実疎通。
- 2e(稼働): `trading-owner` の owner ユーザー付与＋OIDC ログイン end-to-end・非 owner 存在秘匿。
- 1d: AST Playwright E2E（統合スタック）。

## 二重定義回避・依存規則

- AST frontend は `@features`（platform 合成点）を import しない（AST 側 ESLint で既に禁止／
  MSP 側でも `ai-stock-trading/frontend/src/**` に対し禁止を張る）。
- `@foundation` は MSP の実装が単一の真実源。AST 側スタックは合成時に使われない（IADR-0080）。
- Platform.Bff は AST 契約（可変ユニット）を参照しない（platform→可変ユニット禁止・IADR-0057）。
  よって `/bff/assumptions` は **DTO 非依存の pass-through** とする（IADR-0070）。

## 受け入れ基準（DoD 抜粋）

- 上記「本 PR」1〜5 の各受け入れが緑。
- 既存 CI（image-mapping.yml=#275／ci.yml／frontend*.yml／pr-title.yml）を壊さない。
- 安全既定（fail-safe）: 後段不達は 502、匿名は 401、非 owner は後段 403/404 を透過。
- live 依存は後続 issue へ優先度ラベル付きで分離。

---
title: AST SC-02/SC-03 の /bff/risk-controls/* プロキシ登録と submodule 再pin（Issue #287 / AST #106 T2 の残り配線）
type: spec
status: draft
related_ids:
  - FR-14
  - IADR-0056
  - IADR-0057
  - IADR-0063
  - IADR-0068
  - IADR-0070
  - IADR-0071
author: claude
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md (コンポーザブル)"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md (合成点)"
related_specs:
  - "../adr/IADR-0071_ast-risk-controls-bff-integration.md"
  - "../adr/IADR-0070_ast-frontend-integration.md"
  - "20260718_issue-283_ast-frontend-integration.md"
---

# 仕様書: AST リスク設定/統制状態参照の BFF 配線（Issue #287）

> 本仕様書は実装着手前に作成する。先行は #283（PR #285・SC-01・`/bff/assumptions`）。本書は SC-02/SC-03 の
> `/bff/risk-controls/*` プロキシ登録と submodule 再pin という**リポ内配線の残り**を確定する作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-14**（構成変更で完結する疎結合ユニット。合成点 1 行での組み込み）
- 実装判断: [[IADR-0056]]／[[IADR-0057]]（一方向依存）／[[IADR-0063]]（BFF 合成点・例外3）／
  [[IADR-0068]]（image-mapping ドリフト検査）／[[IADR-0070]]（先行・SC-01）／
  **[[IADR-0071]]（本スライスの設計判断）**
- Issue: MSP #287（本 issue）／先行 MSP #283（PR #285）／ AST endazon/ai-stock-trading#106（T2）
- 上流: AST PR #186（SC-02/SC-03 追加）／#192（SC-02 ガード変更 UI・AST IADR-0086）／#194（3画面 E2E・AST IADR-0087）／#195

## 目的・背景

#285 で AST SC-01（設定・`/bff/assumptions`）を MSP SPA へ載せた。その後 AST develop に SC-02（リスク設定）/
SC-03（統制状態参照）が追加された。これらは RiskManagementService の OwnerOnly 契約 `/risk-controls/*` を BFF
経由で消費する。本 issue は SC-02/03 を MSP SPA で実到達させる**合成点の残り**（submodule 再pin＋`/bff/risk-controls/*`
の BFF 登録＋RiskManagementService の deploy 登録）を完了させる。合成の形・pass-through 方針は [[IADR-0070]] を踏襲する。

## スコープ（リポ内検証完結を優先／live 依存は分離）

### 本 PR（リポ内で緑）

1. **submodule 再pin**: `src/ai-stock-trading` を AST develop 最新 `c367f60`（#186/#192/#194/#195 込・
   `frontend/src/features/sc02-risk-settings`・`sc03-controls` を含む）へ更新する。合成点（`@ai-stock-trading`）は
   #285 で配線済みのため、再pinで features/index.ts の 2 画面が自動的に載る。
   - 受け入れ: `npm run typecheck` / `npm run lint` / `npm run build` / 横断 `vitest`（AST SC-02/03 feature を
     実 foundation 上で収集）が緑。

2. **BFF `/bff/risk-controls/*` pass-through**（IADR-0071 決定2）: SC-02/03 が実消費する 6 経路のみ。
   - `src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/RiskControlsBffEndpoints.cs`（新規）:
     GET `/settings`・GET `/settings/history`・PUT `/settings/limits`・PUT `/settings/guard`・GET `/status`・
     GET `/stage-gate` を後段 `/risk-controls/*` へ pass-through（`AssumptionsBffEndpoints` と同型）。
   - `Program.cs`: 名前付きクライアント `RiskManagementService`（既定 `http://risk-management-service:8080`）を追加。
     readiness の UriHealthCheck には含めない（可変ユニット未導入で BFF 可用性を左右させない・fail-safe）。
   - `Composition/BffEndpointComposition.cs`: `MapRiskControlsBffEndpoints()` を合成点へ 1 行追加。
   - 認証必須（匿名 401）＋`Authorization` 伝播。認可は後段 OwnerOnly へ委譲し 400/403/404/409 透過、後段不達 502。
   - 受け入れ: `Platform.Bff.Tests` の新規 risk-controls 群（GET/PUT 中継・401・403/400/409 透過・502・トークン伝播・
     PUT 本文転送）が緑。合成点回帰テスト（モジュール数）を更新。

3. **RiskManagementService デプロイ登録**（IADR-0071 決定3・#285 の ConfigurationService と同形）:
   - `deploy/docker-compose.yml`: `risk-management-service`（context/args・専用 DB `risk_management_svc`・
     `*rabbit-env`・`depends_on` postgres+rabbitmq healthy）を追加。
   - `deploy/create-multiple-dbs.sh`: `risk_management_svc` DB を作成（k3d 用 `local/infra/postgres.yaml` は既存）。
   - `deploy/helm/microservices-platform/values.yaml`: `services.risk`（既定 `enabled: false`・fail-safe）を追加。
   - `scripts/k8s-local-images.sh`: MAPPING に `risk-management-service`（context/args）を追加。
   - 受け入れ: `check-image-mapping.js --self-test`＋実突合 0、`helm template`（enabled 時のみ描画）、`helm lint`、
     `docker compose config` が妥当。#275 ドリフト検査・images.yml を緑に保つ。

### 非スコープ（live=#284 へ分離）

- Istio エッジの `/bff/*` 実ルーティング・OIDC 実ログイン疎通・RiskManagementService の稼働導入
  （helm `enabled: true`＋DB/RabbitMQ プロビジョニング＋Secret＋実 E2E）は稼働 k3d 依存のため #284（live）が担当。

## 受け入れ基準（Definition of Done）

- [ ] submodule が `c367f60` へ再pinされ、frontend `typecheck`/`lint`/`build`/横断 `vitest` が緑。
- [ ] `/bff/risk-controls/*` の 6 経路が pass-through 登録され、BFF 単体テスト（正常中継・匿名 401・非owner403/
      検証400/競合409 透過・後段不達 502・トークン/PUT本文 伝播）が緑。
- [ ] RiskManagementService が compose/helm/MAPPING に登録され、drift 検査 0・`helm template`/`helm lint`/
      `docker compose config` が妥当。
- [ ] `dotnet format --verify-no-changes`（platform/knowledge 両 slnx）が 0。
- [ ] 設計判断が [[IADR-0071]] に記録され、live 依存が #284 へ分離されている。

## 例外・fail-safe

- 匿名アクセスは 401（グループ `RequireAuthorization()`）。owner 判定は後段 OwnerOnly（非 owner は 403 透過）。
- 後段（RiskManagementService）不達・タイムアウトは 502 へ縮退（利用者キャンセルは除外）。
- helm `services.risk` は既定 disabled（実行時依存未充足でのクラッシュループを防ぐ）。
- BFF は AST 契約 DTO に結合しない（素の JSON を透過・IADR-0057 遵守）。

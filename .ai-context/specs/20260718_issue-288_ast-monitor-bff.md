---
title: AST SC-02 監視銘柄（watchlist）の /bff/monitor/* プロキシ登録（Issue #288 / AST#196 の残り配線）
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
  - IADR-0072
author: claude
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md (コンポーザブル)
  - planning:projects/microservices-platform/06_technical/10_composability-design.md (合成点)
related_specs:
  - "../adr/IADR-0072_ast-monitor-bff-integration.md"
  - "../adr/IADR-0071_ast-risk-controls-bff-integration.md"
  - "20260718_issue-287_ast-risk-controls-bff.md"
---

# 仕様書: AST 監視銘柄（watchlist）の BFF 配線（Issue #288）

> 本仕様書は実装着手前に作成する。先行は #287（PR #289・SC-02/03・`/bff/risk-controls/*`）。本書は SC-02 の
> 監視銘柄（watchlist）変更 UI が消費する `/bff/monitor/*` プロキシ登録という**リポ内配線の残り**を確定する作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-14**（構成変更で完結する疎結合ユニット。合成点 1 行での組み込み）
- 実装判断: [IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md)／[IADR-0057](../adr/IADR-0057_unit-dependency-machine-check.md)（一方向依存）／[IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md)（BFF 合成点・例外3）／
  [IADR-0068](../adr/IADR-0068_image-mapping-drift-check.md)（image-mapping ドリフト検査）／[IADR-0070](../adr/IADR-0070_ast-frontend-integration.md)（SC-01）／[IADR-0071](../adr/IADR-0071_ast-risk-controls-bff-integration.md)（SC-02/03 risk-controls）／
  **[IADR-0072](../adr/IADR-0072_ast-monitor-bff-integration.md)（本スライスの設計判断）**
- Issue: MSP #288（本 issue）／先行 MSP #287（PR #289）／ AST endazon/ai-stock-trading#196（PR #197）
- 上流: AST#195（watchlist 設定ストア API・IADR-0088）／#197（SC-02 監視銘柄変更 UI・IADR-0090）

## 目的・背景

#289 で AST SC-02/03（risk-controls）を MSP SPA へ載せた。その後 AST develop の SC-02（`settings/risk`）に
**監視銘柄（watchlist）変更 UI** が追加された（AST#196/AST#197・IADR-0090）。これはリスク設定とは別サービスの
MarketMonitorService の OwnerOnly 契約 `/monitor/watchlist` を BFF 経由で消費する。本 issue は SC-02 の監視銘柄
セクションを実 BFF へ到達させる**合成点の残り**（`/bff/monitor/*` の BFF 登録＋MarketMonitorService の deploy
登録）を完了させる。合成の形・pass-through 方針は [IADR-0070](../adr/IADR-0070_ast-frontend-integration.md)/[IADR-0071](../adr/IADR-0071_ast-risk-controls-bff-integration.md) を踏襲する。

## スコープ（リポ内検証完結を優先／live 依存は分離）

### 本 PR（リポ内で緑）

1. **submodule 再pin: 不要**。develop は既に AST `36570d6`（#195/#197 込・`frontend/src/features/monitor`・
   `sc02-risk-settings/WatchlistForm.tsx` を含む）へ pin 済み。合成点（`@ai-stock-trading`）は #285 で配線済みの
   ため、watchlist セクションは既に載っている。
   - 受け入れ: 横断 `vitest`（AST monitor/sc02 feature を実 foundation 上で収集）が緑。

2. **BFF `/bff/monitor/*` pass-through**（IADR-0072 決定2/3）: SC-02 watchlist UI が実消費する 4 経路のみ。
   - `src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/MonitorBffEndpoints.cs`（新規）:
     GET `/watchlist`・POST `/watchlist`・DELETE `/watchlist`・GET `/watchlist/history` を後段 `/monitor/*` へ
     pass-through（`RiskControlsBffEndpoints` と同型。ただし **DELETE も本文転送**）。
   - `Program.cs`: 名前付きクライアント `MarketMonitorService`（既定 `http://market-monitor-service:8080`）を追加。
     readiness の UriHealthCheck には含めない（可変ユニット未導入で BFF 可用性を左右させない・fail-safe）。
   - `Composition/BffEndpointComposition.cs`: `MapMonitorBffEndpoints()` を合成点へ 1 行追加。
   - 認証必須（匿名 401）＋`Authorization` 伝播。認可は後段 OwnerOnly へ委譲し 400/403/404/409 透過、後段不達 502。
   - 受け入れ: `Platform.Bff.Tests` の新規 monitor 群（GET/POST/DELETE 中継・DELETE 本文転送・401・403/400/409
     透過・502・トークン伝播）が緑。合成点回帰テスト（モジュール数 11→12・ルートグループに `/bff/monitor` 追加）を更新。

3. **MarketMonitorService デプロイ登録**（IADR-0072 決定4・#289 の RiskManagementService と同形＝DB+RabbitMQ）:
   - `deploy/docker-compose.yml`: `market-monitor-service`（context/args・専用 DB `market_monitor_svc`・
     `*rabbit-env`・`depends_on` postgres+rabbitmq healthy）を追加。
   - `deploy/create-multiple-dbs.sh`: `market_monitor_svc` DB を作成（k3d 用 `local/infra/postgres.yaml` は既存）。
   - `deploy/helm/microservices-platform/values.yaml`: `services.market-monitor`（既定 `enabled: false`・fail-safe）を追加。
     キー名は `market-monitor`（テンプレートが `{name}-service` を付す）で Service 名を `market-monitor-service` に一致させる。
   - `scripts/k8s-local-images.sh`: MAPPING に `market-monitor-service`（context/args）を追加。
   - `NetworkIsolationTests`: `market-monitor-service` を内部 API（expose のみ）回帰ガードへ追加。
   - 受け入れ: `check-image-mapping.js --self-test`＋実突合 0、`helm template`（enabled 時のみ描画）、`helm lint`、
     `docker compose config` が妥当。#275 ドリフト検査・images.yml を緑に保つ。

### 非スコープ（live=#284 へ分離）

- Istio エッジの `/bff/*` 実ルーティング・OIDC 実ログイン疎通・MarketMonitorService の稼働導入
  （helm `enabled: true`＋DB/RabbitMQ プロビジョニング＋Secret＋実 E2E）は稼働 k3d 依存のため #284（live）が担当。

## 受け入れ基準（Definition of Done）

- [x] `/bff/monitor/*` の 4 経路が pass-through 登録され、BFF 単体テスト（正常中継・DELETE 本文転送・匿名 401・
      非owner403/検証400/競合409 透過・後段不達 502・トークン/POST・DELETE本文 伝播）が緑。
      → `Platform.Bff.Tests` 139 passed（新規 monitor 10）／CI `build-and-test` 緑。
- [x] MarketMonitorService が compose/helm/MAPPING/NetworkIsolationTests に登録され、drift 検査 0・`helm template`/
      `helm lint`/`docker compose config` が妥当。→ `NetworkIsolationTests` 4 passed／drift 0／CI `image-mapping`・
      `build (market-monitor-service)` 緑。
- [~] 横断 `vitest`（AST monitor/sc02 feature）: 本 PR は frontend 無改変（submodule 再pinなし・frontend ファイル 0）の
      ため develop から不変であり、本 PR では再実行しない（`frontend-tests.yml` の `paths` フィルタにも非該当で非トリガ）。
- [x] `dotnet format --verify-no-changes`（platform/knowledge 両 slnx）が 0。
- [x] 設計判断が [IADR-0072](../adr/IADR-0072_ast-monitor-bff-integration.md) に記録され、live 依存が #284 へ分離されている。

## 例外・fail-safe

- 匿名アクセスは 401（グループ `RequireAuthorization()`）。owner 判定は後段 OwnerOnly（非 owner は 403 透過）。
- 後段（MarketMonitorService）不達・タイムアウトは 502 へ縮退（利用者キャンセルは除外）。
- DELETE は本文（銘柄・理由）を後段へ転送する（欠落すると後段が 400・削除不能）。
- helm `services.market-monitor` は既定 disabled（実行時依存未充足でのクラッシュループを防ぐ）。
- BFF は AST 契約 DTO に結合しない（素の JSON を透過・IADR-0057 遵守）。

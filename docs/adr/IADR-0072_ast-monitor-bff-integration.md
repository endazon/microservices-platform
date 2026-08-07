---
title: IADR-0072 AST SC-02 監視銘柄（watchlist）の /bff/monitor/* は IADR-0070/0071 と同形の DTO 非依存 pass-through とし、MarketMonitorService は DB+RabbitMQ を伴う deploy 面へ既定 disabled で登録する
type: impl-adr
status: Accepted
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
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
---

# IADR-0072: AST 監視銘柄（SC-02 watchlist）の MSP 組み込み

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: claude（実装）／ endazon（マージ判断）

## 起点・関連

- 関連する計画書 ID: **FR-14**（構成変更で完結する疎結合ユニット・合成点 1 行組み込み）
- 関連 ADR: [[IADR-0056]]（ユニット構成）／[[IADR-0057]]（一方向依存）／[[IADR-0063]]（BFF 合成点・例外3）／
  [[IADR-0068]]（image-mapping ドリフト検査）／[[IADR-0070]]（AST フロント第1スライス SC-01）／
  [[IADR-0071]]（先行・SC-02/03 risk-controls）／
  AST IADR-0088（watchlist 設定ストア API）／AST IADR-0090（フロント watchlist UI）
- Issue: MSP #288 ／ 先行 MSP #287（PR #289）／ AST endazon/ai-stock-trading#196（PR #197）
- 上流仕様: `src/ai-stock-trading/frontend/src/features/monitor/contracts.ts`（watchlist の応答契約）／
  `src/ai-stock-trading/backend/Services/MarketMonitorService/src/MarketMonitorService.Worker/Foundation/Endpoints/MonitorSettingsEndpoints.cs`

> **［2026-08-07 追記 / #570］上記「上流仕様」の 2 本目のパスと、決定 4 の `SERVICE_PROJECT` /
> `SERVICE_DLL` の値は、submodule pin `91d52c2` 以降は実在しない。** AST がサービスホストのプロジェクトを
> **`*.Worker` → `*.Api` へ一斉改名**した（技術詳細は `*.Infrastructure` へ分離。AST/IADR-0128）ためで、
> 現行のパスは `.../MarketMonitorService/src/MarketMonitorService.Api/Foundation/Endpoints/MonitorSettingsEndpoints.cs`
> である。**本文は 2026-07-18 時点の記録としてそのまま残す。**
> #564 の pin bump（`655e2ed` → `91d52c2`）でこの改名が入り、追随していなかった
> `deploy/docker-compose.yml` / `scripts/k8s-local-images.sh` を #570 で `*.Api` へ揃えた
> （`build (market-monitor-service)` の `dotnet restore` が MSB1009 で落ちていた）。
> 決定 4 のうち**登録の形（DB＋RabbitMQ・既定 disabled・専用 DB `market_monitor_svc`・expose のみ）は不変**で、
> 動いたのは build args の値だけである（[作業仕様書](../specs/20260807_issue-570_ast-project-rename.md)）。
> なお決定 4 本文の「MarketMonitorService は Worker で〜」というホストの呼称も改名前の記述である。

## 背景・課題

#289（IADR-0071）で AST の SC-02（リスク設定）/ SC-03（統制状態参照）を `/bff/risk-controls/*` 経由で MSP SPA へ
載せた。その後 AST develop で SC-02（`settings/risk`）に **監視銘柄（watchlist）変更 UI**（AST #196/PR #197・
IADR-0090）が追加された。これはリスク設定（RiskManagementService）とは**別サービス**の MarketMonitorService の
OwnerOnly 契約 `/monitor/watchlist`（AST #195・IADR-0088）を BFF 経由で消費する。`/bff/monitor/*` の BFF 登録が
未了のため、監視銘柄セクションは実 BFF へ到達しない。本 issue はその**リポ内配線**を完了させる。

論点は #285/#289 でおおむね確定済み（合成の形／deploy ツールの context/args 対応／pass-through／interim 同居）
だが、本スライス固有の 2 点を判断する。

1. **どの `/monitor/*` 経路を BFF に登録するか**。MarketMonitorService は `/monitor` 配下に `settings`(GET/PUT)・
   `watchlist`(GET/POST/DELETE)・`watchlist/history`(GET) を持つが、SC-02 の watchlist UI が実際に叩くのは一部である。
2. **`DELETE /monitor/watchlist` の本文転送**。#289 の risk-controls は本文を持つのが PUT のみだったが、
   MarketMonitorService の `DELETE /monitor/watchlist` は `[FromBody] WatchlistChangeRequest`（銘柄・理由）を取り、
   フロントも DELETE に JSON body を送る。pass-through は DELETE でも本文を後段へ転送する必要がある。

## 決定

### 1. フロント合成・BFF pass-through・interim 同居は IADR-0070/0071 を厳密踏襲する

`@ai-stock-trading` 合成点は #285 で配線済みで、submodule は develop が既に AST `36570d6`（#195/#197 込）へ
pin 済みのため、**再pinは不要**（`sc02-risk-settings` の watchlist セクションは既に載っている）。
`/bff/monitor/*` は IADR-0070 決定3・IADR-0071 決定1 と同型の **DTO 非依存 pass-through**（応答本文・ステータス・
Content-Type を透過、`Authorization` 伝播、後段不達 502）とする。`MonitorBffEndpoints` は interim で
`Platform.Bff/Foundation/Endpoints/` 同居（恒久像＝AST 側 unit-owned Bff への移行は follow-up #286）。

> **更新（2026-07-19・#286 / [[IADR-0073]]）**: 本 interim は #286 で解消済み。`MonitorBffEndpoints` は
> AST 側 unit-owned Bff プロジェクト `AiStockTrading.Bff.Endpoints`（AST PR）へ挙動不変で移設され、合成点は
> 例外3 で参照する。

### 2. BFF は SC-02 watchlist UI が実消費する 4 経路のみを登録する（未使用経路は登録しない）

AST フロント（`sc02-risk-settings/WatchlistForm.tsx`）が `apiFetch` で叩くのは以下 4 経路のみ（後段は
いずれも **OwnerOnly**。`MonitorSettingsEndpoints.cs` 参照）。

| BFF | メソッド | 後段 `/monitor/*` | 本文 | 消費画面 |
| --- | --- | --- | --- | --- |
| `/bff/monitor/watchlist` | GET | `/monitor/watchlist` | なし | SC-02 |
| `/bff/monitor/watchlist` | POST | `/monitor/watchlist` | あり（symbol/market/reason） | SC-02 |
| `/bff/monitor/watchlist` | DELETE | `/monitor/watchlist` | **あり**（symbol/market/reason） | SC-02 |
| `/bff/monitor/watchlist/history` | GET | `/monitor/watchlist/history` | なし | SC-02 |

`/monitor/settings`(GET/PUT) は watchlist UI が**叩かない**ため登録しない（起こり得ない経路への防御的追加を
避ける＝CLAUDE.md 禁止事項・IADR-0071 決定2 と同方針）。将来 SC が監視設定 UI を消費する時にその画面の PR で
追加する。グループは `RequireAuthorization()`（匿名 401）とし、owner 判定は後段 OwnerOnly へ委ねる（非 owner は
後段 403 を透過）。

### 3. pass-through は本文を持つ全メソッド（POST/PUT/PATCH に加え **DELETE**）でリクエスト本文を転送する

IADR-0071 の `ProxyAsync` は本文転送条件を PUT/POST/PATCH に限っていた。MarketMonitorService の
`DELETE /monitor/watchlist` は本文（`[FromBody] WatchlistChangeRequest`）で削除対象銘柄と理由を受け取り、
不在削除・空理由はサーバ 400（AST #191）。よって本 proxy は **DELETE でも本文を後段へ転送**する（`HttpMethods.IsDelete`
を本文転送条件へ追加）。GET は従来通り本文なし。ステータス・本文・Content-Type の透過方式は #289 と同一（バッファ方式）。

### 4. MarketMonitorService は DB+RabbitMQ を伴う形で deploy 面へ既定 disabled で登録する

MarketMonitorService は Worker で `MarketMonitorDbContext`（Npgsql・専有 DB `market_monitor_svc`）と
MassTransit（RabbitMQ・`TradeDecisionMade` 購読／監視イベント発行）を初期化してから `/monitor/*` を提供する。
#289 の RiskManagementService と同型（DB＋RabbitMQ）で登録する。

- `deploy/docker-compose.yml`: `market-monitor-service` を `context: ../src/ai-stock-trading` /
  `dockerfile: backend/Dockerfile` / `args: {SERVICE_PROJECT, SERVICE_DLL}` で追加。`*rabbit-env` を併用し、
  `depends_on` に `postgres`（healthy）と `rabbitmq`（healthy）を含める。接続文字列は専用 DB `market_monitor_svc`
  （`Host=postgres;...;Username=kp;Password=kp`）。IADR-0017: 内部 API のため host 公開しない（expose のみ）。
- `deploy/create-multiple-dbs.sh`: compose 用に `market_monitor_svc` DB を作成（未作成だと DB 不在で
  クラッシュループ）。k3d 用 `deploy/local/infra/postgres.yaml` には既に `market_monitor_svc` が存在するため変更不要。
- `deploy/helm/microservices-platform/values.yaml`: `services.market-monitor`（RiskManagement と同型）を
  **既定 `enabled: false`（fail-safe）** で追加。キー名は `market-monitor`（テンプレートが `{name}-service` を
  付す）とし、Service 名 `market-monitor-service` を compose のサービス名・BFF 既定
  （`http://market-monitor-service:8080`）と一致させる。稼働導入（`enabled: true`＋DB/RabbitMQ プロビジョニング＋
  Secret）は稼働クラスタ前提のため live #284 へ分離する。
- `scripts/k8s-local-images.sh`: MAPPING に `market-monitor-service` エントリ（context/args）を追加。
  compose の build ターゲットと 1:1 で対応させ、#275 ドリフト検査（`check-image-mapping.js`）を緑に保つ。
  `images.yml` は compose config から build 対象を自動導出するため追加変更不要。

**根拠**: MarketMonitorService の HTTP サーフェス（`/monitor/*`）は Worker が DbContext と MassTransit を
初期化してから提供される。compose（ローカル dev）で実際に到達させるには DB と RabbitMQ が要る。helm は #289 と
同じく「宣言・既定 disabled」に留め、稼働時依存の充足は live 側の責務とする（本 PR はリポ内検証に閉じる）。

## 影響・トレードオフ

- **利点**: SPA は再pin不要（既に載っている）、BFF は AST 契約に結合せず（IADR-0057）4 経路を薄く中継する。
  deploy 登録は #289 の RiskManagement 登録（context/args・DB+RabbitMQ）をそのまま再利用する。
- **代償**: pass-through は BFF での型検証を行わない（契約検証は後段 MarketMonitorService と AST の
  Playwright/単体テストが担う）。compose に RabbitMQ 依存のサービスが 1 つ増える（既存 rabbitmq インフラを共有）。
- **却下案**: (a) `/monitor/*` を総なめでプロキシ登録 → 未使用経路（settings）への防御的実装（禁止事項）。
  (b) MarketMonitorService を helm 既定 enabled で登録 → 稼働時依存（DB/RabbitMQ/Secret）未充足で
  クラッシュループ・fail-safe 逸脱で却下。(c) DELETE 本文を転送しない → 後段が銘柄・理由を受け取れず 400 に
  ならず削除が機能しないため却下。(d) Platform.Bff に AST 契約 DTO を参照させ型付きプロキシ化 →
  IADR-0057 逸脱で却下（IADR-0070 決定3 を踏襲）。

## 計画環流

なし（IADR-0070 の計画環流＝「MSP 登録は context/args 対応が前提」で吸収済み。本 IADR はその踏襲）。

## 検証

- フロント: submodule 再pin不要のため差分なし。横断 `vitest`（AST monitor/sc02 feature を実 foundation 上で収集）が緑。
- deploy: `node scripts/check-image-mapping.js --self-test` と実突合が緑、`helm template`
  （`services.market-monitor.enabled=true` で Deployment/Service 描画・既定は非描画）、`helm lint`、
  `docker compose config` が妥当。
- BFF: `dotnet test Platform.Bff.Tests`（downstream モック）が緑（GET/POST/DELETE 中継・DELETE 本文転送・401・
  403/400/409 透過・502・トークン伝播）。
- live（実イメージビルド・OIDC ログイン・Istio 疎通・E2E・MarketMonitorService 稼働導入）は #284 へ分離。

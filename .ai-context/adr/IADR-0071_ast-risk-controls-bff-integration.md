---
title: IADR-0071 AST/SC-02・AST/SC-03 の /bff/risk-controls/* は IADR-0070 と同形の DTO 非依存 pass-through とし、RiskManagementService は DB+RabbitMQ を伴う deploy 面へ既定 disabled で登録する
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - IADR-0056
  - IADR-0057
  - IADR-0063
  - IADR-0068
  - IADR-0070
author: claude
created: 2026-07-18
updated: 2026-08-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
  - planning:projects/microservices-platform/06_technical/10_composability-design.md
---

# IADR-0071: AST リスク設定/統制状態参照（AST/SC-02/SC-03）の MSP 組み込み

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: claude（実装）／ endazon（マージ判断）

## 起点・関連

- 関連する計画書 ID: **FR-14**（構成変更で完結する疎結合ユニット・合成点 1 行組み込み）
- 関連 ADR: [IADR-0056](./IADR-0056_repo-unit-structure-platform-knowledge.md)（ユニット構成）／[IADR-0057](./IADR-0057_unit-dependency-machine-check.md)（一方向依存）／[IADR-0063](./IADR-0063_bff-unit-endpoint-composition.md)（BFF 合成点・例外3）／
  [IADR-0068](./IADR-0068_image-mapping-drift-check.md)（image-mapping ドリフト検査）／[IADR-0070](./IADR-0070_ast-frontend-integration.md)（AST フロント第1スライス AST/SC-01・本 IADR の先行）／
  AST/IADR-0084（AST/SC-02/03 の `/risk-controls/*` 契約消費）／AST/IADR-0086（AST/SC-02 ガード変更 UI）／
  AST/IADR-0087（BFF 契約の Playwright E2E 追認）
- Issue: MSP #287 ／ 先行 MSP #283（PR #285）／ AST endazon/ai-stock-trading#106
- 上流仕様: `src/ai-stock-trading/frontend/src/features/risk/contracts.ts`（AST/SC-02/03 が共有する応答契約）

## 背景・課題

#285（IADR-0070）で AST フロントの AST/SC-01（設定・`/bff/assumptions`）を MSP SPA へ載せた。その後 AST develop に
**AST/SC-02（リスク設定）/ AST/SC-03（統制状態参照）** の 2 画面が追加された（AST#186/AST#192/AST#194/AST#195）。これらは
RiskManagementService の OwnerOnly 契約 `/risk-controls/*` を BFF 経由で消費する。#285 時点の submodule ピンは
#185 で、`/bff/risk-controls/*` の BFF 登録も未了だった。本 issue はその**リポ内配線の残り**を完了させる。

論点は #285 でおおむね確定済み（合成の形＝決定1／deploy ツールの context/args 対応＝決定2／pass-through＝決定3／
interim 同居＝決定4）だが、本スライス固有の 2 点を判断する。

1. **どの `/risk-controls/*` 経路を BFF に登録するか**。RiskManagementService は `/risk-controls` 配下に
   kill-switch・pause・sizing-context・open-positions など多数の経路を持つが、AST/SC-02/03 が実際に叩くのは一部である。
2. **RiskManagementService のデプロイ登録形**。#285 の ConfigurationService は **DB 専用**（RabbitMQ 不使用）
   だったが、RiskManagementService は **DB（`risk_management_svc`）に加え RabbitMQ（MassTransit）** を使う。

## 決定

### 1. フロント合成・BFF pass-through・interim 同居は IADR-0070 を厳密踏襲する

`@ai-stock-trading` 合成点（vite/vitest/tsconfig/ESLint）は #285 で配線済みのため、**submodule 再ピンのみ**で
`sc02-risk-settings`/`sc03-controls` features が自動的に載る（features/index.ts の 1 行合成は AST 側で完結）。
`/bff/risk-controls/*` は決定3（IADR-0070）と同型の **DTO 非依存 pass-through**（応答本文・ステータス・
Content-Type を透過、`Authorization` 伝播、後段不達 502）とする。`RiskControlsBffEndpoints` は interim で
`Platform.Bff/Foundation/Endpoints/` 同居（決定4・恒久像＝AST 側 unit-owned Bff への移行は follow-up #286）。

> **更新（2026-07-19・#286 / [IADR-0073](./IADR-0073_ast-unit-owned-bff-migration.md)）**: 本 interim は #286 で解消済み。`RiskControlsBffEndpoints` は
> AST 側 unit-owned Bff プロジェクト `AiStockTrading.Bff.Endpoints`（AST PR）へ挙動不変で移設され、合成点は
> 例外3 で参照する。

### 2. BFF は AST/SC-02/03 が実消費する 6 経路のみを登録する（未使用経路は登録しない）

AST フロント（`sc02-risk-settings`/`sc03-controls`）が `apiFetch` で叩くのは以下 6 経路のみ（バックエンドは
いずれも **OwnerOnly**。`RiskControlEndpoints.cs` 参照）。

| BFF | メソッド | 後段 `/risk-controls/*` | 消費画面 |
| --- | --- | --- | --- |
| `/bff/risk-controls/settings` | GET | `/settings` | AST/SC-02 |
| `/bff/risk-controls/settings/history` | GET | `/settings/history` | AST/SC-02 |
| `/bff/risk-controls/settings/limits` | PUT | `/settings/limits` | AST/SC-02 |
| `/bff/risk-controls/settings/guard` | PUT | `/settings/guard` | AST/SC-02（AST/IADR-0086） |
| `/bff/risk-controls/status` | GET | `/status` | AST/SC-03 |
| `/bff/risk-controls/stage-gate` | GET | `/stage-gate` | AST/SC-03 |

kill-switch・pause・sizing-context・open-positions・settings/stage・stage-gate/history 等は**フロントが叩かない**ため
登録しない（起こり得ない経路への防御的追加を避ける＝CLAUDE.md 禁止事項）。将来 SC が増えて新経路を消費する時に
その画面の PR で追加する（合成点は 1 経路 = 1 行）。グループは `RequireAuthorization()`（匿名 401）とし、owner
判定は後段 OwnerOnly へ委ねる（非 owner は後段 403 を透過）。

### 3. RiskManagementService は DB+RabbitMQ を伴う形で deploy 面へ既定 disabled で登録する

#285 の ConfigurationService と同形（単一 Dockerfile＋build args＋ユニットルート context）で登録しつつ、
RiskManagementService 固有の実行時依存を compose に反映する。

- `deploy/docker-compose.yml`: `risk-management-service` を `context: ../src/ai-stock-trading` /
  `dockerfile: backend/Dockerfile` / `args: {SERVICE_PROJECT, SERVICE_DLL}` で追加。ConfigurationService と異なり
  **`*rabbit-env` アンカーを併用**し、`depends_on` に `postgres`（healthy）と `rabbitmq`（healthy）を含める。
  接続文字列は専用 DB `risk_management_svc`（`Host=postgres;...;Username=kp;Password=kp`）。
- `deploy/create-multiple-dbs.sh`: compose 用に `risk_management_svc` DB を作成（未作成だと DB 不在で
  クラッシュループ）。k3d 用 `deploy/local/infra/postgres.yaml` には既に `risk_management_svc` が存在するため変更不要。
- `deploy/helm/microservices-platform/values.yaml`: `services.risk-management`（ConfigurationService と同型）を
  **既定 `enabled: false`（fail-safe）** で追加。キー名は `risk-management`（テンプレートが `{name}-service` を
  付す）とし、Service 名 `risk-management-service` を compose のサービス名・BFF 既定
  （`http://risk-management-service:8080`）と一致させる。稼働導入（`enabled: true`＋DB/RabbitMQ プロビジョニング＋
  Secret）は稼働クラスタ前提のため live #284 へ分離する。
- `scripts/k8s-local-images.sh`: MAPPING に `risk-management-service` エントリ（context/args）を追加。
  compose の build ターゲットと 1:1 で対応させ、#275 ドリフト検査（`check-image-mapping.js`）を緑に保つ。
  `images.yml` は compose config から build 対象を自動導出するため追加変更不要。

**根拠**: RiskManagementService の HTTP サーフェス（`/risk-controls/*`）は Worker が DbContext と MassTransit を
初期化してから提供される。compose（ローカル dev）で実際に到達させるには DB と RabbitMQ が要る。helm は #285 と
同じく「宣言・既定 disabled」に留め、稼働時依存の充足は live 側の責務とする（本 PR はリポ内検証に閉じる）。

> **［2026-08-07 追記 / #570］決定 3 の `SERVICE_PROJECT` / `SERVICE_DLL` の値が失効し、追随した。**
> AST がサービスホストのプロジェクトを **`*.Worker` → `*.Api`** へ一斉改名した（技術詳細＝DbContext・
> consumer・常駐ジョブは `*.Infrastructure` へ分離。AST/IADR-0128）。#564 の pin bump
> （`655e2ed` → `91d52c2`）で旧パスが実在しなくなり `build (risk-management-service)` が MSB1009 で落ちたため、
> `deploy/docker-compose.yml` と `scripts/k8s-local-images.sh` を
> `.../RiskManagementService.Api/RiskManagementService.Api.csproj` ＋ `RiskManagementService.Api.dll` へ揃えた。
> **上記本文（呼称「Worker」を含む）は 2026-07-18 時点の記録としてそのまま残す。**
> 登録の形（DB＋RabbitMQ・既定 disabled・専用 DB `risk_management_svc`・helm キー `risk-management`）と
> 到達先（`http://risk-management-service:8080`）は不変で、動いたのは build args の値だけである
> （[作業仕様書](../specs/20260807_issue-570_ast-project-rename.md)）。

## 影響・トレードオフ

- **利点**: SPA は再ピンだけで AST/SC-02/03 が載り、BFF は AST 契約に結合せず（IADR-0057）6 経路を薄く中継する。
  deploy 登録は #285 の後方互換拡張（context/args）を再利用し、DB+RabbitMQ 依存だけ compose へ足す。
- **代償**: pass-through は BFF での型検証を行わない（契約検証は後段 RiskManagementService と AST#194 の
  Playwright E2E が担う）。compose に RabbitMQ 依存のサービスが 1 つ増える（既存 rabbitmq インフラを共有）。
- **却下案**: (a) `/risk-controls/*` を総なめでプロキシ登録 → 未使用経路への防御的実装（禁止事項）。
  (b) RiskManagementService を helm 既定 enabled で登録 → 稼働時依存（DB/RabbitMQ/Secret）未充足で
  クラッシュループ・fail-safe 逸脱で却下。(c) Platform.Bff に AST 契約 DTO を参照させ型付きプロキシ化 →
  IADR-0057 逸脱で却下（IADR-0070 決定3 を踏襲）。

## 計画環流

なし（IADR-0070 の計画環流＝「MSP 登録は context/args 対応が前提」で吸収済み。本 IADR はその踏襲）。

## 検証

- フロント: `npm run typecheck` / `npm run lint` / `npm run build` / 横断 `vitest`（AST/SC-02・AST/SC-03 feature を実
  foundation 上で収集）が緑。
- deploy: `node scripts/check-image-mapping.js --self-test` と実突合が緑、`helm template`
  （`services.risk-management.enabled=true` で Deployment/Service 描画・既定は非描画）、`helm lint`、
  `docker compose config` が妥当。
- BFF: `dotnet test Platform.Bff.Tests`（downstream モック）が緑。
- live（実イメージビルド・OIDC ログイン・Istio 疎通・E2E・RiskManagementService 稼働導入）は #284 へ分離。

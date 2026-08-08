---
title: IADR-0070 AST フロント/設定画面は @knowledge と同形の合成で SPA へ載せ、AST 共有 Dockerfile は deploy ツールを context/args 対応へ拡張して登録し、/bff/assumptions は DTO 非依存の pass-through とする
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - IADR-0056
  - IADR-0057
  - IADR-0063
  - IADR-0068
  - IADR-0124
author: claude
created: 2026-07-18
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
---

# IADR-0070: AST フロント/設定画面の MSP 組み込み

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: claude（実装）／ endazon（マージ判断）

## 起点・関連

- 関連する計画書 ID: **FR-14**（構成変更で完結する疎結合ユニット・合成点 1 行組み込み）
- 関連 ADR: [[IADR-0056]]（ユニット構成）／[[IADR-0057]]（一方向依存）／[[IADR-0063]]（BFF 合成点・例外3）／
  [[IADR-0068]]（image-mapping ドリフト検査）／ AST [[IADR-0080]]（AST フロント第1スライス）
- Issue: MSP #283 ／ AST endazon/ai-stock-trading#106
- 上流仕様: `src/ai-stock-trading/docs/integration/20260718_msp-frontend-integration-requirements.md`

## 背景・課題

AST の設定画面（AST/SC-01・AST/FR-17）を MSP の単一 SPA に載せ、BFF 経由で AST ConfigurationService へ
到達させる（他プロジェクト計画書の ID はプロジェクト修飾を付ける。`.claude/rules/traceability.md`
「ユニット横断・クロスリポジトリの ID 修飾」。MSP の SC-01＝検索/チャットと衝突するため）。
実装にあたり 3 つの設計上の論点があった。

1. **フロント合成の形**: 既存の可変ユニットは `@knowledge`（vite alias＋`features/index.ts` 1 行＋
   root vitest 横断＋ESLint 依存方向）で合成される。AST も同形にするか。
2. **AST バックエンドのデプロイ登録**: 統合要件仕様は
   `configuration-service|.../ConfigurationService.Worker/Dockerfile` を前提にしていたが、
   **実際の AST は per-service Dockerfile を持たず**、単一のパラメータ化 Dockerfile
   （`backend/Dockerfile`＋build args `SERVICE_PROJECT`/`SERVICE_DLL`、**build context = AST ユニットルート**。
   AST/IADR-0048 決定3）に統合済みだった。MSP の `k8s-local-images.sh` は context=MSP ルート・args なしを
   前提とし、#275 ドリフト検査（`check-image-mapping.js`）は compose の `dockerfile:` リテラルのみを
   突合するため、そのままでは AST サービスを正しくビルド／検査できない。
3. **BFF の型結合**: `/bff/assumptions` の DTO は AST 契約（可変ユニット）に属する。platform→可変ユニット
   参照は禁止（IADR-0057）のため、Platform.Bff から AST 契約を参照できない。

## 決定

### 1. フロントは `@knowledge` と厳密同形で合成する（独自機構を持ち込まない）

`@ai-stock-trading` エイリアスを vite（build/dev）・root vitest（横断テスト）・
platform `tsconfig.app.json`（型検査）へ追加し、`platform/frontend/src/features/index.ts` へ
`import { features as aiStockTradingFeatures } from '@ai-stock-trading/features'` を 1 行足して束ねる。
ESLint 依存方向ルールも `@knowledge` と同形にする（platform は合成点以外で `@ai-stock-trading` を
参照禁止／AST frontend は `@features` を参照禁止）。AST feature テストは root vitest が
**実 foundation** 上で収集・実行する（AST の `@foundation` スタブは合成時に使われない）。

> **［2026-08-04 追記］決定 1 の「厳密同形」は [[IADR-0124]]（#490）で部分改定された。**
> 計画 [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md) が
> ルーティングを TanStack Router と確定し、移行第 2 段でユニットの合成契約が
> **型付きルート factory のタプル ＋ ナビ項目**（[IADR-0124](IADR-0124_tanstack-router-unit-composition.md) 決定 1）へ
> 変わったためである。**現行値では `@knowledge` と `@ai-stock-trading` は同形ではない**——
> `@knowledge` は新契約、**`@ai-stock-trading` は旧契約（`FeatureModule { id, routes: {path, element}[], nav }`）の
> 互換ブリッジ**（同 決定 2）で束ねられる。ブリッジのルートは型付きルート木の外側にあり、
> 実行時にのみ共通シェルへ接ぎ木される（`<Link to>` の型 union には現れない）。
> **この非同形は本リポジトリから AST を変更できないこと（[[IADR-0120]]）の写像であり、意図的である。**
> AST が新契約へ移れば同形へ戻り、ブリッジは削除できる。
> 改定はこの 1 点に限り、決定 1 のうち「独自機構を持ち込まない」「エイリアスを vite / root vitest /
> `tsconfig.app.json` へ追加する」「ESLint 依存方向ルールを同形にする」「AST feature テストを
> root vitest が実 foundation 上で実行する」および決定 2・3 は本 IADR が引き続き有効である
> （したがって状態は `Accepted` のまま）。現行値は
> [IADR-0124](IADR-0124_tanstack-router-unit-composition.md) 決定 1・2 を正とする。

### 2. AST 共有 Dockerfile を「context/args 対応」で deploy 面へ載せる（ツールを後方互換拡張）

per-service Dockerfile を前提にした統合仕様どおりの MAPPING は**採らない**（実在しないパスになる）。
代わりに、AST の実態（単一 Dockerfile＋build args＋ユニットルート context）に合わせ、MSP の 3 点を
**後方互換で拡張**する。

- `deploy/docker-compose.yml`: `configuration-service` を `context: ../src/ai-stock-trading` /
  `dockerfile: backend/Dockerfile` / `args: {SERVICE_PROJECT, SERVICE_DLL}` で追加する。
- `scripts/k8s-local-images.sh`: MAPPING エントリを
  `image|context|dockerfile|arg1=val,arg2=val`（`|` 区切り 2〜4 フィールド）へ拡張する。フィールド 2〜4 が
  省略された既存エントリは **context=リポルート（`.`）・dockerfile はリポルート相対・args なし**（従来挙動）へ
  フォールバックする。ビルドは `docker build -f <context>/<dockerfile> [--build-arg ...] <context>` とする。
- `scripts/check-image-mapping.js`: compose の `context`/`args` と MAPPING の context/args も突合する
  （context は compose が `deploy/` 相対、MAPPING がリポルート相対のため、compose 側の先頭 `../` を 1 段
  剥がして正規化して比較する）。従来の 2 フィールド・context=ルートのエントリは挙動不変。自己試験
  （`--self-test`）に AST 型（context/args 付き）のケースを追加する。

**根拠**: #275 検査の目的は「compose と k8s ビルドが同一の Dockerfile/context/args から同一イメージを作る」
ことの保証である。context が異なれば別物になるため、検査は context/args も見なければ意味を保てない。
既存エントリはすべて context=ルートで不変のため、拡張は後方互換（フォールバック）で成立する。

> **［2026-08-07 追記 / #570］決定 2 の仕組み（単一 Dockerfile＋context/args）は有効なまま、`SERVICE_PROJECT` /
> `SERVICE_DLL` の値だけが失効した。** AST が**サービスホストのプロジェクトを `*.Worker` → `*.Api` へ一斉改名**
> （11 ホスト全部。技術詳細を `*.Infrastructure` へ分離。AST/IADR-0128）したためである。#564 が submodule pin を
> `655e2ed` → `91d52c2` へ上げた時点で旧パスが実在しなくなり、`build (configuration-service)` /
> `build (risk-management-service)` / `build (market-monitor-service)` が `dotnet restore` の
> **MSBUILD error MSB1009** で落ちた（集約ゲート `image-build` はその派生）。
> **本文および上記「背景・課題」の論点 2 が引用する `ConfigurationService.Worker` は、2026-07-18 時点の
> 記録としてそのまま残す。** 現行値は `deploy/docker-compose.yml` と `scripts/k8s-local-images.sh` を正とする
> （`.../ConfigurationService.Api/ConfigurationService.Api.csproj` ＋ `ConfigurationService.Api.dll`）。
> 決定 2 が要求する「compose と MAPPING が同一の Dockerfile/context/args を指す」不変条件は保たれており
> （`check-image-mapping.js` の `args-mismatch` 検査が両者を突合する）、**#570 で動いたのは名前だけ**である
> ——SDK（`Microsoft.NET.Sdk.Web`）・待受（`:8080`）・ヘルス（`/health/live`・`/health/ready`）・
> アセンブリ名の規則（csproj 名と同一）は改名の前後で不変であることを pin `91d52c2` で実測した
> （[作業仕様書](../specs/20260807_issue-570_ast-project-rename.md)）。

### 3. `/bff/assumptions` は DTO 非依存の pass-through プロキシとする

Platform.Bff は AST 契約を参照しない（IADR-0057）。よって GET `/bff/assumptions`・
GET `/bff/assumptions/history`・PUT `/bff/assumptions` は、後段 ConfigurationService `/assumptions*` の
**応答本文・ステータス・Content-Type をそのまま透過**する（型付けしない）。グループは
`RequireAuthorization()`（認証必須＝匿名は 401）とし、owner 判定は**後段の OwnerOrService/OwnerOnly**へ
委ねる（非 owner の PUT は後段 403、非 owner GET も後段 403、いずれも透過）。利用者トークンは
`Authorization` ヘッダをそのまま後段へ伝播する（既存 BFF プロキシと同方式）。後段不達は 502 へ縮退する
（fail-safe）。フロント側の存在秘匿（`RequireRole`→NotFound）はサーバ 401/403 の**表示側バックストップ**。
応答転送は小さな管理系ペイロードのため `AuthzBffEndpoints` と同型のバッファ方式（`ReadAsStringAsync`→
`Results.Content`）を採る（SSE 用の低レベル `Response.Body` 直書きは用いない）。

### 4. 本スライスの `/bff/assumptions` は Platform.Bff 同居（interim）とし、例外3 の unit-owned Bff 化は後続へ分離する

> **更新（2026-07-19・#286 / [[IADR-0073]]）**: 本 interim は #286 で解消済み。`AssumptionsBffEndpoints` は
> AST 側 unit-owned Bff プロジェクト `AiStockTrading.Bff.Endpoints`（AST PR）へ挙動不変で移設され、合成点は
> 例外3 で参照する。以下は当時（interim 採用時）の記録として残す。

`src/README.md`「依存規則 例外3」（IADR-0063）は、可変ユニットのドメイン固有 BFF エンドポイントを
**当該ユニットの `<unit>/backend/Bff/` プロジェクト**に置き、合成点から参照する形を規範とする（knowledge は
`Knowledge.Bff.Endpoints` として実施済み）。本 PR はこの規範に対し、`AssumptionsBffEndpoints` を
`Platform.Bff/Foundation/Endpoints/`（platform 同居）へ置いた。理由は以下。

- **AST は submodule（読み取り専用）**: knowledge は本リポ内ユニットのため 例外3 のプロジェクトを追加できるが、
  `src/ai-stock-trading` は別リポの submodule で、`AiStockTrading.Bff.Endpoints` を新設するには **AST 側の PR＋
  ピン更新**が要る（本 MSP PR からは AST へコミットできない）。これは本 IADR が却下案 (b)（submodule 越境変更）
  として退けたのと同じ制約である。
- **pass-through で薄い**: 決定3 のとおり DTO 非結合の素通しであり、Platform.Bff から AST への依存は生じない
  （`check-unit-dependencies.js` 上も違反なし）。よって platform 同居は「規範逸脱」ではなく「合成点の器を
  platform 側に置いた薄い interim」に留まる。
- **境界の明示**: 恒久像は 例外3（AST 側の `AiStockTrading.Bff.Endpoints` を合成点から 1 行参照）。本スライスは
  interim とし、AST 側プロジェクト化＋合成点参照への移行を **follow-up issue #286（AST PR 前提・priority:could）**
  へ分離する。ユニット追加のたびに Platform.Bff を直接肥大させないため、AST の BFF エンドポイントが増える前
  （現状 1 本のうち）に #286 で移行する。

## 影響・トレードオフ

- **利点**: 合成は既存機構の 1 行追加で完結。deploy ツールは後方互換のまま AST の実 Dockerfile を正しく
  ビルド／検査でき、#275 の保証を context/args まで強化する。BFF は AST 契約に結合せず疎結合を保つ。
- **代償**: `check-image-mapping.js` の突合ロジックが context/args 分だけ増える（自己試験で担保）。
  pass-through は BFF での型検証を行わない（契約検証は後段と AST 側テストが担う）。
- **却下案**: (a) 統合仕様どおり per-service Dockerfile を MAPPING に書く → 実在せずビルド不能。
  (b) MSP から AST submodule 内へ per-service Dockerfile を追加 → submodule への越境変更で不可。
  (c) Platform.Bff に AST 契約 DTO を参照させ型付きプロキシにする → IADR-0057 逸脱で却下。

## 計画環流

統合要件仕様の 2d が前提にした per-service Dockerfile パスは AST の実態（単一 Dockerfile＋args）と
乖離している。AST 側へ `/plan-feedback` で「MSP 登録は context/args 対応が前提」である旨を還流する。

## 検証

- フロント: `npm run typecheck` / `npm run lint` / `npm run build` / 横断 `vitest` が緑。
- deploy: `node scripts/check-image-mapping.js --self-test` と実突合が緑、`helm template` が
  ConfigurationService を描画、`docker compose config` が妥当。
- BFF: `dotnet test Platform.Bff.Tests`（downstream モック）が緑。
- live（実イメージビルド・OIDC ログイン・Istio 疎通・E2E）は後続 issue（#283 に列挙）へ分離。

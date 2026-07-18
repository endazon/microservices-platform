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
author: claude
created: 2026-07-18
updated: 2026-07-18
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

AST の設定画面（SC-01・FR-17）を MSP の単一 SPA に載せ、BFF 経由で AST ConfigurationService へ
到達させる。実装にあたり 3 つの設計上の論点があった。

1. **フロント合成の形**: 既存の可変ユニットは `@knowledge`（vite alias＋`features/index.ts` 1 行＋
   root vitest 横断＋ESLint 依存方向）で合成される。AST も同形にするか。
2. **AST バックエンドのデプロイ登録**: 統合要件仕様は
   `configuration-service|.../ConfigurationService.Worker/Dockerfile` を前提にしていたが、
   **実際の AST は per-service Dockerfile を持たず**、単一のパラメータ化 Dockerfile
   （`backend/Dockerfile`＋build args `SERVICE_PROJECT`/`SERVICE_DLL`、**build context = AST ユニットルート**。
   AST IADR-0048 決定3）に統合済みだった。MSP の `k8s-local-images.sh` は context=MSP ルート・args なしを
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

### 3. `/bff/assumptions` は DTO 非依存の pass-through プロキシとする

Platform.Bff は AST 契約を参照しない（IADR-0057）。よって GET `/bff/assumptions`・
GET `/bff/assumptions/history`・PUT `/bff/assumptions` は、後段 ConfigurationService `/assumptions*` の
**応答本文・ステータス・Content-Type をそのまま透過**する（型付けしない）。グループは
`RequireAuthorization()`（認証必須＝匿名は 401）とし、owner 判定は**後段の OwnerOrService/OwnerOnly**へ
委ねる（非 owner の PUT は後段 403、非 owner GET も後段 403、いずれも透過）。利用者トークンは
`Authorization` ヘッダをそのまま後段へ伝播する（既存 BFF プロキシと同方式）。後段不達は 502 へ縮退する
（fail-safe）。フロント側の存在秘匿（`RequireRole`→NotFound）はサーバ 401/403 の**表示側バックストップ**。

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

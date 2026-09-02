---
title: 作業仕様書 — AST ユニットを型付きルート契約で合成し、旧契約の互換ブリッジを撤去する（AST#414）
type: spec
status: done
related_ids:
  - ADR-0031
  - ADR-0032
  - ADR-0066
  - IADR-0120
  - IADR-0124
  - IADR-0125
  - IADR-0251
  - IADR-0273
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - ../../../project-planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - ../../../project-planning/projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
---

# 作業仕様書: AST ユニットを型付きルート契約で合成し、旧契約の互換ブリッジを撤去する

## 起点となる計画書（トレーサビリティ）

- 関連 ADR: `ADR-0031`（フロントエンド採用技術）／`ADR-0032`（BFF セッション方式）／`ADR-0066`（feature 境界）
- 関連 IADR: `IADR-0124` 決定 1・決定 2（型付きルート factory と互換ブリッジ）／`IADR-0125` 決定 9（`UnitNavGroup`）／
  `IADR-0251`・`IADR-0273` 決定 7（BFF セッション・旧形フォールバック）／`IADR-0120`（可変ユニットは本リポから変更できない）
- 起点: **AST#414**（可変ユニット側の追随）。本 PR はその**受け皿**である。
- 親トラッキング: #454（「AST 側が新契約へ移ればブリッジごと削除できる。別リポジトリの issue が要る」と記載）

## 目的・背景

`IADR-0124` 決定 2 は、旧契約（`FeatureModule { id, routes, nav }`）の互換ブリッジ `createLegacyRoutes` を
**「本リポジトリから変更できないユニット（`src/ai-stock-trading`。`IADR-0120`）のために、契約の形を変えずに残す」**
ものと位置づけ、`@deprecated` を付けていた。ブリッジで載せたルートは**型付きルート木の外側**にあり、
AST の 3 画面は `<Link to>` の静的検査にも `useSearch({ from })` の型にも現れない。

**AST 側が AST#414 で新契約（型付きルート factory ＋ ナビ項目）へ移った。** 残されていた理由が消えたので、
合成点を新契約へ差し替え、ブリッジを撤去する。

同じく `IADR-0273` 決定 7 は、`roles.ts` の JWT 復号フォールバックを
**「`ai-stock-trading` submodule のテストが旧形（`{ access_token }`）の値を流し込むため」**に残し、
**「AST 側が追随したらこのフォールバックごと削る」**と明記していた。**その条件が満たされた。**

## 対象範囲

- 対象:
  - 合成点 `src/platform/frontend/src/features/index.ts` を新契約へ差し替える
  - `src/platform/frontend/src/app/routing/router.tsx` から `appendLegacyRoutes` を撤去する
  - `src/platform/frontend/src/app/routing/featureRegistry.ts` から旧契約の型と関数
    （`LegacyFeatureRoute` / `FeatureModule` / `createLegacyRoutes` / `legacyNavItems`）を削除する
  - `IADR-0273` 決定 7 に従い `SessionUser.access_token` と `roles.ts` の JWT 復号フォールバックを削除する
  - 旧契約を前提にしたコメント・テスト名を実態へ合わせる
- 対象外:
  - 🔴 **submodule `src/ai-stock-trading` の pin 更新**（別 PR。本 PR では動かさない）
  - AST 側のパンくず宣言（`FeatureBreadcrumb`）。AST は現時点で宣言を持たない——**合成点が代わりに書かない**
    （画面の名前と親子関係はユニットしか知らない）
  - `src/package.json` の pnpm `overrides`（React / Vite / Vitest をワークスペースで固定するもの）。
    AST が同じ版を宣言したので**根拠は消えた**が、外すと lockfile が動くため別 PR とする（後述「未決事項」）

## 設計

### 1. 合成点（`features/index.ts`）

```ts
import { createAiStockTradingRoutes, aiStockTradingNavItems } from '@ai-stock-trading/features';

export const createUnitRoutes = (shell: ShellRoute) =>
  [...createKnowledgeRoutes(shell), ...createAiStockTradingRoutes(shell)] as const;

export const unitNavGroups: readonly UnitNavGroup[] = [
  { id: 'ai-stock-trading', label: msg`株式自動売買`, items: aiStockTradingNavItems },
];
```

- **AST のルートが型付きの木へ入る。** これが本 PR の実質である（ブリッジの撤去は結果にすぎない）。
- **ナビは `planNavItems` ではなく `unitNavGroups` のままである。** AST は本計画に属さないため `group` を
  宣言せず、機能名（「株式自動売買」）のグループへ束ねる（`IADR-0125` 決定 9）。**この非対称は変えない。**
- `legacyNavItems(aiStockTradingFeatures)` の呼び出しは、AST が直接ナビ項目を公開するので不要になる。

### 2. ルート木（`router.tsx`）

`appendLegacyRoutes` は「型付きの children へ型を持たないルートを後から足す」ための**唯一の型消去**だった。
AST が型付き factory を公開した以上、この関数ごと消える。`shellWithUnits` のタプルが最終形になる。

### 3. 認証（`IADR-0273` 決定 7 の後始末）

`SessionUser.access_token` は「本体の SPA では常に undefined」であり、読むのは `roles.ts` の
フォールバックだけだった。**供給側（AST のテスト・E2E ハーネス）が消えたので、両方を削除する。**
`decodeJwtPayload` も他に使い道が無いので消える。

## 受け入れ基準

- [x] 合成点が `@ai-stock-trading/features` から**ルート factory とナビ項目**を import している
- [x] `legacyUnitFeatures` / `createLegacyRoutes` / `legacyNavItems` / `FeatureModule` / `LegacyFeatureRoute` が
      リポジトリから消えている（`grep` で 0 件）
- [x] AST の 3 画面（`/settings` / `/settings/risk` / `/controls`）が**型付きルート木**に載っている
- [x] 左ナビの「株式自動売買」グループが従来どおり 3 項目を持つ（`Layout.test.tsx` が緑）
- [x] `SessionUser.access_token` と JWT 復号フォールバックが消え、`roles.ts` は `/bff/auth/me` の
      `roles` だけを読む
- [x] `pnpm -r run typecheck` / `pnpm run lint` が緑（横断 `pnpm run test` は 1310 passed / 2 failed。**失敗 2 件は `knowledge` SC-10 の既存タイムアウトで、単独実行では緑**。実測は下の §実測）

## テスト方針

| 観点                                        | 写像先                                                                    |
| ------------------------------------------- | ------------------------------------------------------------------------- |
| AST の 3 画面がルート木に載る               | `router.test.ts`（`fullPaths()` の突合。describe 名を実態へ改める）       |
| catch-all が AST の実在ルートを横取りしない | 同ファイルの `it.each`（`/settings` の行はそのまま生きる）                |
| 左ナビが機能名のグループを出す              | `Layout.test.tsx`（既存。**総称の見出しへ退行しないことを固定している**） |
| ロール判定が `roles` 配列だけを読む         | `roles.test.ts`（旧形 JWT のケースを削り、`roles` 由来のケースを残す）    |

🔴 **本 PR は submodule pin を動かさないため、CI は AST#414 のマージと pin 更新まで赤である。**
これは既知かつ意図した順序である（合成点は「AST が公開する名前」を参照するので、AST が先に入らないと解決しない）。
**赤を隠すために pin を動かすことはしない**（指示された分業のとおり、pin は別 PR が担う）。

## 計画書との差異

- 差異: なし（`ADR-0031` / `ADR-0032` / `IADR-0124` / `IADR-0273` が定めた終点へ寄せる作業である）

## 未決事項

- `src/package.json` の pnpm `overrides`（`react` / `react-dom` / `@types/react*` / `vite` / `vitest` /
  `@vitest/coverage-v8`）は、**AST が React 18 / Vite 5 / Vitest 2 を宣言していたことを理由に置かれていた**。
  AST#414 でその理由は消えたので外せるはずだが、**外すと lockfile が動き、影響は本 PR の射程を超える**。
  別 issue として起票する。

## 新規 IADR を作らない判断（2026-09-03）

**本 PR は新しい決定をしていない。** 撤去した 2 つは、いずれも**先行 IADR が自ら撤去条件を書いていた**ものである。

| 撤去したもの                              | 条件を書いた先行 IADR | 条件の文言                                                                                |
| ----------------------------------------- | --------------------- | ----------------------------------------------------------------------------------------- |
| 互換ブリッジ（`createLegacyRoutes` ほか） | `IADR-0124` 決定 2    | 「本リポジトリから変更できないユニットのために、契約の形を変えずに残す」（`@deprecated`） |
| JWT 復号フォールバック（`access_token`）  | `IADR-0273` 決定 7    | 「AST 側が追随したらこのフォールバックごと削る」                                          |

したがって本 PR は**指示の遂行**であり、記録はこの作業仕様書と PR 本文で足りる。

**採番の実務上も、新規 IADR を足さないほうが正しい。** `IADR-0340`〜`IADR-0352` は**未マージのブランチが
確保している**（実測: 全リモートブランチの `.ai-context/adr/` を走査）。`develop` 上ではこれらが欠番であり、
`scripts/check-adr-numbering.js` は**欠番を違反として落とす**。ここで `IADR-0353` を採ると
**13 件の欠番違反を新たに作る**（実測: exit 1）。本 PR は submodule pin の更新を待つため**最後にマージされる**
可能性が高く、その間ずっと赤いままになる。

🔴 **これは「検査を避けるために記録を省いた」のではない。** 記録すべき決定が無いことが先にあり、
採番の実務がそれを裏づけただけである。**新しい決定が出たら IADR を書く**（そのときは develop の
最新版号を見て採番する）。

## 実測（2026-09-03・ローカル検証）

**submodule を AST の作業ブランチ（`feat/SC-01-frontend-new-stack`）へ一時的に切り替えて実走した**
（pin は動かしていない＝コミットしていない）。

| 検査                                                                                                                                              | 結果                                                                                                                                                                                                                                                     |
| ------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `pnpm -r run typecheck`（6 プロジェクト）                                                                                                         | ✅ 全て Done（AST の 3 画面が型付きの木に載った状態で通る）                                                                                                                                                                                              |
| `pnpm run lint`                                                                                                                                   | ✅ 0 errors（`react-refresh` の既存 warning 10 件のみ）                                                                                                                                                                                                  |
| `pnpm run test`（横断 Vitest）                                                                                                                    | 1310 passed / 2 failed。**失敗 2 件は本変更と無関係**——`knowledge` の SC-10 運用ダッシュボード（`OperationsDashboardPage.test.tsx`）の 5000ms タイムアウトで、**同ファイルだけを走らせると 31 件すべて緑**（実測）。全体実行時の負荷によるフレークである |
| `scripts/check-adr-numbering.js` / `check-trace-blocks.js` / `check-doc-links.js` / `check-cross-repo-refs.js` / `check-plan-id-qualification.js` | ✅ すべて OK                                                                                                                                                                                                                                             |

🔴 **`pnpm install --frozen-lockfile` は失敗する。** `src/pnpm-lock.yaml` は **submodule 側の
`package.json` の specifier まで記録している**ため、AST の依存が変わると lockfile が古くなる（実測の差分:
`@tanstack/react-query` / `@tanstack/react-router` / `eslint-plugin-import` / `eslint-import-resolver-typescript`
の 4 件が追加、`oidc-client-ts` / `react-router-dom` の 2 件が削除）。

**したがって submodule pin を更新する PR は、同じコミットで `src/pnpm-lock.yaml` も再生成する必要がある。**
lockfile だけを先に更新すると、pin が指す実体と食い違う状態になるため**本 PR では触らない**。

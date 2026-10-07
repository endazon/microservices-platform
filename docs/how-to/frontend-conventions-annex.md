---
title: 別紙 — フロントエンド（TypeScript / React）規約の理由・設定の置き場所・手順
type: how-to
status: fixed
created: 2026-10-07
updated: 2026-10-07
author: claude
---
<!-- trace:
ids: [NFR]
adrs: [ADR-0031, ADR-0032]
iadrs: [IADR-0034, IADR-0056, IADR-0121, IADR-0124, IADR-0125, IADR-0172, IADR-0190, IADR-0203, IADR-0251, IADR-0273]
specs: [20261007_1770_reading-budget-frontend-annex]
issues: [#562, #1770]
-->

# 別紙: フロントエンド（TypeScript / React）規約の理由・設定の置き場所・手順

> **★ これは「参照時にだけ読む別紙」である。** 毎セッション読む必要は無い。
> **規約の入口は [`CLAUDE.md`](../../CLAUDE.md)「技術スタック別ルール / TypeScript / React」節**であり、
> **規範（採るスタック・禁止事項・通すべきコマンド）はそちらに在る。**
>
> **本別紙が持つのは、規範の理由・設定ファイルの所在・生成と検査の手順だけ**である
> （必読規約の減量にあたり、機械が既に止めている規範の説明から先に別紙へ出す、という方針による）。
> 規範を変えるときは入口を直し、ここは追随させる。**ここだけを直して規範を変えたことにしない。**

## スタック

- **React 19** + TypeScript 5.6 + **Vite 6**。パッケージは ESM（`"type": "module"`）。テストは **Vitest 3**。
  Node は CI と揃えて **22** を使う。
- 計画が確定したスタックへの移行は、移行の段を定めた実装 ADR が段に分割して管理する。
  **段の進捗はその実装 ADR が正本であり、必読規約へ書かない** —— 進捗は最も速く腐る。
- ルーティングは **TanStack Router**。`react-router-dom` は platform / knowledge から撤去済みで、
  再混入は ESLint が止める。

## 構成

- pnpm workspace のルートは `src/` で、**メンバは `pnpm-workspace.yaml` が正**である。
- `platform/frontend`（foundation ＋ アプリホスト）と `knowledge/frontend`（画面 features）を分離する。
- import はエイリアス `@foundation` / `@features`（合成点） / `@knowledge` を使う。

## サーバー状態

- **TanStack Query** に一元化する。`foundation/api/queryClient.ts` が唯一の生成点である。
- **グローバルストア（Redux）は持たない** —— `redux` / `@reduxjs/*` の import は ESLint が error にする。

## UI / CSS

- **Tailwind CSS v4** ＋ 共有 UI パッケージ **`@platform/ui`**（`src/packages/ui`。説明は同ディレクトリの `README.md`）。
- `@platform/ui` へ入れてよいのは**デザイントークン・`cn()`・shadcn/ui 派生プリミティブのみ**で、
  ドメイン・通信・ルーティング・認証・**表示文言**は入れない。
- 公開面は `src/index.ts` の 1 ファイルで、深い参照は ESLint が禁止する。
- **外部 CDN・Web フォント・analytics を使わない**（データの外部送信方針による）。フォントはシステムフォント、
  アイコンは lucide-react を使う。この禁止は `node scripts/check-static-egress.js --require <dist>` が
  **ビルド成果物を走査して**強制する。
- 状態表示は**色だけで意味を持たせない**（色 ＋ アイコン ＋ テキスト）。`StatusBadge` / `Alert` / `notify` が
  API の形で強制する。

## i18n

- **Lingui（ja / en）**。マクロ（`@lingui/core/macro` の `msg`）を babel で展開する設定は
  **`src/vitest.config.ts` と `src/platform/frontend/vite.config.ts` の両方**に置く —— 片方だけだと静かに割れる
  （テストだけ通る、またはビルドだけ通る）。
- カタログ（`locales/<locale>/messages.{po,ts}`）は **orval 生成物と同じくコミットし、`pnpm run i18n` の
  再生成差分を CI が検査する**。
- **未翻訳キーは `node scripts/check-i18n-catalogs.js` が止める。**
- 表示言語はブラウザ設定から決め、**切替 UI は持たない**。
- テストのロケールは **ja に固定**する。Vitest 側は `src/testing/setup.ts`、Playwright 側は
  `playwright.config.ts` の `use.locale` で固定する（Playwright の既定は en-US で、固定しないと
  CI の既定ロケール次第でアサーションが割れる）。

## コンポーネントカタログ

- **Storybook**（`src/packages/ui/.storybook/`）。ビルドは `pnpm --filter @platform/ui run build-storybook`。
- 対象は `@platform/ui` のプリミティブのみで、画面（features）は入れない。
- テレメトリとクラッシュレポートは無効化する（データの外部送信方針による）。

## BFF 境界

- バックエンドへは必ず `/bff/*` 経由で到達する。経路は次の 2 つに限る。
  - **orval 生成フック**: `pnpm run codegen` で生成する。入力は `docs/api/openapi.yaml` の `/bff/` 配下のみ。
    **生成物はコミットし、CI が再生成差分を検査する。**
  - `foundation/api` の `apiFetch` / `apiStream`。
- **手書き HTTP クライアントは禁止**である。`foundation/api` 以外での `fetch` / `XMLHttpRequest` /
  `EventSource` と、`axios` 等の import は ESLint が error にする。
- 接続先はビルドに焼き込まず、実行時 config（`platform/frontend/public/config.js`）で注入する。
- フロントから各サービスを直接叩かない。

## 認証

- **BFF セッション方式（Token Handler）**。OIDC（Authorization Code + PKCE）は BFF が実施し、
  **SPA はトークンを扱わない**。資格情報は HttpOnly セッション Cookie である。
- `oidc-client-ts` は撤去済みで、再導入しない。
- 運用手順は [`bff-session-design.md`](../authz/bff-session-design.md)。

## Lint / 型

- ESLint flat config（`src/eslint.config.js`）＋ typescript-eslint。
- `src/` で `pnpm run lint` / `pnpm run typecheck` が通ること。

## フォーマット

- `pnpm run format` で整形する（設定は `src/.prettierrc.json`）。
- **CI の `frontend.yml` の `lint` 相当ジョブが `pnpm run format:check` を強制する**
  （C# 側の `dotnet format --verify-no-changes` と同じ役割。#562）。
- **対象範囲の単一情報源は `src/.prettierignore`** であり、除外グロブを `package.json`・ワークフロー・
  必読規約・**本別紙**へ複写しない（`src/` の外は例外）。

## テスト・カバレッジ

- 単体は **Vitest**（jsdom）＋ Testing Library、E2E は **Playwright**。テストは実装と同居し `*.{test,spec}.{ts,tsx}`。
- カバレッジは `pnpm run test:coverage`（v8 provider）。`src/vitest.config.ts` の `coverage.thresholds` は
  **回帰防止のラチェット**であり、全ユニット横断で計測する。
- テストを増やしたらしきい値を引き上げる。床を割る変更は CI（`frontend-tests.yml`）で止める。

## 関連

- 入口: [`CLAUDE.md`](../../CLAUDE.md)「技術スタック別ルール / TypeScript / React」
- 減量の計画: 入口を残して中身を別紙へ出す ／ 恒久的な余白: 実例・説明を別紙と正本へ寄せる
- 同じ方式の別紙: [`commit-message-rules-annex.md`](./commit-message-rules-annex.md) ／
  [`changelog-overrides-annex.md`](./changelog-overrides-annex.md) ／
  [`cross-project-id-refs-annex.md`](./cross-project-id-refs-annex.md) ／
  [`adr-supersede-citation-annex.md`](./adr-supersede-citation-annex.md) ／
  [`plan-id-range-history-annex.md`](./plan-id-range-history-annex.md) ／
  [`population-drawing-annex.md`](./population-drawing-annex.md)

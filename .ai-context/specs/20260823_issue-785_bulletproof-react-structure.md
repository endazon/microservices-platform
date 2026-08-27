---
title: フロントエンドのディレクトリ構成を Bulletproof React（計画 §ディレクトリ構成）へ適合させる — 第 1 段: knowledge の feature 内部分割
type: spec
status: done
related_ids: [NFR, ADR-0031, ADR-0019, IADR-0056, IADR-0121, IADR-0124, IADR-0125, IADR-0134, IADR-0262]
author: claude
created: 2026-08-23
updated: 2026-08-27
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# 仕様書: フロントエンドの Bulletproof React 適合（第 1 段）

起票は #785。実装 ADR は IADR-0262。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-06〜FR-10（SPA 画面群）、FR-14（可変ユニット）
- ユースケース（UC）: UC-01〜UC-04
- 画面（SC）: SC-01〜SC-11
- 関連 ADR: ADR-0031（フロントエンド技術スタック。設計 = Bulletproof React）/ ADR-0019（ユニット構成）
- 計画書リンク: `projects/microservices-platform/06_technical/13_frontend-stack.md` §基本方針・§ディレクトリ構成

## 目的・背景

計画 `13_frontend-stack`（`status: fixed` / updated 2026-08-22）§ディレクトリ構成 は、ユニット内 SPA の構成を
ツリーで明示し、**Feature 内部の分割（`api/ components/ hooks/ routes/ stores/ types/`）まで含めて必須**と
定めている（利用者裁定 2026-08-16 → 2026-08-22 に「適合は必須。実装を計画へ合わせる」と再確定）。

実測では、`features/*/` の内部分割はリポジトリ全体で **0 件**である（後述 §母集合の走査）。本作業は
その是正の第 1 段として **knowledge ユニットの 13 feature を分割**し、あわせて knowledge ユニット直下の
ツリー項目を揃える。

## 対象範囲

- 対象
  - `src/knowledge/frontend/src/features/` の 13 feature の内部分割
  - `src/knowledge/frontend/src/` 直下のツリー項目（`app/ assets/ components/ hooks/ lib/ locales/ stores/ testing/ types/ utils/`）の設置
  - 上記に追随する `src/eslint.config.js` / `src/vitest.config.ts` / `docs/` の実パス参照
  - `@foundation` エイリアスの扱いの**決定**（第 2 段のやり直しを防ぐため、着手前に確定する）
- 対象外（第 2 段へ送る。理由は §計画書との差異）
  - `src/platform/frontend/src/foundation/` の分解（`app/` `lib/` `components/` `testing/` `locales/` へ）
  - `src/packages/ui`（計画のツリーは「ユニット内 SPA」の規範であり、ワークスペースパッケージには及ばない）
  - `src/ai-stock-trading`（別リポジトリの submodule。本リポジトリからは是正できない）

## 母集合の走査

**誤りの側から引く。** 「内部分割された feature」ではなく「区分ディレクトリが 1 つも無い feature」を数える。

### 走査 1 — 計画の 6 区分が `features/*/` 配下に実在する件数

```
$ find . -path ./node_modules -prune -o -path ./ai-stock-trading -prune -o \
    -type d -regex '.*/features/[^/]+/\(api\|components\|hooks\|routes\|stores\|types\)' -print
（出力なし）
→ 件数: 0
```

### 走査 2 — `features/*/` 直下のサブディレクトリ数（0 = 内部未分割）

```
knowledge/frontend/src/features/abac/            subdirs=0  files=6
knowledge/frontend/src/features/sc01-search/     subdirs=0  files=6
knowledge/frontend/src/features/sc02-results/    subdirs=0  files=4
knowledge/frontend/src/features/sc03-document/   subdirs=0  files=6
knowledge/frontend/src/features/sc04-wiki/       subdirs=0  files=3
knowledge/frontend/src/features/sc05-documents/  subdirs=0  files=6
knowledge/frontend/src/features/sc06-datasources/ subdirs=0 files=7
knowledge/frontend/src/features/sc07-conversions/ subdirs=0 files=7
knowledge/frontend/src/features/sc08-analysis/   subdirs=0  files=6
knowledge/frontend/src/features/sc09-admin-abac/ subdirs=0  files=10
knowledge/frontend/src/features/sc10-operations/ subdirs=0  files=6
knowledge/frontend/src/features/sc11-config/     subdirs=0  files=7
knowledge/frontend/src/features/scope-filter/    subdirs=0  files=5
```

**不適合 13 件 / 母集合 13 件（100%）。** 除外はゼロ（`platform/frontend/src/features/` は合成点の
`index.ts` だけを持ち feature を持たないため母集合に入らない。`ai-stock-trading` は submodule で
未 populate かつ本リポジトリの是正対象外）。

### 走査 3 — ユニット直下に計画のツリー項目が実在するか

```
[platform]  OK: features / MISS: app assets components hooks lib locales stores testing types utils
            実在: App.tsx  features  foundation  main.tsx  test
[knowledge] OK: features / MISS: app assets components hooks lib locales stores testing types utils
            実在: features
```

### 走査 4 — issue 本文の走査の再現（ツリー項目名に一致するディレクトリ）

```
packages/ui/src/components
packages/ui/src/lib
platform/frontend/src/foundation/api
platform/frontend/src/foundation/i18n/locales
platform/frontend/src/foundation/testing
```

issue 本文は「該当は `platform/frontend/src/foundation/api` ただ 1 つ」としていたが、実測では
`foundation/i18n/locales` と `foundation/testing` も名前としては一致する（`packages/ui` はユニット内 SPA
ではないため対象外）。**結論（不適合）は変わらない** —— いずれも計画のツリーが求める**位置**にない。

### 走査 5 — `@foundation/<区分>` の利用実績（エイリアスの決定に使う）

```
125 @foundation/api        35 @foundation/routing   31 @foundation/testing
 30 @foundation/i18n       29 @foundation/auth      26 @foundation/ui
 16 @foundation/config      1 @foundation/notifications
```

裸の `from '@foundation'`（サブパス無し）は **0 件**。したがって 8 区分すべてが
`@foundation/<区分>` の形でしか使われておらず、**区分ごとに向き先を差し替えれば
エイリアス名を変えずに分解できる**。

## 設計

### 決定 1 — `@foundation` エイリアスは名前を変えない。区分ごとに向き先を差し替える

計画 §ディレクトリ構成 は「**本項が必須とするのはディレクトリ構成であって、エイリアス名ではない**」と
明記している。エイリアス名を変えると `src/ai-stock-trading`（別リポジトリの submodule。本リポジトリから
是正できない）と雛形 `templates/unit-template/frontend` の契約が同時に割れる。走査 5 のとおり
利用形はすべて `@foundation/<区分>` なので、第 2 段では次の 8 本の前置詞マッピングへ置き換える。

| エイリアス | 現在 | 第 2 段の向き先 |
| --- | --- | --- |
| `@foundation/config` | `src/foundation/config` | `src/app/config` |
| `@foundation/i18n` | `src/foundation/i18n` | `src/app/i18n` |
| `@foundation/routing` | `src/foundation/routing` | `src/app/routing` |
| `@foundation/api` | `src/foundation/api` | `src/lib/api` |
| `@foundation/auth` | `src/foundation/auth` | `src/lib/auth` |
| `@foundation/ui` | `src/foundation/ui` | `src/components/ui` |
| `@foundation/notifications` | `src/foundation/notifications` | `src/components/notifications` |
| `@foundation/testing` | `src/foundation/testing` | `src/testing` |

**この決定により、本段（knowledge の分割）は第 2 段でやり直しにならない** —— knowledge 側の
`@foundation/...` import は 1 行も変わらない。区分の割り当ては計画 §ディレクトリ構成 の
2026-08-22 追記が明示している対応（`config`/`i18n`/`routing`→`app/`、`api`/`auth`→`lib/`、
`ui`/`notifications`→`components/`、`testing`→`testing/`）をそのまま採る。

### 決定 2 — feature 内部の 6 区分は閉じた集合とし、`utils/` を新設しない

計画は括弧書きで 6 つ（`api/ components/ hooks/ routes/ stores/ types/`）を列挙する。純関数の
表示写像モジュール（`citations.ts` / `syncState.ts` / `driftView.ts` 等）は 6 区分のどれにも
「関数」としては当てはまらないが、**いずれも「その feature の表示用の型と、その型に付随する
写像」**であり、雛形 `types/index.ts` が定義する `types/` の役割（「画面の都合で組み立てる表示用の型」）の
延長に置く。`utils/` を feature 内に新設すると 7 つ目の区分になり、計画の列挙を実装側の判断で
広げることになるため採らない。

### 決定 3 — 区分の割り当て規則

| 現在のファイル形 | 行き先 | 根拠 |
| --- | --- | --- |
| `<X>Page.tsx` / `<X>Panel.tsx` / `<X>Form.tsx` / `ScopeFilter.tsx`（＋各テスト） | `components/` | 画面・部品 |
| `use<X>.ts`（＋各テスト） | `api/` | 実測では 13 feature の hook **12 本すべて**が TanStack Query ＋ orval 生成物のサーバー状態である |
| 純関数の表示写像・値集合（＋各テスト） | `types/` | 決定 2 |
| `index.tsx` のルート factory ＋ ナビ項目 | `routes/<x>Route.ts` | 雛形と同型 |
| （新規）feature の公開面 | `index.ts` | 雛形と同型。`routes/` から再輸出する |
| `hooks/` `stores/` | `.gitkeep` | クライアント状態の hook / Zustand ストアは現時点で 0 件。**枠は残す**（雛形 README の作法） |

### 決定 4 — 中身の無い区分にも枠を置く

雛形 `templates/unit-template/README.md` が定める作法（「中身が無い区分も、フォルダと `.gitkeep` だけは
置いてある。何も無いと**その構成要素が意図的に不在なのか単に作り忘れなのかが一見して分からない**」）に
従い、knowledge ユニット直下にも `app/ assets/ components/ hooks/ lib/ locales/ stores/ testing/ types/ utils/`
を `.gitkeep` 付きで置く。

### 決定 5 — `abac/` `scope-filter/` に公開面（`index.ts`）を置き、feature 跨ぎの import をそこへ寄せる

現在 5 ファイルが `../abac/confidentiality` `../scope-filter/scopeSelection` のように**他 feature の
内部ファイルを直接**参照している（実測 12 行）。分割後はその実パスが `../abac/types/confidentiality` に
なるため、いずれにせよ書き換えが要る。書き換え先を**公開面（`../abac`）**にすることで、
Bulletproof React の「feature の外から触ってよいのは index が再輸出したものだけ」を同時に満たす。

### 決定 6 — `features/` 直下の横断テストは動かさない

`adminFlow.test.tsx` / `opsFlow.test.tsx` / `searchFlow.test.tsx` / `routeSplitting.test.ts` は
**`features/index.ts`（ユニットの束ね役）そのものの試験**であり、特定の feature に属さない。雛形も
`features/` 直下に `index.ts` を置いており、直下にファイルがあること自体は計画に反しない。
`routeSplitting.test.ts` の `vi.mock` パスだけを新しい実パスへ追随させる。

## 受け入れ基準

- [ ] knowledge の 13 feature がすべて、計画の 6 区分のディレクトリだけを直下に持つ（`index.ts` を除く）
- [ ] 走査 2 を再実行して `subdirs=0` の feature が 0 件になる
- [ ] knowledge ユニット直下に計画のツリー項目 10 個が実在する
- [ ] feature 跨ぎの import が公開面（`index.ts`）経由になる
- [ ] `pnpm run typecheck` / `lint` / `format:check` / `test` が通る
- [ ] カバレッジ床（`src/vitest.config.ts` の `thresholds`）を割らない
- [ ] Lingui カタログの再生成差分が無い（`pnpm run i18n` 後に差分ゼロ／`check-i18n-catalogs.js` が通る）
- [ ] `node scripts/check-trace-blocks.js` / `check-doc-links.js` が通る
- [ ] `@foundation` エイリアスの扱いが決定され、第 2 段の手順が IADR に残る

## テスト方針

**振る舞いを変えない構成変更である。** 既存テストは 1 件も削らず、1 件も足さない（移動と import の
追随のみ）。テスト件数・カバレッジが変わらないことをもって「振る舞いを変えていない」ことの証跡とする。
`routeSplitting.test.ts`（遅延境界の回帰ガード）が通ることが、分割で静的 import が復活していないことの
証跡になる。

## 計画書との差異

- 差異: **あり（段の分割）。** 計画 §ディレクトリ構成 への完全適合は platform 側の `foundation/` 分解を
  含むが、これは本リポジトリの `scripts/scripts.repo.test.js`・`.github/workflows/pr-size.yml`・
  `.github/workflows/frontend.yml` が保持する**実パスの固定**（生成物の PR サイズ除外・codegen 差分検査・
  `SUPPORTED_LOCALES` の突合）を同時に更新しなければ CI が赤になる。本作業の担当範囲はそれらの
  ファイルを含まないため、**第 2 段として分離する**。issue #785 自身も「規模が大きいため分割する」
  「着手前に 2（`foundation/` の扱い）の判断を先に決める」と提案しており、その提案どおりの進め方である。
  判断（決定 1）は本書と IADR-0262 で先に確定させたので、本段のやり直しは生じない。
- 差異: **なし（構成そのもの）。** ツリー・6 区分は計画のまま採る。

## 未決事項

- 第 2 段（platform の `foundation/` 分解）の実施主体と、上記 3 ファイルの更新の割り当て。
- 退行防止の検査器（`features/*/` 直下に 6 区分以外を置かせない）は、運用ガイドの
  「同型の事故が 2 回起きたら」の条件を満たしていないため本段では置かない（issue 本文も同じ判断）。

---
title: NotFound のランドマーク重複（Layout の main との入れ子）を解消し、axe の走査面に存在秘匿の 404 と確認ダイアログを加える
type: spec
status: done
related_ids: [NFR-12, ADR-0031, SC-11, SC-19, IADR-0009, IADR-0035, IADR-0120, IADR-0121, IADR-0124, IADR-0125, IADR-0134, IADR-0330, IADR-0435, IADR-0438, IADR-0440, IADR-0442]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# 仕様書: NotFound のランドマーク重複解消と axe 走査面の拡張（#1438）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（既存画面の a11y 是正であり、新しい機能要求は起点にならない）
- 非機能要件（NFR）: `NFR-12`（アクセシビリティ）
- ユースケース（UC）: なし
- 画面（SC）: `SC-11`（構成ビューア。**権限外＝存在秘匿の 404 を出す面として使う**）／
  `SC-19`（個人資料。**確認ダイアログを開いた面として使う**）
- 関連 ADR: `ADR-0031`（フロントエンドスタック）／`IADR-0009`（404 で存在秘匿）／
  `IADR-0035`（権限外は `RequireRole` → `NotFound`）／`IADR-0124`（ルート木と catch-all の配置）／
  `IADR-0438`（`NotFound` の `<main>` 入れ子＝既存負債として据え置いた）／
  `IADR-0440`（a11y の機械検査。走査面 3 つ・`wcag2a` / `wcag2aa` のみ）／
  `IADR-0442`（本作業の決定）
- 計画書リンク: `projects/microservices-platform/02_requirements/01_requirements.md`（NFR-12）

## 目的・背景

#1436（IADR-0440）で a11y の機械検査を入れたが、**`NotFound` が自前の `<main>` を持ち、
`Layout` の `<main id="main-content">` と入れ子になる既存負債**は据え置かれた
（IADR-0438 §結果・IADR-0440 §結果・
[20260912_frontend-nocturne-and-experience-gaps](20260912_frontend-nocturne-and-experience-gaps.md)
§残余リスク 3 / §未決事項 3・4）。

**2 つの検査が同時に見逃す構造**になっている。

1. 静的解析（`eslint-plugin-jsx-a11y`）は **JSX の要素名しか見ない**ため、
   `Layout.tsx` の `<main>` と `NotFound.tsx` の `<main>` が**実行時に入れ子になる**ことを判定できない。
2. axe の走査面 3 つ（共通シェル・SC-01・SC-05）は**いずれも `NotFound` を通らない**。
   加えてタグが `wcag2a` / `wcag2aa` だけなので、ランドマークの規則
   （`landmark-no-duplicate-main` / `landmark-main-is-top-level`。どちらも `best-practice`）は
   **そもそも評価されていない**。

本作業は (a) 負債そのものの解消、(b) それを**今後は機械が捕まえる**状態にすること、の 2 つを行う。

## 対象範囲

- 対象:
  - `src/platform/frontend/src/components/ui/NotFound.tsx`（外側要素を `<main>` 以外へ／シェル外用の器を追加）
  - `src/platform/frontend/src/app/routing/shell.tsx`（シェル**外**の経路にだけ `<main>` を与える）
  - `src/platform/frontend/src/app/routing/router.test.ts`・`src/platform/frontend/src/app/Layout.test.tsx`
  - `src/platform/frontend/e2e/a11y.smoke.spec.ts`（`best-practice` タグ／走査面 2 つ追加）
  - `best-practice` を有効化して出た既存違反の是正（**抑制へ逃がさない**。裁定 7）
- 対象外:
  - **存在秘匿の文言・役割**（`見つかりませんでした` / `お探しのページは存在しないか、アクセスできません。`
    と見出しレベル 1）。**一字も変えない。**
  - `src/ai-stock-trading/**`（submodule ＝別プロジェクト。IADR-0120。**読むだけで変更しない**）
  - `src/platform/frontend/src/features/index.ts` / `IADR-0441` への追記
    （**親エージェントが同じ checkout で行う**）。
    `scripts/chunk-budget-baseline.json` は**床の数値と本作業専用の `$comment_…_20260912_1438` キー
    だけ**を触る（既存の `$comment` 本体は触らない＝親の編集と衝突させない）
  - `AiChatPanel.tsx`（#1437 が worktree で並行して `AiChatRail` の lazy 化とファイル分割を進めている）。
    着手時は「閉じた列が `region` 違反を出すなら、ラッパ要素と `aria-label` だけ最小に触る」と
    構えていたが、**実測で `region` は出なかったため 1 行も触っていない**（§検証記録 2）。
    🔴 **見込みで直さない** —— 触っていれば #1437 と無用に衝突していた

## 設計

### 1. `NotFound` の外側要素（ランドマーク重複の解消）

```
<section aria-labelledby={NOT_FOUND_HEADING_ID}>  ← 従前は <main>
  <h1 id={NOT_FOUND_HEADING_ID}>見つかりませんでした</h1>
  <p>お探しのページは存在しないか、アクセスできません。</p>
</section>
```

- 🔴 **見出しの id は固定値にする。** `useId()` は描画位置で値が変わるため、
  「未知パスと権限秘匿で markup が完全一致する」ことを `outerHTML` で固定している
  `Layout.test.tsx`（存在秘匿の担保そのもの）が割れる。`NotFound` は 1 画面に 1 つしか出ないので
  固定 id で衝突しない。
- `<section>` ＋ 名前は `region` ランドマークになる。**`main` の中の `region` は入れ子違反ではない**
  （`landmark-main-is-top-level` が禁じるのは `main` の入れ子である）。
- クラス（`px-n4 py-n8 text-center`）は据え置き＝**見た目を変えない**。

### 2. シェルの**外**で描かれる経路にだけ `<main>` を与える

`NotFound` は 4 箇所から描かれる（後述の母集合 B）。**`rootRoute.notFoundComponent` だけがシェルの外**で、
ここから `<main>` を外すとページにランドマークが 1 つも無くなる。そこで同じファイルに器を足す。

```
export function NotFoundPage() { return <main …><NotFound /></main>; }   // rootRoute 専用
```

- **存在秘匿は損なわれない。** 未知 URL は必ず**シェル配下**の `catchAllRoute` が受ける
  （IADR-0124 決定 8。配線は `router.test.ts` が固定済み）。`rootRoute` 側は
  「シェルの外で `notFound()` が投げられた場合」の受け皿であり、URL で到達して比較できる面ではない。
- **シェル内の 2 経路（`catchAllRoute.component` / `shellRoute.notFoundComponent`）と
  `RequireRole` は素の `NotFound` のまま**＝今までどおり完全に同一の markup を出す。
- この非対称の理由を `shell.tsx` と `router.test.ts` の両方にコメントで残す
  （後任が「揃っていない」と見て戻さないため）。

### 3. axe の走査面とタグ

- `expectNoAxeViolations` のタグへ **`best-practice` を足す**（`landmark-no-duplicate-main` /
  `landmark-main-is-top-level` / `region` / `landmark-unique` がここで初めて評価される）。
  規則単位の除外に備えて第 3 引数 `{ disableRules?: string[] }` を受ける形にするが、
  **使うのは「構造上直せない」と実測で判明したときだけ**である（面単位・ファイル単位の抑制はしない）。
- **追加面 4: 存在秘匿の 404**。`sessionUser([])` で `/admin/config-viewer`（SC-11＝管理/運用限定）を開く。
  `RequireRole` → `NotFound` がシェルの内側に出る面である。
- **追加面 5: 確認ダイアログを開いた状態（SC-19）**。`/my/notes` の「削除する」からダイアログを開く
  （開き方は `keyboard-navigation.smoke.spec.ts` の先行例に倣う）。
- 🔴 **引き方は役割と表示名だけ**（`getByTestId` へ逃げない）。
- 🔴 **spec ファイルを新設しない。** `scripts/check-route-manifest.js` は `sc<NN>-*.spec.ts` を
  画面の母集合として数えるため、横断の spec を足すと母集合が乱れる（IADR-0440 決定 4）。
  既存の `a11y.smoke.spec.ts` に足す。

### 4. 母集合（`.claude/rules/traceability.repo.md` 規則 1〜10。**記憶ではなく走査で引いた**）

**母集合 A: `<main>` を持つ要素**（`cd src && grep -rn "<main" --include=*.tsx .`。node_modules 除く）

| ファイル | 描画位置 | 判定 |
| --- | --- | --- |
| `platform/frontend/src/app/Layout.tsx:275` | 共通シェルの本文列（`id="main-content"`） | 正。skip link の着地点 |
| `platform/frontend/src/components/ui/NotFound.tsx:11` | **シェルの内と外の両方** | 🔴 負債の本体＝本作業の対象 |
| `platform/frontend/src/components/ui/ErrorBoundary.tsx:46` | ルータの**外**（`App.tsx`） | 入れ子にならない。対象外 |
| `platform/frontend/src/lib/auth/LoginPage.tsx:23` | `loginRoute`（`rootRoute` 直下＝シェル外） | 入れ子にならない。対象外 |
| `ai-stock-trading/frontend/test/foundation-stub/ui/NotFound.tsx:4` | AST の型検査／自前 e2e ハーネス専用スタブ | **除外**: submodule ＝別プロジェクト（IADR-0120）。読むだけ |

他の 3 件（`Layout.test.tsx:318,321,322`）は**コメント中の言及**である。
うち 318〜322 行は「器は Layout の `<main>`、その中の `<main>` が NotFound」と書いており、
**本作業で嘘になるため是正する**（規則 10＝是正のたびに「この変更で新たに誤りになる自分の記述」を引き直す）。

**母集合 B: `NotFound` を描く経路**（`grep -rn "NotFound" --include=*.tsx --include=*.ts`）

| 参照元 | 位置づけ | 本作業での扱い |
| --- | --- | --- |
| `app/routing/shell.tsx:45`（`shellRoute.notFoundComponent`） | シェルの内 | 素の `NotFound` のまま |
| `app/routing/shell.tsx:80`（`catchAllRoute.component`） | シェルの内 | 素の `NotFound` のまま |
| `lib/auth/RequireRole.tsx:18` | シェルの内 | 素の `NotFound` のまま |
| `app/routing/shell.tsx:17`（`rootRoute.notFoundComponent`） | **シェルの外** | `NotFoundPage`（`<main>` の器）へ |
| `router.test.ts` / `Layout.test.tsx` / `i18n.test.tsx` / `initialChunk.test.ts` | テスト | 期待値・コメントの是正のみ |
| knowledge `sc09` / `sc10` / `sc11` のアクセステスト | テスト | **無変更**（`render(<NotFound />)` 同士の markup 比較なので両辺が同時に動く） |
| `ai-stock-trading/**`（ハーネス・スタブ・access テスト 4 本） | submodule | **除外**（IADR-0120）。役割と文言で引いており、本作業では壊れない |

除外は上表のとおり 2 系統（AST submodule / コメント中の言及）だけで、**黙って外したものは無い**。

## 受け入れ基準

- [x] `NotFound` の外側が `<main>` でなくなり、`Layout` の `<main>` と入れ子にならない
- [x] 存在秘匿の**文言・役割・見出しレベル**が不変（既存テストを 1 件も書き換えずに緑）
- [x] シェル外の経路（`rootRoute.notFoundComponent`）でもページに `main` ランドマークが 1 つある
- [x] `a11y.smoke.spec.ts` のタグに `best-practice` が入り、走査面が 5 つ（＋存在秘匿の 404 ＋ 確認ダイアログ）
- [x] **陰性対照**: 修正前に 404 面で `landmark-*` 違反が**出る**ことを実測し、修正後 0 件を実測（§検証記録）
- [x] `best-practice` で出た既存違反を**抑制へ逃がさず**直した（規則単位の `disableRules` を使うなら IADR に理由）
- [x] §4 のゲートが全て緑

## テスト方針

- **E2E（axe）** が主。追加 2 面は「壊れても他のどのテストも赤くならない」経路である
  ——存在秘匿の 404 は 9 本の spec が通るが**どれも axe には掛けていない**。
- **単体**は既存の契約（markup 一致・catch-all の配線）で守る。
  加えて `Layout.test.tsx` に「**`main` ランドマークはシェルに 1 つだけ**」の回帰テストを 1 件足す
  ——E2E が落ちるのはビルドまで進んだ後であり、単体で先に落ちる方が速い。
- 陰性対照の取り方は「**先に spec だけ変えて赤を見る → 実装を直して緑を見る**」の 2 段で行い、
  両方の実測を §検証記録に残す（「検査が効いている」証拠が無い緑を作らない）。

## 検証記録

### 1. 陰性対照 —— 修正前（E2E の spec だけを変え、`NotFound` は現行のまま）

```
cd src && pnpm run build
cd platform/frontend && pnpm exec playwright test e2e/a11y.smoke.spec.ts
→ 4 passed / 1 failed
```

**落ちたのは追加面「存在秘匿の 404」だけ**（light テーマで停止するため dark は未到達）:

| 規則 ID | impact | 対象 |
| --- | --- | --- |
| `landmark-main-is-top-level` | moderate | `.py-n8`（＝ `NotFound` の `<main>`） |
| `landmark-no-duplicate-main` | moderate | `#main-content`（＝ `Layout` の `<main>`） |
| `landmark-unique` | moderate | `#main-content` |

🔴 **これが「検査が効いている」証拠である。** 同じコミットで `best-practice` を足さなければ、
この 3 件は 1 件も報告されない（規則自体が評価されない）。

### 2. `best-practice` を足して新たに出た既存違反 —— **`NotFound` 由来の 3 件だけ**

**他の 4 面（共通シェル・SC-01・SC-05・SC-19 の確認ダイアログ）は `best-practice` でも違反 0 件**であった。

- 事前に懸念していた `region`（"All page content should be contained by landmarks"）は**出なかった**。
  `AiChatPanel` は畳まれているとき素の `<div>` ＋ `<button>` をシェルの列に描くため、
  ランドマークの外に置かれた操作として報告されると見込んでいたが、実測では報告されない。
  **よって `AiChatPanel` は変更していない**（見込みで直さない）。
- `disableRules` の使用は **0 件**。抑制へ逃がしたものは 1 つも無い。

### 3. 修正後

```
cd platform/frontend && pnpm exec playwright test e2e/a11y.smoke.spec.ts
→ 5 passed（全 5 面 × light/dark の 2 テーマで違反 0・`passes` は各面 1 件以上）
```

### 4. ゲート（すべて緑。2026-09-12 実測）

| ゲート | 結果 |
| --- | --- |
| `pnpm -r run typecheck` | 6 パッケージ Done |
| `pnpm run lint` | 0 error（warning 12 は既存の `react-refresh/only-export-components`） |
| `pnpm run format:check` | 初回 2 件 → `pnpm run format` → All matched files use Prettier code style |
| `node ../scripts/check-knip.js --require` | 床どおり 36 件 |
| `pnpm run i18n` → 再生成差分 | **0 件**（文言を 1 つも増やしていない）。`check-i18n-catalogs` も未翻訳 0 |
| `pnpm run build` → `check-chunk-budget --require` | 731.29 kB → **731.43 kB（+142 B）**。`--update` で床を更新（内訳は下記） |
| `pnpm run test:coverage` | 137 files / 1616 tests passed。98.33 / 93.09 / 94.96 / 98.33（床 93/93/89/88） |
| `node ../scripts/check-route-manifest.js` | 画面 17 件・E2E 17 画面ぶん（新 spec を作っていないので母集合は不変） |
| `pnpm exec playwright test`（全走） | **60 passed**（従前 58 ＋ 追加 2 面） |
| `check-trace-blocks` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-links` / `check-reading-budget` / `check-adr-numbering` / `gen-knowledge-graph --check` | すべて OK |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 771 tests passed（ADR 索引タイトルのラチェットを含む） |

#### チャンク床の増分（+142 B）の帰属

🔴 **測る前に `rm -rf platform/frontend/dist` して再ビルドした。** 同じ checkout を他の作業が使っており、
別作業の合成ビルド（初期ロード 706 kB）が dist に残っていると**それを自分の成果と誤読する**。
再ビルド後の実測は 731.43 kB で、クリーンビルドでも再現した。

増分は `NotFoundPage`（`<main>` 1 枚）の追加と `<section aria-labelledby>` ＋ 見出しの固定 id だけである。
`NotFound` は存在秘匿のため初期チャンクに残る部品なので（IADR-0134）、増分は初期ロードに載る。
**i18n の文言は 1 つも増えていない**（決定 1「文言・役割は不変」の帰結。再生成差分 0 件が裏付け）。
E2E の走査面追加は成果物に入らない。

### 5. 規則 10 の引き直し（この変更で新たに誤りになる自分の記述）

`入れ子` ＋ `main|landmark|NotFound` と `3 面|走査面|a11y.smoke` の 2 軸で追跡下を走査した結果、
誤りになるのは次の 3 文書だけであった（`docs/` 配下には 1 件も無い＝ trace ブロックの作業は発生しない）。
いずれも**本文プロズを書き換えず、`［2026-09-12 追記 / #1438］` の日付つき追記**で閉じた。

| 文書 | 誤りになった記述 | 追記 |
| --- | --- | --- |
| `IADR-0438` §結果・§フォローアップ | 「既存負債として据え置いた」「`jsx-a11y` は検出しない」 | 解消済み（IADR-0442）。「片方に合わせるともう片方が `<main>` を失う」の解き方も書いた |
| `IADR-0440` §結果・§フォローアップ | 「どちらの検査でも落ちない」「走査するのは 3 面だけ」「広げるかは実測を見てから」 | 塞いだ／5 面へ広げた（IADR-0442 決定 3） |
| `20260912_frontend-nocturne-and-experience-gaps` §残余リスク 3・§未決事項 3・4 | 同上 | 打ち消し線 ＋ 追記（`.ai-context/specs/` の経過追記は `traceability.repo.md` §凍結の射程が許す） |

## 計画書との差異

- 差異: なし

## 未決事項

- なし

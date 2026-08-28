---
title: フロントエンドのディレクトリ構成を Bulletproof React（計画 §ディレクトリ構成）へ適合させる — 第 2 段: platform の foundation/ 分解
type: spec
status: done
related_ids: [NFR, ADR-0031, ADR-0019, IADR-0056, IADR-0121, IADR-0124, IADR-0125, IADR-0134, IADR-0181, IADR-0211, IADR-0262]
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
---

# 仕様書: フロントエンドの Bulletproof React 適合（第 2 段 — platform）

起票は #785。実装 ADR は IADR-0262（決定 5 が段分けを定める）。第 1 段（knowledge の feature 内部分割）の
作業仕様書は `20260823_issue-785_bulletproof-react-structure.md`。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-06〜FR-10（SPA 画面群）、FR-14（可変ユニット合成）
- 非機能要件（NFR）: 構成規約への適合。**個別 ID は持たない** —— 計画の非機能要件表は稼働する製品の
  要件であり、ディレクトリ構成の統制は工程の軸である（`.claude/rules/traceability.md`「起点 ID の種別」の
  無採番 `NFR` 許容ケース 2）。**環流しない。**
- 画面（SC）: SC-01〜SC-11 / SC-18 / SC-21（構成変更の影響先。仕様は変えない）
- 関連 ADR: ADR-0031（フロントエンド技術スタック。設計 = Bulletproof React）/ ADR-0019（ユニット構成）
- 計画書リンク: `projects/microservices-platform/06_technical/13_frontend-stack.md` §ディレクトリ構成

## 目的・背景

計画 `13_frontend-stack`（`status: fixed` / updated 2026-08-22）§ディレクトリ構成 は、ユニット内 SPA の
構成を次のツリーで示し、**2026-08-22 の利用者裁定で「適合は必須。実装を計画へ合わせる」**と再確定された。

```text
src/
├── app/          # providers / router / i18n / config
├── assets/       # 自己ホストのフォント・画像
├── components/   # 共通コンポーネント
├── features/     # Feature 単位（api/ components/ hooks/ routes/ stores/ types/）
├── hooks/ lib/ stores/ testing/ types/ utils/
├── locales/      # ja / en（Lingui）
└── main.tsx
```

同裁定は **`foundation/` → `app/` の改名だけでは適合にならない**（`foundation/` 直下の区分がツリーの
5 項目にまたがる）と明記している。第 1 段で knowledge を適合させたので、残るのは platform である。

## 母集合の走査

**走査は着手前に自分で引いた**（`.claude/rules/traceability.repo.md`「是正・追随の母集合の取り方」規則 9・10）。
走査コマンドはいずれも `git grep`（追跡下の全ファイル・拡張子で絞らない・行フィルタで絞らない）。

### 走査 1 — platform 直下にツリー項目が実在するか（適合前）

```
MISS app / MISS assets / MISS components / OK features / MISS hooks / MISS lib
MISS locales / MISS stores / MISS testing / MISS types / MISS utils
実在: App.tsx  features  foundation  main.tsx  test
```

**11 項目中 10 項目が不在。** 加えてツリーに無い `foundation/` `test/` `App.tsx` が直下に居る。

### 走査 2 — `@foundation/<区分>` の利用実績（追跡下の全ファイル。AST・雛形を含む）

```
170 @foundation/api          47 @foundation/routing    46 @foundation/testing
 36 @foundation/i18n         34 @foundation/ui         31 @foundation/auth
 19 @foundation/config        4 @foundation/notifications   1 @foundation/ai-chat
```

裸の `from '@foundation'`（サブパス無し）は **0 件**（`['"]@foundation['"]` の 4 件はいずれも
エイリアス定義そのものか、その 0 件を記録した文書である）。**利用形はすべて `@foundation/<区分>`** なので、
区分ごとに向き先を差し替えれば利用側の import を 1 行も書き換えずに実配置を動かせる（IADR-0262 決定 1）。

**区分は 9 つある。** IADR-0262 決定 1 の対応表は 8 区分（計画 §ディレクトリ構成 の 2026-08-22 実測を
写したもの）だが、その後 #788 で `ai-chat` が加わった。→ §設計 決定 A で扱う。

### 走査 3 — 実パス `foundation/` を指す参照（`@foundation` を除く。自ディレクトリ・AST 除く）

`git grep -l -I -E "(^|[^@a-zA-Z_-])foundation/"` の全ヒット **117 ファイル**。区分すると:

| 区分 | 件数 | 扱い |
| --- | --- | --- |
| `.ai-context/{adr,specs,superpowers}` の凍結記録 | 70 | **除外**（凍結。IADR-0262 のみ日付つき追記） |
| `CHANGELOG.md` | 1 | **除外**（生成物。手で書き足さない） |
| 残り | 46 | 下記 3 分類 |
| ── 機械が読む設定・CI・検査器 | 15 | **全件追随**（走査 3b の 1 件を足して計 16） |
| ── live な文書のうち**完全パス／実配置のツリー図** | 12 | **全件追随** |
| ── live な文書のうち**公開面の名前**として書いているもの | 19 | **除外**（理由は下記） |
| `src/ai-stock-trading` | — | **除外**（別リポジトリの submodule。IADR-0120） |

70 + 1 + 15 + 12 + 19 = 117。**引き算を明示する。**

**「公開面の名前」を除外する理由。** IADR-0262 決定 1 は「**帰結として `@foundation` は『ディレクトリ名』
ではなく『platform 基盤の公開面の名前』になる**」と明記している。散文の `foundation/api` /
`foundation/ui/Layout` / `foundation/testing/renderUnitRoute.tsx` は、その公開面
（`@foundation/api` 等）を指しており、**エイリアス名は本作業で変えない**ので誤りにならない。
一方 `src/platform/frontend/src/foundation/...` のように**リポジトリ相対の完全パス**を書いた箇所と、
**実配置を図示したディレクトリツリー**は、移動後に実在しなくなるので誤りである。
**線はこの 1 本で引き、例外を作らない。**

除外した 19 件（黙って落とさない。すべて公開面の名前としての言及）:
`docs/api/openapi.yaml` / `docs/authz/bff-session-design.md` /
`docs/screens/{SC-01,SC-02,SC-03,SC-05,SC-06,SC-07,SC-08,SC-09,SC-11}`（9 件。いずれも
「`foundation/ui/Layout` が既に持つ」の形） / `docs/tests/{SC-10,SC-11}` /
`scripts/README.md` / `scripts/chunk-budget-baseline.json`（`$comment`） /
`src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/TagDictionaryBffEndpoints.cs` /
`src/knowledge/frontend/src/features/{sc05-documents/api/useDocumentAdmin.test.tsx,
sc07-conversions/api/useConversionJobs.ts, sc11-config/routes/access.test.tsx}`。

`src/eslint.config.js` / `src/lingui.config.ts` / `src/orval.config.ts` は**注釈が公開面の名前**で
書かれている（除外）一方、**同じファイルのグロブ・パスは機械が読むので追随する**（分類 A 側に数えた）。

### 走査 3b — 別の形: 末尾スラッシュの無い `foundation`（規則 2「あり得る形をすべて列挙してから引く」）

走査 3 のパターンは `foundation/` を要求するため、**`'./src/foundation'` のように末尾で終わる形を
構造的に取りこぼす**。`git grep -l -I "foundation"` は 264 ファイル。走査 3 との差 147 ファイルのうち、
`@foundation/<区分>`（エイリアス。不変）と `.ai-context/`・`*_frontend-spa-foundation.md` の
ファイル名一致・C# の `Foundation/`（バックエンドの別概念）を除くと、**新たに追随が要るのは 1 件**:

```
src/platform/frontend/vite.config.ts:82   '@foundation': fileURLToPath(new URL('./src/foundation', …))
```

**これは走査 3 だけでは捕まらなかった。** 規則 2 が防ぐ事故の実例であり、
落としていれば「型検査は通るがビルドだけ壊れる」形になっていた（`tsconfig` の `paths` と
`vite` の `alias` は別々に持っているため）。

`README.md` / `src/README.md` / `docs/tech/system-architecture.md` /
`src/platform/frontend/package.json` の「SPA 基盤（foundation + アプリホスト）」は**公開面の名前**
であり上の線の外側なので除外する。`scripts/check-unit-dependencies.js` の `foundation-composable` は
**C# バックエンドの `Foundation/`** を指す別概念であり無関係（除外）。

### 走査 4 — 別の軸: platform の `src/test/` を指す参照（規則 5「軸を 1 本で終わらせない」）

```
CLAUDE.md:128                                        （散文・完全パス）
src/platform/frontend/src/foundation/testing/renderUnitRoute.tsx:94  （注釈・完全パス）
src/vitest.config.ts:38  setupFiles                  （機械）
src/vitest.config.ts:67  coverage.exclude            （機械）
```

走査 3 の語（`foundation/`）では **1 件も捕まらない** —— 規則 5 の実例である。

### 走査 5 — さらに別の軸: `App.tsx` の実参照（移動先を決めるため）

```
src/platform/frontend/src/main.tsx:7                      import { App } from './App';
src/platform/frontend/src/foundation/routing/initialChunk.test.ts:45  await import('../../App')
src/platform/frontend/README.md:24                        構成図の記載
```

残る 8 件（`.ai-context/` の凍結記録・テスト注釈の「実アプリ（`App.tsx`）」）は**ファイル名の言及**であり
パスではないため対象外。

### 走査 6 — 導出値の引き直し（規則 7・8）

- 単体テストの基準値は**走査ではなく実走で取り直した**: 適合前 `pnpm -w run test -- --run` =
  **91 ファイル / 1080 テスト 全緑**。
- 本仕様書自身が走査 2・3・3b の検索語を含む。**走査は本ファイルを書く前に実行している**ので、
  走査 3 の 117 件・走査 3b の 264 件に本ファイルは入っていない。**コミット後はそれぞれ
  118 件 / 265 件になる**（117 + 自己参照 1 = 118、264 + 1 = 265）。値は本コミットで固定する。

### 走査・追随の破れ（作業中に赤で見つけた取りこぼし 3 件・黙って直さない）

**走査 1〜6 と追随作業は 3 件を取りこぼしており、いずれも検査器が赤で教えた。** 記録に残す
（規則 6「除外したものとその理由を書く」の裏返しで、**引き漏らしも書く**）。

| # | 取りこぼし | なぜ落ちたか | どう直したか |
| --- | --- | --- | --- |
| 1 | `foundation/notifications/notificationContract.test.ts` が `readFileSync` で **`src/platform/frontend/src/foundation/api/generated/bff.schemas.ts` の完全パス**を読んでいた | 走査 3 が `':!src/platform/frontend/src/foundation'` で**移動する当のディレクトリを除外していた**。「動かす側」の中から「動かす側」を指す参照は、この除外で構造的に落ちる（規則 3 の親戚） | パスを `lib/api/generated/` へ。移動後に同ディレクトリを**改めて全走査**して残りが無いことを確認した |
| 2 | 凍結記録 `.ai-context/specs/20260725_issue-353_…md` の **frontmatter 値と本文リンク**が `foundation/auth/` を指しており `check-doc-links.js` が落ちた | 走査 3 が `.ai-context/` を一律「凍結だから除外」としていた。**凍結でもリンクは機械が実在検査する** | **リンク先だけ**を `lib/auth/` へ追随させ、日付つき追記で記録した。**当時の実測・判断の文は変えていない**（同ファイルが #439 で既に採っていた作法をそのまま踏襲） |

| 3 | 追随した `docs/` 7 件の frontmatter `updated:` を進め忘れた | `check-doc-updated.js` は
**HEAD（コミット済み）を読む**ため、**編集をコミットする前に実行すると緑に見える**（検査器自身が
その旨を warn で言う）。「検査は全部通した」の実行時点が編集の**前**だった | 7 件の `updated:` を
2026-08-28 へ進め、**コミットしてから**再実行して 615 件全緑を確認した |

**教訓 1**: 「凍結だから触らない」と「機械が実在を検査する」は別の軸である。
`.ai-context/` を母集合から外すときは、**リンク／frontmatter のパス値だけは別に引く**。

**教訓 2**: **HEAD を読む検査器は、コミット前の実行では緑を返す。** 完了判定は必ずコミット後に取る。

## 設計

### 決定 A — `ai-chat` は `components/ai-chat` へ置く（決定 1 の対応表の第 9 行）

IADR-0262 決定 1 の表は 8 区分しか持たない（計画の 2026-08-22 実測を写したため）。`ai-chat` は
#788 で後から加わった第 9 区分であり、**表に無いので置き場を決める必要がある**。`components/` を採る。

- `src/eslint.config.js` が既に **「共通シェルに載る文言なので、`foundation/ui` と同じ規則の下に置く」**
  と書いて `ai-chat` を `ui` と同じ files 配列へ入れている。**規約上すでに `ui` と同類**である。
- 唯一の利用者は `foundation/ui/Layout.tsx`（共通シェル）であり、画面（features）ではない。
- `aiChatStore.ts`（Zustand）と `useAiChatStream.ts`（hook）を**同居のまま運ぶ**。トップレベルの
  `stores/` `hooks/` へ散らさない —— knowledge 側の `components/` も `echartsLoader.ts` 等の
  非コンポーネントを同居させており（第 1 段の実績）、`@foundation/ai-chat` という 1 つの公開面を割らない。

### 決定 B — Lingui カタログはユニット直下の `locales/` へ出す（i18n の実装は `app/i18n`）

IADR-0262 決定 1 は **`@foundation/i18n` → `src/app/i18n`** と定める。これは**エイリアスの向き先**の
指定であり、カタログ（`locales/<locale>/messages.{po,ts}`）の置き場は同表の射程外である。
実測でも `@foundation/i18n/locales/...` の形で外から import されている実績は **0 件**（走査 2）で、
カタログは `app/i18n/index.ts` が相対 import するだけである。**つまりカタログをどこへ置いても
決定 1 の対応表は満たされる。**

置き場は計画ツリーに従い**ユニット直下の `locales/`** とする。ツリーは
`locales/      # ja / en（Lingui）` と**中身まで名指し**しており、2026-08-22 の裁定は
**「必須とするのはツリー全体への適合である。名前だけを揃える対応は採らない」**と定めている。
`app/i18n/locales/` に留めると、platform でも `locales/` が空になり、
**ツリーが列挙する区分のうち 1 つが誰にも満たされない**状態が残る。

雛形 README（IADR-0262 と同じ作業で書かれたもの）も **「`app/` と `locales/` は、ユニットでは
通常空のままになる（アプリホストである `platform/frontend` が持つ）」**と明記しており、
**アプリホストが `locales/` を持つ**ことを前提にしている。決定 B はこの記述と一致する。

結果として `app/i18n/index.ts` の import だけが `./locales/...` → `../../locales/...` になる。
**エイリアスへ寄せない**（`@foundation/<区分>` は公開面の名前であり、`locales/` は公開面ではない）。
その理由は同ファイルの import の直前に書いた。

### 決定 C — `App.tsx` は `app/` へ、`test/setup.ts` は `testing/` へ

計画ツリーの直下に置いてよいのは 11 の区分と `main.tsx` だけである。

- `App.tsx` は providers（I18nProvider / ErrorBoundary / QueryClientProvider / AuthProvider）と
  RouterProvider の合成ルートであり、ツリーの `app/  # providers / router / i18n / config` そのもの
  → `app/App.tsx`。
- `test/` はツリーに無い。中身は横断 Vitest の setup 1 本だけであり、`testing/`（＝
  `@foundation/testing` の移動先）と役割が同じ → `testing/setup.ts`。
- `main.tsx` はツリーが直下に置いている → **動かさない**。

### 移動の対応表（旧 → 新。すべて `git mv`）

| 旧 | 新 | 根拠 |
| --- | --- | --- |
| `src/foundation/config/` | `src/app/config/` | 決定 1 |
| `src/foundation/i18n/` | `src/app/i18n/` | 決定 1 |
| `src/foundation/i18n/locales/` | `src/locales/` | 決定 B |
| `src/foundation/routing/` | `src/app/routing/` | 決定 1 |
| `src/foundation/api/`（`generated/` を含む） | `src/lib/api/` | 決定 1 |
| `src/foundation/auth/` | `src/lib/auth/` | 決定 1 |
| `src/foundation/ui/` | `src/components/ui/` | 決定 1 |
| `src/foundation/notifications/` | `src/components/notifications/` | 決定 1 |
| `src/foundation/ai-chat/` | `src/components/ai-chat/` | 決定 A |
| `src/foundation/testing/` | `src/testing/` | 決定 1 |
| `src/test/setup.ts` | `src/testing/setup.ts` | 決定 C |
| `src/App.tsx` | `src/app/App.tsx` | 決定 C |

新設する空区分（`.gitkeep`）: `assets/` `hooks/` `stores/` `types/` `utils/`。
`locales/` は決定 B により**実体を持つ**ので `.gitkeep` を置かない。

### エイリアスの向き先（名前は変えない）

`tsconfig.app.json`（platform）/ `knowledge/frontend/tsconfig.json` / `templates/unit-template/frontend/tsconfig.json`
の `paths`、`platform/frontend/vite.config.ts` と `src/vitest.config.ts` の `resolve.alias` を、
**ワイルドカード 1 本から区分ごとの 9 本へ**置き換える。9 つのキーは互いに前方一致しないので順序に依存しない。

## 同時更新が必須の 3 ファイル（IADR-0262 決定 5）

| ファイル | 固定している実パス | 本作業での更新 |
| --- | --- | --- |
| `.github/workflows/pr-size.yml` | `EXCLUDES` の生成物 2 本 | `.../src/lib/api/generated/**` / `.../src/locales/**` |
| `.github/workflows/frontend.yml` | codegen / i18n の再生成差分検査の対象 | 同上（`platform/frontend/` 相対） |
| `scripts/scripts.repo.test.js` | 上記 2 本の**実在検査**と `foundation/i18n/index.ts` の直読み | 3 箇所を新パスへ |

## 追随する設定・ワークフロー・検査器（機械が読むもの・全 16 件）

1. `src/platform/frontend/tsconfig.app.json` — `paths`
2. `src/platform/frontend/vite.config.ts` — `resolve.alias`（走査 3b で発見）
3. `src/vitest.config.ts` — `resolve.alias` / `setupFiles` / `coverage.exclude`
4. `src/knowledge/frontend/tsconfig.json` — `paths`
5. `templates/unit-template/frontend/tsconfig.json` — `paths`（配置前後の 2 候補とも）
6. `src/eslint.config.js` — `ignores`（生成物・カタログ）/ BFF 境界の例外 / lingui 規則の files
7. `src/eslint-suppressions.json` — 3 キーのパス
8. `src/knip.jsonc` — `platform/frontend` の `entry`
9. `src/.prettierignore` — 生成物・カタログ
10. `src/lingui.config.ts` — `catalogs[].path` と `exclude`
11. `src/orval.config.ts` — `target` と mutator の `path`
12. `.github/workflows/frontend.yml` — 2 箇所
13. `.github/workflows/pr-size.yml` — `EXCLUDES` 2 本
14. `scripts/scripts.repo.test.js` — 3 箇所
15. `src/platform/frontend/src/main.tsx` — 相対 import をエイリアスへ
16. `src/platform/frontend/src/testing/setup.ts` — 相対 import をエイリアスへ

移動に伴い**中身の相対 import を直すもの**（走査 5）:
`src/platform/frontend/src/app/routing/initialChunk.test.ts`（`../../App` → `../App`）。

**カタログ（`.po`）の `#:` 参照行は、`pnpm run i18n` で再生成して追随させる。**
`.po` は**ソースの位置を本文に持つ生成物**であり、移動すれば必ず古くなる。放置すると
`frontend.yml` の「i18n catalogs are up to date」が落ちる。**訳文は 1 つも触らない**（差分は `#:` 行のみ）。
orval 生成物は出力先が変わるだけで**中身は変わらない**（mutator の相対 import `../../orvalMutator` は
`api/` ごと動くので不変）ため、`git mv` だけで足りる。

## 追随する live 文書（完全パス／実配置のツリー図・全 12 件）

`CLAUDE.md` / `src/platform/frontend/README.md`（構成ツリー・エイリアス表） /
`templates/unit-template/README.md`（第 2 段が未了である旨） / `src/orval-bff-only.cjs`（注釈中の
grep コマンドの完全パス） / `docs/api/BFF_bff-surface.md`（1 行。同ファイルの他 4 行は公開面の名前
なので触らない） / `docs/tech/composable-component-guide.md` / `docs/tech/tech-requirements.md` /
`docs/screens/SC-10_operations-dashboard.md`（1 行。同ファイルの他 1 行は公開面の名前） /
`docs/tests/FR-22_user-notifications.md` / `docs/tests/NFR-01_performance-load-test.md` /
`docs/tests/SC-09_admin-abac-settings.md`（2 行。他 1 行は公開面の名前） /
`docs/tests/SC-16_account-settings-entry.md`。

走査 4 が見つけた `renderUnitRoute.tsx` の注釈（`platform/frontend/src/test/setup.ts`）も同時に直す。

## 受け入れ基準（実測・全項目達成）

- [x] `platform/frontend/src/` 直下がツリーの 11 区分 ＋ `main.tsx` だけになった。実測:
      `app assets components features hooks lib locales main.tsx stores testing types utils`
      （`foundation/` `test/` `App.tsx` は消えた）
- [x] `@foundation/<区分>` の import 文が **1 行も変わっていない**（knowledge・AST・雛形を含む。
      差分に `@foundation` の import 行は 1 行も無い）
- [x] `pnpm run typecheck` — 5 プロジェクトすべて Done
- [x] `pnpm run lint` — **error 0 / warning 9**（適合前と同数・同内容。すべて既存の
      `react-refresh/only-export-components`）。`pnpm run lint:templates` も緑
- [x] `pnpm run format:check` / `pnpm run format:templates` — All matched files use Prettier code style
- [x] `pnpm -w run test -- --run` — **91 ファイル / 1080 テスト全緑**（適合前と同数。
      アサーションは 1 つも変えていない。変えたのは移動に伴う import パス 2 箇所のみ）
- [x] `pnpm run build` 成功（11.46s → 9.26s。成果物は同一構成）
- [x] `node scripts/check-chunk-budget.js --require` — 初期ロード合計 **586.64 kB（床 586.64 kB）**、
      最大チャンク 586.04 kB（上限 600.00 kB）、必須チャンク 5 本。**床は動かしていない**
      （移動はモジュールの中身を変えないので、チャンクの内訳も変わらない）
- [x] `node scripts/check-static-egress.js --require src/platform/frontend/dist` — 32 ファイル、違反 0
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` — **615 件全緑**
- [x] `node scripts/check-i18n-catalogs.js` — 2 ロケール、未翻訳・fuzzy・obsolete なし。
      `pnpm run i18n` の再生成差分は `#:` 参照行のみ（各 104 行。**訳文は 0 行**）
- [x] `pnpm run codegen` の再生成差分 **0 件**（出力先だけが動いた）
- [x] 併せて緑を確認: `check-knip.js --require`（床どおり 38 件）/ `check-trace-blocks.js`（150 件）/
      `check-doc-links.js`（914 件）/ `check-cross-repo-refs.js`（2386 件）/
      `check-plan-id-qualification.js`（1984 件）/ `check-doc-type-vocabulary.js`（885 件）/
      `check-reading-budget.js`（3 集合とも 51,200 バイト内）
- [x] `node scripts/check-commit-messages.js --range e43e0a9..HEAD` 緑

## 計画書との差異

無し。本作業は計画 §ディレクトリ構成 のツリーへ実装を合わせるものであり、計画側に不足は見つかっていない。

## 親への申し送り

- **退行防止の検査器は置かない。** `src/` 直下がツリーの 11 区分に閉じていることを機械で見る検査は、
  IADR-0262 §結果 が「同型の事故が 2 回起きたら」の条件を満たさないとして見送っている。
  第 2 段でも事故は起きていない（**1 回目も無い**）ので条件は変わらない。
- **決定 A は IADR-0262 決定 1 の対応表へ第 9 行（`@foundation/ai-chat` → `components/ai-chat`）を
  足したものである**（既存 8 行は動かしていない）。**決定 B は対応表を変えていない** ——
  `@foundation/i18n` の向き先は表のまま `src/app/i18n` である。どちらも同 ADR へ日付つきで追記した。

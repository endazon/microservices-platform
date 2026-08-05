---
title: IADR-0134 SPA バンドルの分割境界 — ルート単位の遅延と、初期チャンクに残すものの線引き
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0031, SC-01, SC-02, SC-03, SC-04, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, IADR-0009, IADR-0056, IADR-0120, IADR-0121, IADR-0124, IADR-0125]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ../specs/20260805_issue-512_spa-route-code-splitting.md
  - ../tech/tech-requirements.md
---

# IADR-0134: SPA バンドルの分割境界（ルート単位の遅延と、初期チャンクに残すものの線引き）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-05
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  React 19 / Vite / TanStack Router / TanStack Query / Tailwind v4 ＋ shadcn/ui / Lingui）／
  [13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)（fixed）／
  **NFR〔性能〕**（[02_requirements](../../planning/projects/microservices-platform/02_requirements/01_requirements.md)。
  ただし**計画は初期バンドルの上限値を定めていない**——後述 §コンテキスト）
- 関連する実装 ADR:
  [IADR-0124](IADR-0124_tanstack-router-unit-composition.md)（型付きルート木・合成点。本決定はこれを**壊さない**）／
  [IADR-0121](IADR-0121_spa-stack-migration-staging.md)（移行の 5 段。本作業は第 2 段の後片付けであり段の順序を変えない）／
  [IADR-0125](IADR-0125_ui-primitives-i18n-catalog-and-storybook.md)（`@platform/ui` の公開面は `src/index.ts` の 1 ファイル）／
  [IADR-0009](IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿）／
  [IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md)（ユニット分離）／
  [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)（AST は本リポから変更できない）
- 関連する実装仕様書:
  [20260805_issue-512](../specs/20260805_issue-512_spa-route-code-splitting.md)（本決定と対で読む。**実測の生データはこちらが正**）
- 関連 issue: #512（親 #446 / #454。出所は #490 / #496 / #502 / #503 / #504 の申し送り）

## コンテキストと課題

全画面の再実装（#502 / #503 / #504）が終わり、SPA のバンドルは **632.98 kB（gzip 190.04 kB）**の
単一チャンクになった。Vite は 500 kB/チャンクの既定予算を超えたことを毎ビルド警告している。

**判定基準が計画に無い。** 計画の非機能要件は検索 p95・RAG 初回応答・取り込み速度に数値を与えるが、
**初期バンドルの上限は定めていない**。よって本決定の合否は (a) ビルドツールの既定予算（500 kB/チャンク）と
(b) 前後の実測差、の 2 つで判定する。これは issue #512 の受け入れ基準の立て方と同じである。

**難しさは「どこを切ればよいかが自明でない」ことにある。** 画面が 11 本あるので
「画面が重い」と考えるのが自然だが、それは**推測**である。#490 以降の申し送りが
「どの画面がどれだけ重いかが確定してから分割する」と繰り返してきたのは、この推測を避けるためである。

## 実測（決定の前提。**これが無ければ全部推測になる**）

**測定条件**: `origin/develop` `68d91ce` 基点 ／ Node 22.22.2 ／ pnpm 10.33.0 ／ Vite 6.4.3 ／
`rollup-plugin-visualizer` 7.0.1（`template: 'raw-data'`）。
値は断りがない限り **minify 前の rendered bytes**（比率の議論に使う）。
minify 後の値は「kB（minified）」と明記する。生の出力は[作業仕様書 §計測](../specs/20260805_issue-512_spa-route-code-splitting.md#計測実測推測で分割しないための一次資料)を正とする。

### 分割前の初期チャンク 1527.89 kB（= 632.98 kB minified）の内訳

| 区分 | rendered | 比率 |
| --- | --- | --- |
| **依存（node_modules）** | **1253.27 kB** | **82.0%** |
| 画面（knowledge の 11 feature） | 146.98 kB | 9.6% |
| AST（旧契約ユニット） | 62.09 kB | 4.1% |
| アプリ（platform/frontend/src。i18n カタログ 25.89 ＋ foundation 25.41 ほか） | 53.38 kB | 3.5% |
| `@platform/ui` のソース | 10.76 kB | 0.7% |

依存の上位: react-dom 561.39 ／ @tanstack/router-core 131.65 ／ **oidc-client-ts 121.18** ／
**tailwind-merge 102.19** ／ @tanstack/query-core 75.62 ／ **sonner 64.23** ／ @tanstack/react-router 42.49。

**この 1 枚が方針を決めた。** ルート境界で動かせるのは 9.6%（＋ 本リポから動かせない AST の 4.1%）だけである。
`tailwind-merge` が 102.19 kB を占めること（`cn()` のためだけに入っている）は推測では出てこない。

### 分割方針の候補（**全部ビルドして測った**。同一ソース状態）

| 変種 | 最大チャンク | 初期ロード JS | 同 gzip | 500 kB 警告 | 1 kB 未満の遅延チャンク |
| --- | --- | --- | --- | --- | --- |
| V0 分割なし（`68d91ce`） | 632.98 kB | 632.98 kB | 190.04 kB | **あり** | 0 |
| V1 ルート遅延のみ | 533.66 kB | 533.66 kB | 164.33 kB | **あり** | 9 |
| V2 ＋`vendor-react` | 335.72 kB | 532.41 kB | **163.63 kB** | なし | 9 |
| V3 ＋`ui` | 487.98 kB | 566.02 kB | 175.11 kB | なし | 3 |
| V4 ＋`vendor-query` | 495.19 kB | 545.86 kB | 168.52 kB | なし | 9 |
| V6 全依存を単一 `vendor` へ | 494.74 kB | 573.54 kB | 176.60 kB | なし | 10 |
| **V5 採用（3 規則）** | **274.33 kB** | 577.54 kB | 177.94 kB | なし | 3 |

- **V1（ルート遅延のみ）では警告が消えない。** issue #512 が想定した筋は成り立たなかった。
- **V6 は 494.74 kB**（上限の 1.05% 手前）、**V4 は 495.19 kB**（同 0.96% 手前）。
  依存を 1 つ足せば警告が戻る。
- **初期 gzip が最小なのは V2（163.63 kB）**で、採用した V5 はそれより **14.31 kB 重い**。

## 決定

### 決定 1: 画面（ルート）を `lazyRouteComponent` で遅延させる。合成点の型付き構成は変えない

`@knowledge` の 11 feature の `index.tsx` で、画面モジュールの静的 import を
`lazyRouteComponent(() => import('./XxxPage'), 'XxxPage')` へ置き換える。

**[[IADR-0124]] 決定 1 の性質は無傷である**——変えるのは `component` の値だけで、
ルート factory の形（`(shell: ShellRoute) => Route`）・`path`・`validateSearch`・
束ね役のタプル（`createKnowledgeRoutes`）・合成点のスプレッドはいずれも触らない。
`component` の型はルート ID・パス・検索パラメータの推論に関与しないため、
`<Link to>` の union も `useSearch({ from })` の型も保たれる（`pnpm run typecheck` で確認した）。
**束ね役・合成点に型注釈を書かない**という IADR-0124 の禁止事項も維持している。

### 決定 2: ガード（`RequireRole`）は初期チャンクに残し、**画面だけ**を遅延させる

ガードのある 6 画面（SC-05 / 06 / 07 / 09 / 10 / 11）は、`component` を
「`RequireRole` で包む素の関数」のまま残し、その中で遅延コンポーネントを描画する。
**ルートには `wrapInSuspense: true` を宣言する。**

| 案 | 権限外の利用者が画面チャンクを取得するか | Suspense 境界 | 判定 |
| --- | --- | --- | --- |
| **A. ガードを初期側に残す（採用）** | **取得しない**（ガードが先に評価される） | **要る**（`router.load()` の事前読み込みが効かないため描画時に suspend する） | 採用 |
| B. ガードごと遅延させる（`component: lazyRouteComponent(...)`） | 取得する | 不要（`preloadRouteComponents` が `.preload()` を呼ぶ） | 棄却 |

- **B が「秘匿の破れ」なわけではない。** チャンク名は初期チャンクの中に静的に現れるため、
  取得の有無で分かることは増えない（分割前は全員が全画面のコードを読んでいた）。
  それでも A を採るのは、**権限外の利用者に不要なコードを配らない**方が素直だからである。
- **`wrapInSuspense: true` を省いてはならない。** 省くと suspend がルート木の最上位まで遡り
  （`Match.js` の `rootRouteId` の Suspense が受ける）、**チャンク取得の間だけ共通シェルごと空白**になる。
  **この退行はテストでは動いてしまう**（§変異試験 M3）。

### 決定 3: 依存は `manualChunks` で 3 つに分ける（`vendor-react` / `ui` / `vendor-query`）

**ルート境界だけでは警告が消えない**（V1 = 533.66 kB）という実測から、依存側にも境界を引く。
規則は 3 つだけで、それぞれ**別の理由**を持つ。

| チャンク | 中身 | 理由 |
| --- | --- | --- |
| `vendor-react` | react / react-dom / scheduler / use-sync-external-store | **初期チャンク最大の塊**（561.39 kB rendered = 全体の 36.7%）であり、**更新頻度が最も低い**。目的は初期ロード量の削減では**ない**（静的 import なので同時に読まれる）。(1) アプリ側の更新でキャッシュが無効化されないこと、(2) 最大チャンクを 274.33 kB まで下げて 500 kB の予算に余裕を作ること |
| `ui` | `src/packages/ui` のプリミティブと、それが引き込む依存（Radix・tailwind-merge） | **全画面が使う**。放置すると Rollup が「2 つ以上の遅延チャンクが共有するモジュール」として 1 kB 未満のチャンクへ切り出す（実測: Label / Tag / Card / Input / Select / StatusBadge）。エントリが `Button` を静的 import しているため、この名前へ寄せると**初期ロードのまま 1 本に束ねられる** |
| `vendor-query` | @tanstack/react-query / query-core | 同上（実測: `useMutation` 3.10 kB / `Table` 11.41 kB の 2 本へ切り出されていた） |

**棄却した案**:

- **V6（全依存を単一 `vendor` へ）**: 494.74 kB。**上限の 1.05% 手前**であり、依存を 1 つ足せば
  警告が戻る。加えてキャッシュの粒度が最悪になる（どれか 1 つの更新で 494.74 kB 全部が無効化される）。
- **`chunkSizeWarningLimit` の引き上げ**: 警告を消すだけで何も速くならない。
- **`experimentalMinChunkSize` による自動併合**: 20000 で試すと**画面 7 本が初期チャンクへ併合された**
  （JS チャンクが 22 → 5 本）。しきい値の微調整に依存し、しかも分割の意図と逆向きに働く。

### 決定 4: 「初期チャンクに残すもの」を**テストで固定する**

分割の壊れ方は「ビルドは通る・テストも通る・警告も出ない」という三重の意味で静かである。
実測で、**共通シェル（`Layout`）を遅延側へ移しても 551 件のテストが全部緑のまま**だった（§変異試験 M5a）。
よって次の 2 本を置く。どちらも `vi.mock` の factory が**実際に import されたときにだけ評価される**
性質を使い、バンドラを起動せずにモジュールグラフの向きを見る。

| テスト | 固定するもの |
| --- | --- |
| `knowledge/…/features/routeSplitting.test.ts` | 画面 11 本が feature index の**静的 import へ戻らない**こと・遅延境界の宣言（`.preload` / `wrapInSuspense`） |
| `platform/…/foundation/routing/initialChunk.test.ts` | `Layout` / `NotFound` / `RequireAuth` / `RequireRole` / `AuthProvider` / `@platform/ui` が**初期側に残る**こと |

**チャンクの分け方そのもの（`manualChunks` の規則）は固定できていない**（§変異試験 M6 / M7 が素通り）。
機械検査を足すには CI への結線（`.github/workflows/`）が要り、本作業の権限では編集できないため、
「あるのに走らない検査」を作らないよう見送った。**別 issue として起票する**（作業仕様書 §申し送り 3）。

### 決定 5: 計測は再現可能にする（`build:analyze`）

`rollup-plugin-visualizer` を `@platform/frontend` の devDependency として持ち、
**`ANALYZE_BUNDLE=1` のときだけ**プラグインへ載せる（`pnpm --filter @platform/frontend run build:analyze`）。
既定のビルドは成果物も所要時間も変えない。出力 `dist/stats.json` は生成物であり、
`src/.gitignore` の `dist` に含まれるためコミットされない。

**「一度測って捨てる」形にしない理由**は、本決定の全ての数字がこの計測に依存しており、
次に誰かが分割を見直すときに**同じ計測を再現できなければ議論が推測に戻る**からである。

## 理由

- **決定 1 と決定 3 は同じ実測から出ているが、効き方が違う。**
  ルート遅延は**初回訪問の転送量**を減らす（632.98 → 533.66 kB minified）。
  `manualChunks` は転送量を減らさず、**チャンクの粒度**（予算の遵守と再訪時のキャッシュ）を整える。
  この 2 つを混ぜて「分割したので速くなった」と書くと、あとで数字が読めなくなる。
- **決定 2 の `wrapInSuspense` は、ライブラリの内部仕様（`preloadRouteComponents` が
  `route.options.component.preload` しか見ない）から必然的に要る。** ガードを初期側に残すという
  設計判断の代償であり、忘れると「シェルごと空白」という目に見える退行になる。
- **決定 4 の 2 本は [[IADR-0124]] 決定 5 と同じ思想である**——型で捕まえられない配線の誤りは、
  実行時テストで塞ぐ。分割境界は型にまったく現れないので、なおさら必要である。

## 結果

- 良い影響:
  - 500 kB 警告が消え、最大チャンクが **632.98 → 274.33 kB minified** になった（予算に 2 倍近い余裕）。
  - 初回訪問の JS が **632.98 → 577.54 kB（gzip 190.04 → 177.94 kB）**。
  - 再訪時は `vendor-react`（196.69 kB / gzip 61.67 kB）がアプリ更新で無効化されなくなる。
  - 画面を足すときの増分が初期チャンクに乗らない（＝ #452 以降の画面追加でバンドルが再び膨らまない）。
- 悪い影響・トレードオフ:
  - **初回訪問の削減は gzip で 12.10 kB にとどまる。** 初期チャンクの 82% が依存だからであり、
    「ルート分割すれば大きく減る」という直感は本リポでは成り立たない。
  - **JS の総量は 6.08 kB 増える**（分割の境界コスト）。初回で読まない画面のぶんは減るので割に合うが、
    「全画面を巡回する利用者」には損である。
  - **`@platform/ui` を初期側に置く判断は gzip 14.31 kB のコストを持つ**（V2 との差）。
    実体は `Tabs` が引き込む Radix であり、`Tabs` を使う画面は SC-09 だけである。
    [[IADR-0125]] 決定 1（公開面は `src/index.ts` の 1 ファイル）がある限り、barrel が初期側にあれば
    Radix も初期側に来る。**規約を変えずに直す方法は無い。**
  - **チャンク構成の退行を止める機械が無い**（決定 4 の但し書き）。
  - ルートの `component` に 2 つの形（遅延そのもの／ガード＋遅延）が混在する（決定 2）。
  - **ガード配下の画面は `findBy*` の待ち時間に動的 import が乗る。** カバレッジ計測を有効にすると
    Testing Library の既定 1000 ms に収まらないことがあり（実測: 9 回中 1 回）、
    `asyncUtilTimeout` を 5000 ms へ引き上げた。**決定 2 で B（ガードごと遅延）を採れば
    事前読み込みが効いてこの揺れは出なかった**——権限外へコードを配らないことの代償である。
- フォローアップ（詳細は[作業仕様書 §申し送り](../specs/20260805_issue-512_spa-route-code-splitting.md#申し送り)）:
  1. i18n カタログのロケール別遅延読み込み（初期に ja＋en 両方。25.89 kB rendered）。
  2. AST 3 画面（62.09 kB rendered）の遅延化。**AST が新契約（[[IADR-0124]] 決定 2）へ移るのが前提**。
  3. チャンク構成の機械検査（`.github/workflows/` の編集権限が要る）。
  4. `oidc-client-ts`（121.18 kB rendered）は移行第 3 段（#439 / ADR-0032）でライブラリごと消える見込み。
  5. `tailwind-merge`（102.19 kB rendered）の妥当性評価。

## 変異試験（「壊すと落ちる」ことの実測）

**8 件の変異（M4 は 3 変種を個別に数えて 10 行）を実測した。素通りした 3 件も載せる。**
全件の詳細は[作業仕様書 §変異試験](../specs/20260805_issue-512_spa-route-code-splitting.md#変異試験壊すと落ちることの実測)を正とする。

| # | 壊した箇所 | 落ちたもの |
| --- | --- | --- |
| M1 | SC-09 の遅延 import を静的 import へ戻す | `routeSplitting.test.ts` 2 件 |
| M2 | 遅延 import を存在しないモジュールへ向ける | `typecheck` / `build`（TS2307） |
| M3 | `wrapInSuspense: true` を外す | **`routeSplitting.test.ts` 1 件のみ**（画面テストは素通り＝「動きはする」） |
| M4a | `NotFound` の見出しを 1 文字変える | 5 件 |
| M4b | `NotFound` の `padding` を変える（見出しは変えない） | **素通り**（markup 一致は**乖離**を見るテストであり、両側が同じだけ変わる変異は原理的に検出しない） |
| M4c | **未知パス側の `NotFound` だけ**を `<div>` で包む | **是正前は素通り**。比較範囲が `NotFound` 自身の `<main>` に閉じていた。**比較範囲を Outlet の器まで広げて是正**し、落ちることを再確認した |
| M5a | **共通シェル（`Layout`）を遅延側へ移す** | **是正前は完全に素通り**（551 件全 green・警告も出ず初期チャンクはむしろ縮む）。**`initialChunk.test.ts` を足して是正**し、落ちることを再確認した |
| M5d | ガード（`RequireRole`）ごと外す | 4 件 |
| M6 | `manualChunks` の `ui` 規則を外す | **素通り**（検出手段が無い。決定 4 の但し書き） |
| M7 | `manualChunks` の `vendor-react` 規則を外す | **素通り**（index は 458.79 kB へ増えるが 500 kB は超えず警告も出ない） |
| M8 | 分割後の**遅延**チャンクへ CDN の URL を混ぜる | `check-static-egress.js`（走査対象は 4 → 20 ファイルへ増えている） |

**M4c と M5a は「本 PR が足したもの」ではなく「本 PR が見つけて塞いだ穴」である。**
どちらも変異試験をやらなければ気付かなかった——M5a に至っては、退行させたほうが
バンドルが小さく見えるため、数字を眺めても異常に見えない。

## 関連

- Supersedes: なし。
- Superseded by: なし。
- **部分的に補う**: [IADR-0124](IADR-0124_tanstack-router-unit-composition.md) 決定 1 の「ルート factory」に
  `component` の遅延化という選択肢を足す（型付き構成の要件は変わらない）。
- **採番について**: 本 ADR は当初 `IADR-0133` を採ったが、**`IADR-0134` へ改番した**（本 PR は改番時点で
  未 push であり、改番コストが最小だったため。`.claude/rules/traceability.md` §採番衝突時の改番手順）。
  経緯は次のとおりで、**`0133` は #526 の改番先として予約されている**。
  - `develop`（`9dc6c13`）には **`IADR-0132` が 2 件ある** —— `IADR-0132_openapi-required-from-csharp-nullability.md`（#520 / PR #528。先着）と
    `IADR-0132_abac-dev-seed.md`（#526。後着）が相次いでマージされ、同番のまま残った。
  - 先着尊重により**後着の #526 が `IADR-0133` へ改番する**。本 PR がその番号を空けるため `0134` を採る。
  - よって `0133` は**一時的な欠番**であり、#526 の改番が入った時点で解消する（索引 `docs/adr/README.md` にも注記した）。

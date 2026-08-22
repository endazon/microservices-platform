---
title: SPA 移行 第 4 段 — 画面機能の土台（Zustand / TanStack Table / ECharts / RHF + Zod / dayjs / react-error-boundary・SSE 右レール）
type: spec
status: done
related_ids: [NFR, ADR-0031, ADR-0032, IADR-0121, IADR-0124, IADR-0125, IADR-0126, IADR-0129, IADR-0131, IADR-0134, IADR-0135, IADR-0146, IADR-0262, IADR-0271]
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
---

# 仕様書: SPA 移行 第 4 段（画面機能の土台）

起票は #788。実装 ADR は IADR-0271。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04 / FR-05 / FR-07 / FR-08 / FR-10 / FR-11（SC-08 / SC-10 の受け入れ）
- ユースケース（UC）: UC-01（AI 回答）／ UC-02（分析依頼）／ UC-05（運用の把握）
- 画面（SC）: SC-08（AI分析ダッシュボード）／ SC-10（運用ダッシュボード）／ §共通シェル（右レール AI チャットパネル）
- 関連 ADR: ADR-0031（フロントエンド技術スタック。§採用技術一覧）／ ADR-0032（BFF セッション）／
  08_data-egress-policy（自己ホスト・外部 egress 禁止）
- 実装 ADR: IADR-0121 決定 1（5 段分割。**本作業は第 4 段**）／決定 5（右レール SSE の状態管理パターン）／
  決定 8（機械強制）／ IADR-0126 決定 1（SC-01 本文の回答はキャッシュに載せない）／
  IADR-0131 決定 4（SSE は `apiStream` が恒久的な正規の口）／ IADR-0134（チャンク予算）

## 目的・背景

計画 `13_frontend-stack` §採用技術一覧 の 6 群（クライアント状態 = Zustand ／ テーブル = TanStack Table ／
チャート = Apache ECharts ／ フォーム = React Hook Form + Zod + @hookform/resolvers + RHF DevTools ／
日付 = dayjs ／ Error Boundary = react-error-boundary）が、**pnpm workspace のどこにも存在しない**
（実測。後述 §母集合の走査）。IADR-0121 決定 1 はこれらを**第 4 段**に置いており、他の 4 段が
すべて issue を持つのに対し第 4 段だけが未起票だった。

あわせて IADR-0121 決定 5 が「実装は第 4 段」「第 4 段の着手時に再評価条件を確認する」と申し送った
**右レール AI チャットパネル（SSE）**を実装する。

## 母集合の走査

**誤りの側から引く。** 「導入済みのライブラリ」ではなく「計画が採用と定めたのに宣言が無いもの」を数える。

### 走査 1 — 追跡下の全 `package.json` に対する宣言の有無

```
$ cd src && for p in zustand echarts @tanstack/react-table react-hook-form zod \
    @hookform/resolvers @hookform/devtools dayjs react-error-boundary; do
    grep -rl "\"$p\"" --include=package.json --exclude-dir=node_modules . ; done
（出力なし）
→ 宣言のあるパッケージ: 0 件 / 9 件すべて未宣言
```

`src/ai-stock-trading`（submodule）は未取得のため走査対象から外れる。**AST は別プロジェクトであり
本リポジトリからは是正できない**（IADR-0120）。

### 走査 2 — 置き換わる既存実装（誤りの側の文字列で引く）

| 引いた文字列 | 実測 | 置換の可否 |
| --- | --- | --- |
| `class ... extends Component` | `platform/frontend/src/foundation/ui/ErrorBoundary.tsx` 1 件 | react-error-boundary へ置換する |
| `toLocaleString()` による日時整形 | 実装 3 件 —— `foundation/ui/formatDateTime.ts` ／ `sc11-config/components/ConfigViewerPage.tsx` の**同名ローカル関数** ／ `sc03-document/components/DocumentDetailPage.tsx` の `formatDate`（`i18n.locale` 付き） | dayjs へ置換し、**重複 2 件を foundation へ寄せる** |
| `<Table>`（`@platform/ui`）の直書き | SC-02 / SC-06 / SC-07 / SC-09 / SC-10 / SC-11 | **本段では SC-10 の 2 表のみ** TanStack Table へ載せる（後述 §対象範囲） |
| チャート描画（`svg` / `canvas` / `echarts`） | **0 件**（SC-08 / SC-10 とも） | SC-10 に ECharts を入れる |
| フォームの `useState` 束ね | SC-08（3 つの `useState` ＋ 手書き検証） | RHF + Zod へ載せる |
| クライアント状態のストア | **0 件**（`features/*/stores/` は全 13 feature が `.gitkeep` のみ） | 右レールの履歴・開閉を Zustand で持つ |

### 走査 3 — 検査器・規約の側の前提

- `src/eslint.config.js` は**既に Zustand 前提**（Redux 系 import を error にし、メッセージが
  「クライアント状態は Zustand」と名指す）。**裁定と検査器はあり、実体だけが無い状態**である。
- `scripts/chunk-budget-baseline.json` の `requiredChunks` は `ui` / `vendor-react` / `vendor-query` の 3 本（着手前）。
  **ECharts は初期ロードへ入れてはならない**（IADR-0134 の初期ロード ratchet）。

## 対象範囲

- 対象
  - 6 群 9 パッケージの導入（宣言先はそれぞれ最初の利用者のユニット）
  - `foundation/ui/ErrorBoundary.tsx` の react-error-boundary への置換（＋単体テストの新設）
  - `foundation/ui/formatDateTime.ts` の dayjs 化と、SC-11 のローカル重複の解消
  - **右レール AI チャットパネル**（`platform/frontend/src/foundation/ai-chat/`。Zustand ＋ `apiStream` 自前フック）
  - SC-10 の 2 表の TanStack Table 化（列定義 ＋ 並べ替え）
  - SC-10 の ECharts 2 図（利用状況の推移・上位検索語）。**遅延チャンク**として読み込む
  - SC-08 のフォームの React Hook Form + Zod 化
  - 追随: `src/eslint.config.js`（Lingui 規則の適用先）／ `src/vitest.config.ts`（カバレッジ床）／
    `platform/frontend/vite.config.ts`（`vendor-echarts` の分割規則）／ `docs/` の該当仕様書
- 対象外（理由つき）
  - **`templates/unit-template/frontend` への追随**（issue が「本段の完了時に雛形へ追随させること」と
    書いているが、`templates/` は**本作業の担当エージェントに割り当てられた編集範囲の外**である。
    雛形の `stores/` は `.gitkeep` で枠だけ在り、Zustand の実体を置く作業は残件として引き継ぐ）
  - **SC-08 への ECharts の適用**。計画 `05_screens` §SC-08 の主要素は「分析対象の指定・分析内容の入力・
    分析実行・結果パネル・出典リンク・注記」であり、**図を 1 つも要求していない**。契約
    （`AiAnswerDto`）も系列・集計値を持たない（`answer` / `citations` / `model` / `inputTokens` /
    `outputTokens`）。**描くべき数値が無いところに図を足すのは計画外の機能追加である**ため入れない。
    13_frontend-stack の備考「SC-08 / SC-10 のダッシュボードで使用」と 05_screens §SC-08 の食い違いは
    **計画側の論点**であり、環流の候補として §計画書との差異 に記す。
  - **SC-02 / SC-06 / SC-07 / SC-09 / SC-11 の表の TanStack Table 化**。本段の目的は
    「画面機能の**土台**の導入」であり、全表の載せ替えは 1 PR に収まらない（IADR-0116 規約 4）。
    土台（列定義・並べ替え・`FlexRender` と `@platform/ui` の合わせ方）を SC-10 で確立し、
    他画面は後続で追随させる。
  - `experimental_streamedQuery` の採用（後述 §再評価の結果により**不採用を維持**）
  - 第 5 段の運用系ツーリング（Knip / Plop / Renovate / Husky）= #493

## 設計

### 0. 導入するパッケージと宣言先

| パッケージ | 版 | 宣言先 | 最初の利用者 |
| --- | --- | --- | --- |
| `react-error-boundary` | ^6.1.3 | `platform/frontend`（dependencies） | `foundation/ui/ErrorBoundary.tsx` |
| `dayjs` | ^1.11.23 | `platform/frontend`（dependencies） | `foundation/ui/formatDateTime.ts` |
| `zustand` | ^5.0.15 | `platform/frontend`（dependencies） | `foundation/ai-chat/aiChatStore.ts` |
| `@tanstack/react-table` | ^9.1.2 | `knowledge/frontend`（dependencies） | SC-10 の 2 表 |
| `echarts` | ^6.1.0 | `knowledge/frontend`（dependencies） | `knowledge/frontend/src/components/EChart.tsx` |
| `react-hook-form` | ^7.86.0 | `knowledge/frontend`（dependencies） | SC-08 フォーム |
| `zod` | ^4.4.3 | `knowledge/frontend`（dependencies） | SC-08 の検証スキーマ |
| `@hookform/resolvers` | ^5.9.1 | `knowledge/frontend`（dependencies） | 同上 |
| `@hookform/devtools` | ^4.4.0 | `knowledge/frontend`（**devDependencies**） | SC-08 フォーム（**開発時のみ**） |

`@hookform/devtools` は**開発時のインスペクタ**であり、本番の初期ロードへ入れてはならない
（IADR-0134 の ratchet）。`import.meta.env.DEV` の真下で**動的 import** し、production ビルドでは
一度も読み込まれない遅延チャンクに留める。

### 1. Error Boundary（react-error-boundary）

`foundation/ui/ErrorBoundary.tsx` の**公開面（`export function ErrorBoundary({children})`）と描画結果を変えない**
——`App.tsx` の呼び出し側を触らないためである。中身だけを `react-error-boundary` の
`<ErrorBoundary FallbackComponent onError>` へ置き換える。`ApiError` は自身の `message`、
それ以外は中立文言（既存文言をそのまま使う＝カタログに差分を出さない）。

### 2. 日付（dayjs）

`formatDateTime` の**契約は変えない**（`null`/`undefined`/空 → `—`、解釈不能 → 原文のまま）。
整形だけを `dayjs(value).format('YYYY/MM/DD HH:mm')` にする。

- `toLocaleString()` からの変更は**表示の安定化**である。現行はブラウザのロケールに依存し、
  同じ値が実行環境ごとに違う文字列になるため、テストで固定できず日時列の退行を検出できない。
- 妥当性判定は `dayjs(value).isValid()` に一本化する（`Date.parse` の実装差を持ち込まない）。
- SC-11 の同名ローカル関数と SC-03 の `formatDate` を削除し `@foundation/ui/formatDateTime` を使う
  （**同じ整形規則を 3 か所に置かない**）。既存テストは整形結果の文字列を 1 件も固定していないため
  （実測: `syncState.test.ts` の `—` / 原文素通しのみ）、表示の統一で落ちるテストは無い。

### 3. 右レール AI チャットパネル（Zustand ＋ 自前フック）

配置は `platform/frontend/src/foundation/ai-chat/`（`foundation/notifications/` と同じ作法。
共通シェルに載る基盤機能であり、可変ユニットの feature ではない）。

- `aiChatStore.ts`（**Zustand**）: `open`（開閉）と `historyByScreen`（**画面別履歴**）を持つ。
  計画 05_screens §共通シェル が「**画面別履歴（画面ごとの保持／全消去）**」を名指しで要求しており、
  これは**サーバー状態ではない**（BFF に会話履歴の契約が無い）。よってクライアント状態＝Zustand の担当である。
- `useAiChatStream.ts`（**自前フック**。IADR-0121 決定 5）: `apiStream('/analysis/ask/stream')` の上に載せ、
  ストリーム中の途中状態（token 列・エラー・中断）だけを持つ。`done` の到達で 1 ターンを確定させ、
  ストアの履歴へ積む。
- `AiChatPanel.tsx`: ランチャーボタン ＋ `<aside>` の右レール。`Layout` は **1 要素の追加**だけで済ませる
  （シェルのテストを最小限しか動かさないため）。画面キーは `useRouterState` の `location.pathname`。
- 状態表示は**色だけで意味を持たせない**（送信中 = アイコン ＋ 「回答を生成中…」／
  失敗 = `Alert tone="danger"` ＝ アイコン ＋ ラベル ＋ 本文）。

#### IADR-0121 決定 5 の再評価（同決定が「第 4 段の着手時に確認する」と申し送った事項）

再評価条件は「`streamedQuery` が experimental を外し、**かつ**任意の非同期イテレータを素直に受けられる」。

```
$ node -p "require('@tanstack/react-query/package.json').version"   → 5.101.4
$ grep -n "streamedQuery" node_modules/.../@tanstack/react-query/build/modern/index.d.ts
  79: export { experimental_streamedQuery } from './_tsup-dts-rollup.js';
$ pnpm view @tanstack/react-query version                            → 5.102.0（最新）
```

**接頭辞 `experimental_` は付いたままである。条件 (1) が満たされていないため、決定 5 の
「自前フック ＋ Query は確定済み履歴のみ」を維持する。**

#### 決定 5 と IADR-0126 決定 1 の食い違いの扱い（同決定が「第 4 段で判断する」と申し送った事項）

決定 5 は「ストリーム完了時に `setQueryData` / `invalidateQueries` で Query へ引き渡す」、
IADR-0126 決定 1 は「回答を Query のキャッシュに載せない」である。**本段では引き渡し先が存在しない**
——`docs/api/openapi.yaml` の `/bff/` 配下に**会話履歴の取得口が無い**（実測）。
したがって右レールの履歴は**クライアント状態（Zustand）に閉じる**。これは決定 5 の否定ではなく
**適用条件の不成立**であり、履歴の口が契約に入った時点で決定 5 の引き渡しが有効になる。

### 4. テーブル（TanStack Table v9）

v9 は v8 と API が異なる（`useReactTable` → `useTable`、行モデルは `tableFeatures` のスロット登録）。
**v9 を採る**——`latest` が 9.1.2 であり、v8 を選ぶと Renovate（採用済み）の更新と衝突し続ける。
API はパッケージ同梱の `skills/getting-started/SKILL.md`（v9.1.2）を出典とする。

- 対象は SC-10 の `利用状況（日次）` と `検索傾向（上位語）`。
- **マークアップは `@platform/ui` の `Table` 一式のまま**（ヘッドレスの原則。`table.FlexRender` で
  セルを描き、`<table>` の意味づけとスタイルは既存プリミティブが持つ）。
- 追加する挙動は**並べ替えのみ**。ヘッダはボタンにし、`aria-sort` を付ける
  （**色だけで意味を持たせない**——並び順は矢印アイコン ＋ `aria-sort` ＋ ボタン名で表す）。

### 5. チャート（ECharts・自己ホスト・遅延）

- 入口は `knowledge/frontend/src/components/EChart.tsx`（ユニット共通コンポーネント）。
- **`echarts/core` のツリーシェイク版を使う**（`LineChart` / `BarChart` / `GridComponent` /
  `TooltipComponent` / `SVGRenderer` のみ登録）。**`SVGRenderer` を選ぶ**のは、jsdom に canvas が
  無くても単体テストが走るためと、成果物に canvas 依存を持ち込まないためである。
- **読み込みは `useEffect` 内の動的 import**。これにより echarts は初期ロードに載らない
  （IADR-0134 の ratchet を触らない）。`vite.config.ts` の `manualChunks` に `vendor-echarts` を足し、
  分割を意図として固定する。

  🔴 **［2026-08-23 追記 / #788］起草時の「`requiredChunks` には足さない」は誤りであった。**
  `requiredChunks` は「初期ロードに載る規則」の一覧ではなく「`manualChunks` が返す名前」の一覧であり、
  遅延か否かを区別しない（区別しているのは `initialTotalBytes` のほう）。自己試験が両者の**完全一致**を
  突き合わせるため、片方だけ足すと `scripts-tests` が落ちる（実測で落ちた）。
  「図を使う画面が 1 つも無いビルドで落ちる」という懸念も実在しない —— 本リポジトリのビルドは 1 つ
  （`platform/frontend` が `knowledge` の features を合成する）で、SC-10 が `EChart` を使うため
  `vendor-echarts` は必ず出力される。統括側で `scripts/chunk-budget-baseline.json` へ追加した。
- **外部 egress を持ち込まない**（08_data-egress-policy）。echarts は npm から自己ホストし、
  CDN・Web フォント・テレメトリを使わない。図のフォントは指定せず、`@platform/ui` の
  システムフォントを継承させる。
- **図は表の代替ではなく補助**である。既存の 2 表は残し、図には `role="img"` と `aria-label` を付ける。
  読み込み・失敗時は表だけが残る（**情報が失われない**）。
- option の組み立ては**純関数**（`sc10-operations/types/dashboardCharts.ts`）へ出し、単体テストで固定する。

### 6. フォーム（React Hook Form + Zod）

SC-08 の 3 入力（`instruction` 必須・`rangeQuery` 任意・`taskType` 列挙）を `useForm` ＋ `zodResolver` へ移す。

- **検証規則の実体は既存の純関数から移設する**（`isSubmittableInstruction` / `MAX_*_LENGTH`）。
  上限値は `types/analysisRange.ts` が引き続き正本であり、スキーマはそれを参照する。
- **エラー文言はスキーマに書かない。** Zod の `message` には安定した符号（`required` / `tooLong`）を置き、
  画面側で Lingui の文言へ写す。スキーマに日本語を書くと Lingui の抽出対象外になり、
  `check-i18n-catalogs.js` の網羅検査を素通りする。
- 送信可否は `formState`（`isValid` / `isSubmitting`）から導く。**既存の受け入れ（空文字・空白のみは
  送信できない／実行中は送信できない）を変えない。**

## 受け入れ基準

- [ ] 計画 §採用技術一覧 の 6 群が pnpm workspace に**宣言され、実際に使われている**（未使用宣言を作らない）
- [ ] `pnpm run typecheck` / `lint` / `format:check` / `test` が通る（**AST submodule 未取得に由来する
      既知の失敗を除く**。§検証の切り分け）
- [ ] `node scripts/check-i18n-catalogs.js` が通り、カタログ（ja / en）をコミットする
- [ ] カバレッジ床（`src/vitest.config.ts`）を割らない
- [ ] ECharts が**初期ロードへ入らない**（動的 import ＋ `vendor-echarts` の分割規則）
- [ ] 外部 CDN・Web フォント・analytics を増やさない（08_data-egress-policy）
- [ ] Redux 系の import を持ち込まない（ESLint が error）
- [ ] SSE は `apiStream` を通る（手書き HTTP クライアント・`EventSource` を使わない）
- [ ] 状態表示が色だけで意味を持たない（アイコン ＋ テキストを伴う）
- [ ] IADR-0121 決定 5 の再評価結果（`experimental_streamedQuery` 不採用の維持）を実装 ADR に記録する

## テスト方針

各テストの直前コメントに起点 ID を置く（本リポジトリの規約）。

| 対象 | 種別 | 固定すること |
| --- | --- | --- |
| `ErrorBoundary` | 描画 | 正常時は children／`ApiError` は自身の message／それ以外は中立文言／`role="alert"` |
| `formatDateTime` | 純関数 | `—` の 3 系統／解釈不能は原文／整形結果がロケールに依存しない |
| `aiChatStore` | 純ロジック | 開閉／画面別に履歴が混ざらない／画面ごとの消去／全消去 |
| `useAiChatStream` | フック | token 連結／`done` で履歴へ確定／`error` で縮退／中断は失敗にしない |
| `AiChatPanel` | 描画 | 既定は閉じている／開くと入力できる／送信中の表示が色だけでない／画面別履歴 |
| `dashboardCharts` | 純関数 | 系列の組み立て（種別ごとの分離・日付の昇順・空入力） |
| `EChart` | 描画 | 読み込み前後で `role="img"` と `aria-label`／失敗しても投げない |
| SC-10 画面 | 描画 | 並べ替えの往復と `aria-sort`／図が表を置き換えないこと |
| SC-08 画面 | 描画 | 必須の未入力で送信できない／上限超過で送信できない／正常時は依頼が飛ぶ |

## 検証の切り分け（この環境の制約）

`src/ai-stock-trading`（submodule）が未取得のため、**着手前の時点で既に**次が失敗する。
本作業に由来しない既存の環境制約である。

```
typecheck: platform/frontend  src/features/index.ts(10,52): TS2307 Cannot find module '@ai-stock-trading/features'
test:      3 files / 6 tests  （router.test.ts / Layout.test.tsx / initialChunk.test.ts）
```

`pnpm run build` も同じ理由で通らないため、**ビルド成果物を要する検査
（`check-static-egress.js --require` / `check-chunk-budget.js --require`）はこの環境では実走できない**。
引数なしで実行し、skip の警告が出ることを確認する（CI では `--require` が走る）。

## 計画書との差異

- 差異: **あり**
  1. **SC-08 のチャート**: `13_frontend-stack` §採用技術一覧 の備考は ECharts を
     「SC-08 / SC-10 のダッシュボードで使用」とするが、`05_screens` §SC-08 は主要素に図を持たず、
     BFF の契約（`AiAnswerDto`）も系列・集計値を返さない。**本段では SC-10 のみに適用**し、
     SC-08 は据え置く。計画側の 2 文書の食い違いとして環流の候補に挙げる（起票は統括側の判断に委ねる）。
  2. **右レール AI チャットパネルの設定項目**: `05_screens` §共通シェル は
     「モデル選択・フォールバックモデル・データ越境設定・画面コンテキスト添付の ON/OFF・回答の詳しさ」
     を挙げるが、`/bff/analysis/ask/stream` の要求本文は `question` と `attributeFilters` だけであり、
     **送る先が契約に無い**。**動かない設定 UI を置かない**——本段は履歴（画面別保持・全消去）と
     ストリーミングに限り、設定項目は契約が追いついた時点で足す。
  3. **RHF DevTools**: 計画は「採用」とするが、開発時のインスペクタであり本番の初期ロードへ
     入れてはならない。`import.meta.env.DEV` 下の動的 import に限定して採る（採否は満たす）。

## 未決事項

- 雛形（`templates/unit-template/frontend/src/features/sample/stores/`）への Zustand の反映は
  **本作業の編集範囲外**であり、残件として引き継ぐ。
- SC-02 / SC-06 / SC-07 / SC-09 / SC-11 の表の TanStack Table 化は後続の追随作業とする。

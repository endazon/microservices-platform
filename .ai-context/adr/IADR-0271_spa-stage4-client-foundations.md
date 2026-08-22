---
title: IADR-0271 SPA 第 4 段の土台 —— クライアント状態・表・図・フォームの採り方と、右レール SSE の帰属
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-05, FR-07, FR-08, FR-10, FR-11, SC-08, SC-10, UC-01, UC-02, UC-05, ADR-0031, ADR-0032, IADR-0121, IADR-0126, IADR-0131, IADR-0134]
author: claude（実装）
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# IADR-0271: SPA 第 4 段の土台 —— クライアント状態・表・図・フォームの採り方と、右レール SSE の帰属

- 状態: Accepted
- 日付: 2026-08-23
- 決定者: claude（実装）／起票 #788

## 起点・関連

- 関連する計画書 ID: ADR-0031（フロントエンド技術スタック §採用技術一覧）・08_data-egress-policy・05_screens §共通シェル / SC-08 / SC-10
- 関連する実装仕様書: [`.ai-context/specs/20260823_issue-788_spa-stage4-foundations.md`](../specs/20260823_issue-788_spa-stage4-foundations.md)
- 先行する実装ADR: [IADR-0121](./IADR-0121_spa-stack-migration-staging.md) 決定 1（5 段分割。**本作業は第 4 段**）・決定 5（右レール SSE の状態管理）／[IADR-0126](./IADR-0126_sse-answer-state-and-search-url-state.md) 決定 1／[IADR-0131](./IADR-0131_openapi-as-bff-contract-source.md)／[IADR-0134](./IADR-0134_spa-route-code-splitting-boundaries.md)（初期ロードの ratchet）

## コンテキストと課題

計画 §採用技術一覧 の 6 群（クライアント状態 = Zustand／表 = TanStack Table／図 = Apache ECharts／
フォーム = React Hook Form + Zod／日付 = dayjs／Error Boundary = react-error-boundary）が
**pnpm workspace のどこにも宣言されていなかった**（実測 0 件 / 9 パッケージ）。IADR-0121 決定 1 は
これらを第 4 段に置いており、他の 4 段がすべて issue を持つのに対し第 4 段だけが未起票だった。

決めるべきことは 4 つある。

1. **右レール AI チャットの履歴は誰の状態か。** サーバー状態なら TanStack Query、クライアント状態なら Zustand である。
2. **IADR-0121 決定 5 が「第 4 段の着手時に確認する」と申し送った再評価**（`streamedQuery` の採否）。
3. **決定 5 と IADR-0126 決定 1 の食い違い**（前者は「完了時に Query へ引き渡す」、後者は「回答をキャッシュに載せない」）。
4. **図（ECharts）を初期ロードへ入れない方法**と、それを機械で固定する場所。

## 決定

- **決定 1: 右レールの履歴はクライアント状態（Zustand）に閉じる。** 計画 §共通シェルが「画面別履歴（画面ごとの保持／全消去）」を名指しで要求しているが、**BFF に会話履歴の契約が無い**（`docs/api/openapi.yaml` の `/bff/` 配下に取得口が無い。実測）。サーバー状態ではないものを TanStack Query に載せない。
- **決定 2: `experimental_streamedQuery` は採らない。** IADR-0121 決定 5 の再評価条件は「experimental が外れ、**かつ**任意の非同期イテレータを素直に受けられる」だった。実測で `@tanstack/react-query` 5.101.4（最新 5.102.0）とも `experimental_` 接頭辞が付いたままであり、**条件 (1) が満たされていない**。決定 5 の「自前フック ＋ Query は確定済み履歴のみ」を維持する。
- **決定 3: 決定 5 と IADR-0126 決定 1 は衝突していない。** 決定 5 の「完了時に Query へ引き渡す」は**引き渡し先が存在して初めて意味を持つ**。本段では会話履歴の口が契約に無いため、**適用条件の不成立**であって決定 5 の否定ではない。口が契約に入った時点で決定 5 の引き渡しが有効になる。
- **決定 4: ECharts は `echarts/core` のツリーシェイク版を `useEffect` 内の動的 import で読む。** レンダラは **SVG** を採る（jsdom に canvas が無くても単体テストが走り、成果物に canvas 依存を持ち込まない）。`vite.config.ts` の `manualChunks` へ `vendor-echarts` を足して分割を意図として固定する。
- **決定 5: 図は表の代替ではなく補助である。** 既存の 2 表を残し、図には `role="img"` と `aria-label` を付ける。読み込み中・失敗時は表だけが残り、**情報が失われない**。
- **決定 6: 表は TanStack Table v9 をヘッドレスのまま使う。** マークアップは `@platform/ui` の `Table` 一式のままで、追加する挙動は並べ替えのみ。並び順は**矢印アイコン ＋ `aria-sort` ＋ ボタン名**で表す（色だけで意味を持たせない）。
- **決定 7: 検証スキーマに表示文言を書かない。** Zod の `message` には安定した符号（`required` / `tooLong`）を置き、画面側で Lingui の文言へ写す。**スキーマに日本語を書くと Lingui の抽出対象外になり、`check-i18n-catalogs.js` の網羅検査を素通りする。**
- **決定 8: `@hookform/devtools` は devDependencies に置き、`import.meta.env.DEV` の下で動的 import する。** 開発時のインスペクタであり、本番の初期ロードへ入れてはならない（IADR-0134 の ratchet）。
- **決定 9: 同じ整形規則を 3 か所に置かない。** `formatDateTime` を `dayjs` 実装へ寄せ、SC-11 の同名ローカル関数と SC-03 の `formatDate` を削除して `@foundation/ui/formatDateTime` へ集約する。**契約（`null`/`undefined`/空 → `—`、解釈不能 → 原文）は変えない。**

## 理由

- **決定 1・3**: 「サーバー状態は TanStack Query に一元化する」（IADR-0121 決定 3）は、**サーバーに状態がある**ことが前提である。契約に口が無いものを Query に載せると、キャッシュの無効化キーが実体を持たないまま増える。
- **決定 2**: 再評価条件を**そのまま測った**。「experimental が外れたか」を判断で置き換えず、パッケージの型定義とレジストリの最新版で確かめている。
- **決定 4**: 動的 import が実際の遅延を作り、`manualChunks` はそれを 1 本へ束ねて意図を固定する（規則が無いと図を使う画面ごとに echarts の断片が散る）。SVG レンダラの選択は**テストが走ること**を設計の制約として扱った結果である。
- **決定 7**: 「文言をどこに書くか」は好みではなく、**機械検査が届く場所かどうか**の問題である。

## 結果

- 良い影響: 計画 §採用技術一覧 の 6 群が実際に使われた形で入り、未使用宣言を作っていない。ECharts は初期ロードへ載らない。
- 悪い影響 / トレードオフ:
  - **TanStack Table は v9 を採った**（`latest` が 9.1.2）。v8 と API が異なる（`useReactTable` → `useTable`、行モデルは `tableFeatures` のスロット登録）ため、v8 前提の資料はそのまま使えない。v8 を選ぶと Renovate（採用済み）の更新と衝突し続けるため v9 を採った。
  - **SC-08 のチャートは据え置いた。** 計画 §採用技術一覧 の備考は ECharts を「SC-08 / SC-10 で使用」とするが、`05_screens` §SC-08 は主要素に図を持たず、BFF の契約（`AiAnswerDto`）も系列・集計値を返さない。**計画側の 2 文書の食い違い**であり、環流の候補として残す。
  - **右レールの設定項目（モデル選択・フォールバック・データ越境設定・画面コンテキスト添付・回答の詳しさ）を置いていない。** `/bff/analysis/ask/stream` の要求本文は `question` と `attributeFilters` だけで、**送る先が契約に無い**。動かない設定 UI を置かない。契約が追いついた時点で足す。
- フォローアップ:
  1. 会話履歴の取得口が契約に入ったら、決定 3 に従って決定 5 の Query 引き渡しを有効にする。
  2. SC-02 / SC-06 / SC-07 / SC-09 / SC-11 の表の TanStack Table 化（後続の追随作業）。
  3. 雛形（`templates/unit-template/frontend/`）への Zustand の反映（本作業の編集範囲外）。
  4. **初期ロードの床（`scripts/chunk-budget-baseline.json` の `initialTotalBytes`）は本作業では更新できていない。** `src/ai-stock-trading` submodule が未取得で `pnpm run build` が通らず、実測値が取れないためである。**推測で数字を書かない。** CI（`frontend.yml` の build ステップ）が実測するので、床超過が出たらその実測値で更新する。

## 関連

- Supersedes: なし
- Superseded by: なし

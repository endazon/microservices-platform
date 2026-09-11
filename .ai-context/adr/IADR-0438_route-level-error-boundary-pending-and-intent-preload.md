---
title: IADR-0438 画面単位のエラー境界・待ち表示と、リンクに触れた時点の先読み
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0031, SC-01, SC-02, SC-03, SC-04, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, SC-12, SC-17, SC-18, SC-19, SC-20, SC-21, IADR-0009, IADR-0121, IADR-0124, IADR-0134, IADR-0436, IADR-0437]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
related_specs:
  - ../specs/20260912_frontend-nocturne-and-experience-gaps.md
---

# IADR-0438: 画面単位のエラー境界・待ち表示と、リンクに触れた時点の先読み

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）／先読みの採用は利用者裁定（2026-09-12 裁定 6）

## 起点・関連

- 関連する計画書 ID:
  05_screens（計画リポ）§共通シェル §横断の表示規約
  （**画面単位のエラー境界: 1 画面のクラッシュが全体を落とさない。フォールバックには
  「再読み込み」「ホームへ戻る」を置く**）／
  ADR-0031（計画リポ）（§採用技術一覧 Error Boundary = react-error-boundary、ルータ = TanStack Router）
- 関連する実装 ADR:
  [IADR-0124](IADR-0124_tanstack-router-unit-composition.md)（型付きルート木・合成点。本決定は木の形を変えない）／
  [IADR-0121](IADR-0121_spa-stack-migration-staging.md)（第 4 段で `react-error-boundary` へ載せ替え済み）／
  [IADR-0009](IADR-0009_wiki-browsing-404-hides-existence.md)（中立文言）／
  [IADR-0436](IADR-0436_shared-ui-states-panels-and-base-ui-adoption.md)（`ErrorState` / `LoadingState`）／
  [IADR-0437](IADR-0437_query-state-three-phase-component-and-retry.md)（**画面の内側**の 3 相。本決定は画面の**外側**）／
  [IADR-0134](IADR-0134_spa-route-code-splitting-boundaries.md)（ルート単位の遅延。先読みが取りに行くのはこのチャンクである）
- 関連する実装仕様書:
  [20260912_frontend-nocturne-and-experience-gaps](../specs/20260912_frontend-nocturne-and-experience-gaps.md)
- 関連 issue: 本 PR（UI/UX 改善）。検討メモ 第 4 弾「体験の穴」(4) と次段「遷移時の先読み」（別途送付）

## コンテキストと課題

境界は `App.tsx` の 1 枚だけだった。ルート内で例外が出ると**アプリ全体**が差し替わり、
ヘッダも左レールもパンくずも消える —— 利用者は「どこに居るのか」も「どこへ行けるのか」も失う。
加えて、そのフォールバックは素の見出しと段落で、**次の一手が 1 つも無かった**
（ページを閉じる以外の選択肢が無い状態）。

待ちについても、ルート遷移中の表示が無かった。遅延ルート（`lazyRouteComponent`）のチャンク取得中は
**何も描かれない**。

先読みは未設定（TanStack Router の既定は `false`）だった。

## 検討した選択肢

| 論点 | 案 | 却下理由 |
| --- | --- | --- |
| 境界の位置 | **ルータの既定（`defaultErrorComponent`）＋ App の 1 枚を残す（採用）** | — |
| | ルートごとに `errorComponent` を書く | 21 画面に書き漏れが出る。既定の方が**漏れない** |
| | App の 1 枚だけ（現状維持） | 問題の本体である |
| | App の 1 枚を**外す** | ルータの**外**（プロバイダ・i18n・描画そのもの）の例外を誰も捕まえない |
| 復帰 | **`reset()` ＋ `router.invalidate()`（採用）** | — |
| | `reset()` のみ | **同じ失敗したデータをそのまま描き直す**——押しても何も起きないボタンになる |
| | `location.reload()` | SPA の中に居るのに全体を読み直す。入力中の他の状態も捨てる |
| 先読み | **`'intent'`（hover / focus で開始。採用）** | — |
| | `'render'` | 左レールに常時 10 数本のリンクが並ぶ。**開いただけで全画面分の取得が走る** |
| | `false`（現状維持） | 裁定 6 が先読みを求めた |

## 決定

### 決定 1: `createRouter` に `defaultErrorComponent` / `defaultPendingComponent` を置く

- `defaultErrorComponent` は**マッチしたルートの位置（＝共通シェルの本文）だけ**を差し替える。
  **シェルは残る。**
- フォールバックの中身は `ErrorState` ＋ 2 つの導線。**どちらも「利用者が実際に取れる行動」**である。
  - **再読み込み**: `reset()` で境界の状態を戻し、`router.invalidate()` でルータのデータを無効化する。
  - **ホームへ戻る**: 主入口（`ENTRY_ROUTE_PATH`）への**ルータ遷移**（`<Link>`）。
    `<a href>` で全体を読み直させない。
- 🔴 **内部事情を画面へ出さない。** `ApiError` は種別ごとの中立メッセージを自分で持つのでそれを出し、
  それ以外は既定文言へ倒す（スタックや例外メッセージをそのまま出さない）。
- 🔴 **`role="alert"` を自分で付けない**（`ErrorState` が持つ。二重に読み上げられる）。
- 中身は `app/routing/routeStates.tsx` に置く ——`createRouter` の呼び出しに JSX を混ぜない。

### 決定 2: `App.tsx` の 1 枚は**残す**。役割は Provider 層の受け皿である

ルータの外（`I18nProvider` / `QueryClientProvider` / `AuthProvider` / 描画そのもの）で起きる例外は
**ルータの境界では捕まらない**。ここへ来るときシェルは存在しないので、**素の全画面表示**で出す。

- **「ホームへ戻る」はここだけ `<a href="/">` である。** ルータの外であり、router context が
  壊れている可能性がある場所だからである（`<Link>` は使えない）。
- `onReset` で `queryClient.clear()` はしない —— ここへ来る例外はキャッシュ由来とは限らず、
  **消せば直るという根拠が無い**（消すのは確実な副作用だけ）。

### 決定 3: `resetKeys` は使わない

`react-error-boundary` の `resetKeys` は「この値が変われば自動で境界を戻す」機構である。
**使わない**。理由は 2 つ。

1. **自動で戻す条件を書ける場所が無い。** アプリ全体の境界に渡せる「変われば直っているはずの値」が
   存在しない（ルートの変化は既にルータ側の境界が扱う）。
2. **無限ループを作りやすい。** 失敗の原因が残ったまま境界が戻ると、描画 → 例外 → リセット →
   描画を繰り返す。復帰は**利用者の明示的な操作**（再読み込みボタン）に限る。

### 決定 4: `defaultPendingComponent` を置き、`defaultPendingMs` は既定のままにする

待ちは `LoadingState`。`defaultPendingMs`（既定 1000ms）は変えない ——
**速い遷移で待ち表示が一瞬光るのは、何も出ないより読みにくい。**

### 決定 5: `defaultPreload: 'intent'` を採り、`defaultPreloadStaleTime` は既定のままにする

リンクに触れた（hover / focus）時点で次の画面を読み始める。**遷移の体感はこれで決まる。**
`defaultPreloadStaleTime`（既定 30 秒）は伸ばさない —— 先読みの結果をどれだけ信じるかは
**画面ごとの `staleTime` が決めるべき値**であり、ここで一律に伸ばすと古い内容が出る。

## 理由

- 境界を 2 段にしたのは、**捕まえられる範囲が構造的に違う**からである。どちらか 1 つでは
  「シェルが消える」か「ルータ外が捕まらない」のどちらかが必ず起きる。
- 復帰導線の 2 つは、**利用者の行動**（この画面をやり直す／別の画面へ行く）に対応している。
  技術的な操作（キャッシュクリア・ハードリロード）を押させない。
- 先読みを `'intent'` にしたのは**実際のナビの形**（左レールに常時 10 数本）から導いた判断であり、
  一般論としての優劣ではない。

## 結果

- 良い影響:
  - 1 画面の失敗でシェルが残る。利用者は現在地と行き先を失わない。
  - 失敗・待ちの見た目が画面内（`QueryState`）と画面外（ルート境界）で同じ部品・同じ文言になった。
  - 遷移の体感が改善した（先読み）。
- 悪い影響・トレードオフ:
  - **境界が 2 段になったので、「どちらが出たか」が見た目では区別できない。**
    区別の手掛かりはシェルの有無だけである。
  - 先読みは**触れただけで通信が走る**。到達しないリンクにも取得が走ることがある。
  - 🔴 **既存負債: `NotFound` が自前の `<main>` を持ち、`Layout` の `<main id="main-content">` と
    入れ子になる。** 本 PR では据え置いた（`NotFound` はシェルの内と外の両方から使われており、
    片方に合わせるともう片方が `<main>` を失う）。**a11y 上は landmark の入れ子であり、
    機械検査（`jsx-a11y`）は検出しない。**
- フォローアップ:
  - `NotFound` の `<main>` 入れ子の解消（上記）。

## 関連

- Supersedes: なし。
- Superseded by: なし。

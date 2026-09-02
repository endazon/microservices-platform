---
title: IADR-0331 描画しないモジュールの置き場は「外へ何を渡すか」で決める（`utils/` と `lib/` の分界）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0031
  - ADR-0067
  - IADR-0262
  - IADR-0325
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md §ディレクトリ構成（fixed）
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md (Accepted)
  - planning:projects/microservices-platform/07_adr/ADR-0067_frontend-layer-classification-and-composition-point.md (Accepted 2026-08-30) 決定 5
---

# IADR-0331: 描画しないモジュールの置き場は「外へ何を渡すか」で決める

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: 実装（issue #1131）

## 起点・関連

- 関連する計画書 ID: NFR（保守性）、ADR-0031 §ディレクトリ構成、ADR-0067 決定 5（shared 層の内訳）
- 関連する実装仕様書: `.ai-context/specs/20260902_issue-1131_pure-modules-out-of-components.md`
- 起点 issue: #1131

## コンテキストと課題

計画 `13_frontend-stack` §ディレクトリ構成 は `components/`（共通コンポーネント）と
`hooks/ lib/ stores/ types/ utils/` を**別の区分**として列挙している。ADR-0067 決定 5 は
これらをまとめて shared 層と分類したが、**区分どうしの分界は定めていない**（層の向きだけを定めた）。

その帰結として、**描画しないモジュールが `components/` に溜まっていた。**
基点 `origin/develop` `89b4d26e` の実測（`git rev-parse --is-shallow-repository` = false）:

```console
$ git ls-files 'src/*/frontend/src/**/components/**' 'src/*/frontend/src/components/**' \
    | grep -E '\.(ts|tsx)$' | grep -vE '\.(test|spec)\.' | wc -l
49                                 ← components/ 配下の実装ファイル（分母）
$ xargs grep -L '</\|/>' < <上の一覧>
10                                 ← JSX を 1 つも持たないもの（分子）
```

🔴 **陽性対照つきである。** 同じ検索を一致側（`-l`）で回すと 39 件が出て、**39 ＋ 10 = 49** と
分母に一致する。「10 件しか引っかからなかった」ではなく「**JSX を持たないのはこの 10 件で全部**」である。

**`utils/` が空だった理由は「置くものが無いから」ではない。置くべきものが `components/` に居ただけである。**
次に純粋関数を書く人は `components/ui/` に前例を見るので、そこへ置く。`utils/` は永久に空のままになる。

### なぜ「純粋関数かどうか」だけでは決まらないか

10 件の中身は 1 種類ではなかった。

| 種類 | 例 | 数 |
| --- | --- | ---: |
| 自前の純粋関数 | `toMessages` / `formatDateTime` | 2 |
| 設定済み外部ライブラリの提供口 | `echartsBundle` / `echartsLoader`（＋ graph 面の 2 本） | 4 |
| 状態を持ち、同居する 1 部品しか使わないもの | `aiChatStore` / `useAiChatStream` / `notificationMessages` / `useNotifications` | 4 |

**原典（Bulletproof React）の `lib` は「アプリ向けに設定済みの再利用ライブラリ」、`utils` は
「共有ユーティリティ関数」である。** `formatDateTime` は dayjs を内部で使うので `lib/` 寄りにも読め、
**この曖昧さこそが「決めきれないので `components/` に置いたまま」を許してきた。**

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | **「外部ライブラリを内部で使うか」で `lib/` / `utils/` を分ける** | **却下。** dayjs / zod / clsx を 1 行でも使う関数がすべて `lib/` へ流れ、**`utils/` は空のまま**になる（本 issue の再来） |
| B | 描画しないものは一律 `utils/` へ | 却下。`echartsBundle` は「設定済みの echarts そのもの」を渡しており、原典が `lib` と名指しした形である |
| C | 消費者の数（2 つ以上の feature が使うか）で決める | 却下。**数は増減する**ので、置き場が時期によって変わる。判定が人に残る |
| **D（採用）** | **何を外へ渡すか**で決める。ライブラリを渡すなら `lib/`、自前の関数なら `utils/` | 採用。**モジュールの形だけで決まり、消費者の増減で揺れない** |

## 決定

**決定 1**: `components/` に置いてよいのは **JSX を返す部品**と、**その部品ひとつに閉じた内部**だけとする。

**決定 2**: 描画しないモジュールの行き先は、**export の形**で決める。

| 何を export するか | 行き先 |
| --- | --- |
| **設定済み／遅延読み込みした外部ライブラリそのもの**（`export { echarts }` のような形） | `lib/` |
| **自前の関数・値**（内部で外部ライブラリを使ってよい） | `utils/` |

🔴 **判定は「外部ライブラリを内部で使うか」ではなく「外部ライブラリを外へ渡すか」である。**

**決定 3**: **消費者が同ディレクトリの 1 部品に閉じており、かつ状態を持つものは、部品と同居させる。**
出すと**呼び出し元が 1 つしかない間接層が増えるだけになる。**
🔴 **ただし「同居する部品を持たない」ものは同居ではない** —— 描く側が別ディレクトリに居るなら、
それは共有された何かであり、決定 2 で行き先を決める。

**決定 4**: **`@foundation` の公開面に区分が増えたときは、面を足してよい**（改名はしない）。
`src/platform/frontend/README.md` の「エイリアス名は変えない」は**改名の禁止**である
（改名すると submodule `ai-stock-trading` と `templates/unit-template` の契約が同時に割れるため）。
🔴 **足すときは宣言 5 箇所すべてに足す**（`platform/frontend/tsconfig.app.json` /
`knowledge/frontend/tsconfig.json` / `templates/unit-template/frontend/tsconfig.json` /
`platform/frontend/vite.config.ts` / `src/vitest.config.ts`）。
**#1131 本文は「3 箇所」と書いているが、実測は 5 箇所である。**

## 理由

### 決定 2 —— 「外へ渡すか」だけがモジュールの形で決まる

原典の `lib` の実例は「設定済みの axios インスタンス」であり、**ライブラリを設定して再輸出するもの**である。
`echartsBundle.ts` は文字どおり `export { echarts }` を書いており、この形に一致する。
一方 `formatDateTime` が渡すのは `string → string` の自前関数であって、dayjs ではない。

**この基準は消費者の数に依存しない。** 案 C（消費者の数で決める）は、2 つ目の利用者が現れた瞬間に
ファイルが動くことを意味し、**置き場が時期の関数になる。**

### 決定 3 —— 「同居する部品」があるかどうかは実測できる

`@foundation/ai-chat` と `@foundation/notifications` は、いずれも外へ**コンポーネント 1 個**しか出していない
（実測: 両者を引くのは `app/Layout.tsx` の 2 行だけ）。中の 4 件は自ディレクトリ外に消費者が **0** である。

対して `echartsGraphLoader.ts` は `components/` に居ながら**同居する部品を持たない**
（描く側は `features/sc18-graph/components/GraphCanvas.tsx`）。**同居に見えて同居ではない。**
`echartsLoader.ts` のほうは `EChart.tsx` と同居しているが、**両者は互いの冒頭コメントで相手を指す
対称な設計**であり、片方だけ動かすと対称が壊れる。**同じ形（`export { echarts }`）なので決定 2 で揃える。**

### 決定 4 —— 面を足さないと、移送が改名を強いる

`formatDateTime` を `utils/` へ移すと `@foundation/ui/formatDateTime` は成立しない。
`@foundation/ui` を `src/utils` へ向け直す（＝改名に相当する意味の変更）ことは、
`ui` という名前が指すものを変えるので採らない。**足す**なら既存の面は 1 つも動かない。

## 結果

- **良い影響**
  - **`utils/` に実体が入る。** 次に純粋関数を書く人が見る前例が `components/ui/` ではなくなる。
  - **判定が人に残らない。** 「JSX を返すか」「ライブラリを外へ渡すか」「同居する部品があるか」は
    いずれもファイルを見れば決まる。
  - **`@foundation` の 9 面は 1 つも動いていない。** submodule（`ai-stock-trading`）が使う 5 面
    （`api/ApiError` 13 / `api/apiClient` 7 / `auth/AuthContext` 6 / `routing/featureRegistry` 4 /
    `auth/RequireRole` 3。実測）に触れていないので、波及は無い。
- **悪い影響・トレードオフ**
  - **エイリアスの宣言が 5 箇所へ 2 行ずつ増える**（10 面目）。片方だけ足すと
    「型検査は通るがビルド／テストだけ壊れる」形になるのは従来どおりで、増えるのは面の数だけである。
  - **`knowledge/frontend/src/utils/` は依然として空**である。knowledge 側に「自前の純粋関数」が
    1 つも無いためであり、**枠の可否は planning#510 の裁定待ち**（[IADR-0325](./IADR-0325_unit-level-scaffolding-frames-await-arbitration.md)）。本 IADR は枠の可否を決めない。
- **フォローアップ**
  - 🔴 **機械検査（ESLint 規則）は入れない。** 運用規約は「**同型の事故が 2 回起きたら**検査器を足す
    （1 回目は記録に留める）」と定める。`components/` への混入を**是正した**のは本 IADR が 1 回目である
    （#1122 は「空枠を残すか消すか」の issue であり、**実体の移送は明示的に対象外**としていた）。
    **2 回目が起きたら `import/no-restricted-paths` と同じブロックへ規則を足す。**
  - `aiChatStore.ts` を `stores/` へ出すかは**別の問い**（共有コンポーネント群の内部をどこまで外へ出すか）
    であり、本 IADR は決めない。実測で自ディレクトリ外の消費者が 0 なので、現時点では出さない。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連 IADR: [IADR-0262](./IADR-0262_bulletproof-react-directory-conformance.md)（決定 1 = エイリアス名を変えない。**覆さない**。本 IADR は足すだけである）、[IADR-0325](./IADR-0325_unit-level-scaffolding-frames-await-arbitration.md)（空枠の可否。本 IADR は枠を決めない）、[IADR-0311](./IADR-0311_layer-zone-enforcement-and-alias-resolution.md)（層の向きの機械強制）

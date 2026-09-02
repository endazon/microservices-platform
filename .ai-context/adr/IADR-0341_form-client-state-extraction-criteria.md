---
title: IADR-0341 フォームのクライアント状態を `hooks/` へ出す条件は「共有されるか、画面を描かずに固定したい規則を含むか」
type: impl-adr
status: Accepted
related_ids:
  - SC-09
  - SC-12
  - SC-17
  - ADR-0031
  - IADR-0309
  - IADR-0333
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md §ディレクトリ構成（fixed）
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md (Accepted) §採用技術一覧
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-09 / §SC-12 / §SC-17
---

# IADR-0341: フォームのクライアント状態を `hooks/` へ出す条件

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: 実装（issue #1123）

## 起点・関連

- 関連する計画書 ID: SC-09 / SC-12 / SC-17、ADR-0031 §採用技術一覧（サーバー状態 = TanStack Query）
- 関連する実装仕様書: `.ai-context/specs/20260902_issue-1123_form-state-to-hooks.md`
- 起点 issue: #1123

## コンテキストと課題

[IADR-0309](./IADR-0309_feature-internal-split-substance-over-scaffolding.md) 決定 1 は
`hooks/` を「feature 固有のクライアント状態」と定め、サーバー状態は `api/` の TanStack Query が持つとした。
**規則は在るが、実体は `components/` に残っていた。**

基点 `origin/develop` `4d0f80e8` の実測（`git rev-parse --is-shallow-repository` = false）:

```console
$ grep -rlE "useState|useReducer" --include=*.ts --include=*.tsx \
    src/knowledge/frontend/src src/platform/frontend/src | grep -vE '\.(test|spec)\.' | wc -l
28
```

🔴 **陽性対照つきである。** 否定側（`grep -L`）が 185 件で、**28 ＋ 185 = 213** が非テスト実装の全数と
一致する。うち **22 件**が `hooks/` にも `api/` にも `lib/` にも居ない。最大は
`sc12-mcp-clients` の 1 画面で **11 本**（登録 7 ＋ 編集 4）である。

### 「全部出す」も「全部残す」も採れない

**全部出すと、呼び出し元が 1 つしかない間接層が増える。** 開閉フラグ 1 本のためにファイルが 1 つ増え、
読み手は「画面 → フック → 画面」を往復することになる。#1123 本文もこれを禁じている。

**全部残すと、規則が画面テストにしか現れない。** 実測すると、いま画面テストが確かめているのは
「どの要素がどう見えるか」であって、遷移の規則ではない —— 例えば
「属性キーを選び直すと値が消える」「登録成功後も種別は残す」「任意属性を空へ戻すとキーごと落ちる」は、
**画面全体を描かないと踏めず、実際いくつかは踏まれていなかった。**

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | `useState` の本数で決める（例: 3 本以上なら出す） | **却下。** 本数は規則の有無と関係しない。開閉フラグ 3 本は出す価値が無く、規則を含む 1 本は出す価値がある |
| B | 「フォーム」なら一律に出す | 却下。1 項目の登録欄（`TagDictionaryPanel` の `name`）までファイルが増える |
| C | 共通の「フォームフック」を 1 本作り、全画面がそれを設定して使う | **却下。** 呼び出し側で分岐する 1 つのフックになり、2 本のままより読めなくなる（下記 §理由） |
| **D（採用）** | **①共有されるか、②画面を描かずに固定したい規則を含むか** で決める | 採用。**規則の有無で決まるので、本数や見た目で揺れない** |

## 決定

**決定 1**: `features/<sc>/hooks/` へ出すのは、次のどちらかを満たすものだけとする。

1. **2 つ以上のコンポーネントが共有する**クライアント状態
2. **画面を描かずに固定したい規則**（遷移・検証）を含むもの

**決定 2**: フックに入れてよいのは**下書き（まだ送っていないクライアント状態）とその遷移**だけとする。
**ミューテーション（TanStack Query）はフックへ入れない** —— 入れるとサーバー状態との境界が消え、
**画面もサーバーも無しに遷移を試験できなくなる**（フックを作った理由がそれである）。
入力規則そのものの純関数は従来どおり `types/` に置く（[[IADR-0129]] 決定 6）。

**決定 3**: **上のどちらも満たさないものは画面に残し、残した理由を書く。**
本 issue で残したのは 3 つ（`sc17` の絞り込み 2 本・`sc09` `TagDictionaryPanel` の登録欄 1 本・
同 `TagRow` の行内改名 2 本）である。

**決定 4**: 🔴 **同じ feature の中でも、入力の形が違うフォームは共通化しない。**
`sc09` の 2 パネルは「辞書項目を新規作成する（自由入力）」と「既存の辞書項目と許可値を選ぶ（Select）」で
**別物**であり、`useAttributeDraft` / `usePolicyDraft` の 2 本に分けたままにする。

## 理由

### 決定 1 —— 「規則を含むか」だけがファイルを見て決まる

案 A（本数）は**規則の有無と相関しない**。実測でも、出す価値がある 5 本の状態数は 4〜7 とばらけており、
出さないと決めた `sc17` の絞り込みは 2 本だが**規則を 1 つも持たない**（`useState` 2 本と、
既に純関数として在る `filterUsers()` の呼び出しだけである）。

**「画面を描かずに固定したい規則」は、いま画面テストが偶然通しているだけのものを指す。** 出した 5 本が
持ち込んだ規則は 34 件の単体テストになり、そのうち**画面テストが 1 件も踏んでいなかった枝**が含まれる
（属性キーの選び直しで値が消えること／登録・保存の成功後に種別とアクションが残ること／
任意属性を空へ戻すとキーごと落ちること）。

### 決定 4 —— #1123 本文の「同じ形が 2 パネルに重複」は成り立たない

issue は sc09 について「**属性 key/value の入力と検証**という同じ形が 2 パネルに重複している」と述べる。
**実測すると同じ形ではない。**

| パネル | 状態 | 何をする入力か |
| --- | --- | --- |
| `AttributeDictionaryPanel` | `key` / `label` / `allowedValues` / `required` / `scope` | 辞書項目**そのものを新規作成する**（キーは自由入力・許可値はカンマ区切り文字列） |
| `PolicyEditorPanel` | `name` / `action` / `conditions` / `attributeKey` / `conditionValue` | **既存の辞書項目を選び、その許可値のひとつを選ぶ**（どちらも Select） |

**共有できる語彙は既に `types/abacVocabulary.ts` に在る**（`parseAllowedValues` / `buildConditions` 等）。
残っているのは遷移だけで、その遷移は 2 パネルで別物である。

🔴 **sc09 で本当に 3 パネルへ重複しているのは別の形である** —— `Object.values(actions)` で
ミューテーション群を辿り、新しい操作の開始時に全部 `reset()` する（[[IADR-0127]] 決定 7）という形である。
**ただしこれは sc09 固有ではない** —— 同じ形が 8 コンポーネント / 6 feature に及ぶ（実測）。

```console
$ grep -rn "Object.values(actions)" --include=*.tsx src/knowledge/frontend/src | wc -l
8
```

feature をまたぐ共有は `hooks/`（feature 固有）ではなく `lib/` の話であり、**#1123 の宣言ファイル領域
（`sc09-admin-abac/**` / `sc12-mcp-clients/**` / `sc17-users/**`）を超える。** 本 IADR は出さず、記録に留める。

## 結果

- **良い影響**
  - **3 feature の `hooks/` に実体が入る**（5 本）。空枠ではない。
  - **規則が画面から独立して固定される**（34 件の単体テスト）。画面を描かないので実行は速く、
    「どの要素がどう見えるか」を変えても落ちない。
  - 🔴 **`eslint-disable` が 2 件減った。** 一覧の列定義（`useMemo`）が呼ぶ関数
    （`open` / `start`）を**フック側で `useCallback` により固定した**ので、
    「安定した関数である」が注記ではなく**コードの性質**になり、依存配列にそのまま置ける。
    **フックから出す関数がメモ化された場所で呼ばれるなら参照を固定する** —— さもないと
    抑制コメントが増える（最初の実装がそうなり、warning が 4 件増えた）。
  - **画面テストを 1 行も変えていない。** 振る舞いを変えていないことの証拠である
    （`AdminAbacSettingsPage.test.tsx` 36 件 / `McpClientManagementPage.test.tsx` 12 件 /
    `UserAccountManagementPage.test.tsx` 13 件、いずれも緑）。
- **悪い影響・トレードオフ**
  - **画面からフックへ 1 段の間接が入る**（5 箇所）。読み手は下書きの実体を別ファイルで読む。
    **決定 3 でこれを最小化している** —— 規則を持たないものは出さない。
  - **フックが返すオブジェクトは大きい**（`sc12` の登録フォームは 18 の面を返す）。分割すると
    呼び出し側が 2 つのフックを束ねることになり、**同じ下書きが 2 箇所へ分かれる**ので採らない。
- **フォローアップ**
  1. **宣言ファイル領域の外に同型が 3 件残っている**（実測）——
     `sc05-documents/components/DocumentForm.tsx`（5）・
     `sc06-datasources/components/DataSourceForm.tsx`（6）・
     `DataSourceAttributesForm.tsx`（3）。**別 issue で扱う**（並列判定を壊さないため本 PR では触らない）。
  2. `Object.values(actions)` のミューテーション整理（8 コンポーネント / 6 feature）は
     `lib/` 行きの候補として記録に留める。**同型の事故が起きたわけではないので、いま検査器は足さない。**

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連 IADR: [IADR-0309](./IADR-0309_feature-internal-split-substance-over-scaffolding.md)（決定 1 = `hooks/` の定義。**前提として扱う**）、[IADR-0333](./IADR-0333_non-rendering-module-placement.md)（`components/` に置いてよいものの基準。本 IADR はその feature 内部版である）、[IADR-0129](./IADR-0129_sc09-11-admin-ops-screen-composition.md)（入力規則の純関数を `types/` に置く）

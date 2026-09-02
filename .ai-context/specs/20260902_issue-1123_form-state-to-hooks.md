---
title: 作業仕様書 — 登録・編集フォームのクライアント状態を hooks/ へ出し、画面を描かずに規則を固定する（#1123）
type: spec
status: done
related_ids:
  - SC-09
  - SC-12
  - SC-17
  - ADR-0031
  - IADR-0309
  - IADR-0341
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md §ディレクトリ構成（fixed。planning#378 → planning#445）
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md (Accepted) §採用技術一覧（サーバー状態 = TanStack Query / クライアント状態 = Zustand）
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-09 / §SC-12 / §SC-17
related_specs:
  - ./20260902_issue-1131_pure-modules-out-of-components.md
---

# 作業仕様書: 登録・編集フォームのクライアント状態を `hooks/` へ（#1123）

起点: 実装 issue #1123（#1100 の作業で feature ごとに中身を確かめたときに検出したもの）。

## 1. 母集合（着手時に自分で引き直した）

基点 `origin/develop` = **`4d0f80e8`**（`git rev-parse --is-shallow-repository` = **false**）。
🔴 **#1123 本文の数え（sc12=12 / sc17=7 / sc09 の 2 パネル=6 ずつ）は転記しない。**
本文は `grep -c useState` の**行数**を数えており、**`import { useState }` の 1 行を含む**。
実際のフック呼び出し数は 1 つずつ少ない。母集合は下記のとおり自分で引いた。

### 軸 1 — クライアント状態を持つ非テスト実装ファイル（両ユニット frontend 全体）

```console
$ grep -rlE "useState|useReducer" --include=*.ts --include=*.tsx \
    src/knowledge/frontend/src src/platform/frontend/src | grep -vE '\.(test|spec)\.' | wc -l
28
$ grep -rLE "useState|useReducer" ... | grep -vE '\.(test|spec)\.' | wc -l
185
```

🔴 **陽性対照つきである。** 同じ走査の否定側が 185 件あり、**28 ＋ 185 = 213** が非テスト実装の全数と
一致する。「28 件しか引っかからなかった」ではなく「**クライアント状態を持つのはこの 28 件で全部**」である。

### 軸 2 — そのうち `hooks/` にも `api/` にも `lib/` にも居ないもの（＝本 issue の対象母集合）

**22 件**。呼び出し数の多い順（`useState(` / `useState<` / `useReducer(` を数えた。import 行は含まない）:

| ファイル | 数 | 種別 | 判断 |
| --- | ---: | --- | --- |
| `sc12-mcp-clients/components/McpClientManagementPage.tsx` | **11** | 登録フォーム ＋ 編集フォーム | **出す（2 本）** |
| `sc17-users/components/UserAccountManagementPage.tsx` | **6** | 絞り込み ＋ 編集ドラフト | **一部出す（1 本）** |
| `sc06-datasources/components/DataSourceForm.tsx` | 6 | 登録フォーム | 宣言領域外（後述） |
| `sc09-admin-abac/components/PolicyEditorPanel.tsx` | **5** | 登録フォーム | **出す（1 本）** |
| `sc09-admin-abac/components/AttributeDictionaryPanel.tsx` | **5** | 登録フォーム | **出す（1 本）** |
| `sc05-documents/components/DocumentForm.tsx` | 5 | 登録フォーム | 宣言領域外 |
| `sc19-private-notes/components/PrivateNotesPage.tsx` | 4 | 画面状態 | 宣言領域外 |
| `sc07-conversions/components/ConversionJobsPage.tsx` | 4 | 画面状態 | 宣言領域外 |
| `sc09-admin-abac/components/TagDictionaryPanel.tsx` | **3** | 1 項目の登録 ＋ 行内改名 | **出さない**（§2） |
| `sc06-datasources/components/DataSourceManagementPage.tsx` | 3 | 画面状態 | 宣言領域外 |
| `sc06-datasources/components/DataSourceAttributesForm.tsx` | 3 | 登録フォーム | 宣言領域外 |
| `sc02-results/components/SearchResultsPage.tsx` | 3 | 画面状態 | 宣言領域外 |
| `sc20-obsidian-settings` / `sc07` / `sc05` / `sc01` の 4 件 | 2 | 画面状態 | 宣言領域外 |
| `platform` 3 件（`app/Layout.tsx` / `AiChatPanel` / `NotificationBell`）と `sc10` / `sc08` | 1 | 開閉・タブ | 宣言領域外・単一 |

🔴 **本 issue の宣言ファイル領域は `sc09-admin-abac/**` / `sc12-mcp-clients/**` / `sc17-users/**` の 3 つだけ**である
（並列作業の非重複判定に使われる）。**`sc05-documents/components/DocumentForm.tsx`（5）と
`sc06-datasources/components/DataSourceForm.tsx`（6）・`DataSourceAttributesForm.tsx`（3）も同型の
登録フォームだが、宣言領域の外なので本 PR では触らない**（触ると並列判定が壊れる）。**積み残しとして報告する。**

### 軸 3 — issue 本文の「同じ形が 2 パネルに重複している」は、実測すると成り立たない

本文は sc09 について「**属性 key/value の入力と検証**という同じ形が 2 パネルに重複している」と述べる。
2 パネルの入力を並べると、**同じ形ではない**。

| パネル | 状態 | 何をする入力か |
| --- | --- | --- |
| `AttributeDictionaryPanel` | `key` / `label` / `allowedValues` / `required` / `scope` | **辞書項目そのものを新規作成する**（キーは自由入力・許可値はカンマ区切り文字列） |
| `PolicyEditorPanel` | `name` / `action` / `conditions` / `attributeKey` / `conditionValue` | **既存の辞書項目を選び、その許可値のひとつを選ぶ**（どちらも Select。自由入力ではない） |

**共有できる語彙は既に `types/abacVocabulary.ts` に在る**（`ATTRIBUTE_SCOPES` / `parseAllowedValues` /
`buildConditions` 等の純関数）。**残っているのは状態遷移だけで、その遷移は 2 パネルで別物である。**
よって **共通フックは作らない**（無理に束ねると「呼び出し側で分岐する 1 つのフック」になり、
2 本のままより読めなくなる）。

🔴 **sc09 で本当に 3 パネルへ重複しているのは別の形である** —— `Object.values(actions)` で
ミューテーション群を辿り、新しい操作の開始時に全部 `reset()` する（[[IADR-0127]] 決定 7）という形が
`AttributeDictionaryPanel` / `PolicyEditorPanel` / `TagDictionaryPanel` の 3 つに在る。
**ただしこれは sc09 固有ではない** —— 同じ形が `sc05` / `sc06` / `sc07` / `sc19` / `sc20` にもあり、
**計 8 コンポーネント / 6 feature に及ぶ**（実測）。

```console
$ grep -rn "Object.values(actions)" --include=*.tsx src/knowledge/frontend/src | grep -c .
8
```

feature をまたぐ共有は `hooks/`（feature 固有）ではなく `lib/` の話であり、**6 feature へ手を入れる
ことになるので宣言ファイル領域を超える**。**本 PR では出さず、記録に留める。**

## 2. 出すもの・出さないもの

**基準**（#1123 本文の「判断すること」に従う）: 切り出す価値があるのは
**①「複数コンポーネントが共有する」** か **②「画面を描かずに固定したい規則を含む」** ものである。

### 出す（5 本）

| 置き場 | フック | 移す状態 | 画面を描かずに固定する規則 |
| --- | --- | ---: | --- |
| `sc12-mcp-clients/hooks/useMcpClientRegistrationForm.ts` | 登録フォーム | 7 | 属性キーを選び直すと値が消える／同じキーは後勝ちで 1 件だけ積む／キーか値が空なら積まない／`validateRegistration` の結果を保持し空のときだけ送れる／送信成功で ID・表示名・属性は消えるが**種別は残る** |
| `sc12-mcp-clients/hooks/useMcpClientAttributeEditor.ts` | 属性の差し替え | 4 | 編集開始時に**現在の属性を読み込む**（差し替えは置換であって追加ではない）／同じ 3 規則（後勝ち・空は積まない・キー切替で値が消える）／**1 件も無ければ保存できない** |
| `sc17-users/hooks/useUserPermissionEditor.ts` | 権限編集ドラフト | 4 | 対象が変わったときだけ下書きを引き直す（入力途中を毎再描画で潰さない）／ロールはトグル／**任意属性を空へ戻すとキーごと落ちる**（差し替えなので送らなければ外れる）／`validateAssignment` の結果を保持 |
| `sc09-admin-abac/hooks/usePolicyDraft.ts` | ポリシー登録 | 5 | 属性を選び直すと条件の値が消える／条件は**属性定義の scope** で積む（フォームの値ではない）／保存と検証で**同じ本文**を作る／名前が空なら送れない／保存成功で名前と条件は消えるが**アクションは残る** |
| `sc09-admin-abac/hooks/useAttributeDraft.ts` | 属性辞書登録 | 5 | 本文はキー・ラベルを trim し許可値を `parseAllowedValues` で畳む／キーが空なら送れない／作成成功でキー・ラベル・許可値は消えるが**必須・スコープは残る** |

**いずれも「規則」を持つ。** 上の各行の右列は、いまは画面テストが**画面全体を描いて**確かめている
（あるいは確かめていない）ものである。

### 出さない（同じ 3 feature の中で）

| 残すもの | 数 | 理由 |
| --- | ---: | --- |
| `sc17` の `departmentFilter` / `roleFilter` | 2 | **規則を持たない。** `useState` 2 本と、既に純関数として在る `filterUsers()` の呼び出しだけである。出すと**呼び出し元が 1 つしかない間接層**が増える（#1123 本文が禁じている形そのもの） |
| `sc09` `TagDictionaryPanel` の `name` | 1 | 1 項目の登録欄。空判定は `create` ボタンの `disabled` に閉じており、遷移が無い |
| `sc09` `TagRow` の `editing` / `draft` | 2 | **行に閉じた開閉と下書き**。行部品の中で完結し、パネルからも他の行からも見えない |

## 3. 設計

### 置き場と依存の向き

`features/<sc>/hooks/` に置く（計画 §ディレクトリ構成 の feature 内部 6 分割。[[IADR-0309]] 決定 1）。
フックは同じ feature の `types/`（純関数の語彙）と `@foundation/api` の生成型だけを引く。
**`api/` の TanStack Query は引かない** —— サーバー状態はあちらが持ち、フックが持つのは
「画面が編集中の下書き」だけである（ADR-0031）。**ミューテーションはフックへ入れない**
（入れるとサーバー状態とクライアント状態の境界が消え、画面を描かずに試験できなくなる）。

### 振る舞いを変えない

**移送であって設計変更ではない。** 既存の画面テスト（`AdminAbacSettingsPage.test.tsx` 821 行 /
`McpClientManagementPage.test.tsx` 405 行 / `UserAccountManagementPage.test.tsx` 384 行）は
**1 行も変えずに緑のままであること**を条件とする —— 変えないといけないなら振る舞いを変えている。

## 4. 受け入れ基準

- [x] 3 feature の `hooks/` に**実体**が置かれている（空枠は作らない）
- [x] 切り出さなかった状態の理由が PR 本文にある（§2）
- [x] 切り出したフックに、**画面を描かずに**状態遷移と検証規則を固定する単体テストがある
- [x] 既存の画面テストが 1 件も落ちない（**テスト本体を 1 行も変えない**）
- [x] `pnpm run lint` / `typecheck` / `test` / `build` / `format:check` がすべて成功する
- [x] `node scripts/check-route-manifest.js` / `node scripts/check-chunk-budget.js` が成功する
- [x] 移送後、3 feature の `components/` に残るクライアント状態が §2 で残すと決めた 3 群
      （合計 **5 本**）だけになる —— **陽性対照: 移送前の 30 本 ＝ 残した 5 本 ＋ `hooks/` の 25 本**
      で、増減が無いこと（新しい状態を発明していない／落としていない）まで確かめる

## 5. テスト方針

`renderHook`（`@testing-library/react`。既に `sc05-documents/api/useDocumentAdmin.test.tsx` で使っている）で
**DOM を 1 つも描かずに**遷移を回す。1 フック 1 ファイル（`hooks/<name>.test.ts`）。
固定するのは §2 の表の右列 —— **いま画面テストが偶然通しているだけの規則**である。

## 6. 計画書との差異

- 差異: なし。§ディレクトリ構成 の `hooks/` へ実装を寄せる作業であり、計画を動かさない。

## 7. 未決事項

- なし。#1123 の「判断すること」（何を出して何を残すか）は §2 で決めた。

## 8. 検証（実走。Node 22 / submodule init 済み）

**基点は測定時 `4d0f80e8`、着手後に `origin/develop` `d561509d` を取り込んだ**（#1150）。
取り込んだ変更は **backend の `Tests/` 配置だけ**でフロントに 1 行も触れないため、§1 の母集合は測り直していない。

| 検査 | 結果 |
| --- | --- |
| `pnpm run typecheck` | OK（5 workspace すべて） |
| `pnpm run lint` | OK（0 errors / **9 warnings**。全件 `react-refresh/only-export-components` で、**本 PR が触ったファイルは 1 件も含まない**） |
| `pnpm run format:check` | OK |
| `pnpm run test` | **107 ファイル / 1306 件 緑**（develop 基準線 1272 ＋ 本 PR の 34 件） |
| `pnpm run build` | OK |
| `node scripts/check-chunk-budget.js` | OK。**初期ロード 617.16 kB（床 617.16 kB）＝ 1 バイトも動いていない。** baseline の更新は不要 |
| `node scripts/check-route-manifest.js` | OK（画面 17 件） |
| `node scripts/check-static-egress.js --require <dist>` | OK（39 ファイル） |
| `node scripts/check-doc-links.js` | OK（1071 件） |
| `node scripts/check-trace-blocks.js` | OK（166 件） |
| `node scripts/gen-knowledge-graph.js --check` | OK |
| `node scripts/check-i18n-catalogs.js` | OK |
| `node scripts/check-adr-numbering.js` | 🔴 **欠番 6 件（IADR-0335〜0340）**。採番は上位から**仮置きで渡された `IADR-0341`** であり、0335〜0340 は並行 PR が保持している。**マージ時に改番して埋める** |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 🔴 上と同じ欠番で**中断**する。**陽性対照つきで切り分けた** —— 同じ本文を `IADR-0335` へ仮改番して回すと **674 件すべて緑**（＝落ちているのは採番だけで、他に赤は無い）。確認後 `0341` へ戻した |

### 移送前後の突き合わせ（受け入れ基準の最後の 1 行）

移送後、3 feature の `components/` に残るクライアント状態は `TagDictionaryPanel.tsx` の **3 本**と
`UserAccountManagementPage.tsx`（絞り込み）の **2 本**だけである。`hooks/` の 5 本は
`useAttributeDraft` 5 ／ `usePolicyDraft` 5 ／ `useMcpClientAttributeEditor` 4 ／
`useMcpClientRegistrationForm` 7 ／ `useUserPermissionEditor` 4 の計 **25 本**を持つ。

🔴 **移送前 30 本（5 ファイル）＝ 残した 5 本（2 ファイル）＋ `hooks/` の 25 本（5 ファイル）。**
合計が一致するので、**状態を落としても発明してもいない**と言える。

### 途中で直した 1 件（lint の退行）

最初の実装では列定義（`useMemo`）がフックの関数を直接呼んでおり、
`react-hooks/exhaustive-deps` の warning が **4 件増えた**（`editor` が毎描画で新しい参照になるため）。
🔴 **`eslint-disable` で黙らせずに、フック側の `open` / `start` を `useCallback` で固定して依存に入れた。**
結果として**元々あった `eslint-disable` 2 件も不要になり**、warning は develop と同じ 9 件へ戻っている
（「安定した関数である」という主張が、注記ではなくコードの性質になった）。

---
title: 作業仕様書 — 描画しないモジュールを components/ から出し、utils/ に実体を与える（#1131）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0031
  - ADR-0067
  - IADR-0262
  - IADR-0331
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md §ディレクトリ構成（fixed。planning#378 → planning#445）
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md (Accepted)
  - planning:projects/microservices-platform/07_adr/ADR-0067_frontend-layer-classification-and-composition-point.md (Accepted 2026-08-30) 決定 5
related_specs:
  - ./20260831_issue-1122_unit-level-scaffolding-frames.md
---

# 作業仕様書: 描画しないモジュールの置き場（#1131）

起点: 実装 issue #1131（#1122 の作業で 24 件の空枠それぞれの「なぜ空か」を確かめたときに検出したもの）。

## 1. 母集合（着手時に自分で引き直した）

基点 `origin/develop` = **`89b4d26e`**（`git rev-parse --is-shallow-repository` = **false**）。
🔴 **#1131 本文の数え（`components/ui/` の 2 件 ＋ knowledge の echarts 4 件 = 6 件）は転記しない。**
本文は `components/ui/` と knowledge 直下しか見ておらず、**`components/ai-chat/` と
`components/notifications/` を走査していない**。母集合は下記のとおり自分で引いた。

### 軸 1 — `components/` 配下の非テスト実装ファイル（分母）

```console
$ git ls-files 'src/*/frontend/src/**/components/**' 'src/*/frontend/src/components/**' \
    | grep -E '\.(ts|tsx)$' | grep -vE '\.(test|spec)\.' | wc -l
49
```

feature 内部の `components/`（`features/<sc>/components/`）も分母に含める。**issue は共有側しか
挙げていないが、「描画しないものが `components/` に居る」という問いは feature 内部にも同じ形で立つ。**

### 軸 2 — そのうち JSX を 1 つも持たないもの（分子）

```console
$ xargs grep -L '</\|/>' < <軸 1 の一覧>
src/knowledge/frontend/src/components/echartsBundle.ts
src/knowledge/frontend/src/components/echartsGraphBundle.ts
src/knowledge/frontend/src/components/echartsGraphLoader.ts
src/knowledge/frontend/src/components/echartsLoader.ts
src/platform/frontend/src/components/ai-chat/aiChatStore.ts
src/platform/frontend/src/components/ai-chat/useAiChatStream.ts
src/platform/frontend/src/components/notifications/notificationMessages.ts
src/platform/frontend/src/components/notifications/useNotifications.ts
src/platform/frontend/src/components/ui/apiErrors.ts
src/platform/frontend/src/components/ui/formatDateTime.ts
```

🔴 **陽性対照つきの 10 件である。** 同じ検索を `-l`（一致した側）で回すと **39 件**が出る。
**39 ＋ 10 = 49** で軸 1 の分母と一致するので、「10 件しか引っかからなかった」ではなく
**「JSX を持たないのはこの 10 件で全部である」**と言える。

**issue 本文の 6 件に対し実測は 10 件**（`ai-chat` 2 件・`notifications` 2 件が本文に無い）。
**feature 内部の `components/` からは 1 件も出ていない**（そこの 30 件はすべて JSX を持つ）。

### 軸 3 — エイリアス `@foundation/ui` の宣言箇所（移送の波及先）

```console
$ grep -rn "@foundation/ui" --include=*.json --include=*.ts . | grep -v node_modules | grep -v '/src/src'
src/knowledge/frontend/tsconfig.json:33,34
src/platform/frontend/tsconfig.app.json:41,42
src/platform/frontend/vite.config.ts:92
src/vitest.config.ts:38
templates/unit-template/frontend/tsconfig.json:74,78
```

🔴 **#1131 本文は「3 箇所」と書いているが、実測は 5 箇所である。** 本文が挙げた platform の 3 つに加え、
**knowledge ユニットの `tsconfig.json`** と **`templates/unit-template/frontend/tsconfig.json`** が
同じ `@foundation/*` の面を宣言している。**足すなら 5 箇所すべてに足す。**

### 軸 4 — submodule（`ai-stock-trading`）への波及

submodule を init して直接走査した（`git submodule status` = `0844b584`）。

```console
$ grep -rn "@foundation/" --include=*.ts --include=*.tsx src/ai-stock-trading/frontend/src \
    | sed 's/.*from //' | sort | uniq -c | sort -rn
  13 '@foundation/api/ApiError';
   7 '@foundation/api/apiClient';
   6 '@foundation/auth/AuthContext';
   4 '@foundation/routing/featureRegistry';
   3 '@foundation/auth/RequireRole';
```

**issue 本文の「AST は `@foundation/ui/*` を使っていない」は実測で裏が取れた**（陽性対照: 同じ検索が
他の 5 面を 33 文ちゃんと拾っている）。加えて **本 PR はエイリアスを 1 つも削らない**（`@foundation/ui`
はそのまま残す）ので、仮に使っていても壊れない。

## 2. 除外理由（母集合 10 件のうち移さない 4 件）

| ファイル | 自ディレクトリ外の消費者 | 判断 |
| --- | ---: | --- |
| `components/ai-chat/aiChatStore.ts` | **0** | 移さない |
| `components/ai-chat/useAiChatStream.ts` | **0** | 移さない |
| `components/notifications/notificationMessages.ts` | **0** | 移さない |
| `components/notifications/useNotifications.ts` | **0** | 移さない |

実測（2 つの公開面を外から引く箇所。設定ファイルを除く）:

```console
$ grep -rn "@foundation/ai-chat\|@foundation/notifications" --include=*.ts --include=*.tsx src/
src/platform/frontend/src/app/Layout.tsx:14  { NotificationBell }
src/platform/frontend/src/app/Layout.tsx:15  { AiChatPanel }
```

**どちらの公開面も「コンポーネント 1 個」しか外へ出していない。** 4 件はいずれも**状態を持つ**
（zustand ストア / React フック）ものであり、`utils/`（純粋関数）の定義に当たらない。
**消費者が同ディレクトリの 1 コンポーネントに閉じている**ので、`hooks/` `stores/` へ出すと
**呼び出し元が 1 つしかない間接層が増えるだけになる。**

> 🔴 **`aiChatStore.ts` は `stores/`（グローバル状態）の候補としても読める** —— ヘッダのランチャーと
> パネル本体が画面遷移をまたいで同じ履歴を読むためである。**本 PR では移さず、記録に留める。**
> 移すかどうかは「描画しない純粋関数の置き場」ではなく「共有コンポーネント群の内部をどこまで
> 外へ出すか」の判断であり、#1131 が立てた問いとは別である。

## 3. 設計 — 置き場の基準（IADR-0331 に記録する）

`components/` に置いてよいのは**描画する部品**と、**その部品ひとつに閉じた内部**だけとする。
それ以外は、**何を外へ渡すか**で行き先を決める。

| 何を export するか | 行き先 | 根拠 |
| --- | --- | --- |
| JSX を返す部品 | `components/` | 13_frontend-stack §ディレクトリ構成「共通コンポーネント」 |
| **設定済み／遅延読み込みした外部ライブラリ**そのもの | `lib/` | Bulletproof React `lib` ＝「アプリ向けに設定済みの再利用ライブラリ」 |
| **自前の純粋関数**（内部で外部ライブラリを使ってよい） | `utils/` | 同 `utils` ＝「共有ユーティリティ関数」 |
| 状態を持ち、消費者が同ディレクトリの 1 部品に閉じるもの | 部品と同居 | §2 |

🔴 **`formatDateTime` を `lib/` ではなく `utils/` にするのは、`dayjs` を外へ渡していないからである。**
原典の `lib` は「ライブラリを設定して**再輸出**する」（例: 設定済みの axios インスタンス）であり、
`formatDateTime` が渡すのは `string → string` の自前関数である。**「外部ライブラリを内部で使うか」
ではなく「外部ライブラリを外へ渡すか」で分ける** —— 前者を基準にすると dayjs / zod / clsx を
1 行でも使う関数がすべて `lib/` へ流れ、**`utils/` は永久に空のままになる**（本 issue の再来）。

### 移送表

| from | to | 判定 |
| --- | --- | --- |
| `platform/src/components/ui/apiErrors.ts`（＋ `.test.ts`） | `platform/src/utils/apiErrors.ts` | 自前の純粋関数。`components/ui/` の外に **14 ファイル**の import |
| `platform/src/components/ui/formatDateTime.ts`（＋ `.test.ts`） | `platform/src/utils/formatDateTime.ts` | 同上。外に **10 ファイル**の import |
| `knowledge/src/components/echartsBundle.ts` | `knowledge/src/lib/echarts/echartsBundle.ts` | `export { echarts }` ＝**設定済みライブラリを外へ渡す** |
| `knowledge/src/components/echartsLoader.ts`（＋ `.test.ts`） | `knowledge/src/lib/echarts/echartsLoader.ts` | 同ライブラリの遅延読み込み口 |
| `knowledge/src/components/echartsGraphBundle.ts` | `knowledge/src/lib/echarts/echartsGraphBundle.ts` | 同上（graph 面） |
| `knowledge/src/components/echartsGraphLoader.ts`（＋ `.test.ts`） | `knowledge/src/lib/echarts/echartsGraphLoader.ts` | 同上 |

**echarts 4 本を 2 つに割らない。** `echartsLoader` の消費者は同居する `EChart.tsx` だけなので
§2 の「同居する内部」にも読めるが、**`echartsGraphLoader` は同居する部品を持たない**
（描く側は `features/sc18-graph/components/GraphCanvas.tsx`）。両者は**意図的に対称に書かれており**
（互いの冒頭コメントが相手を指す）、片方だけ動かすと対称が壊れる。**`export { echarts }` という
同じ形をしているので、同じ規則（`lib/`）で同じ場所へ置く。**

### エイリアス

`@foundation/utils` / `@foundation/utils/*` を**足す**（既存の面は 1 つも変えない・削らない）。
`src/platform/frontend/README.md` の「エイリアス名は変えない」は**改名の禁止**であって追加の禁止ではない
（改名すると submodule と `templates/unit-template` の契約が同時に割れる、という理由である）。
**足す先は軸 3 の 5 箇所すべて。**

knowledge ユニット内部の `lib/echarts/` は**相対パスで引く**（`@knowledge/*` は knowledge 側の
`tsconfig.json` にしか無く、既存の `lib/abac` / `lib/scope-filter` も相対で引かれている）。

### ESLint による再発防止は入れない（1 回目である）

`.claude/rules/` の運用規約は「**同型の事故が 2 回起きたら**検査器・規約を足す（1 回目は記録に留める）」
と定める。`components/` への純粋関数の混入を**是正した**のは本 PR が 1 回目である（#1122 は
「空枠を残すか消すか」の issue で、**実体の移送は明示的に対象外**としていた）。
よって **IADR-0331 に基準を記録するに留め、ESLint 規則は入れない。**

## 4. 受け入れ基準

- [x] `components/ui/apiErrors.ts` / `formatDateTime.ts` が `components/` に居ない（`utils/` へ移った）
- [x] 移送しなかった 4 件の理由が PR 本文にある（§2）
- [x] エイリアスの向き先が**宣言 5 箇所すべて**で一致する
- [x] `pnpm run test` が 1 件も落ちない（振る舞いを変えない移送である）
- [x] `pnpm run lint` / `typecheck` / `build` / `format:check` が成功する
- [x] `node scripts/check-chunk-budget.js --require <dist>` が成功する（動いたら理由を PR へ書く）
- [x] 移送後、`components/` 配下に JSX を返さないファイルが **4 件だけ**残る（陽性対照: 移送前は 10 件）

## 5. テスト方針

**新規のテストは書かない。** 振る舞いを変えない配置是正であり、既存の
`apiErrors.test.ts` / `formatDateTime.test.ts` / `echartsLoader.test.ts` / `echartsGraphLoader.test.ts` を
**実装と同じ移送先へ `git mv` で一緒に運ぶ**（テストは実装と同居する）。
**テストの中身は import のパス以外 1 行も変えない。**

ローカル基準線（`origin/develop` `89b4d26e`・Node 22・submodule init 済み）: **102 ファイル / 1272 件すべて緑**。
🔴 **submodule を init しないと `@ai-stock-trading/features` が解決できず 4 ファイルが赤になる**
（合成点 `platform/frontend/src/features/index.ts` が引いている）。**この赤はコードの問題ではない。**

## 6. 計画書との差異

- 差異: なし。13_frontend-stack §ディレクトリ構成 の `components/` と `utils/` / `lib/` の区分へ
  実装を寄せる作業であり、計画を動かさない。

## 7. 未決事項

- なし。#1131 の「判断すること」3 点は §3 で決めた。

## 8. 検証（実走。Node 22 / submodule init 済み）

| 検査 | 結果 |
| --- | --- |
| `pnpm run typecheck` | OK（5 workspace すべて） |
| `pnpm run lint` | OK（0 errors / 10 warnings。**warning 10 件は `react-refresh/only-export-components` のみ**で、移送したファイルは 1 件も含まない） |
| `pnpm run format:check` | OK |
| `pnpm run test` | **102 ファイル / 1272 件 緑**（develop 基準線と同数） |
| `pnpm run build` | OK |
| `node scripts/check-chunk-budget.js` | OK。**初期ロード 617.16 kB（床 617.16 kB）＝ 1 バイトも動いていない。** 必須チャンク 5 本・最大 586.04 kB・1 kB 未満の遅延チャンク 6 本も同じ。**baseline の更新は不要。** 動かなかったのは、移送でファイル名（＝遅延チャンク名）も `manualChunks` の規則（`node_modules` のパッケージ名で判定する）も変わっていないためである |
| `node scripts/check-static-egress.js --require <dist>` | OK（39 ファイル） |
| `node scripts/check-route-manifest.js` | OK |
| `node scripts/check-adr-numbering.js` | OK |
| `node scripts/check-doc-links.js` | OK（1062 件） |
| `node scripts/check-trace-blocks.js` | OK（166 件） |
| `node scripts/gen-knowledge-graph.js --check` | OK |
| `node scripts/check-i18n-catalogs.js` | OK |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **676 件 緑** |
| knip | 未使用 devDeps 4 / unlisted 1 / exports 16 / types 17 —— **`scripts/knip-baseline.json` の床と完全一致**（`check-knip.js` 自身は Windows で `.bin/knip` を spawn できず起動しない。**環境の制約であってコードの問題ではない**ので、同じ数を `pnpm exec knip` で直に測った） |

### 移送後の母集合（受け入れ基準の最後の 1 行）

```console
$ git ls-files 'src/*/frontend/src/**/components/**' 'src/*/frontend/src/components/**' \
    | grep -E '\.(ts|tsx)$' | grep -vE '\.(test|spec)\.' | wc -l
43                          ← 移送前 49（6 件が components/ の外へ出た）
$ xargs grep -L '</\|/>' < <上の一覧>
src/platform/frontend/src/components/ai-chat/aiChatStore.ts
src/platform/frontend/src/components/ai-chat/useAiChatStream.ts
src/platform/frontend/src/components/notifications/notificationMessages.ts
src/platform/frontend/src/components/notifications/useNotifications.ts
$ xargs grep -l '</\|/>' < <上の一覧> | wc -l
39                          ← 陽性対照。39 ＋ 4 = 43 で分母と一致する
```

🔴 **移送前 10 件 → 移送後 4 件**であり、残る 4 件は §2 で除外理由を書いたものと**同一である**
（別のものが紛れ込んでいない）。

### 追随の母集合（規則 9・10）—— 誤りの側の文字列で走査した

```console
$ grep -rn "components/ui/apiErrors\|components/ui/formatDateTime\|components/echarts" \
    --include=*.ts --include=*.tsx --include=*.md --include=*.json src/ docs/ scripts/
```

| 出た箇所 | 対応 |
| --- | --- |
| `src/platform/frontend/src/lib/api/ApiError.ts`（散文） | 直した |
| 移送した `apiErrors.ts` 自身の冒頭コメント（「foundation/ui へ単一情報源化」） | 直した（`@foundation/utils`） |
| `src/platform/frontend/README.md`（ツリー・空枠の理由・エイリアスの節） | 直した。**「定義は 3 箇所」も 5 箇所へ是正**し、従前の記述が誤りだった旨を残した |
| `docs/tech/composable-component-guide.md` §2.6（基盤の区分一覧） | 直した（`utils` を足した） |
| `scripts/chunk-budget-baseline.json` の `$comment_initialTotalBytes_20260830_1078` | **直さない。** `［2026-08-30 / #1078］` と日付つきで当時の実測を記録した箇所であり、**過去の記録を後から書き換えない** |
| `src/platform/frontend/src/components/ui/ErrorList.tsx`（「foundation/ui へ単一情報源化」） | **直さない。** `ErrorList` 自身は `components/ui/` に居るままで、記述は正しい |

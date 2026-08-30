---
title: 作業仕様書 — 4 feature の内部分割を実体で満たす（#1066・SC-18〜SC-21）
type: spec
status: done
related_ids:
  - SC-18
  - SC-19
  - SC-20
  - SC-21
  - ADR-0031
  - ADR-0065
  - ADR-0066
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md §ディレクトリ構成（fixed）
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md (Accepted)
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 4
  - planning:projects/microservices-platform/07_adr/ADR-0066_frontend-feature-isolation-and-import-direction.md (Accepted 2026-08-30) §結果 フォローアップ 2
related_specs: []
related_adrs:
  - IADR-0307
issue: "#1066"
---

# 作業仕様書 — 4 feature の内部分割を実体で満たす

## 目的と射程

計画 `13_frontend-stack` §ディレクトリ構成 は feature 内部の 6 分割
（`api/ components/ hooks/ routes/ stores/ types/`）まで規範化しており、planning#445 の裁定は
**「必須とするのはツリー全体への適合である。名前だけを揃える対応は採らない」** と定める。

`sc18-graph` / `sc19-private-notes` / `sc20-obsidian-settings` / `sc21-ai-suggestions` の 4 件が
`hooks/`（4 件）・`types/`（`sc21` のみ）・`stores/`（4 件）を持たない。**本作業はこの 4 件を
実体（コード）で満たす。** 空フォルダ＋ `.gitkeep` で枠を作る形は採らない（後述 §`.gitkeep` の扱い）。

**射程外**: 既に 6 分割を満たしている 15 feature、`src/eslint.config.js`、`src/lib/`。
理由は §射程の切り方 に書く。

## 着手前の実測（`develop` = `e286fd5`。shallow ではない）

```console
$ git rev-parse --is-shallow-repository
false

$ git ls-files src/knowledge/frontend/src/features | ...   # 集計は下表
```

| feature | 現状のディレクトリ | 欠け |
| --- | --- | --- |
| `sc18-graph` | `api components routes types` | `hooks/` `stores/` |
| `sc19-private-notes` | `api components routes types` | `hooks/` `stores/` |
| `sc20-obsidian-settings` | `api components routes types` | `hooks/` `stores/` |
| `sc21-ai-suggestions` | `api components routes` | `hooks/` `stores/` `types/` |

**19 feature 中 15 が 6 分割を満たす**という issue 本文の実測を再現した。ただし
**15 件の「満たしている」の中身は大半が `.gitkeep` の空枠**である（例: `abac` は `types/` 以外の
5 区分がすべて `.gitkeep` 1 個。`hooks/.gitkeep` は 15 件中 15 件、`stores/.gitkeep` は 15 件中 15 件）。

```console
$ git ls-files 'src/knowledge/frontend/src/features/*/hooks/*'   # → すべて .gitkeep（15 件）
$ git ls-files 'src/knowledge/frontend/src/features/*/stores/*'  # → すべて .gitkeep（15 件）
```

**実体のある `hooks/` はリポジトリ全体で 0 件、実体のある feature `stores/` も 0 件**である。

## `.gitkeep` の扱い（本作業が明示的に裁く点）

計画 `ADR-0065` 決定 4 は **「実体が無いものは空フォルダ＋`.gitkeep` を置く」規範を撤回**した。
理由は **「`.gitkeep` が『適合の見え方』を作った」** ことであり、枠だけの状態が
**機械的にも目視でも「揃っている」ように見えた**ためである。

- 🔴 **本作業の 4 feature に `.gitkeep` を置かない。** 置けば `ADR-0065` 決定 4 が名指しした
  「適合の見え方」をフロントエンドで再生産することになる。`ADR-0066` §理由 も
  **「§ディレクトリ構成 への適合がフォルダ名の一致で判定されると、バックエンドで一度起きた
  誤判定をフロントエンドで繰り返す」** と書いている。
- **したがって `hooks/` と `types/` は実コードを移して満たし、`stores/` は置かない。**
  `stores/` を置かない理由は feature ごとに §stores を置かない理由 で述べる（issue の
  受け入れ基準 3「本当に不要な区分がある場合は PR 本文で述べる」に対応）。

### 射程の切り方 — 既存 15 feature の `.gitkeep` 空枠は本 issue の射程外とする

**矛盾を伏せずに名指しする。** 既存 15 feature が 6 分割を満たしている形は、
`ADR-0065` 決定 4 が撤回したのとまったく同じ形（空枠による適合の見え方）である。
それでも本作業では触らない。理由は 3 つ。

1. **issue #1066 の宣言ファイル領域が 4 feature に限られている**（`sc18-graph/**`,
   `sc19-private-notes/**`, `sc20-obsidian-settings/**`, `sc21-ai-suggestions/**`）。
   運用ガイドは**並列作業を宣言済みファイル領域の非重複で機械的に判定**すると定める。
   領域外へ手を伸ばすと、同時に走っている #1065（`abac` / `scope-filter` / `sc01` / `sc05` /
   `sc06` / `sc08` / `src/lib/` / `src/eslint.config.js`）と衝突する。
   **`abac` / `scope-filter` は #1065 が `src/lib/` へ移送する対象**であり、
   いま `.gitkeep` を整理すると移送と真正面からぶつかる。
2. **`ADR-0065` 決定 4 の明文はバックエンドの 8 要素標準（planning#180 裁定）の部分改定である。**
   フロントエンドの feature 6 分割へ「`.gitkeep` を置いてはならない」と直接及ぼす明文は
   計画側に無い。**理由（適合の見え方を作る）は移せるが、規範の射程を実装側の判断で広げると、
   15 feature の枠を消したあとで「6 分割が無い」と判定される余地が残る。**
3. **したがって本 PR は「実体で満たす」側だけを進める。** 既存 15 件の空枠の撤去は
   **計画側の裁定（フロントエンドにも決定 4 を及ぼすか）を要する**ため、
   planning へ環流 issue を起票することを PR 本文で提案する。

## 変更内容

### 共通の設計方針 — 純関数は `types/`、状態と副作用は `hooks/`

`templates/unit-template/frontend/src/features/sample/` が持つ形（`hooks/useSampleFilter.ts` /
`types/index.ts` に実体があり、`stores/` だけが `.gitkeep`）を**雛形の正解形**として写す。

- `types/` … React に依存しない純粋な語彙・写像・判定。単体テストを直接書ける。
- `hooks/` … feature 固有の**クライアント状態**（URL 検索パラメータの読み書き・一時状態）。
  **サーバー状態は持ち込まない**（取得・キャッシュは `api/` の TanStack Query。ADR-0031）。

### sc18-graph

- 追加 `hooks/useGraphExploration.ts` — `GraphViewPage.tsx` に混ざっていた探索条件の
  クライアント状態を移す。URL（`root` / `hops` / `by` / `types`）への書き込み（`setParams`）、
  辺の型フィルタの ON/OFF（`toggleType` / `activeTypes` / `lastActive`）、
  グラフ内検索（`nodeQuery` / `matches` / `focusedId`）、選択ノード（`selectedId`）。
- `components/GraphViewPage.tsx` は描画と `api/` の呼び出しだけを残す。

### sc19-private-notes

- 追加 `hooks/useNoteListView.ts` — `PrivateNotesPage.tsx` から、タブ（URL の `tab`）と
  絞り込み語（URL の `q`）から表示行を導く部分と、削除済みタブの選択状態を移す。
  `live` / `trashed` / `rows` / `now` / `selected` / `switchTab` / `setParams`。

### sc20-obsidian-settings

- 追加 `hooks/useIssuedToken.ts` — 平文トークンの一時状態を移す。
  **平文は発行・再発行の応答にしか載らず、次の操作を始めた時点で捨てる**という規則
  （`05_screens` §SC-20）を、画面の描画から切り離して 1 箇所に閉じる。

### sc21-ai-suggestions

- 追加 `types/suggestionVocabulary.ts` — `routes/` に置かれていた語彙と写像を移す。
  `STATE_OPTIONS` / `KIND_OPTIONS` / `StateOption` / `KindOption` / `AiSuggestionSearch` /
  `normalizeAiSuggestionSearch()`（`validateSearch` の中身）/ `suggestionTone()` /
  `edgeTypeNameMap()`。**いずれも React にも router にも依存しない。**
- 追加 `types/suggestionVocabulary.test.ts` — 上の純関数を直接固定する。
- 追加 `hooks/useSuggestionFilters.ts` — URL 検索パラメータの読み書き（`search` / `setParams`）。
- `routes/sc21AiSuggestionsRoute.ts` は `normalizeAiSuggestionSearch` を呼ぶだけにする
  （**再輸出は置かない**。置くと `check-knip` の未使用 export になる）。

### 再発防止 — plop 雛形と unit-template 雛形の食い違いを解消する

**issue が挙げた仮説（plop が 6 分割を生成していない）は誤りである。** 実測では
`src/plopfile.js` は `api/` `hooks/` `stores/` `types/` の 4 つを `.gitkeep` で生成しており、
`routes/` `components/` と合わせて 6 分割を作る。**4 feature が落ちたのは plop 経由でないためである**
（`sc18`〜`sc21` と `src/plopfile.js` は同一コミット `736f599`（#1009）で入っている）。

**食い違いは「6 分割を作るか」ではなく「何で埋めるか」である。**

| 雛形 | `api/` | `hooks/` | `types/` | `stores/` |
| --- | --- | --- | --- | --- |
| `src/plop-templates/feature/`（plop） | `.gitkeep` | `.gitkeep` | `.gitkeep` | `.gitkeep` |
| `templates/unit-template/.../sample/` | 実体 | 実体 | 実体 | `.gitkeep` |

**plop 側を unit-template 側（正解形）へ寄せる。** `api/` `hooks/` `types/` は実体のある
雛形ファイルを生成し、`.gitkeep` は `stores/` だけに残す。plopfile の該当コメントも直す
（現在は「空の区分は `.gitkeep` で枠だけ残す」と書いており、`ADR-0065` 決定 4 の後では
そのまま維持できない）。

🔴 **検査器は追加しない。** `CLAUDE.md` の「同型の事故が 2 回起きたら」に従い、今回は記録に留める
（issue 本文も「今回は記録に留めてよい」と明記している）。

**決定の記録は [`IADR-0307`](../adr/IADR-0307_feature-internal-split-substance-over-scaffolding.md) に置く**
（`stores/` を置かない判断・空枠を作らない方針・plop 雛形の改定は、PR 本文だけに残すと次の実装者が
「6 分割が欠けている」と読んで空枠を作り直す）。**番号は本ブランチ上の最大 `IADR-0306` + 1 である**
（同時進行の PR が次番号を押さえている場合は改番が要る）。

## `stores/` を置かない理由（受け入れ基準 3 への回答）

**4 feature とも、クライアント状態の単一情報源を URL に置くと計画・実装が決めている。**
Zustand ストアを足すと**同じ状態の情報源が 2 つになる**（URL とストア）。
`IADR-0124` 決定 3 が URL を単一情報源とする理由（共有・再読込・戻るで状態が失われない）を
そのまま壊す。既存コードのコメントも同じことを書いている。

| feature | 置かない理由（コード内の既存の明記） |
| --- | --- |
| `sc18-graph` | 「**URL（root / hops / by / types）が探索条件の単一情報源である**」（`GraphViewPage.tsx` 冒頭）。残るのは選択ノードと検索語だけで、いずれも画面を閉じたら消えてよいローカル状態 |
| `sc19-private-notes` | 「**タブは URL（`?tab=trash`）に持つ**」。一覧そのものは TanStack Query（`api/`）が持つサーバー状態 |
| `sc20-obsidian-settings` | ルートに「**絞り込みも並べ替えも無い画面なので、URL に持つ状態は無い**」。平文トークンは 🔴 **保存してはならない**（`05_screens` §SC-20「保存もコピー履歴も残さない」）—— ストアへ載せることが**仕様違反**になる |
| `sc21-ai-suggestions` | ルートに「**クライアント状態ストアを持ち込まない —— 共有・再読込・戻るのいずれでも同じ一覧になる**」と明記済み |

**「いま要らない」ではなく「置くと計画に反する」である。** よって空枠も置かない。
なお**リポジトリ全体で実体のある feature `stores/` は 0 件**であり、Zustand の唯一の利用は
`platform/frontend/src/components/ai-chat/aiChatStore.ts`（共通シェルの右レール。feature ではない）である。

## 受け入れ基準（issue の Given-When-Then への写像）

| # | 基準 | 満たし方 |
| --- | --- | --- |
| 1 | 4 feature に 6 分割がすべてある | 🔴 **`stores/` は満たさない。** 上表の理由で置かない（受け入れ基準 3 の逃げ道を使う）。`hooks/` は 4 件とも、`types/` は `sc21` も追加する |
| 2 | フックは `hooks/`、型は `types/` にある（空フォルダで満たしたことにしない） | 実コードを移す。移した先は上の §変更内容 |
| 3 | 不要な区分は PR 本文で理由を述べる | §`stores/` を置かない理由 を PR 本文へ転記する |
| 4 | `pnpm run lint` / `typecheck` / `test` が通る | §検証 |
| 5 | `node scripts/check-route-manifest.js` が通る | §検証 |

## 検証（実行結果。2026-08-30）

| コマンド | 結果 |
| --- | --- |
| `pnpm run typecheck` | ✅ 5 workspace すべて Done |
| `pnpm run lint` | ✅ **0 errors** / 10 warnings（すべて既存。`react-refresh/only-export-components` 等で、本変更のファイルは 1 件も含まない） |
| `pnpm run test` | ⚠️ **1271 件中 1270 件成功。** 唯一の失敗 `platform/frontend/src/lib/api/orvalMutator.test.ts`（`res.data.arrayBuffer is not a function`）は 🔴 **本変更を stash した `develop` 相当の状態でも同じく失敗する**（実測）。Node 24 の jsdom で `Blob#arrayBuffer` が生えない環境差であり、本変更とは無関係 |
| `pnpm run build` | ✅ built |
| `pnpm run format:check` | ✅ All matched files use Prettier code style |
| `node scripts/check-route-manifest.js` | ✅ 画面 17 件とマニフェスト 16 行が対応 |
| `node scripts/check-chunk-budget.js` | ✅（床を +0.18 kB 更新。理由は次節） |
| `node scripts/check-i18n-catalogs.js` | ✅ 未翻訳・fuzzy・obsolete なし |
| `node scripts/check-knip.js` | ⚠️ **Windows では起動できない**（`.bin/knip` を `.CMD` 無しで spawn するため ENOENT。本変更と無関係の環境差）。**代わりに `pnpm exec knip` を直接実行し、`knip-baseline.json` の床（devDependencies 4 / exports 16 / types 17 / unlisted 1）と実測が完全一致することを確認した**（新設した export は 1 件も湧いていない） |
| `node scripts/check-trace-blocks.js` | ✅ 158 件 違反なし |
| `node scripts/check-doc-links.js` | ✅ 997 件 破損リンクなし |
| `node scripts/check-adr-numbering.js` | ✅ 重複・欠番なし、索引と双方向一致 |
| `node scripts/check-doc-type-vocabulary.js` | ✅ 968 件 値域内 |
| `node scripts/gen-knowledge-graph.js --check` | ✅ in-repo エッジ先の実在に違反なし |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | ✅ 664 tests passed |

`node scripts/check-commit-messages.js` はコミット後に実行する（起点 ID `SC-18,SC-19,SC-20,SC-21` は
`.claude/rules/traceability.repo.md` の宣言レンジ `SC-01..21` の内側である）。

### 初期ロード床を上げた理由（`scripts/chunk-budget-baseline.json`: 616,025 → 616,202 B）

SC-21 の語彙を `routes/` から `types/suggestionVocabulary.ts` へ移した結果、
**ルート定義（初期チャンクに載る）が引くモジュール**に、画面側だけが使う 2 関数
（`suggestionTone` / `edgeTypeNameMap`）が同居した。**バンドルの都合で `types/` を 2 ファイルへ
割ることはしない** —— 区分は関心で切るものであり、チャンク境界で切ると次の実装者が
どちらへ書くか判断できなくなる。増加は初期ロードの 0.03%（+0.18 kB）である。
床の更新は `--update` の実測値で、`chunk-budget-baseline.json` へ日付つきの `$comment` を添えた。

## 母集合の引き方（`.claude/rules/traceability.repo.md` §是正・追随の母集合）

**規則 9（誤りの側の文字列で走査してから挙げる）**: 「feature 内部分割の欠け」を記述している
文書を、`sc18-graph` / `sc19-private-notes` / `sc20-obsidian-settings` / `sc21-ai-suggestions` と
`hooks/ stores/ types/` の両方で追跡下の全ファイルへ走査した（`src/ai-stock-trading` は submodule
のため除外）。**本リポジトリ内で「4 件が欠けている」と述べている文書は無い**（issue 本文と
計画側 `ADR-0066` §結果 フォローアップ 2 のみ）。計画側は本リポジトリから書き換えない。

**規則 10（是正で新たに誤りになる自分の記述を引き直す）**: `.gitkeep` を「枠として残す」と
書いている自分の記述を走査した。

| 箇所 | 扱い |
| --- | --- |
| `src/plopfile.js`（「空の区分は `.gitkeep` で枠だけ残す」） | 🔴 **直す**（§再発防止） |
| `templates/unit-template/.../hooks/useSampleFilter.ts`（「枠は隣に `stores/`（`.gitkeep` のみ）として在る…Zustand 自体は本リポジトリへ未導入」） | 🔴 **記述が古い**（Zustand は #788 で導入済み）。`stores/` の位置づけを現況へ直す |
| `.ai-context/adr/IADR-0218`（バックエンドの `.gitkeep` 枠置き） | **対象外**。`ADR-0065` 決定 4 の追随は #1061 系の別 issue が持つ（本 issue の宣言領域外） |

## 計画との差異・環流の候補（PR 本文へ書く）

1. **既存 15 feature の `.gitkeep` 空枠**（§射程の切り方）—— `ADR-0065` 決定 4 をフロントエンドの
   feature 6 分割へも及ぼすかの裁定が要る。planning への環流を提案する。
2. **lingui 適用範囲（`src/eslint.config.js` の `files`）に `sc18`〜`sc21` /
   `sc04-wiki` / `scope-filter` が無い。** 4 feature は `Trans` / `useLingui` で i18n 済みなので、
   **「i18n 化したのに検査されない」状態**である（同ファイルのコメントが避けたいと書いている状態
   そのもの）。**本 PR では直さない** —— `src/eslint.config.js` は #1065 の宣言ファイル領域であり、
   同時に編集すると FIFO マージが壊れる。**別 issue を提案する。**
   なお `ADR-0066` §理由 は、この「画面を作るたびに `files` を伸ばす運用」を
   **「伸ばし忘れが規則の穴になる」**例として名指ししている。

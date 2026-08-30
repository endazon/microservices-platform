---
title: IADR-0309 feature 内部 6 分割の適合は実体で示し、`.gitkeep` の空枠で満たさない
type: impl-adr
status: Accepted
related_ids: [SC-18, SC-19, SC-20, SC-21, ADR-0031, ADR-0065, ADR-0066, IADR-0124, IADR-0218, IADR-0262, IADR-0275]
author: Claude
created: 2026-08-30
updated: 2026-08-30
related_specs:
  - ../specs/20260830_issue-1066_feature-internal-split.md
---

# IADR-0309: feature 内部 6 分割の適合は実体で示す

## 文脈

計画 `13_frontend-stack` §ディレクトリ構成 は feature 内部の 6 分割
（`api/ components/ hooks/ routes/ stores/ types/`）まで規範化しており、planning#445 の裁定は
**「必須とするのはツリー全体への適合である。名前だけを揃える対応は採らない」** と定める。

実測（2026-08-30 / `develop` = `e286fd5`。shallow ではない）で、`knowledge/frontend` の
19 feature のうち 4 件（`sc18-graph` / `sc19-private-notes` / `sc20-obsidian-settings` /
`sc21-ai-suggestions`）が `hooks/` を持たず、`sc21` は `types/` も持たず、4 件とも `stores/` が無い。

**ところが「満たしている」15 件の中身は、ほぼ空枠である。**

| 区分 | 15 feature 中、実体があるもの |
| --- | ---: |
| `hooks/` | **0**（15 件すべて `.gitkeep` 1 個） |
| `stores/` | **0**（15 件すべて `.gitkeep` 1 個） |

計画 `ADR-0065` 決定 4 は、バックエンドの 8 要素標準について
**「実体が無いものは空フォルダ＋`.gitkeep` を置く」規範を撤回**した。理由は
**「`.gitkeep` が『適合の見え方』を作った」** —— 枠だけの状態が機械にも目視にも
「揃っている」と見え、2026-08-22 の適合判定がその見え方をそのまま拾ったことである。
`ADR-0066` §理由 は、同じ読みがフロントエンドで繰り返される入口を名指ししている。

**つまり、欠けている 4 件を `.gitkeep` で埋めると、計画が撤回したばかりの形を新規に作ることになる。**

## 決定

### 決定 1: 欠けている区分は**実コードの移送**で満たす。`.gitkeep` は置かない

`components/` に混ざっていたクライアント状態・純粋な語彙を、`hooks/` と `types/` へ移す。

| feature | 追加 | 移した中身 |
| --- | --- | --- |
| `sc18-graph` | `hooks/useGraphFilters.ts` / `hooks/useGraphNodeSearch.ts` | 探索条件（URL）の読み書きと辺の型フィルタ／グラフ内検索と選択ノード |
| `sc19-private-notes` | `hooks/useNoteListView.ts` | タブ・絞り込み語（URL）から表示行を導く部分と選択状態 |
| `sc20-obsidian-settings` | `hooks/useIssuedToken.ts` | 平文トークンの一時状態 |
| `sc21-ai-suggestions` | `types/suggestionVocabulary.ts` / `hooks/useSuggestionFilters.ts` | 状態・種類の語彙と `validateSearch` の実体・バッジ色・辺の型辞書／絞り込み条件の読み書き |

**区分の意味を、置き場所の規則として固定する。**

- `types/` … React にも router にも依存しない純粋な定義（**画面を描かずに単体テストで固定できる**）。
- `hooks/` … feature 固有の**クライアント状態**。**サーバー状態を持ち込まない**（`api/` の TanStack Query が持つ。ADR-0031）。

### 決定 2: 4 feature に `stores/` を置かない —— 「いま要らない」ではなく「置くと計画に反する」

**4 件とも、クライアント状態の単一情報源を URL に置くと既に決めている**（IADR-0124 決定 3）。
Zustand ストアを足すと同じ状態の情報源が 2 つになり、
**共有・再読込・戻るで同じ画面になる**という性質が壊れる。

| feature | 置かない理由（コード内に既存の明記がある） |
| --- | --- |
| `sc18-graph` | 「URL（root / hops / by / types）が探索条件の単一情報源である」 |
| `sc19-private-notes` | 「タブは URL（`?tab=trash`）に持つ」。一覧そのものは `api/` のサーバー状態 |
| `sc20-obsidian-settings` | 🔴 平文トークンは**保存してはならない**（`05_screens` §SC-20「保存もコピー履歴も残さない」）。ストアへ載せることが**仕様違反**になる |
| `sc21-ai-suggestions` | 「クライアント状態ストアを持ち込まない —— 共有・再読込・戻るのいずれでも同じ一覧になる」 |

**よって空枠も置かない。** 区分が無いこと自体が「この画面はストアを持たない」という情報になる
（`ADR-0065` 決定 4 が単一プロジェクト構成について書いた「フォルダが無ければその関心が無い」と同じ形）。

なお **Zustand は #788 で導入済み**であり、「ライブラリが無いから空」ではない。
唯一の利用は `platform/frontend/src/components/ai-chat/aiChatStore.ts`（共通シェルの右レール。feature ではない）である。

### 決定 3: 既存 15 feature の `.gitkeep` 空枠は本 IADR の射程外とし、計画へ環流する

**矛盾を伏せない。** 15 件が 6 分割を満たしている形は、`ADR-0065` 決定 4 が撤回したのと同じ形である。
それでも本 PR では触らない。

1. **issue #1066 の宣言ファイル領域が 4 feature に限られている。** 並列作業は宣言済み領域の
   非重複で機械的に判定する運用であり、`abac` / `scope-filter` は同時進行の #1065 が
   `src/lib/` へ移送する対象である。いま空枠を整理すると移送と正面から衝突する。
2. **`ADR-0065` 決定 4 の明文は、バックエンドの 8 要素標準（planning#180 裁定）の部分改定である。**
   フロントエンドの feature 6 分割へ直接及ぼす明文は計画側に無い。**理由は移せるが、規範の射程を
   実装側の判断で広げると、15 件の枠を消したあとで「6 分割が無い」と判定される余地が残る。**
3. **したがって裁定を計画へ求める**（`ADR-0065` 決定 4 をフロントエンドの feature 6 分割へも及ぼすか）。

### 決定 4: plop 雛形が `api/` `hooks/` `types/` に**実体**を生成する。`.gitkeep` は `stores/` だけ

**issue #1066 が挙げた仮説（plop が 6 分割を生成していない）は誤りである。** 実測では
`src/plopfile.js` は 6 区分すべてを作っていた。**食い違いは「6 分割を作るか」ではなく「何で埋めるか」**である。

| 雛形 | `api/` | `hooks/` | `types/` | `stores/` |
| --- | --- | --- | --- | --- |
| `src/plop-templates/feature/`（従前） | `.gitkeep` | `.gitkeep` | `.gitkeep` | `.gitkeep` |
| `templates/unit-template/.../sample/` | 実体 | 実体 | 実体 | `.gitkeep` |
| `src/plop-templates/feature/`（本決定） | **実体** | **実体** | **実体** | `.gitkeep` |

生成される実体は**差し替え前提**であり、生成後の案内文にもそう書く。
**`stores/` だけ `.gitkeep` を残す**のは、決定 2 のとおりストアを持つのが例外だからである
（実体を生成すると「置くべきもの」と読み違えられる）。

### 決定 5: 機械検査は追加しない

`CLAUDE.md`「検査器・規約の追加は『同型の事故が 2 回起きたら』」に従う。**今回は 1 回目**であり、
記録に留める（issue 本文も「今回は記録に留めてよい」と明記している）。
2 回目が起きたら、feature 直下の区分と実体の有無を突き合わせる検査を検討する。

## 影響

- **`scripts/chunk-budget-baseline.json` の初期ロード床を 616,025 → 616,202 B（+0.18 kB）へ上げた。**
  SC-21 の語彙を `routes/` から `types/` へ移したことで、**ルート定義（初期チャンク）が引く
  モジュール**に画面側だけが使う 2 関数（`suggestionTone` / `edgeTypeNameMap`）が同居したためである。
  🔴 **バンドルの都合で `types/` を 2 ファイルへ割らない** —— 区分は関心で切るものであり、
  チャンク境界で切ると次の実装者がどちらへ書くか判断できなくなる。増加は初期ロードの 0.03% である。
- `templates/unit-template/.../hooks/useSampleFilter.ts` の「Zustand 自体は本リポジトリへ未導入」は
  #788 で古くなっていた。現況（実体のある feature `stores/` が 0 件）へ書き直した。
- `IADR-0218`（バックエンドの `.gitkeep` 枠置き）は**本 IADR の射程外**である。`ADR-0065` 決定 4 の
  追随はバックエンド側の別 issue が持つ。

## 残余リスク

- **決定 2 は受け入れ基準の字面（6 区分がすべてある）を満たさない。** issue #1066 の受け入れ基準 3
  （「本当に不要な区分がある場合は PR 本文で述べる」）を根拠に採っており、PR 本文に理由を載せている。
  **計画が「空でも 6 つ置け」と裁定したら本決定は覆る**（決定 3 の環流で問う点でもある）。
- **決定 5 により、次に画面を作る人が `hooks/` を作り忘れても機械は止めない。** plop 経由なら落ちないが、
  4 feature がそうだったように**手で作る経路が残る**。

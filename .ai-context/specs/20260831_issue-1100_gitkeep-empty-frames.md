---
title: 作業仕様書 — .gitkeep だけの空枠を撤去し、雛形が空枠を生まないようにする（#1100）
type: spec
status: done
related_ids:
  - NFR
  - SC-01
  - SC-02
  - SC-03
  - SC-04
  - SC-05
  - SC-06
  - SC-07
  - SC-08
  - SC-09
  - SC-10
  - SC-11
  - SC-12
  - SC-17
  - ADR-0031
  - ADR-0065
author: claude
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 4・§結果 フォローアップ 5
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md (Accepted)
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md §ディレクトリ構成（fixed。planning#378 → planning#445）
related_specs:
  - ./20260830_issue-1066_feature-internal-split.md
---

# 作業仕様書: `.gitkeep` だけの空枠を撤去し、雛形が空枠を生まないようにする

起点: 実装 issue #1100（親 #452）。先行 #1066（closed）／IADR-0309／IADR-0218。

## 1. 何を解くか

計画 `ADR-0065` 決定 4 は **「実体が無いものは空フォルダ＋`.gitkeep` を置く」規範を撤回した。**
撤回の理由は `.gitkeep` が**「適合の見え方」を作った**ことである（枠だけの状態が機械にも目視にも
「区分が揃っている」と見え、2026-08-22 の適合判定がその見え方をそのまま拾った）。

撤回前に作られた枠が `knowledge/frontend` の feature 内部に残っている。**#1066 はこれを名指しで
記録したうえで意図的に繰り延べた**（IADR-0309 決定 3）。本作業がその繰り延べを回収する。

## 2. 母集合（自分で引いた。issue 本文の数えは転記しない）

基点: `origin/develop` = `dfec09f1`。`git rev-parse --is-shallow-repository` = **false**
（履歴の打ち切りではないことを確認済み。planning#410）。

### 軸 1 — ファイル名で引く（`.gitkeep`）

```console
$ git ls-files | grep -cE '(^|/)\.gitkeep$'
70
```

### 軸 2 — 名前を変えた同型の置き方（`.keep` / `.placeholder` / `PLACEHOLDER` / `.empty`）

```console
$ git ls-files | grep -E '(^|/)\.(keep|placeholder|gitkeep_)$|(^|/)PLACEHOLDER|(^|/)\.empty$'
（0 件）
```

### 軸 3 — 中身で引く（追跡下の 0 バイトファイル。`.gitkeep` を除く）

```console
$ git ls-files -z | xargs -0 -I{} sh -c 'test -s "{}" || echo "{}"' | grep -v '\.gitkeep$'
src/ai-stock-trading            ← submodule の gitlink（ファイルではない）
src/plop-templates/feature/gitkeep.hbs   ← 🔴 空枠を生む側の雛形
```

**軸 3 が本件の再発防止の本体を出した。** 軸 1 だけで止めていたら `gitkeep.hbs` は
「`.gitkeep` という名前ではない」ため落ちていた。

### 軸 4 — 文言で引く（`.gitkeep` / 空枠 / 枠のみ / 空フォルダ / 枠置き / 枠だけ）

追随が要る live な記述として `src/plopfile.js` / `src/plop-templates/feature/*.hbs` /
`templates/unit-template/README.md` / `templates/unit-template/frontend/src/features/sample/hooks/useSampleFilter.ts`
を検出した（`src/README.md`・`docs/tech/tech-requirements.md`・`src/platform/frontend/README.md`
はバックエンドまたはユニット直下の話であり、本作業では偽にならない）。

### 70 件の内訳と本作業での扱い

| # | 群 | 件数 | 扱い |
| --- | --- | ---: | --- |
| A | `src/knowledge/frontend/src/features/*/{api,hooks,stores,types}/.gitkeep` | **30** | **本 PR で撤去する**（§3） |
| B | `templates/unit-template/frontend/src/features/sample/stores/.gitkeep` | 1 | **本 PR で撤去する**（#1100 の宣言領域。2 つの雛形を揃える） |
| C | `src/{knowledge,platform}/frontend/src/{app,assets,hooks,locales,stores,testing,types,utils}/.gitkeep` | 13 | **別 issue へ出す**（§5） |
| D | `templates/unit-template/frontend/src/{app,assets,components,config,hooks,lib,locales,stores,testing,types,utils}/.gitkeep` | 11 | **別 issue へ出す**（C と同じ規範。C と同時に判断する） |
| E | `docs/<種別>/.gitkeep`（14 種別フォルダ）・`.ai-context/specs/.gitkeep` | 15 | **残す**（§6 に理由） |
| 合計 | | **70** | |

### 🔴 issue #1100 の数えとの差

issue は `hooks/` 13 ＋ `stores/` 13 = **26 件**を挙げている。**実測は群 A だけで 30 件**である。

| 差分 | ファイル | issue が落とした理由 |
| --- | --- | --- |
| +1 | `sc02-results/types/.gitkeep` | issue の走査が `*/stores/*` と `*/hooks/*` の 2 本しか引いていない |
| +1 | `sc04-wiki/api/.gitkeep` | 同上（`api/` を引いていない） |
| +1 | `sc04-wiki/types/.gitkeep` | 同上 |
| +1 | `sc05-documents/types/.gitkeep` | 同上 |

**母集合の規則 2（あり得る形をすべて列挙してから引く）に対する破れである** —— 6 分割は
`api/ components/ hooks/ routes/ stores/ types/` の 6 区分なのに、走査は 2 区分しか見ていない。
**feature の数（13）は一致する**（`.gitkeep` を持つ feature は 13 件）。

なお #1066 が「19 feature」と数えた母集合は、**現在は 17 feature** である（`abac` /
`scope-filter` が #1065 で `src/lib/` へ移った）。本作業の判断には影響しない。

### 除外したものとその理由

- 群 C・D（ユニット直下の枠 24 件）: **規範が別である。** 群 A が問われているのは計画
  §ディレクトリ構成 の **feature 内部 6 分割**、群 C・D は同節の**ツリー最上位**である。
  最上位は「可変ユニットは持たないのが常態（platform 側が持つ）」という別の事情を抱えており
  （`src/platform/frontend/README.md` は現に「消さない」と書いている）、同じ diff で扱うと
  判断の根拠が混ざる。#1100 の宣言ファイル領域にも入っていない。→ 別 issue。
- 群 E（`docs/` の種別フォルダ・`.ai-context/specs/`）: **「標準構成要素の枠」ではない。**
  `docs/README.md` が定める 19 種別の**出力先**であり、`/new-spec <種別>` が書き込む宛先が
  先に在ることに意味がある（`ADR-0065` 決定 4 が撤回したのは「構成要素が揃って見える」枠であって、
  出力先ディレクトリではない）。→ 残す。
- `src/ai-stock-trading`: submodule。本リポジトリからファイルを足さない（IADR-0120）。

## 3. feature ごとの判断（群 A・30 件）

**#1100 の制約に従い、実体のあるコードは 1 行も移さない。** 混ざりを見つけたら別 issue へ切る
（「消した」と「直した」を同じ diff に混ぜない）。判定の物差しは IADR-0309 決定 1 と同じ:

- `hooks/` … feature 固有の**クライアント状態**（サーバー状態は `api/` の TanStack Query が持つ）
- `stores/` … 複数ルート／複数コンポーネントを跨いで生き残るクライアント状態（Zustand）
- `types/` … React にも router にも依存しない純粋な定義
- `api/` … BFF 呼び出し

| feature | 撤去する区分 | 判断の根拠（実測） |
| --- | --- | --- |
| `sc01-search` | `hooks/` `stores/` | 質問文と対象範囲は `SearchChatPage.tsx` に閉じた入力欄の値。回答は SSE（`api/useAskStream.ts` がストリーム状態を持つ）。跨いで持つものが無い |
| `sc02-results` | `hooks/` `stores/` `types/` | 検索語の単一情報源は URL `?q=`（IADR-0126 決定 3）。入力欄はレンダー中に URL へ追随する局所値。表示型は契約生成 DTO（IADR-0135 決定 1）で `../types` の import が 0 件 |
| `sc03-document` | `hooks/` `stores/` | **クライアント状態が 1 つも無い**（`useState` / `useRef` / `useReducer` が 0 件）。文書 ID は `useParams`、本文・版・提案はすべてサーバー状態 |
| `sc04-wiki` | `api/` `hooks/` `stores/` `types/` | 画面は Wiki.js への**遷移導線のみ**（IADR-0020）。`appConfig().wikiBaseUrl` を読んでリンクを描くだけで、**BFF を 1 本も呼ばない**ため `api/` も要らない |
| `sc05-documents` | `hooks/` `stores/` `types/` | 編集対象と通知は一覧ページ局所、フォーム値は `DocumentForm` 局所。DTO は契約生成、機密区分の語彙は `lib/abac`（#1065 で移送済み）で `../types` の import が 0 件 |
| `sc06-datasources` | `hooks/` `stores/` | フォーム開閉・編集 ID・通知は一覧ページ局所、属性フォームの値は各フォーム局所。`types/syncState.ts` は既に実体を持つ |
| `sc07-conversions` | `hooks/` `stores/` | 絞り込み・補正対象・再実行対象・通知は一覧ページ局所。ポーリングは `api/` 側。`types/jobStatus.ts` は実体あり |
| `sc08-analysis` | `hooks/` `stores/` | 入力の状態は React Hook Form が持ち、検証規則は `types/analysisFormSchema.ts`（Zod）にある。ストアを足すと入力の情報源が 2 本になる |
| `sc09-admin-abac` | `hooks/` `stores/` | 3 つのパネル（属性辞書・ポリシー・タグ辞書）が**それぞれ自分の入力だけを持ち、互いに共有していない**。共通化すべき状態が無い |
| `sc10-operations` | `hooks/` `stores/` | クライアント状態は表示期間 `days` の 1 つだけ。指標はすべてサーバー状態 |
| `sc11-config` | `hooks/` `stores/` | **クライアント状態が 1 つも無い**（`useState` 等 0 件）。再取得は `api/useRefreshConfigViewer` |
| `sc12-mcp-clients` | `hooks/` `stores/` | 登録・編集フォームの値が `McpClientManagementPage.tsx` 1 ファイルに閉じている。跨ぐ利用者が無い |
| `sc17-users` | `hooks/` `stores/` | 絞り込みと編集ドラフトが `UserAccountManagementPage.tsx` 1 ファイルに閉じている |

**`stores/` を 13 件すべて撤去する共通の根拠**: クライアント状態の単一情報源を URL に置くと
決めてある（IADR-0124 決定 3）。Zustand は #788 で導入済みで、唯一の利用は
`platform/frontend/src/components/ai-chat/aiChatStore.ts`（共通シェルの右レール。feature ではない）。
**「ライブラリが無いから空」ではなく、「持たないのが既定」である。**

**`hooks/` を 13 件すべて撤去する共通の根拠**: 13 画面のクライアント状態はいずれも
**1 コンポーネントに閉じている**（上表の実測）。抽出しても呼び出し元が 1 つしかない間接層が増えるだけで、
`hooks/` が意味するもの（複数コンポーネントが共有する feature 固有の状態）にならない。

### 別 issue へ出すもの（本 PR では触らない）

- `sc12-mcp-clients` / `sc17-users` / `sc09-admin-abac` の登録・編集フォームは
  **1 ファイルに 10 前後の `useState` が積み上がっており**、`hooks/` へ切り出す価値が実際にある。
  ただし**これは「空枠を消す」ではなく「実体を作る」であり、#1100 の制約が明示的に禁じている。**
  → 追随 issue を起票する（§5）。

## 4. 再発防止 —— 雛形が空枠を生まないようにする

`src/plopfile.js` は 6 区分のうち `api/` `hooks/` `types/` を実体で生成する一方、
**`stores/.gitkeep` だけを空枠として生成し続けている**（IADR-0309 決定 4）。
`ADR-0065` 決定 4 が撤回した形を雛形が再生産している。

**採る形**: **`stores/` を生成しない。** 生成物の一覧から `add('stores/.gitkeep', …)` を外し、
`src/plop-templates/feature/gitkeep.hbs`（0 バイト）を削除する。かわりに生成後の案内文へ
「クライアント状態ストアが要るとわかった時点で `stores/` を作る」ことと、その判断基準を出す。

**採らない形と理由**:

- **実体のストアを生成する** —— `stores/` を持つのが既定であるかのように読める。実測で feature の
  `stores/` 実体は 0 件であり、既定は URL である（IADR-0124 決定 3）。**逆向きの誤りを作る。**
- **対話で選ばせる** —— `plopfile.js` に既に理由が書かれているとおり、条件付きプロンプトを足すと
  `plop feature <値> …` の非対話実行が「You can not bypass conditional prompts」で止まる。
  **CI・スクリプトから叩けない生成器は使われなくなる。**

あわせて `templates/unit-template/frontend/src/features/sample/stores/.gitkeep` を撤去し、
2 つの雛形が生む形を一致させる（#1100 受け入れ基準）。

**［鮮度の是正］** `plopfile.js` の案内文が「`eslint.config.js` の lingui 適用範囲（`files`）へ
本 feature のパスを足す」と指示しているが、**#1105 が許可リストを撤去して両ユニット全体を
検査範囲にした**ため、この指示は既に偽である。触る以上、偽の指示を残さない。

## 5. 起票する追随 issue

1. **ユニット直下の枠 24 件（群 C・D）をどうするか。** 同じ `ADR-0065` 決定 4 の射程判断が要る。
   `src/platform/frontend/README.md` が「消さない」と明記しているため、消すなら同 README と
   `templates/unit-template/README.md` を同時に直す。
2. **`hooks/` へ切り出す価値のあるフォーム状態**（`sc09` / `sc12` / `sc17`）。実体を作る作業。
3. **計画への環流**（planning 側 issue）: `ADR-0065` 決定 4 をフロントエンドの feature 6 分割へも
   及ぼしてよいか。**IADR-0309 決定 3 が「裁定を計画へ求める」と書いたまま起票されていない。**

## 6. `IADR-0218` の扱い

`ADR-0065` §結果 フォローアップ 5 が「`IADR-0218` の改定または後継（決定 4 により前提を失う）」を
実装側へ求めている。

- `IADR-0218`（＋ それを改定した `IADR-0219`）は**バックエンド 8 要素標準の `.gitkeep` 枠**を
  決めたものである。前提（計画 `12_backend-application-stack` §規範性の条件節）は
  `ADR-0065` 決定 4・5・6 が撤回・改定した。
- 実体は既に消えている（`IADR-0282` 決定 3 が枠を全廃し、`ADR-0065` は「`.gitkeep` のみの層
  ディレクトリは 0 件」と実測している）。**残っているのは状態欄だけが `Accepted` のままという
  記録の食い違いである。**
- **`.claude/rules/traceability.repo.md` §Superseded の書式に従う**: 旧 ID を残し後継を併記する。
  ID を後継へ付け替えない。注記そのものへ起票 ID（#1100）を書き、`updated:` を前進させる。
  凍結の射程については同節が **「frontmatter の状態欄は対象外」**（#717 / IADR-0191 決定 2）
  と定めており、`.ai-context/adr/` は書き換え禁止の列挙（`specs/` / `superpowers/`）に無い。
  → **`status:` を `Superseded` にし、`［2026-08-31 追記 / #1100］` の追記ブロックを置く。**

## 7. 受け入れ基準（#1100 から写像）

- [x] `git ls-files | grep -E '(^|/)\.gitkeep$'` の結果に、feature 内部の区分が 1 件も無い
- [x] 残った `.gitkeep` は群ごとに理由を持ち、走査結果を PR 本文に貼る
- [x] feature ごとの判断が PR 本文にある（まとめて 1 行で済ませない）
- [x] planning#445 の裁定との整合を PR 本文で述べる
- [x] 2 つの feature 雛形が生む構成が一致する
- [x] **雛形から実際に feature を生成し、`.gitkeep` が 1 件も出ないことを実測する**
- [x] `pnpm run lint` / `typecheck` / `test` / `build` / `format:check` が通る
- [x] `node scripts/check-route-manifest.js` ほか文書・トレーサビリティ検査が通る

## 8. 検査器を足すか（#1100 判断事項 3）

**足さない。** `CLAUDE.md` の条件は「**同型の**事故が 2 回起きたら」である。#1078（lingui の
`files` 列挙の伸ばし忘れ）・#1066（feature 分割の作り忘れ）・#1100（撤回された枠の残置）は
**「伸ばし忘れ」という抽象では同型だが、機械検査の対象としては同型ではない** ——
それぞれ検査すべき不変条件が違う（i18n カタログの網羅 / feature 区分の実体 / 撤回済み規範の残置）。
本件の再発経路は**雛形が空枠を生むこと**であり、§4 でその生成そのものを止めた。
生成器を通さず手で作る経路は残るが、それは #1066 の残余リスクとして既に記録がある
（IADR-0309 残余リスク）。**2 回目が起きたら検査を足す。**

## 9. ［2026-08-31 追記 / #1100］実行結果

### 採番の改番

起案時は `IADR-0317` を採ったが、中断中に `develop` が `c45533bc` まで進み **`IADR-0317` /
`IADR-0318` / `IADR-0319` が先に着地した**。`.claude/rules/traceability.md`「採番衝突時の改番手順」
（**先着尊重。後発は次の空き番号へ改番し、欠番を作らない**）に従い **`IADR-0320`** へ改番した。
参照は 6 箇所（新 IADR 本体・`IADR-0218`・`IADR-0219`・索引 3 行・`src/plopfile.js`・
`useSampleFilter.ts`）。`node scripts/check-adr-numbering.js` が緑であることで取り残しが無いことを
確かめた（1 回目は索引の後継リンク 2 行を取り残し、`check-doc-links.js` が捕まえた）。

### 雛形の実測（受け入れ基準 3）

`develop` 取り込み後の木で `plop feature knowledge/frontend sc99-scaffold-probe …` を実走させた。

```console
$ find .../sc99-scaffold-probe -mindepth 1 -maxdepth 1 | sort
api  components  hooks  index.ts  routes  types      ← stores/ は無い
$ find .../sc99-scaffold-probe -name '.gitkeep' | wc -l
0
$ find .../sc99-scaffold-probe -type f -empty | wc -l
0
```

`templates/unit-template/frontend/src/features/sample` の直下も同じ 6 項目であり、**2 つの雛形が
生む形は一致した**。確認後、生成物は削除した。

### 最終走査（受け入れ基準 1・2）

```console
$ git ls-files | grep -E '(^|/)\.gitkeep$' | wc -l
39                                    ← 着手時 70。31 件を撤去
$ git ls-files | grep -E 'features/[^/]+/[^/]+/\.gitkeep$' | wc -l
0                                     ← feature 内部の空枠は 0 件
```

残る 39 件の内訳: `docs/<種別>` 14 ／ `.ai-context/specs` 1（§2 群 E。**残す**）／
ユニット直下 実装 13 ・ 雛形 11（§2 群 C・D。**#1122 へ分けた**）。

### 通した検査

`pnpm run lint`（error 0 / warning 10・既存）／ `typecheck` ／ `test` ／ `build` ／ `format:check`、
`check-route-manifest` ／ `check-chunk-budget`（床 617.16 kB のまま。**0 バイトのファイルしか
消していないので当然である**）／ `check-i18n-catalogs` ／ `check-adr-numbering` ／
`check-commit-messages` ／ `check-trace-blocks` ／ `check-doc-links` ／ `check-doc-updated` ／
`check-doc-type-vocabulary` ／ `check-doc-status-vocabulary` ／ `check-plan-id-qualification` ／
`check-cross-repo-refs` ／ `gen-knowledge-graph --check` ／ `REQUIRE_REPO_TESTS=1 scripts.test.js`（668 件）。

**`scripts.test.js` は 1 回目に `title-too-long`（`IADR-0320` の索引タイトル 365 字 > 上限 200）で
落ちた。** baseline へ足さず要約を 165 字へ縮めて直した（ratchet の趣旨は「新規混入は fail」である）。

### 落ちたテストと、それが本件由来でないことの実測

| テスト | 判定 |
| --- | --- |
| `platform/frontend/src/lib/api/orvalMutator.test.ts`（1 件） | **環境差。** Node 24 で `Blob.arrayBuffer` が生えない。`git diff --name-only origin/develop HEAD -- src/platform src/packages` が **0 件**であり、**この試験と依存の全ファイルが `origin/develop` とバイト単位で同一**である。すなわちここで走らせているのは基点の内容そのものである。CI は Node 22 |
| `sc10-operations` ほか 2 件（初回のみ） | **負荷由来の揺れ。** 全体実行の 1 回目だけ 5000ms でタイムアウトし、**単体実行では 577ms で通り**、全体実行の 2 回目も通った |

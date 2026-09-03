---
title: 作業仕様書 — 計画 ADR レンジを ADR-0001..0077 へ更新する（planning の新 ADR 9 件の前提）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0069
  - ADR-0070
  - ADR-0071
  - ADR-0072
  - ADR-0073
  - ADR-0074
  - ADR-0075
  - ADR-0076
  - ADR-0077
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0069_frontend-scaffolding-frames-and-absence-semantics.md (Accepted 2026-09-02)
  - planning:projects/microservices-platform/07_adr/ADR-0070_pdf-body-extraction-and-ingest-format-set.md (Accepted 2026-09-03)
  - planning:projects/microservices-platform/07_adr/ADR-0071_search-trend-minimum-count-threshold.md (Accepted 2026-09-03)
  - planning:projects/microservices-platform/07_adr/ADR-0072_usage-event-subject-and-retention.md (Accepted 2026-09-03)
  - planning:projects/microservices-platform/07_adr/ADR-0073_wikijs-ui-not-exposed-sc04-via-gateway.md (Accepted 2026-09-03)
  - planning:projects/microservices-platform/07_adr/ADR-0074_owner-mapping-table-container-in-sc06.md (Accepted 2026-09-03)
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md (Accepted 2026-09-03)
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md (Accepted 2026-09-03)
  - planning:projects/microservices-platform/07_adr/ADR-0077_operation-semantics-in-three-level-slice.md (Accepted 2026-09-03)
related_specs:
  - ./20260830_issue-1060_plan-adr-range-0066.md
issue: ""
---

# 作業仕様書 — 計画 ADR レンジを `ADR-0001..0077` へ

## 目的と射程

`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節の計画 ADR レンジ宣言を
`ADR-0001..0068` → `ADR-0001..0077` へ更新する。**同節は `check-commit-messages.js` /
`check-trace-blocks.js` の一次情報**であり、更新しないまま `ADR-0069`〜`ADR-0077` を参照すると
コミット件名・PR タイトル・trace ブロックの値域検査がすべて落ちる。

**射程はレンジ宣言 1 行と、その追随記録（別紙への 1 世代分の追記）に限る。**
9 件の ADR の中身を実装へ反映する作業は、本 PR とは別に受け皿 issue で持つ。

## 計画側の実在確認（planning は submodule ではないため GitHub API で直接読む）

```console
$ gh api "repos/endazon/project-planning/contents/projects/microservices-platform/07_adr" \
    --jq '[.[].name | select(startswith("ADR-"))] | length'
77

$ gh api "repos/endazon/project-planning/contents/projects/microservices-platform/07_adr" \
    --jq '[.[].name | select(startswith("ADR-")) | .[4:8]] | sort | .[0] + ".." + .[-1]'
0001..0077
```

**77 ファイルが `0001..0077` に収まる** —— したがって欠番は無い。9 件とも frontmatter は
`status: Accepted`（本文を 9 件とも全文取得して確認した）。

| ADR | planning コミット | planning PR | 環流 issue | 表題（要約） |
| --- | --- | --- | --- | --- |
| `ADR-0069` | `6bdc950` | planning#517 | planning#510 | フロントエンドにも空枠を置かない |
| `ADR-0070` | `1824ec5` | planning#521 | planning#509 | PDF の本文抽出は pandoc の外・取り込み形式の集合は計画が持つ |
| `ADR-0071` | `e6b713f` | planning#525 | planning#514 | 検索傾向は出現件数のしきい値で伏せる |
| `ADR-0072` | `6e7f787` | planning#526 | planning#515 | 利用イベントに利用者識別子を保持しない・保持期間 90 日 |
| `ADR-0073` | `23390d9` | planning#528 | planning#516 | Wiki.js 本体 UI は露出しない・SC-04 は前段ゲートウェイ経由 |
| `ADR-0074` | `37e732a` | planning#529 | planning#518 | owner の写像表の器は SC-06 が持つ |
| `ADR-0075` | `2f1bc8d` | planning#530 | planning#520 | east-west gRPC の移行順序は基盤先行 |
| `ADR-0076` | `67ceb5b` | planning#531 | planning#524 | SLO の統制は「評価対象があること」まで含む |
| `ADR-0077` | `e6fd295` | planning#532 | planning#527 | 「操作」の語義は契機の形で決めない |

## FR / UC / SC / NFR のレンジは動いていない（自分で走査した）

**「不動」も陰性の主張であるため、走査そのものを検証できる形で引いた。**

```console
$ gh api ".../02_requirements/01_requirements.md" -H "Accept: application/vnd.github.raw" \
    | grep -oE '\b(FR|NFR)-[0-9]{2}\b' | sort -u
FR-01 … FR-22 / NFR-01 … NFR-27   （22 件 / 27 件）

$ gh api ".../03_usecases/01_usecases.md"  … | grep -oE '\bUC-[0-9]{2}\b' | sort -u
UC-01 … UC-11   （11 件）

$ gh api ".../05_screens/01_screens.md"    … | grep -oE '\bSC-[0-9]{2}\b' | sort -u
SC-01 … SC-21   （21 件）
```

**いずれも列挙が非空で返っている**（＝走査が機能している陽性対照そのものである）。
`FR-01..22` / `UC-01..11` / `SC-01..21` / `NFR-01..27` は宣言と一致し、**更新は不要**。
🔴 **`ADR-0073` は SC-04 §ルート を撤回し、`ADR-0074` は SC-06 の欄を増やすが、
どちらも既存 SC の内容の改定であって新しい SC 番号を作っていない。**

## 母集合の引き方（`.claude/rules/traceability.repo.md` §是正・追随の母集合 規則 9）

**誤りの側の文字列で追跡下の全ファイルを走査した**（`src/ai-stock-trading` は submodule のため除外）。

```console
$ git grep -n "0001\.\.0068" -- . ':!src/ai-stock-trading'
.ai-context/adr/IADR-0324_unowned-plan-id-mutation-uses-synthetic-range.md:63
.claude/rules/traceability.repo.md:7
docs/how-to/plan-id-range-history-annex.md:27
docs/how-to/plan-id-range-history-annex.md:29
scripts/scripts.repo.test.js:1376
```

5 件の内訳と除外理由:

| 分類 | 件数 | 扱い |
| --- | ---: | --- |
| **レンジ宣言そのもの**（`.claude/rules/traceability.repo.md:7`） | **1** | 🔴 **本作業の対象** |
| `docs/how-to/plan-id-range-history-annex.md:27,29`（5 回目の記録） | 2 | **過去の記録であり正しい。書き換えない。代わりに 6 回目を追記する**（規則 10） |
| `.ai-context/adr/IADR-0324:63` | 1 | **合成レンジを説明する凍結記録**。`UC-01..12` という実在しない値を含むことから分かるとおり合成フィクスチャの説明であり、実レンジの写しではない。**`.ai-context/` の確定済み記録は書き換えない** |
| `scripts/scripts.repo.test.js:1376` | 1 | **テスト内の合成フィクスチャ**（同じく `UC-01..12` を含む）。実ファイルを読んでおらず値域の検査対象ではない。対象外 |

**新 ADR 9 件を引く既存の記述は 1 件も無い**（＝レンジ更新以外の追随先が無い）。陽性対照を対で置いた:

```console
$ git grep -nE "(^|[^I[:alnum:]])ADR-00(69|7[0-7])" -- . ':!src/ai-stock-trading' | wc -l
0
$ git grep -nE "(^|[^I[:alnum:]])ADR-0065"          -- . ':!src/ai-stock-trading' | wc -l
218
```

**同じ正規表現の形で 218 件を返す対照があるため、0 件は走査の失敗ではない。**

## 規則 10 —— この変更で新たに誤りになる自分の記述

- `docs/how-to/plan-id-range-history-annex.md` は**世代ごとの追随記録**を持つ。`0001..0068` で止めたままだと
  別紙が「最後の引き直しは 5 回目」と読める状態になる。**6 回目を追記する。**
- **世代数（「N 世代目」という総数）は書かない** —— `.claude/rules/traceability.repo.md` が
  「別紙が増えるたびに腐る導出値である」として禁じている。見出しの `［日付・N 回目］` は既存の書式であり維持する。
- 別紙の trace ブロック（`adrs:`）は `ADR-0068` までを列挙している。**本追記で引く新 ADR 9 件を足す**
  （足さないと `gen-knowledge-graph` / `check-trace-blocks` から見て本文が引く ID が trace ブロックに無い状態になる）。
- 別紙は `docs/` 配下であるため**表示テキストへ計画 ID を書けない**。**ただし本別紙は既存の全世代で
  ID そのものを表示テキストに書いている**（ID の記録が本文の主題であり、隠すと記録が成立しない）。
  **既存の書き方に倣い、`check-trace-blocks.js` が実際に通ることで担保する。**

## 受け入れ基準

- [x] `.claude/rules/traceability.repo.md` §起点 ID の種別 のレンジ表記が `` `ADR-0001..0077` `` である（欠番なしの宣言も維持）
- [x] `FR-01..22` / `UC-01..11` / `SC-01..21` の 3 種は不動のまま（走査で確認済み）
- [x] `docs/how-to/plan-id-range-history-annex.md` に 6 回目の追随記録がある（日付・planning のコミット・planning の PR / issue を伴う）
- [x] コミット件名 `docs(ADR-0077): …` が `check-commit-messages.js` の単一件名モードを通り、**`ADR-0078` は落ちる**（陰性対照）
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑
- [x] `node scripts/check-reading-budget.js` が予算内
- [x] `node scripts/check-doc-links.js` / `check-trace-blocks.js` / `check-doc-updated.js` が緑

## テスト方針

検査器そのものを変更しないため新規テストは書かない。**値域が実際に効いていることは変異試験で示す**
（宣言だけでは検査器が働いた証跡にならない）—— `ADR-0077` を含む件名が通り `ADR-0078` が落ちることを対で見る。

## 実測（2026-09-03）

### 1. 単一件名モード —— 陽性と陰性を対で置いた

```console
$ node scripts/check-commit-messages.js \
    --title "docs(NFR,ADR-0069,ADR-0077): 計画 ADR レンジを ADR-0001..0077 へ追随させる" --author endazon
✓ PR タイトルが規約に適合
exit=0

$ node scripts/check-commit-messages.js --title "docs(NFR,ADR-0078): 陰性対照" --author endazon
✗ PR タイトルが規約違反:
      - 起点 ID "ADR-0078" が planning の 07_adr/ に実在しない（誤記・廃止の可能性）
exit=1
```

**境界がちょうど `0077` と `0078` の間にあることが、宣言レンジを実際に読んでいる証跡である。**

### 2. 変異試験 —— trace ブロックの値域も同じ宣言を読んでいる

trace ブロックの `adrs:` へ `ADR-0078` を一時的に入れると落ち、戻すと通る。

```console
$ node scripts/check-trace-blocks.js
  docs/how-to/plan-id-range-history-annex.md
    - trace ブロック adrs: 計画 ADR レンジ（ADR-0001..0077）外です: ADR-0078

$ # 戻して再実行
$ node scripts/check-trace-blocks.js
[check-trace-blocks] OK: 167 件の Markdown に trace ブロックの違反はありません。
```

**エラー文の「ADR-0001..0077」が、更新後の宣言レンジを読んでいることを示している。**

### 3. その他の検査

```console
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js | tail -1
✓ 677 tests passed

$ node scripts/check-reading-budget.js | tail -1
[check-reading-budget] OK: 3 件の集合すべてが予算 51,200 バイト内である。
    ok  Claude Code: 46,044 バイト（予算 51,200 の 89.9%）

$ node scripts/check-doc-links.js  > /dev/null; echo $?
0

$ node scripts/check-doc-updated.js | tail -1
[check-doc-updated] OK: base 以降にコミットがありません。
```

**必読規約は 46,044 バイト（予算の 89.9%）**。本変更でのバイト増は宣言レンジの
`0068` → `0077` の 0 バイトであり、予算は動いていない。

## 計画書との差異

- 差異: なし。**本 PR はレンジ宣言の追随のみを行い、9 件の ADR の中身は受け皿 issue へ渡す。**

## 未決事項

- なし（レンジの範囲では未決は無い）。9 件の ADR の実装反映は受け皿 issue 側の論点である。

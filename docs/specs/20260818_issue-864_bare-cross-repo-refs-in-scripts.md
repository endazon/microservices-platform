---
title: 作業仕様書 — 検査器の死角に残る裸の他リポジトリ issue 番号を修飾する（#864）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0115
  - IADR-0140
  - IADR-0141
  - IADR-0169
  - IADR-0183
  - IADR-0192
  - IADR-0201
author: claude
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)"
  - "../../planning/docs/ai-implementation-workflow-guide.md"
related_specs:
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
  - "../adr/IADR-0140_cross-repo-issue-ref-checker.md"
  - "../adr/IADR-0169_cross-repo-ref-scan-beyond-markdown.md"
  - "../adr/IADR-0192_kit-sync-classification-and-check.md"
---

# 仕様書: 検査器の死角に残る裸の他リポジトリ issue 番号を修飾する（#864）

> 波 13 レーン D。起点 ID は**無採番 `NFR`**（場合 2・メタ作業。`.claude/rules/traceability.md`
> 「起点 ID の種別」／[[IADR-0179]]）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 該当なし（工程の規律であり、稼働する製品の要件ではない）
- ユースケース（UC）/ 画面（SC）: 該当なし
- 関連 ADR: [[IADR-0115]]（キットを足場の単一情報源とし固有デルタを 4 種に限定）/
  [[IADR-0140]]（クロスリポジトリ issue 参照の検査器）/ [[IADR-0169]]（走査を Markdown 以外へ拡張）/
  [[IADR-0192]]（キット分類表と突合検査）
- 規約の正本: `.claude/rules/traceability.md`「クロスリポジトリの issue / PR 番号の修飾」＋
  `.claude/rules/traceability.repo.md`（短縮形 `planning#NNN` / `AST#NNN` に寄せる）

## 目的・背景

裸の `#NNN` は GitHub 上で**本リポジトリの** issue / PR へ自動リンクする。他リポジトリの番号を
裸で書くと無関係な issue へ誤リンクする。`.claude/settings.json` の `"//"` は
`planning#146 / planning#149 / planning#155 / planning#160 / planning#163 / planning#168` について
**同番号の無関係な issue が本リポジトリにも実在する**と名指ししている。

機械検査 `scripts/check-cross-repo-refs.js` には**構造的な死角が 2 つ**ある。

1. **走査範囲の死角**: 置換点 `EXCLUDED_DIRS = ['scripts/']` により **`scripts/` の非 Markdown
   73 件**を走査対象から外している（検査器・自己試験フィクスチャ・baseline が住む場所であり、
   違反の文字列を書くことが仕事であるため。**この除外は意図的**で、本作業では変更しない）。
2. **検出型の死角**: 検出する 4 型は「長い表記」「列挙形の修飾漏れ」「空白区切り」「owner 誤り」で
   あり、**修飾語を伴わない単独の裸 `#NNN`** はどの型にも当たらない。**この死角は走査範囲の内側
   （`.md` を含む全ファイル）にも存在する。**

よって **`node scripts/check-cross-repo-refs.js` が緑であることは、本作業の達成の証拠にならない**
（この範囲は元々検査対象外である）。

## 対象範囲

- **対象**: 追跡下の全ファイルのうち、下記「編集可能領域」に属するもの。
- **対象外（本作業では触らない）**:
  - `planning/` submodule ・ `src/ai-stock-trading` submodule
  - 確定済み（`status: done`）の `docs/specs/` と `docs/adr/` の**本文**（波 13 の並行レーンとの
    ファイル領域非重複を保つため）
  - `CLAUDE.md` / `.claude/rules/`（必読規約の総量予算 51,200 B を動かさない）
  - キット分類 **A**（バイト一致）のファイル —— 後述「§ 判断: 分類 A を編集しない」
  - **検査器の追加・`EXCLUDED_DIRS` の変更**（issue 本文が明示的に禁じている）

## 母集合の引き方（[[IADR-0141]] 決定 1 の規則 1〜6 ／ `traceability.repo.md` の規則 9・10）

**誤りの側から引いた。拡張子で絞らず、パス除外（`:!planning` / `:!src/ai-stock-trading`）のみで
取った。行フィルタで絞らず、走査の出力を `head` / `sed` で切らずに読んだ。軸は 7 本引いた。**

走査基準: `git ls-files -- ':!planning' ':!src/ai-stock-trading'` = **1,869 件**。
裸の `#NNN`（直前が `\w` / `/` / `-` でなく、直後が数字でないもの）の**全出現は 14,863 件**である。
内訳: Markdown 11,648 ／ `scripts/` 以外の非 Markdown 1,680 ／ **`scripts/` の非 Markdown 1,535**。

**この 14,863 件の圧倒的多数は本リポジトリ自身の issue を指す正しい記述である**（裸の `#NNN` は
本リポジトリを指すのが正）。よって全数を「違反候補」として読むことはできず、**誤りの側を絞り込む
軸を複数引いて交差させた**。

| # | 軸 | 引き方 | 生の件数 | 真の違反 |
| ---: | --- | --- | ---: | ---: |
| 1 | **パス軸**: 検査器が除外している範囲 | `scripts/` の非 Markdown 73 件の全裸番号 | 1,535 | 1 |
| 2 | **番号軸**: `settings.json` が名指しする 6 番号 | `#146 / #149 / #155 / #160 / #163 / #168` の裸出現を全ファイルから | 117 | 0（在処は分類 A・確定済み記録・コードスパン） |
| 3 | **文脈軸（広）**: 他リポマーカー ±3 行 | `planning` / `計画リポ` / `AST` / `キット` 等 | 4,953 | —（広すぎるため軸 5 へ精密化） |
| 4 | **番号集合軸**: 既知の他リポ番号 | `planning#NNN` / `AST#NNN` として既出の 213 番号と同値の裸出現 | 2,425 | —（MSP と planning の採番域が重なり弁別力が無い） |
| 5 | **形状軸**: 他リポ名と裸番号が**空白・句読点以外のつなぎ**で同一行に結びつく形 | S1（名前→つなぎ→`#`）/ S2（`#`→つなぎ→名前）。つなぎ 0 文字（＝正しい修飾）と、既存検査器の型 2・型 3 が見る区切りのみの形は差し引いた | 351 | 2（うち 1 は軸 6 と重複） |
| 6 | **主題軸**: 主題が「キット / 計画リポ」である記録の**全文読み** | `feedback/*kit*` `feedback/*ai-workflow*` `feedback/*ai-review*` 等 ＋ `.github/workflows/claude-*.yml` ＋ `AI_SETUP.md` / `AGENTS.md` / `docs/ai-workflow.md` | 全文 | **29** |
| 7 | **規則 10（自己引き直し）**: 本作業で修飾した番号を持つ他の箇所 | 是正後に同じ 21 番号で編集可能領域を全走査し直す | 72 | **0**（§検証結果） |

**重複を排した真の違反は 31 occurrence / 4 ファイル**（軸 1 = 1、軸 5 の固有分 = 1、軸 6 = 29）。

**軸 5 だけでは足りなかったことが実測で出た**（規則 5）。`feedback/20260803_ai-workflow-...md` の
違反 15 件のうち**軸 5 が捕まえたのは 1 件だけ**である —— 残りは修飾語が
**前の行**にあるか（`planning#145 / planning#146 /` ↓ `#155 / #157 / …`）、見出し
（`### 17. #138 の反映で…`）で修飾語を伴わないためである。**軸 6（全文読み）が本体だった。**

### 引いたが**除外した**もの（と理由）

| # | 引いたもの | 件数 | 除外理由 |
| ---: | --- | ---: | --- |
| E1 | `scripts/check-ai-workflow-config.js` の裸 `#122 / #130 / #131 / #134 / #136 / #149 / #155 / #160 / #163`（issue 本文が名指しした `:305` を含む） | **26 件**（同ファイルの裸番号は**全件**が計画リポの issue） | **キット分類 A（バイト一致）**。後述「§ 判断」 |
| E2 | `scripts/check-permission-denials.js` の裸 `#146 / #149 / #155 / #158 / #160 / #391 / #395` | **28 件**（同上・全件） | 同上（分類 A） |
| E3 | `scripts/scripts.test.js` の裸 `#136 / #137 / #138 / #139 / #140 / #142 / #146 / #148 / #152 / #153 / #155 / #158 / #160` | **20 件**（裸番号 40 件のうち。残り 20 件は `(#123)` / `(#100)` / `#319` 等の**試験フィクスチャ**で issue 参照ではない） | 同上（分類 A） |
| E4 | `docs/adr/IADR-0066_local-k8s-dev-environment.md:34` の `（特に #121 = 取引サイクル…）`＝ `AST#121` | 1 件 | **真の違反だが領域外**（`docs/adr/` 本文）。同ファイル `:30` は `AST#121` と正しく書いており**内部不整合**である。**申し送り（後述）** |
| E5 | 確定済み `docs/specs/` の AST 参照 —— `20260712_issue-245:85`（`発注 = #13`）／ `20260713_issue-266:39`（`#121`）／ `20260718_issue-283:51`（`#185`）／ `20260718_issue-287:37`（`#192` `#194` `#195`）／ `20260718_issue-288:52`（`#195` `#197`）／ `20260801_issue-290:115`（`#290`）／ `20260814_issue-719:49`（`#476`） | **10 件 / 7 ファイル** | **確定済み（`status: done`）の本文**につき領域外。**申し送り** |
| E6 | `docs/specs/20260818_issue-835_...md:239` / `20260818_wave12-audit-followup.md:354` の `#163` | 2 件 | **コードフェンス／インラインコードの中**（規約: literal な引用は表記規約の対象外）＋ 確定済み |
| E7 | `.claude/rules/traceability.md` ・ `scripts/check-cross-repo-refs.js` 自己試験の `誤: planning#146 / #149 / #160` 等 | 多数 | **意図的な反例**。コードスパン／フィクスチャであり、直すと検査器の自己試験と規約の例示が壊れる |
| E8 | `feedback/README.md:76` / `scripts/check-feedback-dispatched.js:57,170,370` / `scripts/scripts.test.js:1407` の `planning_issue: #319` | 5 件 | **frontmatter の定義済みフィールド書式**。鍵 `planning_issue:` が名前空間を明示しており、値を `planning#319` にすると `check-feedback-dispatched.js` のパースが壊れる |
| E9 | `CHANGELOG.md` の裸 `#146 / #149 / #155 / #160 / #163 / #168` ほか | 6 件 | **`gen-changelog.js` の生成物**（手で書き足さない）。かつ末尾 `(#NNN)` はスカッシュ既定件名＝自リポ PR 番号で**正しい** |
| E10 | `docs/screens/SC-05_document-management.md:267` の `（#7）` | 1 件 | **issue 番号ではない可能性が高い**。同ファイルの追記表が当該項目を **`Q7 / Q8 / 派生 Q30`**（質問票の番号）へ写像しており、`#7` は質問番号 Q7 を指すと読める。断定できないため触らない。**申し送り** |
| E11 | `docs/adr/IADR-0139:175` の `#1・#2`（planning 側の連番） | 2 件 | **引用**であり、直後の `:176` が「**裸番号は planning 側の連番**」と明示している。かつ `docs/adr/` 本文（領域外） |
| E12 | `docs/adr/IADR-0186:106,153` / `docs/adr/README.md:242` の `AST 側は #722` | 3 件 | **違反ではない。`#722` は MSP の issue である**（`docs/specs/20260813_issue-716_...:191` が「`security.yml` の走査範囲の見直し —— **#722** で天秤として扱う」と書いており、MSP 自身の申し送り issue と判る） |
| E13 | `docs/adr/IADR-0022:44` の `計画側は Issue #69` | 1 件 | **違反ではない**。同 ADR `:42` が「`claude-fable-5` と GitHub Copilot SDK は未実装だった（Issue #69）」＝**実装側の欠落**を指しており、MSP の issue と読める |
| E14 | `scripts/` MSP 固有スクリプトの裸番号（`k8s-local-images.sh` の `#570 / #283 / #287 / #288`、`check-image-mapping.js:444`、`check-test-traceability.js:529`、`lib/excluded-units.js:145` ほか） | 実測 1,534 件のうち該当多数 | **違反ではない**。いずれも MSP 自身の issue 番号であり、裸で書くのが正しい |

**「反映先」は issue 本文から転記していない。上表はすべて本作業で引き直した結果である。**

## 判断: キット分類 A のファイルを編集しない（issue 本文の名指し対象を含む）

issue #864 は `scripts/check-ai-workflow-config.js:305` の裸 `#163` を名指しする。**これは実在する
違反である**（`planning#163` ＝ 読み取り専用コマンドの非対称を塞いだ計画リポの issue）。しかし
**本作業では是正しない。** 根拠は 3 つで、いずれも記録済みの決定である。

1. **[[IADR-0115]] 決定 1（Accepted）**: 分類 A は「キットの内容で上書きし、`diff` でバイト一致を
   保つ」。`scripts/kit-sync-classification.json` の `classes.A` に本ファイルが載っており、
   **実測でキット（pin `282c2d0`）とバイト一致である**（`cmp` で確認。§検証結果）。編集すれば
   `check-kit-sync.js` が落ちる。緑を保つには分類を B（種 X ＝環流債務・追跡 issue 必須）へ
   移す必要があり、**その瞬間このファイルはバイト一致検査の対象から外れる**（キット側の改善が
   機械に見えなくなる）。これは分類の変更＝新たな決定であり、本 issue の「新規 IADR を起こさない」
   という拘束と衝突する。
2. **同型の先例が既に裁定済みである**: `docs/specs/20260804_issue-478_staged-policy-citation-fix.md`
   （`status: done`）は `scripts/check-permission-denials.js` の裸 `#146 / #149 / #160` について
   **「キット由来の分類 A（バイト一致）。…キットの名前空間では裸の `#146` が正しい（planning
   リポジトリ自身の issue）。…是正するなら `/plan-feedback` による環流であって本リポジトリの
   ローカル編集ではない」**と結論している。`check-ai-workflow-config.js:305` は**同一の型**である。
3. **キット側では裸が正しい**: キットは計画リポジトリ（`project-planning`）の成果物であり、その
   名前空間では `#163` は自リポジトリの issue を指す。**配布された先でだけ誤りになる**ため、
   `.claude/rules/traceability.md` が「キット配布物の中では他リポジトリを番号で引かない」と
   戒めているのと同じ構造の**上流の課題**である。

**申し送り（本 PR では起票しない。親が起票する）**: キット `repo-template` の
`scripts/check-ai-workflow-config.js` / `scripts/check-permission-denials.js` / `scripts/scripts.test.js`
が持つ裸の issue 番号を、**キット側で** `planning#NNN` へ修飾する環流。`.claude/settings.json` は
本リポで既に「【暫定デルタ】…キット側の是正を環流したら本デルタは撤去する」と宣言しており、
**同じ暫定デルタを分類 A へ広げるのではなく、上流を直すのが宣言済みの方針**である。

## 設計（是正内容）

短縮形へ寄せる（`planning#NNN` / `AST#NNN`）。**自リポを指す裸の `#NNN` は触らない。**

| # | ファイル | 行 | 現在 | 是正後 | 根拠 |
| ---: | --- | ---: | --- | --- | --- |
| 1 | `feedback/20260803_ai-workflow-grep-sort-and-submodule-git-c.md` | 55 | `#155 / #157 / #158 / #160 / #161 / #162` | `planning#…` ×6 | 直前行が `planning#145 / planning#146 /` で終わる同一列挙。**改行をまたぐため型 2 が捕まえない** |
| 2 | 同上 | 86 | `#155 の cat/head/tail、#160 の cmp/diff` | `planning#155` / `planning#160` | 同記録 `:80` `:84` が同じ番号を `planning#160` と書いている |
| 3 | 同上 | 134 | `#147`（`（読み取り系 git の欠落）・` の直後） | `planning#147` | 全角括弧が列挙を切るため型 2 が捕まえない |
| 4 | 同上 | 135 | `#155 / #157 / #158`・`#160` | `planning#…` ×4 | 同上（行頭） |
| 5 | 同上 | 136 | `#161 / #162` | `planning#…` ×2 | 同上（行頭） |
| 6 | `feedback/20260801_impl-handoff-kit-gaps.md` | 167 | `#108`・`#110` | `planning#108` / `planning#110` | 同記録 `:138` が `planning#108`、`:139` が `planning#110` と書いている |
| 7 | 同上 | 262 | `### 17. #138 の反映で…` | `planning#138` | 直下 `:264` が `planning#138` と書いている |
| 8 | 同上 | 290 | `#96 / #104 / #108 / #111 / #114 / #117 / #121 / #126 / #130` | `planning#…` ×9 | 同行が「**起票した planning issue のうち**」と明示 |
| 9 | 同上 | 291 | `#136 / #140` | `planning#136` / `planning#140` | 同記録 `:258` `:279` が `planning#136` / `planning#140` |
| 10 | `deploy/local/README.md` | 5 | `AST#122（AST chart）・#121（K8s CronJob）` | `AST#121` | 同行が `Issue #266（MSP）` と自他を書き分けており、`#121` だけ無修飾。`docs/adr/IADR-0066:30` が `AST#121` と書いている |
| 11 | `scripts/k8s-local-up.sh` | 435 | `AST 連結は AST chart(#122) 適用後に` | `AST chart(AST#122)` | `deploy/local/README.md:17` が `AST chart（AST#122 で追加）`。**検査器が除外する `scripts/` 非 Markdown（軸 1 の唯一の実収穫）** |

合計 **31 occurrence / 4 ファイル**（`git diff --cached` から機械的に数えた。§検証結果）。

## 受け入れ基準

1. 上表 11 項すべてが是正され、`planning#NNN` / `AST#NNN` の短縮形（**空白を挟まない**）である。
2. **自リポを指す裸の `#NNN` を 1 件も書き換えていない**（`git diff` で全数確認する）。
3. `scripts/` の非 Markdown の除外（`EXCLUDED_DIRS`）と検査器の実装を**変更していない**。
4. キット分類 A のファイルが**バイト一致のまま**で、`check-kit-sync.js` が pass。
5. `CLAUDE.md` / `.claude/rules/` のバイト数が不変（`check-reading-budget.js` が同値）。
6. `planning/` と `src/ai-stock-trading` に変更が無い。
7. 確定済み `docs/specs/` / `docs/adr/` の本文に変更が無い。
8. 下記「検証」の全件で判定行が pass。

## 検証（[[IADR-0183]] の順序）

`git add -A` → 検査器 → コミット → HEAD を読む検査器。**終了コードは判定ではない。判定行を読む。**
終了コードはパイプで終端せず `cmd > log 2>&1; echo "EXIT=$?"` の形で取る。
`check-kit-sync.js` は **planning submodule を populate してから**走らせる。
`scripts.test.js` に `KIT_DIR` の skip 迂回を**付けない**。

結果は §検証結果 に記す。

## 検証結果（判定行は生の出力）

| 検査 | EXIT | 判定行 |
| --- | ---: | --- |
| `check-doc-links.js` | 0 | `[check-doc-links] OK: 705 件の Markdown に破損した相対リンクはありません（未 populate の submodule 配下 2 件は対象外 — src/ai-stock-trading: 2 件）。` |
| `check-doc-status-vocabulary.js` | 0 | `[check-doc-status-vocabulary] OK: 664 件の仕様書の status が値域に収まっています` |
| `check-doc-type-vocabulary.js` | 0 | `[check-doc-type-vocabulary] OK: 678 件の文書の type が、テンプレート 19 種類の値域に収まっています` |
| `check-cross-repo-refs.js` | 0 | `走査 1797 件 / 除外 73 件（scripts/ の非 Markdown）` ＋ `OK: 1797 件に他リポジトリ参照の表記違反はありません。` —— **緑は達成の証拠ではない**（§目的・背景。除外 73 件と単独裸番号は元々対象外） |
| `check-plan-id-qualification.js` | 0 | `[check-plan-id-qualification] OK: 1455 件に他プロジェクト ID の修飾違反はありません。` |
| `check-adr-numbering.js` | 0 | `[check-adr-numbering] OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です。` |
| `check-reading-budget.js` | 0 | `warn Claude Code: 49,885 バイト（予算 51,200 の 97.4%）` —— **着手前と同値。`CLAUDE.md` / `.claude/rules/` を増やしていない** |
| `check-kit-sync.js`（**submodule populate 後**） | 0 | `[check-kit-sync] OK: キット 117 件を分類表と突合しました（A 80 件はバイト一致 / B 25 件は固有デルタ / C 4 件は同期しない / 対象外 8 件）。` —— **分類 A は 80 件ともバイト一致のまま** |
| `STRICT_AI_WORKFLOW_CONFIG=1 check-ai-workflow-config.js` | 0 | `✓ AI ワークフローのツール許可設定に問題なし` |
| `check-ai-workflow-config.js --self-test` | 0 | `✓ 検証器の自己試験 30 件すべて合格` |
| `REQUIRE_REPO_TESTS=1 scripts.test.js`（`KIT_DIR` の skip 迂回**なし**） | 0 | `✓ 659 tests passed` |
| `check-doc-updated.js`（コミット後） | 0 | `[check-doc-updated] OK: 変更された docs/ の Markdown 1 件に updated: の据え置きはありません。` |
| `check-commit-messages.js origin/develop..HEAD`（コミット後） | 0 | `検査対象 1 件 / 除外 0 件` ＋ `✓ すべてのコミットが規約に適合` |

**分類 A のバイト一致の実測**（§判断の根拠 1）:

```console
$ cmp scripts/check-ai-workflow-config.js planning/tools/impl-handoff-kit/repo-template/scripts/check-ai-workflow-config.js && echo IDENTICAL
IDENTICAL
$ cmp scripts/check-permission-denials.js  planning/tools/impl-handoff-kit/repo-template/scripts/check-permission-denials.js  && echo IDENTICAL
IDENTICAL
$ cmp scripts/scripts.test.js              planning/tools/impl-handoff-kit/repo-template/scripts/scripts.test.js              && echo IDENTICAL
IDENTICAL
```

**是正件数の機械的な数え**（受け入れ基準 1）: `git diff --cached -U0` の `+` 行に現れる
`planning#NNN` / `AST#NNN` は 34、`-` 行に 3。差し引き **31 が新たに修飾された occurrence** である。

**規則 10 の引き直し（軸 7）**: 本作業で修飾した 21 番号
（planning 96/104/108/110/111/114/117/121/126/130/136/138/140/147/155/157/158/160/161/162、AST 121/122）
を、是正後に編集可能領域へ再走査した。**72 件の裸出現が残るが、いずれも MSP 自身の issue である**
（`#126` = SPA 基盤 CI、`#136` = SC-10、`#130` = SC-04、`#140` = SC-11 アクセス制御 等）。
**これが `.claude/settings.json` の `"//"` が警告している番号衝突そのものである** —— 同じ番号が
両リポジトリに実在するため、**番号だけでは弁別できず、1 件ずつ中身を読む以外に方法が無い**
（軸 4 が弁別力を持たなかった理由でもある）。**新たな是正対象は 0 件。**

**判定の作法**: 終了コードは判定に使わず判定行を読んだ。終了コードはパイプで終端せず
`cmd > log 2>&1; echo "EXIT=$?"` の形で取った。走査の出力を `head` / `sed` で切っていない。

> **実測メモ**: 回帰テストの実行を `... ; grep -c -E '^\s*(NG|FAIL)' log` で終端した最初の試行が
> **exit 1** を返した。原因はテストの失敗ではなく **`grep -c` が 0 件で 1 を返す**ことであり、
> 判定行は `✓ 659 tests passed` / `EXIT=0` であった。**「終了コードをパイプで終端しない」
> という作法が、まさにこの形の誤判定を防ぐためにある**ことを再確認した。

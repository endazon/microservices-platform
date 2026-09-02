---
title: パス形 `planning/<パス>` の残存記述と、AST 入れ子 planning への許可エントリを畳む（issue #1141）
type: spec
status: done
created: 2026-09-02
updated: 2026-09-02
author: claude
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0048_impl-docs-restructure.md
related_ids:
  - NFR
  - ADR-0048
  - IADR-0228
  - IADR-0331
---

# 作業仕様書: パス形 `planning/` の残存と入れ子 planning 許可を畳む（#1141）

## 起点となる計画書（トレーサビリティ）

- 起点 ID: NFR（文書整合）／計画 `ADR-0048` 決定 2。
- 実装 ADR: IADR-0228（planning 依存の全面撤去）／IADR-0331（残存記述の是正方針。§結果「積み残し」が本 issue の出所）。
- Issue: #1141（出所は #1092 / PR #1140 の母集合引き直し）。

## 目的・背景

計画 `ADR-0048` 決定 2 以降、`planning` は本リポジトリの submodule ではない。にもかかわらず
**submodule がマウントされていた前提の相対パス**（`planning/docs/glossary.md` 等）で計画リポジトリ内の
ファイルを指す live な記述が残っている（系統 1）。また `claude-*.yml` の許可リストに、AST 側の
入れ子 submodule `src/ai-stock-trading/planning` に対する `git -C` 許可が 5 エントリ×2 残っている（系統 2）。

## 対象範囲

- 対象: 系統 1（live なパス形 `planning/<パス>` の是正）と系統 2（入れ子 planning の許可エントリ撤去）。
- 対象外: 凍結記録（`.ai-context/adr` `.ai-context/specs` `.ai-context/superpowers`）／`CHANGELOG.md`
  （自動生成物）／`src/ai-stock-trading/**`（submodule）。過去形の記述・テストのフィクスチャ・
  「当時の測定」を明示した日付つき追記ブロック。

## 母集合の引き直し（🔴 自分で引いた結果）

**issue 本文の「20 行超」は他人の数えであり転記しない。** `.claude/rules/traceability.repo.md`
§是正・追随の母集合の取り方に従い、**誤りの側の語**で追跡下（除外適用後 1,943 ファイル）を走査した。
基点は `develop` の `66a78f82`。`git rev-parse --is-shallow-repository` = `false`（履歴は完全）。

### 走査した軸

| 軸 | パターン | 生ヒット |
| --- | --- | --- |
| A | `planning/`（`project-planning/` を除く） | 26 行 |
| B | `git -C [^ ]*planning` | 30 行 |
| B2 | `ai-stock-trading/planning` | 7 行 |
| C | 他の区切り（`planning\`・`../planning`・引用符でくくった `planning`） | 5 行 |
| D | frontmatter `plan_refs` の項目で `planning:` 前置が無いもの | **0 行** |
| E | `planning` の直後が英字・`#`・`:`・`/`・空白**以外**（TAB / バッククォート / 全角括弧 / wiki リンクの区切りを拾う） | 40 行 |

軸を 1 本で終わらせない（規則 5）は今回も効いた —— **軸 E だけが
`scripts/check-commit-messages.js` の定数 `DEFAULT_PLAN_PROJECTS_DIR` を出した**（軸 A の
`planning/` では `path.join(__dirname, '..', 'planning', 'projects')` の形に当たらない）。

### 陽性対照（巻き込んでいないことの対照）

- **`project-planning` の正当な参照 103 行 / 37 ファイル**（隣接クローンのパス・リポジトリ名）は
  **是正の前後で不動**である。「パス形 `planning/` が 0 件」を陰性結論として出すため、
  同じ走査条件で**必ず出るはずの陽性側**を対で置く（`absence claims need a positive control`）。
- 系統 2 の陽性対照: AST の現 pin `0844b584` の root tree は **31 エントリを返す**（`backend` /
  `frontend` / `docs` 等）。API は生きており空振りではない。そのうえで **`.gitmodules` も
  `planning` も無い** —— すなわち許可先が実在しない。

### 判定（軸 A・26 行の内訳）

**直す（live・14 行）**

| ファイル:行 | 種別 |
| --- | --- |
| `docs/functional/FR-04_ai-answer-citations.md:62` | 散文 |
| `docs/screens/SC-03_document-detail.md:33,82,225,364` | 散文・表 |
| `docs/screens/SC-05_document-management.md:85,244` | 表 |
| `scripts/check-commit-messages.js:244` | コメント |
| `scripts/check-cpm-versions.js:447` | コメント |
| `scripts/check-doc-updated.js:38` | コメント |
| `scripts/check-reading-budget.js:8,38,68,252` | コメント・出力文字列 |
| `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/SearchResultDto.cs:43` | コメント |
| `src/knowledge/frontend/src/features/sc03-document/types/attributes.ts:10` | コメント |
| `src/knowledge/frontend/src/features/sc05-documents/components/DocumentManagementPage.tsx:46` | コメント |
| `src/knowledge/frontend/src/lib/abac/confidentiality.ts:14` | コメント |
| `src/packages/ui/src/stories/Primitives.stories.tsx:96` | コメント |

**直さない（12 行）** —— 除外理由つき

| ファイル:行 | 除外理由 |
| --- | --- |
| `scripts/check-doc-links.js:96` | **過去形**（「かつては `planning/` 固定で判定していた」）。IADR-0331 §結果が「退役の根拠として残す」と決めている |
| `scripts/lib/excluded-units.js:15` | **過去形**（「一度踏んでおり」）。同型事故の記録 |
| `scripts/scripts.repo.test.js:2028` | **過去形**（#139 の作法の引用） |
| `scripts/scripts.repo.test.js:3698` | **過去形**（#756 で実走した当時の記録） |
| `scripts/scripts.repo.test.js:5459,6320` | **退役の説明**（「撤去済みの planning submodule」）。入力が取れないことを述べている当事者 |
| `scripts/check-plan-id-qualification.js:330` | **フィクスチャ**（自己テストの入力文字列） |
| `scripts/scripts.repo.test.js:3766` | **フィクスチャ**（`excluded('planning/x.md')` の期待値） |
| `scripts/scripts.test.js:1353` | **フィクスチャ** |
| `docs/how-to/plan-id-range-history-annex.md:239,261,263,274` | **「当時の測定」の日付つきブロック**（`### ［2026-08-17］` / `［2026-08-16］` 配下）。IADR-0331 決定 1 が名指しで「触らない」と定めている（軸 B のヒット） |

**軸 B / C / E で出たが直さないもの**

- `scripts/check-ai-workflow-config.js:307,541`・`scripts/check-permission-denials.js`（8 行）・
  `scripts/scripts.test.js:217,218,304`: `Bash(git -C planning log:*)` は**許可リストのラベル書式の
  例示**であり、計画リポ内のファイルを指すパス形ではない。自己テストの期待値と結合しているため触らない。
- `scripts/check-knip.js:536`・`scripts/lib/excluded-units.js:126,127,137,146`・
  `scripts/check-plan-id-qualification.js:328`・`scripts/scripts.repo.test.js:2006,2042`:
  `.gitmodules` のフィクスチャ。
- `scripts/check-reading-budget.js:196`: 自己テストの正規表現（`/^(planning|src)\//`）。フィクスチャ。
- `scripts/check-commit-messages.js:265`（`DEFAULT_PLAN_PROJECTS_DIR`）: **実在しないことが仕様である
  番兵値**。同ファイル 276 行の `projectsDir !== DEFAULT_PLAN_PROJECTS_DIR` が「既定パスなら宣言レンジへ
  進む」の分岐に使っており、値を変えると挙動が変わる。253-263 行の doc コメントが既に
  「planning submodule を撤去したため既定パスは実在しない」と正しく説明している。**コメント（244 行）
  だけを直し、定数は動かさない。**

### 🔴 判断が割れた 3 件（明示する）

`docs/screens/SC-03_document-detail.md:33,364` と `SC-05_document-management.md:85` のパス形は、
いずれも `［2026-08-10 …／#553］` の**日付つきブロックの内側**にある。

- **直した。** これらの追記はどれも「裁定は着地している。**正は** …」と**現在形で正本を指す live な
  ポインタ**であり、IADR-0331 決定 1 が触らないと定めた「**当時の測定**を明示済みのブロック」
  （`plan-id-range-history-annex.md` の `［2026-08-16］`／`［2026-08-17］` 節等）とは種別が違う。
  各ブロック内の歴史部分（SC-05 なら〔当時の理由〕以下、SC-03 なら「〔当時〕」以下）に
  パス形は含まれない。
- 直した範囲は**パス形の表記だけ**で、主張・日付・issue 番号は 1 文字も変えていない。
- 同じ 2 文書に追記外の同一参照（SC-03:82,225 / SC-05:244）があり、片方だけ直すと文書内で不整合になる。
- **逆に、真に「当時の測定」である `plan-id-range-history-annex.md` の
  `git -C planning …`（4 行）は直していない** —— 差の付け方をこの 2 例で対にして示す。

## 設計

### 系統 1: 置換の形

- 散文・コメント: `` `planning/<パス>` `` → 「計画リポジトリ `project-planning` の `<パス>`」。
- frontmatter の `plan_refs`: `planning:<パス>`（軸 D の結果 **該当 0 件**。既に全件が `planning:` 前置）。
- `docs/` 配下は**表示テキストへ計画 ID・IADR・仕様書名・修飾付き issue 参照を書かない**（trace ブロックへ）。
  本作業の置換はパス表記のみで、新たな ID を表示テキストへ持ち込まない。
- `src/` のコード注釈は**コメント行だけの最小差分**にする（`src/knowledge/**` は #1131 / #1123 /
  #1103 / #1126 / #1118 が、`Tests/**` は #1063 が並行編集中）。
- `updated:` を追随させる（`check-doc-updated` は HEAD を読む）。

### 系統 2: 許可リストの 3 系統同期

`Bash(git -C src/ai-stock-trading/planning {log,show,diff,ls-tree,grep}:*)` の 5 エントリを

1. `.github/workflows/claude-coding.yml` の `--allowedTools`
2. `.github/workflows/claude-code-review.yml` の `--allowedTools`
3. `.claude/settings.json` の `permissions.allow`

から撤去し、両ワークフローの prompt 本文・コメントにある同記述（`（同 `/planning` も）` /
`（および同 `/planning`）` / `（入れ子の `/planning` も同様）`）を畳む。

- `check-ai-workflow-config.js` の `genericBashDrift` は**両ワークフローを突き合わせる**。
  5 エントリを**両方から同時に**外すので非対称は生じず、`CODING_ONLY_BASH` /
  `REVIEW_ONLY_BASH` の更新は不要である。
- **`on:` / `jobs.<id>` / `steps` の名前は動かさない。** 変更は `claude_args:` の値と
  コメント行だけであり、起動条件・必須チェック名は変わらない（`git diff` で示す）。
- **`.claude/settings.json` は編集できなかった（実測）。** 同ファイル自身の `permissions.deny` が
  `Edit(./.claude/settings.json)` / `Write(./.claude/settings.json)` を持ち、Edit は
  `File is in a directory that is denied by your permission settings.` で拒否された。
  **`sed` 等で迂回しない**（deny は利用者が置いた権限設定であり、回避は禁止事項である）。
  よって畳めたのは 2 系統で、`.claude/settings.json` の 5 エントリは**人手で外す積み残し**として
  PR 本文へ書く。`check-ai-workflow-config.js` の非対称検査は 2 つのワークフローだけを突き合わせるため、
  この状態でも緑である（許可が余分に残るのは**危険側ではない** —— 存在しないパスへの読み取り専用
  サブコマンドであり、当たっても対象が無い）。
- 「入れ子の submodule も別パスとして列挙が要る」という**規則そのものは残す**（前方一致の性質は
  変わっていない）。「現時点の AST pin に入れ子 submodule は無い」という事実を添えて、
  次に読む人が「規則ごと消された」と誤読しないようにする（IADR-0331 決定 2 と同じ作法）。
- **同じコメントに書かれていた誤りを 1 つ正した。** `claude-code-review.yml` の当該コメントは
  入れ子を列挙する理由として「ワークフローは `git submodule update --init --recursive` で入れ子まで
  populate する」と書いていたが、**両ワークフローの `actions/checkout` は `submodules:` を
  渡しておらず、submodule を一切 populate しない**（実測）。理由の置き換えと同じ差分の中にあり、
  残すと次に読む人が偽の前提で判断するため、事実に合わせた。

## 受け入れ基準

- [ ] 現況としてパス形 `planning/<パス>` で計画リポ内を指す live な記述が 0 件（陽性対照:
      `project-planning` の 103 行が不動）。
- [ ] 過去形・フィクスチャ・「当時の測定」の日付つき追記を巻き込んでいない（上表の除外理由）。
- [ ] 許可リストの 3 系統（または編集不能を明記したうえで 2 系統）を同期し、
      `node scripts/check-ai-workflow-config.js` と `node scripts/check-permission-denials.js` が緑。
- [ ] `on:` / `jobs` / `steps` が動いていないことを `git diff` で示す。
- [ ] `check-doc-links` / `check-trace-blocks` / `check-doc-updated` / `check-cross-repo-refs` /
      `check-plan-id-qualification` / `REQUIRE_REPO_TESTS=1 scripts.test.js` が緑。
- [ ] `src/` を触るので `pnpm run lint` / `pnpm run format:check` / `dotnet format --verify-no-changes` が緑。

## 積み残し

- `Tests/**` 配下の注釈: **今回の母集合に 1 件も無かった**（軸 A で `Tests/` のヒットは 0）。
  #1063 の移送との衝突は生じないため、積み残しは無い。
- `scripts/check-permission-denials.js:255` の診断文言「相対パス `planning` を使うこと」は、
  もはや実在しないディレクトリを例に挙げている。ただしパス形 `planning/` ではなく
  `git -C` の相対パス指定一般の助言であり、同ファイルの自己テスト期待値と結合しているため
  本 PR の射程外とする（気付いた事実として記録する）。

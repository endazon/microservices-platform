---
title: feedback 記録の status を計画側の実態へ同期する（#497）
type: spec
status: done
related_ids: [NFR, FR-05, FR-12, FR-13, FR-14, FR-15, UC-06, UC-07, SC-11]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/draft/feedback/README.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/04_workflows/03_conversion-flow.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/06_technical/03_tech-stack-selection.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
related_specs:
  - "../../feedback/README.md"
  - "../screens/SC-11_configuration-viewer.md"
---

# 仕様書: feedback 記録の status を計画側の実態へ同期する（#497）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/` および
> `draft/feedback/`）を一次情報とし、本書は「この作業で何をどう変更するか」を確定する作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 非機能（**NFR**）: 計画と実装のトレーサビリティ維持。規約は
  [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) と
  [`feedback/README.md`](../../feedback/README.md)（**この `feedback/` は実装リポジトリ側の控えであり、
  原典の反映先は計画リポジトリである**）。
- 画面（**SC-11**）: 構成ビューア（[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md)）。
- 関連 FR / UC: **書き換え対象 10 件の記録が `related_ids` に挙げている起点 ID の和**である
  （各記録の frontmatter を開いて確認した）。
  - FR-12 / UC-06（正規化変換。`20260703_conversion-retry-vs-image-fallback.md` の起点）
  - **FR-13 / UC-07（Wiki 連携。`wiki-selfhosted-supersedes-adr-0011` と
    `wiki-js-deployment-follows-adr-0011` の 2 件の起点）**
  - FR-05（内部サービス認証の NFR 乖離）
  - FR-14 / FR-15（コンポーザビリティ・構成情報 API）／SC-11（構成ビューア）
  - ただし `20260704_plan-status-reflux-fr-adr.md` は**性質上 FR-01〜FR-13 / SC-01〜SC-10 をまとめて
    起点に挙げる**（計画書全体の status 環流）ため、その分は列挙に含めない（含めると全 ID の再掲になる）。
- 本リポジトリの起点: #497。

## 目的・背景

`feedback/` の記録は**控え**であり、原典（計画リポジトリ `draft/feedback/`）でトリアージが完了しても
控え側の `status:` は自動では追随しない。その結果、控えだけを見ると「未処理が多数ある」ように読める。

本作業は #497 が挙げた 10 件について、**計画側の実体（planning submodule pin `d980a01`）を自分で開いて
突合し**、控えの `status:` を実態へ同期する。あわせて、なぜその判定になったのかを後から辿れるよう、
**確認先を各記録の本文へ日付つきの追記として残す**。

**pin は動かさない。`planning/` の内容は読み取り専用の突合相手としてのみ使う。**

## 用語（status の語彙）

計画リポジトリ [draft/feedback/README.md](../../planning/draft/feedback/README.md)（`:19` / `:85`）が正である。

- 遷移: `open` → `triaged` → `accepted` / `rejected`
- **語彙は `open` / `triaged` / `accepted` / `rejected` の 4 値**。
- 同 README `:86-87` は「`20260709_composable-implementation-guide-upstream.md` が使っていた
  **`reflected` は 2026-08-04 のトリアージで `accepted` へ揃えた**（表記の揺れは解消した）」と明記する。
  → **#497 の表が求める `reflected` は既に廃語である**（§食い違い 1）。
- **裁定待ちの論点を残したまま反映した記録は `triaged` に留める**（同 `:88-90`）。

## 突合結果（10 件・**すべて自分で確認した**）

確認は planning submodule pin `d980a01`（`git submodule status` で実測）に対して行った。
**行番号は #497 の表ではなく実測値**である。「一致」列は #497 の表との突合結果。

| # | 記録 | #497 の現 status | 実測の現 status | #497 の → | **実施値** | 計画側 draft の status（実測） | 計画書側の根拠（実測行） | 一致 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `20260703_conversion-retry-vs-image-fallback.md` | open | open | accepted | **accepted** | accepted | `04_workflows/03_conversion-flow.md:65-67`（再試行／縮退／人手補正の 3 分割）・`ADR-0012:41`（失敗時の縮退方針）・`:43`（確定の経緯が本記録を参照）・`03_usecases/01_usecases.md:163`（UC-06 例外フロー） | ✅ |
| 2 | `20260703_wiki-selfhosted-supersedes-adr-0011.md` | open | open | rejected | **rejected** | **rejected** | `ADR-0011_wiki-engine.md:5` = `status: Accepted`・`:49-50` = `Supersedes / Superseded by: なし`・`:46`（確定の経緯: Issue #66 で (a) Wiki.js 配備を選択） | ✅ |
| 3 | `20260704_plan-status-reflux-fr-adr.md` | open | open | accepted | **accepted** | accepted | `02_requirements/01_requirements.md` / `03_usecases/01_usecases.md` / `04_workflows/` 3 件 / `06_technical/` 01〜10・12・13 が `status: fixed`。ADR-0001〜0017 は `ADR-0003` を除き `Accepted`（**ADR-0003 は `Superseded`**。§食い違い 3） | ⚠️ 一部 |
| 4 | `20260705_internal-service-auth-nfr-deviation.md` | open | open | accepted | **accepted** | accepted | `02_requirements/01_requirements.md:100`（認証・認可の暫定／恒久フェーズ分け）・`:107`（通信暗号化）・`:123`（暫定運用の注記）。**#497 の 99 / 105 / 121 から 1〜2 行ずれる**（§食い違い 2） | ⚠️ 行番号 |
| 5 | `20260705_wiki-js-deployment-follows-adr-0011.md` | open | open | accepted | **accepted** | accepted | `ADR-0011_wiki-engine.md:5` = `status: Accepted`・`:46`（確定の経緯が本記録を参照） | ✅ |
| 7 | `20260709_composability-open-items-resolved.md` | open | open | accepted | **accepted** | accepted | `06_technical/10_composability-design.md:176-181`（「実装で確定済み（IADR 環流、2026-07-09）」節。`:177` が本記録を参照）。**#497 の 178-181 は節の箇条書きのみを指す**（見出しは `:176`） | ⚠️ 行番号 |
| 9 | `20260709_composable-implementation-guide-upstream.md` | open | open | **reflected** | **accepted** | **accepted** | `06_technical/10_composability-design.md:96`（実装ガイドへの相互参照）・`:189`（変更履歴 2026-07-09 が本記録を参照）。`draft/feedback/README.md:86-87` が **`reflected` を廃語とし `accepted` へ揃えたと明記** | ❌ **食い違い 1** |
| 12 | `20260709_dotnet10-target-framework-deviation.md` | open | open | accepted | **accepted** | accepted | `ADR-0020_dotnet-10-upgrade.md:5` = `status: Accepted`・`06_technical/03_tech-stack-selection.md:34`（制約表 = .NET 10）・`:60`（選定根拠の見出し = .NET 10）。**残渣とされた `:55` は既に「.NET 10 / C# 13」へ是正済み**（変更履歴 `:114` が挙げるのは planning#188 / planning#189。**是正 PR は planning#194** だが、その番号は同ファイルに 1 度も現れず、`draft/feedback/20260709_dotnet10-target-framework-deviation.md` 側にのみある） | ✅ |
| 15 | `20260709_sc11-wireframe-drawio.md` | open | **closed** | rejected | **rejected** | **open** | `05_screens/01_screens.md:39`（「ワイヤーフレームは HTML モックアップを正とし、**draw.io ワイヤーフレームは作成しない**」）・`05_screens/mockups/wireframe/sc-11.html` が実在（計画リポジトリに `.drawio` は 1 件も無い） | ❌ **食い違い 4** |
| 16 | `20260710_repo-positioning-and-unit-structure.md` | open | open | accepted | **accepted** | accepted（**`20260712_` 付き**） | `ADR-0019_unit-first-repo-structure.md:5` = `status: Accepted`・`06_technical/10_composability-design.md:190`（変更履歴 2026-07-12 が `20260712_` 版を参照）。日付ずれ・内容同一を `diff` で確認（差分は status / `copied:` / トリアージ結果節のみ） | ✅ |

## #497 の表と食い違った点（**最重要**）

### 食い違い 1 — #9 の目標値は `reflected` ではなく `accepted`（**採用値を変更した**）

#497 は「計画 draft = **reflected**」を根拠に `reflected` を指定するが、pin `d980a01` の実測は
**`accepted`** である（`planning/draft/feedback/20260709_composable-implementation-guide-upstream.md:4`）。
計画リポジトリの [draft/feedback/README.md](../../planning/draft/feedback/README.md) `:85-87` は語彙を
`open` / `triaged` / `accepted` / `rejected` の 4 値に定め、**`reflected` は 2026-08-04 のトリアージで
`accepted` へ揃えた（表記の揺れは解消した）**と明記している。

→ **実測を優先し `accepted` を採用する。** `reflected` を書けば、計画側が解消したはずの表記の揺れを
実装側の控えに再導入することになる。

### 食い違い 2 — 行番号のずれ（#4）

#497 の `02_requirements/01_requirements.md:99,105,121` は、pin `d980a01` では **`:100` / `:107` / `:123`**
である。内容（暫定／恒久のフェーズ分け 2 行と暫定運用の注記）は一致するため、判定は変わらない。
**行番号は pin が動くとずれるため、本書と追記は内容で特定する。**

### 食い違い 3 — 「ADR-0001〜0017 が `Accepted` 化済み」は厳密には誤り（#3）

**`ADR-0003_messaging-masstransit-rabbitmq.md` は `Superseded`** である（ADR-0027 Wolverine による）。
`Accepted` を経たうえでの後続の状態遷移であり、#3 のトリアージ（accepted）自体は覆らない。
同様に `06_technical/` のうち **`11_mcp-server-integration.md` と `14_knowledge-graph-graphrag.md` は
`draft`** である（いずれも #3 のトリアージより後に新設された文書）。

### 食い違い 4 — #15 は**現 status が `open` ではなく `closed`**。作業は #504 で先行実施済み（**射程の重複**）

`feedback/20260709_sc11-wireframe-drawio.md` は **2026-08-05 に #504（PR #511・`56d3f50`）が
`status: closed` へ変更済み**で、取り下げ理由の節（「取り下げ（2026-08-05 / #504）— 計画は draw.io を
作らない方針である」）と `wireframe/sc-11.html` への相対リンクを既に持つ。#497 の表は起票時点の状態で
ある。同様に **付随作業「15 の追随」（SC-11 未決事項 5）も #504 で完了済み**（§SC-11 の追随）。

**本作業での扱い**: `closed` → **`rejected`** へ揃える。理由は 2 つ。

1. `closed` は計画リポジトリが定める語彙（`open` / `triaged` / `accepted` / `rejected`）に無い**一点物**で
   ある（`feedback/` 27 件で `closed` を使うのはこの 1 件のみ）。
2. #497 の受け入れ基準が明示的に `rejected` を指定しており、意味（取り下げ・計画側へ渡す作業なし）は同じ。

**なお計画側の draft は `open` のままである**（`planning/draft/feedback/README.md` の「未処理」表にも
残る）。これは計画リポジトリ側の追随作業であり、**本リポジトリからは触れない**（§申し送り）。

**この「控えが原典より先行している」事実は frontmatter からも読めるようにする。** 本文の表にだけ書くと、
`status` だけを機械的に読む運用（一覧生成・棚卸し）では先行が見えない。計画側が用いる
`status_note:`（`planning/draft/feedback/20260709_composability-open-items-resolved.md:6` の書式。
`category:` の直後に置く 1 行）に倣い、本記録へ `status_note: 計画側原典は open（planning 未追随。控えが #497 で先行）`
を足す。**`feedback/TEMPLATE.md` に `status_note` の項は無い**（＝任意項目）ため、書式は計画側の先例に合わせた。

## `feedback/` 全 27 件の現 status と分類

件数は次のコマンドの実測値である。**作業ツリー（`ls feedback/*.md`）ではなくマージ先
（`origin/develop`）を数える**——棚卸しは「マージ後にどうなるか」が答えであり、作業ツリーを数えると
並行してマージされた記録を取りこぼして、書いた瞬間に古くなる（本書は実際にこれで 26 → 27 とずれた）。

```console
$ git ls-tree --name-only origin/develop feedback/ | grep -v -E 'README|TEMPLATE' | wc -l
27
```

`impl` = 本リポジトリ `feedback/` の値、`plan` = `planning/draft/feedback/` の同名ファイルの値（pin `d980a01`）。

| # | 記録 | impl（作業前） | plan | #497 の分類 |
| --- | --- | --- | --- | --- |
| 1 | `20260703_conversion-retry-vs-image-fallback.md` | open | accepted | 書換対象 |
| 2 | `20260703_wiki-selfhosted-supersedes-adr-0011.md` | open | rejected | 書換対象 |
| 3 | `20260704_plan-status-reflux-fr-adr.md` | open | accepted | 書換対象 |
| 4 | `20260705_internal-service-auth-nfr-deviation.md` | open | accepted | 書換対象 |
| 5 | `20260705_wiki-js-deployment-follows-adr-0011.md` | open | accepted | 書換対象 |
| 6 | `20260707_iadr-0017-superseded-mesh-mtls.md` | **open** | **accepted** | **どれにも該当しない（分類漏れ）** |
| 7 | `20260709_composability-open-items-resolved.md` | open | accepted | 書換対象 |
| 8 | `20260709_composability-safety-net-gaps.md` | accepted | accepted | 既に正しい |
| 9 | `20260709_composable-implementation-guide-upstream.md` | open | accepted | 書換対象 |
| 10 | `20260709_config-version-history-source-gitops.md` | **open** | **accepted** | **どれにも該当しない（分類漏れ）** |
| 11 | `20260709_conversion-job-query-reconvert-api.md` | accepted | accepted | **どれにも該当しない**（既に正しいが 3 件の列挙から漏れ） |
| 12 | `20260709_dotnet10-target-framework-deviation.md` | open | accepted | 書換対象 |
| 13 | `20260709_fr01-connector-and-nfr-verification-status.md` | **open** | **accepted** | **どれにも該当しない（分類漏れ）** |
| 14 | `20260709_frontend-sc-screens-implemented-status.md` | accepted | accepted | **どれにも該当しない**（既に正しいが 3 件の列挙から漏れ） |
| 15 | `20260709_sc11-wireframe-drawio.md` | **closed** | **open** | 書換対象（前提が古い。§食い違い 4） |
| 16 | `20260710_repo-positioning-and-unit-structure.md` | open | （同名なし。`20260712_` = accepted） | 書換対象 |
| 17 | `20260719_headlamp-k8s-management-ui.md` | open | **triaged** | **どれにも該当しない**（計画側は `triaged` = 裁定待ちあり） |
| 18 | `20260801_impl-handoff-kit-gaps.md` | accepted | （同名なし） | 既に正しい |
| 19 | `20260802_review-allowlist-diff-and-denial-labeling.md` | accepted | （同名なし） | 既に正しい |
| 20 | `20260803_ai-review-execution-permissions.md` | open | accepted | 計画 issue 追跡中（planning#168） |
| 21 | `20260803_ai-workflow-grep-sort-and-submodule-git-c.md` | open | accepted | 計画 issue 追跡中（planning#163） |
| 22 | `20260803_doc-links-code-extensions.md` | open | accepted | 計画 issue 追跡中（planning#167） |
| 23 | `20260804_frontend-migration-staging-interpretation.md` | accepted | （同名なし） | 計画 issue 追跡中（planning#186） |
| 24 | `20260804_sc01-03-bff-contract-gaps.md` | **open** | **（同名なし）** | **どれにも該当しない（計画側未到達）** |
| 25 | `20260805_abac-attribute-combination-measurement-result.md` | **open** | **（同名なし）** | **どれにも該当しない（計画側未到達）**。#515（`a14f912`）が develop へ後から追加した記録 |
| 26 | `20260805_sc05-07-admin-contract-gaps.md` | **open** | **（同名なし）** | **どれにも該当しない（計画側未到達）** |
| 27 | `20260805_sc09-11-admin-ops-contract-gaps.md` | **open** | **（同名なし）** | **どれにも該当しない（計画側未到達）** |

**#497 の 3 分類の合計は 10 + 4 + 3 = 17 件であり、10 件が分類から漏れている。** 内訳は次のとおりで、
質が 4 種類に分かれる（**いずれも本作業の射程外。status は書き換えない**）。

| 種別 | 件数 | 該当 | 性質 |
| --- | --- | --- | --- |
| A: **書換対象 10 件と同型の未同期** | 3 | #6・#10・#13 | impl=`open` / plan=`accepted`。**同じ欠陥だが #497 の表に無い**。なお**同型は #497 が「計画 issue 追跡中」へ分類した #20・#21・#22 にもあり、全数では 6 件**である（§申し送り 1） |
| B: 既に正しいが列挙から漏れ | 2 | #11・#14 | impl=`accepted` / plan=`accepted`。作業不要 |
| C: 計画側が `triaged`（裁定待ち） | 1 | #17 | `accepted` にはできない。ADR-0040 / ADR-0042 とも `Proposed` |
| D: 計画側へ未到達 | 4 | #24・#25・#26・#27 | `open` が正しい（`draft/feedback/` に同名なし） |

> **#497 の注意書きへの補足**: 「自動突合の ✅ を積み残しなしと読まない」に加え、**A の 3 件は #497 の
> 表からも漏れている**。控えの `status` だけを数えても実態は出ない。

## 対象範囲

| # | 作業 | 出力 |
| --- | --- | --- |
| 1 | 10 件の frontmatter `status:` の書き換え（`updated:` も追随） | `feedback/*.md` × 10 |
| 2 | 各記録への日付つき追記（**確認先を計画側の相対リンクで示す**）。`rejected` の 2 件は取り下げ／別解の理由を残す | 同上 |
| 3 | SC-11 未決事項の追随（**#504 で完了済み。根拠の直接引用を補強するのみ**） | `docs/screens/SC-11_configuration-viewer.md` |

**対象外**:

- `planning/`（submodule）の内容と pin。**読み取り専用**。
- 上表 A〜D の 10 件の `status`（**射程外。勝手に書き換えない**）。
- 計画側 `06_technical/03_tech-stack-selection.md:55` の残渣（**pin `d980a01` で既に是正済み**。§突合結果 #12）。
- `.github/workflows/` ・ソースコード・テスト（1 行も変えない）。

## 追記の書式

本リポジトリの先例（IADR への日付つき追記・`20260709_sc11-wireframe-drawio.md` の取り下げ節）に倣い、
各記録の**末尾**へ次の形で置く。

```md
## ［2026-08-05 追記 / #497］計画側の実態へ status を同期した

**判定: <accepted|rejected>**（... 1 行の理由 ...）

確認は planning submodule pin `d980a01` に対して行った（**行番号は pin が動くとずれるため内容で特定する**）。

| 確認先（計画リポジトリ） | 確認した記述 |
| --- | --- |
| [<パス>](../planning/...) | ... |
```

- リンクは**相対リンク**（`../planning/...`）で書く。`feedback/` はリポジトリ直下のため 1 段上がる。
- **ただし `check-doc-links.js` は既定で `docs/` しか走査しない**（`--dir` の既定値が `docs`。`ci.yml` も
  夜間の `doc-links-planning.yml` も `--dir` を渡さない）ため、**`feedback/` に置いた相対リンクは
  CI のどの経路でも検査されない**（§申し送り 2）。
  本作業では `node scripts/check-doc-links.js --dir feedback` を明示的に実走して確認し、
  既定で検査されない事実を §検証（変異試験 M2）で実測し、§申し送り へ残す。

## SC-11 の追随

**#497 が求める「未決事項 5 を解決済みへ更新する」は、既に #504（PR #511・`56d3f50`）で完了している。**

- `docs/screens/SC-11_configuration-viewer.md` の §未決事項 は**現在 2 項目**で、
  **旧 5 は引用ブロック「［2026-08-05 / #504］解決して畳んだ未決事項」へ移されている**
  （同ブロック中の「**旧 5（ワイヤーフレーム `sc-11.drawio` の作成）は取り下げる。**」）。
  **行番号では特定しない**——本作業自身が同ブロックへ追記するためマージ時点でずれる（本書が
  件数でやったのと同じ型の誤りになる）。
  → **#497 が言う「未決事項 5」は現在の番号ではなく「旧 5」である**（実測を優先する）。
- したがって**番号の振り直しは行わない**。本作業で足すのは根拠 1 点のみ:
  現在の記述は「§HTMLモックアップ が hi-fi / wireframe の HTML を挙げている」「`.drawio` が 1 件も無い」
  という**間接証拠**で結論しているが、`05_screens/01_screens.md:39` には
  **「draw.io ワイヤーフレームは作成しない」という直接の明文がある**。これを引用として補う。

## 検証

すべて作業ツリー `wt497` で実走し、出力を報告に貼る。

- `node scripts/check-doc-links.js`（既定 = `docs/`）
- `node scripts/check-doc-links.js --dir feedback`（**本作業が足した相対リンクの実在確認**）
- `node scripts/check-commit-messages.js --base origin/develop`
- `node scripts/check-test-traceability.js`
- `node scripts/check-test-spec-coverage.js`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `git diff --name-only origin/develop...HEAD` に `src/` ・`.github/workflows/` ・`planning` が現れないこと
  （**three-dot**。two-dot だと develop が進んだぶんの無関係な差分が「削除」として混ざり、射程の判定を誤る）
  （＝ビルド・テストの実走は不要）
- **変異試験**（実行後は必ず復元し、`git status` が汚れていないことを確認する）

| # | 変異 | 期待 | **実測** |
| --- | --- | --- | --- |
| M1 | `docs/screens/SC-11_configuration-viewer.md` の追記に足した `01_screens.md` へのリンクを存在しないパスへ | 既定の `check-doc-links.js` が fail | **fail（exit 1・破損リンク 1 件を名指し）**。→ **検査対象である** |
| M2 | `feedback/20260709_sc11-wireframe-drawio.md` の追記に足した `01_screens.md` へのリンクを存在しないパスへ | 既定は緑 / `--dir feedback` は fail | **既定 = exit 0（「OK: 425 件」のまま）／`--dir feedback` = exit 1**。→ **`feedback/` は既定の検査対象外だった**（§申し送り 2） |
| M3 | `docs/screens/SC-11_configuration-viewer.md` の `wireframe/sc-11.html` を存在しないパスへ | （追加検証） | **exit 0**。→ **`.html` は `LINK_EXT` に無く、そもそも検査されない**（§申し送り 6） |
| M4 | `20260709_dotnet10-target-framework-deviation.md` へ足した計画側 draft へのリンクを存在しないパスへ | 既定は緑 / `--dir feedback` は fail | **既定 = exit 0 ／ `--dir feedback` = exit 1（破損 1 件を名指し）**。→ 明示指定でのみ守られる（§申し送り 2 の穴そのもの） |
| M5 | クロスリポ番号を壊す（`planning#14` → `#14`、変更履歴の引用元 `planning#188` → 実在しない番号） | （追加検証） | **`check-doc-links.js`（既定・`--dir feedback` とも）= exit 0、`check-commit-messages.js` = exit 0**。→ **本文中の issue / PR 番号は修飾の有無も引用元の正誤も一切機械検査されない**（§申し送り 7） |

**M2〜M5 はいずれも「検査されていた」という前提が成り立たないことの実測である。隠さず報告する。**

## 申し送り

1. **書換対象 10 件と同型の未同期（impl=`open` / plan=`accepted`）は、全数で 6 件残る。** 後続 issue の
   射程を「A 群 3 件」で切ると 3 件が取り残されるため、**6 件で起票すること**。

   次のコマンドで全 27 件の impl / plan を突き合わせた実測である（**個別の目視ではなく全数照合**）。

   ```console
   $ for f in feedback/*.md; do b=$(basename "$f"); case "$b" in README.md|TEMPLATE.md) continue;; esac; \
       impl=$(awk '/^status:/{print $2; exit}' "$f"); p="planning/draft/feedback/$b"; \
       if [ -f "$p" ]; then plan=$(awk '/^status:/{print $2; exit}' "$p"); else plan="(none)"; fi; \
       [ "$impl" = "open" ] && [ "$plan" = "accepted" ] && echo "$b"; done
   20260707_iadr-0017-superseded-mesh-mtls.md
   20260709_config-version-history-source-gitops.md
   20260709_fr01-connector-and-nfr-verification-status.md
   20260803_ai-review-execution-permissions.md
   20260803_ai-workflow-grep-sort-and-submodule-git-c.md
   20260803_doc-links-code-extensions.md
   ```

   前半 3 件（#6・#10・#13）は **#497 の表からも 3 分類からも漏れていた**もの。後半 3 件（#20・#21・#22）は
   #497 が「計画 issue 追跡中（planning#168 / planning#163 / planning#167）」へ分類したものだが、
   **`status` の同期という観点では前半とまったく同型である**——計画側原典は 3 件とも 2026-08-04 に
   `accepted` でトリアージ済みで、控えだけが `open` に取り残されている。
   **除外しない**（本書は当初「A 群 3 件」と書いていたが、これは過少である）。

   ただし後半 3 件には**同期とは別の残タスク**が付いている点を後続 issue へ引き継ぐこと。計画側の
   トリアージ結果が「`repo-template/.claude/settings.json` は AI 編集が deny のため未反映（人間対応）」
   「MSP 側は kit 反映のリリース後、暫定デルタを撤去してバイト一致へ戻す（[[IADR-0115]] の運用）」と
   記しており、**`status` を `accepted` へ揃えても、その残タスクが消えるわけではない**。
2. **`feedback/` は `check-doc-links.js` の既定走査対象外であり（M2 で実測）、`ci.yml` と
   `doc-links-planning.yml` の*両方*が素通りする。** 走査対象は `--dir` の既定値 `docs` だけである。

   ```console
   $ grep -n "const a = { dir: 'docs'" scripts/check-doc-links.js
   41:  const a = { dir: 'docs', requirePlanning: false };
   $ grep -n check-doc-links .github/workflows/*.yml
   .github/workflows/ci.yml:102:        run: node scripts/check-doc-links.js --self-test
   .github/workflows/ci.yml:104:        run: node scripts/check-doc-links.js
   .github/workflows/doc-links-planning.yml:60:        run: node scripts/check-doc-links.js --require-planning
   ```

   **どちらの起動にも `--dir` が無い。** とりわけ夜間の `doc-links-planning.yml` は
   **planning submodule を populate して計画側リンクを実際に解決する唯一のジョブ**であり
   （`ci.yml` の `doc-links` は submodule 無しで checkout するため計画側リンクを検査対象外にする）、
   それが `docs/` しか見ていない。結果として、**本作業が `feedback/` へ足した計画側への相対リンクは
   CI のどの経路でも検査されない**——PR CI（対象外・かつ planning 未 populate）でも、夜間ジョブ
   （planning は解決できるが `feedback/` を見ない）でも守られていない。pin がずれても機械検出されない。

   `.github/workflows/` は本エージェントの権限では編集できないため、結線は親へ引き渡す。**`ci.yml`
   だけでは足りない**（それでは計画側リンクは相変わらず解決されない）。両方に手当てすること:
   - `ci.yml` の `doc-links` ジョブ: 非 planning リンクを `feedback/` でも毎 PR 検査する。
   - `doc-links-planning.yml`: `--require-planning` に `--dir feedback` の走査を足す（または
     `check-doc-links.js` の既定走査対象へ `feedback` を含め、両ジョブが自動で拾うようにする）。

   **既存の `feedback/20260709_sc11-wireframe-drawio.md`（#504 が足した計画側リンク）も同じく未検査の
   ままだった。**
3. **計画側 `draft/feedback/20260709_sc11-wireframe-drawio.md` は `open` のままである。** 実装側は
   `rejected` へ揃えたが、原典は未追随であり、計画リポジトリの「未処理」表にも残る。計画側の
   トリアージ（`rejected`）が必要——`/plan-feedback` の候補。**本リポジトリからは触れない。**
4. **#17（Headlamp）は計画側が `triaged`**（裁定待ちを残したまま反映）。控えを `accepted` にすると
   計画側より進んだ状態になる。`triaged` を控えでも使うかは運用判断が要る（**要裁定**）。
5. `feedback/README.md` には status の語彙が書かれていない（計画側 `draft/feedback/README.md` にのみ
   ある）。控え側にも語彙を明記すれば `closed` のような一点物の再発を防げる。
6. **`.html` は `check-doc-links.js` の `LINK_EXT` に含まれず、リンクが検査されない（M3 で実測）。**
   計画側のモックアップは HTML であり、**SC-01〜SC-21 の画面仕様書が「実装の正」として指す
   `mockups/{hi-fi,wireframe}/sc-NN.html` は 1 件も検査されていない**。planning#167 が同種の欠落
   （コード拡張子の不足）を扱って解消された先例があるため、`html` の追加も同じ形で扱えるはずである。
   ただし `LINK_EXT` の増減は `check-doc-links.js --self-test` の正例・負例と対で更新する必要があり、
   本 issue の射程外とする（別 issue の候補）。
7. **本文中の issue / PR 番号は、修飾（`planning#NNN`）の有無も引用元の正誤も機械検査されない（M5 で実測）。**
   `check-commit-messages.js` はコミット件名 / PR タイトルのスコープしか見ず、`check-doc-links.js` は
   相対パスしか見ない。裸の `#NNN` は GitHub 上で本リポジトリの無関係な issue / PR へ**実際に誤リンクする**
   （本 PR で是正した `Issue #14` がその実例）。`.claude/rules/traceability.md` は規約を定めているが、
   **仕様書 / 記録の本文に対する検査器が無い**。`feedback/` と `docs/` の本文で「行頭・空白直後の裸 `#\d+`」を
   拾う lint は機械化できるはずで、別 issue の候補とする（**本 issue の射程外**）。

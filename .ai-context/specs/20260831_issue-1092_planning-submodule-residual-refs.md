---
title: 撤去済み planning submodule を現況として述べている記述の是正（issue #1092）
type: spec
status: done
created: 2026-08-31
updated: 2026-08-31
author: claude
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0048_impl-docs-restructure.md
related_ids:
  - NFR
  - ADR-0048
  - IADR-0058
  - IADR-0060
  - IADR-0065
  - IADR-0228
  - IADR-0327
---

# 作業仕様書: 撤去済み planning submodule の残存記述を是正する（#1092）

## 1. 起点・制約

- 起点 ID: **NFR**（文書整合）／計画 **ADR-0048 決定 2**／実装 **IADR-0228**。
- 確定済み制約:
  - **本リポジトリは planning に依存しない（submodule は張らない）**。参照は GitHub URL か隣接クローン
    `../project-planning`（読み取り専用・pin 固定なし）。
  - **planning 依存の検査器は退役済み。復活させない**（`CLAUDE.md`）。
  - `IADR-0228` は「**個々の旧 IADR を書き換えず、Superseded にもしない**」と明示的に決めている
    （同 ADR「決定」前文・「関連 / Supersedes: なし」）。
- 実測（2026-08-31・本 worktree）:
  - `.gitmodules` のエントリは **`src/ai-stock-trading` ただ 1 つ**。
  - `.github/workflows/` に **`doc-links-planning.yml` は存在しない**（実在 18 本を `ls` で確認）。
  - `src/ai-stock-trading` の pin `0844b584` の **root tree に `.gitmodules` も `planning/` も無い**
    （`gh api repos/endazon/ai-stock-trading/git/trees/0844b584…`）。すなわち**入れ子の planning も現存しない**。

## 2. 母集合（自分で引き直した。issue 本文の表は転記しない）

除外（恒久）: `.ai-context/adr/` `.ai-context/specs/` `.ai-context/superpowers/`（凍結記録）／`CHANGELOG.md`（自動生成物）／
`src/ai-stock-trading/**`（submodule）／`.git` `node_modules` `obj` `bin` `dist`。
**拡張子では絞らない**（規則 3）。**行フィルタで母集合を作らず、パス除外だけで取る**（規則 4）。

| 軸 | 検索語 | ヒット | 備考 |
| --- | --- | ---: | --- |
| 0 | `planning`（大小無視・追跡下全件） | **943 ファイル** | 大半は `planning#NNN` の issue 修飾。**母集合として使えない** |
| A | 同一行に `planning` と `submodule`\|`サブモジュール`\|`gitmodules` | **57 行 / 25 ファイル** | 本命 |
| B | `submodules:\s*(recursive\|true)` | **3 行**（live） | 回避理由の所在 |
| C | `PLANNING_REPO_TOKEN`\|`doc-links-planning`\|`require-planning`\|`planningPopulated`\|`planning-pin` | **18 行**（live） | 退役資産の参照 |
| D | `.gitmodules` の言及 | **98 行**（live） | 件数を述べる箇所の特定 |
| E | パス形 `planning/` | **41 行**（live） | submodule マウント前提の相対パス |
| F | `--recurse-submodules`\|`submodule update --init` | **22 行**（live） | 手順の実在性 |
| G | `サブモジュール`（カタカナ） | **7 行**（live） | 表記ゆれ（規則 2） |
| H | `project-planning` | **80 行**（live） | 正当な別リポ参照の確認用（**陽性対照**） |
| I | ファイル単位の共起（`planning` を含む ∧ `submodule` 系を含む） | **36 ファイル** | 軸 A が拾えない**行またぎ**を拾う |

軸 I が軸 A を超えて出したのは `.github/workflows/codeql.yml` / `security.yml` の 2 件で、
**いずれも直す対象だった**（軸を 1 本で終わらせない＝規則 5 が効いた実例）。

### 2.1 直す（planning を **本リポジトリの** submodule として現況で述べている／実在しない資産を指す）

| # | 対象 | 誤り |
| --- | --- | --- |
| 1 | `docs/how-to/adding-a-unit-submodule.md` 87-90 | 「本体リポと各ユニットは private な `planning` を submodule として持つ」＝ `recursive` 回避の理由が失効 |
| 2 | 同 102 | 存在しない `doc-links-planning.yml` を private ユニット取得の同型例として指す |
| 3 | 同 193-197 | `.gitmodules` に `planning` が列挙されている前提／private submodule（`planning`）の Dependabot 権限 |
| 4 | `docs/how-to/local-development.md` 30・33-41・128 | 前提ツール欄・§2 見出しと本文・詰まり表が `planning/` submodule を手順として述べる |
| 5 | `docs/how-to/session-handoff.md` 503・557・580 | §3 恒久制約「`planning/` は pin 更新のみ」／populate 実測コマンドに `planning` ／§3 の要約 |
| 6 | `docs/operations/llm-model-pin-runbook.md` 172 | 実在しない `doc-links-planning.yml` を無限定で引く |
| 7 | `README.md` 6・13・99・117・110・169 | 位置づけ・ディレクトリ図・前提ツール・clone 手順・参照先が submodule 前提 |
| 8 | `.github/workflows/codeql.yml` 52 / `security.yml` 86 | 「private planning は巻き込まない」＝非再帰の理由が失効 |
| 9 | `src/README.md` 167 / `templates/unit-template/README.md` 200 | private ユニットに `submodules: recursive` を勧める（本リポ CI の実装と食い違う。規則 10 の引き直しで発見） |
| 10 | `scripts/check-test-traceability.js` 27 | 「計画リポジトリ（planning/）は submodule で未 populate があり得る」＝現況でない |
| 11 | `scripts/check-reading-budget.js` 32 | 母集合の除外理由に `planning/` を submodule として挙げる |
| 12 | `scripts/scripts.repo.test.js` 5105-5125 | `git show origin/develop:.github/workflows/doc-links-planning.yml` を fixture にした変異試験が**常に skip**（無音で緑） |
| 13 | `.github/workflows/claude-code-review.yml` 184 | 「submodule が 1 つなら `planning` の 5 エントリを書き換えるだけでよい」（本リポの submodule 1 件は `src/ai-stock-trading`） |
| 14 | `scripts/scripts.repo.test.js` 5458-5477（#703） | 「issue テンプレートはキットとバイト一致（分類 A）」が `planning/tools/impl-handoff-kit/…` の存在で分岐し、**常に「未 populate のため省略」で抜けていた**。`CLAUDE.md` は kit 同期のバイト一致検査を退役済み・復活させないと定める |
| 15 | 同 3714-3722（#717） | 「状態欄の更新主体をキットが定めている」が同じく `planning/…/feedback/README.md` の存在で分岐し常に skip。`feedback/` 自体も ADR-0048 決定 5 で撤去済み |

**#14・#15 は軸 E（パス形 `planning/`）でしか出ない。** 軸 A（同一行の `planning` ∧ `submodule`）にも
軸 C（退役資産名）にも掛からない —— **軸を 1 本で終わらせない（規則 5）の 2 例目**である。

### 2.2 直さない（理由つき）

| 対象 | 直さない理由 |
| --- | --- |
| `planning#NNN` 形の issue 参照（軸 0 の大半・`docs/` `src/` `scripts/` 全域） | **別リポジトリの issue への正当な参照**。規約（`traceability.repo.md`）が要求している表記そのもの |
| `project-planning` / `../project-planning` の参照（軸 H・80 行） | **正当な別リポ参照**。`CLAUDE.md` が定める参照手段。**陽性対照**として使い、これを巻き込んでいないことを確認した |
| `docs/how-to/adr-supersede-citation-annex.md` 32・40・56-57 | **点時点の測定記録**。`［2026-08-21 追記］`で「上表の『例外は 2 本』は撤去済みで、現在は 0 本」と既に是正済み。本文は「当時の測定」として残す運用（黙って消さない） |
| `docs/how-to/plan-id-range-history-annex.md` 361-375 | 同上。`［2026-08-21 追記 / #872］`が冒頭に付いた引用ブロック。**加えて `scripts.repo.test.js` の `MOVED_OUT` が本別紙に `doc-links-planning.yml` の存在を要求している**（消すとテストが落ちる） |
| `docs/how-to/commit-message-rules-annex.md` 199-200 | 「旧記述（黙って消さない）」ブロック。既に「submodule 自体が撤去されたためこの案は適用しない」と明示済み |
| `scripts/lib/excluded-units.js` 126-146 / `check-knip.js` 536 / `check-plan-id-qualification.js` 328 / `scripts.test.js` 1351 / `scripts.repo.test.js` 2042 | **自己試験の fixture**。`[submodule "planning"]` は「**リポジトリ直下の submodule はユニットでない**」という規則（#473）を検査するための入力であり、現況の主張ではない。書き換えると検査が弱る |
| `scripts/check-doc-links.js` 28-29・96 | **過去形で退役を述べている**（受け入れ基準 3 の実測結果は §4 参照） |
| `scripts/check-cross-repo-refs.js` 128 / `check-plan-id-qualification.js` 68 / `check-commit-messages.js` 256 / `scripts/README.md` 9 / `.github/workflows/ci.yml` 132 / `.github/dependabot.yml` 14 / `CLAUDE.md` / `AGENTS.md` / `AI_SETUP.md` | **既に正しい**（「依存しない」「撤去済み」と述べている） |
| `scripts/check-cpm-versions.js` 447-448 | 「submodule を populate した環境での実測」＝**過去の測定条件の記載**。結論（MSP 計画コーパスに CPM の言及 0 件）は現況でも成立 |
| `.claude/settings.json` 151 | 「submodule が複数ある構成では…」の一般記述。planning を名指ししていない |
| `.github/workflows/claude-code-review.yml` 233 / `claude-coding.yml` 198-199（`git -C src/ai-stock-trading/planning …`） | **AST ユニットが内包していた入れ子 submodule**への許可であり、本リポの planning 依存ではない。現 pin `0844b584` には存在しないが、**AST 側の pin 事情であって ADR-0048 決定 2 の射程外**。加えて許可リストは「本ファイル / claude-coding / claude-code-review」の 3 系統同期と `check-ai-workflow-config.js` の非対称検査に縛られており、**同一 PR で触ると本 issue の是正と CI 権限の変更が混ざる**。**#1141 へ切り出した** |
| `.github/workflows/ci.yml` 437-440 ほか「`IADR-0058` 型トークン」 | `IADR-0058` は `IADR-0228` の決定により **Superseded にしない**。パターン名としての引用は有効 |
| パス形 `planning/docs/glossary.md`・`planning/projects/…`（`docs/functional/` `docs/screens/` `src/**` `scripts/**` 計 20 行超） | **submodule であるとは述べていない**が、`planning/` 前置は submodule マウント時代の名残である。**別系統の古さ**（表記規約の問題）であり、`src/` のコード注釈まで広く触れると並列作業と衝突する。**#1141 へ切り出した** |

### 2.3 規則 10（この変更で新たに誤りになる自分の記述）の引き直し

- **`submodules: recursive` を避ける理由を書き換える**⇒ `src/*` のユニット submodule について**別の理由**が要る。
  「理由ごと消す」ことはしない。`IADR-0065` の決定文から**まだ生きている半分**を取り出して据える:
  1. **`src/*` 限定**: ユニット実体が要るのはビルド/テスト/検査ジョブだけであり、`src/` 以外へ submodule を
     足したときにそれらを巻き込まない。`checkout` の `submodules:` には**取得対象を選ぶ手段が無い**。
  2. **非再帰**: ユニットが内包する入れ子 submodule を辿らない。入れ子が private なら既定 `GITHUB_TOKEN` では
     read できず、**checkout ステップごと `Repository not found` で落ちる**（ジョブ本体に入る前に失敗する）。
- 上を書いた結果、`src/README.md` 167 と `templates/unit-template/README.md` 200 の
  「private submodule は `submodules: recursive` + トークン」が **how-to と矛盾する**ことが判った（→ 2.1 #9）。
  この 2 件は軸 B（`submodules: recursive`）で引かなければ出てこない。
- 導出値（`.gitmodules` の件数）は**走査ではなく数え直した**: 1 件（`src/ai-stock-trading`）。

## 3. 凍結記録の扱い（判断と根拠）

`.claude/rules/traceability.repo.md` §Superseded の「凍結の射程は記録種ごとに違う」を読んだうえで:

- **`.ai-context/specs/` は書式つき経過追記が可、`.ai-context/superpowers/` は不可**（[[IADR-0166]] 決定 2 の 2026-08-17 追記）。
  本 PR では**どちらにも追記しない**（本 issue の対象は live な記述である）。
- **`IADR-0058`（planning submodule のリンク検査）は触らない。** 理由は 2 つ。
  1. **`IADR-0228` が明示的に決めている** ——「個々の撤去対象を定めた旧 IADR は書き換えず、本 IADR から一括で
     参照する」「Supersedes: なし（個々の旧 IADR は Superseded にはしない）」。Accepted な IADR の決定であり、
     覆すには新しい IADR が要る。**本 PR の射程はそこではない。**
  2. `IADR-0058` 決定 1 には既に**当時の書式で**「（ADR-0048 決定 2 により撤去済み）」が入っており、
     読者が前提喪失に気づけないという実害が無い。
- **`IADR-0060` / `IADR-0065` も同様に触らない。** ただし **live な文書がそれらを現況の手順として引いている**
  箇所（2.1 #1・#8・#9）は直す。**凍結記録は残し、それを引く live 側を直す**、が本 PR の方針である。

## 4. 受け入れ基準 3 の実測: `check-doc-links.js` の planning 分岐

```
$ grep -n 'require-planning\|planningPopulated' scripts/check-doc-links.js
29: * planning submodule 未 populate 時の分岐（`--require-planning` / `planningPopulated()`）は
```
**実装は残っていない。** 引数解釈（`--dir` / `--self-test` のみ）にも `--require-planning` は無く、
`planningPopulated` という識別子も定義されていない。29 行目は**退役を過去形で記録したコメント 1 行**である。
`scripts/scripts.test.js` 488-509 が「`collectBroken` は `onSkip` 等の追加引数なしで動く（planning submodule 分岐の撤去）」
として撤去を回帰で固定している。

判断: **退役は完了しており、追加の退役作業は不要。コメントも残す**（`CLAUDE.md`「復活させない」の根拠が読める）。
未 populate submodule の fail-open は `src/ai-stock-trading` を対象とする一般則へ既に置き換わっている。

## 5. 受け入れ基準

1. §2.1 の 15 件が是正され、§2 の全軸を引き直して **planning を本リポの submodule として現況で述べる live な記述が 0 件**。
2. `.github/workflows/` の記述が実在と一致する（`doc-links-planning.yml` を live な資産として指す箇所が 0 件）。
3. `check-doc-links.js` の planning 分岐の実測結果（§4）を記録し、判断を示す。
4. `docs/` の表示テキストへ計画 ID・IADR・仕様書名・修飾付き issue 参照を書かない。本文を変えた `docs/` は `updated:` を揃える。
5. §6 の検査がすべて緑。

## 6. 検証

```
node scripts/check-trace-blocks.js && node scripts/check-doc-links.js && node scripts/check-doc-updated.js \
  && node scripts/check-doc-type-vocabulary.js && node scripts/check-doc-status-vocabulary.js \
  && node scripts/gen-knowledge-graph.js --check
node scripts/check-commit-messages.js && node scripts/check-cross-repo-refs.js \
  && node scripts/check-plan-id-qualification.js && node scripts/check-reading-budget.js
node scripts/check-ai-workflow-config.js
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
```

`.github/workflows/` は**コメントのみ**を触る（`on:` / `jobs.<id>` / `steps` は変えない）ため、
起動条件・必須チェック名は動かない。差分で確認する。

## 7. 測れないもの

- `src/ai-stock-trading` は本 worktree で未 populate である（`git submodule status` の先頭が `-`）。
  AST 側の `.gitmodules` 不在は **GitHub API（pin の tree）で確認**した。ローカル実体では測っていない。
- `.github/workflows/` の変更が実際に CI で起動条件を変えないことは、**差分の性質（コメント行のみ）でしか示せない**。
  ワークフローの起動可否をローカルで実走する手段は無い。

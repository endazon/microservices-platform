---
title: 作業仕様書 — 検査器まわりのメタ作業 3 件を 1 PR に束ねる（#877 / #842 / #826 の残骸）
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0115
  - IADR-0116
  - IADR-0130
  - IADR-0139
  - IADR-0141
  - IADR-0179
  - IADR-0183
  - IADR-0228
  - IADR-0230
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md
  - planning:projects/microservices-platform/07_adr/ADR-0048_impl-docs-restructure.md
related_specs:
  - "../adr/IADR-0230_meta-work-bundled-prs.md"
  - "../adr/IADR-0116_reimplementation-branching-and-pr-policy.md"
  - "../adr/IADR-0139_domain-bundled-contract-prs.md"
  - "../adr/IADR-0183_false-green-warning-on-worktree-state.md"
  - "../adr/IADR-0228_planning-dependency-removal.md"
---

# 仕様書: 検査器まわりのメタ作業 3 件を 1 PR に束ねる（#877 / #842 / #826 の残骸）

> 起点 ID は**無採番 `NFR`**（場合 2・メタ作業。`.claude/rules/traceability.md`「起点 ID の種別」／
> [IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 2）。稼働する製品の要件ではなく
> 工程の規律であるため、計画側の非機能要件表に当たる番号が無い。**環流しない**（場合 2 の扱い）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 該当なし（検査器・文書統制のメタ作業）
- ユースケース（UC）/ 画面（SC）: 該当なし
- 関連 ADR: 計画 `ADR-0048` 決定 2（planning 依存の全面撤去。#826 の残骸はその副産物）／
  [IADR-0228](../adr/IADR-0228_planning-dependency-removal.md)（撤去の実施記録）／
  [IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md)（`lib/worktree-state.js` の設計）／
  [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1・
  [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md)（束ねの第 1 の例外）／
  **[IADR-0230](../adr/IADR-0230_meta-work-bundled-prs.md)（本 PR で起草。束ねの第 2 の例外）**
- 利用者裁定: 2026-08-21「メタ作業に限り PR の束ねを緩和してよい」／
  2026-08-21「`.ai-context/` は凍結記録であり `related_specs` の post-hoc 訂正はしない」（#877 の根拠）

## 目的・背景

検査器領域に閉じた是正 3 件を 1 PR に束ねる。**束ねてよい根拠そのものを本 PR で起草する**
（[IADR-0230](../adr/IADR-0230_meta-work-bundled-prs.md)。本 PR が同 IADR の最初の適用例である）。

| 件 | 症状 | 直す場所 |
| --- | --- | --- |
| **#877** | `gen-knowledge-graph.js --check` は凍結記録の未解決 `related_specs` を情報表示に留める（利用者裁定 2026-08-21）のに、`check-doc-links.js` は**同じ値**を `docs/` と同じ厳格さで fail にする。**2 つの検査器が凍結記録の扱いで正反対の要求を出す** | `scripts/check-doc-links.js` |
| **#842** | `check-cross-repo-refs.js` の遅延 require（88〜98 行）は `require.resolve` が `MODULE_NOT_FOUND` を返すと `MODE = {}` ＋ no-op のまま**警告 1 行も出さずに** exit 0 で走る。意図された fail-open だが、「正常な一時 dir」と「本リポで結線が壊れた」を区別しない | `scripts/scripts.repo.test.js`（**検査器本体は触らない**） |
| **#826 の残骸** | `scripts/setup.sh` 27 行の「下の pin 検査の注記と同じ罠」が、撤去済み（[IADR-0228](../adr/IADR-0228_planning-dependency-removal.md) / 計画 `ADR-0048` 決定 2）の pin 鮮度検査を指しており、**参照先が存在しない** | `scripts/setup.sh` |

## 対象範囲

- **対象**: `scripts/check-doc-links.js` ／ `scripts/scripts.repo.test.js` ／ `scripts/setup.sh` ／
  `.ai-context/adr/IADR-0230_meta-work-bundled-prs.md`（新規）／ `.ai-context/adr/README.md`（索引 1 行）／
  本仕様書（新規）／ `.ai-context/adr/IADR-0116_*.md`・`.ai-context/adr/IADR-0139_*.md`（日付つき追記）。
- **対象外（本作業では触らない）**:
  - `src/**`・`.github/**`・`deploy/**`・`templates/**`・`CLAUDE.md`・`.claude/rules/**`
    （[IADR-0230](../adr/IADR-0230_meta-work-bundled-prs.md) 決定 1 M-A の除外領域そのもの）
  - `scripts/check-cross-repo-refs.js`（#842 は**検査器を変えずに**回帰試験で固定する方針）
  - `scripts/gen-knowledge-graph.js`（#877 の方針 (a) は「厳しい側を緩める」であり、緩い側は変えない）
  - **確定済みの `.ai-context/specs/` と `.ai-context/adr/` 本文**（凍結記録。`IADR-0116` / `IADR-0139` への
    日付つき追記は `.claude/rules/traceability.repo.md` が明示的に認める形式である）

## 母集合の引き方（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1 の規則 1〜8 ／ `traceability.repo.md` の規則 9・10）

**誤りの側の文字列で引いた。拡張子で絞らず、パス除外（`:!src/ai-stock-trading`）のみで取った。
行フィルタで切らず、走査の生の出力を読んだ。軸は 3 本引いた。** 実測は develop 上の作業ツリー
（2026-08-21）で `git grep` を用いた。

| # | 軸 | 引き方 | 生の件数 | 追随が要るもの |
| ---: | --- | --- | ---: | ---: |
| 1 | **#877 の是正で新たに誤りになる自分の記述**（規則 10） | `git grep -l "check-doc-links" -- ':!src/ai-stock-trading'` | **212 ファイル** | **1**（`scripts/README.md`。下記） |
| 2 | **#826 の誤りの側の語** | `git grep -n "pin 検査\|pin 鮮度" -- ':!src/ai-stock-trading'` | 3 | **1**（`scripts/setup.sh`。他 2 件は正しい） |
| 3 | **#842 で流用するヘルパの参照元** | `git grep -n "runWithLibX836" -- ':!src/ai-stock-trading'` | 3（すべて `scripts/scripts.repo.test.js` 内） | 0（同一ファイル内で完結） |

**軸 1 の 212 ファイルの内訳と除外理由**（規則 6。**黙って除外しない**）:

- **`.ai-context/adr/` 15 件・`.ai-context/specs/` 約 190 件 = 凍結記録**。post-hoc に本文を訂正しない
  運用であり除外する（#877 の裁定そのものと同じ根拠）。
- **`CHANGELOG.md` = 生成物**。手で書き足さない（`CLAUDE.md`「補助成果物の自動生成」）。
- **`.github/workflows/ci.yml` = 呼び出しの配線のみ**（`node scripts/check-doc-links.js`）。挙動の説明を
  持たないため追随不要。かつ M-A の除外領域。
- **`docs/tests/TEST_STRATEGY.md` / `docs/how-to/session-handoff.md` / `src/.prettierignore` =
  対象拡張子・ジョブ名・改行の話**であり、fail-open の範囲に触れていない。追随不要。
- **`docs/how-to/plan-id-range-history-annex.md` = 撤去済み `--require-planning` の履歴記述**。
  過去の記録として正しい。
- **🔴 `scripts/README.md` 9 行目 = 追随が要る（本 PR では未実施）。** 同行は
  「**未 populate な submodule 配下は対象外にし、その件数を submodule 別に `notice` で報告する**」と
  fail-open を **1 種類だけ**列挙しており、本 PR が 2 種類目（凍結記録 × frontmatter × submodule 配下）を
  足したことで**不完全な記述になった**。**本 PR の編集許可範囲（`check-doc-links.js` /
  `scripts.repo.test.js` / `setup.sh` / `.ai-context/` の 3 本）に含まれていないため未実施であり、
  追随 issue として残す**（除外の理由は「対象外だから」ではなく「権限範囲外だから」である。混同しない）。

**導出値は走査ではなく計算し直した**（規則 7・10）。`check-doc-links.js` の自己試験件数は
本 PR で 45 件 → **54 件**（+9）。`scripts.repo.test.js` の companion 登録数は 419 件 → **420 件**（+1）。
どちらも**実行して読んだ値**であり、記憶や差分の見積もりではない。

**規則 8（自己参照）の扱い**: 軸 1 の 212 ファイルは**本仕様書を書く前**の値である。本仕様書は
`check-doc-links` の文字列を含むため、コミット後は 213 になる。**212 → 自己参照 1 件を足す → 213** と
引き算を見せておく（`IADR-0230` は同文字列を含まないため増えない）。

## 設計

### 1. #877 — `check-doc-links.js` に範囲を絞った fail-open 例外を入れる

**採る方針は (a)（厳しい側を緩める）。** 絞り込みは **3 条件の連言**であり、いずれか 1 つでも欠ければ
従来どおり fail する。

| 条件 | 実装 |
| --- | --- |
| (1) 参照元が `.ai-context/` 配下（凍結記録） | `isFrozenRecordPath(fp, root)` |
| (2) frontmatter のリスト項目値である | **呼び出し位置が担う**。`collectBroken()` の 1) の枝でだけ呼び、2) 本文 Markdown リンク・3) インラインコードからは呼ばない |
| (3) 解決先が `.gitmodules` 由来の submodule 配下 | `submoduleOf(resolved, root)`（**populate の有無を問わない**。既存の `unpopulatedSubmoduleOf` から包含判定を抽出して共有） |

- 3 条件を満たすとき `onFrozenSkip(sub, fp, val)` を呼び、`broken` へ**入れない**。
- **黙らせない**（[IADR-0130](../adr/IADR-0130_test-spec-coverage-ratchet.md)）。`main()` が
  `notice()`（既存の未 populate submodule の notice と同じ作法）＋ **1 件ごとの「参考:」行**
  （`gen-knowledge-graph.js --check` の文言に合わせる）＋ OK 行末尾の括弧書きの 3 面で出す。
- **一律 fail-open にしない理由**: `.ai-context/` の frontmatter には in-repo（隣の `adr/` 配下の IADR 等）を
  指す値が大量にあり、一律に緩めるとそれらのリンク切れが全件無検査になる。

### 2. #842 — `scripts.repo.test.js` に「結線が生きている」ことの回帰試験を足す

- 既存の `runWithLibX836`（#836 群）を **`runCheckerWithLibX836(populateLib)`** へ薄く一般化し、
  `runWithLibX836` はその上のラッパにする（既存 2 本の挙動は変えない）。
- 新テストは一時ディレクトリへ **本物の `lib/worktree-state.js` と `lib/ci-annotate.js` の 2 ファイル**を
  写す（前者が `./ci-annotate` を require するため、1 ファイルだけでは #836 側の試験になってしまう）。
  加えて untracked ファイルを 1 件作り、`MODE.TRACKED` の警告条件を**偶然に頼らず**満たす。
- **判定は終了コードではない**（0 件走査の門が exit 1 を返すため区別できない）。**判定行の有無**
  （`/#683 \/ IADR-0183/` と `untracked のファイルが N 件ある`）で見る。既存 4884 / 4909 行のテストと同じ作法。
- **既存テストとの非重複**: 4884 行＝クラス分類の宣言（静的）、4909 行＝**本リポジトリのツリー上**での
  4 本の実挙動（`lib/` が在るので遅延 require の分岐を通らない）、6797 / 6819 行＝**壊れた lib** を
  握り潰さないこと。**「正しい lib を置いたときに結線が実際に働くこと」を見ているものは 1 本も無い。**

### 3. #826 の残骸 — `setup.sh` の宙に浮いた参照

- 「（下の pin 検査の注記と同じ罠）」を削り、**罠そのものをその場で書き切る**形にする。
- 併せて「pin 鮮度検査は `ADR-0048` 決定 2 / [IADR-0228](../adr/IADR-0228_planning-dependency-removal.md) で
  撤去済みであり**復活させない**」と明記する（`CLAUDE.md` 禁止事項と矛盾しない文面）。

### 4. [IADR-0230](../adr/IADR-0230_meta-work-bundled-prs.md) の起草と既存 IADR への追記

- 束ねの条件は **M-A〜M-E**、上限は **4 件**（[IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) と
  同じ数に揃える。詳細は同 IADR）。
- [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1 へ
  `［2026-08-21 追記 / #877］` 形式で**第 2 の限定例外**を記す（第 1 の例外 = IADR-0139 の追記と同じ形）。
- [IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) へは**射程が交わらないことだけ**を短く追記する
  （あちらは製品の契約、こちらは検査器領域。上限 4 件が同じ数である理由も 1 行）。

## 受け入れ基準

- [x] 1. `node scripts/check-doc-links.js` が **exit 0**、かつ見逃した 1 件が **notice** として出る
      （実測: `OK: 719 件` ＋ notice 1 行 ＋「参考:」1 行、`EXIT=0`）
- [x] 2. 変異試験: `.ai-context/` 配下の**本文リンク**を壊すと **exit 1**（fail-open が広がっていない）
      （本仕様書の本文リンク 1 本を存在しない `.md` へ差し替えて実測。`EXIT=1` ＋ 当該行を名指し）
- [x] 3. 変異試験: `.ai-context/` 配下の frontmatter で **in-repo を指す値**を壊すと **exit 1**
      （同じ frontmatter の `related_specs` を壊して実測。対照として **submodule 配下**の壊れた値を
      足すと fail-open ＋ notice 2 件で `EXIT=0` になることも実測した）
- [x] 4. `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑
      （**本作業由来の失敗は 0 件**。執筆時点では `Platform.Shared.Kernel.Tests` の追加により
      テストプロジェクト数 ratchet が 14 → 15 で赤だったが、**PR が open になる前に当の
      `feat(ADR-0041)` コミット自身が 15 へ進めて解消している**。下記「未決事項」の 2026-08-21 追記）
- [x] 5. 変異試験: `scripts/lib/worktree-state.js` を退避／無効化すると **#842 のテストが fail** する
      （no-op スタブへ差し替えると **#842 だけが fail し #836 の 2 本は緑**。退避（ファイル削除）では
      `check-doc-updated.js` の即時 require が先に落ちて companion 全体が止まる）
- [x] 6. `grep -n "pin" scripts/setup.sh` の結果が、存在する対象だけを指している（1 行のみ・撤去済みの明示）
- [x] 7. `node scripts/check-doc-links.js --self-test` が緑（**54 件 OK**。45 → 54 へ +9）
- [x] 8. `node scripts/check-adr-numbering.js` が緑（`IADR-0229` の着地後に実測して OK）

## テスト方針

- **#877**: 自己試験（`--self-test`）へ**正例 1・負例 4 ＋ 経路試験 4** を対で足す。負例は 3 条件を
  1 つずつ崩したもの（`docs/` 由来／in-repo 指し／本文リンク）で構成する。実データに対しては
  変異試験（受け入れ基準 2・3）を**本仕様書自身の frontmatter と本文**に対して行う
  （凍結記録を書き換えずに `.ai-context/` 配下で変異させられる唯一のファイルである）。
- **#842**: 回帰試験 1 本。検出力は「lib を無効化すると当該テストだけが落ち、#836 の 2 本は緑のまま」
  で示す（**その 1 本だけが新しい穴を塞いでいる**ことの証拠になる）。
- **#826**: コメントのみの是正。固定する挙動が無いため試験を足さない
  （[IADR-0230](../adr/IADR-0230_meta-work-bundled-prs.md) 決定 1 M-D の但し書き）。

## 計画書との差異

- 差異: なし（計画 `ADR-0048` 決定 2 の方針〔planning 依存を復活させない〕に沿った是正である）。

## 未決事項

- ~~**`IADR-0229` は並行作業が予約している。**~~ → **解消（2026-08-21）**。並行作業が
  `IADR-0229_shared-kernel-result-surface.md` を着地させたため欠番は消え、
  `node scripts/check-adr-numbering.js` は緑（重複・欠番なし・索引と双方向一致・昇順）。
- ~~**テストプロジェクト数 ratchet（`scripts/scripts.repo.test.js` の `found.length, 14`）が 15 と食い違う。**~~
- ~~**`scripts/README.md` 9 行目の追随。** 本 PR の編集許可範囲外のため未実施。~~

  **［2026-08-21 追記 / #877］上の未決事項 2 件は、本 PR が open になる前に**
  **どちらも解消していた。記録が実態に追いついていなかった。**

  AI レビューの 🟢 指摘（PR #878）が「仕様書の記述が実際の diff と食い違っている」と挙げたもので、
  **指摘は正しい**。ただし**同レビューが挙げたコミットは誤り**である。実測:

  ```console
  $ for c in 167be72 1da80ad dd6c867 517c8b9; do
      printf "%s: " "$c"; git show "$c" -- scripts/scripts.repo.test.js | grep -c "found.length, 15"; done
  167be72: 1      ← feat(ADR-0041) 共有カーネル。**ここに在る**
  1da80ad: 0
  dd6c867: 0      ← レビューはこの fix(NFR,IADR-0230) に在ると述べたが、無い
  517c8b9: 0
  ```

  **`14 → 15` は `Platform.Shared.Kernel.Tests` を足したコミット自身が持っている。**
  これは本仕様書が「`src/` を触った側が同じ PR 内で進めるのが筋」と書いた形そのものであり、
  [IADR-0230](../adr/IADR-0230_meta-work-bundled-prs.md) 決定 1 M-A（`src/` 配下は束の外）にも適合する。
  **「本 PR では触らない」という記述だけが、書いた時点の見込みのまま残っていた。**

  `scripts/README.md` 9 行目も同様に `dd6c867` で追随済みである（`fail-open は 2 種類ある` の記述が
  同コミットの diff に含まれることを実測）。

  **教訓**: 未決事項は「解消したら消す」ではなく「**解消したことを日付つきで残す**」。
  消すと、なぜ未決だったのかと誰が解いたのかが失われる。逆に書きっぱなしにすると、
  **記録が実態と逆を向いたまま監査を通ってしまう** —— 本件がその実例である。

---
title: 作業仕様書 履歴由来のクロスリポ表記違反を changelog-overrides の remap で是正する（#595）
type: spec
status: done
related_ids: [NFR, IADR-0140, IADR-0115]
author: Claude
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - 20260807_issue-507_cross-repo-issue-refs.md
---

# 仕様書: 履歴由来のクロスリポ表記違反を changelog-overrides の remap で是正する（#595）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**NFR**。保守性・追跡可能性——計画と実装の相互追跡が誤リンクで壊れないこと）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: [IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md)（検査器の設計・検査対象の決め方）／
  [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（キット同期規約。分類 A のファイルは変更しない）
- 計画書リンク:
  [02_requirements/01_requirements.md](../../planning/projects/microservices-platform/02_requirements/01_requirements.md)
- 関連 issue: [#595](https://github.com/endazon/microservices-platform/issues/595)。
  発端は [#507](https://github.com/endazon/microservices-platform/issues/507)（検査器の新設）。
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
  「クロスリポジトリの issue / PR 番号の修飾」

## 目的・背景

**`develop` が単体で赤い。** `node scripts/check-cross-repo-refs.js` が exit 1 になり、
すべての PR が止まっている。

```console
$ node scripts/check-cross-repo-refs.js
[check-cross-repo-refs] 他リポジトリ参照の表記違反 1 件を検出しました:

  CHANGELOG.md
    CHANGELOG.md:198  [空白区切りの修飾] planning PR #144  →  planning#144
```

違反行は**自動生成された `CHANGELOG.md`**（`scripts/gen-changelog.js` の出力）にあり、その入力は
**コミット `3441861` の件名**である。

```
docs(NFR,IADR-0116): 全面再実装（#454）の着手準備 — planning PR #144 の取り込み・ID レンジ追随・issue 単位の PR 規約 (#459)
```

`.claude/rules/traceability.md:102` は「**修飾語と番号の間に空白を入れない。** 誤: `AST #24` /
`planning PR #144`。正: `AST#24` / `PR planning#144`」と定めている。

**このコミットは恒久履歴にあり、force push は禁止なので書き換えられない。** #507 のクロス監査
（IADR-0140）は `.md` 側の 63 件を是正したが、**生成物 `CHANGELOG.md` と、その入力である
不変の履歴は射程外だった**。#507 のマージ後に CHANGELOG が再生成されて初めて違反が表面化した。

### なぜ #507 の時点で出なかったか

`CHANGELOG.md` は `changelog.yml` が **`develop` への push を契機に**再生成し、
`automation/changelog-update-develop` ブランチの PR で反映する。#507 が検査器を入れた時点の
`CHANGELOG.md` は `3441861` より前に生成された版で、当該行をまだ含んでいなかった。
**検査器の追加と、その検査対象の更新が非同期**だったため、develop へ入ったあとで赤くなった。

## 対象範囲

- 対象:
  - [`scripts/changelog-overrides.json`](../../scripts/changelog-overrides.json) へ `3441861` の
    `remap`（`desc` のみ）を追加
  - [`CHANGELOG.md`](../../CHANGELOG.md) の再生成（`node scripts/gen-changelog.js --out CHANGELOG.md`）
  - [IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md) への日付つき追記
    （生成物と不変履歴の扱いを決定として明文化する）
  - **履歴の悉皆走査**（全コミットの件名・本文に 4 型を当て、同型の残存を実測する）
- 対象外:
  - `scripts/check-cross-repo-refs.js` の検査ロジック（**変更しない**。検出は正しい）
  - `scripts/gen-changelog.js`（**変更しない**。override 機構は既にあり、それを使うのが設計どおり）
  - `scripts/scripts.test.js` / `scripts/check-permission-denials.js` / `scripts/lib/ci-annotate.js`
    （[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md) 分類 A。**変更禁止**）
  - `.github/workflows/`（権限外。`changelog.yml` は `fetch-depth: 0` で既に正しい）
  - `planning/` / `src/ai-stock-trading`（submodule）
  - **git 履歴の書き換え**（force push / rebase / `reset --hard`）。本 issue の前提そのもの

## 設計

### 決定: 検査対象から外すのではなく `remap` で直す

`CHANGELOG.md` を検査対象から外す案（`trackedMarkdown` の pathspec に `:!CHANGELOG.md` を足す）は
**採らない**。根拠は 3 つ。

1. **`CHANGELOG.md` は利用者が読む成果物である。** 表記ゆれの害（機械的突合の不安定）は
   `.md` 一般と同じであり、生成物だからといって規約の外に置く理由が無い。
2. **`gen-changelog.js` は既に `changelog-overrides.json` という是正の口を持っている。**
   誤記コミットを「履歴は書き換えず CHANGELOG 上でのみ補正する」ための機構であり、
   既に 2 件（`b421761` / `3d8852f`）が使っている。**同じ口が使える違反を、別の口
   （検査除外）で処理すると是正の方針が割れる。**
3. **外すと入力側の増殖が見えなくなる。** CHANGELOG は履歴の写しなので、そこが赤いことは
   「新しい違反コミットが入った」ことの信号でもある。外すと信号が消える。

**ただしこの判断は「履歴の違反が少数である」ことに依存する。** 多数あれば override の列挙が
運用コストに見合わない。よって**先に悉皆走査で実測してから確定する**（下記「履歴の悉皆走査」）。
結果は **342 コミット中 1 件**で、閾値の議論をするまでもなく remap で足りる。

### なぜ検査器の提案どおり `planning#144` にしないか

検査器の出す `suggestion` は型 3 の機械的な是正案（空白を詰める）であり、
`PR` の語を落とす。**`PR planning#144` と `planning#144` は意味が違う**——前者は
「planning リポジトリの PR #144」、後者は修飾のみで issue / PR の別を示さない。
規約 `.claude/rules/traceability.md:102-103` が明示している正例も **`PR planning#144`** である。
`suggestion` は「検出した文字列をそのまま置換すれば通る最小形」であって、
**採用すべき表記の唯一解ではない**。remap の `desc` は規約の正例に合わせる。

### remap の形

`applyOverride` は `type` / `scope` / `desc` を任意に差し替え、**省略した項目は元コミットの値を保つ**
（`gen-changelog.js:53-58`）。本件は**件名の表記だけ**が問題で、種別 `docs` も起点 ID
`NFR,IADR-0116` も正しい。よって `desc` のみを指定する。

| 項 | 値 |
| --- | --- |
| `hash` | `3441861` |
| `action` | `remap` |
| `type` | 指定しない（`docs` のまま） |
| `scope` | 指定しない（`NFR,IADR-0116` のまま） |
| `desc` | `全面再実装（#454）の着手準備 — PR planning#144 の取り込み・ID レンジ追随・issue 単位の PR 規約 (#459)` |

元の `desc` との差は **`planning PR #144` → `PR planning#144` の 1 箇所のみ**である。
`(#459)` は末尾のスカッシュ既定 PR 番号で、規約が許す形なので触らない。

## 履歴の悉皆走査（本作業の主眼）

**母集合は「誤りの側」から引く**（#541 / IADR-0140 の教訓）。拡張子でも行フィルタでも絞らない。

### 走査の設計

| 項 | 本走査 | 絞ってはいけない理由 |
| --- | --- | --- |
| 対象 ref | `git log --all`（全ブランチ・全タグ） | `develop` だけだと未マージブランチの違反が将来 develop へ入る |
| マージコミット | **含める**（`--no-merges` を付けない） | CHANGELOG の生成範囲は `--no-merges` だが、生成範囲は将来変わり得る |
| 対象フィールド | **件名（`%s`）＋ 本文（`%b`）** | CHANGELOG は件名しか使わないが、本文の裸 `#NNN` は GitHub 上で実際に誤リンクする |
| 判定 | `check-cross-repo-refs.js` の `findViolations(text, { markdown: false })` | 4 型（長い表記 / 列挙形の修飾漏れ / 空白区切り / 閉じないフェンス）を全部当てる。`markdown: false` は `check-commit-messages.js:325` と同じ扱い（コミットメッセージはバッククォートをコードスパンにしないので**潰さない＝より厳しい**） |

**補助として「4 型に依らない緩い走査」も掛けた。** 修飾語（`planning` / `AST` /
`project-planning` / `ai-stock-trading` / `計画リポ` 等）と `#?\d{2,4}` が同一行に共起する行を
**全部目視**し、4 型が取りこぼす「第 5 の表記」が無いことを確かめる
（IADR-0140 の「形の列挙を先にしてから走査式を書く」への対応）。

### 前提: **クローンが shallow だった**

着手時の作業ツリーは `git log --all` で **115 コミット**しか見えなかった
（`.git/shallow` に 2 本の境界。`git rev-list HEAD --count` = 52）。
**この状態の走査は悉皆ではない。** さらに、この状態で `gen-changelog.js` を走らせると
**CHANGELOG が大量に欠落した版で上書きされる**（生成物の破壊）。

`git fetch --filter=blob:none --unshallow origin` で履歴を完全化してから走査した
（blob を落とさない partial fetch。コミットメッセージの走査に blob は要らない）。

- 完全化後: **342 コミット**（`git rev-list --all --count`）
- 検証: 完全化後に `gen-changelog.js` を走らせた出力は、**コミット済み `CHANGELOG.md` と
  1 行を除いて完全一致**した。差分の 1 行は `CHANGELOG.md` を追加した当の HEAD コミット
  （`ed3e32b`）の行であり、CI が「生成 → コミット」の順で動く以上必ず生じる自己参照の差である。
  **これで「CI と同じ母集合を再現できている」ことが確かめられた。**

### 実測結果

| 項 | 実測 |
| --- | --- |
| 走査コミット総数（`--all`、マージ込み） | **342** |
| **件名**に違反のあるコミット | **1** |
| **本文**に違反のあるコミット | **0** |
| 違反箇所の総数 | **1** |
| 緩い走査の近傍ヒット行（目視） | 79 行 → **4 型以外の違反は 0** |

唯一の違反:

| コミット | 面 | 型 | 検出文字列 | 採用する是正 |
| --- | --- | --- | --- | --- |
| `3441861` | 件名 | 型 3（空白区切り） | `planning PR #144` | `PR planning#144` |

**CHANGELOG に現れていない違反は 0 件である。** 生成範囲外（マージコミット・本文・
未マージブランチ `chore/NFR-586-planning-pin` / `claude/issue-triage-580-merge-mrp3p0` /
`fix/NFR-580-adr-records-drift` / `origin/automation/changelog-update-develop` / `origin/main`）
にも同型は無い。よって将来 CHANGELOG の生成範囲が変わっても、追加の override は要らない。

### 緩い走査で見た「掛からないが紛らわしい」形（偽陰性ではない）

| 形 | 例（コミット） | なぜ違反でないか |
| --- | --- | --- |
| `planning#201 → #552` | `a9c0e6b` 本文 | `→` は列挙の区切りではない。`#552` は**本リポジトリの実在 issue** を指しており正しい |
| `bump planning from \`f099322\` to \`94a7b78\` (#364)` | dependabot 多数 | `planning` と `(#364)` の間に語があり型 3 に掛からない。`(#364)` は本リポの PR 番号で正しい |
| `AST 3 サービス` / `AST 監視銘柄（SC-02 watchlist）` | `6ed3667` / `10d79e0` | 数字が issue 番号ではない |
| `PR planning#244〔裁定依頼 planning#237〕` | `462410b` 本文 | **規約どおりの正例**。本件で採る形と同じ |

最後の行が重要である——**規約の正例（`PR planning#NNN`）は既に履歴で使われている**。
本件の remap はその表記へ揃えるだけであり、新しい書式を導入しない。

## 受け入れ基準

- [x] `node scripts/check-cross-repo-refs.js` が **exit 0**
- [x] `CHANGELOG.md:198` の行が `PR planning#144` になっている（`planning#144` 単独ではない）
- [x] `scripts/changelog-overrides.json` に `3441861` の `remap` があり、`reason` に #595 が書いてある
- [x] override は `desc` のみを指定し、`type` / `scope` は元コミットの値を保つ
- [x] **git 履歴を書き換えていない**（`3441861` の件名は不変）
- [x] `gen-changelog.js` を 2 回走らせて出力が一致する（冪等）
- [x] 履歴の悉皆走査を実施し、件数とコミットを実測で報告した
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が exit 0
- [x] `node scripts/check-doc-links.js` が exit 0
- [x] `node scripts/check-commit-messages.js --base origin/develop` が exit 0
- [x] `git status` に削除（`D`）が無い
- [x] IADR-0140 に日付つき追記がある

## テスト方針

**新しい自動テストは足さない。** 理由は次のとおり。

- `scripts/scripts.test.js` は [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
  分類 A で**変更禁止**。
- `scripts/scripts.repo.test.js`（companion）には既に
  「`check-cross-repo-refs`: 本リポの `*.md` が green（実データ）」が常設されており、
  **`CHANGELOG.md` は `git ls-files -- '*.md'` に含まれるので、この 1 本が本件の回帰を捕まえる**。
  同型が再発すれば `ci.yml` の `scripts-tests` ジョブが落ちる。テストを足すのは重複である。
- override の適用機構そのもの（`applyOverride` / `hashMatches`）は `scripts.test.js:609-668` が
  既に固定している（注入した overrides で `remap` / `exclude` を検証しており、実データに依存しない）。

代わりに**変異試験で「効いていること」を実測**する（下記）。

## 検証（実測）

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-cross-repo-refs.js` | **OK: 525 件の Markdown に違反なし** / exit 0（着手前は 1 件検出 / exit 1。件数はコミット後の値。コミット前は本仕様書が未追跡のため 524） |
| `node scripts/check-cross-repo-refs.js --self-test` | 自己試験 **58 件 all passed** / exit 0（**検査器は未変更**） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **269 tests passed** / exit 0（`scripts.test.js` は未変更） |
| `node scripts/check-doc-links.js` | OK: **451 件**の Markdown に破損リンクなし / exit 0 |
| `node scripts/check-doc-links.js --self-test` | 自己試験 34 件 OK / exit 0 |
| `node scripts/check-commit-messages.js --base origin/develop` | OK / exit 0 |
| `node scripts/gen-changelog.js` ×2 | 2 回の出力が **byte 一致**（`md5 315701b6…`。`--out` 経路でも同値。冪等） |
| `git status --porcelain` | 削除（`D`）**0 件** |
| 履歴走査（`--all` / 件名＋本文 / 4 型） | 342 コミット中 **違反 1 件**（`3441861` の件名のみ） |
| 同（本作業のコミット追加後） | 343 コミット中 **違反 1 件**（増えていない＝本作業が新たな違反を持ち込んでいない） |

### 冪等性について（同一 HEAD で安定、HEAD が進めば自己参照 1 行が増える）

`gen-changelog.js` は**同一 HEAD なら何回走らせても byte 一致**である（生成日時などの可変値を
出力に含めない設計。`gen-changelog.js:148` のコメント）。上の `×2` はこれを実測したもの。

一方、**コミット後に走らせるとそのコミット自身の 1 行が増える**。これは不安定さではなく、
CI が「生成 → コミット」の順で動く以上必ず生じる自己参照である
（`changelog.yml` が develop への push を契機に再生成し、別 PR で反映する）。
**本作業ではこの自己参照行をコミットに含めない**——本コミットはスカッシュマージで別の SHA と
`(#PR)` 付きの件名になるため、ローカル SHA の行を焼き込むと誤った内容が残る。
`develop` へ入った後に `changelog.yml` が正しい形で追加する。

### 変異試験

| # | 変異 | 期待 | 実測 | 判定 |
| --- | --- | --- | --- | --- |
| M1 | override の `hash` を実在しない値へ壊す（`3441861` → `9999999`） | CHANGELOG が元の誤記へ戻り検査が赤くなる | 再生成で `planning PR #144` が復活し `CHANGELOG.md:199 [空白区切りの修飾]` で **exit 1** | **落ちた（検出）** |
| M2 | override の `action` を `remap` → `romap`（未知の値）へ壊す | 警告を出し補正を適用しない | `警告: changelog-overrides.json の hash "3441861" の action "romap" は未知` を stderr へ出力し、CHANGELOG が誤記へ戻り **exit 1** | **落ちた（検出）** |
| M3 | `desc` を検査器の提案どおり `planning#144`（`PR` 落ち）にする | 検査は通るが規約の正例と食い違う | `OK: 524 件…` / **exit 0**。**検査だけでは `PR` 落ちを捕まえられない** | **素通り（既知の限界）** |
| — | 基準（無変異） | 通る | `check-cross-repo-refs` exit 0（524 件）/ CHANGELOG に `PR planning#144` | — |

**素通りしたもの（隠さない）**: **M3 は機械では捕まらない。** 検査器は「型 3 に当たらない」ことしか
見ないため、`PR` を落とした `planning#144` でも exit 0 になる。`PR planning#144` を選んだ根拠は
規約の正例（`.claude/rules/traceability.md:102-103`）であり、**人間のレビューでしか守れない**。
本仕様書と override の `reason` に根拠を残すことでこれを補う。

## 既知の限界

1. **override は「1 コミット = 1 エントリ」の手作業である。** 履歴に同型が増えれば増えるほど
   列挙が伸びる。今回は 342 コミット中 1 件なので成立しているが、**将来 10 件を超えるようなら
   検査除外か、生成側での一括正規化を再検討すべき**である（IADR-0140 の追記に閾値の観点として記した）。
2. **入口が塞がれたのは 3 面だけである。** 新しい違反コミットが入るのを止めるのは
   `check-commit-messages.js`（件名・本文・PR タイトル）であり、**PR 本文・issue 本文・
   レビューコメントは依然として未検査**（IADR-0140 決定 2 の限界）。
3. **`CHANGELOG.md` の再生成タイミングと検査は非同期である。** 本件の再発形——「違反コミットが
   develop へ入る → `changelog.yml` が CHANGELOG を再生成 → その PR で初めて赤くなる」——は
   `check-commit-messages.js` が件名を止めるので原理的には起きないが、**allowlist で除外された
   件名や、検査が入る前の履歴**では起き得る。
4. **shallow clone では悉皆走査ができない。** 本作業は `--unshallow` してから走査した。
   同種の作業をするときは、**まず `git rev-list --all --count` を確認すること**。
   気付かずに `gen-changelog.js --out CHANGELOG.md` を走らせると、生成物を欠落した版で破壊する。

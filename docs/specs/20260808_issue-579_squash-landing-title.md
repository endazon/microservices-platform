---
title: 作業仕様書 スカッシュ着地件名を検査する経路を作り、FR/UC/SC の実在性を機械化する（#579）
type: spec
status: done
related_ids: [NFR, FR-12, SC-07, IADR-0115, IADR-0140, IADR-0141, IADR-0145]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs: []
related_specs:
  - ../adr/IADR-0145_landed-subject-check-scope.md
  - ../adr/IADR-0141_audit-rounds-and-population-drawing.md
  - 20260808_issue-581_adr-numbering-check.md
---

# 仕様書: スカッシュ着地件名の検査と FR/UC/SC の実在性（#579）

> 本作業は **3 つのことを同時に行う**: ①#568 の記録の是正（生成物のみ）／②着地件名を見る検査の新設／
> ③FR/UC/SC の実在性検査。**②③はどちらも「直す違反」がゼロで、作るのは壊れたときに止まる仕組みである。**

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#579**（親 #454）／起点 ID: **NFR**（是正対象の記録は **FR-12 / SC-07**）
- 発見: 定期監査（`traceability-auditor`・2026-08-07・`ef588ce`）
- 原因の PR: **#568**（`bc7bc8e`）
- 分類（[[IADR-0141]] 決定 4）: **「機械検査を新設する」＋「規約を改定する」** → 重い方を採る。
  クロス監査は**フェーズ末に 1 回**（同 決定 4 の 2026-08-08 追記）
- 制約: **[[IADR-0115]] 分類 A** ——`scripts/scripts.test.js` は変更禁止（読むのは可）。
  `.github/workflows/` は GitHub App 権限で編集不可（[[IADR-0140]] 決定 2）

## 母集合の引き直し（[[IADR-0141]] 決定 1）

**走査基準**: `origin/develop` = `b71630f`（#606 マージ後）。**issue 本文が挙げた 3 件（#567/#568/#569）は
母集合ではない。** 着地件名を全件走査した。

### 軸 1: 着地件名の書式違反（`validateSubject`）

| 段階 | 件数 |
| --- | ---: |
| `git log --no-merges` の全件 | 341 |
| うち `(#NNN)` で終わる**着地件名** | **316** |
| うちスコープ `()` を持つもの | 307 |
| `validateSubject` の違反（**bot 除外前**） | 52 |
| **bot 著者・Revert / `[skip ci]` を除外した違反** | **10** |

**52 → 10 の差はすべて dependabot の `build(deps): ...`** である。`check-commit-messages.js` は
これを**著者**で除外しており、件名だけを見ると違反に見える。**除外の軸を件名側だけで引くと 5 倍に膨らむ。**

残る 10 件は**すべてリポジトリ初期（#1〜#95）**、`pr-title.yml` が存在しなかった時期のものである。
force push 禁止で直せないため **baseline へ据え置く**（[[IADR-0145]] 決定 4）。

### 軸 2: 着地件名の起点 ID が計画レンジに実在するか

| 対象 | 実測 |
| --- | ---: |
| 着地件名のスコープに現れる `FR-xx` / `UC-xx` / `SC-xx` | — |
| **計画レンジ（`.claude/rules/traceability.md`）に実在しないもの** | **0 件** |

**したがって軸 2 の検査は是正ではなく予防である。** issue が挙げた
`feat(SC-99)` / `feat(FR-77)` / `feat(UC-88)` が exit 0 で受理される、という指摘は再現した
（下記「変異試験」M1・M2）が、**実際に混入したものは 1 件も無い。**

### 軸 3: **PR タイトルと着地件名の突合** —— ★ 引けなかった軸とその理由

**#568 で実際に起きた「ID の脱落」を全件で検出するには、着地件名 316 件それぞれの PR タイトルが要る。**
PR タイトルは**リポジトリの中に無く**、GitHub API を引くしかない。

- `scripts/` の検査器は**外部依存ゼロ**（Node 標準モジュールのみ）を守っており、API 依存を持ち込めない。
- 一時的に API で引く選択肢はあったが、**引いても機械検査にはできない**（検査器が API に依存できない）ため、
  母集合の一次調査としての価値しか無い。
- **したがってこの軸は引かず、「機械では判定できない」ことを [[IADR-0145]] 決定 3 として明記する方を選んだ。**
  **黙って落としたのではなく、落とした理由をここに書く**（[[IADR-0141]] 決定 1 規則 6）。

> **★ この作業でいちばん重要なのは、この「引けなかった軸」である。**
> 検査を 1 本足したことで「着地件名は守られている」と読まれるのが最も危険な誤解であり、
> **#579 の起点となった事故そのものは、入れた検査では捕まらない。**

### 引かなかった軸

| 軸 | 理由 |
| --- | --- |
| マージコミット | `--no-merges` で除外（スカッシュ運用なので着地件名はマージコミットにならない） |
| 他リポジトリの計画 ID（`AST/SC-01`） | 修飾付き ID は自名前空間の突合対象外（規約「複数プロジェクトを跨ぐ場合の ID 修飾」） |
| コミット**本文** | 本検査の対象は件名。本文のクロスリポ表記は `check-cross-repo-refs.js` が既に見ている |

## やったこと

### 1. #568 の記録を是正した（履歴は不変・生成物のみ）

`scripts/changelog-overrides.json` へ `bc7bc8e` の `remap` を追加し、`scope` を
`FR-12` → **`FR-12,SC-07`** へ戻した。`type`（`feat`）と `desc` は元コミットの値を保つ
——**誤っているのは落ちた ID だけ**である。`node scripts/gen-changelog.js --out CHANGELOG.md` で再生成し、
`CHANGELOG.md` の該当行が `**FR-12,SC-07**` になったことを実測した。

> **★ ここで自分でバグを 1 つ作った（記録する）**: 最初の編集で JSON の要素境界を誤り、
> **隣の `3441861` エントリの `desc` が `bc7bc8e` の行に載った**（`FR-12,SC-07` なのに要約が
> 「全面再実装（#454）の着手準備 …」になった）。再生成して初めて気づいた。
> **JSON を手で編集したら、生成物を実際に作って目で確かめる。** 構文が通ることは意味が通ることを保証しない。

### 2. 着地件名の検査を新設した（`scripts/check-landed-subjects.js`）

`(#NNN)` で終わる件名を `validateSubject` ＋ `validateIdExistence` へ通す。
ラチェット（`scripts/landed-subject-baseline.json`）で既存 10 件を据え置き、**新規混入だけを落とす**。
CI は `scripts.repo.test.js` 経由で `ci.yml` の `scripts-tests` に載る（新ワークフローは足さない）。

### 3. FR / UC / SC の実在性検査を足した（`scripts/check-commit-messages.js`）

レンジのパーサは `check-test-traceability.js` の `readPlanIds()` を**再利用**する
（同じ事実を 2 本のパーサで持たない）。**fail の向きを 2 つに割った** ——
モジュールが無い構成は skip＋notice、節がパースできない場合は例外（[[IADR-0145]] 決定 2）。

### 4. 規約へ明記した（`.claude/rules/traceability.md`）

「スカッシュ件名を書き直すときはスコープの ID を 1 つも落とさない」と、
**その事故は機械では検出できない**こと、**最も確実なのは件名を書き直さないこと**を書いた。

## 変異試験（すべて実測）

### 実在性検査（`--title` 単一件名モード・実バイナリ）

| 変異 | 是正前 | 是正後 |
| --- | --- | --- |
| M1 `feat(SC-99): 存在しない画面 ID` | exit 0 | **exit 1** |
| M2 `feat(FR-77)` / `feat(UC-88)` | exit 0 | **exit 1** |
| M3 `feat(SC-06)` / `feat(FR-12,SC-07)`（正当） | exit 0 | **exit 0**（偽陽性なし） |
| M3' `feat(FR-012)`（ゼロ埋め桁違い） | exit 0 | **exit 0**（`normalizePlanId` で正規化。桁数で誤検出しない） |
| M3'' `chore(NFR)`（連番を持たない） | exit 0 | **exit 0** |
| **M4 計画レンジを 1 つ伸ばす**（`FR-01..22` → `FR-01..23`） | — | **`FR-23` を実在として受理する**（54 → 55 件）。レンジの伸長に追随する |

### 着地件名の検査（純関数 ＋ 実バイナリ）

| 変異 | 結果 |
| --- | --- |
| M1 ブランチ名由来の既定件名（`Claude/issue 71 ...`） | 検出する |
| M2 起点 ID の無い `feat:` 件名 | 検出する |
| M3 `feat(SC-99)`（実在しない画面 ID） | 検出する |
| M4〜M6（負例） | 正当な件名 / dependabot / Revert は**落ちない** |
| ラチェット 3 件 | baseline 内は素通り／新規は `added` で fail／直ったのに残っていれば `fixed` で fail |
| **実バイナリ: baseline から 1 件外す** | **exit 1**（`3d8852f Claude/issue 71 ...` を出力） |

### ★ 変異試験が設計を 1 段変えた（fail-open の穴を自分で作って踏んだ）

**baseline に存在しないハッシュ `0000000` を 1 行足したら、検査全体が exit 0 の緑になった。**
当初 `baselineReachable()` が「ハッシュを解決できない＝浅いクローン」と決め打ちしており、
**打ち間違い 1 つで検査が黙って無効化される**状態だった。

`scanPrecondition()` へ作り替え、`git rev-parse --is-shallow-repository` で
**「浅いクローン（skip）」と「履歴は完全なのに解決できない（fail）」を割った**。
再走で **exit 1**（`baseline のハッシュ 1 件が履歴に見つからない: 0000000`）を確認した。

**「壊すと落ちる」を実測していなければ、入れたのに黙って無効化できる検査になっていた。**

## ［2026-08-08 追記 / #612 レビュー 🔴］変異試験の当て方を間違えていた

**指摘**: FR/UC/SC 実在性検査を `main()` のレンジモード（`ci.yml` の `commit-messages` ジョブが
実際に実行する経路）へ配線しておらず、**必須チェックでは無効のままだった。**

**裏取り**: `grep -n 'validateIdExistence(' scripts/check-commit-messages.js` → 呼び出しは 3 箇所。
うち `main()` の 1 箇所だけが 3 引数だった。**指摘は正しい。**

**なぜ気づけなかったか（ここが本質）**: 上表の M1〜M4 を**すべて `--title` 単一件名モードで当てた**。
`--title` は `checkSingleTitle` を通り、そこには `planIds` を渡していたので**全部緑になった**。
**「検査が落ちること」は確かめたが、「CI が実際に走らせる経路で落ちること」は確かめていなかった。**

**是正**:
1. `main()` のループへ `planIds` を渡した。
2. `scripts.repo.test.js` へ**実バイナリでレンジモードを通す**テストを 3 件追加した
   （使い捨ての git リポジトリに件名 1 件のコミットを作り、`--range HEAD~1..HEAD` で検査する）。
3. **そのテストが空振りでないことを変異で確かめた** —— 1 の修正を戻すと
   `AssertionError: レンジモードで実在性検査が効いていない（planIds の配線漏れ）` で赤くなる。

**この型はこのリポジトリで 3 度目である**（`crossRepoRefReasons` のラベル欠落が #507 と #590）。
**引数を増やすときは、呼び出し口を `grep` で全部出してから配線する。**

## 素通りするもの（開示）

- **PR タイトルからの ID 脱落**（軸 3・[[IADR-0145]] 決定 3）。**#579 の起点そのもの。**
- **マージ前の予防**。着地件名の検査は**事後検知**であり、恒久履歴への混入は止められない。
- **要約文の書き換え**（#567 の型）。ID が保たれていれば規約上の問題ではない。
- **計画 ADR（`ADR-xxxx`）の実在性**は従来どおり submodule 未 populate で skip される
  （規約が既に明記。本 issue では変えない）。

## 受け入れ基準（#579）

- [x] `CHANGELOG.md` の該当エントリが `FR-12,SC-07` を持つ（remap 経由・**履歴は不変**）
- [x] `feat(SC-99)` 型が落ちる
- [x] 正当な件名が落ちない（`feat(SC-06)` / `feat(FR-12,SC-07)` / `feat(FR-012)` / `chore(NFR)`）
- [x] `traceability.md` に「スカッシュ件名で ID を落とさない」が明記されている
- [x] スカッシュ着地件名の検査について、**できること／できないこと**（ワークフロー編集不可・
      **ID 脱落は機械判定不能**）が本書と [[IADR-0145]] に記録されている

## 検証

```
node scripts/check-landed-subjects.js --self-test
node scripts/check-landed-subjects.js
node scripts/check-commit-messages.js --title "feat(SC-99): x" --author endazon   # exit 1
node scripts/check-adr-numbering.js
node scripts/check-doc-links.js
node scripts/check-cross-repo-refs.js
node scripts/check-plan-id-qualification.js
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
node scripts/gen-changelog.js --out CHANGELOG.md
```

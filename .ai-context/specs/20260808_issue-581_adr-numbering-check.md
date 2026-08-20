---
title: 作業仕様書 IADR 採番の一意性を機械検査する（#581）
type: spec
status: done
related_ids: [NFR, IADR-0115, IADR-0140, IADR-0141, IADR-0143, IADR-0144]
author: Claude
created: 2026-08-08
updated: 2026-08-08
plan_refs: []
related_specs:
  - ../adr/IADR-0141_audit-rounds-and-population-drawing.md
  - ../adr/IADR-0143_plan-id-qualification-checker-scope.md
  - 20260808_issue-576_ast-id-qualification.md
---

# 仕様書: IADR 採番の一意性を機械検査する（#581）

> 本仕様書は実装着手前に作成した。**本作業は「違反を直す」作業ではない** ——
> develop は現時点で全判定 clean であり、作るのは**壊れたときに止まる仕組み**である。

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#581**（親 #454）／起点 ID: **NFR**
- 発見: 定期監査 1 回目（`adr-guardian`・2026-08-07・`ef588ce`）
- 前提: **#580**（Superseded 表記の統一）は**完了済み**（open issue 一覧に無いことを実測）
- 規約: `.claude/rules/traceability.md`「採番衝突時の改番手順」
- 分類（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 4）: **「機械検査を新設する」** —— クロス監査は**フェーズ末に 1 回**
  （利用者裁定 2026-08-08。PR ごとには走らせない）

## 母集合の引き直し（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1）

**走査基準**: `origin/develop` = `296b4f6`（#603 マージ後）。

### ★ この作業の母集合は「違反」ではなく「判定の対象」である

是正作業なら「誤りの側から引く」が効くが、**本作業には直す違反が 1 件も無い**。
したがって母集合は **5 判定それぞれの対象集合**であり、**現状値はすべて基準線（baseline）**として働く。

| 判定 | 対象 | 実測（`296b4f6` ＋ 本 PR 着手時点） |
| --- | --- | --- |
| 1. 番号の重複 | `docs/adr/IADR-*.md` のファイル名番号 | **0 件** |
| 2. 欠番 | 同上を `0000..NNNN` と突合 | **0 件**（`0000..0143` が連続） |
| 3. ファイル名 = 本文の自称番号 | 各ファイル冒頭 | **不一致 0 件** |
| 4. 索引 ⇔ 実ファイル（**双方向**） | `docs/adr/README.md` の `\| [IADR-xxxx](...)` 行 | **過不足 0 件** |
| 5. 索引の並び | 同上の出現順 | **昇順 OK** |
| — | ファイル総数 | **144** |

> **★ issue 本文の「139 件」は着手時点で既に古い。** #581 は 2026-08-07 の実測値で、
> その後 `IADR-0140`〜`0143` が増えて **144 件**になっている。
> **件数を受け入れ基準に書かない** —— 書けば次の IADR 追加で必ず古くなる（#590 の教訓）。
> **判定は「連続していること」であって「N 件であること」ではない。**

### 引いた軸と、引かなかった軸

| 軸 | 引いたか | 理由 |
| --- | --- | --- |
| ファイル名の番号 | ✅ | 判定 1・2・3 の基礎 |
| 本文の自称番号 | ✅ | 判定 3。ファイル冒頭 400 文字に `IADR-xxxx` が現れるかで見た |
| 索引行 | ✅ | 判定 4・5 |
| **計画 ADR（`ADR-xxxx`）の採番** | ❌ | **計画リポの所有物**であり本リポは pin を進めるだけ。実装側が採番しない |
| **`docs/superpowers/` の旧 IADR** | ❌ | 保管された旧計画。live な採番空間ではない |

## やること

1. **`scripts/check-adr-numbering.js` を新設**し、上記 5 判定を機械化する。
2. **`--self-test`** を持たせる（正例・負例を**対で**）。
3. **CI へ結線** —— `.github/workflows/` は編集不可なので `scripts/scripts.repo.test.js` 経由で
   `ci.yml` の `scripts-tests` へ相乗りする（[IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md) 決定 2・[IADR-0143](../adr/IADR-0143_plan-id-qualification-checker-scope.md) と同じ経路）。
4. **変異試験 M1〜M6** を実測し、**素通りするものを開示**する。

### 設計上の注意（先に決めた）

- **件数を baseline に焼かない。** 判定は「連続」「重複なし」「双方向一致」という**関係**であり、
  総数は関係から導かれる。数を持つと IADR を足すたびに更新が要り、必ず古くなる。
- **検査器の自己参照**（[IADR-0143](../adr/IADR-0143_plan-id-qualification-checker-scope.md) 決定 4）: 本検査は `docs/adr/` のファイル名と索引しか見ないので、
  自分自身（`scripts/`）は構造的に対象外である。**#576 で踏んだ死角はここには無い**。
  ただし**自己試験のフィクスチャは一時ディレクトリに作る**（実データを汚さない）。
- **`docs/adr/README.md` の索引行の抽出**は、既存の `inspectAdrIndexTitles`（`scripts.repo.test.js` の
  索引タイトル列ラチェット）が使っている正規表現と**同じ形**に揃える
  —— 2 箇所で別々に書くと片方だけ直したとき挙動が割れる（行番号はここに書かない。ずれて腐る）。

## ［2026-08-08 追記 / #606 レビュー］引き直した 3 点

PR #606 の AI レビューが指摘した 3 点はいずれも**測って裏を取った上で**是正した。

### 1. #580 からの申し送りが未消化だった（🟡・実測で確認）

[`20260807_issue-580_adr-records-drift.md`](20260807_issue-580_adr-records-drift.md) は
「#581 が採番の機械検査を入れるときは、**本ブロックを #581 側の検査へ統合し、
`scripts.repo.test.js` からは削除する**。**同じ不変条件の検査を 2 本残さない**」と申し送っていた。

**指摘の裏取り**: `git diff origin/develop...HEAD -- scripts/scripts.repo.test.js | grep -c '^-[^-]'`
→ **0**。追加のみで、`inspectAdrIndex` ブロックは残ったままだった。**指摘は正しい。**

**是正**: `inspectAdrIndex`（`not-linked` / `id-file-mismatch` / `no-trailing-pipe`）を
**判定 6** として `check-adr-numbering.js` へ統合し、`scripts.repo.test.js` から削除した。
**fail-open 下限（索引行数 >= ADR 本体数）は引き継がない** —— 判定 4 の `index-missing` と
`no-adr-files` が同じことをより直接に見るためで、下限値という手作業の更新点も消える。
変異試験 **M8〜M10** で 3 種すべてを対で固定した（[IADR-0144](../adr/IADR-0144_adr-numbering-check-scope.md) 決定 6）。

### 2. 委譲された「状態列 ⇔ 本体 `status:` の突合」が未実装かつ未開示だった（🟡）

#580 は 3 項目を #581 へ委ねていたが（状態列の突合・採番の連続性・索引行の欠落）、
実装したのは後ろ 2 つだけで、**1 つ目は実装も開示もしていなかった**。
**実装しない判断**（状態セルは `Superseded by IADR-XXXX` を含む自由文で本体 `status:` の語と
1 対 1 に対応せず、先に語彙の規約化が要る）を [IADR-0144](../adr/IADR-0144_adr-numbering-check-scope.md)「検出しないこと」と
スクリプト冒頭の両方へ**開示**した。**開示は検査の代わりにならないが、黙って落とすのとは違う。**

### 3. 判定 2 が「先頭がまるごと欠けている」型を取りこぼす（🟢）

欠番の走査起点を**実在する最小番号**にしていたため、`IADR-0000` から数本消えても「連続」で緑になった。
**下端を 0 に固定**し、`numbering-not-from-zero` を新設。変異試験 **M7** で固定した。

## この検査が防げないこと（#581 が明記を求めている）

**衝突の本体は「並行 PR が互いの採番を見ない」ことであり、本検査は develop に着地した後しか見られない。**

- **衝突を未然に防げない。** 2 本の PR が同じ番号を持ったまま並行しても、先にマージされた側は通り、
  **後発の CI が初めて落ちる**。
- それでも価値はある —— **現状は後発も素通りし、人が気づくまで develop が壊れたまま**である。
  実際に**衝突は 2 回起きて 2 回とも人手で事後修復**している（#530・#533/#538）。
- 規約は「**先着尊重・後発は次の空き番号へ・欠番を作らない**」と定めており、本検査は
  **その後半（欠番を作らない・重複を残さない）だけを機械化する**。先着の調停はできない。

## 受け入れ基準（#581 より）

- [x] 5 判定すべてが機械化され、`--self-test` を持つ（**#580 から統合した判定 6 を加えて 6 判定**）
- [x] CI の既存呼び出し口から実際に走る（**変異を当てて落ちることを実証する**
      —— `scripts.repo.test.js` が `--self-test` ／ 実データ ／ CLI の exit 1 を固定）
- [x] 「防げないこと」（並行 PR の未然防止はできない）が仕様書と実装 ADR に明記されている
- [x] 変異試験 **M1〜M10** の結果が開示されている（自己試験 16 件が常設・all passed）

## 検証

```
node scripts/check-adr-numbering.js --self-test
node scripts/check-adr-numbering.js
node scripts/check-doc-links.js
node scripts/check-plan-id-qualification.js
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
```

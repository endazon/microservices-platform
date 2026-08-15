---
title: 作業仕様書 — feedback/ 11 件の本文から #721 の追記ブロックを撤去する（#733）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0166
  - IADR-0187
  - IADR-0191
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/docs/ai-implementation-workflow-guide.md"
related_specs:
  - "../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md"
  - "../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md"
---

# 作業仕様書 — `feedback/` 11 件の本文から #721 の追記ブロックを撤去する（#733）

## 1. 起点と、利用者承認

[#733](https://github.com/endazon/microservices-platform/issues/733) は **AI が独断で実装してはならない** issue として起票されている。
マージ済みの判断（PR #726 / [IADR-0187](../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md) 決定 2 の補足）を覆す是正であり、
**規則を決めた側（同じ AI）が続けて自分の過去の作業を消しに行く形**になるためである。

> **利用者裁定（2026-08-15）: 「追記を撤去する」を採る。**
> 選択肢として「本文への追記も可へ倒す」（＝ 11 件はそのまま。ただし [IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) 決定 2 と
> [IADR-0166](../adr/IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 2 の**両方を改定する新 IADR が要る**）も併記したうえでの判断である。

## 2. 根拠となる決定

[IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) 決定 2 が、記録を書き換えてよい境界を確定している。

| 対象 | 可否 | 理由 |
| --- | --- | --- |
| **frontmatter の状態欄**（`status` / `dispatched:` / `planning_issue:` / `updated:`） | **可** | キットが更新主体を定めている |
| **本文**（**日付つき追記ブロックを含む**） | **不可** | 本文は「送った内容」そのもの |

**#721（PR #726）は `feedback/` 11 件の本文へ `［2026-08-14 追記 / #721］` を足しており、本決定の下では不可に当たる。**

経緯も記録されている —— IADR-0187 決定 2 の補足は「規約の射程外だから可」と論じたが、
**先に同じ線を引いていた [IADR-0166](../adr/IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 2 を一度も引用していない**（`grep` で 0 件。#732 のレビューが独立に確認）。

## 3. 対象（着手前の実測 2026-08-15・`a87f50b`）

```console
$ grep -l "［2026-08-14 追記 / #721］" feedback/*.md | wc -l
11
```

11 件すべてで**形が揃っている** —— 空行 1 行 ＋ `>` で始まる引用 3 行 ＋ 空行。本文末尾（「関連」節の直後）に置かれている。

## 4. 実装方針

1. **11 件から追記ブロック（引用 3 行）と、それが作った余分な空行を撤去する**
2. **frontmatter は一切触らない** —— `status: open` / `dispatched: true` はそのまま残す（IADR-0191 決定 2 で「可」の側）
3. **[IADR-0187](../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md) 決定 2 の補足へ日付つき追記**を入れ、当該補足が誤りだったことと IADR-0191 決定 2 が正であることを記録する
   （**ADR は live な権威文書なので日付つき追記の対象であり、`feedback/` とは扱いが違う**）
4. **回帰検査**を `scripts/scripts.repo.test.js` へ置く。**当初は「本文に追記ブロックが無い」ことを固定する予定だったが、
   §6 の実測により置けないと分かった**ため、baseline 付き **ratchet**（新規混入は fail・既知は許容・減らし忘れも fail）とする。**変異試験で検出を確認する**

### 4.1 情報は失われない（撤去前に確かめた）

| 追記が述べていたこと | 撤去後の在り処 |
| --- | --- |
| `triaged` → `open` ＋ `dispatched: true` へ移した事実 | **frontmatter の値そのもの**（残る） |
| 移行の理由（上流裁定 planning#323） | **IADR-0187** ／ `docs/specs/20260814_issue-721_*.md` |
| いつ誰が変えたか | **git 履歴**（`git log -p feedback/`） |

## 5. 受け入れ基準（#733 から）

- [x] **利用者の承認がある**（2026-08-15。§1 に記録）
- [x] 11 件から追記ブロックが撤去され、**frontmatter の状態欄は変わっていない**（差分は 44 行削除のみ。`git diff` に frontmatter 行は 1 行も無い）
- [x] IADR-0187 決定 2 の補足に、誤りだった旨の日付つき追記がある
- [x] 回帰テストがあり、**変異試験で検出を確認**している（2 変異とも検出）
- [x] 必読 2 ファイルが予算内かつ下限（余白 1,000B）を割っていない（**48,976 B / 余白 2,224 B**。本 PR は両ファイルを変更していない）

### 実測ログ（2026-08-15）

```console
$ grep -c "追記 / #721" feedback/*.md | grep -v ":0" | wc -l
0                                    # 撤去完了

$ git diff --stat feedback/ | tail -1
 11 files changed, 44 deletions(-)   # 3 引用行 + 空行 1 × 11 件

$ git diff -U0 feedback/ | grep -E "^[+-](status|dispatched|planning_issue|updated):"
                                     # 空 ＝ frontmatter 無傷

$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
✓ 518 tests passed                   # 516 → 518（新規 2 件）

# 変異試験
本文へ新しい追記ブロックを足す → 「新規に足された」で検出
#721 の追記ブロックを戻す      → 「#721 の追記ブロックが残っている」で検出

$ wc -c CLAUDE.md .claude/rules/traceability.md
48976 B 合計 / 予算 51200 B / 余白 2224 B
```

### 回帰検査を ratchet にした理由

**受け入れ基準の「本文に追記ブロックが無いことを固定する」は、そのままでは置けなかった。**
残る 15 ブロック（#497 の 10・#712 の 3・ID 無しの 2）があり、**うち #497 の 10 件は消してはならない**（§6 参照）。
よって `scripts/feedback-body-addendum-baseline.json` を持つ **ratchet**（新規混入は fail・既知は許容・
減らし忘れも fail）とした。**規約の衝突そのものは #743 で追跡する。**

## 6. 母集合（`.claude/rules/traceability.md` §是正・追随の母集合の取り方）

**是正の対象は「`feedback/` 本文に後から差し込まれた日付つき追記ブロック」である。** #721 の追記だけを狙い撃ちにすると、
**同型が他にも在った場合に取り残す**ため、誤りの側から引く。

### 走査語（規則 1・2・7）

`［2026-08-14 追記` / `追記 / #721` / **`［YYYY-MM-DD 追記` の全変種（日付・ID を問わない）**

### 結果 —— **#721 の 11 件で尽きていない**

```console
$ grep -o "［20[0-9][0-9]-[0-9][0-9]-[0-9][0-9] *追記[^］]*］" feedback/*.md | ...
```

| 由来 | 件数 | 形 | 中身 |
| --- | ---: | --- | --- |
| **`#721`** | **11** | 引用（`>` 3 行） | frontmatter の状態変更（`triaged` → `open` ＋ `dispatched: true`）を**本文で二重に述べたもの** |
| `#712` | 3 | 引用 | 同型（`open` → `triaged` の是正） |
| **`#497`** | **10** | **`##` 見出し ＋ 本文** | **「判定: accepted」＝ トリアージ結果**。`ADR-0012` の `Accepted` 化の根拠を述べている |
| ID 無し | 2 | 引用 | 実装の消化結果（#634 / #635 / #640 / #544 が実装した旨） |

**当初この節に「#721 以外の混入は 0 件」と書いたが、実測すると誤りだった。** 書いてから測る順序になっており、
本リポジトリが繰り返し記録している型そのものである。**測ってから書き直した。**

### **26 ブロックは同質ではない —— 一律に撤去してはならない**

[IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) 決定 2 は「本文（日付つき追記ブロックを含む）は不可」と
一般規則として書かれているが、**`#497` の 10 件を消すと計画リポの規約に反する**。

> `project-planning` の `CLAUDE.md` §中間成果物:
> **裁定・決定の内容そのものは必ずリポジトリへ残す。** 環流記録（`draft/feedback/`）の「**トリアージ結果**」・
> 計画書の変更履歴・ADR のいずれかに記録し、**送付物が手元から失われても決定の根拠を追える**状態にする

`#497` の追記は**まさにその「トリアージ結果」**であり（`判定: accepted`）、`ADR-0012` が `Accepted` 化された根拠を述べている。
**frontmatter には無い情報**であり、撤去すると失われる。

| 由来 | 撤去してよいか | 根拠 |
| --- | --- | --- |
| **`#721`（11）** | **よい（本作業の対象）** | 述べている内容が **frontmatter の値そのもの**であり、理由は IADR-0187、経緯は git 履歴にある |
| `#712`（3） | 同型だが**本作業の承認範囲外** | 利用者承認は #733（＝ #721 の 11 件）に対して出ている |
| **`#497`（10）** | **撤去してはならない** | **トリアージ結果＝裁定の記録**。計画リポ規約が保存を求める |
| ID 無し（2） | **要確認** | 実装の消化結果。issue 側に在るか未確認 |

**したがって回帰検査は「`feedback/` 本文に日付つき追記ブロックが 1 つも無い」ことを要求できない。**
残る 15 件を baseline に持つ **ratchet**（新規混入は fail・既知は許容）とする —— 本リポが
`backend-library-baseline.json` / `adr-index-title-baseline.json` で既に採っている形である。

**規約の衝突そのもの（IADR-0191 決定 2 と「裁定の記録は残す」）は本作業では解けない。** 別 issue で起票する。

### 除外したものと理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| `feedback/*.md` の **frontmatter** | IADR-0191 決定 2 で「可」の側。撤去対象ではない |
| `docs/adr/` の日付つき追記 | **ADR は live な権威文書**であり、追記は正しい作法（IADR-0191 決定 2 の対象外） |
| `docs/specs/` | `追記 / #721` の grep は **1 件ヒットするが、それは本仕様書自身が文字列を引用しているだけ**であり、追記ブロックではない。確定済みの仕様書に #721 由来の追記ブロックは **0 件** |
| `planning/draft/feedback/` の写し | **別リポジトリ**。本リポジトリからは変更できない |

## 7. 対象外（本作業でやらないこと）

- **`feedback/` の frontmatter の値を変えること**。`status` / `dispatched:` は #721 の成果として正しく、IADR-0191 決定 2 で「可」の側である
- **IADR-0187 決定 2 そのものの撤回**。誤っていたのは決定ではなく**補足の論拠**であり、日付つき追記で是正する
- **`planning/draft/feedback/` の写しの是正**（別リポジトリ）

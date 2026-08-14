---
title: 作業仕様書 — 「feedback/ は書き換えない」の射程を一意にする（#717）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0166
  - IADR-0185
  - IADR-0187
  - IADR-0189
  - IADR-0190
  - IADR-0191
author: claude
created: 2026-08-14
updated: 2026-08-14
plan_refs:
  - "../../planning/tools/impl-handoff-kit/repo-template/feedback/README.md (status の「誰が書き換えるか」)"
related_specs:
  - "../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md"
  - "./20260814_issue-728_follow-planning311-unnumbered-nfr-clause.md"
---

# 作業仕様書: 記録の書き換え境界（#717）

## 起点

- **NFR**（文書統制。**当たる番号が無い＝場合 ②** なので無採番・環流しない。[IADR-0189](../adr/IADR-0189_follow-upstream-adjudication-in-kit.md) 決定 1）
- 起点 issue: **#717**。実装 ADR: **[IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md)**
- 出所: **PR #715 の AI レビュー 🟡**

> **★ 値の基準時点は develop `de4a5d6` / planning pin `915981a`（2026-08-14 実測）である。**

## ★★ 母集合 —— 実測で引いた

### ★ 軸 a: **先にキットを見た**（#728 の教訓）

| 確かめたこと | 結果 |
| --- | --- |
| キットに「Superseded 引用書式」の節はあるか | **無い**（見出しを全数で確認）。**当該の一文は本リポ固有**であり、上流の制約は無い |
| キットは `feedback/` の書き換えについて何か定めているか | **定めている** —— `feedback/README.md` が **`status` の「誰が書き換えるか」を表で持つ** |

**→ 読み B（一切書き換えない）は上流と矛盾するので採れない。** **一意に決まった。**

> **★ #728 で「着手前にキット本文を読む」を学んだ直後の適用である。**
> **本リポの理屈だけで組んでいた [IADR-0185](../adr/IADR-0185_feedback-status-vocabulary.md) / [IADR-0187](../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md) の補足より、根拠が強い。**

### ★★ 軸 b: **既存 ADR との衝突を引いた**

**「改竄」「書いた時点の記録」の 2 語で全追跡ファイルを走査した**（`docs/specs/` / `feedback/` は確定済み記録なので除外）。

| ADR | 内容 | 本件との関係 |
| --- | --- | --- |
| **[IADR-0166](../adr/IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 2** | `docs/specs/` について「**本文への注記追加 → 不可**」 | **本件の線と一致する** |
| **[IADR-0187](../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md) 決定 2 の補足** | `feedback/` 11 件の**本文へ追記した**のを「射程外だから可」と論じた | **★ 衝突する。IADR-0166 を引いていない** |
| [IADR-0185](../adr/IADR-0185_feedback-status-vocabulary.md) 決定 2 の補足 | 同じ読み（A）を本リポの理屈で組んだ | 結論は一致 |

### 軸 c: **`feedback/` は「凍結された写し」か —— 実測して否定した**

**#721 より前（develop `7a9e5e9`）の本リポ版と、planning 側の同名ファイルを突き合わせた。**

| 鍵 | 本リポ | planning 側 |
| --- | --- | --- |
| `status` | `triaged` | **`awaiting-decision`** |
| `source_ref` | 実装側の文脈 | **計画側の文脈** |
| `updated` | 2026-08-08 | **2026-08-13** |

**→ 両側がそれぞれ自分の frontmatter を維持しており、バイト一致の鏡ではない。**
**「写しなのだから一致しているはず」という直感は成り立たない。**

### 軸 d: **予算**

| | 実測 |
| --- | ---: |
| 着手時の余白（#730 の別紙化後） | **1,334B** |
| 下限（#730 が置いたラチェット） | **1,000B** |
| **使える幅** | **334B** |

## 判断

### 判断 1: **読み A を採り、境界を「本文 / frontmatter の状態欄」で切る**

**記録の種類（`docs/specs/` か `feedback/` か）で規則を割らない。**
**割ると、次に新しい記録の種類が増えたときにまた迷う。**

### ★ 判断 2: **[IADR-0187](../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md) との衝突を明記する。是正は分離する**

**本判断の下では #721 が足した 11 件の追記は不可に当たる。**
**しかし本 PR では撤去しない。**

| | 理由 |
| --- | --- |
| 1 | **規則を決めることと、過去の記録を是正することは別の作業**（#713 が定めた扱いと同じ） |
| 2 | **マージ済みの判断を覆す変更**であり、**独立した承認判断に載せるべき** |
| 3 | **情報は失われない**（git 履歴・[IADR-0187](../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md)・作業仕様書に残る） |

### 判断 3: **入口への加筆は 2 行に収める**

**下限 1,000B を割らないため、詳細は [IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) が持つ。**
**入口に置くのは「何が対象で、何が対象外か」だけ。**

> **★ 最初の草案は下限を 101B 割った。** **#730 が置いた下限テストが実際に効いた**（人手の見積りでは気づかない）。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準（#717） | 確かめ方 |
| --- | --- | --- |
| 1 | 射程が一意に読める | **回帰テストで「本文が対象 / 状態欄は対象外」を固定** |
| 2 | `status` の更新主体と矛盾しない | **キットの表を根拠に据えた**（軸 a）。[IADR-0185](../adr/IADR-0185_feedback-status-vocabulary.md) 決定 1 の遷移とも整合 |
| 3 | 予算内（**下限も**）に収まる | **#730 の下限テストが緑** |
| 4 | 変異試験で検出を確認 | **Q1〜Q4** |

## 着地の実測

| | 値 |
| --- | --- |
| **必読合計** | **48,666B（余白 1,334B）→ 48,976B（余白 1,024B）** |
| 入口への加筆 | **＋310B**（2 行） |
| 衝突の記録 | **[IADR-0191](../adr/IADR-0191_rewrite-boundary-is-body-vs-frontmatter.md) 決定 3**（[IADR-0187](../adr/IADR-0187_status-vocabulary-follows-upstream-adjudication.md) 決定 2 の補足が誤りだったことを明記） |
| `scripts.test.js` | **503 件 全数 pass**（`planning` を pin `915981a` どおり populate） |
| 文書系検査 7 本 | **すべて exit=0** |
| 変異試験 | **4 変異すべてを検出**（Q1〜Q4） |

## 射程外

- **#721 が足した 11 件の追記の撤去** —— **判断 2。別 issue へ分離した**
- **キット追随の棚卸し** —— #713
- **`docs/specs/` の `status` の扱い** —— [IADR-0166](../adr/IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 2 が正本。**本 PR では触らない**

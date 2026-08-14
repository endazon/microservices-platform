---
title: IADR-0191 記録を書き換えてよい境界は「本文か frontmatter の状態欄か」で切る
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0166
  - IADR-0185
  - IADR-0187
  - IADR-0189
author: claude
created: 2026-08-14
updated: 2026-08-14
plan_refs:
  - "../../planning/tools/impl-handoff-kit/repo-template/feedback/README.md (status の「誰が書き換えるか」)"
---

# IADR-0191: 記録の書き換え境界（#717）

- 状態: Accepted
- 日付: 2026-08-14
- 決定者: claude（実装）

## 起点・関連

- **NFR**（文書統制。**当たる番号が無い＝場合 ②** なので無採番・環流しない。[IADR-0189](./IADR-0189_follow-upstream-adjudication-in-kit.md) 決定 1）
- 実装 issue: **#717**（出所: PR #715 の AI レビュー 🟡）
- 作業仕様書: [20260814_issue-717](../specs/20260814_issue-717_rewrite-boundary.md)

## 文脈 —— **同じ一文が 2 通りに読めていた**

> **この書式を適用する母集合は「live な権威文書とコード」に限る**——…`feedback/`（計画リポへ送った内容の写し）…は
> **書いた時点の記録**であり、後から注記を足すのは記録の改竄にあたるので**書き換えない**。

| 読み | 意味 |
| --- | --- |
| **A（限定）** | 「この書式」＝ `Superseded by` の後付け注記。**その書式の適用範囲**を限定しているだけ |
| **B（一般）** | `feedback/` などは**そもそも一切書き換えない** |

## ★★ 決定 1: **A を採る。B は上流と矛盾するので採れない**

**キットの [`feedback/README.md`](../../planning/tools/impl-handoff-kit/repo-template/feedback/README.md) は `status` の「誰が書き換えるか」を表で定めている。**

| 値 | 誰が書き換えるか |
| --- | --- |
| `awaiting-decision` | **計画側**（`/triage-feedback`） |
| `accepted` / `rejected` | **計画側**の裁定を実装側が転記する |

**上流が「書き換える主体」を定めている以上、「一切書き換えない」は成り立たない。**
**B を採ると、キットが配る鍵が最初から意味を持てない。**

> **★ これは本リポの都合ではなく、上流の記述から出る結論である。**
> **[IADR-0185](./IADR-0185_feedback-status-vocabulary.md) 決定 2 の補足 / [IADR-0187](./IADR-0187_status-vocabulary-follows-upstream-adjudication.md) 決定 2 の補足は同じ A を採ったが、
> 根拠を本リポの理屈だけで組んでいた。** **キットを引いていれば一意に決まった**（#728 と同じ型）。

## ★★ 決定 2: **境界は「本文」か「frontmatter の状態欄」かで切る**

| 対象 | 可否 | 理由 |
| --- | --- | --- |
| **frontmatter の状態欄**（`status` / `dispatched:` / `planning_issue:` / `updated:`） | **可** | **キットが更新主体を定めている**（決定 1）。遷移が前提の欄である |
| **本文**（**日付つき追記ブロックを含む**） | **不可** | **本文は「送った内容」そのもの**である |

**[IADR-0166](./IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 2 が `docs/specs/` について既に同じ線を引いている**
（「本文への注記追加 → **不可**」）。**`feedback/` でも同じに扱う** —— **記録の種類で規則を割らない。**

> **★ [IADR-0166](./IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) の「語彙の是正は可 / 状態の進行は不可」は `docs/specs/` の `status` の話である。**
> **`feedback/` の `status` は上流が遷移を定めているので、状態の進行も可**である ——
> **同じ鍵名でも、誰が値を決めるかが違えば扱いは違う。**

## ★★ 決定 3: **本決定は [IADR-0187](./IADR-0187_status-vocabulary-follows-upstream-adjudication.md) 決定 2 の補足と衝突する。衝突を明記し、是正は分離する**

**#721（PR #726）は `feedback/` 11 件の本文へ `［2026-08-14 追記 / #721］` を足した。**
**[IADR-0187](./IADR-0187_status-vocabulary-follows-upstream-adjudication.md) 決定 2 の補足はそれを「規約の射程外だから可」と論じたが、[IADR-0166](./IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) 決定 2 を引いていない。**
**本決定 2 の下では、あの 11 件の追記は不可に当たる。**

**是正（追記の撤去）は本 ADR では行わない。**

| | 理由 |
| --- | --- |
| 1 | **規則を決めることと、過去の記録を是正することは別の作業**である（#713 が「個別の是正は別 issue」と定めた扱いと同じ） |
| 2 | **マージ済みの判断を覆す変更**であり、**独立した承認判断に載せるべき**である |
| 3 | **情報は失われない** —— 追記が述べた内容は git 履歴・[IADR-0187](./IADR-0187_status-vocabulary-follows-upstream-adjudication.md)・作業仕様書に残る |

> **★ 自分の直近の判断を否定する決定なので、都合のよい方へ倒していないか特に疑った。**
> **逆（追記も可）を採ると、`docs/specs/` と `feedback/` で本文の扱いが割れ、[IADR-0166](./IADR-0166_status-vocabulary-and-record-rewrite-boundary.md) との衝突が残る。**
> **割れを残すより、自分の過去の判断を訂正するほうが規約として一貫する。**

## 決定 4: **`feedback/` は「凍結された写し」ではない（実測）**

**B の読みを支える直感は「写しなのだから原本と一致しているはず」だが、実測は違った。**

`feedback/20260719_headlamp-k8s-management-ui.md` を planning 側の同名ファイルと突き合わせた
（**#721 より前の develop `7a9e5e9` 時点**）。

| 鍵 | 本リポ | planning 側 |
| --- | --- | --- |
| `status` | `triaged` | **`awaiting-decision`** |
| `source_ref` | 実装側の文脈 | **計画側の文脈** |
| `updated` | 2026-08-08 | **2026-08-13** |

**両側がそれぞれ自分の frontmatter を維持している。** **バイト一致の鏡ではない。**
**→ 決定 2 の「状態欄は可」は実態とも合う。**

## 結果

- 良い影響
  - **射程が一意に読める**（#717 の起点が解消）
  - **根拠が上流に接地した** —— 本リポの理屈だけで組んだ [IADR-0185](./IADR-0185_feedback-status-vocabulary.md) / [IADR-0187](./IADR-0187_status-vocabulary-follows-upstream-adjudication.md) の補足より強い
  - **`docs/specs/` と `feedback/` で本文の扱いが揃った**
- 悪い影響・トレードオフ
  - **[IADR-0187](./IADR-0187_status-vocabulary-follows-upstream-adjudication.md) 決定 2 の補足が誤りだったことになる。** 記録としては読みにくいが、**衝突を残すよりよい**
  - **11 件の追記が規約違反のまま残る**（是正まで）—— **決定 3 で別 issue へ分離した**
- フォローアップ
  - **#721 が足した 11 件の追記の撤去**（決定 3。別 issue）

## 検出しないこと（明示する）

- **本文が書き換えられたか** —— **機械検査は無い。** git 履歴を見るしかない
- **状態欄の値が正しいか** —— **計画側が決める。** 実装側は転記の正しさしか担保できない
- **planning 側の写しとの乖離** —— **決定 4 のとおり乖離は正常**であり、検出対象ではない

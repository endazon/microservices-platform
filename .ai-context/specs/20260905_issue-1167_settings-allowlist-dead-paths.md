---
title: 許可リストの 3 系統同期を、実在しないパスへの git -C 許可 10 エントリの撤去で回復する
type: spec
status: done
related_ids:
  - NFR
  - ADR-0048
  - IADR-0228
  - IADR-0331
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0048_document-hierarchy-and-planning-dependency.md (決定 2)
---

# 作業仕様書: 実在しないパスへの `git -C` 許可の撤去（#1167）

## 背景

`.claude/settings.json:202` の `"//"` 注記は、許可リストが **「本ファイル / `claude-coding.yml` / `claude-code-review.yml`」の 3 系統を手作業で同期する構造**であることと、
**`git -C` の許可は `.gitmodules` にあるパスごとに 5 サブコマンド（`log` / `show` / `diff` / `ls-tree` / `grep`）で列挙する**ことを自ら定めている。

#1141（PR #1148）は CI 側 2 系統から実在しない入れ子 planning への `git -C` 許可を撤去したが、
`.claude/settings.json` は同ファイル自身の `permissions.deny` が `Edit(./.claude/settings.json)` を持つため畳めず、
「人手で外していただきたい」という依頼だけが残って #1141 は close された。#1167 はその受け皿である。

本作業は利用者の明示許可を得て、`Edit` ツールではなく Bash（`node`）で当該エントリを撤去する。

## 🔴 母集合の訂正 —— #1167 の「5 エントリ」は狭い（実測は 10）

規則 9（「追随する文書」を記憶で挙げない。**誤りの側の文字列で全文書を走査してから挙げる**）に従い、
基点 `origin/develop` `3663b2ba`（`git rev-parse --is-shallow-repository` = `false`）で自分で引き直した。

**引いたのは「実在しないパスへの `git -C` 許可」であって「`src/ai-stock-trading/planning` という文字列」ではない。**
後者で引くと #1167 が挙げた 5 件しか出ない。

```console
$ git config -f .gitmodules --get-regexp '\.path$'
submodule.src/ai-stock-trading.path src/ai-stock-trading      ← 宣言されている submodule は 1 つだけ

$ grep -c 'Bash(git -C' .claude/settings.json
16                                                             ← うち 1 件は "//" 注記の本文（許可エントリではない）

$ grep -n 'Bash(git -C' .claude/settings.json      # 許可エントリは 15
  18-22  Bash(git -C planning …)                    ×5
  23-27  Bash(git -C src/ai-stock-trading …)        ×5
  28-32  Bash(git -C src/ai-stock-trading/planning …) ×5

$ for p in planning src/ai-stock-trading src/ai-stock-trading/planning; do test -e "$p" && echo "EXISTS $p" || echo "ABSENT $p"; done
ABSENT  planning
EXISTS  src/ai-stock-trading            ← submodule（未 populate だが .gitmodules が宣言している）
ABSENT  src/ai-stock-trading/planning
```

**陽性対照**（「無い」を宣言する前に走査器が生きていることを確かめた）:
同じ `test -e` の判定は `src/ai-stock-trading` に対して `EXISTS` を返し、
同じ `grep -n 'Bash(git -C'` は 16 行を返す。走査は掛かっている。

**撤去対象は 10 エントリである。**

| パス | エントリ | 実在 | `.gitmodules` の宣言 | 判定 |
| --- | --- | --- | --- | --- |
| `planning` | 5 | ❌ | ❌ | **撤去**（下記） |
| `src/ai-stock-trading` | 5 | ⭕ | ⭕ | **残す** |
| `src/ai-stock-trading/planning` | 5 | ❌ | ❌ | **撤去**（#1167 が挙げた 5 件） |

`git -C planning` を撤去する根拠は 3 つあり、いずれも本リポジトリの既存の記述である。

1. **本リポジトリは planning に依存しない**（`ADR-0048` 決定 2 / `IADR-0228`）。submodule は張らない。
2. `.gitmodules` は `planning` を宣言していない。settings.json 自身の注記が定める列挙規則
   （**`.gitmodules` にあるパスごと**に 5 サブコマンド）に照らして、この 5 件は規則の外にある。
3. `.github/workflows/claude-code-review.yml:279` が既に
   「**（`ADR-0048` 決定 2）ため `git -C planning` は使えない**」と明記している。
   **CI 側 2 系統は既に `src/ai-stock-trading` の 5 件だけを持っている。**

したがって撤去後の settings.json は `src/ai-stock-trading` の 5 件だけになり、
**3 系統の `git -C` 許可が初めて一致する。** #1167 が挙げた 5 件だけを外すと、
`planning` の 5 件が残って**同期は破れたままになる**（規則 10: 是正のたびに「この変更で新たに誤りになる自分の記述」を引き直す）。

### 除外理由（撤去しないもの）

- `src/ai-stock-trading` の 5 件 —— `.gitmodules` が宣言しており、CI 側 2 系統も同じ 5 件を持つ。
  **未 populate であることは撤去の理由にならない**（submodule は `git submodule update` で実体化する）。
- `"//"` 注記の本文に現れる `git -C` の記述 —— 許可エントリではなく規則の説明である。
  ただし列挙規則そのものは正しいので**書き換えない**。
- CI 側 2 系統 —— 既に正しい（#1141 / PR #1148 で撤去済み）。本 PR は触らない。

## 対象範囲

- 対象: `.claude/settings.json` の `permissions.allow` から 10 エントリを削除する
- 対象外: `.github/workflows/*.yml`（既に正しい）／`permissions.deny`／`hooks`／その他の allow エントリ

## 設計

`Edit` / `Write` ツールは `permissions.deny` の `Edit(./.claude/settings.json)` /
`Write(./.claude/settings.json)` に遮られるため、**利用者の明示許可のもとで Bash（`node`）から
当該 10 行だけを削除する。** JSON の再整形（キー順・インデント・その他の値）は行わない
——差分を「10 行の削除」だけに閉じ、レビューで全差分を読めるようにする。

## 受け入れ基準

- [x] `.claude/settings.json` の `permissions.allow` に、実在しないパスへの `git -C` 許可が 0 件である
- [x] `src/ai-stock-trading` の 5 エントリは残っている（過剰な撤去をしていない＝陰性対照）
- [x] 3 系統（settings.json / `claude-coding.yml` / `claude-code-review.yml`）の `git -C` 許可が一致する
- [x] 差分は 10 行の削除だけであり、他のキー・整形は変わっていない
- [x] `node -e "JSON.parse(...)"` で JSON として妥当である

## テスト方針

機械検査は置かない。**同型の事故が 2 回起きたら検査器を足す**という規約に対し、本件は 1 回目である
（#1141 は「撤去し忘れ」であって「実在しないパスの混入」ではない）。記録に留める。

判定は上の受け入れ基準を実測で確かめる。3 系統の一致は 3 ファイルの `git -C` 列挙を並べて突き合わせる。

## 計画書との差異

- 差異: なし

## 未決事項

- なし

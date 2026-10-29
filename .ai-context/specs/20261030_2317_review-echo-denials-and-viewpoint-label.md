---
title: 作業仕様書 — AI レビューのプロンプトで検査器に echo を繋ぐ形を禁じ、check-review-verdict の計画書の除外で観点の見出し「ADR 制約」を外す（#2317）
type: spec
status: done
related_ids: [NFR, IADR-0538, IADR-0643]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2317"
---

# 作業仕様書 — AI レビューの権限拒否と誤判定（#2317）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `e760d82e`）。

## 現状（実測。ジョブのログを読んだ）

1. **権限拒否で claude-review が赤になった。同型で 2 回起きた。**
   - MSP#2311: run 40919880268、job 123610149665
   - MSP#2316: run 40932225509、job 123653349692
   - どちらも `check-permission-denials` が `Bash(node scripts/<文書系の検査器 6 本>.js | echo)` を 6 件と表示した。
   - 表示は `labelOf` が `;` と `|` で分けたセグメントを ` | ` で並べたものなので、実際の形は `node scripts/X.js; echo "exit:$?"` の類である。
   - `echo` は `--allowedTools` にある（`Bash(echo:*)`）。それでも拒否された。
   - プロンプトは 3 段以上の連鎖（`… | head -20 | echo "exit:$?"`）だけを例に挙げており、2 段の `; echo "exit:$?"` を禁じていなかった。
2. **`check-review-verdict` が誤判定した**（MSP#2311 の初回。run 40915142285 の attempt 1、job 123593254757）。
   - 赤にした文は「- ADR 制約: 計画 ADR の原文は読めていない。」である。
   - これは計画書についての文で、検査器は計画書（`PLAN_DOC`）の文を除外している。
   - ところが除外の条件（計画書の語を除いた残りに `NON_GENERATED_TARGET` が無い）に、観点の見出し「ADR 制約」の `ADR` が当たり、除外が外れた。

## 方針

- (1) **プロンプトで禁じる（許可の面は広げない）**。`claude-code-review.yml` の prompt の 2 か所に書く。
  - 【検証の下限】の文書系の検査器の項に、「1 本ずつ単体で実行する。`; echo "exit:$?"` や `| echo …` を繋がない」と書く。
  - 誠実性の節に、「終了コードを echo で取り直さない（2 段でも拒否される）。単体で実行してツールの結果を読む。要るときは子プロセス形を単体で使う」と書く。
  - `--allowedTools` は変えない。(a) の「無害な後段を足す」は、`echo` が既に許可されていても拒否されたので効かない。権限拒否を赤にする検査（`check-permission-denials`）も緩めない。
  - prompt の固定の試験（`scripts.repo.test.js` の #1911 の節）に、禁止の文言を足す。消すと赤になる陰性の対照も足す。
- (2) **`check-review-verdict` の計画書の除外で、観点の見出しを残りの判定から外す**（`REVIEW_VIEWPOINT_LABEL`）。
  - 外すのは、出力フォーマットのレビュー観点の名前（トレーサビリティ / 受け入れ基準 / ADR 制約 / 品質 / AI 固有リスク）だけである。
  - 文の中の IADR・ファイル名・差分は、従来どおり数える。下限は緩めない。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n "echo \"exit"` で prompt を走査した。当たりは誠実性の節の 1 か所（3 段の例）だけで、2 段の形を禁じる文は無かった。
  - 実装用のワークフロー（`claude-coding.yml`）は対象外とした。理由は 2 つある。
    - 許可の一覧が違う。
    - 権限拒否の検査は、レビューのジョブにだけ掛かっている。
- **規則 10**: `PLAN_DOC` の除外を使っているのは `findNotReadAdmissions` の 1 か所だけで、そこを直した。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| prompt が echo を繋ぐ形を名指しで禁じる | `scripts.repo.test.js` の `claude-code-review.yml[#1911]`（語の固定と陰性の対照） | — |
| 「- ADR 制約: 計画 ADR の原文は読めていない。」を数えない（✅ の節・🔍 の節とも）。陽性の対照として、同じ見出しで IADR の差分の未精読を並べた文は数える | `check-review-verdict.js --self-test` の #2317 の試験 | 赤（見出しを外す前の式へ戻す変異で失敗。実測） |

## workflow の変更の影響

- 変えたのは `prompt:` の本文だけである。
- 次のものは変えていない。
  - `permissions:`、トリガー（`on:`。fork の PR の扱いを含む）、`secrets` の参照
  - `--allowedTools` / `--model`
  - ジョブの順序と検査のステップ

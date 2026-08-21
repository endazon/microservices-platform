---
title: 作業仕様書 — 本文を変えたのに frontmatter の updated: が古いままの文書を CI で止める（#649）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0116
  - IADR-0141
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - "./20260809_issue-544_sc10-operator-access.md"
---

# 作業仕様書: `updated:` の据え置きを止める検査器（#649）

## 起点

- **NFR**（文書の保守性・トレーサビリティ）。`CLAUDE.md`「**検査器・規約の追加は同型の事故が 2 回起きたら**」
- 事故 1: PR #648 レビュー 2 巡目 —— 内容を変えた文書 **9 件**の `updated:` が据え置き
- 事故 2: PR #648 レビュー 4 巡目 —— `docs/adr/IADR-0030_*` に追記したのに `updated:` が `2026-07-08` のまま
- 判定式の実測記録: `docs/specs/20260809_issue-544_sc10-operator-access.md` §8

**原因は 2 回とも同じ。**「本文を編集した」と「frontmatter を更新した」が**別操作**であり、
**前者だけでも既存の機械検査（`check-doc-links` / `check-cross-repo-refs` /
`check-plan-id-qualification` / `check-adr-numbering`）は全て通る。**

## 射程

**射程内**: `scripts/check-doc-updated.js` の新設と、`scripts/scripts.repo.test.js` への自己試験。

**射程外**（理由つき）:

| 除外するもの | 理由 |
| --- | --- |
| **`.github/workflows/` の新ジョブ** | **GitHub App 権限では編集できない**（`CLAUDE.md`）。**新ジョブは作らない**——が、**配線はできる**。下記「★ 着手時に前提が覆った」参照 |
| `created:` の検査 | 作成日は後から動かない。据え置きが問題になるのは `updated:` だけである |
| `planning/` 配下 | 別リポジトリ。本リポの CI の射程外 |
| **`docs/templates/` 配下** | 雛形の `updated:` は**穴**（`<YYYY-MM-DD>`）であり、**埋まっていないのが正しい**。本文を編集するたびに `invalid-date` で落ちると**検査器が邪魔者になって外される**（PR #652 レビュー 1 巡目が 17 件で実測。**入れる前に気づけなかった見落とし**である） |
| **#647**（宣言ロールと実装の突合） | **別資源**。#647 は「文書の主張と実装の一致」、本件は「文書を触ったのに更新日が動いていない」を見る（[IADR-0139](../adr/IADR-0139_domain-bundled-contract-prs.md) の判定単位は資源）。**束ねない** |

## 判定式（**3 案を実測してから決めた**）

PR #648 の変更文書 12 件へ当てた実測:

| 案 | 判定 | 挙げた件数 | 評価 |
| --- | --- | --- | --- |
| A | `updated:` が base から変わっていない | **2** | **誤検知あり**。`docs/api/BFF_bff-surface.md` は develop 側が既に同日付で、**同日中の再編集は据え置きが正しい** |
| B | `updated:` < その文書を最後に変えたコミットの日付 | **10** | **誤検知だらけ**。コミットが UTC の日付境界を跨ぐと全件落ちる（#648 は `2026-08-10` に着地した） |
| **C** | **`updated:` < PR の最初のコミットの日付** | **1** | **`IADR-0030` だけを正しく挙げる** |

**採るのは案 C。**

> base（`git merge-base`）以降で **frontmatter 以外の行が変わった** `docs/**/*.md` のうち、
> **`updated:` が「base 以降の最初のコミットの日付」より古いもの**を fail させる。

**案 A・B を実測せずに入れていたら、誤検知で早々に無効化されていた。**
検査器は「思いついた式」ではなく**手元の実例へ当てて誤検知を数えてから**入れる。

## 実装方針

### 本文が変わったかの判定は **diff のハンク解析ではなく本文比較**で行う

base 版と HEAD 版から**それぞれ frontmatter を除いた本文**を取り出し、**異なれば本文変更**とする。
ハンクの行番号と frontmatter の範囲を突き合わせる方式は、**frontmatter の途中に本文が挟まる異常な
ファイルで壊れる**うえ、`git diff` の出力形式に依存する。本文比較なら形式に依存しない。

### 対象と除外

| 状態 | 扱い |
| --- | --- |
| 新規追加 | **検査する**（base 版が無いので本文は「全部が変更」とみなす） |
| 削除 | 対象外 |
| frontmatter が無い / `updated:` が無い | **対象外**（`README.md` 等。**notice として件数だけ出す**） |
| `updated:` が日付形式でない | **violation**（`invalid-date`）。据え置きと同じく静かに腐るため |
| frontmatter 以外が変わっていない | 対象外（`updated:` 自体の修正・誤字直しのみ等） |

### base の決め方

`git merge-base <base-ref> HEAD`。`base-ref` は `--base` 引数 → 環境変数 `GITHUB_BASE_REF` →
`origin/develop` の順で解決する。**shallow clone では merge-base が引けない**ため、
その場合は**検査を飛ばして notice を出す**（黙って通さない・黙って落とさない）。

## テスト（受け入れ基準の写像）

`scripts/scripts.repo.test.js` に自己試験を置く（`selfTest` を export する既存の検査器と同じ形）。

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 本文を変えて `updated:` 据え置き → violation | `stale` |
| 2 | 本文を変えて `updated:` を進めた → OK | 正常系 |
| 3 | **同日中の再編集（base と同じ日付）は OK** | **案 A の誤検知を固定する回帰** |
| 4 | frontmatter だけの変更は対象外 | `updated:` 自体の修正で落ちない |
| 5 | `updated:` が無い文書は対象外 | notice に出るが violation ではない |
| 6 | 日付形式でない `updated:` は violation | `invalid-date` |
| 7 | 新規追加も検査する | 追加した文書の日付が古ければ落ちる |

## ★ 着手時に前提が覆った —— CI 配線は本 PR でできる

**起票時は「`.github/workflows/` を編集できないので配線は別途」と書いた。これは誤りだった。**

`scripts/scripts.repo.test.js` を読むと、**同じ問題を repo が既に解いていた**:

> **ここが `check-cross-repo-refs.js` の CI 呼び出し口である。** `.github/workflows/` は
> GitHub App 権限で編集できないため、新しい検査器を足しても新ジョブからは呼べない。
> `ci.yml` の `scripts-tests` ジョブ（`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`）が
> 本 companion を読み込むので、そこから子プロセスで検査器を起動する。

`check-plan-id-qualification` も同じ相乗りをしている（[IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md) 決定 2）。
**本検査器も同じ口へ載せた**ので、**ワークフローを触らずに CI で走る**。

**教訓**: 「権限が無いからできない」と書く前に、**同じ制約下で先に解いた例が無いかを見る**。
本件では**規約の書いてあるファイル自身に答えが書いてあった**。#646 の「隣のファイルの実装ではなく
ADR 本文を開く」と同じ型の見落としである。

## 申し送り

- **`fetch-depth: 0` は既に満たされている** —— `scripts-tests` ジョブの `actions/checkout` は
  指定済みである（merge-base を引くのに要る）。**新しい要求は無い。**
- 本検査器は **#647 とは別物**である（射程の表を参照）。
- **`updated:` を持たない文書**は notice に留めた。「日付を持て」という規約は別の話であり、
  本検査器の射程ではない（`docs/README.md` 等が該当する）。

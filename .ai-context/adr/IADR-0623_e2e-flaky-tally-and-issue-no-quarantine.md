---
title: IADR-0623 L1 E2E の flaky は JSON レポートで数えて警告し、develop で 3 回連続なら test ごとに起票する。隔離で緑にしない
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0090, IADR-0569, IADR-0621, IADR-0568]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0090（門は依存ごと。全 skip を緑にしない）
related_specs:
  - ../specs/20261024_2037_e2e-flaky-tally.md
---

# IADR-0623: E2E の flaky の集計と起票（#2037）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2037。親 #2013 の E-73。範囲はオーケストレーターの指示で「集計・可視化・再発時の起票」に限った）

## 起点・関連

- 起点 issue: #2037（親 #2013）。関連する計画書 ID: NFR（無採番）。計画 ADR: ADR-0090（全 skip を緑にしない）。
- 関連する実装判断: [IADR-0621](./IADR-0621_l1-e2e-shards-and-aggregate-check.md)（L1 のシャードと集約 `e2e`）・
  [IADR-0568](./IADR-0568_age-apkindex-daily-watch.md)（専用 issue の起票の型）・[IADR-0569](./IADR-0569_e2e-layers-and-live-opt-in.md)（実行層）。
- 作業仕様書: [20261024_2037_e2e-flaky-tally](../specs/20261024_2037_e2e-flaky-tally.md)。

## コンテキストと課題

L1 は `retries: 1` で、再試行で通った test（flaky）は check を緑にする。flaky の件数はどこにも数えられず、同じ test が揺れ続けても気付けない。
基準書 §10 は「1 件でも flaky なら警告、同じ test が 3 回連続なら起票」を目標にし、live については「`test.fixme` で隔離し 2 週間で解消」と書いていた。

## 検討した選択肢

| 論点 | 案 | 採否 |
| --- | --- | --- |
| 入力 | A. html レポートの埋め込みデータを解く | 採らない。Playwright の内部形式に依存する |
| | **B. 同じ実行の JSON レポート（`merge-reports` に json を足す）** | **採る**。同じ結果・公開された形 |
| 連続の履歴 | A. リポジトリにコミットする／キャッシュに置く | 採らない。コミットは develop を汚す。キャッシュは消える時期を選べない |
| | **B. 各実行の要約を成果物（14 日）に残し、直前の develop の実行から読む** | **採る** |
| 起票の場所 | A. `e2e` ジョブで起票する | 採らない。PR でも走るジョブに `issues: write` を持たせる |
| | **B. develop への push だけで走る別ジョブ `e2e-flaky-issue`** | **採る** |
| 再発時の扱い | A. 隔離（`test.fixme` / skip / 再試行の引き上げ）して緑にし、期限内に直す | **採らない**（下の決定 4） |
| | **B. 起票して直す。test は流し続ける** | **採る** |

## 決定

1. `frontend.yml` の `e2e` ジョブで `merge-reports` に json を足し、`scripts/check-e2e-flaky.js --report` が flaky を数える。
   1 件でも flaky なら test ごとに `::warning::`、step summary に件数の表。**PR は落とさない。** 要約を成果物 `e2e-flaky-summary`（14 日）に残す。
   レポートが読めない・test 0 件・未知の status は exit 1。
2. `e2e-flaky-issue` ジョブ（develop への push で `e2e` が success のときだけ。`actions: read`・`issues: write`）が直前 2 回の要約を読み、
   **3 回連続で flaky の test ごとに専用 issue**（ラベル `e2e-flaky`・本文のマーカーで引く）を起票し、在ればコメントを足す。自動ではクローズしない。
3. 直前の develop の実行を新しい順に 2 つ見て、**要約の無い実行に当たったらそこで履歴を打ち切る**（飛ばして古い実行と繋がない。打ち切りは検査器の `historyUntilGap` が目録 `manifest.tsv` の順に行う）。打ち切った結果、履歴が 2 件に満たなければ判定しない。
4. **flaky を隔離して緑にしない。** `test.skip` / `test.fixme` / 再試行回数の引き上げ / 実行から外す、のいずれも再発時の扱いにしない。
   基準書 §10 の live の隔離の規約（2 nightly 連続で `test.fixme`・2 週間で解消）は改めた。

## 理由

- 隔離は検知を止めるだけで、壊れた test と壊れた画面を区別できなくする（ADR-0090 の「全 skip を緑にしない」と同じ側）。
  起票は検知を残したまま、直す人に場所と実行を渡す。
- 起票を develop の連続に限ると、PR の一度きりの揺れで issue が増えない。

## 影響

- flaky の件数が PR の画面（注釈・step summary）に出る。check の色は変わらない。
- develop で同じ test が揺れ続けると issue が立つ。直すまで実行ごとにコメントが足される。
- live 層を配備する変更は、同じ検査器で live の flaky を数える（隔離の規約は持ち込まない）。

## 残余リスク

- 成果物の保存が 14 日なので、develop の Frontend CI が 14 日に 3 回未満しか走らなければ連続は判定されない（フロントの変更が少ない時期）。
- 要約の無い実行で履歴を打ち切るため、e2e をまとめられなかった実行（要約が無い）を挟むと連続は途切れる（起票が遅れる側へ倒している）。e2e が赤でも `merge-reports` と集計が通った実行は要約を残すので、連続の判定に入る。

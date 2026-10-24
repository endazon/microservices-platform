---
title: IADR-0621 L1 E2E を 2 シャードで流し、blob を merge-reports でまとめる集約ジョブに check 名 `e2e` を残す
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0090, IADR-0569, IADR-0232]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0090（門は依存ごと。全 skip を緑にしない）
related_specs:
  - ../specs/20261024_2036_e2e-shards-and-artifacts.md
---

# IADR-0621: L1 E2E のシャードと集約 check（#2036）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2036。親 #2013 の E-71）

## 起点・関連

- 起点 issue: #2036（親 #2013）。関連する計画書 ID: NFR（無採番）。
- 関連する実装判断: [IADR-0569](./IADR-0569_e2e-layers-and-live-opt-in.md)（E2E の実行層。L1 の project 名 `smoke`）・
  [IADR-0232](./IADR-0232_ci-pr-latency-reduction.md)（CI の並行と所要）。
- 作業仕様書: [20261024_2036_e2e-shards-and-artifacts](../specs/20261024_2036_e2e-shards-and-artifacts.md)。
- 人が読む基準: `docs/tests/E2E_STANDARDS.md` §9〜§12。

## コンテキストと課題

`frontend.yml` の `e2e` ジョブは 1 本で L1 の全 spec（185 本）を流し、成果物を残していなかった。基準書は
2 シャード・`fullyParallel`・blob → `merge-reports`・成果物の常時アップロード（14 日）・`timeout-minutes` を目標にしている。
シャードを matrix にすると check 名が `e2e (1)` のように脚ごとに変わる。check 名 `e2e` を文書と検査器が名指ししている。

## 検討した選択肢

| 論点 | 案 | 採否 |
| --- | --- | --- |
| check 名 | A. `e2e` を matrix にする（脚の名前が `e2e (1)` / `e2e (2)` になる） | 採らない。`e2e` という check が消える。文書の名指しとずれる |
| | **B. 脚を `e2e-shard` にし、`e2e` を集約ジョブにする** | **採る**。check 名が変わらない |
| 集約の判定 | A. `needs` の既定（シャードが赤なら集約はスキップ） | 採らない。レポートをまとめられない。スキップは必須チェック上「合格」扱いになり得る |
| | **B. `if: !cancelled()` で走らせ、最後に `needs.e2e-shard.result == success` を確かめる** | **採る** |
| 注釈（github reporter） | A. シャードで出す | 採らない。まとめた側でも出すと二重になる |
| | **B. まとめた側だけで出す（シャードは blob ＋ list）** | **採る** |

## 決定

1. **L1 は 2 シャード**（`e2e-shard`、`strategy.fail-fast: false`）。`playwright.config.ts` に `fullyParallel: true`。
2. **check 名 `e2e` は集約ジョブが持つ。** 集約は blob をまとめて html ＋ github の注釈を出し、シャードの成否を 1 つに集める。
   `if: !cancelled()` で走り、最後のステップでシャードの結果が `success` でなければ落とす。
3. **成果物**: シャードの blob（`e2e-blob-report-<N>`）とまとめた html（`e2e-html-report`）を `if: !cancelled()` で 14 日残す。trace は両方に含まれる。
4. **時間の上限**: 各ジョブ `timeout-minutes: 20`。`globalTimeout` は CI で 15 分（ジョブの上限より先に切れ、止まった test がレポートに残る）。
5. **シャードの数を変えるとき**（基準書 §10 の 1 シャード 10 分を超えたとき）は matrix の値と `--shard=N/<総数>` と表示名の 3 か所を同時に変える。

## 理由

- check 名を変えずに脚を増やせる。集約の判定を明示することで、「シャードが赤でも集約が緑」の偽の緑を作らない（ADR-0090 の「全 skip を緑にしない」と同じ側）。
- `fullyParallel` で test 単位に配るため、ファイルごとの本数の偏りがシャードの偏りにならない（手元の実測: 93 本 / 92 本）。

## 影響

- `docs/ai-workflow.md` の必須チェック表は変わらない（`e2e` は載っていない。`frontend.yml` が `paths:` を持つため）。
- **IADR-0569 決定 1 の表の「PR の必須チェック: はい（現状の `frontend.yml` の `e2e` ジョブ）」は誤りである。** L1 の check は
  ブランチ保護の必須チェックではない。本 IADR で訂正を記録し、基準書 §2.1・§11 を同じく訂正した（IADR-0569 の本文は凍結記録なので書き換えない）。
- 集約ジョブも `pnpm install` を行うため、PR あたりの所要（ランナー時間）が 1 ジョブ分増える。

## 残余リスク

- シャードが blob を残さずに落ちた場合（install の失敗など）、集約の `merge-reports` も落ちる。原因はシャードの側にある（集約のログに `e2e-shard` の結果を出す）。

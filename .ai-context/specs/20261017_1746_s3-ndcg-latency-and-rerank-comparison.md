---
title: nDCG の計測器にクエリごとの所要時間と再順位付けの有効・無効の 2 構成の比較を足し、段 S3 の計測手順書を置く（#1746 段 S3 の準備）
type: spec
status: done
related_ids: [FR-03, FR-04, SC-02, NFR-01, NFR-02, NFR-29, ADR-0127, ADR-0129, ADR-0138, ADR-0076, IADR-0422, IADR-0498, IADR-0559]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0127_high-confidentiality-lexical-only-and-claude-rerank.md（決定 3・6）
  - planning:projects/microservices-platform/07_adr/ADR-0129_rerank-latency-target-nfr29-and-staged-rollout.md（決定 1・3・フォローアップ 1、2026-10-10 の補完の注記）
issue: "#1746"
---

# 仕様書: nDCG の計測器の所要時間と再順位付けの 2 構成比較、段 S3 の計測手順書（#1746）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。計画は project-planning の `main`（`55e464a`）を読んだ。実装の基点は MSP `origin/develop` `01c3dcc2`。
> 新しい IADR は起こさない（下の「判断」）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-03（横断検索）・FR-04（RAG 回答）
- 画面（SC）: SC-02（検索結果一覧の並び）
- 非機能: NFR-01（検索 p95 1.5 秒）・NFR-02（RAG 初回応答 p95 5 秒）・NFR-29（再順位付けが有効なときの目標。段 S3 の実測まで pending）
- 関連 ADR: ADR-0127（決定 3: Claude による再順位付け／決定 6: 公開可能な標本で「全文のみ＋Claude の再順位付け」と「ハイブリッド（voyage）」の nDCG@10 の差を記録。閾値なし）・
  ADR-0129（決定 1: NFR-29 は段 S3 の実測を見て定める／決定 3: 既定無効から段階導入／2026-10-10 の補完: 計測は `claude-haiku-5-5` で行う）
- 関連 IADR: IADR-0422（nDCG のハーネス）・IADR-0498（再順位付けの段。掛ける検索・候補の幅・計器）・IADR-0559（検索 p95 の警報と `search_rerank_enabled`）

## 目的・背景

段 S3（ADR-0129 フォローアップ 1）は、再順位付けを**有効・無効の 2 構成**で、nDCG@10 と遅延（検索 p95・RAG 初回応答 p95）を測る。
その値を見て計画が NFR-29 を定める。

今の道具の不足:

1. `scripts/measure-search-ndcg.js` は順位しか記録しない。**同じクエリの所要時間が無い**ので、精度と遅延を同じ標本で並べられない。
2. 再順位付けの有無は検索サービスの構成（`Rerank__Enabled`）で決まり、要求ごとには選べない。そのため 2 構成の比較は「構成を変えて 2 回収集し、並べる」になる。
   しかし収集した結果に**どちらの構成で採ったか**が残らない（名札 `NDCG_LABEL` の文字列でしか分からない）。モードごとの差を自動で並べる手段も無い。
3. 段 S3 の計測手順（前提・回数・費用の見積もり・記録先）がどこにも書かれていない。

## 対象範囲

- 対象:
  - `scripts/measure-search-ndcg.js`: 収集でクエリごとの所要時間（ミリ秒）と縮退の理由（`degradedReasons`）を記録する。run に再順位付けの構成（`rerank: true|false`。`NDCG_RERANK=on|off` か `--rerank on|off`）を記録する。
    同じクエリを繰り返す回数（`NDCG_REPEAT` / `--repeat`。既定 1）を足す。集計に所要時間の統計（件数・平均・p50・p95・最大）と、2 構成の比較（モードごとの nDCG の差・p95 の差、ADR-0127 決定 6 の組）を足す。
  - `scripts/scripts.repo.test.js`: 上の試験（既存の nDCG の試験の隣。本リポジトリ固有の試験は `scripts.test.js` ではなくこのファイルに置く）。
  - `docs/operations/search-rerank-s3-measurement-runbook.md`（新規）: 段 S3 の計測手順書。`docs/operations/operations.md` と `perf/ndcg/README.md` から張る。
- 対象外:
  - 計測の実施（稼働 PoC と Voyage の鍵・qrels が要る）・NFR-29 の値の決定（計画の裁定）・段 S5（有効化）。
  - `perf/k6/*.js` の変更（手順書は今のシナリオのまま使う。負荷の段は手順書で読み方だけを書く）。
  - 再順位付けの構成を応答から確かめる手段（応答には載っていない。手順書で計器 `search_rerank_enabled`・`search_rerank_total` を見る）。

## 後方互換（受け入れ基準 4 の中身）

- 既存の dump（`latencyMs`・`rerank`・`degraded` を持たない）を `--input` で読むと、**テキスト出力は 1 文字も変わらない**。JSON 出力は項目が増えるだけで、既存の項目の値は変わらない。
- 既存の引数・環境変数の意味は変えない。`NDCG_RERANK` を与えなければ run に `rerank` を書かない。
- 集計は純関数のまま（同じ入力なら同じ出力）。所要時間は収集の時にだけ測り、dump に保存する（時刻は書かない）。

## 判断

- **再順位付けの構成は、運用者が名乗る**（`NDCG_RERANK`）。応答に段の有無が無く、検索サービスの構成を外から読む口は計器（`search_rerank_enabled`）だけである。計測器から Prometheus を読む形は、
  依存（URL・認証）が増え、BFF のセッションで測る今の作りと合わない。名乗りの取り違えは手順書で計器を見て防ぐ（収集の前後で `search_rerank_enabled` と `search_rerank_total{search_rerank_result="applied"}` の増え方を確かめる）。
- **`rerank-failed` の縮退を記録する。** 有効の構成でも期限切れ等で元の順に戻ったクエリは、再順位付けの nDCG を測れていない。集計はその件数と ID を出す（平均からは外さない。実際に利用者が見る並びであるため）。
- **百分位は最近順位法**（並べて `ceil(p/100 × n)` 番目）。補間はしない。標本が 20 未満なら p95 は最大値と同じになる。nDCG の計測器の所要時間は少ない標本の参考値であり、
  **NFR-01・NFR-02・NFR-29 の p95 の正は k6**（手順書に書く）。
- 新しい IADR は起こさない。計測器の機能の追加であり、計画の決定（ADR-0129 決定 1・ADR-0127 決定 6）の手段である。判断は本仕様書に残す。

## 受け入れ基準

1. 収集は、クエリ × モードごとに所要時間（`latencyMs`。繰り返し分の配列）を記録し、集計は run ごとの件数・平均・p50・p95・最大を出す（試験: 純関数の既知値）。
2. run に `rerank` を記録でき、集計はモードごとに有効と無効を組にして nDCG の差と p95 の差を出す。キーワード＋有効とハイブリッド＋無効の組（ADR-0127 決定 6）も出す（試験）。
3. 縮退の理由 `rerank-failed` のクエリを件数と ID で報告する（試験）。
4. 既存の dump の集計（テキスト）は変わらない（試験: 変更前の出力を固定値として突き合わせる）。
5. `NDCG_RERANK` の値域外・`NDCG_REPEAT` の値域外は収集の前に落ちる（試験）。
6. 手順書が、2 構成での nDCG@10・k6 の検索 p95・RAG 初回応答 p95 の測り方、前提（Voyage の鍵・qrels の標本・Claude の課金回数）、PoC で何を何回測るか、記録先を持つ。trace ブロック規約に適う（`check-trace-blocks`）。

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 文書系検査（`check-trace-blocks`・`check-doc-links`・`check-doc-updated`・`check-trace-followthrough`・`gen-knowledge-graph --check` ほか）
- 変異: 百分位の式・組の取り方を崩すと試験が赤になること

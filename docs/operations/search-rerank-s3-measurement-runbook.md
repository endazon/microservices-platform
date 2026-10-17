---
title: 運用 Runbook — 再順位付けの有効・無効の 2 構成で検索精度（nDCG@10）と遅延（検索 p95・RAG 初回応答 p95）を測る
type: runbook
status: draft
author: claude
created: 2026-10-17
updated: 2026-10-17
---
<!-- trace:
ids: [FR-03, FR-04, SC-02, NFR-01, NFR-02, NFR-29]
adrs: [ADR-0127, ADR-0129, ADR-0138, ADR-0076, ADR-0140]
iadrs: [IADR-0422, IADR-0498, IADR-0559, IADR-0504, IADR-0552]
specs: [20261017_1746_s3-ndcg-latency-and-rerank-comparison]
issues: [#1746]
-->

# 運用 Runbook: 再順位付けの 2 構成での計測

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。** 起点は #1746 の段 S3。
> 検索サービスの Claude による再順位付け（`Rerank__Enabled`）を**無効（既定）と有効の 2 構成**に切り替え、
> 同じ標本で **検索精度（nDCG@10）** と **遅延（検索 p95・RAG 初回応答 p95）** を測って記録する。
> 計画は、ここで記録した値を見て「再順位付けが有効なときの遅延の目標」を定める（今は未定）。**本書は合否を判定しない。**

## 0. 何を測るか

| # | 指標 | 構成 | 道具 | 読む値 |
| --- | --- | --- | --- | --- |
| M1 | nDCG@10（キーワード・意味・ハイブリッド） | 無効 | `scripts/measure-search-ndcg.js` | モードごとの平均・クエリごとの所要時間 |
| M2 | nDCG@10（キーワード・ハイブリッド） | 有効 | 同上 | 同上・`rerank-failed` の件数 |
| M3 | 検索 p95 | 無効・有効 | `perf/k6/search-load.js` | `http_req_duration{scenario:search}` の p95 |
| M4 | RAG 初回応答 p95 | 無効・有効 | `perf/k6/rag-load.js` | `http_req_duration{scenario:rag}` の p95（応答全体での近似）。補助に Prometheus の初回トークンの p95 |

- **計画が求める組**は 2 つある。
  - **精度の差**: 「全文のみ＋再順位付け」（M2 のキーワード）と「ハイブリッド（voyage）」（M1 のハイブリッド）。計測器は `hybrid・無効 → keyword・有効` の行として出す。
  - **有効化の遅延**: 同じモードの無効と有効（M1 と M2、M3・M4 の 2 構成）。
- **閾値は置かない。** 差を記録する（精度の差は受け入れたトレードオフの大きさとして残る）。
- 意味検索（`semantic`）には再順位付けが掛からない。並び替え（`updated`）・合成監視の検索にも掛からない。そのため M2 は意味検索を測らない。
- 🔴 **p95 の正は k6（M3・M4）である。** 計測器の所要時間は qrels のクエリ数 × 繰り返し回数の少ない標本の参考値である（最近順位法。標本が 20 未満なら p95 は最大と同じ）。

## 1. 前提

| 前提 | 確かめ方 | 無いとどうなるか |
| --- | --- | --- |
| **稼働 PoC**（経路B のローカル k8s）に、本番相当の文書が索引されている | `kubectl -n platform-infra run qdrant-check --rm -i --restart=Never --image=curlimages/curl -- sh -c 'curl -s http://qdrant:6333/collections/knowledge_chunks_voyage_3_5'` の `points_count` が 0 でない | 意味・ハイブリッドの系統が空になり、精度の差が測れない |
| **Voyage の鍵**（検索クエリの埋め込み）が LLM ゲートウェイに入っている | [Voyage の鍵の Runbook](voyage-embedding-key-runbook.md) の手順 4（長さで測る） | ハイブリッドがキーワードだけで返り、応答に `embed-failed` が付く（計測器の dump の `degraded` に出る）。M1 のハイブリッドが「ハイブリッド（voyage）」にならない |
| **qrels の標本**（公開してよい文書だけで作った正解ラベル） | `perf/ndcg/qrels.json`（`qrels.example.json` を複製して埋める。`.gitignore` 済み）。計測器が `正解ラベルが 1 件も無いクエリ` を 0 件と出すこと | 雛形のままだと全クエリが平均から除外される |
| **Claude の経路**（用途 `rerank`・`rag-answer` がともに `claude-haiku-5-5`） | 運用仕様書 §LLM ゲートウェイ の割当。ゲートウェイの用途 `rerank` の effort（`Llm__PurposeEffort__rerank`。既定 `low`）を記録する | 旧モデルで測った遅延は使えない（計画の補完の注記） |
| **BFF のセッション Cookie**（利用者として測る。ABAC が効いた検索になる） | ブラウザで BFF にログインし、`__Host-msp-session` の値を写す。**値は資格情報である** | BFF は利用者のトークンを Bearer で受けない（401） |
| ツール | `kubectl`・`helm`・`node`（22）・`k6`・`curl` | — |

- **qrels の大きさ**: 30 クエリ以上を目安にする（クエリ間のばらつきで平均が動く。少ないと差の符号が標本で入れ替わる）。関連度は 0〜3 で付ける（書式は `perf/ndcg/README.md`）。
  🔴 **高機密の文書を qrels に入れない。** 標本は公開してよい文書だけで作る（高機密の文書は voyage で埋められず、同じ文書での比較ができない）。
- **合成監視**: 合成監視の検索・回答は再順位付けを呼ばず費用にも入らない。k6 の要求は利用者の要求として数えられる（実利用の費用に入る）。

### 1.1 Claude の課金回数の見積もり

再順位付けは**有効のときに限り、関連度順のキーワード・ハイブリッド検索 1 回につき最大 1 回**、Claude を呼ぶ（再試行は無い）。
RAG 回答は、検索の候補で 1 回（有効のとき）、回答の生成で 1 回呼ぶ。

| 計測 | 再順位付けの呼び出し | 回答生成の呼び出し |
| --- | --- | --- |
| M1（無効） | 0 | 0 |
| M2（有効。2 モード × Q クエリ × 繰り返し R） | **2 × Q × R**（Q=30・R=3 で 180） | 0 |
| M3 無効 | 0 | 0 |
| M3 有効 | **要求の数**（シナリオの VU 秒は 2,640。1 回 1 秒の思考時間があるので上限 2,640。応答が遅いほど減る） | 0 |
| M4 無効 | 0 | **要求の数**（VU 秒 1,140・思考時間 2 秒で上限 570） |
| M4 有効 | **要求の数**（上限 570） | **要求の数**（上限 570） |

- 再順位付け 1 回の目安は、平均 約 $0.0006〜$0.001・最大 約 $0.0014（運用仕様書 §再順位付け の見積もり）。上の上限を全部使っても、再順位付けは 約 $5 に収まる。
- 回答生成の費用は見積もらない。**実績を計器で読む**（下の手順 6）。
- 要求の実数は k6 の要約の `http_reqs` で読む。計測の窓の `llm_completion_total{llm_purpose="rerank"}` の増分と突き合わせる（ずれたら、窓に別の利用が混ざっている）。

## 2. 準備

```bash
NS=microservices-platform
W=$(mktemp -d)                      # 作業ディレクトリ。dump・k6 の要約・保存した値を置く（コミットしない）
helm get values msp -n "$NS" -o yaml > "$W/current-values.yaml"
grep -A1 '^searchRerank:' "$W/current-values.yaml" || echo 'searchRerank は既定（enabled: false）'
kubectl -n platform-infra port-forward svc/prometheus 19090:9090 >/dev/null 2>&1 &
curl -s http://127.0.0.1:19090/-/ready                         # → Prometheus Server is Ready.
```

**構成を名乗りと突き合わせる**（`NDCG_RERANK` は検索サービスの構成を**名乗るだけ**で、構成を変えない。取り違えると 2 構成の差の符号が反転する）:

```bash
curl -s --get http://127.0.0.1:19090/api/v1/query \
  --data-urlencode 'query=max(search_rerank_enabled{job="microservices-platform.retrieval-service"})' | jq -r '.data.result[0].value[1]'
# 0 = 無効・1 = 有効。系列が無い（空）なら、検索サービスが計器を出していない。名乗りを確かめられないので先へ進まない
```

## 3. 無効の構成で測る（M1・M3・M4 の無効）

手順 2 の値が `0` であることを確かめてから行う。

```bash
export NDCG_BASE_URL=https://<エッジのホスト> NDCG_SESSION_COOKIE=<セッション Cookie の値>
NDCG_RERANK=off NDCG_LABEL=rerank-off NDCG_MODES=keyword,semantic,hybrid NDCG_REPEAT=3 \
  node scripts/measure-search-ndcg.js --live --qrels perf/ndcg/qrels.json --dump "$W/rerank-off.dump.json"

export BASE_URL=$NDCG_BASE_URL SESSION_COOKIE=$NDCG_SESSION_COOKIE
k6 run --summary-trend-stats 'avg,p(50),p(95),p(99),max' --summary-export "$W/k6-search-off.json" perf/k6/search-load.js
k6 run --summary-trend-stats 'avg,p(50),p(95),p(99),max' --summary-export "$W/k6-rag-off.json"    perf/k6/rag-load.js
```

- 計測器の出力に `⚠ 収集されていないクエリ` が出たら、その回は使わない（検索が失敗している）。
- 無効の構成では、k6 の閾値（検索 1.5 秒・RAG 5 秒）が今の目標である。閾値を外れて k6 が非 0 で終わっても、値は記録する。

## 4. 有効へ切り替える

```bash
helm upgrade msp deploy/helm/microservices-platform -n "$NS" -f "$W/current-values.yaml" --set searchRerank.enabled=true
kubectl -n "$NS" rollout status deployment/retrieval-service --timeout=10m
kubectl -n "$NS" get deploy retrieval-service -o jsonpath='{.spec.template.spec.containers[?(@.name=="retrieval-service")].env[?(@.name=="Rerank__Enabled")].value}{"\n"}'   # true
# 手順 2 のクエリが 1 を返すこと（スクレイプの間隔ぶん待つ）
```

- 🔴 **有効にしている間、警報 `SearchRerankLatencyTargetUndefined`（warning）が 5 分で鳴る。** 有効時の検索 p95 の目標がまだ無いことを知らせる設計どおりの警報である。計測の窓の間は鳴ったままでよい。
  検索 p95 の 1.5 秒の警報はこの間は外れる。RAG 初回応答（5 秒）の警報は外れない。
- 有効の間は、利用者の検索・回答・MCP のツールの検索にも再順位付けが掛かる（費用が出る）。**窓は短く取り、終わったら手順 7 で必ず戻す。**

## 5. 有効の構成で測る（M2・M3・M4 の有効）

```bash
NDCG_RERANK=on NDCG_LABEL=rerank-on NDCG_MODES=keyword,hybrid NDCG_REPEAT=3 \
  node scripts/measure-search-ndcg.js --live --qrels perf/ndcg/qrels.json --dump "$W/rerank-on.dump.json"

k6 run --summary-trend-stats 'avg,p(50),p(95),p(99),max' --summary-export "$W/k6-search-on.json" perf/k6/search-load.js
k6 run --summary-trend-stats 'avg,p(50),p(95),p(99),max' --summary-export "$W/k6-rag-on.json"    perf/k6/rag-load.js
```

- 有効の構成では、k6 の閾値は今の目標（無効時）のままなので非 0 で終わり得る。**値を記録する**（合否ではない）。
- 計測器の `⚠ 再順位付けが元の順へ戻ったクエリ（rerank-failed）` の件数を記録する。多ければ、再順位付けの順位を測れていない。
  繰り返し（`NDCG_REPEAT`）のときは、縮退の理由は**全回の和**で数え、順位は 1 回目を採る。したがって件数は多めに出る（1 回目は再順位付けが効いていた場合も数える）。
  理由は計器 `search_rerank_total{search_rerank_result="degraded"}` の `search_rerank_reason` で読む（`timeout` が多ければ期限 `Rerank__TimeoutSeconds` 8 秒に当たっている）。

## 6. 集計と計器の読み取り

```bash
node scripts/measure-search-ndcg.js --input "$W/rerank-off.dump.json" --input "$W/rerank-on.dump.json"
node scripts/measure-search-ndcg.js --input "$W/rerank-off.dump.json" --input "$W/rerank-on.dump.json" --json > "$W/ndcg-summary.json"
```

- 末尾の「再順位付けの比較」に、モードごとの nDCG の差と所要時間の p95 の差（有効 − 無効）が出る。`hybrid・無効 → keyword・有効` の行が、計画が記録を求めた精度の差である。
- qrels の指紋が 2 つの dump で違うと集計は落ちる（違う正解ラベルの比較を防ぐ）。

計測の窓（手順 5 の開始から終了まで。秒で `$RANGE` に入れる）の計器を読む:

```bash
q() { curl -s --get http://127.0.0.1:19090/api/v1/query --data-urlencode "query=$1" | jq -c '.data.result'; }
q "sum by (search_rerank_result, search_rerank_reason) (increase(search_rerank_total[${RANGE}s]))"     # applied / degraded の内訳
q "sum by (llm_purpose) (increase(llm_completion_total{llm_purpose=~\"rerank|rag-answer\"}[${RANGE}s]))" # 呼び出し回数
q "sum by (llm_purpose) (increase(llm_cost_total{llm_purpose=~\"rerank|rag-answer\"}[${RANGE}s]))"        # 費用（USD）
q "histogram_quantile(0.95, sum by (le) (rate(rag_answer_first_token_duration_seconds_bucket{job=\"microservices-platform.aianalysis-service\"}[${RANGE}s])))"  # 初回トークンの p95（補助）
```

- k6 の p95 は、要約 JSON の `metrics.http_req_duration{scenario:search}`（`rag`）の `p(95)` で読む。
- 初回トークンの p95 は合成監視の回答も含む。k6 の値（応答全体での近似）と並べて記録し、正は k6 とする。

## 7. 戻す

```bash
helm upgrade msp deploy/helm/microservices-platform -n "$NS" -f "$W/current-values.yaml"   # 保存した利用者値だけを渡す
kubectl -n "$NS" rollout status deployment/retrieval-service --timeout=10m
kubectl -n "$NS" get deploy retrieval-service -o jsonpath='{.spec.template.spec.containers[?(@.name=="retrieval-service")].env[?(@.name=="Rerank__Enabled")].value}{"\n"}'   # false
# 手順 2 のクエリが 0 に戻ること。警報 SearchRerankLatencyTargetUndefined が解消すること
```

`--reuse-values` ではなく保存した利用者値を渡す（`--set` の上書きを確実に外す）。

## 8. 記録する

次を #1746（段 S3）へ残し、遅延の値は [性能の負荷試験のテスト仕様書](../tests/NFR-01_performance-load-test.md) の「実測記録」へ 4 行（M3・M4 × 2 構成）を足す。
精度の値は運用仕様書 §埋め込みプロバイダ の測定の項にも要約を残す。dump・qrels・k6 の要約は**コミットしない**（社内文書の題名・ID が入る）。

| 項目 | 例 |
| --- | --- |
| 日時（窓の開始・終了）・環境 | 2026-10-xx 10:00〜10:40 JST・経路B |
| 文書数（voyage と語彙索引の `points_count`） | — |
| qrels の指紋・クエリ数・繰り返し回数 | `76de8f566aa9f56f`・30・3 |
| モデルと effort（`rerank`・`rag-answer`） | `claude-haiku-5-5`・`low` |
| 再順位付けの調整の口（候補数・本文の字数・期限・出力上限） | 20・400・8 秒・1024 |
| nDCG@10（M1 の 3 モード・M2 の 2 モード）と差 | — |
| `rerank-failed` の件数・`search_rerank_total` の内訳 | — |
| 検索 p95・RAG 初回応答 p95（k6。2 構成） | — |
| 呼び出し回数と費用（`rerank`・`rag-answer`） | — |

## 9. 起こり得ること

| 症状 | 読み | 対処 |
| --- | --- | --- |
| 手順 2 の系列が空 | 検索サービスが構成のゲージを出していない（未確認の事項） | 名乗りを確かめられない。先へ進まず、運用仕様書の監視の項を見る |
| M1 のハイブリッドに `embed-failed` | Voyage の鍵が無い・効いていない | 前提の表の Voyage の鍵を確かめる。その回は使わない |
| M2 で `rerank-failed` が多い | 再順位付けの期限切れ・出力の打ち切り | 計器の理由を記録する。期限・出力上限を変えて測り直すときは、変えた値も記録する |
| k6 の `http_req_failed` が閾値を超える | 検索・回答そのものが失敗している | 遅延の値は使わない。ゲートウェイ・検索サービスのログを見る |
| 呼び出し回数が k6 の `http_reqs` と合わない | 窓に利用者・MCP の検索が混ざっている | 窓を取り直すか、混ざった分を備考に書く |

---
title: 運用 Runbook — 既定層 LLM の出力トークン実測（max_tokens 4096 の再判断・レート制限の確認）
type: runbook
status: draft
author: claude
created: 2026-09-26
updated: 2026-09-26
---
<!-- trace:
ids: [FR-11, SC-08, NFR-18, NFR-19]
adrs: [ADR-0010, ADR-0025, ADR-0038, ADR-0044, ADR-0095]
iadrs: [IADR-0101, IADR-0110, IADR-0210, IADR-0212, IADR-0225, IADR-0369, IADR-0374, IADR-0400, IADR-0456, IADR-0466]
specs: [20260926_issue-380_output-token-measurement-runbook, 20260830_issue-380_opus5-max-tokens-measurement, 20260926_issue-1558_runbook-nits]
issues: [#380, #1089, #1091, #1111, #1411, #1539, #1558]
-->

# 運用 Runbook: 既定層 LLM の出力トークン実測

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 起点は #380。既定モデルを `claude-opus-5` にしたときに置いた `max_tokens` 4096 は**実測前の出発値**であり、
> 実測で見直すことが既定モデルの実装 ADR のフォローアップとして残っている（trace ブロック参照）。
>
> 🔴 **この手順は費用を発生させる。** 実行するかどうか・いくらまで出すかは**所有者の判断**であり、
> 本書はその判断に要る数字（§2）と、判断した後の手順（§3〜§6）を与える。**AI はキーを入れず、実行もしない。**
>
> **キーの値は本書にもリポジトリのどこにも置かない。**

## この手順を実行する条件（いつ走らせるか）

- 所有者が §2 の費用の上限を見て、**実行と予算を承認したとき**に 1 回だけ走らせる（定期ではない）。
- 対象は経路B（ローカル k8s。`scripts/k8s-local-up.sh` で立てた環境）の Prometheus と LLM ゲートウェイである。
- 再測定は、`max_tokens` を変えた後（§5 で「引き上げ」を選んだ場合）に同じ手順でもう 1 回行う。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | 対象クラスタへの `kubectl`（`microservices-platform` と `platform-infra` の読み取りと `exec`、`port-forward`）。キー投入に製品の画面（秘密情報・接続設定の管理）を開けるロール。AI 分析ダッシュボードを使える通常の利用者アカウント（**合成監視の主体ではないもの**） |
| 必要なツール | `kubectl`・`curl`。PromQL は Prometheus の HTTP API へ `curl --get --data-urlencode` で送る |
| 必要なもの | **この測定専用に発行した** Anthropic の API キー（止めるときに他へ影響を出さずに失効できるように） |
| 所要時間の目安 | 前提の確認 15 分。標本づくりは N と頻度に依存（N=100 を画面から手で作ると 2〜3 時間）。測定と記録 30 分 |

---

## §1 前提がそろっていることを確かめる（読み取りだけ）

2026-09-05 の再検証で、環境側の前提は 4 つとも満たされたと記録されている（#380 のコメント）。
**記録を信じず、実行の直前に測り直す。** どれか 1 つでも崩れていれば §3 へ進まない
—— 費用だけが出て数字が残らない。

以下、Prometheus へは次のポートフォワードを張った状態で問い合わせる。

```sh
kubectl -n platform-infra port-forward svc/prometheus 19090:9090
# 別の端末で
curl -s http://127.0.0.1:19090/-/ready          # → Prometheus Server is Ready.
```

PromQL を送る形はこれで統一する（`--data-urlencode` を使う。`--post-data` で送ると式中の `+` が空白に化ける）。

```sh
q() { curl -s --get http://127.0.0.1:19090/api/v1/query --data-urlencode "query=$1"; echo; }
```

### 1-1. collector がアプリのメトリクスを Prometheus へ転送している

```sh
kubectl -n platform-infra get cm otel-collector-config -o jsonpath='{.data}' | grep -o 'prometheusremotewrite' | head -1
q 'otelcol_exporter_sent_metric_points{exporter="prometheusremotewrite"}'
```

- 1 行目が `prometheusremotewrite` を返し、2 行目が**空でない値**を返すこと。
- 🔴 **陽性対照**: 同じ問い合わせを `exporter="debug"` で送り、値が返ることも確かめる。
  `debug` だけが返り `prometheusremotewrite` が空なら、**fail-safe 構成（受け取った点を全部捨てる）で動いている**。
  その状態では何件呼んでも `llm_*` は 1 系列も残らない（2026-08-30 に実際に起きた）。

### 1-2. 稼働中のゲートウェイの image に計器が入っている

```sh
kubectl -n microservices-platform get pods -o name | grep -i llmgateway      # → pod/<llmgateway の Pod 名>
kubectl -n microservices-platform exec <llmgateway の Pod 名> -c llmgateway-service -- sh -c "ls -la /app/LlmGateway.dll"
kubectl -n microservices-platform exec <llmgateway の Pod 名> -c llmgateway-service -- \
  sh -c "tr -d '\000' < /app/LlmGateway.dll | grep -ao 'llm\.[a-z_.]\{3,\}' | sort -u"
```

出力に次の 4 つが**すべて**含まれること: `llm.completion.output_tokens`・`llm.upstream_status`・`llm.cost.total`・`llm.tokens.total`。

- 🔴 **アセンブリは `/app/LlmGateway.dll` である**（`LlmGateway.Api.dll` ではない。存在しないファイル名で測って「無い」と結論した誤りが過去にある）。
- 🔴 **`tr -d '\000'` を外さない。** .NET のメタデータ文字列は UTF-16 なので、素の `grep` は 0 件を返す。
- **陽性対照**: `llm.completion.total` が出ていること。これすら出ないなら、image ではなく**測り方**が違う。

### 1-3. Prometheus が蓄積する（再起動で消えない）

```sh
kubectl -n platform-infra get deploy prometheus -o jsonpath='{.spec.template.spec.volumes}'; echo
curl -s http://127.0.0.1:19090/api/v1/status/runtimeinfo | grep -o '"startTime":"[^"]*"\|"storageRetention":"[^"]*"'
```

- `volumes` に `"claimName":"prometheus-data"` が含まれること（`config` の ConfigMap だけなら**永続化されていない**）。
- `storageRetention` に `35d` が含まれること。**標本づくりから読み取りまでをこの期間に収める。**
- **陽性対照**: Pod の起動より前のデータが残っていること。`startTime` より 1 時間前を指定して問い合わせ、値が返れば蓄積している。

```sh
curl -s --get http://127.0.0.1:19090/api/v1/query \
  --data-urlencode 'query=up{job="otel-collector"}' --data-urlencode 'time=<startTime の 1 時間前（RFC3339）>'
```

  （Prometheus を一度も再起動していない環境では空になりうる。そのときは PVC の mount だけで判断する。）

### 1-4. レート制限（429）を他の失敗と区別する軸がある

1-2 の出力に `llm.upstream_status` があれば、計器の側はそろっている。
Prometheus の側では**最初の呼び出しの後**にしか確かめられない（§4-0 で見る）。

### 1-5. キーを入れる経路が動いている

保管先（Vault）・同期（External Secrets Operator）・消費側の作り直し（Stakater Reloader）が動いていること。
確かめ方は [`secret-rotation-runbook.md`](secret-rotation-runbook.md) §前提 と [確認](secret-rotation-runbook.md#確認この手順が成功したと言える条件) に従う。

---

## §2 費用の上限（所有者の承認用）

### 単価（リポジトリの単価表から読んだ値）

単一情報源は `src/platform/backend/Services/LlmGateway/appsettings.json` の `Llm:Pricing` である。
**通貨は `USD`**、単位は**百万トークンあたり**。本書の値が古くなったら、単価表を正として次の式で計算し直す。

```sh
node -e "const d=require('./src/platform/backend/Services/LlmGateway/appsettings.json');\
console.log(d.Llm.Pricing.Currency, JSON.stringify(d.Llm.Pricing.Models, null, 1))"
```

| モデル | 入力 | 出力 | 本書での使いみち |
| --- | --- | --- | --- |
| `claude-opus-5` | 5.0 USD | 25.0 USD | **既定層**（`PurposeModels.default`）。標本の本体（AI 分析） |
| `claude-sonnet-5` | 3.0 USD | 15.0 USD（2026-09-01 以降の区間） | 検索チャットの回答（任意の追加標本）・AI 分析のフォールバック先 |

### 式

`max_tokens` は**思考と本文を合わせた出力の上限**である。上限を承認するため、**全呼び出しが 4096 に張り付いた**と置く。

```
1 回あたりの最悪額 = 入力トークン × 入力単価 / 1,000,000  +  4096 × 出力単価 / 1,000,000
合計の最悪額       = 1 回あたりの最悪額 × N
```

入力の代表値は 2 通り置く。**4,000 トークン**（短い指示と少ない文脈）と、**16,000 トークン**（保守側の置き値:
AI 分析が渡す文脈は既定で上位 5 チャンク、1 チャンクは最大 2,048 文字。5 × 2,048 = 10,240 文字を
1 文字 1.5 トークンと見て 15,360、指示文を足して丸めた）。**16,000 は推定である。§3-3 で実測値へ置き換える。**

### `claude-opus-5`（AI 分析）の最悪額

| 入力 | 1 回あたり | N = 50 | N = 100 | N = 200 |
| --- | --- | --- | --- | --- |
| 4,000 | 0.020 + 0.1024 = **0.1224 USD** | 6.12 USD | 12.24 USD | 24.48 USD |
| 16,000 | 0.080 + 0.1024 = **0.1824 USD** | 9.12 USD | 18.24 USD | 36.48 USD |

### `claude-sonnet-5`（検索チャット。任意）の最悪額

| 入力 | 1 回あたり | N = 50 | N = 100 | N = 200 |
| --- | --- | --- | --- | --- |
| 4,000 | 0.012 + 0.06144 = **0.07344 USD** | 3.672 USD | 7.344 USD | 14.688 USD |
| 16,000 | 0.048 + 0.06144 = **0.10944 USD** | 5.472 USD | 10.944 USD | 21.888 USD |

- **これは上限であって見込みではない。** 実際の額は出力の分布で決まり、§4-4 で読める。
- **フォールバック**: AI 分析の第 1 候補が 4xx（429 を除く）で断られると、同じ依頼が `claude-sonnet-5` で 1 回だけ再送される。
  断られた試行は応答を生成していないので通常は加算されないが、仮に両方を数えても 1 依頼あたり
  0.1824 + 0.10944 = **0.29184 USD**（入力 16,000 のとき）を超えない。
- 🔴 **キーを入れた時点で、標本以外の経路も費用を出し始める。** グラフの AI 提案（文書の更新を購読して発火し、`claude-opus-5` へ行く。
  メトリクス上の用途は `other`）、検索チャット、図のコード化、同じゲートウェイを使う他ユニットの呼び出しである。
  標本の期間中は文書の大量取り込みを避け、§4-4 の費用の累計を**用途別に**見て、承認額を超えそうなら §3-4 で止める。

### N の選び方（統計的な意味）

上限到達が **0 件**だったとき、上限到達率の 95% 上側信頼限界はおよそ **`3 / N`** である（3 の法則）。

| N | 0 件だったときに言えること |
| --- | --- |
| 50 | 到達率は 6% 未満（95%） |
| 100 | 到達率は 3% 未満（95%） |
| 200 | 到達率は 1.5% 未満（95%） |

§5 の判断基準（提案値）は到達率 2% を境にしている。**「0 件なら据え置き」を 2% 未満の根拠として言いたいなら N ≥ 150** が要る。
N = 100 は「3% 未満」までしか言えないことを承知で選ぶ。

**所有者が決めること**: N（と、任意で検索チャットの N）、承認額（USD）、期間。決めたら §6 の記録先へ書いてから §3 へ進む。

---

## §3 標本をつくる（所有者が実行する）

### 3-1. キーを入れる

**手順は [`secret-rotation-runbook.md`](secret-rotation-runbook.md) の [手順 A](secret-rotation-runbook.md#手順-a-items画面から回す) に従う**
（項目 `llm-provider-credentials`、プロパティ `anthropic-api-key`）。画面が使えないときだけ
[`secret-item-console-injection-runbook.md`](secret-item-console-injection-runbook.md) を使う。
**値を画面・端末・記録のどこにも出さない。**

投入が成立すると、画面が同期を依頼し、Stakater Reloader がゲートウェイ（Deployment `llmgateway-service`）を作り直す。

```sh
kubectl -n microservices-platform rollout status deploy/llmgateway-service --timeout=180s
kubectl -n microservices-platform get secret llm-provider-credentials \
  -o jsonpath='{.data.anthropic-api-key}' | base64 -d | wc -c        # 長さだけ。発行したキーの長さと一致すること
```

### 3-2. 最初の 1 回で経路を確かめる

AI 分析ダッシュボード（画面のルート `/analyze`）で、**機密区分が公開または社内の文書**を対象に分析を 1 回依頼する。
回答に本文とモデル名が出ること。続けて §4-0 の陽性対照を見る。**ここで系列が出なければ標本づくりへ進まない**
（出ない理由は §1 のどれかが崩れている）。

### 3-3. 入力の実測値で上限を計算し直す

5 回ほど依頼した後で、実際の平均入力トークンを読む。

```sh
q 'sum by (llm_purpose, llm_model) (increase(llm_tokens_total{llm_token_type="input"}[1d]))
   / sum by (llm_purpose, llm_model) (increase(llm_completion_total{llm_result="sent"}[1d]))'
```

`analysis` / `claude-opus-5` の値を §2 の式の入力に入れて 1 回あたりの最悪額を出し直し、**承認額 ÷ 最悪額 ≥ 残りの N** であることを確かめる。
足りなければ N を減らすか、承認額を上げる判断を所有者が行う。

### 3-4. 標本を N 件までつくる

- **発生源は AI 分析ダッシュボードだけにする**（本リポジトリで人が意図して `claude-opus-5` を呼べるのはここである。
  メトリクス上は `llm_purpose="analysis"`, `llm_model="claude-opus-5"`）。
- **依頼の中身は実運用に近いものにする。** 実際に使う予定の分析指示（要約・比較・抽出）を 10〜20 種用意し、
  対象範囲（タグ・部門・プロジェクト）を変えながら回す。**短い定型文の反復は、上限を測る標本にならない**
  （思考量が実運用より少なく出る）。
- **間隔を空ける。** 自分でレート制限を起こさないよう、1 件ずつ応答を待ってから次を送る。
- 検索チャットの追加標本（`rag-answer` / `claude-sonnet-5`）を取る場合も同じ要領で行う。
- 合成監視の主体では標本にならない（その主体の依頼は既定で LLM を呼ばない）。
- 途中で §4-1 の件数と §4-4 の費用を見て、N に達したか・承認額を超えそうかを確かめる。

### 3-5. 止める（N に達したとき・承認額に近づいたとき・429 が続いたとき）

画面は空の値を受け付けないので、**画面でキーを消すことはできない。** 次の順で止める。

1. **発行元（Anthropic の管理画面）で、この測定用のキーを失効させる。** これで費用は確実に止まる
   （測定専用のキーにしたのはこのためである）。
2. **保管先の値を空に戻す**（fail-safe の状態へ戻す）。コンソール手順
   [`secret-item-console-injection-runbook.md`](secret-item-console-injection-runbook.md) の手順 3・4 を、値を空にして行う。

   ```sh
   kubectl -n platform-infra exec deploy/vault -- sh -c '
     export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"
     vault kv patch -method=patch secret/msp/llm-provider-credentials anthropic-api-key=""
   '
   kubectl -n microservices-platform annotate externalsecret llm-provider-credentials force-sync="$(date +%s)" --overwrite
   kubectl -n microservices-platform rollout status deploy/llmgateway-service --timeout=180s
   ```

   - 空文字の `patch` は本書の作成時点で**実測していない**。下の確認で長さが 0 になることを必ず見る。
   - Reloader が作り直さなかったときは `kubectl -n microservices-platform rollout restart deploy/llmgateway-service`。
   - コンソールを使ったことは、同書 手順 5 の記録先へ残す（監査ログに乗らないため）。

**止まったことの確認**:

```sh
kubectl -n microservices-platform get secret llm-provider-credentials \
  -o jsonpath='{.data.anthropic-api-key}' | base64 -d | wc -c        # → 0
```

その後に AI 分析を 1 回依頼し、15 分後に次を見る。**`sent` が増えていなければ止まっている。**

```sh
q 'sum(increase(llm_completion_total{llm_result="sent"}[15m]))'
q 'sum by (llm_result, llm_upstream_status) (increase(llm_completion_total[15m]))'
```

後者は、止めた後の依頼が送信に失敗した系列（`upstream_error` や `fallback`）として現れることの確認である
（**止めた後の依頼が 1 系列も現れないなら、依頼そのものが届いていない**。確認をやり直す）。

---

## §4 測る

以下の `<窓>` は標本づくりの開始から読み取り時点までを覆う長さ（例 `14d`。**35d 以内**）に置き換える。
標本は間隔が空くので `rate()` ではなく**窓全体の `increase()`** で読む（`rate()` は系列ごとに 2 点以上を要し、
まばらな呼び出しでは 0 や空を返す）。`increase()` は外挿するので件数が整数にならないことがある —— 四捨五入して読む。

### 4-0. 陽性対照（最初に見る。空なら以降の空に意味は無い）

```sh
q 'count by (__name__) ({__name__=~"llm_.+"})'
q 'sum by (llm_purpose, llm_model, llm_upstream_status) (increase(llm_completion_total[<窓>]))'
```

- 1 本目に `llm_completion_total`・`llm_completion_output_tokens_bucket`（`_count` / `_sum`）・`llm_tokens_total`・`llm_cost_total` が出ること。
- 2 本目に **`llm_upstream_status="none"` の系列が実在する**こと。これが 429 の読み（§6）の陽性対照でもある。

### 4-1. 標本数

```sh
q 'sum by (llm_purpose, llm_model) (increase(llm_completion_total{llm_result="sent"}[<窓>]))'
q 'sum by (llm_purpose, llm_model) (increase(llm_completion_output_tokens_count[<窓>]))'
```

- 1 本目の `analysis` / `claude-opus-5` が N に達していること。
- 2 本目（分布に載った件数）が 1 本目とほぼ一致すること。**Counter は送信した呼び出しを全部数え、Histogram は出力トークン数を受け取れた呼び出しだけを数える**ため、
  逐次経路で途中終了があると 2 本目が少なくなる。**判断には 2 本目を分母に使う。**

### 4-2. 出力トークンの分布

```sh
# 累積の度数（le ごと）。上限付近の厚みを見る
q 'sum by (le) (increase(llm_completion_output_tokens_bucket{llm_purpose="analysis", llm_model="claude-opus-5"}[<窓>]))'

# 3072 を超えた割合（上限のすぐ下に山があるか）
q '1 - sum(increase(llm_completion_output_tokens_bucket{llm_purpose="analysis", llm_model="claude-opus-5", le="3072"}[<窓>]))
      / sum(increase(llm_completion_output_tokens_count{llm_purpose="analysis", llm_model="claude-opus-5"}[<窓>]))'

# p95 / p99（参考値）
q 'histogram_quantile(0.95, sum by (le) (increase(llm_completion_output_tokens_bucket{llm_purpose="analysis", llm_model="claude-opus-5"}[<窓>])))'
q 'histogram_quantile(0.99, sum by (le) (increase(llm_completion_output_tokens_bucket{llm_purpose="analysis", llm_model="claude-opus-5"}[<窓>])))'

# 平均出力トークン
q 'sum(increase(llm_completion_output_tokens_sum{llm_purpose="analysis", llm_model="claude-opus-5"}[<窓>]))
   / sum(increase(llm_completion_output_tokens_count{llm_purpose="analysis", llm_model="claude-opus-5"}[<窓>]))'
```

- バケットの境界は `0, 16, 64, 128, 256, 512, 1024, 2048, 3072, 4096, 8192`。**`le` の値の書式は 1 本目の出力で確かめてから** 2 本目の `le="3072"` を合わせる。
- 🔴 **p95 / p99 はバケット内の線形補間であり、3072〜4096 の間では粗い。** 判断には補間値ではなく**「3072 超の割合」と §4-3 の上限到達率**を使う。
- 上限に達した呼び出しは出力が 4096 ちょうどになるので `le="4096"` のバケットに入る。4096 と 8192 の間に値が出るのは、
  既定より大きい `max_tokens` を渡した呼び出しだけである。

### 4-3. 上限到達率（用途別・モデル別）

```sh
# 終了理由の内訳（こちらを主に読む）
q 'sum by (llm_purpose, llm_model, llm_stop_reason) (increase(llm_completion_total{llm_result="sent"}[<窓>]))'

# 到達率
q 'sum by (llm_purpose, llm_model) (increase(llm_completion_total{llm_stop_reason="max_tokens"}[<窓>]))
   / sum by (llm_purpose, llm_model) (increase(llm_completion_total{llm_result="sent"}[<窓>]))'
```

- 🔴 **一度も上限に達していなければ `max_tokens` の系列は作られず、到達率の式は 0 ではなく空を返す。**
  内訳の式で `end_turn` の系列が実在することを確かめてから「0 件」と読む。
- 上限に達した呼び出しはゲートウェイのログにも警告として残る（`LLM hit the output limit (stop_reason=max_tokens)`）。
  個別の事例を見たいときは次で数える（Pod のログは作り直しで消える。標本の期間中に見る）。

  ```sh
  kubectl -n microservices-platform logs deploy/llmgateway-service -c llmgateway-service --since=24h | grep -c "stop_reason=max_tokens"
  ```

### 4-4. 実際の費用

```sh
q 'sum by (llm_purpose, llm_model, llm_currency) (increase(llm_cost_total[<窓>]))'
q 'sum by (llm_pricing_status, llm_model) (increase(llm_pricing_unpriced_total[<窓>]))'
```

- 2 本目は**空または 0** であること。0 でなければ単価の解決漏れで、1 本目は**過小**である。
- 🔴 2 本目の空は、1 本目が空でないときにだけ「漏れが無い」と読める。

---

## §5 `max_tokens` を判断する

### 判断基準（提案値）

🔴 **以下の数値は本書が置いた提案であり、所有者が承認して初めて基準になる。** 実装側は数字を決めない。

N 件（§4-1 の 2 本目）のうち、上限に達した件数を H、到達率を r = H / N、3072 超の割合を S とする。

| 観測 | 判断 | 理由 |
| --- | --- | --- |
| H = 0 かつ S < 5% | **4096 のまま据え置く** | 上限に余裕がある。§2 の N の表のとおり、H = 0 から言える到達率の上限は N で決まる |
| H ≥ 1 かつ r < 2% | **据え置く**。§4-3 のログで個別の事例を見る | 特定の依頼だけが長い可能性がある。一律に上げると全呼び出しの最悪額が上がる |
| r ≥ 2% または S ≥ 10% | **8192 へ引き上げる候補**。上げた後に同じ N で測り直す | 本文が空または途中で切れる応答が無視できない頻度で出ている（例外にならず静かに縮退する） |
| 下げる | **提案しない** | `max_tokens` は上限であって消費量ではない。下げても短い応答の費用は変わらず、減るのは最悪額だけで、切断の危険が増える |

- 引き上げると 1 回あたりの最悪額の出力側が倍になる（`claude-opus-5` で 0.1024 → 0.2048 USD）。§2 の式で計算し直してから承認する。
- 8192 を超えて上げる場合は、出力トークンの計器のバケット境界（最上段 8192）も見直しが要る —— 8192 超は全部 `+Inf` に入り、分布が読めなくなる。

### 値を持っている場所（変えるならどこを変えるか）

**どの用途の証拠かで、変える場所が違う。** 1 か所だけ変えても、目的の用途に効かないことがある。

| 場所 | 効く経路 | 効く用途（現行の割当） |
| --- | --- | --- |
| `src/knowledge/backend/Services/AiAnalysisService/Infrastructure/ExternalServices/RagOrchestrator.cs` の `MaxTokens: 4096`（**2 か所**: 一括の生成と逐次の生成） | AI 分析と検索チャット。**明示指定なので下の既定値を変えても効かない** | 一括は `analysis`（`claude-opus-5`）と `rag-answer`（`claude-sonnet-5`）の**共用**、逐次は `rag-answer` |
| `src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/CompletionDto.cs` の `CompletionApiRequest(… int MaxTokens = 4096 …)` | HTTP 経路で `max_tokens` を省略した呼び出し元 | グラフの AI 提案・クラスタ要約（`claude-opus-5`。メトリクス上は `other`） |
| `src/platform/backend/Services/LlmGateway/Domain/Ports/ILlmProvider.cs` の `CompletionRequest(… int MaxTokens = 4096 …)` | プロバイダを直接呼ぶ内部経路だけ（ゲートウェイの端点は常に明示して渡す） | 通常の経路には効かない。上と揃えるために変える |
| `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Llm/LlmGrpcMapping.cs` の `DefaultMaxTokens = 4096` | gRPC 経路で `max_tokens=0`（未指定）を受けたとき | HTTP 経路の既定の写しである。**上の DTO の既定と必ず同時に変える** |

- 起票時（#380 本文）の対象は上の 3 か所（一覧の 1〜3 行目）だった。4 行目は後から gRPC 経路が入って増えた。
- `analysis` の証拠で `RagOrchestrator` の一括側を上げると、`rag-answer` も同じ値になる。
  用途ごとに値を分けるのは設計の変更であり、実装 ADR を起こしてから行う。

### 記録する

- **結果（据え置きでも）を、既定 `max_tokens` を定めた実装 ADR へ日付つき追記として書く**
  （2026-08-30 の「測れなかった」の追記と同じ形。PR #1089 が前例）。書く項目: 測定期間・N・H と r・S・p95 / p99（参考）・
  平均出力トークン・実際の費用（USD）・判断と、それが提案値のどの行に当たるか。
- 値を変える場合は、上の場所の変更と同じ PR に追記を置く。値を変えない場合は追記だけの PR にする。
- #380 へ、同じ内容の要約と PR へのリンクをコメントする。

---

## §6 レート制限（429）を確かめる

### 読み方

```sh
# 分子: 429 が起きたか
q 'sum by (llm_purpose, llm_model) (increase(llm_completion_total{llm_upstream_status="rate_limited"}[<窓>]))'
# 陽性対照: 同じ期間に計器が動いていたか（これが空なら上の空には意味が無い）
q 'sum by (llm_purpose, llm_model) (increase(llm_completion_total{llm_upstream_status="none"}[<窓>]))'
# 失敗の内訳
q 'sum by (llm_result, llm_upstream_status) (increase(llm_completion_total{llm_result=~"upstream_error|fallback"}[<窓>]))'
```

- 🔴 **Prometheus は起きていないラベル値を 0 として持たない。** 1 本目の空ベクタは、2 本目が同じ窓で空でないときにだけ
  「429 は起きなかった」と読める。**2 本を対で記録する。**
- 429 は `llm_result="upstream_error"` の中に `llm_upstream_status="rate_limited"` として現れる（フォールバックはしない設計である）。
- ログでは `(upstream status 429)` の形で残る。

  ```sh
  kubectl -n microservices-platform logs deploy/llmgateway-service -c llmgateway-service --since=24h | grep -c "upstream status 429"
  ```

- 🔴 **「429 が出なかった」は、この標本の頻度（人が 1 件ずつ送る程度）で出なかった、という意味でしかない。**
  実運用の同時実行で出ないことの証明ではない。記録にはそう書く。

### 429 が出たとき

1. **標本づくりの間隔を広げて続ける。** 1 件ずつ送っていても出るなら、§3-5 で止めて次へ進む。
2. **発行元の管理画面で、組織に割り当てられたレート制限の枠（`claude-opus-5` の枠）を確かめる。**
   Opus 5 の枠は Opus 4.x 系とは別であり、既定層を移したことで足りなくなっている可能性がある。
3. 🔴 **別モデルへのフォールバックで逃がさない。** 429 は再試行の対象であってフォールバックの対象ではない
   （[`llm-model-pin-runbook.md`](llm-model-pin-runbook.md) §レート制限（429）は別物である）。
   なお、429 の再試行はゲートウェイにまだ実装されていない。
4. 緩和策（呼び出しの周期を延ばす・用途別の振り分けで一部を別モデルへ移す・再試行の実装）は**設計の変更**であり、
   本手順では行わない。件数・発生した用途とモデル・時刻を #380 へ記録し、対応を別の issue として起票する。

---

## 確認（この手順が成功したと言える条件）

- §1 の 5 項目がすべて陽性対照つきで満たされていた。
- §4-1 の分布に載った件数が、所有者が決めた N 以上である（`analysis` / `claude-opus-5`）。
- §4-4 の実費が承認額以内で、単価の解決漏れが 0（空）である。
- §3-5 の停止確認で、キーの長さが 0、止めた後に `sent` が増えていない。
- §5 の判断が提案値（または所有者が承認した基準）のどの行に当たるかを示して記録されている。
- §6 の 429 の読みが、分子と陽性対照の対で記録されている。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| §4-0 で `llm_*` が 1 系列も出ない | collector が fail-safe 構成（§1-1）／ゲートウェイの image が古い（§1-2）／依頼が LLM まで届いていない | §1 をやり直す。**直るまで標本を増やさない**（費用だけが出る） |
| 依頼の回答が「送信できません」「利用できません」になる | キーが同期されていない・Pod が作り直されていない／機密区分が高い文書を対象にしている | §3-1 の長さの確認と `rollout status` を見る。対象を公開・社内の文書にする |
| `llm_purpose` が `analysis` ではなく `other` に積まれる | 設定の用途一覧から `analysis` が外れている | `appsettings.json` の `Llm:Routing:PurposeModels` を読む（[`llm-model-pin-runbook.md`](llm-model-pin-runbook.md) の列挙コマンド） |
| `llm_model` が `claude-opus-5` ではない | 用途の割当が変わった／フォールバックが起きた（`llm_result="fallback"` の系列を見る） | 割当が変わったなら本書 §2 の単価と表を計算し直す |
| §4-4 の単価の解決漏れが 0 でない | 単価表に該当モデルの区間が無い | 費用の読みを止め、単価表を直してから読み直す。分布と到達率の読みは影響を受けない |
| 空文字の `patch` 後も長さが 0 にならない | 同期が走っていない／`patch` が空文字を受け付けなかった | `kubectl describe externalsecret llm-provider-credentials` を読む。**費用は 3-5 の 1（発行元での失効）で既に止まっている** |

## 記録

- **承認（§2 の後）**: #380 へコメントする。N・承認額（USD）・期間・キーを発行した日（**値は書かない**）。
- **結果（§5・§6 の後）**: 既定 `max_tokens` を定めた実装 ADR への日付つき追記と、#380 へのコメント。
- **コンソール操作（§3-5 の 2）**: [`secret-item-console-injection-runbook.md`](secret-item-console-injection-runbook.md) 手順 5 の記録先。

## 限界（この手順で担保できないこと）

- **標本は実運用ではない。** 人が画面から作った依頼の分布であり、実運用の依頼の分布と一致する保証は無い。
  指示の種類を実運用に寄せるのは実行者の責任である。
- **429 の確認は標本の頻度でしか言えない**（§6）。
- **月次予算の上限アラートは配線済みだが、金額が未設定のあいだは発火しない。** 用途別の上限アラートは入っており、
  所有者が金額を設定すれば、本測定の費用の超過にも気づく安全網として使える
  （置き場と手順は [`llm-cost-monthly-review-runbook.md`](llm-cost-monthly-review-runbook.md) §金額を設定する手順）。
  ただし**アラートは検知であって費用を止めない**（既定の受信先は通知をどこへも届けない）。また金額を置く変更は
  月次確認の Runbook を終了させる変更と同じであり、金額を決める前提（数か月分の実績）は本測定の後に初めて貯まり始める。
  **金額が無いあいだ、実費の上限は本書の承認額と §3-5 の停止でしか守られない。**
- `rag-answer` の値は `analysis` と共用であり、本書の標本の本体（`analysis`）だけでは `rag-answer` の妥当性は言えない。
  `rag-answer` についても判断したいなら §3-4 の任意の追加標本を取る。
- 空文字での保管先の書き戻し（§3-5 の 2）は実測していない。費用を止める一次手段は発行元での失効である。

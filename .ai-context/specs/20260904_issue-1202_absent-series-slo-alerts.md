---
title: 作業仕様書 — 常時トラフィックがある経路の SLO へ、系列の不在を検知する absent ルールを併設する
type: spec
status: done
related_ids:
  - NFR-21
  - NFR-01
  - NFR-02
  - ADR-0006
  - ADR-0076
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - "ADR-0076 決定 3（常時トラフィックがある経路の SLO は、系列の不在そのものを warning とする。absent() / absent_over_time() を併設する。判定基準は『その経路が無風でいられる時間が検知要件（5 分）より短いこと』。無風が 5 分を超え得る経路（/analysis/ask 系）は対象外とし、決定 4 の合成監視で常時トラフィックを作ってから対象へ入れる）"
  - "ADR-0076 決定 2（SLO の統制は『アラートルールがあること』ではなく『評価対象があること』まで含む）"
  - "ADR-0076 理由（決定 3 が限定適用なのは、恒常発火が警報を無効化するためである）"
  - "ADR-0076 §残るもの（absent の対象経路の確定は運用設計に残る。決定 3 は判定基準を定めたが、どの経路が該当するかの一覧は作っていない）"
  - "ADR-0076 §統制と現在の実現手段（『評価対象が無いことを検知する』の現在の実現手段は無い。ServiceRequestMetricsAbsent が拾うのは『直近まで受信していたのに途絶した』場合だけで、一度も存在しなかった系列は拾えない）"
  - "02_requirements/01_requirements.md NFR-21（障害検出 5 分以内 / MTTR 30 分以内）"
related_adrs:
  - IADR-0369
  - IADR-0345
  - IADR-0354
  - IADR-0165
  - IADR-0168
  - IADR-0304
  - IADR-0322
issue: "#1202"
---

# 作業仕様書: 系列の不在（`absent`）を SLO へ併設する

## 起点

`ADR-0076` 決定 3 の受け皿が #1202 である。裁定は 3 つを同時に縛っている。

| # | 縛り | 本作業での帰結 |
| --- | --- | --- |
| 1 | **限定適用**（全 SLO に対で置かない） | 対象を「無風でいられる時間が 5 分より短い経路」に絞る。案 B（全部に置く）は計画が却下している |
| 2 | **判定は稼働環境の事実で決める** | `ADR-0076` 自身が「どの経路が該当するかの一覧は作っていない」と残している。**リポジトリの中だけでは決められない** |
| 3 | **`/analysis/ask` 系は対象外** | 合成監視（決定 4）が着地するまで入れない |

`ADR-0076` 決定 3 は、`IADR-0345` 決定 5 が「静的検査は 2 回目まで繰り延べる」と判断した穴を、
**稼働環境の側から**塞ぐものである。**静的検査の側は繰り延べたままでよい**（決定 3 の 🔴 が明示している）。

## 母集合（着手前に自分で引いた。issue 本文からは転記していない）

### 母集合 1: 直すファイル（誤りの側の文字列で走査する。規則 9）

走査語は**現在のルール名**である（「absent」で引くと PromQL 関数と識別子が混ざり、
`ServiceRequestMetricsAbsent` という**名前だけ absent のルール**を取り落とす）。

```console
$ git grep -ln "ServiceRequestMetricsAbsent" -- . ':!src/ai-stock-trading'
.ai-context/adr/IADR-0345_slo-alert-metric-alignment.md      ← 凍結記録。書き換えない
.ai-context/adr/IADR-0354_rag-first-token-latency-metric.md  ← 凍結記録。書き換えない
.ai-context/specs/20260830_issue-546_alertmanager.md          ← 確定済み仕様書。書き換えない
.ai-context/specs/20260831_issue-1110_slo-alert-metric-alignment.md ← 同上
deploy/grafana/provisioning/alerting/slo-alerts.yaml          ← 直す
deploy/local/observability/grafana.yaml                       ← 直す（上の inline）
deploy/local/observability/prometheus.yaml                    ← 直す（alerts.yml の inline）
deploy/prometheus/alerts.yml                                  ← 直す
```

`docs/operations/operations.md` は上の走査に**掛からない**（`docs/` は表示テキストへ
ルール名を書いてよいが、SLO 表は「監視対象」の日本語で書かれている）。
**掛からないことを「対象外」と読まない** —— 表の中身は同じものを指しているので、
別の語（`SearchLatencyP95High` を含む文書 → `perf/k6/README.md` と `docs/operations/operations.md`）で
引き直して確認した。`perf/k6/README.md` は k6 の閾値の話であり、SLO アラート表ではない（対象外）。

### 母集合 2: 導出値（走査ではなく計算し直す。規則 10）

**ルール件数は導出値である。** 本 PR がルールを足すと、下の 9 か所すべてが同時に誤りになる。

```console
$ git grep -n "6 ルール\|6 件返す\|下表 6\|全 6" -- deploy/ docs/operations/ scripts/
deploy/grafana/provisioning/alerting/slo-alerts.yaml:24        （/api/v1/provisioning/alert-rules が 6 件）
deploy/local/observability/grafana.yaml:435                    （同・inline の写し）
deploy/local/observability/prometheus.yaml:24                  （全 6 ルールを inline する）
docs/operations/operations.md:609,636,653,663,673,678          （6 ルール ×6 か所）
scripts/check-grafana-alerting.js:12                           （配備時に 6 件返すことを確かめる）
```

🔴 **`scripts/check-grafana-alerting.js` は #1202 の宣言ファイル領域に無い。**
**それでも直す** —— 本 PR が加える変更が、この行を誤りにするからである（規則 10）。
変更はコメント 1 行のみで、検査ロジックには触らない。並列相手（#1088）は `scripts/k8s-local-up.sh` /
`check-stack-ready.js` を持っており、本ファイルとは重ならない。

### 母集合 3: `absent` を併設する経路（**稼働クラスタで実測して決める**）

**issue 本文の推測を転記しない。** 判定基準は `ADR-0076` 決定 3 の
「**その経路が無風でいられる時間が検知要件（5 分）より短いか**」である。
実測の手順と結果は §実測 に置く。

## 設計

### 判断 1: 併設するのは「SLO ルールが参照する系列」であって「SLO ルールそのものの対」ではない

現在の 6 ルールのうち **`ServiceRequestMetricsAbsent` と `HighHttp5xxRate` は同じ系列
（`http_server_request_duration_seconds_count`）を見ている。**
ルール 1 件ごとに対を置くと**同じ不在で 2 通鳴る**。`ADR-0076` 理由が禁じた
「恒常発火が警報を無効化する」と同じ型（重複通知が「既知の誤報」の習慣を作る）なので、
**系列ごとに 1 件**置く。

### 判断 2: 名前は `…SeriesAbsent` にし、既存の `ServiceRequestMetricsAbsent` と読み分ける

🔴 **既存の `ServiceRequestMetricsAbsent` は名前に反して `absent` を使っていない。**
式は「**直近まで受信していたのに途絶した**」（`== 0 and on (job) (… offset 15m > 0)`）であり、
**一度も存在しなかった系列は拾えない**（`ADR-0076` §統制と現在の実現手段 の暫定手段欄と同じ指摘）。

**改名はしない**（`IADR-0345` の実測記録・`docs/` の記述・`scripts/` の自己試験がこの名前で書かれており、
改名は本 PR の射程を大きく超える）。代わりに**新ルールへ一貫した接尾辞 `SeriesAbsent`** を与え、
`alerts.yml` のコメントで「どちらが何を拾うか」を対にして書く。

### 判断 3: Grafana 版は `noDataState` を `OK` にする（**既存 6 件の `NoData` を写さない**）

🔴 **これは Prometheus 版には無い、Grafana 版だけの落とし穴である。**

`absent(m)` は **`m` が存在するとき空ベクタを返す**（＝正常時が「データ無し」である）。
Grafana の閾値式は空入力を **NoData** として扱い、`noDataState: NoData` のままだと
**正常時に `DatasourceNoData` が恒常発火する。** `ADR-0076` 理由が名指しした事故そのものになる。

したがって新ルールは `noDataState: OK` とする。**既存 6 件は `NoData` のまま**（あちらは
正常時に値を返す式であり、空になること自体が異常である）。

### 判断 4: `for` と `severity` は既存の `ServiceRequestMetricsAbsent` に揃える（`for: 5m` / `warning`）

**検知までの実時間を正直に書く。** `absent(m)` は瞬間ベクタ選択子の
**既定 5 分の lookback** が空になって初めて真になる。そこへ `for: 5m` を積むので、
**系列が途絶えてから firing までは最大でおよそ 10 分**である。

🔴 **これは NFR-21 の「障害検出 5 分以内」を満たしていない。満たしているように書かない。**
本ルールが拾うのは**サービス障害ではなく「SLO の評価対象が消えた」という統制の欠落**であり、
`ADR-0076` 決定 2 が「統制は評価対象があることまで含む」と定めた側の器である。
障害そのものの 5 分検知は既存の `OtelCollectorDown`（2m）と `HighHttp5xxRate`（5m）が担う。
`for` を短くして早める案は採らない —— `ADR-0076` 理由が「恒常発火が警報を無効化する」を
限定適用の根拠に置いており、**器の目的（再発の検知）に対して 10 分は十分に速い**。

### 判断 5: `absent_over_time()` ではなく `absent()` を使う

`absent_over_time(m[5m])` は `absent(m)` と本件の条件下ではほぼ同値であり、
**窓を明示的に書ける分だけ後者が読みやすい**が、既存 6 ルールが瞬間選択子で書かれているため
**書式を揃える側**を採る。窓（5 分）は `for: 5m` と対でコメントに明記する。

### やらないこと

- **全 SLO へ対で置くこと**（`ADR-0076` §検討した選択肢 ② 案 B。計画が却下している）
- **静的検査器の新設**（`IADR-0345` 決定 5 の繰り延べを `ADR-0076` は覆していない）
- **`/analysis/ask` 系（`RagFirstTokenP95High` / `RagLatencyP95High`）を対象に入れること**
- **`ServiceRequestMetricsAbsent` の改名・式の変更**

## 実測

**環境**: 稼働中の Rancher Desktop k3s、名前空間 `platform-infra`（Prometheus v2.52.0 / Grafana / Alertmanager）。
`kubectl -n platform-infra exec deploy/prometheus -- wget -qO- --post-data='query=…' http://localhost:9090/api/v1/query`
で問い合わせた。**クラスタは #1088 が `PERSIST=1` で作り直した直後であり、着手時は転送（`prometheusremotewrite`）が
有効な状態だった**（`IADR-0345` §F の fail-safe＝debug のみ、ではない）。

### A. 母集合 3 —— `absent` を併設する経路（窓 20 分・刻み 15 秒。2026-09-04 11:16〜11:36 UTC）

**測ったのは「窓の中で `absent(X)` が一度でも真になったか」である。** `absent()` は瞬間ベクタ選択子の
既定 5 分 lookback が空になって初めて真になるので、**これが窓中ずっと偽であることは
「その系列が 5 分以上途切れなかった」と同値**である —— `ADR-0076` 決定 3 の判定基準そのものを直接測っている。

```console
$ … --post-data='query=max_over_time((absent(<式>))[20m:15s])'
```

| 系列 | 結果 | 判定 |
| --- | --- | --- |
| `up{job="otel-collector"}` | **空**（一度も真にならず） | ✅ 対象 |
| `http_server_request_duration_seconds_count`（全 job） | **空** | ✅ 対象 |
| `http_server_request_duration_seconds_bucket{job="microservices-platform.retrieval-service"}` | **空** | ✅ 対象 |
| `rag_answer_first_token_duration_seconds_bucket{job="…aianalysis-service"}` | **`1`** | 🔴 **対象外** |
| `http_server_request_duration_seconds_bucket{job="…aianalysis-service",http_route="/analysis/ask"}` | **`1`** | 🔴 **対象外** |

🔴 **陰性の結論に陽性対照を対で置いた**（走査器そのものが働いていることの確認）:

| 対照 | 結果 |
| --- | --- |
| `absent(this_metric_never_existed_1202)`（実在しない） | **`1`**（＝真を返せる） |
| `absent(up)`（実在する） | **空**（＝偽を返せる） |
| `/api/v1/label/__name__/values` の総数 | **95 件**（＝TSDB は空ではない） |

**RAG 側の 2 件は「未実装」ではなく「まだ誰も質問していない」である。** `rag_answer_first_token_*` は
`/api/v1/label/__name__/values` に現れず、`http_route` の実在値は
`/health/live` `/health/ready` `/internal/introspection`（＋AST 側の 2 つ）だけであった。
**常時トラフィックの源は kubelet の liveness / readiness プローブである。**

### B. 無風時間の直接測定（同じ窓）

```console
$ … 'count(min_over_time((sum by (job) (rate(http_server_request_duration_seconds_count[5m])))[20m:15s]) > 0)'
  → 24
$ … 同 '== 0'
  → 2  ＝ microservices-platform.conversion-service / microservices-platform.ingestion-service
$ … 'count(count by (job) (http_server_request_duration_seconds_count))'
  → 26（job の総数）
```

**26 job 中 24 job は、窓のどの時点でも直近 5 分のレートが 0 でなかった。**
残る 2 件（conversion / ingestion）は**窓がクラスタ起動直後を含むため**であり、定常状態の性質ではない。
🔴 **それでも、job 単位で `absent` を置くなら 2 件は候補から外れる** ——
本 PR は job 単位で置いていない（`HttpServerMetricsSeriesAbsent` は全 job の論理和で、
`SearchLatencySeriesAbsent` だけが job を絞る）ので影響しない。

**窓の取り方の注意（陰性の落とし穴）**: 窓を 6 分・17 分へ広げて起動直後を含めると、
`retrieval-service` の系列は **11:00:30〜11:11:30 UTC の間 `absent`＝1** であった
（クラスタ再作成中でサービスが未起動）。**上表の窓はこの時間帯を含まない。**
🔵 **クラスタを作り直している最中に本ルールが鳴るのは「誤報」ではなく正しい**
（そのとき評価対象は本当に無い）。

### C. 新ルールが Prometheus に載ること

`kubectl apply -k deploy/local/observability-persistence` の後、`/api/v1/rules`:

```
OtelCollectorDown              inactive  health=ok  group=platform-availability
ServiceRequestMetricsAbsent    inactive  health=ok  group=platform-availability
HighHttp5xxRate                inactive  health=ok  group=platform-slo
SearchLatencyP95High           inactive  health=ok  group=platform-slo
RagFirstTokenP95High           inactive  health=ok  group=platform-slo
RagLatencyP95High              inactive  health=ok  group=platform-slo
OtelCollectorUpSeriesAbsent    inactive  health=ok  group=platform-slo-evaluation-target
HttpServerMetricsSeriesAbsent  inactive  health=ok  group=platform-slo-evaluation-target
SearchLatencySeriesAbsent      inactive  health=ok  group=platform-slo-evaluation-target
```

**9 件すべて `health=ok`。通常時は全件 `inactive`、Alertmanager `/api/v2/alerts` は 0 件**（受け入れ基準 4）。

### D. 変異試験 —— 系列を人為的に途切れさせる（**サービス Pod は 1 つも止めていない**）

`deploy/local/infra/otel-collector.yaml`（`prometheusremotewrite` を持たない fail-safe 構成）を apply し、
collector だけを `rollout restart` した。**アプリは動いたまま、Prometheus への転送だけが止まる。**

| 時刻（UTC） | 事象 |
| --- | --- |
| 11:37:00 | 転送を切断（configmap 差し替え → collector rollout。`prometheusremotewrite` の出現数 4 → 0 を確認） |
| 11:37:13 | `up{job="otel-collector"}` = **1**（scrape は生きている＝陰性対照の前提） |
| 11:44:27 | `HttpServerMetricsSeriesAbsent` / `SearchLatencySeriesAbsent` が **pending**、`OtelCollectorUpSeriesAbsent` は inactive |
| 11:46:19 | `SearchLatencySeriesAbsent` **firing**（Alertmanager `startsAt`）＝切断から **9 分 19 秒** |
| 11:46:49 | `HttpServerMetricsSeriesAbsent` **firing** ＝切断から **9 分 49 秒** |
| 11:50:05 | Prometheus で 2 件 `firing`、Alertmanager `/api/v2/alerts` に **`state: active` で 2 件**（`severity: warning`） |
| 11:50:24 | 転送を復旧（forward 構成を apply → collector rollout） |
| 11:57:38 | **9 件すべて `inactive`・Alertmanager 0 件・job 数 26 に回復** |

**検知に要した実時間は 9 分台であり、判断 4 が予告した「最大およそ 10 分」と一致した。**

🔴 **同じ窓で、既存ルールは 1 件も発火しなかった。**

| ルール | 11:50:05 の状態 |
| --- | --- |
| `ServiceRequestMetricsAbsent` | **inactive**（系列が消えると `rate()` が空になり、式全体が空になる） |
| `HighHttp5xxRate` | inactive（分母が空） |
| `SearchLatencyP95High` | inactive（`histogram_quantile` の入力が空） |
| `OtelCollectorDown` | inactive（collector は動いている） |

**`ADR-0076` §統制と現在の実現手段 が「一度も存在しなかった系列は拾えない」と書いた穴が、実在することの実測である。**
本 PR の 3 件だけがこの状態を鳴らした。

**陰性対照（同じ変異の中で鳴ってはいけないもの）**: `OtelCollectorUpSeriesAbsent` は
**最後まで `inactive`** であった —— `up` 系列は Prometheus の scrape が作るので転送を切っても残る。
**「変異を入れれば何でも鳴る」わけではないことを、同じ試験の中で示している。**

### E. Grafana が provisioning を受理すること（9 件）と、`noDataState: OK` が効くこと

```console
$ POST /api/admin/provisioning/alerting/reload → {"message":"Alerting config reloaded"}
$ GET  /api/v1/provisioning/alert-rules        → 9 件
```

`noDataState` の実配備値: 既存 6 件は `NoData` / 新 3 件は **`OK`**。

🔴 **判断 3 の落とし穴が実在することを、Grafana 自身の評価状態で確かめた**
（`/api/prometheus/grafana/api/v1/rules`・通常時）:

```
OtelCollectorDown              alerts=1 [NoData]
ServiceRequestMetricsAbsent    alerts=1 [NoData]
HighHttp5xxRate                alerts=1 [Normal]
SearchLatencyP95High           alerts=1 [Normal]
RagFirstTokenP95High           alerts=1 [NoData]
RagLatencyP95High              alerts=1 [NoData]
OtelCollectorUpSeriesAbsent    alerts=1 [Normal (NoData)]   ← noDataState: OK が効いている
HttpServerMetricsSeriesAbsent  alerts=1 [Normal (NoData)]
SearchLatencySeriesAbsent      alerts=1 [Normal (NoData)]
```

**新 3 件は「NoData を受け取って Normal へ写した」状態である。**
`noDataState` を既存と揃えて `NoData` にしていたら、**正常時に上 4 件と同じ `NoData` 状態へ入っていた。**

### F. クラスタを元の構成へ戻したこと

- `otel-collector`: **転送有効（`prometheusremotewrite` 4 か所）へ復旧**し、rollout 完了を確認。
  🔴 **戻した先は `IADR-0345` §F の fail-safe（debug のみ）ではなく、「着手時に見つけた構成」である** ——
  クラスタは #1088 が `PERSIST=1` で立てた直後であり、転送は最初から有効だった。
  **他エージェントの作業状態を勝手に落とさない**方を採った。
- `prometheus`: 9 ルールが `health=ok` / 全件 `inactive`。一時ルール群は**入れていない**（本番ルールで測った）。
- `alertmanager`: `/api/v2/alerts` 0 件。
- **アプリの Pod は 1 つも再起動していない。**

🔴 **着手時に 1 つ壊して直した。記録に残す。** `kubectl apply -f deploy/local/observability/prometheus.yaml` を
**単独で**当てたため、`observability-persistence` オーバーレイが足していた **PVC のマウントが外れ**、
Prometheus が再起動した。`kubectl apply -k deploy/local/observability-persistence` で復旧したが、
**TSDB に約 2 分の欠落が生じた**（上の窓はこの時間帯より後である）。
**永続化オーバーレイが当たっているクラスタへ base を直接 apply しない。**

## 受け入れ基準（#1202 の Given-When-Then をそのまま持つ）

1. 各 `job` / `http_route` の無風時間を実測し、「5 分より短い」経路の一覧が本書にある（クエリと出力つき）
2. その一覧に `/analysis/ask` 系が含まれていない
3. 対象経路の系列が存在しない状態で `absent` ルールが `firing` へ到達する（稼働クラスタで実測）
4. 系列が正常に流れている状態で `absent` ルールは `inactive` のままである
5. `node scripts/check-grafana-alerting.js` と `node scripts/check-grafana-provisioning-parity.js` が成功する
6. `docs/operations/operations.md` の SLO 表に `absent` ルールが載り、「評価対象が無いことを検知する手段が無い」という記述が同時に是正されている
7. IADR に「どの経路を対象にしたか・判定に使った実測値」が残っている

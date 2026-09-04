---
title: IADR-0370 SLO の評価対象の不在は系列ごとに 1 件の absent ルールで拾い、対象は稼働クラスタの無風時間で決める
type: impl-adr
status: Accepted
related_ids:
  - NFR-21
  - NFR-01
  - NFR-02
  - ADR-0006
  - ADR-0076
  - IADR-0345
  - IADR-0354
  - IADR-0165
  - IADR-0168
  - IADR-0304
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md (決定 2・3・4)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR-21)
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
---

# IADR-0370: SLO の評価対象の不在を `absent` で拾う（#1202）

- 状態: Accepted
- 日付: 2026-09-04
- 決定者: claude（実装）

## 起点・関連

- 計画 ADR **ADR-0076** 決定 3（常時トラフィックがある経路の SLO は、系列の不在そのものを warning とする）
- 環流 **planning#524** / 計画 PR **planning#531**
- 実装 issue **#1202**（前段は #1110 ＝ 是正 PR #1165・MERGED）
- 先行: **IADR-0345**（参照先を稼働 TSDB の実在へ揃えた。決定 5 で静的検査を 2 回目まで繰り延べた）／
  **IADR-0354**（TTFT 計器と `RagFirstTokenP95High`）／**IADR-0165**（Grafana 暫定アラート）／
  **IADR-0168**（Grafana provisioning の経路間パリティ）

## コンテキストと課題

**既存の `ServiceRequestMetricsAbsent` は、名前に反して `absent` を使っていない。**

```promql
sum by (job) (rate(http_server_request_duration_seconds_count[5m])) == 0
and on (job) (sum by (job) (http_server_request_duration_seconds_count offset 15m) > 0)
```

🔴 **系列そのものが消えると `rate()` が空になり、式全体が空になって発火しない。**
拾えるのは「**直近まで受信していたのに途絶した**」場合だけである。
**2026-08-30 まで 4 ルールが一度も存在しないメトリクス名を見ていた事故（#1110）は、この形では検知できない。**

`ADR-0076` 決定 3 は、`IADR-0345` 決定 5 が繰り延べた静的検査の穴を**稼働環境の側から**塞ぐよう定めた。
ただし裁定は判定基準しか置いておらず、**どの経路が該当するかの一覧は計画側で作っていない**
（同 §残るもの）。**対象の確定は実装側の運用設計に残っている。**

## 決定

### 決定 1: 対象は「稼働クラスタで無風時間が 5 分より短いと実測できた系列」に限る

`ADR-0076` 決定 3 の判定基準をそのまま適用し、**リポジトリの中だけでは決めない。**
実測とその一覧は §実測 A に置く。**`/analysis/ask` 系（`RagFirstTokenP95High` / `RagLatencyP95High`）は対象外**とする
—— 呼ばれない限り系列を持たず、無風が 5 分を超え得る（§実測 A の陰性側）。
決定 4 の合成監視が着地してから対象へ入れる。

### 決定 2: 併設するのは「SLO ルールが参照する**系列**」ごとに 1 件であり、ルールごとの対ではない

`ServiceRequestMetricsAbsent` と `HighHttp5xxRate` は**同じ系列**
（`http_server_request_duration_seconds_count`）を見ている。ルール 1 件ごとに対を置くと
**同じ不在で 2 通鳴る。** 重複通知は「片方は既知の誤報だ」という運用習慣を生み、
**本物の通知を握り潰す方向に働く**（`ADR-0076` 理由が限定適用の根拠に置いたのと同じ機序）。

したがって 3 件を置く。

| 新ルール | 式 | 支える既存 SLO |
| --- | --- | --- |
| `OtelCollectorUpSeriesAbsent` | `absent(up{job="otel-collector"})` | `OtelCollectorDown` |
| `HttpServerMetricsSeriesAbsent` | `absent(http_server_request_duration_seconds_count)` | `ServiceRequestMetricsAbsent` / `HighHttp5xxRate` |
| `SearchLatencySeriesAbsent` | `absent(http_server_request_duration_seconds_bucket{job="microservices-platform.retrieval-service"})` | `SearchLatencyP95High` |

### 決定 3: 名前は接尾辞 `…SeriesAbsent` で統一し、既存の `ServiceRequestMetricsAbsent` は**改名しない**

読み分けは名前ではなく `alerts.yml` のコメントが担う（「途絶」と「不在」の対を並べて書いた）。
**改名しない理由**: この名前は `IADR-0345` の実測記録・`docs/operations/operations.md`・
`scripts/` の自己試験・確定済みの作業仕様書 4 本に書かれており、**改名は本 PR の射程を大きく超える。**
（凍結記録は書き換えられないため、改名すると**記録と稼働の名前が永久にずれる。**）

### 決定 4: Grafana 版は新 3 件だけ `noDataState: OK` にする

🔴 **Prometheus 版には無い、Grafana 版だけの落とし穴である。**

`absent(m)` は **`m` が存在するとき空ベクタを返す** —— **正常時が「データ無し」**である。
Grafana の閾値式は空入力を **NoData** として扱うため、既存 6 件と同じ `noDataState: NoData` にすると
**正常時に `DatasourceNoData` が恒常発火する。** `ADR-0076` 理由が「警報を無効化する」と名指しした型そのものになる。

**既存 6 件は `NoData` のまま**である（あちらは正常時に値を返す式であり、空になること自体が異常を意味する）。
**一律に揃えない**ことが正しい。

### 決定 5: `for: 5m` / `severity: warning`。**検知は最大およそ 10 分であることを、満たしているように書かない**

`for` と `severity` は既存の `ServiceRequestMetricsAbsent` に揃える。

🔴 **ただし実時間は 5 分ではない。** `absent()` は瞬間ベクタ選択子の**既定 5 分 lookback**が
空になって初めて真になり、そこへ `for: 5m` が積まる。**系列途絶から firing までは最大およそ 10 分**である。

**これは NFR-21 の「障害検出 5 分以内」を満たしていない。** 本ルールが拾うのは
**サービス障害ではなく「SLO の評価対象が消えた」という統制の欠落**であり、
`ADR-0076` 決定 2 が「統制は評価対象があることまで含む」と定めた側の器である。
障害そのものの 5 分検知は `OtelCollectorDown`（2m）と `HighHttp5xxRate`（5m）が担う。

`for` を短くして早める案は採らない —— `ADR-0076` 理由が「恒常発火が警報を無効化する」を
限定適用の根拠に置いており、**器の目的（再発の検知）に対して 10 分は十分に速い。**
**「5 分以内」と書けるように `for` を削る**のは、`ADR-0076` 決定 2 が禁じた
「動いていない統制を、動いているかのように読める書き方」に当たる。

### 決定 6: `absent_over_time()` ではなく `absent()` を使う

本件の条件下では `absent_over_time(m[5m])` とほぼ同値である。既存 6 ルールが瞬間選択子で書かれているため
**書式を揃える側**を採り、窓（5 分）は `for: 5m` と対でコメントに明記した。

### 決定 7: 静的検査器は**新設しない**（`IADR-0345` 決定 5 を覆さない）

`ADR-0076` 決定 3 の 🔴 が明示している。**稼働側と静的検査は検知できる時点が違う** ——
稼働側は壊れてから最大 10 分で、静的検査は変更を出す時点で気づく。**代替関係にない。**

## 実測（2026-09-04・稼働中の Rancher Desktop k3s `platform-infra`）

**陰性の結論には必ず陽性対照を対で置いた。**

### A. 対象の確定 —— 窓 20 分（11:16〜11:36 UTC）・刻み 15 秒

測ったのは **`max_over_time((absent(<式>))[20m:15s])`**、すなわち「窓の中で `absent` が一度でも真になったか」である。
`absent()` は瞬間ベクタ選択子の既定 5 分 lookback が空になって初めて真になるので、
**窓中ずっと偽であることは「その系列が 5 分以上途切れなかった」と同値**であり、
`ADR-0076` 決定 3 の判定基準を直接測っている。

| 系列 | 結果 | 判定 |
| --- | --- | --- |
| `up{job="otel-collector"}` | **空** | ✅ 対象 |
| `http_server_request_duration_seconds_count`（全 job） | **空** | ✅ 対象 |
| `…_bucket{job="microservices-platform.retrieval-service"}` | **空** | ✅ 対象 |
| `rag_answer_first_token_duration_seconds_bucket{job="…aianalysis-service"}` | **`1`** | 🔴 対象外 |
| `…_bucket{job="…aianalysis-service",http_route="/analysis/ask"}` | **`1`** | 🔴 対象外 |

**走査器の陽性対照**: `absent(this_metric_never_existed_1202)` → **`1`**（真を返せる）／
`absent(up)` → **空**（偽を返せる）／`/api/v1/label/__name__/values` の総数 **95 件**（TSDB は空ではない）。

**常時トラフィックの源は kubelet の liveness / readiness プローブである** ——
`http_route` の実在値は `/health/live` `/health/ready` `/internal/introspection`（＋AST 側 2 つ）だけで、
`/analysis/ask` は**現れなかった**。`rag_answer_first_token_*` も名前一覧に**現れなかった**。
🔴 **これは「未実装」ではなく「まだ誰も質問していない」である。**

**無風時間の直接測定**（同じ窓）: `min_over_time((sum by (job) (rate(…[5m])))[20m:15s])` が
**`> 0` の job が 24 / `== 0` が 2**（`conversion-service` / `ingestion-service`。**窓がクラスタ起動直後を含むため**）。
job の総数は **26**。**job 単位で `absent` を置くならこの 2 件は候補から外れる** ——
本 PR は `SearchLatencySeriesAbsent` だけが job を絞っており、この 2 件は含まない。

🔴 **窓の取り方で結論が変わる。** 窓を起動直後まで広げると `retrieval-service` の系列は
**11:00:30〜11:11:30 UTC の間 `absent`＝1** であった（サービスが未起動）。
**クラスタ再作成中に本ルールが鳴るのは誤報ではない**（そのとき評価対象は本当に無い）。

### B. ルールが載ること（陰性 ＝ 通常時は鳴らない）

`/api/v1/rules` が **9 件すべて `health=ok` / 全件 `inactive`**、Alertmanager `/api/v2/alerts` は **0 件**。

### C. 変異試験 —— 系列を人為的に途切れさせる（**サービス Pod は 1 つも止めていない**）

`prometheusremotewrite` を持たない fail-safe 構成（`deploy/local/infra/otel-collector.yaml`）を apply し、
**collector だけを rollout restart した。アプリは動いたまま、Prometheus への転送だけが止まる。**

| 時刻（UTC） | 事象 |
| --- | --- |
| 11:37:00 | 転送を切断（configmap の `prometheusremotewrite` 出現数 4 → 0） |
| 11:37:13 | `up{job="otel-collector"}` = **1**（scrape は生きている＝陰性対照の前提） |
| 11:44:27 | 新 2 件が **pending**、`OtelCollectorUpSeriesAbsent` は inactive |
| 11:46:19 | `SearchLatencySeriesAbsent` **firing**（`startsAt`）＝切断から **9 分 19 秒** |
| 11:46:49 | `HttpServerMetricsSeriesAbsent` **firing** ＝切断から **9 分 49 秒** |
| 11:50:05 | Alertmanager `/api/v2/alerts` に **`state: active` で 2 件**（`severity: warning`） |
| 11:50:24 | 転送を復旧 |
| 11:57:38 | **9 件すべて `inactive`・Alertmanager 0 件・job 数 26 に回復** |

**実測 9 分台は、決定 5 が予告した「最大およそ 10 分」と一致した。**

🔴 **同じ窓で、既存ルールは 1 件も発火しなかった。**
`ServiceRequestMetricsAbsent`（系列が消えて `rate()` が空 → 式全体が空）・`HighHttp5xxRate`（分母が空）・
`SearchLatencyP95High`（`histogram_quantile` の入力が空）はすべて **inactive** のままである。
**`ADR-0076` が書いた穴が実在することの実測であり、本 PR の 3 件だけがこの状態を鳴らした。**

**陰性対照**: `OtelCollectorUpSeriesAbsent` は**最後まで inactive**（`up` は scrape が作るので残る）。
**「変異を入れれば何でも鳴る」わけではないことを、同じ試験の中で示している。**

### D. Grafana —— 受理（9 件）と、決定 4 が効いていること

`POST /api/admin/provisioning/alerting/reload` → `{"message":"Alerting config reloaded"}`、
`GET /api/v1/provisioning/alert-rules` → **9 件**（`slo-alerts.yaml` の冒頭が求めていた確認。
`IADR-0345` §E が 5 件で閉じた未決事項の、件数だけを引き直したもの）。

`/api/prometheus/grafana/api/v1/rules`（通常時）:

```
OtelCollectorDown              alerts=1 [NoData]
ServiceRequestMetricsAbsent    alerts=1 [NoData]
HighHttp5xxRate                alerts=1 [Normal]
SearchLatencyP95High           alerts=1 [Normal]
RagFirstTokenP95High           alerts=1 [NoData]
RagLatencyP95High              alerts=1 [NoData]
OtelCollectorUpSeriesAbsent    alerts=1 [Normal (NoData)]   ← noDataState: OK
HttpServerMetricsSeriesAbsent  alerts=1 [Normal (NoData)]
SearchLatencySeriesAbsent      alerts=1 [Normal (NoData)]
```

**新 3 件は「NoData を受け取って Normal へ写した」状態である。**
`noDataState` を既存と揃えていたら、**正常時に上 4 件と同じ `NoData` 状態へ入っていた。**
**決定 4 が回避した恒常発火は、机上の懸念ではない。**

### E. クラスタを元の構成へ戻したこと

- `otel-collector`: **転送有効へ復旧**（rollout 完了を確認）。
  🔴 **戻した先は `IADR-0345` §F の fail-safe（debug のみ）ではなく「着手時に見つけた構成」である** ——
  クラスタは #1088 が `PERSIST=1` で立てた直後で、転送は最初から有効だった。
  **他エージェントの作業状態を勝手に落とさない**方を採った。
- `prometheus`: 9 ルール `health=ok` / 全件 `inactive`。**一時ルール群は入れていない**（本番ルールで測った）。
- `alertmanager`: 0 件。**アプリの Pod は 1 つも再起動していない。**

🔴 **着手時に 1 つ壊して直した。** `kubectl apply -f deploy/local/observability/prometheus.yaml` を**単独で**当てたため、
`observability-persistence` オーバーレイが足していた **PVC のマウントが外れ**、Prometheus が再起動した。
`kubectl apply -k deploy/local/observability-persistence` で復旧したが、**TSDB に約 2 分の欠落が生じた**。
**永続化オーバーレイが当たっているクラスタへ base を直接 apply しない。**

## 結果

- `ADR-0076` §統制と現在の実現手段 の「評価対象が無いことを検知する 🔴 **無い**」が埋まる
  （**ただし対象経路に限る**。`/analysis/ask` 系は埋まらない）。
- 変更は配備設定と文書・検査器のコメントに閉じており、アプリケーションコードの変更は無い。
- **合成監視（`ADR-0076` 決定 4）は未着手のまま残る。** `/analysis/ask` 系を対象へ入れるにはそれが要る。
- **静的検査（CI で全ルールの非空ベクタを確かめる）も残る。** `IADR-0345` 決定 5 の繰り延べのままである。

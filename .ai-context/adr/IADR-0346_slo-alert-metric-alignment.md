---
title: IADR-0346 SLO アラートの参照先を稼働 TSDB の実在に合わせ、束ねの軸も同時に移す
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - NFR-21
  - ADR-0006
  - IADR-0130
  - IADR-0164
  - IADR-0165
  - IADR-0304
  - IADR-0322
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR-21)
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md
---

# IADR-0346: SLO アラート 5 件のうち 4 件が発火し得なかった件の是正（#1110）

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: claude（実装）

## 起点・関連

- 非機能要求 **NFR-21**（障害検出 5 分以内 / MTTR 30 分以内）
- 計画 ADR **ADR-0006**（可観測性スタック。アラートは Alertmanager を用いる）
- 実装 issue **#1110**（出所は #1112 ＝ #1090 / #546 の実測）
- 先行: **IADR-0304**（Alertmanager 配備・`default-null`）／**IADR-0165**（Grafana 暫定アラート）／
  **IADR-0322**（転送構成の自己テレメトリ・検査器の 2 回目判断）

## コンテキストと課題

**「アラートが設定されている」ことと「アラートが発火しうる」ことは別である。**

SLO ルールは 5 件ある。うち 4 件（`OtelCollectorDown` 以外）は、**Prometheus に一度も存在したことのない**
メトリクス名 `http_server_duration_milliseconds_*` を参照していた。式は PromQL として構文上正当なので
Prometheus はエラーを出さず、ルールは `health: "ok"` / `state: "inactive"` のまま静かに評価され続ける。
**評価されていることは、評価対象があることを意味しない。**

🔴 **計画の前提が 1 つ崩れている。** NFR-21 の 2026-08-08 注記と ADR-0006 の同日追記は、どちらも
**「アラートルールは既に評価されており、欠けているのは通知の配線だけである」**と書いている。
本件の実測はこれを否定する —— 配線を全部やっても、その 4 件は永久に発火しない。
（計画側の是正は実装リポジトリでは行わない。planning への環流 issue で扱う。）

ずれは 4 種類あり、いずれも「名前の誤り」であって未実装でも転送漏れでもない。

| 軸 | 誤り | 実在 |
| --- | --- | --- |
| 名前 | `http.server.duration`（旧 HTTP セマンティック規約） | `http.server.request.duration`（安定版） |
| 単位 | ミリ秒（しきい値 `1500` / `5000`） | **秒**（`1.5` / `5`） |
| ラベル軸 | `service_name` | **`job`** |
| ラベル名 | `http_status_code` | `http_response_status_code` |

計器は `AddAspNetCoreInstrumentation()` で全サービスに入っており、転送を有効にすれば
`http_server_request_duration_seconds_count` が 26 の `job` から届く。
ラベル軸がずれるのは `prometheusremotewrite` がリソース属性 `service.namespace`+`service.name` を
**`job`** へ、`service.instance.id` を `instance` へ写すためで、アプリのメトリクスに `service_name` は**付かない**。

## 決定

### 決定 1: 4 ルールの参照先を、稼働 TSDB に実在する名前・ラベル・単位へ揃える

Prometheus 版（`deploy/prometheus/alerts.yml`）と経路B の inline（`deploy/local/observability/prometheus.yaml`）、
Grafana 版（`deploy/grafana/provisioning/alerting/slo-alerts.yaml`）と同 inline を**同時に**直す。

### 決定 2: Alertmanager の束ね（`group_by`）と抑止（`inhibit_rules.equal`）も同じ軸へ同時に移す

🔴 **アラート側だけ直すと、新しい壊れ方を作る。** `group_by: ['alertname', 'service_name']` のままだと、
アラートに存在しないラベルで束ねることになり、**全サービスが 1 群へ潰れる**。抑止（critical が warning を
抑止する）も同一性判定に失敗する。**実測で確認した（§実測 D）** —— `job` なら 54 群、`service_name` なら 4 群。

### 決定 3: しきい値の置き場所の分担は変えない

Prometheus 版は `expr` の中に、Grafana 版は `conditions[].evaluator.params` に持つ。
`check-grafana-alerting.js` はルール名の 1 対 1 しか見ないのでどちらでも通るが、
**通るからこそ勝手に動かさない**（#665 が選んだ形である）。単位が秒であることを両方のコメントに明記した。

### 決定 4: ダッシュボードも同時に直す（**単位表記を含む**）

パネル 1〜4 は同じ誤った名前を使っており、5xx 率・p99・RAG レイテンシは**空のグラフ**だった。
アラートだけ直すと、**運用者が空のグラフを見て「異常なし」と記録する**状態が残る。
パネル題の `(ms)` から `(s)` も直す —— **単位表示を直さないと 0.005 を 5 ミリ秒と読む**（1000 倍ずれて見える）。

### 決定 5: 退行防止 —— **1 回目なので検査器を新設しない。2 回目に何を作るかを書き残す**

`CLAUDE.md`「検査器・規約の追加は同型の事故が 2 回起きたら」に従う。
**本件は「ルールの式が参照するメトリクスが稼働 TSDB に実在しない」という事故の 1 回目である。**

**IADR-0322 が新設した `check-collector-self-telemetry.js` の同型ではない。**
あちらは「**同じ宣言が複数の配備設定の間で食い違う**」＝**静的な自己整合**の欠落であり、
リポジトリ内の 3 ファイルを突き合わせれば検出できる。
本件は「**式が参照する名前が、稼働中の TSDB に存在するか**」＝**リポジトリの外の事実**であり、
**静的検査では原理的に判定できない** —— メトリクス名はアプリのコードにも設定にも文字列として現れない。
OTel の計器名 `http.server.request.duration` は**ライブラリの内部**にあり、Prometheus 名
`http_server_request_duration_seconds_count` は **exporter の変換規則**が作るからである。

🔴 **2 回目が起きたら作るもの（次の担当が判断をやり直さずに済むように書き残す）**:
`integration-stack.yml` に**転送を有効にした collector と 1 サービス**を立て、
`alerts.yml` の全ルールについて **`/api/v1/query` が非空ベクタを返すこと**を検査するジョブを足す。
**閾値を割るかは見ない**（それは変異試験の仕事である）。**評価対象があるか**だけを見る。
1 回目の時点でこれを作らないのは、`integration-stack.yml` が既にそれだけで数分かかる規模であり、
1 回目の事故に見合わないからである。

### 決定 6: 代わりに「式を変えるときの検証手順」を配備ファイルの冒頭へ人手の規律として書く

**機械検査が置けない以上、人が守る手順として明示する。** `deploy/prometheus/alerts.yml` の冒頭を正本とし、
稼働 Prometheus へ `/api/v1/series` で問い合わせること、
🔴 **「0 件だった」を「無い」と読む前に陽性対照を対で置く**ことを書いた。
（`--post-data` は form 符号化であり**式中の `+` が空白へ復号される**。`{__name__=~"otelcol_.+"}` が
0 件で返り、実際に「存在しない」と読み違えかけた。`+` は `%2B` で送る。）

## 実測（2026-09-02・稼働中の Rancher Desktop k3s `platform-infra`）

**陰性の結論には必ず陽性対照を対で置いた。**

### A. 参照先メトリクスの実在

`/api/v1/label/__name__/values` に現れる `http_server*` は **4 つだけ**である ——
`http_server_active_requests` / `http_server_request_duration_seconds_{bucket,count,sum}`。
**`http_server_duration_milliseconds*` は 1 つも無い**（陽性対照: 同じ問い合わせが全体で 92 の名前を返す）。

### B. 5 ルールが評価対象を持つこと（陽性）と、旧式が持たないこと（陰性対照）

| ルール | 是正後の式 | 旧式（陰性対照） |
| --- | --- | --- |
| `OtelCollectorDown` | **1 系列**（値 1） | 0 系列 |
| `ServiceRequestMetricsAbsent` | **26 系列** | 0 系列 |
| `HighHttp5xxRate` | **26 系列** | 0 系列 |
| `SearchLatencyP95High` | **1 系列**（p95 = 0.0085 秒） | 0 系列 |
| `RagLatencyP95High` | **1 系列**（p95 = 0.0628 秒） | 0 系列 |

🔴 `RagLatencyP95High` だけは**着手時 0 系列**であった。`/analysis/ask` が稼働中に一度も呼ばれておらず
（実測の `http_route` は `/health/live` `/health/ready` `/internal/introspection` の 3 つだけ）、
`http_route` ラベルが生えていなかったためである。**人為的に呼んで生やした**（50 秒後に系列が出現）。
**これは「未実装」ではなく「まだ呼ばれていない」である。**

### C. 変異試験 —— 閾値を割る条件を作り、Alertmanager `/api/v2/alerts` へ届くこと

**本番の式に最小の変異を 1 つだけ入れた一時ルール群**（`probe-issue-1110`）を Prometheus へ入れ、
SIGHUP で再読込した（TSDB を失わないため再起動はしない）。**変異は「今満たせる形」にするためだけのものである。**

| プローブ | 変異 | 結果 |
| --- | --- | --- |
| `ProbeServiceRequestMetricsAbsent` | `== 0` を `>= 0` へ（`and on (job)` の結合と `offset 15m` の右辺は実データ） | **firing・26 件到達** |
| `ProbeHighHttp5xxRate` | 状態選択 `5..` を `2..` へ（ラベル名・除算・`by (job)` は不変） | **firing・24〜26 件到達** |
| `ProbeSearchLatencyP95High` | しきい値 `1.5` を `0.001` へ（式は同一） | **firing・1 件到達** |
| `ProbeRagLatencyP95High` | しきい値 `5` を `0.001` へ（式は同一） | **firing・1 件到達** |
| `ProbeOtelCollectorDown` | `== 0` を `== 1` へ（系列選択は同一） | **firing・1 件到達** |
| `ProbeNegativeControlMustNotFire` | 同じ式・しきい値 `> 1000000` | 🔴 **inactive・到達 0 件（陰性対照）** |

Alertmanager `/api/v2/alerts` の合計は **53〜54 件**、陰性対照は**含まれない**。
**5 ルールすべての式の形が Alertmanager まで到達することを実測した。**

🔴 **合成プローブの発火は「本番ルールが発火した」ではない。** ただし本番の
`ServiceRequestMetricsAbsent` は**変異なしで `state=pending` に入り**、実在の `job`
（`microservices-platform.conversion-service` / `ingestion-service`）を伴った。
`for: 5m` を満たす前にヘルスプローブの往来が再開するため `firing` までは行かないが、
**旧式では `pending` にすら入れなかった**（評価対象が 0 だったため）。
本物の 5 分間の途絶を人為的に作るにはサービスを止める必要があり、本作業の許可範囲外としたので**行っていない**。
`OtelCollectorDown` の**本物の発火**は #1112 が collector を 0 台へ落として実測済みである。

### D. 束ねの軸（決定 2 の根拠）

同じ 54 件のアラートに対し、Alertmanager の設定だけを入れ替えて実測した。

- `group_by: ['alertname', 'job']` → **54 群**（キーの形は `(alertname, job)`）
- `group_by: ['alertname', 'service_name']` → 🔴 **4 群**。`ProbeHighHttp5xxRate` の 26 件が**1 群へ潰れた**

**アラート側だけ直していたら、この潰れ方を新しく作っていた。**

### E. Grafana の provisioning が受理されるか（#665 §判断 0 が積み残した未決事項）

🔴 **受理される。未決事項を閉じた。** `/api/v1/provisioning/alert-rules` は **5 件**を
`provenance: "file"` で返す。**さらに、稼働中の Grafana には旧式のまま残っていた**ことも判明した
（Prometheus 側だけ直っていた）。是正版を apply して
`POST /api/admin/provisioning/alerting/reload` した前後で:

| ルール | before | after |
| --- | --- | --- |
| `OtelCollectorDown` | 閾値 `[0]` / 正 | 変化なし |
| `ServiceRequestMetricsAbsent` | 旧名・`service_name` | **是正済み** |
| `HighHttp5xxRate` | 旧名・`http_status_code` | **是正済み** |
| `SearchLatencyP95High` | 閾値 **`[1500]`**（ミリ秒） | **`[1.5]`（秒）** |
| `RagLatencyP95High` | 閾値 **`[5000]`**（ミリ秒） | **`[5]`（秒）** |

### F. クラスタを作業前の fail-safe 構成へ戻したこと

- `otel-collector`: `prometheusremotewrite` を含まない **debug のみ**の fail-safe へ戻し、rollout 完了を確認
- `prometheus`: 一時ルール群 `probe-issue-1110` を撤去（`alert:` は本番 5 件のみ・probe 参照 0 件）
- `alertmanager`: `group_by` / `equal` とも `['alertname', 'job']`（是正後の姿）
- 一時的に張った `/analysis/ask` の port-forward と負荷生成を停止（他エージェントの port-forward は残置）

## 結果

- NFR-21 が実際に担保されるようになった。**4 件は、これまで何が起きても鳴らなかった。**
- 変更は配備設定と文書に閉じており、アプリケーションコードの変更は無い。
- **計画側の「検知は足りている」という前提は残る。** planning への環流で扱う。
- **`RagLatencyP95High` は、`/analysis/ask` が呼ばれない限り評価対象を持たない。** これは仕様であり
  バグではないが、**「鳴らない」と「鳴りようがない」の区別が付かない**点は残る。
  無風時に区別したいなら別途 blackbox 的な合成監視が要る（本 PR の射程外）。

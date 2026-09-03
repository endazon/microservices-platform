---
title: IADR-0365 初回応答は SSE の最初の token で測り、応答完了のルールは名前を保ったまま「傾向の観察」へ降ろす
type: impl-adr
status: Accepted
related_ids:
  - NFR-02
  - NFR-21
  - FR-04
  - UC-01
  - SC-01
  - ADR-0006
  - ADR-0076
  - IADR-0037
  - IADR-0110
  - IADR-0212
  - IADR-0244
  - IADR-0345
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md (決定 1・5)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR-02 / NFR-21)
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
---

# IADR-0365: `/analysis/ask/stream` の初回トークンまでの時間（TTFT）計器の新設（#1204）

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: claude（実装）

## 起点・関連

- 非機能要求 **NFR-02**（RAG 回答の初回応答 p95 5 秒以下。単位は秒）／**NFR-21**（障害検出 5 分以内）
- 計画 ADR **ADR-0076** 決定 5（SLI の定義は変えず、初回トークンまでの時間を測る計器を新設する。
  測定対象は SC-01 が実際に使う `/analysis/ask/stream`。**計器が無いことを理由に要求の側を下げない**）
  ／同 決定 1（単位は秒。OTel 安定版 HTTP セマンティック規約に従う）
- 実装 issue **#1204**（環流 planning#524 の裁定 2026-09-03 の受け皿）
- 先行: **IADR-0345**（#1110。SLO アラート 5 件のうち 4 件が稼働 TSDB に無い名前を見ていた件の是正）
  ／**IADR-0037**（SSE）／**IADR-0212**（ヒストグラムのバケット設計）／**IADR-0110**（属性の値域を閉じる規律）

## コンテキストと課題

**`RagLatencyP95High` は、名前と中身が食い違っていた。**

注釈は「RAG 初回応答 p95（NFR: 5s）」と称するが、式は
`http_server_request_duration_seconds_bucket{http_route="/analysis/ask"}` の p95 ＝
**サーバー応答が完了するまでの所要時間**である。しかも `http_route` は**一括経路** `/analysis/ask` であり、
**SC-01 が実際に使う SSE 経路 `/analysis/ask/stream` ではない。**

`IADR-0345` は名前・単位・ラベル軸の 4 種のずれを直したが、**「何を測るか」のずれは残っていた** ——
あれは実在する計器へ式を合わせる作業であり、**計器そのものが SLI と違うことは射程外だった。**

`ADR-0076` は SLI を「応答完了 p95」へ改める案を却下している。
**長い回答ほど SLO 違反になり、回答品質を上げると SLO が悪化する** —— 逆向きの誘因を持つ指標は壊れている。

### 実測（着手前・`develop` `45853885`）

**TTFT の計器は 1 件も無い。陰性の結論なので陽性対照を対で置いた。**

```console
$ grep -rn "CreateHistogram" --include=*.cs src/ | grep -v "/Tests/"
src/platform/backend/Services/LlmGateway/Common/Observability/LlmCompletionMetrics.cs:77
```

**1 件返る。**ヒストグラム計器そのものはリポジトリに実在し、同じ走査器に掛かる。
したがって「TTFT の計器が 0 件」は走査の不備ではない。

## 決定

### 決定 1: 計器は `rag.answer.first_token.duration`（`Histogram<double>`・単位 `s`）

Meter 名はサービス名 `microservices-platform.aianalysis-service` と一致させる（既存 5 種の Meter が例外なくそう）。
計器名は既存の `<領域>.<主語>.<測るもの>` と、OTel 安定版 semconv の `<name>.duration` の両方に揃う形を採った。

🔴 **単位は秒である。** ミリ秒の計器を選ぶと **1000 倍ずれた閾値が静かに成立する** ——
#1110 がまさにそれであった。`ADR-0076` 決定 1 が計画側で単位を固定したのはこの事故が理由である。

バケット境界は `0.1 / 0.25 / 0.5 / 1 / 2 / 3 / 5 / 8 / 13 / 21`（秒）。
**境界に SLO のしきい値 5 を必ず置く** —— 境界に無いと `histogram_quantile` が隣の境界へ内挿し、
しきい値の前後で判定が滑る。上側は「どれだけ遅いか」が読めるだけの粗さで足りる。

### 決定 2: 起点はハンドラ入口、終点は**最初の `token` フレームをフラッシュし終えた時刻**

`ADR-0076` 決定 5 は「初回トークンまでの時間」としか書いていないため、実装側で両端を確定させる。

- **起点**: `/analysis/ask/stream` のハンドラ入口の `Stopwatch.GetTimestamp()`。
  ミドルウェア（相関 ID・認証）通過後である。その差分は本計器に含まれないが、
  同じ経路を丸ごと測る `http_server_request_duration_seconds` との差として観測できる。
- **終点**: 最初の `event: token` フレームを応答本文へ書き、`FlushAsync` が完了した時刻。
  **バイトがサーバを出た瞬間**であり、「利用者に最初の文字が届き得る」時刻に最も近い。

🔴 **`event: citations` では止めない。** 出典は本文のトークンではなく、**LLM 生成が始まる前に確定する**。
そこで止めると「生成前の時刻」を初回応答として記録することになり、**指標が常に速く見える。**

### 決定 3: `token` が 1 件も出なかったストリームは**記録しない**

`error` のみ・途中終端・取り消しでは記録しない。記録は 1 ストリームにつき高々 1 回である。

**0 を積んではならない。**「初回トークンが無かった」が「速かった」として分布の最下段へ入り、
**p95 が下振れする** ＝ SLO 違反を取りこぼす。`IADR-0212` 決定 3 が出力トークン数について
同じ判断をしている（未送信の経路では Histogram を記録しない）。**同じ理由である。**

### 決定 4: 属性は `ai.purpose` の 1 軸のみ。**モデル名は載せない**

値域は `rag-answer` / `other` に閉じ、未知値は `other` へ集約する（`IADR-0110` の規律）。
`llm_completion_total{llm_purpose=...}` と同じ値を採るので、両者を同じ軸で読める。

🔴 **モデル名を載せない理由は「基数」ではなく「まだ確定していない」である。**
使用モデルは `done` イベントで初めて分かり、**初回トークンの時点では未確定**である。
未確定のものを `none` として載せると系列が実質 1 本になるだけでなく、**「モデル別 TTFT」と誤読される。**

**プロンプト・質問文・検索語・利用者識別子は載せない**（非有界。カーディナリティが爆発する）。

### 決定 5: 🔴 新ルール `RagFirstTokenP95High` を足し、`RagLatencyP95High` は**式を据え置いて注釈だけ是正する**

**`RagLatencyP95High` の式を TTFT へ付け替えない。** 理由は 2 つある。

1. **`ADR-0076` は応答完了 p95 を残す前提で書かれている。** §統制と現在の実現手段 が
   「応答完了の p95 を代理値として読む。ただし長い回答ほど悪化するため、**SLO 判定には用いない
   （傾向の観察に留める）**」と定めている。式を付け替えると一括経路 `/analysis/ask` の観測が消え、
   **ADR が残すと書いたものを実装が消すことになる。**
2. 🔴 **同じ名前が黙って別のものを指すようになる。** `RagLatencyP95High` は Alertmanager の履歴と
   `IADR-0345` の実測表（p95 = 0.0628 秒）に既に現れている。名前を保ったまま中身を替えると、
   **過去の記録がすべて別の意味に化ける。名前と中身の食い違いこそ、本 issue が是正している当のものである。**

したがって:

| ルール | 扱い |
| --- | --- |
| `RagFirstTokenP95High`（新規） | TTFT p95 > 5 秒が 10 分・warning。🔴 **NFR-02 の SLO 判定はこれで行う** |
| `RagLatencyP95High`（既存） | **式・しきい値・severity を据え置く。**注釈から「初回応答」を削り、応答完了であること・判定に用いないことを明記する |

**severity を下げない。** `warning` のまま残す —— 一括経路の応答完了が 5 秒を超えることは
SLO の判定材料でなくとも運用上の異常であり、`severity` を動かすと Alertmanager の束ね・抑止の挙動が変わる
（`IADR-0345` 決定 2 が軸を `job` へ揃えた直後であり、**同じ PR で 2 つ動かさない**）。

ルール数は **5 → 6**。`IADR-0345` 決定 1 と同じく **4 ファイル**（Prometheus の実体と経路 B の inline、
Grafana provisioning の実体と inline）へ同時に足す。ダッシュボードも 2 ファイルへ同時に足す ——
**アラートだけ直すと、運用者が空のグラフを見て「異常なし」と記録する**（#1110 の教訓）。

### 決定 6: 退行防止は試験で持ち、検査器は新設しない

`MeterListener` でプロセス内の測定を購読して固定する。**購読は Meter の「インスタンス」で絞る** ——
`IMeterFactory` は容器ごとに別の `Meter` を作るので、自分の factory から解決したものだけを拾えば、
xUnit がテストクラスを並列に走らせても他クラスの測定が混入しない
（`CompletionMetricsTests` は `Collection` で直列化しているが、**そこまで要らない**）。

🔴 **プローブは計器の生成を先に済ませる。** `RagStreamMetrics` は singleton であり、解決するまで
Histogram が存在しない。存在しない計器は `InstrumentPublished` に載らず、
**購読しているつもりで何も見ていない**状態になる（`IADR-0130` の fail-closed と同じ型の罠）。

**検査器（CI で全ルールの非空ベクタを確かめる）は新設しない。** `IADR-0345` 決定 5 が
「同型の事故の 2 回目に作る」と書き残したものであり、**本件は事故ではなく新設である。**

## 実測

### A. 変異試験（決定 6 の裏付け。3 変異とも当たり、戻して残渣 0）

| 変異 | 結果 |
| --- | --- |
| 記録そのものを止める（`if (false && ...)`） | **2 件 FAIL**（1 件は陰性試験なので通る＝正しい） |
| `citations` で止める（終点を出典へ付け替え） | **1 件 FAIL**（`token` 無しでも記録されてしまう試験が落ちる） |
| 単位を `ms` にする | **1 件 FAIL**（単位の試験が落ちる） |

戻したあと `grep -rn "false &&\|unit: \"ms\"" src/.../AiAnalysisService/` は **0 件**（残渣なし）、
`dotnet test`（AiAnalysisService.Tests）は **98 件すべて成功**。

### B. 稼働クラスタでの系列の実在（2026-09-03・Rancher Desktop k3s `platform-infra` / `microservices-platform`）

`IADR-0345` 決定 6 の手順に従い、`prometheusremotewrite` を一時的に有効化して実測し、**fail-safe へ戻した**。
AiAnalysisService は**イメージだけ差し替えた**（`kubectl set image`。他の Pod は再起動していない）。

**B-1. 陰性（配備前）と陽性対照を対で置いた。**

```console
$ kubectl -n platform-infra exec deploy/prometheus -- wget -qO- \
    --post-data='match[]=rag_answer_first_token_duration_seconds_count' http://localhost:9090/api/v1/series
{"status":"success","data":[]}                       ← 陰性: 0 系列

$ ... --post-data='match[]=otelcol_receiver_accepted_metric_points' ...
{"status":"success","data":[{...},{...}]}            ← 🔴 陽性対照: 同じ問い合わせ方で 2 系列
```

**「0 件だった」は問い合わせ方の不備ではない。**

**B-2. 陽性（配備後・`/analysis/ask/stream` を 11 回呼んだ）。**

| 問い合わせ | 結果 |
| --- | --- |
| `match[]={__name__=~"rag_.+"}`（`+` は `%2B` で送る） | **13 系列**（`_bucket` 11 ＋ `_count` ＋ `_sum`） |
| バケット境界（`le` の値） | `0.1 / 0.25 / 0.5 / 1 / 2 / 3 / 5 / 8 / 13 / 21 / +Inf` ＝ **advice がそのまま届いた。境界に 5 がある** |
| ラベル | `ai_purpose="rag-answer"` / `job="microservices-platform.aianalysis-service"` のみ（**質問文は無い**） |
| `rag_answer_first_token_duration_seconds_count` | **11** ＝ **成功した呼び出し数と完全に一致**（1 ストリーム 1 件） |
| `rag_answer_first_token_duration_seconds_sum` | 0.252796 秒（平均 23 ミリ秒） |
| **アラートの式そのもの** `histogram_quantile(0.95, …)` | **0.1405 秒** → 後の実行で **0.095 秒**。**非空ベクタ**である |

🔴 **Prometheus 側の名前は予測どおり `rag_answer_first_token_duration_seconds_*` であった**
（単位 `s` が `_seconds` サフィックスへ写る）。**予測ではなく実測で確かめた** —— 名前は
exporter の変換規則が作るのであって、アプリのコードにも設定にも文字列として現れないからである（`IADR-0345`）。

🔴 **`count` が 11 で一致したことは、決定 3（token が無ければ記録しない）の裏付けでもある。**
呼び出しは 12 回あり、**1 回は要求本文が壊れて 400 で終わっている**（Git Bash の符号化で日本語が壊れた。
製品の欠陥ではない）。**その 1 回は計上されていない。**

### C. Prometheus と Grafana が 6 ルールを受理すること

```console
$ kubectl -n platform-infra exec deploy/prometheus -- wget -qO- http://localhost:9090/api/v1/rules
OtelCollectorDown / ServiceRequestMetricsAbsent / HighHttp5xxRate /
SearchLatencyP95High / RagFirstTokenP95High / RagLatencyP95High   ← 6 件 health=ok
```

Grafana は `/api/v1/provisioning/alert-rules` が **6 件**返す（`RagFirstTokenP95High` を含む。
`IADR-0165` 決定 1 が残した「配備時に確かめること」を、件数を数え直した上で満たした）。

### D. ダッシュボードのパネルが空でないこと

新しいパネルの式を **Grafana のデータソースプロキシ経由で**引いた（パネルが実際に投げる経路）。

```console
$ ... /api/datasources/proxy/uid/prometheus/api/v1/query?query=histogram_quantile(0.95, …)
{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[…,"0.095"]}]}}
```

🔴 **直前の実行では `NaN` が返った。** トラフィックが止まって 5 分窓の `rate()` が全バケット 0 になったためである。
**これは「速い」ではなく「呼ばれていない」** —— パネルの説明文とアラートのコメントに同じことを書いた。

### E. クラスタを作業前の姿へ戻したこと

- `otel-collector`: `prometheusremotewrite` を含まない **debug のみ**の fail-safe へ戻し、rollout 完了を確認（実測）
- `aianalysis-service`: **イメージ参照を `:latest` へ戻した**（一時タグ `:issue1204` は Deployment に残っていない）。
  🔴 **`:latest` は本 PR のコードを指す**（同じイメージへ付け替えた）—— 戻したのは**参照**であって中身ではない。
  マージ前のコードが dev クラスタで動いている状態は、この PR が着地するまで続く
- `prometheus` / `grafana`: 本 PR のルール・ダッシュボードを apply した状態で残した（内容はリポジトリと一致する）
- 一時的に張った port-forward を停止した

## 検出しないこと（先に書く。次の人が「試験済み」と読まないように）

- 🔴 **呼ばれない限り系列が無い。** ダッシュボードのパネルが空なのは「速い」ではなく
  「**まだ誰も質問していない**」である。`ADR-0076` 決定 3 の `absent` 併設の対象にはできない ——
  無風でいられる時間が検知要件（5 分）を超えるためで、**恒常発火は警報を無効化する。**
  区別を付けるには決定 4 の合成監視が要る。**未配備である。**（#1202 への申し送り）
- 🔴 **権限縮退の中立文言も `token` として計上される。** 閲覧できる文書が無いときの
  「閲覧権限のある文書が見つかりませんでした。」は LLM を経ずに即座に出るため、**速い値として分布へ入る。**
  利用者から見た「最初の文字が出た時刻」としては正しいが、**LLM 経路だけの分布ではない。**
  分けるにはオーケストレータ側の信号が要る（`AskEvent` は出自を持たない）。**位置が特定されている穴として記録する。**
- **ミドルウェアの所要時間は含まない**（起点はハンドラ入口）。
- **BFF から利用者ブラウザまでの区間は含まない。** ネットワークと画面描画は入らない。
- **`RagFirstTokenP95High` の本物の発火は未実測である。** 5 秒を超える初回応答を稼働環境で作るには
  実際に遅い LLM 応答が要る。`IADR-0345` §実測 C と同じ形の変異プローブでしか確かめられない。

## 代替案

- **BFF（`/bff/analysis/ask/stream`）で測る** —— 利用者に最も近いが、`job` が BFF になり、
  `ADR-0076` 決定 5 が名指しした経路と食い違う。上流の値に中継分を足した二重計上にもなる。採らない。
- **LlmGateway の `/complete/stream` で測る** —— **検索時間を含まない**。
  「RAG の初回応答」ではなく「LLM の初回トークン」になり、SLI と別物になる。採らない。
- **`RagLatencyP95High` の式を TTFT へ付け替える** —— 決定 5 の 2 つの理由により採らない。
- **`RagLatencyP95High` を削除する** —— `ADR-0076` が「傾向の観察に留める」と残す前提で書いている。採らない。
- **属性にモデル名を載せる** —— 初回トークンの時点で未確定であり、載せると誤読される。採らない。

## 影響

- `AiAnalysisService` に `Common/Observability/RagStreamMetrics.cs` が 1 つ増え、
  `Program.cs` が Meter を 1 本 OTLP パイプラインへ載せる（`AddPlatformObservability` は変更しない ——
  OpenTelemetry の builder は加算的である）。
- `AskStream/Endpoint.cs` が `RagStreamMetrics` を注入して受け取る（署名が 1 引数増える）。
- アラートルールが 5 → 6 件（4 ファイル）。ダッシュボードのパネルが 6 → 7 枚（2 ファイル）。
- 「5 ルール」「5 件返す」という**導出値**を持つ live な記述を数え直した
  （`docs/operations/operations.md` 6 箇所・配備設定 3 ファイル・`scripts/check-grafana-alerting.js`）。
  **確定済みの `.ai-context/` の記録は書き換えていない。**

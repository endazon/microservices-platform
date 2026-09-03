---
title: 作業仕様書 — /analysis/ask/stream の初回トークンまでの時間（TTFT）を測る計器を新設し、NFR-02 の SLO 判定をそこへ移す
type: spec
status: done
related_ids:
  - NFR-02
  - NFR-21
  - FR-04
  - UC-01
  - SC-01
  - ADR-0006
  - ADR-0076
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - "ADR-0076 決定 5（NFR-02 の SLI『初回応答』は定義を変えず、初回トークンまでの時間を測る計器を新設する。測定対象は SC-01 が実際に使う /analysis/ask/stream。計器が無いことを理由に要求の側を下げない）"
  - "ADR-0076 決定 1（HTTP サーバーのレイテンシは OTel 安定版 HTTP セマンティック規約に従い、単位は秒。旧規約のミリ秒は用いない）"
  - "ADR-0076 §統制と現在の実現手段（応答完了 p95 は代理値として読むが、長い回答ほど悪化するため SLO 判定には用いない＝傾向の観察に留める）"
  - "02_requirements/01_requirements.md NFR-02（RAG 回答の初回応答 p95 5 秒以下。［2026-09-03］単位は秒）"
  - "02_requirements/01_requirements.md NFR-21（障害検出 5 分以内 / MTTR 30 分以内。［2026-09-03 訂正］充足は『評価対象があること』まで含めて判断する）"
related_adrs:
  - IADR-0354
  - IADR-0345
  - IADR-0212
  - IADR-0110
  - IADR-0037
  - IADR-0168
  - IADR-0244
issue: "#1204"
---

# 作業仕様書: `/analysis/ask/stream` の初回トークンまでの時間（TTFT）計器

## 起点

`ADR-0076` 決定 5 の受け皿が #1204 である。裁定は 3 つを同時に縛っている。

| # | 縛り | 本作業での帰結 |
| --- | --- | --- |
| 1 | **SLI「初回応答」の定義は変えない** | `RagLatencyP95High`（応答完了 p95）を SLI として使い続けない。式の付け替えでもない |
| 2 | **測定対象は `/analysis/ask/stream`** | 一括経路 `/analysis/ask` は測定対象にしない（SC-01 が使わない） |
| 3 | **計器が無いことを理由に要求の側を下げない** | 5 秒という数値は据え置く。動かすのは計器の側だけ |

`RagLatencyP95High` は注釈が「RAG 初回応答 p95（NFR: 5s）」と称しながら、式は
`http_server_request_duration_seconds_bucket{http_route="/analysis/ask"}` の p95 ＝ **応答が完了するまでの所要時間**
であり、しかも SC-01 が使わない一括経路を見ている。**名前と中身が食い違っている。**

## 母集合（着手前に自分で引いた。issue 本文からは転記していない）

### 走査 1 — TTFT を測れる箇所（SSE の最初のトークンを書く位置）

```console
$ grep -rn "event: token\|text/event-stream" --include=*.cs src/ | grep -v "/Tests/"
src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/AnalysisBffEndpoints.cs:94
src/knowledge/backend/Services/AiAnalysisService/Features/Analysis/AskStream/Endpoint.cs:9,24
src/platform/backend/Services/LlmGateway/Features/Completions/CompleteStream/Endpoint.cs:36
src/platform/backend/Services/LlmGateway/Infrastructure/ExternalServices/AnthropicResponseSanitizingHandler.cs:15（コメント）
```

**候補は 3 箇所**であり、採るのは 1 つである。

| 位置 | 採否 | 理由 |
| --- | --- | --- |
| `AiAnalysisService` `AskStream/Endpoint.cs` | **採る** | `ADR-0076` 決定 5 が名指しした `/analysis/ask/stream` そのもの。検索（ABAC スコープ解決＋ハイブリッド検索）と LLM 生成の**両方**を含む区間であり、SLI「初回応答」の意味に一致する |
| `Knowledge.Bff.Endpoints` `AnalysisBffEndpoints.cs:94` | 採らない | 利用者に最も近いが、**別サービス**であり `job` は BFF になる。決定 5 は測定対象を `/analysis/ask/stream` と書いており、BFF の `/bff/analysis/ask/stream` ではない。BFF での計測は上流の値に中継分を足したものになり、二重計上になる |
| `LlmGateway` `CompleteStream/Endpoint.cs:36` | 採らない | **検索時間を含まない**。ここで測ると「RAG の初回応答」ではなく「LLM の初回トークン」になり、SLI と別物になる |

### 走査 2 — 既存の計器の命名規約（自分で引いた実測）

```console
$ grep -rn "const string.*Name = \"" --include=*.cs src/ | grep -v "/Tests/" | grep -iE "meter|counter|histogram"
（13 件。Meter 名 5 種・計器名 10 件）
```

| 観察 | 実測 |
| --- | --- |
| Meter 名 | **サービス名と一致させる**（`microservices-platform.<service>`。5 種とも例外なし） |
| 計器名 | 小文字ドット区切りの `<領域>.<主語>.<測るもの>`（`llm.completion.total` / `llm.completion.output_tokens` / `search.keyword_degraded.total` / `graph.edge_type_fallback.total` ほか） |
| 累計カウンタ | 末尾 `.total` |
| ヒストグラム | `.total` を付けない（`llm.completion.output_tokens`。`CreateHistogram` の実在＝**陽性対照**。同じ走査器で `CreateHistogram` を引くと 1 件返る） |
| 単位 | UCUM。`{completion}` / `{token}` の注釈単位は Prometheus 名へ写らない |
| 属性 | **値域を閉じる**（未知値は `other` へ集約）。プロンプト・本文・利用者識別子は載せない |

OTel の安定版 semconv では所要時間の計器は `<name>.duration`・単位 `s` である
（`http.server.request.duration` がその形。`ADR-0076` 決定 1 が版と単位を固定した）。
本作業の計器名は両者に揃えて **`rag.answer.first_token.duration`（`Histogram<double>`・単位 `s`）** とする。

### 走査 3 — アラートルールの写し（何ファイルに書くか）

```console
$ grep -rln "RagLatencyP95High" deploy/
deploy/prometheus/alerts.yml
deploy/local/observability/prometheus.yaml
deploy/grafana/provisioning/alerting/slo-alerts.yaml
deploy/local/observability/grafana.yaml
```

**4 ファイル。**`node scripts/check-grafana-alerting.js` はこのうち 3 つ
（`alerts.yml` / `slo-alerts.yaml` / `grafana.yaml` の inline）の 1 対 1 を見る。
**`deploy/local/observability/prometheus.yaml` の inline は機械検査の射程外**であり、
`IADR-0345` 決定 1 が「同時に直す」と人手の規律として定めている。**本作業もそれに従う。**

### 走査 4 — ダッシュボードの写し

```console
$ grep -rln "microservices-platform-overview" deploy/
deploy/grafana/provisioning/dashboards/microservices-platform-overview.json
deploy/local/observability/grafana.yaml
deploy/grafana/provisioning/dashboards/dashboards.yaml（provider 定義）
```

**2 ファイルに同内容**（`node scripts/check-grafana-provisioning-parity.js` が JSON を深く比較する）。

### 走査 5 — 「5 ルール」という**導出値**の追随先（是正規則 10。走査ではなく数え直す）

ルールを 1 本増やすと、リポジトリ内の「5 ルール」「5 件返す」という記述が**新たに誤りになる**。

```console
$ grep -rn "5 ルール\|5 件返す" --include=*.md --include=*.js --include=*.yml --include=*.yaml .
```

| ファイル | 追随 |
| --- | --- |
| `docs/operations/operations.md`（6 箇所） | **する**（live な運用文書） |
| `deploy/grafana/provisioning/alerting/slo-alerts.yaml` / `deploy/local/observability/grafana.yaml` / `deploy/local/observability/prometheus.yaml` | **する**（live な配備設定） |
| `scripts/check-grafana-alerting.js` の冒頭コメント | **する**（live な検査器） |
| `.ai-context/adr/IADR-0165` / `IADR-0345`、`.ai-context/specs/*` 4 件 | **しない**。確定済みの凍結記録であり、本文を後から書き換えない（`traceability.repo.md`） |

## 設計

### 1. 計器

| 項目 | 値 |
| --- | --- |
| Meter 名 | `microservices-platform.aianalysis-service`（`Program.cs` の `ServiceName` と一致） |
| 計器 | `rag.answer.first_token.duration`（`Histogram<double>`・**単位 `s`**） |
| 発行元 | `AiAnalysisService` の `POST /analysis/ask/stream` **のみ** |
| Prometheus 側の名前（予測） | `rag_answer_first_token_duration_seconds_{bucket,count,sum}` ← **稼働クラスタで実測して確かめる** |
| バケット境界 | `0.1 / 0.25 / 0.5 / 1 / 2 / 3 / 5 / 8 / 13 / 21`（秒） |

**バケット境界に 5 を必ず置く。** SLO のしきい値そのものであり、境界に無いと
`histogram_quantile` が隣の境界へ内挿して、しきい値の前後で判定が滑る。

### 2. 起点と終点（`ADR-0076` 決定 5 が定義を要求している箇所）

- **起点**: `/analysis/ask/stream` のハンドラ入口で採る `Stopwatch.GetTimestamp()`。
  ミドルウェア（相関 ID・認証）を通過した後である。**その差分は本計器に含まれない**が、
  `http_server_request_duration_seconds` が同じ経路を丸ごと測っており、両者の差として観測できる。
- **終点**: **最初の `event: token` フレームを応答本文へ書き、`FlushAsync` が完了した時刻。**
  バイトがサーバを出た時点であり、「利用者に最初の文字が届き得る」瞬間に最も近い。
- **`citations` は起点でも終点でもない。** 出典は本文のトークンではなく、SC-01 は本文表示中に併記する。
  出典の送出で計器を止めると、**LLM 生成が始まる前の時刻を「初回応答」として記録する**ことになる。

### 3. 記録しない場合

- `token` イベントが **1 件も出ずに** ストリームが終わった（`error` のみ・途中終端・取り消し）→ **記録しない。**
  0 を積むと「初回トークンが無かった」が「速かった」として分布の最下段へ入り、p95 が下振れする。
- 記録は 1 ストリームにつき **高々 1 回**（2 件目以降の `token` では測らない）。

### 4. 属性（値域を閉じる）

| 属性 | 値域 | 意味 |
| --- | --- | --- |
| `ai.purpose` | `rag-answer` / `other` | 用途。`llm_completion_total{llm_purpose=...}` と同じ軸で読めるようにする |

- **`model` は載せない。** 使用モデルは `done` イベントで初めて確定し、**初回トークンの時点では未確定**である。
  未確定のものを `none` として載せると、系列が実質 1 本になるだけでなく「モデル別 TTFT」と誤読される。
- **プロンプト・検索語・利用者識別子・質問文は載せない**（非有界。カーディナリティが爆発する）。
- 値域は既知集合の照合で閉じ、未知値は `other` へ落とす（`LlmCompletionMetrics` と同じ規律）。

### 5. アラート —— 新ルールを足し、既存は据え置いて注釈だけ是正する

**`RagLatencyP95High` の式は付け替えない。新ルール `RagFirstTokenP95High` を足す。**

| 判断 | 内容 |
| --- | --- |
| 採る | `RagFirstTokenP95High`（新規。TTFT p95 > 5 秒が 10 分・warning）＝ **NFR-02 の SLO 判定はこれで行う** |
| 採る | `RagLatencyP95High` は**式・しきい値・severity を据え置き**、注釈から「初回応答」を削り「応答完了 p95。傾向の観察に留め、NFR-02 の判定には用いない」と明記する |
| 却下 | `RagLatencyP95High` の式を TTFT へ向け直す |

**却下の理由（2 つ）。**

1. `ADR-0076` §統制と現在の実現手段 は応答完了 p95 を「**代理値として読む。ただし SLO 判定には用いない
   （傾向の観察に留める）**」と書いており、**残す前提で書かれている。** 式を付け替えると一括経路
   `/analysis/ask` の観測が消え、ADR が残すと書いたものを本作業が消すことになる。
2. **同じ名前が別のものを指すようになる。** `RagLatencyP95High` は Alertmanager の履歴・運用記録・
   `IADR-0345` の実測表（p95 = 0.0628 秒）に既に現れている。名前を保ったまま中身を替えると、
   過去の記録が黙って別の意味になる。**名前と中身の食い違いは、本 issue が是正している当のものである。**

ルール数は **5 → 6**。4 ファイルすべてに同時に足す。

### 6. ダッシュボード

`microservices-platform-overview.json` と `deploy/local/observability/grafana.yaml` の inline に
**TTFT の p95 パネル**を足す（`#1110` の教訓 —— アラートだけ直すと運用者が空のグラフを見て「異常なし」と記録する）。

## 受け入れ基準（#1204 から転記）

- [ ] `/analysis/ask/stream` を 1 回呼ぶと TTFT ヒストグラムに 1 件記録される
- [ ] `token` を 1 件も出さず `error` で終わったストリームでは記録されない
- [ ] 本文長が 10 倍違う 2 ストリームで TTFT が本文長に比例して増えない
- [ ] 計器の単位が**秒**である
- [ ] 属性にプロンプト・検索語・利用者識別子が入っていない
- [ ] 稼働クラスタの Prometheus で TTFT の系列が返る（**陽性対照を対で置く**）
- [ ] Grafana の TTFT パネルにデータが描かれる
- [ ] 計器の記録を消す変異でテストが落ちる（戻して残渣 0）
- [ ] `docs/operations/operations.md` の SLO 表で NFR-02 が TTFT に置き換わり、応答完了 p95 が「傾向の観察に留める」と明記されている
- [ ] `node scripts/check-grafana-alerting.js` / `check-grafana-provisioning-parity.js` が成功する
- [ ] `dotnet build` / `dotnet test knowledge/backend/backend.slnx` が成功する

## テスト方針

`MeterListener` でプロセス内の測定を購読する（`CompletionMetricsTests` と同じ形。`IADR-0244` の
「観測できる面から決める」に従い、**外から観測できる面＝ `MeterListener` の測定イベント**で固定する）。

| ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-a | 1 回呼ぶと 1 件 | `POST /analysis/ask/stream` → 測定 1 件・値 > 0 |
| T-b | token 無しでは記録しない | `token` を出さず `error` だけ流すオーケストレータへ差し替え → 測定 **0 件**（陰性）。**同じ試験クラス内に T-a を陽性対照として持つ** |
| T-c | 本文長に比例しない | 1 トークンのストリームと、末尾に長い遅延を積んだストリームで TTFT が同等（応答完了時間なら大きく開く） |
| T-d | 単位が秒 | `MeterListener` の `instrument.Unit` が `s` |
| T-e | 属性の値域 | 測定のタグ集合が `ai.purpose` のみで、質問文を含まない |
| T-f | 変異で落ちる | `Record` を消す／`citations` で止める／単位を `ms` にする の 3 変異を当て、落ちることを実測して戻す |

## 計画書との差異

- 差異: なし。`ADR-0076` 決定 5 の 3 つの縛りをそのまま実装する。

## 未決事項（本作業の射程外・#1202 へ申し送り）

- **`absent` ルールの対象経路**（`ADR-0076` 決定 3）は #1202 の射程である。
  🔴 **`/analysis/ask/stream` は「常時トラフィックがある経路」ではない** —— 無風でいられる時間が
  検知要件（5 分）を超える。決定 3 は無風が 5 分を超え得る経路を対象外とし、決定 4 の合成監視で
  常時トラフィックを作ってから対象へ入れると定めている。**本計器も同じ性質を継ぐ**（呼ばれなければ系列が無い）。
- **合成監視**（決定 4）は未配備。したがって無風時に「鳴らない」と「鳴りようがない」の区別は付かないままである。
- **ABAC 縮退で出る中立文言も `token` である。** 権限が無い場合の
  「閲覧権限のある文書が見つかりませんでした。」は LLM を経ずに即座に出る `token` であり、
  **TTFT として記録される**。利用者から見た「最初の文字が出た時刻」としては正しいが、
  LLM 経路の TTFT より速いため分布を下へ引く。**縮退と正常を属性で分けるにはオーケストレータ側の
  信号が要る**（`AskEvent` は出自を持たない）。本作業では分けず、限界として記録する。

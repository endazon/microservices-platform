---
title: 作業仕様書 — 合成トラフィックに偽装できない標識を与え、LLM 費用計測と利用状況・検索傾向の集計から除外する
type: spec
status: done
related_ids:
  - NFR-02
  - NFR-21
  - ADR-0044
  - ADR-0071
  - ADR-0072
  - ADR-0076
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - "ADR-0076 決定 4（合成監視を計画へ位置づける。低頻度経路（/analysis/ask 系）と 5xx 経路について一定間隔で代表リクエストを打つ観測専用の経路を置く。合成トラフィックは識別できる標識を持ち、ADR-0044 の LLM 費用計測と、FR-10 の利用状況・検索傾向（SC-10）の集計から除外する。除外できない構成では合成監視を配備しない。標識と除外は同時に入れる）"
  - "ADR-0076 決定 3（常時トラフィックがある経路の SLO は系列の不在そのものを warning とする。無風が 5 分を超え得る経路は対象外とし、決定 4 の合成監視で常時トラフィックを作ってから対象へ入れる）"
  - "ADR-0076 §残るもの（合成監視の頻度・費用の上限は定めていない。LLM を呼ぶ経路を含めるなら費用が発生する。除外規則は指標を守るものであり、費用そのものを減らさない）"
  - "ADR-0076 §残るもの（本番相当の 5xx を発生させる経路が無い）"
  - "ADR-0072 §決定 冒頭の［2026-09-03 補完］（利用イベント側から同じ除外を受ける）"
  - "ADR-0071（検索傾向の最小出現件数しきい値。合成の語がしきい値を通過してはならない）"
  - "ADR-0044 決定 1・3（LLM 利用実績は用途別・モデル別。属性の値域を閉じる。単価はゲートウェイ側で解決する）"
  - "02_requirements/01_requirements.md NFR-02（RAG 回答 初回応答 p95 5 秒）・NFR-21（障害検出 5 分以内）"
related_adrs:
  - IADR-0378
  - IADR-0370
  - IADR-0345
  - IADR-0354
  - IADR-0343
  - IADR-0367
  - IADR-0357
  - IADR-0299
  - IADR-0110
issue: "#1203"
---

# 作業仕様書: 合成トラフィックの標識と、費用・集計からの除外（#1203）

## 0. 裁定要否の判定（着手前に行った）

`ADR-0076` 決定 4 を読み、**実装裁量で決められない組織入力が含まれているか**を判定した。

| 論点 | 決定 4 の定め | 実装裁量か | 判断 |
| --- | --- | --- | --- |
| 対象経路 | 「低頻度経路（`/analysis/ask` 系）と 5xx 経路」と**名指しされている** | ○（粒度は実装） | 裁定不要 |
| 標識の形 | 「識別できる標識を持ち」まで。形は定めていない | ○ | **裁定不要**（本 PR で決める） |
| 除外先 | `ADR-0044` の費用計測・`SC-10` の利用状況・`ADR-0071` の検索傾向、と**列挙されている** | ○ | 裁定不要 |
| 配備の可否条件 | 「除外できない構成では配備しない」 | ○ | 裁定不要 |
| **実行頻度** | 🔴 **定めていない**（§残るもの が自認） | ✗ | **裁定が要る** |
| **費用の上限** | 🔴 **定めていない**（§残るもの が自認。「LLM を呼ぶ経路を含めるなら費用が発生する」） | ✗ | **裁定が要る** |
| 外部監視 SaaS の利用可否 | 計画 `08_data-egress-policy` が外部 CDN・SaaS・テレメトリ送出を禁じている | ✗（既に裁定済み） | **裁定不要**（＝使わない、で確定） |
| **意図的に 5xx を出す合成経路の可否** | §残るもの が「本番相当の 5xx を発生させる経路が無い」と残す。**故意に 5xx を作る**のは製品の振る舞いを変える | ✗ | **裁定が要る** |

🔴 **さらに実装で判明した従属関係がある。** `NFR-02` の SLI を測る計器
`rag.answer.first_token.duration`（`IADR-0354` / #1206）は **`token` イベントが 1 件も出なければ記録されない**。
したがって **`RagFirstTokenP95High` の評価対象を常時存在させるには、合成トラフィックが実際に LLM を呼ばねばならない。**
呼べば恒常的に費用が出る。**費用上限が未定のまま LLM を呼ぶ合成監視を既定で回すことは実装裁量では決められない。**

→ **planning へ `decision-needed` で起票する**（§7）。そのうえで**裁定に依存しない範囲**を本 PR で実装する。

| 範囲 | 本 PR | 理由 |
| --- | --- | --- |
| 標識（偽装できない印） | **やる** | 実装裁量。決定 4 の配備条件そのもの |
| 費用・利用状況・検索傾向からの除外 | **やる** | 実装裁量。決定 4 が列挙している |
| 除外件数の可視化 | **やる** | 黙って消すと「合成だけが通っていて実利用は 0」でも緑に見える |
| **LLM を呼ばない**合成トラフィック（`/analysis/ask` 系の HTTP 系列を常時作る） | **やる** | 費用 0。頻度の既定値は**運用者が設定で決める**形にし、実装は数字を持たない |
| **LLM を呼ぶ**合成トラフィック（`RagFirstTokenP95High` の評価対象） | **やらない（既定 off）** | 費用上限が未定。裁定待ち |
| 意図的な 5xx の合成経路 | **やらない** | 製品の振る舞いを変える。裁定待ち |
| `absent` の対象拡大（`/analysis/ask` 系を決定 3 の対象へ入れる） | **やらない** | #1202 の射程（issue #1203 本文が「#1202 の対象拡大」を後段と明示） |

## 1. 母集合（自分で走査した。issue の記述は転記していない）

基点 `develop` `f2b82d7d`。`git rev-parse --is-shallow-repository` → `false`（履歴は打ち切られていない。出典に使える）。

### ① 費用を計上している箇所

```console
$ git grep -n "LlmUsageMetrics|llm.cost.total|llm.tokens.total|llm_cost_total|llm_tokens_total" -- src deploy docs scripts
```

| 箇所 | 役割 | 除外の要否 |
| --- | --- | --- |
| `src/platform/backend/Services/LlmGateway/Common/Observability/LlmUsageMetrics.cs` | `llm.tokens.total` / `llm.cost.total` / `llm.pricing.unpriced.total` の**計上の実体** | 🔴 **要**（`ADR-0044` の費用計測そのもの） |
| `src/platform/backend/Services/LlmGateway/Features/Completions/Complete/Endpoint.cs:77` | `usage.RecordUsage(...)` の呼び出し（一括） | 🔴 **要** |
| `src/platform/backend/Services/LlmGateway/Features/Completions/CompleteStream/Endpoint.cs:139` | 同（SSE） | 🔴 **要** |
| `LlmCompletionMetrics`（`llm.completion.total` / `llm.completion.output_tokens`） | 呼び出し**回数**と出力トークン**分布**。拒否率の分母 | **不要**（決定 4 が挙げるのは費用計測。回数は「呼んだ事実」であり、合成を抜くと拒否率の分母が欠ける） |
| `deploy/grafana/provisioning/dashboards/llm-usage.json` / `deploy/local/observability/grafana.yaml` | 費用ダッシュボード（`increase(llm_cost_total[...])`） | **不要**（計器側で除外すれば式は不変） |
| `docs/observability/llm-usage-and-cost-metrics.md` / `docs/operations/llm-cost-monthly-review-runbook.md` | 費用の読み方 | **追随が要る**（除外の存在と除外件数の読み方） |

### ② 集計・ダッシュボードの対象（利用状況・検索傾向）

```console
$ git grep -ln "UsageEvent" -- src
```

| 箇所 | 役割 | 除外の要否 |
| --- | --- | --- |
| `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Usage/UsageEventReporter.cs` | `IUsageEventReporter.Report`。**発火の唯一の口**（同期・O(1)・例外を投げない） | 🔴 **要**（ここが最短の絞り） |
| `.../Usage/UsageEventDispatcher.cs` | 列を排出し `POST /dashboard/events` へ送る常駐処理 | 不要（発火で落ちれば列へ載らない） |
| `.../Usage/UsageEventMetrics.cs` | `usage.event.dispatch.total{outcome=sent/rejected/unreachable/dropped}` | **要**（除外の結末を足す。**外した件数の可視化**） |
| `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/SearchBffEndpoints.cs:~` | `search` の発火点（検索語つき） | 🔴 **要**（標識の判定はここで） |
| `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/AnalysisBffEndpoints.cs` | `answer` の発火点 3 経路（`/ask`・`/analyze`・`/ask/stream`） | 🔴 **要** |
| `src/knowledge/backend/Services/DashboardService/Features/Dashboard/RecordEvent/Endpoint.cs` | 受け口。`RequireAuthorization()`。**行を作る主体** | **要**（多層防御。直接叩かれた場合） |
| `.../Features/Dashboard/Trends/Endpoint.cs` ＋ `DashboardEndpoints.AggregateTrendsAsync` | 検索傾向（`ADR-0071` のしきい値 `minCount`） | **不要**（行が入らなければ語も入らない。**入口で落とす方が確実**） |
| `.../Features/Dashboard/Summary/Endpoint.cs` / `Usage/` | 利用状況の集計 | 同上 |
| `.../Features/Dashboard/PurgeExpired/*` | 90 日保持の削除（`ADR-0072`・#1198） | 不要 |
| `src/knowledge/frontend/src/features/sc10-operations/components/OperationsDashboardPage.tsx` | SC-10 の画面 | 不要（源が汚れなければ画面は汚れない） |

🔴 **検索傾向（`ADR-0071`）に独立した除外を置かない**理由: 合成の語が `UsageEvents` に**一行も入らない**なら、
しきい値 3 を通過する余地が無い。**入口が 1 か所しかない**（`IUsageEventReporter`）ことは①の走査で確かめている。
受け口側の多層防御と合わせて 2 枚になる。

### ③ SLO / アラートの対象

`deploy/prometheus/alerts.yml`（＋ `deploy/grafana/provisioning/alerting/slo-alerts.yaml` と
`deploy/local/observability/grafana.yaml` の写し）。

| ルール | 参照系列 | 合成トラフィックとの関係 |
| --- | --- | --- |
| `OtelCollectorDown` / `OtelCollectorUpSeriesAbsent` | `up{job="otel-collector"}` | 無関係 |
| `ServiceRequestMetricsAbsent` / `HighHttp5xxRate` / `HttpServerMetricsSeriesAbsent` | `http_server_request_duration_seconds_count` | 合成が**乗る**（乗ってよい。トラフィックの実在そのものが目的） |
| `SearchLatencyP95High` / `SearchLatencySeriesAbsent` | `..._bucket{job=...retrieval-service}` | 合成検索を打てば乗る |
| `RagLatencyP95High` | `..._bucket{job=...aianalysis-service, http_route="/analysis/ask"}` | 🔴 **本 PR の合成トラフィックで評価対象が生まれる**（LLM を呼ばなくても HTTP 系列は立つ） |
| `RagFirstTokenP95High` | `rag_answer_first_token_duration_seconds_bucket` | 🔴 **LLM を呼ばないと系列が立たない。裁定待ち** |

**SLO の分子分母から合成を外すか**: 🔴 **外さない。** 決定 4 が除外を命じたのは
**費用計測と利用状況・検索傾向**であって SLO ではない。SLO から外すと**合成監視を置く意味が消える**
（決定 3・4 は「評価対象を作る」ためにこれを置いている）。この読みは §理由 へ IADR に残す。

### 走査の陽性対照（「無い」を「見えていない」と読み違えないため）

```console
$ git grep -lEi "synthetic|blackbox|合成監視|合成トラフィック" -- src deploy docs scripts
  deploy/grafana/provisioning/alerting/slo-alerts.yaml   ← 注記（未実施の宣言）
  deploy/local/observability/grafana.yaml                ← 同じ注記の写し
  deploy/local/observability/prometheus.yaml             ← 同上
  deploy/prometheus/alerts.yml                           ← 同上
  docs/operations/operations.md                          ← follow-up の宣言
  scripts/k8s-local-up.test.js                           ← テストの汚染データ変数名。監視ではない
  → 配備物（マニフェスト・Deployment・CronJob・スクリプト）は 0 件

  陽性対照: $ git grep -lci "exporter" -- deploy → 6 ファイル（非 0）。deploy/ は走査に掛かっている
```

## 2. 標識の設計（決定 4 の配備条件そのもの）

🔴 **「偽装できない」を成立させるには、外から到達できる面とメッシュ内部の面で判定材料を変える。**

| 面 | 到達性 | 判定材料 | 偽装可能性 |
| --- | --- | --- | --- |
| **外周**（`Platform.Bff` の `/bff/*`、`DashboardService` の `/dashboard/events`） | 外部から到達し得る | 🔴 **検証済み JWT の主体**（`azp` / `client_id` / `preferred_username` / `sub` のいずれかが構成の許可集合に一致） | **無い** —— 利用者は他人の `azp` を名乗るトークンを発行できない（クライアント資格情報が要る） |
| **内周**（`AiAnalysisService` → `LlmGateway`） | ClusterIP ＋ NetworkPolicy 既定拒否 ＋ STRICT mTLS（`IADR-0299` が受容した残余リスクと同じ境界） | ヘッダ `X-Synthetic-Traffic: 1`（**外周が付ける**） | 同一ネットワーク内からのみ。`IADR-0299` の受容と同型 |

🔴 **外周は受信ヘッダを一切見ない。** 見れば「外から印を付けて費用計上を免れる」経路ができる。
**偽装試験（`X-Synthetic-Traffic: 1` を外から付けても除外されない）を陰性対照として固定する。**

**fail-closed**: 許可集合（`SyntheticMonitoring:Subjects`）が**空なら何も合成と見なさない**。
設定漏れが「全部が合成」へ倒れない向きに倒す。

## 3. 変更点

| # | ファイル | 変更 |
| --- | --- | --- |
| 1 | `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Observability/SyntheticTraffic.cs`（新規） | 標識の**唯一の定義**。`SyntheticMonitoringOptions`（`Subjects` / `AllowLlmEgress`）・主体判定・内部ヘッダ名・DI 拡張 |
| 2 | `.../Usage/UsageEventSignal`・`UsageEventReporter` | `IsSynthetic` を signal へ持たせ、`Report` の入口で落とす（**唯一の口**） |
| 3 | `.../Usage/UsageEventMetrics` | `OutcomeExcludedSynthetic = "excluded_synthetic"` を追加（**外した件数の可視化**） |
| 4 | `SearchBffEndpoints` / `AnalysisBffEndpoints` | 主体から合成を判定して signal へ載せる。分析 3 経路は後段へ `X-Synthetic-Traffic` を付ける |
| 5 | `DashboardService/Features/Dashboard/RecordEvent/Endpoint.cs` | 多層防御。合成主体の直接投入は行を作らず `202 Accepted` ＋ 警告ログ |
| 6 | `AiAnalysisService/Infrastructure/ExternalServices/RagOrchestrator.cs` | ヘッダを `LlmGateway` へ中継。**`AllowLlmEgress=false`（既定）なら LLM を呼ばずに縮退**（費用上限 0 の実装） |
| 7 | `LlmGateway/Common/Observability/LlmUsageMetrics.cs` | 合成は `llm.tokens.total` / `llm.cost.total` に**積まない**。`llm.usage.synthetic_excluded.total` に積む |
| 8 | `LlmGateway/Features/Completions/{Complete,CompleteStream}/Endpoint.cs` | 上の分岐 |
| 9 | 各 `Program.cs`（Bff / DashboardService / AiAnalysisService / LlmGateway） | 構成の束縛と Meter 宣言 |
| 10 | `deploy/local/synthetic-monitor/`（新規） | クラスタ内で完結する常駐プローブ（Deployment ＋ Secret ＋ README ＋ kustomization）。**既定 overlay には入れない**（opt-in） |
| 11 | `deploy/keycloak/microservices-platform-realm.json` | `synthetic-monitor` クライアント（`client_credentials`・ロール無し・ABAC ポリシー無し） |
| 12 | `docs/operations/operations.md` / `docs/observability/llm-usage-and-cost-metrics.md` / `docs/observability/synthetic-monitoring.md`（新規） | 運用手順・頻度・費用上限の扱い・停止手順 |
| 13 | `.ai-context/adr/IADR-0378_*.md` ＋ 索引 | 実装判断 |

## 4. 受け入れ基準 → テストの写像

| 受け入れ基準 | テスト |
| --- | --- |
| 標識つきの検索が `UsageEvents` に行を作らない | `SyntheticTrafficExclusionTests.検索_合成主体_利用イベントを発火しない`（陽性） |
| 標識なしの検索は行を作る | 同 `…_通常主体_利用イベントを発火する`（陰性対照） |
| 外からヘッダを付けても除外されない | 同 `…_外部からヘッダを付けても除外されない`（**偽装**） |
| 合成の語が検索傾向の上位に出ない | 発火しない＝行が無い（上記）＋ `DashboardService` 受け口の多層防御試験 |
| 合成の回答が LLM 費用に入らない | `LlmSyntheticUsageExclusionTests`（陽性）／通常は入る（陰性対照） |
| 除外の実装を消す変異でテストが落ちる | §6 の変異試験 |
| 合成監視が常時トラフィックを作る | §5 の実測 |

## 5. 実測（稼働 k3s）

- 生成物を当てる前に **Prometheus の現況**（`/analysis/ask` 系列の不在＝陽性対照付き）を採る。
- **`deploy/local/observability/prometheus.yaml` を単体で `kubectl apply` しない**（PVC が外れて TSDB を失う。#1202 実測）。
- 本 PR の除外はコード側にあり、**稼働イメージには入っていない**。したがって
  「合成が費用系列に乗らない」ことの**稼働実測は、イメージを焼き直さない限り取れない**。
  取れなかったものは「取れなかった」と PR に書く。

## 6. 変異試験

`UsageEventReporter.Report` の合成除外（早期 return）を消し、
`SyntheticTrafficExclusionTests` の陽性が落ちることを確認して戻す。**残渣 0 を `git diff --stat` で確認する。**

## 7. planning への起票（`decision-needed`）

- 合成監視の**実行頻度**と**1 か月あたりの費用上限**（`ADR-0076` §残るもの が未定と自認）
- **`NFR-02` の SLI を常時監視するには合成が LLM を呼ぶ必要がある**（`rag.answer.first_token.duration` は
  `token` イベントが出て初めて記録される）という従属関係
- **意図的に 5xx を出す合成経路**の可否（§残るもの が「本番相当の 5xx 経路が無い」と残す）

**起票済み: planning#538**（`decision-needed` / `feedback`）。

重複検索（実施済み・2026-09-05）:

```console
$ gh issue list --repo endazon/project-planning --state open --limit 100
  → 14 件。合成監視の頻度・費用上限を扱う open issue は 0 件（全件を目視）
$ gh search issues --repo endazon/project-planning "合成監視" --limit 10  → planning#524（closed）ほか別義のみ
$ gh search issues --repo endazon/project-planning "synthetic" --limit 10 → planning#524（closed）1 件のみ
$ gh search issues --repo endazon/project-planning "ADR-0076" --limit 10 → planning#524（closed）1 件のみ
  → **陽性対照**: 同じ検索器が planning#524 を確かに引けている（0 件が「検索できていない」ではない）
```


---
title: 作業仕様書 — PoC のノードの常駐メモリを下げる（MSP の .NET に AST と同じ GC の設定・Keycloak のヒープ・観測スタックの Go のヒープと Loki の保持）（#1927）
type: spec
status: draft
related_ids: [NFR, NFR-05, ADR-0006, ADR-0008, ADR-0111, IADR-0066, IADR-0077, IADR-0210, IADR-0539]
author: claude
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md
  - planning:projects/microservices-platform/07_adr/ADR-0111_reset-floor-replicas-and-no-bypass-on-failure.md
issue: "#1927"
---

# 作業仕様書 — PoC のノードの常駐メモリを下げる（#1927）

> 本仕様書は実装着手前に作成した（着手 2026-10-14）。基点は MSP `origin/develop` `764bd780`。
> 判断（本番の既定にするか）は [IADR-0539](../adr/IADR-0539_dotnet-gc-workstation-conserve-memory-default.md) に置く。

## 起点

- 起点 issue: **#1927**（PoC の稼働ホストで、ノードのメモリが 13.7 GiB / 15.5 GiB＝87% に張り付く）。関連: #1919（ホストの安定化・requests / limits の静的な棚卸し）。
- 計画: 該当する FR は無い（運用の前提・メタ作業）。番号付きの NFR に当たるものが無いため無採番の `NFR` とする。
  可用性に関わる箇所（reset-floor）は計画 ADR-0111（Accepted）が拘束する。観測スタックは計画 ADR-0006。
- 実測は PoC が配備の前後に `kubectl top` で取る（本 PR は静的な値と AST の実績から見込みを書く）。

## issue 本文の主張の検証（規則 10: 他人の記述を検証せずに転記しない）

| 本文の主張 | develop `764bd780` の実物 | 判定 |
| --- | --- | --- |
| MSP の .NET サービスに GC の設定が 0 件 | `git grep -nE 'gcServer\|GCHeapHardLimit\|GCConserveMemory\|ServerGarbageCollect\|ConcurrentGarbage\|TieredPGO'`（`src/**` の csproj・props・json・Dockerfile、`deploy/**`）→ **0 件**。`helm template` の 15 の .NET Deployment の env にも無い | ✅ 成り立つ |
| 「18 Deployment 中 0」 | チャートの Deployment は 18（既定・values-local とも同じ）。うち **.NET は 15**（`services` の有効な 15）、残り 3 は frontend（Caddy）・seaweedfs（Go）・wiki-js（Node） | ⚠️ 母数の言い方が粗い。**.NET は 15** として扱う |
| AST は 3 つの env を持つ | AST `deploy/helm/ai-stock-trading/templates/deployment.yaml` が全 .NET サービスへ無条件に描く（values ではなくテンプレートに固定＝本番の既定にも入る）。ほかに `MALLOC_ARENA_MAX=2`・`MALLOC_MMAP_THRESHOLD_`・`MALLOC_TRIM_THRESHOLD_` | ✅ 成り立つ（MALLOC_* の扱いは下） |
| Keycloak は `JAVA_OPTS_KC_HEAP=-XX:MaxRAMPercentage=50` | `deploy/local/infra/keycloak.yaml:99-100`。limits 2Gi → ヒープ上限 ≈1Gi | ✅ 成り立つ |
| Tempo のトレースの保持を短くする | **既に 1 時間**（`deploy/local/observability/tempo.yaml` の `compactor.compaction.block_retention: 1h`・`ingester.max_block_duration: 5m`） | ❌ 縮める余地が無い（下の判断表） |
| `reset-floor` の 2 レプリカを 1 にする | **計画 ADR-0111 決定 1（Accepted）が「2 レプリカ以上」を定める**（`deploy/mail-relay/reset-floor/reset-floor.yaml:58-64`） | ❌ 計画に反するので変えない（下） |

## 走査した母集合（規則 2・9・10）

### .NET サービスの母集合

- `helm template msp deploy/helm/microservices-platform`（既定）と `-f deploy/local/values-local.yaml` を描き、`kind: Deployment` の `metadata.name` を列挙した。
  既定・values-local とも同じ 18: aianalysis・authorization・bff・conversion・dashboard・datasource・document・feedback・graph・ingestion・llmgateway・mcp・
  notification・retrieval・wiki（以上 `templates/deployment.yaml` が `services` から描く **.NET 15**）、frontend-service（`templates/frontend.yaml`・Caddy）、
  seaweedfs（`templates/seaweedfs.yaml`・Go）、wiki-js（`templates/wikijs.yaml`・Node）。
- `services` のうち既定で無効の 3（configuration・risk-management・market-monitor）は AST のサービスで、有効化されれば同じテンプレートから描かれ同じ env を受ける
  （AST の実物と同じ値なので矛盾しない）。
- **`templates/deployment.yaml` は `services` だけを描く**ので、そこへ足せば .NET だけに効き、frontend・seaweedfs・wiki-js・embedding（TEI）・
  synthetic-monitor（Node）・drift の PostSync Job には描かれない（それぞれ別のテンプレート）。
- GC の設定を意図して外しているサイドカー: **MSP には無い**（`git grep -n "opend-auth"` は MSP のチャートに 0 件。AST の `opend.yaml` の
  opend-auth-gateway が AST#782 で意図して外したもので、AST のチャートの側の話である）。
- 個別の上書きの衝突: `extraEnv` / `extraEnvAppend` に `DOTNET_` で始まる名前は既定・values-local とも **0 件**（描画を grep）。

### 観測スタックの設定の所在

| 部品 | 経路 B（k8s） | compose | 本番像（helm） |
| --- | --- | --- | --- |
| Prometheus | `deploy/local/observability/prometheus.yaml`（retention 35d / 4GB を args で明示。IADR-0210） | `deploy/docker-compose.yml` の `prometheus.command`・`deploy/prometheus.yml`・`deploy/prometheus/alerts.yml` | 無い |
| Loki | `deploy/local/observability/loki.yaml`（config を inline） | `deploy/loki-config.yaml` | 無い |
| Tempo | `deploy/local/observability/tempo.yaml`（config を inline） | `deploy/tempo.yaml` | 無い |
| otel-collector | `deploy/local/infra/otel-collector.yaml`（既定・debug だけ）／`deploy/local/observability/otel-collector-forward.yaml`（OBSERVABILITY=1） | `deploy/otel-collector-config.yaml` | 無い |
| 永続化 | `deploy/local/observability-persistence/`（PVC の patch） | named volume | — |

- 観測スタックは **`OBSERVABILITY=1` の opt-in**（`scripts/k8s-local-up.sh:838`）。既定の経路 B には出てこない。PoC は有効にしている（#1927 の集計に tempo・otel-collector が載る）。

### H-1 / AST#690 の計測に要る Prometheus の系列

| 計測 | 系列（Prometheus 名） | 入り方 | 依存する設定 |
| --- | --- | --- | --- |
| H-1（graph → dashboard のナレッジ健全性の報告） | `knowledge_health_report_total{knowledge_indicator=…}` | DashboardService の計器 → OTLP → collector の `metrics` パイプライン → `prometheusremotewrite` → Prometheus | 規則 `KnowledgeHealth*ProducerAbsent`（`absent_over_time(...[2h])`）。保持は 2h 以上 |
| AST#690（NFR-01 / NFR-02 の開場中の実測） | `ast.trade_cycle.order_completion_latency_ms`・`ast.trade_cycle.record_completion_latency_ms`（ヒストグラム）・`ast.trade_cycle.latency_unobserved` | AST の trade-decision 等 → OTLP → 同じ collector → remote write | 同じパイプライン。単位の接尾辞は remote write の変換に従う |
| （参考）LLM の月次予算 | `llm_cost_total`・`llm_budget_monthly_limit` | 同上 | `increase(...[30d])` のため保持 35d（IADR-0210） |

- **どれも Prometheus の scrape 設定や recording rule ではなく、collector の `metrics` パイプライン（受け口 `otlp` → 処理 `memory_limiter`・`batch` → 出口 `prometheusremotewrite`）だけで入る。**
  recording rule は 1 本も無い（`grep -n "record:"` は 0 件）。したがって固定すべきは「パイプラインが値で落とさない」「remote write の受け口が開いている」「保持が窓より長い」の 3 点である。
- 固定の方法: 新しい試験 `scripts/resident-memory.test.js`（CI の `static-checks-units`）が `kubectl kustomize` の描画で確かめる（下の受け入れ基準 5）。

## 判断表（依頼の 1〜5）

| # | 対象 | 判断 | 理由 | 見込み（静的） |
| --- | --- | --- | --- | --- |
| 1 | .NET 15 の GC | **入れる。チャートの `global.dotnetGc`（本番の既定）から全 .NET へ一括**。サービス単位で上書き可（`services.<名>.dotnetGc`） | IADR-0539。CPU の limits が 500m / 1000m で Server GC の並列が効かない。AST は同じ値を本番の既定で 1 か月運用し、平均 ≈150 MiB | **−0.5〜−1.0 GiB** |
| 1' | `MALLOC_ARENA_MAX` ほか MALLOC_* | **入れない** | AST#811 の実測で 3 つとも保持の総量を減らさなかった（AST のテンプレートの 2026-09-17 追記）。効いたのは codegen の事前生成で、MSP は計画が事前生成を禁じている（#1919 の所見 ②） | 0 |
| 2a | Tempo の保持 | 変えない（既に 1h） | 上の検証 | 0 |
| 2b | Tempo を既定で止める | **止めない** | Grafana の Tempo データソース・SC-10 の文書・NetworkPolicy・永続化の patch・`k8s-local-up.test.js` が Tempo を前提にする。機能に効く削減は保守的に（依頼の注意） | 0（残余に書く） |
| 2c | Tempo の Go のヒープ | **`GOMEMLIMIT=192MiB`** | Go の GC を目標ヒープの手前で早く回し、返せるページを OS へ返させる（ソフトな上限で、超えても落ちない）。現状 260 MiB / limits 512Mi | −50〜−80 MiB |
| 2d | Loki の取り込み（メモリに持つ時間） | **`ingester.chunk_idle_period: 5m`・`max_chunk_age: 30m`**（既定 30m / 2h）＋ `GOMEMLIMIT=192MiB`。compose の `deploy/loki-config.yaml` も同じ値 | ingester が書き出し前のチャンクをメモリに持つ時間を 1/4 にする。保持（ディスク）は変えない | −20〜−60 MiB |
| 2e | Prometheus の保持・取り込み | **変えない** | 保持（35d / 4GB）はディスクを決め、メモリはほぼ head（直近 2h の系列）で決まる。35d は `[30d]` の窓の最小（IADR-0210）。取り込みを絞る（系列を落とす）と H-1 / AST#690 / 警報の評価対象を落とす危険がある | 0 |
| 3 | Keycloak のヒープ | **`-XX:MaxRAMPercentage=40 -XX:MinHeapFreeRatio=10 -XX:MaxHeapFreeRatio=20 -XX:G1PeriodicGCInterval=60000`**（limits 2Gi は据え置き） | 上限 ≈1,024Mi → ≈819Mi。Keycloak 26.7.4 の `kc.sh` は G1 で「最大ヒープまで取りに行きがち」と自ら書く。空き率の上限と周期 GC で、起動時の augmentation で広げたヒープを暇な間に返させる。非ヒープ（metaspace ≤256m・コードキャッシュ ≤240m・スレッド）を足しても ≈1.4Gi で limits の内側 | −0.2〜−0.3 GiB |
| 3' | Keycloak のキャッシュ | 変えない | `start-dev` は local の Infinispan。PoC の realm は利用者・クライアントが少なく、件数の上限（既定 10,000）に届かない | 0 |
| 4a | reset-floor の 2 レプリカ | **1 にしない** | **計画 ADR-0111 決定 1（Accepted）**が「2 レプリカ以上 ＋ PDB」を定め、経路 B の例外を置いていない（目的は Pod の作り直しで申請を 503 にしないこと）。変えるには計画の改定が要る。削減は Node の小さな Pod 1 つ（≈30〜40 MiB） | 0 |
| 4b | wiki-js | **無効にしない** | wiki-service が `WikiJs__GraphQlEndpoint=http://wiki-js:3000/graphql` で読む（SC-04 の閲覧は `/bff/wiki/*` → wiki-service → Wiki.js） | 0 |
| 4c | mailpit | **無効にしない** | 近接 MTA（mail-relay）の dev の上流（ADR-0078 決定 2）。SC-15 のパスワードリセットと `check-password-reset-mail.js --live` が使う。limits 256Mi・実測は小さい | 0 |
| 4d | その他（seaweedfs・mail-relay・reset-gate・inotify-sysctl） | 無効にしない | document / conversion の保存先・メールの経路・申請の門・ファイル監視の上限で、いずれも機能に効く | 0 |
| 5 | Istio のサイドカー | **見送る** | `concurrency` は未設定で、CPU の limits 200m からワーカー 1 になる（既に最小）。サイドカーのメモリの大半は xDS の構成の量で、効くのは `Sidecar` 資源で egress の宛先を絞ることだが、サービスごとの宛先の棚卸しが要り、漏れると east-west の gRPC が落ちる | 0（残余） |

**合計の見込み: −0.8〜−1.4 GiB**（13.7 GiB → 12.3〜12.9 GiB。15.5 GiB の上限で 79〜83%）。
**70% 以下（≈10.9 GiB）には本 PR だけでは届かない見込み**である。届かせるには `.wslconfig` の 24GB（13.7 / 24 ≈ 57%）か、残余の Tempo の停止・Istio の `Sidecar` を要する。

### .NET の見込みの根拠

- MSP の .NET の実測（#1927 の上位）: graph 498・wiki 406・document 280・ingestion 262・retrieval 210 MiB（5 つで 1,656 MiB）。残る 10 は 210 MiB 未満。
- AST の 11 は同じ 3 つの env・同じ requests / limits（128Mi / 512Mi）で平均 ≈150 MiB（#1927 本文）。
- 上位 5 が AST の平均まで下がると −0.9 GiB、残る 10 が平均 30〜50 MiB 下がると −0.3〜−0.5 GiB。ただし Wolverine の実行時コード生成（conversion・datasource・document・graph・
  ingestion・retrieval・wiki の 7）が持つネイティブのメモリは GC の設定では減らない（AST#808 / AST#811）。ここを割り引いて **−0.5〜−1.0 GiB** とした。

## 設計

1. `deploy/helm/microservices-platform/values.yaml` の `global.dotnetGc`（`gcServer: "0"`・`conserveMemory: "5"`・`heapHardLimitPercent: "60"`）。
   `templates/deployment.yaml` が `services` の各 Deployment の env の先頭（`Otlp__Endpoint` の次）に `DOTNET_gcServer`・`DOTNET_GCConserveMemory`・
   `DOTNET_GCHeapHardLimitPercent` を描く。`services.<名>.dotnetGc` があれば鍵ごとに上書きし、**空文字の鍵は描かない**（そのサービスだけ .NET の既定へ戻す退路）。
   `extraEnv` / `extraEnvAppend` に同じ名前があれば描画で落とす（同名の env を 2 つ置かない。#1389 の注記と同じ理由）。
2. `deploy/local/infra/keycloak.yaml` の `JAVA_OPTS_KC_HEAP`。
3. `deploy/local/observability/tempo.yaml`・`loki.yaml` の本体のコンテナに `GOMEMLIMIT`。`loki.yaml` の inline と `deploy/loki-config.yaml` に `ingester` の 2 項目。
4. 試験 `scripts/resident-memory.test.js`（helm と kubectl で実際に描く。無ければ落ちる＝fail-closed）を CI の `static-checks-units` に足す。

## 受け入れ基準 → 検証

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | 既定・values-local の両方で、.NET 15 の Deployment すべてに 3 つの env が 1 回ずつ、値は `0` / `5` / `60` | `resident-memory.test.js`（描画） |
| 2 | frontend・seaweedfs・wiki-js（と opt-in の embedding・synthetic-monitor）には描かれない | 同上 |
| 3 | サービス単位の上書き（値の変更・空文字で描かない）と、`extraEnv` の同名での描画の失敗 | 同上（`--set` で描く） |
| 4 | 描画の差分が意図した変更だけ（helm: 15 Deployment × 3 行＋コメント。kustomize: keycloak の 1 行・tempo / loki の env と loki の config） | 前後の描画の `diff`（PR 本文に要約） |
| 5 | H-1 / AST#690 の系列の経路が残る: collector の `metrics` パイプラインに `otlp` 受け口・`prometheusremotewrite` 出口があり、処理は `memory_limiter`・`batch` だけ（値で落とす段が無い）。Prometheus は `--web.enable-remote-write-receiver` と保持 ≥30d。規則 `knowledge_health_report_total` が残る | `resident-memory.test.js`（`kubectl kustomize deploy/local/observability` と `-persistence` の描画） |
| 6 | Keycloak のヒープ上限 × limits ＋非ヒープの見積り（512Mi）が limits 以下 | 同上 |
| 7 | reset-floor は 2 レプリカのまま（計画 ADR-0111） | 既存の `reset-floor.test.js` が緑 |
| 8 | `helm lint`・`check-deploy-manifests`・文書系の検査器が緑。`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` は IADR の欠番（0537・0538 は #1925・#1926 で未マージ）だけで赤 | 実行ログ |

## 計画書との差異

- 無い。reset-floor は計画 ADR-0111 に従い変えない（依頼の 4 の一部を実施しない理由）。

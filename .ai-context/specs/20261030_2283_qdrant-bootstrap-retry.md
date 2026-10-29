---
title: 作業仕様書 — 取り込みの Qdrant の索引の保証を、成功するまで上限つきのバックオフで再試行し、成功するまで readiness を NotReady にする（#2283）
type: spec
status: done
related_ids: [FR-02, FR-03, NFR-21, NFR-06, IADR-0650, IADR-0602, IADR-0536]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2283"
---

# 作業仕様書 — 取り込みの Qdrant の索引の保証の再試行（#2283）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `7c71742d9`）。

## 現状（issue の記述を develop で確かめた）

- `QdrantBootstrapHostedService.StartAsync` が起動時に 1 回だけ、コレクションと索引 4 種を保証する（`EnsureCollectionsAsync` → `EnsureCjkNgramIndexAsync` → `EnsureKeywordIndexesAsync` → `EnsureDocumentIdIndexAsync`）。
- 失敗は `catch (Exception)` で Error のログを残すだけである。コメントは「初回 upsert 前に再試行余地を残す」と書くが、upsert の経路（`UpsertChunkAsync` 等）は保証を呼ばない。**1 回の失敗で、Pod が生きている間ずっと書き込みが失敗する。**
- 取り込みの readiness は `/health/ready` に Wolverine のブローカと Qdrant の疎通（`QdrantHealthCheck`）だけを載せる。疎通が戻れば Ready になり、保証の成否は見ない。
- e2e-live の失敗時ダンプ（`.github/workflows/e2e-live.yml`）は bff-service と keycloak のログしか出さない。

## 方針

1. **(a) 再試行**: `QdrantBootstrapHostedService` を `BackgroundService` にし、保証が成功するまで再試行する。待ちは 1 秒から倍々で上限 30 秒。回数の上限は置かない（諦めても NotReady のまま止まるだけで、得るものが無い）。起動は塞がない。
   - 失敗は計数 `ingestion.qdrant_bootstrap.failures`（Prometheus では `ingestion_qdrant_bootstrap_failures_total`）で数える。Meter は既存の `EmbeddingPendingMetrics.MeterName`（OTLP の収集対象へ追加済み）を使う。
   - ログは最初の失敗を Error、以後を Warning で残す。#1116 の「索引の失敗は自分では現れないので Error」を保ちつつ、30 秒ごとの Error で溢れさせない。
2. **(b) readiness**: 新しいチェック `qdrant-bootstrap`（`QdrantBootstrapHealthCheck`）を `ReadyTag` 付き・`failureStatus: Unhealthy` で登録する。保証の成功は singleton の `QdrantBootstrapState` が持つ。一度立てたら戻さない。
3. **後続の器は保証の成功を待つ**: 従前は「登録順」で保証より後に走ることに頼っていた。保証が裏で再試行するようになると、登録順では前後が決まらない。そこで、次の 3 つの器は `QdrantBootstrapState.WaitUntilEnsuredAsync` を待ってから始める。
   - 2-gram の後付け（`QdrantCjkNgramBackfillHostedService`）
   - 属性キーの索引の発見（`QdrantKeywordIndexDiscoveryHostedService`）
   - 埋め込み待ちの埋め直し（`EmbeddingPendingReembedHostedService`）

   状態は `Common/Concurrency` に置く。Features と Infrastructure の両方から引くため、Features から Infrastructure への参照を作らない。
4. **(c) ダンプ**: e2e-live の失敗時ダンプへ ingestion-service・retrieval-service・llmgateway-service のログ（`--tail=150`）を足す。
5. G13 は変えない。

## 母集合（規則 9・10。窓の点検は AST の規則 11 の作法を借りた）

- **規則 9**（誤りの側の文字列で走査）: `git grep -n "QdrantBootstrapHostedService\|より後に登録\|初回 upsert 前に再試行"` で走査した（`.ai-context/specs` を除く）。
  - **追随したもの**:
    - `EmbeddingPendingReembedHostedService`: コメントが「ブートストラップより後に登録する」に頼っていた。待つように直し、追記を付けた。
    - `QdrantCjkNgramBackfillHostedService` と `QdrantKeywordIndexDiscoveryHostedService`: 待つように直した。
  - **除外したもの**:
    - 検索側の `QdrantFullTextIndexHealthCheck` と `QdrantCjkNgramIndexHealthCheck`: メッセージが「取り込みの起動時ブートストラップを確認すること」と案内する。依然として正しいので除外した。
    - `ObjectStorageBootstrapHostedService`: 「同じ方針」と書く。オブジェクトストレージは書き込みの経路が `NoSuchBucket` を捕まえてバケットを作り直す（IADR-0303）ので、1 回きりでも自己回復する。本件の欠陥は無いので除外した。
    - IADR-0002 / IADR-0497 / IADR-0502: 凍結記録なので書き換えない。新しい IADR-0650 が後継の挙動を記録する。
  - **文書**: `docs/operations/operations.md` の §ヘルスプローブ と `docs/tests/FR-02_ingestion.md`（T-64）へ追記した。
- **規則 10**（この変更で新たに誤りになる自分の記述）: `HealthEndpointTests` について 2 点を確かめた。
  - readiness の集合の期待値（`OwnDependencies`）に `qdrant-bootstrap` を足した。
  - 試験名「readiness に載るのは Wolverine のブローカと自前の Qdrant だけ」は、保証も自前の Qdrant についての検査なので、そのまま正しい。
- **窓の点検**（MSP の `traceability.repo.md` に規則 11 は無い。AST の `traceability.repo.md` の規則 11 の作法を任意で借りた）: 本件は時間差の是正ではない（「一度成功したか」の単調な状態）。該当しない。

## 秘密の値の確認（受け入れ基準 4。ジョブのログは公開）

- 走査: 3 サービスのログの呼び出しで、引数・テンプレートに `key|token|secret|password|authorization|bearer|credential` を含むものを走査した（`grep -rn -E "Log(Information|Warning|Error|Debug|Critical)?\(|logger\.Log"` の結果を絞った。Tests を除く）。
  - IngestionService と RetrievalService: 該当なし。
  - LlmGateway: `Program.cs` の起動時の警告 1 件だけが該当した。出すのは**設定キーの名前**（`AnthropicClientFactory.ApiKeyConfigKey`）であり、値ではない。
- URL の問い合わせ文字列に鍵を載せる呼び出し（`?key=` 等）は 3 サービスに無い。
  - HttpClient の既定のログは URI を Information で出すが、ヘッダは出さない。
  - LogLevel は 3 つとも `Default: Information`（Debug は出ない）。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 届かなかった後で届くと作られる | `QdrantKeywordIndexHostedServiceTests.Bootstrap_RetriesUntilQdrantIsReachable_ThenEnsuresEverything`（3 回失敗 → 4 回目で全保証） | 1 回きりへ戻す変異で赤（実測） |
| 成功まで not-ready、成功後 ready（陽性対照） | `BootstrapHealthCheck_IsUnhealthyUntilEnsured`。配線は `HealthEndpointTests`（ready の集合・`qdrant-bootstrap` が落ちると 503） | 登録を外す変異で 2 件赤（実測） |
| 失敗が計器で数えられる | 上の再試行の試験で、計数が 3 であること | — |
| 停止で抜ける | `Bootstrap_StopsRetryingOnShutdown` | — |
| 待ちが倍々で上限 | `Backoff_DoublesAndCaps` | — |
| 後続の器は待つ | `Discovery_WaitsForBootstrap`、`EmbeddingPendingReembedderTests.保証が成功するまで最初の周期を始めない` | 待ちを外す変異で赤（実測） |
| ダンプに 3 サービスのログ | `e2e-live.yml` の失敗時の段 | — |

## PoC 配備への影響

- 取り込みの Pod は、Qdrant の保証が成功するまで Ready にならない。Qdrant が起きていれば、数秒の差で従来どおり Ready になる。
- Qdrant に届かない間は、Deployment が available にならない。これは意図した挙動で、`check-stack-ready` で捕まる。
- 起動器の引数・Secret・migration の変更は無い。新しいイメージへの更新で Pod は作り直されるが、手順の追加は無い。

## ［2026-10-30 追記 / #2311 のレビュー］受け口も保証の成功を待つ

- **順序の実測**: 本番の Program.cs を、外部の転送を切った試験ホストで組んだ。`IHostedService` の登録順は次のとおりで、`HostOptions.ServicesStartConcurrently=false`（直列）だった。使った試験は使い捨てで、コミットしない。
  1. Telemetry
  2. DataProtection
  3. HealthCheckPublisher
  4. **QdrantBootstrap**
  5. CjkNgramBackfill
  6. KeywordIndexDiscovery
  7. EmbeddingPendingReembed
  8. **Wolverine ×2**
  9. GenericWebHost
- 従前は保証の `StartAsync` が await するので、受信は保証の 1 回目の後だった。`BackgroundService` にすると、受信が保証より先に始まる。**新しい窓である。**
- **再試行で吸収できるか**: 共通既定（`UsePlatformMessagingDefaults`）は `RetryWithCooldown(2 s, 10 s, 30 s)`。つまり 4 試行で待ちの合計は 42 秒で、その後デッドレターへ移る。保証の所要は、Qdrant が正常なら数秒である。一方、停止が 42 秒程度を超えると、窓の中で受けた取り込みはデッドレターへ落ちる。**必ず吸収されるとは言えない。**
- 方針: 2 つの受け口（取り込み・削除）の入口で、`QdrantBootstrapState.WaitForConsumerAsync` を待つ（IADR-0650 決定 4）。
  - 上限は 60 秒で、超えたら `QdrantBootstrapPendingException` を投げて再試行の鎖へ返す。
  - 1 配信で待てるのは最大で約 282 秒。各試行は受け口の実行期限 435 秒に収まる。
  - 排他の外で待つ。
- 後続の器 3 つと受け口 2 つは、状態を必須の引数で受ける（レビュー 🟡 2）。試験は `QdrantBootstrapStateForTests.Ensured()` を渡す。
- 試験と red→green: 次の 3 件は、待ちの 1 行を外す変異で 3 件とも赤、戻して緑になる。
  - `Consumer_WaitsForQdrantBootstrapBeforeWriting`（成功まで書かない、成功後に書く）
  - `Consumer_ThrowsForRetry_WhenBootstrapStaysPendingBeyondLimit`
  - `DeleteConsumer_ThrowsForRetry_WhenBootstrapStaysPendingBeyondLimit`

## ［2026-10-30 追記 / #2311 の CI］必須の引数にした型の生成箇所の母集合（規則 9）

- 7b289f29 で、受け口 2 つと後続の器 3 つの状態を必須の引数にした。しかし検証が IngestionService.Tests だけだったので、`Knowledge.IntegrationTests` のコンパイル失敗（CS7036）を CI の `backend-build (knowledge 2/3)` で初めて知った。
- 走査:
  - `git grep -n "new DocumentUpdatedConsumer(\|new DocumentDeletedConsumer(\|new QdrantCjkNgramBackfillHostedService(\|new QdrantKeywordIndexDiscoveryHostedService(\|new EmbeddingPendingReembedHostedService(\|new QdrantBootstrapHostedService(" -- src`
  - 対象は全ユニット（AST の submodule を除く）。IngestionService.Tests 以外の結果は 19 件だった。
- 結果:
  - **取り込みの型は 1 件**: `Knowledge.IntegrationTests/Search/IngestToSearchInProcessTests.cs:273`（`DocumentUpdatedConsumer`）。成功済みの状態を渡すように直した。
  - 残る 18 件は、別サービスの同名の型である（GraphService・RetrievalService・WikiService の `DocumentDeletedConsumer`）。除外した。
  - 後続の器 3 つと保証の器を、IngestionService の外で生成している箇所は 0 件だった（`git grep -n "QdrantBootstrap\|EmbeddingPendingReembedHostedService\|QdrantCjkNgramBackfill\|QdrantKeywordIndexDiscovery"` で、ヘルスチェックの文言と ObjectStorage のコメントの 3 件だけが出た）。
  - DI で受け口を組む統合試験（`DocumentUpdatedFanOutTests` 等）は IngestionService の型を DI へ登録していない（取り込みは観測点のスタブ）。実行時の解決の失敗も無い。
- 検証:
  - `dotnet build src/knowledge/backend/backend.slnx`: 成功、警告 0。
  - `Knowledge.IntegrationTests` の `IngestToSearchInProcessTests`: 8/8。
  - IngestionService.Tests: 293/293。

## ［2026-10-30 追記 / #2311 の Integration］試験の Qdrant は gRPC が応答するまで待つ

- **事象**: head `fc7b03c8` の Integration（run 40937476986 / job 123671757887）で 1 件だけ落ちた。`IngestionIndexVersionQdrantTests.版の印は最初の書き込みでコレクションごと作られ読み戻せる` が、`NewStoreAsync` → `EnsureCollectionsAsync` → `CollectionExistsAsync` で `RpcException Unavailable`（HTTP/2 の Broken pipe）になった。所要は 609 ms で、コンテナ起動後の最初の gRPC 呼び出しだった。
- **本 PR の変更は関与しない**（実測）:
  - この試験は自前の Testcontainers の Qdrant を試験ごとに起こし、`QdrantIngestionVectorStore` を直接組む。ホストも保証の器も起こさない。CI は外部の Qdrant を与えていない（`PLATFORM_TEST_*` は未設定とログにある）。
  - 本 PR は `QdrantIngestionVectorStore` を変えていない（`git diff origin/develop...HEAD` に無い）。ほかの試験のコンテナや接続を共有する経路も無い。ポートはコンテナごとに無作為に割り当てられる。
- **develop の履歴**: Integration の failed run を 10 件見た（2026-10-22〜10-29）。同じ試験の失敗も Broken pipe も 0 件だった。落ちていたのは AST の `TradeExecutionPipelineE2ETests`・platform の `TraceContinuityTests`・本 PR 前回の CS7036 で、いずれも別の原因である。
- **原因（試験の土台の競合）**:
  - Testcontainers.Qdrant 4.12.0 の既定の待機は、REST の `/readyz`（6333）だけを見る。
  - Qdrant v1.18.1 は REST と gRPC を別のスレッドで起こす（上流 `src/main.rs` の `web` と `grpc` のスレッド）。`/readyz` が 200 でも、gRPC（6334）がまだ listen していないことがある。
  - その間も Docker のポート転送は接続を受けてすぐ切る。だから最初の呼び出しが Broken pipe で落ちる。外部ポートの TCP 待機では塞げない。
  - 同じ土台（`QdrantTestImage.CreateBuilder`）を使う試験は 5 クラスあり、どれも同じ競合を抱える。
- **直し方**: `QdrantTestImage.CreateBuilder` に、gRPC の Health が応答するまで待つ待機（`QdrantGrpcReady`。上限 60 秒、1 回の問い合わせ 2 秒）を足した。1 か所で 5 クラスに効く。
- **試験**（`QdrantGrpcReadyTests`。コンテナを起こさないので ci.yml で走る）:
  - 対照: 接続を受けてすぐ切る受け口への `CollectionExistsAsync` は `Unavailable` で落ちる（CI と同じ型を再現した）。
  - 待機の問い合わせは、その相手と待ち受けの無い口を「まだ」と読む（例外を投げない）。
  - 組み立ての入口が gRPC の待機を持つ。**red→green**: 待機を外すと赤（実測）、戻すと緑。
- **限界**: 手元に Docker が無いので、実コンテナでの競合そのものは再現していない。確かめられるのは次の CI の Integration だけである。

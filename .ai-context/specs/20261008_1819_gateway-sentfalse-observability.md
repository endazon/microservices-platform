---
title: 作業仕様書 — LLM ゲートウェイの Sent=false の原因を追えるようにする（越境拒否のログ・応答の FailureKind・Voyage 鍵未設定のログ洪水の停止）（#1819）
type: spec
status: done
related_ids: [FR-11, FR-02, FR-04, NFR-17, NFR-19, NFR-28, ADR-0010, ADR-0016, ADR-0038, IADR-0104, IADR-0110, IADR-0400, IADR-0504, IADR-0256]
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md
issue: "#1819"
---

# 作業仕様書 — LLM ゲートウェイの Sent=false の可観測性（#1819）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。判断の記録は **IADR-0104 の追記**（応答契約・越境拒否のログ）と
> **IADR-0504 の追記**（鍵未設定のログ）に置き、新しい IADR は起こさない（採番が #1815 / #1816 と競合しているため）。
> 計画は project-planning `aa068ac`（隣接クローン）を読んだ。基点は MSP `origin/develop` `5748d5d9`。
> 🔴 **稼働中のクラスタには何も実行しない**（単体・端点の試験だけ）。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-11**（LLM の送信可否の統制。`Sent=false` の縮退）・**FR-02**（取り込みの埋め込み）・FR-04（AI 回答。同じ `/complete` を使う）。
- 非機能: **NFR-17**（データ越境統制・監査ログ）・**NFR-19**（可観測性）・**NFR-28**（外部由来の文字列はログへ渡す前に無害化する）。
- 計画 ADR: ADR-0010（LLM ゲートウェイ）・ADR-0016（埋め込みは Voyage）・ADR-0038（フォールバックと 429 の境界）。
- 起点 issue: **#1819**（PoC。AST の取引判断が 132 件連続で `Sent=false` を受け、原因を事後に確定できなかった）。AST 側の消費は AST#1267 が並行で直す。

## 現状（`5748d5d9` の実測）

- `CompletionUseCase` の越境拒否（`decision.Allowed == false`）の枝は計器だけを数え、**ログを出さない**。
  ルータ（`LlmRouter.Route`）は拒否のたびに `LLM routing denied` を Warning で出すが、理由の文言（`decision.Reason`）は載らず、
  件数の要約も無い。経路B には Prometheus が無いので計器は読めない。
- `CompletionApiResponse` は `Sent=false` の 3 経路（越境拒否・プロバイダ未登録・上流不調）を **`Text` の文言でしか区別していない**。
  上流の HTTP 状態はログ（`upstream status {Status}`）にしか出ない。
- `VoyageEmbeddingProvider` は鍵が空なら `InvalidOperationException` を投げ、`EmbedUseCase` の catch が **毎回スタック付きの `LogError`** を出す。
  取り込みは `Retryable=true` を受けて例外を投げ、Wolverine の再試行（2s / 10s / 30s・4 回）→ DLQ へ回る。
  **再試行は 1 メッセージ 4 回で有界であり、空回り（tight loop）ではない**（`WolverineExtensions.RetryIntervals`）。
  1,146 件は「文書数 × 4 回」の総和である（約 290 文書）。

## 設計（正は IADR-0104 / IADR-0504 の 2026-10-08 追記）

1. **応答契約に `FailureKind`（`string?`）と `UpstreamStatusCode`（`int?`）を末尾へ足す**（`CompletionApiResponse` と `CompletionStreamEvent` の両方。既定 null）。
   - 値は共有契約の `CompletionFailureKinds`（`egress_denied` / `provider_missing` / `upstream_error`）。**計器の `llm.result` と同じ文字列**にし、
     計器の定数はこの定数から取る（語彙を 2 か所に持たない）。
   - **enum にしない**（`StopReason` と同じ理由。語彙が増えたとき古い呼び出し側が既定値へ黙って落ちない）。
   - `UpstreamStatusCode` は `upstream_error` のときだけ、上流が HTTP 状態を返した場合に載る（輸送の失敗は null）。
   - `Sent=true` の応答では両方 null。`Text` / `RoutingReason` / `Sent` の意味は変えない。
   - gRPC（`completion.proto`）にも同じ項目を足す（`CompleteResponse` 9・10、`CompletionStreamEvent` 10・11。空文字 / 0 は未設定）。写像は `LlmGrpcMapping`。
   - OpenAPI（`docs/api/openapi.yaml` の `CompletionApiResponse`）に同じ 2 項目を足す（応答側なので nullable・required に入れない）。
2. **越境拒否の枝に Warning のログを足す**。載せるのは `decision.Reason`・purpose・sensitivity。
   - 頻度の抑制: **(用途, 理由) の組ごとに初回は即時、以後は 5 分ごとに 1 行**（抑えた件数を併記する要約）。抑制の状態は単一の
     `LogOccurrenceThrottle`（シングルトン・`TimeProvider`）が持つ。要約は次の発生時に出す（タイマーは持たない）。
   - purpose は呼び出し側の自由文字列なので、**抑制の鍵には値域を閉じた値**（`LlmMetricValues.NormalizePurpose`）を使い、
     ログには制御文字を落とした値（ルータと同じ無害化。`LlmRouter.Sanitize` を internal にして共有する）を載せる（NFR-28）。
   - ルータの `LLM routing denied`（監査ログ。ADR-0010）は**変えない**（呼び出しごとの監査記録であり、抑制すると監査の件数が欠ける）。
3. **鍵未設定は「埋め込みの構成が無い」状態として扱い、スタックを出さない。**
   - `VoyageEmbeddingProvider` は鍵が空のとき専用の `EmbeddingProviderNotConfiguredException`（`InvalidOperationException` の派生）を投げる。
   - `EmbedUseCase` はこれを別の catch で受け、**エンドポイントごとに初回は即時・以後 5 分ごとに 1 行の Warning（スタックなし・抑えた件数つき）**を出す。
   - **応答は変えない**（`Embedded=false`・`Retryable=true`）。取り込みは従来どおり再試行の後 DLQ へ送る —— 鍵を入れてから DLQ を再投入すれば索引へ入る。
     「鍵が無ければ埋め込みを試みない（恒久スキップ）」は採らない: 取り込みが `Retryable=false` を受けると**チャンクを捨てて完了扱い**にし、
     後から鍵を入れても索引に入らない（IADR-0504 §検討した代替案「自動で無効にする」と同じ理由）。
   - その他の上流失敗（Voyage の 5xx 等）は従来どおりスタック付きの `LogError`（本件の対象外。鍵の欠落と違い個別の調査が要る）。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| A1 | 越境拒否は Warning を 1 行出し、理由・用途・機密区分を載せる。同じ (用途, 理由) の 2 回目以降は 5 分の間出ない。5 分後の次の発生で抑えた件数つきの 1 行が出る | `EgressDeniedLogTests`（一括・逐次） |
| A2 | `Sent=false` の 6 経路すべてが `FailureKind` を持つ（一括 3・逐次 3）。上流不調は HTTP 状態を `UpstreamStatusCode` に載せる。`Sent=true` は両方 null | `CompletionFailureKindTests`・既存の端点試験 |
| A3 | gRPC の応答・イベントが同じ 2 項目を運ぶ（REST と一致） | `GrpcCompleteTests` / `GrpcCompleteStreamTests` への追加 |
| A4 | 鍵未設定の埋め込みはスタックを出さない。10 回呼んでも Warning は 1 行。応答は `Embedded=false`・`Retryable=true` のまま。外部へ送らない | `VoyageEmbeddingKeyTests` への追加 |
| A5 | 取り込みの再試行は有界（4 回 → DLQ）で、鍵未設定でも空回りしない | 既存の `WolverineExtensions` の定数（変更なし）と本仕様書の実測 |
| A6 | 契約の検査（`check-contract-schema` / `check-proto-contracts` / `check-openapi-dto-drift`）が通る。baseline は `--update` で更新する | 検査器の実走 |

## 母集合（規則 9・10。`5748d5d9` 時点）

### 規則 9-a: ゲートウェイの `Sent=false` の全経路

走査: `git grep -nE "Sent: false|Sent = false|Sent=false" -- 'src/*.cs'`（試験を除く）。

| 当たり | 扱い |
| --- | --- |
| `CompletionUseCase.cs:56`（一括・越境拒否） | **`egress_denied`**＋ログ（抑制つき） |
| `CompletionUseCase.cs:69`（一括・プロバイダ未登録） | **`provider_missing`**（ログは既存の `LogError`） |
| `CompletionUseCase.cs:137`（一括・上流不調） | **`upstream_error`**＋`UpstreamStatusCode` |
| `CompletionUseCase.cs:168`（逐次・越境拒否） | **`egress_denied`**＋ログ（一括と同じ抑制器・同じ鍵） |
| `CompletionUseCase.cs:180`（逐次・プロバイダ未登録） | **`provider_missing`** |
| `CompletionUseCase.cs:258`（逐次・上流不調） | **`upstream_error`**＋`UpstreamStatusCode`（列挙の失敗の例外から取る） |
| AiAnalysisService の `HttpLlmCompletionTransport` / `GrpcLlmCompletionTransport` / `RagOrchestrator` が合成する `done(Sent=false)` | **追随しない**: ゲートウェイに届かなかった（輸送の失敗）ことを呼び出し側が合成するもので、ゲートウェイの原因ではない。`FailureKind` は null のまま（「ゲートウェイが原因を名乗っていない」と読める） |
| GraphService / ConversionService / RetrievalService のコメント | 追随しない（`Sent` を読むだけ。項目の追加は非破壊） |

### 規則 9-b: 埋め込みの呼び出しのログの全箇所

走査: `git grep -nE "Log(Error|Warning|Information)\(" -- src/platform/backend/Services/LlmGateway`（埋め込み関係）と取り込み・検索の埋め込み呼び出し側。

| 当たり | 扱い |
| --- | --- |
| `EmbedUseCase.cs:87` `Embedding call failed at endpoint …`（`LogError(ex, …)`） | **鍵未設定だけ別の catch へ分け、抑制つき Warning（スタックなし）**。他の上流失敗は据え置き |
| `EmbedUseCase.cs:50` プロバイダ未登録（`LogError`・スタックなし） | 追随しない（構成不備。`Retryable=false` で取り込みは再試行しないため洪水にならない） |
| `EmbedUseCase.cs:66` 次元不整合（`LogError`・スタックなし） | 追随しない（同上） |
| `EmbeddingRouter.cs:77` 拒否（Warning）・`:89` 判定（Information） | 追随しない（ADR-0016 の監査ログ。1 行・スタックなし） |
| `VoyageEmbeddingProvider.cs:23` 鍵未設定の例外 | **型を `EmbeddingProviderNotConfiguredException` へ**（文言は据え置き。既存試験の `*Embedding:Voyage:ApiKey*` を保つ） |
| IngestionService `DocumentUpdatedConsumer.cs:161 / :304` `transient embedding failure, retrying via broker`（Warning・スタックなし） | 追随しない（取り込みのコンテナのログであり、ゲートウェイのログを押し出していない。1 試行 1 行） |
| RetrievalService `LlmGatewayEmbeddingService.cs:46` / `LlmGatewayGrpcEmbeddingService.cs:42`（コレクション不一致） | 追随しない（別事象） |

### 規則 10（この変更で新たに誤りになる自分の記述）

- `VoyageEmbeddingKeyTests` の「鍵が無ければ外部へ送らずに失敗する」は `ThrowAsync<InvalidOperationException>` —— 派生型なので**崩れない**。
- `docs/operations/voyage-embedding-key-runbook.md` が「ゲートウェイのログ」で鍵の欠落を確かめる手順を書いていれば、文言（`Embedding call failed` のスタック）が変わる → 追随。
- `docs/functional/FR-11_llm-egress-routing.md:44` の出力の列挙（`Text`, …, `RoutingReason`）は 2 項目が足りなくなる → 追随。
- `docs/api/east-west-grpc.md:246` の項目の列挙 → 追随。
- `docs/observability/llm-completion-metrics.md` の `llm.result` の値域は不変（定数の出どころが変わるだけ）→ 追随不要。ただし応答の `failureKind` と同じ語彙であることを 1 行足す。
- `CompletionMetricsTests` は定数名で比較している → 値は不変なので崩れない。
- 契約の baseline（`scripts/contract-schema-baseline.json` / `scripts/proto-contract-baseline.json`）は**検査器の `--update` で更新**する（手で書かない）。
- IADR-0104 の「`Sent` は変更しない」・IADR-0110 決定 2 の値域は覆らない（追記で明記）。

## 範囲外

- 要約を時間で自発的に出すタイマー（発生が止まった後の残りの件数は出ない。残余として記録）。
- 取り込み側（Ingestion）の再試行の方針の変更（有界であり変えない）。
- AST 側の消費（AST#1267）。

## 検証（2026-10-08）

- `dotnet build`（platform・knowledge の両 slnx）警告 0・エラー 0。`dotnet format --verify-no-changes`（両 slnx）差分なし。
- `dotnet test`: LlmGateway.Tests 389・Platform.Shared.Infrastructure.Tests 491・写像の利用側（AiAnalysis 180・Conversion 223＋skip 9・Graph 805・Retrieval 568・Ingestion 153）すべて緑。
- 変異（いずれも戻した）:
  1. 越境拒否のログの抑制を外す（常に記録）→ `SentFalseObservabilityTests` の要約・鍵共有の 2 本が赤。
  2. 鍵未設定の専用 catch を無効にする（従来の `LogError(ex, …)` へ落ちる）→ `VoyageEmbeddingKeyTests` の抑制の 1 本が赤。
  3. `LlmGrpcMapping.ToProto` から `FailureKind` を落とす → 写像の往復と `GrpcCompleteTests.Upstream_failure_is_a_response_not_an_error` の 2 本が赤。
- 契約: `check-contract-schema --update`（非破壊のメンバー追加 4・型追加 1）・`check-proto-contracts --update`（非破壊のフィールド追加 4）で baseline を更新。`check-openapi-dto-drift` は `openapi.yaml` へ 2 項目を足して緑。
- `check-test-spec-coverage --update`: テスト仕様書（FR-11 の T-31・T-32）に載せた 3 クラスを床へ入れた。
- IADR の索引（`.ai-context/adr/README.md`）の IADR-0104 / IADR-0504 の行は**変えない**。索引のタイトルセルへ追記を書くと
  `scripts.repo.test.js` の索引タイトルの検査（`title-addendum` / `title-too-long`）が赤になる（実測）。追記は本体だけに置き、状態列（Accepted）も不変。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 995 本緑。
- 文書: `docs/functional/FR-11_llm-egress-routing.md`（出力・例外表・新節・受け入れ基準）・`docs/tests/FR-11_llm-egress-routing.md`（T-31・T-32）・
  `docs/operations/voyage-embedding-key-runbook.md`（ログの表・5-b の数え方）・`docs/api/east-west-grpc.md`・`docs/observability/llm-completion-metrics.md`。

## 残るもの

- 要約は次の発生時に出る（タイマーなし）。発生が止まった後の最後の窓の件数はログに出ない（計器・呼び出し側の記録で補う）。
- ルータの `LLM routing denied`（監査ログ）と `Embedding routing decision`（Information）は呼び出しごとのまま。洪水の主因ではない（1 行・スタックなし）が、量は呼び出し数に比例する。
- 経路B に Prometheus が無いこと自体は本件の範囲外。

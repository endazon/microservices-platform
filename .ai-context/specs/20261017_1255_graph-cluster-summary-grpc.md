---
title: 作業仕様書 — グラフのクラスタ要約を east-west gRPC（LlmCompletion/Complete）へ移し、最後の east-west REST 同期呼び出しを撤去する（#1255 / IADR-0533 フォローアップ 2）
type: spec
status: done
related_ids: [FR-11, FR-17, NFR-09, NFR-16, ADR-0029, ADR-0035, ADR-0075, ADR-0089, IADR-0379, IADR-0400, IADR-0424, IADR-0430, IADR-0533]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md（east-west 同期は gRPC）
  - planning:projects/microservices-platform/07_adr/ADR-0089_east-west-completion-rule-and-authz-service-face.md（決定 1。撤去が移行の終わりである。段の on は条件にしない）
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md（決定 3・6）
issue: "#1255"
---

# 作業仕様書 — グラフのクラスタ要約の gRPC 化（#1255 の最後の 1 本）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。基点は MSP `origin/develop` `7fce8972`。
> 判断の記録は [IADR-0533](../adr/IADR-0533_east-west-rest-retirement.md) へ日付つきの追記で残す
> （新しい判断は無い。フォローアップ 2 が予告した移行を、既存の参照実装 `LlmGatewayGrpcSuggestionClient` と同じ形で行うだけである）。

## 起点（トレーサビリティ）

- 起点 issue: **#1255**（IADR-0533 フォローアップ 2）。部分対応ではなく、#1255 の受け入れ基準 1 の最後の 1 本である。
- 計画: ADR-0029・ADR-0089 決定 1・ADR-0075 決定 3・NFR-09・NFR-16。機能: FR-17（クラスタ要約）・FR-11（越境判定）・ADR-0035 決定 3。
- 計画の変更は要らない。

## 現状（基点 `7fce8972` の実測）

- `GraphService/Program.cs` が `AddHttpClient<IClusterSummaryLlmClient, LlmGatewayClusterSummaryClient>` を
  `Services:LlmGateway`（既定 `http://llm-gateway:5010`）へ向け、`AddLlmGatewayServiceToken`（`ServiceTokenHandler`）で s2s を付けている。
- 同じサービスは AI 提案の生成で既に `AddLlmGatewayGrpcClient` → `Pb.LlmCompletion.LlmCompletionClient`（`Services:LlmGatewayGrpc`）を登録済みである。
- 参照実装の 4 決定（IADR-0379）は graph → llmgateway について**既に満たされている**:
  1. proto: `platform/llmgateway/v1`（`LlmCompletion/Complete`）。追加・変更しない（`check-proto-contracts` の baseline は不変）。
  2. versioning: 変更なし。
  3. h2c: llmgateway の `grpcPort: 8081`（helm）・`expose 8081`＋`Grpc__Port`（compose）は在る。graph 側の宛先 `Services__LlmGatewayGrpc` も helm・compose に在る。
  4. s2s: realm の confidential client `graph-service`（`serviceAccountsEnabled`・`platform-service`・既定スコープ `platform-api-audience`）と、
     helm の `serviceToken.existingSecret: graph-service-token`、compose の `ServiceToken__*` が在る。受け手 `LlmCompletionGrpcService` は
     `[Authorize(Policy = ServiceCaller)]`（deny-by-default）。稼働 k3s の実測 L-4（graph → llmgateway `LlmCompletion/Complete`）は PERMISSIVE / STRICT とも合格（#1255 の 2026-10-10 コメント）。
- したがって本作業は**呼び出し元の実装の差し替えと、REST 側の撤去・env の掃除**に閉じる。

## 母集合の取り方（規則 9・10）

誤りの側の文字列で全追跡ファイル（`src/`（AST submodule を除く）・`deploy/`・`scripts/`・`docs/`・`.github/`・`perf/`）を引いた。

1. `LlmGatewayClusterSummaryClient` → 本体・試験・`Program.cs`・`LlmGatewayHttpClient.cs` の注記・IADR（凍結記録）。
2. `AddLlmGatewayServiceToken` / `LlmGatewayHttpClient` → 本体・`ServiceTokenHandler` 注記・`ServiceTokenHandlerTests`・`Program.cs`。
3. `ServiceTokenHandler` → 本体・`ServiceTokenHandlerTests`・注記のみ（`BffTestFactory`・`AnonymousAccessContractTests` のコメント）。
4. `Services__LlmGateway`（`Grpc` 接尾辞なし）→ helm の aianalysis（readiness・残す）と graph（**撤去**）、compose の aianalysis（残す）と graph（**撤去**）。
   `Services:LlmGateway` の読み手は撤去後 AiAnalysis の readiness（`/health/live`）だけになる。GraphService の appsettings は元からこのキーを持たない。
5. AST: submodule の pin `bc2ab989` を隣接クローンで `git grep` —— `using Platform.` は 0 件、`AddLlmGatewayServiceToken` / `LlmGatewayHttpClient` /
   `Platform.Shared.Infrastructure.Foundation.Authz` は 0 件。AST の `ServiceTokenHandler` は自前の `AiStockTrading.TestSupport.PlatformShim.Foundation.Auth` であり、MSP の型ではない。
6. `docs/` の記述: `docs/api/east-west-grpc.md`（§REST 実装の退役の「クラスタ要約は REST のまま残る」・§3 つ目の面の呼び出し元表）、
   `docs/operations/operations.md`（呼び出し側の期限の表の 2 行）。

## 処置

| # | 対象 | 処置 | 理由 |
|---|---|---|---|
| 1 | `LlmGatewayClusterSummaryClient`（REST） | **撤去**し、`LlmGatewayGrpcClusterSummaryClient`（gRPC）を新設 | 形は `LlmGatewayGrpcSuggestionClient` に揃える（共通写像 `LlmGrpcMapping`、`RpcException` と s2s 取得失敗の `InvalidOperationException` を null へ、呼び出し元の取り消しは伝播） |
| 2 | 用途名 | `graph-cluster-summary` を新クラスの `PurposeName` へ移す（値は変えない） | 費用集計の軸（IADR-0511・`scripts/lib/llm-purposes.js` の突合）を保つ |
| 3 | モデル | 指定しない（`model` は空＝未指定） | ゲートウェイの経路決定が宛先を持つ（ADR-0035 決定 3 の注記） |
| 4 | `LlmGatewayClusterSummaryClientTests` | **撤去**し、gRPC 版の試験へ写す | 写し方は下表 |
| 5 | `Program.cs` の登録 | `AddSingleton<IClusterSummaryLlmClient, LlmGatewayGrpcClusterSummaryClient>()` | 生成クライアントは `AddLlmGatewayGrpcClient` が登録済み（未構成なら `UNAVAILABLE` → null） |
| 6 | `LlmGatewayHttpClient.cs`（`AddLlmGatewayServiceToken`） | **撤去** | 呼び出し元 0（MSP・AST pin とも） |
| 7 | `ServiceTokenHandler.cs` と `ServiceTokenHandlerTests.cs` | **撤去** | 唯一の利用者が 6 だった。AST は自前の同名型を使い MSP の型を参照しない |
| 8 | helm `services.graph.extraEnv` の `Services__LlmGateway` | **撤去** | 読み手 0 |
| 9 | compose `graph-service` の `Services__LlmGateway` | **撤去** | 読み手 0 |
| 10 | `EastWestGrpcDeploymentWiringTests` | graph の組を「残る組」から「撤去した組」へ移す | 両方の向きを固定し続ける |
| 11 | LlmGateway の REST `POST /complete` | **残す**（射程外） | AST の kb-writer 等・合成監視が叩く。撤去の可否は AST の利用確認が要る（PR 本文の残課題） |
| 12 | `docs/` 2 文書 | 記述を現状へ追随（日付つき） | 「クラスタ要約は REST だけ」が誤りになる（規則 10） |
| 13 | realm の `graph-service` の description | 用途に「クラスタ要約」を足す | 主体が呼ぶ経路の記述を現物に合わせる（権限は変えない） |

## 試験の写し方

| REST の試験 | gRPC での扱い |
|---|---|
| 用途名と機密区分を添えて `/complete` を呼ぶ・モデルを固定しない | **写す**: 生成クライアントの偽物が受けた `CompleteRequest` の `purpose` / `confidentiality` / `model`（空）を表明。陽性（要約が返る）を兼ねる |
| 縮退した応答（`Sent=false`・`refusal`・空・空白）は null | **写す**（`LlmGrpcMapping.ToProto` で応答を組む。`sent` の向きの写し漏れを捕まえるため手で組まない） |
| 非 2xx → null | **写す**: `RpcException`（`Unavailable` / `Unauthenticated` / `PermissionDenied`）→ null |
| 不達（`HttpRequestException`）→ null | 上に含まれる（gRPC の不達は `Unavailable`）。加えて s2s 取得失敗（`InvalidOperationException`）→ null |
| （無かった） | **足す**: 呼び出し元の取り消しは null へ畳まず伝播する（バッチの停止を「要約が採れなかった」に化けさせない） |
| （無かった） | **足す**: DI の組み立てで `IClusterSummaryLlmClient` が gRPC 実装に解決される（REST 実装へ戻す変異を捕まえる） |

## 受け入れ基準（Given-When-Then）

- [x] Given 撤去後の develop 相当 / When `AddHttpClient` を本番コードで全走査する / Then east-west（内部サービス宛）の登録は **0 本**（残りは north-south の BFF エッジ・外部（Vault・Keycloak・Wiki.js・オブジェクトストレージ・LLM プロバイダ・SaaS）・トークン取得だけ。分類を PR に載せる）
- [x] Given クラスタ要約 / When gRPC 実装で呼ぶ / Then `purpose=graph-cluster-summary`・`confidentiality` は封の値・`model` は空で送られ、応答本文を要約として返す
- [x] Given `sent=false` / `refusal` / 空 / 空白の応答 / When 要約する / Then null
- [x] Given `RpcException` 各種・s2s 取得失敗 / When 要約する / Then 例外にせず null。Given 呼び出し元の取り消し / Then 伝播する
- [x] Given graph の DI / When `IClusterSummaryLlmClient` を解決する / Then `LlmGatewayGrpcClusterSummaryClient`
- [x] Given helm と compose の graph / When 読む / Then `Services__LlmGateway` が無く、`Services__LlmGatewayGrpc` が在る（`EastWestGrpcDeploymentWiringTests`）
- [x] Given `node scripts/check-proto-contracts.js` / Then 成功（baseline 不変）
- [x] Given knowledge と platform の両 slnx / When build・test・`dotnet format --verify-no-changes` / Then 0 警告 0 エラー・Failed=0
- [x] Given `helm template` / Then 描画が通り、graph の env から `Services__LlmGateway` だけが消える

## 未決事項・残課題

1. LlmGateway の REST `POST /complete` の受け口の撤去は**しない**。呼び出し元は AST（`RestLlmCompletionTransport` 等。AST#584 の受け皿）と合成監視であり、AST の gRPC 移行の完了を確認してから別 issue で扱う。
2. 呼び出し側の期限: REST では `HttpClient` の既定 100 秒が掛かっていた。gRPC 経路は AI 提案と同じく deadline を付けない（ゲートウェイの期限＝既定 100 秒が上限）。挙動上の差は「どちらが先に切るか」だけで、いずれも null（未要約のまま）へ落ちる。

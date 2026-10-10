---
title: 作業仕様書 — east-west の REST 同期呼び出しを退役させる（移行済み経路の REST 実装の撤去と、扇形 2 経路の REST の撤去。#1255 残射程 2・#1517）
type: spec
status: done
related_ids: [NFR-09, NFR-16, ADR-0029, ADR-0075, ADR-0086, ADR-0087, ADR-0089, IADR-0379, IADR-0426, IADR-0462, IADR-0533]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0089_east-west-completion-rule-and-authz-service-face.md（決定 1。REST 実装の退役をもって「解けた」と数える）
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md（決定 2。移行順）
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md（east-west 同期は gRPC）
issue: "#1255, #1517"
---

# 作業仕様書 — east-west の REST 同期呼び出しの退役（#1255 残射程 2・#1517）

> 本仕様書は実装着手前に作成した（着手 2026-10-10）。判断の記録は **IADR-0533** に置く。
> 基点は MSP `origin/develop` `2008b991`（#1899・#1900 のマージ後）。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: **#1255 の残射程 2**（移行済み経路の REST 実装の退役と、IADR-0379 決定 5「並走中の正は REST」の反転）と
  **#1517**（扇形 2 経路＝構成の自己申告 `/internal/introspection` とツール申告 `/internal/mcp-tools` の REST の撤去と、
  呼び出し側 `HttpEffectiveConfigCollector` / `HttpToolDeclarationSource` の撤去）。
- 計画: **ADR-0089 決定 1**（「REST 実装が残っていて構成でどちらかが選ばれるなら、その経路は解けたと数えない」・
  「段が on になることは達成の条件にしない」・「撤去が移行の終わりである」）・**ADR-0029**・**ADR-0075 決定 2**・
  NFR-09（east-west の同一性の供給）・NFR-16。計画の変更は要らない（計画は退役の時期を定めず、数え方だけを定める）。
- 前提 IADR: IADR-0379（決定 5 を本件で反転する）・IADR-0426（RAG の検索輸送。利用者トークンの転送は REST 輸送だけ）・
  IADR-0462（扇形 2 経路の gRPC 面）。
- 射程外（依頼者の指定）: 業務上の失敗の扱い（H-1 の業務失敗 → #1895、ListDocuments の UNKNOWN → #1897 / #1378）。

## PR の分け方（選択 b: 1 PR）

依頼は (a) 2 PR（#1255 と #1517）か (b) 1 PR かを選ぶよう求めた。**(b) を採る。** 理由:

1. #1517 は「並走の正の反転と同じ段で行う」と書いており、反転（IADR-0379 決定 5）の記録は 1 つの IADR で足りる。
   2 PR に割ると、先に入る側が「REST が正」の記述と矛盾する中間状態を develop に作る。
2. 触るファイルが重なる（BFF / McpServer の `Program.cs`、helm `values.yaml`、`deploy/docker-compose.yml`、
   `ConfigInspectionExtensions`）。FIFO で 2 本を流すと 2 本目が必ず rebase 衝突する。

## 母集合（引き直した手順と結果）

**手順**: `git grep -n "AddHttpClient\|CreateClient("` を src/platform・src/knowledge の本番コード（Tests を除く）で引き、
各呼び出し先が east-west（同じクラスタ内の MSP サービス）か、同じ経路の gRPC 実装が在るかで分類した。
規則 9（記憶で挙げない）に従い、誤りの側の語「並走中の正は REST」でも全文書を引いた。

### 撤去した east-west の REST 実装（19 経路）

| # | 経路 | 呼び出し元 → 先 | 撤去した REST 実装 | 残った gRPC 実装 |
| --- | --- | --- | --- | --- |
| 1 | B-1 | BFF → 認可（スコープ解決） | `AuthzScopeHttpClient` の BFF 経路（`AddPlatformAuthzScopeHttpClient`）・`BffScopeResolver` の REST 枝 | `AuthzScopeGrpcClient` |
| 2 | A-1〜A-4 | AiAnalysis / Graph / Wiki / Retrieval → 認可 | 各リゾルバの REST 枝・`AuthzScopeHttpClient`・`AuthzScopeRestLog` | `AuthzScopeGrpcClient` |
| 3 | A-5 | McpServer → 認可（登録者の配れる区分） | `AuthorizationServiceRegistrarAttributes` | `GrpcRegistrarAttributes` |
| 4 | A-6 | DataSource → 認可（利用者の実在照会） | `AuthorizationServiceUserDirectory` | `GrpcPlatformUserDirectory` |
| 5 | L-1 | Ingestion → LLM（取り込みの埋め込み） | `LlmGatewayEmbeddingService`（Ingestion） | `LlmGatewayGrpcEmbeddingService` |
| 6 | L-2 | Retrieval → LLM（問いの埋め込み・追加コレクション） | `LlmGatewayEmbeddingService`（Retrieval） | `LlmGatewayGrpcEmbeddingService` |
| 7 | L-2' | Retrieval → LLM（再順位付け。既定 off） | `HttpRerankCompletionClient` | `GrpcRerankCompletionClient` |
| 8 | L-3 | AiAnalysis → LLM（RAG の生成） | `HttpLlmCompletionTransport` | `GrpcLlmCompletionTransport` |
| 9 | L-4 | Graph → LLM（AI 提案） | `LlmGatewaySuggestionClient` | `LlmGatewayGrpcSuggestionClient` |
| 10 | L-5 | Conversion → LLM（図のコード化） | `LlmGatewayDiagramCoder`・`DiagramCoderRegistration` | `LlmGatewayGrpcDiagramCoder` |
| 11 | R-1 | AiAnalysis → Retrieval（RAG の検索） | `HttpRagSearchTransport` | `GrpcRagSearchTransport` |
| 12 | R-2 | BFF → Retrieval（属性値照会） | BFF の REST 枝 | `AttributeValuesGrpcClient` |
| 13 | G-1 | Retrieval → Graph（近傍展開。既定 off） | `GraphServiceNeighborExpander` | `GrpcGraphNeighborExpander` |
| 14 | D-1 | BFF → Document（文書の読み取り 4 種） | BFF の REST 枝 | `DocumentReadGrpcClient` |
| 15 | D-2 | Graph → Document（タグの書き戻し） | `HttpDocumentTagWriter` | `GrpcDocumentTagWriter` |
| 16 | D-3 | Graph → Document（タグ辞書） | `HttpTagDictionaryReader` | `GrpcTagDictionaryReader` |
| 17 | N-1 | Document → Notification（個人資料の通知） | `HttpPrivateNoteNotifier` | `GrpcPrivateNoteNotifier` |
| 18 | H-1 | Graph → Dashboard（知識の健全性の報告） | `HttpKnowledgeHealthReporter` | `GrpcKnowledgeHealthReporter` |
| 19 | I / M | BFF → 13 サービス（構成の自己申告）／McpServer → 3 サービス（ツール申告） | `HttpEffectiveConfigCollector`・`HttpToolDeclarationSource`・受け口 `GET /internal/introspection`・`GET /internal/mcp-tools` | `GrpcServiceIntrospectionCollector`・`GrpcToolDeclarationCollector` |

- **G-1 を含める**（依頼の「G-1 は issue と IADR-0379 から判断」への答え。理由は IADR-0533 決定 4）。
- **L-2 / H-1 / D-2** は依頼者が名指しした「輸送だけ渡す」「PERMISSIVE だけ」の経路である。業務上の失敗の扱いは射程外
  （#1895）なので、輸送だけを gRPC へ寄せ、業務の枝は変えていない。

### 除外（撤去しない）と理由

| 対象 | 理由 |
| --- | --- |
| Graph → LLM のクラスタ要約（`LlmGatewayClusterSummaryClient`。REST `/complete`・既定 off） | gRPC 実装が無い（未移行）。移行は残余 |
| BFF の north-south の 15 の名前つきクライアント | エッジの中継（#1397 の裁定）。east-west ではない |
| AST → MSP の 4 呼び出し | AST の所有。本リポジトリからは変えない（ADR-0075 決定 4） |
| 外部への呼び出し（Vault・Keycloak・オブジェクトストレージ・WikiJs・コネクタ・LLM プロバイダ） | east-west ではない |
| readiness の健康診断（AiAnalysis の `Services:RetrievalService` / `Services:LlmGateway` の `/health/live`） | IADR-0379 決定 3（readiness は HTTP のまま） |
| 呼び出し元が 0 になった REST の受け口（`/authz/scope`・`/embed`・`/complete/stream`・`/search/attribute-values`・グラフ近傍と辺の型の辞書・`/internal/notifications`・`/internal/knowledge-health/observations`・`/internal/tags/names`・`/documents/{id}/tags`） | ADR-0089 決定 1 の基準は「構成で選ばれる」であり、呼び出し元の撤去で満たされる。受け口は試験の移植が要り、一部は north-south や AST も使う（`/complete`・`/search`・`/documents`）。撤去は残余（IADR-0533 決定 5） |

## 設計（正は IADR-0533）

1. 宛先が未構成の gRPC は **`UNAVAILABLE` を返す呼び出し器**（`UnconfiguredGrpcDestination`）の上に組む。各実装の
   既存の縮退（deny-by-default・申告なし・提案 0 件・画像保持など）をそのまま通す。
2. 扇形の構成キーを一本化する: `Introspection:Services` / `Mcp:Services` の値を gRPC の宛先にする。
   旧キー `Introspection:GrpcServices` / `Mcp:GrpcServices` が残っていれば**起動を止める**（両キーを名指す例外）。
3. `ServiceTokenHandler` は `AuthzScopeHttpClient` から独立させて残す（クラスタ要約の REST が使う）。

## 配備（helm・compose）の挙動の変更

1. BFF に `Services__AuthorizationServiceGrpc` を足す（B-1 は従前 REST だった）。
2. BFF の `Introspection__Services__*`（13 件）を `:8081` へ。`Introspection__GrpcServices__*` を削除。旧キーが残ると BFF は起動しない。
3. mcp の `Mcp__GrpcServices__*` を `Mcp__Services__*` へ改名。旧キーが残ると McpServer は起動しない。appsettings の既定も `:8081`。
   mcp の `Services__AuthorizationService`（REST）を削除。
4. 全サービス: REST `GET /internal/introspection`・`GET /internal/mcp-tools` は 404 になる。
5. gRPC の宛先が未構成の配備は、REST へ戻らず `UNAVAILABLE` として縮退する。
6. Runbook §6.2 の「REST への緊急切り戻し」は使えなくなる。

## 試験

- 撤去した REST 実装の単体試験は削除し、同じ性質を gRPC 実装の試験が持つことを確かめた。REST と gRPC の同値を測っていた
  試験は gRPC の絶対値の表明へ書き換えた。HTTP のスタブを入力に使う既存の試験は、スタブの応答を生成クライアントへ
  載せ替える器（`BffTestFactory` の橋渡し・`TestRagOrchestrator`）で残した。
- `BffSharedDocumentGrpcReadTests` の「REST と gRPC の応答が一致する」は**削除**した（REST が無く、器の既定のクライアントも
  gRPC になったので同値の対が成り立たない。性質は同クラスの `所有者への_gRPC_経路の応答に共有先の写しが載る` が持つ）。
- `DocumentReadFailureLog.Folded` の引数 `grpc`（bool）を外し、輸送名は常に `grpc` を出す（WARN は残す）。
- ［2026-10-10 追記・独立監査の指摘 🟡-1 / 🟡-2］
  - **配備の配線**: `check-bff-downstreams.js` から service→service の呼び出し元を外したので、1:1 の gRPC 宛先の env を
    helm・compose から 1 行消しても CI が赤くならなかった（起動は成功したまま UNAVAILABLE へ縮退する）。
    呼び出し元 × キーの表を `EastWestGrpcDeploymentWiringTests`（Platform.Shared.Infrastructure.Tests）の 1 か所に置き、
    helm の `services.<呼び出し元>` と compose の `<呼び出し元>` の両方に、各キーが宛先の `:8081` で入っていることを固定した。
    逆向き（配備に在る `Services__*Grpc` がすべて表に載る）も固定する。変異（helm・compose で 1 行消す・ポートを 8080 にする）で赤になることを確かめた。
  - **縮退の WARN**: REST 側の `…WarnTests` の撤去で #1378 の性質（縮退を無言で畳まない・WARN に利用者 ID・属性を載せない）の表明が消えた。
    `AuthzScopeGrpcWarnTests` で 3 つの入口（`ResolveAsync` / `ResolveScopeAsync` / `TryResolveScopeAsync`）について、
    輸送の失敗と宛先の未構成は WARN を出し、正当な deny と許可は出さず、WARN に利用者 ID・属性が載らないことを固定した（変異で確認）。

## 検証

- `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` を platform・knowledge の両ユニットで実行。
  失敗は Docker を要する既存の試験（BFF の Valkey 5 件・Knowledge.IntegrationTests の 72 件）だけで、着手前の基点と同じ集合。
- node の検査器（CI が回すもののうち環境に依存しないもの）と helm の描画試験を実行。

## 残余（別 issue）

1. 呼び出し元が 0 になった REST の受け口の撤去（上の除外表の最終行）。
2. クラスタ要約（Graph → LLM）の gRPC 移行。
3. 使われなくなった REST の宛先の env（`Services__AuthorizationService`・`Services__LlmGateway` の一部）の掃除。
4. `BffScopeResolver.ResolveAsync` の未使用の引数 `IHttpClientFactory` の除去（BFF の端点が多く、本 PR の差分を膨らませる）。
5. `SearchUserContext.ForwardableCredential`（REST 輸送の転送のためだけの値）の整理。

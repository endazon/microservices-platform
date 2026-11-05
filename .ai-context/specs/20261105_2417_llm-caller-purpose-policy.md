---
title: 作業仕様書 — LLM ゲートウェイの補完で、呼び手ごとの用途とモデルの指定を許可表で判定する（段 1 は報告・段 2 で拒む。#2417）
type: spec
status: done
related_ids: [NFR-18, NFR-09, FR-11, ADR-0084, IADR-0424, IADR-0684]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2417"
---

# 作業仕様書 — 補完の呼び手ごとの許可表（#2417）

> 本仕様書は、実装に着手する前に作成した（着手 2026-11-05。`origin/develop` `170f1efb0`）。

## 事象

- 補完の門は `ServiceCaller`（`RequireRole("platform-service")`）だけである（`Complete/Endpoint.cs`・`GrpcService.cs`）。
- realm で `platform-service` を持つサービスアカウントは 13 個あり（`deploy/keycloak/microservices-platform-realm.json`）、補完を呼ばないものも含む。

## 方針（IADR-0684）

- 許可表 `Llm:CallerPolicy`。判定器 `LlmCallerPolicy` を REST と gRPC の両面から呼ぶ。段 1（既定）は報告、段 2 は 403 / PERMISSION_DENIED。

## 受け入れ基準と試験

- [x] 段 2 では、許可表に無い用途・無い呼び手・許されていないモデルの指定を 403 で拒み、許可表にある用途は通す（`CallerPurposePolicyTests`。develop の上で 4 件が赤であることを確かめてから実装した）。
- [x] 段 1 では、gRPC の面でも違反を止めずに計器へ数える（同上）。
- [x] 用途は原文で照合し、未知の用途は許可しない。未指定は `default`（`LlmCallerPolicyTests`）。
- [x] 不正な設定は起動時に落とす（同上）。

## 母集合（規則 9・10。着手前に引いた）

- 補完の呼び手（MSP）: `grep -rn "PurposeName\|Purpose =\|Purpose\b.*\"" src/knowledge --include=*.cs`。
  - AiAnalysis は `rag-answer`・`analysis`。
  - Graph は `graph-suggestion`・`graph-cluster-summary`。
  - Retrieval は `rerank`。
  - Conversion は `diagram-coding`。
  - いずれも `Model: null` で送る。
- 補完の呼び手（AST）: `backend/Shared/AiStockTrading.Shared.Contracts/Llm/LlmPurposes.cs` の 8 用途。主体は realm の `ai-stock-trading-llm-caller`。モデルを明示して送る（`HttpLlmCompletionClient`）。
- 補完を呼ばない主体（表に載せない）: bff・ingestion・wiki・datasource・mcp-server・document ほか。
- 試験の主体: `TestWebApplicationFactory` は `retrieval-service`、gRPC の器は `aianalysis-service` を名乗り、用途 `default` 等を送る。段 1 の既定では拒まないので、既存の試験は変えない。

## 対象外

- 量の上限（#2404）。埋め込みの面。段 2 への切り替え（稼働の設定）。

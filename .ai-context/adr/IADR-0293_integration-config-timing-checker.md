---
title: IADR-0293 ビルダ構築時に読まれる構成キーの検査は「未注入だと壊れる読み方」に限り、器ごとに突き合わせる
type: impl-adr
status: Accepted
related_ids: [NFR, UC-03, UC-04, UC-05, ADR-0027, IADR-0232, IADR-0286, IADR-0291]
author: claude
created: 2026-08-28
updated: 2026-08-28
---

# IADR-0293: 統合テストの構成注入タイミングの検査

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: claude（実装担当）
- 起票: #1040

## 文脈

**同型の事故を 3 回踏んだ。** 本番の `Program.cs` はトップレベル文で構成を**即座に**読むが、統合テストの器が `ConfigureAppConfiguration` でしか値を与えていないと**読み取りに間に合わない**。

| # | キー | 契機 | 症状 |
| --- | --- | --- | --- |
| 1 | `Pipeline:ConfigPath` | #455 Phase 0 U0d | 段宣言が 1 行も読まれないまま**全テストが緑** |
| 2 | `RabbitMq:ConnectionString` | #998 の Wolverine 切替後（PR #1006） | `BrokerInitializationException` |
| 3 | `ConnectionStrings:DefaultConnection` | #1012 の fail-fast 化後（#1032） | **28 件が起動に到達せず失敗** |

**散文の警告は機能しなかった。** 器はこの罠を本文中に 2 度書いており、2 回目を直した本人が 3 回目を踏んだ。規約「同型の事故が 2 回起きたら検査器を置く」の条件を明確に超えている。

`integration.yml` は develop への push と日次でしか起動しない（[[IADR-0232]] 決定 1）ため、**この型の欠陥を PR で捕まえる手段が無い**。静的走査なら PR で走らせられる —— そこに最大の価値がある。

## 決定

`scripts/check-integration-config-timing.js` を新設し、`ci.yml` の `static-checks` で PR ごとに走らせる。

### 決定 1: 落とすのは「未注入だと壊れる読み方」に限る

起票時の案は「`Build()` 前に読まれるキーをすべて要求する」だった。**採らない。** 実測すると `Build()` 前の読みには既定値つきのものが多数ある（`Services:LlmGateway` / `WikiJs:ApiKey` / `Qdrant:Host` / `Services:NotificationService` 等、8 件）。これらは未注入でも壊れないので、**全部を要求すると偽陽性の山になり、検査器ごと無視される**。

落とすのは次の 2 つに限る。3 件の事故はすべてこの形だった。

- **A（fail-fast）**: `?? throw` を伴う読み。未注入なら**ホストが起動しない**。
- **B（無言の縮退）**: 未設定時に例外を投げず黙って return する読み手（`AddPlatformPipelineConfig`）。**壊れたことがテスト結果に現れないので、A より危険であり機械でしか気付けない。**

### 決定 2: 器（host group）ごとに突き合わせる。全器の `UseSetting` を 1 つの集合にしない

🔴 **初版はここで間違えた。** テスト木全体の `UseSetting` を 1 つの集合に集める実装は、実データでは緑だったが、**基底フィクスチャから 3 件のキーを 1 つずつ外す変異試験が 3 件とも生存した** —— 同じキーを `QueueOverrideFanOutTests` と `McpToolDeclarationHosts` も与えているため、集合から消えなかったからである。

**緑は「検出力がある」を意味しない。** 変異試験を回していなければ、検出力ゼロの検査器を「置いた」と記録して終わっていた。

したがって器ごとに数える。

- **基底フィクスチャ群** …… `Fixtures/IntegrationTestFactory.cs` の `UseSetting`。`IntegrationTestFactoryBase` を継承する器と、それを `new` するテストが受け取る。
- **単独の器** …… `WebApplicationFactory<TMarker>` を直接継承する器（`McpToolDeclarationHosts` / `RagIntegrationFactory`）。**基底の値は届かないので自分で与える。**

### 決定 3: B は基底フィクスチャ群にだけ要求する

A はどの器でもホストが起動しないので普遍に要求できる。B はそうではない —— **縮退して困るかどうかは、そのテストが何を主張するかによる**。実例: `McpToolDeclarationHosts` は DocumentService / GraphService を起こすが見るのは `/internal/mcp-tools` だけであり、段宣言が読まれなくても主張は壊れない。ここに B を要求すると**無意味な注入を強いる偽陽性**になる。

事故 1 が起きたのは段を実際に流す試験群＝基底フィクスチャ群であり、**そこに限って要求する**。

### 決定 4: `ConfigureAppConfiguration` のキーを「与えている」と数えない

**最重要点である。** ここを外すと検査が過去 3 件をすべて素通りする（＝検査したつもりで何も検査していない状態）。自己試験に専用のケースを置いて固定した。

## 検出力の実測（変異試験）

| 変異 | 結果 |
| --- | --- |
| 基底から `ConnectionStrings:DefaultConnection` の `UseSetting` を外す | **KILLED** |
| 基底から `RabbitMq:ConnectionString` の `UseSetting` を外す | **KILLED** |
| 基底から `Pipeline:ConfigPath` の `UseSetting` を外す | **KILLED** |
| 単独の器（`McpToolDeclarationHosts`）から `ConnectionStrings:DefaultConnection` を外す | **KILLED** |
| 現状の develop | **緑**（器 × サービスの組 14 件・偽陽性 0） |

**この変異試験は `scripts.repo.test.js` に入れてある**（`#1040: 基底フィクスチャから UseSetting を外すと落ちる（検出力の実測）`）。検査器が将来「緑だが検出力ゼロ」へ退行しても、そこで落ちる。

## 検査しないこと（既知の限界）

- **器が条件つきで与えるキーの対応づけ**は見ていない。`RabbitMq:ConnectionString` の `UseSetting` は `if (_rabbit is not null)` の中に在り、`AuthorizationServiceFactory` は `base(pg, null)` を渡す。実測では AuthorizationService は RabbitMq を 1 度も読まない（`grep -c RabbitMq` = 0）ため齟齬は無い。**RabbitMq を読むサービスの器が null を渡す**構成が将来生まれたら本検査は素通りし、器の側が起動時に落ちる。
- 器を持たないサービス（RetrievalService / FeedbackService / DashboardService / NotificationService / McpServer / LlmGateway / Platform.Bff）は対象外である。**要求する器が無いキーを要求しても意味が無い。**
- `SILENT_DEGRADERS` は列挙である。**新しい「無言で縮退する読み手」は自動では見つからない** —— 足すときは「未設定時に無言で return するか」を実際に読んで確かめること。

## 影響

- `ci.yml` の `static-checks` に self-test ＋ 本走査の 2 ステップが増える（fs のみ・追加取得なし）。
- 検査器の母集合が 44 → 45 本になる（`scripts.repo.test.js` のラチェットが設計どおり発火した）。

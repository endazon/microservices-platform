---
title: MCP のツールの実行口が返す文書属性を、MCP サーバーが読むキーの許可リスト（共有の定数 1 か所）へ揃える（#1671）
type: spec
status: done
related_ids: [FR-16, UC-08, NFR-09, ADR-0024, ADR-0034, ADR-0117, IADR-0479, IADR-0373, IADR-0405]
author: Claude（実装）
created: 2026-09-27
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0117_mcp-tool-destination-and-execution-context.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0024_mcp-server-integration.md §4
  - planning:projects/microservices-platform/06_technical/11_mcp-server-integration.md §4・§6
---

# 仕様書: MCP のエンベロープの属性の許可リストを共有の定数へ（#1671）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-16（MCP サーバー統合）／UC-08／NFR-09（認可）
- 関連 ADR: `ADR-0024` §4（ツール応答は外部 LLM へ渡る前提。越境判定は文書の機密区分で行う）／`ADR-0117` 決定 2・3
  （実行口は各サービス。受け手が自分で認可する）／`ADR-0034` 決定 4・9（件数は判定後。サービスアカウントは個人資料を一律除外）
- 計画 `11_mcp-server-integration` §4（データ越境・機密統制）・§6（打ち切りの表現）
- 実装 ADR: [[IADR-0479]]（実行口。本 PR で日付つき追記を足す）／[[IADR-0373]] 決定 1・[[IADR-0405]] 決定 3（語彙は契約プロジェクトの 1 か所。
  `RestrictedProject` と同じ置き場・同じ理由）
- issue: #1671（本件）／#1611（実行口。段 1 = PR #1662、段 3 = PR #1668、段 2 = DocumentService は未着手）

## 起点の確認

基点 `origin/develop` `cb42524`。`git rev-parse --is-shallow-repository` = `true`（`git log` / `git blame` は出典に使わない。以下の母集合は `git grep` の実測）。

## 束ねるかの判断

1 issue = 1 PR（IADR-0116 規約 1）。束ねない。

## 母集合（規則 9。誤りの側の文字列で走査した）

誤りの側 = 「エンベロープの属性へ全キーを写す」「許可リストを受け口ごとに持つ」「MCP サーバーの読み手のキーを各所で直書きする」。

| 走査 | ヒット | 扱い |
| --- | --- | --- |
| `git grep -n "McpToolExecution.McpToolExecutionBase" -- src`（実行口） | Retrieval `Features/McpTools/Execute/GrpcService.cs`・Graph 同名・McpServer 試験の `StubMcpToolExecutionService` | Retrieval・Graph は**対象**。McpServer の試験用スタブは受け口の実装ではない（除外） |
| `git grep -n "new Pb.McpToolDocument\|new McpToolDocument(" -- src ':!**/Tests/**'`（エンベロープ写像） | Retrieval `ToResult`（全キーを写す＝**誤り**）・Graph `ToResult`（許可リスト済み。ローカルの `EnvelopeAttributeKeys`）・McpServer `GrpcToolInvoker.ToResult`（受信側。proto → ドメイン） | Retrieval・Graph は**対象**（共有の定数へ）。McpServer の受信側は**変えない**（下記「設計」の判断 3） |
| `git grep -n "Attributes\.TryGetValue\|IsPrivateNote(\|IsRestricted(\|ConfidentialityKey\|\.Attributes\[" -- src/platform/backend/Services/McpServer ':!**/Tests/**'`（属性を読む箇所） | `EgressPolicy`（`ConfidentialityKey`）・`ServiceAccountDocumentFilter`（`DocumentScope.IsPrivateNote`・`RestrictedProject.IsRestricted`）・`ToolPublicationConfigValidator`・`*RegistrarAttributes`（`TagsKey`）・`RegistrarScopeReading`（`confidentiality`） | 読み手は `EgressPolicy`・`DocumentScope`・`RestrictedProject` の 3 つ（**対象**）。`ToolPublicationConfigValidator`・`*RegistrarAttributes`・`RegistrarScopeReading` は**主体（サービスアカウント・登録者）の属性**を読むもので、エンベロープの文書属性ではない（除外） |
| `git grep -n "Documents" -- src/platform/backend/Services/McpServer/{Features,Domain,Infrastructure}`（応答を読む箇所の漏れ確認） | 上の 2 つ＋`ToolInvocationService`（件数のログのみ） | 属性を読まない（除外） |
| DocumentService の実行口 | `Features/McpTools/` に `Declare` しか無い | 段 2（#1611）で作る。**段 2 はこの定数を使う**（下記「段 2 への申し送り」） |
| REST `POST /search`・gRPC `DocumentSearch/Search`（MCP 以外の検索応答） | `SearchEndpoint` / `SearchResultDto.Attributes` | **変えない**（BFF と同じ閲覧権限の応答。#1671 の射程は MCP の経路だけ） |

## 受け入れ基準（#1671 の本文から）

1. 許可リストを**共有の定数 1 か所**（`Platform.Shared.Contracts`）に置き、MCP サーバーの読み手（`EgressPolicy`・`DocumentScope`・`RestrictedProject`）と
   受け口（Retrieval・Graph）の両方がそれを参照する。
2. Retrieval の実行口の応答から `owner`・`dept` などの許可リスト外のキーが消える。陽性対照として `confidentiality`・`doc_scope`・`project` は残る。
3. `Seal` の単体試験（`NodeAttributes` に `shared_with` が無い）と、X-51 のサービスアカウント版（刈った個人資料が全体件数に入らない）を足す。
4. 段 2 の母集合にこの許可リストを入れる（段 2 の作業仕様書はまだ無いので、本書と IADR-0479 の追記に「段 2 はこの定数を使う」と書く）。

## 設計

- **置き場**: `Platform.Shared.Contracts.Dtos.McpEnvelopeAttributes`（新設）。`RestrictedProject.DocumentKey` と同じ置き場・同じ理由
  （読み手は platform ユニットの McpServer、受け口は knowledge ユニット。ユニット外参照は `Platform.Shared.{Contracts,Infrastructure,Kernel}` だけ）。
  - `ConfidentialityKey = "confidentiality"`・`DocumentScopeKey = "doc_scope"`・`ProjectKey = RestrictedProject.DocumentKey`
  - `Keys`（`StringComparer.Ordinal` の集合。上の 3 つから組む）・`IsCarried(key)`
- **読み手が同じ定数を参照する**: `EgressPolicy.ConfidentialityKey` と `DocumentScope.Key` は `McpEnvelopeAttributes` の定数を別名で指す
  （綴りを直書きしない）。`RestrictedProject.DocumentKey` は既存の正本のまま、許可リストがそれを参照する。
- **受け口**: Retrieval の `ToResult` は `McpEnvelopeAttributes.IsCarried` のキーだけを写す。Graph の `ToResult` はローカルの `EnvelopeAttributeKeys` を撤去し、
  同じ定数を参照する。
- 判断 1（大小文字）: `Ordinal`。MCP サーバーの受信側の辞書（`GrpcToolInvoker.ToDictionary`）が `Ordinal` であり、綴りの違うキーはそもそも読まれない。
  大小文字の違う `confidentiality` を落としても越境判定は「欠落 = 送信不可」の安全側へ倒れる。個人資料の除外は受け口の 1 層目が別に持つ。
- 判断 2（試験の固定）: MCP サーバーの試験で「読み手のキーが許可リストに入る」ことを、受け口の試験で「許可リストのキーが残り、外のキーが消える」ことを固定する。
  共有の定数から 1 つ外すと**両側が赤になる**（片側だけ変わる割れ方を試験で塞ぐ）。
- 判断 3（MCP サーバーの受信側で濾すか）: **本 PR では濾さない**。#1671 の受け入れ基準は受け口側の許可リストであり、受信側で濾すと
  既存の MCP サーバーの試験（任意の属性を運ぶスタブ）の意味が変わる。受信側での三重目は残る懸念に記す。

## 段 2 への申し送り（#1611）

DocumentService の実行口（段 2）は、エンベロープの `attributes` へ `McpEnvelopeAttributes.IsCarried` のキーだけを写す。DocumentService の
`DocumentDto.Attributes` は所有者・部署等を持つので、全キーを写してはならない。段 2 の作業仕様書の母集合に `McpEnvelopeAttributes` を入れる。

## 試験の方針（テスト仕様 `docs/tests/FR-16_mcp-server.md`。develop の最大 X-51 の次から）

| # | 試験 | 場所 |
| --- | --- | --- |
| X-52 | Retrieval: 許可リスト外（`owner`・`dept`）が消え、`confidentiality`・`doc_scope`・`project` が残る（有人・サービスアカウント） | `RetrievalService.Tests` `GrpcMcpToolExecutionTests` |
| X-53 | Graph: `Seal` の `NodeAttributes` に `shared_with`（大小文字の変種も）が無い。陽性対照として他のキーは残る | `GraphService.Tests` `AuthorizedGraphViewTests` |
| X-54 | Graph: X-51 のサービスアカウント版。打ち切り時の全体件数に刈った個人資料が入らない（同じ器で有人は数える） | `GraphService.Tests` `GrpcMcpToolExecutionTests` |
| X-55 | 共有の定数: MCP サーバーの読み手のキー 3 つが許可リストに入り、許可リストはその 3 つだけ | `McpServer.Tests` |

X-16（Retrieval の陽性対照）は「属性に `dept` が載る」を確かめていたので、許可リストのキーを確かめる形へ改める（テスト仕様の同行に日付つきで記す）。

## 実施結果

### 検証（すべて前景・timeout 付き。待受は 127.0.0.1）

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/platform/backend/backend.slnx`（submodule を init） | 0 エラー・0 警告 |
| `dotnet build src/knowledge/backend/backend.slnx` | 0 エラー（警告 1 は既存の `QdrantBuilder()` 旧式化） |
| `dotnet test` McpServer.Tests | 241 件合格（新 X-55 の 2 件を含む） |
| `dotnet test` RetrievalService.Tests | 454 件合格（新 X-52 の 2 件を含む） |
| `dotnet test` GraphService.Tests | 761 件合格（新 X-53 の 3 件・X-54 の 1 件を含む） |
| `dotnet test` Platform.Shared.Infrastructure.Tests | 484 件合格 |
| `dotnet format <両ユニットの slnx> --verify-no-changes` | 両方 exit 0 |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 843 件合格（初回は `check-contract-schema` の baseline 差分〔型の追加・additive〕で赤 → `--update` で床を更新） |
| `check-trace-blocks` / `check-test-spec-coverage`（`--update` で対 +2） / `check-test-traceability` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-proto-contracts` / `gen-knowledge-graph --check` | すべて OK |
| `check-commit-messages --range=origin/develop..HEAD` | 適合 |

### 変異試験（コミット済みの状態で当て、`git show HEAD:<path>` で戻した）

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M1 | Retrieval の写像の許可リストを外す（全キーを写す） | Retrieval X-52（2 件）・X-16（2 件） |
| M2 | 共有の定数から `project` を外す | McpServer X-55（2 件）・Retrieval X-52（2 件）・Graph X-40（2 件）・X-50（2 件）—— **読み手と受け口の両側が赤** |
| M3 | `Seal` の `shared_with` 除去を外す | Graph X-53（3 件。段 3 の再監査 W4 が生き残った変異） |
| M4 | Graph の写像の許可リストを外す | Graph X-40（2 件）・X-50（2 件） |
| M5 | Graph の探索で個人資料を刈らない（写像の除去は残す） | Graph X-44・X-54 |

### 残る懸念

- MCP サーバーの受信側（`GrpcToolInvoker.ToResult`）では濾していない。受け口が許可リストを外せば、MCP サーバーはそのまま外部へ返す（受け口の試験が止める）。
- 大小文字の違うキー（例: `Confidentiality`）は運ばれない。越境判定は欠落を送信不可へ倒すので安全側だが、個人資料の除外は受け口の 1 層目に依存する。
- 段 2（DocumentService）は未着手。本書「段 2 への申し送り」と IADR-0479 の追記が引き継ぎである。

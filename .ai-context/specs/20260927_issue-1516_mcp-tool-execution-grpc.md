---
title: MCP のツール実行（HttpToolInvoker）を gRPC へ差し替え、ツール定義規約から endpoint を外す（#1516 / #1255 経路 ④-b・スライス 3）
type: spec
status: done
related_ids: [FR-16, UC-08, NFR-09, NFR-16, ADR-0024, ADR-0029, ADR-0075, ADR-0086, ADR-0117, IADR-0269, IADR-0292, IADR-0379, IADR-0462]
author: Claude（実装）
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0117_mcp-tool-destination-and-execution-context.md 決定 1・2・4
  - planning:projects/microservices-platform/10_feedback/20260926_mcp-tool-endpoint-and-execution.md
---

# 仕様書: MCP のツール実行を gRPC へ差し替える（#1516 / #1255 経路 ④-b）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-16（MCP サーバー統合）／UC-08／NFR-09（認可）／NFR-16（east-west の統一）
- 関連 ADR: `ADR-0117`（**本 PR の射程を決める裁定**。決定 1: ツール定義規約から `endpoint` を外し、実行先は申告したサービスとツール名で決める／
  決定 2: #1516 は輸送の差し替えだけ。実行口は FR-16 の別作業／決定 4: 実行口ができるまでツールの実行は fail-closed）／
  `ADR-0024` §2・§5／`ADR-0029`・`ADR-0075`（east-west は gRPC）／`ADR-0086` 決定 1（利用者文脈を本文で運ぶ。**本 PR では運ぶ中身を変えない**）
- 実装 ADR: [[IADR-0462]]（経路 ④-a の形。**本 PR は日付つき追記で ④-b へ適用する**）／[[IADR-0292]] 決定 5（実行口を作らない判断。ADR-0117 が裁定を下した旨を追記）／
  [[IADR-0379]]（置き場・versioning・h2c・s2s）
- issue: #1516（本スライス）／親 #1255（閉じない）／後続 #1611（各サービスの実行口と、本文の中身を利用者文脈へ改める）・#1517（REST 退役）

## 起点の確認

基点 `origin/develop` `1bcb0a3b`。`git rev-parse --is-shallow-repository` = `false`。

## 母集合（着手時に自分で引いた。誤りの側の文字列から）

走査の除外は `bin` / `obj` / `node_modules` / `.git` / `src/ai-stock-trading` のみ。

1. **申告の URL を作る側・運ぶ側**（`Endpoint` プロパティ・`"endpoint"`・`SelfBaseUrl`・`/internal/mcp/`）——
   McpServer の DTO（`Domain/McpToolContracts.cs`）・gRPC 収集器の写し（`GrpcToolDeclarationCollector.ToDto`）／
   3 サービス（Document / Retrieval / Graph）の DTO の写し・候補（`Candidates(selfBaseUrl)`）・gRPC 面の写し（`GrpcService.cs`）／
   proto `mcp_tool_declarations.proto` の `string endpoint = 4`
2. **申告の URL を dial する側** —— `HttpToolInvoker`（`Program.cs` で `IToolInvoker` に登録）の 1 つだけ
3. **試験** —— McpServer: `ToolInvocationServiceTests`・`ToolCatalogTests`・`GrpcToolDeclarationCollectorTests`・`McpToolDeclarationGrpcTestHost`／
   3 サービス: `McpToolDeclarationEndpointTests`（D-3・D-8）・`GrpcMcpToolDeclarationTests`（G-1）／
   `Knowledge.IntegrationTests/McpTools/McpToolCatalogIntegrationTests`（I-2）／`scripts/helm-private-notes-sync-authz.test.js`（McpServer → REST ツール呼び出しの行）
4. **構成・配備** —— `Mcp:SelfBaseUrl` は helm・compose・appsettings に **0 件**（コード既定のみ）。ツール実行の宛先の構成キーは **存在しない**（URL を申告から取っていた）。
   gRPC の宛先 `Mcp:GrpcServices:*` は helm・compose に 3 宛先ずつ在る（#1515）。McpServer の s2s 資格情報（`serviceToken` / `ServiceToken__*`）も在る
5. **文書** —— `docs/api/FR-16_mcp-server.md`（手順 4・申告の例・gRPC 収集の注記・実行口の節）・`docs/api/east-west-grpc.md`（13 つ目の面の注記・移行の残りの記述）・
   `docs/tests/FR-16_mcp-server.md`（D-3・D-8・I-2・G-1・G-5・G-12・残件）・`docs/tests/UC-08_mcp-agent-knowledge-access.md`（基本 4）／
   [[IADR-0462]]（射程外の行）・[[IADR-0292]]（決定 5）
6. **「6 項目」の語**（規則 9）—— 上記 McpServer / 3 サービスのコメントと試験名・`docs/tests/FR-16_mcp-server.md` の D-3 / G-1 / G-5

### 除外とその理由

- **各サービスのツール実行口の実装**: #1611（ADR-0117 決定 2）。本 PR は受け口を作らない。**解決済みの scope を信じる受け口を先に作らない**（同 決定 4）
- **本文の中身（解決済みの scope → 利用者文脈）の変更**: #1611（同 決定 3）。本 PR は現行の意味論（主体・種別・属性・個人資料の除外制約・必要スコープ ＋ 引数）を proto へ写すだけ
- **REST の退役**（申告の REST 口・`Mcp:Services`・DTO の写し）: #1517
- **[[IADR-0401]] / [[IADR-0418]] の `HttpToolInvoker` への言及**: いずれも当時の経路の事実（「McpServer は `/search` を呼ばない」）を述べた記録であり、本 PR の後も結論は変わらない。書き換えない
- **`CHANGELOG.md`**: 生成物（手で書き足さない）

## 受け入れ基準（#1516 の裁定コメント・ADR-0117 から）

- [x] ツール定義規約は 5 項目（`name` / `description` / `input_schema` / `required_scope` / `egress_class`）。proto の番号 4 と名前 `endpoint` を `reserved` にする
- [x] 旧い申告元が `endpoint` を載せても（REST の JSON・gRPC の番号 4）、McpServer は**無視し、決して dial しない**
- [x] ツールの実行は、公開構成で申告を突き合わせた**サービス**（`PublishedTool.Service`）と**申告名**で宛先を決め、gRPC（h2c・s2s）で送る。申告の中身から宛先を作らない
- [x] 実行口の無い宛先（`UNIMPLEMENTED`）・経路が構成されていない宛先・時間切れ・拒否は **fail-closed**（利用者には明確な拒否文言を返し、結果を返さない）。`tools/list` は申告されたツールをそのまま出す
- [x] 期限は構成（`Mcp:ToolExecutionTimeoutSeconds`、既定 30 秒、1 未満は 1）から常に有限に与える。呼び出し側の取り消しは拒否へ畳まず外へ出す
- [x] 試験の待受は 127.0.0.1 だけ
- [x] 両 slnx の build / test / format、`check-proto-contracts`、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る

## 設計

1. **proto**（新規 `platform/mcp/v1/mcp_tool_execution.proto`）: `platform.mcp.v1.McpToolExecution/Execute`。
   要求 = `tool`（申告名）・`arguments_json`（MCP クライアントの引数 JSON。文字列のまま）・`scope`（現行の意味論の写し。**#1611 で利用者文脈へ改め、番号は reserved へ**）。
   応答 = 共通エンベロープ（`documents` / `total_count` / `truncated`）。null を取り得る `body` / `reference_url` は `optional string`（原則 A）。
   宛先の URL を運ぶ項目は持たない
2. **申告の proto**: `McpToolDeclaration` から `endpoint` を外し `reserved 4; reserved "endpoint";`。破壊的変更の承認は ADR-0117 決定 1 を根拠に allowlist へ書き、`--update` で baseline へ移す
3. **DTO**: McpServer と 3 サービスの `McpToolDeclaration` から `Endpoint` を外す。3 サービスの `Mcp:SelfBaseUrl`・`Candidates(selfBaseUrl)` を外す（URL を作る理由が無くなる）
4. **呼び出し側**（McpServer）: `HttpToolInvoker` を消し、`GrpcToolInvoker` を `IToolInvoker` に登録する。
   - 宛先: `Mcp:GrpcServices:<PublishedTool.Service>`（申告の収集と同じ、申告元サービスの h2c アドレス）。無ければ fail-closed
   - 資格情報: 収集と同じ MCP サーバー自身の s2s トークン。宛先が構成されているのに発行側が無ければ起動時に落とす（収集器と同じ）
   - 失敗の畳み方: 全 status・トークン取得失敗・期限切れを `ToolExecutionUnavailableException`（利用者向けの文言を持つ）へ包み、`ToolInvocationService` が拒否（`ToolInvocationOutcome.Rejected`）へ写す。
     拒否の文言は内部のサービス名・アドレスを含めない。ログは配線不備（`UNAUTHENTICATED` / `PERMISSION_DENIED` / トークン取得失敗）を Error、それ以外を Warning
   - s2s の取得失敗の印付け（収集器の private 型）を McpServer 内の共有の内部型へ出し、収集器と実行器で 1 つを使う
5. **配備**: 新しい宛先の構成は要らない（`Mcp__GrpcServices__*` を使う）。期限のキーは appsettings.json に既定値で明示する。helm・compose は変えない

## 試験の方針

- McpServer: ループバックの実 Kestrel（`McpToolDeclarationGrpcTestHost` を拡張し、実行面の代役を持てるようにする）で往復を測る。
  申告の URL の側にも待受を立て、**接続が 0 回である**ことを数える（変異「申告の URL を dial する」で赤になることを確かめる）
- 3 サービス: 本番の `Program.cs` の h2c ポートで `Execute` が `UNIMPLEMENTED`（実行口はまだ無い。#1611 で反転する）ことを固定する
- 旧い申告元: REST の JSON に `endpoint` を足した申告・gRPC の番号 4 を持つ生のバイト列を収集させ、読み飛ばされることと dial されないことを測る

## 実施結果

### 変えたもの

| 区分 | 対象 |
| --- | --- |
| proto | `mcp_tool_declarations.proto`（`endpoint` を外し `reserved 4; reserved "endpoint";`）／新規 `mcp_tool_execution.proto`（`McpToolExecution/Execute`） |
| 契約 baseline | `scripts/proto-contract-baseline.json`（承認 `field:McpToolDeclaration.endpoint` を `$acceptedBreakingChanges` へ。allowlist は空へ戻った） |
| McpServer | `HttpToolInvoker` を削除し `GrpcToolInvoker`（＋登録 `AddMcpToolInvoker`）／`ToolExecutionUnavailableException` と単一経路での拒否への写し／`ServiceTokenFailures`（収集器の private 型を共有へ）／DTO から `Endpoint`／`appsettings.json` に `ToolExecutionTimeoutSeconds: 30`／Program.cs で実行器を起動時に 1 度組む |
| 3 サービス | DTO の写し・候補・gRPC 面の写しから `endpoint`／`Mcp:SelfBaseUrl` と `Candidates(selfBaseUrl)` を外し `Declare()` を引数なしへ／RetrievalService `SearchEndpoints.cs` のコメントに経過を追記 |
| 配備 | helm `values.yaml`・compose の McpServer の `Mcp__GrpcServices__*` の**コメントだけ**（実行も同じアドレスへ送る旨）。描画は不変（下記） |
| 文書 | `docs/api/FR-16_mcp-server.md`・`docs/api/east-west-grpc.md`（14 つ目の面）・`docs/tests/FR-16_mcp-server.md`（D-3・D-8・G-14・G-15・X-1〜X-14・変異）・`docs/tests/UC-08_mcp-agent-knowledge-access.md`・`scripts/test-spec-coverage-baseline.json` |
| IADR | [[IADR-0462]] に「経路 ④-b への適用」（2026-09-27 追記）／[[IADR-0292]] 決定 5 に ADR-0117 の裁定の追記。新しい IADR は起こしていない |
| 試験の器 | `scripts/helm-private-notes-sync-authz.test.js` の呼び出し元の一覧: 「McpServer → REST ツール呼び出し」を「McpServer → gRPC ツール実行（:8081 `/platform.mcp.v1.McpToolExecution/Execute`）」へ差し替え |

### 試験

- McpServer: `GrpcToolInvokerTests`（E-1〜E-14。新規）・`GrpcToolDeclarationCollectorTests` T-G15〜T-G17（新規）・`ToolInvocationServiceTests`（拒否への写し 3 経路・取り消し）・
  `ToolDeclarationSourceFailFastTests`（本番の登録で実行器が gRPC の実行器）
- 3 サービス: `McpToolDeclarationEndpointTests`（D-8 を「申告の JSON のキーが 5 項目ちょうど」へ差し替え）・`GrpcMcpToolDeclarationTests`（`Execute` が `UNIMPLEMENTED`。新規）
- 検証（2026-09-27、ローカル）:
  - `dotnet test src/platform/backend/backend.slnx` —— 全 7 プロジェクト Passed（McpServer.Tests 233・Platform.Bff.Tests 768 + skip 1 ほか。Failed 0）
  - `dotnet test src/knowledge/backend/backend.slnx` —— 全 12 プロジェクト Passed（DocumentService 692・GraphService 648・RetrievalService 313・Knowledge.IntegrationTests 62 + skip 46 ほか。Failed 0）
  - `dotnet format <slnx> --verify-no-changes` —— 両ユニット exit 0
  - `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` —— `✓ 841 tests passed`（初回は新規の試験ファイルが untracked で `check-test-name-references` が赤。`git add` 後に緑）
  - `node scripts/check-proto-contracts.js` —— OK（15 ファイル）／`check-trace-blocks`・`check-test-spec-coverage`・`check-cross-repo-refs`・`check-plan-id-qualification`・`gen-knowledge-graph --check` —— OK
  - `node scripts/helm-private-notes-sync-authz.test.js` —— `✓ 18 tests passed`
- 待受はすべて 127.0.0.1（`McpToolDeclarationGrpcTestHost`・各サービスの `GrpcKestrelFactory`・申告の URL 側の `TcpListener(IPAddress.Loopback, 0)`）

### 変異試験（scratchpad の実行器で 1 件ずつ当て、試験後にファイルを戻した）

| 変異 | 結果 |
| --- | --- |
| 🔴 申告の URL を再び dial する（DTO に `endpoint` を戻し、実行器がその URL の authority を宛先にする） | **赤 4 件**: `Legacy_declared_endpoint_is_never_dialled`・`Legacy_rest_declaration_with_endpoint_is_collected_without_the_endpoint`・`Proto_fields_match_the_rest_wire_names_of_the_dto`・`Declaration_carries_no_endpoint_and_field_4_is_not_reused` |
| 申告したサービスを見ず、構成の 1 つ目の宛先へ送る | **赤 2 件**: `Executes_on_the_declaring_service_over_grpc_with_the_declared_name`・`Unrouted_service_fails_closed_without_dialling_anything`（初版の E-1 は片方向だけを見ていて宛先の並びしだいで緑になった。対のツールをもう一方へ送る検査を足して赤に固定した） |
| `UNIMPLEMENTED` の枝を外す | **赤**: `Missing_execution_port_fails_closed_with_a_clear_message` |
| 実行に期限を付けない | **赤**: `Deadline_from_configuration_ends_a_silent_execution`（見張りで打ち切り） |
| 呼び出し側の取り消しを拒否へ畳む | **赤**: `Caller_cancellation_propagates_instead_of_failing_closed` |
| 単一経路が `ToolExecutionUnavailableException` を拒否へ写さない | **赤 3 件**: `実行できない下流は拒否になり結果を返さず一覧には残る`（3 経路） |
| 文書サービスの申告に `endpoint` を戻す | **赤**: DocumentService `申告は実行先のURLを持たない` |

### 配備（helm の描画）

`helm template msp <chart> -f deploy/local/values-local.yaml` を develop（`origin/develop` `1bcb0a3b` の `git archive`）と本ブランチで描画し比較した ——
**差分なし**（2741 行ずつ、sha256 `adf468a6…` で一致）。McpServer の `Mcp__GrpcServices__{document,retrieval,graph}-service` と `serviceToken`（`mcp-server-token`）は
描画に在り、実行はそれをそのまま使う。**稼働クラスタには触れていない。**

### 確かめていないこと

- 稼働 k3s での McpServer → 各サービス h2c の実行の往復（受け口が無いので `UNIMPLEMENTED` になるはず）。ループバックの実 Kestrel で代替した
- 外部エージェントから見た `tools/call` の拒否の見え方（MCP SDK が `CallToolResult.IsError` と文言をどう載せるか）は `McpCallToolHandler` の既存の写しに任せ、SDK を通した結合は測っていない

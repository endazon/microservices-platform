---
title: MCP のツール実行口を申告したサービスに作り、MCP サーバーは利用者文脈を本文で運ぶ（#1611 / 段 1: 共通部分と RetrievalService）
type: spec
status: done
related_ids: [FR-16, UC-08, NFR-09, NFR-16, ADR-0024, ADR-0034, ADR-0086, ADR-0088, ADR-0117, IADR-0479, IADR-0269, IADR-0292, IADR-0379, IADR-0426, IADR-0462]
author: Claude（実装）
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0117_mcp-tool-destination-and-execution-context.md 決定 1〜4
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1・4
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1
  - planning:projects/microservices-platform/06_technical/11_mcp-server-integration.md §2・§3・§6
---

# 仕様書: MCP のツール実行口（#1611 段 1: 共通部分と RetrievalService）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-16（MCP サーバー統合）／UC-08／NFR-09（認可）／NFR-16（east-west の統一）
- 関連 ADR: `ADR-0117`（決定 1: 宛先は申告したサービス＋ツール名／決定 2: 実行口は FR-16 の別作業＝本件／
  決定 3: MCP サーバーは自分の資格情報で呼び、本文で**利用者文脈（`user_id`・`action`）とツールの引数**を運ぶ。
  解決済みの scope を運んで信じさせる形は採らない。受けたサービスが自分で認可する／決定 4: それまでは fail-closed）／
  `ADR-0086` 決定 1・4（本文の利用者文脈。呼び出し元が `user_id` を正直に主張することへの依存は受け入れ済み）／
  `ADR-0088` 決定 1（属性は認可サービスが引き直す）／`ADR-0034` 決定 9（サービスアカウント実行は個人資料を一律除外）／
  `ADR-0024` §2〜§4
- 実装 ADR: [[IADR-0292]] 決定 5（権限伝播の裁定の切り出し）／[[IADR-0462]]（経路 ④-b の輸送）／[[IADR-0379]]（s2s・versioning）／
  [[IADR-0426]] 追記 1（`DocumentSearch` の中継者の許可集合。同じ形を実行口へ当てる）／**[[IADR-0479]]**（本 PR で新設。許可集合の既定・認可の問い方・宛先の突合・サービスアカウントの `user_id`）
- issue: #1611（本件。**本 PR は段 1**）／前段 #1516（輸送の差し替え・`scope` は暫定）／#1636・#1645（`TrustedUserContextRelay`）

## 起点の確認

基点 `origin/develop` `632664e1`。`git rev-parse --is-shallow-repository` = `false`。

## 束ねるかの判断（IADR-0139 決定 1）

**束ねない。** 3 サービスの実行口は 1 PR に入れず、段に分ける。**本 PR（段 1）は共通部分と RetrievalService**、
段 2・段 3 で DocumentService・GraphService の実行口を足す（`Refs #1611`。#1611 は段 3 で閉じる）。

| 条件 | 判定 | 理由 |
| --- | --- | --- |
| A. 同一資源 | ✗ | 契約（`McpToolExecution/Execute`）は 1 つだが、背後の資源は 3 つ（検索索引・文書台帳・グラフ）で、認可の実施点も別（検索は `SearchAccessResolver`、文書は台帳の読み取り＋組織文書の ABAC を新たに持つ必要、グラフはホップごとの判定） |
| C. 非破壊側 | ✗ | proto の `scope`（番号 3）を reserved へ移し、`McpToolInvocationScope` を削る（破壊的。allowlist の承認を要する） |
| F. 契約の追加に閉じる | ✗ | 各サービスに認可の実施と応答の写像（本体の実装）が入り、配備の許可集合の配線も要る |

条件 A・C・F を満たさないので束ねない。共通部分（proto・MCP サーバーの呼び出し側・許可集合の形）は最初の 1 本に置く ——
段 2・3 は受け口を 1 つずつ足すだけになる。

## 母集合（規則 9。誤りの側の文字列で走査した）

走査の除外は `bin` / `obj` / `node_modules` / `.git` / `src/ai-stock-trading`、および凍結記録 `.ai-context/specs/`。

1. **`#1611` を「未来の作業」として名指す記述**（`git grep '#1611'`）——
   proto `mcp_tool_execution.proto`（3 箇所）／`GrpcToolInvoker.cs`（2）／`ToolInvocationService.cs`（1）／`GrpcToolInvokerTests.cs`（1）／
   `McpToolDeclarationGrpcTestHost.cs`（1）／3 サービスの `GrpcMcpToolDeclarationTests.cs`（各 1）／`IADR-0292`（1）・`IADR-0462`（4）／
   `deploy/docker-compose.yml`（1）・`deploy/helm/.../values.yaml`（1）／`docs/api/FR-16_mcp-server.md`（trace）
2. **本文の暫定 `scope` を作る側・運ぶ側**（`ToolInvocationScope`・`McpToolInvocationScope`・`request.Scope`）——
   McpServer の `Domain/ToolInvocation.cs`・`ToolInvocationService.cs`・`GrpcToolInvoker.cs`（`ToRequest`）／
   試験 `ToolInvocationServiceTests`・`LogForgingSanitizationTests`・`GrpcToolInvokerTests`・`McpToolDeclarationGrpcTestHost`
3. **「受け口はまだ無い」「UNIMPLEMENTED」を固定する記述**（`受け口はまだ`・`実行口はまだ`・`Unimplemented`）——
   `docs/api/FR-16_mcp-server.md`・`docs/api/east-west-grpc.md` §14 つ目の面・`docs/tests/FR-16_mcp-server.md`（X-3・X-9・X-14）／
   3 サービスの `Tool_execution_port_is_not_served_yet_and_returns_unimplemented`
4. **旧い申告先の路に触れる記述** —— RetrievalService `Features/Search/SearchEndpoints.cs` のコメント

**本 PR で直すもの**: 1・2 の全件、3 のうち RetrievalService の試験と文書の記述（「3 サービスとも無い」→「Retrieval には在る。文書・グラフは段 2・3」）、
4 は「McpServer はこの群を叩かない」の記述が引き続き正しいので触らない（実行口は別の gRPC 面）。
**除外（直さない）**: DocumentService / GraphService の `UNIMPLEMENTED` の試験（段 2・3 で反転する。今も正しい）／`.ai-context/specs/` の凍結記録。

**この変更で新たに誤りになる自分の記述（規則 10）**: proto・`GrpcToolInvoker` の「受け口はまだどのサービスにも無い」→「文書・グラフには無い」へ。
compose・helm の「各サービスの受け口は #1611 まで無い」→ 同上。

## 受け入れ基準（#1611 の本文と ADR-0117 から）

- AC-1 **実行口（Retrieval）**: `platform.mcp.v1.McpToolExecution/Execute` を h2c で受け、`tool` が**自分の申告名**なら実行する。
  自分の申告に無い名前（他のサービスのツールを含む）は `NOT_FOUND`。面は `ServiceCaller` を要求する。
- AC-2 🔴 **MCP サーバー以外の主体は拒否**: 本文の利用者文脈を信じるのは許可集合（既定 `mcp-server` だけ。`TrustedUserContextRelay` と同じ形）の
  機械クライアントが運んだときだけ。他の `platform-service` の主体・接頭辞や大小文字の変種・人のトークンは `PERMISSION_DENIED`。
  拒否した呼び出しでは認可の問い合わせを 1 度も行わない。
- AC-3 🔴 **自分で認可する**: 本文の `user_id` で認可サービスへ判定を問う（属性は送らない。認可サービスが引き直す）。
  **利用者に権限の無い文書は返らない**・`granted=false` なら 1 件も返らない・件数は判定後の件数。
- AC-4 🔴 **本文の scope は効かない**: proto から `scope` を外し番号 3 と名前を reserved にする。旧い呼び出し元が番号 3 に
  scope を載せても、引数に `scope` / `filters` を書いても、結果は変わらない（未知のフィールドとして読み飛ばす）。
- AC-5 `action` は受け口が自分のツールから決める（検索は `read`）。本文の `action` が違えば `INVALID_ARGUMENT` —— 本文の `action` で判定を緩めさせない。
- AC-6 利用者文脈が無い・`user_id` が空・引数が不正（`query` 無し・`limit` が 1〜50 の外・JSON オブジェクトでない）は `INVALID_ARGUMENT`（丸めない）。
- AC-7 🔴 **サービスアカウント実行は個人資料を返さない**（ADR-0034 決定 9 の要求側）: `user_id` が `service-account-` で始まるなら
  個人資料（`doc_scope=private-note`）を落とす。MCP サーバーの応答側のフィルタ（2 層目）は残す。
- AC-8 **MCP サーバー（呼び出し側）**: 本文で運ぶものを `user`（`user_id`・`action`）とツールの引数へ改める。
  `user_id` は有人なら利用者名（`preferred_username`）、サービスアカウントなら `service-account-<client>`。
  有人で利用者名が無ければ実行を送らず拒否する（主体の分からない実行を下流へ送らない）。
- AC-9 **配備**: 既定の許可集合 `mcp-server` が compose・helm の MCP サーバーの s2s の client と一致し、realm に機密クライアントとして在ることを
  配線試験で固定する。compose・helm は集合を上書きしない。

## 設計

- **proto**: `ExecuteMcpToolRequest` の `scope = 3` を削り `reserved 3; reserved "scope";`、`McpToolUserContext user = 4`（`user_id`・`action`）を足す。
  `McpToolInvocationScope` は削る。破壊的変更 2 件は allowlist で承認する（受け手は Retrieval が初、呼び出し元は MCP サーバー 1 つで同じ PR）。
- **MCP サーバー**: `ToolInvocationScope` を `ToolUserContext(UserId, Action)` に置き換え、`ToolInvocationService` が主体から組む。
  属性・必要スコープ・除外制約は運ばない（除外は `service-account-` の利用者名から受け手が導く。応答側のフィルタは従来どおり）。
- **Retrieval の受け口**（`Features/McpTools/Execute/`）: 許可集合（`McpToolExecutionRelayOptions`。節 `McpToolExecution`）→ 利用者文脈の検証 →
  自分の申告との突合 → `action` の突合 → 引数の検証 → `ISearchAccessResolver.ResolveForUserAsync`（属性は空）→ 既存の `SearchEndpoint.ExecuteAsync`（同じ関数）→
  サービスアカウントなら個人資料を落とす → 文書単位へ畳む（同じ文書のチャンクは最上位の 1 つ）→ 共通エンベロープ。
  申告の gRPC 面と同じく `MapMcpToolEndpoints` で対に張る。
- **IADR**: 許可集合の既定・認可の問い方（属性を送らない・`action` は受け手が決める）・宛先の突合・サービスアカウントの `user_id` の形と除外の導き方を新設 IADR に残す。

## 配備の順番

1. 本 PR（段 1）をマージ・配備する。**MCP サーバーと Retrieval は同時に上げる**（同じ chart）。
   先に MCP サーバーだけが上がっても、旧い Retrieval は `UNIMPLEMENTED` で拒否する（fail-closed のまま）。
   先に Retrieval だけが上がっても、旧い MCP サーバーの要求は `user` を持たず `INVALID_ARGUMENT` で拒否される（番号 3 の scope は読み飛ばす）。
   **どちらの順でも緩む向きは無い。**
2. 段 2（DocumentService）・段 3（GraphService）は同じ proto に受け口を足すだけで、MCP サーバーの変更は要らない。

## 試験の方針

- Retrieval: 本番の `Program.cs` のまま実 Kestrel の h2c（`GrpcKestrelFactory`。127.0.0.1）で往復する。陰性（拒否）と陽性対照を同じ器・同じ索引で対にする。
  番号 3 への scope の注入は、生のバイト列の gRPC 呼び出し（`Method<byte[], byte[]>`）で実際にワイヤへ載せる。
- MCP サーバー: 要求の組み立て（`user` の中身・項目名）と、主体からの `user_id` の組み方（有人・サービスアカウント・名前の無い有人）。
- 配線: compose・helm・realm を読み、既定の許可集合と MCP サーバーの client が一致することを固定する。
- 変異試験（3 件以上）: 許可集合の検査を外す／認可の問い合わせ結果を無視する（権威スコープの代わりに全許可）／本文の `action` の突合を外す 等。

## 実施結果

基点は rebase 後 `origin/develop` `be507d19`（#1661 の着地後。`docs/api/east-west-grpc.md` の trace ブロックだけ衝突し、両方の値を併せた）。

### 検証（すべて前景・timeout 付き。待受は 127.0.0.1）

| 検査 | 結果 |
| --- | --- |
| `dotnet build platform/backend/backend.slnx` | 警告 0・エラー 0 |
| `dotnet test` McpServer.Tests | 237 件合格 |
| `dotnet test` RetrievalService.Tests | 448 件合格（新規 58 件の McpTools 系を含む） |
| `dotnet test` DocumentService.Tests / GraphService.Tests（proto の変更の影響確認。`UNIMPLEMENTED` の試験は据え置き） | 825 件 / 692 件合格 |
| `dotnet format --verify-no-changes`（platform・knowledge） | 差分なし |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 841 件合格 |
| `check-trace-blocks` / `check-test-spec-coverage`（床を `--update`）/ `check-test-traceability` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-proto-contracts`（破壊的 2 件を承認し baseline へ）/ `gen-knowledge-graph --check` / `check-commit-messages --range=origin/develop..HEAD` | すべて OK |

🔴 **knowledge の slnx 全体の `dotnet build` は実施していない。** 作業環境の一時領域の空きが 1GB 前後しかなく（同じ領域に他の作業の clone が多数あり、
最初の全体ビルドで領域が満杯になった）、変更したプロジェクトとその試験（Retrieval・Document・Graph）を 1 つずつビルド・試験し、そのたびに自分の
`bin` / `obj` を消して進めた。knowledge の他のサービスは変更した proto の型（`platform.mcp.v1` の実行の契約）を参照していない。CI の全体ビルドで確かめる。

### 変異試験（コミット済みの状態で当て、`git show HEAD:<path>` で戻した）

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M1 | 受け口の許可集合の検査（`EnsureTrustedRelay`）を外す | `MCPサーバー以外の主体からの実行はPERMISSION_DENIED`（6 件）・`MCPサーバーの変種や人のトークンは信じない`（4 件） |
| M2 | 認可の問い合わせ結果を無視して全許可にする | `利用者に権限の無い文書は返らず件数にも含まれない`・`旧い番号3のscopeをワイヤへ載せても効かない`・`引数にscopeを書いても効かない`・陽性対照ほか（計 8 件） |
| M3 | 本文の `action` の突合を外す | `操作がツールの要する操作と違えばINVALID_ARGUMENT`（3 件） |
| M4 | サービスアカウント実行の個人資料の除外を外す | `サービスアカウント実行は個人資料を返さない` |
| M5 | MCP サーバーがサービスアカウントの `user_id` に client の識別子をそのまま運ぶ | `サービスアカウント実行ではservice_accountの利用者名と読み取りの操作だけを下流へ運ぶ` |
| M6 | MCP サーバーが利用者名の無い有人を `sub` へ読み替えて送る | `有人実行で利用者名が無ければ下流を呼ばず拒否する` |

「本文の scope を信じる」変異は、proto から `scope` を外したため書けない（受け口に読む項目が無い）。代わりに旧い番号 3 を実際にワイヤへ載せる試験と M2 で、
受け口が自分で引いたスコープだけが効くことを固定した。

### 残る懸念

- サービスアカウントの属性: MCP サーバーの登録簿で割り当てた属性は下流の判定に使われなくなり、認可サービスが IdP の `service-account-<client>` から引き直す（ADR-0088）。
  登録簿の属性と IdP の属性の関係は計画に定めが無い（[[IADR-0479]] §結果）。個人資料は 2 層で落ちるので緩む向きではない。
- proto の破壊的変更の承認（allowlist の `approvedBy`）は、ADR-0117 決定 3 と #1611 の本文を根拠に記入した。レビューで利用者の確認を受けること。
- MCP サーバーと受け口を同じ器で動かす結合試験は無い（ユニットをまたぐ器が無い）。

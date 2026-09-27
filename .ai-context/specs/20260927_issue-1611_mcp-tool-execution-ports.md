---
title: MCP のツール実行口を申告したサービスに作り、MCP サーバーは利用者文脈を本文で運ぶ（#1611 / 段 1: 共通部分と RetrievalService）
type: spec
status: done
related_ids: [FR-16, UC-08, NFR-09, NFR-16, ADR-0024, ADR-0034, ADR-0086, ADR-0088, ADR-0117, ADR-0121, IADR-0479, IADR-0483, IADR-0269, IADR-0292, IADR-0379, IADR-0426, IADR-0462]
author: Claude（実装）
created: 2026-09-27
updated: 2026-09-28
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

---

## ［2026-09-27 追記 / #1611］段 3: GraphService の実行口

> 段 1 の記録（上）は書き換えない。本節は段 3 の着手前に書き、実施結果を末尾に足した。

### 起点・段の並び

- 基点 `origin/develop` `b633e303`（段 1＝#1662 の着地後）。`git rev-parse --is-shallow-repository` = `false`。ブランチ `feat/FR-16-mcp-tool-execution-graph`。
- **段 2（DocumentService）は #1615（内容の ABAC）の後ろへ回した** —— DocumentService の読み取りには内容の ABAC がまだ無く、実行口を今の判定点に
  乗せると組織文書が属性で絞られないため。**本 PR（段 3）は `Refs #1611` で、#1611 は段 2 で閉じる。**
- 束ねない判断（段 1 の表）は変わらない。本 PR は GraphService の受け口だけを足し、proto・MCP サーバーは変えない。

### 母集合（規則 9。誤りの側の文字列で走査した）

走査語: `#1611`・`段 2・3`・`後続の段`・`文書・グラフ`・`DocumentService・GraphService`・`UNIMPLEMENTED`/`Unimplemented`。
除外は段 1 と同じ（`bin` / `obj` / `node_modules` / `.git` / `src/ai-stock-trading`、凍結記録 `.ai-context/specs/`、生成物 `CHANGELOG.md`）。

1. **「グラフには受け口が無い」と書く記述** —— proto `mcp_tool_execution.proto`（■ 受け口の段落）／`GrpcToolInvoker.cs`（2 箇所）・`ToolInvocationService.cs`（1）・
   `GrpcToolInvokerTests.cs`（1）の `［#1611 段 1］文書・グラフ`／compose・helm の `document・graph は後続の段まで拒否`／
   `docs/api/FR-16_mcp-server.md` の注記／`docs/api/east-west-grpc.md` §14 つ目の面／`docs/tests/FR-16_mcp-server.md` X-14・未実施・残件／
   IADR-0462 の追記（「文書・グラフは後続の段まで」）／IADR-0479 §残るもの
2. **グラフの `UNIMPLEMENTED` を固定する試験** —— GraphService `GrpcMcpToolDeclarationTests.Tool_execution_port_is_not_served_yet_and_returns_unimplemented`
3. **近傍展開の本体**（`ExpandNeighborsUseCase` / `GraphTraversal` / `GraphViewResponse.Seal`）—— 実行口が再利用する既存の経路（変更は下の「設計」の 2 点だけ）

**本 PR で直すもの**: 1 の全件（「文書だけが無い」へ）・2 の反転。IADR-0462 は凍結記録の追記ブロックなので本文を直さず、IADR-0479 側の追記で段 3 を記録する。
**除外（直さない）**: DocumentService の `UNIMPLEMENTED` の試験（段 2 で反転する。今も正しい）。
**この変更で新たに誤りになる自分の記述（規則 10）**: 1 を「文書だけ」へ直した記述は段 2 で再び誤りになる（段 2 の母集合へ引き継ぐ）。

### 受け入れ基準（段 3）

- G-1 **実行口（Graph）**: `platform.mcp.v1.McpToolExecution/Execute` を h2c で受け、自分の申告名（`graph.get_backlinks` / `graph.get_links` / `graph.traverse`）だけを実行する。
  他は `NOT_FOUND`。面は `ServiceCaller` を要求し、申告の口（`MapMcpToolEndpoints`）と対で張る。
- G-2 🔴 **MCP サーバー以外は拒否**: 許可集合 `McpToolExecution:TrustedUserContextClients`（既定 `mcp-server`。共有 `TrustedUserContextRelay`。
  `Program.cs` で `ThrowIfScalar` → `Configure`）。他の `platform-service` の主体・変種・人のトークンは `PERMISSION_DENIED` で、認可の問い合わせを 1 度も行わない。
  近傍展開の面（`GraphNeighbors:`。既定 `retrieval-service`）とは集合を共有しない。
- G-3 🔴 **自分で認可する・第二の判定点を作らない**: 本文の利用者名で `IGraphAccessResolver.ResolveForUserAsync`（属性は空・操作は `read`）を問い、
  既存の `ExpandNeighborsUseCase`（検証 → 認可 → `AuthorizedNode` の型ゲートによるホップごと ABAC → `Seal`）をそのまま通す。
  権限外の文書は返らず、**権限外の文書を橋にした先も返らない**。`granted=false`・認可サービス不達（既存の縮退で `granted=false`）は 1 件も返らない。
- G-4 🔴 **本文の scope は効かない**: 旧い番号 3 をワイヤへ載せても、引数に `scope` / `filters` を書いても結果は変わらない。
- G-5 `action` は受け口が決める（3 ツールとも `read`）。違えば `INVALID_ARGUMENT`。利用者文脈が無い・空も `INVALID_ARGUMENT`。
- G-6 **引数は丸めない**: `document_id`（必須・GUID）、`hops`（traverse のみ。整数 1〜3・既定 2）、`edge_types`（traverse のみ。GUID の文字列の配列）。
  外れは `INVALID_ARGUMENT`。境界ちょうど・省略は通る。上限・既定は申告と同じ値（`GraphTraversal` の定数）。
- G-7 🔴 **サービスアカウント実行は個人資料を返さない・橋にもしない**（ADR-0034 決定 9 の要求側）: 利用者名が `service-account-` で始まるなら、
  個人資料を起点・中継・結果のどこにも使わない。有人には同じ点で返る（対照）。
- G-8 **応答**: 共通エンベロープ。`traverse` は起点を除く到達文書、`get_links` は起点が参照する文書（起点 → 相手の辺）、`get_backlinks` は起点を参照する文書（相手 → 起点の辺）。
  題名と属性を返し、本文・参照リンクは持たない（グラフは本文を持たない）。件数は判定後の件数、表示上限で打ち切ったら `truncated`。
  起点が見えない・無いは空（存在秘匿。区別しない）。
- G-9 **取り消し**は取り消しのまま外へ出る（#1646 の規約。既存の経路が `OperationCanceledException` を畳まない）。
- G-10 **配備**: 既定の許可集合が compose・helm の MCP サーバーの s2s の client と realm の機密クライアントに一致し、実行の宛先に graph-service の h2c が在る（配線試験）。

### 設計（段 3）

- **受け口**（`Features/McpTools/Execute/`）: Retrieval の型と同じ判定順。引数の解釈の後、`GraphUserContext(userId, 空の属性, IsAuthenticated: true)` を組んで
  `ExpandNeighborsUseCase.ExecuteAsync` を呼ぶ（`get_backlinks` / `get_links` は `hops=1`、`traverse` は指定または既定）。**判定器は増やさない。**
- **既存の経路への変更は 2 点だけ**:
  1. `ExpandNeighborsUseCase` / `GraphTraversal` に **`excludePrivateNote`（既定 false）** を足す。true なら起点と各ホップの相手が個人資料のとき、
     非許可と同じくその場で刈る（**展開・計数より前**）。応答から落とすだけだと、個人資料を橋にして到達した文書が残る（サービスアカウントに関係の存在を漏らす）。
     REST・近傍展開の gRPC は既定 false のままで挙動は変わらない。
  2. `GraphViewResponse` に **`Seal` が通したノードの属性**を内部の項目（直列化しない）として持たせる。MCP サーバーの 2 層目（個人資料・制限プロジェクトの除外）と
     越境判定（機密区分）は属性を読むため、属性の無い応答は越境判定が安全側（本文なし）へ倒れるだけでなく 2 層目が効かなくなる。REST の応答の形は変えない。
- **応答の写像**: `Seal` 済みのノードと辺だけから作る（未フィルタの部分グラフには触れない）。ノード・辺の順序は探索の到達順。辺の向きは `Edge` の
  `Source → Target`（バックリンクは Target の逆引き。対称型の辺は正規化順で向きが決まる）。
- **IADR**: 新設しない。IADR-0479 に段 3 の追記を足す（受け口の形・既存の経路への 2 点の変更・段 2 の後回し）。

### 試験の方針（段 3）

- GraphService の既存の器 `GrpcKestrelFactory`（本番の `Program.cs`・実 Kestrel の h2c・127.0.0.1・本物の JwtBearer）で往復する。器の解決器は
  `ScopeFor` で答えを決め `ResolvedFor` に問い合わせを記録する（判定の位置の観測点）。陰性と陽性対照を同じ器・同じ点で対にする。
- テスト仕様書 `docs/tests/FR-16_mcp-server.md` の行は **X-40〜X-49**（段 1 は X-28 まで、段 2 は X-30〜X-39 を予約）。
- 変異（3 件以上。コミット済みの状態で当てて `git show HEAD:<path>` で戻す）: 許可集合の検査を外す／認可の結果を無視する（`Granted` を真にする）／
  ホップごとの判定を外す（`AuthorizedNode.Authorize` を素通しにする）／サービスアカウントの刈り込みを外す／操作の突合を外す。

### 実施結果（段 3）

基点 `origin/develop` `b633e303`（push 前に取り直して差分なし）。実装コミットは `feat(FR-16,ADR-0117,ADR-0086): …グラフサービスに作り…`。
設計からの追加 1 点: `graph.traverse` の申告の説明から「辺を返す」を外した（応答のエンベロープは文書の並びしか持たない。IADR-0479 段 3 の追記）。

#### 検証（すべて前景・timeout 付き。待受は 127.0.0.1）

| 検査 | 結果 |
| --- | --- |
| `dotnet build knowledge/backend/backend.slnx`（`--no-incremental`） | エラー 0。警告 1 件は既存（`Knowledge.IntegrationTests` の `QdrantBuilder()` の廃止予告。本 PR は触れていない） |
| `dotnet build platform/backend/backend.slnx` | 警告 0・エラー 0 |
| `dotnet test` GraphService.Tests | 748 件合格（新規・反転の McpTools 系 69 件を含む） |
| `dotnet test` McpServer.Tests / Platform.Shared.Infrastructure.Tests | 237 件 / 484 件合格 |
| `dotnet format --verify-no-changes`（knowledge・platform） | 差分なし |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 841 件合格 |
| `check-trace-blocks` / `check-test-spec-coverage`（`--update` の差分なし。新しい試験クラスは段 1 と同名で、仕様書 × クラスの対は増えない）/ `check-test-traceability` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-proto-contracts`（コメントだけの変更で baseline と差分なし）/ `gen-knowledge-graph --check` / `check-commit-messages --range=origin/develop..HEAD` | すべて OK |

#### 変異試験（コミット済みの状態で当て、`git show HEAD:<path>` で戻した）

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M1 | 受け口の許可集合の検査（`EnsureTrustedRelay`）を素通しにする | `MCPサーバー以外の主体からの実行はPERMISSION_DENIED`（6 件）・`MCPサーバーの変種や人のトークンは信じない`（4 件） |
| M2 | 認可の問い合わせ結果を無視して全許可にする（`ExpandNeighborsUseCase`） | 陽性対照（2）・`権限の無い文書もそれを橋にした先も返らない`・`許可が無い起点が見えない起点が無いはどれも空で区別できない`・`旧い番号3のscopeや引数のscopeは効かない`・`サービスアカウント実行は個人資料を返さず橋にもしない`・引数の境界（4）（計 10 件） |
| M3 | ホップごとの判定を外す（`GraphTraversal` の展開で許可を問わずに `AuthorizedNode` を作る＝「探索してから濾す」形。出力の門は残る） | `権限の無い文書もそれを橋にした先も返らない`・`旧い番号3のscopeや引数のscopeは効かない`・陽性対照（2）ほか（計 8 件） |
| M4 | サービスアカウント実行の探索中の刈り込みを外す（応答の写像での除外は残る） | `サービスアカウント実行は個人資料を返さず橋にもしない`（個人資料の先 Q が浮上する） |
| M5 | 本文の `action` の突合を外す | `操作がツールの要する操作と違えばINVALID_ARGUMENT`（3 件） |

#### 残る懸念（段 3）

- `graph.traverse` は辺を返さない（エンベロープの改定は計画 ADR-0024 §4 の範囲）。到達文書は返るので緩む向きではない。
- 被参照・参照先は 1 ホップの結果を辺の向きで選ぶ。対称型の辺（`related`）は書き込み時に文書 ID の昇順へ正規化されるため、向きは意味を持たず、
  どちらか一方のツールにだけ現れる（近傍探索には両方の向きで現れる）。
- 認可サービス不達は既存の `GraphAccessResolver` の縮退（`Granted=false`）で空の応答になる。拒否（status）ではなく空で返るのは近傍展開の gRPC と同じであり、
  「結果を返さない」は満たすが、MCP クライアントからは「該当なし」と区別できない（存在秘匿と同じ形）。
- 取り消しは既存の経路が `OperationCanceledException` を畳まないことに依る（受け口は捕まえない）。受け口の取り消しを直接起こす試験は置いていない。
- MCP サーバーと受け口を同じ器で動かす結合試験は無い（段 1 と同じ）。

### ［2026-09-27 追記 / #1611］段 3 の監査（PR #1668、NO-GO）への対応

- **B-1（ブロッキング）: 他人の共有先が MCP クライアントへ渡っていた。** `GraphDocument.Attributes` は同期時に `shared_with` を重ねた ABAC 判定用の像であり
  （`GraphDocumentSyncConsumer.AbacAttributes`）、段 3 の初版は `NodeAttributes` がそれを丸ごと複製し、写像が全キーをエンベロープへ載せていた。
  共有先は所有者にだけ返す規則（ADR-0098 / IADR-0450）を MCP のグラフ経路だけが迂回していた。
  **エンベロープの属性は許可リストの文書属性だけ。共有先は運ばない。** 直し方:
  1. 写像の許可リスト `EnvelopeAttributeKeys` = `confidentiality`・`doc_scope`・`project`。根拠は McpServer のコードで属性を読む箇所の走査
     （`git grep` の `Attributes.TryGetValue` / `IsPrivateNote` / `IsRestricted`）: `EgressPolicy.ConfidentialityKey`（越境判定）、`DocumentScope.Key`
     （2 層目の個人資料の除外）、`RestrictedProject.DocumentKey`（2 層目の制限プロジェクトの除外）の 3 つだけ。登録簿の属性（`clearance`・`tags`）は
     文書ではなく主体の属性なので対象外。
  2. 多層防御として `Seal` が作る `NodeAttributes` からも `AttributeValueKeys.SharedWith` を除く（判定用の像でなく文書の属性だけ）。
  3. **`owner`（N-2）は落とす** —— McpServer は読まない（上の走査で 0 件）。
  - **段 1（Retrieval）との差**: Retrieval は索引の入れ子 `attributes` を全キー返す（`QdrantVectorStore.ExtractAttributes`）。共有先は入れ子の外
    （ペイロード直下）なので入らないが、`owner`・部署等は返る。グラフは許可リストに絞ったので、返すキーは Retrieval より狭い。
    Retrieval は本 PR では変えない（揃えるかは別件として残る懸念に書く）。
- **N-1**: `graph.traverse` は表示上限で打ち切ったら許可済みの全体件数（`TotalNodes` − 起点）を `total_count` に返す。被参照・参照先は向きごとの全体件数を
  本体が数えないので、打ち切り時も返した件数のまま（`truncated` で示す）。
- **N-4**: 本体の失敗を `INTERNAL` で返すときは固定文言にした（`Error.Message` を外へ出さない）。
- テスト仕様書に X-50（共有先・所有者の否定と陽性対照）・X-51（打ち切り時の件数）を足した。`docs/tests/FR-17_knowledge-graph.md`・`docs/tests/UC-10_graph-traversal.md` の
  trace ブロックに IADR-0479・本仕様書・#1611 を足した。
- **残る懸念（追加）**: Retrieval の実行口は `owner`・部署等を含む索引の属性を全キー返す（共有先は入らない）。MCP の応答の属性を許可リストへ揃えるかは
  別件の判断とする。被参照・参照先の打ち切り時の件数は全体件数ではない。

#### 監査対応の検証と変異（修正コミット `fix(FR-16,ADR-0117,ADR-0098): …許可リストに絞る` の後）

検証: knowledge・platform の build（エラー 0。knowledge の既存警告 1 件のみ）、GraphService.Tests 751 件・McpServer.Tests 237 件合格、`dotnet format --verify-no-changes` 両ユニット差分なし、
`scripts.test.js` 841 件合格、check-trace-blocks / check-test-spec-coverage（`--update` で差分なし）/ check-test-traceability / check-cross-repo-refs /
check-plan-id-qualification / check-proto-contracts / gen-knowledge-graph --check / check-commit-messages はすべて OK。

| # | 変異（コミット済みの状態で当て、`git show HEAD:<path>` で戻した） | 結果 |
| --- | --- | --- |
| Ma | 写像の許可リストを外して全キーを写す（`Seal` の共有先の除去は残る） | 赤 4 件: `共有先と所有者はエンベロープの属性に載らない`（有人・SA）・陽性対照（2。許可リスト外のキーが載る）|
| Mb | `Seal` の共有先の除去を外す（写像の許可リストは残る） | 緑のまま —— 写像の許可リストで守られる（多層防御の 1 層だけを外した形。想定どおり） |
| Mab | 写像の許可リストに共有先だけを通す（`Seal` の除去は残る） | 緑のまま —— `Seal` の除去で守られる（もう 1 層だけを外した形。想定どおり） |
| Mc | 打ち切り時の全体件数を返さない（返した件数に戻す） | 赤 1 件: `表示上限で打ち切ったら全体件数を返す` |

---

## ［2026-09-28 追記 / #1611］段 2: DocumentService の実行口

> 段 1・段 3 の記録（上）は書き換えない。本節は段 2 の着手前に書き、実施結果を末尾に足す。**本段で #1611 を閉じる**（`Closes #1611`）。

### 起点・前提

- 基点 `origin/develop` `29d23882`（#1675 = #1615 の内容の ABAC の着地後）。`git rev-parse --is-shallow-repository` = `true`
  （`git log` / `git blame` は出典に使わない。以下の母集合は `git grep` の実測）。ブランチ `feat/FR-16-mcp-tool-execution-document`。
- 前提としてマージ済み: 段 1（#1662）・段 3（#1668）・属性の許可リスト（#1671 / #1672。`Platform.Shared.Contracts.Dtos.McpEnvelopeAttributes`）・
  内容の ABAC（#1615 / #1675。判定点 `DocumentReadAccess`・門 `IContentAbacGate`・IADR-0483）。
- 計画: ADR-0117 決定 1〜4、ADR-0086 決定 1・4、ADR-0088 決定 1、ADR-0034 決定 9、ADR-0024 §4、ADR-0121 決定 2・4・5・6（`/home/user/project-planning` の `origin/main` `17518cc` を読み取り専用で読んだ）。
- 並行作業 #1676（`DocumentReadAccess` 周辺の試験・AuthorizationService・運用仕様書・`.claude/rules/traceability.repo.md`）とは、
  **`DocumentReadAccess` 本体と `traceability.repo.md` を触らない**ことで交差を避ける。
- 束ねない判断（段 1 の表）は変わらない。本 PR は DocumentService の受け口だけを足し、proto・MCP サーバーのコードは変えない（注記だけ）。

### 母集合（規則 9。誤りの側の文字列で走査した）

誤りの側 = 「文書には受け口が無い」「文書は段 2 まで拒否」「実行口の無い宛先（文書）」「文書の実行は `UNIMPLEMENTED`」。
走査語: `段 2`・`document は段`・`文書だけ`・`受け口が無い`・`実行口の無い`・`［#1611 段 3 時点］`・`UNIMPLEMENTED`/`Unimplemented`・`#1611`。
除外は段 1・段 3 と同じ（`bin` / `obj` / `node_modules` / `.git` / `src/ai-stock-trading`、凍結記録 `.ai-context/specs/`、生成物 `CHANGELOG.md`）。

1. **「文書には受け口が無い」と書く記述** ——
   proto `mcp_tool_execution.proto`（■ 受け口の段落）／McpServer `ToolInvocationService.cs`（1）・`GrpcToolInvoker.cs`（2）・
   `GrpcToolInvokerTests.cs`（1）・`McpToolDeclarationGrpcTestHost.cs`（1。「実行口の無い本番の申告元と同じく」）／
   compose `deploy/docker-compose.yml`・helm `values.yaml` の「document は段 2 まで拒否」／
   `docs/api/FR-16_mcp-server.md`（§実行の注記）／`docs/api/east-west-grpc.md`（14 つ目の面）／
   `docs/tests/FR-16_mcp-server.md`（X-14・未実施・残件）
2. **文書の `UNIMPLEMENTED` を固定する試験** —— DocumentService `GrpcMcpToolDeclarationTests.Tool_execution_port_is_not_served_yet_and_returns_unimplemented`
3. **実行口が乗る既存の経路**（変更しない）—— `DocumentReadUseCase.GetAsync` / `ListAsync`・判定点 `DocumentReadAccess`（門 `IContentAbacGate` の 2 つの枝）・
   `DocumentReadPrincipal.RelayedUser`（`service-account-` を機械として扱う）・`IDocumentReadScopeSource`（属性は空で認可サービスへ問う）
4. **#1671 の申し送り**: エンベロープの属性は `McpEnvelopeAttributes.IsCarried` のキーだけ（`DocumentDto.Attributes` は `owner`・部署等を持つ）
5. **申告の説明** —— `document.get_document` の説明「（タイトル・属性・本文の参照）」。応答は本文も参照リンクも持たないので、返らないものを約束している

**本 PR で直すもの**: 1 の全件（「3 サービスとも受け口を持つ」へ）・2 の反転・5 の説明（「（タイトル・属性）」へ。段 3 の `graph.traverse` と同じ扱い）。
**除外（直さない）**: IADR-0462 の追記・IADR-0483 フォローアップ 2・IADR-0479 §残るもの（凍結記録の本文。IADR-0479 の段 2 の追記で記録する）／
McpServer の `UNIMPLEMENTED` を拒否へ写す枝とその試験（X-3。配備の順番〔旧い DocumentService〕と将来の供給元のために残る。事実として今も正しい）／
`docs/tests/FR-16_mcp-server.md` の変異表「受け口が無いこと（`UNIMPLEMENTED`）の枝を外す」（同上）。
**この変更で新たに誤りになる自分の記述（規則 10）**: 段 3 で「文書だけが無い」へ直した記述（上の 1）はすべて本段で誤りになる —— 1 に含めた。
段 3 の仕様で X-30〜X-39 を段 2 に予約していたが、テスト仕様書の develop の最大は X-55 なので、本段は **X-56 から**採る（予約は使わない。欠番は仕様書の表に現れない）。

### 受け入れ基準（段 2）

- D-1 **実行口（Document）**: `platform.mcp.v1.McpToolExecution/Execute` を h2c で受け、自分の申告名（`document.get_document` / `document.list_documents`）だけを実行する。
  他（他のサービスのツール・申告から落とした `document.list_private_notes`・公開名・変種）は `NOT_FOUND`。面は `ServiceCaller` を要求し、申告の口（`MapMcpToolEndpoints`）と対で張る。
- D-2 🔴 **MCP サーバー以外は拒否**: 許可集合 `McpToolExecution:TrustedUserContextClients`（既定 `mcp-server`。共有 `TrustedUserContextRelay`。
  `Program.cs` で `ThrowIfScalar` → `Configure`）。他の `platform-service` の主体（`DocumentRead:` の中継者 `bff` を含む）・変種・人のトークンは
  `PERMISSION_DENIED` で、認可の問い合わせも台帳の読み取りの判定も 1 度も行わない。`DocumentRead:`（既定 `bff`）とはキーも集合も共有しない。
- D-3 🔴 **自分で認可する・第二の判定点を作らない**: 本文の利用者名を `DocumentReadPrincipal.RelayedUser` で主体にし、既存の `DocumentReadUseCase`
  （→ `DocumentReadAccess`。門の状態で枝を選ぶ）をそのまま通す。認可サービスへは利用者名で問う（属性は空。ADR-0088）。
  権限外の文書は個別では空・一覧では現れず件数にも入らない。無い・見えないは同じ空（バイト列まで区別できない）。
- D-4 🔴 **REST の同じ利用者の結果を超えない（門の両状態）**: 門が閉じている間（既定 Off）は #1615 の閉じた枝（＝従前の判定）で、
  門が開いた後は内容の ABAC の枝で、MCP 経路の個別・一覧の結果が DocumentService の REST（`GET /documents/{id}`・`GET /documents`）の同じ利用者の結果と一致する
  （一覧の件数も一致する）。閉じている間は他人の機密の組織文書が返り（従前どおり）、開いた後は属性の合わない利用者に返らないことも固定する。
- D-5 🔴 **本文の scope は効かない**: 旧い番号 3 をワイヤへ載せても、引数に `scope` / `filters` / `attributes` を書いても結果は変わらない。
- D-6 `action` は受け口が決める（2 ツールとも `read`）。違えば `INVALID_ARGUMENT`。利用者文脈が無い・空も `INVALID_ARGUMENT`（機械の主体へ読み替えない）。
- D-7 **引数は丸めない**: `get_document` は `document_id`（必須・GUID の文字列）、`list_documents` は `limit`（整数 1〜100・既定 20。申告の `input_schema` と同じ値）。
  外れは `INVALID_ARGUMENT`。境界ちょうど・省略は通る。
- D-8 🔴 **サービスアカウント実行は個人資料を返さない**（ADR-0034 決定 9 の要求側）: 利用者名が `service-account-` で始まるなら、その名前が所有者・共有先でも
  個人資料を返さず件数にも入れない（門の両状態）。判定点（`RelayedUser` が機械として扱う）と写像の 2 層で落とす。有人の所有者には返る（対照）。
- D-9 **応答**: 共通エンベロープ。`get_document` は 0 件か 1 件、`list_documents` は更新の新しい順の先頭 `limit` 件・`total_count` は判定と除外の後の全体件数・
  超えたら `truncated`。題名と属性を返し、**属性は `McpEnvelopeAttributes.IsCarried` のキーだけ**（`owner`・部署・`shared_with` は運ばない）。
  本文・参照リンクは持たない（台帳は本文を持たず、`MarkdownUri` は内部の格納先であって利用者へ見せるリンクではない）。
- D-10 **配備**: 既定の許可集合が compose・helm の MCP サーバーの s2s の client と realm の機密クライアントに一致し、実行の宛先に document-service の h2c が在る（配線試験）。

### 設計（段 2）

- **受け口**（`Features/McpTools/Execute/`。Graph・Retrieval と同じ型）: 判定の順は段 1 の決定 2 のまま。引数の解釈の後、
  `DocumentReadPrincipal.RelayedUser(userId)` を組み、`get_document` は `DocumentReadUseCase.GetAsync`、`list_documents` は `ListAsync` を呼ぶ
  （REST・gRPC `DocumentRead` と同じ関数。**判定器は増やさない・`DocumentReadAccess` は変えない**）。
- **門は受け口では読まない**: 枝の選択は `DocumentReadAccess` の中（要求の中で最初に読んだ値に固定）で行う。受け口が門を読んで分岐すると判定点が 2 つになる。
- **写像**: サービスアカウント実行なら個人資料を落とし（多層防御）、件数を数えてから `limit` で切る。属性は許可リストのキーだけ。
- **一覧の件数**: `ListAsync` は読める文書を全件返すので、`total_count` は全体・`truncated` は `limit` 超え（REST の `GET /documents` と同じ母集合）。
- **申告の説明**: `document.get_document` の「本文の参照」を外す（返らないものを LLM に約束しない）。
- **IADR**: 新設しない。IADR-0479 に段 2 の追記を足す（受け口の形・門の閉じた枝に乗ること・本文を返さないこと）。

### 試験の方針（段 2）

- DocumentService の既存の器 `GrpcKestrelFactory`（本番の `Program.cs`・実 Kestrel の h2c・127.0.0.1・本物の JwtBearer）で往復する。
  器の `IDocumentReadScopeSource` と `IContentAbacGate` を代役（`StubDocumentReadScopeSource`・`StubContentAbacGate`。既定は「読めるものは無い」・閉）へ差し替える
  （未構成の縮退と同じ向きなので、同じ器の他の試験の挙動は変わらない）。門を開く試験は `finally` で閉じる。
- REST との突き合わせは同じ器の HTTP/1.1 側へ利用者のトークン（`preferred_username`）で送る。
- テスト仕様書 `docs/tests/FR-16_mcp-server.md` の行は **X-56〜**。
- 変異（5 件以上。コミット後に当てて `git show HEAD:<path> > <path>` で戻す）: 許可集合の検査を外す／認可の結果を無視して全許可（判定点を通さず台帳を直接読む）／
  エンベロープの許可リストを外す／サービスアカウントの個人資料の除外を外す（写像・主体の両層）／門の状態を見ず常に開いた枝で判定／操作の突合を外す。

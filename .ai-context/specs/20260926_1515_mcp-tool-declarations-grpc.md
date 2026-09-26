---
title: MCP のツール申告の収集（/internal/mcp-tools）を扇形のまま east-west gRPC へ移す（#1255 経路 ④-a・スライス 2）
type: spec
status: draft
related_ids: [FR-16, FR-15, NFR-09, NFR-16, ADR-0024, ADR-0029, ADR-0075, ADR-0089, IADR-0029, IADR-0117, IADR-0269, IADR-0292, IADR-0379, IADR-0419, IADR-0462]
author: Claude（実装）
created: 2026-09-26
updated: 2026-09-26
plan_refs: []
---

# 仕様書: MCP のツール申告の収集を gRPC へ移す（#1515 / #1255 経路 ④-a）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-16（MCP サーバー統合）／NFR-09（認可）／NFR-16（east-west の統一）
- 関連 ADR: `ADR-0024` §2・§5（自己申告と公開構成の突合・推測で公開しない）／`ADR-0029`（east-west は gRPC。2026-08-04 追記で全移行）／
  `ADR-0075`（一括移行・基盤先行）／`ADR-0089` 決定 1（退役をもって解けたと数える）
- 実装 ADR: [[IADR-0462]]（経路 ⑤ の形。**本 PR は日付つき追記で ④-a へ適用する**）／[[IADR-0379]]（置き場・versioning・h2c・s2s・並走）／
  [[IADR-0269]] 決定 6・[[IADR-0292]] 決定 4（申告スキーマの共有契約への昇格。本 PR の proto が昇格を兼ねる）
- 裁定: #1255 のオーナー裁定（2026-09-25）「④⑤ は配り先のすべてのサービスに gRPC の口を実装して移す」
- issue: #1515（本スライス）／親 #1255（閉じない）／後続 #1516（④-b。`decision-needed`）・#1517（REST 退役）

## 起点の確認

基点 `origin/develop` `10805447`。`git rev-parse --is-shallow-repository` = `false`。

## 母集合（着手時に自分で引いた）

走査は 5 軸（パスの除外は `bin` / `obj` / `node_modules` / `.git` / `src/ai-stock-trading` のみ。拡張子で絞らない）。

1. **申告の受け口（誤りの側 = REST の路の文字列から）** —— `grep -rIl "mcp-tools\|McpToolDeclaration\|ToolDeclarationSource\|ServiceToolDeclarations"`:
   本番コードで受け口を張るのは **Document / Retrieval / Graph の 3 サービス**（各 `Features/McpTools/Declare/Endpoint.cs`）。
   ほかに NotificationService の `NotificationIngressEndpoints.cs`（コメント中の言及のみ）・RetrievalService の `SearchEndpoints.cs`（実行口が無いことのコメント）
2. **宛先の構成（`Mcp:Services` / `Mcp__Services` / `ServicesSection`）** —— compose 3 行・McpServer `appsettings.json` 3 項目・
   統合試験 1 箇所。**helm は 0 行**（chart は appsettings の既定に寄りかかっている）
3. **宛先の h2c リスナ・ポート** —— 3 サービスとも `builder.AddPlatformGrpcListener();` を持ち、helm `grpcPort: 8081`・compose `Grpc__Port: "8081"` と `expose` を持つ（既存の他経路のため）
4. **宛先の認証**（面の `ServiceCaller` の前提）—— 3 サービスとも `AddPlatformAuth` を持つ
5. **呼び出し側** —— `IToolDeclarationSource` の実装は `HttpToolDeclarationSource` 1 つ（`Program.cs` で登録）。消費者は `ToolCatalogRefresher` 1 つ。
   McpServer の s2s 資格情報（`ServiceToken`）は helm（`serviceToken`）・compose の両方に既にある（認可サービスの gRPC 経路のため）

⇒ 本 PR が触るもの:

| 区分 | 対象 | 本数 |
| --- | --- | --- |
| proto（`platform.mcp.v1`。共有契約への昇格を兼ねる） | `Platform.Shared.Contracts` | 1 |
| gRPC 面（`MapMcpToolEndpoints` が REST と対で張る。本体は REST と同じ `McpToolDeclarationSource.Declare`） | Document / Retrieval / Graph | 3 |
| 呼び出し側（宛先ごと opt-in。`Mcp:GrpcServices`） | McpServer | 1 |
| McpServer の gRPC 宛先（`Mcp__GrpcServices__*`） | helm・compose | 3 × 2 |
| 契約 baseline | `scripts/proto-contract-baseline.json` | 1 |

### 除外とその理由

- **ツールの実行経路（`HttpToolInvoker`）**: #1516（④-b。`decision-needed`）。宛先に実行口が 1 つも無く、申告 `endpoint` の意味の判断が先に要る。
  本 PR は `endpoint` を**文字列のまま**運ぶ（意味を変えない）
- **REST の退役**: #1517（#1255 残射程 2 と同じ段）。並走中の正は REST（[[IADR-0379]] 決定 5）
- **REST の DTO の写し（3 サービスの `McpToolContracts.cs`）の撤去・`*.Contracts` の C# 型への昇格**: 写しは REST の受け口が使う。
  REST を退役させる段（#1517）で写しごと消える。今 C# 型を共有契約へ移すと `contract-schema-baseline.json` の更新を伴い、
  退役で消える型を共有物にする。**gRPC の契約（proto）が共有契約であり、昇格は proto で行う**（#1515 §やること 1）
- **helm の REST 宛先（`Mcp__Services__*`）の明示**: 既存の chart は appsettings の既定に寄りかかっており、本 PR の射程外（挙動を変えない）。
  配線の試験は helm の gRPC 宛先を appsettings の REST 宛先と突き合わせる
- **AST のサービス**: 隣接クローン `ai-stock-trading` に `/internal/mcp-tools` の実装は 0 件（仕様書 1 件の言及のみ）
- **Knowledge.IntegrationTests の収集の結合試験（`McpToolCatalogIntegrationTests`）の gRPC 化**: REST の収集を実物で回す試験であり、並走中の正は REST なので残す。
  gRPC の往復はループバックの実 Kestrel（McpServer.Tests）と各サービスの h2c 実 Kestrel（各サービスの `GrpcKestrelFactory`）で固定する

## 受け入れ基準（#1515 と同じ）

- [ ] Given s2s トークン / When gRPC で申告を取る / Then REST と同じ申告が返る。トークン無しは `UNAUTHENTICATED`、利用者トークン（管理者を含む）は `PERMISSION_DENIED`
- [ ] Given 収集の失敗（全 status・期限切れ・トークン取得失敗・空の `service`） / When 収集する / Then 従来どおり「申告なし」として扱い、推測で公開しない（ADR-0024 §5）
- [ ] Given 3 サービスの本番 `Program.cs` / When 起動する / Then gRPC の申告面が `ServiceCaller` 付きで張られている
- [ ] 試験は 0.0.0.0 で待ち受けない
- [ ] `check-proto-contracts` / 両ユニットの build / test / format が通る

## 設計（[[IADR-0462]] の形をそのまま写す。違いは IADR-0462 の追記に書く）

1. **proto** `platform.mcp.v1.McpToolDeclarations/Declare`（`Platform.Shared.Contracts/Protos/platform/mcp/v1/`）。
   6 項目はいずれも DTO で null を取らないので素の `string`。enum は持たない。**空の `service` は申告として無効**（呼び出し側で「申告なし」へ）
2. **受け口**（3 サービス）: `McpToolDeclarationGrpcService`（`Features/McpTools/Declare/GrpcService.cs`）。REST と同じ `McpToolDeclarationSource.Declare(configuration)` を写すだけ。
   `[Authorize(Policy = ServiceCaller)]`。`MapMcpToolEndpoints` が REST と gRPC を**対で**張る（張り忘れを構造で起こさない。IADR-0462 決定 1-A と同じ理由をサービス内で満たす）
3. **呼び出し側**（McpServer）: `ToolDeclarationSource`（新しい `IToolDeclarationSource`）が宛先 = `Mcp:Services` と `Mcp:GrpcServices` のキーの和を回し、
   `Mcp:GrpcServices` に空でないアドレスが在れば `GrpcToolDeclarationCollector`、無ければ `HttpToolDeclarationSource.CollectOneAsync`
   - 期限: REST の HttpClient のタイムアウト（同じ名前付きクライアントの `Timeout`。値を書き写さない）
   - リトライ: 持たない（REST も持たない。次の周期が再試行）
   - 失敗: 全 status・トークン取得失敗・期限切れ・空の申告を「申告なし」へ。`UNAUTHENTICATED` / `PERMISSION_DENIED` とトークン取得失敗は Error、他は Warning
   - 取り消し: 呼び出し側 ct 由来だけを `OperationCanceledException` で外へ（REST と同じ）
   - 登録: `AddMcpToolDeclarationSources` は `Mcp:GrpcServices` が構成されたときだけ s2s と gRPC 収集器を登録する。構成されているのに収集器が無ければ起動時に落とす
4. **配備**: helm・compose の McpServer に 3 宛先の `Mcp__GrpcServices__*`（`http://<svc>:8081`）。資格情報は既存の `mcp-server` client（`platform-service`）

## 試験の方針

- 収集器の往復・宛先ごとの輸送選択・失敗の畳み方・登録は McpServer.Tests に**ループバック（127.0.0.1）限定の実 Kestrel**を立てて測る（待受アドレスを起動直後に表明）
- 3 サービスの受け口は各サービスの `GrpcKestrelFactory`（ループバックの実 Kestrel・本物の JwtBearer）で、REST との同値と 3 通りの判定を測る
- helm・compose・本番 `Program.cs` の配線は正のファイルを読む試験で固定する（`IntrospectionGrpcDeploymentWiringTests` と同型）
- proto と REST の DTO の項目の一致（6 項目の名前）を試験で固定する（並走中は 2 つの形が在るため）

## 実施結果

### 変えたもの

- proto `platform/mcp/v1/mcp_tool_declarations.proto`（`scripts/proto-contract-baseline.json` を `--update` で更新。非破壊の file 追加 1）
- 3 サービス: `Features/McpTools/Declare/GrpcService.cs`（面）と `Endpoint.cs`（`MapMcpToolEndpoints` が REST と gRPC を対で張る）。
  `McpToolContracts.cs` の写しには昇格の経過を追記した（コードは変えていない）
- McpServer: `ToolDeclarationSource`（宛先ごとの輸送選択）・`GrpcToolDeclarationCollector`（1 宛先ぶんの gRPC 収集）・
  `AddMcpToolDeclarationSources`（登録）。`HttpToolDeclarationSource` から 1 宛先ぶんの `CollectOneAsync` を切り出した（REST の挙動は不変）
- 配備: helm・compose の McpServer に `Mcp__GrpcServices__*` 3 行。3 サービスの `grpcPort` / `Grpc__Port` の注記に受ける面を追記
- 記録: [[IADR-0462]] に「経路 ④-a への適用」を日付つきで追記（新しい IADR は起こしていない）。[[IADR-0269]] フォローアップ・[[IADR-0292]] フォローアップに昇格の経過を追記
- 文書: `docs/api/east-west-grpc.md`（13 つ目の面・状態の経路数 12 → 13・未決事項）、`docs/api/FR-16_mcp-server.md`（gRPC での収集）、
  `docs/tests/FR-16_mcp-server.md`（G-1〜G-13・変異）。`scripts/test-spec-coverage-baseline.json` を `--update`（対 +3）

### 試験

- 追加した試験は **31 件**: McpServer 19（`GrpcToolDeclarationCollectorTests` 14・`McpToolsGrpcDeploymentWiringTests` 5）／
  各サービスの `GrpcMcpToolDeclarationTests` 4 × 3 = 12
- 実走（develop `10805447` 基点）:
  - knowledge: `dotnet test knowledge/backend/backend.slnx` 合格 2,730・スキップ 52・失敗 0（rc=0）
  - platform: McpServer 198 / Shared.Infrastructure 439 / Authorization 405 / Notification 104 / LlmGateway 326 / Kernel 42。失敗 0
  - 🔴 **Platform.Bff.Tests は手元で建たない** —— worktree に submodule `src/ai-stock-trading` が未取得で、BFF の `AiStockTrading` 参照が
    CS0246 になる（環境の都合。BFF には触れていない）。CI で確認する
- 待受: McpServer の器は `IPAddress.Loopback` の動的ポート 2 本だけに開き、起動直後に全待受アドレスがループバックであることを表明する。
  各サービスの器（`GrpcKestrelFactory`）も 127.0.0.1 の空きポートで、起動直後に表明する

### 変異試験（`scratchpad` の実行器で 1 件ずつ当て、試験後にファイルを戻した）

| # | 変異 | 結果 |
| --- | --- | --- |
| M1 | Document の `MapGrpcService<McpToolDeclarationGrpcService>()` を外す | 赤: Document 3 ＋ 配線 1 |
| M2 | 収集器が gRPC の宛先を無視する | 赤 9 |
| M3 | 空の service を受け入れる | 赤 1 |
| M4 | 期限を付けない | 赤 1（見張り 15 秒で打ち切り。止まらない） |
| M5 | `UNAUTHENTICATED` / `PERMISSION_DENIED` の Error 枝を外す | 赤 2 |
| M6 | トークン取得失敗の Error 枝を外す | 赤 1 |
| M7 | Graph の面を `[AllowAnonymous]` に | 赤 3 |
| M8 | gRPC の収集器を登録しない | 赤 1 |
| M9 | helm の graph の gRPC 宛先を 8080 に | 赤 1 |
| M10 | 呼び出し側の取り消しを申告なしへ畳む | 赤 1 |
| M11 | Program.cs で収集器を 1 度組むのをやめる（［2026-09-26 追記］AI レビュー指摘への対応で追加） | 赤 1（`Host_does_not_start_…`。陽性対照は緑のまま） |

［2026-09-26 追記 / #1515］**AI レビューの指摘（重大）への対応**: 「構成が在るのに gRPC の収集器が無ければ起動時に落とす」は、
`ToolDeclarationSource`（scoped）が ToolCatalogRefresher の周期の中で初めて組まれるため、例外が「収集の一時失敗」として握られて
成立していなかった。McpServer の Program.cs が要求を受ける前に 1 度組むようにし、`ToolDeclarationSourceFailFastTests`（2 件。
否定形と陽性対照）で本番の Program.cs のままホストが起動しないことを固定した。追加した試験は計 **33 件**になった。
推奨の指摘（`GetOrAdd` の生成関数の競合）は、収集が逐次である前提と並列化するときの手当てをコメントに書いた。

### 確かめていないこと

- 稼働 k3s・compose での h2c 往復（Pod の再構築を要する。live クラスタは使わない方針）
- 並走中の正は REST のまま（[[IADR-0379]] 決定 5）。ツールの実行（#1516）と REST の退役（#1517）は射程外

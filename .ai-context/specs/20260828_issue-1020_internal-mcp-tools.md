---
title: 作業仕様書 — 各サービスの自己申告端点 `GET /internal/mcp-tools` を実装し、実効カタログを空でなくする（#1020）
type: spec
status: done
related_ids:
  - FR-16
  - FR-19
  - UC-08
  - SC-12
  - ADR-0024
  - ADR-0034
  - ADR-0054
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "ADR-0024（MCP サーバーの追加）§決定「初期公開範囲」: 初期公開は検索・文書取得系（retrieval.* / document.*）に限定し、AI 分析系（ai.*）は含めない"
  - "ADR-0024 2026-08-01 注記 / 11_mcp-server-integration §6: グラフ系は探索系（get_backlinks / get_links / traverse）のみ公開し、要約系（get_cluster_summary）は公開しない"
  - "ADR-0024 2026-08-02 注記 / ADR-0034 決定 9: サービスアカウント実行では個人資料（private-note）を一律に対象外とし、個人資料を読ませる属性割当を構成上禁止する"
  - "11_mcp-server-integration §2: ツール定義規約（name / description / input_schema / endpoint / required_scope / egress_class）と『自サービスに /internal/mcp-tools を実装』＋『公開構成に追記』の 2 手順"
related_adrs:
  - IADR-0269
  - IADR-0292
issue: "#1020"
---

# 作業仕様書: 各サービスの自己申告端点 `GET /internal/mcp-tools`（#1020）

## 起点

#445 で McpServer 側の収集機構（`HttpToolDeclarationSource` ＋ `ToolCatalogRefresher` ＋
`ToolCatalog` ＋ `ToolPublicationConfigValidator`）は着地している。**しかし実効カタログは空である** ——
`/internal/mcp-tools` を実装したサービスが 0 件であり、集める対象が存在しない。
「動的ツール連携は動くが、載るツールがゼロ」という状態であり、FR-16 の受け入れ基準は実質満たされていない。

IADR-0269 のフォローアップは本作業を明示している ——
「`RetrievalService` / `DocumentService` / `GraphService` に `GET /internal/mcp-tools` と
共通エンベロープの実行口を実装する（それぞれの再実装 issue の射程）」。

## 母集合と公開対象の確定（着手時に自分で引いたもの）

### 母集合（走査で引いた全サービス）

`src/*/backend/Services/*` を走査した実測。

| ユニット | サービス | 公開対象 | 判定の根拠 |
| --- | --- | --- | --- |
| knowledge | DocumentService | **対象** | ADR-0024 §決定「初期公開範囲」の**文書取得系（`document.*`）**の供給元 |
| knowledge | RetrievalService | **対象** | 同じく**検索系（`retrieval.*`）**の供給元。11_mcp-server-integration §2 が挙げる例（`retrieval.search_documents`）そのもの |
| knowledge | GraphService | **対象** | ADR-0024 2026-08-01 注記／11_mcp-server-integration §6 が**探索系（読み取り）の 3 ツール**を公開すると確定させ、供給元を GraphService と名指ししている |
| knowledge | WikiService | 対象外 | 公開範囲（`retrieval.*` / `document.*` / グラフ探索系）のどれにも当たらない。既定は非公開（許可リスト方式）であり、**挙げられていないものは公開しない**。加えて Wiki ページは DocumentService の文書の投影であり、公開すると `document.*` と二重の経路になる（経路が増えるほど統制の適用点が増える。IADR-0269 決定 2 が避けた形） |
| knowledge | AiAnalysisService | 対象外 | **`ai.*` は初期公開に含めない**（ADR-0024 §決定・確定事項 2026-07-23）。`ToolPublicationConfigValidator` が構成側でも弾く |
| knowledge | IngestionService / ConversionService / DataSourceService | 対象外 | 取り込み・変換・データ源接続の**副作用を伴うパイプライン**であり、読み取りツールの供給元ではない。公開範囲に挙げられていない |
| knowledge | DashboardService / FeedbackService | 対象外 | 画面向け集約・環流受け。公開範囲に挙げられていない |
| platform | McpServer | 対象外 | 集める側であり、自分に申告しない |
| platform | AuthorizationService / LlmGateway / NotificationService | 対象外 | 認可・LLM 中継・通知。公開範囲に挙げられていない（LlmGateway の公開は `ai.*` 非公開方針にも反する） |
| （submodule） | src/ai-stock-trading | 対象外 | 別プロジェクト（独自の計画リポジトリと ADR を持つ）。本リポジトリの規約を適用しない |

**結論: DocumentService / RetrievalService / GraphService の 3 件。** issue が挙げた候補のうち
**WikiService は除外した**（上記の理由）。この 3 件は IADR-0269 のフォローアップが名指しした 3 件とも一致する。

### 公開するツール

| サービス | ツール名 | 対象 | 根拠 |
| --- | --- | --- | --- |
| document-service | `document.get_document` | 文書 1 件の取得 | ADR-0024「文書取得系（`document.*`）」 |
| document-service | `document.list_documents` | 文書の一覧 | 同上 |
| retrieval-service | `retrieval.search_documents` | ハイブリッド検索 | 11_mcp-server-integration §2 の例示名そのもの |
| graph-service | `graph.get_backlinks` | 被参照の一覧 | 11_mcp-server-integration §6 の表（公開する） |
| graph-service | `graph.get_links` | 参照先の一覧 | 同上 |
| graph-service | `graph.traverse` | 近傍のホップ探索 | 同上。`hops` は既定 2・上限 3 を `input_schema` に写す |

**申告しない（候補として挙がるが除外する）もの**

| サービス | 候補 | 除外の根拠 |
| --- | --- | --- |
| document-service | `document.list_private_notes`（実在する `/private-notes` 面） | ADR-0024 2026-08-02 注記／ADR-0034 決定 9。**個人資料は一律で対象外**。検索系・文書取得系にも適用される |
| graph-service | `graph.get_cluster_summary` | 11_mcp-server-integration §6 の表（公開しない。要約系＝LLM 呼び出しを伴う）。GraphService に実体も無い |

## 対象範囲

- 対象:
  - 3 サービスへ `GET /internal/mcp-tools`（メッシュ内部限定・`ExcludeFromDescription`）を実装する
  - 申告の**個人資料除外**（`doc_scope` 対象範囲による選別）を 3 サービス共通の形で持つ
  - McpServer の公開構成（`Configuration/mcp-publication.json`）と収集先（`appsettings.json` の `Mcp:Services`）へ 6 ツール・3 サービスを追記する（ADR-0024 の 2 手順目）
  - 収集 → カタログ反映の統合テスト（Docker 不要）
- 対象外（理由つき）:
  - **共通エンベロープの実行口（`endpoint` が指す実体）**。理由は下記「未決事項・残件」。本 issue では**申告と収集・公開の経路**を閉じる
  - `deploy/**` の配線（領域宣言で禁止。`Mcp__Services__*` の注入は残件）
  - 申告 DTO の `Platform.Shared.Contracts` への昇格（IADR-0269 決定 6）。理由は下記

## 設計

### 1. 申告の形（契約は新設しない）

応答は McpServer の既存契約（`McpServer/Domain/McpToolContracts.cs` の
`ServiceToolDeclarations` / `McpToolDeclaration`）の**ワイヤ形式**にそのまま合わせる。
`service` と `tools[] { name, description, input_schema, endpoint, required_scope, egress_class }`。

**DTO は各サービス内（`Features/McpTools/`）に持つ。** 共有化しない理由は 2 つある。

1. **ユニット外参照ができない。** 可変ユニット（knowledge）から platform の McpServer は参照できない
   （許可は `platform/backend/Shared/` の 3 プロジェクトのみ）。GraphService の `GraphDocumentScope` が
   同じ理由で McpServer の `DocumentScope` を共有せず持っている（IADR-0274 §検討した選択肢）先例がある。
2. **`Platform.Shared.Contracts` / `Knowledge.Contracts` への昇格は本 issue の領域外である。**
   IADR-0269 決定 6 は「最初の生成側が実装された時点で昇格させる」と定めるが、
   `*.Contracts` への型追加は `scripts/contract-schema-baseline.json` の更新を伴い、
   **`scripts/**` は本 issue の領域宣言で触ってはならない**。昇格は追随 issue で行う（IADR-0292 決定 3）。

### 2. 個人資料の一律除外（申告する側）

**候補ツールは対象文書スコープ（`doc_scope`）を明示して持ち、`private-note` を対象に含む候補は申告しない。**

- 判定は **集合帰属**（`doc_scope == "private-note"`）で書く。**否定（`!= "organization"`）で書かない** ——
  `doc_scope` は実データ 0 件・遡及付与しない方針（ADR-0054 §結果）であり、否定で書くと
  スコープを持たない候補がすべて個人資料に倒れて**組織向けツールが一斉に落ちる**。
  McpServer の `DocumentScope` / GraphService の `GraphDocumentScope` / DocumentService の
  `DocumentAttributes.IsPrivateNote` と同じ向きである。
- **2 つの書き方は「個人資料を除外する」点では動作で見分けがつかない。**
  分けられるのは**陽性対照**（スコープを持たない候補・組織スコープの候補が落ちないこと）だけである。

### 3. 端点

- `GET /internal/mcp-tools` — 認可を要求しない。`/internal/introspection`（FR-15）と同じ規約系・
  同じ防御（メッシュ内部限定・ネットワーク分離・mTLS）に置く。`ExcludeFromDescription()` で
  OpenAPI から外す（BFF 契約ではない）。
- `endpoint` は構成 `Mcp:SelfBaseUrl`（既定はメッシュ内の自サービス URL）＋ ツールごとの実行パス。
  実行口そのものは本 issue の対象外なので、**実装されるまでこの URL は解決しても 404 である**。
  これは IADR-0292 決定 2 で明示的に受け入れた状態であり、`ToolCatalog` の突合（申告の有無）と
  ドリフト検出（ADR-0024 §5）はこの状態でも正しく働く。

### 4. 公開構成（ADR-0024 の 2 手順目）

`McpServer/Configuration/mcp-publication.json` へ 6 件を追記し、`appsettings.json` の
`Mcp:Services` へ 3 サービスのメッシュ URL を入れる。これで**実効カタログが空でなくなる**。

## 受け入れ基準

- [x] DocumentService / RetrievalService / GraphService が `GET /internal/mcp-tools` に 200 で応答し、既存契約の形の JSON を返す
- [x] 申告された各ツールは `name` / `description` / `input_schema` / `endpoint` / `required_scope` / `egress_class` をすべて非空で持つ
- [x] 収集（`HttpToolDeclarationSource`）→ 突合（`ToolCatalog.Refresh`）を通し、**公開構成に載せた 6 ツールが個々に**実効カタログへ現れる
- [x] 申告のある公開宣言ではドリフトが**沈黙**し、申告の無い公開宣言では `missing-declaration` が**発火**する（ADR-0024 §5）
- [x] 個人資料（`private-note`）を対象に含む候補は申告に現れない（否定形）
- [x] 個人資料でない候補は申告に現れる（陽性対照）

## テスト方針

| 層 | 置き場所 | 測るもの |
| --- | --- | --- |
| 端点（3 サービス） | 各サービスの `Tests/McpToolDeclarationEndpointTests.cs` | 200・サービス名・**申告した個々のツール名**（陽性対照）・6 項目が非空・**個人資料候補が現れない**（否定形） |
| 選別ロジック（3 サービス） | 同上 | 個人資料スコープの候補を渡すと落ちること（否定形）／組織スコープ・スコープ無しの候補が残ること（陽性対照） |
| 収集 → カタログ | `Knowledge.IntegrationTests/McpTools/McpToolCatalogIntegrationTests.cs` | 実サービス 3 本を **in-process** で起こし、`HttpToolDeclarationSource` で実際に収集して `ToolCatalog` へ反映し、**6 ツールが個々に**載ること。ドリフトの沈黙と発火 |

### 実走の確認手順（統合テストの罠）

- 🔴 **本統合テストは Docker を要求しない。** `[DockerFact]` / Testcontainers を使わず、
  3 サービスを `WebApplicationFactory` で in-process に起こす。したがって
  **`integration.yml`（develop への push と日次のみ・PR では走らない。IADR-0232 決定 1）を待たずに、
  PR の `ci` ジョブ（`dotnet test <unit>/backend/backend.slnx`）で実走する。**
  ローカルでの実走は `dotnet test src/knowledge/backend/backend.slnx --filter FullyQualifiedName~McpTool`。
- 🔴 **構成の注入は `UseSetting` で行う。`ConfigureAppConfiguration` では間に合わない。**
  3 サービスの `Program.cs` はトップレベル文で `ConnectionStrings:DefaultConnection`（#1012）と
  `RabbitMq:ConnectionString`（#1022）を**ビルダ構築中に即座に読み**、未設定なら例外で落ちる。
  `ConfigureAppConfiguration` で足した値が見えるのはその後である。
  `IntegrationTestFactory.cs` が記録している 3 件の実測事故（`Pipeline:ConfigPath` /
  `RabbitMq:ConnectionString` / `ConnectionStrings:DefaultConnection`）と同型の罠であり、
  **「統合テストの config 上書きは効く」は一般化できない —— 読まれる時点で決まる。**
- 🔴 **環境変数（`[ModuleInitializer]`）は使わない。** 既存の Docker 依存テストが同じキーを
  フィクスチャから `UseSetting` で与えているため、プロセス全体へ env を置くと
  **フィクスチャの失敗と構成の注入漏れが読み分けられなくなる**（#1032 の再発）。
  新しい器の中だけで `UseSetting` する。
- 🔴 **Wolverine の外部トランスポートを切る。** 切らないとテストホストの起動が実ブローカへ
  接続を試み、約 135 秒ハングする（DocumentService.Tests / GraphService.Tests の実測と同型）。

### 変異試験（実測。**否定形テストは「落ちること」を確かめないと意味を持たない**）

| 変異 | 実測 |
| --- | --- |
| ① 選別（`Publishable` の除外）を外し、候補を素通しで申告する | **否定形が落ちた** —— 端点 1 件（`個人資料のツールは申告に現れない`）＋ 選別 3 件（3 サービス）＋ 結合 1 件（`個人資料のツールは公開構成が要求しても載らない`）の計 5 件。**陽性対照は全部通った** |
| ② 申告を常に空にする（`Publishable` が `[]` を返す） | **陽性対照が落ちた** —— 端点・選別 10 件（Document 3 / Retrieval 3 / Graph 4）＋ 結合 4 件。**否定形（`個人資料のツールは申告に現れない` 等）は通った** ＝ 空実装が緑にならないことの確認 |
| ③ 判定を否定（`!= "organization"`）で書き換える | **陽性対照だけが落ちた** —— 3 サービスの `選別は個人資料でない候補を残す`（**文書スコープを持たない候補**が落ちるため）。否定形は 3 サービスとも通った ＝ **集合帰属と否定は否定形テストでは見分けが付かない**ことの実測 |

**変異はすべて戻し、残渣 0 を確認した**（`grep -rn MUTATION src/` が 0 件。戻したあとの再実行も緑）。

## 計画書との差異

- 差異: なし。公開範囲・ツール名・`hops` の既定 2 / 上限 3・個人資料の一律除外は計画の確定事項をそのまま写した。
  計画が実装へ委任した範囲（申告スキーマの詳細形・置き場所）だけを本書と IADR-0292 で決めている。

## 未決事項・残件

1. **共通エンベロープの実行口（`endpoint` の実体）は未実装である。** 理由は「権限伝播の方式が未決」だからである ——
   `HttpToolInvoker` は `ToolInvocationScope` を**本文で**渡す（方式 B）が、GraphService は
   `GraphAccessResolver` で JWT から自分でスコープを解決する型（方式 A）であり、
   `GraphServiceNeighborExpander` は「**解決済み scope を本文で渡す方式 B を採ってはならない ——
   採ると『本文で渡された scope を信じる』口が開き、そこへ到達できる誰もが任意の scope を主張できる**」と
   明記している。McpServer は現在**資格情報を下流へ運んでいない**。半端に実装すると
   セキュリティホールになるため、**方式の裁定を別 issue へ切り出す**（IADR-0292 決定 2）。
2. **`deploy/**` の配線**（`Mcp__Services__*` の注入、Istio Ingress の `/mcp` ルーティング、レート制限初期値）。
   領域宣言で触れない。
3. **申告 DTO の `Platform.Shared.Contracts` 昇格**（IADR-0269 決定 6）。`scripts/contract-schema-baseline.json` の
   更新を伴い領域外。
4. `notifications/tools/list_changed` の配信（IADR-0269 のフォローアップ。本 issue の射程外）。
5. **テスト仕様書（`docs/tests/FR-16_mcp-server.md`）の実装マッピングに、新しいテストクラス名を書けていない。**
   被覆ラチェットの床（`scripts/test-spec-coverage-baseline.json`）を同時に上げないと
   `check-test-spec-coverage` が落ちる仕組みであり、`scripts/**` は領域宣言の外だった。
   クラス名の代わりに置き場所を書いてある。**床へ載せるところまでが追随作業として残る。**
6. **`docs/tests/SC-12_*.md` が無い。** SC-12（MCP クライアント登録管理画面）は計画レンジに在るが
   テスト仕様書が無く、`check-test-traceability` は「テスト側のコメントが参照している ID に
   仕様書が無い」を fail にする。本作業のテストが測るのは**申告する側**（FR-16 / FR-19）であり
   SC-12 の画面ではないため、**テストコメントの起点 ID から SC-12 を外した**（仕様書を捏造しない）。
   SC-12 のテスト仕様書は、クライアント登録管理を扱う issue の射程である。

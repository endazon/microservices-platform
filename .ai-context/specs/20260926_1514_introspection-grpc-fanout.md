---
title: 実効構成の収集（introspection）を扇形のまま east-west gRPC へ移す（#1255 経路 ⑤・スライス 1）
type: spec
status: draft
related_ids: [FR-15, FR-16, NFR-09, NFR-16, ADR-0018, ADR-0024, ADR-0029, ADR-0075, ADR-0089, IADR-0017, IADR-0026, IADR-0029, IADR-0117, IADR-0379, IADR-0397, IADR-0419, IADR-0426, IADR-0458, IADR-0462]
author: Claude（実装）
created: 2026-09-26
updated: 2026-09-26
plan_refs: []
---

# 仕様書: 実効構成の収集を扇形のまま gRPC へ移す（#1514 / #1255 経路 ⑤）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-15（構成情報 API・ドリフト検出）／NFR-09（認可）／NFR-16（east-west の統一）
- 関連 ADR: `ADR-0018`（自己申告と宣言の突合）／`ADR-0029`（east-west は gRPC。2026-08-04 追記で全移行）／
  `ADR-0075` 決定 2〜6（一括移行・基盤先行）／`ADR-0089` 決定 1（退役をもって解けたと数える）
- 実装 ADR: [[IADR-0029]]（到達不能と適用漏れの区別）／[[IADR-0379]]（置き場・versioning・h2c・s2s・並走）／
  [[IADR-0419]] 決定 1（扇形は宛先の側が面を実装しないと移らない）／[[IADR-0458]]（BFF の利用者資格情報中継は射程外）／
  [[IADR-0462]]（**本 PR で新設**）
- 裁定: #1255 のオーナー裁定（2026-09-25）「④⑤ は配り先のすべてのサービスに gRPC の口を実装して移す。BFF → サービスの 15 本は対象外」
- issue: #1514（本スライス）／親 #1255（閉じない）

## 起点の確認

基点 `origin/develop` `e2581d3e`。`git rev-parse --is-shallow-repository` = `false`。

## 経路 ④⑤ の列挙（コードから引いた）

| # | 呼び出し元 | 宛先集合（構成で開く） | 本文 | 認証（REST） |
| --- | --- | --- | --- | --- |
| ⑤ | `Platform.Bff` の `HttpEffectiveConfigCollector`（`AddPlatformConfigInspection`。定期ドリフト検出・構成情報 API・適用直後の即時検出が同じ経路） | `Introspection:Services:<名前>` → 各サービスの `GET /internal/introspection` | `ServiceIntrospectionDto` | 無し（メッシュ内部限定） |
| ④-a | `McpServer` の `HttpToolDeclarationSource`（起動時＋既定 5 分） | `Mcp:Services:<名前>` → `GET /internal/mcp-tools`（Document / Retrieval / Graph の 3 つが実装） | `ServiceToolDeclarations` | 無し |
| ④-b | `McpServer` の `HttpToolInvoker` | 申告の `endpoint`（絶対 URL `<自サービス>/internal/mcp/<ツール名>`） | `{scope, arguments}` | 無し |

🔴 **④-b の宛先に実行口は 1 つも無い**（Document / Retrieval / Graph のどれも `/internal/mcp/*` を Map していない。
`grep -rn 'MapGroup("/internal\|"/internal/mcp' src` で 0 件）。したがって今の REST の実行経路は常に 404 であり、
輸送の差し替えだけでは 1 経路も通らない —— 判断が要る（#1516）。

## スライス計画

| スライス | issue | 内容 | 依存 |
| --- | --- | --- | --- |
| 1 | #1514 | ⑤ introspection（**本 PR**） | — |
| 2 | #1515 | ④-a ツール申告の収集（`platform.mcp.v1`・3 宛先・McpServer の宛先ごと opt-in。共有契約への昇格を兼ねる） | 1（宣言ファイル領域が重なる） |
| 3 | #1516 | ④-b ツール実行（`decision-needed`。申告 `endpoint` の扱いと実行口の実装主体） | 2 |
| 4 | #1517 | ④⑤ の REST 退役（#1255 残射程 2 の反転と同じ段） | 1〜3 |

## 母集合（着手時に自分で引いた）

走査は 3 軸で行った（パスの除外は `bin` / `obj` / `node_modules` / `src/ai-stock-trading` のみ。拡張子で絞らない）。

1. **受け口の側** —— `grep -rln "MapPlatformIntrospection()" src`（試験を除く）: **14 サービス**
   （knowledge: AiAnalysis / Conversion / Dashboard / DataSource / Document / Feedback / Graph / Ingestion / Retrieval / Wiki、
   platform: Authorization / LlmGateway / McpServer / Notification）
2. **宛先の構成** —— `grep -rn "Introspection__Services\|Introspection:Services\|\"Introspection\""`:
   compose 13 行・helm 13 行・BFF `appsettings.json` 1 箇所（ローカル既定 2 宛先）。**mcp-server は収集先に無い**
3. **h2c リスナの有無** —— `grep -rln "AddPlatformGrpcListener()" src`（試験を除く）: 7 サービス
   （Dashboard / Document / Graph / Retrieval / Authorization / LlmGateway / Notification）。
   helm の `grpcPort` も同じ 7 つ（document / retrieval / authorization / llmgateway / dashboard / graph / notification）
4. **認証の有無**（gRPC 面の `ServiceCaller` の前提）—— `grep -rln "AddPlatformAuth(" --include=Program.cs`:
   Conversion と Ingestion の 2 つだけが持たない
5. **呼び出し側** —— `IEffectiveConfigCollector` の実装・登録: `HttpEffectiveConfigCollector` 1 つ（`ConfigInspectionExtensions`）。
   試験の偽実装（`BffTestFactory` / `ConfigVersionHistory*Tests` / `ConfigInspectionServiceTests`）は DI を差し替えるので影響しない

⇒ 本 PR が触るもの:

| 区分 | 対象 | 本数 |
| --- | --- | --- |
| gRPC 面（共通基盤に 1 つ。`MapPlatformIntrospection` が REST と対で張る） | 受け口を持つ全サービス | 14 |
| h2c リスナの追加（`AddPlatformGrpcListener`） | 収集先のうち未設定のもの: AiAnalysis / Conversion / DataSource / Feedback / Ingestion / Wiki | 6 |
| 認証の追加（`AddPlatformAuth`。ミドルウェアは登録があれば WebApplication が自動で挟む —— 明示の `Use*` を外す変異で試験が緑のままだったことで確認） | Conversion / Ingestion | 2 |
| helm `grpcPort` / compose `Grpc__Port` ＋ `expose` の追加 | 上の 6 | 6 |
| BFF の gRPC 宛先（`Introspection__GrpcServices__*`） | helm・compose の収集先 13 | 13 × 2 |

### 除外とその理由

- **McpServer の h2c リスナ・`grpcPort`**: 収集先に無い（軸 2）ので gRPC で呼ばれない。gRPC 面自体は共通基盤が張る
  （張り忘れを構造で起こさないため）。**収集先へ加えるのは FR-15 の挙動変更**（ドリフト検出の対象が 1 つ増える）なので本 PR では行わない
- **BFF `appsettings.json` のローカル既定**（`:5001` / `:5006` の REST 2 宛先）: ローカル実行の既定であり gRPC アドレスを持たない。
  REST のまま残しても並走の正（REST）どおりに動く
- **BFF → 各サービスの利用者資格情報中継**: [[IADR-0458]] によりエッジであり east-west に数えない
- **AST の introspection**: AST のサービスは `Introspection__Services` に無い（軸 2）

## 受け入れ基準（#1514 と同じ）

- [ ] Given s2s トークン / When gRPC で自己申告を取る / Then REST と同じ内容が返る（`target` の null と空文字が区別される）
- [ ] Given トークン無し・利用者トークン（管理者を含む） / When gRPC で取る / Then `UNAUTHENTICATED` / `PERMISSION_DENIED`
- [ ] Given 14 サービスの本番 `Program.cs` / When 起動する / Then gRPC の自己申告面が `ServiceCaller` 付きで張られている
- [ ] Given gRPC 宛先を構成した収集先と REST だけの収集先 / When 収集する / Then 各々が構成どおりの輸送で集まり、失敗は到達不能に入る
- [ ] Given helm・compose / When 読む / Then 全収集先に gRPC 宛先と `grpcPort` / `Grpc__Port` が揃う
- [ ] 試験は 0.0.0.0 で待ち受けない（ループバックの器か TestServer）
- [ ] `check-proto-contracts` / `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（両ユニット）が通る

## 設計

1. **proto** `platform.introspection.v1.ServiceIntrospection/Get`（`Platform.Shared.Contracts/Protos/platform/introspection/v1/`）。
   原則 A: null を取り得るのは `PortSelectionDto.Target` だけで `optional string`。enum は持たない。
   空の `service` は「申告として無効」とし呼び出し側で到達不能へ（REST の空応答と同じ枝）
2. **受け口** `IntrospectionGrpcService`（`Platform.Shared.Infrastructure`）。DI の同じ `ServiceIntrospectionDto` を写すだけ。
   `[Authorize(Policy = ServiceCaller)]`。`AddPlatformIntrospection` が `AddGrpc` を、`MapPlatformIntrospection` が
   `MapGrpcService` を呼ぶ（REST と gRPC の面を必ず対にする）
3. **呼び出し側** `EffectiveConfigCollector`（新しい `IEffectiveConfigCollector`）: 宛先 = `Services` と `GrpcServices` のキーの和。
   `GrpcServices` に空でないアドレスが在れば `GrpcServiceIntrospectionCollector`、無ければ `HttpEffectiveConfigCollector.CollectOneAsync`。
   集約は 1 つ（`HttpEffectiveConfigCollector.Aggregate`）
   - 期限: `Introspection:TimeoutSeconds`（REST のタイムアウトと同じ値を引く）
   - リトライ: 無し（REST も無し。定期検出の次の周期が再試行）
   - 失敗: 全 status・トークン取得失敗・期限切れを到達不能へ。`UNAUTHENTICATED` / `PERMISSION_DENIED` は Error ログ（配線不備）
   - 取り消し: 呼び出し側 ct 由来だけを `OperationCanceledException` で外へ
   - `AddPlatformConfigInspection` は `GrpcServices` が構成されたときだけ s2s トークンと gRPC 収集器を登録する。
     構成されているのに収集器が無ければ起動時に落とす
4. **配備**: 上表のとおり。BFF の資格情報（`bff` client ＋ `platform-service`）は既存のものを使う（realm・Secret の追加なし）

## 試験の方針

- 受け口と収集器の往復は `Platform.Shared.Infrastructure.Tests` に**ループバック（127.0.0.1）限定の実 Kestrel**を立てて測る
  （h2c・s2s・`optional` の往復・拒否の 3 status・宛先ごとの輸送選択・到達不能の隔離）。待受アドレスがループバックであることを起動直後に表明する
- 14 サービスの本番配線は既存の `TestWebApplicationFactory`（TestServer。待ち受けない）で、gRPC の経路が `ServiceCaller` 付きで張られていることを見る
- helm・compose の配線は正のファイルを読む試験で固定する（宛先・`grpcPort`・`Grpc__Port`・`AddPlatformGrpcListener` の対応）

## 実施結果

（実装後に追記）

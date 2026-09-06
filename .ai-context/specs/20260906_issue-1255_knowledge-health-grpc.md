---
title: ナレッジ健全性の観測値の報告（GraphService → DashboardService）を east-west gRPC へ移し、しきい値の presence を契約で運ぶ
type: spec
status: in-progress
related_ids:
  - FR-10
  - FR-17
  - FR-18
  - FR-19
  - NFR-09
  - NFR-16
  - NFR-21
  - UC-05
  - SC-10
  - ADR-0002
  - ADR-0006
  - ADR-0029
  - ADR-0030
  - ADR-0065
  - ADR-0075
  - ADR-0076
  - IADR-0139
  - IADR-0256
  - IADR-0265
  - IADR-0299
  - IADR-0353
  - IADR-0379
  - IADR-0389
  - IADR-0397
  - IADR-0400
  - IADR-0401
  - IADR-0402
  - IADR-0408
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 3・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability.md
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md 決定 2
---

# 作業仕様書: ナレッジ健全性の報告を east-west gRPC へ移す（#1255 第 5 スライス）

## 起点

- issue: #1255（east-west 同期呼び出しの gRPC 移行）
- 直前の 4 スライス: [[IADR-0397]]（LlmGateway 埋め込み）／[[IADR-0400]]（LlmGateway テキスト生成）／
  [[IADR-0401]]（認可サービスの 5 呼び出し元）／[[IADR-0402]]（BFF の文書読み取り 4 箇所）
- 設計の正本: [[IADR-0379]] 決定 1〜4（置き場・versioning・h2c・s2s）。**本スライスはこれを変えない。**

## 1. 母集合（自分で引いた。issue 本文の数字は転記していない）

基点 `origin/develop` = `2bb41b6d`。`git rev-parse --is-shallow-repository` = **`false`**（履歴の打ち切り位置を
出典に引いていないことの確認。planning#410）。

### 1.1 母集合の定義（先に決める。数えてから決めない）

**単位は「呼び出し箇所（call site）」である。** 登録（`AddHttpClient`）単位ではない ——
[[IADR-0402]] が BFF で示したとおり、**同じ名前付きクライアントの兄弟で資格情報の有無が割れる**
（`/search` は運び `/search/attribute-values` は運ばない）ため、登録単位では判定できない。

範囲: 追跡下の `src/**/*.cs` のうち、

- **除外**: submodule `src/ai-stock-trading`（AST は `ADR-0075` 決定 4 で本リポジトリの proto に追随する側。
  本リポジトリからは起票も変更もしない）、テストプロジェクト（`*/Tests/**`・`*Tests.cs`・`*TestFactory*`・
  `*Benchmark*`）、**呼び出し先がクラスタ外**のもの（Keycloak／Wiki.js／SaaS コネクタ／オブジェクトストレージ／
  LLM プロバイダ）。
- **含める**: 呼び出し先が MSP のサービス（BFF を含む）である同期 HTTP 呼び出し。

### 1.2 走査（生の出力。加工していない）

```
$ git grep -n "CreateClient(" -- 'src/**/*.cs' ':!src/ai-stock-trading' \
    | grep -v -i "tests\?/" | grep -vi "Tests\.cs\|TestFactory\|Benchmark"
```

（出力 62 行。うち外部宛て・トークン取得・`CreateClient` の private ヘルパ定義を落として下表を作った。
型付きクライアント〔`AddHttpClient<TIface, TImpl>`〕は `CreateClient` を通らないので、
`git grep -nE "PostAsJsonAsync|GetAsync|SendAsync|PostAsync"` で各アダプタの送出行を別に数えた。）

資格情報の有無は **送る側の行**で引いた（[[IADR-0402]] 決定 1。ファイル単位では引けない）:

```
$ git grep -nE "Headers\.(Authorization|TryAddWithoutValidation\(\"Authorization)|AuthenticationHeaderValue|\"Authorization\"" \
    -- 'src/**/*.cs' ':!src/ai-stock-trading' | grep -viE "/Tests?/|Tests\.cs|TestFactory"
```

### 1.3 数えた結果 — **49 箇所**（16 移行済み／27 資格情報を運ぶ／**6 残**）

| 区分 | 箇所 | 内訳 |
| ---: | ---: | --- |
| 移行済み（gRPC 面が在り構成で切り替わる） | **16** | BFF 文書読み取り 4／`BffScopeResolver` 1／AiAnalysis 3（`/complete` `/complete/stream` `/authz/scope`）／Conversion 1／DataSource 1／Graph 2（`/authz/scope`・AI 提案生成）／Ingestion 1／Retrieval 1／Wiki 1／McpServer 1 |
| **利用者の資格情報を運ぶ**（触らない） | **27** | BFF 23（[[IADR-0402]] の実測を再現）／AiAnalysis `RagOrchestrator`→Retrieval 1／Graph `HttpDocumentTagWriter`→Document 1／Retrieval `GraphServiceNeighborExpander`→Graph 1／McpServer `HttpToolInvoker` 1 |
| **残**（資格情報を運ばない・未移行） | **6** | 下表 |

**残 6 箇所の内訳**

| # | 経路 | 面 | 本スライス | 射程外の理由 |
| --- | --- | --- | --- | --- |
| 1 | BFF → RetrievalService | `POST /search/attribute-values` | — | 兄弟の `/search` が資格情報を運ぶ。**ホップごと ABAC の未決と同じ PR で決める**（[[IADR-0402]] フォローアップ 1） |
| 2 | DocumentService → NotificationService | `POST /internal/notifications` | — | §2 の判断（束ねない） |
| 3 | **GraphService → DashboardService** | `POST /internal/knowledge-health/observations` | **✅ 本 PR** | — |
| 4 | GraphService → DocumentService | `GET /internal/tags/names` | — | 同じ名前付きクライアントの兄弟（`HttpDocumentTagWriter`）が**資格情報を運ぶ**。切り離しの判断が要る（§2） |
| 5 | McpServer → 構成で決まる N サービス | `GET /internal/mcp-tools` | — | **宛先が構成（`Mcp:Services`）で決まる扇形**。全申告元を一度に移すスライスに属する（[[IADR-0402]] フォローアップ 3 と同型） |
| 6 | `HttpEffectiveConfigCollector` → 全サービス | `GET /internal/introspection` | — | 同上（扇形）。**#1255 の作業指示で名指しの射程外** |

**issue #1255 本文の「残 31 本」は古い。** 本スライス着手時点の残は **6 箇所**である
（[[IADR-0379]] §結果 の「31 本」は 4 スライス着地前・かつ登録単位に近い数え方であり、
本表とは単位も時点も違う。**同じ数え方で数え直したのが上表である**）。

### 1.4 陽性対照（走査が本当に効いていることの対）

| 対照 | 期待 | 結果 |
| --- | --- | --- |
| **PC-1（陽性・移行済み）** `GraphService/LlmGatewaySuggestionClient` | 走査に現れ「移行済み」へ落ちる | ✅ 現れた（`LlmGatewayGrpcSuggestionClient` が対で在る） |
| **PC-2（陽性・資格情報あり）** `GraphService/HttpDocumentTagWriter:31` | 走査に現れ「運ぶ」へ落ちる | ✅ 現れた（送出行 34/36 が `Authorization` を付ける） |
| **PC-3（陰性）** `DocumentService/ObsidianSyncEndpoints` | **母集合に現れない** | ✅ 現れない。**射程外リストに名指しされているが、そもそも送出しない** —— `Headers.Authorization` を読むのは**自分の受け口の資格情報**（同期トークン）としてであり、後段への転送ではない（同ファイル 34 行目の注記）。走査は「送る側の行」で引いたのでこれを混入させなかった |
| **PC-4（再現）** BFF の 28／23／5／4 | [[IADR-0402]] の実測と一致 | ✅ 一致（28 箇所・運ぶ 23・運ばない 5・うち移行済み 4） |

PC-4 が一致したので、**同じ判定軸が別の時点でも同じ数を出す**ことを確かめた。

## 2. 束ねるかどうかの判断 — **束ねない**（1 issue = 1 PR の原則に戻す）

作業指示は候補 2 本（Document→Notification と Graph→Dashboard）を挙げ、
[[IADR-0139]] 決定 1 の「裁定済みの同型な契約追加」に当たるかを自分で判定せよ、とした。
**6 条件のうち A と F が満たされない。** 実測は次のとおり。

| 条件 | 判定 | 実測（推測ではない） |
| --- | --- | --- |
| **A. 同一資源** | ❌ **満たさない** | 資源が別である。①`platform` ユニットの `NotificationService` の通知受け口（`/internal/notifications`。DTO 群は `NotificationIngressDtos`）／②`knowledge` ユニットの `DashboardService` の観測値受け口（`/internal/knowledge-health/observations`。DTO は `KnowledgeHealthReportRequest`）。**proto の置き場すら別プロジェクト**（`Platform.Shared.Contracts` vs `Knowledge.Contracts`）であり、これらを「1 系統」と定めた計画 ADR も無い。[[IADR-0139]] は「同じ画面を共有するだけでは足りない」と言い、ここは画面すら共有しない |
| **B. 裁定が済んでいる** | ⭕ | [[IADR-0379]] 決定 1〜4 が形を確定している |
| **C. 非破壊側に収まる** | ⭕ | どちらも新規 proto の追加（削除・番号変更なし） |
| **D. 1 コミット = 1 issue** | — | どちらも #1255 の一部なので自明に満たす |
| **E. 着手済みを含まない** | ⭕ | どちらもブランチ・PR なし |
| **F. 契約の追加に閉じる** | ❌ **満たさない** | どちらも**受け側サービスの配備形が変わる**（新しい h2c ポート・helm `grpcPort`・compose `expose`）。①はさらに **realm に新しい confidential client `document-service` と secret の注入経路**を要する（`document-service` は realm の `users[]` にも `clients[]` にも無い ——実測）。[[IADR-0139]] 条件 F は「外部システムの配備を伴わない」ことを求めており、IdP への client 追加はこれに当たる |

したがって **1 本に絞る**。「固定費が惜しい」は [[IADR-0139]] が明示的に退けた理由付けではないが、
**条件 A を満たさない束は理由の如何によらず作れない**。

### 選んだのは ③ GraphService → DashboardService である。理由（実測）

1. **呼び出し元の s2s 資格情報が既に全部配線済みである。** realm の `clients[]` に `graph-service`
   （`serviceAccountsEnabled: true`）、`users[]` に `service-account-graph-service`
   （`realmRoles: ["platform-service"]`）が在り、compose にも `ServiceToken__ClientId: graph-service` が在る
   （`deploy/keycloak/microservices-platform-realm.json` 869-872 行・`deploy/docker-compose.yml` 693-694 行）。
   ②を選ぶと **realm に新しい主体と secret を足す**ことになり、#1301 が是正した
   「`users[]` への service account 登録漏れ」と同じ事故の面が 1 つ増える。
   **secret を増やす判断は、輸送の差し替えとは別の PR で単独に受けるべきである。**
2. `Knowledge.Contracts` は [[IADR-0402]] で既に codegen を持つ（`Protos/**/*.proto` の glob）。
   proto の追加でプロジェクト構成は 1 バイトも変わらない。
3. **面が 1 rpc に閉じる**（`ReportAsync` 1 本）。呼び出し元は 1 箇所（`HttpKnowledgeHealthReporter:46`）。

②（Document → Notification）は**次のスライス**として残す。#1255 の残は本 PR 後 **5 箇所**になる。

## 3. 🔴 触らないもの（射程外。作業指示の名指しと、自分で引いた母集合の突き合わせ）

| 名指しされたもの | 母集合での位置 | 触らない理由 |
| --- | --- | --- |
| `AiAnalysisService/RagOrchestrator.cs` → Retrieval `/search` | 「運ぶ」27 の 1 | 呼び出し先が**利用者の権限で動く**（ホップごと ABAC）。[[IADR-0379]] 決定 4 を守ったまま運ぶ手が無い。token exchange の候補 |
| `GraphService/HttpDocumentTagWriter.cs` → Document | 同上 | 承認者本人の資格で `POST /documents/{id}/tags` を書く。サービスアカウントは書き込み権を持たない |
| `RetrievalService/GraphServiceNeighborExpander.cs` → Graph | 同上 | 二段検索のホップごと ABAC |
| `DocumentService/ObsidianSyncEndpoints.cs` | **母集合に無い**（PC-3） | そもそも送出しない。**受け口**であり、読む `Authorization` は同期トークン（自分の資格情報）である |
| `Platform.Bff` の 14 本と `/internal/introspection` | 「運ぶ」23 ＋「残」1（#6） | 同上／introspection は扇形で本リポジトリだけでは完結しない |
| `McpServer/HttpToolInvoker` | 「運ぶ」27 の 1 | 利用者トークンで任意の登録ツールを叩く |

加えて **編集しない**（別 PR が動いている）: `deploy/mail-relay/`・`scripts/check-realm-constraints.js`・
`.github/workflows/integration-stack.yml`・`scripts/check-stack-ready.js`。
**realm の JSON は読むだけ**で足りる（本スライスは realm を変更しない。§2 の理由 1）。

## 4. 実装（何を作るか）

### 4.1 契約 — `knowledge.dashboard.v1.KnowledgeHealthReport`

置き場: `src/knowledge/backend/Shared/Knowledge.Contracts/Protos/knowledge/dashboard/v1/knowledge_health.proto`
（[[IADR-0379]] 決定 1。所有者は呼び出される側＝ knowledge ユニットの DashboardService）。

rpc は 1 本（unary）: `Report(ReportRequest) returns (ReportResponse)`。

🔴 **proto3 に null は無い。REST の「省略」を presence で運ぶ**（[[IADR-0397]] 決定 5 / [[IADR-0400]] 決定 4 /
[[IADR-0402]] 決定 4 と同型）。本面の写しは 3 つある。

| 契約 | REST の意味 | proto3 の「未指定」 | 写し |
| --- | --- | --- | --- |
| `threshold_days` | **項目そのものを出さない = しきい値なし**（受け口はしきい値の行を**削除**する） | `0` | 🔴 **`optional int32`。** 非 `optional` にすると「省略」が `0` になり、検証器が `thresholdDays must be greater than zero` で **400** を返す ——「しきい値を持たない 3 指標の報告が全部落ちる」形で壊れる |
| `doc_scope` | `null` = 個人資料ではない／`"private-note"` = 個人資料 | `""` | `optional string`。`""` を書くと台帳に空文字が入り、`null` との区別が消える |
| `dimension` | 項目を出さない = 軸を持たない指標 | `""` | `optional string`。内訳の集計が `""` という軸を 1 本作ってしまう |

`ReportResponse` は REST の 202 本文（`{ indicator, accepted }`）と同形。

### 4.2 呼び出し先（DashboardService）

1. `Features/KnowledgeHealth/Report/ReportKnowledgeHealthUseCase.cs` を括り出す。
   🔴 **REST の端点と gRPC の rpc が同じ関数を通る**（[[IADR-0397]] `EmbedUseCase` /
   [[IADR-0400]] `CompletionUseCase` / [[IADR-0402]] `DocumentReadUseCase` と同じ形。**判定器を 2 つにしない**）。
   `ADR-0065` 決定 2 の適用としては「1 操作の実体」なので、その操作のフォルダに置く。
2. `Features/KnowledgeHealth/Report/GrpcService.cs` —— `[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]`。
   検証失敗 → `INVALID_ARGUMENT`（REST の 400 と同値）、それ以外の失敗 → `INTERNAL`（REST の 500 と同値）。
3. `Program.cs` に `builder.AddPlatformGrpcListener();` と `app.MapGrpcService<...>();`。
   `.csproj` に `Grpc.AspNetCore`（CPM に版が在る。新しいライブラリは足さない）。

🔴 **判定の位置を動かさない。** 受け口の統制は現状「メッシュの mTLS ＋ ネットワーク分離」であり
REST 側は無認証である（[[IADR-0299]] 決定 4・利用者裁定）。gRPC 面に `ServiceCaller` を掛けるのは
**現状より狭い**向きであり（[[IADR-0401]] 決定 1・[[IADR-0402]] 決定 3 と同じ向き）、REST 側は変えない。

### 4.3 呼び出し元（GraphService）

`Infrastructure/ExternalServices/GrpcKnowledgeHealthReporter.cs`（`IKnowledgeHealthReporter` の 2 つ目の実装）。
`Services:DashboardServiceGrpc` が構成されたときだけ登録し、無ければ `HttpKnowledgeHealthReporter` のまま
（**並走中の正は REST**。[[IADR-0379]] 決定 5）。

🔴 **チャネルはキー付き。** GraphService は既に 2 つの宛先を持つ（認可サービス＝**キー無し**・
LlmGateway＝**キー付き**）。3 つ目をキー無しで足すと認可サービス宛のチャネルへ繋がる
（[[IADR-0400]] / [[IADR-0402]] 決定 6 と同じ理由）。

🔴 **縮退の枝を現行と同じにする。** `HttpKnowledgeHealthReporter` の枝は 3 つで、
**gRPC 実装はこれを 1 つも増やさず・1 つも減らさない**。

| 事象 | 現行 REST | gRPC | 副作用 |
| --- | --- | --- | --- |
| 成功 | 2xx | `RpcException` が出ない | `metrics.RecordDelivered(indicator)` **のみ** |
| 受理されない | 非 2xx → `LogError`（status つき） | `RpcException` → `LogError`（`StatusCode` つき） | **`RecordDelivered` を呼ばない**・**例外を投げない** |
| 到達できない／トークン取得失敗 | 例外 → `LogError`（「受け口へ到達できない」） | `RpcException`（UNAVAILABLE）・`InvalidOperationException` | 同上 |
| 呼び出し元のキャンセル | **伝播させる**（握らない） | 同じ | — |

🔴 **故障を「該当なし」に化けさせない**（[[IADR-0256]] 決定 3）: 失敗時に `RecordDelivered` を呼ばないことが
`absent` 系アラートの土台である（[[IADR-0389]] 決定 5）。**試みた回数を数える形へ変えない。**

### 4.4 配備

- helm `values.yaml`: `services.dashboard.grpcPort: 8081`／`graph` の env に `Services__DashboardServiceGrpc`。
- compose: `dashboard-service` に `expose: "8081"` と `Grpc__Port: "8081"`／`graph-service` に
  `Services__DashboardServiceGrpc: http://dashboard-service:8081`。
- **readiness は HTTP（8080）の `/health/ready` のまま**（[[IADR-0379]] 決定 3）。
- **realm は変えない**（`graph-service` の主体が既に在る。§2 理由 1 で実測を記した）。

## 5. 受け入れ基準

1. `knowledge.dashboard.v1` の proto が `check-proto-contracts.js` の R1〜R4 を通り、baseline が更新される。
2. `Services:DashboardServiceGrpc` **未設定**なら `HttpKnowledgeHealthReporter` が解決される（REST が正）。
   設定すると `GrpcKnowledgeHealthReporter` が解決される。**コードを変えずに戻せる。**
3. gRPC 面は `ServiceCaller` を要求する。**管理者の利用者トークンでも `PERMISSION_DENIED`**（リフレクション試験＋実 Kestrel 試験）。
4. REST と gRPC が**同じ関数**（`ReportKnowledgeHealthUseCase`）を通る。同じ入力で同じ副作用（置換・しきい値の upsert/delete）になる。
5. `threshold_days` を添えない報告が、受け口でしきい値の行を**削除**する（`0` として保存しない・400 にならない）。
6. `doc_scope` / `dimension` の未指定が `null` として保存される（`""` にならない）。
7. 縮退の 3 枝が §4.3 の表のとおり（値・副作用とも）。
8. h2c ポートを有効にしても HTTP/1.1 のポート（REST・`/health/*`）が残る。
9. 既存の試験を 1 本も減らさない。

## 6. テスト方針

| # | 試験 | 置き場 |
| --- | --- | --- |
| T-01 | proto ↔ 要求 DTO の写像（presence 3 つの往復） | `DashboardService.Tests` |
| T-02 | gRPC の `Report` が REST の端点と同じ置換・同じしきい値の upsert/delete を起こす | `DashboardService.Tests`（実 Kestrel） |
| T-03 | 検証失敗 → `INVALID_ARGUMENT`（REST の 400 と同じメッセージ） | 同上 |
| T-04 | 利用者トークン（管理者ロール）→ `PERMISSION_DENIED`／トークン無し → `UNAUTHENTICATED` | 同上 |
| T-05 | `[Authorize(Policy = ServiceCaller)]` がリフレクションで在る | 同上 |
| T-06 | h2c を有効にしても HTTP/1.1 の `/health/ready` が生きている | 同上 |
| T-07 | `RpcException` のとき **`RecordDelivered` を呼ばず・例外を投げない** | `GraphService.Tests` |
| T-08 | トークン取得失敗（`InvalidOperationException`）でも同じ縮退 | 同上 |
| T-09 | 呼び出し元のキャンセルは**伝播する**（握らない） | 同上 |
| T-10 | 成功時にだけ `RecordDelivered` が 1 回 | 同上 |
| T-11 | 配線: 構成の有無で解決される実装が入れ替わる | `GraphService.Tests` |
| T-12 | helm / compose の `grpcPort` ↔ `Grpc__Port` の一致 | `scripts.test.js` 既存 or 配備試験 |

## 7. 変異試験（着手後に実走して記録する）

1. `optional int32 threshold_days` → `int32` に落とす（presence を捨てる）
2. `GrpcKnowledgeHealthReporter` の失敗枝で `RecordDelivered` を呼ぶ（故障を成功に化けさせる）
3. `[Authorize(Policy = ServiceCaller)]` を外す
4. `doc_scope` を `optional` から非 `optional` へ落とす

## 8. 確かめていないこと

- 稼働クラスタでの h2c 往復（Pod の再起動を要する）。[[IADR-0402]] と同じく**未実測**である。
- Istio の `appProtocol: grpc` が dashboard-service の Service で期待どおり効くこと（テンプレート描画までは検査するが、実クラスタでは見ていない）。

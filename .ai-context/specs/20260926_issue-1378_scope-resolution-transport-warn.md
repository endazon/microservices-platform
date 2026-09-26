---
title: 作業仕様書 — 認可スコープ解決（REST）が輸送失敗・非 2xx・空本文で deny へ倒れた理由を WARN へ出す（#1378 受け入れ基準 2）
type: spec
status: done
related_ids:
  - FR-05
  - NFR-09
  - ADR-0004
  - ADR-0036
  - IADR-0379
  - IADR-0401
  - IADR-0413
author: claude
created: 2026-09-26
updated: 2026-09-26
issue: "#1378"
---

# 作業仕様書 — 認可スコープ解決の縮退理由を WARN へ出す

## 起点

- issue: #1378（稼働クラスタで人の利用者の文書一覧が 0 件・作成 403）
- 本 PR が扱うのは **受け入れ基準 2**「認可スコープ解決の輸送失敗が WARN 以上のログに出る（deny-by-default に倒れた理由が読める）」だけである
- **受け入れ基準 1**（再デプロイ後に `verify-oidc-edge-flow.sh`（`OIDC_USER=admin`）が全段 PASS）は稼働クラスタの再構築を要する**所有者作業**であり、本 PR では行わない（PR 本文に手順を置く）。したがって PR は `Refs #1378`（Closes にしない）

### 背景（issue の切り分けから）

`AuthzScopeGrpcClient`（gRPC 経路）は `RpcException` / s2s トークン取得失敗を WARN で出してから deny へ倒す。
一方 **REST 経路（並走中の正。[[IADR-0379]] 決定 5）は、非 2xx・不達・空本文をすべて無言で deny へ畳む**。
s2s トークンの取得失敗も `ServiceTokenHandler` が `HttpRequestException` へ畳む（[[IADR-0413]]）ので、同じ無言の枝へ落ちる。
その結果、稼働環境では「一覧 0 件」「作成 403」としか見えず、切り分けに 1 時間以上を要した（issue 本文 方針 3）。

## 母集合（着手時に自分で引いた）

走査は誤りの側（無言で deny へ畳む形）から 4 軸で行った（パス除外は `bin` / `obj` のみ。試験は呼び出し元の特定にだけ使う）。

1. **宛先** —— `grep -rn --include=*.cs '"/authz/scope' src`（試験を除く本番コード）: 6 箇所
   `BffScopeResolver` / `RagOrchestrator`（AiAnalysis）/ `GraphAccessResolver` / `SearchAccessResolver`（Retrieval）/ `WikiAccessResolver` / `AuthorizationServiceRegistrarAttributes`（McpServer）
2. **畳み先の形** —— `grep -rn --include=*.cs 'AccessScopeResponse(.*false)' src`（試験を除く）: 上の 4 サービス（AiAnalysis / Graph / Retrieval / Wiki）の REST 枝と `AuthzScopeGrpcClient.ResolveScopeAsync`、および認可サービス自身の受け口（`Endpoint.cs` / `GrpcService.cs`）
3. **本文の読み口** —— `grep -rn --include=*.cs 'AccessScopeResponse>' src`（試験を除く）: 1 の 6 箇所と一致（取りこぼし無し）
4. **スコープ解決クライアントの利用者** —— `grep -rln 'AuthzScopeGrpcClient\|AuthzScopeHttpClient\|AuthzScope\.AuthzScopeClient' src`（試験を除く）:
   上記に加え `GrpcRegistrarAttributes`（McpServer gRPC）・各 `Program.cs`（登録のみ）・`DocumentReadGrpcClient` / `GrpcRagSearchTransport` / `GrpcDocumentTagWriter` / `GrpcKnowledgeHealthReporter` / `GrpcGraphNeighborExpander` / `UserDirectoryGrpcClient` / `LlmGateway*`（チャネル・s2s トークンの共用のみでスコープ解決ではない）
5. **呼び出し元**（`BffScopeResolver.ResolveAsync(`）: knowledge BFF の `DocumentBffEndpoints`（3）/ `PrivateNoteBffEndpoints`（1）/ `SearchBffEndpoints`（2）—— 解決器の中を直せば全て覆われる

### 各実装の枝ごとの現状（着手時）

| 実装 | 非 2xx | 不達（`HttpRequestException` / `TaskCanceledException`） | 2xx だが本文 `null` | `Granted=false` |
| --- | --- | --- | --- | --- |
| `BffScopeResolver`（REST 枝） | **無言** → null | **無言** → null | **無言** → null | 無言 → null（正当な deny） |
| `RagOrchestrator.ResolveScopeAsync`（REST 枝） | **無言** → deny | **無言** → deny | **無言** → deny | 正当な deny |
| `GraphAccessResolver.ResolveForUserAsync`（REST 枝） | **無言** → deny | **無言** → deny | **無言** → deny | 正当な deny |
| `SearchAccessResolver.ResolveForUserAsync`（REST 枝） | **無言** → deny | **無言** → deny | **無言** → deny | 正当な deny |
| `WikiAccessResolver.ResolveAsync`（REST 枝） | **無言** → deny | **無言** → deny | **無言** → deny | 正当な deny |
| `AuthorizationServiceRegistrarAttributes`（McpServer REST） | WARN 済み | WARN 済み | **無言** → `Unavailable` | 引けた（空集合） |
| `AuthzScopeGrpcClient`（gRPC。BFF / 4 サービス共通） | — | WARN 済み（`RpcException` / `InvalidOperationException`） | — | 正当な deny |
| `GrpcRegistrarAttributes`（McpServer gRPC） | — | WARN 済み（上 ＋ 自前） | — | — |

⇒ **本 PR が触るもの**: 太字の無言の枝（REST 5 実装 × 3 枝、McpServer REST の空本文 1 枝）。

### 除外とその理由

- **`Granted=false`**: 認可サービスが正しく「許可なし」と答えた正常の deny。出すと全利用者の通常操作でログが溢れ、本当の縮退が埋もれる（依頼の指定どおり）
- **`AuthzScopeGrpcClient` / `GrpcRegistrarAttributes`**: 既に WARN を出している（上表）
- **2xx だが本文が JSON として読めない（`JsonException` / `NotSupportedException`）**: どの実装も catch しておらず **deny へ畳まれない**（例外として伝播し、ホストの未処理例外ログに出る）。受け入れ基準 2 の射程（deny に倒れた理由）の外であり、捕まえると挙動が変わる
- **呼び出し側の取り消し（`ct.IsCancellationRequested`）**: catch の条件で除外されて伝播する（既存どおり）。利用者が切っただけで縮退ではない
- **McpServer `FindRegistrarAsync` の本文 `null`**: 「名簿に見つけられませんでした」の WARN が既に出る（縮退の理由は読める）
- **DataSourceService `AuthorizationServiceUserDirectory`（`/authz/users`）**: スコープ解決ではない（名簿）
- **認可サービス自身の `AccessScopeResponse(…, false)`**（`Endpoint.cs` / `GrpcService.cs`）: 判定そのもの（正当な deny）であって輸送の縮退ではない
- **稼働クラスタでの確認**（受け入れ基準 1）: 所有者作業

## 受け入れ基準

- [x] Given REST のスコープ解決 / When 認可サービスが非 2xx を返す / Then WARN が状態コードつきで 1 行出て、戻り値は従来どおり deny（null / `Granted=false`）
- [x] Given REST のスコープ解決 / When 不達・タイムアウト・s2s トークン取得失敗（`HttpRequestException` / `TaskCanceledException`）/ Then WARN が例外の型つき（例外本体を添えて）1 行出て、戻り値は従来どおり deny
- [x] Given REST のスコープ解決 / When 2xx だが本文が `null` / Then WARN が出て、戻り値は従来どおり deny
- [x] Given `Granted=false` の正常応答 / When 解決する / Then WARN は出ない（陰性対照）
- [x] 利用者 ID・属性はログへ載せない（既存の gRPC 側の WARN と同じく、状態・例外の型だけ）
- [x] `BffScopeResolver` は静的のまま、`http.RequestServices` の `ILoggerFactory` からロガーを得る（無ければ出さない）。呼び出し元・既存試験は無変更
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（両ユニット）が通る

## 設計

1. **文言と構造化フィールドを 1 か所に置く** —— `Platform.Shared.Infrastructure/Foundation/Authz/AuthzScopeRestLog.cs`（静的）。
   5 実装が同じ 3 行を持つと文言が散る（#1323 で属性抽出が 6 か所に散った教訓と同型）。gRPC 側の文言（「認可スコープの gRPC 解決に失敗しました（{Status}）」）と対にする。
   - 非 2xx: `認可スコープの REST 解決に失敗しました（HTTP {Status}）。deny-by-default へ縮退します。`
   - 不達: `認可スコープの REST 解決で認可サービスへ届きませんでした（{ErrorType}）。deny-by-default へ縮退します。`（例外本体を添える —— s2s トークン取得失敗は内側の例外に理由がある）
   - 空本文: `認可スコープの REST 応答本文が空でした（HTTP {Status}）。deny-by-default へ縮退します。`
2. **各実装は枝を分けるだけ**（`IsSuccessStatusCode ? Read : null` の三項を if に開く）。戻り値・例外の伝播は 1 文字も変えない
3. **ロガーの入手**
   - `BffScopeResolver`（静的）: 失敗の枝でだけ `http.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(typeof(BffScopeResolver))`。無ければ `NullLogger`
   - `RagOrchestrator`: 既存の `ILogger<RagOrchestrator>? logger`
   - `GraphAccessResolver` / `SearchAccessResolver` / `WikiAccessResolver`: 主構築子の**末尾に既定 null の `ILogger<T>?`** を足す（既存の直接構築 `new X(factory)` / `new X(factory, grpc)` を壊さない。DI は `ILogger<T>` を解決して渡す）
   - McpServer: 既存の `logger`
4. IADR は起こさない（ログの追加であり、設計上の新しい判断を含まない）

## 試験の方針

- 各実装の REST 枝ごとに、記録ロガーで **非 2xx（状態コードが構造化フィールドに載る）・不達（例外型が載る）・空本文** の WARN と、**`Granted=false` で WARN 0 件**を表明し、併せて戻り値が deny のままであることを表明する
- 共通基盤は既存の `RecordingLogger<T>`（`Platform.Shared.Infrastructure.Tests/Testing`）を使う。サービス側の試験プロジェクトは共通基盤の試験プロジェクトを参照しないため、各ファイルに最小の記録ロガーを置く（既存の `DataSourceCredentialExposureTests` / `TermOverlapSimilarityCandidateSourceTests` と同じ流儀）
- 変異: 各実装で WARN の呼び出しを外すと対応する試験が赤になることを確かめる

## 実施結果

### 変更

| 区分 | ファイル |
| --- | --- |
| 文言の 1 か所（新規） | `Platform.Shared.Infrastructure/Foundation/Authz/AuthzScopeRestLog.cs` |
| REST 枝を分けて WARN | `BffScopeResolver.cs` / `RagOrchestrator.cs` / `GraphAccessResolver.cs` / `SearchAccessResolver.cs` / `WikiAccessResolver.cs` |
| 空本文の WARN | McpServer `AuthorizationServiceRegistrarAttributes.cs` |
| 試験（新規） | `BffScopeResolveWarnTests`（11）/ `GraphAccessResolverWarnTests`（5）/ `SearchAccessResolverWarnTests`（5）/ `WikiAccessResolverWarnTests`（5）/ `RagOrchestratorScopeWarnTests`（4） |
| 試験（追記） | McpServer `AuthorizationServiceRegistrarAttributesTests` に 2 件 |

- 追加した試験は **32 件**（platform 13: 共通基盤 11・McpServer 2 ／ knowledge 19）
- 各サービスの `…WarnTests` には、**本番の登録（`AddScoped<IX, X>`）でロガーが実際に注入される**ことの試験を 1 件ずつ置いた
  （構築子のロガーは既定 null の省略可能引数なので、DI が渡さなければ無言のまま他の試験は緑になり得る）
- 共通基盤の試験には、**#1378 の実際の形**（`AddPlatformAuthzScopeHttpClient` の本物の登録 ＋ s2s トークン取得失敗）で、
  WARN が内側の例外（`ServiceToken:ClientId / ClientSecret が未設定`）まで運ぶことを見る 1 件を置いた

### 試験

- platform: Kernel 42・Shared.Infrastructure 426・McpServer 179・Authorization 349・Notification 104・LlmGateway 302・Bff 755（skip 1）。失敗 0
- knowledge: Contracts 89・Wiki 118・Ingestion 83・DataSource 256・Document 570・Retrieval 271・Feedback 39・Conversion 163（skip 6）・Dashboard 93・Graph 637・AiAnalysis 161・IntegrationTests 62（skip 45）。失敗 0
- 待受: 追加した試験はどれもソケットを開かない（`HttpMessageHandler` のスタブのみ）

### 変異試験（21 件。すべて赤）

| # | 変異 | 結果 |
| --- | --- | --- |
| M1 | BFF: 非 2xx の WARN を外す | 赤 4 |
| M2 | BFF: 不達の WARN を外す | 赤 3 |
| M3 | BFF: 空本文の WARN を外す | 赤 1 |
| M4 | BFF: `Granted=false` でも WARN を出す（陰性対照） | 赤 1 |
| M5 | BFF: ロガーを要求の DI から引かない | 赤 8 |
| M6 | 共通: 非 2xx の WARN を Information へ落とす | 赤 4 |
| M7 | 共通: 不達で例外本体を添えない | 赤 2 |
| M8〜M10 | Graph: 非 2xx / 不達 / 空本文の WARN を外す | 赤 2 / 1 / 1 |
| M11〜M13 | Search: 同上 | 赤 2 / 1 / 1 |
| M14〜M16 | Wiki: 同上 | 赤 2 / 1 / 1 |
| M17〜M19 | Rag: 同上 | 赤 1 / 1 / 1 |
| M20 | Graph: 注入されたロガーを使わない | 赤 4 |
| M21 | McpServer: 空本文の WARN を Debug へ落とす | 赤 1 |

### 確かめていないこと

- 稼働クラスタでの出力（受け入れ基準 1 と同じく所有者作業。手順は PR 本文）
- develop の chart では **BFF は `Services__AuthorizationServiceGrpc` を持たず REST で解決する**（DataSource / Retrieval / AiAnalysis / Wiki / Graph / McpServer は gRPC を構成。`values.yaml` の `Services__AuthorizationServiceGrpc` を走査）。
  したがって #1378 の症状（一覧 0 件・作成 403）の経路で新しい WARN を出すのは `BffScopeResolver` である

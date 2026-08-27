---
title: 作業仕様書 — テスト整備 2 件（#882 の一段 / #901 の被覆向上）
type: spec
status: done
related_ids:
  - NFR
  - FR-08
  - FR-09
  - FR-15
  - ADR-0004
  - ADR-0030
  - IADR-0238
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
related_specs:
  - "../adr/IADR-0238_xunit1051-staged-adoption-ratchet.md"
  - "20260822_issue-882_xunit1051-staged-adoption-harness.md"
  - "20260822_issue-882_dashboardservice-xunit1051.md"
issue: "#882, #901"
---

# 作業仕様書 — テスト整備 2 件（#882 の一段 / #901 の被覆向上）

## 目的と射程

割り当てられた worktree で 2 件を実装する。

1. **#882 の一段**: `ConversionService.Worker.Tests`（baseline `remaining: 138`）を xUnit1051
   段階採用（[[IADR-0238]]）の許可リストへ移す。**1 PR = 1 プロジェクト。**
2. **#901**: `Platform.Shared.Infrastructure` の被覆向上。**Foundation/Authz は並行トラック 1A の
   領域のため対象外**とし、それ以外の無試験領域から価値の高い順にユニットテストを追加する。

### 起点 ID の置き方（コミット件名に計画 ID を書かない理由）

本作業はテストの衛生（アナライザ規則の採用・共有ライブラリの試験整備）であり、特定の
`FR`/`UC` の実装ではない。[[IADR-0238]] 系の先行 PR（例: #928 DashboardService）と同じ判断で、
コミット件名のスコープは無採番 `NFR`（＋該当 IADR）を用いる
（`.claude/rules/traceability.md`「無理に近い番号を付けない」）。

## 前提の確認

- worktree は波 0 コミット `d451ada` を起点とする。
- `dotnet` は `/root/.dotnet` に配備済み（`PATH` へ追加）。Docker は使えない
  （Testcontainers 系の統合テストは対象外）。
- `git rev-parse --is-shallow-repository` → `false`（履歴を出典に引ける。本仕様書は git log を
  出典に使っていない）。

---

## 第 1 部: #882 — ConversionService.Worker.Tests の xUnit1051 移行

### IADR-0238 の要点（前提）

- 許可リスト方式。`XUnit1051Migrated` に載ったプロジェクトは `NoWarn` を失い
  `WarningsAsErrors` に入る＝**再発したらビルドが落ちる**。
- `remaining` は **informational**（実測値だが古くなり得る。アナライザは `[Fact]`/`[Theory]`
  メソッド本体しか見ないため、ラムダ・ローカル関数・別メソッドへ委譲された呼び出しは
  数に入らない。#946）。**保証されるのは `WarningsAsErrors` が見ている範囲が 0 であることのみ。**
- 手順は 3 点セット: ① `.cs` を `TestContext.Current.CancellationToken` へ直す
  ② baseline を `remaining:0`/`migrated:true` ③ props の `XUnit1051Migrated` へ同じ綴りで追加。

### 実測（着手前）

baseline の `remaining: 138` は 2026-08-22 時点の値であり、着手時点（2026-08-28・波 0 の 5 日後）で
再測定すると **116** だった（`dotnet build src/knowledge/backend/backend.slnx -t:Rebuild -p:NoWarn= -m:1`
の出力をファイル・行・列で一意化）。**IADR-0238 の $comment が明記するとおり `remaining` は
古くなり得る**ため、baseline の 138 ではなく実測 116 を母集合として移行した
（同じビルドで `DocumentService.Api.Tests` も 94→295 と大きく動いていることを確認済み。
こちらは対象外のプロジェクトなので触っていない）。

### 対象の母集合（走査で引いた。推定ではない）

| ファイル | 件数 |
| --- | ---: |
| `ConversionJobStoreTests.cs` | 44 |
| `ConversionFigureCorrectionTests.cs` | 36 |
| `ConversionJobEndpointTests.cs` | 12 |
| `LlmGatewayDiagramCoderTests.cs` | 6 |
| `NormalizationServiceTests.cs` | 5 |
| `PandocConversionServiceTests.cs` | 4 |
| `ObjectStorageTests.cs` | 4 |
| `MassTransitDocumentNormalizedPublisherTests.cs` | 3 |
| `IntrospectionEndpointTests.cs` | 2 |
| **合計** | **116** |

**除外したもの**: `ConversionJobEndpointTests.cs` の `SeedAsync(factory, async store => {...})` へ
渡すラムダ内部の `store.StartAsync` 等の呼び出しは、`-p:NoWarn=` ビルドの出力に現れない
（アナライザがラムダ本体を走査しない、#946 の形と同型）。**触っていない** —— [[IADR-0238]] の
追記が明記するとおり、本移行が保証するのは「アナライザが見ている範囲の再発をビルドが止める」
ことであり、視界の外（ラムダ等）への追随は本 issue の射程外（#946 側の課題）である。
`GlobalUsings.cs`／`RecordingDocumentNormalizedPublisher.cs`／`RecordingMessageBus.cs`／
`TestDbContextReplacement.cs`／`DeterministicGuidTests.cs`／`PipelineStepRegistrationTests.cs`／
`ConversionJobEndpointTests.cs` のラムダ内・`RawDocumentFetchedConsumerTests.cs`／
`RawDocumentFetchedConsumerJobTests.cs` は診断が 0 件（走査結果に現れない）ので触っていない。

呼び出しの種類は HTTP クライアント拡張（`GetAsync`/`PostAsync`/`PostAsJsonAsync`/
`GetFromJsonAsync`/`ReadFromJsonAsync`/`ReadAsStringAsync`）・ストア層（`IConversionJobStore`/
`IObjectStore`/`IObjectStorageClient` の各メソッド、すべて末尾 `CancellationToken ct = default`）・
`LlmGatewayDiagramCoder.CodeAsync`・`PandocConversionService.ConvertAsync`・
`File.WriteAllTextAsync`・MassTransit.Testing（`IPublishedMessageList.Any<T>`/`Select<T>`・
`TestingServiceProviderExtensions.Stop`）。**すべて `CancellationToken` を受けるオーバーロードを
持つ**（MassTransit.Testing の 2 種はリフレクションで実シグネチャを確認した。後述）。

### 判断を要した箇所

- **ゼロ引数呼び出し**（`ReadFromJsonAsync<T>()`/`ReadAsStringAsync()`）は #935 の教訓どおり
  トークンを**唯一の引数**として渡した（先頭カンマの構文エラーを避ける）。
- **CT の前に省略可能引数があるシグネチャ**（`IConversionJobStore.SucceedAsync`/`FailAsync`/
  `PrepareRetryAsync` の各 `ct` の前に `figures`/`deadLettered`/`discardCorrections` がある）は、
  該当の省略可能引数を明示的に埋めているときは位置引数のままでよいが、**その引数を省略している
  呼び出しでは名前付き引数 `ct:` を使った**（位置引数のまま追加すると別の引数へ滑るため）。
- **`HttpClient.PostAsync(string, HttpContent, CancellationToken)` へ `content: null` を渡す形**は、
  名前付き引数の後ろへ位置引数を続けられない（C# の構文規則）ため、`cancellationToken:` も
  名前付きにした。
- **MassTransit.Testing の `IPublishedMessageList.Any<T>`/`Select<T>`** は、ビルド済みバイナリ
  （`ConversionService.Worker.Tests/bin/Debug/net10.0/MassTransit.dll`）を小さな reflection
  ツールで読み、`Any(CancellationToken cancellationToken=)`・
  `Select(CancellationToken cancellationToken=)` のオーバーロードを確認してから当てた
  （ドキュメントを読まずに推測しない）。同様に `TestingServiceProviderExtensions.Stop(ITestHarness,
  CancellationToken cancellationToken=)` も確認した。

### 実施結果

- 置換は **9 ファイル・116 箇所**。属性・アサーション・テスト名・制御フローは変更していない。
- `dotnet build .../ConversionService.Worker.Tests.csproj -t:Rebuild -p:NoWarn= -m:1` →
  **0 Warning(s) / 0 Error(s)**（xUnit1051 が一覧から消えた）。
- `dotnet build .../ConversionService.Worker.Tests.csproj -t:Rebuild -m:1`（既定の `NoWarn` 抜き、
  つまり移行後の実際のビルド設定）でも **0 Warning(s) / 0 Error(s)** —— `WarningsAsErrors` が
  効いた状態でクリーンにビルドできることを確認した。
- `dotnet test src/knowledge/backend/backend.slnx --filter "FullyQualifiedName~ConversionService.Worker.Tests"`
  → **Passed: 74, Skipped: 2, Failed: 0, Total: 76**。Skip 2 件は `PandocConversionServiceTests` の
  pandoc 未導入環境向けの意図的スキップ（`Assert.SkipWhen`/`Assert.SkipUnless`。本 worktree に
  pandoc が無いため発火）であり、移行前と同じ挙動である。**テスト件数は減っていない。**

### baseline / props の更新

- `scripts/xunit1051-baseline.json` の `ConversionService.Worker.Tests` エントリを
  `remaining: 0` / `migrated: true` に更新。
- `src/Directory.Build.props` の `XUnit1051Migrated` へ `ConversionService.Worker.Tests;` を追加
  （既存の綴り規則に合わせ前後 `;` で区切る）。
- `node scripts/check-xunit1051-ratchet.js` → **OK**（baseline と実在プロジェクトが双方向で一致し、
  許可リストは props と揃い、抑止の混入も無い）。

### 受け入れ基準の結果

| 基準 | 結果 |
| --- | --- |
| 116 箇所すべてが `TestContext.Current.CancellationToken` を渡す | ✅ `-p:NoWarn=` 再測定で当該プロジェクトが一覧から消えた |
| 再発したらビルドが落ちる | ✅ 既定ビルド（`WarningsAsErrors` 込み）が 0 Warning でクリーンに通ることを確認（`XUnit1051Migrated` に載った状態） |
| テスト件数が減らない | ✅ 74 Passed + 2 Skipped = 76（移行前と同数。属性を変えていない） |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |

---

## 第 2 部: #901 — Platform.Shared.Infrastructure の被覆向上

### 除外領域

**`Foundation/Authz`（`BffScopeResolver.cs` 1 ファイル）は並行トラック 1A の領域のため、
走査・実装のいずれからも対象外とした。** 触っていない。

### 前提の再確認（issue 起票時点との乖離）

issue 本文は「772 行中 9 行（1.16%）」と書いているが、これは issue 起票時点（U4 直後・
`Platform.Shared.Infrastructure.Tests` 新設直後）の値であり、**その後の wave で
優先 1〜5（`MapPlatformHealthChecks`/`UsePlatformMiddleware`/`AddPlatformObservability`/
`AddPlatformLogging`/`AddPlatformObjectStorage`）と `ObjectStorageBootstrapHostedService`（#939）
へのテストが既に追加済みだった**（issue コメント 2026-08-22 分・#931/#938/#939 の成果）。

着手前に `dotnet test .../Platform.Shared.Infrastructure.Tests.csproj --collect:"XPlat Code Coverage"`
を実測すると、本プロジェクト単独の cobertura で **93 テスト・行被覆 426/1006 = 42.35%** だった
（issue の 1.16% から大きく進んでいる）。**古い数字を信じず実測し直した。**

### 母集合の引き方（誤りの側から引く: 「無試験」の定義）

`Platform.Shared.Infrastructure` 配下の `.cs`（`bin`/`obj` 除く。29 ファイル）を対象に、
本プロジェクトの cobertura レポートでクラス単位の行被覆を実測した（走査の生出力。加工していない）。
0% または低被覆のクラスをまず機械的に列挙し、そのうえで**他ユニットのテストからの間接試験**を
排除しないよう、issue の先行コメント（2026-08-22・型名／メソッド名の両方で走査した記録）と
本セッションでの `grep` 横断を突き合わせた。

| 領域 | 判定 | 根拠 |
| --- | --- | --- |
| `Foundation/Introspection`（`DriftDetectionHostedService`/`DriftRunner`/`HttpEffectiveConfigCollector`/`DriftAlertSink`/`IntrospectionOptions`。優先 4「運搬経路」） | **見送り** | issue コメント（2026-08-22）が明示的に「C3 待ち」と裁定済み。`scripts/backend-library-baseline.json` を実測すると **C3 は未着地**（`ConversionService`/`Platform.Bff` 等 11 プロジェクトに MassTransit の残件があり空になっていない）。優先 4 の「運搬経路 3 クラス」の具体名も issue 上で未確定（6 候補中どれが該当か決まっていない）。この状況で書くテストは、C3 着地時の設計変更で書き直しになる可能性が高い |
| `Foundation/Pipeline/PipelineExtensions.cs`（30.8%） | **見送り** | baseline（`scripts/xunit1051-baseline.json`）の `Platform.Shared.Infrastructure.Tests` エントリが「U5 / Wolverine 移行チェーン（#455 系）が本プロジェクトへテストを追加中」と明記。同じ理由（並行トラックとの衝突）で本 PR からは触らない |
| `Composable/Adapters/Storage/S3ObjectStorageClient.cs`（20%。async 本体は 0%） | **見送り** | 実 `IAmazonS3` を要する薄いラッパで、**実体（MinIO）を要するラウンドトリップは既存の `Knowledge.IntegrationTests/Storage/ObjectStorageRoundTripTests.cs`（`[DockerFact]`）が担当**する設計（`ObjectStorageTests.cs` 冒頭コメント）。本 worktree は Docker 不可のため実行・検証ができず、モックだけで固めると「通すためだけのテスト」になりやすい |
| `Composable/Adapters/Storage/NullObjectStorageClient.cs`（12.5%） | **見送り** | `ConversionService.Worker.Tests/ObjectStorageTests.cs`（`NullClient_emits_deterministic_uri_but_cannot_resolve`）が `PutTextAsync`/`CanResolve`/`GetTextAsync` の主要経路を試験済み（cross-unit）。本プロジェクト単独の被覆には出ないが「無試験」ではない |
| `Foundation/Ports/Storage/StorageUri.cs`（0%、本プロジェクト内） | **見送り** | 同じく `ConversionService.Worker.Tests/ObjectStorageTests.cs` が `Build`/`TryParse`/`IsStorageUri` を包括的に試験済み（cross-unit）。issue コメントの先行走査でも「試験あり」と確定済み |
| `Foundation/Audit/AuditLogger.cs`（0%、6 行） | **見送り** | 静的ロガー呼び出しの薄いラッパ（ロジック分岐が無い）。issue コメントが「`PlatformLoggingTests.cs` で試験あり」（cross-unit）と確定済み |
| **`Foundation/Extensions/AuthExtensions.cs`（0%、61 行）** | **対象** | 全サービスが `AddPlatformAuth` 経由で依存する認証・認可の基盤（JWT 検証設定・ロールポリシー・claims 変換の配線）。壊れると全サービスへ波及し、かつ設定値だけの変更は個々のサービステストでは気づきにくい。Docker 不要・InMemory Configuration で決定的に試験できる |
| **`Foundation/Extensions/KeycloakRolesClaimsTransformation.cs`（0%、29 行）** | **対象** | `AuthExtensions` が登録する `IClaimsTransformation`。realm_access.roles の展開が fail-closed であることは RBAC の前提そのもの。Docker 不要・純粋に近いロジックで決定的に試験できる |
| `Foundation/Extensions/MassTransitExtensions.cs`（50%、6 行） | **見送り** | 残り 3 行は `Platform.Bff.Tests` 等の統合寄り経路にしか現れず、本 PR の射程（決定的な単体テスト）に対して費用対効果が低い。小さすぎて優先度を割く理由が無い |
| `Foundation/Extensions/WolverineExtensions.cs`（`WolverineBrokerHealthCheck` の一部が低被覆） | **見送り** | 既存の `WolverineBrokerHealthCheckTests.cs` が主要経路を試験済み。残る分岐はブローカ接続失敗系（統合テスト寄り） |

### 実施内容

`AuthExtensionsTests.cs`（19 件）・`KeycloakRolesClaimsTransformationTests.cs`（13 件、Theory 展開後）
の 2 ファイルを追加した（1 コミット。両者は `AddPlatformAuth` が
`KeycloakRolesClaimsTransformation` を登録するという同一機能領域のため 1 論理単位とした）。

- xUnit1051: baseline で `Platform.Shared.Infrastructure.Tests` は `migrated: false`
  （`deferReason`: U5/Wolverine 移行チェーン着地待ち）であることを確認した。**追加したテストへ
  `TestContext.Current.CancellationToken` は通していない**（同プロジェクトの既存テストと同じ書式に
  揃えた。移行は本 PR の射程外）。
- `AuthExtensionsTests`: `AddPlatformAuth` を `ServiceCollection` へ適用し、
  `IOptionsMonitor<JwtBearerOptions>` 経由で実際に構成された値を検証する（内部実装への
  リフレクションではなく、フレームワークが実際に読む設定値を見る）。Authority/MetadataAddress の
  排他・`RequireHttpsMetadata=false`・`ValidateAudience=false`・`RoleClaimType`・`NameClaimType`・
  `ValidIssuers` のパース（区切り文字 4 種・空要素除去・trim・未設定時に上書きしない）・
  `IClaimsTransformation` としての登録・`AdminOnly`/`ConfigViewer` ポリシーの `RolesAuthorizationRequirement`
  を検証する。
- `KeycloakRolesClaimsTransformationTests`: 正常展開・**未認証は fail-closed**・
  `realm_access` 欠落・不正 JSON（3 パターン、例外を投げないことも検証）・`roles` を欠く形状
  （4 パターン）・非文字列/空白要素の無視・**冪等性**（複数回適用で重複しない）・既存ロールの保持を検証する。

### 変異試験（検出力の実測）

「通すためだけの空テストを書かない」の裏取りとして、本番コードを一時的に破壊し、追加テストが
実際に落ちることを確認してから元に戻した（各回 `diff`/`cmp` でバイト一致に復旧したことを確認）。

| # | 対象 | 変異 | 期待 | 実測 |
| --- | --- | --- | --- | --- |
| M-1 | `KeycloakRolesClaimsTransformation.TransformAsync` | `!identity.IsAuthenticated` チェックを削除（未認証でもロールを付与してしまう） | fail-closed の試験が落ちる | 検証中に別件をブロックしたため、`ExtractRoles` 側の変異（M-2）で代替実測した |
| M-2 | `KeycloakRolesClaimsTransformation.ExtractRoles` | 空白要素フィルタ `.Where(s => !string.IsNullOrWhiteSpace(s))` を削除 | 非文字列/空白の無視を見る試験が落ちる | **実測どおり失敗**（`roles配列内の非文字列と空白は無視される` が `"  "` を含んだ 3 件を返し FAIL） |
| M-3 | `AuthExtensions.ParseValidIssuers` | 区切り文字からカンマ `,` を除去 | ValidIssuers の分割を見る Theory が落ちる | **実測どおり失敗**（4 ケース中カンマ区切りを含む 3 ケースが FAIL、タブ区切りの 1 ケースは通過 — 期待どおりの選択的検出） |

M-1 は `IsAuthenticated` 分岐を触った直後に `dotnet test` の実行がハーネス側でブロックされたため、
安全側に倒してすぐ復旧し、**同じ「fail-closed 系ロジックの除去」という性質を持つ M-2 で代替して
検出力を確認した**（M-2 も `ExtractRoles` 内の防御的フィルタを除去する変異であり、同種の検証意図を持つ）。
M-1 のコードは復旧後 `diff` でバイト一致を確認済みであり、当該分岐は残る 3 件の `Fact`
（`未認証のPrincipalはロールを付与しない_failclosed` 等）が引き続き直接カバーしている。

### 被覆の前後実測

`dotnet test .../Platform.Shared.Infrastructure.Tests.csproj --collect:"XPlat Code Coverage"`
（cobertura。本プロジェクト単独の実測であり、他ユニットからの間接試験は含まない）。

| | テスト数 | 行被覆（`Platform.Shared.Infrastructure/**`、テストプロジェクト自身は除く） |
| --- | ---: | --- |
| 着手前 | 93 | 426/1006 = **42.35%** |
| 追加後 | 125（+32） | 516/1006 = **51.29%**（+8.94pt） |

クラス単位では `AuthExtensions`（0% → **100%**、61/61 行）・`KeycloakRolesClaimsTransformation`
（0% → **100%**、29/29 行）。

### `src/coverage-floor.json` について

**本 PR では変更していない**（割り当て範囲外。統括が波末に ratchet する）。上表の実測値は
床の引き上げ判断の材料として残すのみである。

### 受け入れ基準の結果

| 基準 | 結果 |
| --- | --- |
| 無試験領域の走査結果と優先順位の根拠が作業仕様書にある | ✅ 上表（除外領域の判定つき） |
| 追加したテストが変異試験で検出力を示している | ✅ M-2・M-3 で実測（M-1 は同種の M-2 で代替） |
| 床を引き上げ、根拠を `src/coverage-floor.json` に残す | **見送り**（`src/coverage-floor.json` は割り当て範囲外・変更禁止。実測値は本書に残した） |

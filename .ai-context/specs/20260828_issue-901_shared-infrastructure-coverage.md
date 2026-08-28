---
title: 作業仕様書 — Platform.Shared.Infrastructure の被覆向上（#901・認可分岐と Introspection 運搬経路）
type: spec
status: done
related_ids:
  - NFR
  - FR-05
  - FR-15
  - FR-19
  - ADR-0018
  - ADR-0036
  - IADR-0029
  - IADR-0239
  - IADR-0253
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0018_config-introspection.md (FR-15 宣言と実効構成のドリフト検出)
  - planning:projects/microservices-platform/07_adr/ADR-0036_abac-policy-model.md (FR-05/FR-19 ABAC スコープ解決)
related_specs:
  - "20260828_issue-882-901_test-hardening.md"
issue: "#901"
---

# 作業仕様書 — Platform.Shared.Infrastructure の被覆向上（#901）

## 目的と射程

`Platform.Shared.Infrastructure` は platform / knowledge の**全サービスが依存する共有ライブラリ**で
ありながら、専用テストが薄い。ここが割れると 1 サービスではなく全サービスへ波及する。
**被覆率の数字ではなく「割れたときの影響が大きい振る舞い」を固定する**ことを目的とする。

🔴 **本作業は「床を戻すため」に行わない**（issue #901 注記）。床の前進は結果であって目的ではない。

### 起点 ID の置き方

本作業は共有基盤の試験整備であり、特定の `FR`/`UC` の実装ではない。先行 PR（#931 / #938 / #939）
および `20260828_issue-882-901_test-hardening.md` と同じ判断で、コミット件名のスコープは
無採番 `NFR` を用いる（`.claude/rules/traceability.repo.md`「メタ作業は代表例で、製品の作業にも
当たる番号が無いことはある」）。

## 前提の確認（実測。記憶で書かない）

| 項目 | 実測 |
| --- | --- |
| worktree HEAD | `b1da69e4dd08f5122a7ec5b1f3a3e0c7b5e4a231`（= base） |
| `git rev-parse --is-shallow-repository` | **`true`** → **`git log` / `git blame` を出典に引かない**（planning#410） |
| .NET SDK | 10.0.400 |
| Docker | 利用不可（Testcontainers 系の統合テストは実走できない） |
| `src/ai-stock-trading`（submodule） | **未 populate**。このため `Platform.Bff` / `Platform.Bff.Tests` は**基準コミットの時点でビルドできない**（後述） |
| xUnit1051 | `Platform.Shared.Infrastructure.Tests` は `XUnit1051Migrated` **収載済み** → 新規テストは `TestContext.Current.CancellationToken` 必須 |

### 🔴 基準コミットで `backend.slnx` が既に赤い（本作業と無関係）

`dotnet build src/platform/backend/backend.slnx` は base `b1da69e`（作業ツリー無変更）の時点で
`Platform.Bff` が `error CS0246: AiStockTrading` で失敗する。AST submodule が未 populate なため
`AiStockTrading.Bff.Endpoints.csproj` が実在せず、合成点 `BffEndpointComposition.cs` が解決できない。
**環境起因の既存事象であり本作業が壊したものではない。** 検証はこの事実を明記した上で、
`Platform.Bff` を除いた範囲で行う。

## 母集合の引き方（走査。推定ではない）

### 走査 1: 本プロジェクト単独 cobertura でクラス別行被覆を実測

`dotnet test Platform.Shared.Infrastructure.Tests.csproj --collect:"XPlat Code Coverage"` の
Cobertura を `<class filename>` 単位で集計（`obj/`・`Migrations/` 除外）。**着手前 125 テスト /
line 516/1016 = 50.79% / branch 147/328 = 44.82%。**

### 走査 2: 被覆 0 のものが本当に無試験かを、リポジトリ全体のテストファイル参照で交差確認

🔴 **issue の先行コメント（2026-08-22）が記録した罠を踏まないよう、型名とメソッド名の両方で引いた。**
「本プロジェクトの被覆が 0」は「無試験」を意味しない（他ユニットのテストが触っていることがある）。

| 対象（被覆 0 / 低） | 他ユニットからの試験参照 | 判定 |
| --- | --- | --- |
| `Foundation/Authz/BffScopeResolver.cs`（47 行 / 30 分岐） | `Platform.Bff.Tests/BffScopeResolverTests.cs` あり | **部分的に対象**（下記） |
| `Introspection/HttpEffectiveConfigCollector.cs`（38 行 / 6 分岐） | **0 件** | **対象** |
| `Introspection/DriftDetectionHostedService.cs`（34 行 / 4 分岐） | **0 件** | **対象** |
| `Introspection/DriftRunner.cs`（15 行 / 2 分岐） | **0 件** | **対象** |
| `Introspection/DriftAlertSink.cs`（10 行 / 2 分岐） | Bff の `ConfigBffEndpointTests` はダブルを置くのみ（実装は通らない） | **対象** |
| `Introspection/IntrospectionExtensions.cs`（56 行 / 34 分岐） | Docker 必須の `Knowledge.IntegrationTests` のみ（既定では skip）＋ reflection 1 件 | **対象** |
| `Ports/Storage/StorageUri.cs` | `ConversionService.Worker.Tests/ObjectStorageTests.cs` | 見送り（cross-unit で試験あり） |
| `Audit/AuditLogger.cs` | `Platform.Bff.Tests/PlatformLoggingTests.cs` | 見送り（cross-unit で試験あり） |
| `Storage/NullObjectStorageClient.cs` | 本プロジェクト 3 件 ＋ knowledge 側 | 見送り |
| `Storage/S3ObjectStorageClient.cs` | `Knowledge.IntegrationTests`（`[DockerFact]`） | 見送り（実体 MinIO 必須。Docker 不可） |
| `Pipeline/PipelineExtensions.cs` | knowledge 側 7 ファイル ＋ U5/Wolverine の並行トラック領域 | **見送り**（並行作業と衝突する） |

#### `BffScopeResolver` の扱い —— 「試験あり」は `Matches` までである

先行コメントは本ファイルを「試験あり」と分類したが、**実際に何が実行されているかを読むと違う。**
`Platform.Bff.Tests/BffScopeResolverTests.cs` が実行するのは純ロジックの `Matches`（分岐 OR/AND・
deny-by-default）と `ExtractUserAttributes` であり、**`ResolveAsync` については
`ResolveAsync_ActionParameter_HasNoDefaultValue` という reflection のみの試験しか無い**。
同ファイル冒頭のコメント自身が「`ResolveAsync` の HTTP 経路は Document/Search の BFF
エンドポイントテストが回帰保証する」と書いており、**単体としては未実行**である。

🔴 issue #901 が確定した知見「**リフレクションのみの試験は行被覆に 1 行も寄与しない**」がそのまま当たる。
さらに本環境では `Platform.Bff.Tests` は AST submodule 未 populate のため**ビルドすらできない** ——
`ResolveAsync` の deny-by-default 分岐は、**この worktree では 1 本も守られていない**。

**したがって `ResolveAsync` の HTTP 経路と例外縮退を本作業の第 1 優先とする。**
既存の `Matches` 系テストは重複させない（`Platform.Bff.Tests` に在るものを写さない）。

#### Introspection を「C3 待ち」から外す根拠（走査で確認した）

先行コメントは優先 4（Introspection の運搬経路）を「Wolverine チェーンの C3 待ち」としていた。
**C3 は未着地である**（`scripts/backend-library-baseline.json` に MassTransit 残件が 9 プロジェクト）。
しかし対象 5 ファイルを走査すると、**`MassTransit` / `Wolverine` / `IMessageBus` の出現は 0 件**である
（`HttpEffectiveConfigCollector` / `DriftRunner` / `DriftAlertSink` /
`DriftDetectionHostedService` / `IntrospectionOptions`）。これらが依存するのは
`IHttpClientFactory` / `ILogger` / `IOptions` / `BackgroundService` のみで、
**メッセージング移行の設計変更を受けない**。「C3 着地時に書き直しになる」という見送り理由は
この 5 ファイルには当たらない。

唯一 `IntrospectionExtensions.cs` だけが `using MassTransit;` を持つが、それは
`AddStep<TConsumer>`（MassTransit 版）の制約であり、**本作業が対象にする
`AddPlatformIntrospection` / `AddWolverineStep` / `AddPort` / `AddConnector` は当たらない**。
`AddStep`（MassTransit 版）には触れない。

## 対象範囲

- **対象**
  - `Foundation/Authz/BffScopeResolver.ResolveAsync`（認可の deny-by-default 分岐）
  - `Foundation/Introspection/HttpEffectiveConfigCollector`（到達不能への例外変換）
  - `Foundation/Introspection/DriftRunner`（不一致時のみ警告）
  - `Foundation/Introspection/LoggingDriftAlertSink`（警告の形）
  - `Foundation/Introspection/DriftDetectionHostedService`（無効化・例外でループを止めない・停止）
  - `Foundation/Introspection/IntrospectionExtensions` / `IntrospectionBuilder`（自己申告の組み立てと**起動時 fail-fast**）
- **対象外**
  - 本番コードの変更（テストのために本番を変えない。**1 行も変えない**）
  - `Pipeline/*`（U5 / Wolverine の並行トラック）、`AddStep<TConsumer>`（MassTransit 版・C3 の射程）
  - `S3ObjectStorageClient` の実体往復（Docker 不可）
  - `src/coverage-floor.json` の判定実走（Docker 不可のため CI と同じ母集合を作れない。後述）

## 設計（何を固定するか）

本番コードは変更しない。テストは `Platform.Shared.Infrastructure.Tests` に追加する。
外部依存は**ハンドラ／ダブルで置き換え、実プロバイダで観測する**（#901 の知見:
リフレクションだけの検査は被覆にも検出力にも寄与しない）。

### A. `BffScopeResolver.ResolveAsync`（優先 1・認可）

`IHttpClientFactory` をスタブ化し、`AuthorizationService` の応答を実 HTTP メッセージで与える。

| 固定する振る舞い | 意図 |
| --- | --- |
| 2xx ＋ `Granted:true` → スコープを返し **Branches を運ぶ** | IADR-0253 段 3。落とすと検索経路だけ混成を許す |
| 2xx ＋ `Granted:false` → `null` | deny-by-default |
| 非 2xx（403 / 500） → `null` | 本文を読まない |
| 2xx ＋ 本文が JSON `null` → `null` | `is not { Granted: true }` の縮退 |
| `HttpRequestException`（認可サービス停止） → `null`（**投げない**） | 可用性障害で認可が緩まない |
| `TaskCanceledException`（タイムアウト・ct 未キャンセル） → `null` | 同上 |
| **ct キャンセル済みなら例外を伝播**（`null` にしない） | 呼び出し元のキャンセルを「拒否」と誤認しない |
| 要求本文の `UserId` は**サーバ側の `HttpContext.User` 由来**・`Action` は引数どおり | クライアント指定を信頼しない（権限昇格の防止） |
| 未認証は `"anonymous"` | 同上 |

### B. `HttpEffectiveConfigCollector`（優先 2・例外/エラー変換）

| 固定する振る舞い | 意図 |
| --- | --- |
| 全件応答 → `Services` / `ReachableServices` に載り `Unreachable` は空 | 正常系の対照条件 |
| 1 件が例外 → **その 1 件だけ `Unreachable`**・他は収集継続 | 部分障害の隔離 |
| 本文が空（JSON `null`） → `Unreachable` へ | 「応答したが空」を到達扱いしない |
| URL は `baseUrl.TrimEnd('/') + Options.Path` | 収集先の組み立て |
| `TimeoutSeconds<=0` でも 1 秒以上（`Math.Max(1,…)`） | 0 秒は `HttpClient.Timeout` が投げる |
| `OperationCanceledException` は**握らず伝播** | 停止要求を「到達不能」に化けさせない |
| `Services` 空 → HTTP を 1 本も呼ばない | 無設定時に外へ出ない |

### C. `DriftRunner` / `LoggingDriftAlertSink` / `DriftDetectionHostedService`（優先 2）

| 固定する振る舞い | 意図 |
| --- | --- |
| `HasDrift:true` → sink が**その report で**呼ばれる | FR-15 の警告 |
| `HasDrift:false` → sink は**呼ばれない** | 対照条件（常に警告する実装を落とす） |
| sink は finding 1 件につき Warning 1 件・`ConfigDrift=true` を含む | 運用アラートの抽出キー |
| finding 0 件 → ログ 0 件 | 同上の対照 |
| `Enabled:false` → runner を**一度も呼ばない**で終了 | 無効化が効く |
| `Enabled:true` → **待たずに初回実行**（do-while の初回） | 起動直後検出（#146） |
| 初回が例外 → **ExecuteTask は落ちない**（ループ継続） | 1 回の失敗でループを殺さない |
| 停止要求 → 例外なく完了 | `SafeWaitAsync` の縮退 |

`PeriodicTimer` の間隔は下限 10 秒のため、**2 周目を待つ試験は書かない**（初回実行と例外耐性、
および停止で判定する）。

### D. `IntrospectionExtensions` / `IntrospectionBuilder`（優先 3・DI 登録ヘルパ）

| 固定する振る舞い | 意図 |
| --- | --- |
| `AddPlatformIntrospection` が `ServiceIntrospectionDto` を singleton 登録し、service 名・段・ポート・コネクタを運ぶ | 自己申告の組み立て |
| 宣言（`PipelineOptions`）から `Enabled` / `Outputs` を解決 | 登録規則との整合 |
| 宣言が無い段は**既定で有効** | `AddPlatformPipelineStep` 規則 1 |
| 🔴 `AddWolverineStep<TStep>` で `IPipelineStep<TIn>` が導出できないと **`InvalidOperationException` で起動を止める** | IADR-0239 決定 2。空文字申告に縮退すると実行時まで気づけない |
| 導出できる段は入力イベント型名を申告する | 上の対照条件 |

## 受け入れ基準

- [ ] 無試験領域の走査結果と、選んだ優先順位の根拠が本書にある（issue #901 の基準 1）
- [ ] 追加したテストが**変異試験で検出力を示している**（同 基準 2。生存した変異も正直に記録する）
- [ ] 追加後に `dotnet test` が緑で、**テスト件数が増えている**（skip で緑にしない）
- [ ] `dotnet format --verify-no-changes` が通る
- [ ] 本番コードを 1 行も変更していない
- [ ] 床（`src/coverage-floor.json`）は**導出規則に従って**判断し、適用した規則を報告に書く

## テスト方針

- xUnit v3 / `[Fact]` `[Theory]` / 表明は AwesomeAssertions（ADR-0030）。
- **xUnit1051 収載済みプロジェクトのため、`CancellationToken` を取る呼び出しには
  `TestContext.Current.CancellationToken` を渡す**（`WarningsAsErrors` で再発はビルドが落ちる）。
- 新規パッケージは追加しない（`src/Directory.Packages.props` は触らない見込み）。
- ログの検査は記録用 `ILogger` ダブルで行う（新規パッケージ不要）。
- **各テストに対照条件を置く**（`DriftServiceCoverageTests` の作法。片側だけだと
  「常に true / 常に false」の壊れた実装でも通る）。

### 変異試験（検出力の実測）

追加後、本番コードへ一時的に小さな変異を入れ、**期待したテストが落ちること**を実測してから
元へ戻す（`git diff` で復旧を確認）。最低限、次を当てる。

1. `ResolveAsync` の `is not { Granted: true }` を `is null` へ（deny-by-default の緩み）
2. `ResolveAsync` の `catch` の `when` 条件から `!ct.IsCancellationRequested` を削除
3. `HttpEffectiveConfigCollector` の `Math.Max(1, …)` を外す
4. `CollectOneAsync` の `when (ex is not OperationCanceledException)` を削除
5. `DriftRunner` の `if (report.HasDrift)` を無条件化
6. `DriftDetectionHostedService` の `if (!_options.Enabled) return;` を削除
7. `AddWolverineStep` の `throw` を `?? typeof(object)` 等へ置換（fail-fast の喪失）

## 床（ratchet）の扱い

床の値の単一情報源は `src/coverage-floor.json`（現行 `line: 88` / `branch: 68`）。
**導出規則は IADR-0118 決定 2「実測からの整数切り下げ」**であり、IADR-0195 決定 3 の例外は
「切り下げが機能する床を与えないとき（耐性 0 本）に限り 1 つ下を採る」ものである。
また `$comment` の #900 / #929 追記が **「引き上げは pt ではなく被覆行・被覆分岐の本数で余裕を見る」**
と定めている。

🔴 **床が判定する母集合は「src 配下の全 Cobertura をレポート跨ぎで重複排除した合算」であり、
本プロジェクト単独の値ではない。** 本環境は Docker 不可で統合テストが走らず、
`Platform.Bff` はビルドすらできないため、**CI と同じ母集合を作れない**。
`$comment` の #899 追記が同じ状況を「本環境では床判定を実走できない」と記録している。

したがって**測れないものを推測で書き換えない**。本作業では実測できた増分（本プロジェクト単独の
前後値と、追加した被覆行・被覆分岐の本数）を本書と報告に残し、**床の値を動かすかは
その本数が「1 pt 上げるのに要る本数」に届くかで判断する**（#901 の先行 PR が
「増分が届かないため引き上げなかった」と記録したのと同じ判断規則）。

## 計画書との差異

- 差異: なし（本作業は試験の追加のみで、計画書の要求・制約を変更しない）

## 未決事項

- なし（着手条件は上記の走査で確定した）

---

# 実施結果

## 追加したもの（本番コードは 1 行も変更していない）

| ファイル | 件数 | 対象 |
| --- | ---: | --- |
| `Foundation/Authz/BffScopeResolveTests.cs` | 12 | `ResolveAsync` の HTTP 経路と deny-by-default |
| `Foundation/Introspection/HttpEffectiveConfigCollectorTests.cs` | 14 | 到達不能への例外変換・タイムアウト下限 |
| `Foundation/Introspection/DriftDetectionChainTests.cs` | 9 | runner / alert sink / hosted service |
| `Foundation/Introspection/IntrospectionRegistrationTests.cs` | 10 | 登録ヘルパと起動時 fail-fast |
| `Testing/RecordingLogger.cs` | — | ログ検査用ダブル（新規パッケージ不要） |
| **合計** | **45** | |

`git diff --stat`（追跡下）は**空**である ＝ 本番コードは 1 バイトも変わっていない。

## 被覆の前後（`Platform.Shared.Infrastructure.Tests` 単独 cobertura・Debug）

| | テスト | line | branch |
| --- | ---: | --- | --- |
| 着手前 | 125 | 516/1016 = **50.79%** | 147/328 = **44.82%** |
| 追加後 | 170（+45） | 684/1016 = **67.32%**（+168 行 / +16.53pt） | 196/328 = **59.76%**（+49 本 / +14.94pt） |

クラス別（0% → 到達後）:

| クラス | line 前 → 後 | branch 前 → 後 |
| --- | --- | --- |
| `HttpEffectiveConfigCollector` | 0/38 → **38/38（100%）** | 0/6 → **6/6** |
| `DriftDetectionHostedService` | 0/34 → **34/34（100%）** | 0/4 → **4/4** |
| `DriftRunner` | 0/15 → **15/15（100%）** | 0/2 → **2/2** |
| `DriftAlertSink` | 0/10 → **10/10（100%）** | 0/2 → **2/2** |
| `IntrospectionExtensions` | 0/56 → **36/56（64.3%）** | 0/34 → **18/34** |
| `BffScopeResolver` | 0/47 → **30/47（63.8%）** | 0/30 → **17/30** |
| `IntrospectionOptions` | 0/13 → **5/13（38.5%）** | — |

`BffScopeResolver` の残り 17 行は `Matches` / `MatchesAll` / `ExtractUserAttributes` であり、
**`Platform.Bff.Tests` が既に直接検証している**ため意図的に重複させていない。
`IntrospectionExtensions` の残りは `AddStep<TConsumer>`（MassTransit 版・C3 の射程）と
`MapPlatformIntrospection` のエンドポイント本体である。

## 変異試験（16 種。**生存 1 件を含めて記載する**）

各回とも変異適用 → 対象テスト実行 → `git checkout` で復旧 → **md5 一致を確認**した。

| # | 対象 | 変異 | 結果 |
| --- | --- | --- | --- |
| M-1 | `BffScopeResolver` | `is not { Granted: true }` → `is null` | **KILLED**（2 件失敗） |
| M-2 | `BffScopeResolver` | catch の `&& !ct.IsCancellationRequested` を削除 | **KILLED**（1 件） |
| M-3 | `BffScopeResolver` | 非 2xx でも本文を読む | **KILLED**（3 件） |
| M-4 | `HttpEffectiveConfigCollector` | `Math.Max(1, …)` を外す | **KILLED**（2 件） |
| M-5 | `HttpEffectiveConfigCollector` | `when (ex is not OperationCanceledException)` を削除 | **KILLED**（1 件） |
| M-6 | `HttpEffectiveConfigCollector` | `baseUrl.TrimEnd('/')` を外す | **KILLED**（1 件） |
| M-7 | `DriftRunner` | `if (report.HasDrift)` → `if (true)` | **KILLED**（1 件） |
| M-8 | `LoggingDriftAlertSink` | `ConfigDrift` の値 `true` → `false` | **KILLED**（1 件） |
| M-9 | `DriftDetectionHostedService` | `if (!_options.Enabled)` → `if (false)` | **KILLED**（1 件） |
| M-10 | `DriftDetectionHostedService` | `SafeRunOnceAsync` の catch を無効化 | **KILLED**（1 件） |
| M-11 | `DriftDetectionHostedService` | `SafeWaitAsync` の catch を無効化 | 🔴 **初回 SURVIVED** → 試験を是正して **KILLED** |
| M-12 | `IntrospectionBuilder` | `?? throw` → `?? typeof(object)`（fail-fast 喪失） | **KILLED**（1 件） |
| M-13 | `IntrospectionBuilder` | `decl?.Enabled ?? true` → `?? false` | **KILLED**（1 件） |
| M-14 | `BffScopeResolver` | `userId` をサーバ側 ID から取らない | **KILLED**（1 件） |
| M-15 | `BffAccessScope` | `ToContractScope()` で `Branches` を落とす | **KILLED**（1 件） |
| M-16 | `HttpEffectiveConfigCollector` | `if (report is null)` → `if (false)` | **KILLED**（3 件） |

### 🔴 M-11 が暴いた「通すだけのテスト」——是正した内容

初版の「停止要求では例外を外へ出さずに終了する」は `StopAsync` が投げないことを見ていたが、
**`BackgroundService.StopAsync` は `ExecuteTask` の完了を待つだけで、その例外を観測しない。**
そのためループが例外で落ちても `StopAsync` は正常に返り、**変異が生き延びた**。
`ExecuteTask` を明示的に `await`（`WaitAsync` で時間を区切る）する形へ是正し、再試験で KILLED を確認した。
**この 1 件が「変異試験をやる意味」そのものである** —— 被覆率は初版でも 100% だった。

### もう 1 件の是正（ハングするテスト）

M-9 の初回試行で、無効化の試験が**失敗ではなくハングした**（素の `await ExecuteTask` が
次のティックまで 300 秒待つため）。ハングは CI ではタイムアウトとして現れ、どの表明が
壊れたか判らない。`WaitAsync(5 秒)` で区切る形へ是正し、再試験で 5 秒で KILLED になった。

## 床（`src/coverage-floor.json`）——**動かしていない。その根拠**

適用した導出規則:

1. **IADR-0118 決定 2**: 床は**実測からの整数切り下げ**（切り上げは初回から fail するため行わない）。
2. **IADR-0195 決定 3**: 切り下げが機能する床を与えないとき（耐性 0 本）に限り 1 つ下を採る。
3. **IADR-0236 決定 6b / `$comment` の #899・#929 追記**: 引き上げの余裕は
   **pt ではなく被覆行・被覆分岐の本数**で見る。

規則 1 が要求する「実測」は**床が判定する母集合**（`src/` 配下の全 Cobertura を
レポート跨ぎで重複排除した合算）に対するものである。**本環境ではそれを作れない**ことを
実測で確認した。

| | 本環境の局所集計 | CI 基準（`$comment` #929: run 32549865103） |
| --- | --- | --- |
| レポート件数 | **18** | **17** |
| line | 88.71%（10972/**12368**） | 89.55%（6986/**7801**） |
| branch | 72.9%（2432/**3336**） | 69.83%（1558/**2231**） |
| 統合テスト | **40 件 skip**（Docker 無し） | 全件実行 |
| `Platform.Bff.Tests` | **ビルド不可**（AST submodule 未 populate） | 含む |
| 構成 | Debug | Release |

**キー総数が 12368 対 7801 と大きく違う** ——同じ尺度の値ではなく、切り下げの材料にできない。
`$comment` の #899 追記が同じ状況を「本環境では床判定を実走できない」と記録しているのと同型である。

したがって **`勝手な値を入れない`** を優先し、床は据え置いた。これは本 issue の先行 2 PR
（#931 / #938）が「増分が本数で 1 pt に届かないため引き上げていない」と記録したのと同じ判断規則である。
なお **局所集計での床判定は `exit 0`（`OK: 床を下回っていません`）** であり、
本作業が床を割る変更でないことは確認済みである。

### 引き上げを検討する人へ（CI 実測が取れたときの材料）

- 本作業の増分は `Platform.Shared.Infrastructure` に対し **被覆行 +168 / 被覆分岐 +49**。
  重複排除後の母集合ではこの共有ライブラリの行は**1 部しか載らない**ため、
  CI 集計での増分の**上限**が +168 行 / +49 本である（他レポートが既に被覆していた分だけ減る）。
- `$comment` #929 の記録では、床を 89 / 69 へ上げた場合の耐性は **line 43 行 / branch 19 本**。
  本作業の増分はその見積りを**緩める向き**にしか働かない。
- ただし **`$comment` は「引き上げの可否は未裁定」と明記している。** 値を動かすなら
  CI（Release・全レポート）の実測を取り直してから、上の規則 1〜3 を当てること。

## 受け入れ基準の結果

| 基準 | 結果 |
| --- | --- |
| 無試験領域の走査結果と優先順位の根拠が本書にある | ✅ 走査 1（被覆実測）＋走査 2（テスト参照の交差確認）を上表に記載 |
| 変異試験で検出力を示している | ✅ 16 種。15 種が初回 KILLED、1 種（M-11）は生存 → 試験を是正して KILLED |
| テスト件数が増えている（skip で緑にしない） | ✅ 125 → 170（**Skipped: 0**） |
| `dotnet format --verify-no-changes` | ✅ exit 0（slnx 単位・プロジェクト単位の双方） |
| 本番コードを変更していない | ✅ `git diff --stat`（追跡下）が空 |
| 床は導出規則に従って判断した | ✅ 据え置き。規則と根拠を上に記載 |

## 申し送り

- 🔴 **`Platform.Bff` は基準コミットの時点でビルドできない**（AST submodule 未 populate）。
  本作業と無関係な環境事象だが、**`Platform.Bff.Tests` が守っている `BffScopeResolver.Matches`
  の試験がこの worktree では 1 件も走らない**ことを意味する。本作業が `ResolveAsync` を
  共有ライブラリ側へ移して固定したことで、この経路の一部はユニットに閉じた形で守られるようになった。
- `Foundation/Pipeline/PipelineExtensions.cs`（30.8%）と `AddStep<TConsumer>`（MassTransit 版）は
  **U5 / Wolverine 移行チェーン（C3）の領域**として手を付けていない。C3 着地後に別途。
- `S3ObjectStorageClient`（5.1%）は実体（MinIO）を要するため Docker のある環境で扱うのが妥当。
- `IntrospectionOptions` の残りは純粋なプロパティであり、単体で固定する価値は低い。

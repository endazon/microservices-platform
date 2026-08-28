---
title: 作業仕様書 — Platform.Shared.Infrastructure の被覆向上（#901・ブローカ readiness と構成自己申告の実体）
type: spec
status: done
related_ids:
  - NFR
  - FR-06
  - FR-15
  - FR-19
  - ADR-0004
  - ADR-0018
  - ADR-0027
  - ADR-0028
  - ADR-0057
  - IADR-0029
  - IADR-0046
  - IADR-0216
  - IADR-0233
  - IADR-0296
author: claude
created: 2026-08-29
updated: 2026-08-29
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md (手順 3〜5 の共通ヘルパとブローカ readiness)
  - planning:projects/microservices-platform/07_adr/ADR-0018_config-introspection.md (FR-15 構成情報 API・自己申告)
related_specs:
  - "20260828_issue-901_shared-infrastructure-coverage.md"
  - "20260822_issue-901_shared-infra-coverage-pr1.md"
  - "20260822_issue-901_shared-infra-coverage-pr2.md"
  - "20260823_issue-939_objectstorage-bootstrap-tests.md"
issue: "#901"
---

# 作業仕様書 — Platform.Shared.Infrastructure の被覆向上（#901・第 4 陣）

## 目的と射程

`Platform.Shared.Infrastructure` は platform / knowledge の**全サービスが依存する共有ライブラリ**である。
ここが割れると 1 サービスではなく全サービスへ波及する。**被覆率の数字ではなく
「割れたときの影響が大きく、かつ壊れ方が静かな振る舞い」を固定する**ことを目的とする。

🔴 **本作業は「床を戻すため」に行わない**（issue #901 注記）。床の前進は結果であって目的ではない。

### 🔴 issue 本文の前提値は既に古い（着手前に実測して判明）

issue #901 は被覆を **1.16%（772 行中 9 行）** と記す。これは `Platform.Shared.Infrastructure.Tests`
新設直後（#897 / #899 の時点）の値である。**その後 #931 / #938 / #939 と
`20260828_issue-901_shared-infrastructure-coverage.md` が着地しており、本作業の着手時点は
177 テスト / line 67.49%** である（実測は次節）。issue の走査表（`Foundation/Introspection` 23 クラス
無被覆 等）も同様に古い。**本書は issue の表を鵜呑みにせず、全て測り直した値で書く。**

### 起点 ID の置き方

共有基盤の試験整備であり、特定の `FR`/`UC` の実装ではない。先行 PR（#931 / #938 / #939）および
先行仕様書と同じ判断で、コミット件名のスコープは無採番 `NFR` を用いる
（`.claude/rules/traceability.repo.md`「メタ作業は代表例で、製品の作業にも当たる番号が無いことはある」）。

## 前提の確認（実測。記憶で書かない）

| 項目 | 実測 |
| --- | --- |
| worktree HEAD | `adaa2f8`（= base。作業ツリーは着手時点で clean） |
| `git rev-parse --is-shallow-repository` | **`true`** → **`git log` / `git blame` を出典に引かない**（planning#410） |
| .NET SDK | 10.0.400（`export PATH="$PATH:/root/.dotnet"` が要る） |
| Docker | **利用不可**（`docker info` が失敗）。Testcontainers 系の統合テストは実走できない |
| `src/ai-stock-trading`（submodule） | **未 populate（空ディレクトリ）** |
| xUnit1051 | 本プロジェクトは `XUnit1051Migrated` 収載済み → 新規テストは `TestContext.Current.CancellationToken` 必須 |

### 🔴 基準コミットで `backend.slnx` が既に赤い（本作業と無関係）

`dotnet build src/platform/backend/backend.slnx` は base `adaa2f8`（作業ツリー無変更）の時点で
`Platform.Bff` が `error CS0246: AiStockTrading` で落ちる。AST submodule が未 populate なため
`AiStockTrading.Bff.Endpoints.csproj` が実在せず、合成点 `BffEndpointComposition.cs` が解決できない。
**環境起因の既存事象であり本作業が壊したものではない。**

🔴 **この事実は本作業の優先順位付けに直接効く。** 下の走査で「唯一の実行経路が `Platform.Bff.Tests`」
と判明した対象は、**この worktree では 1 件も試験されていない**。CI（submodule populate 済み）では
走るが、共有ライブラリの振る舞いが**下流ユニットのテストにしか守られていない**状態そのものが
issue #901 の言う「共有ライブラリ側に専用テストが無い」に当たる。

## 母集合の引き方（走査。推定ではない）

### 走査 1: 本プロジェクト単独 cobertura をクラス別に集計（着手前の実測）

`dotnet test Platform.Shared.Infrastructure.Tests.csproj --collect:"XPlat Code Coverage"` の Cobertura を
**class 直下の `<lines>` のみ**で集計（`scripts/check-coverage-floor.js` と同じ規約。`obj/`・`Migrations/` 除外）。

**着手前 177 テスト / line 793/1175 = 67.49% / branch 216/340 = 63.53%。**

被覆の低い順（`Platform.Shared.Infrastructure` 配下のみ抜粋）:

| クラス | line | branch |
| --- | --- | --- |
| `Foundation/Audit/AuditLogger.cs` | **0/6（0%）** | **0/2（0%）** |
| `Composable/Adapters/Storage/NullObjectStorageClient.cs` | **3/30（10.0%）** | — |
| `Foundation/Pipeline/PipelineExtensions.cs` | 24/78（30.8%） | 6/40（15.0%） |
| `Foundation/Introspection/IntrospectionOptions.cs` | 5/13（38.5%） | — |
| `Composable/Adapters/Storage/S3ObjectStorageClient.cs` | 47/117（40.2%） | 14/20（70.0%） |
| `Foundation/Extensions/MassTransitExtensions.cs` | 3/6（50.0%） | — |
| `Foundation/Extensions/WolverineExtensions.cs` | 40/78（51.3%） | **1/18（5.6%）** |
| `Foundation/Introspection/ConfigInspectionService.cs` | 61/101（60.4%） | 29/48（60.4%） |
| `Composable/Adapters/Storage/ObjectStorageBootstrapHostedService.cs` | 12/19（63.2%） | 3/4 |
| `Foundation/Authz/BffScopeResolver.cs` | 30/47（63.8%） | 17/30 |
| `Foundation/Introspection/IntrospectionExtensions.cs` | 36/56（64.3%） | 18/34 |
| `Foundation/Introspection/DriftDetector.cs` | 56/72（77.8%） | 26/32 |

### 走査 2: 「被覆 0 / 低」が本当に無試験かを、リポジトリ全体で交差確認

🔴 **「本プロジェクトの被覆が 0」は「無試験」を意味しない**（issue #901 の明示する罠）。
他ユニットのテストが実行していることがある。よって**軸を 3 本**引いた（母集合規則 5）。

- **軸 1（型名）**: `AuditLogger` / `IAuditLogger` / `NullObjectStorageClient` / `WolverineExtensions` /
  `WolverineBrokerHealthCheck` / `ConfigInspectionService` / `PipelineExtensions` / `IntrospectionOptions` /
  `MassTransitExtensions` / `S3ObjectStorageClient` / `DriftDetector` / `StorageUri` / `CorrelationIdMiddleware`
- **軸 2（メソッド名 / DI 登録名）**: `GetVersionHistoryAsync` / `GetEffectiveConfigAsync` / `GetDriftAsync` /
  `CreatePresignedGetUrl` / `PutBytesAsync` / `GetBytesAsync` / `UsePlatformRetry`
- **軸 3（実行の実体か、ダブルか）**: ヒットした各テストファイルを開き、**実装型を `new` して
  メソッドを呼んでいるか**、それとも `BeOfType<>` の型検査・インタフェース実装のスタブに留まるかを読んだ

🔴 **走査は拡張子で絞らず、パスの除外のみで行った**（母集合規則 3）。
除外パス: `.git/` `node_modules/` `obj/` `bin/` `src/ai-stock-trading/`（別プロジェクトの submodule）。

#### 走査 2 の結果表

| 対象 | 他ユニット / 既存テストの実行実体 | 判定 |
| --- | --- | --- |
| `Audit/AuditLogger` | `Platform.Bff.Tests/PlatformLoggingTests.cs:143` が**実体を `new` して `Record` を呼ぶ 1 件のみ**。`NotificationService/Tests/TestDoubles.cs` と `DocumentService/Tests/TestWebApplicationFactory.cs` は `RecordingAuditLogger` へ**差し替える**（実体は 1 行も通らない）。`ObservabilityExtensionsTests` はコメントで名前に触れるのみ | **対象**（共有側 専用テスト 0 / 唯一の実行経路が本 worktree でビルド不可） |
| `Extensions/WolverineExtensions`（`WolverineBrokerHealthCheck`） | 専用テスト `WolverineBrokerHealthCheckTests.cs` は**登録面 3 件 ＋「トランスポート 0 件 → Unhealthy」1 件**のみ。集約ループ（167-244 行）は**全ユニットで 0 件** | **対象**（最優先。下記） |
| `Introspection/ConfigInspectionService`（実体） | 共有側 `DriftDetectionChainTests.cs` は `StubInspection : IConfigInspectionService`（**スタブ**）。実体を `new` するのは `Platform.Bff.Tests/ConfigVersionHistoryTests.cs` / `ConfigVersionHistoryBindingTests.cs` の `GetVersionHistoryAsync` のみ。`GetEffectiveConfigAsync` / `GetDriftAsync` の**実体呼び出しは本番 `ConfigBffEndpoints.cs` だけ** | **対象** |
| `Storage/NullObjectStorageClient` | `ConversionService/Worker/Tests/ObjectStorageTests.cs:58` が `PutTextAsync` / `CanResolve` / `GetTextAsync` を実行。共有側 `PortSwapCompositionTests` / `ObjectStorageExtensionsTests` は `BeOfType<>` の**型検査のみ**。**`DeleteAsync` / `PutBytesAsync` / `GetBytesAsync` / `CreatePresignedGetUrl` は全ユニットで実行 0 件** | **対象**（未実行メンバのみ） |
| `Pipeline/PipelineExtensions` | knowledge 側 ＋ 共有側 `PartialMigrationSafetyValveTests` / `WolverinePipelineExtensionsTests` が実行 | **見送り**（U5 / Wolverine 移行 C3 の並行トラック領域。先行仕様書と同じ判断） |
| `Storage/S3ObjectStorageClient` | `Knowledge.IntegrationTests/Storage/ObjectStorageRoundTripTests.cs`（Docker 必須）＋ 共有側 `S3ObjectStorageClientDeleteTests` | **見送り**（残りは実体 MinIO 往復。本環境は Docker 不可） |
| `Introspection/IntrospectionOptions` | `HttpEffectiveConfigCollectorTests` が一部を通す | **見送り**（残りは純粋なプロパティ。単体で固定する価値が低い。先行仕様書と同判断） |
| `Extensions/MassTransitExtensions` | `WolverineExtensionsTests` がリフレクションで `RetryIntervals` を読む | **見送り**（残り 3 行は MassTransit のバス設定ラムダで、実ブローカ設定を要する。ADR-0027 で撤退中の経路であり、いま固定すると C3 で捨てる） |
| `Authz/BffScopeResolver` の残り | `Matches` / `ExtractUserAttributes` は `Platform.Bff.Tests/BffScopeResolverTests.cs` が直接検証 | **見送り**（先行 PR が意図的に重複させていない。同じ判断を継ぐ） |
| `Introspection/DriftDetector` の残り | `Platform.Bff.Tests/DriftDetectorTests.cs` ＋ 共有側 `DriftServiceCoverageTests` | **見送り**（共有側に専用テストが既にある） |
| `Introspection/IntrospectionExtensions` の残り | 残りは `AddStep<TConsumer>`（MassTransit 版）と `MapPlatformIntrospection` の本体 | **見送り**（前者は C3 の射程。後者はエンドポイント本体で実 HTTP 器が要る） |
| `Middleware/CorrelationIdMiddleware` / `Ports/Storage/StorageUri` / `Extensions/AuthExtensions` ほか | 100% / 90%+ 被覆済み | 対象外 |

**黙って除外したものは無い**（母集合規則 6）。上表に挙げた 8 件がすべての除外であり、理由を各行に書いた。

## 優先順位と根拠（依存の広さ × 壊れたときの静かさ）

「静かに壊れる」＝**落ちずに誤った結果を返す**もの。以下の順で着手する。

| 順 | 対象 | 依存の広さ | 壊れたときの静かさ |
| --- | --- | --- | --- |
| **P1** | `WolverineBrokerHealthCheck.CheckHealthAsync` の集約（`WolverineExtensions.cs` 167-244 行） | Wolverine で発行する**全サービスの `/health/ready`** | **最大。** ブローカ不達でも 200 を返す readiness は、k8s が publish できない pod へトラフィックを流す。実装自身のコメントが「無いのと同じであるうえに**在るように見える**ぶん悪い」と書いている |
| **P2** | `AuditLogger` | DashboardService / DocumentService（4 機能）/ NotificationService / Platform.Bff の**計 8 箇所** | **最大。** `Audit=true` の構造化プロパティかメッセージテンプレートが崩れると、可観測性基盤が監査として抽出できなくなる。**例外も警告も出ない**（IADR-0216 決定 2 / `docs/security/security.md` の約束） |
| **P3** | `ConfigInspectionService` の実体（`GetDriftAsync` / `GetVersionHistoryAsync` / `BuildHistory`） | FR-15 構成情報 API・SC-11 画面・ドリフト定期検出 | **高。** 履歴の並び（新しい順）と未注入時の縮退は、壊れても 200 が返る。SC-11 が**古い順に見せる**だけで誰も落ちない |
| **P4** | `NullObjectStorageClient` の未実行メンバ | ストレージ未構成の**全 dev/test 環境**・本番 5 配線 | **高。** `CanResolve` が true を返せば読み取り側のプレースホルダー縮退が止まる。`DeleteAsync` は IADR-0296 / ADR-0057 決定 1 が「**例外にしてはならない**」と 🔴 で明記した非自明な決定なのに**試験が 1 件も無い** —— 将来の「整理」で最も戻されやすい形である |

`WolverineBrokerHealthCheck` を P1 に置いた決め手は **branch 1/18（5.6%）** という実測である。
登録面（3 件）と「トランスポート 0 件」（1 件）だけが試験されており、
**ブローカが実際に不健全なときの判定は 1 本も通っていない。**

## 対象範囲

- **対象**（すべて `Platform.Shared.Infrastructure.Tests` へのテスト追加）
  - `WolverineBrokerHealthCheck`: 例外 → Unhealthy / `null` → Unhealthy / Degraded 集約 / Healthy /
    複数トランスポートの併合 / 組み込みトランスポート（stub・local・tcp）を検査対象にしない allowlist
  - `AuditLogger`: 構造化プロパティ（`AuditAction` / `AuditSubject` / `AuditOutcome` / `AuditDetail` / `Audit`）と Information レベル
  - `ConfigInspectionService`: `GetDriftAsync` の `HasDrift` 導出 / `GetVersionHistoryAsync` の降順・縮退・空
  - `NullObjectStorageClient`: `DeleteAsync` の no-op / `PutBytesAsync` / `GetBytesAsync` / `CreatePresignedGetUrl`
- **対象外**
  - **本番コードの変更**（テストのために本番を変えない。変異試験の一時変異は必ず復元し md5 で確認する）
  - 走査 2 の表で「見送り」とした 8 件
  - Docker / 実 DB / 実ネットワークを要するもの

## 設計（何を固定するか）

### P1. `WolverineBrokerHealthCheck`

実装型は `internal`（`InternalsVisibleTo` は既に本テストプロジェクトへ開いている）だが、
コンストラクタ引数 `IWolverineRuntime` は約 30 メンバのインタフェースで**ダブルを書くのは非現実的**である。
かわりに**実 Wolverine ホストを建て、`WolverineOptions.Transports.Add(ITransport)`（public API）で
偽トランスポートを差し込む**。実装は `transport.GetType().GetMethod(..., DeclaredOnly)` で
`BuildHealthCheck` を解決するため、偽トランスポート側に public な `BuildHealthCheck` を宣言すれば経路に乗る。

`WolverineTransportHealthCheck` は **public abstract**（`TransportName` / `Protocol` / `CheckHealthAsync` が abstract）
であり、テストアセンブリで派生できる。`TransportHealthResult` は public record で構築可能。
—— いずれも着手前にリフレクションで実測して確かめた（推測ではない）。

| 固定する振る舞い | 意図 |
| --- | --- |
| ブローカが Unhealthy を返す → 全体 Unhealthy・**メッセージに protocol と理由が載る** | 落ちているのに 200 を返さない |
| `BuildHealthCheck` が例外 → **Unhealthy**（握り潰して Healthy にしない） | 実装コメントの 🔴 そのもの |
| `BuildHealthCheck` が `WolverineTransportHealthCheck` を返さない（形が変わった） → **Unhealthy** | 「観測できない」を「異常が無い」と読まない |
| Degraded のみ → **Degraded**（Unhealthy でも Healthy でもない） | 3 値の縮退を潰さない |
| 全て Healthy → **Healthy** | 対照条件（常に Unhealthy を返す実装を落とす） |
| Unhealthy と Degraded が混在 → **Unhealthy が勝つ** | 悪い方に倒す |
| 組み込み（stub / local / tcp）しか無い → allowlist に載らず「0 件」の Unhealthy | denylist へ退行させない |
| `OperationCanceledException` は握らない | 停止要求を「ブローカ異常」に化けさせない |

### P2. `AuditLogger`

`RecordingLogger<AuditLogger>`（既存のダブル）で Information 1 件と**構造化プロパティの key/value** を検査する。
整形済み文字列だけを見ると抽出キーの喪失を見逃すため、`State` の key/value で表明する。

| 固定する振る舞い | 意図 |
| --- | --- |
| `Record` が **Information** を 1 件出す | レベルが落ちると監査が既定の収集から外れる |
| `AuditAction` / `AuditSubject` / `AuditOutcome` が引数どおり | 抽出キー |
| **`Audit` プロパティが `true`** | `docs/security/security.md` が約束する抽出条件（IADR-0216 決定 2） |
| `detail` 省略時は `AuditDetail` が**空文字**（`null` ではない） | `detail ?? string.Empty` の分岐。null だと構造化ログ側で欠落する |
| `detail` 指定時はその値 | 上の対照条件 |

### P3. `ConfigInspectionService`

`IEffectiveConfigCollector` のスタブ ＋ `TimeProvider`（固定）＋ `IOptions<ConfigVersionOptions>` で組み立てる。

| 固定する振る舞い | 意図 |
| --- | --- |
| `GetDriftAsync`: finding 0 件 → `HasDrift == false` | 対照条件 |
| `GetDriftAsync`: finding 1 件以上 → `HasDrift == true`・**`DetectedAt` は `TimeProvider` 由来** | 時刻源を握る |
| `GetVersionHistoryAsync`: `History` 注入時は **`AppliedAt` 降順** | SC-11 の並び。壊れても 200 が返る |
| 同上: `AppliedAt` 不明（未設定・不正文字列）は**末尾**へ、同値は注入順を保つ（安定ソート） | 実装コメントの明示的な約束 |
| 同上: `History` 未注入 → **現在バージョンの単一エントリ**（`HadDrift` は `null`） | dev/compose の縮退 |
| 同上: 現在バージョンも空 → **空一覧**（単一エントリを作らない） | 上の対照条件 |

### P4. `NullObjectStorageClient`

| 固定する振る舞い | 意図 |
| --- | --- |
| 🔴 `DeleteAsync` は **例外を投げず完走**し、Warning を 1 件出す | IADR-0296 / ADR-0057 決定 1。FR-19 / FR-06 が未構成環境で 500 にならない |
| `PutBytesAsync` は決定的 URI を返す（`PutTextAsync` と同じ規則） | 未構成でも参照が壊れない |
| `GetBytesAsync` / `CreatePresignedGetUrl` は `NotSupportedException` | 「無い本文を返せない」——`DeleteAsync` と**向きが逆**であることを対で固定する |
| `CanResolve` は常に false | 読み取り側のプレースホルダー縮退の唯一の条件 |

## 受け入れ基準

- [ ] 無試験領域の走査結果と、選んだ優先順位の根拠が本書にある（issue #901 基準 1）
- [ ] 追加したテストが**変異試験で検出力を示している**（同 基準 2）。各領域 1 変異以上・全体 5 種以上・
      **無変異ベースラインを対で取り、全て KILL を実測**する
- [ ] 追加後に `dotnet test` が緑で、**テスト件数が増えている**（skip で緑にしない）
- [ ] `dotnet format --verify-no-changes` が通る
- [ ] 本番コードを 1 行も変更していない（変異は復元し md5 一致を確認）
- [ ] 床（`src/coverage-floor.json`）は**導出規則に従って**判断し、適用した規則を報告に書く

## テスト方針

- xUnit v3 / 表明は AwesomeAssertions（ADR-0030）。新規パッケージを追加しない。
- `CancellationToken` を取る呼び出しには `TestContext.Current.CancellationToken` を渡す（xUnit1051 収載済み）。
- ログ検査は既存の `Testing/RecordingLogger<T>` を使う。
- **各テストに対照条件を置く**（片側だけだと「常に true / 常に false」の壊れた実装でも通る）。

## 床（ratchet）の扱い

床の単一情報源は `src/coverage-floor.json`（現行 `line: 88` / `branch: 68`）。導出規則は
**IADR-0118 決定 2「実測からの整数切り下げ」**、例外は IADR-0195 決定 3（切り下げが機能する床を
与えないときに限り 1 つ下）、余裕は **pt ではなく被覆行・被覆分岐の本数**で見る（`$comment` #899 / #929）。

🔴 **床が判定する母集合は「`src/` 配下の全 Cobertura をレポート跨ぎで重複排除した合算」であり、
本プロジェクト単独の値ではない。** 本環境は Docker 不可で統合テストが走らず、`Platform.Bff` は
ビルドすらできないため、**CI と同じ母集合を作れない。** `$comment` の #899 追記が同じ状況を
「本環境では床判定を実走できない」と記録している。**測れないものを推測で書き換えない。**

## 計画書との差異

- 差異: なし（試験の追加のみ。計画書の要求・制約を変更しない）

## 未決事項

- なし（着手条件は上の走査で確定した）

---

# 実施結果

## 追加したもの（本番コードは 1 行も変更していない）

| ファイル | 件数 | 対象 |
| --- | ---: | --- |
| `Foundation/Extensions/WolverineBrokerHealthAggregationTests.cs` | 9 | ブローカ readiness の集約判定 |
| `Foundation/Audit/AuditLoggerTests.cs` | 7 | 監査ログの構造化プロパティ |
| `Foundation/Introspection/ConfigInspectionServiceTests.cs` | 13 | ドリフト導出・構成バージョン履歴 |
| `Composable/Adapters/Storage/NullObjectStorageClientTests.cs` | 8 | 未構成時の縮退契約 |
| **合計** | **37** | |

`git diff --stat`（追跡下）は**空**である ＝ 本番コードは 1 バイトも変わっていない。
新規は上記 4 ファイルと本書、および `src/coverage-floor.json` の変更のみ。

## 被覆の前後（`Platform.Shared.Infrastructure.Tests` 単独 cobertura・Debug）

| | テスト | line | branch |
| --- | ---: | --- | --- |
| 着手前 | 177 | 793/1175 = **67.49%** | 216/340 = **63.53%** |
| 追加後 | 214（+37） | 911/1175 = **77.53%**（+118 行 / +10.04pt） | 248/340 = **72.94%**（+32 本 / +9.41pt） |

クラス別:

| クラス | line 前 → 後 | branch 前 → 後 |
| --- | --- | --- |
| `Foundation/Audit/AuditLogger` | 0/6（0%） → **6/6（100%）** | 0/2 → **2/2** |
| `Foundation/Extensions/WolverineExtensions` | 40/78（51.3%） → **78/78（100%）** | 1/18（**5.6%**） → **17/18（94.4%）** |
| `Foundation/Introspection/ConfigInspectionService` | 61/101（60.4%） → **99/101（98.0%）** | 29/48 → **43/48（89.6%）** |
| `Composable/Adapters/Storage/NullObjectStorageClient` | 3/30（10.0%） → **23/30（76.7%）** | — |
| `Foundation/Introspection/IntrospectionOptions`（副次） | 5/13 → **13/13（100%）** | — |

`NullObjectStorageClient` の残り 7 行は `PutTextAsync` の本体で、
`ConversionService.Worker.Tests` が既に直接検証しているため意図的に重複させていない。

## 変異試験（14 種・**全て KILLED**。無変異ベースラインを対で取得）

各回とも **① 無変異ベースライン実行 → ② 変異適用 → ③ 実行 → ④ 復元 → ⑤ md5 一致確認 → ⑥ 再実行**
の順で行った。⑥ が緑に戻ることまで毎回確認している。

| # | 対象 | 変異内容 | ベースライン | 変異後 | 落ちた試験 | 復元後 |
| --- | --- | --- | --- | --- | --- | --- |
| M-1 | `WolverineBrokerHealthCheck` | `result is null` の分岐を `continue`（観測不能を健全扱い） | 13 pass | **1 fail** | ヘルスチェックを取得できなければUnhealthyにする | 13 pass / md5 一致 |
| M-2 | 同上 | 末尾を常に `Healthy()` に | 13 pass | **1 fail** | Degradedのみなら全体もDegradedにする | 13 pass / md5 一致 |
| M-3 | 同上 | `catch ... when (ex is not OperationCanceledException)` の `when` 削除 | 13 pass | **1 fail** | 停止要求は握らずに伝播する | 13 pass / md5 一致 |
| M-4 | 同上 | allowlist を denylist（`t.Protocol != "local"`）へ退行 | 13 pass | **4 fail** | 組み込みトランスポートはブローカとして数えない ほか 3 件 | 13 pass / md5 一致 |
| M-5 | 同上 | `unhealthy.Count > 0` に `&& degraded.Count == 0` を追加（Degraded を勝たせる） | 13 pass | **1 fail** | UnhealthyとDegradedが混在すればUnhealthyが勝つ | 13 pass / md5 一致 |
| M-6 | `AuditLogger` | `detail ?? string.Empty` → `detail` | 7 pass | **1 fail** | detail省略時は空文字になる_nullにしない | 7 pass / md5 一致 |
| M-7 | 同上 | `Audit` プロパティの値 `true` → `false` | 7 pass | **3 fail** | Audit_フラグが_true_で付く ほか 2 件 | 7 pass / md5 一致 |
| M-8 | 同上 | `LogInformation` → `LogDebug` | 7 pass | **3 fail** | 監査は_Information_で1件だけ出る ほか 2 件 | 7 pass / md5 一致 |
| M-9 | `ConfigInspectionService` | `OrderByDescending` → `OrderBy` | 13 pass | **2 fail** | 履歴は適用日時の降順に並べ替える ほか 1 件 | 13 pass / md5 一致 |
| M-10 | 同上 | 日時不明の既定値 `MinValue` → `MaxValue` | 13 pass | **1 fail** | 適用日時が不明な履歴は末尾へ送る | 13 pass / md5 一致 |
| M-11 | 同上 | `HasDrift` を `findings.Count > 0` → `false` に固定 | 13 pass | **1 fail** | 不一致があれば_HasDrift_が真になる | 13 pass / md5 一致 |
| M-12 | 同上 | 空バージョン時の空一覧縮退を無効化 | 13 pass | **1 fail** | 現在バージョンも空なら空一覧を返す | 13 pass / md5 一致 |
| M-13 | `NullObjectStorageClient` | `DeleteAsync` を `NotSupportedException` へ | 8 pass | **1 fail** | 削除は例外を投げずに完走する | 8 pass / md5 一致 |
| M-14 | 同上 | `CanResolve` を `true` に | 8 pass | **4 fail** | 常に解決不可を返す（4 ケース） | 8 pass / md5 一致 |

**生存（SURVIVED）は 0 件。** 復元は pristine スナップショットからの `cp` で行い、
4 ファイルすべてが base と md5 一致であることを最後に再確認した。

## 実走した検証

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/platform/backend/backend.slnx` | **base と同一の既存 1 エラー**（`Platform.Bff` の `CS0246: AiStockTrading`。AST submodule 未 populate）。他は全て成功 |
| 全ビルド可能テストプロジェクト（platform 6 件） | 672 pass / **0 fail / 0 skip**（Kernel 42・Shared.Infrastructure 214・Authorization 95・LlmGateway 202・McpServer 66・Notification 53） |
| 全ビルド可能テストプロジェクト（18 件・knowledge 含む） | 全て緑（統合テストは Docker 不在で 41 件 skip） |
| `dotnet format src/platform/backend/backend.slnx --verify-no-changes` | **exit 0** |
| `node scripts/check-coverage-floor.js`（現行の床 88 / 68 で） | **exit 0**（`OK: 床を下回っていません`） |

## 床（`src/coverage-floor.json`）——**88 / 68 のまま据え置いた**

これは**測定定義の変更ではなく ratchet の引き上げ**である（#571・#574・#900 とは性質が違う）。
本番コードを 1 行も足していないため**分母は動かず、被覆数だけが増えた**。

同一条件の前後比較（base `adaa2f8` / Debug / レポート 18 件 / 2026-08-29）:

| | line | branch |
| --- | --- | --- |
| 追加前 | 89.37%（11355/**12706**） | 73.48%（2491/**3390**） |
| 追加後 | 90.17%（11457/**12706**） | 74.4%（2522/**3390**） |
| 差分 | **被覆行 +102** | **被覆分岐 +31** |

### 🔴 ［2026-08-29 追記 / #901］引き上げは**統合時に取り下げた**

本書は当初 **89 / 69 へ引き上げた**と書いていた。導出（#929 / #934 が記録した CI 実測
line 89.55% = 6986/7801 / branch 69.83% = 1558/2231 を切り下げ、耐性 line 43 行 / branch 18 本）は
それ自体としては筋が通っていたが、**統合時の再検討で据え置きへ改めた。**

**理由は 1 つである。基準にした CI run（commit `fa4987c2`）は、本 PR の本番コードを 1 行も含んでいない。**

本 PR は削除の伝播（`DocumentObjectPurger` / 全版削除）・資格情報のマスク・SC-12 の境界層・
健全性指標の生産者など、**本番コードを大きく足しており分母が動く**。43 行の余裕は、
その増分が完全に被覆されている場合にしか残らない。
🔴 **分母が動く変更と同じ PR で床を上げるのは、実測ではなく賭けである。**

さらに床を強制するのは `integration.yml`（develop への push と日次）**だけ**であり、
PR 側は `--report-only` である。**賭けが外れたとき赤くなるのは develop であって、この PR ではない。**

**いつ上げるか**: 本 PR が develop へ入り、`integration.yml` が**新しい母集合で 1 回実測してから**、
その値を切り下げて上げる。本作業は被覆を 1 行も減らしていないので、上げ幅は下の +102 / +31 を
下回らない見込みである。**据え置きは「上げる根拠が無い」ではなく「上げる基準値がまだ無い」である。**

同じ判断と数値を `src/coverage-floor.json` の `$comment` にも残した。

## 受け入れ基準の結果

| 基準 | 結果 |
| --- | --- |
| 無試験領域の走査結果と優先順位の根拠が本書にある | ✅ 走査 1（クラス別実測）＋走査 2（3 軸の交差確認・除外 8 件の理由つき） |
| 変異試験で検出力を示している | ✅ 14 種・**全て KILLED**・生存 0。ベースラインと復元後の対を毎回取得 |
| テスト件数が増えている（skip で緑にしない） | ✅ 177 → 214（**Skipped: 0**） |
| `dotnet format --verify-no-changes` | ✅ exit 0 |
| 本番コードを変更していない | ✅ `git diff --stat`（追跡下）が空・md5 一致 |
| 床を引き上げ、根拠を残した | ⚠️ **据え置き（88/68）＋根拠を記載。** 引き上げの基準にできる CI 実測が本 PR の本番コードを含まないため（上記追記）。**増分の実測（+102 行 / +31 分岐・分母不動）は `$comment` に残した** |

## 申し送り

- 🔴 **issue #901 本文の前提値（1.16% / 772 行中 9 行）は #897 時点のもので、既に 3 世代古い。**
  走査表も同様である。次に触る者は**必ず測り直す**こと。
- 🔴 **`Platform.Bff` は base の時点でビルドできない**（AST submodule 未 populate）。本作業と無関係だが、
  `AuditLogger` / `ConfigInspectionService` の唯一の実行経路がそこにあったため、
  **この worktree では 1 件も試験されていない状態だった**。本作業で共有ユニットに閉じた形で守られるようになった。
- `Foundation/Pipeline/PipelineExtensions.cs`（30.8% / branch 15.0%）は
  **U5 / Wolverine 移行チェーン（C3）の並行トラック領域**として手を付けていない。C3 着地後に別途。
- `S3ObjectStorageClient`（40.2%）の残りは実体 MinIO 往復であり、Docker のある環境で扱うのが妥当。
- `MassTransitExtensions` の残り 3 行はバス設定ラムダで、ADR-0027 で撤退中の経路である。
  いま固定すると C3 で捨てることになるため見送った。

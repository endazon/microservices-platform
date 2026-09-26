---
title: 個人資料の日次保守ループが停止要求以外の取り消しで永久に止まる件を直し、削除の直前の読み直しと起動失敗の競合の試験の抜けを塞ぐ（#1598）
type: spec
status: done
related_ids: [FR-19, FR-22, FR-17, FR-18, FR-10, FR-11, NFR-21, UC-11, SC-19, SC-10, ADR-0096, ADR-0057, ADR-0035, ADR-0044, IADR-0431, IADR-0474, IADR-0299, IADR-0425, IADR-0430, IADR-0380, IADR-0466]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0096_private-note-disposal-after-view-window.md
related_specs:
  - 20260926_issue-1583_purge-reread-before-delete.md
  - 20260926_issue-1582_similarity-wiring-startup-race.md
issue: "#1598"
---

# 仕様書: 日次保守ループの取り消しの扱いと、#1591・#1594 の監査の残り（#1598）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-19**（個人資料のライフサイクル。退職者の資料の削除・90 日の自動物理削除）、**FR-22**（削除の通知。日次ループが止まると以後届かない）、
  FR-17 / FR-18 / FR-10（GraphService の日次・毎時のバッチ。同じ形の欠陥）、FR-11 / NFR-21（LlmGateway の月次予算の起動検証。T-2f）
- 関連 ADR: ADR-0096 決定 1・2（退職 30 日後の完全削除）、ADR-0057 決定 2（残余を置かない）、ADR-0035 決定 3（クラスタの日次バッチ）、ADR-0044（予算）
- 関連 IADR: IADR-0431（退職者の資料の削除）、IADR-0474 決定 6 と #1583 追記（削除の直前の読み直し）、
  IADR-0299 決定 3（ナレッジ健全性のワーカーの形。IADR-0425・IADR-0430 がこの形を写した）、IADR-0380（#1594 の試験）、IADR-0466 決定 2（T-2f）
- 起点 issue: #1598（#1591＝#1583 の PR と #1594＝#1582 の PR の監査の指摘）

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | `PrivateNoteMaintenanceHostedService` は、**停止要求（`stoppingToken`）が立っているときだけ**ループを抜ける。停止要求の無い取り消し（`OperationCanceledException`）は失敗として記録し、次の周期へ進む | 新しい試験: 1 周期目に停止要求と無関係な取り消しを起こし、2 周期目が回ることを測る |
| AC-2 | 停止要求では従前どおり静かに終わる（例外で終わらない） | 同上のクラスで `StopAsync` 後に `ExecuteTask` が正常完了 |
| AC-3 | 同じ欠陥を持つ他の常駐処理を走査し、全件を下の「母集合」に記録する。**手当てが同一で、各々に試験を付けられるものだけ**本 PR で直す。それ以外は PR 本文と本書でフォローアップに回す | 本書「母集合」 |
| AC-4 | 削除の直前の読み直しで、窓が再び開いた（`WithinWindow`）・判定不能（`NotEvaluable`）になった所有者は、**無効のままでも**消さない（「資格の判定を無視する」変異を殺す） | `PrivateNoteDepartedOwnerPurgeTests` に 2 件 |
| AC-5 | 読み直しの最中の定期処理の取り消しは伝わる（サービスの水準で測る。「取り消しのフィルタを外す」変異を殺す） | 同上に 1 件 |
| AC-6 | `StubOwnerRetentionDirectory.DeclareSequence` は、最後の答えが例外を投げるなら**宣言の時点で**失敗する（約束を機械で強制する） | スタブの実装＋その自己試験 |
| AC-7 | LlmGateway `LlmBudgetMetricsTests` の T-2f は、#1594 と同じく起動検証（`IStartupValidator`）を包んで検証の例外を host の破棄より前に記録し、着順に依らず「起動しない」と「落ちた理由が起動検証」の 2 点を測る | T-2f の書き換え |
| AC-8 | #1594 の試験のコメント（`SimilaritySourceWiringTests.cs` の「ValidateOnStart を外すと未登録の枝に入る」）を実測に合わせて直す | コメントの是正 |

## 設計

### 1. 常駐ループの取り消しの扱い（AC-1〜AC-3）

- 周期の本体の `catch (Exception ex) when (ex is not OperationCanceledException)` を
  `when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)` にする。停止要求の無い取り消し
  （HttpClient の時間切れの `TaskCanceledException`、下流の口が自分の期限で投げる取り消し等）は失敗として記録し、次の周期へ進む。
- 外側の `catch (OperationCanceledException)` にも `when (stoppingToken.IsCancellationRequested)` を付ける。外側へ届く取り消しは、
  直した後は停止要求のものだけのはずである。**想定外の取り消しを黙って「シャットダウン」と読まない**（届いたら例外のまま出す）。
- 形は `DriftDetectionHostedService`（#1382）が既に採っているものと同じである。新しい判断ではない。
- 周期の長さ（24 時間・1 日・1 時間）は試験で待てないので、**周期を差し替える口を 1 つ足す**:
  `internal TimeSpan CycleInterval { get; init; } = Interval;`。本番の組み立て（DI）は触らず、`Interval`（公開の定数）の意味も変えない。
  試験だけがオブジェクト初期化子で短くする（`InternalsVisibleTo` は DocumentService・GraphService とも既にある）。
- 試験: 周期を数十ミリ秒にしたワーカーを `StartAsync` し、1 周期目の本体で停止要求と無関係な `OperationCanceledException` を投げ、
  2 周期目が走ったことを待つ（上限つき）。直す前の形では 1 周期目でループが終わり、2 周期目は来ない（赤）。
  - `PrivateNoteMaintenanceHostedService`: 本体は `PrivateNoteMaintenanceService.RunAsync`（sealed・具象の依存）なので、試験ホスト
    （`TestWebApplicationFactory`）のスコープを使い、1 巡目の判定（`IOwnerRetentionDirectory.GetAsync`。**捕まえない側**）に取り消しを投げさせる。
    2 周期目で同じ所有者が「経過」と答え、資料が消えることを測る（周期が回った＝退職者の削除が止まっていない）。
  - GraphService の 3 つ: 本体の最初の呼び出しはリースの取得（`TryAcquireAsync`）なので、1 回目だけ取り消しを投げ、以後はリースを渡さない
    （＝本体は読まない）偽物を差し込み、2 回目の取得が起きることを測る。

### 2. 読み直しの試験の抜け（AC-4〜AC-6）

- AC-4: 判定で「経過」、読み直しで `WithinWindow`（無効のまま）／`NotEvaluable`（無効のまま）を返す 2 件。資料が残り、照会が 2 回。
- AC-5: 2 人の所有者の読み直しの答えを「取り消しを立てて投げる」にし、`RunAsync` が `OperationCanceledException` で終わること、
  **2 人目の読み直しが起きないこと**（照会の合計が 3 回）、資料が残ることを測る。取り消しのフィルタを外す変異では 1 人目の取り消しが
  「見送り」に畳まれて 2 人目の読み直しへ進む（照会の合計が 4 回）。`RunAsync` 自体は後段の EF の呼び出しで結局取り消しを投げるので、
  **例外の有無だけでは変異を殺せない** —— 照会の回数で測る。
  - 読み直しの順序（`Distinct()` の順）に依らないよう、2 人とも同じ列を宣言する。列の最後は `null`（AC-6 の約束）。
- AC-6: `DeclareSequence` で、**最後の答えを宣言の時点で 1 回評価し**、例外なら `ArgumentException` で宣言を拒む。
  答えは副作用の無いラムダで、評価しても列の消費には影響しない。自己試験を 1 件足す（単体）。

### 3. 起動失敗の競合（AC-7・AC-8）

- `RecordingStartupValidator`（#1594）と同じ包みを LlmGateway の試験側へ置く（ユニットが別で、試験の補助は共有しない —— 片方のユニットの
  試験プロジェクトからもう片方を参照しない）。T-2f は `async Task` にし、① `host.Services` が投げる ② 記録された起動検証の例外に
  `OptionsValidationException`（`no-such-purpose` を含む）がある、の 2 点を測る。LlmGateway の `ValidateOnStart` は 3 件あり、
  失敗が複数なら `AggregateException` になり得るので、記録した例外は従前の `Flatten` で平らにして探す。
- AC-8: 実測（本書「検証」）では、GraphService の `Program.cs` から `.ValidateOnStart()` を外しても `IStartupValidator` の登録は残り、
  包みの「未登録」の枝には入らない。起動が通って `act.Should().Throw` が赤になる。コメントをこの事実に直し、枝は「登録そのものが無い構成」への
  備えであると書く（LlmGateway 側の同じ包みも同じ注記にする）。

### 記録

- IADR-0431 へ日付つき追記（日次ループの取り消しの扱い）。IADR-0474 の #1583 追記の変異表へ［#1598］行を足す（読み直しの試験の抜け）。
  GraphService の 3 つは形の出所である IADR-0299 へ日付つき追記。**新しい IADR 番号は取らない**（欠番を作らない規約・依頼の指示）。
- `docs/tests/FR-19_private-notes-lifecycle.md` の 18 の件数（14 → 17）と、ループの行（新規）を更新する。

## 母集合（AC-3。常駐処理の取り消しの扱い）

走査は 2026-09-26、`origin/develop` = `8a51ec30` の上で行った。対象は `src/platform/**` と `src/knowledge/**` の試験以外
（`src/ai-stock-trading` は別リポジトリの submodule で、依頼の「両ユニット」に入らないため**除外**）。

- 軸 1（常駐処理の全数）: `git grep -nE "IHostedLifecycleService|override Task ExecuteAsync|override async Task ExecuteAsync"` と
  `grep -rlnE "(:|,)\s*(BackgroundService|IHostedService)\b"` → **14 件**（`ExecuteAsync` を持つ 12 件＋`StartAsync` だけの起動時処理 2 件）。
- 軸 2（型だけの捕捉）: `grep -rnE "catch\s*\(\s*(System\.)?OperationCanceledException"` → 試験以外 15 行。うち常駐処理の中の 12 行を下表へ。
  残る 3 行（`DepartmentAttributeSync.cs:102`・`GrpcOwnerAccountDirectory.cs:44`・`GrpcOwnerRetentionDirectory.cs:46`）は常駐処理の
  ループではない（周期の本体の内側・口の実装）で、いずれも `when` で絞っている（**除外**）。
- 軸 3（取り消しを素通しする捕捉）: `git grep -n "is not OperationCanceledException"` → 試験以外 30 行。常駐処理のループに在るのは
  PrivateNote（:392）・GraphService の 3 つ・`ToolDeclarationSource.cs`（:177・:190）・`DriftDetectionHostedService.cs`（:47。停止要求で絞り済み）。
  他は要求処理・口の実装の内側で、常駐ループの寿命を決めない（**除外**）。
- 軸 4（`TaskCanceledException` の捕捉）: 0 件。

| 常駐処理 | 行 | 形 | 判定 | 本 PR |
| --- | --- | --- | --- | --- |
| `DocumentService` `PrivateNoteMaintenanceHostedService` | :392 / :399 | 内側 `ex is not OCE`・外側は型だけ | 🔴 **欠陥**（停止要求の無い取り消しでループが永久に終わる） | **直す＋試験** |
| `GraphService` `ClusterDetectionHostedService` | :33 / :41 | 同上（同一の形） | 🔴 **欠陥**（日次のクラスタ検出が止まる） | **直す＋試験**（手当てが同一） |
| `GraphService` `ClusterSummaryHostedService` | :53 / :61 | 同上（同一の形） | 🔴 **欠陥**（日次の要約生成が止まる） | **直す＋試験**（手当てが同一） |
| `GraphService` `KnowledgeHealthHostedService` | :34 / :42 | 同上（同一の形） | 🔴 **欠陥**（毎時の健全性の報告が止まる） | **直す＋試験**（手当てが同一） |
| `DataSourceService` `DataSourceSyncHostedService` | :34 | `catch (OCE) { break; }`（do-while） | 🔴 **欠陥**（定期同期が止まる） | **フォローアップ**。手当ては同じ向き（`when (stoppingToken…)`）だが、周期が構成から来て最短 30 秒に丸められ、試験で待てる周期の口が別の形になる（「同一の手当て」に当たらない） |
| `McpServer` `ToolCatalogRefresher`（`ToolDeclarationSource.cs`） | :190 / :196 | 内側 `ex is not OCE`・外側は `Task.Delay` だけを包む | ⚠ **別の欠陥**（停止要求の無い取り消しが `ExecuteAsync` から漏れ、既定の `StopHost` でホストが落ちる。静かに止まるのではなく落ちる） | **フォローアップ**（壊れ方が違う。直すなら同じ `when` だが、試験の形も別） |
| `Platform.Shared` `DriftDetectionHostedService` | :47 / :59 | 内側は停止要求で絞り済み（#1382）・外側は `WaitForNextTickAsync(ct)` だけを包む | 問題なし（外側に届く取り消しは停止要求のものだけ） | — |
| `AuthorizationService` `DepartmentAttributeSyncHostedService` | :76 | `when (stoppingToken.IsCancellationRequested)` | 問題なし | — |
| `NotificationService` `NotificationMaintenanceHostedService` | :49 | 同上 | 問題なし | — |
| `DashboardService` `UsageRetentionHostedService` | :54 | 同上 | 問題なし | — |
| `Knowledge.Bff` `UsageEventDispatcher` | :49 / :89 | 外側 `when (stoppingToken…)`・内側 `!IsShutdown` | 問題なし | — |
| `IngestionService` `QdrantCjkNgramBackfillHostedService` | :35 | 1 回きり・`when (stoppingToken…)` | 問題なし（ループではない） | — |
| `IngestionService` `QdrantBootstrapHostedService` | — | `StartAsync` の 1 回きり・`catch (Exception)` | 対象外（ループではない） | — |
| `Platform.Shared` `ObjectStorageBootstrapHostedService` | — | 同上 | 対象外（ループではない） | — |

- 起動失敗の競合（AC-7）の母集合は #1594 の仕様書（`20260926_issue-1582_similarity-wiring-startup-race.md`「母集合」軸 1）が引いた 2 件で、
  残りは本件の T-2f だけである。本 PR の時点で引き直した（`git grep -n "=> host.Services\|=> factory.Services\|=> f.Services" -- 'src/**/*Tests.cs'`）
  結果は 12 行: `SimilaritySourceWiringTests.cs:137`（#1594 で対応済み）・`LlmBudgetMetricsTests.cs:141`（**本件・反映**）・同 :155
  （起動に成功したホストの計器の読み出し。**除外**）・`PipelineStepRegistrationTests.cs:143`／`PipelineRecomposeTests.cs:125`
  （試験スレッドで開始済みの汎用ホストから `IMessageBus` を引き、ハンドラ不在の失敗を待つ。起動失敗を待たず、`DeferredHost` の競合が無い。**除外**）・
  他 7 行はプロパティの読み出し（`factory.Services.GetRequiredService<…>()`）で起動失敗を待たない（**除外**）。

## 検証

実測はすべて 2026-09-26、手元（Windows・.NET SDK 10.0.401）。

- 全件: `DocumentService.Tests` **631/631**、`GraphService.Tests` **644/644**、`LlmGateway.Tests` **326/326**。
  `dotnet format <slnx> --verify-no-changes`（変更したファイル）は両ユニットで差分なし。
- 時間に依る新しい試験の安定性: `BatchLoopForeignCancellationTests`（3 件）を **15/15 回**、`PrivateNoteMaintenanceHostedServiceTests` ＋
  `PrivateNoteDepartedOwnerPurgeTests`（18 件）を **15/15 回** 連続実行して全件緑。
- 変異（1 か所ずつ書き換えて実行し、元へ戻した）:

  | 変異 | 実行した試験 | 赤 |
  | --- | --- | --- |
  | GraphService の 3 つを直す前の形へ戻す | `BatchLoopForeignCancellationTests` | **3/3**（3 件とも 10 秒の時間切れ） |
  | `PrivateNoteMaintenanceHostedService` を直す前の形へ戻す | `PrivateNoteMaintenanceHostedServiceTests` | **1/1**（「10 秒待っても 2 周期目が退職者の資料を消さなかった」） |
  | 同: 周期の本体の絞り込みだけを戻す | 同上 | **1/1**（同上） |
  | 読み直しで資格の判定を見ない（`status is { Found: true, Enabled: false }`） | 退職者削除 17・`GrpcOwnerRetentionDirectoryTests`・ループの計 37 件 | **2**（AC-4 の 2 件だけ。従前の 14 件は全件緑＝監査の指摘どおり生き残っていた） |
  | 読み直しの `catch` から取り消しのフィルタを外す | 同上 37 件 | **1**（AC-5。照会の合計が 3 ではなく 4。`RunAsync` は変異の下でも取り消しで終わった＝例外の有無では殺せないことを確認） |
  | スタブ: 最後の答えを宣言時に評価しない | `StubOwnerRetentionDirectoryTests` | **1/2**（陽性対照の 1 件は緑） |
  | LlmGateway の `LlmBudgetOptions` の `.ValidateOnStart()` を外す | `LlmBudgetMetricsTests` | **1**（T-2f。「no exception was thrown」＝未登録の枝ではない） |
  | GraphService の `.ValidateOnStart()`（唯一の 1 件）を外す（AC-8 の実測。試験は #1594 のまま） | `Unknown_source_fails_at_startup` | **1**（:139 の `act.Should().Throw` で「no exception was thrown」。`IStartupValidator` の登録は残り、未登録の枝には入らない） |

  外側の `catch (OperationCanceledException)` の絞り込みだけを外す変異は、周期の本体が停止要求の無い取り消しを捕まえる限り外側へ何も届かないので、
  ループの試験では**等価**である（生き残るが挙動は変わらない）。
- 競合の再現（AC-7）: 一時的な試験（コミットしない）で `CreateHost` の `builder.Build()` と `host.Start()` の間に 10 秒待たせると、
  **従前の T-2f は 5/5 回赤**（`ObjectDisposedException: Cannot access a disposed object. Object name: 'IServiceProvider'.` で
  `OptionsValidationException` が見つからない）、**新しい T-2f は同じ遅延の下で 5/5 回緑**。
- 実装中に同じ型の穴を 1 つ踏んだ: AC-5 の試験は 2 人目の列に取り消しの答えを**途中に**残す（読み直されないのが狙い）ため、
  同じクラスの後続の試験の周期の 1 巡目がそれを引いて破棄済みの取り消し元で落ちた（初回実行で 1 件赤）。試験の最後に 2 人とも
  「引けなかった」へ戻して解消した。スタブの「最後の答え」の強制は、途中に残った答えまでは防がないので、試験側に注記した。

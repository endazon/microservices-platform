---
title: 作業仕様書 — fan-out 統合テストの待ち合わせを購読開始と実処理へ分ける
type: spec
status: done
related_ids:
  - FR-02
  - FR-06
  - FR-13
  - FR-14
  - UC-04
  - ADR-0018
  - ADR-0027
author: claude
created: 2026-08-29
updated: 2026-08-29
plan_refs:
  - "ADR-0027 移行チェックリスト手順 3（リスニングキュー名にサービス名を前置する）"
---

# 作業仕様書: fan-out 統合テストの待ち合わせを分ける（#1038）

## 目的

`Knowledge.IntegrationTests.Messaging` の fan-out 系 2 件が負荷依存で落ちる（#1038）。
**待ち時間を伸ばす直し方は #1038 が明示的に禁じている**ため、
**①ブローカ接続 ②購読開始 ③実処理** の切り分けを器の側で行う。

## 🔴 本作業で #1038 は閉じない

#1038 の受け入れ基準は 4 つあり、うち 3 つが **Docker のある環境でしか測れない**。

| # | 受け入れ基準 | 本作業での扱い |
| --- | --- | --- |
| ① | fan-out の実到達時間をフル実行時に計測し、記録する | **計測を出力へ載せる実装は入れた。値そのものは `integration.yml` の実走待ち** |
| ② | 遅さの出どころ（接続 / 購読開始 / 実処理）を切り分ける | **器として実装した**（購読開始を別枠の待ち合わせへ切り出した） |
| ③ | 全 70 件のフル実行を複数回回して安定して緑であることを実測する | **未実施**（Docker が無い。skip になる） |
| ④ | `integration.yml` が緑になる | **未確認** |

**「実装したから直った」と書かない。** 本作業は測定と切り分けを可能にするものである。

## 着手前の走査（母集合）

**軸 1 — 30 秒の待ち合わせを持つ箇所**（`git grep -n "FromSeconds(30)" -- src ':!src/ai-stock-trading'`）

🔴 **この走査が本作業で最大の発見を出した。** 変更後の実測は 7 件（変更前も同数。改名しただけ）:

```
Knowledge.IntegrationTests/Fixtures/IntegrationTestFactory.cs:202   o.StartTimeout = TimeSpan.FromSeconds(30);
Knowledge.IntegrationTests/Messaging/DocumentUpdatedFanOutTests.cs:201   ProcessingBudget
Knowledge.IntegrationTests/Messaging/QueueOverrideFanOutTests.cs:206     ProcessingBudget
Platform.Bff.Tests/SessionTokenRefresherTests.cs:22                      （無関係。トークン更新の閾値）
Platform.Shared.Infrastructure.Tests/.../WolverineExtensionsTests.cs:278 （無関係。再試行間隔）
Platform.Shared.Infrastructure/.../MassTransitExtensions.cs:11           （無関係。再試行間隔）
Platform.Shared.Infrastructure/.../WolverineExtensions.cs:39             （無関係。再試行間隔）
```

**当初「4 件・すべて本件の 2 ファイル内」と書いていた。誤りである**（走査せずに書いた）。
引き直した結果、`IntegrationTestFactory.cs:202` が出てきた —— これが根本原因につながる。

**軸 1b — 見つかった `MassTransitHostOptions` の実効性**

`IntegrationTestFactory.cs:195-204` は次のように書いていた:

> WaitUntilStarted=true で レシーブエンドポイントのバインド完了までホスト起動を待機させ、
> **購読確立後に Publish されることを保証する**（Issue #33 の Bus 起動レース対策）

🔴 **この保証は ADR-0027 の Wolverine 移行で失効していた。** 実測:

| ホスト | MassTransit | 実測 | 帰結 |
| --- | --- | --- | --- |
| `WikiService` | **参照していない**（csproj に `PackageReference` が無い） | — | 本設定は**完全な no-op** |
| `IngestionService.Worker` | `AddMassTransit` は在る | `UsingRabbitMq` ＋ `ConfigureEndpoints(ctx)` のみで**コンシューマを 1 つも登録していない** | 待つべきレシーブエンドポイントが無い |
| 両者の `DocumentUpdated` 購読 | — | `AddWolverineStep<DocumentUpdatedConsumer>` / `<DocumentSyncConsumer>` 側にある | `WaitUntilStarted` の射程外 |

**これが #1038 の機序である。** Issue #33 で一度塞いだ起動レースが、購読の実装を
MassTransit から Wolverine へ移したときに**黙って開き直り**、
**「塞いだ」と読める記述だけが残っていた**。#1032 を直してテスト本体へ到達するようになり、
開いていた穴が観測にかかった。

**是正**: 当該コメントを、実際に効いている範囲だけを述べる形へ書き直した（規則 10）。
設定自体は MassTransit を実際に使う経路（ConversionService の発行側）のために残す。

**軸 2 — 実ブローカを使う統合テストのうち「発行 → 副作用」を待つもの**
（`Messaging/` 配下の全 6 ファイルを読む。拡張子・行フィルタで絞らない）

| ファイル | 待ち合わせの形 | 本作業の対象 |
| --- | --- | --- |
| `DocumentUpdatedFanOutTests.cs` | 発行 → 2 購読者の終端副作用を 30 秒 | **対象** |
| `QueueOverrideFanOutTests.cs` | 同上 | **対象** |
| `WolverineBrokerEdgeTests.cs` | 発行 → 受信の確認 | **対象外**（同型の懸念はあるが #1038 は挙げていない。**同型の事故が 2 回起きたら**共通化する、が本リポの規約） |
| `RawDocumentFetchedEdgeTests.cs` | 同上 | 同上 |
| `WolverineBrokerReadinessTests.cs` | readiness の HTTP 応答を待つ | 対象外（ブローカ遮断の試験であり fan-out ではない） |
| `PipelineDeclarationLoadedTests.cs` | 待ち合わせなし | 対象外 |

**除外の理由を明示する**（規則 6）: 後ろ 2 ファイルは待ち合わせの型が違う。中の 2 件は同型だが、
**1 回目は記録に留める**という規約に従い今回は触らない。**フォローアップとして IADR に残した。**

## 器の実測（Docker 不要の範囲で確かめたこと）

記憶で API を使わない。専用の実行可能プロジェクトを作って**実際に走らせた**。

| # | 確かめたこと | 実測 |
| --- | --- | --- |
| 1 | キューエンドポイントの Uri 形 | `RabbitMqTransport.Queues["ingestion-service.DocumentUpdated"].Uri` → `rabbitmq://queue/ingestion-service.DocumentUpdated`（`EndpointName` はキュー名そのもの） |
| 2 | 未知エンドポイントの状態 | `WolverineTracker.StatusFor(uri)` → `ListeningStatus.Unknown` |
| 3 | **期限切れの挙動** | 存在しない Uri へ 2 秒で待つと **2.0 秒後に `TimeoutException`** |

🔴 **3 が決定的である。** この API が「待って諦めて何事もなく返る」型なら、
**購読していないのに緑になる fail-open** を仕込むことになっていた。

## 変更内容

1. `Messaging/ListenerReadiness.cs`（新規）—— キューのリスナーが `Accepting` になるまで待ち、
   **掛かった時間を返す**。期限切れは握り潰さず、状態を添えて投げ直す。
2. 両 fan-out テストの `InitializeAsync` で、**発行ホストを立てる前に**両購読者の
   `Accepting` を待つ。掛かった時間をフィールドへ保持する。
3. テスト本体の 30 秒を **`ProcessingBudget`（実処理だけの予算）へ改名して据え置く。**
4. 実到達時間を `TestOutputHelper` へ出力し、失敗時は assert のメッセージにも載せる。

## 検証（実走）

```
$ export PATH="$PATH:/root/.dotnet"
$ dotnet build src/knowledge/backend/backend.slnx
Build succeeded.  0 Error(s)   3 Warning(s)（既存の MinioBuilder 廃止予告。本変更とは無関係）

$ dotnet test src/knowledge/backend/backend.slnx
Knowledge.Contracts.Tests        Failed 0 / Passed  27
IngestionService.Worker.Tests    Failed 0 / Passed  28
DashboardService.Tests           Failed 0 / Passed  26
ConversionService.Worker.Tests   Failed 0 / Passed  74 / Skipped  2
GraphService.Tests               Failed 0 / Passed 258
FeedbackService.Tests            Failed 0 / Passed  21
WikiService.Tests                Failed 0 / Passed  64
DocumentService.Tests            Failed 0 / Passed 225
DataSourceService.Tests          Failed 0 / Passed 166
RetrievalService.Tests           Failed 0 / Passed 137
AiAnalysisService.Tests          Failed 0 / Passed  95
Knowledge.IntegrationTests       Failed 0 / Passed  34 / Skipped 41

$ dotnet format src/knowledge/backend/backend.slnx --verify-no-changes
（差分なし）
```

🔴 **対象の 2 件は `Skipped` である。** Docker daemon が無いため実走していない。
**「緑」と呼ばない。**

## 🔴 ［2026-08-29 追記 / #1038］**仮説は実測で否定された**

AI レビューの環境には `/var/run/docker.sock` が在り（**過去 7 回のレビューが「Docker は無い」と
記録していたのは誤りだった**）、対象 2 件を実コンテナで走らせた結果が返ってきた。

| 実測（レビュー環境・3 回） | 結果 |
| --- | --- |
| `Knowledge.IntegrationTests` 全件 | **72 件 Passed / 対象 2 件が毎回 FAIL** |
| 単独実行でも同時実行でも | **同じ 2 件が失敗**（30〜34 秒で） |
| `_ingestionReady` / `_wikiReady` | **どちらも 0.0 秒**（購読開始は即座に `Accepting`） |
| 失敗した購読者 | **実行ごとに入れ替わる**（ingestion 側 / wiki 側） |

**つまり本作業の仮説（①② が③の予算を食い潰していた）は誤りである。**
①② を独立させて 0.0 秒で通過してもなお、③ の 30 秒で終端へ届いていない。

### 🔴 これは「失敗」ではなく、切り分けが機能した結果である

#1038 の受け入れ基準②は「**遅さの出どころ（接続 / 購読開始 / 実処理）を切り分ける**」であり、
本作業はそれを果たした —— **①② が原因でないことが測定で確定した。**
仮説が否定されたことと、装置が用途を果たさなかったことは別である。
**待ち時間を伸ばしていたら、この切り分けは永久に得られなかった。**

### 追加で確かめたこと（Docker 不要の範囲。次に着手する者への引き渡し）

レビューが挙げた「**exchange の束縛が publish と競合しているのでは**」という筋を、
Wolverine の実物を反射して確かめた。**3 つとも否定された。**

| # | 疑い | 実測 | 判定 |
| --- | --- | --- | --- |
| 1 | 発行側と購読側でキュー宣言が食い違い `PRECONDITION_FAILED` でチャンネルが落ちる | 両者とも `durable=True / autoDelete=False / exclusive=False / args=[]` で**完全に一致** | ✕ 否定 |
| 2 | `BindQueue` を 2 回呼んでも束縛が 1 本しか残らない | キュー 2 本それぞれに束縛 1 本ずつ（束縛はキュー側 `_bindings` に載る）。**2 本とも生成される** | ✕ 否定 |
| 3 | 自動生成される束縛キー（`<exchange>_<queue>`）が違うので片方にしか届かない | exchange の既定型は **`Fanout`**。**ルーティングキーは無視される** | ✕ 否定 |

**したがってメッセージは両方のキューへ届いているはずであり、残るのは消費側である。**

### 次の仮説（**まだ測っていない。断定しない**）

🔴 **Wolverine のハンドラは実行時に Roslyn でコンパイルされる**（`IADR-0217` が
「事前 codegen ＋ `TypeLoadMode.Static` は採らない」と決めており、`WolverineFx.RuntimeCompilation`
を参照している）。**その代金は「最初の 1 通を受けたとき」に払われる。**

- ホストが 2 つ ＋ Testcontainers の Postgres / RabbitMQ ＋ 他 72 件が同時に走る状況で、
  **どちらか一方のコンパイルが 30 秒に間に合わない**なら、症状は観測と一致する ——
  購読は始まっている（`Accepting` は 0.0 秒）／メッセージは届いている／
  終端の副作用だけが出ない／**どちらが負けるかは実行ごとに変わる**。
- **これは #1038 の受け入れ基準②で言えば「③実処理」ではなく、その手前の第 4 の段である。**
  ①接続 ②購読開始 ③**ハンドラのコンパイル** ④実処理、と読み直す必要がある。

**確かめ方**（Docker のある環境で）: 計測用の 1 通を先に流して終端まで届くのを待ち、
**そのあとで**検査対象の 1 通を時間つきで流す。前者が長く後者が短ければ本仮説が支持される。
`TypeLoadMode.Static` へ切り替えて再測するのも同型の対照になるが、
**`IADR-0217` の決定を覆すことになるので、測るためだけに変えない。**

🔴 **本作業では実装しない。** この環境では測れず、**測らずに 2 つ目の仮説へ賭けることは
#1038 が明示的に禁じている**（「測らずに待ち時間を延ばすだけ」と同じ誤り）。

## やらなかったこと

- **待ち時間の延長**（#1038 が禁じている）
- **リトライ属性の導入**（フレークを隠すだけで遅さの出どころが分からないままになる）
- `WolverineBrokerEdgeTests` / `RawDocumentFetchedEdgeTests` への同型適用
  （1 回目は記録に留める。IADR のフォローアップ 2 に書いた）

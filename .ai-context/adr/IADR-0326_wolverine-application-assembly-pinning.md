---
title: IADR-0326 Wolverine のハンドラ探索アセンブリを段登録の共通ヘルパで明示固定する
type: impl-adr
status: Accepted
related_ids:
  - FR-02
  - FR-13
  - FR-14
  - UC-04
  - UC-07
  - ADR-0018
  - ADR-0027
  - IADR-0232
  - IADR-0234
  - IADR-0239
  - IADR-0245
  - IADR-0302
  - IADR-0305
  - IADR-0314
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - "ADR-0027 移行チェックリスト手順 3（リスニングキュー名にサービス名を前置する）"
  - "ADR-0027 移行チェックリスト手順 6（3〜5 を共通ヘルパへ封じ込める）"
---

# IADR-0326: Wolverine のハンドラ探索アセンブリを共通ヘルパで明示固定する

- 状態: Accepted
- 日付: 2026-08-30
- 決定者: 実装（#1073。#1038 / #1059 の真因）

## 起点・関連

- #1073（CI の `Integration` が失敗している）。前身は #1038 / #1059
- IADR-0305（fan-out の統合テスト 2 クラスを直列化する）—— **覆さないが、原因ではなかったことを確定させる**
- ADR-0027 手順 6（3〜5 を共通ヘルパへ封じ込め、個別サービスでの逸脱を静的検査で禁止する）

## 背景 —— 3 件・6 ラウンドかけて外し続けた

`Messaging.DocumentUpdatedFanOutTests` と `Messaging.QueueOverrideFanOutTests` の 2 件が
非決定的に落ち続けた。症状は毎回同じである。

- 購読開始は **0.0 秒**（発行時点で両購読は `Accepting`）
- 購読キュー名は **2 本に正しく分かれている**（競合コンシューマ化ではない）
- **30 秒の予算を使い切って 1 通も処理されない**
- 落ちる側（ingestion / wiki）が **run ごとに入れ替わる**が、**同一 run の 2 クラスでは同じ側**
- 緑のときは 341ms〜3 秒。**中間が 1 件も無い（二峰分布）**

これまでに除外された仮説: 購読の立ち上がり待ち（#1038）／クラス跨ぎの競合コンシューマ（#1038）／
宣言ファイルの相互汚染（#1038）／同時実行による混雑（#1069 で直列化しても再発）。
「待ち時間を伸ばす」は #1038 が禁じ、二峰分布が無効を裏づけていた。

## 🔴 実測 —— 稼働 k3s の RabbitMQ へ専用 vhost を切って再現させた

本作業環境には Docker daemon が無く Testcontainers が起きない。そこで

1. `PostgresFixture` に `PLATFORM_TEST_POSTGRES` を足し（ブローカ側 `PLATFORM_TEST_RABBITMQ` と対称）、
2. 稼働 k3s の `platform-infra/rabbitmq` と `platform-infra/postgres` を port-forward し、
3. **配備済みサービスと queue を奪い合わないよう専用 vhost `it-local` を切って**

当該 2 件を実走させた。**再現した。**

> ⚠️ 最初の 1 回は vhost を切らずに走らせ、**稼働クラスタの `ingestion-service` /
> `wiki-service` の pod が同じキューを消費して**赤くなった。これは CI の症状とは別物である
> （DLQ に 2 通残った）。**汚染された測定を原因の証拠に使わないため、ここに残す。**

### 事実 1: メッセージは届き、処理され、ack されていた（配送の欠落ではない）

RabbitMQ 管理 API（予算切れ直後）:

```
GET /api/queues/it-local/wiki-service.DocumentUpdated
  message_stats = { "publish":1, "deliver":1, "ack":1, "redeliver":0 }
GET /api/queues/it-local?columns=name,messages,consumers
  wolverine-dead-letter-queue | msgs= 0 | consumers= 0
```

**publish=1 / deliver=1 / ack=1 / redeliver=0 / DLQ 0 件。** これで残る仮説が割れる ——
束縛の欠落なら `publish=0`、ハンドラ例外なら `redeliver>0` か DLQ に残る。
**届いて、ack されて、何も起きていない。**

### 事実 2: 後発ホストが「相手サービスのハンドラ」を拾っていた

ホストのログ（xunit v3 の実行ファイルを直に起動して採取）:

```
warn: Wolverine.Runtime.WolverineRuntime[0]
      Wolverine adopted application assembly 'IngestionService' for handler discovery,
      but this host was registered from 'WikiService'. The application assembly is a
      process-wide value pinned by whichever host started FIRST in this process (GH-3521),
      so handler discovery will NOT scan 'WikiService'.
info: Wolverine.Configuration.HandlerDiscovery[0]
      Searching assembly IngestionService ... for Wolverine message handlers   ← wiki ホストなのに
fail: Wolverine.Runtime.WolverineRuntime[0]
      System.NotSupportedException: Handler type
        IngestionService.Features.Ingestion.Ingest.DocumentUpdatedConsumer
        does not have a suitable, public constructor for Wolverine or is missing registered dependencies
```

逆コンパイル（Wolverine 6.24.4）でも裏が取れた。

```csharp
public static Assembly? RememberedApplicationAssembly;   // WolverineOptions の static フィールド

private void establishApplicationAssembly(string? assemblyName) {
    ...
    else if (RememberedApplicationAssembly != null) { ApplicationAssembly = RememberedApplicationAssembly; }
    else { ApplicationAssembly = determineCallingAssembly(); RememberedApplicationAssembly = ...; }
}
// 呼ばれるのは _applicationAssembly == null のときだけ（＝明示設定は尊重される）
```

## 機序（4 段。どこにも例外は残らない）

1. 1 プロセスで複数サービスのホストを立てると、**最初のホストがアプリケーションアセンブリを
   プロセス全体に固定する**
2. 後発ホストのハンドラ探索は**相手のアセンブリ**を走査し、`DocumentUpdated` のチェーンに
   **相手サービスのコンシューマ**が入る
3. 受信時、そのハンドラの依存が後発ホストの DI に無いため executor の生成が
   `NotSupportedException` で落ちる
4. Wolverine は `fail:` を 1 行出して**メッセージを ack し捨てる**。再配信も DLQ も無い。
   **同じチェーンに居る正しいハンドラも道連れで走らない**

これで観測事実がすべて説明される。

| 観測 | 説明 |
| --- | --- |
| 落ちる側が run ごとに入れ替わる | どのホストが「最初」になるかで決まる |
| 同一 run の 2 クラスでは同じ側 | 固定値は**プロセス全体で 1 つ**。1 度決まれば両クラスで同じ |
| 二峰分布（341ms か 30 秒） | ハンドラが在るか、在らずに何も起きないかの 2 値 |
| キューは正しく `Accepting` | トポロジは最初から正しかった |
| DLQ も再配信も無い | ack して捨てるため |
| 直列化（IADR-0305）で直らない | 固定は最初のホスト起動時に済んでおり、実行窓とは無関係 |

**本番は 1 サービス = 1 プロセスなので当たらない。当たるのは統合テストである。**
ただし帰結は「テストが遅い」ではなく **「fan-out の退行を検出するために書かれたテストが、
その退行を検出できない状態で赤くなっていた」** であり、検出力の欠損である。

## 決定

1. **`WolverinePipelineExtensions.AddPlatformWolverineStep<TStep>` で
   `options.ApplicationAssembly = typeof(TStep).Assembly` を明示設定する（規則 0）。**
   置き場所は「ハンドラ探索を設定している当の場所」である —— 同メソッドは既に
   `Discovery.IncludeType<TStep>()` と `Discovery.CustomizeHandlerDiscovery(...)` を持つ。
   ADR-0027 手順 6（共通ヘルパへの封じ込め）とも一致する。
2. **`Discovery.IncludeAssembly` は使わない。** あれは走査対象を**足す**ので、相手のアセンブリが
   残り続ける。置き換わるのは `ApplicationAssembly` だけである。
3. **`ProcessingBudget`（30 秒）は 1 秒も触らない。** IADR-0305 と同じ判断を引き継ぐ。
4. **退行防止を 2 つ置く**（同型の事故が 3 回起きたため）。
   - `WolverinePipelineExtensionsTests` の**決定的な単体試験** 3 件（宣言あり／なし／`enabled:false` の
     3 経路すべてで探索アセンブリが固定されること。**陽性対照として固定前が `null` であることを先に表明する**）
   - **追随試験**: `WolverineOptions.RememberedApplicationAssembly` が在り続けることを見る。
     消えた（＝ Wolverine 側がホスト単位に直した）ら落ち、規則 0 の要否を測り直させる
5. **失敗メッセージへ「ハンドラ探索アセンブリ」を載せる**（`ListenerReadiness.DescribeHandlerDiscovery`）。
   6 ラウンドを費やした理由は、症状が「配送の欠落」と見分けられなかったことである。
   実測: 修正を外した状態で `ingestion=IngestionService / wiki=IngestionService` と出て、
   **1 行で原因が読める。**
6. **`PostgresFixture` に `PLATFORM_TEST_POSTGRES` を足す**（`RabbitMqFixture` と対称・fail-closed）。
   ブローカだけ外部化されていて DB が外部化されていなかったため、Docker の無い環境では
   fan-out が**1 行も走らず**、原因調査が CI の実走だけに依存していた。これが 6 ラウンドの下地である。

## 因果の裏づけ（変異）

同一手順（vhost を作り直し・新しい DB 名）で、修正の 1 行だけを外して比較した。

| 状態 | 結果 |
| --- | --- |
| 修正あり（3 回） | **2 件とも合格** / 5.2 s・19.5 s・3.9 s |
| 修正を外す（規則 0 の 1 行をコメント化） | **2 件とも失敗** / 76 s（＝ 30 秒 × 2 の予算切れ） |

**相関ではなく、当該 1 行が結果を反転させる。**

## 検討して採らなかった案

| 案 | 却下理由 |
| --- | --- |
| 予算を 60 秒へ伸ばす | #1038 が禁じ、二峰分布が無効を示している。そもそも原因ではない |
| 器（`IntegrationTestFactory`）の側だけで固定する | 本番コードが**プロセス共有の推測値**に依存している事実は残る。明示のほうが強い |
| `Discovery.IncludeAssembly` を足す | 相手のアセンブリが走査対象に残るので直らない（決定 2） |
| `UsePlatformMessagingDefaults` へ置く | 型引数が無くアセンブリを導けない。引数を足すと全サービスの呼び出しが変わる |
| IADR-0305（直列化）を撤回する | **撤回しない。** 相関が交絡だったことは確定したが、取り除くことを支える新しい実測は無い。コストは実行時間だけであり、測らずに戻すのは測らずに入れるのと同じ損なわれ方である |
| 発行専用ホスト（DocumentService 等）へも広げる | それらはリスナーを持たず、今日の被害は無い。**起きていないものへ手を入れない。** リスナーを持つ経路は必ず `AddPlatformWolverineStep` を通る（ADR-0027 手順 3 の封じ込め） |

## 射程外・申し送り

- 発行専用ホスト（`DocumentService` / `DataSourceService`）は探索アセンブリが他所のままだが、
  リスナーが無いため消費は起きない。**将来これらが Wolverine ハンドラを持つなら規則 0 の適用点が要る。**
- 本 IADR は IADR-0305 を**覆さない**。あれは「混雑は原因ではない」を証拠つきで確定させ、
  本 IADR の切り分けを可能にした。

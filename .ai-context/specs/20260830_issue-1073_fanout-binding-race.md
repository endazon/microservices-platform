---
title: 作業仕様書 — fan-out 統合テスト 2 件が CI で落ち続ける原因を実測で特定し、原因側を直す
type: spec
status: done
related_ids:
  - FR-02
  - FR-13
  - FR-14
  - UC-04
  - UC-07
  - ADR-0018
  - ADR-0027
  - IADR-0232
  - IADR-0245
  - IADR-0302
  - IADR-0305
  - IADR-0314
  - IADR-0320
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - "ADR-0027 移行チェックリスト手順 3（リスニングキュー名にサービス名を前置する）"
  - "ADR-0027 移行チェックリスト手順 6（3〜5 を共通ヘルパへ封じ込める）"
---

# 作業仕様書: #1073（Integration の fan-out 2 件が直列化後も落ちる）

## 結論の先出し

**真因は Wolverine の `ApplicationAssembly` がプロセス全体で共有されることである。**
1 プロセスで 2 サービスのホストを立てる統合テストでは、**後発ホストが相手サービスのハンドラを拾い、
自分のハンドラを拾わない**。受信したメッセージは依存未解決で executor 生成に失敗し、
**例外も再配信もデッドレターも残さず ack されて捨てられる**。

対処は共通ヘルパ `AddPlatformWolverineStep<TStep>` での明示固定（1 行）。
**`ProcessingBudget`（30 秒）は 1 秒も触っていない。**
判断と実測は [`IADR-0320`](../adr/IADR-0320_wolverine-application-assembly-pinning.md)。

## 対象

- `Knowledge.IntegrationTests.Messaging.DocumentUpdatedFanOutTests.PublishOnce_BothSubscribersReceive`
- `Knowledge.IntegrationTests.Messaging.QueueOverrideFanOutTests.SharedQueueDeclaration_KeepsFanOut_ServicePrefixSeparatesQueues`

## 出発点（先行作業が確かめたこと・確かめられなかったこと）

#1038 / #1059 は PR #1069（`4a39e7c8`）で「2 クラスの直列化」＋「予算切れ時に購読キュー名を載せる」を
入れて閉じられた。**しかしその PR は「直った」と主張していない** —— 本作業環境に Docker が無く、
判定は develop への push 後の実走に委ねられていた。

**その実走（run `33309205241` / `beaeb9e4`）が同じ 2 件で落ちた**（`Total 77 / Passed 74 / Failed 2 / Skipped 1`）。
つまり #1069 の主目的（「同時実行による混雑」を証拠つきで除外する）は達せられた ——
**混雑は原因ではない。** 直列化そのものは残す（IADR-0320 §検討して採らなかった案）。

## 🔴 母集合の引き方（規則 9・10）と除外

### 走査 1 —— 「exchange への発行が束縛の成立前に走り得る」形

```
grep -rn "DeclareExchange|BindQueue|ToRabbitExchange" --exclude-dir=node_modules --exclude-dir=.git .
  | grep -v "^./src/ai-stock-trading/"
```

当たり 20 行（拡張子で絞らず、パス除外だけで取った＝規則 3）。実装コードは 4 ファイル:

| 箇所 | 扱い |
| --- | --- |
| `Messaging/DocumentUpdatedFanOutTests.cs` / `QueueOverrideFanOutTests.cs` | **本件の対象** |
| `Fixtures/WolverineBrokerEdge.cs`（2 購読先へ fan-out） | 🔴 **陽性対照として重要**。同じ形なのに**赤い run でも常に緑**（run 33309205241 で 2 s / Passed）。<br>違いは「購読ホストが**同一アセンブリ（Knowledge.IntegrationTests）内のハンドラ**を使う」ことであり、<br>これが真因（探索アセンブリ）の切り分けを支えた。**触らない** |
| `Fixtures/RawDocumentFetchedEdge.cs` | 同上（単一購読者）。未発火。**触らない** |
| `Services/*/Program.cs` の `BindPlatformQueue<T>`（#1113 で新設） | 本件と独立。二重宣言（本番 exchange ＋ テスト固有 exchange）が新たな失敗を生んでいないことを<br>実測で確認した（`Declared a Rabbit Mq binding ...` が両方出て、いずれも成功） |

### 走査 2 —— 「外部エンドポイントで Testcontainers を迂回する」経路の非対称

```
grep -rn "PLATFORM_TEST_" ... → 当たり 1 行（RabbitMqFixture.cs のみ）
```

🔴 **ブローカだけが外部化されていて DB は外部化されていなかった。**
`PostgresFixture.IsAvailable` が false になるため、Docker の無い環境では
**ブローカを外から与えても fan-out は 1 行も走らない**。これが「CI でしか再現できない」状態を作り、
6 ラウンドの下地になった。→ 対称に `PLATFORM_TEST_POSTGRES` を足した（IADR-0320 決定 6）。

### 走査 3 —— 「複数の Wolverine ホストを 1 プロセスで立てる」箇所（真因が効く範囲）

```
grep -rn "UseWolverine|AddPlatformWolverineStep" --include=*.cs src/ | grep -v ai-stock-trading
```

本番の `UseWolverine` は 6 サービス。うち **`AddPlatformWolverineStep` を呼ぶ購読側は 5 つ**
（Conversion / Graph / Ingestion / Retrieval / Wiki）。

**除外したもの**: `DocumentService` / `DataSourceService` は発行専用で `AddPlatformWolverineStep` を
呼ばない。探索アセンブリは他所のままになるが、**リスナーを持たないので消費が起きない**
（実測: `local3.log` で両者は `Knowledge.IntegrationTests` を走査したが、キューを 1 本も購読していない）。
**起きていない事象へ予防的に手を入れない**（#1038 が禁じた作法と同型）。申し送りは IADR-0320 §射程外。

## 実測

### 環境

Docker daemon 無し（Rancher Desktop / containerd）。稼働 k3s の `platform-infra` を port-forward:

```
kubectl -n platform-infra port-forward svc/rabbitmq 5672:5672 15672:15672
kubectl -n platform-infra port-forward svc/postgres 55432:5432
```

🔴 **最初の 1 回は vhost を切らずに `/` へ繋ぎ、稼働クラスタの `ingestion-service` /
`wiki-service` pod が同じキューを消費して**赤くなった（DLQ に 2 通）。**これは CI の症状ではない。**
以後は専用 vhost `it-local` を切り、**測定ごとに作り直した**。

### 再現（vhost 分離・修正前）

```
CI=true PLATFORM_TEST_RABBITMQ=amqp://guest:guest@localhost:5672/it-local \
PLATFORM_TEST_POSTGRES='Host=localhost;Port=55432;Database=...' \
  ./Knowledge.IntegrationTests.exe -method '*FanOut*'
→ Total: 2, Failed: 2   （CI と同じ症状: 0.0s Accepting / キュー名正常 / 30 秒使い切り）
```

### 切り分け（RabbitMQ 管理 API）

```
GET /api/queues/it-local/wiki-service.DocumentUpdated
  message_stats = {"publish":1,"deliver":1,"ack":1,"redeliver":0}
wolverine-dead-letter-queue | msgs=0
```

→ **配送はされている。ack もされている。DLQ も再配信も無い。**
仮説 A（束縛の欠落）と B（ハンドラ例外）は**どちらも否定**された。

### 原因（ホストログ）

```
warn: Wolverine adopted application assembly 'IngestionService' ... but this host was
      registered from 'WikiService' ... pinned by whichever host started FIRST (GH-3521)
info: HandlerDiscovery: Searching assembly IngestionService ...      ← wiki ホストなのに
fail: System.NotSupportedException: Handler type IngestionService...DocumentUpdatedConsumer
      does not have a suitable, public constructor for Wolverine or is missing registered dependencies
```

Wolverine 6.24.4 の逆コンパイルで `WolverineOptions.RememberedApplicationAssembly`（public static）を確認。

### 変異（因果の裏づけ）

同一手順（vhost 作り直し・新しい DB 名）で修正の 1 行だけを外す:

| 状態 | 結果 |
| --- | --- |
| 修正あり（3 回） | **2 件とも合格** / 5.2 s・19.5 s・3.9 s |
| 修正を外す | **2 件とも失敗** / 76 s |

### 診断の実効

修正を外した状態での失敗メッセージ（新設の診断）:

```
ハンドラ探索アセンブリ: ingestion=IngestionService / wiki=IngestionService
```

**1 行で原因が読める。**

## 🔴 ローカルで測るときに踏んだ罠（次に測る人へ）

外部の**永続**インフラを使うため、CI（クラスごとに新品のコンテナ）と条件が違う。

1. **クラスタの配備済みサービスが同じキューを消費する** → 専用 vhost を切る
2. **前回の実行が残したメッセージを次の実行が食う** → 測定ごとに vhost を作り直す
3. **`Pages` の Slug は Title 由来なので、同じ DB を使い回すと 2 回目で
   `IX_Pages_Slug` の一意制約に当たる** → 測定ごとに新しい DB 名を使う
4. 全名前空間を一度に走らせると 3 が他クラスにも当たる（`relation "Tags" already exists` 等）。
   **これらはローカル固有であり、CI の赤ではない**

## 変更点

| ファイル | 変更 |
| --- | --- |
| `Foundation/Pipeline/WolverinePipelineExtensions.cs` | 規則 0: `options.ApplicationAssembly = typeof(TStep).Assembly`（**本体の修正はこの 1 行**） |
| `Platform.Shared.Infrastructure.Tests/.../WolverinePipelineExtensionsTests.cs` | 決定的な退行試験 3 件 ＋ Wolverine 追随試験 1 件 |
| `Knowledge.IntegrationTests/Messaging/ListenerReadiness.cs` | `DescribeHandlerDiscovery`（失敗メッセージへ探索アセンブリを載せる） |
| `Messaging/DocumentUpdatedFanOutTests.cs` / `QueueOverrideFanOutTests.cs` | 診断の追加、および**直列化では直らなかった**旨の日付つき追記 |
| `Messaging/FanOutTestCollection.cs` | 相関が交絡であったことの日付つき追記（**直列化は残す**。理由も明記） |
| `Fixtures/PostgresFixture.cs` | `PLATFORM_TEST_POSTGRES`（Rabbit 側と対称・fail-closed） |

## 退行防止をどう置くか（同型 3 回目の判断）

規約は「同型の事故が 2 回起きたら検査器・規約を足す」である。#1038 / #1059 / #1073 で**3 回目**なので置く。
ただし**置き方を選ぶ**。

- ✅ **決定的な単体試験**（`WolverinePipelineExtensionsTests`、ミリ秒・ブローカ不要）。
  規則 0 を消せば必ず落ちる。**陽性対照**（固定前が `null` であること）を対で置く
- ✅ **Wolverine への追随試験**（`RememberedApplicationAssembly` が在り続けること）。
  Wolverine 側が直したら落ち、規則 0 の要否を測り直させる
- ✅ **失敗メッセージの情報量**（探索アセンブリ）。6 ラウンドの直接原因は「症状が配送の欠落と
  見分けられなかった」ことである
- ❌ **新しい `scripts/*.js` の検査器は足さない。** 守るべき不変条件は「共通ヘルパが
  `ApplicationAssembly` を設定していること」で、これは C# の試験で直接・厳密に見られる。
  文字列走査の検査器を重ねても弱い写しにしかならず、必読規約の予算（50KB）も消費する
- ❌ **`ProcessingBudget` を触らない。** 待ち時間の水増しは #1038 が禁じている

## 受け入れ基準

- [x] 原因を証跡つきで特定する（管理 API の `message_stats` ＋ ホストログ ＋ 逆コンパイル ＋ 変異）
- [x] `ProcessingBudget`（30 秒）を 1 秒も伸ばさない
- [x] 退行防止の置き方を判断し、理由とともに記録する
- [ ] CI の `Integration` が実走で緑になる（**develop への push 後に判定する**）

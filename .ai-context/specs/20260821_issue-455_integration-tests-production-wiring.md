---
title: 作業仕様書 — 統合テストを本番配線で走らせる（#455 Phase 0 / U0a）
type: spec
status: done
related_ids:
  - ADR-0027
  - ADR-0030
  - UC-04
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - "ADR-0027（メッセージング基盤 = Wolverine。移行チェックリスト手順 3・7・8）"
related_adrs:
  - IADR-0219
  - IADR-0231
issue: "#455"
---

# 作業仕様書: 統合テストを本番配線で走らせる（#455 Phase 0 / U0a）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: `ADR-0027`（メッセージング基盤）—— 移行チェックリスト **手順 3**（リスニングキュー名に
  サービス名を前置する）／**手順 7**（移行後、対応表が保存されていることを**実ブローカで**検査する）／
  **手順 8**（実ブローカ結合テストを完了条件に含める）
- 実装 issue: `#455` / `#441`

## なぜ要るのか —— 現状の統合テストは何も証明していない

`IntegrationTestFactoryBase.ConfigureWebHost` は次を行っている。

```csharp
// MassTransit: Program.cs が AddMassTransit() 済みのためアセンブリ単位で全削除してから再登録
RemoveAllMassTransitServices(services);          // ← サービス自身の配線を全部捨てる
services.AddMassTransit(x => {
    RegisterConsumers(x);                        // ← テストが自分で列挙し直す
    x.UsingRabbitMq((ctx, cfg) => { cfg.Host(...); cfg.ConfigureEndpoints(ctx); });
});
```

**捨てているもの**: `AddPlatformPipelineStep`（段の宣言照合）・`UsePlatformRetry`（リトライ / DLQ）・
`AddPlatformIntrospection`。つまり**本番の配線は 1 行も通っていない**。

🔴 **最も重い帰結**: `DocumentUpdated` の購読者は **IngestionService と WikiService の 2 つ**だが、
**登録されるのは `RegisterConsumers` が明示列挙した 2 つだけ**（`DocumentNormalizedConsumer` と
`DocumentSyncConsumer`）である。**`DocumentUpdatedConsumer`（Ingestion）は統合テストに載っていない。**
したがって **2 購読者が同時に生きている状態を作るテストが存在しない** ——
移行手順 3 を誤って competing consumer 化しても、**試験する場所が無い**。

## 着手前の実測

| 項目 | 実測 |
| --- | --- |
| 統合テストの基準 | **43 / 43 通過**（本作業前に実走して確認） |
| `RegisterConsumers` を override する factory | **2**（Document / Wiki） |
| 本番で `IConsumer` を実装する段 | **5**（Conversion / Document / Ingestion / Wiki ×2） |
| 統合テストに載っている段 | **2 / 5** |
| `Knowledge.IntegrationTests.csproj` の `ProjectReference` | **7**（Worker は **0 件**） |

### 🟢 Worker はホストできる（前提の訂正）

当初「Worker は `WebApplicationFactory` でホストできない」と見立てたが**誤りだった**。
両 Worker とも `WebApplication.CreateBuilder` を使い、末尾に

```csharp
// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
```

を**既に持っている**。欠落は 2 点だけである。

1. `Knowledge.IntegrationTests.csproj` に Worker への `ProjectReference` が無い
2. 両者が公開するのは**グローバル名前空間の `Program`** なので、2 つ参照すると**型が衝突する**
   —— Api 系 5 サービスが `TestMarker.cs` を持つのはこのためである

したがって U0b は **既存パターンの踏襲**であり、新しい仕組みではない。

### 🟢 本番配線は接続先を config から読む

```csharp
cfg.Host(builder.Configuration["RabbitMq:ConnectionString"] ?? "amqp://guest:guest@rabbitmq:5672");
```

`IntegrationTestFactoryBase` は **すでに `RabbitMq:ConnectionString` を Testcontainers 向けに
上書きしている**。したがって「捨てるのをやめる」だけで本番配線が Testcontainers を向く。

🔴 **ただし `ConfigureAppConfiguration` の上書きが `Program.cs` の評価時点で見えているかは
実測で確かめる。** minimal hosting model では `Program` の builder と factory のコールバックの
順序が自明でない。見えていなければ既定の `amqp://guest:guest@rabbitmq:5672` へ繋ぎに行って
失敗するので、**落ち方でわかる**。

### 🟢 段の宣言が無くても登録される

`AddPlatformPipelineStep` は `pipeline.Steps.Count == 0` のとき
**「Pipeline config absent; step enabled by default」として `AddConsumer<TConsumer>()` する**
（`PipelineExtensions.cs:81-88`）。統合テストは `Pipeline:ConfigPath` を設定しないため
この分岐に入り、**本番配線のままコンシューマが登録される**。

## スコープ

### U0a — 本番配線を捨てるのをやめる

- `RemoveAllMassTransitServices()` と自前 `AddMassTransit` を削除する
- 用済みになる `RegisterConsumers` の override 2 件と virtual 宣言も削除する
- 🔴 **`MassTransitHostOptions.WaitUntilStarted = true` は残す。** issue #33 のバス起動レース対策で、
  消すと `CreateClient()` 直後の `Publish` がキューバインド完了前に走り**メッセージが破棄され得る**

### スコープ外（別 PR）

- 🔴 **U0b（Worker 2 つを統合テストに載せる）は本 PR から外した。** 着手して**構造的な障害**が
  分かったためである。`IntegrationTestFactoryBase<TProgram, TDbContext>` は
  **`where TDbContext : DbContext` を要求する**が、**`IngestionService.Worker` は DbContext を
  持たない**（実測 0 件。`ConversionService.Worker` は持つ）。載せるには
  **DbContext を要求しない基底を切り出す**リファクタが要り、U0a（配線を捨てるのをやめる）とは
  別の関心である。**U0a だけで独立して価値があり、検証も済んでいる**ので分けた
- **U0c**（`DocumentUpdated` の 2 購読者同時受信テスト）—— **U0b に従属する**。
  2 購読者は IngestionService と WikiService に分かれており、Worker を載せないと書けない
- **U0d**（`Pipeline:ConfigPath` を実 `pipeline.json` へ向ける）—— U0a は「本番の配線コードを通す」
  ところまでで、**`pipeline.json` の段宣言・`queue` 上書きは依然として通っていない**。
  これは**残る穴として明示する**（黙って「本番配線を通した」と言わない）
- **U3 / U4 / U5** —— Phase 0 の残りと Phase 1

## 受け入れ基準

1. `IntegrationTestFactory.cs` に `RemoveAllMassTransitServices` が存在しない
2. **既存 43 テストが緑のまま**（1 件も減らない・落ちない）
3. `dotnet build|test` 両ユニットが **Failed 0**、件数が減っていない
4. 検査器一式・`scripts.test.js` が EXIT=0
5. `dotnet format --verify-no-changes` が両ユニットで EXIT=0

🔴 **2 が破れたら、それは「本番配線では通らない」という発見である。**
**テストを緩めて通さない。** 原因を突き止めて記録する。

## 変異試験

| 変異 | 期待 |
| --- | --- |
| (a) `WaitUntilStarted` を消す | 起動レースでイベント系テストが不安定になる（#33 の再現。**落ちなければ「消してよい」ではなく、レースが顕在化しにくいだけ**と解釈する） |
| (b) `RabbitMq:ConnectionString` の上書きを消す | 既定ホストへ繋ぎに行って失敗する（＝上書きが効いていることの裏返し） |

**復旧を確認し、復旧したことを報告に含める。**

## 母集合（規則 9・10）

**是正後に「テストが本番配線を通っていない」と書いた自分の記述を引き直した。**

## 実装後に確定した結果

| 項目 | 実測 |
| --- | --- |
| 統合テスト（本番配線） | **43 / 43 通過**（基準と同数。1 件も減っていない） |
| 所要時間 | **57 秒 → 38 秒**（テスト自前のバス構築が消えた分） |
| 削除した仕組み | `RemoveAllMassTransitServices` / `IsFromMassTransitAssembly` / `RegisterConsumers`（virtual ＋ override 2 件） |

### ✅ 実測で確かめた仮説 2 件

1. **`ConfigureAppConfiguration` の上書きは `Program.cs` の評価時点で見えている。**
   見えていなければ既定の `amqp://guest:guest@rabbitmq:5672` へ繋ぎに行って失敗するはずだが、
   **43 件すべてが通った**。minimal hosting model の順序は自明でなかったので、
   推論ではなく落ち方で確かめる方針にしていた。
2. **段の宣言が無くてもコンシューマは登録される。** `AddPlatformPipelineStep` の
   `pipeline.Steps.Count == 0` 分岐が効いている（効いていなければ購読が消えてイベント系が落ちる）。

### 🔴 残る穴（黙って「本番配線を通した」と言わない）

- **`Pipeline:ConfigPath` は設定していない。** よって `pipeline.json` の**段宣言・`queue` 上書きは
  依然として通っていない**。本 PR が通したのは `AddMassTransit` / `AddPlatformPipelineStep` /
  `UsePlatformRetry` / `AddPlatformIntrospection` という**配線コード**までである
- **`DocumentUpdated` の 2 購読者が同時に生きる状態はまだ作れない**（U0b / U0c）

### 規則 10 —— この変更で新たに誤りになる自分の記述

| 場所 | 従前 | 是正 |
| --- | --- | --- |
| `docs/tech/tech-requirements.md`「Wolverine 移行の前提」 | 「統合テストは現状この判定に使えない…**本番配線を通す作り替えが先に要る**」 | 作り替えを**実施した**。ただし `pipeline.json` は未通過であり、2 購読者同時のテストも未成立であることを**残る穴として**書き直す |

**除外したもの（理由つき）:**

- **Phase 0 の前作業仕様書**（`20260821_issue-455_wolverine-phase0-preconditions.md`）—— **凍結記録**であり、
  執筆時点の事実として正しい。訂正の参照点は live 側（`docs/`）に 1 つ置く（[[IADR-0141]]）

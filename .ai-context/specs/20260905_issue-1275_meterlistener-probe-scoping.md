---
title: MeterListener の probe を Meter インスタンスで絞り、共有 Meter の他テストの測定を拾わないようにする
type: spec
status: done
related_ids:
  - NFR
  - FR-10
  - FR-11
  - ADR-0006
  - ADR-0044
  - ADR-0076
  - IADR-0110
  - IADR-0378
  - IADR-0389
  - IADR-0394
author: claude
created: 2026-09-05
updated: 2026-09-05
---

# 作業仕様書: 共有 Meter を購読する probe の絞り込み（#1275）

## 背景

`MeterListener` は **Meter 名でプロセス全体の測定を購読する**。`LlmUsageMetrics.MeterName` は
`LlmCompletionMetrics.MeterName` と同じ定数（`microservices-platform.llm-gateway`）であり、
production のコードが共有している。したがって Meter 名だけで絞る probe は、**同じ Meter へ発行する
別のテストクラスの測定を拾う**。

`CompletionEndpointCollection`（[IADR-0110](../adr/IADR-0110_llm-completion-stop-reason-metrics.md) / #395）は
この危険を認識して直列化しているが、**加入規則が「補完エンドポイントを叩くクラス」であり、
危険の範囲（「共有 Meter へ発行するクラス」）より狭い**。`LlmUsageMetricsTests` は
`[Collection]` を持たず、`RecordUsage` で同じ Meter へ `llm.tokens.total` を発行する。

#1259 が穴を作ったのではない。穴は前から在り、#1259 が**共有 Meter の計器に対する初めての
不在（`NotContain`）の表明**を足したことで観測可能になった。

## 🔴 再現した（直す前に落ちることを見た）

issue の時点では「非決定的・develop では 3 回とも緑」だったが、**並列度を上げると 5/5 で再現する**。
一時変更はコミットしない（xUnit v3 の CLI 引数のみで、ファイルは触っていない）。

```
cd src/platform/backend/Services/LlmGateway/Tests/bin/Debug/net10.0
./LlmGateway.Tests.exe \
  -class LlmGateway.Tests.Common.Observability.LlmSyntheticUsageExclusionTests \
  -class LlmGateway.Tests.Common.Observability.LlmUsageMetricsTests \
  -parallel all -maxThreads unlimited -noColor
```

`LlmSyntheticUsageExclusionTests.PostCompleteStream_WhenSynthetic_ExcludesFromCostAndCountsExclusion`
が **Failed: 1（5 回中 5 回）**。混入した測定の値が原因を確定させる ——
`llm.tokens.total` = **1000 / 500 / 1000000**、`llm.cost.total` = 0.0105 / 0.00018 は
いずれも `LlmUsageMetricsTests` が `RecordUsage` へ渡した値そのものである。

## 母集合（規則 9。**誤りの側の文字列で走査してから挙げた**）

走査: `grep -rl "MeterListener" --include='*.cs' src/` → **12 ファイル**（うち 1 つは
`CompletionEndpointCollection.cs` のコメントのみ）。各ファイルについて
**① 何で購読を絞っているか × ② 不在（`NotContain` / `BeEmpty` / `ContainSingle` / `Single`）を
表明しているか**を読み、**「production の Meter 名で絞る」かつ「不在を表明する」**ものだけが
危険であると判定した。

| # | テストクラス | 購読の絞り方 | 不在の表明 | 判定 |
| --- | --- | --- | --- | --- |
| 1 | `AiAnalysisService` `AskStreamFirstTokenMetricsTests` | **Meter インスタンス**（`ReferenceEquals`。自分の DI 容器から解決） | `BeEmpty` / `HaveCount(1)` | 安全（**先例**） |
| 2 | `DocumentService` `IngestTagFilterTests` | Meter 名が**テストごとに一意**（`{name}.test-{guid}`） | `ContainSingle` 他 | 安全 |
| 3 | `DocumentService` `PrivateNoteNotificationDispatchTests` | 同上（一意な Meter 名） | `HaveCount` 他 | 安全 |
| 4 | `GraphService` `KnowledgeHealthReportMetricsTests` | **production の Meter 名**＋計器名 | `BeEmpty` ×2 / `ContainSingle` | 🔴 **危険（本 PR で是正）** |
| 5 | `GraphService` `TagDictionaryEnforcementTests` | 一意な Meter 名 | — | 安全 |
| 6 | `GraphService` `LinkEdgeSyncTests` | 一意な Meter 名 | — | 安全 |
| 7 | `RetrievalService` `QdrantCjkNgramSearchTests` | **Meter の Scope**（`instrument.Meter.Scope == factory`） | — | 安全 |
| 8 | `RetrievalService` `QdrantFullTextIndexObservabilityTests` | 同上（Scope） | — | 安全 |
| 9 | `LlmGateway` `CompletionMetricsTests` | production の Meter 名＋計器名 | `ContainSingle` 多数 | 条件付き安全（下記） |
| 10 | `LlmGateway` `LlmSyntheticUsageExclusionTests` | production の Meter 名＋計器名 | `NotContain` ×3 | 🔴 **本 issue の失敗** |
| 11 | `LlmGateway` `LlmUsageMetricsTests` | production の Meter 名**のみ**（計器名すら絞らない） | `NotContain` / `Single` / `HaveCount(2)` | 🔴 **危険（加害者かつ被害者）** |

### #9 が「条件付き安全」である根拠（**陽性対照つき**）

`CompletionMetricsTests` が購読するのは `llm.completion.total` と
`llm.completion.output_tokens` の 2 本だけである。この 2 本を発行するのは補完エンドポイントだけであり、
**それを叩くテストクラスは 7 つ**（`grep -rl '"/complete' src/platform/backend/Services/LlmGateway/Tests/`）、
**7 つとも `[Collection]` を持つ**（実測）。よって現状は直列化で守られている。

**陽性対照**: 同じ走査手順を #10・#11 へ当てると、`LlmUsageMetricsTests` が
`[Collection]` を持たないことを検出する —— すなわち走査は「加入していないクラス」を実際に見つける。
「7 つとも加入している」は走査漏れではない。

### #4 が危険であることの根拠（**陽性対照つき**）

`KnowledgeHealthReportMetricsTests`（Unit・`[Collection]` なし）は Meter 名
`microservices-platform.graph-service` ＋ 計器名 `knowledge.health.report.total` で購読し、
`Measurements.Should().BeEmpty()` を 2 回表明する。

**陽性対照**: 同じ計器を発行する別クラスを走査で挙げた ——
`KnowledgeHealthProducerTests`（Integration・`[Collection]` なし）が `NewReportMetrics()` を
**5 箇所**で `HttpKnowledgeHealthReporter` へ渡し、そのうち
`送出のパスと本文は指標名と観測値だけで構成される` は既定応答 `HttpStatusCode.Accepted` を返す
`FakeIngressHandler` を使うため **`metrics.RecordDelivered(indicator)` に到達する**
（`HttpKnowledgeHealthReporter` は `IsSuccessStatusCode` のときだけ数える）。
別容器の `IMeterFactory` から作っても **Meter 名は同じ**なので、名前で絞る probe は拾う。
LlmGateway と**同型**であり、まだ観測されていないだけである。

🔴 **ただし GraphService 側は再現していない。** 同じ並列度引き上げ（`-parallel all -maxThreads unlimited`）を
この 2 クラスへ当てたが **3 回とも緑**だった（発行の窓が短く、重ならない）。
よって GraphService の是正は「**同型の欠陥として直した**」であり、「落ちていたものを直した」ではない。
**機構は決定的に示した** —— 同型の回帰試験を足し、変異（名前で絞る版へ戻す）で
`999999` を拾って落ちることを実測した。

## 決定（詳細は [IADR-0394](../adr/IADR-0394_meter-probe-instance-scoping.md)）

1. **購読は Meter の「インスタンス」で絞る（主）。** `instrument.Meter` は Meter オブジェクトを
   返すので、**インスタンスでは区別できないという issue の前提は誤りである**（実測で反証した）。
   `IMeterFactory` は容器ごとに別の Meter インスタンスを作るため、自分の容器から解決した
   Meter と同一インスタンスの計器だけを購読すれば、他クラスの発行は構造的に入らない。
   **本リポジトリに先例が 3 件ある**（母集合 #1・#7・#8）。
2. **タグは捨てずに保持する。** 絞り込みの主軸ではないが、混入が起きたときに
   「どのテストの発行か」を失敗メッセージから読めるようにするため、および回帰試験が
   タグで検証できるようにするため。
3. **production へテスト専用の Meter 名注入口は作らない**（過剰な抽象化・計画外）。
   production の振る舞いは 1 行も変えない。
4. **コレクションの加入規則を危険の範囲へ言い直す（多層防御）。**
   `CompletionEndpointCollection` → `SharedMeterCollection` へ改名し、規則を
   「共有 Meter へ発行するクラス」とする。`LlmUsageMetricsTests` を加える。

## 受け入れ基準

- [x] Given probe / When 他のクラスが同じ Meter 名へ発行する / Then `Items` に入らない
- [x] Given 陰性対照 / When 当該テスト自身の発行を止める / Then 表明が落ちる（絞り込みが
      「何も拾わない」に退化していない）
- [x] Given 変異試験 / When 絞り込みを外す / Then 再現した失敗が戻る
- [x] Given `dotnet test .../LlmGateway.Tests.csproj` / When 連続 3 回 / Then 3 回とも緑
- [x] production コードの差分は 0 行

## 影響ファイル（並列判定に使う宣言）

- `src/platform/backend/Services/LlmGateway/Tests/**`
- `src/knowledge/backend/Services/GraphService/Tests/Common/Observability/KnowledgeHealthReportMetricsTests.cs`
- `.ai-context/adr/IADR-0110_llm-completion-stop-reason-metrics.md`（日付つき追記のみ）
- `.ai-context/adr/IADR-0394_meter-probe-instance-scoping.md`（新規）・`.ai-context/adr/README.md`

## 除外理由

- 母集合 #2・#3・#5・#6 は Meter 名自体がテストごとに一意であり、**インスタンス絞りを足す価値が無い**
  （すでに同値の保護である）。触らない。
- #1・#7・#8 は既にインスタンス／Scope で絞っており、触らない。
- #9 は直列化で守られており、購読計器の発行元が補完エンドポイントに閉じている。
  **改名した規則の下でも加入したままである**ため差分は改名の追随のみ。

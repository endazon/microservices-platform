---
title: 作業仕様書 — IngestionService.Worker.Tests を xUnit1051 から剥がす（実測 33 箇所）（#882）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - ADR-0027
  - IADR-0238
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
related_specs:
  - "../adr/IADR-0238_xunit1051-staged-adoption-ratchet.md"
  - "20260822_issue-882_feedbackservice-xunit1051.md"
issue: "#882"
---

# 作業仕様書 — IngestionService.Worker.Tests を xUnit1051 から剥がす

## 目的と射程

実測の昇順で次（**報告 33 箇所・2 ファイル**）。`Refs #882`（`Closes` は最後の 1 本だけ）。

起点 ID の置き方は
[`20260822_issue-882_dashboardservice-xunit1051.md`](20260822_issue-882_dashboardservice-xunit1051.md)
の同名節が正本。

## 🔴 これまでと呼び出しの種類がまったく違う

過去 6 本はすべて **HTTP クライアント呼び出し**（＋ 1 本だけ自ドメインのメソッド）だったが、
本プロジェクトの 31 箇所は **MassTransit の `ITestHarness`** である。

| ファイル | 件数 | 呼び出し |
| --- | ---: | --- |
| `DocumentUpdatedConsumerTests.cs` | 31 | `harness.Bus.Publish` 10 / `harness.Consumed\|Published.Any<T>` 11 / `harness.Stop` 10 |
| `IntrospectionEndpointTests.cs` | 2 | `GetAsync` / `ReadFromJsonAsync`（従来型） |

### 🔴 `harness.Start()` は**触ってはいけない**

同じハーネスの `Start()` は **11 箇所あるがアナライザは 1 つも報告しない**
（`CancellationToken` を受けるオーバーロードを持たない）。
**「同じオブジェクトの似たメソッドだから」で足すとコンパイルエラーになる。**
報告された行だけを対象にし、置換後に `Start(TestContext` が **0 件**であることを確認した。

### 🔴 `Any` は LINQ の `Any()` と名前が衝突する

置換器のメソッド集合を恒久的に広げず、**この回だけ環境変数で `Publish,Any,Stop` を渡した**。
事前に本プロジェクト内の `.Publish(` / `.Stop(` がすべて対象ファイルに閉じていること、
`Any` の出現がハーネス経由のものだけであることを走査で確かめてある。
**HTTP 系の既定集合に `Any` を混ぜると、他プロジェクトで LINQ の `Any()` を壊す。**

## ラムダの盲点が**ローカル関数**でも再現した

[`20260822_issue-882_feedbackservice-xunit1051.md`](20260822_issue-882_feedbackservice-xunit1051.md)
で「`.Select(...)` のラムダ内はアナライザが報告しない」を記録したが、
本プロジェクトでは **ローカル関数**で同じことが起きた:

```csharp
async Task<List<Guid>> IngestOnce()          // ← ローカル関数
{
    ...
    await harness.Bus.Publish(SampleEvent());                      // 124: 報告されない
    (await harness.Consumed.Any<DocumentUpdated>()).Should().BeTrue();  // 125: 報告されない
    ...
    finally { await harness.Stop(); }                              // 128: 報告されない
}
```

**報告 33 に対し置換は 36**（差の 3 がこれ）。前回と同じ理由で**この 3 箇所も移行した**
（同一ファイル内で一部だけ渡さない状態は、後から漏れか意図か判別できない）。

🔴 **盲点は「ラムダ」ではなく「テストメソッド本体で直接 `await` されていない文脈」である。**
ラムダ・ローカル関数の 2 形で実測した。`remaining: 0` は
**そのプロジェクトの全呼び出しがトークンを渡していることを意味しない**。

## Wolverine 移行との関係（着手前に確認した）

本プロジェクトは `scripts/backend-library-baseline.json` に **MassTransit 残存**として載っており
（`IngestionService.Worker` / `.Tests` の 2 行）、いずれ `ADR-0027` で Wolverine へ移る。
着手前に確認した結果:

- **Wolverine 移行の open PR は無い**、直近の develop にも該当コミットは無い
- 関連する open issue は #921（トランスポート ratchet の追加）だけで、**本テストの書き換えではない**

したがって `Platform.Shared.Infrastructure.Tests` のようなゲートは不要と判断した。
**将来 Wolverine 化で本ファイルが書き換わっても、ratchet が「書き換え後も 0 件」を強制する**ので、
今剥がしておく価値は失われない。

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| 33 箇所が移行済み | ✅ 再測定で**一覧から消えた**。knowledge 合計 **438 → 405**（ちょうど −33） |
| 置換の総数が説明できる | ✅ **36 = 報告 33 ＋ ローカル関数 3**。先頭カンマの壊れた形は 0 件 |
| `Start()` を触っていない | ✅ 11 箇所すべて素のまま（`Start(TestContext` が 0 件） |
| **再発したらビルドが落ちる** | ✅ M-1（`error xUnit1051` / exit 1） |
| **テスト件数が減らない** | ✅ **28 → 28**（属性数も develop と一致） |
| ローカル関数を触ったテストが通る | ✅ `Consumer_ShouldUseDeterministicChunkIds_AcrossReingestion` 単独で Passed |
| 他プロジェクトの残件が変わらない | ✅ 他 5 プロジェクトの実測が baseline と一致 |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |

### 変異試験

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-1 | 引数ゼロだった `Any<IngestionCompleted>()` を 1 箇所戻す | **ビルド失敗** | **`error xUnit1051` 2 行 / exit 1** |
| M-2 | baseline を `migrated:true` のまま `remaining: 33` へ戻す | `migrated-nonzero` | **exit 1・当該 1 件のみ** |
| M-3 | props の許可リストから外す | `props-desync` | **exit 1・当該 1 件のみ** |

復旧は `cmp` でバイト一致を確認した。

## 申し送り

残件 **855 → 822**。移行済み 9 本。次は `RetrievalService.Api.Tests`（47）。

- **置換器のメソッド集合をプロジェクトごとに決める。** 既定（HTTP 系）へ `Any` のような
  汎用名を恒久追加しない
- **報告されないメソッド（本件の `Start()`）に足さない。** 「同じ型の似たメソッド」は根拠にならない
- **報告数と置換数の差は必ず特定する。** 本 PR ではローカル関数の盲点の発見につながった

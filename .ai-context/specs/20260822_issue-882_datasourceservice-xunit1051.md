---
title: 作業仕様書 — DataSourceService.Api.Tests を xUnit1051 から剥がす（実測 75 箇所・衝突が最も多い）（#882）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - IADR-0238
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
related_specs:
  - "../adr/IADR-0238_xunit1051-staged-adoption-ratchet.md"
  - "20260822_issue-882_wikiservice-xunit1051.md"
issue: "#882"
---

# 作業仕様書 — DataSourceService.Api.Tests を xUnit1051 から剥がす

## 目的と射程

実測の昇順で次（**報告 75 箇所・8 ファイル**）。`Refs #882`（`Closes` は最後の 1 本だけ）。
起点 ID の置き方は
[`20260822_issue-882_dashboardservice-xunit1051.md`](20260822_issue-882_dashboardservice-xunit1051.md)
の同名節が正本。

## 🔴 これまでで最も衝突が多いプロジェクトである

呼び出しの種類が 4 系統に分かれ、**そのうち 2 つは自動置換から外した**。

| 系統 | 件数 | 扱い |
| --- | ---: | --- |
| HTTP クライアント（`GetAsync` / `PostAsync` / `PostAsJsonAsync` / `PutAsJsonAsync` / `PatchAsJsonAsync` / `DeleteAsync` / `SendAsync` / `ReadFromJsonAsync` / `ReadAsStringAsync`） | 46 | 自動 |
| 自ドメイン `svc.SyncAsync(source)` | 13 | 自動（`ct` が最後・手前に省略可能引数なし） |
| MassTransit `harness.Published.Select<T>()` / `.Any<T>(filter)` | 6 + 2 | **`Select` は手で当てた**（後述） |
| EF Core `db.DataSources.FindAsync(id)` / `db.SaveChangesAsync()` | 5 + 1 | **`FindAsync` は手で当てた**（後述） |

### 🔴 `Select` を自動置換から外した —— LINQ が 21 箇所ある

`.Select` の出現は **27 件**だが、`CancellationToken` を取るのは
**MassTransit の `harness.Published.Select<RawDocumentFetched>()` の 6 件だけ**である。
残り **21 件は LINQ の `.Select(lambda)`** で、引数を足せば壊れる。

```csharp
return harness.Published.Select<RawDocumentFetched>(TestContext.Current.CancellationToken)  // ← 対象
    .Select(x => x.Context.Message)                                                          // ← LINQ。触らない
```

**同じ 1 文の中に両方が現れる。** 置換器のメソッド集合から `Select` を外し、
`harness.Published.Select<RawDocumentFetched>()` という**完全一致の文字列で 6 件だけ**手で当てた
（件数を assert）。適用後に `\.Select([a-z] =>.*TestContext` が **0 件**であることを走査で確認した。

**#951 で `Any` の同型の事故を起こしているので、今回は着手前にレシーバを 1 件ずつ数えた。**
（`Any` は本プロジェクトでは 2 件ともハーネスで、LINQ の `.Any(` は 0 件だったので自動で足した。）

### 🔴 `FindAsync` を自動置換から外した —— `params object[]` へ黙って滑る

`db.DataSources.FindAsync(id)` に位置引数でトークンを足すと `FindAsync(id, token)` になる。
EF Core の `DbSet` は 2 つのオーバーロードを持つ:

```csharp
FindAsync(params object?[]? keyValues)
FindAsync(object?[]? keyValues, CancellationToken cancellationToken)
```

`(Guid, CancellationToken)` は 2 つ目に**normal form では一致しない**（`Guid` は `object[]` ではない）ため、
**params の展開形へ滑って `new object[] { id, token }` を複合キーとして解釈**し得る。
**コンパイルは通るが、実行時に「そんなキーの行は無い」で null が返る**——**静かに壊れる型**である。

配列形にして normal form で束縛させた:

```csharp
await db.DataSources.FindAsync([id], TestContext.Current.CancellationToken);
```

🔴 **コンパイルが通ったことは束縛が正しい証拠にならない。** 実証はテストで取った ——
`FindAsync` の戻り値へアサートする `DataSourceUpdateEndpointTests` の **13 件が全て Passed**
（params へ滑っていれば null が返って落ちる）。

### テストダブルの宣言（4 ファイル・14 個）は無傷

`DataSourceSyncServiceTests.cs`（`DiscoverAsync` / `FetchAsync` を持つスタブ 5 つ）、
`DataSourceSyncHostedServiceTests.cs`（`TryAcquireAsync`）、
`DatabaseConnectorTests.cs`（`OpenAsync` / `ReadAsync`）。
#949 で入れた**「先頭 `.`（メンバアクセス）必須」**が効き、適用後も宣言はすべて素のまま。

## 報告 75 に対し置換 82（差の 7 はすべて private ヘルパ）

[[#946]] の盲点（形 3）である。3 ファイルに分かれて 7 件:

| ファイル | 件数 | 位置 |
| --- | ---: | --- |
| `DataSourceSyncEndpointTests.cs` | 4 | `FirstPublishedForAsync` / 別の private ヘルパ |
| `DataSourceUpdateEndpointTests.cs` | 2 | 先頭の private ヘルパ（作成 → JSON 取得） |
| `SyncScheduleTests.cs` | 1 | private ヘルパ |

前回までと同じ理由で**この 7 件も移行した**（同一ファイル内で一部だけ渡さない状態を作らない）。

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| 75 箇所が移行済み | ✅ 再測定で**一覧から消えた**。knowledge 合計 **307 → 232**（ちょうど −75） |
| 置換の総数が説明できる | ✅ **82 = 報告 75 ＋ private ヘルパ 7**。先頭カンマの壊れた形は 0 件 |
| **LINQ の `.Select` を壊していない** | ✅ `\.Select([a-z] =>.*TestContext` が **0 件**（21 箇所すべて素のまま） |
| **`FindAsync` が正しいオーバーロードへ束縛** | ✅ ビルド成功 ＋ **戻り値へアサートする 13 件が Passed** |
| **テストダブルの宣言が無傷** | ✅ 4 ファイル・14 個すべて素のまま |
| **再発したらビルドが落ちる** | ✅ M-1（`error xUnit1051` / exit 1） |
| **テスト件数が減らない** | ✅ **133 → 133**（属性数も develop と一致: 145 / 145） |
| 他プロジェクトの残件が変わらない | ✅ 他 2 プロジェクトの実測が baseline と一致 |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |

### 変異試験

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-1 | **最も厄介な `FindAsync` の配列形**を 1 箇所戻す | **ビルド失敗** | **`error xUnit1051` 2 行 / exit 1** |
| M-2 | baseline を `migrated:true` のまま `remaining: 75` へ戻す | `migrated-nonzero` | **exit 1・当該 1 件のみ** |
| M-3 | props の許可リストから外す | `props-desync` | **exit 1・当該 1 件のみ** |

### 検証

`dotnet build`（Release）**0 エラー**（警告は既存 `CS0618` 2 件のみ）／
`dotnet test` knowledge **701 件・0 失敗**（🔴 **`--filter "Category!=Integration"` 付き**）／
`scripts.test.js` **597 件 all passed**。

## 申し送り

残件 **657 → 582**。移行済み 13 本。**残るは knowledge 2 本と platform 3 本**。

🔴 **`ConversionService.Worker.Tests`（138）と `DocumentService.Api.Tests`（94）は
MassTransit を使う**（`backend-library-baseline.json` に残存として載る）。
本 PR と同じく **`Select` / `Any` の LINQ 衝突**が出る見込みなので、着手前に
**レシーバを 1 件ずつ数えてから**メソッド集合を決めること。

- `Platform.Bff`（232）は **#439 の 3a と衝突する**ので着手前に要相談
- `LlmGateway`（114）は platform ユニットなので AST submodule の初期化が要る

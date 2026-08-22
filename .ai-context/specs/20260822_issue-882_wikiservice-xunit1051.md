---
title: 作業仕様書 — WikiService.Api.Tests を xUnit1051 から剥がす（実測 51 箇所）（#882）
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
  - "20260822_issue-882_retrievalservice-xunit1051.md"
issue: "#882"
---

# 作業仕様書 — WikiService.Api.Tests を xUnit1051 から剥がす

## 目的と射程

実測の昇順で次（**報告 51 箇所・6 ファイル**）。`Refs #882`（`Closes` は最後の 1 本だけ）。
起点 ID の置き方は
[`20260822_issue-882_dashboardservice-xunit1051.md`](20260822_issue-882_dashboardservice-xunit1051.md)
の同名節が正本。

## 着手前の call site 読み

| ファイル | 件数 | 呼び出し |
| --- | ---: | --- |
| `DocumentDeleteArchiveSyncTests.cs` | 15 | MassTransit harness（`Publish` / `Any` / `Stop`） |
| `DocumentSyncConsumerTests.cs` | 13 | 同上 |
| `WikiEndpointsAbacTests.cs` | 10 | HTTP |
| `WikiJsGraphQlClientTests.cs` | 6 | 自ドメイン（`UpsertPageAsync` / `ArchivePageAsync` / `DeletePageAsync` / `GetRenderedContentAsync`） |
| `PipelineRecomposeTests.cs` | 5 | MassTransit harness |
| `HealthEndpointTests.cs` | 2 | HTTP |

**同居するテストダブルが 4 ファイルに在る**（前回 `RetrievalService` で手順に加えた確認）。
`IWikiJsClient` 等を実装し、`UpsertPageAsync(WikiJsPage page, CancellationToken ct = default)` の
ような**宣言**を計 20 個ほど持つ。置換器の「先頭 `.` 必須」（#949 で入れた）が効き、
**宣言はすべて無傷**であることを適用後に確認した（`CancellationToken ct = default` が各ファイル 5 件ずつ残存）。

## 🔴 LINQ の `Any()` に引数を足す不具合を作り込み、件数照合で捕まえた

`IngestionService`（#943）で「`Any` は LINQ と衝突するので恒久的に既定へ入れない」と書いたのに、
**本 PR で実際にその衝突を踏んだ。**

```csharp
// 置換器が壊した形（Queryable.Any(predicate) に第 2 引数は無い）
db.Pages.Any(p => p.DocumentId == DocId, TestContext.Current.CancellationToken).Should().BeFalse();
```

`DocumentDeleteArchiveSyncTests.cs` と `DocumentSyncConsumerTests.cs` に **1 箇所ずつ、計 2 箇所**。

### なぜ事前の走査で防げなかったか

`Publish` / `Any` / `Stop` の**メンバアクセス出現数**（17 / 7 / 11）は数えたが、
**`Any` の 7 件それぞれがハーネス呼び出しかどうかを分解しなかった。**
実際は 5 件がハーネス（`harness.Consumed.Any<T>()`）、**2 件が LINQ**（`db.Pages.Any(predicate)`）だった。

🔴 **「件数を数えた」と「1 件ずつ確かめた」は違う。** #943 では
`.Publish(` / `.Stop(` が対象ファイルに閉じていることを確認して満足し、
`Any` については同じ粒度の確認をしていなかった。本 PR で同じ手を繰り返した。

### 何が捕まえたか

**報告 51 に対し置換 54** という差を追ったこと。内訳を出すと:

- `WikiJsGraphQlClientTests.cs:67` … `var act = () => Build(handler).UpsertPageAsync(Page());`
  → **ラムダ**（既知の盲点。移行してよい）
- `DocumentDeleteArchiveSyncTests.cs:109` / `DocumentSyncConsumerTests.cs:146`
  → **LINQ の `Any`**（不具合。戻す）

**ビルドでも落ちた**（`Any` に 3 つ目の引数を渡すオーバーロードは無い）が、
**件数照合のほうが先に、かつ原因つきで**教えてくれた。
ビルドエラーだけだと「どの `Any` が悪いか」を突き止める作業が要る。

2 箇所を戻し、**52 = 報告 51 ＋ ラムダ 1** に収束させた。

## 手順（器が強制する 3 点セット）

1. テストの `.cs` を直す（51 箇所 ＋ ラムダ 1 ＝ 52）
2. `scripts/xunit1051-baseline.json` を `remaining: 0` / `migrated: true`
3. `src/Directory.Build.props` の `XUnit1051Migrated` へ追加

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| 51 箇所が移行済み | ✅ 再測定で**一覧から消えた**。knowledge 合計 **358 → 307**（ちょうど −51） |
| 置換の総数が説明できる | ✅ **52 = 報告 51 ＋ ラムダ 1**（LINQ の 2 件は戻した） |
| **LINQ の `Any` に token が付いていない** | ✅ `.Any(.*TestContext` が 0 件 |
| **テストダブルの宣言が無傷** | ✅ `CancellationToken ct = default` が 4 ファイルに 5 件ずつ残存 |
| **再発したらビルドが落ちる** | ✅ M-1（`error xUnit1051` / exit 1） |
| **テスト件数が減らない** | ✅ **39 → 39**（属性数も develop と一致: 40 / 40） |
| LINQ を含むテストが通る | ✅ `DocumentSyncConsumerTests` 7 件すべて Passed |
| 他プロジェクトの残件が変わらない | ✅ 他 3 プロジェクトの実測が baseline と一致 |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |

### 変異試験

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-1 | **ハーネスの** `Any<DocumentDeleted>()` を 1 箇所戻す | **ビルド失敗** | **`error xUnit1051` 2 行 / exit 1** |
| M-2 | baseline を `migrated:true` のまま `remaining: 51` へ戻す | `migrated-nonzero` | **exit 1・当該 1 件のみ** |
| M-3 | props の許可リストから外す | `props-desync` | **exit 1・当該 1 件のみ** |

### 検証

`dotnet build`（Release）**0 エラー**（警告は既存 `CS0618` 2 件のみ）／
`dotnet test` knowledge **677 件・0 失敗**（🔴 **`--filter "Category!=Integration"` 付き**）／
`scripts.test.js` **584 件 all passed**。

## 申し送り

残件 **775 → 724**。移行済み 11 本。次は `AuthorizationService.Api.Tests`（74。**platform ユニット**）。

🔴 **`Any` のような汎用名を置換器へ渡すときは、出現数ではなく 1 件ずつレシーバを確かめる。**
本 PR で「数えただけ」が不十分であることを実証した。確かめ方:

```
grep -n "\.Any[<(]" *.cs      # レシーバ（harness.Consumed / db.Pages …）を目で見る
```

- **報告数と置換数の差は 4 回連続で何かを教えている**（3 回は盲点、1 回は自分の不具合）。
  差が出たら必ず 1 件ずつ特定する

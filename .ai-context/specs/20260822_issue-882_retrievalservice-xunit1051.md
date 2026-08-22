---
title: 作業仕様書 — RetrievalService.Api.Tests を xUnit1051 から剥がす（実測 47 箇所）（#882）
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
  - "20260822_issue-882_ingestionservice-xunit1051.md"
issue: "#882"
---

# 作業仕様書 — RetrievalService.Api.Tests を xUnit1051 から剥がす

## 目的と射程

実測の昇順で次（**報告 47 箇所・5 ファイル**）。`Refs #882`（`Closes` は最後の 1 本だけ）。
起点 ID の置き方は
[`20260822_issue-882_dashboardservice-xunit1051.md`](20260822_issue-882_dashboardservice-xunit1051.md)
の同名節が正本。

## 着手前の call site 読み

| ファイル | 件数 | 呼び出し |
| --- | ---: | --- |
| `HybridSearchEndpointTests.cs` | 19 | HTTP（`PostAsJsonAsync` / `ReadFromJsonAsync` / `ReadAsStringAsync`） |
| `HybridSearchServiceTests.cs` | 17 | 自ドメイン `svc.SearchAsync` |
| `TagFilteringTests.cs` | 7 | 自ドメイン `store.SearchAsync` / `store.KeywordSearchAsync` |
| `HealthEndpointTests.cs` | 2 | HTTP |
| `IntrospectionEndpointTests.cs` | 2 | HTTP |

**署名を確認した結果、自ドメインの 3 メソッドはいずれも `ct` が最後で、手前に省略可能引数を持たない**
（`AiAnalysisService` の `AskAsync` と違い、**位置指定での追加が可能**）:

```csharp
Task<List<SearchResultDto>> SearchAsync(SearchRequest request, CancellationToken ct = default);
Task<List<SearchResultDto>> SearchAsync(float[] queryVector, int topK,
    IReadOnlyList<AttributeFilter>? filters, CancellationToken ct = default);
Task<List<SearchResultDto>> KeywordSearchAsync(string query, int topK,
    IReadOnlyList<AttributeFilter>? filters, CancellationToken ct = default);
```

## 🔴 置換器がテストダブルの**メソッド宣言**を壊しかけた

`HybridSearchServiceTests.cs` は `IVectorStore` を実装する**テストダブル**を同居させており、
**メソッド宣言**を持つ:

```csharp
public Task<List<SearchResultDto>> SearchAsync(          // 20 行: 宣言
public Task<List<SearchResultDto>> KeywordSearchAsync(   // 28 行: 宣言
```

置換器の正規表現はメソッド名から `(` を探すだけだったので、**宣言にも引数を足してしまう**。
着手前に `grep "Task<.*> SearchAsync"` で宣言の存在を確かめて気付いた。

**対処: 先頭の `.`（メンバアクセス）を必須にした。** 対象の呼び出しはすべて
`svc.` / `store.` / `client.` / `resp.Content.` のいずれかであり、宣言は `.` を伴わない。
自己試験へ宣言のケースを 3 件足して **13/13** 通過を確認してから適用した。

🔴 **これは「先に全部読む」でしか見つからない。** 件数 assert は
「宣言 2 件も置換された」を**多い側のズレとして**しか見せず、原因が分かりにくい。
実際 `SearchAsync` の出現は 23 で、うち 2 件が宣言だった。

## 🔴 アナライザの盲点が **3 つ目の形**で出た（private ヘルパ）

置換 **49** に対し報告は **47**。差の 2 は `HybridSearchEndpointTests.cs` の
**private static ヘルパメソッド**の中だった:

```csharp
private static async Task<AttributeValuesResponse> ListValuesAsync(
    TestWebApplicationFactory factory, string key, AccessScope? scope)
{
    var resp = await factory.CreateClient()
        .PostAsJsonAsync("/search/attribute-values", new AttributeValuesRequest(key, scope));  // 報告されない
    resp.StatusCode.Should().Be(HttpStatusCode.OK);
    return (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>())!;                 // 報告されない
}
```

これまでの 2 形（`.Select(...)` のラムダ / ローカル関数）と合わせて **3 形**である。
**統一的な規則は「`[Fact]` / `[Theory]` が付いたテストメソッドの本体だけを見る」**と読める ——
ラムダ・ローカル関数・別メソッドは、いずれも**別のメソッド本体**だからである。

前 2 回と同じ理由でこの 2 箇所も移行した（同一ファイル内で一部だけ渡さない状態を作らない）。
**独立 issue #946 に 3 形目として追記する。**

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| 47 箇所が移行済み | ✅ 再測定で**一覧から消えた**。knowledge 合計 **405 → 358**（ちょうど −47） |
| 置換の総数が説明できる | ✅ **49 = 報告 47 ＋ private ヘルパ 2**。先頭カンマの壊れた形は 0 件 |
| **テストダブルの宣言を壊していない** | ✅ 20 / 28 行は素のまま |
| **再発したらビルドが落ちる** | ✅ M-1（`error xUnit1051` / exit 1） |
| **テスト件数が減らない** | ✅ **71 → 71**（属性数も develop と一致: 76 / 76） |
| private ヘルパを触ったテストが通る | ✅ `AttributeValues*` 9 件すべて Passed |
| 他プロジェクトの残件が変わらない | ✅ 他 4 プロジェクトの実測が baseline と一致 |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |

### 変異試験

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-1 | **自ドメイン** `svc.SearchAsync` を 1 箇所戻す | **ビルド失敗** | **`error xUnit1051` 2 行 / exit 1** |
| M-2 | baseline を `migrated:true` のまま `remaining: 47` へ戻す | `migrated-nonzero` | **exit 1・当該 1 件のみ** |
| M-3 | props の許可リストから外す | `props-desync` | **exit 1・当該 1 件のみ** |

### 検証

`dotnet build`（Release）**0 エラー**（警告は既存 `CS0618` 2 件のみ）／
`dotnet test` knowledge **677 件・0 失敗**（🔴 **`--filter "Category!=Integration"` 付き**。
PR CI の `backend-build` と同じ条件）／`scripts.test.js` **584 件 all passed**。

## 申し送り

残件 **822 → 775**。移行済み 10 本。次は `WikiService.Api.Tests`（51）。

- **テストダブル（同居する実装クラス）の有無を必ず確認する。** 宣言に引数を足すと壊れる
- **報告数と置換数の差は必ず特定する。** 3 回連続で盲点の発見につながっている

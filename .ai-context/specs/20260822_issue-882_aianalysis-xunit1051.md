---
title: 作業仕様書 — AiAnalysisService.Api.Tests を xUnit1051 から剥がす（実測 30 箇所）（#882）
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
  - "20260822_issue-882_dashboardservice-xunit1051.md"
issue: "#882"
---

# 作業仕様書 — AiAnalysisService.Api.Tests を xUnit1051 から剥がす

## 目的と射程

実測の昇順で次（**30 箇所・8 ファイル**）。`Refs #882`（`Closes` は最後の 1 本だけ）。

起点 ID の置き方（件名を無採番 `NFR` ＋ `IADR-0238` にする理由）は
[`20260822_issue-882_dashboardservice-xunit1051.md`](20260822_issue-882_dashboardservice-xunit1051.md)
の同名節が正本（**同じ判断を各仕様書へ複写しない**）。

## 🔴 これまでの 4 本と違い、**判断を要する箇所がある**

`DashboardService.Api.Tests`（24 箇所）は「引数を 1 つ後ろへ足すだけ」で済んだが、
**本プロジェクトは 2 種類が混在する**。着手前に全 30 箇所を目視し、次のとおり分類した。

### 分類 A: HTTP クライアント呼び出し（**18 箇所**）—— 機械的

`PostAsJsonAsync` / `GetAsync` / `ReadFromJsonAsync` / `ReadAsStringAsync`。
`CancellationToken` が**最後の引数**なので、末尾へ足すだけで解決する。

| ファイル | 件数 | 行 |
| --- | ---: | --- |
| `AnalysisEndpointTests.cs` | 7 | 14, 18, 31, 40, 51, 54, 62 |
| `AskAttributeFilterTests.cs` | 5 | 125, 147, 154, 166, 170 |
| `AskStreamEndpointTests.cs` | 2 | 14, 20 |
| `HealthEndpointTests.cs` | 2 | 13, 20 |
| `IntrospectionEndpointTests.cs` | 2 | 21, 24 |

### 分類 B: 自ドメインのメソッド呼び出し（**12 箇所**）—— **名前付き引数が要る**

`IRagOrchestrator` の 2 メソッドは、**`ct` の前に省略可能引数 `attributeFilters` を持つ**:

```csharp
Task<AiAnswerDto> AskAsync(string question, string userId,
    Dictionary<string, string> userAttributes,
    Dictionary<string, List<string>>? attributeFilters = null,
    CancellationToken ct = default);

IAsyncEnumerable<AskEvent> AskStreamAsync(string question, string userId,
    Dictionary<string, string> userAttributes,
    Dictionary<string, List<string>>? attributeFilters = null,
    [EnumeratorCancellation] CancellationToken ct = default);
```

テストはいずれも `attributeFilters` を省略して呼んでいるため、**末尾へ位置指定で足せない**。

| ファイル | 件数 | 行 | 呼び出し |
| --- | ---: | --- | --- |
| `RagOrchestratorDegradedModelTests.cs` | 6 | 22, 46, 72, 96, 125, 151 | `AskAsync` |
| `RagOrchestratorStopReasonTests.cs` | 5 | 22, 35 / 52, 72, 94 | `AskAsync` 2 / `AskStreamAsync` 3 |
| `RagOrchestratorScopeTests.cs` | 1 | 51 | `AskStreamAsync`（複数行） |

**採った方針: 名前付き引数 `ct:` を使う。**

```csharp
// 採用
await orchestrator.AskAsync("質問", "user-1", new Dictionary<string, string>(),
    ct: TestContext.Current.CancellationToken);

// 採らなかった
await orchestrator.AskAsync("質問", "user-1", new Dictionary<string, string>(),
    null, TestContext.Current.CancellationToken);
```

**位置指定で `null` を挟む案を採らなかった理由**:

- 読み手が `null` を `attributeFilters` へ**自分で対応づける**必要があり、
  「範囲フィルタを明示的に無しにした」という**意味を持つ変更に見えてしまう**。
  本作業はテストの意味を変えないことが前提である
- **署名が変わったとき静かに壊れる。** `attributeFilters` と `ct` の間に省略可能引数が
  1 つ増えれば、位置指定の `null` は別の引数へ滑る。名前付きなら**コンパイルエラーになる**
- `AskStreamAsync` は `[EnumeratorCancellation]` を持つので、渡した `ct` は
  `await foreach` の取り消しへ正しく伝播する（`.WithCancellation()` は不要）

🔴 **分類 B は「引数を足すだけ」ではない。** 渡したトークンは**実装の中まで流れる**。
ただし本テスト群が使う `RagOrchestrator` は `ThrowingHttpClientFactory` 等のスタブで
即座に例外／既定応答へ落ちる経路であり、**トークンを観測する前に完了する**。
受け入れ基準「テスト件数が減らない・結果が変わらない」で担保する。

## 手順（器が強制する 3 点セット）

1. テストの `.cs` を直す（30 箇所）
2. `scripts/xunit1051-baseline.json` を `remaining: 0` / `migrated: true`
3. `src/Directory.Build.props` の `XUnit1051Migrated` へ追加

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| 30 箇所が移行済み | ✅ `-p:NoWarn=` 付き再測定で**一覧から消えた**。knowledge 合計 **498 → 468**（ちょうど −30） |
| **再発したらビルドが落ちる** | ✅ M-1（`error xUnit1051` / exit 1） |
| **テスト件数が減らない** | ✅ **68 → 68**（属性数も develop 版と一致） |
| **テストの結果が変わらない**（分類 B の懸念） | ✅ 失敗 0・スキップ 0 のまま。分類 B を含む 3 ファイルも全件 Passed |
| 他プロジェクトの残件が変わらない | ✅ 他 7 プロジェクトの実測が baseline と一致 |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |

### 変異試験

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-1 | **分類 B** の 1 箇所（名前付き引数）を元へ戻す | **ビルド失敗** | **`error xUnit1051` / exit 1** |
| M-2 | baseline を `migrated:true` のまま `remaining: 30` へ戻す | `migrated-nonzero` | **exit 1・当該 1 件のみ** |
| M-3 | props の許可リストから外す | `props-desync` | **exit 1・当該 1 件のみ** |

M-1 に**分類 B を選んだ**のは、位置指定で足せない側こそ回帰したときに気付きにくいためである。

## 🔴 途中で作り込んだ不具合（ビルドが捕まえた）

分類 A の置換を「末尾の `)` を削って `, <token>)` を足す」という一律の規則で書いたところ、
**引数ゼロの呼び出しで壊れた**:

```csharp
// 元
await response.Content.ReadFromJsonAsync<AiAnswerDto>()
// 生成された壊れた形（先頭にカンマ）
await response.Content.ReadFromJsonAsync<AiAnswerDto>(, TestContext.Current.CancellationToken)
```

`error CS0839: 引数がありません` が **7 件**（`ReadFromJsonAsync` 5 / `ReadAsStringAsync` 2）。
`( )` が空のときは**カンマを付けてはいけない**。

- **ビルドが即座に捕まえた**（`WarningsAsErrors` ではなく通常のコンパイルエラー）。
  検査器も変異試験も、この型は見ない —— **コンパイラだけが防壁である**
- 修正後、`TestContext.Current.CancellationToken` の総数が **30 のまま**であることを再確認した
  （壊れた形を直す過程で件数が動いていないこと）

**残りのプロジェクトでも同じ規則を使うなら、引数ゼロの呼び出しを別扱いにすること。**
`ReadFromJsonAsync<T>()` / `ReadAsStringAsync()` / `ReadAsStreamAsync()` が典型である。

## 申し送り

残件 **915 → 885**。移行済み 7 本。
次は実測の昇順で `FeedbackService.Api.Tests`（30）→ `IngestionService.Worker.Tests`（33）。

**分類 B のような「自ドメインのメソッドで `ct` の前に省略可能引数がある」形は、
残りのプロジェクトにも現れうる。** 着手前に必ず署名を読み、位置指定で足せるかを確かめること。

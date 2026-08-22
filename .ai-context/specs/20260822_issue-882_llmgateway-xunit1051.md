---
title: 作業仕様書 — LlmGateway.Api.Tests を xUnit1051 から剥がす（実測 114 箇所）（#882）
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
  - "20260822_issue-882_datasourceservice-xunit1051.md"
issue: "#882"
---

# 作業仕様書 — LlmGateway.Api.Tests を xUnit1051 から剥がす

## 目的と射程

実測の昇順で次（**報告 114 箇所・13 ファイル**）。platform ユニット。
`Refs #882`（`Closes` は最後の 1 本だけ）。起点 ID の置き方は
[`20260822_issue-882_dashboardservice-xunit1051.md`](20260822_issue-882_dashboardservice-xunit1051.md)
の同名節が正本。

## submodule の扱い（#961 で実証した手順の反復）

platform ユニットのビルドには AST submodule の初期化が要る。
**別セッションが AST を作業中**なので、前後を実測して報告する。

| | |
| --- | --- |
| 初期化【前】の主 worktree AST HEAD | `abce0015…` |
| 本 worktree の AST HEAD（gitlink へ初期化） | `9b9c6763…` |
| 初期化【後】の主 worktree AST HEAD | `abce0015…` ✅ **バイト同一** |
| 自コミットへの pin の混入 | **0 件**（`git status` で確認） |

**記録された gitlink に対して初期化したので、誰の状態も動かしていない。**

## 着手前の call site 読み

| メソッド | 件数 | 種類 |
| --- | ---: | --- |
| `PostAsJsonAsync` / `ReadFromJsonAsync` / `ReadAsStringAsync` / `GetAsync` | 82 | HTTP |
| `CompleteAsync` | 20 | 自ドメイン（`ILlmProvider`） |
| `EmbedAsync` | 9 | 自ドメイン（`IEmbeddingProvider`） |
| `StreamAsync` | 3 | 自ドメイン（`IAsyncEnumerable`） |

**自ドメイン 3 メソッドはいずれも `ct` が最後で手前に省略可能引数を持たない**ため位置指定で足せる:

```csharp
Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default);
Task<float[]> EmbedAsync(string text, string model, int dimensions,
    EmbeddingRoutePurpose purpose, CancellationToken ct = default);
IAsyncEnumerable<CompletionChunk> StreamAsync(
    CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default);
```

- **同居テストダブルが 5 ファイルに 9 個**（`CompleteAsync` 7 / `EmbedAsync` 2 の宣言）。
  #949 の「先頭 `.`（メンバアクセス）必須」が効き、適用後も宣言は素のまま
- **LINQ 衝突なし**（`.Select` は 2 件あるがメソッド集合に入れていない。`.Any` は 0 件）

## 🔴 置換器が「既にトークンを渡している呼び出し」へ 2 つ目を足した（ビルドが検出）

```csharp
// テストダブルの SendAsync(HttpRequestMessage request, CancellationToken ct) の中
Body = await request.Content.ReadAsStringAsync(ct, TestContext.Current.CancellationToken);
//                                             ^^ 既存のトークン。2 つ目は不正
```

`error CS1501`（`ReadAsStringAsync` に引数 2 個のオーバーロードは無い）。**1 箇所。**

**原因**: 置換器の冪等判定が `TestContext.Current.CancellationToken` の**文字列一致だけ**で、
**別の名前のトークン（`ct`）を既に渡している**ことを見ていなかった。

**対処**: 引数リストの**最後の引数**を取り出し、`ct` / `token` / `cancellationToken` 等に
一致したら触らないようにした（`TOKEN_ARG_RE` ＋ `last_top_level_arg`）。
自己試験へ 5 ケース足して **18/18** 通過。

🔴 **この箇所はそもそも移行対象ではない。** ハンドラが受け取った `ct` を流すのが正しく、
テストのトークンを渡すのは意味が変わる。**アナライザも報告していない**（テストメソッド外）。
**「報告されていないのに置換された」ものを 1 件ずつ確かめたから見つかった。**

## 報告 114 に対し置換 116（差の 2 は盲点、1 は上の不具合）

- `AnthropicContentBlockSanitizerTests.cs:137` … **private static ヘルパ**（[[#946]] 形 3）
- `ClaudeProviderThinkingTests.cs:86` … **`var act = () => …` のラムダ**（形 1）
- `EmbeddingPurposeTests.cs:32` … **不具合。戻した**（上記）

最終 **116 = 報告 114 ＋ 盲点 2**。

## 🔴 変異試験が #946 を 1 ファイルの中で直接示した

**同じファイル・同じメソッド・同じトークン**で、囲む文脈だけを変えて 2 回測った。

| # | 戻した箇所 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-1 | `var act = () => provider.CompleteAsync(…)` … **ラムダ内** | 落ちない（盲点） | **BUILD_EXIT=0 / error 0** |
| M-1b | `var result = await provider.CompleteAsync(…)` … **テスト本体** | **落ちる** | **BUILD_EXIT=1 / error xUnit1051 2 行** |

**差は「囲むメソッド本体が `[Fact]` を持つか」だけである。**
[[#946]] の主張（`remaining: 0` は全呼び出しの移行を意味しない・`WarningsAsErrors` も同じ盲点を持つ）を、
**対照実験として最小の形で示した**ことになる。この対はそのまま #946 へ追記する。

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-2 | baseline を `migrated:true` のまま `remaining: 114` へ戻す | `migrated-nonzero` | **exit 1・当該 1 件のみ** |
| M-3 | props の許可リストから外す | `props-desync` | **exit 1・当該 1 件のみ** |

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| 114 箇所が移行済み | ✅ 再測定で**一覧から消えた**。platform 合計 **350 → 236**（ちょうど −114） |
| 置換の総数が説明できる | ✅ **116 = 報告 114 ＋ 盲点 2**（不具合 1 は戻した） |
| **既存トークンへ 2 つ目を足していない** | ✅ `(ct, TestContext` が 0 件 |
| **テストダブルの宣言が無傷** | ✅ 5 ファイル・9 個すべて素のまま |
| **再発したらビルドが落ちる** | ✅ M-1b（`error xUnit1051` / exit 1） |
| **テスト件数が減らない** | ✅ **183 → 183**（属性数も develop と一致: 201 / 201） |
| submodule pin を混入させていない | ✅ `git status` に 0 件・主 worktree の AST HEAD も不変 |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |

### 検証

`dotnet build src/platform/backend/backend.slnx`（Release）→ **0 警告・0 エラー**／
`dotnet test` platform **615 件・0 失敗**（🔴 **`--filter "Category!=Integration"` 付き**）／
`scripts.test.js` **603 件 all passed**。

## 申し送り

残件 **582 → 468**。移行済み 14 本。**残り 4 本。**

| プロジェクト | 実測 | 状態 |
| --- | ---: | --- |
| `Platform.Shared.Infrastructure.Tests` | 4 | Wolverine チェーン待ち |
| `DocumentService.Api.Tests` | 94 | **E2（辺 `DocumentNormalized`）がコンシューマを書き換えるため待ち** |
| `ConversionService.Worker.Tests` | 138 | MassTransit。`Select` / `Any` の衝突に注意 |
| `Platform.Bff.Tests` | 232 | **#439 3b（SPA 切替）待ち**。着手時に再実測すること |

🔴 **置換器の冪等判定は「既に別のトークンを渡しているか」まで見る**ようになった。
残りのプロジェクトでも、テストダブルの中の `SendAsync(..., CancellationToken ct)` のような
**自前でトークンを持つ文脈**は同じ形で現れる。

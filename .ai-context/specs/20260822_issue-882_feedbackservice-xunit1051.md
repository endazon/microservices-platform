---
title: 作業仕様書 — FeedbackService.Api.Tests を xUnit1051 から剥がす（実測 30 箇所）（#882）
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
  - "20260822_issue-882_aianalysis-xunit1051.md"
issue: "#882"
---

# 作業仕様書 — FeedbackService.Api.Tests を xUnit1051 から剥がす

## 目的と射程

実測の昇順で次（**30 箇所・3 ファイル**）。`Refs #882`（`Closes` は最後の 1 本だけ）。

起点 ID の置き方は
[`20260822_issue-882_dashboardservice-xunit1051.md`](20260822_issue-882_dashboardservice-xunit1051.md)
の同名節が正本。

## 着手前の call site 読み（前回の申し送りの適用）

[`20260822_issue-882_aianalysis-xunit1051.md`](20260822_issue-882_aianalysis-xunit1051.md) が
「**件数の小ささは機械性を意味しない**」「**引数ゼロの呼び出しが `CS0839` を生む**」を残したので、
**編集前に全 30 箇所を読んだ**。

| ファイル | 件数 | 行 |
| --- | ---: | --- |
| `FeedbackEndpointTests.cs` | 26 | 20, 24, 36, 40, 52, 55, 59, 70, 80, 90, 107, 108, 109, 111, 125, 127, 139, 141, 173, 185, 188, 208, 222, 235, 247, 260 |
| `HealthEndpointTests.cs` | 2 | 12, 19 |
| `IntrospectionEndpointTests.cs` | 2 | 21, 24 |

**全 30 箇所が HTTP クライアント呼び出し（前回でいう分類 A）である。**
`IRagOrchestrator` のような「`ct` の前に省略可能引数を持つ自ドメインのメソッド」（分類 B）は**無い**。
ただし次の 2 つが混在する:

- **引数ゼロの呼び出し**（24, 40 行の `ReadFromJsonAsync<FeedbackDto>()`）
- **複数行にまたがる呼び出し**（20, 36, 55, 70, 80, 90, 208, 222 行）

## 🔴 一律の文字列置換をやめ、括弧対応で書き換えた

前回は「末尾の `)` を削って `, <token>)` を足す」規則で **`CS0839` を 7 件作り込んだ**。
同じ規則は本プロジェクトにも**引数ゼロが 2 箇所ある**ので必ず再発する。

そこで置換器を**括弧対応（paren matching）**で書き直した（`scratchpad` の作業用スクリプト。
リポジトリへはコミットしない）。要点:

- メソッド名から `(` を見つけ、**文字列・逐語文字列・補間文字列・文字リテラルを飛ばしながら**
  対応する `)` を探す。複数行・入れ子括弧・文字列内の `)` に耐える
- **引数リストが空ならカンマを付けない**（`Foo()` → `Foo(<token>)`）
- **既にトークンが入っている呼び出しは触らない**（冪等）

### 置換器の自己試験が、件数 assert より先にバグを 1 つ捕まえた

置換器に 10 件の自己試験を付けたところ、**ジェネリックの入れ子を取りこぼす**バグが出た:

```
FAIL  a.GetFromJsonAsync<List<T>>("/f")     ← 素通りしていた
```

ジェネリック引数の正規表現を `<[^<>()]*>`（入れ子不可）で書いていたためである。
本ファイルは `GetFromJsonAsync<List<FeedbackDto>>` を **3 箇所**（59, 141, 188 行）持つので、
**そのまま流していれば 3 箇所が黙って移行されず**、件数 assert で初めて気付くことになった。
`<(?:[^<>()]|<[^<>()]*>)*>` へ直して 10/10 通過。

## 🔴 xUnit1051 が拾わない呼び出しを 1 箇所見つけた（ratchet の盲点）

置換器は **31 箇所**を書き換えたが、アナライザが報告したのは **30 箇所**だった。
差分は `FeedbackEndpointTests.cs:159`:

```csharp
var requests = Enumerable.Range(0, 8)
    .Select(_ => client.PostAsJsonAsync("/feedback", new FeedbackRequest(answerId, "up")));
var responses = await Task.WhenAll(requests);
```

**`.Select(...)` のラムダの中**にあり、テストメソッド本体で直接 `await` されていない。
アナライザはこれを**報告しない**（着手前の実測でも 159 行は警告一覧に現れていない）。

### 判断: この 1 箇所も移行する（＝31 箇所を変更する）

- 同じファイル内で 25/26 がトークンを渡し 1 つだけ渡さない状態は、**後から読む人が
  「意図的に外したのか、漏れたのか」を判別できない**
- トークンを渡しても挙動は変わらない（テスト中に取り消されない）。実際
  `ConcurrentDoubleSubmit_NoServerError` は単独実行でも Passed

🔴 **より重要なのは、ここが ratchet の盲点だという事実である。**
`remaining: 0` は「**そのプロジェクトのすべての呼び出しがトークンを渡している**」を意味しない。
**アナライザが見ていない形（ラムダの中など）は、剥がした後も検出されない。**
`WarningsAsErrors` も同じアナライザに依存しているので、**この型の回帰はビルドでも止まらない。**
（#882 のコメントへ、`CS0839` の件と並べて記録した。）

## 手順（器が強制する 3 点セット）

1. テストの `.cs` を直す（30 箇所 ＋ 上記の 1 箇所 ＝ 31）
2. `scripts/xunit1051-baseline.json` を `remaining: 0` / `migrated: true`
3. `src/Directory.Build.props` の `XUnit1051Migrated` へ追加

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| 30 箇所が移行済み | ✅ `-p:NoWarn=` 付き再測定で**一覧から消えた**。knowledge 合計 **468 → 438**（ちょうど −30） |
| 置換の総数が説明できる | ✅ **31 = 報告された 30 ＋ ラムダ内の 1**（上記）。先頭カンマの壊れた形は **0 件** |
| **再発したらビルドが落ちる** | ✅ M-1（`error xUnit1051` / exit 1） |
| **テスト件数が減らない** | ✅ **20 → 20**（属性数も develop と一致: 20 / 20） |
| ラムダ内を触ったテストが通る | ✅ `ConcurrentDoubleSubmit_NoServerError` 単独で Passed |
| 他プロジェクトの残件が変わらない | ✅ 他 6 プロジェクトの実測が baseline と一致 |
| 器の 3 点が揃っている | ✅ `check-xunit1051-ratchet.js` exit 0 |

### 変異試験

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-1 | **引数ゼロの呼び出し**を 1 箇所戻す（前回 `CS0839` を生んだ型） | **ビルド失敗** | **`error xUnit1051` 2 行 / exit 1** |
| M-2 | baseline を `migrated:true` のまま `remaining: 30` へ戻す | `migrated-nonzero` | **exit 1・当該 1 件のみ** |
| M-3 | props の許可リストから外す | `props-desync` | **exit 1・当該 1 件のみ** |

復旧は `cmp` でバイト一致を確認した。

### 検証

`dotnet build`（Release）**0 エラー**（警告は既存の `CS0618` 2 件のみ）／
`dotnet test` knowledge **654 件・0 失敗**／`scripts.test.js` **584 件 all passed**。

## 申し送り

残件 **885 → 855**。移行済み 8 本。次は `IngestionService.Worker.Tests`（33）。

- **括弧対応の置換器を使う**（一律の文字列置換に戻らない）。自己試験を先に通すこと
- **報告された件数と置換した件数が食い違ったら、必ず差分を特定してから進める。**
  本 PR ではそれがアナライザの盲点の発見につながった

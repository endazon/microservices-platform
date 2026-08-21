---
title: 作業仕様書 — xUnit v2 から v3 へ 16 テストプロジェクトを一斉に切り替える（#455 A-2）
type: spec
status: done
related_ids:
  - ADR-0030
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - "ADR-0030（バックエンドアプリケーション層標準・テストは xUnit v3）"
related_adrs:
  - IADR-0229
  - IADR-0231
issue: "#455"
---

# 作業仕様書: xUnit v2 → v3 の一斉切替（#455 A-2）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: `ADR-0030`（バックエンドアプリケーション層標準）§テスト —— **標準は xUnit v3**
- 実装 issue: `#455`

## なぜ「一斉」でなければならないか

`xunit.runner.visualstudio` は **v2 用（2.x）と v3 用（3.x）で別系列**である。
**CPM（Central Package Management）は 1 パッケージにつき 1 バージョンしか持てない**ため、
runner を 3.x へ上げた瞬間に **v2 のままのプロジェクトは非互換の runner と組み合わさる**。
段階移行はできない。

🔴 **`src/ai-stock-trading`（AST submodule）は対象外である。** 自前の
`Directory.Packages.props` を持ち、本リポと CPM を共有しない（両ファイルとも `<Import>` を持たず、
`DirectoryPackagesPropsPath` の上書きもリポジトリ全体で 0 件。MSBuild は最も近い祖先だけを使う）。
**AST は既に v3 へ移行済みであり、本作業の参照実装として読める。**

## 着手前の実測（母集合。誤りの側の文字列で引いた）

```
git grep -l 'PackageReference Include="xunit"'  -- '*.csproj' ':!src/ai-stock-trading'   → 16
git grep -l 'PackageReference Include="xunit.v3"' -- '*.csproj' ':!src/ai-stock-trading' → 0
git grep -l 'IAsyncLifetime'      -- '*.cs' ':!src/ai-stock-trading'                     → 9
git grep -n 'Xunit.Abstractions|ITestOutputHelper|SkippableFact|Skip\.' -- '*.cs' '*.csproj'
```

| 項目 | 実測 |
| --- | --- |
| 切り替える `.csproj` | **16**（knowledge 11 / platform 4 / 雛形 1） |
| `IAsyncLifetime` を実装する `.cs` | **9**（すべて `Knowledge.IntegrationTests`） |
| `Xunit.Abstractions` / `ITestOutputHelper` | **1 ファイル**（`BffDocumentWriteRoundtripBenchmark.cs`） |
| `[SkippableFact]` / `Skip.IfNot` | **同じ 1 ファイル**（`Platform.Bff.Tests`） |
| `Xunit.SkippableFact` の `PackageReference` | **1**（`Platform.Bff.Tests.csproj`） |

### 対象プロジェクト（16）

knowledge（11）: `AiAnalysisService.Api.Tests` / `ConversionService.Worker.Tests` /
`DashboardService.Api.Tests` / `DataSourceService.Api.Tests` / `DocumentService.Api.Tests` /
`FeedbackService.Api.Tests` / `IngestionService.Worker.Tests` / `RetrievalService.Api.Tests` /
`WikiService.Api.Tests` / `Knowledge.Contracts.Tests` / `Knowledge.IntegrationTests`

platform（4）: `Platform.Bff.Tests` / `AuthorizationService.Api.Tests` /
`LlmGateway.Api.Tests` / `Platform.Shared.Kernel.Tests`

雛形（1）: `templates/unit-template/.../SampleService.Tests`

## スコープ

1. **CPM**（`src/Directory.Packages.props`）
   - `xunit` 2.9.3 の `PackageVersion` を**削除**（v3 の本体 ID は `xunit.v3`。`xunit` は v2 系のまま）
   - `xunit.runner.visualstudio` **2.8.2 → 3.1.5**
   - `Xunit.SkippableFact` 1.4.13 の `PackageVersion` を**削除**（下記 3）
   - `xunit.v3` 3.2.2 は既に宣言済み（そのまま使う）
2. **16 プロジェクト**の `PackageReference Include="xunit"` → `xunit.v3`
3. **`Xunit.SkippableFact` を撤去する**（`Platform.Bff.Tests`）
   - **v3 対応版が存在しない**（最新 1.5.61 も `xunit.extensibility.execution` v2 に依存する）
   - v3 標準の **`Assert.Skip`** へ移す: `[SkippableFact]` → `[Fact]`、
     `Skip.IfNot(cond, msg)` → `Assert.SkipUnless(cond, msg)`
   - 🔴 **A-2 より前に撤去してはならなかった。** xUnit v2 には動的スキップが無く、先に外すと
     **「真の Skipped」が「何もしない Passed」へ退化する**。`Assert.Skip` が在る本段が正しい置き場である
4. **`IAsyncLifetime` の 9 ファイル**: v3 では `ValueTask InitializeAsync()` / `ValueTask DisposeAsync()`
   （v2 は `Task`）。戻り値型だけを変える
5. **`Xunit.Abstractions` の撤去**: v3 では `ITestOutputHelper` が `Xunit` 名前空間へ移った。
   `using Xunit.Abstractions;` を削除する
6. **検査器 `check-backend-libraries.js` の `xunitRunnerMismatch` を対称にする**（下記）
7. **記述の追随**（規則 9・10。「現行は v2」と書いた記述はすべて偽になる）

### 🔴 検査器を対称にする理由（射程の拡大ではなく、同じ検査の欠けた半分）

現行の `xunitRunnerMismatch` は **`xunit.v3` 参照 ＋ runner 2.x** の一方向しか見ない。
切替後は runner が 3.x になるため、**`xunit`（v2）を参照したまま取り残されたプロジェクト**が
同じく非互換になるのに**検出されない**。これは「新しい検査器の追加」ではなく、
**既存の検査が持つべき対称な半分**である。本作業がまさに「取り残しが起きうる唯一の局面」であり、
一斉切替という性質そのものを機械で担保する。

### スコープ外

- **`MassTransit` → Wolverine**（別作業）。🔴 部分移行は禁止であり 1 件も触らない
- **テストの中身の変更**。件数を 1 件も減らさない。表明・アサーションは書き換えない
- **AST submodule**（前述のとおり CPM 非共有・移行済み）

## 受け入れ基準

1. `git grep -l 'PackageReference Include="xunit"' -- '*.csproj' ':!src/ai-stock-trading'` が **0 件**
2. 16 プロジェクトすべてが `xunit.v3` を参照する
3. CPM から `xunit` と `Xunit.SkippableFact` の `PackageVersion` が消え、runner が **3.1.5**
4. `dotnet build|test src/{platform,knowledge}/backend/backend.slnx` が **Failed 0**、
   **テスト件数が 1 件も減っていない**（基準値: Kernel 26 / Bff 231+1skip / Authorization 68 /
   LlmGateway 183 / Contracts 6 / Dashboard 16 / Ingestion 28 / Feedback 20 / Conversion 75 /
   Document 101 / AiAnalysis 68 / Retrieval 71 / Wiki 39 / DataSource 133）
5. **`Platform.Bff.Tests` の skip が 1 件のまま**である（`Assert.SkipUnless` が効いている＝
   `RUN_BFF_BENCH` 未設定で **Passed ではなく Skipped**）
6. `Knowledge.IntegrationTests` が **43/43**（dockerd を起こして実走）
7. `node scripts/check-backend-libraries.js` が EXIT=0
8. `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が EXIT=0（対称化に伴う自己試験を追加する）
9. `dotnet format --verify-no-changes` が両ユニットで EXIT=0
10. 雛形（`templates/`）もビルドできる（CI の `template-backend-build` ジョブ）

## 変異試験（EXIT はリダイレクトして読む。`| tail` の終了コードを読まない）

| 変異 | 期待 |
| --- | --- |
| (a) 1 プロジェクトを `xunit`（v2）へ戻す | **対称化した検査器が fail**（これが対称化の存在理由） |
| (b) CPM の runner を 2.8.2 へ戻す | 既存方向の検査器が fail |
| (c) `Assert.SkipUnless` を消す | skip 件数が 1 → 0 になり、受け入れ基準 5 が破れる |

**復旧を確認し、復旧したことを報告に含める。**

## 母集合（規則 9・10）

**是正後に「現行は v2」「v3 を参照してはならない」で引き直した** —— `xunit` という語だけで
引いても捕まらない記述がある。

### 結果（実装後に確定した値）

| 分類 | 件数 | 扱い |
| --- | ---: | --- |
| `xunit` → `xunit.v3` に替えた `.csproj` | **16** | knowledge 11 / platform 4 / 雛形 1 |
| CPM から削除した `PackageVersion` | **2** | `xunit` 2.9.3 / `Xunit.SkippableFact` 1.4.13 |
| CPM で上げた版 | **1** | `xunit.runner.visualstudio` 2.8.2 → **3.1.5** |
| `IAsyncLifetime` の `Task` → `ValueTask` | **9 ファイル** | すべて `Knowledge.IntegrationTests` |
| `Xunit.Abstractions` 撤去 ＋ `[SkippableFact]` → `[Fact]` ＋ `Assert.SkipUnless` | **1 ファイル** | `BffDocumentWriteRoundtripBenchmark.cs` |
| ソフトスキップ `if (cond) return;` → `Assert.Skip*` | **3 箇所** | `PandocConversionServiceTests.cs`（下記） |
| `xUnit3003` の是正 | **1 ファイル** | `DockerFactAttribute` に Caller 情報のコンストラクタ |
| `xUnit1051` の抑止 | **1 箇所** | `src/Directory.Build.props`（テストプロジェクトのみ） |
| 追随させた記述 | **8 箇所** | 下表 |

### 🔴 走っていないのに緑だったテストが 2 件見つかった

`PandocConversionServiceTests` は `if (!PandocAvailable()) return;` で前提を満たさないケースを
**ソフトスキップ**していた（`Xunit.SkippableFact` を導入しない方針だったため）。
これは**走らなかったケースを Passed として報告する**。

**CI に pandoc は入っていない**（`.github/workflows/ci.yml` に `pandoc` の記述は 0 件）。
つまりこの 2 ケースは**毎回 Passed と報告されながら本体を 1 行も実行していなかった**。
v3 の `Assert.Skip*` は追加パッケージゼロで使えるため、真の Skipped へ改めた。

```
移行前: ConversionService.Worker.Tests  Passed: 75, Skipped: 0, Total: 75
移行後: ConversionService.Worker.Tests  Passed: 73, Skipped: 2, Total: 75
        Skipped ...PandocConversionServiceTests.Degrades_when_source_not_locally_readable
        Skipped ...PandocConversionServiceTests.Runs_pandoc_on_local_markdown_source
```

**総数は 75 のまま変わっていない。** 減ったのは「実行したことにされていた 2 件」である。

### xUnit1051（1,886 件）を抑止した判断

助言アナライザであり正しさの検査ではない。全件是正は 1,886 箇所の呼び出し側書き換えで、
**「計画外の大規模リファクタを行わない」に反する**。一方で放置すると `CS0618`（Testcontainers の
非推奨 API。実在する 4 件）が埋もれる。本リポジトリ自身が
`scripts/check-backend-libraries.js` に「**赤の常態化は『赤を無視する学習』を生み、検査の目的
そのものを壊す**」と記録しており、**同じ判断を適用した** —— テストプロジェクトのみ `NoWarn` し、
採用は別 issue の段階採用に回す。`TreatWarningsAsErrors` は `false` でビルドの成否は変わらない。

**抑止と是正の線は件数と改修範囲で引いた** —— `xUnit3003` は 1 ファイルなので抑止せず直した。

### 規則 10 —— この変更で新たに誤りになる自分の記述（8 箇所）

| # | 場所 | 従前 |
| --- | --- | --- |
| 1 | `docs/tech/tech-requirements.md` §採用技術表 | 「xUnit **v3**（※現行は v2。後述）」 |
| 2 | 同 §バックエンド標準（本文） | 「標準が v3 だが現行は v2」「`xunit.v3` を参照するプロジェクトを作ってはならない」 |
| 3 | 同 §開発・ビルド | 「標準は v3・現行は v2 で各サービス再実装時に切替」 |
| 4 | 同 §残件 | 「xUnit v2 → v3 の切替時期と `Xunit.SkippableFact` の v3 代替は各サービス側で確定する」 |
| 5 | `docs/tests/TEST_STRATEGY.md` §種別表 ＋ 見出し「xUnit のバージョンは v2 のまま書く」 | v2 前提 |
| 6 | `templates/unit-template/README.md`（2 箇所） | 「テストは xUnit v2 で書く」 |
| 7 | `templates/.../SampleService.Tests.csproj` ＋ `Platform.Shared.Kernel.Tests.csproj` のコメント | 「雛形は現時点で v2 を使う」「xUnit は **v2** を使う」 |
| 8 | `scripts/check-backend-libraries.js` の失敗メッセージ | 「切替は独立した issue で行うこと」 |

**`.ai-context/adr/IADR-0229` 決定 5「テストは xUnit v2 を使う」には日付つき追記**を置いた
（本文は書き換えない）。🔴 **決定 5 は「切替 issue の完了まで」を条件とする暫定であり、
本作業はその条件を満たすのであって覆すのではない。**

**除外したもの（理由つき）:**

- **`.ai-context/specs/` / `.ai-context/superpowers/`（凍結記録）** —— その時点の記録として正しい
- **検査器と自己試験の `2.8.2` / `xunit`** —— **テストデータ**である（両方向の判定を固定する）。
  移行しても消えないし、消してはならない
- **`src/ai-stock-trading`** —— CPM 非共有・移行済み（前述）

### 変異試験の実測

| 変異 | 期待 | 実測 |
| --- | --- | --- |
| 基準（無変異） | EXIT=0 | **EXIT=0** |
| (a) 1 プロジェクトを `xunit`（v2）へ戻す | 対称化した検査器が fail | **EXIT=1**（`[xUnit 版不整合] Platform.Shared.Kernel.Tests.csproj`） |
| (b) CPM の runner を 2.8.2 へ戻す | 既存方向が fail | **EXIT=1**（16 プロジェクトすべてを名指し） |
| (c) `Assert.SkipUnless` を消す | skip が 1 → 0 になる | **実測 232 Passed / 0 Skipped**（基準 231 Passed / 1 Skipped） |

🔴 **(c) の復旧に一度失敗した。** `cd src` で作業ディレクトリが変わったまま相対パスで
`cp` を実行し、`No such file or directory` で**変異が残ったまま**になった。直後に気付いて復旧し、
`git diff` と `git grep -n 'mutated'` で作業ツリー全体に変異残骸が無いことを確認した。
**「変異させたら必ず復旧し、復旧したことを報告に含める」の実例である。**

🔴 **`IADR-0229` 決定 5「テストは xUnit v2 を使う」は条件付きの決定**である
（「切替 issue の完了まで」）。本作業はその条件を**満たす**のであって覆すのではない。
ただし読み手が現状を誤解しないよう、日付つき追記で完了を併記する。

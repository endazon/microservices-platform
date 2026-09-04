---
title: 作業仕様書 — サービス内 Tests/ の単体 / 結合の区分をトレイトで表す（#1145）
type: spec
status: in-progress
related_ids:
  - NFR
  - ADR-0065
  - IADR-0161
  - IADR-0232
  - IADR-0237
  - IADR-0289
  - IADR-0334
  - IADR-0368
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 3
---

# 作業仕様書: 単体 / 結合の区分をトレイトで表す（#1145）

起点: 実装 issue #1145（#1063 の受け入れ基準 4・計画 `ADR-0065` 決定 3 の 3 行目）。

`ADR-0065` 決定 3 は「**単体か結合かはフォルダではなくテストの書き方で表す**（トレイト・命名）。
**区分そのものを捨てるのではない**」と書いている。#1063 は「テストの内容を書き換えない」制約を
持っていたため区分の表現に触れず、`IADR-0334` の射程外として #1145 へ申し送った
（`.ai-context/specs/20260831_issue-1063_tests-mirror-body-structure.md` §7 の 1 番）。

> 🔴 **issue #1145 の本文は移送本体の実装 ADR を `[[IADR-0331]]` と書いているが、これは誤りである。**
> `IADR-0331` は `planning-submodule-residual-references`（別件）であり、移送本体は
> **`IADR-0334`**（`tests-mirror-target-resolution`）である。**起票文の ID を転記せず、
> `.ai-context/adr/` を引いて確かめた。**

## 1. 母集合（着手時に自分で引き直した）

基点 `origin/develop` = **`c15f4696`**。`git rev-parse --is-shallow-repository` = **`false`**
（履歴の打ち切りではないので `git log` を出典に使える）。

### 軸 1 — 追跡下のパスから引く

```console
$ git ls-files | grep -E '^src/(platform|knowledge)/backend/Services/[^/]+/Tests/' \
    | sed -E 's#^(src/[^/]+/backend/Services/[^/]+)/.*#\1#' | sort | uniq -c
```

**14 サービス**（platform 4 / knowledge 10）。`Tests/` 配下の `.cs` は **278 件**。

### 軸 2 — テストクラスを構文で数える（ファイル数ではなく**クラス数**で引く）

ファイル数で引くと、**1 ファイルに 2 つのテストクラスが同居する 3 件**を取りこぼす
（`DocumentBodyIntakeTests.cs` / `SuggestionPromptGateTests.cs` / `HybridSearchServiceTests.cs`）。
`.claude/rules/traceability.md` 規則 5「軸を 1 本で終わらせない」に従い、クラス単位で引き直した。

| 数え方 | 件数 |
| --- | --- |
| `Tests/` の `.cs` | 278 |
| うち `[Fact]` / `[Theory]` を 1 つ以上持つファイル | 214 |
| **トップレベルのテストクラス（＝トレイト付与の母集合）** | **217** |
| 入れ子のテストクラス | **0**（陽性対照: 走査器は入れ子クラスも列挙する。列挙結果が 0 だった） |
| `abstract` なテストクラス | 1（`StubOrchestratorBase`。`[Fact]` を 0 個しか持たないため対象外） |
| `[Fact]` / `[Theory]` の宣言数 | 1623 |
| 既に `[Trait]` を持つクラス | **0** |

> `[Theory]` は実行時に複数ケースへ展開されるため、**1623 は「テスト件数」ではない**。
> 実行件数の前後比較は §5 で `dotnet test` の要約から測る。

## 2. 判定基準（先に決めた）

**「そのテストが、検証対象の外側にある合成または実資源を実際に通すか」**で決める。
`docs/tests/TEST_STRATEGY.md`「テスト種別と責務」の定義（単体＝ドメイン規則・ハンドラの分岐 /
結合＝実依存を伴う往復・イベント連鎖、`Mvc.Testing` / Testcontainers / Respawn）を、
**機械で引ける signal へ落としたものである。**

| signal | 判定 | 中身 |
| --- | --- | --- |
| **A** | 結合 | `WebApplicationFactory` 派生（`TestWebApplicationFactory`）を通す。`.CreateClient()` / `CreateDefaultClient` を含む —— **DI コンテナ・ミドルウェア・ルーティング・認証の合成が丸ごと立つ** |
| **B** | 結合 | `HostApplicationBuilder` / `Host.CreateApplicationBuilder` / `new HostBuilder` / `WebApplication.CreateBuilder` を自前で組む（ホストの合成を通す） |
| **C** | 結合 | プロセス外の実資源への到達を `Assert.SkipUnless` / `Assert.SkipWhen` で門にしている（実 `pandoc` / 実 `pdftotext`） |
| **D** | 結合 | Testcontainers / `DockerRequired` / `PostgresFixture` / `RabbitMqFixture` / `MinioFixture` / `BrokerRequired` / Respawn を使う |
| （どれも無い） | 単体 | 型を直接 `new` して呼ぶ。テストダブル（NSubstitute・手書きの `Recording*` / `Fake*`）で外を止める |

**走査はコメントを剥がしてから当てる。** 剥がさずに当てると **6 件が偽陽性になる** ——
`DatabaseConnectorTests` / `QdrantFullTextIndexBootstrapTests` ほかは、本文ではなく
**コメントで** `DockerRequired` / `Testcontainers` に言及しているだけである
（「実 SQL は follow-up の統合テストで確認する」といった申し送り）。**この 6 件はすべて単体である。**

### 判定に迷ったもの（表に残す）

| 対象 | 迷い | 採った判定 | 根拠 |
| --- | --- | --- | --- |
| `UseInMemoryDatabase` で `DbContext` を組み、ホストを立てずにハンドラを直接呼ぶ **20 クラス** | 「EF の実プロバイダを通る」ので結合とも読める | **単体** | InMemory プロバイダは**実 DB の代役**であって実依存ではない。`TEST_STRATEGY` の結合は「実依存を伴う往復」であり、通しているのはテストダブルである |
| **A の 96 クラス**（`TestWebApplicationFactory`） | 実 DB も実ブローカも使わない（`IADR-0161` により InMemory ＋ 一意 DB 名、`TestRabbitMqConfiguration` でブローカを差し替え）ので単体とも読める | **結合** | 検証しているのは**合成の結果**（認可・ルーティング・フィルタ・DI の解決）であり、単一の型の振る舞いではない。`TEST_STRATEGY` が結合の道具に `Mvc.Testing` を挙げているのはこの意味である |
| `PandocConversionServiceTests` / `PdfTextLayerConverterTests` | **1 クラスの中に、実バイナリを要する `[Fact]` と要さない `[Fact]` が混在**する | **クラス全体を結合** | 付与の粒度をクラスに固定したため（§3 決定 c）。この 2 クラスは外部プロセスへのアダプタの試験であり、主題は実バイナリとの往復にある |
| `PipelineConfigLoaderTests` / `PipelineStepRegistrationTests` / `PipelineRecomposeTests` | ホストを組むが HTTP は叩かない | **結合**（signal B） | 検証対象が「`AddPlatformWolverineStep` の**登録が成立するか**」であり、合成そのものを通している |
| `IntrospectionEndpointTests` / `HealthEndpointTests`（`Program.cs` 由来・`IADR-0334` 決定 4） | 鏡写しでは `Tests/` 直下に残るもの | **結合**（signal A） | 置き場所の軸（`IADR-0334`）と種別の軸（本作業）は**独立**である。`Tests/` 直下だからといって種別が決まるわけではない |

### 母集合の内訳（サービス × 種別）

| ユニット | サービス | 単体クラス | 結合クラス | 計 | 単体 `[Fact]`/`[Theory]` | 結合 `[Fact]`/`[Theory]` | 計 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| knowledge | AiAnalysisService | 9 | 6 | 15 | 68 | 20 | 88 |
| platform | AuthorizationService | 9 | 7 | 16 | 88 | 51 | 139 |
| knowledge | ConversionService | 10 | 7 | 17 | 59 | 52 | 111 |
| knowledge | DashboardService | 0 | 5 | 5 | 0 | 48 | 48 |
| knowledge | DataSourceService | 10 | 11 | 21 | 68 | 78 | 146 |
| knowledge | DocumentService | 6 | 22 | 28 | 35 | 173 | 208 |
| knowledge | FeedbackService | 0 | 3 | 3 | 0 | 21 | 21 |
| knowledge | GraphService | 18 | 17 | 35 | 156 | 147 | 303 |
| knowledge | IngestionService | 9 | 1 | 10 | 51 | 1 | 52 |
| platform | LlmGateway | 14 | 8 | 22 | 119 | 54 | 173 |
| platform | McpServer | 8 | 2 | 10 | 49 | 16 | 65 |
| platform | NotificationService | 4 | 2 | 6 | 17 | 17 | 34 |
| knowledge | RetrievalService | 12 | 5 | 17 | 107 | 43 | 150 |
| knowledge | WikiService | 7 | 5 | 12 | 48 | 37 | 85 |
| **計** | **14** | **116** | **101** | **217** | **865** | **758** | **1623** |

signal の内訳（結合 101 クラス）: **A 96 / B 3 / C 2 / D 0**。

### 🔴 signal D が 0 件であることの陽性対照

「per-service `Tests/` に Testcontainers は 1 件も無い」は、**検出器が生きていることを別に示さなければ
何も証明しない**（`.claude/rules/` の陰性結論の作法・`IADR-0237` 決定 3）。同じ正規表現を
ユニット横断の統合テストへ当てた:

```console
$ grep -rlE "Testcontainers|DockerRequired|PostgresFixture|RabbitMqFixture|MinioFixture|BrokerRequired|Respawn" \
    src/knowledge/backend/Tests/Knowledge.IntegrationTests/ | wc -l
29
$ grep -rlE "<同じパターン>" src/platform/backend/Services src/knowledge/backend/Services | wc -l
6      ← すべてコメントでの言及（本文一致は 0 件）
```

**検出器は 29 件を拾える。** よって per-service `Tests/` の 0 件は「検出器が死んでいる」ではない。
これは `IADR-0289` が別に確かめた「per-service の器は実ブローカを持たない」とも整合する。

## 3. 決定（詳細と論拠は `IADR-0368`）

- **(a) トレイトで表す**（命名規約ではない）。`--filter` で機械的に選べること（受け入れ基準 1）を
  クラス名の綴りに依存させない。
- **(b) トレイト名は `Category` ではなく `TestKind`** とする。値は `Unit` / `Integration`。
  🔴 **`Category` は本リポジトリで既に別の意味を持ち、CI の振り分けに load-bearing である**
  （`ci.yml` の `--filter "Category!=Integration"`。`IADR-0232` 決定 3）。
  そこで言う `Category=Integration` は「**Testcontainers で実コンテナを起こす**」であり、
  本作業の「結合」（合成を通す・実資源に触れる）とは**外延が違う**。
  同じ名前を重ねると、**per-service の結合 101 クラス（758 宣言）が PR の CI から静かに消える。**
- **(c) 付与の粒度はクラス**（メソッド単位の上書きを置かない）。217 クラスすべてに 1 つずつ付く。
- **(d) CI に新しい `--filter` を足さない。** `ci.yml` / `integration.yml` は 1 バイトも触らない。
- **(e) 検査器は足さない。** `CLAUDE.md`「同型の事故が 2 回起きたら」に照らして **0 回目**である
  （走査したが、トレイト付け忘れの事故記録は本リポジトリに存在しない。`IADR-0232` が挙げるのは
  *リスク* であって事故ではない）。`IADR-0368` に 0 回目として記録する。
- **(f) テストの内容は書き換えない。** 足すのは `[Trait("TestKind", "...")]` の 1 行だけである。

## 4. 対象範囲

- **対象**: `src/{platform,knowledge}/backend/Services/*/Tests/` の 14 プロジェクト・217 クラス。
- **対象外（申し送り）**: `Knowledge.IntegrationTests` / `Platform.Bff.Tests` /
  `Platform.Shared.Kernel.Tests` / `Platform.Shared.Infrastructure.Tests` / `Knowledge.Contracts.Tests` の
  5 プロジェクト。#1145 の射程は「14 サービスの `Tests/`」であり、
  `Knowledge.IntegrationTests` は `TEST_STRATEGY` が「サービス単位の `Tests` とは**別の層**」と
  明記している。
  🔴 **この非対称は (d) の根拠でもある** —— slnx 全体へ `--filter TestKind=...` を掛けると、
  トレイトを持たない 5 プロジェクトが**両方のバケツから落ちる**。CI をこのフィルタに依存させない。
- **対象（追加）**: 雛形 `templates/unit-template` の 4 テストクラス（単体 2 / 結合 2）。
  🔴 **起票時の申し送りは「#1146 が持つ」だったが、引き直すと #1146 は既に CLOSED であり、
  その射程は鏡写しの段（フォルダ構造）であって種別ではない。** つまり**誰も持っていない。**
  雛形は新しいユニットが複製する正であり、外すと次のユニットが付いていない状態から始まる。
  雛形はソリューション（`src/*/backend/backend.slnx`）に含まれずビルドされないため、
  テスト件数には影響しない。
- **対象外**: AST 側（`src/ai-stock-trading`）。**`AST#613` が既に持つ**（重複起票しない）。

## 5. 受け入れ基準（検証手順）

1. `dotnet build` が両ユニットで通る。
2. `dotnet test` の**件数が前後で不変**（skip 込み・プロジェクト単位）。基点の実測:

   | プロジェクト | Total（前） |
   | --- | --- |
   | McpServer.Tests | 87 |
   | AuthorizationService.Tests | 149 |
   | NotificationService.Tests | 53 |
   | LlmGateway.Tests | 231 |
   | IngestionService.Tests | 52 |
   | FeedbackService.Tests | 21 |
   | AiAnalysisService.Tests | 98 |
   | DataSourceService.Tests | 185 |
   | WikiService.Tests | 97 |
   | RetrievalService.Tests | 182 |
   | DocumentService.Tests | 248 |
   | DashboardService.Tests | 57 |
   | ConversionService.Tests | 142（Passed 136 / Skipped 6） |
   | GraphService.Tests | 332 |
   | **14 サービス計** | **1934** |

   （射程外の参考値: `Platform.Shared.Kernel.Tests` 42 / `Platform.Shared.Infrastructure.Tests` 251 /
   `Platform.Bff.Tests` 487 / `Knowledge.Contracts.Tests` 47 / `Knowledge.IntegrationTests` 77）
3. **陽性・陰性の対**: `--filter "TestKind=Unit"` と `--filter "TestKind=Integration"` の件数が、
   **フィルタ無しの件数と一致する**（両方が 0 でないこと ＝ フィルタが効いていることの陽性対照）。
4. `dotnet format --verify-no-changes` が両ユニットで通る。
5. `node scripts/check-test-traceability.js` / `check-coverage-floor.js` / `check-doc-links.js` /
   `check-trace-blocks.js` / `check-doc-updated.js` と `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。
6. `Tests/` のフォルダ構造が 1 件も動いていない（`git diff --name-status` に `R` が現れない）。

### 実測（付与後・`DOTNET_CLI_UI_LANGUAGE=en dotnet test <slnx> --no-build -c Release`）

| プロジェクト | 前 | 後 | `TestKind=Unit` | `TestKind=Integration` | Unit ＋ Integration |
| --- | --- | --- | --- | --- | --- |
| McpServer.Tests | 87 | **87** | 71 | 16 | **87** |
| AuthorizationService.Tests | 149 | **149** | 98 | 51 | **149** |
| NotificationService.Tests | 53 | **53** | 28 | 25 | **53** |
| LlmGateway.Tests | 231 | **231** | 168 | 63 | **231** |
| IngestionService.Tests | 52 | **52** | 51 | 1 | **52** |
| FeedbackService.Tests | 21 | **21** | 0 | 21 | **21** |
| AiAnalysisService.Tests | 98 | **98** | 78 | 20 | **98** |
| DataSourceService.Tests | 185 | **185** | 81 | 104 | **185** |
| WikiService.Tests | 97 | **97** | 58 | 39 | **97** |
| RetrievalService.Tests | 182 | **182** | 134 | 48 | **182** |
| DocumentService.Tests | 248 | **248** | 45 | 203 | **248** |
| DashboardService.Tests | 57 | **57** | 0 | 57 | **57** |
| ConversionService.Tests | 142（skip 6） | **142（skip 6）** | 73 | 69（skip 6） | **142** |
| GraphService.Tests | 332 | **332** | 174 | 158 | **332** |
| **計** | **1934** | **1934** | **1059** | **875** | **1934** |

**14 プロジェクトすべてで前後不変・両バケツの和が一致した。**
`FeedbackService` / `DashboardService` の `Unit` が 0 なのは母集合どおりである
（この 2 サービスは単体クラスを 1 つも持たない。§2 の表）。
**陽性対照**: どのプロジェクトでもフィルタが「全件」や「0 件」に潰れていない
（フィルタが無効なら両バケツとも総数に等しくなり、和が 2 倍になる）。

射程外プロジェクトの前後も不変: `Platform.Shared.Kernel.Tests` 42 /
`Platform.Shared.Infrastructure.Tests` 251 / `Platform.Bff.Tests` 487（skip 1）/
`Knowledge.Contracts.Tests` 47 / `Knowledge.IntegrationTests` 77（skip 41）。
これらは `--filter "TestKind=..."` で **`No test matches the given testcase filter`** になる
（決定 5 の 3 が言う「両バケツから落ちる」の実測である）。

## 6. 本 PR で扱わない（申し送り）

1. **AST（`src/ai-stock-trading`）側の同型作業** → **`AST#613` が既に持つ**（重複起票しない）。
2. **射程外 5 プロジェクト（`Knowledge.IntegrationTests` / `Platform.Bff.Tests` /
   `Platform.Shared.Kernel.Tests` / `Platform.Shared.Infrastructure.Tests` /
   `Knowledge.Contracts.Tests`。計 904 件）への `TestKind` 付与** → 起票を検討する。
   **`ADR-0065` 決定 3 の射程はサービス内 `Tests/` であり、これらは対象外である**ため、
   必要になるのは「CI をこのフィルタに依存させたくなったとき」に限る。
3. **`TestKind` を CI の振り分けに使うこと** → `IADR-0232` の門の議論を伴うため、必要が生じてから
   （`IADR-0368` 決定 5）。
4. **`TestKind` の付与漏れの機械検査** → `IADR-0368` 決定 6。**2 回目が起きたら足す。**

> **起票前に既存 issue を検索した。** 陰性結論（「同件の issue は無い」）には陽性対照を対で置く ——
> `gh issue list --search` にキーワードを与えた検索と、絞り込み無しの一覧を並べて確かめる。

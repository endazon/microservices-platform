---
title: 作業仕様書 — サービス内 Tests/ を本体の鏡写し（Tests/Features/・Tests/Domain/ ほか）へ移送する（#1063）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0065
  - ADR-0068
  - IADR-0282
  - IADR-0298
  - IADR-0319
  - IADR-0334
author: claude
created: 2026-08-31
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 1・3・4
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md (Accepted 2026-08-30) 決定 1・2・5
---

# 作業仕様書: `Tests/` を本体の鏡写しへ移送する（#1063）

起点: 実装 issue #1063（環流元 planning#490 / 計画 `ADR-0065` 決定 3）。

## 1. 母集合（着手時に自分で引き直した）

基点 `origin/develop` = **`8eab69b3`**。`git rev-parse --is-shallow-repository` = **`false`**
（履歴の打ち切りではないので `git log` を出典に使える）。

🔴 **issue #1063 本文の「14 サービスの `Tests/` が完全にフラット・計 260 ファイル」は
起票時点（`0784dd2`・2026-08-30）の他人の数えであり、そのままでは 2 点で誤りである。**

### 軸 1 — 追跡下のパスから引く（拡張子・行フィルタで絞らない）

```console
$ git ls-files | grep -cE '^src/(platform|knowledge)/backend/Services/[^/]+/[Tt]ests/'
279
```

内訳（拡張子別）: `.cs` 253 / `.csproj` 14 / `.json` 4 / `.md` 8。

| | issue 本文（`0784dd2`） | 引き直し（`8eab69b3`） | 差 |
| --- | ---: | ---: | ---: |
| サービス数 | 14 | **14** | 0 |
| `Tests/` 配下の追跡ファイル | 260 | **279** | **+19** |
| うち `.cs` | （記載なし） | **253** | — |
| 完全にフラットか | 「完全にフラット」 | 🔴 **偽**（`ConversionService/Tests/Golden/` が 3 階層ある） | — |

差 +19 の内訳は起票後に着地した #1116（`QdrantFullTextIndexBootstrapTests` ほか）・#1138
（`KeycloakIdentityAdminClientTests` ほか）・#1129 系のテスト追加と、#1082〜#1096 の 3 段化に伴う
本体側の移動である。**「完全にフラット」は起票時点でも偽であった** —— `Tests/Golden/`
（`Cases/` 8 ＋ `Expected/` 4 ＋ 器 1 の 13 ファイル）は `IADR-0298` が置いた構造である。

サービス別（`.cs` ＋ 資材 ＋ csproj）:

| ユニット / サービス | 件数 | | ユニット / サービス | 件数 |
| --- | ---: | --- | --- | ---: |
| knowledge/AiAnalysisService | 17 | | knowledge/RetrievalService | 19 |
| knowledge/ConversionService | 34 | | knowledge/WikiService | 13 |
| knowledge/DashboardService | 9 | | platform/AuthorizationService | 21 |
| knowledge/DataSourceService | 26 | | platform/LlmGateway | 26 |
| knowledge/DocumentService | 34 | | platform/McpServer | 13 |
| knowledge/FeedbackService | 8 | | platform/NotificationService | 12 |
| knowledge/GraphService | 36 | | | |
| knowledge/IngestionService | 11 | | **合計** | **279** |

### 軸 2 — 大文字小文字の食い違い（Windows で静かに壊れる）

「ディスク上で小文字 `tests/` になっているサービスがある」という過去の報告を検証した。
**現時点では 0 件である。**

```console
$ find src -maxdepth 5 -type d -iname tests -not -path '*/node_modules/*'
（15 件。すべて "Tests"。うち src/knowledge/backend/Tests は Knowledge.IntegrationTests 用で対象外）
$ git ls-files | grep -cE '^src/(platform|knowledge)/backend/Services/[^/]+/tests/'
0
$ # 陽性対照 —— 同じ引きで大文字は当たる（0 が「引けていない」ではないことの担保）
$ git ls-files | grep -cE '^src/(platform|knowledge)/backend/Services/[^/]+/Tests/'
279
```

**追跡名（`git ls-files`）とディスク名（`find` の出力）が一致することを両方向で確認した。**

> ［2026-09-02 追記 / #1063］🔴 **当初ここには `git ls-files | grep -c '/tests/'` → `0` と
> 書いていたが、この対は再現しない。** 絞り込みなしの `/tests/` は **`docs/tests/` の 60 件**に
> 当たるため、実際の出力は `60` である。軸 2 の主張（**バックエンド `Services/<Svc>/` 配下に
> 小文字 `tests/` は無い**）自体は正しいが、**それを示す引きが広すぎた。**
> 対象を限った引きと陽性対照へ差し替えた。

### 軸 3 — 移送で壊れ得る外部からの参照（誤りの側から引く）

`Services/<Svc>/Tests/` を指す記述を、拡張子で絞らず全追跡ファイルから引いた。

| 参照元 | 件数 | 扱い |
| --- | ---: | --- |
| `docs/tests/*.md`（8 本） | `.cs` パス 13 箇所 | 🔴 **追随が要る**（`check-test-spec-coverage.js` の方向 (a) がパスの実在を見る） |
| `src/*/backend/backend.slnx` ＋ 雛形 slnx | `Tests/<Name>.Tests.csproj` | 移動しないので不変 |
| `scripts/xunit1051-baseline.json` / `scripts/backend-library-baseline.json` | csproj パスと `Tests/` 接頭辞 | 同上・不変 |
| `.ai-context/specs/*.md`（3 本） | 旧パス | **書き換えない**（確定済み記録。`traceability.repo.md` の凍結規則） |

### 除外したもの（と理由）

| 除外 | 理由 |
| --- | --- |
| `src/knowledge/backend/Tests/Knowledge.IntegrationTests/**` | ユニット横断の結合テストであり、サービス内 `Tests/` とは別の層（#1063 本文の明示・`ADR-0065` 決定 3 の射程外） |
| `src/ai-stock-trading/**`（AST submodule） | 別リポジトリ。`ADR-0065` の射程には入るが本 issue の対象外（フォローアップ 7 として計画側が別に持つ） |
| `templates/unit-template/backend/Services/SampleService/Tests/**` | #1063 が「雛形の側を正」としている（本体 3 段に対し `Tests/Features/` 直下という差はあるが、雛形の改定は本 issue の射程外。§7 に申し送る） |
| `src/*/backend/Shared/*.Tests/**`・`Bff/Platform.Bff.Tests/**` | サービス内 `Tests/` ではない（`Services/<Svc>/Tests/` に当たらない） |

## 2. 決めること・決めたこと

`ADR-0065` 決定 3 は「`Tests/Features/` ／ `Tests/Domain/` の形を採る」としか書いていない。
**本体には `Infrastructure/` と `Common/` もあるため、鏡写しの意味を決める必要がある。**
判定手続きは `IADR-0334` に置いた（本仕様書は適用結果を持つ）。要旨:

- **鏡写しの相手は「そのテストが検証する本体の要素が置かれているディレクトリ」である。**
  `Features/` と `Domain/` に限らず、`Infrastructure/<Sub>/`・`Common/<Sub>/` も鏡写す。
- **エンドポイント経由で検証するテストは、叩く操作の 3 段目へ。複数操作にまたがるなら集約（2 段目）へ**
  （`ADR-0068` 決定 2 の「1 つの操作にしか使われないか」と同じ判定を、テスト側へ適用する）。
- **型を直接呼ぶテストは、その型が定義されたファイルのディレクトリへ。**
- **本体に対応物が無いものは `Tests/` 直下に残す** ——
  (a) テスト専用の器（`TestWebApplicationFactory` / `TestAuthHandler` / `Test*Configuration` /
  `Recording*` / `TestDoubles` / `GlobalUsings.cs` / xUnit の collection 定義）、
  (b) `Program.cs` 由来の検証（`/health`・`/internal/introspection`）——
  本体でも `Program.cs` はサービス直下にあるので、鏡写しの位置は `Tests/` 直下である、
  (c) 主題が `Platform.Shared.*` にあるもの（`ConfigViewerPolicyTests` /
  `KeycloakRolesClaimsTransformationTests` / `PlatformAuthJwtBearerOptionsTests` /
  `PipelineConfigLoaderTests`）—— 自サービスの本体に鏡写しの相手が無い。
- **名前空間をフォルダへ追随させる**（`<Svc>.Tests` → `<Svc>.Tests.<移送先>`）。
  雛形が `SampleService.Tests.Features` を採っているのと同じ形である。
  **`using` の追加は不要である** —— C# は外側の名前空間を自動で探索するので、
  `Tests/` 直下に残る器は移送後も無修飾で見える。
- 🔴 **`ConversionService/Tests/Golden/` は動かさない**（`IADR-0298`）。
  `NormalizationGolden.cs` は `[CallerFilePath]` で `Cases/` と `Expected/` を解決するため、
  移すと資材の解決が静かに壊れる。器の名前空間も `ConversionService.Tests` のまま据え置く。

## 3. 移送の内訳（実測）

| | 件数 |
| --- | ---: |
| 移送する `.cs` | **166** |
| `Tests/` 直下に残す `.cs` | **86** |
| `Tests/Golden/` に据え置く `.cs` | 1 |
| `.csproj`（移動しない） | 14 |
| `Tests/Golden/` の資材（`.json` 4 ＋ `.md` 8） | 12 |
| **合計** | **279** |

移送先の全一覧は本 PR の diff（`git log --diff-filter=R --summary`）が持つ。**ここへ 166 行の表を
複写しない**（同じ事実の情報源を 2 つにしない）。

## 4. 受け入れ基準 → 検証の写像

| # | 受け入れ基準（#1063） | 検証 |
| --- | --- | --- |
| 1 | `Tests/` が `Features/` と `Domain/` に分かれている | 移送後のディレクトリ一覧 |
| 2 | 本体の `Features/<集約>/<操作>/` と同じ経路で見つかる | 同上（3 段目 **28** フォルダ。§5 に実測） |
| 3 | `Tests` プロジェクトはサービスあたり 1 本のまま | `git ls-files '*/Tests/*.csproj'` が 14 件 |
| 4 | 単体・結合はトレイトまたは命名で表す | **本 PR では変えない**（`ADR-0065` 決定 3 が求めるのはフォルダ区分の改定であり、区分の表現は現状の命名・`Assert.SkipUnless` のまま。§7 に申し送る） |
| 5 | 移送前後で同じ件数が通る | §5 の前後比較（プロジェクト単位・skip 込み） |
| 6 | `check-test-traceability.js` / `check-coverage-floor.js` が緑 | `/verify` 相当の検査一覧 |

## 5. テスト件数の前後比較（プロジェクト単位・skip 込み）

**環境の制約**: Docker daemon が無い（containerd / nerdctl）ため Testcontainers 依存テストは
skip される。**skip のまま緑になるので、件数は skip 込みで示す。**

**測り方**: `git stash` は共有スタックなので使わない。「前」は基点 `8eab69b3` を別 worktree
（`git worktree add .claude/worktrees/b1063 8eab69b3 --detach`）へ展開して測り、測定後に
`git worktree remove` した。「後」は本作業ツリー。**両方とも同じ 14 プロジェクトを同じ順で
`dotnet test <Tests.csproj> --nologo -v q` に掛けている**（`backend.slnx` 一括ではなく
プロジェクト個別 —— `Platform.Bff` が未展開 submodule を参照して解決できないため）。

> 🔴 **最初の「前」測定は無効だった。** scratchpad の絶対パスが長く、Windows の MAX_PATH に
> 掛かって `testhost.exe` が起動できず（`Win32Exception (267) ディレクトリ名が無効です`）、
> 14 本すべてが「テスト実行が中止されました」で終わっていた。**結果行が 1 本も出ないことに
> 気付かなければ「前 0 件」を前後比較に載せていた。** 短いパスへ置き直して測り直している。

### 前（`8eab69b3`・移送前）

```console
成功!   -失敗:     0、合格:    95、スキップ:     0、合計:    95、期間: 982 ms - AiAnalysisService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    89、スキップ:     3、合計:    92、期間: 25 s - ConversionService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    30、スキップ:     0、合計:    30、期間: 34 s - DashboardService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   166、スキップ:     0、合計:   166、期間: 10 s - DataSourceService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   233、スキップ:     0、合計:   233、期間: 37 s - DocumentService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    21、スキップ:     0、合計:    21、期間: 1 s - FeedbackService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   279、スキップ:     0、合計:   279、期間: 54 s - GraphService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    36、スキップ:     0、合計:    36、期間: 2 s - IngestionService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   164、スキップ:     0、合計:   164、期間: 27 s - RetrievalService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    64、スキップ:     0、合計:    64、期間: 3 s - WikiService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   149、スキップ:     0、合計:   149、期間: 8 s - AuthorizationService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   226、スキップ:     0、合計:   226、期間: 1 m 4 s - LlmGateway.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    66、スキップ:     0、合計:    66、期間: 1 s - McpServer.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    53、スキップ:     0、合計:    53、期間: 40 s - NotificationService.Tests.dll (net10.0)
```

### 後（本 PR・移送後）

```console
成功!   -失敗:     0、合格:    95、スキップ:     0、合計:    95、期間: 918 ms - AiAnalysisService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    89、スキップ:     3、合計:    92、期間: 1 m 3 s - ConversionService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    30、スキップ:     0、合計:    30、期間: 1 m 20 s - DashboardService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   166、スキップ:     0、合計:   166、期間: 17 s - DataSourceService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   233、スキップ:     0、合計:   233、期間: 31 s - DocumentService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    21、スキップ:     0、合計:    21、期間: 1 s - FeedbackService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   279、スキップ:     0、合計:   279、期間: 57 s - GraphService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    36、スキップ:     0、合計:    36、期間: 2 s - IngestionService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   164、スキップ:     0、合計:   164、期間: 1 m 52 s - RetrievalService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    64、スキップ:     0、合計:    64、期間: 3 s - WikiService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   149、スキップ:     0、合計:   149、期間: 14 s - AuthorizationService.Tests.dll (net10.0)
成功!   -失敗:     0、合格:   226、スキップ:     0、合計:   226、期間: 2 m 26 s - LlmGateway.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    66、スキップ:     0、合計:    66、期間: 2 s - McpServer.Tests.dll (net10.0)
成功!   -失敗:     0、合格:    53、スキップ:     0、合計:    53、期間: 1 m 57 s - NotificationService.Tests.dll (net10.0)
```

### 突合

| | 合格 | スキップ | 合計 | プロジェクト |
| --- | ---: | ---: | ---: | ---: |
| 前 | 1671 | 3 | **1674** | 14 |
| 後 | 1671 | 3 | **1674** | 14 |
| 差 | 0 | 0 | **0** | 0 |

```console
$ diff <(grep -oE '合格: *[0-9]+、スキップ: *[0-9]+、合計: *[0-9]+' before.raw) \
       <(grep -oE '合格: *[0-9]+、スキップ: *[0-9]+、合計: *[0-9]+' after.raw)
（差分なし）
```

**14 プロジェクトすべてで合格・スキップ・合計が一致した**（受け入れ基準 5）。
skip 3 件はいずれも `ConversionService`（pandoc 実バイナリ依存）である。

### 🔴 移送が壊した 1 件（発見と是正）

**`ConversionService.Tests.Infrastructure.ExternalServices.PandocConversionServiceTests.Dockerfile_installs_pandoc_into_the_runtime_stage` が
移送直後に落ちていた。**

原因は**段数を数えた相対遡上**である。

```csharp
// 移送前（Tests/ 直下にあることが前提）
private static string DockerfilePath([CallerFilePath] string thisFile = "") =>
    Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!, "Dockerfile");
```

`Tests/PandocConversionServiceTests.cs` から 2 段上がると `ConversionService/` に着き
`ConversionService/Dockerfile` を読めていた。移送先は
`Tests/Infrastructure/ExternalServices/` なので 2 段上は `Tests/` であり、
**存在しない `Tests/Dockerfile` を読みに行って落ちる。**

`ConversionService.csproj` のある階層を探して引き当てる形へ直した（**移送先の深さに依存しない**）。
これは `IADR-0334` 決定 6 が `Tests/Golden/` を動かさなかったのと同じ罠であり、
**Golden 以外にも同型が 1 件あった**ということである。

**同型の全走査**（陽性対照つき）:

```console
$ git grep -ln 'CallerFilePath' -- 'src/**/*.cs' | grep -v ai-stock-trading
src/knowledge/backend/Services/ConversionService/Tests/Golden/NormalizationGolden.cs
src/knowledge/backend/Services/ConversionService/Tests/Infrastructure/ExternalServices/PandocConversionServiceTests.cs
src/knowledge/backend/Tests/Knowledge.IntegrationTests/Fixtures/DockerRequired.cs

$ git grep -ln 'BaseDirectory\|GetDirectoryName' -- 'src/*/backend/Services/*/Tests/**/*.cs'
（上記 2 件 ＋ DataSourceService/.../FileSystemConnectorTests.cs ＋ AuthorizationService/.../IdentityAdminContractTests.cs）
```

4 件を個別に確認し、**是正が要るのは 1 件だけ**だった —— `NormalizationGolden` は移送対象外
（決定 6 で据え置き）、`FileSystemConnectorTests` の `GetDirectoryName` は一時ディレクトリに
掛かるもの、`IdentityAdminContractTests` の `AppContext.BaseDirectory` はビルド出力（`bin/`）
を指すのでソース位置に依存しない。

> **陰性の結論に陽性対照を対で置いた。** 最初に `git grep -ln 'CallerFilePath' -- 'src/*/backend/Services/*/Tests/'`
> で引いたときは **0 件**が返ったが、`NormalizationGolden.cs` が当たるはずだと分かっていたので
> pathspec の誤りに気付けた。**「0 件だった」を「無い」と読んでいたら是正漏れになっていた。**

## 6. `.gitignore` の確認

新設フォルダ名がビルド成果物パターンに飲まれていないことを `git check-ignore -v` で確認した。

```console
$ # 陽性対照（検査器が機能していること）
$ git check-ignore -v src/knowledge/backend/Services/DocumentService/bin/Debug/x.dll
.gitignore:462:bin/	src/knowledge/backend/Services/DocumentService/bin/Debug/x.dll
$ git check-ignore -v src/knowledge/backend/Services/DocumentService/obj/x.json
.gitignore:463:obj/	src/knowledge/backend/Services/DocumentService/obj/x.json

$ # 本題: 新設フォルダ 14 種はいずれも無視されない（出力なし = 一致するパターンなし）
$ for d in Features Domain Ports Infrastructure Persistence ExternalServices Messaging \
           Common Observability Routing Pricing Catalog Sync Normalize; do
    git check-ignore -v "src/knowledge/backend/Services/DocumentService/Tests/$d/Probe.cs"
  done
（14 件すべて出力なし）
```

**追跡下のファイル数でも裏を取った** —— 移送先 166 件はすべて `git ls-files` に現れる
（飲まれていれば追跡できない）。§3 の内訳がそのまま成立していることが同時に証拠になる。

## 7. 本 PR で扱わない（申し送り）

**起票前に既存 issue を検索し、重複を作らなかった**（3 件目は既存が見つかったので起票していない）。

1. **単体 / 結合のトレイト付与**（受け入れ基準 4）。`ADR-0065` 決定 3 は「区分そのものを捨てるのではない」
   と述べるが、トレイトの導入は移送ではなく**テストの書き換え**であり、#1063 の制約
   「テストの内容を書き換えない」と衝突する。→ **#1145 を起票した。**
2. **雛形（`templates/unit-template`）の `Tests/Features/` を 3 段の鏡写しへ揃えること。**
   #1063 は雛形を正としているため本 PR では触らない。→ **#1146 を起票した。**
3. **AST（`src/ai-stock-trading`）側の同型作業**（計画 `ADR-0065` フォローアップ 7）。
   → 🔴 **起票しない。既に `AST#613`（`refactor(NFR): バックエンド構成を計画 ADR-0065 へ追随させる
   （Features 3 段化・Tests 鏡写し・Domain 欠け 3 件・CLAUDE.md の訂正）`）が open で持っている。**
   なお `gh issue list --search` にキーワードを与えた検索は **0 件**を返しており、
   陽性対照（絞り込みなしの `--limit 5`）を並べて初めて AST#613 に気付いた。
   **検索 0 件を「無い」と読んでいたら重複起票していた。**

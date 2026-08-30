---
title: 作業仕様書 — ADR-0068 決定 2 違反の是正（3 サービスの McpToolContracts.cs を Declare/ へ降ろす）
type: spec
status: done
related_ids:
  - NFR
  - FR-16
  - ADR-0065
  - ADR-0068
  - IADR-0261
  - IADR-0282
  - IADR-0292
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md (Accepted 2026-08-30)
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30)
related_specs:
  - ./20260830_issue-1062_three-level-slices-platform.md
  - ./20260830_issue-1062_three-level-slices-document-graph.md
  - ./20260830_issue-1062_three-level-slices-knowledge-rest.md
issue: "#1062"
---

# 作業仕様書 — ADR-0068 決定 2 違反の是正（McpToolContracts.cs）

## 起点と背景

先行 PR（#1084 / #1082）は `Features/McpTools/McpToolContracts.cs` を 2 段目に残し、理由を
「**申告の語彙（候補・除外規則）。操作をまたぐ**」と書いた（`20260830_issue-1062_three-level-slices-document-graph.md`
§移送表）。**クロス監査がこの理由を実測で否定した。**

計画 `ADR-0068` 決定 2 は次を唯一の判定基準として定める。

> **どのファイルを下ろすかは「そのファイルが 1 つの操作にしか使われないか」だけで決める。**
> 1 つの操作にしか使われない → **3 段目へ下ろす** ／ 2 つ以上の操作が使う → **2 段目に残す**

**`McpToolContracts.cs` を使う操作は `McpTools/Declare` の 1 つだけである。** したがって 3 段目へ下ろす。
先行 PR の「操作をまたぐ」という理由は、**実際に何操作が使っているかを数えずに書かれていた。**

## 母集合の引き直し（着手時・[[IADR-0141]] 決定 1 / `traceability.repo.md` 規則 9・10）

**issue 本文・監査報告の数えを転記していない。** `origin/develop` `d3403107` を自分で走査した。

### 軸 1 — 2 段目（`Features/<集約>/` 直下）にあるファイルを全部引く

```
find src/knowledge/backend/Services -mindepth 4 -maxdepth 4 -path "*/Features/*" -name "*.cs"
```

**25 件。** うち `*Endpoints.cs`（`ADR-0068` 決定 1 の「登録表」）が 17 件、それ以外が 8 件。
**登録表は決定 1 により 2 段目が正しい位置なので、判定の対象は残り 8 件である。**

### 軸 2 — 8 件それぞれについて「使う操作フォルダ」を数える

参照はシンボル名で全走査した（`grep -rn <型名> src/knowledge --include=*.cs`。`obj/` `bin/` 除外）。
**`Program.cs` の DI 登録・`Tests/` は「操作」ではないので分母に数えない。散文コメントだけの言及も数えない。**

| # | ファイル | 使う操作フォルダ | 数 | 決定 2 の判定 |
| --- | --- | --- | ---: | --- |
| 1 | `DataSourceService/Features/DataSources/SyncSchedule.cs` | `Create` / `GetById` / `List` / `Patch` / `Update` / `Sync` | **6** | 2 段目のまま正しい |
| 2 | `DocumentService/Features/Documents/DocumentObjectPurger.cs` | `Documents/Delete` / `PrivateNotes/Purge` / `PrivateNotes/Maintenance` | **3** | 2 段目のまま正しい |
| 3 | `DocumentService/Features/PrivateNotes/PrivateNoteUsage.cs`（`PrivateNoteUsage` / `SyncTokens`） | `PrivateNotes/{Create,GetQuota,List,Maintenance,Purge,SetQuota}` / `ObsidianSync/Push` / `SyncDevices/{Issue,Reissue}` ＋ 登録表 `ObsidianSyncEndpoints` | **9** | 2 段目のまま正しい |
| 4 | `GraphService/Features/GraphDocuments/LinkEdgeSynchronizer.cs` | `GraphDocuments/Sync` のみ | **1** | 🔴 **決定 2 違反（本仕様書の引き直しで新規発見）**。本 PR の射程外 → #1094 |
| 5 | `GraphService/Features/AiSuggestions/AiSuggestionGenerator.cs` | `AiSuggestions/Generate` のみ | **1** | 射程外（先行仕様書が別 issue を予告済み） → #1093 |
| 6〜8 | `{DocumentService,GraphService,RetrievalService}/Features/McpTools/McpToolContracts.cs` | 各サービスの `McpTools/Declare` のみ | **各 1** | 🔴 **決定 2 違反 → 本 PR で是正** |

### 軸 3 — 監査が挙げた 3 件目（`RetrievalService/Features/McpTools/` に操作フォルダが無い）は既に解消済み

監査は「`RetrievalService` は操作フォルダ自体が無く決定 4 にも未達」と記録したが、
**`d3403107` 時点では `Features/McpTools/Declare/Endpoint.cs` が実在する**（`find` で確認）。
**監査は #1082 マージ前の版を見ていた。** 3 サービスは既に同型であり、
**本 PR で直すべき差分は `McpToolContracts.cs` の位置だけである。**

> **監査の表を転記していたら、存在しないフォルダを「新設する」ことになっていた。**

### 軸 4 — 誤りの側の文字列で引く（規則 1・9）

移送後に壊れ得るのは「2 段目の名前空間を using している箇所」である。**型名ではなく名前空間で引いた。**

```
grep -rn "Features\.McpTools" src/ | grep -v /obj/ | grep -v /bin/
```

**19 行。** うち `.Declare` 付きを除いた「2 段目の名前空間そのもの」の参照は次の 7 行で、これが追随の全量である。

| ファイル | 参照 | 移送後の扱い |
| --- | --- | --- |
| `DocumentService/…/McpTools/McpToolContracts.cs` | `namespace DocumentService.Features.McpTools;` | `.Declare` を付ける |
| `DocumentService/…/McpTools/Declare/Endpoint.cs` | `using DocumentService.Features.McpTools;` | **削除**（同一名前空間になる） |
| `DocumentService/Tests/McpToolDeclarationEndpointTests.cs` | `using DocumentService.Features.McpTools;` | **削除**（`.Declare` の using が既にある） |
| `GraphService` の同じ 3 ファイル | 同上 | 同上 |
| `RetrievalService` の同じ 3 ファイル | 同上 | 同上 |
| `RetrievalService/Program.cs` | `using RetrievalService.Features.McpTools;` | **削除**。`Program.cs` が使うのは `MapMcpToolEndpoints`（`.Declare` 側）だけで、この using は移送後に**存在しない名前空間**を指す（CS0246 になる） |

**`DocumentService/Program.cs` と `GraphService/Program.cs` は `.Declare` しか using していない**（追随不要）。
**`Knowledge.IntegrationTests/McpTools/` の 2 ファイルは、名前が似ているだけでサービス側の
`Features.McpTools` 名前空間を参照していない**（`McpServer.Domain` 経由。追随不要）。

### 除外したものと理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| `NotificationService/Features/Notifications/{IEmailAddressResolver,IEmailTransport,UnconfiguredSmtpEmailTransport}.cs` | 実測 1 操作だが、**`ADR-0068` が #1083 の着地形を「着地済みの 1 本は変更不要である」と明示裁定している**（§結果 フォローアップ 2）。裁定を実装側で覆さない |
| `GraphService/Features/AiSuggestions/AiSuggestionGenerator.cs` | 1 操作だが、争点は「3 段目へ降ろすか」ではなく「`Features/` の外（`Domain/` ／ `Infrastructure/`）へ出すか」であり、**決定 2 だけでは決まらない別の判断**。先行仕様書が別 issue を予告済み → **#1093 として起票した** |
| `GraphService/Features/GraphDocuments/LinkEdgeSynchronizer.cs` | **本仕様書の引き直しで新たに見つかった決定 2 違反。** 依頼された射程（McpTools の 3 件）の外であり、`Program.cs` の DI 登録行を伴うため移送の粒度が変わる。**黙って除外せず #1094 として起票した** |
| `src/ai-stock-trading`（submodule） | 別リポジトリ。`ADR-0068` §結果 フォローアップ 3 が別作業として分離している |
| platform ユニット全般 | #1083 で着地済み。`ADR-0068` が「変更不要」と裁定 |

## 実装方針

**純粋な移送に留める。振る舞いを 1 つも変えない。**

1. 3 サービスの `Features/McpTools/McpToolContracts.cs` を `Features/McpTools/Declare/McpToolContracts.cs` へ移す（履歴を保つため git の移動として行う）
2. 移送したファイルの `namespace <Svc>.Features.McpTools;` → `namespace <Svc>.Features.McpTools.Declare;`（`IADR-0261` の `<Svc>Service.*` 規約は維持）
3. 軸 4 の表のとおり `using` を削除する（`Declare/Endpoint.cs` ×3、`Tests` ×3、`RetrievalService/Program.cs` ×1 ＝ 計 7 行）
4. **触らないもの**: ルート登録の順序・`MapGroup` のパス／タグ・認可属性・エンドポイントフィルタ・
   `MapMcpToolEndpoints` の名前とシグネチャ・`ToolsPath` の値・`WithName` の値・`ExcludeFromDescription`・DI 登録・
   テストの本数と内容（using 行のみ）

> **`McpTools` 集約には `MapGroup` を持つ登録表が無い**（操作が `Declare` 1 つで、端点が
> `/internal/mcp-tools` を直接 `MapGet` する）。**決定 1 の「登録表は 2 段目に残す」は
> `MapGroup` ／ タグ ／ グループ認可をまとめるファイルについての規定**であり、
> 存在しない登録表を新設せよとは読まない（**新設は「純粋な移送に留める」に反する**）。
> `#1082` が `McpToolEndpoints.cs` ごと `Declare/` へ降ろした形をそのまま維持する。

## 受け入れ基準

1. 3 サービスとも `Features/McpTools/` 直下に `.cs` が 0 件になり、`Declare/` に `Endpoint.cs` と `McpToolContracts.cs` が並ぶ
2. `dotnet build src/knowledge/backend/backend.slnx` が警告なく通る
3. **テスト件数が移送前後でプロジェクト単位（skip 込み）で完全に一致する**
4. `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` が通る
5. 作業ツリーに未追跡の取りこぼしが残らない（`.gitignore` が新パスを食っていないことを `git check-ignore -v` で確認）
6. 検査器一式（`check-commit-messages` / `check-trace-blocks` / `check-doc-links` / `gen-knowledge-graph --check` /
   `check-adr-numbering` / `check-unit-dependencies` / `check-backend-libraries` / `check-test-traceability` /
   `validate-pipeline-config` / `check-event-topology`）が通る

## テスト件数の基準値（移送前・`d3403107`）

Docker daemon が無い環境（containerd / nerdctl）で計測した。**Testcontainers 依存は skip される。**

| プロジェクト | 合格 | スキップ | 合計 |
| --- | ---: | ---: | ---: |
| AiAnalysisService.Tests | 95 | 0 | 95 |
| ConversionService.Tests | 79 | 2 | 81 |
| DashboardService.Tests | 30 | 0 | 30 |
| DataSourceService.Tests | 166 | 0 | 166 |
| DocumentService.Tests | 233 | 0 | 233 |
| FeedbackService.Tests | 21 | 0 | 21 |
| GraphService.Tests | 275 | 0 | 275 |
| IngestionService.Tests | 28 | 0 | 28 |
| Knowledge.Contracts.Tests | 27 | 0 | 27 |
| Knowledge.IntegrationTests | 36 | 41 | 77 |
| RetrievalService.Tests | 156 | 0 | 156 |
| WikiService.Tests | 64 | 0 | 64 |
| **合計** | **1210** | **43** | **1253** |

## 検証結果（移送後）

- `dotnet build src/knowledge/backend/backend.slnx` → **成功 / 0 エラー**。警告 3 件はすべて
  `Knowledge.IntegrationTests/Storage/ObjectStorageRoundTripTests.cs` の `MinioBuilder` 廃止予定
  （**本 PR が触っていないファイル。移送前から出ていた**）
- `dotnet test src/knowledge/backend/backend.slnx` → **12 プロジェクトすべてで合格・スキップの数が
  移送前と一致**（上の基準値表と同一。合計 1210 / 43 / 1253）
- `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` → **exit 0**
- `git status` に未追跡の取りこぼし無し。`git check-ignore -v` は 3 つの新パスいずれにも一致しない（exit 1）。
  **`Declare` は `.gitignore` のどのパターンにも食われていない**（移送は `R`（rename）として追跡された）
- 検査器: `check-commit-messages` / `check-trace-blocks`（158 件）/ `check-doc-links`（1012 件）/
  `gen-knowledge-graph --check`（in-repo 4547 エッジ）/ `check-unit-dependencies`（csproj 39・.cs 918）/
  `check-backend-libraries` / `check-test-traceability` / `validate-pipeline-config --self-test` /
  `check-event-topology` → **すべて exit 0**

### 🔴 `check-adr-numbering` の既知の赤（並行 PR の採番衝突）

`node scripts/check-adr-numbering.js` は **`[missing-number] IADR-0311 が欠番`** を出して exit 1 になる。
**`IADR-0311` は未マージの PR #1087 が押さえており、本 PR は先着尊重で `IADR-0319` を採ったため**である。

- **`IADR-0144` 決定 3 が「並行 PR の衝突は未然に防げない（着地後の不整合しか見えない）」と記録している既知の性質**であり、本 PR の内容に起因する赤ではない。
- **解消条件**: #1087 が先にマージされ、本 PR を `develop` へ rebase すれば欠番が埋まって緑になる。
- **#1087 が取り下げられた場合**は、本 PR を `IADR-0311` へ改番する（ファイル名・本文の自称番号・索引・
  コード内コメント 3 箇所・作業仕様書・**PR タイトル**を追随させる）。

## 射程外（本 PR で変更しない）

- 上の「除外したものと理由」の 5 項目
- 2 段目の登録表 17 件の中身（決定 3 の「1 ファイルが複数操作の処理を含むか」の内容監査）。
  **本 PR では行数と共有ヘルパの参照元だけを見ており、決定 3 の全数監査は行っていない**（測れなかったものとして報告する）

---
title: 作業仕様書 DocumentService / GraphService のスライスを Features/<集約>/<操作>/ の 3 段へ移送する（#1062）
type: spec
status: done
related_ids:
  - NFR
  - FR-06
  - FR-09
  - FR-16
  - FR-17
  - FR-18
  - FR-19
  - FR-20
  - FR-21
  - UC-03
  - UC-10
  - UC-11
  - ADR-0065
  - ADR-0034
  - IADR-0282
  - IADR-0242
  - IADR-0270
  - IADR-0292
  - IADR-0296
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_vertical-slice-depth.md
issue: "#1062"
---

# 仕様書: DocumentService / GraphService の 3 段スライス移送（#1062）

> **本作業は振る舞いを 1 つも変えない。** 動かすのは**ファイルの置き場所と名前空間**だけである。
> 判定順・認可・応答・経路（route）・DI 登録の中身は 1 行も変えない。

## 起点

計画 **ADR-0065 決定 2**: スライスは `Features/<集約>/<操作>/` の **3 段**とし、
1 ユースケースのファイル（`Endpoint` / `Command`|`Query` / `Handler` / そのユースケースが
発行するイベント）を 1 フォルダへ束ねる（REPR）。雛形
`templates/unit-template/backend/Services/SampleService/Features/Samples/Create/`
が正であり、実ツリー（3 段目 0 件）が追随する側である。

**本作業の射程は #1062 のうち `DocumentService` と `GraphService` の 2 サービスだけ**である。
他サービスは別 PR が担う（同一 issue を親として並走。ファイル領域は重ならない）。

## 射程（実測・`0784dd2` 起点、`develop` は `7b57319a`）

| サービス | 集約（2 段目） | `.cs` |
| --- | --- | ---: |
| DocumentService | Documents / McpTools / ObsidianSync / PrivateNotes / SyncDevices / Tags | 12 |
| GraphService | AiSuggestions / EdgeTypes / Graph / GraphDocuments / KnowledgeHealth / McpTools | 11 |

**2 段目（集約）は 1 つも増やさず、1 つも減らさない。** 集約はビジネス能力の単位であり、
その切り直しは「スライスの深さ」を定めた ADR-0065 決定 2 の射程外である（別 issue へ回す）。

## 設計判断

### 判断 1 — 合成点（route group の生成）は集約に残す

エンドポイント群は `app.MapGroup("/documents")` などの **route group を共有**しており、
その `RequireAuthorization(...)` が**その群に属する全操作の認可の下限**を表している。
`DocumentEndpoints.cs` の `#629` 注記が「**このグループ既定は閲覧の下限であり、
書き込みの実効境界ではない**」と明示しているとおり、この 1 行は複数操作にまたがる規範である。

したがって:

- **集約の直下に合成点（`<X>Endpoints.cs`）を残す。** 中身は route group の生成と、
  各操作フォルダの `Map(...)` 呼び出しだけにする。
- **操作フォルダへ group 生成を複写しない。** 複写すると同じ認可既定が 11 箇所に散り、
  1 箇所だけ書き換わる事故（実効境界の静かな変化）を作れる形になる。

これは「**操作をまたいで共有されるものは集約に残す**」という本作業の一般則の適用でもある。

### 判断 2 — 操作フォルダへ入れるのは「そのユースケース専用のもの」だけ

| 入れる | 残す（集約直下） |
| --- | --- |
| その 1 経路のハンドラ本体（ラムダ）と、それに直付けの `.RequireAuthorization` / `.WithName` / `.Produces` | 複数操作が呼ぶ変換・検証・照会ヘルパ |
| その 1 経路だけが受ける要求レコード（＝ `Command` / `Query`） | 複数操作・複数集約が使う DTO・ポート・ストア・生成器・同期器 |
| その 1 メッセージだけを購読する Consumer | route group の生成（判断 1） |

### 判断 3 — ファイル名は雛形の役割名に寄せる（HTTP スライスのみ）

雛形が `Create/{Command,Endpoint,Handler,SampleCreated}.cs` である以上、HTTP スライスの
操作フォルダは `Endpoint.cs` / `Command.cs` を使う。**型名は 1 つも変えない**
（`CreateDocumentRequest` などは公開契約に現れる形であり、改名は振る舞いの変更に当たり得る）。

購読者・常駐ワーカー・収集器には雛形に対応する役割名が無く、**既存のファイル名が既に役割を
名乗っている**（`*Consumer.cs` / `*HostedService.cs` / `*Collector.cs`）ため、そのまま持ち上げる。

### 判断 4 — `GraphEndpoints.cs` は分割する。順序は逐語で運ぶ

`Features/Graph/GraphEndpoints.cs` の `GET /graph/{id}/neighbors` は、
🔴 **`hops` / `types` の検証を認可より前に置く**という順序そのものが存在秘匿
（ADR-0034 決定 2）の実装であり、入れ替えると `hops=99` を投げるだけで文書の実在が判る。
`GraphEndpointsSecrecyTests` がこれを固定している。

**分割するが、ラムダ本体は逐語で移す。** 検証と認可は同じラムダの中にあるので、ラムダを
丸ごと動かせば順序は構造的に保たれる。⚠ CodeQL 注記（`cs/user-controlled-bypass` は
**バイパスではない**という説明）も逐語で運ぶ。

**唯一の書き換えは `NotFound()` → `GraphEndpoints.NotFound()`** である。404 の生成点は
**1 つでなければならない**（ADR-0034 決定 2。本文・ヘッダに差が出ると存在が読める）ので、
操作フォルダへ複写せず集約直下のヘルパを明示参照する。`Results.NotFound()` を操作側へ
直接書き戻さないこと。

🔴 **副作用の申告（2026-08-30 訂正）**: CodeQL のアラート **#22 / #23**
（high・`GraphEndpoints.cs:94`）は**パスに紐づいて採番されている**。ファイルが動けば
旧番号は `fixed` になり、新パスで採番し直される —— これは IADR-0282 の VSA 移送で
**実測済みの挙動**である（#16 / #17 → #22 / #23。
`.ai-context/specs/20260830_issue-1019_codeql-open-alerts.md` §実測 2）。

**［訂正］当初ここへ「#22 / #23 は `dismissed` ではなく `open` なので、握り潰した判断が
失われることは無い」と書いたが、これは誤りである。** 出典にした #1019 の仕様書は
2026-08-30 08:00 頃の実測であり、**その後 05:41 に repo 所有者（endazon）が
`false positive` として dismiss していた**（`gh api .../code-scanning/alerts/23` で実測）。
規則 10「是正のたびに『この変更で新たに誤りになる自分の記述』を引き直す」を、
**出典側の状態変化に対しては効かせられていなかった**。

**したがって dismiss は実際に失われる。** 移送後の実測:

```console
$ gh api ".../code-scanning/alerts?state=&ref=refs/heads/develop" --jq ...
#23 dismissed cs/user-controlled-bypass .../Features/Graph/GraphEndpoints.cs:94
#22 dismissed cs/user-controlled-bypass .../Features/Graph/GraphEndpoints.cs:94
$ gh api ".../code-scanning/alerts?ref=refs/pull/1084/merge" --jq ...
#26 open dismissed=null cs/user-controlled-bypass .../Features/Graph/Neighbors/Endpoint.cs:45
#25 open dismissed=null cs/user-controlled-bypass .../Features/Graph/Neighbors/Endpoint.cs:45
```

**指摘の内容も行も同じで、変わったのはパスと採番だけである**（`GraphEndpoints.cs:94` の
`hops` 検証 → `Neighbors/Endpoint.cs:45` の同じ行）。**#25 / #26 へ同じ理由で
dismiss を打ち直す必要がある。** dismiss は repo 所有者が自分で行った操作であり、
**本作業では代行しない**（人へ返す）。`CodeQL` は必須チェックではない
（`develop` の必須は `build-and-test` / `lint` / `commit-messages` / `pr-title` /
`image-build` / `static-checks-units` / `claude-review` / `scripts-tests` の 8 件。実測）ので
マージは塞がない。

### 判断 5 — `Features/` の外へは 1 ファイルも出さない

`DocumentObjectPurger` / `PrivateNoteUsage` / `SyncTokens` / `LinkEdgeSynchronizer` /
`AiSuggestionGenerator` は「`Features/` に居るべきか」を問う余地があるが、
**それは深さの話ではない**。ADR-0065 決定 2 の射程外として集約直下に残し、追随の候補として
PR に列挙する。

## 移送表

### DocumentService

| 現在 | 移送先 | 判断 |
| --- | --- | --- |
| `Documents/DocumentEndpoints.cs` | 集約直下に合成点として残す ＋ 11 操作へ分割 | 判断 1・2 |
| ↳ `GET /` | `Documents/List/Endpoint.cs` | |
| ↳ `GET /{id}` | `Documents/GetById/Endpoint.cs` | |
| ↳ `POST /` | `Documents/Create/{Endpoint,Command}.cs` | `CreateDocumentRequest` は本経路専用 |
| ↳ `PUT /{id}` | `Documents/Update/{Endpoint,Command}.cs` | |
| ↳ `PATCH /{id}/metadata` | `Documents/UpdateMetadata/{Endpoint,Command}.cs` | |
| ↳ `POST /{id}/publish` | `Documents/Publish/Endpoint.cs` | |
| ↳ `POST /{id}/archive` | `Documents/Archive/Endpoint.cs` | |
| ↳ `PUT /{id}/body` | `Documents/PutBody/{Endpoint,Command}.cs` | |
| ↳ `GET /{id}/versions` | `Documents/ListVersions/Endpoint.cs` | |
| ↳ `GET /{id}/versions/{v}` | `Documents/GetVersion/Endpoint.cs` | |
| ↳ `DELETE /{id}` | `Documents/Delete/Endpoint.cs` | |
| `Documents/DocumentShareEndpoints.cs` | 集約直下に合成点 ＋ `ListShares` / `GrantShare` / `RevokeShare` | 集約は `Documents` のまま（判断: `/documents/{id}/shares` は文書の従属資源で、認可も `DocumentBodyIntake.CanWrite` を本文書き込みと共有する） |
| `Documents/DocumentNormalizedConsumer.cs` | `Documents/Catalog/`（同名で移動） | 1 メッセージ = 1 ユースケース。段名は `catalog` |
| `Documents/DocumentObjectPurger.cs` | **集約直下に残す** | `Delete`（本集約）と `PrivateNotes/Purge`・`Maintenance`（別集約）が共有 |
| `McpTools/McpToolEndpoints.cs` | `McpTools/Declare/Endpoint.cs` | 1 経路 = 1 操作 |
| `McpTools/McpToolContracts.cs` | **集約直下に残す** | 申告の語彙（候補・除外規則）。操作をまたぐ |
| `ObsidianSync/ObsidianSyncEndpoints.cs` | 合成点 ＋ `Manifest` / `Push` / `Pull` / `Delete` | `ResolveDeviceAsync` / `FindOwnedAsync` は 4 操作共有 → 残す。`ApplyEditsAsync` / `ContentHashOf` は Push 専用 → 移す |
| `PrivateNotes/PrivateNoteEndpoints.cs` | 合成点 ＋ `List` / `Create` / `SoftDelete` / `Restore` / `Purge` / `SetExposure` / `GetQuota` / `SetQuota` | `PrivateNoteDefaults` / `SubjectOf` / `FindOwnedAsync` / `ActivePathExistsAsync` / `ToDto` / `QuotaExceededProblem` / `PathConflictProblem` は共有（一部は ObsidianSync からも呼ばれる）→ 残す |
| `PrivateNotes/PrivateNoteMaintenanceService.cs` | `PrivateNotes/Maintenance/` | 定期処理は 1 ユースケース（1 周期 = 1 実行）。常駐ワーカーも同居 |
| `PrivateNotes/PrivateNoteUsage.cs` | **集約直下に残す** | `SyncTokens` / `PrivateNoteUsage`。3 集約（PrivateNotes / ObsidianSync / SyncDevices）が使う |
| `SyncDevices/SyncDeviceEndpoints.cs` | 合成点 ＋ `List` / `Issue` / `Reissue` / `Revoke` / `RevokeAll` | `FindOwnedAsync` / `ToDto` は共有 → 残す |
| `Tags/TagDictionaryEndpoints.cs` | 合成点 ＋ `List` / `Create` / `Rename` / `Delete` | `LoadWithUsageAsync` は `List` 専用 → 移す |

### GraphService

| 現在 | 移送先 | 判断 |
| --- | --- | --- |
| `AiSuggestions/AiSuggestionEndpoints.cs` | 合成点 ＋ `List` / `Approve` / `Reject` / `Generate` | `ResolveEndpointsAsync` / `IsSourceWritableAsync` / `ToDto` / `NotFound` / `AnyState` は共有 → 残す |
| `AiSuggestions/AiSuggestionGenerator.cs` | **集約直下に残す** | 生成器（判断 5）。DI 登録され、テストから端点を介さず直接使われる |
| `EdgeTypes/EdgeTypeEndpoints.cs` | 合成点 ＋ `List` / `Catalog` / `Create` / `Rename` / `Delete` | `ExistsAsync` / `UsageOfAsync` / `Conflict` は共有 → 残す。`LoadWithUsageAsync` は `List` 専用、`LoadCatalogAsync` は `Catalog` 専用 → 移す |
| `Graph/GraphEndpoints.cs` | 合成点 ＋ `GetNode` / `Neighbors` / `CreateEdge` | 判断 4。`NotFound()`（**1 種類の 404 しか返さない**）は 3 操作共有 → 残す |
| `GraphDocuments/GraphDocumentSyncConsumer.cs` | `GraphDocuments/Sync/` | 1 メッセージ = 1 ユースケース |
| `GraphDocuments/DocumentDeletedConsumer.cs` | `GraphDocuments/Delete/` | 同上 |
| `GraphDocuments/LinkEdgeSynchronizer.cs` | **集約直下に残す** | 同期器（判断 5） |
| `KnowledgeHealth/{Collector,HostedService}.cs` | `KnowledgeHealth/Report/` | 定期の観測値報告という 1 ユースケース（ワーカー＝契機、収集器＝ハンドラ） |
| `McpTools/McpToolEndpoints.cs` | `McpTools/Declare/Endpoint.cs` | DocumentService と同じ |
| `McpTools/McpToolContracts.cs` | **集約直下に残す** | 同上 |

## やらないこと（明示）

- 振る舞いの変更（判定順・認可・応答本文・status・route・イベント）
- 型名・route・`WithName` の改名
- 2 段目（集約）の切り直し・新設・統合
- `Features/` の外への移動（判断 5）
- `Tests/` の再配置（#1063）。**テストは名前空間・using の追随だけ**を行う
- 他サービスの `Features/`（別 PR）

## 受け入れ基準

1. `Services/{DocumentService,GraphService}/Features/` の深さ 2 のディレクトリが 0 でない。
2. 各操作フォルダに、そのユースケースの Endpoint（または Consumer / Worker）が同居している。
3. 集約（2 段目）の数と名前が移送前と一致する（6 / 6）。
4. `dotnet build src/knowledge/backend/backend.slnx` が成功する。
5. `dotnet test src/knowledge/backend/backend.slnx` の**件数が移送前と一致**する。
   移送前（実測 2026-08-30）: `DocumentService.Tests` **233 合格 / 233 合計**、
   `GraphService.Tests` **275 合格 / 275 合計**、
   `Knowledge.IntegrationTests` **36 合格 / 41 スキップ / 77 合計**。
6. `dotnet format ... --verify-no-changes` が通る。
7. `node scripts/check-unit-dependencies.js` の違反 0 件。
8. `check-commit-messages` / `check-trace-blocks` / `check-doc-links` /
   `gen-knowledge-graph --check` / `scripts.test.js` が緑。

## 母集合の引き方（`.claude/rules/traceability.repo.md` §是正・追随の母集合の取り方）

移送で名前空間が変わるため、**変わる側の文字列**（`DocumentService.Features.<集約>` /
`GraphService.Features.<集約>`）で追跡下の全ファイルを走査し、参照元を列挙してから直した。
記憶で「Program.cs とテスト」と挙げていない（規則 9）。

```console
$ git ls-files -z | xargs -0 grep -l -E \
    "DocumentService\.Features\.(Documents|McpTools|ObsidianSync|PrivateNotes|SyncDevices|Tags)|GraphService\.Features\.(AiSuggestions|EdgeTypes|Graph|GraphDocuments|KnowledgeHealth|McpTools)"
→ 43 件（.cs 41 件 ＋ deploy/helm/.../files/pipeline.json ＋ .ai-context/specs/20260828_issue-941_*.md）
```

🔴 **この走査が `.cs` 以外を 1 件掘り出した —— `deploy/helm/microservices-platform/files/pipeline.json`。**
同ファイルの `steps[].consumer` は **`typeof(TConsumer).FullName` と序数一致でなければ
ホストが起動しない**（`Platform.Shared.Infrastructure/Foundation/Pipeline/PipelineExtensions.cs:102`
と `WolverinePipelineExtensions.cs:78` が `InvalidOperationException` を投げる）。
名前空間を動かした 3 段（`catalog` / `graph-delete` / `graph-sync`）を追随させた。

**これはコンパイルでもローカルのテストでも捕まらない** —— 宣言を実際に読み込む
`PipelineDeclarationLoadedTests` は Docker 不在で skip される（実測 41 件の skip に含まれる）。
`scripts/validate-pipeline-config.js` の V6 も**型名の形式しか見ない**（実在は見ない）。
**規則 9 の走査だけがこれを見つけた。**

`.ai-context/specs/20260828_issue-941_*.md` は**確定済みの凍結記録なので書き換えない**
（`.claude/rules/traceability.repo.md` §凍結の射程）。当時の実測値としてそのまま残す。

## 実測（移送後・2026-08-30）

```console
$ find .../DocumentService/Features .../GraphService/Features -mindepth 1 -maxdepth 1 -type d | wc -l
12                       # 集約は 6 / 6 のまま（増減なし）
$ find ... -mindepth 2 -maxdepth 2 -type d | wc -l
54                       # 操作フォルダ（移送前は 0）
$ find ... -mindepth 2 -maxdepth 2 -name '*.cs' | wc -l
15                       # 集約直下に残した共有物（合成点 9 / 共有器 6）
```

テスト件数（`dotnet test src/knowledge/backend/backend.slnx`）—— **移送前と全プロジェクトで一致**:

| プロジェクト | 前 | 後 |
| --- | --- | --- |
| DocumentService.Tests | 233 / 233 | **233 / 233** |
| GraphService.Tests | 275 / 275 | **275 / 275** |
| Knowledge.IntegrationTests | 36 合格 / 41 skip / 77 | **36 / 41 / 77** |
| 他 9 プロジェクト | 各同数 | **各同数** |

`dotnet build`（成功・新規警告 0）／`dotnet format --verify-no-changes`（差分 0）／
`check-unit-dependencies`（違反 0・VSA 層分類 314 → **370** 件へ増）／
`check-trace-blocks` / `check-doc-links` / `gen-knowledge-graph --check` /
`REQUIRE_REPO_TESTS=1 scripts.test.js`（664 tests passed）／
`validate-pipeline-config.js`（OK）はいずれも緑。

## 🔴 踏んだ罠 —— `Features/Documents/Publish/` が `.gitignore` に飲み込まれた

**症状**: ローカルの `dotnet build` / `dotnet test` / `dotnet format` はすべて緑。
`git status` も clean。にもかかわらず **CI の `backend-build (knowledge)` が
`DocumentEndpoints.cs(10,42): error CS0234: The type or namespace name 'Publish' does not
exist` で落ちた**。

**原因**: `.gitignore:214` の `publish/`（Click-Once の出力。VisualStudio.gitignore 由来）。
Windows の git は既定で `core.ignorecase=true` なので **`Publish/` がこれに一致する**。
`git add -A` は `Features/Documents/Publish/Endpoint.cs` を**静かに拾わなかった**。

**なぜローカルで気付けないか**: ファイルは**作業ツリーには在る**ので、ビルドもテストも
format も通る。落ちているのは「追跡下にあるか」だけであり、**差分にもコミットにも
`git status` にも現れない**（`--ignored=matching` を明示しない限り）。CI は
clone した内容をビルドするので、そこで初めて存在しないファイルとして現れる。

**採った対処**: `.gitignore` の末尾へ、スライスの操作フォルダを再包含する 1 行を足した。

```gitignore
!**/backend/Services/*/Features/**/
```

**現実的な操作名を総当たりで当てた実測**（`git check-ignore -v`）:

| 対処前に飲み込まれた名前 | 対処後 |
| --- | --- |
| `Publish` `Release` `Debug` `Out` `Log` `Logs` `Obj` `TestResults` `Backup` `Express` | **救出済み** |
| `Bin`（`**/[Bb]in/*`）・`Packages`（`**/[Pp]ackages/*`） | **ファイルを直接塞ぐパターンなので、ディレクトリ再包含では救えない** |
| `Dist`（`src/.gitignore:2`） | **より深い .gitignore が勝つので救えない** |

残る 3 つは操作名として不自然なため、ファイル単位の再包含は足していない
（必要になったときに足す旨を `.gitignore` のコメントへ書いた）。

🔴 **#1062 は 12 以上のサービスを並列で移送している。同じ罠は他サービスでも踏み得る**
（`Publish` は文書・記事・提案のどれにも自然な操作名である）。本 PR の `.gitignore` の
1 行が先に着地すれば以後は起きない。**着地前に走っている PR は、コミット後に
`git status --ignored=matching --untracked-files=all -- <Features のパス>` で確かめられたい。**

## 追随の候補（本 PR では扱わない）

- `DocumentObjectPurger` / `PrivateNoteUsage`＋`SyncTokens` / `LinkEdgeSynchronizer` /
  `AiSuggestionGenerator` は器であり `Features/` の外（`Domain/` ないし `Infrastructure/`）が
  自然かもしれない。**深さの話ではない**ので別 issue（判断 5）。
- `Documents` 集約の共有 API（`/documents/{id}/shares`）を `DocumentShares` 集約として
  切り出すか。**集約の切り直しは ADR-0065 決定 2 の射程外**（判断: 従属資源であり認可も共有）。
- `AiSuggestionGenerator` を `Generate/` スライスの `Handler` として取り込むか（REPR の
  完成形。ただし DI 登録され端点を介さず検証される現状との兼ね合いがある）。

## 実装 ADR

**新設しない。** 本作業は ADR-0065 決定 2 の適用であり、判断 1〜5 はいずれも同決定の
「操作フォルダ ＝ 1 ユースケース」を具体化したものである。同型の先行 PR（#1061）も
IADR を新設していない。

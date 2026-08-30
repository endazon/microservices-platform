---
title: 作業仕様書 — ConversionService / IngestionService の Worker/ 中間層と .Worker 接尾辞を撤去する（#1061）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0065
  - IADR-0282
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30)
related_specs:
  - ./20260828_wave45-vsa-migration.md
issue: "#1061"
---

# 作業仕様書 — `Worker/` 中間層と `.Worker` 接尾辞の撤去

## 目的と射程

計画 `ADR-0065` 決定 6 が「**`Worker` は実行入口の形であり、ディレクトリ階層ではない**」と定めた。
残る 2 サービス（`ConversionService` / `IngestionService`）を、他の 12 サービスと同じ
**`Services/<Name>/<Name>.csproj` ＋ 直下 `Program.cs` ＋ `Domain/` `Features/` `Infrastructure/` `Tests/`**
の樹形へ移送する。

**純移送であり、挙動は 1 つも変えない。** 受け入れ基準は「テスト件数が移送前後で一致すること」で担保する。

### 射程外（本 PR で触らない）

- **`Features/<集約>/<操作>/` の 3 段化**（`ADR-0065` 決定 2）。同じサービスのファイルを触るため
  **本件 → 3 段化の順で直列に流す**（issue #1061 補足）。
- `Tests/` の鏡写し化（`ADR-0065` 決定 3）。
- 他 12 サービス（既に新樹形）。
- `src/ai-stock-trading`（submodule。別リポジトリ）。

## 計画側の確認（planning は隣接クローンを直接走査）

```console
$ cd /c/10_SourceCode/project-planning
$ sed -n '121,127p' projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
### 決定 6 — `Worker` は実行入口の形であり、中間ディレクトリを置かない
...
- **`Services/<Name>/Worker/` のような中間ディレクトリを置かない。** `.csproj` 名にも `.Worker` を付けない
- 区別を責務ではなくホストの主目的で行う点（2026-08-17 確定・実装 `IADR-0218` の実測）は変えない。
  **HTTP 面を持つことは `Worker` であることと矛盾しない。**
```

**`Api` / `Worker` の排他（実行入口は 1 サービスに 1 つ。`IADR-0219` 決定 2）は不変**であり、
改まるのは階層と命名だけである。両サービスとも `Microsoft.NET.Sdk.Web` のまま（自己申告
エンドポイントの HTTP 面を持つ。`IADR-0029`）で、`Program.cs` の中身は 1 行も変えない。

## 母集合（自分で引いた。規則 9・10）

追跡下の全ファイルを 2 つの検索語で走査した（`src/ai-stock-trading` は submodule のため除外）。

```console
$ git grep -l -I -E "(Conversion|Ingestion)Service\.Worker|Services/(Conversion|Ingestion)Service/Worker" \
    -- . ':(exclude)src/ai-stock-trading' | wc -l
160
$ # 上と同じ検索語で、2 サービス配下を除いた件数
83
```

| 区分 | 件数 | 扱い |
| --- | --- | --- |
| 2 サービス配下（移送対象そのもの） | **77** | `git mv` ＋ 名前空間・パス置換 |
| 追随して直すもの（後述の表） | **28** | 本 PR で更新 |
| 凍結記録として除外 | **55** | 触らない（後述） |
| 合計 | **160** | |

### 追随して直す 28 件

| ファイル | 直す理由 |
| --- | --- |
| `src/knowledge/backend/backend.slnx` | プロジェクトパスと名前 |
| `src/Directory.Build.props` | `XUnit1051Migrated` のテストプロジェクト名 2 件 |
| `src/coverage-floor.json` | 注記が指すテストプロジェクト名 |
| `src/README.md` | 「Worker は `Services/<Name>/Worker/<Name>.Worker.csproj` を残す」条文が**本変更で新たに誤りになる**（規則 10） |
| `src/knowledge/backend/Tests/Knowledge.IntegrationTests/Knowledge.IntegrationTests.csproj` | `ProjectReference` 2 件 |
| 同 `Fixtures/IntegrationTestFactory.cs` / `Fixtures/RawDocumentFetchedEdge.cs` / `Messaging/DocumentUpdatedFanOutTests.cs` / `Messaging/QueueOverrideFanOutTests.cs` | `using` と型の完全名 |
| `src/platform/backend/Bff/Platform.Bff.Tests/DriftDetectorTests.cs` | 検体の consumer 完全名 |
| `src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/` 3 ファイル | 同上・コメントのテストプロジェクト名 |
| `deploy/docker-compose.yml` | `dockerfile:` パス 2 件 |
| `deploy/helm/microservices-platform/files/pipeline.json` | **`consumer` は型の完全名で起動時 fail-fast の照合対象**。名前空間が変わるので必須 |
| `scripts/k8s-local-images.sh` | Dockerfile パス 2 件 |
| `scripts/backend-library-baseline.json` | csproj パスをキーに持つ ratchet |
| `scripts/xunit1051-baseline.json` | 同上（`project` 値も） |
| `docs/functional/FR-02_ingestion.md` / `FR-12_document-normalization.md` | 実装・テストプロジェクト名 |
| `docs/screens/SC-07_conversion-jobs.md` | ソースパス |
| `docs/tech/tech-requirements.md` | テストプロジェクト名 |
| `docs/tests/FR-02_ingestion.md` / `FR-11_llm-egress-routing.md` / `FR-12_document-normalization.md` / `FR-14_composability.md` / `SC-07_conversion-jobs.md` / `TEST_STRATEGY.md` | テストプロジェクトのパス・名前 |

> `docs/tests/*` の一部は **`Services/<Svc>/tests/<Svc>.Worker.Tests` という既に存在しない旧パス**
> （`IADR-0282` 移送前の形）を指している。同じ行を直すので、あわせて現行パスへ合わせる。

### 除外した 55 件と理由

| 区分 | 件数 | 除外理由 |
| --- | --- | --- |
| `.ai-context/adr/` | 8 | 凍結記録。本文プロズを後から書き換えない（`CLAUDE.md`・`traceability.repo.md`） |
| `.ai-context/specs/` | 44 | 同上（確定済み作業仕様書） |
| `.ai-context/superpowers/` | 1 | 同上（経過追記も不可） |
| `CHANGELOG.md` | 1 | 自動生成物。手で書き足さない（`CLAUDE.md`「補助成果物の自動生成」） |
| `docs/how-to/session-handoff.md` | 1 | 「`git grep -l '.Api.'` が `.Worker.Composable` を拾わなかった」という**過去の検索語そのものを記録した教訓**。書き換えると記録が偽になる |

**`src/README.md` の「現行実態（移送波までの経過措置）」図（`src/<ServiceName>.<Api|Worker>/` 等）は
本変更で新たに誤りになるのではなく、`IADR-0282` の時点で既に古い**。本 PR の射程外とし、
別途起票する（規則 10 の「新たに誤りになる自分の記述」には当たらない）。

## 変更内容

### 1. ディレクトリとプロジェクトの移送（`git mv`）

```
Services/ConversionService/Worker/ConversionService.Worker.csproj
  → Services/ConversionService/ConversionService.csproj
Services/ConversionService/Worker/{Program.cs,appsettings.json,Dockerfile,TestMarker.cs}
  → Services/ConversionService/
Services/ConversionService/Worker/{Domain,Features,Infrastructure}/
  → Services/ConversionService/
Services/ConversionService/Worker/Tests/ConversionService.Worker.Tests.csproj
  → Services/ConversionService/Tests/ConversionService.Tests.csproj
Services/ConversionService/Worker/Tests/**（残り）
  → Services/ConversionService/Tests/
```

`IngestionService` も同型。**`git mv` で移送し、リネーム検出が効く形にする**（レビュー可能性のため）。

### 2. 名前空間（`IADR-0282` 決定 3 / `ADR-0065` 決定 1）

ルート名前空間は `<Name>`（接尾辞を含まない）。実測した現行名前空間 16 種から `.Worker` を落とす。

```
ConversionService.Worker            → ConversionService
ConversionService.Worker.Domain     → ConversionService.Domain
ConversionService.Worker.Domain.Ports
ConversionService.Worker.Features.ConversionJobs
ConversionService.Worker.Infrastructure.{ExternalServices,Messaging,Persistence}
ConversionService.Worker.Migrations
ConversionService.Worker.Tests
IngestionService.Worker(.Domain[.Ports] / .Features.Ingestion /
  .Infrastructure.{ExternalServices,Messaging} / .Tests)
```

**移送で `Services/<Svc>/{Domain,Features,Infrastructure}/**.cs` が
`check-unit-dependencies.js` 規則 3-③（VSA 層の名前空間参照方向）の対象に新たに入る。**
着手前に走査したところ `Domain → Features|Infrastructure|Common.Behaviors` および
`Infrastructure → Features` の `using` は **0 件**であり、移送だけで違反は生じない。

### 3. 生成物・宣言の追随

- Dockerfile の `restore` / `publish` / `ENTRYPOINT`（`ConversionService.dll`）
- `deploy/helm/.../files/pipeline.json` の `consumer` 完全名 2 件
- ratchet 系 JSON（`backend-library-baseline` / `xunit1051-baseline`）のキーと値
- `Directory.Build.props` の `XUnit1051Migrated`

### 4. 空の残骸

issue が挙げた `ConversionService/src/`・`tests/`・`IngestionService/src/`・`tests/` は
**本 worktree には存在しない**（`ls -la` で確認済み。`git status` も clean）。作業なし。

### 5. 実装 ADR は作らない

**本件は計画 `ADR-0065` 決定 6 の実行であり、実装側で決める余地が無い。**
`IADR-0282` 決定 1 が置いた「Worker は `Services/<Name>/Worker/` に残す」という例外は、
上位の計画 ADR に**上書きされた**。`.ai-context/adr/` の凍結記録は書き換えないため、
経緯は本仕様書と PR に残す。

## 受け入れ基準

- [x] `Services/ConversionService/` 直下に `ConversionService.csproj` と `Program.cs` があり `Worker/` が無い
- [x] `Services/IngestionService/` 同上
- [x] 両サービスの `Domain/` `Features/` `Infrastructure/` `Tests/` がサービス直下にある
- [x] `backend.slnx` に `.Worker` 接尾辞のプロジェクトが 0 件
- [x] `dotnet build src/knowledge/backend/backend.slnx` が成功
- [x] `dotnet test src/knowledge/backend/backend.slnx` が成功し、**件数が移送前と一致**
      （移送前実測 2026-08-30: 合計 **1253**（合格 1210 / スキップ 43）・12 テストプロジェクト）
- [x] `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` が差分なし
- [x] `node scripts/check-unit-dependencies.js` 違反 0 件
- [x] `node scripts/check-image-mapping.js` / `check-adr-numbering.js` /
      `check-commit-messages.js` / `check-trace-blocks.js` / `check-doc-links.js` 緑
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 緑
- [x] 旧パス・旧名の残存が、上で除外した 55 件（凍結記録・自動生成物）以外に 0 件

## 検証記録（2026-08-30 実走）

### テスト件数の突合（純移送の担保）

移送**前**（`origin/develop` = `e286fd52` 時点）と**後**で `dotnet test src/knowledge/backend/backend.slnx`
の per-project 件数が**完全に一致**した（12 テストプロジェクト・合計 **1253**／合格 **1210**／スキップ **43**）。

| テストプロジェクト | 前 | 後 |
| --- | --- | --- |
| `ConversionService(.Worker).Tests` | 81（合格 79 / スキップ 2） | 81（同） |
| `IngestionService(.Worker).Tests` | 28 | 28 |
| `Knowledge.IntegrationTests` | 77（合格 36 / スキップ 41） | 77（同） |
| `DocumentService.Tests` | 233 | 233 |
| `GraphService.Tests` | 275 | 275 |
| `DataSourceService.Tests` | 166 | 166 |
| `RetrievalService.Tests` | 156 | 156 |
| `AiAnalysisService.Tests` | 95 | 95 |
| `WikiService.Tests` | 64 | 64 |
| `DashboardService.Tests` | 30 | 30 |
| `Knowledge.Contracts.Tests` | 27 | 27 |
| `FeedbackService.Tests` | 21 | 21 |
| **合計** | **1253** | **1253** |

> スキップ 41 件は Testcontainers（Docker API 不在）由来で移送前から同数。2 件は変換の統合系。

### 実行した検査

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/knowledge/backend/backend.slnx` | 0 エラー（警告 3 は移送前から同一。`MinioBuilder` の CS0618） |
| `dotnet test src/knowledge/backend/backend.slnx` | 緑・件数一致（上表） |
| `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` | 差分なし |
| `dotnet test src/platform/backend/backend.slnx` | `Platform.Shared.Infrastructure.Tests` 242 ほか 6 プロジェクト緑。**`Platform.Bff` 系はビルド不可**（後述） |
| `node scripts/check-unit-dependencies.js` | OK（csproj 39 / .cs 797・VSA 層分類 **314**。違反 0） |
| `node scripts/check-image-mapping.js` | OK（ドリフト 0） |
| `node scripts/check-adr-numbering.js` | OK |
| `node scripts/check-trace-blocks.js` | OK（158 件） |
| `node scripts/check-doc-links.js` | OK（996 件） |
| `node scripts/gen-knowledge-graph.js --check` | OK（in-repo エッジ 4474 件） |
| `node scripts/check-backend-libraries.js` / `check-xunit1051-ratchet.js` | OK（baseline のキー追随を確認） |
| `node scripts/check-event-topology.js` / `check-test-traceability.js` / `check-plan-id-qualification.js` / `check-doc-updated.js` / `check-doc-type-vocabulary.js` / `check-doc-status-vocabulary.js` / `check-unit-service-ownership.js` / `check-test-spec-coverage.js` / `check-nul-bytes.js` / `check-reading-budget.js` | 全 OK |
| `node scripts/validate-pipeline-config.js deploy/helm/.../pipeline.json` | OK（steps=8 / events=6） |
| `helm template deploy/helm/microservices-platform` | レンダリング成功（2460 行） |
| `node scripts/check-commit-messages.js` | OK（1 件） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | OK（**664 tests passed**） |

> **`check-doc-updated.js` は HEAD を読むため、コミット前の単体実行は緑になる**（#683 / IADR-0183）。
> コミット後の `scripts.test.js` が `docs/` 10 件の `updated:` 据え置きを検出したので、
> 同コミットへ amend して揃えた。**「コミット前に単体で緑だった」を根拠にしないこと。**

### 検証できなかったこと

- **`node scripts/check-deploy-manifests.js`**: `kubeconform` が PATH に無く実行不可
  （`helm` / `kubectl` は Rancher Desktop 由来で在る）。代替として `helm template` の
  レンダリング成功と `validate-pipeline-config.js` を実施した。**k8s マニフェストのスキーマ検査は CI に委ねる。**
- **`Platform.Bff` / `Platform.Bff.Tests`**: `Platform.Bff.csproj` が
  `src/ai-stock-trading`（未 populate の submodule）を `ProjectReference` するため、本 worktree では
  `CS0246: AiStockTrading` でビルドできない。**移送前から同じ**（本変更起因ではない）。
  同プロジェクトで触ったのは `DriftDetectorTests.cs` の検体文字列 1 箇所のみで、
  同じ検体形を持つ `Platform.Shared.Infrastructure.Tests`（242 件）は緑である。
- `git rev-parse --is-shallow-repository` = `false`（`git log` を出典に引ける状態であることを確認済み）。

### 残存する旧名（意図的）

`git grep` の再走査で、旧名が残るのは**除外した 55 件**（`.ai-context/` 53・`CHANGELOG.md`・
`docs/how-to/session-handoff.md`）と、**`src/README.md` の「移送波までの経過措置」図 1 行**
（`IngestionService.Worker.Composable.Steps` の例示。`IADR-0282` の時点で既に古い記述）だけである。

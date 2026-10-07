---
title: 作業仕様書 — SC-07 試験仕様書の MassTransit 前提の記述（実在しないテスト名を含む）と README の版を実物に合わせる（#1782）
type: spec
status: done
related_ids: [FR-02, FR-12, SC-07, NFR, ADR-0027, IADR-0137]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1782"
---

# 作業仕様書 — SC-07 試験仕様書と README の記述を実物に合わせる（#1782）

> 本仕様書は編集着手前に作成した（着手 2026-10-08）。基点は MSP `origin/develop` `f7f74918`
> （#1779・#1794 のマージ後）。計画は project-planning `aa068ac`（隣接クローン・読み取り専用）。
> 文書のみの是正であり、コード・テスト・ワークフローには触れない。

## 起点（トレーサビリティ）

- 試験仕様書 `docs/tests/SC-07_conversion-jobs.md`（SC-07 / FR-12。issue の件名は FR-02,NFR で起票されているためコミットのスコープは両方を載せる）。
- 計画 ADR-0027（Wolverine 採用。#441 E1 で `RawDocumentFetched` の購読を Wolverine へ移した）。
- IADR-0137（試行上限の単一情報源）。本 IADR の本文は MassTransit 時代の記述のまま凍結記録として残す（書き換えない）。

## 受け入れ基準

- AC1: SC-07 の記述（:64 / :151 / :152 / :219）が実物（Wolverine の口・実在するテスト名）に合う。書くテスト名はすべて試験ソースに実在する。
- AC2: `README.md` の技術スタックの版が実物（`package.json`・`Directory.Build.props`）に合う。
- AC3: `check-test-name-references` と `check-doc-links` ほか文書系検査が通る。🔴 `check-test-name-references` は **src の C# の注記だけ**を走査し `docs/` の試験仕様書を見ない（是正前の develop でも OK を返した。実測）ため、本件の誤りは検出しない。SC-07 の名前は手で `git grep` して実在を確かめた（下表）。
- AC4（否定形）: 他 issue の作業仕様書・superpowers・IADR 本文（凍結記録）を書き換えない。

## 実物の確認（出典は path:line。基点 `f7f74918`）

| 事実 | 出典 |
| --- | --- |
| `RawDocumentFetchedConsumer` は Wolverine のハンドラ（`Handle(RawDocumentFetched, Envelope, CancellationToken)`） | `src/knowledge/backend/Services/ConversionService/Features/ConversionJobs/Normalize/RawDocumentFetchedConsumer.cs:30` |
| 試行上限の判定は `envelope.Attempts >= WolverineExtensions.MaxAttempts` | 同 `:108-109` |
| ConversionService は `opts.UsePlatformMessagingDefaults()` を呼ぶ | `src/knowledge/backend/Services/ConversionService/Program.cs:189` |
| 再試行の間隔 2s / 10s / 30s・`MaxAttempts => RetryIntervals.Length + 1` | `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/WolverineExtensions.cs:39-44` |
| `UsePlatformMessagingDefaults` が `.RetryWithCooldown(RetryIntervals).Then.MoveToErrorQueue()` | 同 `:136` / `:161-162` |
| 実在するテスト `Consume_failure_on_last_attempt_marks_dead_lettered` | `src/knowledge/backend/Services/ConversionService/Tests/Features/ConversionJobs/Normalize/RawDocumentFetchedConsumerJobTests.cs:164` |
| 実在するテスト `MaxAttempts_contract_constant_matches_platform_retry_policy`（突き合わせ先は `WolverineExtensions.MaxAttempts`） | 同 `:196` / `:206` |
| 実在するテスト `再試行既定_試行上限に達して初めてデッドレターへ移る` | `src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Foundation/Extensions/WolverineExtensionsTests.cs:210` |
| `Consume_failure_exhausting_retries_marks_dead_lettered` は**どの .cs にも無い** | `git grep -n Consume_failure_exhausting_retries -- '*.cs'` が空 |
| React 19 / Vite 6 / TypeScript 5.6 | `src/package.json:88` (`react ^19.2.0`) / `:90` (`typescript ^5.6.2`) / `:92` (`vite ^6.4.3`)、`src/platform/frontend/package.json:25` / `:45` / `:46` |
| .NET 10 / C# 13 | `src/Directory.Build.props:3`（`net10.0`）/ `:6`（`LangVersion 13`） |
| 発行がまだ MassTransit のサービス（README:22 の根拠） | `src/knowledge/backend/Services/ConversionService/Program.cs:155`・`DocumentService/Program.cs:299`・`IngestionService/Program.cs:130`（いずれも `AddMassTransit`） |

## 是正（旧 → 新）

| 箇所 | 旧 | 新 |
| --- | --- | --- |
| SC-07 :64 | `Consume_failure_exhausting_retries_marks_dead_lettered` | `Consume_failure_on_last_attempt_marks_dead_lettered` |
| SC-07 :151 | 本番と同じ試行上限で消費させ最後の失敗で標識（`Fault<T>` 発行で待つ）／実在しない名前 | `Envelope.Attempts` を 1〜上限で直に与え、上限未満で立たず上限で立つ。ランタイムが諦める回は基盤の再試行既定の試験が測る／実在する名前 |
| SC-07 :152 | `UsePlatformRetry` | `UsePlatformMessagingDefaults` の試行上限 `WolverineExtensions.MaxAttempts` |
| SC-07 :219 | MassTransit の再試行→デッドレター | Wolverine の再試行（`UsePlatformMessagingDefaults`。2/10/30 秒）→ `MoveToErrorQueue` |
| README :176 | React 18 … Vite 5 | React 19 … Vite 6 |
| README :22 | イベント（RabbitMQ / MassTransit） | イベント（RabbitMQ / Wolverine。移行中のため一部の発行はまだ MassTransit） |

## 母集合（規則 9 / 10。誤りの側の文字列で全走査）

走査範囲は追跡下の生きた文書（`docs/`・各 `README.md`・`CLAUDE.md`・`scripts/README.md`・`templates/`）。
`.ai-context/`（凍結記録）と `CHANGELOG.md`（生成物）は対象外として**ヒットだけ記録**する。

### 1. `Consume_failure_exhausting_retries`（`git grep -n`）

| ヒット | 判断 |
| --- | --- |
| `docs/tests/SC-07_conversion-jobs.md:64` / `:151` | **是正** |
| `.ai-context/specs/20260806_issue-533_conversion-dead-letter.md:163` / `:262` / `:266` | 除外（他 issue の凍結記録。当時は実在した） |

### 2. `UsePlatformRetry`（`git grep -n -- ':!*.cs'`）

| ヒット | 判断 |
| --- | --- |
| `docs/tests/SC-07_conversion-jobs.md:152` | **是正** |
| `docs/tech/tech-requirements.md:523` | 除外（「2026-08-21。#455 Phase 0 / U0a」の日付つき経過記述。当時の配線として正しい。`UsePlatformRetry` 自体も `MassTransitExtensions.cs:28` に実在する） |
| `.ai-context/adr/IADR-0137`（4 件）・`IADR-0478:167`・`.ai-context/adr/README.md:197`・`.ai-context/specs/` 4 ファイル | 除外（凍結記録） |
| `scripts/check-backend-libraries.js:1119` / `:1148` | 除外（検査器の自己試験の入力文字列。文書ではない） |

### 3. `Fault<`（`docs/` と `*.md`、`.ai-context` 除く）

`docs/tests/SC-07_conversion-jobs.md:151` の 1 件のみ → **是正**。

### 4. `MassTransit`（生きた文書 22 ファイル）

| ヒット | 判断 |
| --- | --- |
| `docs/tests/SC-07_conversion-jobs.md:219` | **是正** |
| `README.md:22` | **是正**（購読は Wolverine。発行は 3 サービスでまだ MassTransit） |
| `CLAUDE.md:120`・`docs/operations/operations.md:1533`・`docs/tech/tech-requirements.md:240`・`scripts/README.md:32`・`templates/unit-template/README.md:110` | 除外（不採用ライブラリとしての言及。真） |
| `docs/data/data-source.md:25`・`docs/functional/FR-01_data-source-catalog.md:25`・`docs/functional/FR-02_ingestion.md:22`・`docs/tests/FR-01_data-source-catalog.md:23`・`docs/tech/system-architecture.md:28` | 除外（Superseded な計画 ADR の引用。後継併記の書式どおり） |
| `docs/tests/FR-12_document-normalization.md:65` | 除外（`MassTransitDocumentNormalizedPublisherTests` は実在する） |
| `docs/operations/operations.md:815` / `:819` | 除外（直下に 2026-10-06 の日付つき訂正があり、Wolverine の DLQ を書いている） |
| `docs/operations/operations.md:1386` | 除外（発行が MassTransit のサービスが残るため「MassTransit は再接続」は偽ではない） |
| `docs/tech/tech-requirements.md:290` / `:314-343` / `:437` / `:475` / `:484` | 除外（移行残件の計数・経過記述。日付つきの実測記録） |
| `docs/tests/TEST_STRATEGY.md:93-100` | 除外（コミット `bc7bc8e` 時点の実例として書かれた経過記述） |
| `docs/how-to/session-handoff.md:431`・`docs/how-to/run-integration-tests-without-docker.md:91`・`docs/tech/20260707_wikijs-poc-record.md:31` | 除外（当時の事象・PoC の記録） |
| `docs/screens/SC-11_configuration-viewer.md:115` | 除外（モックの表示ラベルの引用） |
| `docs/tech/composability-classification.md:115`・`docs/tech/composable-component-guide.md:54` / `:76`・`docs/functional/FR-14_composability.md:42` / `:52` / `:77`・`docs/tests/FR-14_composability.md:30`・`docs/tech/system-architecture.md:75`・`docs/tech/tech-requirements.md:58` | **除外（本 issue の射程外。issue §3 で任意と明記）**。`AddPlatformMassTransit` は `.cs` に実在しない（`git grep -n AddPlatformMassTransit -- '*.cs'` が空）ため**誤りではある**が、段の登録（`AddPlatformWolverineStep`）・発行の残存を含む可組み立て性ガイドの書き直しになり、SC-07 の是正と同型ではない。別 issue へ切り出す |

### 5. 版の文字列（`React ?18|Vite ?5|.NET ?8` ほか、生きた文書）

| ヒット | 判断 |
| --- | --- |
| `README.md:176` | **是正** |
| `docs/tech/system-architecture.md:32`・`docs/tech/tech-requirements.md:36` / `:432` / `:435` | 除外（「旧『計画は .NET 8』」の是正経緯の記述。現行の版を主張していない） |

### 6. 規則 10（この変更で新たに誤りになる自分の記述）

- SC-07 :151 の新記述が引く `再試行既定_試行上限に達して初めてデッドレターへ移る` は実在する（上表）。
- README :22 の「一部の発行はまだ MassTransit」は E 系の移行が進むと古くなる。**移行完了の PR で消す対象**として残す（README は `docs/` 外で日付の欄が無い）。
- SC-07 の `updated:` を 2026-10-08 へ進め、trace ブロックの `issues` に `#1782` を足した（表示テキストに ID を増やしていない）。

## 検証

`check-trace-blocks` / `gen-knowledge-graph --check` / `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` /
`check-reading-budget` / `check-doc-type-vocabulary` / `check-doc-status-vocabulary` / `check-doc-updated` / `check-commit-messages` /
`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。結果は PR 本文に記す。

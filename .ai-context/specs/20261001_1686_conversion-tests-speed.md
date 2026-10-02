---
title: 作業仕様書 — #1686 ConversionService.Tests を、試験を減らさず弱めずに速くする
type: spec
status: done
related_ids: [NFR, IADR-0490, IADR-0232]
author: Claude Opus 5.5 (worker)
created: 2026-10-01
updated: 2026-10-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
issue: "#1686"
---

# 作業仕様書 — #1686 ConversionService.Tests の高速化

## 起点となる計画書（トレーサビリティ）

- issue #1686（ci-latency の逆転）。2026-10-01 の利用者裁定の 2 番目「**ConversionService.Tests の高速化**: 約 45 秒かかる原因を調べてから縮める」。
  閾値は緩めない。**試験を消す・skip する・弱めることはしない**（検証の網羅を落とさない）。
- 起点 ID は **無採番の `NFR`**（CI の所要時間というメタ作業）。判断は **IADR-0490** に置く。
- 同じ裁定の 1 番目（ビルド成果物の再利用）・3 番目（集約ジョブの setup-node の撤去）は別 PR。本件は `.github/workflows/` を触らない。

## 編集前に確かめた事実（計測。2026-10-02・手元 4 コア）

計測の手順: `dotnet build` の後に `dotnet test <ConversionService.Tests> --no-build --filter "Category!=Integration" --collect:"XPlat Code Coverage" --logger trx`（CI の脚と同じフィルタとカバレッジ収集）。
試験ごとの所要は trx の `duration`、全体は `dotnet test` の `Duration`・trx の最初の開始から最後の終了まで（span）・コマンドの壁時計で測った。

- **ビルドと試験の分離**: 試験プロジェクトの初回ビルドは 41.8 秒（restore 済み・依存を含む）。試験の実行は単独で `Duration` 28〜33 秒。
  CI の 45 秒前後（#1686 のコメント）は、同じ脚の他の 3 試験プロジェクトと 4 コアを分け合う条件の値である。
- **試験は 232 件**（Passed 223 / Skipped 9。skip の 9 件は pandoc・pdftotext の実物が要る試験で、CI にも手元にも無い。本件の前後で不変）。
  このプロジェクトに `Category` の Trait は無い（`TestKind` だけ）ので、PR のフィルタ `Category!=Integration` は 1 件も除かない。
- **律速は 1 クラス**: 試験ごとの所要の合計は 49〜66 秒、そのうち `ExternalProcessTimeoutTests`（10 件）が **27.4〜28.8 秒**。
  xUnit は 1 クラス＝1 テストコレクションで、**コレクションの中は直列**に走る。このクラスの試験は実時間の期限
  （`HangTimeout` 2 秒・刈り取りの上限 `ExternalProcess.ReapTimeout` 10 秒）を待つことが検査の本体なので、待ちが足し算になり、
  全体の span（28〜33 秒）とほぼ同じ長さの 1 本の直列になっていた。他のクラスは並列に走り、このクラスの陰に隠れていた。
- 遅い試験の母集合（下の規則 9）を見ても、1 秒を超える試験の大半は**検査そのものが実時間の期限を待つ**もの（期限・刈り取り・Wolverine のハンドラの期限）か、
  WebApplicationFactory・Wolverine ホストの初回起動（負荷で 0.1〜3.6 秒に揺れる）である。前者は待ちを縮めると検査が変わる。後者は並列の陰に入っている。
- 候補として挙げられた他の原因は**該当しない**: Testcontainers・実ファイル変換の外部プロセス（pandoc は起動しない。`sh`/`sleep` の差し替え）・
  `[Collection]` による並列の無効化（変更前のプロジェクトには `[Collection]` も `xunit.runner.json` も無い）・大きな固定データ（golden 6 件・数 KB）。

## 設計

1. **`ExternalProcessTimeoutTests` を入れ子のクラス 6 つへ分ける**（外側は helper だけを持つ `static class`）。
   入れ子のクラスはそれぞれ別のコレクションになり、互いに並列に走る。試験メソッドの本文・期限・待ち・検査は 1 字も変えない（字下げだけ）。
   分け方は T-50 の番号の組: `HungConverter`（(1)〜(3)）/ `TimedOutJob`（(4)）/ `NormalExit`（(5)(6)）/ `VersionProbeTimeout`（(7)）/
   `ExitedBeforeTimeout`（(8)）/ `DetachedGrandchild`（(9)）。
2. **最長の試験を最初に起動する。** xUnit v3 の既定の順序づけはコレクションを無作為に並べる。分けた後の下限は
   `DetachedGrandchild`（期限 2 秒 ＋ 刈り取りの上限 10 秒 ≒ 12 秒）だが、無作為の順で後ろに回ると「開始の遅れ ＋ 12 秒」に伸びた
   （同じビルドで span 12.8〜16.7 秒。長い回は開始が 4.6 秒遅れていた）。試験プロジェクトに順序づけ `StartsFirstCollectionOrderer` を置き、
   `[StartsFirst]` の付いたコレクション定義を先頭へ寄せる（それ以外は既定の無作為な順のまま）。`DetachedGrandchild` だけを定義つきのコレクションに入れて印を付ける。
3. **本番コードは変えない。** `ReapTimeout` を注入できるようにすれば `DetachedGrandchild` の 12 秒も縮むが、
   試験が本番の上限（10 秒）を通らなくなる＝検査が変わるので採らない（IADR-0490 の却下案）。

### 網羅が落ちていないこと

- 試験の集合: 変更前後の trx で、試験名（入れ子のクラス名 `+X` を除いたもの）と結果の組 232 件が**完全に一致**した（Passed 223 / Skipped 9）。
- 試験の中身: 本文は字下げ以外の差分が無い（`git diff -w` で試験メソッドの行に差分が無い）。期限・待ちの値、検査の式、後始末は同一。
- 並列にしたことで共有される状態は無い: プロセス番号のファイルと原本は試験ごとに GUID の別名、helper は状態を持たない `static`、
  `RecordingLogger`・DB（InMemory の GUID 名）は試験ごとに生成する。
- 時間の検査は**下限**（`>= HangTimeout - 100ms`・`> timeout`・`>= HangTimeout + ReapTimeout - 200ms`）か**余裕つきの上限**（取り消しから 20 秒未満・刈り取りの上限 ＋ 10 秒未満）で、
  並列の負荷で遅くなる側には緩い。分けた後に、単独 22 回（カバレッジ収集あり 6 回を含む）・同じ脚の 4 プロジェクト同時実行 3 回のすべてで緑だった。
- 試験仕様書（`docs/tests/FR-12`・`UC-06`）と `scripts/test-spec-coverage-baseline.json` はクラスを**ファイル名**（`ExternalProcessTimeoutTests`）で引いており、ファイル名は不変。

## 母集合（規則 9・10）

**規則 9（遅い試験の母集合を記憶で挙げない）**: 変更前の 3 回の trx から「どれかの回で 1.0 秒以上」の試験を全件引いた（19 件）。

| 試験 | 変更前（秒・3 回の幅） | 変更後 | 遅さの種類 | 扱い |
| --- | --- | --- | --- | --- |
| ExternalProcessTimeoutTests.Detached_grandchild… | 12.03–12.07 | 12.09–12.10 | 期限 2 ＋ 刈り取りの上限 10 を待つのが検査 | 縮めない。最初に起動（設計 2） |
| DiagramCodingLimitsTests.受け口の期限の方針は… | 4.68–5.84 | 1.35–3.45 | 受け口の期限 1 秒 ＋ Wolverine の初回コード生成 | 変えない（別クラス・並列の陰） |
| ExternalProcessTimeoutTests.Output_is_kept… | 5.01 | 5.01 | 期限 2 ＋ 孫が握る 3 を待つのが検査 | 縮めない。並列化（設計 1） |
| PipelineStepRegistrationTests.構成なしのとき… | 0.22–3.59 | 0.32–2.50 | Wolverine ホストの初回起動（負荷で揺れる） | 変えない |
| ConversionFigureCorrectionTests.Correction_OnCodedFigure_Is409 | 0.24–3.14 | 0.08–0.14 | WebApplicationFactory の初回起動の揺れ | 変えない |
| DiagramCodingTimeoutPipelineTests.Exhausted_budget… | 2.01–3.01 | 2.02–3.56 | 総枠 2 秒を使い切るのが検査 | 変えない |
| RawDocumentFetchedConsumerTests.Consumer_publishes… | 0.00–2.65 | 0.01–1.67 | 初回起動の揺れ | 変えない |
| ConversionJobEndpointTests.Retry_NonFailedJob_Returns409 | 0.09–2.63 | 0.08–0.09 | 初回起動の揺れ | 変えない |
| ExternalProcessTimeoutTests.Version_probe…pandoc / …pdftotext | 2.03–2.30 | 2.03–2.11 | 期限 2 秒を待つのが検査 | 並列化（設計 1） |
| IntrospectionEndpointTests.Introspection_endpoint_reports_convert_step | 0.14–2.26 | 0.08–0.22 | 初回起動の揺れ | 変えない |
| ConversionJobStoreTests.PrepareRetry_returns_null_for_unknown_job | 0.00–2.23 | 0.00 | 初回（EF InMemory のモデル構築）の揺れ | 変えない |
| DiagramCodingTimeoutPipelineTests.Caller_cancellation_propagates… | 0.08–2.11 | 0.01 | 初回起動の揺れ | 変えない |
| ExternalProcessTimeoutTests.Timed_out_conversion… | 2.04–2.06 | 2.04–2.08 | 期限 2 秒を待つのが検査 | 並列化（設計 1） |
| ExternalProcessTimeoutTests.Hung_pdftotext… / Hung_pandoc… | 2.02–2.04 | 2.04–2.11 | 期限 2 秒を待つのが検査 | 並列化（設計 1） |
| ConversionJobAuthorizationTests.AdminOnlyRoutes…(/jobs/{id}/retry) | 0.12–1.27 | 0.11–0.15 | 初回起動の揺れ | 変えない |
| DiagramCodingTimeoutPipelineTests.Hung_gateway_keeps_the_figure… | 1.03–1.25 | 1.01–1.02 | ゲートウェイの期限 1 秒を待つのが検査 | 変えない |
| MassTransitDocumentNormalizedPublisherTests.引数は… | 0.04–1.09 | 0.03–0.58 | 初回起動の揺れ | 変えない |

除外: 1.0 秒未満の試験（213 件）。いずれも単独では律速にならない（合計しても並列の陰に入る）。
「実時間の期限を待つのが検査」の試験は、待ちを `TimeProvider` 等へ置き換えると**本番の実時間の期限・プロセスの kill・刈り取り**を検査しなくなるため、置き換えない。

**規則 10（この変更で新たに誤りになる自分の記述）**: 引き直した結果。

- `scripts/backend-test-shards.json` の `$comment` は「3/3 の 43 秒は ConversionService 1 本（4 並列の下で 41 秒）が下限である」「ConversionService 41.3 / 51.3」と書く。
  これは**日付つきの CI run の実測の記録**（run 番号つき）であり、当時の値として正しいので書き換えない。シャードの割り当ての見直しは CI で本件の効果を測ってから
  （ビルド成果物の再利用の PR と合わせて）行う。本件では触らない（残余）。
- `ExternalProcessTimeoutTests.cs` 冒頭の T-50 の注記（pandoc・pdftotext が要らない理由）は分けた後も正しい。クラス名を引く `.ai-context/specs/` の 2 本（#1641・#1654）は凍結記録であり、
  `ExternalProcessTimeoutTests.<メソッド名>` の表記は入れ子の後もメソッドを一意に指すので追随しない。
- 「Trait は付け忘れても PR に残る」（IADR-0232 決定 3）: 入れ子の各クラスに元と同じ `[Trait("TestKind", "Integration")]` を付けた（外側の Trait は入れ子へ継がれないため）。
  フィルタ `Category!=Integration` には掛からないので、PR で走るのは変更前と同じ。

## 受け入れ基準

- [x] ConversionService.Tests の試験の数と結果が変わらない（232 件・Passed 223 / Skipped 9。試験名の集合が一致）。
- [x] 試験の本文（期限・待ち・検査）を変えない。skip・削除・弱めは無い。本番コードは変えない。
- [x] 単独実行の所要（Duration）が 28〜33 秒 → 12〜13 秒。同じ脚の 4 プロジェクト同時実行でも 29〜35 秒 → 14〜17 秒。
- [x] `dotnet build src/knowledge/backend/backend.slnx -warnaserror` が通る。`dotnet format --verify-no-changes` が通る。
- [x] 文書系の検査器が通る（結果は下）。

## 結果（前後の計測。各 3 回・カバレッジ収集あり）

単独実行（`dotnet test` ConversionService.Tests のみ）:

| | 回 1 | 回 2 | 回 3 |
| --- | --- | --- | --- |
| 変更前 Duration / span / 壁時計（秒） | 33 / 33.2 / 37.0 | 28 / 28.4 / 31.2 | 29 / 29.8 / 32.0 |
| 変更後 Duration / span / 壁時計（秒） | 13 / 13.2 / 18.8 | 12 / 12.1 / 17.3 | 13 / 13.1 / 18.3 |

同じ脚の 4 試験プロジェクト（AiAnalysis / Conversion / Feedback / Knowledge.Contracts）を同時に走らせたときの ConversionService.Tests の Duration（CI の脚の近似）:
変更前 29 / 35 / 35 秒 → 変更後 14 / 16 / 17 秒。

上位 5 件（単独実行・秒）:

| 順 | 変更前 回 1 | 変更前 回 2 | 変更前 回 3 |
| --- | --- | --- | --- |
| 1 | Detached_grandchild 12.07 | Detached_grandchild 12.05 | Detached_grandchild 12.03 |
| 2 | 受け口の期限の方針 5.57 | 受け口の期限の方針 5.84 | Output_is_kept 5.01 |
| 3 | Output_is_kept 5.01 | Output_is_kept 5.01 | 受け口の期限の方針 4.68 |
| 4 | Exhausted_budget 3.01 | 構成なしのとき 3.59 | Correction_OnCodedFigure_Is409 2.10 |
| 5 | Retry_NonFailedJob_Returns409 2.63 | Correction_OnCodedFigure_Is409 3.14 | Caller_cancellation_propagates 2.10 |

| 順 | 変更後 回 1 | 変更後 回 2 | 変更後 回 3 |
| --- | --- | --- | --- |
| 1 | Detached_grandchild 12.09 | Detached_grandchild 12.10 | Detached_grandchild 12.09 |
| 2 | Output_is_kept 5.01 | Output_is_kept 5.01 | Output_is_kept 5.01 |
| 3 | 構成なしのとき 2.50 | Exhausted_budget 3.56 | 受け口の期限の方針 3.45 |
| 4 | Version_probe…pdftotext 2.11 | 受け口の期限の方針 2.86 | Exhausted_budget 2.46 |
| 5 | Version_probe…pandoc 2.11 | GetById_ExposesDeadLetterMarker… 2.16 | Hung_pandoc 2.08 |

試験ごとの所要は変わらず（待つのが検査の試験は同じ秒数）、**直列の足し算が並列になった**ことで全体が縮んだ。
変更後の下限は `DetachedGrandchild` の約 12 秒で、3 回とも開始 0.00 秒（順序づけの効果。順序づけ無しの分割だけでは開始が 0〜4.6 秒に揺れた）。

### 検証

- `dotnet build src/knowledge/backend/backend.slnx -warnaserror`: 0 警告・0 エラー（1 回目は並行ビルドの一過性のエラー 1 件で失敗し、再実行で緑。コード由来ではない）。
- `dotnet format src/knowledge/backend/Services/ConversionService/Tests/ConversionService.Tests.csproj --verify-no-changes`: 差分なし。
- 文書系の検査器: `check-trace-blocks` / `check-test-traceability` / `check-doc-links` / `check-reading-budget`（既存の 90% warn のみ）/ `check-test-spec-coverage` /
  `check-cross-repo-refs` / `check-plan-id-qualification` / `gen-knowledge-graph --check` / `check-doc-type-vocabulary` / `check-xunit1051-ratchet` はすべて OK。
- `check-adr-numbering`（と、それを呼ぶ `scripts.test.js`）は **IADR-0488〜0490 の欠番**だけで赤い。この 3 番号は並行する別作業が使う（コーディネータの割り当て）ので、
  それらが先にマージされれば解消する。欠番を一時の仮ファイルで埋めた状態では `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が 853 件すべて緑（仮ファイルは消した）。

## 計画書との差異

無し（計画書の記述に影響しない試験の構成の変更）。

## 残余

- CI での効果は未計測（push しないため）。ci-latency の週次 run とシャードの脚のログで確かめる。
- `DetachedGrandchild` の約 12 秒が ConversionService.Tests の下限として残る。縮めるには本番の刈り取りの上限を試験から差し替える口が要り、検査が変わるので採らない（IADR-0490）。
- `scripts/backend-test-shards.json` の割り当ては、ConversionService が軽くなった前提で組み替える余地がある（ビルド成果物の再利用の PR の後に CI の実測で）。

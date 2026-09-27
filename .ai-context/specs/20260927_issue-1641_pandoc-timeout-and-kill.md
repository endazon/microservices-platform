---
title: pandoc の呼び出しに時間切れとプロセスの停止を持たせ、止まった変換が Inline のキューを長くふさがないようにする（#1641）
type: spec
status: in-progress
related_ids: [FR-12, UC-06, SC-07, ADR-0012, ADR-0027, ADR-0070, IADR-0008, IADR-0320, IADR-0356]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-06 例外フロー（本文変換・資産保存の恒久失敗は再試行し、継続失敗はデッドレターへ送る）
  - planning:projects/microservices-platform/07_adr/ADR-0012（変換パイプライン・段階的コード化）
related_specs:
  - 20260927_issue-1621_diagram-coder-timeout-retain.md
  - 20260926_issue-1604_refresher-and-sync-loop-timeouts.md
issue: "#1641"
---

# 仕様書: pandoc / pdftotext の呼び出しに自前の期限とプロセスツリーの停止を持たせる（#1641）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- ユースケース: **UC-06**（文書を正規化変換する）例外フロー「本文変換・資産保存の恒久失敗は再試行し、継続失敗はデッドレターへ送る」
- 機能要求: **FR-12**（原本の正規化変換）、SC-07（変換ジョブの状況・失敗理由の表示）
- 関連 ADR: ADR-0012（変換パイプライン）、ADR-0027（Wolverine の受け口・再試行）、ADR-0070 決定 2（PDF はテキスト層の抽出器）
- 関連 IADR: **IADR-0008**（2026-09-27 追記 / #1621 の 3 つの時間の上限。本件はそこへ 4 つ目の上限を足す）、IADR-0320（pandoc の実行時イメージ・fail-closed）、IADR-0356（pdftotext）
- 起点 issue: #1641（PR #1624〔#1621〕の再監査で出た「止めない注記」。#1621 の作業仕様書「フォローアップ」に同じ指摘がある）

## 事実（着手前の実測）

`origin/develop` = `c343b9d7`（#1624 を含む）の上で読んだ。

- `PandocConversionService.RunPandocAsync` は `Process.Start` → `ReadToEndAsync(ct)` ×2 → `WaitForExitAsync(ct)`。**自前の期限は無い。**
  `ct`（受け口の ct）が立つと `WaitForExitAsync` が `OperationCanceledException` を投げて抜けるだけで、**プロセスは止めない**（`using` の `Dispose` はハンドルを閉じるだけで kill しない）。
  pandoc は受け口の外で走り続け、次の試行の pandoc と並ぶ。
- `PdfTextLayerConverter.RunPdfToTextAsync`（ADR-0070 決定 2。pandoc と同じ型の外部プロセス）も同じ形。
- 版の確かめ（`pandoc --version` / `pdftotext -v`。`ConvertAsync` の冒頭と readiness の両方が使う）も同じ形（こちらは `catch { return null; }` で包む）。
- 受け口の ct は Wolverine の 1 通ごとの実行期限（`RawDocumentFetched` は #1624 で **300 秒**。`RawDocumentFetchedTimeoutPolicy`）と停止要求の連結。
  再試行は `OnAnyException().RetryWithCooldown(2 s, 10 s, 30 s)`（`WolverineExtensions.UsePlatformMessagingDefaults`）で 1 回の配信 4 試行。
  したがって止まった pandoc は 1 通で最大 4 × 300 ＋ 42 ≒ **1242 秒**受け口をふさぐ（issue の見立てと一致）。
- 構成の注入: `Conversion:*` の鍵は helm（`deploy/helm/microservices-platform/values.yaml`）・compose（`deploy/docker-compose.yml`）・`appsettings*.json` の
  どこにも無い（`git grep -n "Conversion__\|DiagramCodingTimeoutSeconds\|HandlerTimeoutSeconds" -- deploy docs` が 0 行・appsettings に `Conversion` 節なし）→ **稼働構成は既定値で起動する**。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 本文変換の外部プロセス（pandoc・pdftotext）に**自前の期限**を与える（`Conversion:BodyConversionTimeoutSeconds`・既定 **90 秒**・下限 1 秒） | `DiagramCodingLimitsTests`（既定値・丸め）、`ExternalProcessTimeoutTests`（期限で止まる） |
| AC-2 | 起動時の検査を「受け口 ＞ **本文変換の期限** ＋ 総枠 ＋ 1 回」へ広げる（#1624 の「受け口 ＞ 総枠 ＋ 1 回」を包含）。既定（300 ＞ 90 ＋ 120 ＋ 20 ＝ 230）で稼働構成が起動する | `DiagramCodingLimitsTests.受け口の期限が本文変換と総枠と一回の期限の和を超えなければ起動を止める`・`本番の配線は四つの上限を既定値で張る` |
| AC-3 | 期限が来たら**プロセスツリーごと止め**（`Process.Kill(entireProcessTree: true)`）、刈り取り（`WaitForExit`）、`BodyConversionTimeoutException`（`TimeoutException` の派生）を送出する。子孫プロセスは残らない | `ExternalProcessTimeoutTests.Hung_pandoc_is_killed_with_its_process_tree_at_the_timeout`・`Hung_pdftotext_is_killed_with_its_process_tree_at_the_timeout` |
| AC-4 | 呼び出し元（受け口の ct）の取り消しでも**プロセスツリーごと止め**、取り消しは `OperationCanceledException`（その ct を運ぶ）として外へ出す。期限切れと呼び出し元の取り消しを取り違えない（両方立ったら呼び出し元を優先。#1604 / #1621 と同じ境界） | `ExternalProcessTimeoutTests.Caller_cancellation_kills_the_pandoc_process_tree_and_propagates` |
| AC-5 | 期限切れのジョブは**失敗として記録**され、理由（道具名・期限・構成鍵）が変換ジョブの失敗理由に残る。再試行 → デッドレターの扱いは不変（UC-06 例外フロー） | `ExternalProcessTimeoutTests.Timed_out_conversion_is_recorded_as_a_failed_job_with_the_reason` |
| AC-6 | 正常系は不変: 0 終了は標準出力を本文として返し、非 0 終了は従前どおり `InvalidOperationException("pandoc exited with code N …: <stderr>")` | `ExternalProcessTimeoutTests.Normal_pandoc_output_becomes_the_body`・`Non_zero_exit_keeps_the_existing_failure`、既存の `PandocConversionServiceTests`（pandoc 導入環境） |
| AC-7 | 試験は pandoc / pdftotext を要しない（CI に無い）。起動する命令を差し替える試験用の口（`StartInfoFilter`）で、止まる子孫つきのプロセス（Linux: `sh` ＋ `sleep`、Windows: `powershell` ＋ `ping 127.0.0.1`）を代わりに起動する。待ち受けはしない | 上の試験すべて |
| AC-8 | RabbitMQ の prefetch・`consumer_timeout` を変えるかを判定し、理由を記す | 下の「prefetch と consumer_timeout」 |
| AC-9 | IADR-0008 へ日付つき追記（本文は書き換えない）。新しい IADR 番号は取らない | 同 IADR の追記 |

## 設計

- **実行器** `ExternalProcess.RunAsync(psi, timeout, ct, logger)`（`Infrastructure/ExternalServices/ExternalProcess.cs`・internal static）を 4 か所
  （pandoc 変換・pandoc 版・pdftotext 抽出・pdftotext 版）で共用する。
  - 期限の CTS と `ct` を連結した `linked` で `WaitForExitAsync` と読み取りを待つ。
  - `linked` が立ったら `Kill(entireProcessTree: true)`（既に終わっていれば何もしない）→ 刈り取り（`WaitForExitAsync(None)` を上限 10 秒で待つ。越えたら警告ログ）→
    読み取りの `Task` を観測だけして捨てる（止めたプロセスの出力は使わない。パイプを子孫が握っていても待たない）。
  - 例外: `ct` が立っていれば `OperationCanceledException(ct)`（呼び出し元優先）、そうでなければ `BodyConversionTimeoutException`。
- **期限の値**は `DiagramCodingLimits`（#1624 が起動時の順序検査を持つ record）に `BodyConversionTimeout`（init プロパティ・既定 90 秒）として足す。
  位置引数は変えない（既存の `new DiagramCodingLimits(a, b, c)` / `with` はそのまま）。変換器 2 つは DI からこの record を**必須の**引数で受ける
  （省略時の既定へ黙って落ちない）。
  - 数値の根拠: 既定で 300 −（90 ＋ 120 ＋ 20）＝ **70 秒**が原本の取り寄せ・版の確かめ（上限 10 秒）・資産の保管・発行に残る。
    pandoc は数百ページの docx でも数十秒で終わる（期限は 3 倍程度の余裕）。止まった pandoc が 1 通でふさぐ時間は 4 × 90 ＋ 42 ＝ **402 秒**（従前 1242 秒）。
- **版の確かめ**は固定の 10 秒（`ExternalProcess.VersionProbeTimeout`）。構成の鍵は足さない（`--version` が 10 秒で返らない環境は「無い」と同じく扱う）。
- **期限切れは再試行する**（`BodyConversionTimeoutException` を受け口の `catch (Exception)` へそのまま流す）。未対応形式のように再送出を止める経路にはしない ——
  負荷で遅れただけの一過性の時間切れがあり得るうえ、UC-06 例外フローの「本文変換の恒久失敗は再試行し、継続失敗はデッドレター」に揃える。

## prefetch と consumer_timeout（AC-8）

**どちらも変えない。** 理由:

- `consumer_timeout`（ブローカの既定 30 分）は**配信から ack までの時間**に掛かる。本件後、止まった pandoc の 1 通は最大 402 秒で失敗が確定する
  （受け口の期限そのものの最悪は従前どおり 4 × 300 ＋ 42 ＝ 1242 秒で、これは pandoc 以外の段〔取り寄せ・保管〕が止まった場合）。いずれも 1 通単独では 30 分に届かない。
- prefetch（Wolverine の既定 100）で先に受け取った後続の配信は、前の配信の処理中も ack 待ちの時計が進むので、止まる配信が 5 通続けば（5 × 402 ＞ 1800）
  `consumer_timeout` に掛かり得る。そのときブローカはチャネルを閉じ、未 ack の配信を**再キューする**（捨てない）。変換は冪等（`DocumentId` は
  `DeterministicGuid.ForDocument`・IADR-0008 決定 C-2）なので二重処理は害にならない。
- prefetch・`consumer_timeout` は共通基盤（`WolverineExtensions.ListenToPlatformQueue`）とブローカの構成に属し、全サービスの受け口に効く。
  1 サービスの外部プロセスの期限の修正で動かす根拠は無い。**明らかに必要**とは言えないので、記録に留める。

## 母集合（同じ欠陥の走査）

走査は 2026-09-27、`origin/develop` = `c343b9d7` の上で行った。対象は `src/platform/**` と `src/knowledge/**` の試験以外（`/Tests/` と `.Tests/` を除外。
`src/ai-stock-trading` は別リポジトリの submodule のため**除外**）。問いは「**外部プロセスを起動して、自前の期限か取り消し時の停止を欠くものが他にあるか**」。

- `git grep -n "Process.Start(\|new ProcessStartInfo(" -- 'src/platform/**' 'src/knowledge/**'`（試験以外）→ 9 行。
  Dockerfile のコメント 1 行を除く 8 行は `PandocConversionService.cs`（変換 2 行・版 2 行）と `PdfTextLayerConverter.cs`（抽出 2 行・版 2 行）の 4 か所で、**すべて本件で直す**。
- `git grep -nE "new Process\b|new Process\(|Process\.Kill|\.Kill\(" -- 'src/platform/**' 'src/knowledge/**'`（試験以外）→ 0 行（別の起動経路・既存の停止処理は無い）。

## 検証

実測はすべて 2026-09-27、手元（Windows・.NET SDK 10）。結果は PR 本文に記す。

- `dotnet test src/knowledge/backend/Services/ConversionService/Tests`。
- `dotnet format <slnx> --verify-no-changes`: `src/knowledge/backend/backend.slnx`・`src/platform/backend/backend.slnx`。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。
- 変異（1 か所ずつ。修正のコミットの上で書き換え、`git show HEAD:<path> > <path>` で戻す）:
  - M1: `Kill(entireProcessTree: true)` を外す → AC-3・AC-4 の試験が赤。
  - M2: 期限の CTS を外す（期限なし）→ AC-3・AC-5 の試験が赤。
  - M3: `Kill(entireProcessTree: false)`（親だけ止める）→ AC-3・AC-4 の試験が赤（子孫が残る）。
  - M4: 起動時の検査から本文変換の期限を外す → AC-2 の試験が赤。

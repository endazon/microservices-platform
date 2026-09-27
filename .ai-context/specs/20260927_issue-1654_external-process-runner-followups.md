---
title: 外部プロセスの実行器の残り —— kill の AggregateException・版の確認の時間切れのログ・init の無いコンテナ・起動時の式の余裕（#1654）
type: spec
status: done
related_ids: [FR-12, UC-06, ADR-0012, ADR-0070, IADR-0008, IADR-0320, IADR-0356]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-06 例外フロー（本文変換の恒久失敗は再試行し、継続失敗はデッドレターへ送る）
  - planning:projects/microservices-platform/07_adr/ADR-0012（変換パイプライン）
related_specs:
  - 20260927_issue-1641_pandoc-timeout-and-kill.md
issue: "#1654"
---

# 仕様書: 外部プロセスの実行器の残り（#1654）

> 本仕様書は実装着手前に作成する。PR #1648（#1641）の監査で出た低い残り 4 件（A〜D）を片付ける。

## 起点となる計画書（トレーサビリティ）

- ユースケース: **UC-06** 例外フロー「本文変換の恒久失敗は再試行し、継続失敗はデッドレターへ送る」
- 機能要求: **FR-12**（原本の正規化変換）
- 関連 ADR: ADR-0012、ADR-0070 決定 2（pdftotext）
- 関連 IADR: **IADR-0008**（2026-09-27 追記 / #1641。本件はそこへ日付つきの追記を足す）、IADR-0320（pandoc の実行時イメージ）、IADR-0356（pdftotext）
- 起点 issue: #1654

## 事実（着手前の実測）

`origin/develop` = `6fe729d8`（#1648 を含む）の上で読んだ。

- **A.** `ExternalProcess.KillTreeAndReapAsync` の捕捉は `InvalidOperationException or Win32Exception or NotSupportedException`。
  .NET の `Process.Kill(entireProcessTree: true)` は子孫の一部を止められないと `AggregateException` を投げる（.NET のドキュメントの Exceptions 節）。
  これは捕捉を抜けるため、刈り取りが飛ばされる。さらに `RunAsync` の catch の中から投げられるので、期限切れ・取り消しの分類も上書きされる。
- **B.** `PandocConversionService.TryGetPandocVersionAsync` と `PdfTextLayerConverter.TryGetPdfToTextVersionAsync` の `catch { return null; }` は、
  版の確認の時間切れ（`BodyConversionTimeoutException`）もログ無しで握りつぶす。その結果、止まっているだけでも「実行時イメージに無い」と報告される。
  `AllowDegradedBodyConversion=true` では黙って縮退した本文になる。readiness（`PandocHealthCheck` / `PdfToTextHealthCheck`）は logger を渡していない。
- **C.** `RunAsync` は、読み取りを `linked.Token` 付きで開始し、`WaitForExitAsync(linked.Token)` の後に `await stdoutTask` する。そのため次の 2 件が起きる。
  - 期限の直前にプロセスが**自分で**終わっても、読み取りの完了が期限を跨ぐと時間切れになる（正しい出力を捨てる）。
  - 親が終わった後、ツリーの外へ出た孫（デーモン化・`start /b`）が標準出力を握っていると、読み取りは期限まで終わらない。孫はツリーの外なので `Kill(true)` でも止まらない。
  - コンテナの `ENTRYPOINT ["dotnet", "ConversionService.dll"]` には init が無く、dotnet が PID 1 になる。孤児になった孫は PID 1 に付け替えられるが、dotnet は `wait` しないのでゾンビが残る。
- **D.** 起動時の式は「受け口 ＞ 本文変換 ＋ 総枠 ＋ 1 回」で、版の確認の固定 10 秒（`ExternalProcess.VersionProbeTimeout`）と刈り取りの上限 10 秒（`ReapTimeout`）を含まない。
  既定値では余裕 70 秒が吸収するが、最小の合法な構成では保証されない。#1641 の仕様書の AC-2 が挙げる試験名 `本番の配線は四つの上限を既定値で張る` は実在しない
  （実在するのは `本番の配線は三つの上限を既定値で張る` と `本番の配線は本文変換の期限を両方の変換器へ渡す`）。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | kill の捕捉に `AggregateException` を足す（止められない子孫があっても刈り取りへ進み、分類を上書きしない） | コードの読み（再現には EPERM を起こせる別ユーザーのプロセスが要り、試験は置かない。下の「試験を置かないもの」） |
| AC-2 | 版の確認が時間切れになったら、**Warning** を出して null を返す（「止まっている」を「無い」と区別できるようにする）。readiness は自分の logger を渡す | `ExternalProcessTimeoutTests.Version_probe_timeout_is_logged_as_a_warning_for_pandoc`・`…_for_pdftotext` |
| AC-3 | 終了と時間切れの競合: 期限が来た時点でプロセスが**自分で**終わっていたら kill せず、読み取りを刈り取りの上限まで待つ。読み終われば結果を返す | `ExternalProcessTimeoutTests.Output_is_kept_when_the_process_exited_but_a_grandchild_briefly_held_stdout` |
| AC-4 | 親が終わった後にツリーの外の孫が標準出力を握り続けるときは、期限 ＋ 刈り取りの上限で時間切れとして終わる（止まらない・待ち続けない） | `ExternalProcessTimeoutTests.Detached_grandchild_holding_stdout_times_out_within_the_reap_cap` |
| AC-5 | ConversionService のイメージに init（`tini`）を入れ、`ENTRYPOINT ["tini", "--", "dotnet", "ConversionService.dll"]` にする（孤児の孫を刈り取る）。取得元はベースイメージの APT ミラーのまま。イメージがビルドでき、PID 1 が tini であることを手元で確かめる | 手元のイメージビルドと `nerdctl run` の実測（PR 本文） |
| AC-6 | 起動時の式を「受け口 ＞ 本文変換 ＋ 版の確認 10 秒 ＋ 刈り取り 10 秒 ＋ 総枠 ＋ 1 回」へ広げる。既定（300 ＞ 250）で稼働構成は起動する | `DiagramCodingLimitsTests.受け口の期限が本文変換と総枠と一回の期限の和を超えなければ起動を止める`（新しい境界の行を足す）・`受け口の期限が四つの和を超えれば起動し本文変換の期限は構成の値になる`（値を 251 へ） |
| AC-7 | #1641 の仕様書の AC-2 の試験名の誤りを、日付つきの追記で正す（本文は書き換えない） | 同仕様書の追記 |
| AC-8 | IADR-0008 へ日付つき追記（本文・#1641 の追記は書き換えない）。新しい IADR 番号は取らない | 同 IADR の追記 |

## 設計

- **A**: 捕捉の型に `AggregateException` を足す。ログは Warning のまま。
- **B**: 版の確認の 2 関数に `catch (BodyConversionTimeoutException ex)` を足し、Warning を出して null を返す（「無い」と同じ扱いは変えない。縮退の可否もそのまま）。
  試験のために、版の確認の期限を省略可能な引数（既定 `ExternalProcess.VersionProbeTimeout`）にする。readiness は自分の logger を渡す。
- **C（競合と孤児）**:
  - 読み取りは取り消しなしで開始する。待つ側で `WaitAsync(linked.Token)` を掛けて期限を守る（読み取り自体は止めない）。
  - `linked` が立ったとき、呼び出し元の取り消しでなく、かつプロセスが**既に自分で終わっていた**ら kill しない。読み取りを刈り取りの上限（10 秒）まで待ち、読み終われば結果を返す（AC-3）。
    読み終わらなければ（ツリーの外の孫がパイプを握っている）Warning を出して時間切れにする（AC-4）。
  - それ以外（まだ走っている・呼び出し元の取り消し）は従来どおりツリーごと止めて刈り取る。
  - どの経路でも、1 回の呼び出しの最悪は「期限 ＋ 刈り取りの上限 10 秒」で変わらない。
- **C（init）**: `tini` を `apt-get install` に足す（noble の `tini` パッケージ。`/usr/bin/tini`）。ENTRYPOINT を `["tini", "--", "dotnet", "ConversionService.dll"]` にする。
  helm・compose は ConversionService の `command` / `args` を上書きしていない（`git grep -n "command:\|args:" -- deploy/helm/microservices-platform/templates` に conversion は無い）。
  tini は SIGTERM を子へ中継するので、停止の振る舞いは変わらない。孤児の孫はゾンビにならず刈り取られる。ただし、ツリーの外の孫を**止める**ものではない（止めるのは AC-4 の期限）。
  他サービスには外部プロセスが無いので、ConversionService だけに入れる。
- **D**: `DiagramCodingLimits` に、本文変換 1 回の最悪 `BodyConversionWorstCase` ＝ 本文変換の期限 ＋ 版の確認 10 秒 ＋ 刈り取り 10 秒 を持たせ、式に使う。
  版の確認が止まった場合は 10 ＋ 10 秒で「無い」になり本文変換へ進まないので、1 回の最悪はこの和を超えない。
  - 既定: 300 ＞ 90 ＋ 10 ＋ 10 ＋ 120 ＋ 20 ＝ 250（余裕 50 秒）。図のコード化の端から端の試験の縮尺（受け口 30 秒、本文変換 1・総枠 2・1 回 1）は 30 ＞ 24 で通る。

## 試験を置かないもの

- **A** は、`Process.Kill(true)` に `AggregateException` を投げさせるには、止める権限の無い子孫（別ユーザーのプロセス）が要る。CI でも手元でも作れないため、コードの読みで確かめる。

## 母集合（同じ欠陥の走査）

走査は 2026-09-27、`origin/develop` = `6fe729d8` の上で行った（試験・`src/ai-stock-trading` を除く）。

- A: `git grep -n "\.Kill(" -- 'src/platform/**' 'src/knowledge/**'`（試験以外）→ 1 行（`ExternalProcess.cs` のみ）。
- B: `git grep -n "catch { return null; }" -- 'src/knowledge/backend/Services/ConversionService/**'`（試験以外）→ 2 行（版の確認の 2 関数）。本件で両方に Warning を足す。
- C: `ENTRYPOINT` を持つ Dockerfile は 16 本（`git grep -n ENTRYPOINT -- 'src/**/Dockerfile'`）。外部プロセスを起動するのは ConversionService だけ（#1641 の母集合で `Process.Start` は ConversionService の 4 か所のみ）なので、init を入れるのは ConversionService だけにする。
- D: `DiagramCodingLimits.From` を通る構成は、本番の Program.cs と試験 2 つ（`DiagramCodingLimitsTests`・`DiagramCodingTimeoutPipelineTests`）。値を持つ試験の行をすべて新しい式で計算し直す。

## 検証

実測はすべて 2026-09-27、手元（Windows・.NET SDK 10・Rancher Desktop の nerdctl v2.2.2）。修正のコミット `9eae5e9d` の上で測った。

- `dotnet test src/knowledge/backend/Services/ConversionService/Tests/ConversionService.Tests.csproj` → 合格 226・スキップ 6（pandoc / pdftotext 導入環境でだけ走る既存の試験）・失敗 0。
  新しい 4 件（AC-2 の 2 件・AC-3・AC-4）は Windows の分岐（`powershell` ＋ `ping 127.0.0.1` の孫）で実走して合格した。
- `dotnet format <slnx> --verify-no-changes`: knowledge・platform とも終了コード 0。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 841 tests passed。文書・トレーサビリティの検査器（trace ブロック・テスト仕様の床・リンク・知識グラフ・他リポジトリ参照・ID 修飾）はすべて OK。
- イメージ（AC-5）: 名前空間は既定（`k8s.io` ではない）で、専用のタグ `msp-conversion-tini-check:1654` を使った。push はしておらず、確認の後に `nerdctl rmi` で消した。クラスタには触れていない。
  - `nerdctl build -f src/knowledge/backend/Services/ConversionService/Dockerfile -t msp-conversion-tini-check:1654 . </dev/null` → 終了コード 0。
  - `nerdctl image inspect --format '{{json .Config.Entrypoint}}'` → `["tini","--","dotnet","ConversionService.dll"]`。
  - `nerdctl run --rm --network none --entrypoint tini … --version` → `tini version 0.19.0`。
  - 既定の ENTRYPOINT で起動すると、`ConnectionStrings:DefaultConnection が未設定` の未処理例外が出て終了コード 134 になった。tini が dotnet を起動し、その終了コードを返したことを示す。
  - 容器の中で孤児の刈り取りを実演する `sh` 経由の確認は、作業環境の安全装置（任意のシェル入力を容器へ渡す実行を拒む）で走らせられなかった。刈り取りは tini の本来の機能（PID 1 として `waitpid(-1)`）であり、ここでは実測していない。
- 変異（修正のコミットの上で書き換え、`git show HEAD:<path> > <path>` で戻した。戻した後の `git status --short` は毎回空で、`ping.exe` の残りも 0 件）:
  - M1: 版の確認の Warning を Debug にする → AC-2 の 2 件が赤（`Expected logger.Warnings to contain a single item, but the collection is empty`）。
  - M2: 「自分で終わっていたら読み取りを待つ」枝を外す → AC-3 と AC-4 が赤（AC-4 は 5 秒で諦め、期限 ＋ 刈り取りの上限 14.8 秒まで待たない）。
  - M3: 起動時の式から版の確認と刈り取りを外す → AC-6 の新しい境界の 2 行（250 秒・24 秒）が赤。

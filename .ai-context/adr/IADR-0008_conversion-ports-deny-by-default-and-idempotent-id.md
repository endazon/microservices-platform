---
title: IADR-0008 正規化変換はポート分離＋deny-by-default 縮退＋決定的 DocumentId で構成する
type: impl-adr
status: Accepted
related_ids:
  - FR-12
  - UC-06
  - ADR-0027
  - ADR-0029
author: claude
created: 2026-07-03
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-12)
  - planning:projects/microservices-platform/03_usecases/01_usecases.md (UC-06)
  - planning:projects/microservices-platform/04_workflows/03_conversion-flow.md
  - planning:projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md
  - planning:projects/microservices-platform/07_adr/ADR-0014_object-storage.md
  - planning:projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md
related_specs:
  - ../specs/20260703_FR-12_document-normalization-pipeline.md
  - ../specs/20260927_issue-1621_diagram-coder-timeout-retain.md
  - ../specs/20260927_issue-1641_pandoc-timeout-and-kill.md
  - ../specs/20260927_issue-1654_external-process-runner-followups.md
  - ../../docs/functional/FR-12_document-normalization.md
  - ../../docs/tests/FR-12_document-normalization.md
  - ./IADR-0007_llm-egress-routing-config-driven.md
---

# IADR-0008: 正規化変換はポート分離＋deny-by-default 縮退＋決定的 DocumentId で構成する

- 状態: Accepted
- 日付: 2026-07-03
- 決定者: claude（実装）
- 関連: FR-12（原本の正規化変換）、UC-06、ADR-0012（変換パイプライン）、ADR-0014（オブジェクトストレージ）、ADR-0010（LLMゲートウェイ）

## コンテキストと課題

FR-12 / UC-06 は「取得した原本を、AI が扱いやすい正規化形式（本文 Markdown＋資産）へ変換して管理する」を要求する。
本文は pandoc、図は LLM で PlantUML/Mermaid にコード化し、不可分な図は画像として保持する（ADR-0012、段階的に全面コード化）。
本文・資産はオブジェクトストレージへ保管する（ADR-0014）。変換時の LLM 呼び出しも機密区分で送信制御する（ADR-0010）。
実装にあたり、(1) 各外部依存（pandoc / LLMゲートウェイ / オブジェクトストレージ）の抽象化方針、
(2) 図コード化に失敗・拒否したときの縮退方針、(3) 再変換の冪等性の担保方法、を決める必要があった。

## 検討した選択肢

### A. 外部依存の抽象化

1. `NormalizationService` から pandoc / HTTP / ストレージを直接呼ぶ。実装は短いが単体テスト不能で、
   実クライアント未確定（ADR-0014）の現状ではモック化できない。
2. **用途別ポートへ分離**（本決定）: `IBodyConverter`（本文変換）/`IDiagramCoder`（図コード化）/
   `IObjectStore`（資産保管）に分け、`NormalizationService` はオーケストレーションに専念する。

### B. 図コード化の失敗・送信拒否時の扱い

1. コード化不能・送信拒否・呼び出し失敗を例外にし、メッセージ全体を再試行→デッドレターへ送る。
2. **すべて「画像として保持」へ収束**（本決定、deny-by-default）。変換パイプラインは常に完了させ、
   デッドレターは pandoc／保存の恒久失敗に限定する。

### C. 再変換の冪等性

1. 変換のたびに新しい `DocumentId` を採番する。再変換で重複文書が生まれる。
2. **`SourceId`＋原本パスから決定的に導出**（本決定、`DeterministicGuid`, RFC4122 v5 相当）。
   再変換で同一 `DocumentId` となり、文書管理側で重複登録を避けられる（ADR-0012「版で管理」）。

## 決定

- **A-2 を採用**。用途別ポートに分離し、実クライアント（MinIO/S3・pandoc 実行・LLMゲートウェイ）は
  背後の実装差し替えで後付けする。dev 環境では各実装がグレースフルデグレードする
  （pandoc 未導入／原本がローカル解決不能 → プレースホルダ本文、ストレージ未配備 → 決定的 URI 発行）。
- **B-2 を採用**。`IDiagramCoder` は「コード化不能」「機密区分による送信拒否（`Sent=false`）」
  「呼び出し失敗」をすべて `Retain(reason)` として返し、`NormalizationService` が画像保持へ振り分ける。
  送信可否ロジックは FR-11 の `/complete`（越境マトリクス、[IADR-0007](./IADR-0007_llm-egress-routing-config-driven.md)）へ委譲し、
  変換固有の送信制御を二重実装しない。

> **［2026-09-27 追記 / #1621］B-2 の「呼び出し失敗」は時間切れを含む。時間切れが縮退の枝に届くよう、図のコード化に 3 つの時間の上限を置く。**
>
> **欠陥**: REST の図のコード化 `LlmGatewayDiagramCoder.CodeAsync`（`Services:LlmGatewayGrpc` 未構成時の既定）は捕捉を
> `when (ex is not OperationCanceledException)` と型だけで絞っており、LLM ゲートウェイの時間切れ（`TaskCanceledException`）を画像保持へ
> 縮退させず、`RawDocumentFetchedConsumer` の正規化全体を失敗させていた（ジョブは `failed`、再試行を使い切ればデッドレター）。
> 本決定 B-2 と UC-06 例外フロー「図コード化（LLM）の失敗は画像保持へ縮退」に反する。
>
> **絞りの修正だけでは直らない**（本 PR の最初の版の誤り。監査が検出）。受け口の ct は**停止要求と Wolverine の 1 通ごとの実行期限の連結**
> である（WolverineFx 6.24.4 は `HandlerChain.ExecutionTimeoutInSeconds`、未設定なら `WolverineOptions.DefaultExecutionTimeout`＝**60 秒**の CTS を
> 受け口の ct へ連結する。本リポジトリはどちらも設定していなかった）。一方、図のコード化の期限は REST が `HttpClient` 既定の 100 秒、gRPC は期限なし
> だった。したがって応答しないゲートウェイは**いつも受け口の ct が先に立つ形で**終わり、`!ct.IsCancellationRequested` の縮退の枝には届かなかった。
>
> **決定**:
> 1. 捕捉を `when (ex is not OperationCanceledException || !ct.IsCancellationRequested)` にする（#1604 / #1608 と同じ形）。外へ出すのは受け口の ct が
>    立った取り消し（停止要求・実行期限）だけ。gRPC 実装の捕捉（`RpcException or InvalidOperationException && !ct.IsCancellationRequested`）は元から同じ境界である。
> 2. **1 回の呼び出しの期限** `Conversion:DiagramCodingTimeoutSeconds`（既定 **20 秒**・下限 1 秒）。REST は名前付きクライアントの `HttpClient.Timeout`
>    （`DiagramCoderRegistration.AddRestDiagramCoder`）、gRPC は呼び出しの `Deadline`。**同じ値を 1 か所から引く**（#1604 の `Mcp:DeclarationTimeoutSeconds` と同じ扱い）。
> 3. **1 文書あたりの図のコード化の総枠** `Conversion:DiagramCodingBudgetSeconds`（既定 **120 秒**・下限 1 秒）。`NormalizationService` が図ごとに呼び出しの
>    **前**に経過時間を見て、使い切っていたら**ゲートウェイを呼ばずに**画像保持へ回す（理由 `coding-budget-exhausted` をログへ出す。図の記録の形は変えない）。
>    超過は最後の 1 回の期限までに収まる。後日の人手補正（UC-06 代替フロー Phase 1）でコード化できるので、残りの図を捨てることにはならない。
> 4. **受け口の実行期限** `Conversion:HandlerTimeoutSeconds`（既定 **300 秒**）を `RawDocumentFetched` のハンドラの `ExecutionTimeoutInSeconds` へ与える
>    （`RawDocumentFetchedTimeoutPolicy`。他のメッセージ型の既定 60 秒は変えない）。**受け口の期限 ＞ 総枠 ＋ 1 回の期限**でなければ起動を止める
>    （`DiagramCodingLimits.From`）。既定では本文変換（pandoc。自前の期限を持たず受け口の ct に従う）・保管に 300 −（120 ＋ 20）＝ 160 秒が残る。
>
> **選択の理由**: 総枠だけでは受け口の既定 60 秒に収まらず（120 ＋ 20 ＞ 60）、受け口の期限だけでは図の多い文書で期限を食い尽くす。総枠を 60 秒未満へ
> 縮めると 1 回 20 秒で 2 図しか試せず、段階的コード化（ADR-0012）の実効が落ちる。両方を置き、順序（1 回 ＜ 総枠 ＜ 受け口）を起動時に検査する形にした。
> 受け口の期限を延ばした副作用として、固まった pandoc が 1 回の試行を占有する時間は 60 秒から 300 秒へ延びる（再試行の間隔・回数は変えない）。
>
> 試験: `DiagramCodingTimeoutPipelineTests`（縮尺した受け口の期限つき ct を渡し、応答しないゲートウェイで受け口の期限より前に成功する／総枠を使い切った
> 残りの図はゲートウェイを呼ばない／受け口の ct の取り消しは外へ出る）、`NormalizationServiceTests`（総枠の単体）、`LlmGatewayDiagramCoderTests`・
> `LlmGatewayGrpcDiagramCoderTests`（絞りの境界・gRPC の期限）、`DiagramCodingLimitsTests`（既定値・下限・順序の検査・Wolverine の既定 60 秒の固定・
> 方針が受け口の ct を実際に取り消すこと・Program.cs の配線）。作業仕様書: `.ai-context/specs/20260927_issue-1621_diagram-coder-timeout-retain.md`。
> **本文（決定 B-2）は書き換えない。**

- **C-2 を採用**。`DeterministicGuid.ForDocument(SourceId, OriginalPath)` で `DocumentId` を導出する。
- **pandoc 実行**: `IBodyConverter` は pandoc が利用可能かつ原本がローカル解決可能な場合、
  `pandoc -f <fmt> -t gfm --extract-media <tmp> <src>` を実行し、抽出画像を `ExtractedFigure` に写す。
  恒久失敗（pandoc 非0終了）は例外を送出し、MassTransit の再試行→デッドレターへ委ねる。

> **［2026-09-27 追記 / #1641］pandoc の実行（と同じ型の pdftotext の実行）に自前の期限を持たせ、期限切れか呼び出し元の取り消しでプロセスツリーごと止める。**
>
> **欠陥**: pandoc の呼び出し（`PandocConversionService.RunPandocAsync`）は `WaitForExitAsync(ct)` で待つだけで、自前の期限を持たず、
> 取り消されてもプロセスを止めなかった（`using` の `Dispose` はハンドルを閉じるだけ）。PDF のテキスト層抽出（`PdfTextLayerConverter`・IADR-0356）と
> 両者の版の確かめも同じ形だった。受け口は Wolverine の Inline（1 通ずつ処理）で、#1621 の追記 4. が受け口の実行期限を 300 秒にしたため、
> 止まった pandoc は 1 通で最大 4 × 300 ＋ 42 ≒ **1242 秒**受け口をふさぎ（従前 282 秒）、取り消された pandoc は受け口の外で走り続けた。
>
> **決定**:
> 1. **本文変換の外部プロセスの期限** `Conversion:BodyConversionTimeoutSeconds`（既定 **90 秒**・下限 1 秒）。pandoc と pdftotext で共用する
>    （1 文書は形式でどちらか一方だけを通る）。値は #1621 の上限と同じ record（`DiagramCodingLimits.BodyConversionTimeout`）が持ち、変換器は DI から必須の引数で受ける。
> 2. **起動時の検査を広げる**: 受け口の期限 ＞ **本文変換の期限** ＋ 総枠 ＋ 1 回の期限（#1621 の追記 4. の「受け口 ＞ 総枠 ＋ 1 回」を包含）。
>    既定では 300 ＞ 90 ＋ 120 ＋ 20 ＝ 230 で、原本の取り寄せ・版の確かめ・保管・発行に 70 秒が残る。鍵は helm・compose・appsettings のどこにも無いので、稼働構成は既定で起動する。
> 3. **実行器 `ExternalProcess.RunAsync`** を 4 か所（pandoc の変換・版、pdftotext の抽出・版）で共用する。期限の CTS と呼び出し元の ct を連結して待ち、
>    どちらかが立ったら `Process.Kill(entireProcessTree: true)` → 刈り取り（上限 10 秒）→ 例外。**呼び出し元の ct が立っていれば `OperationCanceledException`
>    （その ct を運ぶ）、そうでなければ `BodyConversionTimeoutException`（`TimeoutException` の派生・道具名と期限と構成鍵をメッセージに持つ）**。
>    両方立ったら呼び出し元を優先する（#1604 / #1621 と同じ境界）。版の確かめは固定の 10 秒で、呼び出し元の取り消しは「無い」へ畳まずに外へ出す。
> 4. **期限切れは再試行する**（受け口の `catch (Exception)` が失敗を記録して再送出する。未対応形式のように再送出を止める経路にはしない）。
>    負荷で遅れただけの一過性の時間切れがあり得るうえ、UC-06 例外フロー「本文変換の恒久失敗は再試行し、継続失敗はデッドレターへ送る」に揃える。
>    止まった pandoc が 1 通でふさぐ時間は 4 × 90 ＋ 42 ＝ **402 秒**になる（刈り取りが上限の 10 秒まで掛かる最悪でも 4 × 100 ＋ 42 ＝ 442 秒）。
>
> **RabbitMQ の prefetch・`consumer_timeout` は変えない**: `consumer_timeout`（ブローカ既定 30 分）は配信から ack までの時間に掛かり、1 通単独では
> 最悪（本文変換以外の段が止まった場合の 4 × 300 ＋ 42 ＝ 1242 秒）でも届かない。prefetch（Wolverine 既定 100）で先に受け取った配信は前の配信の処理中も
> 時計が進むので、止まる配信が 5 通続けば（5 × 402 ＞ 1800。刈り取りの最悪 442 秒でも 4 通では 1768 ＜ 1800）掛かり得るが、そのときブローカはチャネルを閉じて未 ack の配信を**再キューする**（捨てない）。
> 変換は冪等（決定 C-2 の決定的 `DocumentId`）なので二重処理は害にならない。どちらも共通基盤（`WolverineExtensions.ListenToPlatformQueue`）とブローカの構成に属し
> 全サービスの受け口に効くため、1 サービスの外部プロセスの期限の修正で動かす根拠は無い。
>
> 試験: `ExternalProcessTimeoutTests`（起動する命令を試験用の口 `StartInfoFilter` で差し替え、子を持って止まる命令〔Linux: `sh` ＋ `sleep`、Windows:
> `powershell` ＋ `ping 127.0.0.1`〕を pandoc / pdftotext の代わりに起動する。期限でツリーごと止まり親も子も残らない・受け口から通してジョブが失敗理由つきで
> 記録される・呼び出し元の取り消しでも止まり取り消しとして外へ出る・正常系と非 0 終了は不変）、`DiagramCodingLimitsTests`（既定値・丸め・広げた検査・配線）。
> 作業仕様書: `.ai-context/specs/20260927_issue-1641_pandoc-timeout-and-kill.md`。**本文（pandoc 実行の決定）は書き換えない。**

> **［2026-09-27 追記 / #1654］外部プロセスの実行器の残り（#1641 の監査で出た低い 4 件）。上の #1641 の追記は書き換えない。**
>
> 1. **kill の失敗の型**: `Process.Kill(entireProcessTree: true)` は子孫の一部を止められない（EPERM 等）と `AggregateException` を投げる。
>    これを kill の捕捉に足す。捕まえないと刈り取りを飛ばし、呼び出し元の catch の中から投げられて期限切れ・取り消しの分類を上書きする。
> 2. **版の確認の時間切れのログ**: 版の確認（`pandoc --version` / `pdftotext -v`）が `BodyConversionTimeoutException` になったら、
>    **Warning** を出してから null（「無い」）を返す。「止まっている」を「無い」と区別できるようにする。readiness は自分の logger を渡す。「無い」として扱うこと自体
>    （fail-closed・縮退の可否）は変えない。
> 3. **終了と時間切れの競合・孤児の孫・init**:
>    - 読み取りは取り消さずに開始し、待つ側で期限を守る。
>    - 期限が来た時点でプロセスが**自分で**終わっていたら kill せず、読み取りを刈り取りの上限（10 秒）まで待つ。読み終われば結果を返す（期限の直前に終わった正しい出力を捨てない）。
>    - 読み終わらないのは、ツリーの外へ出た孫が標準出力を握っている場合である。孫は `Kill(true)` では止まらないので、Warning を出して期限切れにする。
>    - どの経路でも、1 回の呼び出しの最悪は「期限 ＋ 刈り取りの上限」で変わらない。
>    - ConversionService のイメージに **tini** を入れ、`ENTRYPOINT ["tini", "--", "dotnet", "ConversionService.dll"]` にする（noble の APT ミラーから取得。tini 0.19.0）。
>      dotnet は PID 1 になっても孤児を `wait` しないので、ゾンビが溜まる。tini は孤児を刈り取り、SIGTERM を dotnet へ中継する。
>    - helm・compose は ConversionService の `command` / `args` を上書きしていないので、配備の変更は要らない。
>    - 外部プロセスを起動するのは ConversionService だけなので、他サービスには入れない。tini は孤児を**止め**はしない（止めるのは 3. の期限）。
> 4. **起動時の式の余裕**: 本文変換 1 回の最悪を「本文変換の期限 ＋ 版の確認 10 秒 ＋ 刈り取りの上限 10 秒」（`DiagramCodingLimits.BodyConversionWorstCase`）とし、
>    式を「受け口 ＞ その最悪 ＋ 総枠 ＋ 1 回」へ広げる。版の確認が止まったときは 10 ＋ 10 秒で「無い」になり変換へ進まないので、この和を超えない。
>    既定では 300 ＞ 90 ＋ 10 ＋ 10 ＋ 120 ＋ 20 ＝ 250（余裕 50 秒）で、鍵の無い稼働構成はそのまま起動する。
>
> 試験: `ExternalProcessTimeoutTests`（版の確認の時間切れの Warning〔pandoc・pdftotext〕・自分で終わったプロセスの出力を孫が期限を跨いで握っても返す・
> 孫が握り続けたら期限 ＋ 刈り取りの上限で期限切れ）、`DiagramCodingLimitsTests`（新しい境界の行・陽性対照 251 秒）。1. は止められない子孫を CI で作れないため、試験を置かない。
> 作業仕様書: `.ai-context/specs/20260927_issue-1654_external-process-runner-followups.md`。

## 理由

- ポート分離により、実クライアント未確定（ADR-0014 は Proposed）でも受け入れ基準の分岐
  （コード化成功／画像保持／送信拒否縮退／冪等 ID）を単体テストで検証できる。
- deny-by-default 縮退は ADR-0012 の「段階的に全面コード化（当面は画像保持を許容）」と、
  ADR-0010 の「送信不可時は縮退」の双方に整合し、変換パイプラインの完了性を保証する。
- 決定的 `DocumentId` は再投入・再変換に対する冪等性（UC-06 代替フロー＝人手補正後の再登録）を、
  文書管理側の状態に依存せず担保する。

## 結果

- 良い影響: 実クライアント差し替えが局所化。分岐が網羅的にテスト可能。再変換が冪等。
- トレードオフ: 図コード化の LLM 一時障害が画像保持へ縮退し、その図単体では再試行されない
  （下記「計画との差異」参照）。
- フォローアップ（含まないもの）:
  - 実オブジェクトストレージ（MinIO/S3）クライアントの実装（ADR-0014 製品確定後）。
  - LLMゲートウェイのマルチモーダル（Vision）画像入力対応（現状はキャプション/抽出テキストをプロンプト化）。
  - pandoc の入力形式判定の拡充と、実ストレージからの原本フェッチ（現状は file://／ローカルパスのみ）。

## 計画との差異（要環流）

- `04_workflows/03_conversion-flow.md` の例外処理は「LLM 一時障害は再試行する」と定めるが、
  本実装は図コード化の呼び出し失敗を deny-by-default で**画像保持へ縮退**させるため、
  その図についてはメッセージ再試行が発火しない（デッドレターは pandoc／保存の恒久失敗に限定）。
- これは「変換パイプラインを常に完了させ、人手補正で後から再登録する」という段階的コード化方針
  （ADR-0012）を優先した実装判断である。計画書（draft）との差異は
  `feedback/20260703_conversion-retry-vs-image-fallback.md` として `/plan-feedback` で計画側へ環流する。

## 前提リスク（計画ドキュメントの確定状況）

- 本決定が根拠とする **ADR-0010 / ADR-0012 / ADR-0014 はいずれも `Proposed`** であり正式確定していない。
  pandoc＋LLM 構成・オブジェクトストレージ方式・送信制御が確定後に変われば、本実装も追随する。
  確定内容が本決定と矛盾する場合は新 IADR で更新する。

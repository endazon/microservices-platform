---
title: gRPC クライアントが呼び出し元の取り消し（RpcException(Cancelled)）を縮退へ畳み、停止を拒否として記録していた件を直す（#1637）
type: spec
status: done
related_ids: [FR-10, FR-17, FR-18, FR-19, FR-22, FR-04, UC-10, NFR-16, NFR-09, ADR-0029, ADR-0075, ADR-0063, ADR-0086, IADR-0379, IADR-0408, IADR-0410, IADR-0412, IADR-0419, IADR-0462, IADR-0431]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md（east-west gRPC の採用基準。REST と同じ意味論で輸送だけを替える）
  - planning:projects/microservices-platform/02_requirements FR-10（ナレッジ健全性の指標）・FR-18（AI 提案のタグ反映と辞書）・FR-22（利用者本人への通知）・FR-04 / FR-17（検索のグラフ展開）
related_specs:
  - 20260927_issue-1630_caller-cancel-controls-tce.md
  - 20260908_issue-1255_tag-dictionary-grpc.md
  - 20260909_issue-1255_document-to-notification-grpc.md
issue: "#1637"
---

# 仕様書: gRPC クライアントの呼び出し元の取り消し（#1637）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。
> 本件は**本番コードの欠陥の是正**である。計画の受け入れ基準は変えない（REST 版と同じ意味論へ揃える）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-10**（ナレッジ健全性の観測値の送出）、**FR-18**（AI タグ提案の反映・タグ辞書の読み取り）、
  **FR-19** / **FR-22**（個人資料の通知の送出）、**FR-04** / **FR-17** / **UC-10**（検索のグラフ展開）、**NFR-16**（east-west gRPC）
- 関連 ADR: ADR-0029（gRPC と REST の使い分け）、ADR-0075、ADR-0063（タグ反映）、ADR-0086（利用者文脈を本文で運ぶ）
- 関連 IADR: IADR-0408（健全性の報告）・IADR-0410（近傍展開・タグ反映）・IADR-0412（タグ辞書）・IADR-0419（通知の送出）・
  IADR-0379（east-west gRPC の共通部品）・IADR-0462（参照の形 `GrpcServiceIntrospectionCollector`）
- 起点 issue: #1637（#1630＝PR #1633 の作業と監査で見つけた本番の問題。#1630 の仕様書「射程外の観察」）

## 事実（着手前の実測）

- 4 つの gRPC クライアント（`GrpcDocumentTagWriter`・`GrpcTagDictionaryReader`・`GrpcKnowledgeHealthReporter`・`GrpcPrivateNoteNotifier`）は、
  呼び出し元の取り消しを見分ける絞り込み（`ex is OperationCanceledException && ct.IsCancellationRequested`）より**前に**、無条件の
  `catch (RpcException ex)` を置いていた。
- `ThrowOperationCanceledOnCancellation` はリポジトリのどこでも設定していない（既定の false）。本番のチャネルは呼び出し元の取り消しを
  **`RpcException(Cancelled)`** で投げる。本件で足した前提の表明（`前提_本物のチャネルは取り消しを_RpcException_Cancelled_で投げる`、
  GraphService・DocumentService の 2 か所）が実測で確かめる。
- そのため取り消しは型の絞り込みに届かず、縮退へ畳まれていた（下の「直す前の実測」の変異 R が、改めた試験を赤にすることで確かめる）:
  - TagWriter: `Unavailable`（承認の口が既に打ち切られた要求へ 502）＋ Error のログ。
  - TagDictionaryReader: `null`（引けなかった → 候補が出ない）＋ Warning のログ。
  - HealthReporter: 停止のたびに Error のログ（`KnowledgeHealthCollector` が 5 指標ぶん続けて呼ぶ）。
  - PrivateNoteNotifier: `Cancelled` は「不達」（`Unavailable` / `DeadlineExceeded`）に入っていないので、停止が計器に **`rejected`**（受け口の責任）として積まれていた。
- 各試験の「呼び出し元のキャンセルは伝播する」は偽のクライアントへ**素の OCE** を注入しており、本物のチャネルが通らない経路だけを見ていた。
- `GrpcServiceIntrospectionCollector`（IADR-0462）と `GrpcToolDeclarationCollector` は `catch (Exception) when (ct.IsCancellationRequested)` を先頭に置き、
  `ct.ThrowIfCancellationRequested()` で呼び出し元の token を持つ OCE へ揃えている（参照の形）。

## 母集合（規則 9: 誤りの側の文字列で全文書を走査する）

走査は `origin/develop`（bbc1c1c6）に対して、両ユニットの本番コード（`src/platform/backend/**`・`src/knowledge/backend/**`。試験は除く）へ行った。
`src/ai-stock-trading`（submodule・別リポジトリ）は対象外。

- 軸 1（無条件の捕捉）: `git grep -n "catch (RpcException"` → **16 行**。
- 軸 2（取り消しを型だけで見分ける絞り込み）: `git grep -n "is OperationCanceledException && ct.IsCancellationRequested"` → **8 行**。
- 軸 3（生成クライアントを呼ぶ全ファイル）: `git grep -l --all-match -e "cancellationToken: ct" -e "Grpc"` → **19 ファイル**。
  軸 1・2 に出ないものは、それぞれの捕捉を読んで判定した（下の除外表）。

突き合わせ（数えは `origin/develop` の走査の生の出力に対して行った）:

- 軸 1 の 16 行 ＝ 対象 4 行（1〜4）＋ 除外 2 行（参照の形 2）＋ 報告のみ 10 行（BFF 1・共有クライアント 6・Qdrant 3）。
- 軸 2 の 8 行 ＝ 対象 5 行（1〜5）＋ 除外 3 行（REST 2・S3 1）。
- 軸 3 の 19 ファイル ＝ 対象 5 ＋ 除外 10（参照の形 2・LLM／検索の輸送 4・BFF 2・埋め込み 2）＋ Qdrant の取り込み 1（除外）＋ 報告のみ 3（共有クライアント 2・`QdrantVectorStore` 1）。

### 対象（直す）

| # | クライアント | 当たった軸 | 直す前の畳み先 | 直し方 |
| --- | --- | --- | --- | --- |
| 1 | `GrpcDocumentTagWriter`（GraphService） | 1・2 | `Unavailable`・Error | 縮退の catch より前に `catch (Exception) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }` |
| 2 | `GrpcTagDictionaryReader`（GraphService） | 1・2 | `null`・Warning | 同上 |
| 3 | `GrpcKnowledgeHealthReporter`（GraphService） | 1・2 | Error | 同上 |
| 4 | `GrpcPrivateNoteNotifier`（DocumentService） | 1・2 | 計器 `rejected`・Error | 同上 |
| 5 | `GrpcGraphNeighborExpander`（RetrievalService。辞書・近傍の 2 段） | 2（軸 1 に出ないのは `catch (Exception ex) when (!IsCallerCancellation(ex, ct))` の形のため） | 警告つきの縮退（全辺フォールバック重み・空） | 同上を 2 段の各捕捉の前に置く。**issue が挙げていない 5 つ目**だが、同じ欠陥で同じ小さな変更なので同じ PR で直す |

### 除外（理由つき）

| 箇所（軸） | 除外の理由 |
| --- | --- |
| `HttpPrivateNoteNotifier.cs:82`・`HttpKnowledgeHealthReporter.cs:91`（軸 2） | REST 版。`HttpClient` は呼び出し元の取り消しを `TaskCanceledException`（OCE の派生）で投げるので、型の絞り込みで正しく拾える（#1630 が本物の HttpClient で確かめた形） |
| `S3ObjectStorageClient.cs:227`（軸 2） | AWS SDK（HttpClient）。gRPC ではない（#1630 の対象 4 で確かめ済み） |
| `GrpcServiceIntrospectionCollector.cs:81`・`GrpcToolDeclarationCollector.cs:92`（軸 1） | `catch (Exception) when (ct.IsCancellationRequested)` が先頭にあり、status を絞った捕捉はその後ろ。正しい形（参照） |
| `GrpcLlmCompletionTransport`・`GrpcRagSearchTransport`・`LlmGatewayGrpcDiagramCoder`・`LlmGatewayGrpcSuggestionClient`（軸 3） | 絞り込みが `ex is RpcException or InvalidOperationException && !ct.IsCancellationRequested` で、呼び出し元の ct で判定している。取り消しは縮退へ畳まれない（`RpcException` のまま外へ出る。呼び出し元はいずれも要求の経路で、打ち切られた要求の例外として終わる） |
| `DocumentReadGrpcClient`（BFF。軸 3） | クライアント自身は捕捉を持たず、呼び出し元の `DocumentBffEndpoints.IsTransportFailure(ex, grpc, ct)` が ct で判定している |
| `AttributeValuesGrpcClient`（BFF。軸 3） | 捕捉は呼び出し元 `SearchBffEndpoints` にある（下の「報告のみ」1） |
| `LlmGatewayGrpcEmbeddingService`（Ingestion・Retrieval。軸 3） | 捕捉を持たない（すべて外へ出す） |
| `QdrantIngestionVectorStore`（軸 3） | Qdrant 公式クライアント（第三者の gRPC）。`catch (RpcException` を持たない |

### 報告のみ（同じ形だが、同じ小さな変更ではないので本 PR では直さない）

| # | 箇所（軸） | 同じ形であること | 直さない理由 |
| --- | --- | --- | --- |
| 1 | `SearchBffEndpoints.cs:179`（BFF の属性値の口。軸 1） | `IsRetrievalUnreachable(ex, ct)` は ct を見るが、その後ろの `catch (RpcException)` が無条件。呼び出し元の取り消しは 502 になる | クライアントではなく**端点**の捕捉であり、直すと端点の応答の形（502 か取り消しか）が変わる。#1636 が同時に RetrievalService の `ListValues` の受け口を変えている領域に隣接する。影響は打ち切られた要求への応答だけ |
| 2 | `AuthzScopeGrpcClient.cs:44`・`:110`、`UserDirectoryGrpcClient.cs:55`・`:87`・`:115`・`:169`（platform の共有クライアント。軸 1） | 無条件の `catch (RpcException)` が取り消しを `null`（deny / 引けなかった）へ畳む | 共有クライアントで、**型を参照する本番ファイルが両ユニットに 29**（定義の 2 ファイルを含む）ある。`GrpcOwnerAccountDirectory` は「`null` のあと `ct.ThrowIfCancellationRequested()`」で取り消しを取り戻す前提で書かれており、取り消しを投げる形へ変えると各呼び出し元の捕捉（`null` を deny へ倒す）を個別に読み直す必要がある。**同じ小さな変更ではない** |
| 3 | `QdrantVectorStore.cs:179`（キーワード検索。軸 1） | 無条件の `catch (RpcException)` が取り消しを「キーワード検索の縮退」へ畳み、計器 `RecordDegraded` を 1 件積む | Qdrant 公式クライアント（第三者の gRPC）。試験の器（Qdrant の受け口の偽物）が別物になる。計器の汚れは #1637 の notifier と同型なので、別 issue で直すのがよい |
| 4 | `QdrantCjkNgramIndexHealthCheck.cs:52`・`QdrantFullTextIndexHealthCheck.cs:59`（軸 1） | 無条件の `catch (RpcException)` が取り消しを `Degraded` へ畳む | 健全性検査の取り消しはホストの検査の打ち切りで、返り値は使われない。影響は実質なし |

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 対象 1〜5 は、呼び出し元の取り消しを縮退へ畳まず、**呼び出し元の token を持つ `OperationCanceledException`** で外へ出す。縮退のログを出さない。notifier は計器に積まない（`rejected` にしない） | 各試験の「呼び出し元のキャンセル」（下の表） |
| AC-2 | 呼び出し元が取り消していない `CANCELLED`（受け口が返したもの）・期限切れは、従来どおりの縮退のまま | 各試験の「受け口が返した_Cancelled_…」・既存の `DeadlineExceeded` の試験 |
| AC-3 | 「呼び出し元の取り消し」の試験は、127.0.0.1 の実 gRPC サーバーへ本番と同じ既定値のチャネルで繋ぎ、**受け口が要求を受け取ってから**呼び出し元が取り消す（素の OCE の注入を使わない）。前提（チャネルが `RpcException(Cancelled)` を投げる）を同じ器で表明する | `LoopbackGrpcServer`・前提の試験 |
| AC-4 | 各クライアントで、守りを戻す変異（R）・status で判定する変異（S）・`RpcException` のまま投げ直す変異（T）がいずれも赤になる | 変異の記録 |
| AC-5 | 変更した試験クラスを 20 回連続で実行してすべて合格する | 反復の記録 |
| AC-6 | 同じ形の他のクライアントを両ユニットで走査し、直したもの・直さないもの（理由つき）を本仕様書に列挙する | 上の母集合 |

## 設計

- **形は `GrpcServiceIntrospectionCollector` に揃える**: `catch (Exception) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }` を
  縮退の catch より前に置く。**判定は型でも status でもなく呼び出し元の ct で行う**（受け口が返した `CANCELLED` は取り消しではない）。
- **`RpcException` のまま投げ直さず OCE へ揃える**: 呼び出し元の捕捉を読んだ結果である。
  - `KnowledgeHealthHostedService` は `catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)` で周期の失敗を
    記録し、外側の `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)` を「シャットダウン」と読む（#1598）。
    `RpcException` のまま投げ直すと停止が「周期の失敗」として Error で記録される。
  - `PrivateNoteMaintenanceService`（周期の本体 L170・ループ L402/L410）も同じ形。`PrivateNoteUsage.RecordUsageAndWarnAsync`（同期 push）は捕捉を持たず、要求の取り消しとして外へ出る。
  - タグ反映の承認の口（`AiSuggestions/Approve/Endpoint`）・提案の生成（`AiSuggestionGenerator`）・検索のグラフ展開（`GraphExpandingSearchService`）は捕捉を持たず、
    要求の取り消し（OCE）として終わる。REST 版（`HttpClient` が `TaskCanceledException` を投げる）と同じ振る舞いになる。
- **既存の型の絞り込み（`IsCallerCancellation` 等）は残す**: s2s トークン取得の途中の取り消しなど、`RpcException` 以外の OCE を従来どおり素通しする。新しい守りの後ろでは冗長だが、
  変更を最小にするため触らない。
- **試験の器 `LoopbackGrpcServer`**（GraphService・DocumentService・RetrievalService の各 `Tests/Grpc/`）: `WebApplication.CreateEmptyBuilder` ＋ `UseKestrelCore` で
  `IPAddress.Loopback` の動的ポート（h2c）だけに待ち受け、起動直後に全待受がループバックであることを確かめる。受け口の偽物（生成された `*Base` の派生）を載せ、
  クライアント側は `GrpcChannel.ForAddress`（本番と同じ既定値）＋ 生成クライアントで呼ぶ。偽物は要求を受け取ったことを `TaskCompletionSource` で知らせてから
  `Task.Delay(Infinite, context.CancellationToken)` で待ち、試験はそれを待ってから取り消す（壁時計の間隔に依存しない）。
  3 つの試験プロジェクトに同じ器を置くのは、共通の試験基盤が knowledge に無く、各プロジェクトが別サービスの参照を持つためである（`RecordingLogger` も同じ。DocumentService は既存のものを使う）。

## 試験の対応

| クライアント | 呼び出し元の取り消し（AC-1・AC-3） | 受け口が返した `CANCELLED`（AC-2） | 前提の表明 |
| --- | --- | --- | --- |
| TagWriter | `GrpcDocumentTagWriterTests.呼び出し元のキャンセルは伝播する`（T-07 を改めた） | `受け口が返した_Cancelled_は到達不能へ倒す`（T-07b） | `前提_本物のチャネルは取り消しを_RpcException_Cancelled_で投げる`（T-07a） |
| TagDictionaryReader | `GrpcTagDictionaryReaderTests.呼び出し元のキャンセルは伝播する`（T-05 を改めた） | `受け口が返した_Cancelled_は引けなかったである`（T-05b） | — |
| HealthReporter | `GrpcKnowledgeHealthReporterTests.呼び出し元のキャンセルは伝播する`（T-09 を改めた） | `受け口が返した_Cancelled_は数えず投げない`（T-09b） | — |
| PrivateNoteNotifier | `GrpcPrivateNoteNotifierTests.呼び出し元のキャンセルは伝播する`（T-05 を改めた。計器が空であることも表明） | `受け口が返した_Cancelled_は_rejected_である`（T-05b） | `前提_本物のチャネルは取り消しを_RpcException_Cancelled_で投げる`（T-05a） |
| NeighborExpander | `GrpcGraphNeighborExpanderTests.呼び出し元の取り消しは縮退へ畳まず外へ出す(Weights / Neighbors)`（新設 T-06a/b） | `受け口が返した_Cancelled_は空へ縮退する`（T-06c） | — |

## 変異の記録（AC-4）

器: `scratchpad/mut.js`（作業用。コミットしない）。1 つずつ当て、該当の試験クラスを走らせ、`git show HEAD:<path> > <path>` で戻し、
戻した後 `git diff --quiet -- <path>` が真であることを毎回確かめた（全 17 件 `restored-clean=true`）。変異 R は `git show origin/develop:<path>` で
**直す前の本番コード**に戻すもので、「直す前の実装では改めた試験が赤になる」ことの実測を兼ねる。

| 変異 | 当てた箇所 | 赤になった試験（その他はすべて緑） | 失敗の文言 |
| --- | --- | --- | --- |
| TW-R | TagWriter を直す前へ | `呼び出し元のキャンセルは伝播する`（1/19） | `Expected a <System.OperationCanceledException> to be thrown, but no exception was thrown.` |
| TW-S | 守りを `catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)` へ | `受け口が返した_Cancelled_は到達不能へ倒す`（1/19） | （`RpcException(Cancelled)` が外へ出た） |
| TW-T | 守りの `ct.ThrowIfCancellationRequested();` を外す | `呼び出し元のキャンセルは伝播する`（1/19） | `… but found <Grpc.Core.RpcException>` |
| TD-R / TD-S / TD-T | TagDictionaryReader に同上 | それぞれ `呼び出し元のキャンセルは伝播する` / `受け口が返した_Cancelled_は引けなかったである` / `呼び出し元のキャンセルは伝播する`（各 1/16） | R: `but no exception was thrown.`／T: `but found <Grpc.Core.RpcException>` |
| HR-R / HR-S / HR-T | HealthReporter に同上 | それぞれ `呼び出し元のキャンセルは伝播する` / `受け口が返した_Cancelled_は数えず投げない` / `呼び出し元のキャンセルは伝播する`（各 1/15） | 同上 |
| PN-R / PN-S / PN-T | PrivateNoteNotifier に同上 | それぞれ `呼び出し元のキャンセルは伝播する` / `受け口が返した_Cancelled_は_rejected_である` / `呼び出し元のキャンセルは伝播する`（各 1/18） | 同上 |
| NE-R | NeighborExpander を直す前へ（2 段とも） | `呼び出し元の取り消しは縮退へ畳まず外へ出す(Weights)`・`(Neighbors)`（2/17） | `but no exception was thrown.` |
| NE-R1 | 辞書の段の守りだけを外す | `(Weights)`（1/17） | `Expected logger.OfLevel(LogLevel.Warning) to be empty …, but found at least one item`（辞書の段が警告つきで畳み、近傍の段の守りが取り消しを外へ出す —— ログの表明が無ければ生き残った） |
| NE-R2 | 近傍の段の守りだけを外す | `(Neighbors)`（1/17） | `but no exception was thrown.` |
| NE-S / NE-T | NeighborExpander に S / T（2 段とも） | `受け口が返した_Cancelled_は空へ縮退する`（1/17）／`(Weights)`・`(Neighbors)`（2/17） | T: `but found <Grpc.Core.RpcException>` |

## 反復の記録（AC-5）

`scratchpad/repeat.sh`: 3 プロジェクトを 1 度ビルドし、対象 5 クラスのフィルタで `dotnet test --no-build` を 20 回連続で走らせた。

| 試験プロジェクト | 結果 |
| --- | --- |
| GraphService.Tests（TagWriter・TagDictionaryReader・HealthReporter） | 20/20 |
| DocumentService.Tests（PrivateNoteNotifier） | 20/20 |
| RetrievalService.Tests（NeighborExpander） | 20/20 |

## 記録の更新

- IADR の日付つき追記（新しい IADR は起こさない）: IADR-0408 決定 5（健全性の報告）・IADR-0410 決定 5（近傍展開・タグ反映）・IADR-0412 決定 3（タグ辞書）・
  IADR-0419 決定の送出の表（通知）。
- IADR-0431（退職者の個人資料の処分）は issue の件名に挙がっているが、処分の経路は通知を 1 件も送らない（同 決定 4）ので本件のクライアントの所有者ではない。追記しない。
- 試験仕様書: FR-10 P-12（gRPC 版の対照）と実装マッピング、FR-18 T-53・T-54、FR-22 T-39、UC-10 T-36・T-37（検索のグラフ展開の gRPC 版）。
  5 つの試験クラスはもともと試験仕様書に載っていなかった（記載義務を負わない warn の側）ので、本件で載せ、`check-test-spec-coverage.js --update` で床を上げる。

## 結果

- AC-1〜AC-6 を満たした。本番コードの差分は 5 ファイル（守りの追加だけ）。新しい IADR は起こしていない。

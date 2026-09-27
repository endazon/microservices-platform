---
title: BFF の属性の値・認可と利用者名簿の gRPC 共有クライアント・Qdrant で、呼び出し元の取り消しが代替や劣化の計器に畳まれる残りを直す（#1646）
type: spec
status: done
related_ids: [FR-03, FR-05, NFR-16, FR-04, FR-18, FR-19, FR-20, UC-01, NFR-06, NFR-09, ADR-0029, ADR-0075, ADR-0043, IADR-0379, IADR-0401, IADR-0417, IADR-0318, IADR-0462]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md（east-west gRPC の採用基準。REST と同じ意味論で輸送だけを替える）
  - planning:projects/microservices-platform/02_requirements FR-03（ハイブリッド検索）・FR-05（ABAC・deny-by-default）・NFR-16（east-west gRPC）・NFR-06（障害時の縮退運転）
related_specs:
  - 20260927_issue-1637_grpc-client-caller-cancellation.md
  - 20260927_issue-1636_addtag-admin-role-from-authz.md
issue: "#1646"
---

# 仕様書: 呼び出し元の取り消しが代替・劣化の計器に畳まれる残り（#1646）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。
> 本件は**本番コードの欠陥の是正**である。計画の受け入れ基準は変えない（REST 版と同じ意味論へ揃える）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-03**（ハイブリッド検索のキーワード側・索引の健全性）、**FR-05**（ABAC の解決・属性値の照会）、**NFR-16**（east-west gRPC）。
  共有クライアントの呼び出し元として FR-04（検索の対象範囲の候補）・FR-18（タグ反映の承認者の判定）・FR-19（退職の窓）・FR-20（同期トークンの所有者）。
- 関連 ADR: ADR-0029（gRPC と REST の使い分け）、ADR-0075、ADR-0043（属性値の照会）、NFR-06（縮退運転）
- 関連 IADR: IADR-0401（認可サービスの east-west gRPC・名簿の読み口）・IADR-0417 決定 9（属性値の照会の縮退の 2 枝）・IADR-0318 決定 3（キーワード検索の縮退の計器）・
  IADR-0462（参照の形 `GrpcServiceIntrospectionCollector`）・IADR-0379（east-west gRPC の共通部品）
- 起点 issue: #1646（#1637＝PR #1643 の作業仕様書「報告のみ」1〜4）

## 事実（着手前の実測。`origin/develop` = 478bba79）

- 本物のチャネルは呼び出し元の取り消しを `RpcException(Cancelled)` で投げる（`ThrowOperationCanceledOnCancellation` はリポジトリのどこでも設定していない。#1643 の前提の試験が実測で確かめた）。
  Qdrant の公式クライアント（Qdrant.Client 1.18.1）も `Grpc.Net.Client` のチャネルに乗るので同じ（本件の前提の試験が確かめる）。
- 無条件の `catch (RpcException)` が、次の 4 群でその取り消しを縮退へ畳んでいた:
  1. `SearchBffEndpoints.cs:179`（`/bff/attribute-values` の gRPC 経路）: `IsRetrievalUnreachable(ex, ct)` は ct を見て外すが、その後ろの無条件の捕捉が拾い **502**。
  2. `AuthzScopeGrpcClient`（2 口）・`UserDirectoryGrpcClient`（5 口。#1653 の `HasRealmRoleAsync` を含む）: 取り消しを **`null`**（引けなかった／deny）＋ Warning。
  3. `QdrantVectorStore.KeywordSearchAsync`: 取り消しを「キーワード検索の縮退」へ畳み、計器 `search.keyword_degraded.total{reason=backend_error}` を 1 件積む ＋ Warning。
  4. `QdrantFullTextIndexHealthCheck`・`QdrantCjkNgramIndexHealthCheck`: 検査の打ち切りを **`Degraded`**（判定できない）へ畳む。
- 既存の試験はいずれも偽のクライアント（`CallInvoker` の差し替え）で、呼び出し元の取り消しを本物のチャネルの形で通していない。

## 母集合（規則 9: 誤りの側の文字列で全文書・全コードを走査する）

走査は `origin/develop`（478bba79）の両ユニットの本番コード（`src/platform/backend/**`・`src/knowledge/backend/**`。試験は除く）へ行った。`src/ai-stock-trading`（submodule・別リポジトリ）は対象外。

- 軸 1（無条件の捕捉）: `git grep -n "catch (RpcException"` → **20 行**（#1637 の 19 行に #1653 の `HasRealmRoleAsync` の 1 行が増えた）。
- 軸 2（共有クライアントの型を参照する本番ファイル）: `git grep -lE "AuthzScopeGrpcClient|UserDirectoryGrpcClient"` → **30 ファイル**（issue の 29 に #1653 の `GrpcApproverRoleDirectory` が増えた）。
- 軸 3（Qdrant の公式クライアントを使う本番ファイル）: `git grep -l "QdrantClient"` → **7 ファイル**。
- 軸 4（「null を受けてから確かめる」前提の口）: `git grep -n "catch (OperationCanceledException) when (!ct"` → **4 行**（DocumentService）。

突き合わせ:

- 軸 1 の 20 行 ＝ 対象 11 行（BFF 1・共有クライアント 7・Qdrant 3）＋ 除外 9 行（#1643 で守りを前に置いた 4 行・参照の形 5 行）。
- 軸 2 の 30 ファイル ＝ 定義 2 ＋ 呼び出し元 12（下の表で全部読んだ）＋ 登録（`Program.cs`）8 ＋ 注記だけで参照 8。
- 軸 3 の 7 ファイル ＝ 対象 3 ＋ 除外 4（取り込み側 `QdrantIngestionVectorStore`・`FusedCollectionsComposition`・`Program.cs` 2）。
- 軸 4 の 4 行 ＝ 対象（呼び出し元の token へ揃え直す）4。

### 対象（直す）

| # | 箇所 | 軸 | 直す前の畳み先 | 直し方 |
| --- | --- | --- | --- | --- |
| 1 | `SearchBffEndpoints.cs`（属性値の gRPC 経路） | 1 | 502 | 2 つの縮退の catch より前に `catch (Exception) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }` |
| 2 | `AuthzScopeGrpcClient.ResolveAsync`・`TryResolveScopeAsync`（`ResolveScopeAsync` は後者の上の deny への畳み込み） | 1・2 | `null`・deny・Warning | 同上（各口の縮退の catch の前） |
| 3 | `UserDirectoryGrpcClient.CheckUsernamesAsync`・`CheckDepartmentCodesAsync`・`GetUserAttributesAsync`・`HasRealmRoleAsync`・`GetStatusCoreAsync`（退職の窓・同期の 2 口） | 1・2 | `null`・Warning | 同上 |
| 4 | `QdrantVectorStore.KeywordSearchAsync` | 1・3 | 空・計器 `backend_error`・Warning | 同上 |
| 5 | `QdrantFullTextIndexHealthCheck`・`QdrantCjkNgramIndexHealthCheck` | 1・3 | `Degraded` | 同上（token は `cancellationToken`） |
| 6 | DocumentService の `GrpcOwnerAccountDirectory`・`GrpcOwnerRetentionDirectory`・`GrpcApproverRoleDirectory`・`GrpcDocumentReadScopeSource` | 2・4 | （2・3 を直すと）外へ出る OCE の token が内側の `bounded.Token` になる | 既存の `catch (OperationCanceledException) when (!ct.IsCancellationRequested)`（上限の時間切れ）の後ろに `catch (OperationCanceledException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }` |

### 呼び出し元を全部読み直した結果（軸 2 の 12 ファイル）

| 呼び出し元 | 使う口・渡す token | 直した後の取り消しの行き先 | 判定 |
| --- | --- | --- | --- |
| `BffScopeResolver.ResolveAsync`（BFF の文書・個人メモ・検索の各端点が呼ぶ） | `ResolveAsync`・要求の ct | OCE が端点の外へ出る。REST 経路の `catch … when (… && !ct.IsCancellationRequested)` と同じ | 変更不要 |
| `RagOrchestrator`・`GraphAccessResolver`・`SearchAccessResolver`・`WikiAccessResolver` | `ResolveScopeAsync`・要求の ct（gRPC の受け口では `context.CancellationToken`） | 同上（各 REST 経路は ct で絞っている）。周期処理から呼ぶ経路は無い | 変更不要 |
| `GrpcRegistrarAttributes`（McpServer） | `GetUserAttributesAsync`・`TryResolveScopeAsync`・要求の ct | 直す前は取り消しが `Unavailable`（「属性は検証できない」）になっていた。直した後は OCE | 変更不要 |
| `GrpcPlatformUserDirectory`・`GrpcDepartmentDomainDirectory`（DataSourceService） | `CheckUsernamesAsync`・`CheckDepartmentCodesAsync`・要求の ct | 直す前は取り消しが `Unavailable` → 502 になっていた。直した後は OCE | 変更不要 |
| `GrpcOwnerAccountDirectory`・`GrpcOwnerRetentionDirectory`・`GrpcApproverRoleDirectory`・`GrpcDocumentReadScopeSource`（DocumentService） | 各口・**上限つきの linked token** | 上限の時間切れ（要求は生きている）は既存の `when (!ct.IsCancellationRequested)` で従来どおり Unknown／`null`。要求の取り消しは OCE だが token が内側のもの | **対象 6**（要求の token へ揃え直す）。`null` の後の `ct.ThrowIfCancellationRequested()` は、障害の応答と取り消しが入れ違った場合の守りとして残す |

さらに上の呼び出し元の捕捉（OCE を別のものへ畳まないか）を `catch (Exception` / `catch (OperationCanceledException` で読んだ:
`PrivateNoteMaintenanceService`（L170・L402/L410）・`DataSourceSyncService`・`DataSourceSyncHostedService`・BFF の `IsTransportFailure(ex, grpc, ct)`・`IsTransient(ex, ct)` はいずれも
ct（停止の token）で絞っており、取り消しを縮退へ畳まない。

### 除外（理由つき）

| 箇所（軸） | 除外の理由 |
| --- | --- |
| `GrpcPrivateNoteNotifier.cs:77`・`GrpcDocumentTagWriter.cs:107`・`GrpcKnowledgeHealthReporter.cs:90`・`GrpcTagDictionaryReader.cs:54`（軸 1） | #1643 で `catch (Exception) when (ct.IsCancellationRequested)` を前に置いた |
| `GrpcServiceIntrospectionCollector.cs:81`・`GrpcToolDeclarationCollector.cs:92`・`GrpcToolInvoker.cs:111`・`:119`・`:128`（軸 1） | 参照の形（守りが先頭にあり、status を絞った捕捉はその後ろ） |
| `QdrantIngestionVectorStore`・`FusedCollectionsComposition`・`Program.cs` 2（軸 3） | `catch (RpcException` を持たない（取り込み側はすべて外へ出す／組み立てだけ） |
| 軸 2 の登録 8（`Program.cs`）・注記だけの 8（`DocumentReadGrpcClient`・`GrpcRagSearchTransport`・`GrpcDocumentTagWriter`・`GrpcKnowledgeHealthReporter`・`GrpcGraphNeighborExpander`・`AuthzScopeRestLog`・`ClientCredentialsServiceTokenProvider`・`LlmGatewayGrpcClientExtensions`） | 口を呼ばない |

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 対象 1〜5 は、呼び出し元の取り消しを縮退へ畳まず、**呼び出し元の token を持つ `OperationCanceledException`** で外へ出す。縮退のログを出さない。BFF は 502 にしない | 下の試験の表「呼び出し元の取り消し」 |
| AC-2 | 呼び出し元が取り消していない `CANCELLED`（受け口・Qdrant が返したもの）は従来どおりの縮退のまま（502／`null`・deny／縮退の計器 1 件／Degraded） | 同「対」 |
| AC-3 | 計器 `search.keyword_degraded.total` は、打ち切られたキーワード検索でも、打ち切られた索引の健全性の検査でも **1 件も増えない** | `KeywordSearch_呼び出し元の取り消しは縮退として数えず外へ出す`・`HealthCheck_呼び出し元の取り消しは_Degraded_へ畳まず外へ出す` |
| AC-4 | 対象 6 は、要求の取り消しを**要求そのものの token** を持つ OCE で伝える。上限の時間切れは従来どおり Unknown／`null` | `DirectoryCallerCancellationTests`・各口の既存の時間切れの試験 |
| AC-5 | 取り消しの試験は、127.0.0.1 の実サーバーへ本番と同じ既定値のチャネルで繋ぎ、**受け口が要求を受け取ってから**取り消す（素の OCE の注入を使わない）。前提（チャネルが `RpcException(Cancelled)` を投げる）を同じ器で表明する | `LoopbackGrpcServer`・前提の試験 |
| AC-6 | 守りを外す変異・status で判定する変異が赤になる（2 件以上） | 変異の記録 |
| AC-7 | 同じ形の他の箇所を走査し、直したもの・直さないもの（理由つき）を本仕様書に列挙する | 上の母集合 |

## 設計

- **形は #1643（`GrpcServiceIntrospectionCollector`）に揃える**: `catch (Exception) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }` を
  縮退の catch より前に置く。**判定は型でも status でもなく呼び出し元の ct で行う**（受け口が返した `CANCELLED` は取り消しではない）。
- **健全性検査が OCE を投げても Unhealthy へ倒れる経路は増えない**: `DefaultHealthCheckService` は呼び出し元の取り消しの OCE をそのまま伝え（要求の打ち切り）、
  登録の上限の時間切れ（呼び出し元は生きている）は登録の `failureStatus` で記録する。2 つの検査はいずれも `failureStatus: Degraded` で登録している（`RetrievalService/Program.cs`）。
- **DocumentService の 4 つの口は、2 つ目の OCE の catch で要求の token へ揃える**。1 つ目（`when (!ct.IsCancellationRequested)`）は上限の時間切れであり変えない。
- **失うもの（受容）**: DocumentService の 4 つの口の上限の時間切れは、共有クライアントの Warning（「gRPC 解決に失敗しました（Cancelled）」）を出さなくなる。
  共有クライアントから見れば呼び出し元の取り消しだからである。s2s トークン取得の途中の時間切れは元から出していなかった。上限を掛けるのは各口なので、記録が要るなら各口で出す（本件では足さない）。
- **試験の器 `LoopbackGrpcServer`**: #1643 の形（`CreateEmptyBuilder` ＋ `UseKestrelCore`・`IPAddress.Loopback` の動的ポート・起動直後にループバックだけを確かめる）を
  `Platform.Shared.Infrastructure.Tests/Testing/`・`Platform.Bff.Tests/` に写した（platform ユニットは knowledge の器を参照できない）。共有側は受け口 2 つ（`AuthzScope`・`UserDirectory`）を載せる口を足した。
  RetrievalService の器には、生成された `*Base` を持たない Qdrant の偽物を載せる口（`StartRawAsync`。全経路を 1 つの `RequestDelegate` で受け、待つか trailers-only の `grpc-status` を返す）を足した。
- **BFF の端点の結末の観測**: 打ち切った側の `HttpClient` には応答が届かないので、`IStartupFilter` で要求の外側に記録用の middleware を差し込み、状態番号と外へ出た例外を記録する。

## 試験の対応

| 箇所 | 呼び出し元の取り消し（AC-1・AC-5） | 対（AC-2） | 前提の表明 |
| --- | --- | --- | --- |
| BFF 属性値 | `BffAttributeValuesGrpcTests.A_caller_cancellation_over_a_real_channel_is_not_turned_into_a_bad_gateway` | `A_cancelled_status_answered_by_the_downstream_is_still_a_bad_gateway` | （#1643 の前提と同じチャネル） |
| 共有クライアント 9 口 | `AuthzGrpcClientCallerCancellationTests.呼び出し元の取り消しは引けなかったへ畳まず外へ出す`（Theory 9 件） | `受け口が返した_Cancelled_は従来どおり引けなかったである`（Theory 9 件） | `前提_本物のチャネルは取り消しを_RpcException_Cancelled_で投げる` |
| DocumentService の 4 口 | `DirectoryCallerCancellationTests.要求の取り消しは本物のチャネルでも要求のtokenを持つ取り消しとして伝わる`（Theory 4 件） | 各口の既存の時間切れの試験 | — |
| Qdrant キーワード検索 | `QdrantFullTextIndexObservabilityTests.KeywordSearch_呼び出し元の取り消しは縮退として数えず外へ出す` | `KeywordSearch_Qdrantが返した_Cancelled_は縮退として数える` | `前提_Qdrantの公式クライアントは本物のチャネルで取り消しを_RpcException_Cancelled_で投げる` |
| Qdrant 健全性検査 2 つ | `HealthCheck_呼び出し元の取り消しは_Degraded_へ畳まず外へ出す`（Theory 2 件） | `HealthCheck_Qdrantが返した_Cancelled_は_Degraded_である`（Theory 2 件） | 同上 |

## 記録の更新

- IADR の日付つき追記（新しい IADR は起こさない）: IADR-0401（共有クライアントと DocumentService の 4 つの口）・IADR-0417 決定 9（BFF の属性値）・IADR-0318 決定 3（Qdrant）。
- 試験仕様書: FR-05 T-19〜T-23 と対応するテストクラスの表、UC-01 T-26〜T-28 と実装マッピング。`check-test-spec-coverage.js --update` で床を上げる（新しい対 3 件）。

## 変異の記録（AC-6）

（コミット後に実施して追記する。）

## 結果

（検証の後に追記する。）

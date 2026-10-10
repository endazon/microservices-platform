---
title: 文書一覧の gRPC 応答が BFF の受信上限 4 MB を超えて空一覧に化ける件の応急処置（D-1 の受信上限の明示と、畳むときの WARN）
type: spec
status: done
related_ids: [FR-06, UC-03, SC-05, NFR-09, NFR-16, ADR-0029, IADR-0379, IADR-0402]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs: []
issue: "#1897"
---

# 仕様書: D-1（BFF → document）の受信上限の明示と、縮退の WARN（#1897 の応急処置）

## 起点

- 要求: `FR-06`（文書の閲覧）・`NFR-16`（east-west gRPC）・`NFR-09`。画面は `SC-05`（文書管理の一覧）。
- issue: #1897。2026-10-10 の h2c 往復の実測で、bff の sidecar（`reporter=source`）が `ListDocuments` を状態 2（UNKNOWN）と数え、
  利用者 `developer` の `GET /bff/documents` が 200・0 件を返した。
- 根本原因（issue の読み、develop `9d21ca64`）:
  1. `ListDocuments` は台帳の全件を 1 応答で返す（`DocumentReadUseCase.cs:37-54`、`GrpcService.cs:50-57`）。ページングが無い。
  2. BFF のチャネル（`GrpcClientExtensions.CreatePlatformChannel`）は `MaxReceiveMessageSize` を設定しておらず、grpc-dotnet の既定 4 MB で切る。
  3. 超過したクライアントは `RpcException(ResourceExhausted)` を投げてストリームを切る。Envoy は trailer を見ないので 200 を UNKNOWN に写す。
  4. BFF の `FetchListAsync` は `RpcException` を全 status について空一覧へ畳み、ログも出さない（`DocumentBffEndpoints.cs:464-483, 495-498`）。

## 範囲

本 PR は**応急処置**である。issue の是正案のうち次の 2 つを入れる。

- (b) 受信上限を明示する。構成で変えられるようにする。
- (c) 縮退へ畳む箇所で WARN を出す。

恒久対応の (a)（ページング・契約変更）は入れない（§残課題）。IADR は作らない（判断は本書に置く）。

## 決定

### 1. 受信上限は D-1 のチャネルだけに効かせる（共有部品は「渡したときだけ効く」引数を足すだけ）

共有の `GrpcClientExtensions.CreatePlatformChannel` に省略可能な引数 `maxReceiveMessageSize`（`int?`、既定 `null`）を足した。
`null` のときは `GrpcChannelOptions.MaxReceiveMessageSize` に**代入しない**。代入しないと grpc-dotnet の既定 4 MB が残る。

- 🔴 `MaxReceiveMessageSize = null` を代入すると「無制限」の意味になる。したがって `null` は代入せず、値があるときだけ代入する。
- 値を渡すのは `AddDocumentReadGrpcClient`（D-1）だけである。

**全クライアントの既定を上げる形は採らなかった。** 理由は次の 3 点である。

1. 事象が起きたのは D-1 の `ListDocuments` だけである。共有部品の呼び出し元は本番コードで 13 か所あり（下表）、残りは 1 件か小さい集合を返す。
   上げると、それらの経路でも異常に大きな応答を黙って受け取るようになる（メモリの上限が 4 MB から 64 MiB へ 16 倍になる）。
2. 恒久対応（ページング）が入れば、D-1 も既定 4 MB に戻せる。効かせる範囲が狭いほど、戻すときの確認も狭い。
3. 資格情報の付け方（平文 h2c ＋ `UnsafeUseInsecureChannelCallCredentials`）は共有部品に 1 つだけ置く。
   D-1 で `GrpcChannel.ForAddress` を直に書けば共有部品を触らずに済むが、資格情報の付け方の写しが 2 つになる。省略可能な引数のほうが写しが増えない。

既存の呼び出し元は引数を渡さないので、挙動は変わらない。これは試験で固定した（`Default_document_channel_accepts_a_response_above_4MB_while_the_shared_default_still_cuts_it` の後半）。

| 呼び出し元（本番コード） | 上限を渡すか |
| --- | --- |
| `Knowledge.Bff.Endpoints/Documents/DocumentReadGrpcClient.cs`（D-1） | **渡す** |
| `Knowledge.Bff.Endpoints/Search/AttributeValuesGrpcClient.cs` | 渡さない |
| `Platform.Shared.Infrastructure/Foundation/Authz/AuthzScopeGrpcClient.cs` | 渡さない |
| `Platform.Shared.Infrastructure/Foundation/Llm/LlmGatewayGrpcClientExtensions.cs` | 渡さない |
| `Platform.Shared.Infrastructure/Foundation/Introspection/GrpcServiceIntrospectionCollector.cs` | 渡さない |
| `McpServer/.../GrpcToolInvoker.cs`・`GrpcToolDeclarationCollector.cs` | 渡さない |
| `RetrievalService/.../GrpcGraphNeighborExpander.cs` | 渡さない |
| `DocumentService/.../GrpcPrivateNoteNotifier.cs` | 渡さない |
| `AiAnalysisService/.../GrpcRagSearchTransport.cs` | 渡さない |
| `GraphService/.../GrpcKnowledgeHealthReporter.cs`・`GrpcDocumentTagWriter.cs`・`GrpcTagDictionaryReader.cs` | 渡さない |

（走査: `git grep -n "CreatePlatformChannel" -- 'src/**/*.cs'` から `Tests/` を除いた 13 か所。）

`TagDictionary/ListNames`（graph が呼ぶ）と `ListVersions` も、件数に比例する応答を返す（issue の表）。
ただし現状の件数では上限に届きにくい。これらの扱いは恒久対応に送る。

### 2. 構成キーと既定値

- 構成キー: `Services:DocumentServiceGrpcMaxReceiveMessageSize`（バイト。環境変数では `Services__DocumentServiceGrpcMaxReceiveMessageSize`）。
  宛先キー `Services:DocumentServiceGrpc` と同じ接頭辞にして、並べて読めるようにした。
- 既定: **64 MiB（67,108,864 バイト）**。
- 🔴 0 以下・整数でない値（`64MB` など）は登録時（起動時）に `InvalidOperationException` で落とす。黙って既定へ戻すと、構成の誤りがまた「一覧が空」として現れる。
- deploy（helm・compose）は変えない。既定はコードが持つので、構成を足さなくても効く。

#### 既定値の根拠

代表的な `DocumentSummary` の 1 件を、protobuf の符号化の大きさで見積もった。

| 項目 | 想定 | 符号化（バイト） |
| --- | --- | --- |
| `id` | GUID 36 文字 | 38 |
| `title` | 日本語 30 字（UTF-8 で 90 バイト） | 92 |
| `status` | `published` | 11 |
| `markdown_uri` | `storage://documents/<GUID>/normalized.md` | 72 |
| `version` | 小さい整数 | 2 |
| `attributes` | `confidentiality`・`department`・`owner`（GUID）・`doc_scope` の 4 組 | 121 |
| `tags` | `規程`・`経費`・`engineering` の 3 件 | 29 |
| `created_at`・`updated_at` | Timestamp × 2（秒のみ） | 16 |
| `has_body` | bool | 2 |
| `content_fingerprint` | 64 文字 | 66 |
| repeated の tag ＋ 長さ | — | 3 |
| **計** | | **約 450** |

- 試験 `Default_limit_holds_the_current_ledger_with_headroom_while_4MB_does_not` は、同じ形の 1 件を `CalculateSize()` で測る。
  そのうえで「300〜700 バイトに入る」「下限の件数では 4 MB を超える」「下限の件数の 2 倍が 64 MiB に収まる」の 3 点を固定した。
- 稼働の台帳は**少なくとも 38,703 件**ある（#1895 で graph が孤立文書として数えた件数）。1 件 450 バイトとすると約 17.4 MB になる。
  issue の下限見積り（1 件 300 バイト）でも約 11.6 MB である。いずれも 4 MB（4,194,304 バイト）の 2.8〜4.2 倍にあたる。
- 64 MiB に収まるのは、1 件 450 バイトで約 14.9 万件、300 バイトで約 22.4 万件である。下限の件数に対して 3.8 倍以上の余裕がある。
- **これより上げない理由**: 上限は BFF が 1 要求で抱え得るメモリの上限でもある。一覧の口は管理者・運用者だけが呼ぶ（`SC-05`）ので同時要求は少ない。
  それでも 1 要求 64 MiB を超えて許すのは、応急処置の射程を超える。台帳がこれを超える前に、恒久対応（ページング）へ移る。
- **これより下げない理由**: 32 MiB では、1 件 450 バイトで約 7.5 万件しか収まらない。下限の件数に対して 1.9 倍しか余裕が無い。
  稼働の総件数は下限しか分かっていない（`SELECT count(*)` は未実測。issue の確認手順 2）。

### 3. 送信側（document-service）の上限は変えない

document-service の gRPC は `AddPlatformGrpcListener` の `AddGrpc()` を既定のまま使う。grpc-dotnet のサーバの既定は、送信が**無制限**、受信が 4 MB である。
送信は無制限なので、受信側だけを上げれば揃う。サーバの既定の `AddGrpc()` で 4 MB を超える応答（1 万件・約 4.5 MB）が届くことは、試験の本物のチャネルで確かめた。
受信 4 MB はサーバが受ける要求に効く。`ListDocumentsRequest` は利用者文脈しか持たないので、影響は無い。

### 4. 縮退へ畳むときの WARN

`Knowledge.Bff.Endpoints/Documents/DocumentReadFailureLog.cs` を新設した。`DocumentBffEndpoints` の 2 か所から呼ぶ。

| 箇所 | 載せる値 | 畳み方（変えない） |
| --- | --- | --- |
| `FetchListAsync` の catch（REST・gRPC 共通） | rpc=`ListDocuments`・輸送（`grpc` / `rest`）・状態（`RpcException.StatusCode`、REST は HTTP の状態、無ければ `-`）・例外の型・畳み先 `空一覧` | 空一覧 |
| `FetchAuthorizedAsync` の gRPC の catch | rpc=`GetDocument`・同上・畳み先 `404` | `null`（404 秘匿） |

- 文言は `AuthzScopeRestLog`（#1540 で足した、認可スコープ解決の縮退の WARN）と同じ形にした。カテゴリは `Knowledge.Bff.Endpoints.DocumentBffEndpoints` に固定した。
- 🔴 **例外本体・メッセージ・status の detail は載せない。** s2s トークン取得失敗（`InvalidOperationException`）のメッセージは、IdP の応答を含み得る。
  利用者 ID・属性・文書の表題・本文も載せない。
- 🔴 **畳み方は変えない。** issue は「`ResourceExhausted` を空一覧へ畳むかどうかも見直す」と書く。しかし畳み方を変えると、SC-05 の応答の契約が変わる。
  これは恒久対応の段で、ページングと一緒に決める。
- `FetchAuthorizedAsync` の REST の catch には WARN を足さなかった。理由は 2 つある。
  REST の east-west は別の作業で退役させており、同じ箇所を並行して触ることになる。また、事象は gRPC 経路にしか無い。
  `FetchListAsync` の catch は REST と gRPC で共有しているので、REST 経路でも WARN が出る。

## 母集合（規則 9）

| 走査 | 結果 | 扱い |
| --- | --- | --- |
| `git grep -n "CreatePlatformChannel" -- 'src/**/*.cs'` | 本番 13・試験 18 か所（本 PR で足した試験の 1 か所を含む） | 上限を渡すのは D-1 だけ。他は引数を省略しており、既定は変わらない（§決定 1） |
| `git grep -n "MaxReceiveMessageSize\|MaxSendMessageSize" -- src` | 変更前は 0 件 | 上限の設定はどこにも無かった。サーバもクライアントも既定のままだった |
| `IsTransportFailure` の呼び出し箇所（`DocumentBffEndpoints.cs`） | 3 か所（一覧の共通 catch・詳細の gRPC catch・詳細の REST catch） | 前の 2 か所に WARN を足した。REST の catch は §決定 4 の理由で足さない |
| `git grep -n "DocumentServiceGrpc" -- docs` | `docs/api/east-west-grpc.md`・h2c 実測の手順書 | 構成キーの追記は恒久対応の段へ送る（§残課題 4） |

## 規則 10・11

- 規則 10: 本書と `DocumentReadGrpcClient` のコメントに、導出値（件数・倍率・呼び出し元の数）を書いた。これらは変更後に数え直した。
  コード側のコメントには呼び出し元の数を書かなかった。数を書くと、呼び出し元が増えたときに古くなる。
- 規則 11: 窓（時間差）を扱う是正ではないので、該当しない。

## 受け入れ基準と試験の写像

試験はすべて `src/platform/backend/Bff/Platform.Bff.Tests/BffDocumentReadReceiveLimitTests.cs` にある。
受信上限の試験は 127.0.0.1 の実サーバー（`LoopbackGrpcServer`）と本番と同じ登録（`AddDocumentReadGrpcClient`）で行う。上限は `GrpcChannel` の中で効くので、偽の CallInvoker では測れない。

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | 4 MB を超える応答でも `GET /bff/documents` が gRPC 経路で権限内の文書を返す（空一覧に化けない） | `Bff_document_list_over_a_real_channel_is_not_empty_when_the_response_exceeds_4MB`（1 万件・約 4.5 MB） |
| 2 | 構成した上限が実際に効く | `Configured_limit_is_enforced_by_the_document_channel`（応答の半分の上限では `ResourceExhausted`、2 倍の上限では通る） |
| 3 | 未設定なら既定 64 MiB が効く。共有部品の既定 4 MB は変わらない | `Default_document_channel_accepts_a_response_above_4MB_while_the_shared_default_still_cuts_it` |
| 4 | 不正な構成値は起動時に落ちる | `Invalid_limit_fails_at_registration`（`0`・`-1`・`64MB`） |
| 5 | 既定値の根拠（1 件の大きさ × 台帳の下限） | `Default_limit_holds_the_current_ledger_with_headroom_while_4MB_does_not` |
| 6 | 上限超過を空一覧へ畳むとき、状態・経路・例外の型を含む WARN を 1 行出す。detail は出さない（陽性） | `List_fold_on_resource_exhausted_logs_one_warning_with_status_route_and_type` |
| 7 | 詳細を 404 へ畳むときも WARN を出す。例外のメッセージは出さない（陽性） | `Detail_fold_logs_one_warning_without_the_exception_message` |
| 8 | 成功時は WARN を出さない（陰性） | `Successful_list_logs_no_warning`、および #1 の後半 |

変異の確認として、次の 2 つを入れて走らせた。D-1 へ上限を渡さない変異と、WARN を Debug へ落とす変異である。
このとき #1・#2・#3・#6・#7 の 5 件が赤になることを確かめ、そのあと元に戻した。

## 残課題（恒久対応・次段）

1. **(a) ページング**: `ListDocumentsRequest` に `page_size` と `page_token` を、番号を足す形で入れる。SC-05 の一覧を、REST の `GET /documents/page`（#1575）と同じページングへ揃える。
   1 応答の大きさを、台帳の件数に比例させない（issue の受け入れ基準 2）。入れたら、D-1 の受信上限を既定へ戻すか見直す。
2. **`ResourceExhausted` の畳み方**: 「不達」ではなく後段の契約の問題である。空一覧へ畳まず 502 などで返すかを、(a) と一緒に決める。
3. **件数に比例する他の rpc**: `ListVersions`（1 文書の版の数）と `TagDictionary/ListNames`（graph が呼ぶ）。現状は小さいが、上限は既定 4 MB のままである。
4. **文書の追随**: `docs/api/east-west-grpc.md` の「5 つ目の面」に、構成キー `Services:DocumentServiceGrpcMaxReceiveMessageSize` と既定値を書く。
   本 PR では書かなかった。REST の退役の作業が同じ文書を並行して書き換えているからである。(a) で契約が変わるときにまとめて書く。
5. **稼働クラスタでの再測定**: `bff-service → document-service` の `reporter=source` に状態 2 が出ないことを確かめ、#1255 / #1517 の記録へ反映する（issue の受け入れ基準 4）。
   あわせて `SELECT count(*) FROM documents;` で総件数を実測し、§決定 2 の余裕を見直す。

## 検証

- `dotnet build src/platform/backend/backend.slnx` / `dotnet build src/knowledge/backend/backend.slnx`（警告 0・エラー 0）
- `dotnet test src/platform/backend/backend.slnx` / `dotnet test src/knowledge/backend/backend.slnx`
- `dotnet format <slnx> --verify-no-changes`（両ユニット）
- `node scripts/check-trace-blocks.js` / `node scripts/gen-knowledge-graph.js --check` / `node scripts/check-commit-messages.js` ほか、CI の文書系検査
- 稼働クラスタでの再測定はしていない（§残課題 5）。

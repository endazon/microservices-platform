---
title: Wolverine の既定 60 秒の実行期限の下で、外への呼び出しが既定の 100 秒か期限なしに頼る消費側に時間の予算を持たせる（#1640・前半＝共通部品と取り込み）
type: spec
status: done
related_ids: [FR-02, UC-04, FR-13, FR-17, ADR-0027, ADR-0029, ADR-0013, ADR-0016, ADR-0006, IADR-0478, IADR-0008, IADR-0002, IADR-0233]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-04（取り込み。失敗は再試行し、継続失敗はデッドレター）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（再試行・デッドレターは Wolverine の耐久メッセージ機能で賄う）
related_specs:
  - 20260927_issue-1621_diagram-coder-timeout-retain.md
  - 20260926_issue-1604_refresher-and-sync-loop-timeouts.md
issue: "#1640"
---

# 仕様書: 消費側の外への呼び出しの期限（#1640・前半）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- ユースケース: **UC-04**（取り込み）。失敗は再試行し、継続失敗はデッドレターへ（ADR-0027）
- 機能要求: **FR-02**（取り込み）。後半の PR で FR-13（Wiki 同期）・FR-17（ナレッジグラフ）・FR-06（削除の索引からの撤去）
- 関連 ADR: ADR-0027（Wolverine）、ADR-0029（gRPC の期限）、ADR-0013 / ADR-0016（埋め込み）、ADR-0006（計器）
- 関連 IADR: **IADR-0478（新設）**、IADR-0008 決定 B-2 の 2026-09-27 追記（#1621。同じ型の ConversionService の修正）、IADR-0002（取り込みの構造）、IADR-0233（Wolverine の共通部品の置き場）
- 起点 issue: #1640（PR #1624〔#1621〕の作業仕様書「母集合」軸 4）

## 事実（着手前の実測）

- WolverineFx 6.24.4 の `WolverineOptions.DefaultExecutionTimeout` は 60 秒（`DiagramCodingLimitsTests.Wolverine_の既定の実行期限は60秒である`、本 PR の `ConsumerHandlerTimeoutsTests` も突き合わせる）。
  受け口の ct はこの実行期限と停止要求の連結である（#1621 の `受け口の期限の方針は受け口の_ct_をその長さで取り消す` がローカルキューで実測）。
- 取り込み（`DocumentUpdatedConsumer`）の外への呼び出しと期限（origin/develop `c343b9d7`）:
  - 本文: `StorageDocumentContentReader`（`storage://` は S3 共通クライアント、http(s) は `AddHttpClient` の既定 100 秒）
  - 埋め込み: REST `LlmGatewayEmbeddingService`（`AddHttpClient`・既定 100 秒）／gRPC `LlmGatewayGrpcEmbeddingService`（`Deadline` なし）
  - Qdrant: `new QdrantClient(host, port)`（`grpcTimeout` 未指定＝期限なし。単例で、起動時の索引作成・後付け〔`QdrantBootstrapHostedService`・`QdrantCjkNgramBackfillHostedService`〕と共有）
  - 呼び出し回数: 既存チャンクの削除（コレクション数 = appsettings の 2 本）＋ 本文 1 ＋ チャンクごとに埋め込み 1 と登録 1（本文なしならメタデータ点 1 組）＋ 完了の発行（MassTransit）
- 捕捉: 取り込みの外への呼び出しの経路に例外を握る `catch` は無い（`StorageDocumentContentReader`・両埋め込み実装・`QdrantIngestionVectorStore` を確認）。
  S3 共通クライアントの `GetTextAsync` も握らない。したがって ct を連結して渡せば、どの輸送でも期限で呼び出しが終わる。
- LLM ゲートウェイ自身は上流（Voyage 等）への期限を持たない（`git grep -n -i timeout -- src/platform/backend/Services/LlmGateway ':!*Tests*'` は計器の分類 1 行のみ）。
- RabbitMQ の `consumer_timeout` は配備で上書きしていない（`git grep -n -i consumer_timeout -- deploy src` は 0 行）＝既定 30 分。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 縮めた受け口の期限（4 秒）の下で、本文・埋め込み・削除・チャンクの登録・メタデータ点の登録のどれかが止まっても、受け口の期限より前に**その呼び出し先の時間切れ**（`ConsumerTimeoutException`）として投げる（再試行・デッドレターへ）。受け口の ct は立っていない | `IngestionTimeoutTests.止まった依存先は受け口の期限より前に時間切れとして投げる`（FR-02 テスト仕様 T-18） |
| AC-2 | 対照: 呼び出し元の取り消しは時間切れに化けず取り消しのまま外へ出る | `IngestionTimeoutTests.呼び出し元の取り消しは取り消しのまま外へ出る`（T-19） |
| AC-3 | 判定の境界: 自分の期限が立ち呼び出し元の ct は立っていないときだけ時間切れ。両方が立てば呼び出し元を優先。期限の後の `RpcException(Cancelled)` も時間切れ、呼び出し元由来の `RpcException` はそのまま。期限前の失敗・成功は変えない。呼び出しへ渡る ct は連結 | `ConsumerCallTimeoutsTests`（T-19） |
| AC-4 | 時間切れは警告ログと計器 `messaging.consumer.timeout`（`messaging.step` / `messaging.timeout.target`）に残り、取り消しは数えない。Meter は `AddPlatformObservability` が収集する | `ConsumerCallTimeoutsTests.自分の期限が立てば時間切れとして投げ直し計器に残す`・`呼び出し元の取り消しは時間切れにせず外へ出す` |
| AC-5 | 埋め込みの総枠を呼び出しの前に判定し、使い切ったら残りを呼ばずに時間切れ（`embedding-budget`）。収まれば全チャンク | `IngestionTimeoutTests.埋め込みの総枠を使い切ったら…`・`総枠に収まれば全チャンクを埋め込む`（T-20） |
| AC-6 | 構成の既定値と、受け口の実行期限に最悪の所要時間が収まらない構成で起動を止めること（等号の境界・コレクション数を含む） | `IngestionTimeoutTests.構成が無ければ既定の上限になる`・`受け口の実行期限に最悪の所要時間が収まらなければ起動を止める`・`ConsumerHandlerTimeoutsTests`（T-21） |
| AC-7 | 本番の Program.cs が `DocumentUpdated` の受け口に 720 秒の実行期限を与え、上限を DI に置く。方針は指定した型にだけ効く | `IngestionTimeoutWiringTests`・`ConsumerHandlerTimeoutsTests.実行期限の方針は指定した型の受け口にだけ効く`（T-21） |

## 設計

- 決定と値の理由は **IADR-0478** に置いた（ここへ複写しない）。要点: 期限は受け口の呼び出しの側で ct の連結として付ける（共有の単例クライアントの既定を変えない）。
  時間切れは `TimeoutException` の派生へ投げ直す。取り込みは埋め込みの総枠で最悪の所要時間を有限にし、実行期限 720 秒を方針で与える。起動時に予算の和を検査する。
- 値は chart へ足さずコードの既定で持つ（`helm template` の差分は無い）。

## 母集合（同じ欠陥の走査。origin/develop `c343b9d7`）

- 軸 1（Wolverine の段）: `git grep -n -E "IPipelineStep<" -- src ':!src/ai-stock-trading' ':!*Tests*' ':!*/Tests/*'` → 製品の受け口 7 件
  （ConversionService `RawDocumentFetchedConsumer`・GraphService `DocumentDeletedConsumer` / `GraphDocumentSyncConsumer`・IngestionService `DocumentUpdatedConsumer`・
  RetrievalService `DocumentDeletedConsumer`・WikiService `DocumentDeletedConsumer` / `DocumentSyncConsumer`）と、コメント・共通部品の行。
- 軸 2（Wolverine のハンドラ全般）: `git grep -n -E "(Task|void|ValueTask) Handle\(" -- src ':!src/ai-stock-trading' ':!*Tests*'` → 同じ 7 件のみ。
  `UseWolverine(` を持つホストは 7 サービス（DataSourceService・DocumentService は発行だけで受け口を持たない）。
- 軸 3（MassTransit の受け口。Wolverine の実行期限の射程外）: `DocumentService` `DocumentNormalizedConsumer` の 1 件。**除外**（Wolverine の期限を受けない）。
- 除外と理由:
  - ConversionService `RawDocumentFetchedConsumer`: #1621 で直し済み（受け口 300 秒・図のコード化の期限）。pandoc は #1641 の射程。
  - GraphService `DocumentDeletedConsumer`: DB のみで外への呼び出しが無い（#1621 の作業仕様書と同じ判断を再確認）。
- **本 PR の射程**: IngestionService `DocumentUpdatedConsumer`（issue の表の 3 行分）と共通部品。
- **後半の PR（`Closes #1640`）の射程**: GraphService `GraphDocumentSyncConsumer`、RetrievalService `DocumentDeletedConsumer`、WikiService `DocumentSyncConsumer` / `DocumentDeletedConsumer`。

## 変異（1 か所ずつ書き換え、`git show HEAD:<path> > <path>` で戻した）

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M1 | **呼び出しごとの期限を外す**（受け口の `CallAsync` を `call(ct)` に） | `止まった依存先は受け口の期限より前に時間切れとして投げる` の 5 件（受け口の 4 秒で取り消しとして落ちる） |
| M2 | 埋め込みの総枠の判定を無効にする | `埋め込みの総枠を使い切ったら残りを呼ばずに時間切れとして投げる` |
| M3 | Program.cs から受け口の実行期限の方針を外す | `IngestionTimeoutWiringTests` |
| M4 | 起動時の予算の検査を無効にする | `受け口の実行期限に最悪の所要時間が収まらなければ起動を止める` の 3 件 |
| M5 | 捕捉の絞りから `!ct.IsCancellationRequested` を外す | `両方が立っていれば呼び出し元の取り消しを優先する` |
| M6 | 呼び出しへ連結ではなく呼び出し元の ct を渡す | `自分の期限が立てば…`・`期限の後に投げられた_RpcException_も…`・`呼び出しへ渡る_ct_は呼び出し元の_ct_ではない` |

M6 は最初、呼び出し元の ct が試験の文脈の ct（立たない）だったためハングした。呼び出し元に 10 秒の期限を持たせ、変異が赤で終わる形に直した。

## フォローアップ（射程外）

- 完了イベントの発行（MassTransit）には期限を付けない（受け口の実行期限の余白 40 秒で受ける）。
- 大きな文書の所要時間の実測（実埋め込みが要る）は行っていない。IADR-0478「値の理由」を参照。

## 検証

PR 本文に記録する（前景実行・コマンドと出力）。

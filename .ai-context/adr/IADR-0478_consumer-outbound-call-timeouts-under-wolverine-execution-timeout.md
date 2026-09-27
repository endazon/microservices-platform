---
title: IADR-0478 Wolverine の受け口の外への呼び出しは呼び出しごとの期限の下で行い、時間切れを取り消しと分けて記録し、最悪の所要時間が受け口の実行期限に収まることを起動時に検査する
type: impl-adr
status: Accepted
related_ids: [FR-02, UC-04, FR-06, FR-13, UC-07, FR-17, ADR-0027, ADR-0029, ADR-0013, ADR-0016, ADR-0006, IADR-0008, IADR-0002, IADR-0233, IADR-0239, IADR-0021]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（再試行・デッドレターは Wolverine の耐久メッセージ機能で賄う）
  - planning:projects/microservices-platform/03_usecases/01_usecases.md UC-04（取り込み。失敗は再試行し、継続失敗はデッドレター）
related_specs:
  - ../specs/20260927_issue-1640_consumer-outbound-call-timeouts.md
---

# IADR-0478: Wolverine の受け口の外への呼び出しの期限（#1640）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-27
- 決定者: claude（#1640。PR #1624〔#1621〕の作業仕様書「母集合」軸 4 で見つかった 7 か所）

## 起点・関連

- 関連する計画書 ID: FR-02 / UC-04（取り込み）、FR-13（Wiki 同期）、FR-17（ナレッジグラフ）
- 関連する計画 ADR: ADR-0027（Wolverine。再試行・デッドレター）、ADR-0029（gRPC の期限）、ADR-0013 / ADR-0016（埋め込み）、ADR-0006（計器）
- 関連する実装 ADR: IADR-0008 決定 B-2 の 2026-09-27 追記（ConversionService の同じ型の修正。`DiagramCodingLimits` と受け口の実行期限の方針）、
  IADR-0002（取り込みの構造）、IADR-0233（Wolverine の共通部品は `Platform.Shared.Infrastructure` に置く）、IADR-0239（段の登録）
- 作業仕様書: `../specs/20260927_issue-1640_consumer-outbound-call-timeouts.md`

## コンテキストと課題

WolverineFx 6.24.4 は受け口へ渡す ct に 1 通ごとの実行期限（`HandlerChain.ExecutionTimeoutInSeconds`、未設定なら `WolverineOptions.DefaultExecutionTimeout`＝60 秒）を連結する。
本リポジトリは ConversionService（#1621）以外どこでも上書きしていない。外への呼び出しが `HttpClient` の既定 100 秒や期限なしの gRPC（Qdrant・埋め込み）に頼る受け口では、
止まった依存先は**いつも受け口の ct が先に立つ形で**終わる。ログにも計器にも「取り消し」としか残らず、再試行のあとデッドレターへ入る。依存先の時間切れと停止要求の区別が付かない。
さらに取り込みの受け口は埋め込みの回数がチャンク数に比例し、止まっていなくても大きな文書で 60 秒を超え得る（未実測）。決めるべき点は 4 つあった。
**(a) 期限をどこに付けるか**、**(b) 時間切れをどう表すか**、**(c) 回数が入力で変わる呼び出しの予算をどう有限にするか**、**(d) 予算の組み合わせをいつ検査するか**。

## 検討した選択肢

- (a-1) 輸送ごとに付ける（`HttpClient.Timeout`・gRPC の `Deadline`・`QdrantClient` の `grpcTimeout`）。#1621 の REST / gRPC の図のコード化はこの形である。
  ただし Qdrant と gRPC の生成クライアントは**単例**で、検索（RetrievalService）や起動時の索引の後付け（IngestionService）と共有している。既定を変えると射程外の経路の期限まで変わる。
  S3（本文の `storage://`）は `Platform.Shared.Infrastructure` の共通クライアントで、同じく他の経路と共有している。
- (a-2) **受け口の呼び出しの側で ct を連結して付ける**（採用）。HTTP・gRPC・Qdrant・S3 はどれも ct を尊重するので、輸送を問わず 1 つの形で済む。共有クライアントの既定は変えない。
- (b-1) 取り消し（`OperationCanceledException`）のまま投げる。受け口の ct による取り消しと見分けが付かない（現状の欠陥そのもの）。
- (b-2) **`TimeoutException` の派生（`ConsumerTimeoutException`）へ投げ直し、警告ログと計器に残す**（採用）。再試行・デッドレターの流れは変えない（縮退の枝を持たない受け口なので）。
- (c-1) チャンク数に上限を置き、超える文書を切り詰める。FR-02 の索引が黙って欠ける（計画外の機能変更）ので採らない。
- (c-2) 受け口の実行期限だけを大きくする。チャンク数に上限が無い限り、止まった依存先が遅い文書の終盤で受け口の ct に負ける（最悪の所要時間が式にならない）。
- (c-3) **埋め込みの総枠を持ち、チャンクごとに呼び出しの前に判定する**（採用。#1621 の図のコード化の総枠と同じ形）。超過は最後の 1 チャンクの期限までに収まるので、
  チャンク数によらず最悪の所要時間が有限の式になる。
- (d-1) 実行時の失敗で知る。(d-2) **起動時に検査して起動を止める**（採用。#1621 と同じ）。

## 決定

1. **期限は `ConsumerCallTimeouts.RunAsync(step, target, timeout, call, ct)` で付ける**（`Platform.Shared.Infrastructure/Foundation/Messaging`）。呼び出し元の ct と
   `timeout` で立つ CTS を連結して呼び出しへ渡す。輸送ごとの設定（`HttpClient.Timeout` 等）は変えない（従前の既定は外側の上限として残る）。
2. 🔴 **時間切れと判定するのは「自分の期限が立ち、呼び出し元の ct は立っていない」ときだけ**（`!ct.IsCancellationRequested`。#1604 / #1621 と同じ絞り）。両方が立っていれば
   呼び出し元の取り消しを優先し、畳まずに外へ出す。自分の期限が立った後に呼び出しが投げたものは型を問わず時間切れの結果として扱う
   （`OperationCanceledException`・`RpcException(Cancelled)`・ライブラリの包み例外）。自分の期限が立っていなければ何も変えない。
3. 時間切れは `ConsumerTimeoutException`（`TimeoutException` の派生。段名・呼び出し先・期限を持つ）で投げ、警告ログと計器 **`messaging.consumer.timeout`**
   （Prometheus では `messaging_consumer_timeout_total`。タグは `messaging.step` と `messaging.timeout.target`。値域はコードの定数だけで閉じる）に残す。
   Meter `microservices-platform.messaging` は `AddPlatformObservability` が全サービスで収集する。受け口が持つ総枠の使い切りも同じ計器・同じ例外型で表す（`BudgetExhausted`）。
4. **受け口の実行期限は、最悪の所要時間が 60 秒を正当に超える受け口にだけ `HandlerExecutionTimeoutPolicy<TMessage>` で与える**（他のメッセージ型の既定は変えない）。
   方針を入れない受け口の予算は `ConsumerHandlerTimeouts.WolverineDefault`（60 秒。Wolverine の実物と試験で突き合わせる）に対して検査する。
5. **起動時の検査**: `ConsumerHandlerTimeouts.EnsureFits` が「呼び出しごとの期限 × 回数の和」が受け口の実行期限**未満**であることを確かめ、そうでなければ起動を止める
   （等しいときも止める。受け口の ct と同着では、どちらが先に立つかが定まらない）。構成の秒数は 1 未満を 1 に丸める。
6. **取り込み（`DocumentUpdatedConsumer`）の上限**（`IngestionTimeouts`）:

   | 上限 | 構成キー | 既定 | 適用先 |
   | --- | --- | --- | --- |
   | 本文の取得 | `Ingestion:ContentReadTimeoutSeconds` | 20 秒 | `IDocumentContentReader.ReadAsync`（S3・HTTP とも） |
   | 埋め込み 1 回 | `Ingestion:EmbeddingTimeoutSeconds` | 30 秒 | `IEmbeddingService.EmbedAsync`（REST・gRPC とも） |
   | Qdrant 1 回 | `Ingestion:VectorStoreTimeoutSeconds` | 10 秒 | チャンク・メタデータ点の登録。既存チャンクの削除はコレクション数倍（コレクション 1 本ごとに 1 回呼ぶ） |
   | 埋め込みの総枠 | `Ingestion:EmbeddingBudgetSeconds` | 600 秒 | チャンクを回し始めてからの経過。各チャンクの埋め込みの**前**に判定し、使い切ったら残りを呼ばずに時間切れ |
   | 受け口の実行期限 | `Ingestion:HandlerTimeoutSeconds` | 720 秒 | `HandlerExecutionTimeoutPolicy<DocumentUpdated>` |

   起動時の検査の式: 実行期限 ＞ 削除（コレクション数 × Qdrant）＋ 本文 ＋ 総枠 ＋ 最後の 1 チャンク（埋め込み ＋ Qdrant）。既定（コレクション 2 本）では
   20 ＋ 20 ＋ 600 ＋ 30 ＋ 10 ＝ 680 秒で、残り 40 秒は完了イベントの発行（MassTransit。期限の射程外）の余白である。**値は chart へ足さず、コードの既定で持つ。**

## 値の理由（取り込み）

- **大きな文書の所要時間は実測していない**（本作業は実 LLM・実埋め込みを呼ばない制約の下にある）。そのため値を 1 つの魔法の数にせず、**総枠を構成で持つ**（issue #1640 の「実測できない場合は上限を設定で持つ」）。
- 呼び出しごとの期限は「正常時の所要時間より十分長く、受け口の予算より十分短い」で決めた。
  - 本文: クラスタ内のオブジェクトストレージから数 MB 以下の Markdown を読む。正常時は 1 秒未満で、20 秒は 1 桁以上の余裕を持つ。
  - 埋め込み: 1 チャンクは 512 トークン（約 2 KB）以下。LLM ゲートウェイ自身は上流への期限を持たない（`HttpClient` 既定 100 秒）ので、ゲートウェイが上流で固まった場合も 30 秒で切る。
  - Qdrant: 1 点の登録（wait=true）と文書 ID の一致での削除。正常時はミリ秒単位で、10 秒は十分な余裕である。
- 総枠 600 秒は「従来の実質上限（受け口の既定 60 秒）の 10 倍」である。チャンク 1 つあたり埋め込み＋登録が 1 秒なら約 600 チャンク（約 1.2 MB の Markdown）まで収まる。
- 受け口の実行期限の上限は RabbitMQ の `consumer_timeout`（既定 30 分・本リポジトリの配備は上書きしていない）で決まる。受け口の処理中に配信が未 ack のまま残る経路では、
  これを超えるとブローカがチャネルを閉じる。720 秒（12 分）はそれより十分短い。総枠を上げるときは実行期限も上げ、30 分を超えないこと（起動時の検査は前者だけを見る）。
- 総枠を超える文書は、従来（60 秒で取り消し）と同じく再試行のあとデッドレターへ入る。違いは (1) 収まる文書が 10 倍に広がること、(2) 落ちたときに
  「埋め込みの総枠を使い切った（何チャンク中何チャンク）」がログと計器に残ることである。運用は `Ingestion:EmbeddingBudgetSeconds` と `Ingestion:HandlerTimeoutSeconds` を上げて再投入する。

## 結果

- 止まった依存先は、受け口の ct より先に呼び出しごとの期限で時間切れとして落ち、`messaging.consumer.timeout` に数えられる。取り消し（停止要求）は従来どおり取り消しのまま外へ出る。
- 取り込みは Wolverine の既定 60 秒ではなく 720 秒の下で動き、大きな文書を止まっていなくても切ることが無くなる。
- 固まった依存先が 1 回の試行を占有する時間は、呼び出しごとの期限で上から抑えられる（従前は受け口の期限＝60 秒まで）。

## 残るもの

- GraphService `GraphDocumentSyncConsumer`・RetrievalService `DocumentDeletedConsumer`・WikiService `DocumentSyncConsumer` / `DocumentDeletedConsumer` は、
  同じ部品を使う後続の PR で直す（#1640 を 2 つに分けた後半）。本 IADR へ日付つきで追記する。
- 完了イベントの発行（MassTransit）は期限の射程外。受け口の実行期限の余白で受ける。
- DB（EF Core）の呼び出しは Npgsql のコマンド期限（既定 30 秒）に任せ、本 IADR の射程に入れない。

## ［2026-09-27 追記 / #1640］後半: GraphService・RetrievalService・WikiService の 4 受け口

決定 1〜5 をそのまま適用した（部品は同じ `ConsumerCallTimeouts` / `ConsumerHandlerTimeouts`）。4 受け口とも最悪の所要時間が 60 秒を正当に超えないので、
**実行期限の方針は入れず**、既定の実行期限（`ConsumerHandlerTimeouts.WolverineDefault`）に対して起動時に検査する。値は chart へ足さずコードの既定で持つ。

| 受け口 | 上限 | 構成キー | 既定 | 起動時の検査の式（＜ 60 秒） |
| --- | --- | --- | --- | --- |
| GraphService `GraphDocumentSyncConsumer`（`graph-sync`） | 本文の取得 1 回 | `Graph:ContentReadTimeoutSeconds` | 20 秒 | 本文（20） |
| RetrievalService `DocumentDeletedConsumer`（`retrieval-delete`） | Qdrant の削除 1 回 | `Retrieval:VectorStoreDeleteTimeoutSeconds` | 10 秒 | (主 1 ＋ `Qdrant:FusedCollections` の本数) × 10。既定（追加 0 本）で 10、追加 5 本で 60 となり止まる |
| WikiService `DocumentSyncConsumer`（`wiki-sync`） | 本文の取得 1 回・Wiki.js 1 回 | `Wiki:ContentReadTimeoutSeconds` / `Wiki:WikiJsTimeoutSeconds` | 20 秒 / 15 秒 | 本文（20）＋ Wiki.js（15）＝ 35。アーカイブの枝は Wiki.js（15）だけ |
| WikiService `DocumentDeletedConsumer`（`wiki-delete`） | Wiki.js 1 回 | `Wiki:WikiJsTimeoutSeconds` | 15 秒 | Wiki.js（15） |

値の理由:

- 本文の取得の 20 秒は取り込み（決定 6）と同じ理由による（クラスタ内のオブジェクトストレージから Markdown を読む）。
- Qdrant の削除の 10 秒は取り込みの Qdrant 1 回と同じ理由による。文書 ID の一致での削除（wait=true）で、正常時はミリ秒単位である。
- **Wiki.js の期限はポートの 1 回の呼び出しごと**に与える。`UpsertPageAsync` / `ArchivePageAsync` / `DeletePageAsync` は GraphQL の要求をそれぞれ 2 回
  （パスで ID を引く ＋ 変更）出すので、15 秒は 2 回の要求をまとめて抑える。
  - 名前付きクライアントの `HttpClient.Timeout` は変えない。変えると、同じ登録を使う閲覧の経路（`GetRenderedContentAsync`）の期限まで変わる。
- DB（EF Core）の呼び出しは、決定の射程外のまま（Npgsql のコマンド期限）。60 秒からの残り（グラフ同期 40 秒・Wiki 同期 25 秒）で受ける。

🔴 **グラフ同期の本文の時間切れは「本文が取れない（null）」へ畳まない。** `IGraphContentReader` の null は「辺を触らずに成功で終える」縮退である。
時間切れを null に畳むと、本文の変化が再試行されないまま失われる（指紋は保存されるので、同じ指紋の再配信では本文を読み直さない）。
時間切れは例外のまま投げ、同期を保存せずに再試行・デッドレターへ委ねる。

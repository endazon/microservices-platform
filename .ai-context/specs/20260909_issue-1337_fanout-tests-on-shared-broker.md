---
title: 共有ブローカ／共有 DB で落ちる fan-out 統合テストを、実行ごとの一意化で塞ぐ
type: spec
status: done
related_ids: [FR-02, FR-13, FR-14, NFR, UC-03, UC-04, UC-05, ADR-0018, ADR-0027, IADR-0231, IADR-0232, IADR-0239, IADR-0245, IADR-0326, IADR-0414]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 共有した資源に残る「前の実行」を踏まない fan-out 試験（#1337）

## 起点

- FR-02（取り込み）／FR-13（Wiki 同期）／FR-14（宣言的パイプライン）／UC-04（更新イベントの伝播）
- 計画 ADR: `ADR-0027`（Wolverine への移行。手順 3 = リスニングキュー名にサービス名を前置する）／
  `ADR-0018`（宣言的パイプライン）
- 実装 ADR: [[IADR-0239]]（段宣言の `queue`）／[[IADR-0245]]（購読開始の待ち合わせ）／
  [[IADR-0326]]（`ApplicationAssembly` の明示固定）／[[IADR-0414]]（統合テストの門・**本件の親**）
- issue: #1337（[[IADR-0414]] が「残るもの」として切り出した限界）

🔴 **IADR は置かない。** 本件は試験基盤の是正であり、新しい実装判断ではない ——
既存の作法（`WolverineBrokerEdge` / `RawDocumentFetchedEdge` の runId スコープ、
`QueueOverrideFanOutTests` の宣言派生）を、まだ適用されていなかった 2 クラスへ広げただけである。
判断の記録は本仕様書と [[IADR-0414]] への日付つき追記に置く。

## 🔴 現状（作業ツリーを読んで確かめた）

落ちるのは 2 件。CI（Testcontainers・**クラスごとに専用コンテナ**）では緑、
外部供給（`PLATFORM_TEST_RABBITMQ` / `PLATFORM_TEST_POSTGRES`・**全クラスで 1 台を共有**）で赤。

- `Messaging.DocumentUpdatedFanOutTests.PublishOnce_BothSubscribersReceive`
- `Messaging.QueueOverrideFanOutTests.SharedQueueDeclaration_KeepsFanOut_ServicePrefixSeparatesQueues`

### 原因 A（配送側）: 共有ブローカに恒久キューと束縛が残り、他クラスの本物のイベントが滞留する

| 事実 | 出典（作業ツリー） |
| --- | --- |
| fan-out 2 クラスだけが**本番の固定サービス名**でキューを取る | `DocumentUpdatedFanOutTests.cs`（是正前 :118-119）／`QueueOverrideFanOutTests.cs`（是正前 :52,123-124。`SharedQueue` は `const`） |
| exchange だけは実行ごとに一意 | 同 :117 / :122（`Guid.NewGuid()`） |
| そのキューは `DocumentUpdated` exchange へ**恒久的に**束縛される | `WikiService/Program.cs:121-128`（`BindPlatformQueue` ＋ `ListenToPlatformQueue`）／`IngestionService/Program.cs:112-116` |
| 同じ run の他クラスが本物の `DocumentUpdated` を発行し続ける | `DocumentService/Program.cs` の `RoutePlatformEvent<DocumentUpdated>` ＋ `DocumentService/DocumentCrudTests.cs` |
| 束縛を最初に作るのは fan-out 以外のクラスでもある | `WikiService/WikiSyncTests.cs` / `Messaging/PipelineDeclarationLoadedTests.cs`（本番配線のホストを立てる） |
| 他のブローカ試験は**既にこの罠を避けている** | `Fixtures/RawDocumentFetchedEdge.cs:103-116`（runId でサービス名をスコープ）／`Fixtures/WolverineBrokerEdge.cs:128-151` |
| 直列化（`FanOutTestCollection`）は**同時実行しか防がない** | `Messaging/FanOutTestCollection.cs:48`（滞留は時間差でも起こる） |

**消費者が居ない間に溜まった分を先に処理するため、自分の 1 通に辿り着く前に 30 秒を使い切る。**

### 原因 B（受信後の処理側）: 共有 DB ＋ slug 一意索引 ＋ 固定 Title

| 事実 | 出典 |
| --- | --- |
| `Pages.Slug` に一意索引がある | `WikiService/Infrastructure/Persistence/WikiDbContext.cs:21` |
| Slug は **Title だけ**から導かれる | `WikiService/Domain/WikiPage.cs`（`CreateFromDocument` / `Sync` → `ToSlug(title)`） |
| 既存行の検索は `DocumentId` だけ。新 docId なら INSERT | `Features/Wiki/SyncDocument/DocumentSyncConsumer.cs:104-113` → `:131` `SaveChangesAsync` |
| テストの Title が**定数**だった | 是正前の `DocumentUpdatedFanOutTests.cs:168` / `QueueOverrideFanOutTests.cs:184` |
| 失敗は再試行（2s/10s/30s ＝ 42 秒 > 予算 30 秒）ののちデッドレターへ | `WolverineExtensions.cs:39-40,154-156` |
| Testcontainers はクラスごとに新 DB、外部供給は**全クラスへ同一接続文字列** | `PostgresFixture.cs:29`／`IntegrationTestFactory.cs`（`ConnectionStrings:DefaultConnection` の `UseSetting`） |

🔴 **B の症状は「受信しなかった」と 1 ミリも見分けが付かない**（例外はホスト側のログ 1 行で終わる）。

## 決定

### 決定 1: 購読キューを**実行ごとに一意**にする（宣言 `queue` の差し替え）

正本 `pipeline.json` から実行時に派生させ、`ingest` / `wiki-sync` / `wiki-delete` の `queue` を
runId 付きの値へ差し替えた一時ファイルを、両ホストへ `UseSetting("Pipeline:ConfigPath", …)` で渡す。
派生は `Fixtures/PipelineQueueOverride.cs` に 1 か所化した（`QueueOverrideFanOutTests` が
持っていた `WriteSharedQueueFixture()` を畳んだ）。

- 期待キュー名は従来どおり `WolverineExtensions.PlatformQueueName(...)` から導く
  —— **適用点（手順 3）を経る形は変えない。**
- `AutoPurgeOnStartup()` は**使わない**。器から本番の Program 配線を書き換えると、
  「本番と同じ配線で fan-out が成り立つ」という主張そのものが試験されなくなる。
- **Testcontainers 経路にも同じ道を通す**（経路を分岐させない。分岐させると
  「CI では通るがローカルでは通らない」形を新しく作る）。

🔴 **主張は弱めていない。** `DocumentUpdatedFanOutTests` は **ingest と wiki-sync へ同じ値**を
宣言する —— 既定（`<svc>.DocumentUpdated`）とまったく同じ形であり、
**キューを分けているのは前置だけ**という検査対象の性質はそのまま残る。
`QueueOverrideFanOutTests` の「両段へ同一値を宣言する」骨も不変で、値が実行ごとに変わるだけである。

**`wiki-delete` だけは別の値を宣言する**（同一サービスの 2 段へ同じ値を宣言すると前置後の
キュー名が衝突し、2 つの購読が 1 本へ潰れる）。従前ここは差し替えの対象外で、
`wiki-service.DocumentDeleted`（固定名）が共有ブローカ上に残り続けていた。

### 決定 2: 文書 Title を実行ごとに一意にし、**受信後の失敗を診断で見えるようにする**

Title に `docId` を混ぜる（slug が実行ごとに変わる）。併せて、購読ホストの **Warning 以上のログ**を
テスト側の器へ写し（`Messaging/HostFailureLog.cs`）、`Measured()` が失敗メッセージへ載せる。
一意制約違反（SQLSTATE `23505`）が記録されていれば**受信はしている**と読める。

⚠️ **診断は判定を 1 ミリも変えない。** 例外は投げず、採れなければ「採れなかった」と書く
（`ListenerReadiness.DescribeListeners` と同じ姿勢）。

### 決定 3: 派生器の fail-closed を**単体試験で縛る**

実機が無い環境でも回る試験で、次の 3 つを固定する。
差し替えが静かに空振りすると、購読者は既定キュー（滞留し得る側）を聴いたまま**緑になる** ——
#1337 が塞ごうとしている穴と同型なので、器の側に穴を作らない。

1. 指定した段の `queue` だけが差し替わる（指定しなかった段には触れない・正本は書き換わらない）
2. 当たらない段名があれば止まる（段名が変わったときに「理由を取り違えて」進まない）
3. 前置後のキュー名が同一サービス内で衝突するなら止まる
   （**差し替えなかった段の既定キューとの衝突**も見る。実効キュー名は本番と同じ式 `queue ?? input` で導く）

## 触ったファイル

| 面 | ファイル | 内容 |
| --- | --- | --- |
| 器（新規） | `Tests/Knowledge.IntegrationTests/Fixtures/PipelineQueueOverride.cs` | 正本からの派生（決定 1・3） |
| 試験（新規） | `Tests/Knowledge.IntegrationTests/Fixtures/PipelineQueueOverrideTests.cs` | 決定 3 の 9 件 |
| 診断（新規） | `Tests/Knowledge.IntegrationTests/Messaging/HostFailureLog.cs` | ホストの警告・例外の写し（決定 2） |
| 試験 | `Messaging/DocumentUpdatedFanOutTests.cs` | 決定 1・2 の適用（`RecordingProbe` へ `Failures` を追加） |
| 試験 | `Messaging/QueueOverrideFanOutTests.cs` | 同上。自前の派生関数を器へ畳んだ |
| 記録 | `docs/how-to/run-integration-tests-without-docker.md` | 「既知の限界」→「是正済み（実機での再実測待ち）」＋実機での確かめ方 |
| 記録 | `.ai-context/adr/IADR-0414_…md` | 「残るもの」へ日付つき追記（原因の切り分けと未了の実測） |

**本番コード（`src/**/Services/**`・`Shared/**`）は 1 バイトも変えていない。**

## 母集合の引き方（規則 1・2・6・9）

**誤りの側の語**（固定名でキューを取る・固定 Title・「原因未特定」と述べる記述）で全走査した。

| 軸 | 走査 | 生の結果 | 扱い |
| --- | --- | --- | --- |
| A | `git grep -n "PlatformQueueName(" -- .` | 17 行 | 器で runId スコープ済み 2 件（`RawDocumentFetchedEdge` / `WolverineBrokerEdge`）と、それを使う `WolverineBrokerEdgeTests` は**対象外**。ブローカを使わない `WolverineExtensionsTests`（純単体）・実装本体・検査器の文字列も対象外。**残る 4 行（fan-out 2 クラス）が対象** |
| B | `git grep -n "new DocumentUpdated(" -- Tests/Knowledge.IntegrationTests` | 4 行 | fan-out 2 件が対象。`WikiSyncTests`（`Status: "Published"` で同期側が早期 return し DB へ書かない・発行の経路も持たない）と `IngestToSearchInProcessTests`（コンシューマ直呼び・ブローカも DB も通らない）は**対象外** |
| C | `git grep -ln "WikiServiceFactory\|IngestionServiceFactory"` | 5 ファイル | 本番配線のホストを立てる＝固定名キューを作る側。ただし**自分で消費する**ので滞留しない。fan-out 2 クラス以外は対象外（`IntegrationTestFactory` は定義） |
| D | `git grep -rn "1337"`（CHANGELOG 除く） | 8 ファイル | 追随したのは `IADR-0414`（live な権威記録）と how-to。`.ai-context/specs/20260908_issue-1336_…`（凍結記録・当時の事実として正しい）と `deploy/istio/…`（uid 1337 の別物）は**対象外** |

🔴 **自己参照の断り（規則 8）**: 上の D の数は**本仕様書を書く前**の値である
（本仕様書と本 PR の変更を含めれば増える）。

## テスト（受け入れ基準）

- [x] 正本から `ingest` / `wiki-sync` / `wiki-delete` の 3 段の `queue` が差し替わる
- [x] 差し替えなかった段には `queue` を足さない／正本ファイルは書き換わらない
- [x] 当たらない段名があれば例外で止まる（`Derive` と `WriteDerivedFixture` の両面）
- [x] 同一サービス内でキュー名が衝突するなら止まる（**差し替えなかった段の既定キューとの衝突も**）
- [x] 差し替える段が空なら引数として弾く／出力は JSON として読める
- [x] 既存の単体試験が緑のまま（45 → **54**、Failed 0）
- [ ] 🔴 **統合テスト本体の実走**（実ブローカ ＋ 実 DB が要る。下記「実機が要る確認」）

## 変異試験（実出力。すべてビルドし直して実走し、戻したことは緑で確認した）

| # | 変異 | 赤 |
| --- | --- | --- |
| M-1 | 当たらなかった段の fail-closed を外す（`if (false && missing.Length > 0)`） | **2**（`Derive_当たらない段名があれば止まる` / `WriteDerivedFixture_正本に無い段名なら止まる`） |
| M-2 | 前置後のキュー名の衝突検査を外す | **2**（`Derive_同一サービスの2段が同じキュー名になるなら止まる` / `Derive_差し替えなかった段の既定キューと衝突しても止まる`） |
| M-3 | 段を選ばず全段の `queue` を差し替える | **4**（`Derive_指定しなかった段には手を触れない` ほか） |

戻した後: `Failed: 0, Passed: 54`。

🔴 **変異で当てられないもの**: 「fan-out 2 クラスが固定名のキューを取る形へ戻す」変異は、
実ブローカが無ければ赤にできない（統合テスト本体が skip されるため）。
**これは器の試験で埋められない種類の穴であり、実機の実測に委ねる。**

## 実機が要る確認（利用者の手が要る実測）

1. `docs/how-to/run-integration-tests-without-docker.md` の手順でブローカと DB を渡し、
   **全件を 2 回続けて**走らせる。**2 回目も緑であること**（1 回目の残りが 2 回目を汚さない）。
2. 実行の合間にキューを見る。

   ```bash
   nerdctl exec msp-test-mq rabbitmqctl list_queues name messages
   ```

   - fan-out の購読キュー名が**実行ごとに変わる**（`ingestion-service.docupd-<runId>` /
     `wiki-service.docupd-<runId>` / `wiki-service.docdel-<runId>`）
   - 実行を跨いで残る固定名のキューに**メッセージが積み上がっていない**
3. 落ちたら失敗メッセージ末尾の「ホストの直近の警告/例外」を読む。
   `23505` が出ていれば受信はしている（配送ではなく書き込み側）。

⚠️ **残渣**: 実行ごとのキューはブローカ上に残る（束縛も残る）。手順書の後片付け
（コンテナごと破棄）で消える。**器から `AutoPurgeOnStartup()` やキュー削除を行わない**のは
決定 1 の理由による。

## やらないこと

- **待ち時間を伸ばすこと**（#1038 が明示的に禁じている。1 秒も伸ばしていない）
- **`AutoPurgeOnStartup()` / 本番 Program 配線の器からの書き換え**（決定 1）
- **`FanOutTestCollection` の撤去**（効いていないことは分かっているが、戻すことを支える
  新しい実測を持っていない。同ファイルの断りのまま残す）
- **製品側の欠陥の是正**（下記）。本 PR は試験基盤だけを触る

## 🔴 計画へ環流すべき候補（本作業では起票しない）

**同名の文書が 2 件あると Wiki 同期が恒久的に止まる。**
`WikiPage.Slug` は Title だけから導かれ（`WikiPage.cs`）、`Pages.Slug` は一意索引
（`WikiDbContext.cs:21`）である。別 `DocumentId` の文書が同じ Title を持つと
`DocumentSyncConsumer` は INSERT を試み、`23505` で落ち、再試行 3 回のあとデッドレターへ行く。
**残るのはログ 1 行とデッドレターだけ**で、利用者にも運用にも「同期されていない」ことが見えない。

- 影響: 実データで同名文書は普通に起こり得る（部署ごとの「議事録」等）
- 判断が要る点: slug を `DocumentId` で一意化するのか、衝突時に接尾辞を付けるのか、
  そもそも slug の一意性を要件として保つのか（Wiki.js 側のパスは既に `DocumentId` 由来である）
- **これは計画側の裁定が要る別件であり、本 PR の射程外**である

## ［2026-09-09 追記 / PR #1355］同じ CI run で見つかった別の flake —— gRPC 試験の空きポート選択

本仕様書のコミット（テスト基盤のみ・本番コード無変更）を push した run で、**DocumentService.Tests の gRPC 試験 25 件**が
`Failed to bind to address http://127.0.0.1:33401: address already in use` で一斉に落ちた。直前の head では緑であり、
差分は `Knowledge.IntegrationTests` だけである。

**原因**: 各テストプロジェクトの `GrpcTestConfiguration.FreeTcpPort()` が `TcpListener(…, 0)` で OS に番号を選ばせて
**解放し**、その番号を後で Kestrel が bind する。解放から bind までの間に、並列に走る他のテストプロセスの**送信側ソケット**
（HttpClient 等）が同じ番号を取り得る —— 動的範囲（Linux の既定 32768〜60999）は送信側の割当にも使われるからである。
`33401` はその範囲内にある。**再現は確率的で、テストプロセスが増えるほど当たりやすい**（本 PR で Knowledge.IntegrationTests の
試験が 45 → 54 件に増え、同時に走る接続が増えた）。

**是正**: 候補を**動的範囲の外（20000〜29999）**から引き、bind できることを確かめてから返す（7 箇所。`Grpc:Port` に 0 は
「リスナを立てない」の意味なので Kestrel 側の動的割当は使えない）。残る衝突は「別プロセスが同時に同じ候補を引く」ときだけ
（候補 1 万個）。**主張（h2c が実 Kestrel に bind される）は変えていない。**

| 箇所 | |
| --- | --- |
| `{Document,Retrieval,Graph,Dashboard}Service/Tests/Grpc/GrpcTestConfiguration.cs` | knowledge 4 |
| `{LlmGateway,AuthorizationService}/Tests/Grpc/GrpcTestConfiguration.cs` | platform 2 |
| `Platform.Shared.Infrastructure.Tests/Foundation/Grpc/GrpcListenerBindingTests.cs` | 同型の選び方 |

🔴 **同型の写しが 7 箇所にある**ことは本追記で初めて数えた。共有の試験支援へ寄せるかは、IADR-0416 が記録した
「`FakeAuthzScopeClient` が 3 本目 —— 4 本目を作る前に共有の試験支援プロジェクトを立てる」と同じ判断に乗る（本 PR では広げない）。

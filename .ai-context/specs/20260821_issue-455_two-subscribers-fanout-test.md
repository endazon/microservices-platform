---
title: 作業仕様書 — DocumentUpdated の 2 購読者同時受信を統合テストで固定する（#455 Phase 0 / U0c）
type: spec
status: done
related_ids:
  - ADR-0027
  - UC-04
  - FR-02
  - FR-13
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - "ADR-0027（メッセージング基盤 = Wolverine。移行チェックリスト手順 3 / 手順 7 / 手順 8）"
related_adrs:
  - IADR-0219
  - IADR-0021
issue: "#887"
---

# 作業仕様書: DocumentUpdated の 2 購読者同時受信を統合テストで固定する（#455 Phase 0 / U0c）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: `ADR-0027` 移行チェックリスト **手順 3**（リスニングキュー名にサービス名を前置する）・
  **手順 7**（移行後、対応表が保存されていることを実ブローカで検査する）・
  **手順 8**（実ブローカ結合テストを完了条件に含める）
- ユースケース: `UC-04`（取り込み → 変換 → カタログ登録のイベント連鎖）
- 機能要求: `FR-02`（取り込み・チャンク化・索引）/ `FR-13`（Wiki 同期）
- 実装 issue: `#887`（親 `#455` / `#441`）

## なぜ要るのか

`DocumentUpdated` は **fan-out**（1 発行 → 2 購読者）である。

```
DocumentUpdated: 発行 [knowledge/DocumentService]
               → 購読 [knowledge/IngestionService, knowledge/WikiService]
```

移行チェックリスト **手順 3**（リスニングキュー名にサービス名を前置する）を誤り、
2 購読者が**同一キューを共有**すると、RabbitMQ の競合コンシューマ（competing consumer）に
なって**片方だけがメッセージを受け取る**。

🔴 **この退行は例外もログも出さない。** publisher confirms は成功を返し、受け取らなかった側は
「そもそも来なかった」ので何も書かない。ビルドもユニットテストも緑のまま、業務イベントが半分消える。

### いま何が揃っていて、何が無いのか

| | 状態 |
| --- | --- |
| 統合テストが**本番の配線**を通る | ✅ 済（PR #884 / U0a） |
| Worker を統合テストへ**ホストできる** | ✅ 済（PR #886 / U0b） |
| **2 購読者を同時に立てて両方が受信することを assert するテスト** | 🔴 **無い（本作業）** |

🔴 **PR #886 で新設した `IngestionServiceFactory` は、現時点でどのテストからも参照されていない。**
器だけが在って使われていない状態であり、**本作業を消化しないと器が死蔵される**。

## 母集合（着手前に自分で引いた）

規則 9（誤りの側の文字列で引く）に従い、**本作業が着地すると偽になる記述**を 4 軸で走査した。
走査対象は追跡下の全ファイル（`obj/` `bin/` および submodule `src/ai-stock-trading` を除く）。

| 軸 | 検索語 | 生の件数 | 追随対象 |
| --- | --- | --- | --- |
| 1 | `#887` | 3 | 2（`IntegrationTestFactory.cs` の 2 行） |
| 2 | `U0c` | 5（うち 1 は `pnpm-lock.yaml` の base64 偶然一致） | 0（すべて `.ai-context/specs/` = 凍結記録） |
| 3 | `死蔵` | 1 | 1（`IntegrationTestFactory.cs`） |
| 4 | `2 購読者` 等 | 12 | 2（`docs/tech/tech-requirements.md` の残る穴 2） |

### 追随する live な文書

1. **`docs/tech/tech-requirements.md`**「Wolverine 移行の前提」の**残る穴 2** ——
   「🔴 テストそのものはまだ書いていない」が偽になる。**残る穴 1（`Pipeline:ConfigPath` 未設定）は
   本作業の射程外なので残す。**
2. **`src/knowledge/backend/Tests/Knowledge.IntegrationTests/Fixtures/IntegrationTestFactory.cs`**
   の死蔵注記 —— 当の注記が「消化されたらこの注記を消すこと」と自分で書いている。
   `IngestionServiceFactory` は本作業で使われるようになるが、
   🔴 **`ConversionServiceFactory` は依然としてどのテストからも参照されない。**
   注記を丸ごと消さず、**参照されないほうだけを残す**（消すと死蔵が見えなくなる）。

### 黙って除外したものと、その理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| `.ai-context/specs/20260821_issue-455_{integration-tests-production-wiring,workers-in-integration-tests}.md` | **凍結記録**。`status: done` の作業仕様書であり、本文プロズを後から書き換えない。U0c を「これから」と書いているのは**その時点で正しい**記述である |
| `Knowledge.IntegrationTests.csproj:42` の `U0b` コメント | 本作業で偽にならない（Worker を載せた理由の記録であり、載せた事実は変わらない） |
| `src/pnpm-lock.yaml:902` | base64 文字列の偶然一致。文章ではない |
| `.ai-context/adr/IADR-0118`・`IADR-0195`・`docs/tests/TEST_STRATEGY.md` 等の「統合テスト 43/43」 | **測定条件つきの過去実測値**であり、その時点の事実として正しい。本作業は件数を 43 → 44 へ増やすが、**過去の測定値を遡及書き換えしない** |

🔴 **導出値「43」は走査ではなく数え直す**（規則 10）。本作業は**テストを 1 件足す**ので、
着地後の基準は **44** になる。**PR 本文と検証節には 43 → 44 と増分を明示する**（「44 件通った」だけ
書くと、1 件減って 1 件増えたのか、純増なのかが読めない）。

## 設計上の判断

### 1. 受信をどう観測するか —— **各コンシューマの終端副作用**を見る

issue が挙げた選択肢のうち「副作用を見るほうが本番に近い」を採る。テスト用の
`IConsumeObserver` を差すと「受信パイプラインに届いた」ことしか分からず、
**コンシューマが実際に仕事をしたか**は分からない。

| 購読者 | 終端副作用 | 観測方法 |
| --- | --- | --- |
| `WikiService`（`DocumentSyncConsumer`） | **`wiki_svc.Pages` への行 upsert** | Testcontainers Postgres の実行 —— **本物の永続化**を見る |
| `IngestionService`（`DocumentUpdatedConsumer`） | `IIngestionVectorStore.UpsertChunkAsync`（Qdrant） | 記録するフェイクへ差し替えて呼び出しを見る |

🔴 **Ingestion 側だけフェイクなのは、Qdrant / LLM ゲートウェイをコンテナで立てていないからである。**
これは妥協であり、**そう明記する**。差し替えるのは**外向きのポート（アダプタ）だけ**で、
**メッセージングの配線は 1 行も差し替えない** —— 本作業が試験したいのはトポロジであって
取り込みの業務ロジックではない。ユニットテスト（`DocumentUpdatedConsumerTests`）が後者を担う。

同じ理由で WikiService 側も `IWikiJsClient` / `IWikiContentReader` を差し替える
（Wiki.js を立てていない）。**DB 行は本物である。**

### 2. ポートの差し替えは `WithWebHostBuilder` で行う —— `IntegrationTestFactory.cs` を触らない

`IntegrationTestFactoryBase.AdditionalServices` を使うと、テストごとの差し替えのために
**共有のファクトリ定義を編集する**ことになり、他のテストへ波及する。ASP.NET Core 標準の
`factory.WithWebHostBuilder(b => b.ConfigureServices(...))` は**派生ファクトリを作る**ので
波及しない。**既存 5 ファクトリと 2 Worker ファクトリの宣言は 1 文字も変えない。**

### 3. Postgres は 1 つで足りる（issue の確認事項への回答）

`IClassFixture<PostgresFixture>` は**テストクラスごとに 1 インスタンス**を作るため、
本テストクラスは専用の Postgres / RabbitMQ コンテナを得る。同時に立てる 2 ホストのうち
**`DbContext` を持つのは `WikiService` だけ**（`IngestionService.Worker` の `DbContext` 型は
実測 0 件）なので、**スキーマも DB も分ける必要は無い**。

### 4. バインド完了前の Publish を避ける

`MassTransitHostOptions.WaitUntilStarted = true`（issue #33 対策。基底が既に設定済み）に依存する。
ただし `WebApplicationFactory` はホストを**遅延起動**するので、**publish の前に両ホストを明示的に
起こす**（`CreateClient()` を両方に対して呼ぶ）。ここを省くと片方がまだキューをバインドしておらず、
**手順 3 が正しくてもテストが落ちる**（偽陽性）。

## やること

1. `Knowledge.IntegrationTests/Messaging/DocumentUpdatedFanOutTests.cs` を新設する
2. `IngestionServiceFactory` と `WikiServiceFactory` を**同時に**立て、**同一の Testcontainers
   RabbitMQ** へ接続する
3. `DocumentUpdated` を **1 回**発行する
4. **両方の購読者が受信したこと**を assert する

## 受け入れ基準

1. `DocumentUpdated` を 1 回発行し、**IngestionService と WikiService の両方が受信した**ことを
   assert するテストがある
2. **変異試験**: 2 購読者が**同一キュー名**を共有する状態を作ると、**このテストだけが落ちる**
   - 🔴 変異が**実際に当たった**ことを assert してから判定する
3. 既存の統合テストが**緑のまま**（43 件 → **44 件**。既存 43 は 1 件も減らない）
4. PR #886 で新設した `IngestionServiceFactory` が**実際に使われている**
5. `dotnet test` 両ユニット Failed 0 / `dotnet format --verify-no-changes` EXIT=0 / 検査器一式 EXIT=0

## 変異試験の設計

**変異**: 両サービスの `Program.cs` で、`AddPlatformPipelineStep<T>(pipeline)` を
`AddConsumer<T>().Endpoint(e => e.Name = "<共有キュー名>")` へ置き換える
（＝手順 3 の「サービス名を前置する」を怠った状態を再現する）。

🔴 **変異が当たったことを先に確かめる**（本セッションで、当たっていない変異が別の理由で落ちて
「成功」に見える事故を 2 回踏んでいるため）:

- `git diff --stat` で 2 ファイルが変わっていること
- **ビルドが EXIT=0** であること（コンパイルエラーで落ちたなら、それは変異ではなく破壊である）
- 失敗メッセージが**期待した形**（片方だけが受信した）であること —— タイムアウトや
  接続エラーで落ちたなら、キュー共有が原因だと言えない

**復旧の確認**: `git diff` が空・変異残骸 0・復旧後にテストが緑。

## 実測（すべてローカルで dockerd を起こして実走）

### 受け入れ基準の充足

| # | 基準 | 実測 |
| --- | --- | --- |
| 1 | 2 購読者の同時受信を assert するテストがある | ✅ `Messaging/DocumentUpdatedFanOutTests.cs` |
| 2 | 変異試験で**このテストだけ**が落ちる | ✅ **Failed 1 / Passed 43 / Total 44**（下記） |
| 3 | 既存 43 件が緑のまま・純増 1 | ✅ **43 → 44**（`Failed 0, Passed 44, Total 44`） |
| 4 | `IngestionServiceFactory` が実際に使われている | ✅ 本テストが `WikiServiceFactory` と同時に立てる |
| 5 | ビルド / format / 検査器 | 下記 |

### 負のコントロール（観測点が本当に効いているかの確認）

🔴 **「テストが緑だった」ことを、そのまま「観測できている」と読み替えない。**
初回実行は 3 秒で緑になったが、それだけでは**待ち受けが常に true を返すだけの張りぼて**でも
同じ結果になる。**発行していない別の `Guid` を待つ**ように書き換えて実測した。

```
負のコントロール: BUILD EXIT=0 / TEST EXIT=1
  → Failed: 1, Passed: 0（両方の待ち受けがタイムアウトする）
復旧後            : 変異残骸 0
```

観測点は実際に「その `DocumentId` が届いたか」で開閉している。

### 変異試験（手順 3 の退行の再現）

**変異**: 両サービスの `Program.cs` で `AddPlatformPipelineStep<T>(pipeline)` を
`AddConsumer<T>().Endpoint(e => e.Name = "document-updated")` へ置き換え、
**2 購読者を同一キューへ寄せた**（＝リスニングキュー名にサービス名を前置しなかった状態）。

**変異が当たったことの確認**（先に行う）:

```
git diff --stat → 2 files changed, 2 insertions(+), 2 deletions(-)
-    x.AddPlatformPipelineStep<DocumentUpdatedConsumer>(pipeline);
+    x.AddConsumer<DocumentUpdatedConsumer>().Endpoint(e => e.Name = "document-updated");
-    x.AddPlatformPipelineStep<DocumentSyncConsumer>(pipeline);
+    x.AddConsumer<DocumentSyncConsumer>().Endpoint(e => e.Name = "document-updated");

dotnet build src/knowledge/backend/backend.slnx → BUILD EXIT=0（error 0 件）
```

🔴 **ビルドが通ることが重要である。** コンパイルエラーで落ちたなら、それは変異ではなく破壊であり、
テストが落ちても何も証明しない。

**結果**:

```
変異あり: Failed: 1, Passed: 43, Total: 44
変異なし: Failed: 0, Passed: 44, Total: 44
```

**落ち方も期待どおりだった** —— タイムアウトでも接続エラーでもなく、
**取り込み側の assert は通り、Wiki 側の assert が落ちた**（＝競合コンシューマの形。
1 通のメッセージを片方だけが取った）:

```
Expected synced to be True because WikiService が DocumentUpdated を受信し
Wiki 同期メタデータを永続化すること（受信しなかった場合、手順 3 の競合コンシューマ化を疑う）,
but found False.
  at ...DocumentUpdatedFanOutTests.PublishOnce_BothSubscribersReceive()
```

🔴 **そして既存 43 件は 1 件も落ちなかった。** これが本作業の存在理由そのものである ——
**手順 3 を怠っても、本テストが無ければリポジトリ全体が緑のままだった。**

**復旧の確認**: `git diff` 空 / 変異残骸の走査 0 件 / 復旧後 `BUILD EXIT=0`・44/44 緑。

### 追随（母集合どおり）

| 文書 | 変更 |
| --- | --- |
| `docs/tech/tech-requirements.md` | 残る穴 **2** を「塞いだ」へ。**残る穴 1（`Pipeline:ConfigPath` 未設定）は射程外なので残す。** 「残る穴は 2 つある」→「1 つである」。防壁表の「統合テスト ❌ 検出できない」→「⚠️ fan-out の退行だけは捕まえる」。trace ブロックの `specs:` / `issues:` へ本作業を追加 |
| `Fixtures/IntegrationTestFactory.cs` | 死蔵注記を**消さずに縮めた** —— `IngestionServiceFactory` は使われるようになったが、🔴 **`ConversionServiceFactory` は依然として未使用**である。丸ごと消すと死蔵が見えなくなる |

🔴 **trace ブロックの追随を、指摘される前に自分で入れた。** 同型の漏れを本セッションで 2 回続けており
（#885 で検査器の設計を起票済み）、**欠落は「誤りの側の文字列」では引けない**（無い ID は文字列として
存在しない）。**新しい ID の側から逆に引く**しかない。

## 残る穴（本作業の射程外）

- **`Pipeline:ConfigPath` が未設定**であり、`pipeline.json` の段宣言・`queue` 上書きは通っていない（U0d）
- **外向きのポートはフェイクである**（Qdrant / LLM ゲートウェイ / Wiki.js を立てていない）。
  差し替えていないのはメッセージングの配線であり、試験対象もそこである
- 本テストが捕まえるのは**手順 3（キュー名の衝突）**であって、
  **トランスポートの取り違え（MT 発行 → Wolverine 購読）ではない**。後者は
  `check-event-topology.js`（トランスポート認識化済み）が静的に見る

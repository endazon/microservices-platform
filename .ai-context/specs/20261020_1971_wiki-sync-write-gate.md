---
title: Wiki 同期の書き込みの門 —— Wiki.js へ同時に書き込む数を設定値（既定 1）にし、同期と撤去の両方の受け口で共有する。既に非公開のページへのアーカイブの update を送らない（#1971）
type: spec
status: done
related_ids: [FR-13, UC-07, ADR-0011, ADR-0027, IADR-0021, IADR-0478, IADR-0557, IADR-0563]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-13 Wiki 閲覧・同期）
issue: "#1971"
---

# 仕様書: Wiki 同期の書き込みの門（#1971 の続き）

## 起点となる計画書（トレーサビリティ）

- FR-13（Wiki 閲覧・同期）・UC-07。
- ADR-0011（Wiki.js。閲覧・編集の実体）・ADR-0027（Wolverine。fan-out・再試行・DLQ）。
- 実装判断の記録: [IADR-0563](../adr/IADR-0563_wiki-js-write-gate.md)。前段は [IADR-0557](../adr/IADR-0557_wiki-sync-skip-unchanged-writes.md)
  （同じ内容の update を省く・並列度は上げない）。期限の形は [IADR-0478](../adr/IADR-0478_consumer-outbound-call-timeouts-under-wolverine-execution-timeout.md)。
- 起点: #1971（PoC・2026-10-16。`pages.create` が 1 件約 8.4 秒・毎分約 7 件。`wiki-service.DocumentUpdated` に約 4,800 件）。
  #1979（IADR-0557）で部分対応済み。本件はその残り（IADR-0557 の残余リスク (d)・並列度の明示・アーカイブの空の update）を扱う。

## 律速の原因（コードで確かめたこと）

1. **Wiki.js の中の処理である**（IADR-0557 の実測。wiki-service 側は 1 件 0.02 秒）。create は描画と木の再構築の子プロセスを 2 本、update は描画を 1 本起こす。
2. **wiki-service の受け取り方は、既定で 1 キュー 1 通ずつである。** 本件で Wolverine 6.24.4 の端点を組み立てて読んだ値:

   | 端点 | Mode | ListenerCount | MaxDegreeOfParallelism |
   | --- | --- | --- | --- |
   | `wiki-service.DocumentUpdated` | Inline | 1 | 5（Inline では使われない） |
   | `wiki-service.DocumentDeleted` | Inline | 1 | 5（同上） |

   - RabbitMQ のリスナーは Inline（1 通の処理を待ってから次を受け取る）で、並びを決めるのはリスナーの数である。issue の推測
     （「既定の並列度はプロセッサ数で、limits 500m なので 1」）は、結論（実効 1）は合うが理由が違う。`MaxDegreeOfParallelism` の既定は
     「プロセッサ数と 5 の大きい方」で、Buffered のときだけ効く。PoC の毎分 7 件 ≒ 60 ÷ 8.4 は「1 キュー 1 通ずつ」と整合する。
   - **2 つのキューは別のリスナーなので、同期 1 本と撤去 1 本は並ぶ。** create（同期）と delete（撤去・露出の門の撤去）は
     どちらも Wiki.js の木の再構築を走らせ、並ぶと `pageTree` の主キーで衝突し得る（IADR-0557 の残余リスク (d)）。
3. **一括アーカイブの後の再発行で、既に非公開のページへアーカイブの update を送り直している。** `ArchivePageAsync` は今の表題・本文・タグを
   送り返し `isPublished=false` にするだけで、既に false でも update を送る。update は内容が同じでも描画の子プロセスを起こす
   （IADR-0557 の実測: CPU 0.5 で約 5 秒・CPU 1 で約 2.6 秒）。

## 決めたこと（詳細は IADR-0563）

1. **Wiki.js へ同時に書き込む数の上限を設定値 `Wiki:MaxParallelWrites`（環境変数 `Wiki__MaxParallelWrites`）にする。既定 1、許す範囲 1〜4。**
   範囲の外・整数でない値は起動で止める（黙って丸めない）。
2. **同期と撤去の両方の受け口が、プロセスに 1 つの門（`WikiJsWriteGate`）を通ってから Wiki.js を呼ぶ。** 両リスナーの数も同じ値へ明示する
   （既定に頼らない。門より多く受け取って門で待つ形にもしない）。
3. **アーカイブは、Wiki.js のページが既に非公開（`isPublished=false`）なら update を送らない。** 往復は `singleByPath` の 1 回のまま（取る列に `isPublished` を足す）。
   読めない（欠落・null・true）ときは従来どおり送る。
4. 門の待ちには Wiki.js 1 回と同じ期限を付け、超えたら呼び出し先 `wiki-js-gate` の時間切れ（`ConsumerTimeoutException`）として再試行へ委ねる。
   最悪の所要時間の起動時検査に門の待ちを足す（同期 20＋15＋15＝50 秒・撤去 15＋15＝30 秒。既定の実行期限 60 秒に収まる）。

### 採らなかったもの

- 並列度を上げる（既定を 2 以上にする）: IADR-0557 の実測で作成が壊れる（並列 2 で 2/8・並列 4 で 6/8 が duplicate key）。毎分も 1 割強しか増えない。
- バッチ化: Wiki.js 2.5 の GraphQL に複数ページの作成・更新の口は無い（`pages.create` / `pages.update` は 1 ページずつ）。木の再構築は 1 回の作成ごとに走る。
- 駆動スクリプトの背圧（Wiki のキューを見る。issue の案 5）: IADR-0557 のとおり運用の判断。本件では入れない。
- Wiki.js の CPU（案 3）・fan-out の選択（案 4）: IADR-0557 のとおりオーナー・計画の判断。

## 母集合（規則 9・10）

- Wiki.js を呼ぶ受け口: `git grep -n "wikiJs\.\(Upsert\|Archive\|Delete\)PageAsync" -- src` → `DocumentSyncConsumer`（3 口: アーカイブ・露出の門の撤去・upsert）と
  `DocumentDeletedConsumer`（1 口）。**4 口すべてが門を通る**（同期の 3 口は `WikiJsAsync` の 1 点を通る）。閲覧（`GetRenderedContentAsync`）・検索は読み取りなので門の外。
- 受け口を組み立てる箇所（引数が 1 つ増える）: `git grep -n "new DocumentSyncConsumer(\|new DocumentDeletedConsumer(" -- src/knowledge/backend/Services/WikiService` → 試験 5 箇所。
  DI で受け口を解く器（`AddSingleton(WikiSyncTimeouts.Default)` の並び）: 5 箇所。いずれも門を足した。統合試験（`Knowledge.IntegrationTests`）は `Program` の登録を使うので変更不要。
- 「Wiki.js 1 回だけ」で最悪の所要時間を書いた記述: `git grep -n "35 秒と 15 秒\|本文 ＋ Wiki.js 1 回" -- src docs` → `WikiSyncTimeouts.cs` の冒頭（直した）。
- 「並列度は上げない（MaximumParallelMessages を足さない）」と書いた記述: `git grep -n "MaximumParallelMessages\|並列度は上げない" -- src docs` → `Program.cs`（直した）。
  `.ai-context/adr/IADR-0557` は凍結記録なので本文は直さず、残余リスク (d) に日付つきの追記を足す。
- アーカイブを「常に update する」と書いた記述: `docs/tests/FR-13_wiki-browsing-abac.md` の T-22（送る形の記述で、送るときの形としては今も正しい。T-35 を足す）。

## 受け入れ基準と試験

| # | 基準 | 試験 |
| --- | --- | --- |
| B1 | 上限は既定 1、構成で 1〜4。範囲の外・整数でない値は起動で止める | `WikiJsWriteGateTests.上限は既定1で構成から1から4を読む`・`範囲の外か整数でない上限は起動を止める` |
| B2 | 同期（upsert・アーカイブ）と撤去を同時に流しても、Wiki.js への呼び出しの重なりは上限を超えない（上限 2 の対照で、器が重なりを測れることも示す） | `同期と撤去の受け口はWikiJsへの呼び出しを上限より重ねない`（Theory 1・2） |
| B3 | 門の待ちが期限を超えたら `wiki-js-gate` の時間切れ。前の書き込みが終われば枠は返る。呼び出しが失敗・時間切れしても枠は返る | `門の待ちが期限を超えたら…`・`呼び出しが失敗しても枠は返る` |
| B4 | 本番の配線: 門は既定 1、両リスナーは Inline・リスナーの数 1。構成した値は門と両リスナーへ同じ値で入る | `WikiJsWriteGateWiringTests` |
| B5 | 最悪の所要時間の起動時検査に門の待ちが入る（同期 20＋21＋21＝62 秒で止まる。18＋20＋20＝58 秒は通る） | `WikiSyncTimeoutTests.書き込みの門の待ちも最悪の所要時間に数える`・`門の待ちを数えても収まる構成は通る` |
| B6 | アーカイブは既に非公開なら update を送らない（往復 1 回）。公開中・読めないときは従来どおり unpublish の update を送る | `WikiJsGraphQlClientTests.ArchivePageAsync_Skips_WhenAlreadyUnpublished`・`ArchivePageAsync_Updates_UnlessKnownUnpublished` |

- 変異（手元で実施し、それぞれ赤になることを確かめた）: 門を素通りにする → B2・B3 が赤／枠を finally で返さない → B3 と実配送の再試行の試験が赤／
  アーカイブの省略を外す → B6 の省略が赤、常に省略する → B6 の対照と既存 T-22 が赤／リスナーの数の明示を外す → B4 の構成の試験が赤／
  検査から門の待ちを外す → B5 が赤。

## PoC での確認（配備後。読み取りだけ）

- `kubectl -n microservices-platform logs deploy/wiki-service --since=10m | grep -c "archive update skipped"`（アーカイブ済みの再発行が流れている間は増える）。
- `kubectl -n microservices-platform logs deploy/wiki-js --since=30m | grep -c "duplicate key"` が 0 のまま（同期と撤去が重なる一括操作の間も）。
- 時間切れが門で起きていないこと: `kubectl -n microservices-platform logs deploy/wiki-service --since=30m | grep -c "call to wiki-js-gate did not complete"` が 0。

---
title: IADR-0541 Wiki 同期の slug を文書 ID 由来の接尾辞で一意にし（既存の slug は保つ）、接尾辞でも解けない slug の一意違反と本文の NoSuchKey は再試行・DLQ に回さず Warning と計数 wiki.sync.skipped を残して ack する
type: impl-adr
status: Accepted
related_ids: [FR-13, UC-07, NFR-06, ADR-0011, ADR-0027, ADR-0106, IADR-0020, IADR-0021, IADR-0540]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-13 Wiki 閲覧 / NFR-06 障害時の縮退運転）
related_specs:
  - ../specs/20261015_1934_wiki-slug-unique.md
---

# IADR-0541: Wiki 同期の slug の一意化と恒久的な失敗の ack（#1934）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-15
- 決定者: claude（オーナーの依頼 #1934）

## 起点・関連

- 起点 issue: #1934（PoC・2026-10-15 20:40〜。`DocumentUpdated` の再発行で wiki-service が `IX_Pages_Slug` の 23505 を 30 分で 360 件、DLQ が 48 → 86 で再索引の駆動が止まった）。
- 関連する計画書 ID: FR-13・UC-07・NFR-06。
- 関連する計画 ADR: ADR-0011（Wiki.js。ABAC は本システム）・ADR-0027（Wolverine の再試行・DLQ）・ADR-0106（SeaweedFS）。
- 関連する実装 ADR: [IADR-0021](./IADR-0021_wiki-js-sync-graphql-push.md)（正準パス `doc/<id>`。slug は人間可読の索引に留める）・
  [IADR-0540](./IADR-0540_ingestion-body-missing-is-permanent.md)（取り込みの本文 NoSuchKey。その残余「wiki-service の読み手」を本件で閉じる）。
- 関連する実装仕様書: `.ai-context/specs/20261015_1934_wiki-slug-unique.md`（根本原因・母集合・変異試験）。
- 採番: `origin/develop` `02b4d146` の最大は IADR-0540。開いている PR に IADR を足すものは無い（push の直前に確かめた）。本件は **0541**。

## コンテキストと課題

- メタデータ行（`Pages`）は `DocumentId` で引いて更新するので、**同じ文書の再同期は既に upsert で、自分自身とは衝突しない**。
- slug は題名だけから作る（`ToSlug`）。**同名の題名の別文書**（同じ見出しのニュース・重複して作った規程）・題名の変更で他の文書と同じ題名になった文書・
  記号だけの題名（空の slug）は同じ slug へ写り、一意索引 `IX_Pages_Slug` で保存に失敗する。題名が変わらない限り再試行で直らず、4 試行の後 DLQ へ入る。
- slug は `GET /wiki/pages/{slug}` の鍵であり（1 行を引く）、画面の URL に出る。**既存の slug を変えると URL が壊れる。**
- 決めること: (1) 一意な slug の作り方、(2) 既存の slug の扱い、(3) それでも残る衝突（並行の競合・人手のデータ）、(4) 本文の NoSuchKey、(5) 観測、(6) スキーマ。

## 検討した選択肢

1. **一意索引を外す** —— `GetPageBySlug` が 1 行を引けなくなる（同じ slug の別文書のどちらを返すか決まらない）。採らない。
2. **slug を常に文書 ID 付きにする** —— 一意は確実だが、既存の全ページの slug（URL）が変わる。採らない。
3. **題名由来を先に、衝突したときだけ文書 ID 由来の接尾辞（採用）** —— 既存の行は一意制約の下で書かれているので、そのまま保てる。
4. **23505 をすべて恒久的な失敗として ack** —— 同じ文書の並行作成（`IX_Pages_DocumentId` の 23505）は再試行で更新の経路に入って直るのに捨ててしまう。採らない。

## 決定

1. **slug の候補列** `WikiPage.SlugCandidates(title, documentId)` = `[base, base-<文書IDの先頭8桁>, base-<文書ID 32桁>]`。
   `base` は従来の `ToSlug(title)`。空なら素の候補を飛ばし `doc-<8桁>` から始める。接尾辞付きは列の長さ 500 に収まるよう `base` を切る。
   末尾は文書 ID を丸ごと含むので、文書 ID の一意性から他の文書の末尾とは重ならない。
2. **選び方（受け口）**: 既存ページの slug が今の題名の候補に入っていれば**保つ**（接尾辞付きで作られたページも、元の持ち主が消えた後に素の slug へ動かない）。
   入っていなければ（新規・題名の変更）、**他の文書が使っていない最初の候補**を採る。題名の変更で slug が変わるのは従来どおりである。
3. **保存時の 23505**: 判定は `PostgresException` の SQLSTATE `23505` と制約名 `IX_Pages_Slug`（`WikiDbContext.SlugIndexName`。EF の既定の名前を明示しただけで索引は変えない）。
   空きを確かめた後に同名の別文書が並行して同じ slug を取った競合なので、**受け口の中で 1 度だけ末尾の候補（32 桁）へ付け替えて保存し直す**。
   それでも `IX_Pages_Slug` に当たる（または既に末尾だった）なら恒久的な失敗として決定 5 で記録して ack する。
   **`IX_Pages_DocumentId` の 23505・接続断などは捕まえない**（再試行 → 次の試行で更新の経路に入って直る）。
4. **本文の NoSuchKey**: `StorageMarkdownReader` は `NoSuchKeyException` か `AmazonS3Exception.ErrorCode == "NoSuchKey"` だけを `WikiBodyMissingException`（`Domain/Ports`）へ写す。
   **判定は IADR-0540 決定 1 と同じ**（状態コード 404 だけ・`NoSuchBucket`・5xx・時間切れ・接続の失敗は写さない。メッセージ文字列は見ない）。
   受け口はそれを捕まえ、メタデータも Wiki.js も書かずに ack する。既存のページは前の版のまま残る（本文の無い文書の扱いは #1931 項目 3 の裁定に従う。本件は何も消さない）。
5. **観測**: Warning を 1 行（slug: 文書 ID・slug・索引名／本文: 文書 ID・本文の所在・更新日時）と、計数 `wiki.sync.skipped`（Meter はサービス名
   `microservices-platform.wiki-service`。`Program.cs` で `AddMeter`。タグは `reason` = `slug_conflict` | `body_missing` だけ —— 文書 ID や slug は基数が無界なのでタグにしない）。
6. **スキーマは変えない（マイグレーション不要）**。既存の行は一意制約の下で書かれているので衝突を含まず、その slug は決定 2 で保たれる。
   失敗して行が無い文書は、配備後の再発行で接尾辞付きの slug を得る。既に DLQ にある分は古い写しなので再投入しない（再発行で作り直す）。

## 理由

- 一意索引は `GetPageBySlug` の前提であり、外すと別の不具合（どのページを返すか）を作る。衝突時だけ接尾辞を付ければ、既存の URL も新規の大多数の URL も変わらない。
- 接尾辞を文書 ID 由来にするのは、連番（`-2`）と違って並行に同期しても決定的で、同じ文書の再同期で値が揺れないため。
- 恒久的な失敗を DLQ に入れると、人が見て再投入を判断する DLQ が本物の一時障害で埋もれ、再索引の駆動（DLQ の増分で止まる）も止まる。
  捕まえる範囲を制約名と NoSuchKey に限るのは、再試行で直るもの（同じ文書の並行作成・接続断・5xx）を捨てないため。

## 結果

- PoC: 同名の題名の別文書は接尾辞付きの slug で保存され、`IX_Pages_Slug` の 23505 は出なくなる（並行の競合は受け口の中で解ける）。DLQ は wiki-service の分で増えない。
  件数は `wiki_sync_skipped_total{reason=...}`（Prometheus）とログの `Wiki sync ... skipped without retry or dead-lettering` で読める。
- 手元の PostgreSQL 16 で、マイグレーションの適用・同名の別文書 2 件と同じ文書の再同期・2 つの文脈から並行に同じ題名を保存する競合（実際の 23505 `IX_Pages_Slug` → 32 桁へ付け替え）を確かめた（仕様書）。
- 残余: graph-service の読み手（`StorageContentReader`）は同じ NoSuchKey で再試行・DLQ になり得る（射程外）。
  slug の一意違反が付け替え後も残るのは、人手で末尾の候補と同じ slug を書いた行があるときだけである（計数が 0 でなければ調べる）。
- slug_conflict で ack した文書は、Wiki.js にだけページ（正準パス `doc/<id>`）が残り、メタデータ行は無い（Wiki.js への反映が保存より先のため）。
  ゲートウェイは deny-by-default で返さないので安全側である。**次の再同期では直らない** —— 行が無いので候補を選び直すが、末尾の候補を塞いでいる行が残る限り同じ衝突になる。
  計数 `reason=slug_conflict` が 0 でなければ、その slug を持つ行（人手で書かれたもの）を調べて直し、再発行する。
- 成功時のログには slug を出さない（題名から作るので、機密文書の題名が毎回ログに入る）。slug を出すのは異常時（競合の付け替え・恒久的な衝突）の行だけである。

## 残余リスク

- (a) 同じ文書の同時 insert を正しく扱える（`IX_Pages_DocumentId` の 23505 として再試行に回る）のは、`IX_Pages_DocumentId` が `IX_Pages_Slug` より先に作られている（OID 順で先に検査される）という暗黙の前提による。
  索引を作り直して順序が逆になり、かつ両方の試行が 32 桁の候補を選んだときだけ、`slug_conflict` として更新が 1 件捨てられ得る（確率は極めて低い）。
- (b) 本文が NoSuchKey のときは、既存ページの属性の変更（機密区分など）も反映されない。従来も DLQ に入って反映されていなかったので退行ではないが、
  跡は Warning と計数だけになる。扱いは #1931 項目 3 の裁定に委ねる。

## 試験

- 試験: `WikiSyncPermanentFailureTests`（FR-13 テスト仕様 T-25〜T-29）。変異 7 種で赤になることを確かめた（PR 本文）。

## 関連

- #1934・#1931（項目 3: 本文の無い文書の扱い）・#1933（IADR-0540）

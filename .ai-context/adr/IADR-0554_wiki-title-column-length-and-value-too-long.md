---
title: IADR-0554 Wiki 同期は表題を Wiki.js の列の長さ（varchar(255)・符号位置）へ書記素クラスタの境界で切り詰めて印（…）を付け、台帳の表題・slug も列の長さに収め、それでも Wiki.js が値の長さで拒んだものは再試行・DLQ に回さず Warning と計数 wiki.sync.skipped{reason=value_too_long} を残して ack する
type: impl-adr
status: Accepted
related_ids: [FR-13, UC-07, NFR-06, ADR-0011, ADR-0027, IADR-0021, IADR-0541]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-13 Wiki 閲覧 / NFR-06 障害時の縮退運転）
related_specs:
  - ../specs/20261016_1966_wiki-title-column-length.md
---

# IADR-0554: Wiki 同期の表題の長さと、値の長さによる恒久的な失敗の ack（#1966）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-16
- 決定者: claude（オーナーの依頼 #1966）

## 起点・関連

- 起点 issue: #1966（PoC・2026-10-16。`wolverine-dead-letter-queue` の 14 件がすべて `wiki-service.DocumentUpdated` の
  `Wiki.js pages.create failed for 'doc/…' (code=1): insert into "pages" (...) - value too long for type character varying(255)`。直近 90 分で 10 回）。
- 関連する計画書 ID: FR-13・UC-07・NFR-06。
- 関連する計画 ADR: ADR-0011（Wiki.js。表示制御に留める）・ADR-0027（Wolverine の再試行・DLQ）。
- 関連する実装 ADR: [IADR-0021](./IADR-0021_wiki-js-sync-graphql-push.md)（GraphQL push・正準パス `doc/<id>`）・
  [IADR-0541](./IADR-0541_wiki-slug-unique-and-permanent-sync-failures.md)（恒久的な失敗を記録して ack する型・計数 `wiki.sync.skipped`。本件はそれに 1 理由を足す）。
- 関連する実装仕様書: `.ai-context/specs/20261016_1966_wiki-title-column-length.md`（根本原因・母集合・変異試験）。
- 採番: push の直前に `origin/develop` と開いている PR の IADR の最大を確かめ、その +1 を採った（PR 本文に記録）。

## コンテキストと課題

- Wiki.js 2.x の `pages.title` / `pages.description` / `pages.path`（と `pageTree`・`pageHistory` の同名の列）は Knex の `string()` ＝ **varchar(255)**。
  受け口は文書の表題をそのまま送っていたので、255 文字を超える表題は Wiki.js の DB で 22001 になる。Wiki.js はそれを `errorCode: 1` と DB の message に平たくして返し、
  クライアントは一時障害（`WikiJsSyncException`）として投げるので、再試行を使い切って DLQ へ入る。
- 長い表題の出所は AST の保存文書の `<<<UNTRUSTED_DATA ...>>>` の囲み（約 37 文字。AST 側の別 issue）と英文ニュースの長い見出しで、入力として正当である。
- 本サービスの台帳（`wiki_svc.Pages`）も `Title` varchar(500)・`Slug` varchar(500) で、`SlugCandidates` の先頭（題名だけ由来）の候補は切っていなかった。
  **500 を超える表題は台帳の保存の 22001 でも同じく DLQ に入る**（同じ型の欠陥）。
- 決めること: (1) 数える単位、(2) 切る位置、(3) 切ったことの印と元の表題の残し方、(4) 対象の列、(5) 観測、(6) それでも残る恒久的な失敗、(7) DLQ に溜まった分。

## 検討した選択肢

1. **Wiki.js の列を広げる（`ALTER TABLE pages ALTER title TYPE text`）** —— 外部製品のスキーマを本システムが改変し、Wiki.js の更新で戻る・壊れる。採らない。
2. **長い表題の文書を同期しない（恒久的な失敗として ack するだけ）** —— 正当な文書が Wiki に載らない。採らない（切り詰めでも残る失敗の受け皿としてだけ採る）。
3. **元の表題を本文の先頭へ書き足す** —— Wiki.js の本文は文書サービスの正規化 Markdown の写しであり（IADR-0021）、書き足すと写しでなくなる
   （全文検索の当たり・Wiki 上の編集差分にも混ざる）。元の表題は台帳（500 まで）と文書サービスに残るので、Wiki.js へ重ねて持たない。採らない。
4. **書記素クラスタの境界で切り、末尾に印（…）を付ける（採用）**。

## 決定

1. **数える単位は符号位置（Rune）**。PostgreSQL の varchar(n) は文字で数え、UTF-8 の DB では 1 文字＝1 符号位置である。
   バイト数（`varchar(255)` を 255 バイトと読む）でも UTF-16 の単位数（`string.Length`。BMP 外の文字を 2 と数え、必要以上に切る）でもない。
2. **切る位置は書記素クラスタ（`StringInfo` のテキスト要素）の境界に限る**。サロゲートペア・結合文字・異体字セレクタ・ZWJ で連結した絵文字・国旗の対を途中で切らない
   （孤立したサロゲートは UTF-8 へ符号化できず、結合文字だけを落とすと別の文字になる）。実装は `WikiText.FitColumn`（`Domain`）。
3. **印と対象の列**: 切ったら末尾に `…`（U+2026。1 文字）を付け、**印を含めて上限に収める**。上限ちょうどは切らない。
   - Wiki.js の表題: 255（`WikiJsColumns.TitleMaxLength`）。受け口が送る直前に切る。台帳の表題は元のまま（500 まで）残す。
   - 台帳の表題: 500（`WikiPage.TitleMaxLength`。`WikiDbContext` の `HasMaxLength` も同じ定数を引く。値は変えないのでマイグレーション不要）。
   - slug の題名だけの候補: 500（印は付けない —— slug は値そのものが URL の鍵である）。接尾辞付きの候補は従来どおり。
   - Wiki.js の説明（`description`）は**常に空文字の定数**、パスは `doc/<GUID>` の **40 文字固定**で、上限に届かない。切り詰めの処理は置かず、試験で前提を固定する
     （説明に値を入れるようになったら同じ `WikiText.FitColumn` を通す）。
4. **観測（切り詰め）**: 計数 `wiki.sync.truncated`（Prometheus では `wiki_sync_truncated_total`）、タグ `field` = `title` だけ（基数が有限）。同じ Meter
   `microservices-platform.wiki-service`（`Program.cs` で収集対象に入っている）。同期は成功しているので `wiki.sync.skipped` には数えない。
5. **恒久的な失敗（value too long）**: クライアント（`WikiJsGraphQlClient.EnsureSucceeded`）は `responseResult.message` が PostgreSQL 22001 の定型句
   `value too long for type ` を含むときだけ `WikiJsInputRejectedException`（`Domain/Ports`）を投げる。Wiki.js は SQLSTATE を運ばないので、
   **判定は message の定型句しかない**（IADR-0541 は SQLSTATE と制約名で判定したが、ここでは得られない）。受け口はそれを捕まえ、IADR-0541 と同じく
   **Warning を 1 行（文書 ID・操作・パス・切った後の表題の文字数・タグの数・Wiki.js の message）と計数 `wiki.sync.skipped{reason=value_too_long}` を残して ack** する。
   メタデータも書かない（既存のページは前の版のまま残る）。表題は先に切るので、ここへ来るのは表題以外の列（タグ `tags.tag` も varchar(255)）だけである。
   **transport の失敗・GraphQL errors・他の message（接続断・プールの枯渇・6004 等）は従来どおり `WikiJsSyncException`（再試行 → DLQ）**。
6. **DLQ の 14 件**: DLQ のメッセージは古い状態の写しなので再投入しない（運用仕様書の既定の方針）。配備後に DLQ から文書 ID を抜き出し、
   `scripts/republish-document-updated.js --ids` で今の台帳から再発行する（手順は運用仕様書の「DLQ に溜まった Wiki 同期の失敗の再発行」）。

## 理由

- 列の長さは Wiki.js の都合であり、入力（表題）は正当である。Wiki 上で表題の末尾が `…` になる代わりに、文書は Wiki に載る。
- 印を付けるのは、Wiki.js の画面で読み手が「表題はここで終わりではない」と分かるため。本文と台帳は元のままなので、情報は失われない。
- 恒久的な失敗の捕まえ方を定型句 1 つに限るのは、再試行で直るもの（Wiki.js の DB の接続断・プールの枯渇）を捨てないため（IADR-0541 と同じ方針）。

## 結果

- PoC: 長い表題の文書は Wiki.js に `…` 付きの表題で載り、`wiki_sync_truncated_total{field="title"}` が増える。wiki-service の分で DLQ は増えない。
  `wiki_sync_skipped_total{reason="value_too_long"}` は 0 が正常（増えたら Warning の `Wiki.js rejected pages.` の行で文書を引き、どの列かを確かめる）。
- 切り詰めた表題は Wiki.js の全文検索の表題の当たりから末尾が外れる（本文の当たりは変わらない）。本システムの Wiki 検索は台帳で絞り直すので、一覧の表題は台帳（500 まで）である。

## 残余リスク

- (a) Wiki.js の版の更新で DB の message の定型句が変わると、value too long は一時障害へ戻る（DLQ に入るので気付ける。安全側）。
- (b) Wiki.js の DB が UTF-8 以外（`SQL_ASCII` 等）だと、varchar(n) の数え方がバイトになり 255 符号位置でも溢れ得る。経路B・compose はいずれも UTF-8（PostgreSQL の既定）である。
  溢れれば (5) で記録して ack される（DLQ には入らない）。
- (c) タグが 255 文字を超える文書は Wiki に載らない（記録して ack）。現状の入力（AST・取り込み）のタグは短い。起きたら計数で分かる。

## 試験

- `WikiSyncPermanentFailureTests`（FR-13 テスト仕様 T-30〜T-34 の受け口と実配送）・`WikiTextTests`（T-32）・`WikiJsGraphQlClientTests`（T-33・T-34 の分類）。
  変異で赤になることを確かめた（作業仕様書・PR 本文）。

## 関連

- #1966・#1934（IADR-0541）

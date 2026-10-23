---
title: IADR-0597 Document の版を EF の並行トークンにし、期待版の照合と保存の間に追い越された更新・メタデータ更新と、トークン化で新たに負け得るようになった削除・露出トグルを 409 version_conflict へ写す（版テーブルの一意索引違反も同じ競合として読む。修正前から 500 だった他の書き込み口は黙って上書きせずに失敗する）
type: impl-adr
status: Accepted
related_ids: [FR-06, UC-03, SC-05, FR-19, FR-20, ADR-0027, IADR-0596, IADR-0455]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（少なくとも 1 回・再試行）
related_specs:
  - ../specs/20261023_2103_document-optimistic-concurrency.md
---

# IADR-0597: Document の楽観的同時実行（#2103）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2103。親 #2047 の指摘 M-6）

## 起点・関連

- 起点 issue: #2103（親 #2047）。境界: #2102（[IADR-0596](./IADR-0596_wiki-catalog-receiver-ordering.md)。カタログの並行の窓を本件へ渡した）。
- 関連する計画書 ID: FR-06・UC-03・SC-05（FR-19・FR-20 は影響を受ける書き込み口の側）。計画 ADR: ADR-0027。
- 引き継ぐ実装判断: IADR-0596（カタログの受け手の順序）、IADR-0455（書き換える前に門の判定を取る。保存で負けたら発行しない）。
- 作業仕様書: [20261023_2103_document-optimistic-concurrency](../specs/20261023_2103_document-optimistic-concurrency.md)（母集合・受け入れ基準・変異・限界）。

## コンテキストと課題

- `PUT /documents/{id}` と `PATCH /documents/{id}/metadata` は、読んだ文書の `Version` と要求の `ExpectedVersion` を比べてから `SaveChangesAsync` していた。比較と保存の間に別の更新が入る窓がある。
- `Version` は並行トークンではなかった（DocumentService に `IsConcurrencyToken` / `IsRowVersion` / `UseXminAsConcurrencyToken` は 0 件）。
  同じ版を前提にした 2 つの更新は、どちらも版 v+1 の行を版テーブルへ足し、後から確定する側は一意索引 `IX_DocumentVersions_DocumentId_Version` で 23505 → **500** になった。
  版を進めない保存（露出トグル）と版を進める保存の並行は、古い属性で**黙って上書き**した。
- 計画に楽観ロックの明文は無い。409 `version_conflict` は実装の機能仕様書（FR-06）の契約であり、画面（SC-05）はそれを「注意」で出す。

## 検討した選択肢

1. **`Version` を EF の並行トークンにし、`DbUpdateConcurrencyException` と版テーブルの一意索引違反を 409 へ写す（採用）**。
   列は既存で、版は全ての版を作る変更で 1 つ進む（`Document.Touch`）。スキーマは変わらず、マイグレーションが要らない。
2. **`xmin` を並行トークンにする（`UseXminAsConcurrencyToken` 相当の影の列）**。版を進めない変更も含め、行のあらゆる書き換えを検知できる。
   ただし 409 の `currentVersion`（利用者に見える版）と突き合わせの値が別物になり、ドメインに無い PostgreSQL 固有の列をモデルへ足す。
   版を進めない変更も、選択肢 1 で「読んだ後に版が進んでいれば負ける」ので、欠けるのは「版を進めない変更どうしの並行」だけである（露出トグルどうし。同じ値を書く操作が多い）。採らない。
3. **条件付きの UPDATE（`ExecuteUpdate … WHERE "Version" = @expected`）で保存する**。集約（`Document.Update` の版の行の追記・属性の全置換）を迂回し、版の行を別に書く必要がある。採らない。
4. **悲観ロック（`SELECT … FOR UPDATE` / 直列化可能なトランザクション）**。照合から保存までロックを握るため、発行・タグの解決をまたいで接続を持つ。EF InMemory の単体試験と二重の経路になる。採らない。
5. **版テーブルの一意索引違反だけを 409 に写す**。版を進める保存どうしは塞げるが、版の行を足さない保存の黙った上書きが残る（変異の実測で、トークンを外すと試験 4 だけが赤になる）。単独では採らない（選択肢 1 の一部として拾う）。

## 決定

**決定 1（並行トークン）**: `DocumentDbContext` で `Document.Version` を `IsConcurrencyToken()` にする。文書行の UPDATE / DELETE は `WHERE "Version" = @読んだ版` を伴い、0 行なら `DbUpdateConcurrencyException`。
マイグレーションは足さない（並行トークンはスキーマに出ない）。モデルスナップショットだけ `IsConcurrencyToken()` を持たせ、次のマイグレーションの生成で差分が出ないようにする。

**決定 2（競合の判定）**: 競合は `DbUpdateConcurrencyException`、**または** `IX_DocumentVersions_DocumentId_Version` の 23505 とする（`DocumentVersionConflict.Is`）。
同じ版を前提にした 2 つの保存は同じ番号の版の行を足すので、EF が 1 つのバッチに積む順によっては一意索引違反が先に出る（実 PostgreSQL の実測ではこちらが先。分岐を外すと 409 の試験が 500 で赤になる）。
**他の一意索引違反は拾わない**（制約名で絞る）。

**決定 3（期待版を受ける 2 口の写像）**: `Update` と `UpdateMetadata` は保存を `catch (DbUpdateException) when (DocumentVersionConflict.Is(ex))` で囲み、負けたら発行せずに 409 `version_conflict` を返す。
本文は照合で弾くときと同じ形（`error`・`expectedVersion`・`currentVersion`。RFC7807 にしない）で、**`currentVersion` は読み直す**（追跡中の実体は自分が進めた版を持つ）。
`expectedVersion` は要求に無ければ null（期待版を送らない更新も、読んだ後に追い越されたら黙って上書きせず 409）。読み直したときに文書が無ければ 404。
負けた保存は暗黙のトランザクションごと巻き戻る（版の行も残らない）。

**決定 4（トークン化で新たに負ける 2 口も写す。他は写さない）**: 修正前は並行時に黙って通っていた口のうち、**削除（`DELETE /documents/{id}`）と個人資料の露出トグル（`PUT /private-notes/{id}/exposure`）**は、
本決定のトークン化で初めて負け得るようになる（削除は追い越した更新ごと消していた・露出トグルは古い属性で上書きしていた）。新しい失敗の形を 500 で出さず、`Update` と同じ 409 `version_conflict`（同じ本文の形。`expectedVersion` は null）へ写す。
削除が負けたときは、本文の実体を既に消している（IADR-0296 決定 3 の「オブジェクトが先、行が後」の順）。行は勝った側の内容で残るので、409 を受けた呼び出し側が削除をやり直せば完了する（結合試験で確かめた）。露出トグルが負けたときは台帳（`PrivateNote`）の露出も同じトランザクションで巻き戻る。
公開・アーカイブ・一括アーカイブ・本文投入・境界語の剥がし・タグの追加・Obsidian 同期・競合の解決は 409 へ写さない。これらは版を進めて版の行を足すので、修正前も並行時は一意索引違反で 500 だった（新しい失敗の形ではない）。
並行時は黙って上書きせずに例外になる。カタログの受け手（`DocumentNormalizedConsumer`）はメッセージングの再試行で読み直し、IADR-0596 の順序判定からやり直す（ADR-0027）。
Obsidian 同期 `Push` の写像は、保存より前にオブジェクトストレージへ書いた本文と競合台帳（`SyncConflict`）の扱いの判断が要るので本決定に含めない。
個人資料の物理削除（完全削除の口・90 日経過後の定期処理）も写さない。対象は削除済みの資料だけで、同期の push は削除済みの資料を拒む（`deleted` の 409）。ただし露出トグルは削除済みを拒まないので、完全削除と露出トグルが同じ資料で並ぶと完全削除が負けて 500 になり得る（複数件を 1 回で消す口であり、どの資料の版を返すかの形が決まらない。§結果の残余）。

## 結果

- 良い: 同じ版を前提にした 2 つの更新（`PUT`・`PATCH …/metadata`）は一方だけが通り、他方は 409。500 にも黙った上書きにもならない（実 PostgreSQL の `DocumentConcurrentUpdateTests`）。
  追い越された削除・露出トグルも 409 で、追い越した更新を消さず・古い属性で上書きしない（同試験）。
  版を進めない保存も、読んだ後に版が進んでいれば負ける。カタログの 2 つの再正規化の並行（IADR-0596 の残余）も片方が負けて再試行で読み直す。配備にマイグレーションは要らない。
- 悪い（受容）:
  - **追い越された削除は本文の実体を先に消している**（決定 4）。409 の後、削除をやり直すまで、文書は本文の無い状態で残る。
  - **個人資料の完全削除と、削除済みの資料への露出トグルの並行**は 500 になり得る（決定 4）。修正前は黙って通っていた形で、同じ削除済みの資料への同時操作が要る。
  - **Obsidian 同期 `Push` の追い越し**は 409 ではなく 500（修正前も一意索引で 500）。負けた側の本文がオブジェクトストレージの正準キーに残る（本文を保存より前に書く既存の順）。
  - **版を進めない変更どうしの並行**（露出トグルどうし）は検知しない（選択肢 2 を採らない代償）。
  - 試験は実 PostgreSQL の結合試験だけで、CI の `integration.yml` は develop への push と日次で走る（PR では走らない）。PR では手元の実走の出力を本文に貼る。EF InMemory は並行するトランザクションも一意索引の強制も持たないので単体試験に置けない。
- 境界: ConversionService の再試行（`ConversionJobStore.PrepareRetryAsync`）は同型の check-then-write である（作業仕様書 §母集合で実測）が、別サービス・別の表であり本決定の射程外。

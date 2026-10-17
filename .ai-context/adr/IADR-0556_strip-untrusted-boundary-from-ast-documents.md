---
title: IADR-0556 AST が境界語（<<<UNTRUSTED_DATA）つきで保存した文書は、管理者だけの一回限りの口と駆動スクリプトで表題・本文の先頭と末尾の枠だけを剥がす。本文は新しいキーへ書いて参照を付け替え、状態・属性・タグは動かさない。駆動スクリプトの本体は一括アーカイブと共有する（#1973）
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-02, FR-13, UC-03, SC-05, IADR-0503, IADR-0509, IADR-0549, IADR-0554]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
related_specs:
  - ../specs/20261017_1973_strip-untrusted-boundary-from-ast-documents.md
---

# IADR-0556: AST の文書から境界語の枠を剥がす（#1973）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-17
- 決定者: claude（実行は PoC がオーナーの承認のもとで行う）

## 起点・関連

- 起点 issue: #1973。AST は 2026-10-16 まで、KB へ保存するニュースの表題と本文を `<<<UNTRUSTED_DATA\n…\nUNTRUSTED_DATA>>>` で囲んで保存していた（AST#1359）。
  AST#1360（AST の IADR-0542）で新しい保存は囲まなくなったが、旧い文書は台帳・索引・Wiki に枠つきで残る。AST の port は表題を更新できず、基盤に upsert も無い。
- 関連する計画書 ID: FR-06・UC-03（文書の管理）。FR-02（取り込み）・FR-13（Wiki 同期）は下流の受け手。SC-05 は変えない。
- 関連する実装 ADR: [IADR-0549](./IADR-0549_archive-bodyless-pre-cutover-documents.md)（一回限りの管理の口と駆動スクリプトの作法。本件はそれを再利用する）・
  [IADR-0503](./IADR-0503_republish-document-updated-admin-endpoint-and-driver.md)（駆動スクリプトの I/O）・
  [IADR-0509](./IADR-0509_document-keyset-in-sql-and-created-at-id-index.md)（キーセット）・
  [IADR-0554](./IADR-0554_wiki-title-column-length-and-value-too-long.md)（Wiki の表題の長さ。#1966）。
- 関連する実装仕様書: `.ai-context/specs/20261017_1973_strip-untrusted-boundary-from-ast-documents.md`。

## コンテキスト

- 台帳の表題とオブジェクトの本文がそのまま枠つきである（`POST /documents` の本文は `CreateWithBody` で `documents/{id}/body.md` へそのまま入る）。
- 既存の口で直すと副作用が大きい: `PUT /documents/{id}` は属性・タグを全置換し、`PUT /documents/{id}/body` は状態を `normalized` へ戻す。2 つ呼ぶと版とイベントが 2 回進む。
- 表示時に剥がす方式は、索引の入力（語彙・埋め込み）に境界語が残る（#1973 の残余の指摘）。台帳を直せば下流は既存のイベントで追随する。

## 決定

1. **DocumentService に管理者だけの一回限りの口 `POST /documents/strip-untrusted-boundary` を足す**（`AdminOnly`。メッシュ内部。BFF には出さない）。
   形は IADR-0549 の口と同じにする: `dryRun` 必須・`limit`（1〜200）・`cursor`・`requestedBy`・`reason`（書き換える呼び出しでは必須）・ページごとの応答と次のカーソル・
   文書ごとの監査ログ・読めない（NoSuchKey 以外）ときはそのページを何もせず 502・未構成は 503。
   母集合は **アーカイブ済みでない ∩ `project = ai-stock-trading` ∩ 組織文書**で、project は口の側で固定する（他の project を巻き込む呼び出しを組み立てられない）。
   ページ送りは一覧の口と同じ `DocumentPageQuery.ReadPageAsync`（SQL のキーセットで塊ごとに読み、属性の一致は塊の中で当てる）。
   dry-run も同じページ送りで本文を読み、件数と**対象の文書 ID**（と表題・本文のどちらか）を返す。
2. **剥がすのは先頭と末尾の枠だけで、枠が無くなるまで繰り返す**（`UntrustedBoundaryFrame`）。両端の境界語と、その隣の改行 1 つ（`\n` か `\r\n`）を剥がす。
   中身の途中の境界語・前後の空白は触らない（利用者の文字列を変えない。AST の判断側は読むときに不変点まで剥がす）。出力は枠の形をしないので、**2 回目は何もしない（冪等）**。
   剥がすと空になる表題は剥がさずに数える（表題は必須）。剥がすと空になる本文（枠だけの文書）も剥がさずに数える
   （空の本文を取り込み・Wiki 同期へ流したときの扱いが決まっていないため、表題と同じく「触らない」側へ倒す。#1978 のレビュー）。
3. **本文は新しいキー `documents/{id}/body-boundary-stripped.md` へ書き、台帳の参照と指紋を付け替える。元のオブジェクトは上書きしない。**
   - 書き込みはすべて台帳の保存の前に行う。1 件でも書けなければ台帳を保存しない（書けたオブジェクトはどこからも参照されず、次の実行が同じキーへ書き直す）。
     元のオブジェクトを上書きすると、書いた後に台帳の保存が失敗したとき本文と台帳（指紋）が食い違う。
   - 前の版は元の本文を指したまま残る（版の履歴を壊さない）。文書を削除すると `DocumentObjectPurger` が全版の参照を消すので、元のオブジェクトも消える。
4. **台帳は表題・本文の参照・指紋だけを動かす**（`Document.ReplaceUntrustedBoundaryWrapped`）。状態・属性・タグは動かさない。版は 1 つだけ進め、注記は固定の文字列
   （`untrusted-boundary stripped from title/body — one-off migration of AST documents (#1973)`）にする。
5. **下流へは単票の更新と同じ発行の門（`PublishUpdatedIfIndexableAsync`）で伝える。** 取り込みは全コレクションの点を消してから剥がした本文で作り直す。
   Wiki 同期は表題と本文を書き直す（路は文書 ID 由来なので同じページ）。グラフは指紋の変化で本文を読み直す。新しい仕組み（再索引の専用の経路）は作らない。
6. **駆動スクリプトの本体を共有する。** IADR-0549 の駆動器（`archive-bodyless-documents.js`）の main を `scripts/lib/admin-page-driver.js` へ切り出し、
   口ごとに違う所（経路・引数・状態ファイルの種類と絞り込み・集計・文言・`--live` の確認）を profile で受ける。一括アーカイブの駆動器はその profile の 1 つになり、
   挙動は変えない（#1931 の既存の試験が無修正で通る）。新しい入口 `scripts/strip-untrusted-boundary.js` は `--live` 必須・dry-run（ページごとに対象の ID）・
   カナリア・`--max-pages`・状態ファイルと `--resume`・確かめた位置へ戻す・`--operator` / `--reason`・取り込み／Wiki／グラフのキューと DLQ の監視を、同じ本体で持つ。

## 却下した代替案

- **表示時に剥がす**（一覧・Wiki・MCP の出口で落とす）: 索引の入力に境界語が残る。Wiki.js の表題の長さも直らない。出口が多く、1 つ漏れると戻る。
- **既存の `PUT /documents/{id}` と `PUT /documents/{id}/body` を駆動スクリプトから呼ぶ**: 属性・タグの全置換（読んでから書くまでの間の更新を失う）と、
  状態の `normalized` への巻き戻し（公開済みが落ちる）が起きる。版とイベントが 2 回進み、取り込みが 2 回埋め込む。
- **元のキー（`documents/{id}/body.md`）を上書きする**: 書いた後に台帳の保存が失敗すると、本文は剥がれ指紋は古いまま、イベントも出ない。前の版の本文も消える。
- **中身の境界語も不変点まで剥がす**（AST の `StripMarkers` と同じ）: 中身は利用者の文字列であり、移行で書き換える理由が無い。#1973 が求めるのは枠だけである。
- **project を引数にする汎用の口**: 他の project の文書を巻き込む呼び出しを組み立てられてしまう。一回限りの移行には要らない。
- **駆動スクリプトを写して作る**: 量の制御・停止・再開の作法が 2 か所に割れ、片方だけが直る。

## 結果

- 良い点: 一覧・Wiki・MCP に境界語が出なくなり、表題が約 37 字短くなる（#1966 で DLQ にあった文書の多くは 255 字に収まる）。索引の入力から境界語が消える。
  再実行しても安全。一括アーカイブの駆動器と作法が 1 つにまとまる。
- 悪い点・残余:
  - 剥がした文書は取り込みが埋め込み直す（費用がかかる）。件数は dry-run で先に確かめる。Quote（#1974）を先にアーカイブすると対象が減る。
  - **保存の後の発行が途中で失敗すると、そのページの残りは「剥がし済み・イベント未発行」になり、再実行では拾われない**（IADR-0549 と同じ受容）。
    回収は document-service のログの `Stripped untrusted boundary from document` の行で ID を引き、`republish-document-updated.js --ids` で再発行する。
  - 元の本文のオブジェクトは残る（前の版が参照する）。容量は AST のニュースの本文の分だけ増える。
  - アーカイブ済みの AST の文書は対象外なので、文書管理画面のアーカイブ済みの一覧には枠つきの表題が残る（見つかる面には出ない。IADR-0551）。
  - #1966 で DLQ にあるメッセージそのものは残る（剥がした後の同期は新しいイベントで届く）。DLQ の掃除は運用で行う。
  - RAG の文脈の境界（`RagOrchestrator` / `AnalysisPromptBuilder` が取り出した文書を素で入れる）は本件の対象外。旧い AST の文書は偶然囲まれていたが、剥がした後は囲まれない。

## PoC の手順（MSP の配備は原則として大引けの後。実行はオーナーの承認の後）

0. 配備: document-service だけ（マイグレーションは無い）。
1. dry-run（何も変えない）: `node scripts/strip-untrusted-boundary.js --live --dry-run --operator <名前>`
   期待: ページごとに対象の文書 ID が並び、最後に「確かめた」「枠を剥がす対象（表題・本文）」の件数が出る。対象の件数をオーナーへ示して承認を受ける。
2. カナリア（20 件）: `node scripts/strip-untrusted-boundary.js --live --operator <名前> --reason "#1973（カナリア）" --canary 20 --max-pages 1`
   確かめること: 取り込み・Wiki・グラフのキューが空になり DLQ が増えない（スクリプトが見る）／文書管理画面と Wiki で、対象の ID の表題から境界語が消えている／
   document-service のログに `Stripped untrusted boundary from document` の行が出ている。
3. 全件: `node scripts/strip-untrusted-boundary.js --live --resume --operator <名前> --reason "#1973"`（止まったら同じコマンドで再開）。
4. 確認: もう一度 dry-run を流し「枠を剥がす対象」が 0 件であること。台帳を直接引いて確かめる（読み取りだけ）:
   `SELECT count(*) FROM "Documents" WHERE "Status" <> 'archived' AND "Attributes"->>'project' = 'ai-stock-trading' AND "Title" LIKE '<<<UNTRUSTED_DATA%';` が 0。
5. 戻し方: 版の履歴に前の表題と本文の参照が残る（注記が決定 4 の文字列の版の、1 つ前の版）。戻す口は足さない（戻す理由が無い。必要になったら別の issue で扱う）。

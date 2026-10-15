---
title: IADR-0549 9/26 の切り替えより前に作られ本文のオブジェクトが無い（NoSuchKey）文書は、管理者だけの一回限りの口と駆動スクリプトで既存の状態「アーカイブ」へ移す。本文の有無は HEAD と範囲 0-0 の GET で確かめ、索引からは単票のアーカイブと同じ発行で外す。アーカイブ解除は足さない（#1931 項目 3）
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-02, FR-17, UC-03, SC-05, ADR-0106, IADR-0503, IADR-0509, IADR-0540, IADR-0541, IADR-0545]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-05 文書管理。公開・アーカイブ・アーカイブ解除は計画に無い旨）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
related_specs:
  - ../specs/20261016_1931_archive-bodyless-pre-cutover-documents.md
---

# IADR-0549: 本文の無い切り替え前の文書を既存の状態「アーカイブ」へ移す（#1931 項目 3）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-16
- 決定者: claude（オーナー裁定 B・2026-10-15 の実装）

## 起点・関連

- 起点 issue: #1931 項目 3。9/26 の SeaweedFS への切り替え（旧ストレージの中身は破棄と裁定済み）より前に作られた文書は、台帳に残るが本文のオブジェクトが無い。
  PoC の dry-run（`--created-before 2026-09-27T00:00:00Z`）で 5,803 件。オーナーは A（表題だけで索引）・B（アーカイブ）・C（削除）・D（何もしない）から **B** を選んだ。
- 関連する計画書 ID: FR-06・UC-03・SC-05（公開・アーカイブの操作と `lifecycle` の語彙）。FR-02（取り込み）・FR-17（グラフ）は下流の受け手として引く。
- 関連する実装 ADR: [IADR-0503](./IADR-0503_republish-document-updated-admin-endpoint-and-driver.md)（管理の口と駆動スクリプトの作法）・
  [IADR-0509](./IADR-0509_document-keyset-in-sql-and-created-at-id-index.md)（`(CreatedAt, Id)` のキーセット）・
  [IADR-0540](./IADR-0540_ingestion-body-missing-is-permanent.md)（NoSuchKey の判定・取り込みの扱い）・
  [IADR-0541](./IADR-0541_wiki-slug-unique-and-permanent-sync-failures.md)（Wiki 同期の NoSuchKey）・
  [IADR-0545](./IADR-0545_document-list-sort-and-filter-after-abac.md)（SC-05 の状態の絞り込み）。
- 関連する実装仕様書: `.ai-context/specs/20261016_1931_archive-bodyless-pre-cutover-documents.md`（調査・母集合・変異試験）。

## コンテキスト（着手前の調査）

1. **アーカイブの状態は既に在る。** `DocumentStatus.Archived`・`Document.Archive()`・単票の口 `POST /documents/{id}/archive`（管理者だけ）。
   計画 SC-05 は「アーカイブ（`lifecycle` を `archived`）」を管理者限定の操作として定め、SC-05 の状態の絞り込みにも「アーカイブ済み」が在る（IADR-0545）。
2. **索引の扱い**: 単票のアーカイブは `DocumentUpdated`（status=archived）を出す。取り込みは状態を見ずに、全コレクション（モデル別＋語彙索引）から
   当該文書の点を消してから本文を読む（IADR-0540 の冒頭の削除）。本文が NoSuchKey なら何も書かずに ack する。Wiki 同期は本文を読む前の分岐で
   ページを非公開化する。グラフは状態を見ず、本文指紋が変わったとき（またはノードが無いとき）だけ本文を読む —— ノードの無い文書では NoSuchKey で
   再試行の後 DLQ に入り得た（IADR-0540・0541 の残余）。
   新しいコレクション `knowledge_chunks_voyage_3_5` には 5,803 件の点は元から無い（再索引を `--created-after 2026-09-27` で絞った）。
3. **アーカイブ解除は製品の操作として無く、計画にも無い。** ドメインは `archived → published` を禁じ（「再公開する場合は再取り込みが必要」）、
   計画 SC-05 は「`archived → active`（アーカイブ解除）は計画に無い。要否・可否は未定。実装が持つなら環流すること」と書く。
   既存の戻り道は再正規化（同じ文書 ID の `DocumentNormalized` が届けば `normalized` へ戻る）だけである。

## 決定

1. **状態を新設しない。既存の `archived` へ移す。** SC-05 の UI・BFF・proto は変えない（状態の絞り込みと件数の要約は既に `archived` を扱う）。
2. **管理者だけの一回限りの口 `POST /documents/archive-bodyless` と駆動スクリプト `scripts/archive-bodyless-documents.js` にする**（IADR-0503 と同じ作法）。
   - 口: `dryRun` と `createdBefore` は**必須**（既定なし。切り替え後を巻き込む呼び出しを組み立てられないようにする）。アーカイブする呼び出しは `reason` も必須。
     母集合は `CreatedAt < createdBefore` かつ未アーカイブかつ本文の参照が `storage://`。並びは `(CreatedAt, Id)` のキーセット（IADR-0509 の述語を再利用）。
     個人資料は単票の口と同じく対象外（数えるだけ）。ページは既定 100・上限 200（1 件ごとに存在確認を撃つので再発行の 500 より小さくする）。
   - 駆動スクリプト: `--live` 必須・`--dry-run`（全ページを確かめて集計。状態ファイルは作らない）・カナリア（既定 20）・`--max-pages`・状態ファイルと `--resume`・
     確かめた位置へ戻す・`--operator` / `--reason`・連続失敗で止まる。見張るのは取り込み・Wiki・グラフの `DocumentUpdated` のキューと共有の DLQ（許容は既定 0）。
     I/O は再発行の駆動器の関数を再利用する。
   - 🔴 **冪等**: アーカイブ済みは母集合から外れる。2 回流しても 2 回目は何もしない（版を積まない・イベントを出さない）。
   - 🔴 **ページの途中まで保存しない**: 確かめを全件終えてから 1 回だけ保存する。確かめの失敗（NoSuchKey 以外）は失敗として返り、同じ要求の再実行で回収できる。
     ストレージが未構成なら 503（`NullObjectStorageClient` は在るとも無いとも答えない）。
3. **本文の有無は HEAD と、必要なときだけ範囲 0-0 の GET で確かめる。本文を読まない。** 共有の port に `IObjectStorageClient.ExistsAsync` を足した
   （既定の実装は `NotSupportedException`。試験の偽物 16 件を直さずに済ませ、本番の 2 実装は明示的に実装する）。
   - `S3ObjectStorageClient`: HEAD（`GetObjectMetadata`）が 200 なら在る（本文は 1 バイトも運ばない）。**HEAD の 404 だけでは「無い」と言わない** ——
     HEAD の応答は本文を持たず、SDK のエラーコードは状態コード由来（`NotFound`）になり、キーの欠落とバケットの欠落を区別できない。
     そこで 404 のときだけ範囲 0-0 の GET を撃ち、エラーコードを `ObjectStorageErrors.IsNoSuchKey`（IADR-0540 決定 2 と同じ判定）で見る。
     オブジェクトが無ければ GET は本文を返さない（エラーの XML だけ）。HEAD と GET の間に作られていれば在ると答える（最大 1 バイト）。
   - 実ストア（SeaweedFS）でキーの欠落は無い・バケットの欠落は例外になることを統合試験で確かめた（`Exists_tells_missing_key_from_missing_bucket`）。
4. **索引からは単票のアーカイブと同じ発行で外す。取り込みに専用の分岐を足さない。** アーカイブの `DocumentUpdated` を受けた取り込みは、
   従来どおりの位置で全コレクション（旧いモデル別コレクション・語彙索引を含む）から当該文書の点を消し、本文の取得が NoSuchKey になって何も書かない。
   これが「索引から外す」の実体である。題名だけの点（案 A）は書かない。削除を本文の取得の後ろへ動かす変更（点を残す）もしない（ABAC が開いた側に倒れる。IADR-0540）。
   - **グラフの本文の読み手は NoSuchKey を null（本文なし）として返す**ようにした（`StorageContentReader`）。契約どおり辺は触らず、語の出現数は表題だけにする。
     アーカイブの発行がノードの無い文書に届いても、再試行・DLQ に回らない（IADR-0540・0541 の残余のうちグラフの分を本件で塞いだ）。
   - Wiki 同期は `archived` の分岐が本文を読む前にあるので、そのままでページを非公開化する。
5. **版の注記を固定の文字列にし、文書ごとに監査の行を残す。** 注記は `ArchiveBodylessDocumentsEndpoint.ChangeNote`
   （`archived: body object missing (NoSuchKey) — bulk archive of pre-cutover documents (#1931)`）。どの版がこの操作で作られたかを台帳から引ける。
   ログは文書ごとに 1 行（ID・作成日時・本文の所在・認証済みの主体・`requestedBy`・`reason`）、ページごとに 1 行（dry-run も）。
6. **アーカイブ解除の口は足さない。** 計画に無い遷移（`archived → active`）を実装で作らない。裁定の「いつでも戻せる」は、製品の操作としては
   **再正規化（データソースの再同期で同じ文書 ID が届く）だけ**である。台帳の版履歴に直前の状態が残るので、非常時は下の SQL で台帳の状態を戻せるが、
   **計画の裁定（解除の要否・可否）を得てから**行う。要否は計画へ環流する。

### 非常時の戻し方（計画の裁定の後に限る）

対象の確認（読み取りだけ）:

```sql
WITH last AS (
  SELECT DISTINCT ON (v."DocumentId") v."DocumentId", v."Version", v."ChangeNote"
  FROM "DocumentVersions" v ORDER BY v."DocumentId", v."Version" DESC),
prev AS (
  SELECT DISTINCT ON (v."DocumentId") v."DocumentId", v."Status" AS prev_status
  FROM "DocumentVersions" v JOIN last l ON l."DocumentId" = v."DocumentId" AND v."Version" < l."Version"
  ORDER BY v."DocumentId", v."Version" DESC)
SELECT d."Id", d."Status", p.prev_status
FROM "Documents" d
JOIN last l ON l."DocumentId" = d."Id"
JOIN prev p ON p."DocumentId" = d."Id"
WHERE d."Status" = 'archived'
  AND l."ChangeNote" = 'archived: body object missing (NoSuchKey) — bulk archive of pre-cutover documents (#1931)';
```

台帳の状態を直前へ戻すには、上の結果で `UPDATE "Documents" SET "Status" = p.prev_status` を 1 トランザクションで行い、件数を突き合わせる
（版の行は足さないので、最後の版の記録は「archived」のまま残る —— 製品の操作ではないことの跡として受容する）。
下流は戻らない: Wiki.js のページは非公開のまま、索引は本文が無いので点が無いまま（どちらも本文が戻らない限り意味のある状態は無い）。

## 却下した代替案

1. **状態を新設する（「本文なし」等）** —— 計画に無い状態になり、SC-05・BFF・ABAC の `lifecycle` 語彙へ波及する。裁定は既存のアーカイブを指している。
2. **HEAD の 404 で「無い」とする** —— バケット名の誤り・バケットの欠落も 404 であり、構成の不調 1 つで全件をアーカイブしてしまう（依頼の「404 だけで判断しない」）。
3. **本文を GET して例外で判定する（取り込みと同じ読み方）** —— 在る文書の本文を全部読み出す（依頼の「本文を読み出さない」に反する）。範囲 0-0 の GET を 404 のときだけ撃てば足りる。
4. **取り込みに「archived なら本文を読まずに撤収」の分岐を足す** —— 本文の在るアーカイブ文書の索引の扱いを全体で変える（今は再索引される）。計画はアーカイブの効果を
   Wiki の非公開化としか定めておらず、射程を越える。本件の対象は本文が無いので、既存の経路で点は消え、何も書かれない。
5. **再発行の口に「本文の存在確認」とアーカイブを足す** —— 再発行は台帳を書き換えない口（版も更新時刻も動かさない）であり、状態を変える操作を混ぜると監査の意味が割れる。
6. **アーカイブ解除の口を足す** —— 計画 SC-05 が「計画に無い。持つなら環流」と明記している。

## 結果

- 良い点: 台帳と表題は残り、SC-05 で「アーカイブ済み」として見分けられる。検索・RAG の見え方は変わらない（旧い点は消える）。再実行しても安全。
  グラフの NoSuchKey の残余が塞がる。
- 悪い点・残余:
  - アーカイブの発行のたびに取り込みは Warning と計数 `ingestion_body_missing_total` を 1 つ残す（約 5,803 増える。期待どおり。PoC の手順に書く）。
  - 再発行（`republish-document-updated.js`）を `--created-after` なしで流すと、アーカイブ済みの 5,803 件も再発行される（status=archived なので害は無いが、Warning が出る）。
    再発行の口から archived を外す絞り込みは本件では足さない。
  - 本文の在るアーカイブ文書は、再発行されると取り込みが索引へ載せ直す（既存の挙動。本件の対象外）。
  - アーカイブ解除は計画の裁定待ち（決定 6）。
  - **保存の後の発行が途中で失敗すると、そのページの残りの文書は「アーカイブ済み・イベント未発行」になる**（発行は耐久アウトボックスを持たない。
    単票のアーカイブの口と同じ形であり、受容する）。再実行してもアーカイブ済みは母集合から外れるので拾われない。回収は、決定 6 の確認の SQL で
    この操作がアーカイブした文書の ID を引き、`republish-document-updated.js --ids <ID,...>` で再発行する（status=archived のイベントが出て、
    取り込みの点の削除と Wiki の非公開化が届く。冪等）。口の 500 は駆動スクリプトの連続失敗として止まるので、気付ける。
  - 口がページの確かめを終えてから保存するまでの間に、同じ文書が再正規化されて本文を得た場合はアーカイブしてしまう（窓は 1 ページ分の確かめの時間。切り替え前の AST のニュースで起きる見込みは低い）。

## PoC の手順（MSP の配備は原則として大引けの後）

0. 配備: document-service と graph-service を同じリリースで出す（document-service は新しい口、graph-service は NoSuchKey の扱い。マイグレーションは無い）。
1. dry-run（何も変えない）:
   `node scripts/archive-bodyless-documents.js --live --dry-run --operator <名前> --created-before 2026-09-27T00:00:00Z`
   期待: 「アーカイブの対象」が**約 5,803 件**。5 つの内訳（対象・本文あり・個人資料・既にアーカイブ済み・本文の参照なし）の合計が、
   再発行の dry-run の `--created-before 2026-09-27T00:00:00Z` の件数（5,803）と一致する。**「本文が在る」が大きいなら止める**（切り替え前でも本文が在る文書は触らない設計だが、前提が崩れている）。
2. カナリア（20 件）:
   `node scripts/archive-bodyless-documents.js --live --operator <名前> --reason "#1931 項目 3（カナリア）" --created-before 2026-09-27T00:00:00Z --canary 20 --max-pages 1`
   確かめること: 取り込み・Wiki・グラフのキューが空になり DLQ が増えない（スクリプトが見る）／SC-05 の状態「アーカイブ済み」の件数が 20 増える／
   document-service のログに `Archived body-less document` が 20 行／`ingestion_body_missing_total` が 20 増える。
3. 全件: `node scripts/archive-bodyless-documents.js --live --resume --operator <名前> --reason "#1931 項目 3"`（途中で止まったら同じコマンドで再開）。
4. 確認: もう一度 dry-run を流し「アーカイブの対象」が 0・「既にアーカイブ済み」が約 5,803 増えていること。SC-05 の状態別の件数の合計が「すべて」と一致すること。
   検索（SC-01）で切り替え後の文書が従前どおり当たること（新しいコレクションの `points_count` が変わらないこと）。
5. 戻し方: 決定 6（計画の裁定の後に限る）。

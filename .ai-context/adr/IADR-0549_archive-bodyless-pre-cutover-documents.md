---
title: IADR-0549 9/26 の切り替えより前に作られ本文のオブジェクトが無い（NoSuchKey）文書は、管理者だけの一回限りの口と駆動スクリプトで既存の状態「アーカイブ」へ移す。本文の有無は HEAD と範囲 0-0 の GET で確かめ、索引からは単票のアーカイブと同じ発行で外す。アーカイブ解除は足さない（#1931 項目 3）
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-02, FR-17, UC-03, SC-05, ADR-0106, IADR-0503, IADR-0509, IADR-0540, IADR-0541, IADR-0545]
author: claude
created: 2026-10-16
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-05 文書管理。公開・アーカイブ・アーカイブ解除は計画に無い旨）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
related_specs:
  - ../specs/20261017_1983_bulk-op-publish-recovery-without-log-ids.md
  - ../specs/20261017_1974_archive-ast-quote-snapshots.md
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
   - 🔴 **ページの途中まで保存しない**: 確かめを全件終えてから 1 回だけ保存する。確かめの失敗（NoSuchKey 以外）は 502 で返り、同じ要求の再実行で回収できる。
     ストレージが未構成なら 503（`NullObjectStorageClient` は在るとも無いとも答えない）。
3. **本文の有無は HEAD と、必要なときだけ範囲 0-0 の GET で確かめる。本文を読まない。** 共有の port に `IObjectStorageClient.ExistsAsync` を足した
   （既定の実装は `NotSupportedException`。試験の偽物 16 件を直さずに済ませ、本番の 2 実装は明示的に実装する）。
   - `S3ObjectStorageClient`: HEAD（`GetObjectMetadata`）が 200 なら在る（本文は 1 バイトも運ばない）。**HEAD の 404 だけでは「無い」と言わない** ——
     HEAD の応答は本文を持たず、SDK のエラーコードは状態コード由来（`NotFound`）になり、キーの欠落とバケットの欠落を区別できない。
     そこで 404 のときだけ範囲 0-0 の GET を撃ち、エラーコードを `ObjectStorageErrors.IsNoSuchKey`（IADR-0540 決定 2 と同じ判定）で見る。
     オブジェクトが無ければ GET は本文を返さない（エラーの XML だけ）。HEAD と GET の間に作られていれば在ると答える（最大 1 バイト）。
   - 統合試験 `Exists_tells_missing_key_from_missing_bucket` を足した。手元の SeaweedFS で確かめたのは前半の 2 点（キーの欠落は「無い」・バケットの欠落は例外）である。
     後半（在る・消した）は、この環境では Put が SeaweedFS の 500 で落ちて確かめられなかった（既存の同クラスの 3 件も同じ）。日次の integration で確かめる。
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
   - ［2026-10-16 追記 / #1961・[IADR-0551](./IADR-0551_archive-excluded-from-discovery-surfaces.md)］**計画 ADR-0141 がアーカイブの効果（検索・RAG・グラフ・Wiki・MCP の一覧に載せない）を定めたので、この案は IADR-0551 決定 2・3 で採った。**
     本 IADR の決定 4（取り込みの経路）・§結果 の計数の増加・§PoC の手順 2 の `ingestion_body_missing_total` は、IADR-0551 の配備の後は当たらない（本文を読まずに外れる。計数は増えない）。
     決定 6 の「計画の裁定」は計画 ADR-0141 決定 1（解除は持たない。戻し方は (a) 再同期・(b) 本決定の SQL）である。
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
   終わったら、document-service のログの `Archived body-less document` の行数と、スクリプトの「完了: アーカイブ N 件」（状態ファイルの `totals.archived`）を突き合わせる。
   口が 500 で止まった回があれば、§結果 の回収手順（発行の取りこぼし）を行う。
4. 確認: もう一度 dry-run を流し「アーカイブの対象」が 0・「既にアーカイブ済み」が約 5,803 増えていること。SC-05 の状態別の件数の合計が「すべて」と一致すること。
   検索（SC-01）で切り替え後の文書が従前どおり当たること（新しいコレクションの `points_count` が変わらないこと）。
5. 戻し方: 決定 6（計画の裁定の後に限る）。

## ［2026-10-17 追記 / #1974］同じ手段で AST の相場スナップショット（kind = Quote）をアーカイブする

- 起点: #1974（AST#1366 の後始末）。AST は 2026-10-16 まで 5 分ごとに相場のスナップショット（project = ai-stock-trading・属性 `kind = Quote`）を保存していた
  （10-15 の 1 日だけで約 950 件）。旧い文書は索引に残り、銘柄の検索の関連度の候補（4×TopK）を占め続ける。AST の検索の絞り込みは単値の完全一致だけで、
  取り出した後に落としても候補に入れなかったニュース・開示は戻らない。
- **決定: 本 ADR の手段をそのまま使い、母集合だけを変えた口 `POST /documents/archive-ast-quotes` を足す。新しい仕組みは作らない。**
  - 遷移は `Document.Archive(注記)`、下流は単票のアーカイブと同じ発行の門、ページ送り・`dryRun` 必須・アーカイブは `reason` 必須・監査ログ・`requestedBy` / `reason` の上限は本 ADR の口と同じ。
  - 母集合は **アーカイブ済みでない ∩ `project = ai-stock-trading` ∩ `kind = Quote`（大文字小文字を区別する完全一致）∩ 組織文書**で、条件は口の側で固定する。
    属性は jsonb の値変換のため SQL へ訳せないので、一覧の口と同じ `DocumentPageQuery.ReadPageAsync` で塊の中で当てる（本 ADR の口の `CreatedAt < createdBefore` の SQL の絞り込みとはここだけが違う）。
  - 本文は読まない（存在の確認も要らない）。版の注記は `archived: AST quote snapshot (kind=Quote) — bulk archive of AST market snapshots (#1974)`（決定 6 の SQL はこの文字列でも引ける）。
  - dry-run は件数と**対象の文書 ID** を返す。駆動スクリプト `scripts/archive-ast-quote-documents.js` は本 ADR の駆動器と同じ本体（`scripts/lib/admin-page-driver.js`。IADR-0556 決定 6）の profile である。
- 却下: **削除（Delete）**は戻せない（#1974 が残余として求めた）。アーカイブなら台帳と本文が残り、SC-05 で「アーカイブ済み」として見分けられる。
  **検索の絞り込みに否定（`kind ≠ Quote`）を足す**のは AST 側の要求を基盤の検索の契約へ広げることになり、索引に点が残る（候補の母集合は減らない）。
- 残余: 本 ADR §結果 の「保存の後の発行が途中で失敗した文書は再実行で拾われない」も同じ。**回収はログの ID ではなく母集合で再発行する**: `republish-document-updated.js --attr project=ai-stock-trading --attr kind=Quote --status archived`（冪等。ページの途中で発行が失敗すると、それより後の文書は監査の行が出ないまま台帳ではアーカイブ済みになるため、ID では拾えない）。
  アーカイブ解除が無い点（決定 6）も同じ。AST が Quote の保存を止める前に配備すれば、その後の Quote は次の実行で拾う（冪等なので流し直してよい）。
- PoC の手順（実行はオーナーの承認の後）: 0. document-service を配備 → 1. `node scripts/archive-ast-quote-documents.js --live --dry-run --operator <名前>`（件数と ID）→
  2. カナリア `--reason "#1974（カナリア）" --canary 20 --max-pages 1` → 3. 全件 `--resume --reason "#1974"` → 4. dry-run の再実行で対象 0 件・SC-05 の「アーカイブ済み」が件数どおり増える。

## ［2026-10-17 追記 / #1983］発行の取りこぼしの回収は、ログの ID ではなく母集合で行う

- 起点: #1983（MSP#1982 の AI レビュー）。口はページ全体を保存してから 1 件ずつ監査の行を出して発行する（決定 5・§結果）。発行がページの途中で失敗すると、
  それより後の文書は台帳ではアーカイブ済みなのに監査の行（`Archived body-less document`）も発行も無い。運用仕様書が挙げていた「ログの行から ID を引く」回収では拾えない。
- **決定: 回収の母集合にログの行を使わない。** 運用仕様書の「口が 500 で止まったとき」は次の 2 つだけを示す（コードは変えない。口の並びを変える案は採らない）。
  - (a) 既定: `republish-document-updated.js --created-before 2026-09-27T00:00:00Z --status archived`。切り替え前のアーカイブ済み全件に status=archived を出し直す
    （件数の上限なし・本文を読まず費用なし・冪等。単票でアーカイブした文書も含むが害は無い）。dry-run の「対象」は一括アーカイブの dry-run の「既にアーカイブ済み」と一致する。
  - (b) この口の分だけ: 決定 5 の版の注記から ID を引き、`--ids` の上限（500 件）ごとに分けて流す。§結果 の「決定 6 の確認の SQL」は戻すための形（直前の版の状態を結ぶ）なので、
    回収には**文書ごとの最後の版の注記が決定 5 の文字列**の文書だけを引く形を使う（その後に再正規化された文書は別のイベントが出ているので外れる）。
    SQL と分割のループは運用仕様書（本文の無い切り替え前の文書のアーカイブ）にある。
- §PoC の手順 2・3 のログの行数の突き合わせは、途中で止まらなかった回の確認としては有効である。ただし止まった回では行が足りないのが正常であり、回収の母集合には使わない。
- 確かめたこと: 使い捨ての PostgreSQL 16 で、最後の版が本操作の文書だけが引かれ、再正規化・単票のアーカイブが外れること。「どれかの版」で引く変異では再正規化した文書を拾う（作業仕様書 `20261017_1983_bulk-op-publish-recovery-without-log-ids`）。

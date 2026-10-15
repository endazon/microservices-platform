---
title: 作業仕様書 — 9/26 の切り替えより前に作られ本文のオブジェクトが無い文書を、一回限りの管理操作で既存の状態「アーカイブ」へ移す（#1931 項目 3・裁定 B）
type: spec
status: done
related_ids: [FR-06, FR-02, FR-17, UC-03, SC-05, ADR-0106, IADR-0503, IADR-0509, IADR-0540, IADR-0541, IADR-0549]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-05 文書管理）
---

# 作業仕様書: 本文の無い切り替え前の文書のアーカイブ（#1931 項目 3）

> 本仕様書は実装着手前に作成した（着手 2026-10-16）。基点は MSP `origin/develop` `5e49af8d`。

## 起点

- #1931 項目 3。オーナー裁定（2026-10-15）は **B（アーカイブ）**: 「9/26 の切り替えより前に作られ、本文の無い文書（PoC の dry-run で
  5,803 件）はアーカイブにする。台帳と表題は残す。検索の挙動は今と変わらない。SC-05 の一覧では『アーカイブ』として見分けられる」。
- 対象の条件: `createdAt < 2026-09-27T00:00:00Z` かつ本文のオブジェクトが NoSuchKey（判定は IADR-0540 の `ObjectStorageErrors.IsNoSuchKey`。404 だけで判断しない）。
- 先例: 再発行の口 `POST /documents/republish-updated` と駆動スクリプト `scripts/republish-document-updated.js`（IADR-0503 / IADR-0509）。

## 着手前の調査（依頼の 1〜3）

### 1. アーカイブの状態は既にあるか → **在る。状態を新設しない**

- ドメイン: `DocumentStatus.Archived = "archived"`（`Domain/Document.cs`。Issue #88）。遷移 `Document.Archive()`。単票の口 `POST /documents/{id}/archive`（管理者だけ。個人資料は 404）。
- 計画: SC-05 §主要素・§アクション（2026-08-14 追加）が「公開（`lifecycle` を `draft` → `active`）・**アーカイブ（`lifecycle` を `archived`）**」を定め、
  管理者限定の破壊的操作に数えている。FR-06 の計画書にはアーカイブの語は無いが、SC-05 が FR-06 の画面であり根拠になる。
- 機能仕様書 `docs/functional/FR-06_document-crud-versioning.md` も `POST /documents/{id}/archive` を管理者限定として挙げる。
- 画面: SC-05 の状態の絞り込み（#1923 段 2 の前半・IADR-0545）に「アーカイブ済み」が既に在る。**SC-05 の UI 変更は要らない**。
- → 既存の状態へ移すだけにする。停止条件（計画に無い状態の新設・SC-05 の UI 変更）には当たらない。

### 2. アーカイブしたときの索引の扱い・見え方

- 単票のアーカイブは `DocumentUpdated`（status=archived）を発行の門越しに出す。受け手の挙動（実測＝コード読み）:
  - **取り込み**: 状態を見ない。露出の門 → `MarkdownUri` の有無 → **全コレクション（モデル別コレクション＋語彙索引）から当該文書の点を削除**
    （`QdrantIngestionVectorStore.DeleteByDocumentFromAllAsync`。旧コレクションも `_collections` に入っていれば消える）→ 本文の取得 → NoSuchKey →
    IADR-0540 の分岐で Warning と計数 `ingestion_body_missing_total` を残して ack（点は書かない）。**つまり旧コレクション・語彙索引に残る点は、アーカイブの発行で消える。**
  - **Wiki 同期**: `status == "archived"` の分岐が本文を読む前にあり、Wiki.js のページを非公開化しメタデータを Archived にする（冪等・deny-closed）。
  - **グラフ**: 状態を見ない。本文を読むのは本文指紋が変わったとき（またはノードが無く指紋があるとき）だけ。アーカイブは指紋を変えないので通常は読まないが、
    ノードの無い文書では本文を読み、**NoSuchKey で再試行の後 DLQ へ入り得る**（IADR-0540・0541 の残余）。→ 本件で塞ぐ（読み手が NoSuchKey を null＝本文なしとして返す）。
- 検索・RAG: 新しいコレクション `knowledge_chunks_voyage_3_5` には、5,803 件は元から点を持たない（再索引を `--created-after 2026-09-27` で絞ったため。#1931 のコメント 15:09Z）。
  旧コレクション・語彙索引に点が残っていれば、アーカイブの発行で取り込みが消す。**検索・RAG への見え方は「当たらない」のまま変わらない**。
- SC-05: 状態の列と絞り込みで「アーカイブ済み」として見分けられる（IADR-0545）。件数の要約（状態別）にも出る。
- 取り込みの計数 `ingestion_body_missing_total` はアーカイブした件数だけ増える（期待どおり。PoC の手順に書く）。

### 3. 戻す手段（アーカイブ解除）はあるか → **製品の操作としては無い。計画にも無い**

- ドメインは `archived → published` を禁じている（`Publish()` が例外。「再公開する場合は再取り込みが必要」）。解除の口は無い。
- 計画 SC-05 §アクション: 「**`archived → active`（アーカイブ解除）は計画に無い**（実測 2026-08-14）。**要否・可否は未定**。実装が当該操作を持つなら、分類とあわせて環流すること」。
- 既存の戻り道: 再正規化（`ApplyNormalized` が状態を `normalized` へ戻す）＝データソースの再同期で同じ文書 ID の `DocumentNormalized` が届けば戻る。
- → **解除の口は足さない**（計画に無い遷移を実装で作らない）。裁定コメントの「いつでも戻せます」は製品の操作としては成り立っていない —— 報告と計画への環流の論点にする。
  非常時の戻し方（台帳の版履歴から直前の状態を引く SQL）は IADR に書き、実行は計画の裁定の後とする。

## 方式

1. **DocumentService に管理者だけの口 `POST /documents/archive-bodyless`** を足す（`Features/Documents/ArchiveBodyless/Endpoint.cs`）。
   - 要求: `dryRun`（必須）・`createdBefore`（**必須・既定なし**）・`limit`（既定 100・1〜200）・`cursor`・`requestedBy`・`reason`（アーカイブする呼び出しでは必須）。
   - 母集合（SQL）: `CreatedAt < createdBefore` かつ `Status != archived` かつ `MarkdownUri` が `storage://` で始まる。並びは `(CreatedAt, Id)` のキーセット（`DocumentPageQuery.AfterCursor` / `InPageOrder` を再利用）。
   - ページの文書ごとに: 個人資料は数えて飛ばす（単票の口と同じく対象外）→ `IObjectStorageClient.ExistsAsync` で在るかを確かめる。
   - 🔴 確かめが 1 件でも失敗（NoSuchKey 以外）したら、そのページは**何も保存しない**（保存は全件の確かめの後に 1 回）。未構成は 503。
   - アーカイブ: `Document.Archive(ChangeNote)`（版の注記を固定の文字列にする）→ 保存 → 単票と同じ発行の門 `PublishUpdatedIfIndexableAsync`。
   - 応答: `matched`・`remaining`・`selected`・`bodyMissing`・`bodyPresent`・`skippedPrivateNote`・`archived`・`alreadyArchived`・`notStored`・`nextCursor`。
   - 監査: アーカイブした文書ごとに 1 行（ID・作成日時・本文の所在・主体・`requestedBy`・`reason`）と、ページごとに 1 行。dry-run も残す。
2. **存在確認（HEAD 相当）を共有の port に足す**: `IObjectStorageClient.ExistsAsync`（既定の実装は `NotSupportedException`。試験の偽物を全部直さないため）。
   - `S3ObjectStorageClient`: HEAD（`GetObjectMetadata`）→ 200 なら在る。404 のときだけ**範囲 0-0 の GET** でエラーコードを得て、`IsNoSuchKey` なら無い。
     それ以外（NoSuchBucket・403・5xx・時間切れ）は例外。HEAD の 404 はキーの欠落とバケットの欠落を区別できないため（`IsBucketAbsent` の注記と同じ事情）。
   - `NullObjectStorageClient`: 投げる（未構成で「無い」と答えると全件をアーカイブしてしまう）。
3. **駆動スクリプト `scripts/archive-bodyless-documents.js`**: 再発行の駆動器と同じ作法（`--live` 必須・`--dry-run`・カナリア・`--max-pages`・状態ファイルと `--resume`・
   確かめた位置へ戻す・`--operator` / `--reason`・連続失敗で止まる）。I/O は再発行の駆動器の関数を再利用する（`makeIo` / `waitReachable` / `readQueueDepths` を export に足す）。
   見張るキューは取り込み・Wiki・グラフの `DocumentUpdated` の 3 つ（合計の深さ）と共有の DLQ。DLQ の許容は既定 0（アーカイブで下流が失敗する理由は無い）。
   dry-run は全ページを辿って確かめ、集計を出す（状態ファイルは作らない）。
4. **グラフの本文の読み手**（`GraphService/Infrastructure/ExternalServices/StorageContentReader.cs`）: NoSuchKey を null（本文なし）として返す。契約どおり辺は触らず、語の出現数は表題だけ。
5. 取り込み・Wiki の注記（「#1931 項目 3 は未決」）を裁定へ追随させる。取り込みのログの文言も直す（未決と言わない）。

## 対象外（触らない）

- 取り込みに「archived なら本文を読まずに撤収する」分岐は足さない（本文の在るアーカイブ文書の索引の扱いを全体で変える。計画は Wiki の非公開化しか定めていない）。残余に書く。
- 再発行の口から archived を外す絞り込みも足さない（残余。今は `--created-after 2026-09-27` で避けている）。
- SC-05 の UI・BFF・proto は変えない。

## 受け入れ基準と試験の写像

| 基準 | 試験 |
|---|---|
| 管理者だけ。運用者・機械の書き手・一般は 403 で何もしない（陽性対照つき）。口は `AdminOnly` を積む | `ArchiveBodylessDocumentsEndpointTests` 管理者だけ… / 口はAdminOnlyを積む |
| `dryRun`・`createdBefore` 必須、アーカイブする呼び出しは `reason` 必須、`limit` 範囲・不正なカーソルは 400。ストレージに当たらない | 必須の指定が欠けた要求… |
| dry-run は件数と内訳だけ。台帳・版・更新時刻・イベントを動かさない。本文を読まない | dry_runは件数と内訳だけを返し… |
| 本文の無い切り替え前の文書だけをアーカイブ。対象外（本文あり・期限ちょうど・期限後・本文の参照なし・個人資料・アーカイブ済み）は状態も版も更新時刻も動かず、イベントも出ず、ストレージにも当たらない | 本文の無い切り替え前の文書だけを… |
| 冪等（2 回目は何もしない・版を積まない・イベントを出さない） | 二回流しても同じで… |
| キーセットで取りこぼし・重複なし | ページを辿ると… |
| 確かめの失敗（NoSuchBucket）ではページを 1 件も保存しない。直れば同じ要求で回収できる | 確かめに失敗したら… |
| 未構成は 503 で何もしない | ストレージが構成されていなければ503… |
| 存在確認: 在るときは HEAD だけ／HEAD 404＋GET NoSuchKey（専用型・汎用型）で無い／NoSuchBucket・403・5xx は投げる／未構成・既定の実装は投げる | `S3ObjectStorageClientExistsTests` |
| 実ストア（SeaweedFS）でキーの欠落は無い・バケットの欠落は投げる・在るものは在る・消したものは無い | `ObjectStorageRoundTripTests.Exists_tells_missing_key_from_missing_bucket`（統合） |
| グラフの読み手: NoSuchKey は null、NoSuchBucket・5xx は投げる | `StorageContentReaderBodyMissingTests` |
| 駆動器: `--created-before` 必須・`--resume` では受けない・理由必須・再発行の状態ファイルを拒む・dry-run の集計・カナリア・DLQ で戻す・再開と再実行の冪等・状態ファイルが残るまま新規は拒む | `scripts.repo.test.js` の #1931 項目 3 節 |

## 変異試験（赤になることを確かめる）

口: `AdminOnly` を外す（2 件赤）／期限を `<=` にする（3）／存在確認の結果を反転（6）／dry-run でもアーカイブ（1）／アーカイブ済みを母集合から外さない（3）／
確かめの途中で保存する（3）。S3: GET の判定を 404 だけにする（1）／HEAD だけで決める（3）／HEAD を撃たない（4）。
駆動器: DLQ の停止を外す（赤）／dry-run を発行に倒す（赤）。

## 母集合（規則 9・10）

- 「#1931 項目 3」「item 3」で全文書を走査: `IngestionService/.../DocumentUpdatedConsumer.cs`（注記・ログ文言）・`DocumentBodyMissingTests.cs`（注記）・
  `WikiService/.../DocumentSyncConsumer.cs`（注記）・IADR-0540（決定 4 と題）・IADR-0541（残余）・ADR 索引の IADR-0540 の行。
  → コードと試験の注記は追随する。IADR-0540・0541 は凍結記録なので本文を書き換えず、IADR-0540 に日付つきの追記を足す（索引の行は題と同じなので据え置く）。
  `.ai-context/specs/` は point-in-time の記録なので触らない。
- `IObjectStorageClient` の実装（18 件。本番 2・試験の偽物 16）: 既定の実装を置くので偽物は直さない。本番の 2 つは明示的に実装する。
- 自分の記述で新たに誤りになるもの: 再発行の駆動器の export（足すだけ）・`live-scripts.json`・`scripts/README.md` の 2 表・検査器の母集合の除外（`NOT_CHECKERS`）。

## 未決・計画への環流

- **アーカイブ解除（`archived → active`）は計画に無い**（SC-05 が明記）。裁定の前提「いつでも戻せる」は製品の操作として成り立っていない。要否の裁定を計画へ環流する要がある。

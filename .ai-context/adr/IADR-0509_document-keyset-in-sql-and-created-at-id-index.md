---
title: IADR-0509 文書のキーセット（作成時刻昇順・同時刻は ID 昇順）は SQL で並べて比べ、(CreatedAt, Id) の複合索引で引く。GET /documents/page は塊ごとに読んでメモリの述語を当て、再発行の口は属性の絞り込みが無いとき limit + 1 行と COUNT(*) で答える
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-06, NFR-08, UC-04, ADR-0013, ADR-0119, IADR-0503, IADR-0475, IADR-0483]
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0013_embedding-model.md（再索引の手段）
related_specs:
  - ../specs/20261008_1765_document-keyset-sql.md
---

# IADR-0509: 文書のキーセットを SQL へ移し、(CreatedAt, Id) の索引で引く（#1765）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-08
- 決定者: claude（#1765）

## 起点・関連

- 起点 issue: **#1765**（#1763 / [IADR-0503](./IADR-0503_republish-document-updated-admin-endpoint-and-driver.md) の独立監査 Y3 から切り出し）
- 先行: [IADR-0475](./IADR-0475_document-page-and-fingerprint-in-response.md) 決定 4（`GET /documents/page` は台帳を読んでからメモリで絞る）・
  IADR-0503 決定 2（再発行の口は同じキーセットを再利用し、ページごとに投影を全件読む）・
  [IADR-0483](./IADR-0483_document-read-content-abac-behind-gate.md)（内容の ABAC の門が開いたときは切り出しの前に絞る）
- 作業仕様書: [`20261008_1765_document-keyset-sql.md`](../specs/20261008_1765_document-keyset-sql.md)

## コンテキストと課題

再発行の口（`POST /documents/republish-updated`）と `GET /documents/page` は、ページごとに台帳を全件読み、.NET で並べてカーソルの後ろを切っていた。
1 ページ O(N)、1 回の走査 O(N²) である（経路B の 22,564 件・ページ 50 で約 453 回 × 22,564 行 ≈ 1,020 万行）。

決めること:

1. 並べる・カーソルと比べるをどこで行うか（Postgres の uuid の順と .NET の `Guid.CompareTo` の順を混ぜない）。
2. SQL の述語の形（索引の開始位置に使える形か）。
3. 属性の絞り込み（jsonb の値変換。LINQ から SQL へ訳せない）と、再発行の口の件数（`matched` / `remaining`）の扱い。
4. 索引とマイグレーションの当て方。

## 実測（2026-10-08。使い捨てのローカル PostgreSQL 16、`Documents` 22,564 件、作成時刻は 3 件ずつ同時刻）

カーソルを 15,000 件目に置き、続きの 51 行を引いた（`EXPLAIN (ANALYZE, BUFFERS)`）。

| 形 | 計画 | 読んだ／捨てた行 | 実行時間 |
| --- | --- | --- | --- |
| 従前（投影を全件） | Seq Scan | 22,564 行を運ぶ | 2.9 ms（DB 側のみ。転送と jsonb の復元は別） |
| `CreatedAt > c OR (CreatedAt = c AND Id > id)` | 索引を先頭からなめる（Filter のみ） | **15,001 行を捨てる** | 1.76 ms |
| **`CreatedAt >= c AND (CreatedAt > c OR Id > id)`** | **Index Cond: `CreatedAt >= c`** | 2 行を捨てる（同時刻の群だけ） | **0.054 ms** |
| `(CreatedAt, Id) > (c, id)`（行値） | Index Cond: ROW(...) | 0 行 | 0.046 ms |
| `COUNT(*)`（全件） | Seq Scan | 22,564 行（運ばない） | 2.5 ms |
| `COUNT(*)`（カーソルより後ろ） | Bitmap Index Scan | 7,565 行（運ばない） | 1.7 ms |

素の OR の形は索引を「並び」にしか使えず、カーソルの位置に比例して読む（O(N²) が DB の中へ移るだけ）。

## 検討した選択肢

| 論点 | 案 | 評価 |
| --- | --- | --- |
| 述語の形 | 素の OR | 却下。上の実測のとおり位置に比例して読む |
| | 行値の比較（Npgsql の `EF.Functions.GreaterThan(ValueTuple, ValueTuple)`） | 却下。最も速いが EF InMemory で評価できず、単体試験（InMemory）と本番で経路が割れる |
| | **冗長な先頭項つきの AND（`CreatedAt >= c AND (CreatedAt > c OR Id > id)`）** | **採用**。意味は素の OR と同じで、InMemory でもそのまま評価でき、Postgres は先頭項を索引の開始位置に使う。捨てるのは同時刻の群だけ |
| 属性の絞り込み（`GET /documents/page`） | jsonb の包含（`@>`）へ訳す | 却下。個人資料の判定（`IsPrivateNote`）は大文字小文字を区別しない（包含では同じ意味にならない）。内容の ABAC は 1 件ずつの非同期の判定で SQL へ訳せない。値変換の写し方の変更も伴う |
| | **SQL のキーセットで塊ごとに読み、塊の中でメモリの述語を当てる** | **採用**。述語は従前と同じ関数のまま。絞り込みが緩ければ 1 ページ約 `limit + 1` 行、厳しくても 1 回の走査の総読み出しは O(N) |
| 属性の絞り込み（再発行の口） | 塊ごとに読む（上と同じ） | 却下。ページは速くなるが `matched` / `remaining` を正しく数えるには結局全件の属性を読む（ページごとに O(N) が残る） |
| | 件数を「概数」へ緩める | 却下。駆動スクリプトの進捗表示と状態ファイルが使っており、応答の意味を変えずに直すのが本件の射程 |
| | **属性の絞り込みがあるときだけ従前の経路** | **採用**。全件の再索引（属性なし）が主な用途であり、そこを O(limit) にする。属性つきは従前どおり（残余） |

## 決定

### 決定 1: 並べる・カーソルと比べるは、どちらも SQL で行う

- 共通の拡張 `DocumentPageQuery.InPageOrder`（`ORDER BY "CreatedAt", "Id"`）と `DocumentPageQuery.AfterCursor` を両方の口が使う。
  **.NET 側で並べ直さない。** カーソルは DB から読んだ行（timestamptz＝マイクロ秒に丸まった値・uuid）から作るので、境界の行と等しく比べられる。
- カーソルの線上の形（`v1:<UtcTicks>:<Id>` の base64url）と意味（前ページ末尾より厳密に後ろ）は変えない。時刻は offset 0 で渡す（Npgsql の制約）。
- 実 Postgres の試験で、Postgres の uuid の順が従前の .NET の並び（`UtcTicks` → `Guid.CompareTo`）と一致することも固定した
  （先頭バイトの最上位ビット・後半 8 バイト・先頭 3 フィールドの上位／下位バイトだけが違う ID）。したがって配備の前後をまたぐカーソルも同じ位置を指す。

### 決定 2: 述語は冗長な先頭項つきの AND で書く

`CreatedAt >= c AND (CreatedAt > c OR Id > id)`。先頭項を消すと、意味は同じまま索引を先頭からなめる形へ戻る（上の実測）。
統合試験が実行計画に `Index Cond: ("CreatedAt" >= …)` が現れることを見る（素の OR へ戻す変異は赤）。

### 決定 3: `GET /documents/page` は塊ごとに読み、再発行の口は属性の絞り込みが無いときだけ SQL で切る

- `GET /documents/page`: 塊は初回 `limit + 1` 行、以降 2 倍・上限 2,000 行。塊の続きは**塊の末尾の行**から作る。塊の中で述語（個人資料を除く・属性の完全一致・
  門が開いていれば ABAC）に一致した文書を積み、`limit + 1` 件に達するか台帳が尽きるまで進む。集合は従前と同じ（読める ∩ 組織文書 ∩ 絞り込みに一致）。
  ABAC の判定と共有先の読み出しは、塊の中で絞り込みに一致した文書の分だけ行う（従前は台帳の全件分）。
- 再発行の口（属性の絞り込みなし）: ページは `AfterCursor` + `InPageOrder` + `LIMIT limit + 1` の投影、`matched` は `COUNT(*)`、`remaining` は
  カーソルより後ろの `COUNT(*)`（カーソルが無ければ `matched`）。dry-run は残りの投影を 1 回読む（従前どおり O(N)。走査の前に 1 回だけ呼ぶ）。
- 再発行の口（属性の絞り込みあり）: 従前の経路のまま（全件の投影をメモリで絞って並べる）。経路の中では並べる・比べるがどちらも .NET なので規則は閉じている。

### 決定 4: 索引はマイグレーションで張り、起動時に当てる

`HasIndex(d => new { d.CreatedAt, d.Id })` ＋ マイグレーション `AddDocumentCreatedAtIdIndex`（`IX_Documents_CreatedAt_Id`）。
DocumentService は起動時に `MigrateAsync` するので、配備（イメージの差し替え）だけで当たる。`CREATE INDEX` は作り終えるまで表への書き込みを待たせるが、
2 万件規模では一瞬である（`CONCURRENTLY` は移行の取引の中で使えないので使わない）。

### 決定 5: 統合試験から内部の拡張を呼ぶため、`Knowledge.IntegrationTests` へ内部を見せる

`DocumentService.csproj` に `InternalsVisibleTo Knowledge.IntegrationTests` を足した。キーセットの SQL を実 Postgres で辿る試験
（`DocumentKeysetPostgresTests`）は、ブローカを要る HTTP の器を通さずに口の本体（`ReadPageAsync`）と共通の拡張を直接呼ぶ。拡張を公開へ広げるより狭い。

## 結果

- 全件の再索引（属性なし）の 1 ページは `limit + 1` 行の索引走査と 2 回の `COUNT(*)`（行は運ばない）になる。1 回の走査で運ぶ行は O(N)。
- `GET /documents/page` は絞り込みが緩ければ 1 ページ約 `limit + 1` 行を読む。応答の形・並び・カーソルの意味は変えない（既存の試験は無改修で緑）。

## 残余リスク（受け入れたもの）

- **属性の絞り込みつきの再発行は 1 ページ O(N) のまま**（決定 3）。駆動スクリプトの `--attr` で大きな台帳を流すと従前と同じ費用がかかる。
  直すには属性を SQL へ訳す（jsonb の包含。値の大小の扱いを口の意味と揃える）必要があり、口の意味の変更を伴うので別の判断とする。
- `COUNT(*)` は Postgres の中では O(N)（2 万件で約 2 ms）。行は運ばないので、従前の全件の投影の転送と復元よりずっと軽い。
- `GET /documents/page` の厳しい絞り込み（一致がまばら）は、1 ページを満たすまで台帳を塊で読み進める。1 回の走査の総読み出しは O(N) だが、1 ページの読み出しは一致の間隔に比例する。
- 統合試験の実行計画の検査は、逐次走査とビットマップ走査を禁じて「索引を並びのまま引ける形か」を見る。実際の計画の選択は表の大きさと統計に依る。

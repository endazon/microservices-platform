---
title: 作業仕様書 — 再発行の口と GET /documents/page のキーセットを SQL へ移し、(CreatedAt, Id) の複合索引を張る（#1765）
type: spec
status: done
related_ids: [FR-02, FR-06, NFR-08, UC-04, ADR-0013, ADR-0119, IADR-0509, IADR-0503, IADR-0475, IADR-0483]
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0013_embedding-model.md（再索引の手段の性能）
issue: "#1765"
---

# 作業仕様書 — 文書のキーセットを SQL へ移す（#1765）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。判断の記録は **IADR-0509** に置く。
> 基点は MSP `origin/develop` `af8a5400`。**稼働クラスタ・稼働 DB には一切触れていない。**
> 計測は使い捨てのローカル PostgreSQL 16（作業後に停止・削除）で行った。

## 起点（トレーサビリティ）

- 要求: **FR-02**（再索引の手段＝再発行の口）・**FR-06**（文書管理。`GET /documents/page`）。NFR-08（一覧の応答性）。
- 起点 issue: **#1765**（#1763 / IADR-0503 の独立監査 Y3 から切り出し）。関連 IADR: IADR-0503（再発行の口）・IADR-0475（`GET /documents/page`。決定 4「メモリで絞る」）・IADR-0483（内容の ABAC の門）。
- 採番: `origin/develop` `af8a5400` の IADR の最大は 0508、開いている PR に IADR は無い（コミット直前に再確認）。本件は **IADR-0509**。

## 調べたこと（`af8a5400`）

| # | 事実 | 出典 |
| --- | --- | --- |
| f-1 | 再発行の口はページごとに投影（ID・作成時刻・属性・本文の所在の有無）を絞り込みの全件読み、.NET で並べ（`UtcTicks`→`Guid.CompareTo`）、カーソルより後ろを数えてから `limit` 件を切る | `Features/Documents/Republish/Endpoint.cs`・`RepublishSelection.cs` |
| f-2 | `GET /documents/page` は `db.Documents.AsNoTracking().ToListAsync()` で本体ごと全件を読み、門が開いていれば全件へ ABAC をかけ、個人資料・属性で絞り、並べて切る | `Features/Documents/ListPage/Endpoint.cs`・`DocumentPageQuery.cs` |
| f-3 | 属性は `Dictionary<string,string>` を値変換で jsonb 文字列へ写している。LINQ の述語は SQL へ訳せない。個人資料の判定（`IsPrivateNote`）は**大文字小文字を区別しない**ので jsonb の包含でも訳せない | `Infrastructure/Persistence/DocumentDbContext.cs`・`Domain/DocumentAttributes.cs` |
| f-4 | `Documents` に `(CreatedAt, Id)` の索引は無い（主キー `Id` だけ） | `DocumentDbContextModelSnapshot.cs` |
| f-5 | **マイグレーションは起動時に自動で当たる**（`Program.cs` の `db.Database.MigrateAsync()`。リレーショナルのときだけ） | `Program.cs` |
| f-6 | 駆動スクリプトは応答の `matched`・`remaining`・`selected` を進捗表示（`done = matched - remaining + selected`）に使う | `scripts/republish-document-updated.js` |
| f-7 | 単体試験（`DocumentService.Tests`）は EF InMemory。実 Postgres は `Knowledge.IntegrationTests`（Testcontainers または `PLATFORM_TEST_POSTGRES`） | `Tests/TestWebApplicationFactory.cs`・`Fixtures/PostgresFixture.cs` |
| f-8 | .NET の `Guid.CompareTo` は `_a`(uint)・`_b`・`_c`(ushort)・`_d`..`_k`(byte) を符号なしで順に比べる＝文字列表記の順。Postgres の uuid は 16 バイトの memcmp（文字列表記の順）。Npgsql は RFC の順（文字列表記どおり）で書く。**順は一致するはず**だが、本件ではそもそも並べる・比べるを両方 SQL に置くので依存しない（実 Postgres の試験で固定） | .NET / Postgres の仕様 |

## 設計（正は IADR-0509）

1. **キーセットの述語を SQL で書く**: `CreatedAt > c OR (CreatedAt = c AND Id > id)`、並びは `ORDER BY CreatedAt, Id`。
   共通の拡張（`DocumentPageQuery.AfterCursor` / `InPageOrder`）を両方の口が使う。カーソルの時刻は `new DateTimeOffset(UtcTicks, 0)`（offset 0。Npgsql の制約）。
   カーソルは DB から読んだ値から作る（マイクロ秒に丸まった値）ので、境界の比較は丸めでずれない。
2. **`GET /documents/page`**: SQL のキーセットで**塊**（初回 `limit + 1` 行、以降 2 倍・上限 2,000 行）を読み、塊の中で従来と同じ述語
   （個人資料を除く・属性の完全一致・門が開いていれば ABAC）を当て、一致が `limit + 1` 件に達するか台帳が尽きるまで次の塊へ進む。
   述語は .NET のまま（f-3）。絞り込みが緩ければ 1 ページあたり約 `limit + 1` 行、厳しくても 1 回の走査の総読み出しは O(N)。
3. **再発行の口**:
   - 属性の絞り込みが無いとき（全件の再索引＝主な用途）: ページは SQL のキーセット＋`LIMIT limit + 1`、`matched` / `remaining` は `COUNT(*)`
     （カーソルが無ければ `remaining = matched`）。dry-run は残りの投影を 1 回読む（従来どおり O(N)。1 回だけ呼ぶ）。
   - **属性の絞り込みがあるとき: 従来の経路のまま**（全件の投影をメモリで絞る）。件数を正確に返すには属性を SQL へ訳す必要があり（f-3）、
     値変換の写し方の変更を伴う。残余として IADR に記録する。
4. **索引**: `HasIndex(d => new { d.CreatedAt, d.Id })` ＋ マイグレーション `AddDocumentCreatedAtIdIndex`（起動時に自動適用。f-5）。

## 母集合（規則 9・10。`af8a5400`）

`git grep -e "メモリで絞" -e "台帳を読んでから" -e "SQL へ訳せ" -e "全件を読" -e "republish-updated" -e "documents/page" -e "RepublishSelection" -e "DocumentPageQuery"`
（`src/ai-stock-trading`・`.ai-context/specs` を除く）で引いた。

| 出現 | 扱い |
| --- | --- |
| `ListPage/Endpoint.cs`・`DocumentPageQuery.cs`・`Republish/Endpoint.cs`・`RepublishSelection.cs` の「メモリで絞る」「全件を読む」注記 | **直す**（本件の実装） |
| IADR-0475 決定 4・結果（「1 ページごとに台帳全体を読む」）・フォローアップ 2 | **日付つき追記**で IADR-0509 を指す（本文は凍結） |
| IADR-0503 残余リスク（「#1765 へ切り出した」） | **日付つき追記**で解消と残り（属性の絞り込み）を書く |
| IADR-0483 再検討の条件（「台帳をメモリで絞る形が遅くなったとき」） | 触らない（ABAC の判定を DB へ訳す話であり、本件後も判定は .NET。条件は生きている） |
| `docs/operations/operations.md` 再索引の節 | 確認した: 性能・読み方の記述は無い。**直さない**（ただし索引のマイグレーションの記述は不要＝起動時に当たる） |
| `docs/api/openapi.yaml`・`docs/functional/FR-06…`・`docs/tests/FR-06…` | 確認した: 応答の形・並び・意味の記述だけで、読み方の記述は無い。応答は変えないので**直さない**。テスト仕様書へは実 Postgres の試験を 1 行足す |
| IADR-0323・IADR-0452・IADR-0476 の一致 | 別件（語の偶然の一致）。対象外 |

規則 10（この変更で新たに誤りになる自分の記述）: `DocumentPageQuery.Slice`（メモリの切り出し）は口から使われなくなるので削除する。
`DocumentPageCursor.Precedes` も同様に削除する（使う側が無くなる）。`RepublishSelection.Order/Remaining/Slice` は属性の経路で残る。

窓（時間差）の観点（AST 規約の規則 11 の趣旨を当てた。MSP の規約には無い）: 走査の途中の**追加**（末尾に現れる＝従来どおり）と
**削除**（カーソルを動かさない＝従来どおり）の両側をプローブとして試験に置く。カーソルは値なので、窓のどちらの端にも依らない。

## 受け入れ基準 → 試験

| 基準 | 試験 |
| --- | --- |
| 既存の試験（`RepublishDocumentUpdatedEndpointTests`・`DocumentPageTests`）がそのまま通る（応答・並び・カーソルの意味を変えない） | 既存（InMemory） |
| 同じ作成時刻の文書（先頭バイトの最上位ビットだけが違う Guid・後半 8 バイトだけが違う Guid を含む）を実 Postgres でページを辿り、重複も抜けも無い。並びが .NET の (`UtcTicks`, `Guid.CompareTo`) と一致する | 新 `DocumentKeysetPostgresTests`（統合） |
| 境界: 空・最後のページがちょうど `limit` 件・カーソルが最後の行を指す・走査の途中の追加と削除 | 同上＋単体 |
| `GET /documents/page` は絞り込みが厳しくても塊を渡って続きを見つける（塊の境目で抜けない） | 新しい単体試験（`DocumentPageTests`） |
| SQL の実行計画が `(CreatedAt, Id)` の索引を使う | 統合試験で `EXPLAIN` を読む（`enable_seqscan=off` で索引が使えることを確かめる） |
| マイグレーションがモデルと一致する | `dotnet ef migrations has-pending-model-changes` |

## 残るもの（受け入れたもの）

- 属性の絞り込みつきの再発行は従来どおり 1 ページ O(N)（IADR-0509 決定 3）。
- `COUNT(*)` は Postgres の中で O(N)（索引のみの走査）。行は運ばない。
- `GET /documents/page` の厳しい絞り込みは、1 ページを満たすまで台帳を塊で読み進める（1 回の走査で O(N)）。

## 実装中に改めたこと

- 述語を素の OR（`CreatedAt > c OR (CreatedAt = c AND Id > id)`）で書いたところ、22,564 件の実測で索引を先頭からなめていた
  （15,000 件目のカーソルで 15,001 行を捨てた）。冗長な先頭項つきの AND（`CreatedAt >= c AND (CreatedAt > c OR Id > id)`）へ改めた
  （Index Cond に入り、捨てるのは同時刻の群だけ）。実測の表は IADR-0509。
- 統合試験から内部の拡張を呼ぶため `InternalsVisibleTo Knowledge.IntegrationTests` を足した（IADR-0509 決定 5）。
- 試験の作成で `Guid.Empty` を鍵に使うと EF が採番し直すことを踏んだ（試験の側を直した）。

## 検証（2026-10-08）

| 項目 | 結果 |
| --- | --- |
| `dotnet test` DocumentService.Tests（InMemory） | 991 件すべて緑（既存の `DocumentPageTests`・`RepublishDocumentUpdatedEndpointTests` は無改修で緑） |
| `dotnet test` Knowledge.IntegrationTests `DocumentKeysetPostgresTests`（`PLATFORM_TEST_POSTGRES` に使い捨ての PostgreSQL 16） | 13 件すべて緑 |
| 変異（統合） | 素の OR へ戻す → 実行計画の試験が赤／`Id >= id` → 10 件赤／`ThenByDescending(Id)` → 12 件赤／塊を 1 回で打ち切る → 5 件赤。塊の続きを一致した末尾から作る変異は生き残る（読み直すだけで結果は同じ。効率の差） |
| 変異（単体） | 塊を 1 回で打ち切る → 3 件赤／`remaining = matched` → 1 件赤／`Take(limit)` → 3 件赤／カーソルを当てない → 2 件赤／`matched` を絞り込みなしで数える → 2 件赤 |
| `dotnet format backend.slnx --verify-no-changes`（knowledge） | 差分なし |
| `dotnet ef migrations has-pending-model-changes` | No changes |
| node 検査（trace-blocks・gen-knowledge-graph --check・cross-repo-refs・plan-id-qualification・reading-budget・doc-updated・backend-libraries・doc-links・trace-followthrough・test-traceability・test-spec-coverage・adr-numbering・commit-messages ほか）・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | すべて OK（test-spec-coverage は `--update` で床を上げた） |

---
title: 作業仕様書 — タグを識別子参照へ移行し、改名の追随と削除を実装する（#635）
type: work-spec
status: in-progress
related_ids:
  - FR-06
  - FR-09
  - SC-05
  - SC-09
  - UC-03
  - UC-05
  - IADR-0152
  - IADR-0153
author: claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../adr/IADR-0153_tag-identity-storage-and-projection.md"
  - "../adr/IADR-0152_tag-dictionary-contract.md"
  - "./20260809_issue-634_tag-dictionary-values-and-counts.md"
---

# 作業仕様書 — タグを識別子参照へ移行し、改名の追随と削除を実装する（#635）

## 起点となる計画書（トレーサビリティ）

| 種別 | ID | 何を求めているか |
| --- | --- | --- |
| 画面 | **SC-09** | **参照が 1 件でもあるタグは削除拒否**・**改名は既存文書へ追随**・削除前に使用件数を示す（確定 2026-08-02）。**辺は型の識別子を参照して保持し、表示名を複写しない**（同節。タグ辞書にも同じ規則を適用する） |
| 画面 | **SC-05** | タグは「既定タグ辞書に整合」 |
| 要求 | **FR-06** / **FR-09** | 文書管理／管理機能 |

**#542 の後半である**（前半 = #634。契約の定義は [[IADR-0152]]、本作業の実装方式は [[IADR-0153]]）。

## 射程

- `Document.Tags` / `DocumentVersion.Tags` を**タグの識別子**へ移行する（データ移行を伴う）
- **改名**（表示名の差し替え ＋ 該当文書の `DocumentUpdated` 再発行で射影を追随させる）
- **削除**（使用件数 0 件のときだけ許し、1 件以上なら**件数を添えて拒否**する）

**［射程から外れたもの］取り込み経路の扱いは #637 で着地済みである。**
本仕様書の初版は「辞書へ自動登録する」と書いていたが、**利用者裁定 2026-08-09 で覆った**——
**取り込み経路はタグを生成しない**（[[IADR-0153]] 決定 5 / planning#304）。
是正（`BuildTags` の停止・`ApplyNormalized` の上書き停止・未知タグ件数の観測）は **#637** で実施した。

## 母集合（[[IADR-0141]] 決定 1）

**着手時に実装側が引き直した。走査基準: #637 着地後の head。**

**［重要］自分の前の数えを転記しない。** #634 の時点では「30 ファイル」、本仕様書の初版では
「[[IADR-0153]] 決定 2 により狭い」と書いたが、**#637 が着地して前提が変わったので引き直した**——
取り込み経路がタグを生成しなくなり、`ApplyNormalized` からタグ引数が消えている。

### 触るもの（**実測。すべて DocumentService の中である**）

| # | 対象 | 何をするか |
| --- | --- | --- |
| 1 | `Foundation/Domain/Document.cs` | `Tags` を `List<string>`（表示名）→ **識別子**へ |
| 2 | `Foundation/Domain/DocumentVersion.cs` | 同上（`Capture` が `doc.Tags` を複写している） |
| 3 | `Foundation/Domain/Tag.cs` | `Rename` ＋ `UpdatedAt` を足す（#634 で「呼ぶ側が無い」として置かなかったもの） |
| 4 | `Foundation/Persistence/DocumentDbContext.cs` ＋ マイグレーション **1 本** | 列の型変更 ＋ **既存データの移行**（表示名を辞書へ登録してから紐づける） |
| 5 | `Foundation/Endpoints/DocumentEndpoints.cs` | **変換点**。要求の表示名 → 識別子、応答・`ToEvent` の識別子 → 表示名 |
| 6 | `Foundation/Endpoints/TagDictionaryEndpoints.cs` | **改名**（`PUT /tags/{id}`）・**削除**（`DELETE /tags/{id}`）。使用件数を識別子で数える形へ |
| 7 | `Composable/Steps/DocumentNormalizedConsumer.cs` | `KnownTagsAsync` が**識別子を返す**形へ（#637 で入れた絞り込みの戻り値） |

**本体コードは 7 ファイル、いずれも DocumentService の中で閉じる。** ＋ それぞれのテスト。

### 触らないもの（**[[IADR-0153]] 決定 2 が下流を変えないため。実測で確認した**）

| 対象 | 理由 |
| --- | --- |
| `ConversionJob.Tags` ＋ マイグレーション | 変換段はまだ文書ではない。**#637 で取り込みがタグを運ばなくなった**ので、器が残るだけである |
| `WikiPage.Tags` ＋ マイグレーション・`DocumentSyncConsumer` | **外部システムへ push する人が読む面**。`DocumentUpdated` から表示名を受け取り続ける |
| 3 つのイベント（`RawDocumentFetched` / `DocumentNormalized` / `DocumentUpdated`） | **契約は表示名のまま**。`DocumentUpdated` は発行時に解決した表示名を運ぶ |
| Qdrant ペイロード・`QdrantVectorStore` / `QdrantIngestionVectorStore` / `IVectorStore` | **検索の hot path に辞書引きを増やさない**（[[IADR-0153]] 決定 1） |
| `SearchResultDto` / `DocumentDto` / `/bff/attribute-values` / 画面 3 箇所 | 表示名を受け取り続ける。**#540 / #634 の口は変わらない** |
| `DataSourceSyncService` | **#637 で是正済み**（タグを生成しない） |
| `src/ai-stock-trading` | 別プロジェクトの submodule |

**マイグレーションは 3 本ではなく 1 本である**（DocumentService のみ）。

## 実装方針

1. `Tag` へ `Rename` と `UpdatedAt` を足す（#634 で「呼ぶ側が無い」として置かなかったもの。**今は呼ぶ側が在る**）。
   **`UpdatedAt` 列は #635 のマイグレーションで足す**（#634 では書き込む側が無かった）。
2. `Document.Tags` / `DocumentVersion.Tags` を `List<Guid>` へ。**マイグレーションで既存の表示名を辞書へ登録してから紐づける。**
3. `DocumentEndpoints` で解決する（要求の表示名 → 識別子、応答・イベントの識別子 → 表示名）。
4. `PUT /tags/{id}`（改名）: 表示名を差し替え、**該当文書の `DocumentUpdated` を再発行**する。**版は増やさない。**
5. `DELETE /tags/{id}`: 使用件数 0 件なら削除、1 件以上なら**件数を添えて 409**。
6. `DocumentNormalizedConsumer.KnownTagsAsync` の戻り値を**識別子**へ変える
   （#637 で入れた絞り込み。**自動登録はしない**——裁定で覆った）。

## テスト（受け入れ基準の写像）

| # | 確かめること |
| --- | --- |
| 1 | 文書が**識別子**でタグを保持している（表示名を複写していない） |
| 2 | **改名すると既存文書の表示が新しい名前になる**（正本を書き換えずに） |
| 3 | 改名で**該当文書の `DocumentUpdated` が再発行される**（射影が追随する） |
| 4 | 改名で**版が増えない** |
| 5 | **版履歴の過去版も新しい名前で表示される** |
| 6 | 参照 1 件以上のタグの削除が**件数を添えて拒否**される |
| 7 | 使用件数 0 件のタグは削除できる |
| 8 | 移行後も**取り込み経路は辞書を増やさない**（#637 の不変条件が壊れていない） |
| 9 | 既存データの移行後、**表示名が失われていない**（辞書へ登録してから紐づけ直す） |
| 10 | 移行後も `/bff/attribute-values` と検索結果が**表示名**を返す（識別子を露出しない） |

## 実装中に決めたこと（仕様書からの差分）

（着手後に追記する）

## 検証記録（実測）

（着手後に追記する）

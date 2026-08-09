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
- 取り込み経路で辞書に無いタグが来たら**辞書へ自動登録する**（[[IADR-0153]] 決定 5。**計画へ裁定依頼中**）

## 母集合（[[IADR-0141]] 決定 1）

**着手時に実装側が引き直した。走査基準: develop `47006a1`。**

**#634 の時点では「30 ファイル」と見積もっていたが、[[IADR-0153]] 決定 2 により実際の対象はもっと狭い。**
**他人の数え（自分の前の数えも含む）を検証せず転記しない**（[[IADR-0141]]）。引き直した結果は次のとおり。

### 触るもの

| 対象 | 理由 |
| --- | --- |
| `Document.cs` / `DocumentVersion.cs` | **正本**。`List<string>`（表示名）→ 識別子 |
| `DocumentDbContext.cs` ＋ マイグレーション | 列の型変更 ＋ **既存データの移行** |
| `DocumentEndpoints.cs` | **変換点**。`ToEvent` と DTO 写像で識別子 → 表示名、要求の表示名 → 識別子 |
| `DocumentNormalizedConsumer.cs` | 取り込み経路。辞書に無いタグの自動登録 |
| `TagDictionaryEndpoints.cs` | **改名**（`PUT /tags/{id}`）・**削除**（`DELETE /tags/{id}`） |
| `Tag.cs` | `Rename` ＋ `UpdatedAt`（#634 で意図的に置かなかったもの） |
| 上記のテスト | 受け入れ基準の写像 |

### 触らないもの（**[[IADR-0153]] 決定 2 が下流を変えないため**）

| 対象 | 理由 |
| --- | --- |
| `ConversionJob.Tags` ＋ マイグレーション | **変換段はまだ文書ではない**。取り込み元から来た自由文字列であり、辞書との突合はカタログ登録時の仕事である |
| `WikiPage.Tags` ＋ マイグレーション・`DocumentSyncConsumer` | **外部システムへ push する人が読む面**。`DocumentUpdated` から表示名を受け取り続ける |
| 3 つのイベント（`RawDocumentFetched` / `DocumentNormalized` / `DocumentUpdated`） | **契約は表示名のまま**。`DocumentUpdated` は発行時に解決した表示名を運ぶ |
| Qdrant ペイロードの `tags`・`QdrantVectorStore` / `QdrantIngestionVectorStore` / `IVectorStore` | **検索の hot path に辞書引きを増やさない**（[[IADR-0153]] 決定 1） |
| `SearchResultDto` / `DocumentDto` / `/bff/attribute-values` / 画面 3 箇所 | 表示名を受け取り続ける。**#540 / #634 の口は変わらない** |
| `src/ai-stock-trading` | 別プロジェクトの submodule |

**したがってマイグレーションは 3 本ではなく 1 本である**（DocumentService のみ）。

## 実装方針

1. `Tag` へ `Rename` と `UpdatedAt` を足す（#634 で「呼ぶ側が無い」として置かなかったもの。**今は呼ぶ側が在る**）。
2. `Document.Tags` / `DocumentVersion.Tags` を `List<Guid>` へ。**マイグレーションで既存の表示名を辞書へ登録してから紐づける。**
3. `DocumentEndpoints` で解決する（要求の表示名 → 識別子、応答・イベントの識別子 → 表示名）。
4. `PUT /tags/{id}`（改名）: 表示名を差し替え、**該当文書の `DocumentUpdated` を再発行**する。**版は増やさない。**
5. `DELETE /tags/{id}`: 使用件数 0 件なら削除、1 件以上なら**件数を添えて 409**。
6. 取り込み経路で未知のタグは**辞書へ自動登録**する。

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
| 8 | 取り込みで辞書に無いタグが来たら**辞書へ自動登録**され、文書に付く |
| 9 | **画面からの手入力は自動登録しない**（SC-05） |
| 10 | 既存データの移行後、表示名が失われていない |

## 実装中に決めたこと（仕様書からの差分）

（着手後に追記する）

## 検証記録（実測）

（着手後に追記する）

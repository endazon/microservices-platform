---
title: 作業仕様書 — タグを識別子参照へ移行し、改名の追随と削除を実装する（#635）
type: work-spec
status: done
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

### 1. 母集合は 7 ファイルではなく **9 ファイル**だった（＋契約 1）

着手時の表に**漏れがあった**。実測で足したもの:

| 追加 | 何を | なぜ着手時に挙がらなかったか |
| --- | --- | --- |
| `Foundation/Services/TagResolver.cs`（**新規**） | 識別子 ⇄ 表示名の変換点 | 「`DocumentEndpoints` で解決する」とだけ書き、**置き場所を決めていなかった**。改名の再発行（`TagDictionaryEndpoints`）も同じ変換を要るので、端点に埋めると 2 つに割れる |
| `Shared/Knowledge.Contracts/Dtos/TagDictionaryDto.cs` | `RenameTagRequest` / `RenameTagResponse` | 「触らないもの」に**契約を一括で入れてしまっていた**。変わらないのは**既存**の契約（`DocumentDto` 等）であって、改名という新しい操作の要求・応答は当然増える |

**`DocumentEndpoints.ToEvent` を `internal` へ上げた。** 改名の再発行が同じ形を要るためで、
**識別子 → 表示名の変換点を 2 つに割らない**ことがここでの目的である。

### 2. マイグレーションは EF が生成しない部分が**本体**だった

`dotnet ef migrations add` が出したのは `Tags.UpdatedAt` 列の追加だけである。
**`Tags` 列の型は変わらない**（前後とも `jsonb` の配列で、変わるのは中身だけ）ので、EF は差分を検出しない。
**データ移行の SQL は全て手書きである。**

**scaffold の既定値をそのまま使わなかった。** `AddColumn` の既定は `0001-01-01` で、
**未改名のタグが「西暦 1 年に改名された」ように見える**。`defaultValueSql: "now()"` にしたうえで、
既存行を `CreatedAt` と同じ値へ揃えた。

### 3. `btrim` の既定では C# の `Trim()` と揃わない

`Tag.Normalize` は `string.Trim()`（`char.IsWhiteSpace` の集合）だが、**`btrim(x)` の既定は半角空白だけ**である。
揃えないと、移行で登録した名前と実行時に C# が正規化した名前が食い違い、
**辞書に在るのに「辞書に無いタグです」と 400 になる**。落とす文字の集合を明示した。

**そのとき `\v` を使ってはならない。** PostgreSQL の `E''` が解釈する短縮形は `\b \f \n \r \t` だけで、
**`\v` は素の `v` になる**——**タグ名から文字 `v` を削る**という、気づきにくい壊れ方をする。
制御文字はすべて `\uXXXX` で書いた。

### 4. 改名の再発行は「そのタグを使っている文書だけ」に絞った

全文書を流すと**辞書の 1 語の変更で索引全体が再構築され**、規模に比例して費用が出る。
応答に `republishedDocuments` を添えた——Qdrant / Wiki.js の反映は非同期なので、
「0 件だった」と「まだ届いていない」を管理者が切り分けられる。

### 5. ★ **母集合の引き漏らし** —— 統合テスト 2 件が CI で落ちた

**着手時に引いた「触るもの」の表は、テストを「＋ それぞれのテスト」と一括りにしていた。**
そこが漏れた。**CI（実 Docker）の初回実走で 2 件が 400 で落ちた**:

| 落ちたテスト | 何を送っていたか |
| --- | --- |
| `DocumentCrudTests.CreateDocument_ThenGet_ReturnsDocument` | `tags = ["test", "integration"]` |
| `DocumentVersioningTests.CreateUpdate_BuildsVersionHistory` | `tags = ["v1"] / ["v2"] / ["v3"]` |

どちらも**辞書に登録していない名前**であり、#635 が入れた「辞書に無い名前は 400」
（SC-05・[[IADR-0153]] 決定 5）に**正しく弾かれている**。実装ではなくテストの側の追随漏れである。

**なぜ手元で気づけなかったか。2 つ重なっている。**

1. **型エラーとして現れない。** 単体テストは `Document.Create(..., List<Guid>)` を直接呼ぶので
   `string` → `Guid` のコンパイルエラーで全件炙り出せた（48 件）。
   **統合テストはタグ名を HTTP 越しの JSON 文字列として送る**ので、**コンパイルは通り、実行時にだけ落ちる。**
2. **手元では skip される。** `[DockerFact]` は Docker が無いと skip なので、
   **ローカルの `dotnet test` は緑のままだった**（本仕様書の検証記録にも「skip される」と書いてあった）。

**教訓: 「コンパイルエラーで全部出た」を母集合の証拠にしない。**
型が変わったとき、**型検査に掛からない経路（HTTP・JSON・生 SQL・設定ファイル）を別途引くこと。**
[[IADR-0141]] 決定 1 の規則 3「拡張子で絞らない」と同じ話が、**言語の層**でも起きる。

**ただし規則としては足さない。同型 1 回目だからである**
（`CLAUDE.md`「検査器・規約の追加は同型の事故が 2 回起きたら」。1 回目は記録に留める）。
**次に同じ型を踏んだら、`.claude/rules/traceability.md` の母集合の表へ 7 番目の規則として足すこと。**

**是正**: 両テストとも辞書へ先に登録してから文書を作る形にした（共有 DB で他テストと競合しても
落ちないよう、重複時の 409 も許容する）。**引き直した結果、他に該当は無い**——
残る `tags` の指定はすべて空配列である（`DocumentCrudTests` 4 箇所・`DocumentVersioningTests` 2 箇所。実測）。

### 6. ［残件］改名・削除に **BFF 口が無い**

#634 の `POST /tags` と同じく、口は DocumentService 側にだけ在る（`/bff/attribute-values` は読み取り専用）。
**SC-09 の画面から改名・削除を操作するには BFF の書き込み口が要る。**
#542 の射程外なので **#640 へ起票した**（[[IADR-0153]] の残件へも記録した）。

## 検証記録（実測）

**走査基準: develop `040edd6` ＋ 本ブランチ。**

| コマンド | 結果 |
| --- | --- |
| `dotnet build knowledge/backend/backend.slnx` | `Build succeeded.` |
| `dotnet test knowledge/backend/backend.slnx` | 全 11 アセンブリ Passed（`DocumentService.Api.Tests` は **92 件**。着手時 75 件 ＋ #635 の 17 件） |
| `dotnet format knowledge/backend/backend.slnx --verify-no-changes` | 差分なし |
| `node scripts/check-doc-links.js` | `OK: 495 件の Markdown に破損した相対リンクはありません。` |
| `pnpm run codegen` | 再生成差分あり（`bff.schemas.ts` の説明文。コミット済み） |

### 実走できなかったもの（**理由つき**）

- **`TagIdentityMigrationTests`（`[DockerFact]` 2 件）は本環境で skip される**——Docker が無い。
  **データ移行の SQL は実 PostgreSQL でしか走らない**（EF InMemory はマイグレーションを実行しない）ので、
  **本 PR の中核部分は CI 側の実走が初回検証になる**。#636 の一意インデックスと同じ状況である。
  実測（手元）: `Knowledge.IntegrationTests` は Passed 21 / **Skipped 22** / Total 43 で、
  skip の中に `Migration_RewritesDisplayNamesToIdentifiers` と `Migration_Down_RestoresDisplayNames` が
  discovery されていることを確認した（**「書いたが discovery されていない」ではない**ことを分ける）。

  **［CI の実走で確認済み］移行の 2 件はどちらも通った。**
  **数値は最終コミット（`5259b3f`）のジョブログから取った**
  （`build-and-test` run `31314685753` / job `93247918743` = success）:
  `Migration_Down_RestoresDisplayNames` 382ms / `Migration_RewritesDisplayNamesToIdentifiers` 356ms、
  **`Knowledge.IntegrationTests` は 43 件すべて Passed**（実 PostgreSQL 16）。

  **初回の実走（`322970c`）では 41/43 で 2 件が落ちた**——上記「実装中に決めたこと 5」を参照。
  **手元で緑・CI で赤という差そのものが、この issue の検証の要点である。**

  **［記録］「実装が同じでも、実走の記録が最新コミットに無いなら根拠にならない。」**
  従前ここには 1 つ前のコミット（`573158e`）の数値を書いていた。実装差分は無かったが、
  **AI レビューが 🟡 で「最新コミットで確認せよ」と指摘したのは正しい**ので取り直した。

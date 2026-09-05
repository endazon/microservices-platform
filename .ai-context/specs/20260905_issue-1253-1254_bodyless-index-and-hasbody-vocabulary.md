---
title: 本文なし文書の索引にパス・データソース名を載せ、本文の有無の語彙を hasBody へ寄せる（#1253 / #1254）
type: spec
status: done
related_ids: [FR-02, FR-03, FR-12, UC-04, UC-06, SC-02, SC-03, SC-07, ADR-0070, IADR-0122, IADR-0149, IADR-0356, IADR-0358, IADR-0388]
author: Claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - "ADR-0070 決定 3（テキスト層を持たない PDF は「本文なし」で確定させる）"
  - "ADR-0070 決定 4（本文なしの文書はカタログに載せ、タイトル・パス・データソース・更新日時などのメタデータで検索可能にする）"
---

# #1253 / #1254: 本文なしの索引メタデータと「本文の有無」の語彙統一

## 起点となる計画書（トレーサビリティ）

- 機能要求: `FR-02`（取り込み）・`FR-03`（ハイブリッド検索）・`FR-12`（正規化変換）
- ユースケース: `UC-04`（取り込み）・`UC-06`（正規化）
- 画面: `SC-02`（検索結果）・`SC-03`（文書詳細）・`SC-07`（変換ジョブ）
- 計画 ADR: `ADR-0070` 決定 3・決定 4
- 先行記録: `IADR-0356`（#1192。`bodyAbsent` の新設）／`IADR-0358`（#1193。メタデータ点 1 つでの索引）

## 1. なぜ 1 つの PR か

両 issue とも `Knowledge.Contracts/Events/{DocumentNormalized,DocumentUpdated}.cs` と
ConversionService の発行口を触る。両 issue の「宣言ファイル領域」が交差しており、
issue 本文が「直列化する」と明示している。**直列化＝同一 PR で連続して行う**のが
最も安いので 1 本にまとめた（IADR-0116 規約 1 の例外側ではなく、交差による直列化である）。

## 2. 事象（issue の要約）

### #1254

1. `DocumentNormalized.BodyAbsent` は**読み手がゼロ**（write-only の契約項目）。
2. 同じ概念の語彙が割れている ——
   変換側 `bodyAbsent`（`true`＝本文なし・既定 `false`）と
   検索側 `hasBody` / `has_body`（`true`＝本文あり・既定 `true`）。
   既定値の**向き**は両方とも「本文あり」で一致するが、**名前と極性が反転**している。

### #1253

`ADR-0070` 決定 4 は本文なしの文書を「タイトル・**パス**・**データソース**・更新日時などの
メタデータ」で検索に載せると定めるが、`MetadataIndexText.Build(title, tags)` は
**題名とタグだけ**を索引テキストにしている。`DocumentNormalized` も `DocumentUpdated` も
パスとデータソース名を運ばないため、載せる材料が届いていない。

## 3. 母集合の引き方（規則 9・10）

「本文の有無」の綴りは**誤りの側の文字列**（`BodyAbsent` / `bodyAbsent` / `body_absent`）で
追跡下の全ファイルを走査して引いた。記憶で挙げていない。

```console
$ git rev-parse --is-shallow-repository
false
$ git grep -lIn -e BodyAbsent -e bodyAbsent -e body_absent -- . | wc -l
```

除外したもの（理由つき）:

| 対象 | 除外理由 |
| --- | --- |
| `.ai-context/adr/IADR-0356_*.md` | 凍結記録。本文プロズを後から書き換えない（`traceability.repo.md` §凍結の射程） |
| `.ai-context/specs/20260903_issue-1192_*.md` | 同上（`.ai-context/specs/` は経過追記が可だが、本件は追記に値する新事実を足さない） |
| `Migrations/20260903093103_AddBodyAbsentMarker.*` | 履歴として実在したマイグレーション。名前は事実であり改名できない |
| `scripts/contract-schema-baseline.json` の `memberRemoved:` キー | **旧綴りを指すのが仕事**の承認記録 |
| `docs/` の対応表・日付つき追記 | **旧綴りを読み替えるための記述**。消すと読み替えができなくなる |

規則 10（是正で新たに誤りになる自分の記述を引き直す）として、
本 PR で `hasBody` に寄せたあと `hasBody` 側でも全走査し、
`docs/data/conversion-job.md` の対応表が唯一の読み替え正本になっていることを確かめる。

## 4. 決定（詳細は IADR-0388）

1. **語彙は肯定形 `hasBody` へ寄せ、極性を反転する**（否定形の変数は読み違えを生む）。
2. **#1254 は案 A（読み手を作る）**を採る。`DocumentNormalizedConsumer` が
   `DocumentNormalized.HasBody` を台帳（`Document.HasBody`）へ保持し、`DocumentUpdated` へ写し、
   SC-03 の「本文なし（原本を参照）」の材料にする。
3. **索引側（IngestionService）は引き続きチャンク 0 件で判定する**（IADR-0358 決定 1 の理由は残る）。
   **ただし両者が食い違ったら警告を残す**（片方だけ改名されて静かに割れる形を検知する）。
4. **`RawDocumentFetched.SourceName` → `DocumentNormalized.{OriginalPath,DataSourceName}` →
   `DocumentUpdated.{OriginalPath,DataSourceName}` → `MetadataIndexText`** の経路で
   パスとデータソース名を索引テキストへ載せる。すべて**末尾・既定値つき**（IADR-0122 決定 2）。
5. **本文ありのチャンクのペイロードには載せない**（非対称を明示的に固定する）。

## 5. 影響範囲

| ファイル | 変更 |
| --- | --- |
| `Knowledge.Contracts/Dtos/ConversionJobDto.cs` | `BodyAbsent` → `HasBody = true`（破壊的。baseline で承認） |
| `Knowledge.Contracts/Dtos/DocumentDto.cs` | `HasBody = true` を足す（SC-03 の材料） |
| `Knowledge.Contracts/Events/DocumentNormalized.cs` | `BodyAbsent` → `HasBody = true`、`OriginalPath` / `DataSourceName` を末尾へ |
| `Knowledge.Contracts/Events/DocumentUpdated.cs` | `HasBody` / `OriginalPath` / `DataSourceName` を末尾へ |
| `Knowledge.Contracts/Events/RawDocumentFetched.cs` | `SourceName` を末尾へ |
| `DataSourceService/.../DataSourceSyncService.cs` | `source.Name` を発行へ足す |
| `ConversionService/**` | `bodyAbsent` → `hasBody` の一括改名 ＋ パス・DS 名の中継 ＋ 列改名マイグレーション |
| `DocumentService/Domain/Document.cs` ＋ 永続化 ＋ マイグレーション | `HasBody` / `OriginalPath` / `DataSourceName` の列 |
| `DocumentService/.../DocumentNormalizedConsumer.cs`・`DocumentEndpoints.cs`・`IDocumentUpdatedPublisher` | 3 項目の中継 |
| `IngestionService/Domain/MetadataIndexText.cs` | パス・DS 名を索引テキストへ |
| `IngestionService/.../DocumentUpdatedConsumer.cs` | 索引テキストへの受け渡し ＋ **食い違いの警告** |
| `knowledge/frontend/src/features/sc03-document/**` | 「本文なし（原本を参照）」の表示 |
| `knowledge/frontend/src/features/sc07-conversions/**` | `hasBody` 読み ＋ 導出関数の改名 |
| `docs/api/openapi.yaml` ＋ `platform/frontend/src/lib/api/generated/**` | 契約の追随（生成物） |
| `docs/{data,functional,screens,tests}/**` | 対応表・受け入れ基準の追随 |

## 6. 受け入れ基準 → テストの写像

| # | 基準 | テスト |
| --- | --- | --- |
| A-1 | 本文なし文書がパスの語で当たる | `MetadataIndexTextTests` ＋ `DocumentUpdatedConsumerBodylessTests` |
| A-2 | データソース名で当たる | 同上 |
| A-3 | 題名で当たる（**陽性対照**。#1193 の獲得物を壊さない） | `MetadataIndexTextTests` |
| A-4 | 索引に無い語で 0 件（**陰性対照**） | `MetadataIndexTextTests` |
| A-5 | **変異試験**: パス・DS 名を `Build` から外すと A-1 / A-2 が落ち、A-3 / A-4 は通る | 手動の変異 ＋ 出力を PR 本文へ |
| A-6 | 旧発行者（パスを運ばない `DocumentUpdated`）で例外にならない | `DocumentUpdatedConsumerBodylessTests` |
| A-7 | 本文ありの文書はパスで当たらない（非対称の固定） | `QdrantIngestionVectorStoreTests` の本文チャンク経路 |
| B-1 | `DocumentNormalized.HasBody` に読み手が 1 つ以上ある | `DocumentNormalizedConsumer` ＋ `NormalizedBodyPresenceTests` |
| B-2 | SC-03 に「本文なし（原本を参照）」が出る（SC-02 と同じ文言） | `DocumentDetailPage.test.tsx` |
| B-3 | 食い違い（`HasBody=true` なのにチャンク 0 件 / その逆）で警告 | `DocumentUpdatedConsumerBodylessTests` |
| B-4 | `check-contract-schema.js` が通る | baseline の承認記録 |

## 7. 結果

「9. 検証」に実測を残す。

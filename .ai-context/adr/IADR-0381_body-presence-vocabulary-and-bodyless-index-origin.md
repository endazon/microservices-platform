---
title: IADR-0381 本文の有無は肯定形 `hasBody` に一本化し、本文なしの索引テキストへ所在とデータソース名を載せる
type: impl-adr
status: Accepted
related_ids:
  - FR-02
  - FR-03
  - FR-12
  - UC-04
  - UC-06
  - SC-02
  - SC-03
  - SC-07
  - ADR-0070
  - IADR-0122
  - IADR-0149
  - IADR-0153
  - IADR-0356
  - IADR-0358
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - "ADR-0070 決定 3（テキスト層を持たない PDF は失敗ではなく「本文なし」で確定させる）"
  - "ADR-0070 決定 4（本文なしの文書はカタログに載せ、タイトル・パス・データソース・更新日時などのメタデータで FR-03 の検索に載せる。SC-02 には『本文なし（原本を参照）』を示す）"
---

# IADR-0381: 本文の有無の語彙と、本文なし文書の索引の材料

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: 実装（#1254 / #1253）

## 起点・関連

- 関連する計画書 ID: FR-02 / FR-03 / FR-12 / UC-04 / UC-06 / SC-02 / SC-03 / SC-07 / ADR-0070
- 関連する実装仕様書: `.ai-context/specs/20260905_issue-1253-1254_bodyless-index-and-hasbody-vocabulary.md`
- 先行: `IADR-0356`（#1192。「本文なしで完了」の新設）／`IADR-0358`（#1193。メタデータ点 1 つでの索引）／
  `IADR-0122`（契約の非破壊追加は末尾・既定値つき）／`IADR-0149`（索引ペイロードの表現）／
  `IADR-0153`（正本は識別子・射影は表示名を運んでよい）

## コンテキストと課題

フェーズ末監査（バッチ②）が、#1192 と #1193 の着地の間に 2 つの穴を見つけた。

### 課題 1（#1254）—— `DocumentNormalized.BodyAbsent` に読み手がゼロ

#1192 は `DocumentNormalized` へ `BodyAbsent` を足し、「後続（カタログ・索引）が
本文由来のチャンクを作らず、メタデータで検索に載せる判断にこれを使う」と注記した。
しかし #1193 は**この項目に依存しない**判断を採り（`IADR-0358` 決定 1。チャンクが 0 件に
なったときが本文なし）、結果として**この項目を読む箇所が 1 つも無い**契約項目が残った。

`write-only の契約項目は、静かに壊れる。` 誰も読まないので、発行側が止めても・値を反転しても
テストは緑のままである。加えて `DocumentNormalized.cs` の注記が**実装と食い違ったまま**残った。

### 課題 2（#1254）—— 同じ概念の綴りと極性が割れている

| 面 | 項目 | 極性・既定 |
| --- | --- | --- |
| 変換（SC-07）`ConversionJobDto` | `bodyAbsent` | `true`＝本文なし。既定 `false` |
| イベント `DocumentNormalized` | `BodyAbsent` | 同上。**読み手なし** |
| 索引 / 検索（SC-02）`SearchResultDto` | `has_body` / `hasBody` | `true`＝本文あり。既定 `true` |

既定値の**意味**はどちらも「本文あり」で一致しているが、**名前と極性が反転**している。
画面・契約・運用の道具（Qdrant を直接読む scroll 等）で「`bodyAbsent` を見るのか `hasBody` を
見るのか」を毎回引き直すことになっていた。

### 課題 3（#1253）—— 索引テキストが題名でしか当たらない

ADR-0070 決定 4 は「タイトル・**パス**・**データソース**・更新日時などのメタデータで検索に載せる」
と定め、#1193 の受け入れ基準 2 は「その**タイトル・パス・データソース名**で検索を行うと結果に現れる」
だった。しかし `MetadataIndexText.Build(title, tags)` は題名とタグだけを索引テキストにしており、
**受け入れ基準 2 は題名でしか満たされていなかった。**

届いていなかったのは契約の側である。`RawDocumentFetched.OriginalPath` は ConversionService が
`Path.GetFileNameWithoutExtension` で**題名へ畳んだ時点で捨てられ**、データソースの表示名は
そもそもどのイベントにも載っていなかった。`IADR-0358` 決定 2 はこの事実を記録し
「フォローアップ 1: 契約変更。別 issue」と書いたが、**その別 issue は起票されていなかった。**

## 検討した選択肢

### 語彙 —— 案 A: 否定形 `bodyAbsent` へ寄せる／案 B: 肯定形 `hasBody` へ寄せる／案 C: 寄せない

**案 B を採る。**

- 案 C（寄せない・対応表だけ置く）は、**引き直しのコストを恒久化する**。#1215 が指摘した
  「生産側と消費側の齟齬」と同型の温床であり、対応表は「読む人が読んだときだけ」効く。
- 案 A（否定形へ寄せる）は、**否定形の変数が読み違えを生む**。`if (!bodyAbsent)` は二重否定で、
  レビューで向きを取り違える。加えて索引ペイロードのキー `has_body` は**既存の点が持っており**
  （`IADR-0358` 決定 3。欠落＝本文あり）、こちらを反転すると**既存の点の読みが全部裏返る**。
  移行できない側へ寄せる選択肢ではない。
- 案 B は変換側 3 面（列・DTO・イベント）を動かすが、**いずれも移行可能**である
  （台帳は 1 本のマイグレーション、契約は同一 PR 内の全消費者、SPA は導出関数 1 つ）。

### `BodyAbsent` の始末 —— 案 D: 読み手を作る／案 E: 項目を撤去する

**案 D を採る。** 案 E（撤去）は「読み手が無い項目を残さない」を最短で満たすが、
**SC-03（文書詳細）が本文なしの文書を区別する材料を今も持っていない**（実測: `sc03-document/`
を `hasBody|bodyAbsent|本文なし` で走査して 0 件）。ADR-0070 決定 4 は SC-02 に
「本文なし（原本を参照）」を示すと定めており、**同じ文書を開いた詳細画面で空の本文が出る**のは
その裁定の意図に反する。**捨てるより使うほうが計画に近い。**

### 索引側の判定 —— 案 F: 契約の値で判定する／案 G: チャンク 0 件のまま・食い違いだけ鳴らす

**案 G を採る。** `IADR-0358` 決定 1 の理由（上流の状態名に依存すると、改名や別経路で
静かに漏れる）は案 D を採っても残る。しかし**二重化した情報のどちらかだけが変わったとき、
従来は誰も気づかないまま索引の中身が割れていた** —— 観測だけを足す。

### 本文ありのチャンクへの所在 —— 案 H: 載せる／案 I: 載せない

**案 I を採る。** 詳細は決定 5。

## 決定

### 決定 1 — 「本文の有無」は肯定形 `hasBody`（`true`＝本文あり・既定 `true`）に一本化する

変換側の 3 面を**改名し、極性を反転する**。

| 面 | 旧 | 新 |
| --- | --- | --- |
| 台帳 `ConversionJobs` | 列 `BodyAbsent`（既定 `false`） | 列 `HasBody`（既定 `true`） |
| 契約 `ConversionJobDto` | `bodyAbsent`（既定 `false`） | `hasBody`（既定 `true`） |
| イベント `DocumentNormalized` | `BodyAbsent`（既定 `false`） | `HasBody`（既定 `true`） |

**読み替えは「旧 `bodyAbsent == true` ⟺ 新 `hasBody == false`」である。** 読み替えの正本は
`docs/data/conversion-job.md` §本文の有無の語彙 に 1 つだけ置く（他所へ複写しない）。

**契約の破壊的変更として扱う。** `ConversionJobDto.BodyAbsent` /
`DocumentNormalized.BodyAbsent` の削除は `check-contract-schema.js` が `memberRemoved` として
分類するので、`scripts/contract-schema-baseline.json` に理由つきの承認を置いた。安全性の根拠は 4 つ:

1. **既定値の意味は両方の綴りで「本文あり」**なので、項目を持たない旧発行者のメッセージ・応答の
   読みは変わらない。
2. 読み手はリポジトリ内に SC-07 の SPA 1 箇所だけで、同一 PR で `hasBody === false` へ移した
   （`src/` と `docs/` を旧綴りで全走査して確認した）。
3. 台帳の列も同一 PR の `RenameBodyAbsentToHasBody` で**足す → `NOT` で写す → 落とす**の順に
   移し、既存行の内訳を保つ。🔴 **EF が既定で吐く「落として足す」をそのまま採ると内訳が全部消える。**
4. 非破壊で済ませる案（両方を残して片方を deprecated にする）は、**割れた語彙をもう 1 つ増やして
   問題そのものを悪化させる**ため採らない。

**索引ペイロードのキー `has_body` は動かさない**（既に肯定形であり、既存の点が持っている）。

🔴 **SPA の導出関数 `isBodyAbsent(job)` は否定形の名前のまま残す。** これは矛盾ではない ——
画面に出すのは「本文なしで完了」という**否定の言明そのもの**であり、この関数はその言明を
**1 箇所に閉じ込めるための壁**である。契約側が肯定形に揃った以上、`!hasBody` を画面の各所へ
散らさないほうが読み違えは減る。**契約の綴りと画面の言明は別のものである。**

### 決定 2 — `DocumentNormalized.HasBody` の読み手はカタログ（`DocumentNormalizedConsumer`）である

台帳へ `Document.HasBody`（既定 `true`）として保持し、`DocumentUpdated.HasBody` へ写し、
`DocumentDto.hasBody` として SC-03 へ出す。SC-03 は `hasBody === false` のとき本文の位置へ
**「本文なし（原本を参照）」**を出す —— **SC-02 と同じ文言・同じ導出**である。

**空の本文をそのまま描かない。** 空の `pre` は「読み込みに失敗した」と読み違える形であり、
`本文は利用できません。`（取得失敗）と**区別できない**。

台帳に持つ理由は、`DocumentUpdated` が**変換経路以外からも発行される**からである
（属性編集・タグ改名・公開状態の変更で 8 箇所）。イベントを右から左へ流すだけにすると、
利用者が属性を 1 つ直した瞬間に下流の「本文なし」が消える。

### 決定 3 — 索引の判定は変えない。食い違ったら警告を残す

`IngestionService` は今までどおり**チャンク 0 件**で本文なしを判定する（`IADR-0358` 決定 1）。
足すのは観測だけである:

- `hasBody == true` なのにチャンク 0 件 → 警告（本文が空／読めない、または上流の標識が古い）
- `hasBody == false` なのにチャンクが在る → 警告（標識と本文が食い違う）

⚠️ 前者は**変換以外の経路で空本文が投入された**ときにも鳴る。これは異常ではないが、
**黙って本文なし扱いにするのは異常**なので同じ口で鳴らす。

### 決定 4 — 所在とデータソース名を索引テキストへ載せる

経路は `RawDocumentFetched.SourceName` → `DocumentNormalized.{OriginalPath, DataSourceName}`
→ 台帳 `Document.{OriginalPath, DataSourceName}` → `DocumentUpdated.{OriginalPath, DataSourceName}`
→ `MetadataIndexText.Build`。**すべて末尾・既定値つき**（`IADR-0122` 決定 2。旧発行元からの
メッセージは `null` として読める）で、`check-contract-schema.js` は非破壊の追加と分類する。

索引テキストの作り方:

- 🔴 **パスは区切り文字（`/` と `\`）を空白へ開いてから入れる。** 開かないと
  `/共有/経理/2026年度経費.pdf` は 1 つの長い語として索引され、「経理」でも「共有」でも当たらない。
- **拡張子は落とす。** `pdf` で全 PDF が並ぶのは絞り込みの役に立たない。
  🔴 判定は **ASCII 英数字に限る** —— `char.IsLetterOrDigit` は CJK も真を返すので、
  それで判定すると `v1.2.仕様` の「仕様」が拡張子と見なされて消える（実測で落とした）。
- **題名と重なる語は 2 度並べない。** 題名は原本のファイル名（拡張子なし）なので、
  パスの最終要素とほぼ必ず重なる。
- **ABAC 属性の値は入れない**（`IADR-0358` 決定 2 の線は動かさない）。属性は**絞る**ためのものである。
- **更新日時も入れない**（`IADR-0149`。ペイロードが既に持つ）。

🔴 **既に索引済みの文書には遡及しない。** 台帳が所在を知らないので再索引しても足す材料が無く、
所在で当たるようになるのは**次の同期で再取得された後**である。**backfill スクリプトは書かない** ——
再同期そのものが冪等だからである: 文書 ID は `(sourceId, path)` から決定的に導かれ
（`DeterministicGuid.ForDocument`）、メタデータ点の ID も文書 ID から決定的に導かれ
（`ChunkId.DeriveMetadata`）、取り込みは全コレクションから削除してから upsert する。
**何度流しても点は増えず、同じ 1 点が上書きされる。**

⚠️ データソース名は**表示名の複写**である（`IADR-0153` 決定 1 の「複写しない」はタグの正本の話）。
射影＝索引テキストは人が読む面であり、改名の追随義務は無い（次の同期で上書きされる）。

### 決定 5 — 本文ありのチャンクには所在もデータソース名も載せない（意図した非対称）

載せると全文側にパスの断片が当たり、**「本文に書いてある語で当たった」と「置き場所の名前で
当たった」が抜粋から区別できなくなる**。本文なしの点は抜粋が空（`IADR-0358` 決定 4）なので、
その混同が起きない。

**「決めていない」を残さない**ため、非対称は試験で固定する
（`Consumer_ShouldNotPutPathIntoBodyChunks`）。将来これを覆すなら、抜粋に「所在で当たった」を
示す表現が先に要る。

## 結果

### 実測（変異試験）

索引テキストからパスとデータソース名を外すと、**新しい陽性が落ち、陽性対照・陰性対照・
旧発行元の 3 本は通ったまま**である（「何を入れても当たる」実装では緑にならない）。

```console
$ dotnet test src/knowledge/backend/Services/IngestionService/Tests/IngestionService.Tests.csproj
  # MUTATION: Build() から PathSegments(originalPath) と dataSourceName を外した状態
  MetadataIndexTextTests.Build_ShouldIncludePathSegmentsAndDataSourceName [FAIL]
  MetadataIndexTextTests.Build_ShouldSplitWindowsSeparators [FAIL]
  MetadataIndexTextTests.Build_ShouldDropExtension_ButKeepDottedFolderNames [FAIL]
  MetadataIndexTextTests.Build_ShouldNotRepeatTheTitleThatAlsoAppearsInThePath [FAIL]
  DocumentUpdatedConsumerTests.Consumer_ShouldIndexPathAndDataSourceName_WhenBodyIsEmpty [FAIL]
  失敗!   -失敗:     5、合格:    60、スキップ:     0、合計:    65

  # 変異を戻した状態
  成功!   -失敗:     0、合格:    65、スキップ:     0、合計:    65
```

落ちなかった 3 本（`Build_ShouldStillContainTitle_WhenPathIsAdded` / 
`Build_ShouldNotContainWordsThatWereNotGiven` / 
`Consumer_ShouldIndexTitleAndTagsOnly_WhenPublisherDoesNotCarryOrigin`）は
**不変条件を測っているので落ちないのが正しい**。

### 測っていないこと（申し送り）

- 🔴 **稼働 k3s での実測は行っていない。** 実際にテキスト層の無い PDF を取り込み直して
  「パスの語で検索して出る」ところまでは見ていない（本 PR は単体・結合まで）。
  既存の索引済み文書は次の同期まで題名だけのままである（決定 4）。
- 検索側（`RetrievalService`）は 1 行も変えていない。索引テキストが太るだけであり、
  復元・抜粋の射影（`IADR-0358` 決定 4）はそのままである。
- 索引テキストが長くなることの埋め込みコスト・検索品質への影響は測っていない
  （メタデータ点は 1 文書 1 点であり、母数は本文チャンクに比べて小さい）。

### フォローアップ

1. 再同期後に稼働クラスタで「パス・データソース名で当たる」を実測する（別 issue）。
2. SC-02 の抜粋に「所在で当たった」を示す表現が要るようになったら、決定 5 を改定する。

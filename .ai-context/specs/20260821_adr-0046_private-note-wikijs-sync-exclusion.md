---
title: 作業仕様書 — ADR-0046（個人資料を Wiki.js へ同期しない）の実装可否を再検証する
type: spec
status: done
related_ids:
  - FR-13
  - FR-19
  - FR-20
  - UC-07
  - UC-11
  - SC-19
author: claude
created: 2026-08-21
updated: 2026-08-21
plan_refs:
  - "ADR-0046（個人資料は Wiki.js へ同期せず、本文編集は Obsidian 経路に限る。Accepted）"
  - "ADR-0036（所有者ベースの裁量制御。D-12 語彙 / §未確定事項 1）"
  - "ADR-0037（Obsidian 連携の同期方式。着手可否の注記）"
  - "ADR-0011（Wiki エンジン選定）"
  - "planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-19 / FR-20・未裁定リスト）"
  - "planning:projects/microservices-platform/05_screens/01_screens.md（SC-19・§233）"
  - "planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md"
  - "planning:docs/glossary.md（用語「個人資料」「機密区分」）"
related_adrs:
  - IADR-0021
  - IADR-0020
  - IADR-0119
  - IADR-0142
  - IADR-0179
  - IADR-0199
issue: "#449"
related_issues:
  - "#451"
  - "#602"
---

# 作業仕様書: ADR-0046（個人資料を Wiki.js へ同期しない）の実装可否を再検証する

> **結論を先に置く。実装へ着手していない。** ADR-0046 D-01 の実装には
> **「文書が個人資料（`private-note`）であること」をバックエンドが判定する軸**が要るが、
> その軸（ABAC 属性値としての `private-note` の表記）は**計画側が明示的に「未裁定」としている**
> （ADR-0036 §未確定事項 1・02_requirements §未裁定リスト・用語集）。
> 実装が軸を決めると**計画が保留した語彙を実装が事実上定義する**ことになるため、
> `.claude/rules/traceability.repo.md`（「無いことは『実装側で作ってよい』ではない」・IADR-0179 決定 2）
> と CLAUDE.md 手順 2（「曖昧な場合は実装を止め、人間に確認する」）に従い、**裁定を求めて止めた。**

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **`ADR-0046`（Accepted・2026-08-15）** —— D-01「`private-note` は WikiService の
  push 対象に含めない。Wiki.js 上に個人資料のページは作られない」／ D-02「SC-19 は本文編集導線を
  持たない」／ D-06「閲覧側の個人スコープには 3 部品が要る（いずれも未実装）」
- 前提として引く計画 ADR: `ADR-0036`（所有者ベースの裁量制御）／ `ADR-0037`（Obsidian 同期方式）／
  `ADR-0011`（Wiki エンジン）
- 実装 ADR: `IADR-0020`（WikiService を同期・統合・ABAC ゲートウェイへ縮退）／
  `IADR-0021`（Wiki.js への同期は GraphQL push・認可属性は Wiki.js へ持ち込まない）／
  `IADR-0119` ＋ `IADR-0142`（FR-19 / FR-20 の着手条件）
- 起点 issue: **#449**（再実装の大玉）。個人資料そのものの実装担当は **#451**
  （`IADR-0199` フォローアップ 5 が「#451 は個人資料〔`private-note`〕に閉じている」と明記）

## 着手前の再検証（母集合と実測）

`.claude/rules/traceability.repo.md` §是正・追随の母集合の取り方（規則 1〜10）に従い、
**issue 本文の記述を転記せず、誤りの側の文字列で自分で引き直した。** 実施日 **2026-08-21**。

### 引いたコマンドと結果

| # | 軸 | コマンド | 結果 |
| --- | --- | --- | --- |
| A | **誤りの側**（`private-note` のあらゆる表記。規則 1・2・3・4） | `git grep -n -i -E "private[-_ ]?note" -- . ':(exclude)src/ai-stock-trading'` | **36 行 / 10 ディレクトリ**。うち `src/` は **17 行** |
| A' | 同上を `src/` に限る | `git grep -n -i -E "private[-_ ]?note" -- 'src/' ':(exclude)src/ai-stock-trading'` | **17 行。すべてフロントエンド。**内訳は ①`src/knowledge/frontend/src/features/abac/confidentiality.ts:16` の**コメント 1 行**、②`src/platform/frontend/src/foundation/notifications/` の **16 行**（`private-note-purge-weekly` / `-imminent` / `-done` ＝ **FR-22 の通知種別**であり、文書区分ではない別トークン） |
| B | **識別軸になり得る語**（規則 2・5） | `git grep -n -E "data_class\|doc_class\|document_class\|noteKind\|note_kind\|DocumentClass\|PrivateNote" -- 'src/' ':(exclude)src/ai-stock-trading'` | **0 件** |
| C | DTO 側の「個人資料」表現 | `git grep -n "個人資料" -- 'src/knowledge/backend/' 'src/platform/backend/'` | **1 件**。`Knowledge.Contracts/Dtos/SearchResultDto.cs:44` の**コメント**（`restricted` の表示名が「極秘」でない理由の説明）。**`private-note` という文字列は含まない** |
| D | 所有者ベース判定の足場（`owner`） | `git grep -n -w -E "\"owner\"\|OwnerKey" -- 'src/knowledge/backend/' 'src/platform/backend/'` | `DataSourceService` の**データソース既定属性**（`IADR-0019` / `IADR-0199` の予約値 `system`）のみ。**文書の所有者判定は 0 件** |
| E | WikiService 内の除外の有無 | `git grep -n -i -E "private\|personal" -- 'src/knowledge/backend/Services/WikiService/'` | 出現はすべて Wiki.js の `isPrivate`（多層防御の粗粒度フラグ）・C# の `private` 修飾子・`PrivateAssets`。**個人資料の除外は 0 件** |

### 除外したものと理由

- **`src/ai-stock-trading`**（submodule）: 別リポジトリであり本リポジトリの規約対象外。
  `traceability.repo.md` が検査対象から外している範囲と同じ。
- **`private-note-purge-*`（16 行）**: 文字列としては A に掛かるが、**FR-22 の通知種別**
  （`IADR-0215` / `docs/api/BFF_notifications.md` の列挙 5 値）であり、
  **「文書が個人資料であること」を表す値ではない。** 黙って落とさず、ここに理由を書いて除外する。
- **`docs/` ・`.ai-context/` の出現（19 行）**: 文書側の言及であり、実装の判断軸ではない。
  ただし後述の「issue 本文との差異」の根拠として読んだ。

### 再検証の結論 —— issue 本文の前提は 2 点で成り立たない

| # | issue / 依頼文の記述 | 実測 | 判定 |
| --- | --- | --- | --- |
| 1 | 「`DocumentSyncConsumer` に `private-note` の除外が無い」 | 除外は無い（軸 E） | **成り立つ** |
| 2 | 「**機密区分の値** `private-note`」 | **計画が明示的に否定している。** 05_screens §233「**`private-note` は機密区分の値ではない。** 個人資料は『文書の所有と公開範囲の区分』であり、機密区分（どれだけ機微か）とは別の軸である。個人資料は `private-note` であり、**かつ** `confidentiality = restricted` を持つ」／用語集「機密区分」の項も同旨。機密区分の値集合は `public` / `internal` / `confidential` / `restricted` の 4 値（`ConfidentialityLevels` / `DocumentAttributes.AllowedConfidentiality`） | **成り立たない** |
| 3 | 「フロントエンドと **DTO 1 箇所**にしかない」 | DTO には `private-note` の文字列が**無い**（軸 C。あるのは「個人資料」という語のコメント 1 行）。フロント側の 16 行も**通知種別**であって文書区分ではない。**バックエンドでの出現は 0 件** | **成り立たない** |

## 実装を止めた理由（本作業の核）

### 判定軸が存在せず、作ると計画の未裁定事項を実装が決めることになる

ADR-0046 D-01 を実装するには `DocumentUpdated`（`Knowledge.Contracts/Events/DocumentUpdated.cs`。
`DocumentId` / `Title` / `Status` / `MarkdownUri` / `Attributes` / `Tags` / `UpdatedAt`）から
「この文書は個人資料である」と判定できなければならない。**その表現が計画に無い。**

- **`ADR-0036` §未確定事項 1**: 「**`private-note` 語彙の適用範囲（ABAC 属性値 / API のリソース名 /
  画面ラベル）**」を「本 ADR の範囲外であり、**別途確定が要る**」と明記する。
- **`02_requirements/01_requirements.md`**: 「あわせて次は**未裁定**であり、本書には書いていない。
  …②`private-note` 語彙の適用範囲のうち**属性値と API のリソース名**（**画面ラベルは「個人資料」で
  統一と裁定済み**）」。
- **用語集**（`planning/docs/glossary.md` 用語「個人資料」）: 「**ABAC 属性値と API リソース名としての
  表記は未確定**」。
- **`06_technical/07_abac-attribute-model.md` §文書の基本属性**: 属性表に個人資料を表す属性は無い
  （`data_class` は `personal` / `financial` / `general` ＝**個人情報**であり、`ADR-0036` D-12 が
  「二義にしない」と峻別している）。

したがって実装側が属性キー（例: `document_class` / `doc_kind` 等）と値を決めると、
**計画が利用者裁定へ回した語彙を実装が事実上確定させる**。これは
`.claude/rules/traceability.repo.md` の「**無いことは『実装側で作ってよい』ではない**」
（`IADR-0179` 決定 2）に正面から当たる。

### 代替の判定軸はいずれも誤りである

| 案 | 却下理由 |
| --- | --- |
| `confidentiality == "private-note"` で判定する | **計画が「機密区分の値ではない」と明示**（05_screens §233・用語集）。`DocumentService` の `DocumentAttributes.ValidateConfidentiality` は 4 値以外を 400 で拒否するため、この値を持つ文書はそもそも保存できない。**存在し得ない値に対する防御的実装**でもある（CLAUDE.md 禁止事項） |
| `confidentiality == "restricted"` で判定する | **除外が広がりすぎる。** `restricted` は個人資料の**既定**であって専有ではなく、組織文書も取り得る。依頼が求める「二値証明」の反対側（`private-note` 以外は従来どおり同期される）を壊す |
| `owner` の有無で判定する | `owner` は**全文書の必須属性**（07_abac-attribute-model）であり個人資料に固有ではない。加えて実測 0 件（軸 D・`IADR-0119` 追補の実測表） |

### 計画自身が「未着手であり手戻りは無い」と述べている

`ADR-0046` §実装の現状 は「**個人資料（`private-note`）の Wiki.js 同期は未着手である**（対応表に
該当 IADR は無い）。したがって**本決定による実装の手戻りは生じない**」と書く。
**D-01 は既存コードの欠陥ではなく、これから作る FR-19 の実装に課される制約**である。
その FR-19（個人資料そのもの）の担当は **#451** であり、`private-note` の表現もそこで決まる
（`IADR-0199` フォローアップ 5）。**WikiService 側だけを先に作ると、#451 が決める表現と
食い違うか、#451 の裁定を先取りするかのどちらかになる。**

## 求める裁定（計画リポジトリへ環流する内容）

1. **`private-note` の ABAC 属性値としての表記を確定してほしい**（`ADR-0036` §未確定事項 1・
   02_requirements 未裁定リスト②）。属性キーと値の両方。
2. 確定後、本作業は次の形で再開できる（**裁定前に実装しない**）。
   - `Knowledge.Contracts` に単一情報源の定数を置く（`ConfidentialityLevels`〔`SearchResultDto.cs`〕
     と同じ置き方。文字列リテラルを散らさない）
   - `DocumentSyncConsumer.Consume` の先頭付近（`status` 判定の直後・メタデータ upsert の**前**）で
     除外し、**`LogInformation` で「個人資料のため Wiki.js へ同期しなかった」ことを DocumentId 付きで
     残す**（「同期されなかった」と「同期対象が無かった」を出力で区別する）
     —— **依頼文はこの原則の出典として `IADR-0130` を挙げていたが、同 IADR の主題は
     「テスト仕様書カバレッジのラチェット」であり別件である**（`IADR-0130_test-spec-coverage-ratchet.md`。
     2026-08-21 実測）。**出典として引かない。**
   - 否定形テスト（`private-note` の文書で `RecordingWikiJsClient.Pushed` が空）＋
     反対側のテスト（`private-note` 以外は従来どおり push される）を
     `DocumentSyncConsumerTests` へ追加する。表明ライブラリは**当該プロジェクトの既存に合わせ
     `FluentAssertions` 7.2.0**（`WikiService.Api.Tests.csproj` が参照。xUnit v2 ＋ MassTransit
     TestHarness ＋ 手書きスタブ。**NSubstitute は当プロジェクトでは未使用**）
   - メタデータ（`wiki_svc` の `Pages`）を作るか否かは別途判断が要る。D-01 が禁じるのは
     **Wiki.js への push** であり、ゲートウェイの ABAC メタデータまで禁じているとは読めない。
     ただし `IADR-0020` の責務 2 は「Wiki.js 前段ゲートウェイのため」と述べており、
     push しない文書のメタデータを持つ意味は薄い。**この点も裁定材料に含める。**

## 本作業で変更したファイル

- **本ファイルのみ。** ソースコード・テスト・`docs/` は 1 行も変更していない。

## 検証（現状の記録。変更が無いためベースラインの確認）

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/knowledge/backend/backend.slnx` | `Build succeeded. 2 Warning(s) 0 Error(s)`。**警告 2 件は既存**（`Knowledge.IntegrationTests/Storage/ObjectStorageRoundTripTests.cs:48,76` の `MinioBuilder` 廃止予定 `CS0618`）。本作業に由来しない |
| `dotnet test .../WikiService.Api.Tests.csproj --no-build` | `Failed: 0, Passed: 39, Skipped: 0, Total: 39` |
| `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` | `exit=0` |
| `node scripts/check-test-traceability.js` | `exit=0` |
| `node scripts/check-backend-libraries.js` | `exit=0`（既知残件 29 件は baseline 済み） |
| `node scripts/check-contract-schema.js` | **`exit=1`。本作業に由来しない既存のズレ** —— `memberRemoved:Knowledge.Contracts.Events.IngestionCompleted.__Probe`（allowlist 承認済みだが baseline 未更新）。C# を 1 行も変更していないため本作業では発生し得ない |

**変異試験は実施していない**（除外の実装が無いため、外す対象が存在しない）。

## 参考: 現状の `DocumentSyncConsumer` の分岐（実測）

1. `Status == "archived"` → Wiki.js を unpublish + private にし、メタデータを `Archived` にして return
2. `Status != "published" && Status != "normalized"` → **黙って return**（ログ無し）
3. それ以外 → メタデータ upsert → 本文取得 → `UpsertPageAsync`（`IsPrivate = confidentiality != "public"`）

**個人資料に関する分岐は存在しない**（軸 E）。なお 2 の「黙って return」は
観測可能性の観点では改善余地があるが、**本作業の射程外**であり触っていない。

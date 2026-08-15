---
title: 取り込み経路が必須の文書属性 owner / department / lifecycle を付与していない件の是正（#516）
type: spec
status: done
related_ids:
  - FR-05
  - FR-09
  - UC-03
  - UC-04
  - SC-05
  - ADR-0036
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md"
  - "../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md"
---

# 仕様書: 取り込み経路の ABAC 必須属性（`owner` / `department` / `lifecycle`）の既定投入

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を
> 一次情報とし、本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-05**（文書属性・ABAC）／FR-09
- ユースケース（UC）: **UC-03**（文書登録）／UC-04（データソース同期）
- 画面（SC）: SC-05（文書管理。`lifecycle` の表示と公開・アーカイブ操作）
- 関連 ADR: 計画 **ADR-0036**（所有者ベース裁量制御）／ADR-0034（ホップごとの ABAC 強制）／
  実装 IADR-0019（機密区分のフェイルセーフ）・IADR-0047（保存前検証）
- 計画書リンク: [`09_datasource-connectors.md` §システム投入経路での `owner` / `department`](../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md)（**確定・2026-08-15**。裁定依頼 planning#344）

## 目的・背景

#456 / PR #515 の実測で、稼働中の `document_svc` 2,368 件に計画が**必須**と定める文書属性
`department` / `owner` / `lifecycle` が **1 件も付与されていない**ことが判明した。
付いているのは `confidentiality` のみで、**ABAC の判定軸が実質 1 本**になっている。

2026-08-15 の裁定（planning#344）で**取り込み経路の `owner` / `department` の既定が確定した**ため、
本作業でこれを実装する。**作業中に `lifecycle` の追補裁定（planning#361）も下りた**ため、
**必須 4 属性すべて**を対象に含めた（下記 §`lifecycle`）。

## ★ 母集合（規則 5。**引いた結果と除外理由をここに書く**）

**「文書属性を組み立てている箇所」を機械的に引いた。**引き方と結果は次のとおり。

```
grep -rn "confidentiality" --include=*.cs --include=*.ts --include=*.tsx src/
grep -rn "GetEffectiveAttributes\|DefaultAttributes" --include=*.cs src/knowledge/backend/
grep -rn "record SourceItem\|record RawDocumentFetched" --include=*.cs src/
```

| # | 経路 | 組み立て点 | 現状 | 本作業の対象 |
| --- | --- | --- | --- | --- |
| 1 | **取り込み（同期）** | `DataSourceService/.../Services/DataSourceSyncService.cs:67`<br>`source.GetEffectiveAttributes()` | `confidentiality` **のみ**フェイルセーフ補完 | **対象** |
| 2 | **データソース既定属性** | `.../Domain/DataSource.cs` `WithConfidentialityFailsafe`（`Create` / `Update` / `Patch` / `GetEffectiveAttributes` の 4 箇所を一元化） | 同上 | **対象** |
| 3 | 人手（SC-03 / SC-05） | `Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs:252,257` の `Attributes` | **クライアントの値を素通し**。認証主体から `owner` を立てる箇所が**無い** | **対象外**（下記 除外理由 A） |
| 4 | 保存前検証 | `DocumentService/.../Domain/DocumentAttributes.cs` `ValidateConfidentiality` | `confidentiality` のみ必須検証 | **対象外**（除外理由 B） |
| 5 | フロント属性辞書 | `knowledge/frontend/src/features/sc03-document/attributes.ts:34`<br>`known = ['confidentiality','department']` | `owner` / `lifecycle` はラベルも無い | **対象外**（除外理由 B） |
| 6 | 既存 2,368 件 | — | 3 属性とも欠落 | **対象外**（除外理由 C。**裁定で明示**） |
| 7 | 属性辞書（`AttributeDefinitions`） | AuthorizationService | **0 件** | **対象外**（除外理由 D） |
| 8 | **★ AST が platform へ直接書き込む経路** | `src/ai-stock-trading/.../AiStockTrading.Shared.KnowledgeBase/Adapters/HttpKnowledgeBaseWriter.cs:83` `BuildAttributes`<br>（上流は `KnowledgeBaseWriterSink.cs:45` / `ReportKnowledgeMapper.cs:19`） | **`confidentiality` のみ補完**。`owner` / `department` / `lifecycle` は入らない | **対象外**（除外理由 **E**。**別リポジトリ。AST#520 を起票**） |
| 9 | **`DocumentNormalized` の第 2 の発行元**（図の補正後の再投入） | `ConversionService/.../Services/FigureCorrectionService.cs:84`<br>`Attributes: source?.Attributes ?? new Dictionary<string, string>()` | **原本イベントから復元**するため経路 1 の補完が伝播する。ただし `GetSourceEventAsync` が `null` を返すと**属性 0 件で再発行**する | **対象外**（除外理由 **F**） |

> ### ★★ 経路 8 は当初この表から落ちていた（**規則 1 の再発**）
>
> **`traceability-auditor` 監査（🔴-1）が検出した。** 本仕様書に書いた grep をそのまま実行すると
> **AST 配下だけで 17 件ヒットする**。
>
> ```console
> $ grep -rn "confidentiality" --include=*.cs --include=*.ts --include=*.tsx src/ | grep -c "src/ai-stock-trading/"
> 17
> ```
>
> **落とした機序**: 走査自体は当たっていたが、**出力を 40 件で打ち切って読んだ**ため
> AST のヒットが視界に入らなかった。`git grep` が submodule へ降りない問題（既知）とは**別の失敗**で、
> **「引いたが最後まで見なかった」**である。
>
> **これが最も重い漏れである理由**: **#516 が測った 2,368 件を作っているのがこの経路そのものである。**
> #516 本文が「計画の属性体系に無い取り込み経路固有のメタデータ」として挙げた
> `kind` / `symbol` / `publishedAt` / `periodKey` / `confirmedAt` / `assumptionsVersion` は、
> 実測するとすべて AST 由来である（上記 2 ファイル）。
>
> **帰結（#516 のクローズ判断に直結する）**: 本 PR の修正は経路 1・2 に閉じており、
> **経路 8 を一切通らない**。したがって **#516 の受け入れ基準 1（新規文書に必須属性 4 種）と
> 2（`measure` の「必須だが実データに無い属性」が空になる）は本 PR では満たせない。**
> 除外理由 C（既存分は破棄予定）は**新規に作られ続ける分を解決しない**。

### 除外理由

- **A（人手経路の `owner`）**: 本作業の裁定（planning#344）は**「システム投入経路での」**`owner` / `department` を
  定めたものであり、**人手経路には触れていない**。人手経路で `owner` を認証主体から立てるのは
  **ADR-0036 の動的束縛の実装**であり、計画側が「**未着手**」と明記し（ADR-0036 L66）、
  `Document` エンティティに所有者フィールドが無く**スキーマ変更を伴う**（同 L209）。
  **大玉 #451（FR-19/FR-20・ADR-0036/0037）の射程**である。
- **B（保存前検証・フロント辞書）**: A と同じ理由で、`owner` / `lifecycle` を**必須検証に加えると
  人手経路が壊れる**（現在どの画面も送っていないため、全登録が 400 になる）。
  **属性を入れる前に検証を強めない。**
- **C（既存 2,368 件）**: **裁定が「遡及付与しない」と明示した**
  （「当該データは実装側で破棄が予定されており、移行を書いても破棄で失われる」。#457）。
- **D（属性辞書 0 件）**: #516 本文が「**併せて検討する**」と書いた任意項目であり、
  ABAC ポリシー自体が 0 件の現状では**投入しても評価に効かない**。本作業では扱わない。
- **F（図の補正後の再投入）**: **通常は経路 1 の補完が原本イベント経由で伝播する**ため、本作業の修正が効く。
  `null` 分岐（`?? new Dictionary<string, string>()`）で属性 0 件になる経路は**本作業以前からの既存債務**であり、
  同ファイル自身が「**空で再発行すると取り込み側が機密区分を読めず、文書の可視範囲が変わってしまう**」
  （[[IADR-0154]] 決定 3）と警告している。**扱う属性が 1 → 3 に増えたぶん影響面は広がったが、
  壊れ方の質は変わらない。**本作業では触れない（`traceability-auditor` 監査 🟡-1 が検出）。
- **E（AST の書き込み経路）**: **別リポジトリ（`endazon/ai-stock-trading`）の実装**であり、
  本リポジトリからは submodule の pin を通してしか変えられない。**AST#520 として起票した。**
  **「射程外だから無視してよい」という意味ではない** —— 経路 8 が残る限り
  #516 の受け入れ基準 1・2 は満たされないため、**#516 のクローズ条件に AST#520 の完了を含める。**

### ★ `lifecycle` は着手時「未裁定」だったが、**作業中に裁定が下りた**

**着手時点では計画が `lifecycle` の既定を定めていなかった。** 確定節の見出しは
「システム投入経路での **`owner` / `department`**」であり、既定表も 2 行しかなかった。
一方**同節の前文**と #516 の受け入れ基準は `lifecycle` を含む 3 属性を対象にしていた。

計画書全体を走査しても初期値の定めは無かった（`05_screens/01_screens.md:264` の SC-05 は
「状態の**表示と公開・アーカイブの操作**」であり作成時の初期値ではない）。

```
grep -rn "lifecycle" --include=*.md planning/projects/microservices-platform/
```

**推測で `draft` / `active` を選ばなかった** —— `owner` / `department` は「deny 側に倒れる」ことを
確認したうえで予約値が選ばれたが、`lifecycle` は**倒れる向きが有用性の側で問題になる**
（`draft` にすると取り込んだ全文書が既定で不可視になる）。**裁定依頼として環流した**
（planning#361 / `feedback/20260815_ingestion-lifecycle-default-unadjudicated.md`）。

> **★ 2026-08-15 に裁定が下りた（案 C ＋ 終端 `active`）。** 本作業の途中で回答が届いたため、
> **pin を `b640159` へ進めて実装まで含めた。** 否定形テストは
> [[IADR-0199]] 決定 4 が定めたとおり**反転させた**（`Create_FillsLifecycleWithActive`）。
>
> | 属性 | 解決順 | 終端 |
> | --- | --- | --- |
> | `lifecycle` | データソースの既定属性で指定された値 | **`active`** |
>
> **`active` は予約値ではなく既定値である** —— 件数を環流債務として数えない。
> **`active` にしても無制限に公開にはならない**（`read` は属性の連言で `confidentiality` と
> `department` が同時にかかる）。

## 対象範囲

- **対象**: 取り込み経路（経路 1・2）で **`owner` / `department` / `lifecycle`** を欠落させないこと。
  予約値の観測手段。**計画 pin の `b640159` への追随**（`lifecycle` の裁定を読むため）。
- **対象外**: 上記 A〜D・F、経路 8（AST#520）、ADR-0036 の動的束縛（#451）、既存データの遡及（#457）。

## 設計

### 決定 1: `DataSource` のフェイルセーフを 4 属性へ広げる

`WithConfidentialityFailsafe` は既に **`Create` / `Update` / `Patch` / `GetEffectiveAttributes` の
4 箇所を一元化する先例**（IADR-0019）である。同じ関数へ `owner` / `department` / `lifecycle` を足し、
**`WithRequiredAttributeFailsafe` へ改称**する。

| 属性 | 計画が定めた解決順 | 実装での段 | 終端 |
| --- | --- | --- | --- |
| `confidentiality` | 明示指定 | 明示指定 | `internal`（現行のまま） |
| `department` | 投入元（ソース）の所属 → データソース既定属性 | **`DefaultAttributes` の値のみ**（前段は**未実装**） | **`unassigned`** |
| `owner` | ソース側の更新者 → 予約値 | **`DefaultAttributes` の値のみ**（前段は**器が無い**） | **`system`** |
| `lifecycle` | データソース既定属性 → 終端値 | `DefaultAttributes` の値（**1 段目が無い**） | **`active`**（**既定値**） |

**明示指定は上書きしない**（現行の `confidentiality` と同じ規約）。

### 決定 2: `owner` / `department` とも、**明示指定が無い限り**予約値へ倒れる

**両属性とも実運用では事実上 100% が予約値になるが、理由が異なる。混同しない。**

- **`owner`**: コネクタ契約 `SourceItem(string Path, DateTimeOffset ModifiedAt, long Size)`
  （`Ports/IDataSourceConnector.cs:25`）に**更新者を運ぶフィールドが無い**。**器そのものが無い** → **#752**
- **`department`**: **供給源はあるが写像が未実装。** `SourceItem.Path` はフォルダを運んでおり、
  計画 L51 は「ソースのメタ（所在・**部門**・**フォルダ**・更新者等）を ABAC 基本属性へマッピングする」、
  L34 は「**フォルダ単位の既定属性を継承**」と定めている。加えて **SC-06 の登録フォームに
  `department` の入力欄が無い**（`DataSourceForm.tsx:64` は `confidentiality` だけを送る） → **#754**

**「常に」とは書かない** —— `DefaultAttributes` に明示指定があれば保持される
（テスト `Create_WithExplicitOwner_PreservesValue`）。**倒れるのは明示指定が無いときである。**

計画はこの状態を**予期している**。

> `system` / `unassigned` は「解決できなかった」ことの記録であり、既定ではない。
> **恒久的に積み上がるなら、それはコネクタが更新者・部門を運んでいないという報告**であって、
> 正常な状態ではない。**両方とも件数を観測し、環流債務の測定値として読む。**

いずれも本作業の射程外として**別 issue へ切り出す**（1 issue = 1 PR）。

### 決定 3: 予約値の件数を観測できるようにする

計画が「**件数を観測し、環流債務の測定値として読む**」と明示しているため、
観測手段を伴わない実装は**裁定の半分しか満たさない**。
既存の `scripts/measure-abac-combinations.js`（#456 / PR #515。読み取り専用）の出力へ
`owner=system` / `department=unassigned` の件数を加える。

## 受け入れ基準

### 本作業で満たすもの

- [x] **経路 1・2 で**同期された文書に `confidentiality` / `owner` / `department` が**必ず**付与される
- [x] 明示指定がある場合は**上書きされない**（3 属性とも）
- [x] データソース既定属性に `department` があれば**それが使われ**、無い場合のみ `unassigned`
- [x] `owner` は解決できないとき `system`（**明示指定が無い限りこの経路**）
- [x] 予約値の件数が `measure-abac-combinations.js` の出力に現れる
- [x] `lifecycle` の裁定依頼を環流し（planning#361）、**下りた裁定（終端 `active`）を実装した**
- [x] **否定形テストを [[IADR-0199]] 決定 4 のとおり反転させた**
- [x] `SourceItem` に更新者を載せる件を **#752** として起票した
- [x] `department` の供給源が塞がっている件を **#754** として起票した
- [x] **AST の書き込み経路（経路 8）を母集合へ加え、AST#520 として起票した**

### ★ #516 の受け入れ基準に対する線引き（**クローズ判断はこの表で行う**）

| # | #516 の受け入れ基準 | 本作業で | 満たすのに要るもの |
| --- | --- | --- | --- |
| 1 | 新規に取り込まれた文書に必須属性 **4 種**がすべて付与されている | **経路 1・2 では満たした**（`lifecycle` の裁定が下りたため 4 種が揃った）。**経路 8 では満たさない** | **AST#520** |
| 2 | `measure-abac-combinations.js` の「必須とするが実データに無い属性」が**空**になる | **満たせない** | **AST#520**。**#457 の破棄だけでは解決しない**（経路 8 が新規に作り続けるため） |
| 3 | ADR-0036 の所有者判定が実データに対して機能することをテストで示す | **満たせない** | **大玉 #451**（動的束縛が未実装。計画側も「未着手」と明記） |

> **3 件とも満たせないが、いずれも「やらなかった」のではなく「本作業の射程外に依存している」。**
> **#516 は本 PR ではクローズしない。** 上表の依存が解けた時点で改めて判断する。
> **クローズするならこの表を issue へ転記すること。**

## テスト方針

xUnit（`DataSourceService.Api.Tests`）。既存の `DataSourceUpdateEndpointTests` に倣う。

| # | ケース | 期待 |
| --- | --- | --- |
| 1 | 既定属性が空 | 3 属性とも終端値（`internal` / `system` / `unassigned`） |
| 2 | `department` を明示 | その値が残り、`owner` だけ `system` |
| 3 | `owner` を明示 | その値が残る（**上書きしない**） |
| 4 | 3 属性すべて明示 | 全て素通し |
| 5 | `Create` / `Update` / `Patch` / `GetEffectiveAttributes` の 4 経路 | **いずれも同じ結果**（一元化の退行防止） |
| 6 | 空白文字のみの値 | 終端値で補完（現行 `confidentiality` と同じ扱い） |

## 計画書との差異

- 差異: **あり（作業中に解消）**。`lifecycle` の既定が着手時は未裁定だったため環流し（planning#361）、
  **裁定（案 C ＋ 終端 `active`）を受けて本作業に取り込んだ**。pin を `b640159` へ進めている。

## 未決事項

1. ~~**`lifecycle` の既定**~~ → **裁定済み**（planning#361・案 C ＋ 終端 `active`）。本作業で実装した
2. **`SourceItem` へ更新者を載せるか**（別 issue。載せるまで `owner` は常に `system`）
3. 人手経路の `owner`（**#451 の射程**。本作業では触れない）

---
title: 取り込み経路が必須の文書属性 owner / department を付与していない件の是正（#516）
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

# 仕様書: 取り込み経路の ABAC 必須属性（`owner` / `department`）の既定投入

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
本作業でこれを実装する。

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

### ★ `lifecycle` は本作業の対象外である（**裁定が無いため**）

**計画は `lifecycle` の既定を定めていない。** 確定節の見出しは
「システム投入経路での **`owner` / `department`**」であり、既定表も 2 行しかない。
一方**同節の前文**と #516 の受け入れ基準は `lifecycle` を含む 3 属性を対象にしている。

計画書全体を走査しても初期値の定めは無い（`05_screens/01_screens.md:264` の SC-05 は
「状態の**表示と公開・アーカイブの操作**」であり作成時の初期値ではない）。

```
grep -rn "lifecycle" --include=*.md planning/projects/microservices-platform/
```

**推測で `draft` / `active` を選ばない** —— `owner` / `department` は「deny 側に倒れる」ことを
確認したうえで予約値が選ばれたが、`lifecycle` は**倒れる向きが有用性の側で問題になる**
（`draft` にすると取り込んだ全文書が既定で不可視になる）。**裁定依頼として環流する**
（`feedback/20260815_ingestion-lifecycle-default-unadjudicated.md`）。

## 対象範囲

- **対象**: 取り込み経路（経路 1・2）で `owner` / `department` を欠落させないこと。予約値の観測手段。
- **対象外**: 上記 A〜D、`lifecycle`、ADR-0036 の動的束縛（#451）、既存データの遡及（#457）。

## 設計

### 決定 1: `DataSource` のフェイルセーフを 3 属性へ広げる

`WithConfidentialityFailsafe` は既に **`Create` / `Update` / `Patch` / `GetEffectiveAttributes` の
4 箇所を一元化する先例**（IADR-0019）である。同じ関数へ `owner` / `department` を足す。

| 属性 | 解決順 | 終端 |
| --- | --- | --- |
| `confidentiality` | 明示指定 | `internal`（現行のまま） |
| `department` | 明示指定 → **データソース既定属性** | **`unassigned`** |
| `owner` | 明示指定 → ソース側の更新者 | **`system`** |

**明示指定は上書きしない**（現行の `confidentiality` と同じ規約）。

### 決定 2: 取り込み経路の `owner` は**当面必ず `system` になる**。これは仕様である

コネクタの契約 `SourceItem(string Path, DateTimeOffset ModifiedAt, long Size)`
（`Ports/IDataSourceConnector.cs:25`）は **更新者を運ばない**。
したがって「ソース側の更新者を利用者識別子へ解決する」段は**実装できる入力が無い**。

計画はこの状態を**予期している**。

> `system` / `unassigned` は「解決できなかった」ことの記録であり、既定ではない。
> **恒久的に積み上がるなら、それはコネクタが更新者・部門を運んでいないという報告**であって、
> 正常な状態ではない。**両方とも件数を観測し、環流債務の測定値として読む。**

**`SourceItem` に更新者を足すのは本作業の射程外**（コネクタ 4 実装すべての契約変更を伴い、
1 issue = 1 PR を超える）。**別 issue として切り出す。**

### 決定 3: 予約値の件数を観測できるようにする

計画が「**件数を観測し、環流債務の測定値として読む**」と明示しているため、
観測手段を伴わない実装は**裁定の半分しか満たさない**。
既存の `scripts/measure-abac-combinations.js`（#456 / PR #515。読み取り専用）の出力へ
`owner=system` / `department=unassigned` の件数を加える。

## 受け入れ基準

- [ ] 新規に同期された文書に `confidentiality` / `owner` / `department` が**必ず**付与される
- [ ] 明示指定がある場合は**上書きされない**（3 属性とも）
- [ ] データソース既定属性に `department` があれば**それが使われ**、無い場合のみ `unassigned`
- [ ] `owner` は解決できないとき `system`（**現状は常にこの経路**）
- [ ] 予約値の件数が `measure-abac-combinations.js` の出力に現れる
- [ ] `lifecycle` の裁定依頼を計画リポへ環流し、`feedback/` に記録した
- [ ] `SourceItem` に更新者を載せる件を別 issue として起票した

> **#516 の受け入れ基準 3 番目**（「ADR-0036 の所有者判定が実データに対して機能することをテストで示す」）
> は**本作業では満たせない**。動的束縛が未実装（計画側も「未着手」と明記）で、大玉 #451 の射程である。
> **#516 のクローズ時にこの線引きを明記する。**

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

- 差異: **あり**。`lifecycle` の既定が未裁定（上記）。`feedback/` へ記録し裁定依頼として環流する。
  **本作業は `owner` / `department` に限定して進め、`lifecycle` は裁定後に別途対応する。**

## 未決事項

1. **`lifecycle` の既定**（環流済み・裁定待ち）
2. **`SourceItem` へ更新者を載せるか**（別 issue。載せるまで `owner` は常に `system`）
3. 人手経路の `owner`（**#451 の射程**。本作業では触れない）

---
title: IADR-0199 取り込み経路の必須属性フェイルセーフを owner / department へ広げ、lifecycle は裁定まで補完しない
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - UC-04
  - ADR-0036
  - IADR-0019
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md"
  - "../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md"
related_specs:
  - "../specs/20260815_issue-516_abac-required-attributes.md"
---

# IADR-0199: 必須属性フェイルセーフの拡張と、`lifecycle` を補完しない判断

- 状態: Accepted（2026-08-15）
- 決定者: 計画側の裁定（planning#344）＋ claude（実装）

## 起点・関連

- Issue: #516（取り込み経路が必須の文書属性を付与していない）／#752（`SourceItem` が更新者を運ばない）
- 計画: [`09_datasource-connectors.md`](../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md) §システム投入経路での `owner` / `department`（**確定・2026-08-15**）
- 拡張対象: [[IADR-0019]]（機密区分のフェイルセーフ）
- [作業仕様書](../specs/20260815_issue-516_abac-required-attributes.md)

## 決定 1: フェイルセーフを 3 属性へ広げ、**補完点は 1 箇所に保つ**

[[IADR-0019]] が置いた `WithConfidentialityFailsafe` を **`WithRequiredAttributeFailsafe`** へ改称し、
`owner` / `department` を加えた。**`Create` / `Update` / `Patch` / `GetEffectiveAttributes` の
4 経路が同じ関数を通る**という [[IADR-0019]] の構造は維持する。

| 属性 | 計画が定めた解決順 | 実装での段 | 終端 |
| --- | --- | --- | --- |
| `confidentiality` | 明示指定 | 明示指定 | `internal`（現行のまま） |
| `department` | 投入元（ソース）の所属 → データソース既定属性 | **`DefaultAttributes` の値のみ**（前段は**未実装**） | **`unassigned`** |
| `owner` | ソース側の更新者 → 予約値 | **`DefaultAttributes` の値のみ**（前段は**器が無い**） | **`system`** |

**前段が効かない理由は 2 属性で異なる**（詳細は決定 2）。`owner` は器そのものが無く、
`department` は**供給源はあるが写像が未実装**である。**同じ扱いにしない。**

**明示指定は上書きしない**（空白のみは「未設定」と同じ扱い。現行 `confidentiality` と同じ規約）。

**4 経路の一元化を崩さないことが本決定の要点である。** 1 箇所でも漏れると
「**登録時は付くが更新すると消える**」という、最も気づきにくい壊れ方になる。
退行防止として `Update` / `Patch` / `GetEffectiveAttributes` それぞれに独立したテストを置いた。

## 決定 2: `owner` / `department` とも、**明示指定が無い限り**予約値へ倒れる。これは仕様である

**両属性とも実運用では事実上 100% が予約値になる。ただし理由が異なるため、混同しないこと。**

| 属性 | 前段が効かない理由 | 性質 | 追跡 |
| --- | --- | --- | --- |
| `owner` | コネクタ契約に**更新者を運ぶフィールドが無い** | **器そのものが無い** | **#752** |
| `department` | **供給源はあるが写像が未実装**。加えて SC-06 に入力欄が無い | **実装の欠落** | **#754** |

### `owner` —— 器が無い

```csharp
// Ports/IDataSourceConnector.cs:25
public sealed record SourceItem(string Path, DateTimeOffset ModifiedAt, long Size);
```

**所在・更新日時・サイズの 3 つだけ**であり、計画が定めた解決順の第 1 段
（「ソース側の更新者・作成者を利用者識別子へ解決して入れる」）は**実行できる入力が無い**。
契約変更はコネクタ 4 実装すべてに波及し、かつ「取得した値を利用者識別子へ解決する手段」に
新たな裁定が要る可能性があるため、**#752 として分離した**（1 issue = 1 PR。[[IADR-0116]] 規約 1）。

### `department` —— **供給源はある。写像が無いだけである**

**`owner` と同じ扱いにしてはならない。** `SourceItem.Path` は**フォルダを運んでいる**。

> `09_datasource-connectors.md` L51: ソースのメタ（所在・**部門**・**フォルダ**・更新者等）を、
> 文書の ABAC 基本属性（機密区分・**部門**・所有者・ライフサイクル）へマッピングする
>
> 同 L34（ファイルサーバー・優先 1）: **フォルダ単位の既定属性を継承**

同節 L74-78 は**フォルダが取り込み時に属性へ写像されて「消える」設計**だと明記しており、
**#638 で `BuildTags`（親フォルダ名 → タグ）を削除した**のは写像先がタグではなく ABAC 基本属性だからである
（[[IADR-0153]]）。**削除したまま写像先を作っていない状態**である。

加えて **SC-06 の登録フォームは `confidentiality` だけを送る**（`DataSourceForm.tsx:64`。
`department` の入力欄も更新経路も無い）。**画面から登録した全データソースが `unassigned` になる。**

追跡は **#754**。

### なぜ「常に」と書かないか

**`DefaultAttributes` に明示指定があれば上書きしない**（テスト `Create_WithExplicitOwner_PreservesValue`）。
API 経由なら現在も両属性を設定できる。**「常に予約値へ倒れる」と書くと自リポジトリのテストと矛盾する**
——`adr-guardian` 監査の 🟡 指摘。**倒れるのは「明示指定が無いとき」である。**

**いずれも実装の手抜きではなく、計画が予期した状態である。**

> `system` / `unassigned` は「解決できなかった」ことの記録であり、既定ではない。
> **恒久的に積み上がるなら、それはコネクタが更新者・部門を運んでいないという報告**であって、
> 正常な状態ではない。

## 決定 3: **予約値の件数を観測できるようにする**

計画は「**両方とも件数を観測し、環流債務の測定値として読む**」と明示している。
**観測手段を伴わない実装は裁定の半分しか満たさない** —— 予約値が積み上がっても誰も気づかない。

既存の `scripts/measure-abac-combinations.js`（#456 / PR #515。読み取り専用）の出力へ
`owner=system` / `department=unassigned` の件数を加える。

## 決定 4: **`lifecycle` は補完しない**（裁定が無いため）

計画は `lifecycle` を**必須**と定めるが、**取り込み経路での既定を裁定していない** ——
確定節の見出しも既定表も `owner` / `department` の 2 つだけである。
一方**同節の前文**と #516 の受け入れ基準は `lifecycle` を含む 3 属性を挙げている。

**推測で選ばない。**

| 案 | 副作用 |
| --- | --- |
| `active` | 取り込み文書が既定で閲覧対象になる。**実装が「公開してよい」と決めたことになる** |
| `draft` | **取り込んだ全文書が既定で不可視**になり、ナレッジベースとして機能しない |

`owner` / `department` は「**どちらの予約値も deny 側に倒れる**」ことを確認して選ばれたが、
**`lifecycle` は倒れる向きが有用性の側で問題になる**。したがって同じ形の解決にならない。

**裁定依頼を planning#361 として環流した**（記録: `feedback/20260815_ingestion-lifecycle-default-unadjudicated.md`）。
裁定までの状態は**否定形テスト**として固定する。

```
Create_DoesNotFillLifecycle_BecauseDefaultIsNotAdjudicated
```

**裁定が下りたらこのテストを「補完する」へ反転させる。** テスト名に理由を書いたのは、
**将来の読み手が「補完し忘れ」と誤読して黙って直すのを防ぐため**である。

## 決定 5: **保存前検証は強めない**（属性を入れる前に検証を強めると人手経路が壊れる）

`DocumentAttributes.ValidateConfidentiality`（[[IADR-0047]]）へ `owner` / `lifecycle` の
必須検証を**加えない**。

**現在どの画面も送っていない** —— `features/sc03-document/attributes.ts:34` の
`known = ['confidentiality','department']` のとおり `owner` / `lifecycle` はラベルすら無い。
ここで必須検証を足すと**人手経路の全登録が 400 になる**。

人手経路で `owner` を認証主体から立てるのは **ADR-0036 の動的束縛の実装**であり、
計画側が「**未着手**」と明記し（ADR-0036 L66）、`Document` エンティティに所有者フィールドが無く
**スキーマ変更を伴う**（同 L209）。**大玉 #451 の射程**である。

## 理由

- **決定 1**: [[IADR-0019]] が「補完点を 1 箇所に保つ」ことで既存データソースを救った先例をそのまま広げる。
  新しい構造を持ち込まない。
- **決定 2 / 4**: 計画が定めた既定を**実装できる範囲まで実装し、できない部分は測定値として残す**。
  沈黙して埋めると、**乖離が見えなくなる**（#516 が是正しようとしているのがまさにその状態である）。
- **決定 5**: 「必須と定めた」と「必須が満たされている」を混同しない。
  **属性が入る前に検証を強めると、入れる作業そのものができなくなる。**

## 結果

- **良い影響**: 取り込み経路で必須属性が欠落しなくなる。予約値は deny 側へ倒れるため
  **情報が漏れる向きの変化は無い**。
- **悪い影響 / トレードオフ**: `owner=system` の文書は**所有者ベースでは誰も書き込めない**
  （計画が明記した意図した状態。編集は SC-05 の管理者経路）。
- **限界**: **判定軸は当面 `confidentiality` ＋ `department` の 2 本に留まる**
  （`department` は多くが `unassigned` へ倒れるため実効 1 本に近い）。
  `lifecycle` は裁定待ち、`owner` は #752 待ちである。
  **ADR-0034 が求めるホップごとの強制は、依然として設計どおりには検証できない。**

## フォローアップ

1. **planning#361**: `lifecycle` の既定の裁定（環流済み）。**下りたら決定 4 を改定する**
2. **#752**: `SourceItem` へ更新者を載せ、`owner` の予約値を減らす
3. **#754**: `department` の供給源（フォルダ → 部門の写像規則・SC-06 の入力欄）を塞ぐ
4. **AST#520**: **AST の書き込み経路が必須属性を付けない。** `HttpKnowledgeBaseWriter.BuildAttributes` が
   `confidentiality` だけを補完しており、**本 IADR の修正はこの経路を一切通らない。**
   **#516 が測った 2,368 件を作っているのはこの経路である**（`traceability-auditor` 監査 🔴-1 が検出）
5. **#451（大玉）**: ADR-0036 の動的束縛。人手経路の `owner` と所有者判定はここが担当。
   **#451 は個人資料（`private-note`）に閉じているため、通常文書の人手経路 `owner` も
   同 issue で扱うことを明記しておく**（監査 🟢-4）
6. **#457**: 既存 2,368 件の破棄（**遡及付与しないと裁定済み**）

> **#516 は本 IADR だけでは閉じられない。** 受け入れ基準 1・2 は planning#361 と AST#520 に、
> 3 は #451 に従属する。**線引きの表は[作業仕様書](../specs/20260815_issue-516_abac-required-attributes.md)
> §受け入れ基準 にある。**

## 関連

- Supersedes: なし（[[IADR-0019]] を**拡張**する）
- Superseded by: なし

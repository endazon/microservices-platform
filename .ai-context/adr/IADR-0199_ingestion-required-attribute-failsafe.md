---
title: IADR-0199 取り込み経路の必須属性フェイルセーフを owner / department / lifecycle へ広げる
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - UC-04
  - ADR-0036
  - IADR-0019
author: claude
created: 2026-08-15
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
related_specs:
  - "../specs/20260815_issue-516_abac-required-attributes.md"
---

# IADR-0199: 必須属性フェイルセーフを 4 属性へ拡張する

- 状態: Accepted（2026-08-15）
- 決定者: 計画側の裁定（planning#344 ＋ planning#361〔`lifecycle` の追補〕）＋ claude（実装）

## 起点・関連

- Issue: #516（取り込み経路が必須の文書属性を付与していない）／#752（`SourceItem` が更新者を運ばない）
- 計画: `09_datasource-connectors.md`（計画リポ） §システム投入経路での `owner` / `department` / `lifecycle`（**確定・2026-08-15。`lifecycle` は同日追補**）
- 拡張対象: [IADR-0019](./IADR-0019_datasource-default-attributes.md)（機密区分のフェイルセーフ）
- [作業仕様書](../specs/20260815_issue-516_abac-required-attributes.md)

## 決定 1: フェイルセーフを 4 属性へ広げ、**補完点は 1 箇所に保つ**

[IADR-0019](./IADR-0019_datasource-default-attributes.md) が置いた `WithConfidentialityFailsafe` を **`WithRequiredAttributeFailsafe`** へ改称し、
`owner` / `department` / `lifecycle` を加えた。**`Create` / `Update` / `Patch` / `GetEffectiveAttributes` の
4 経路が同じ関数を通る**という [IADR-0019](./IADR-0019_datasource-default-attributes.md) の構造は維持する。

| 属性 | 計画が定めた解決順 | 実装での段 | 終端 |
| --- | --- | --- | --- |
| `confidentiality` | 明示指定 | 明示指定 | `internal`（現行のまま） |
| `department` | 投入元（ソース）の所属 → データソース既定属性 | **`DefaultAttributes` の値のみ**（前段は**未実装**） | **`unassigned`** |
| `owner` | ソース側の更新者 → 予約値 | **`DefaultAttributes` の値のみ**（前段は**器が無い**） | **`system`** |
| `lifecycle` | データソース既定属性 → 終端値 | `DefaultAttributes` の値（**1 段目が無い形**） | **`active`**（既定値。**予約値ではない**） |

**前段が効かない理由は 2 属性で異なる**（詳細は決定 2）。`owner` は器そのものが無く、
`department` は**供給源はあるが写像が未実装**である。**同じ扱いにしない。**
`lifecycle` は**ソース側から解決する対応物が構造的に無い**ため 1 段目を持たない（決定 4）。

**明示指定は上書きしない**（空白のみは「未設定」と同じ扱い。現行 `confidentiality` と同じ規約）。

**4 経路の一元化を崩さないことが本決定の要点である。** 1 箇所でも漏れると
「**登録時は付くが更新すると消える**」という、最も気づきにくい壊れ方になる。
退行防止として `Update` / `Patch` / `GetEffectiveAttributes` それぞれに独立したテストを置いた。

## 決定 2: `owner` / `department` とも、**明示指定が無い限り**予約値へ倒れる。これは仕様である

**両属性とも実運用では事実上 100% が予約値になる。ただし理由が異なるため、混同しないこと。**
**［2026-08-15 追記 / #767］`department` はもう「事実上 100%」ではない** —— SC-06 の登録フォームから
明示指定できるようになったため、**管理者が値を入れなければ倒れる**という状態に変わった（下記 §`department`）。
**`owner` は「事実上 100%」のままである**（#752 の契約変更まで器が無い）。
度合いの対比表は [データ仕様書 `data-source.md` §`DefaultAttributes` の必須属性フェイルセーフ](../../docs/data/data-source.md)
に置き、ここへ複写しない（同じ事実を 2 箇所に置くと片方が古くなる）。

| 属性 | 前段が効かない理由 | 性質 | 追跡 |
| --- | --- | --- | --- |
| `owner` | コネクタ契約に**更新者を運ぶフィールドが無い** | **器そのものが無い** | **#752** |
| `department` | **供給源はあるが写像が未実装**。~~加えて SC-06 に入力欄が無い~~ → **［2026-08-15 追記 / #767］入力欄は足した**（下記） | **実装の欠落** | **#754**（残るのはフォルダ → 部門の写像規則） |

### `owner` —— 器が無い

```csharp
// Ports/IDataSourceConnector.cs:25
public sealed record SourceItem(string Path, DateTimeOffset ModifiedAt, long Size);
```

**所在・更新日時・サイズの 3 つだけ**であり、計画が定めた解決順の第 1 段
（「ソース側の更新者・作成者を利用者識別子へ解決して入れる」）は**実行できる入力が無い**。
契約変更はコネクタ 4 実装すべてに波及し、かつ「取得した値を利用者識別子へ解決する手段」に
新たな裁定が要る可能性があるため、**#752 として分離した**（1 issue = 1 PR。[IADR-0116](./IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 1）。

### `department` —— **供給源はある。写像が無いだけである**

**`owner` と同じ扱いにしてはならない。** `SourceItem.Path` は**フォルダを運んでいる**。

> `09_datasource-connectors.md` L51: ソースのメタ（所在・**部門**・**フォルダ**・更新者等）を、
> 文書の ABAC 基本属性（機密区分・**部門**・所有者・ライフサイクル）へマッピングする
>
> 同 L34（ファイルサーバー・優先 1）: **フォルダ単位の既定属性を継承**

同節 L74-78 は**フォルダが取り込み時に属性へ写像されて「消える」設計**だと明記しており、
**#638 で `BuildTags`（親フォルダ名 → タグ）を削除した**のは写像先がタグではなく ABAC 基本属性だからである
（[IADR-0153](./IADR-0153_tag-identity-storage-and-projection.md)）。**削除したまま写像先を作っていない状態**である。

加えて **SC-06 の登録フォームは `confidentiality` だけを送る**（`DataSourceForm.tsx:64`。
`department` の入力欄も更新経路も無い）。**画面から登録した全データソースが `unassigned` になる。**
［2026-08-28 追記 / #1021］この段落のうち「入力欄が無い」は #767/#771 で、「更新経路が無い」は
既定属性の編集フォーム実装で、いずれも解消済みである。

追跡は **#754**。

**［2026-08-15 追記 / #767］SC-06 の入力欄は足した。** 上の 2 段落は**本 ADR の起草時点（#516）の実測**で
あり、`department` については**現状ではない**。#767 が計画 09_datasource-connectors §システム投入経路の
**2 段目（データソースの既定属性）**を開けた —— 登録フォームに任意のテキスト欄を置き、非空のときだけ
`defaultAttributes.department` を送る（**未入力ならキーごと送らない**。空文字を送ると本 ADR 決定 1 の
`FillIfBlank` の空白判定に依存する形になり、判定が変わったときに予約値との区別が静かに壊れるため）。

**したがって「画面から登録した全データソースが `unassigned` になる」は、管理者が値を入れた場合には
もう当てはまらない。** ただし**残る 2 つは未消化である** —— ①**フォルダ → 部門コードの写像規則**
（planning#372 の裁定待ち。**実装側で推定規則を決めない**）と ②**更新経路**（SC-06 に編集フォームが無い）。
**#754 はこの 2 つを引き受けたまま open である。**
［2026-08-28 追記 / #1021］②更新経路は解消した（SC-06 に既定属性の編集フォームを実装）。
残るのは①の写像規則（値域裁定待ち）のみである。 予約値の件数を環流債務として読む決定 3 も変わらない。

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

## 決定 4: `lifecycle` の終端は **`active`**（裁定 planning#361・2026-08-15 追補）

**当初この決定は「補完しない」だった。** 計画は `lifecycle` を必須と定めながら、
確定節の見出しも既定表も `owner` / `department` の 2 つだけで、**取り込み経路での既定を裁定していなかった**。

| 案 | 副作用 |
| --- | --- |
| `active` | 取り込み文書が既定で閲覧対象になる。**実装が「公開してよい」と決めたことになる** |
| `draft` | **取り込んだ全文書が既定で不可視**になり、ナレッジベースとして機能しない |

`owner` / `department` は「**どちらの予約値も deny 側に倒れる**」ことを確認して選ばれたが、
`lifecycle` は**倒れる向きが有用性の側で問題になる**。**推測で選ばず planning#361 として環流し、
裁定までの状態を否定形テストで固定した。**

### 裁定の結果（**案 C ＋ 終端 `active`**）

**2026-08-15 に裁定が下りたため、本決定を改定する。**

| 属性 | 解決順 | 終端 |
| --- | --- | --- |
| `lifecycle` | **データソースの既定属性で指定された値** | **`active`** |

計画が示した理由は次のとおりである。

- **`department` が既に採っている 3 段の形**（ソースから解決 → データソース既定属性 → 終端値）**に揃える**。
  `lifecycle` は**ソース側から解決する対応物が無い**ため、**3 段のうち 1 段目が無い形**になる
- **`active` にしても「無制限に公開」にはならない。** `read` は**属性の連言**であり、
  `confidentiality`（未指定は `internal`）と `department`（未解決は deny 側の `unassigned`）が同時にかかる。
  **可視性の統制を `lifecycle` 単独に負わせていない**
- **`draft`（案 B）は採らない。** 全文書が既定で不可視になり、SC-05 の管理者が 1 件ずつ公開操作をしない限り
  ナレッジベースとして機能しない。**安全側に倒す判断は、運用が回らないところまで倒すと安全ではない**

### **`active` は予約値ではなく既定値である**

`system` / `unassigned` は「**解決できなかったことの記録**」だが、**`active` はそう決めた値**である。
したがって**件数を環流債務として数えない**（決定 3 の観測対象に含めない）。

### 否定形テストは**反転させた**

```
Create_DoesNotFillLifecycle_BecauseDefaultIsNotAdjudicated   （削除）
  → Create_FillsLifecycleWithActive                          （新設）
  → Create_WithExplicitLifecycle_PreservesValue              （新設・ソース単位の draft 指定）
```

**当初この ADR が「裁定が下りたら反転させる」と定めておいたとおりに反転した。**
テスト名に理由を書いておいたことで、**反転すべき箇所が機械的に見つかった。**

## 決定 5: **保存前検証は強めない**（属性を入れる前に検証を強めると人手経路が壊れる）

`DocumentAttributes.ValidateConfidentiality`（[IADR-0047](./IADR-0047_document-confidentiality-server-validation.md)）へ `owner` / `lifecycle` の
必須検証を**加えない**。

**現在どの画面も送っていない** —— `features/sc03-document/attributes.ts:34` の
`known = ['confidentiality','department']` のとおり `owner` / `lifecycle` はラベルすら無い。
ここで必須検証を足すと**人手経路の全登録が 400 になる**。

> **［2026-08-16 追記 / #796］この節が言う「画面」は文書の人手経路（SC-03 / SC-05）であり、
> そちらは依然として `owner` / `lifecycle` を送っていない。本決定は変わらない。**
> ただし **SC-06（データソースの既定属性）は `lifecycle` を送るようになった**（#796）ので、
> 「どの画面も送っていない」を**データソースの経路まで含めて読まないこと**。
> **2 つは別の経路である** —— こちらは `Document` の属性検証、あちらは `DataSource.DefaultAttributes`
> であり、後者は本 ADR 決定 1 のフェイルセーフを通る（明示指定は上書きしない）。

人手経路で `owner` を認証主体から立てるのは **ADR-0036 の動的束縛の実装**であり、
計画側が「**未着手**」と明記し（ADR-0036 L66）、`Document` エンティティに所有者フィールドが無く
**スキーマ変更を伴う**（同 L209）。**大玉 #451 の射程**である。

## 理由

- **決定 1**: [IADR-0019](./IADR-0019_datasource-default-attributes.md) が「補完点を 1 箇所に保つ」ことで既存データソースを救った先例をそのまま広げる。
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
- **限界**: **必須 4 属性は揃ったが、判定軸としての実効は上がっていない。**
  `department` は多くが `unassigned`（#754）、`owner` は多くが `system`（#752）へ倒れ、
  `lifecycle` は全件 `active` になるため**値のばらつきが無い**。
  **［2026-08-16 追記 / #796］`lifecycle` の「全件 `active`」は解消の途上にある** ——
  SC-06 の登録フォームから `draft` / `active` / `archived` を明示指定できるようになったため、
  **管理者が指定したソース由来の文書はその値を持つ**（未指定なら従来どおり終端の `active`）。
  **「もうばらつく」ではない** —— 開いたのは登録経路だけで、既存の登録済みソースは遡って値を得ず、
  更新フォームもまだ無い（#754）。`department` / `owner` の限界は変わっていない。
  **さらに AST の書き込み経路（AST#520）は本 IADR の修正を通らず、4 属性のうち 3 つを付けないままである。**
  **ADR-0034 が求めるホップごとの強制は、依然として設計どおりには検証できない。**

## フォローアップ

1. ~~**planning#361**: `lifecycle` の既定の裁定~~ → **2026-08-15 に裁定済み**（案 C ＋ 終端 `active`）。
   **決定 4 を改定し、否定形テストを反転させた。**
2. **#752**: `SourceItem` へ更新者を載せ、`owner` の予約値を減らす
3. **#754**: `department` の供給源を塞ぐ。**［2026-08-15 追記 / #767］SC-06 の入力欄は消化した**（#767）。
   **残るのはフォルダ → 部門の写像規則（planning#372 の裁定待ち）と更新経路である**
   （**［2026-08-16 追記 / #796］`lifecycle` の入力欄も消化した**〔#796〕。
   **裁定 planning#372 が「登録・更新フォームは既定属性 3 つを持つ」と確定させ、登録側は 3 つとも揃った。**
   **フォルダ写像と更新フォームは #754 に残る**）
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

- Supersedes: なし（[IADR-0019](./IADR-0019_datasource-default-attributes.md) を**拡張**する）
- Superseded by: なし

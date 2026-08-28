---
title: 作業仕様書 — 認可スコープ契約の選言（OR）表現（ADR-0046 D-06 部品 3）の改定方針を決める
type: spec
status: done
related_ids: [FR-05, FR-19, FR-20, UC-11, ADR-0004, ADR-0034, ADR-0036, ADR-0046, ADR-0054]
author: claude
created: 2026-08-22
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0046_private-note-not-synced-to-wikijs.md
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md
---

# 作業仕様書: 認可スコープ契約の選言（OR）表現 — `ADR-0046` D-06 部品 3

> **本書は改定方針を決めるところまでを射程とする。** 実装そのものは本書では行わない
> （契約・評価器・検索側フィルタ・グラフ側フィルタに跨るため、方針を IADR で確定してから
> 段に分けて着手する）。

## 走査基準（実測の再現条件）

| 対象 | ref | 備考 |
| --- | --- | --- |
| 実装 `microservices-platform` | `origin/develop` = `dd5471f4` | **fetch 前のローカル `develop` は 71 コミット遅れだった** |
| 計画 `project-planning` | `origin/main` = `fbd4dda` | **fetch 前のローカル `main` は 22 コミット遅れだった** |

🔴 **隣接クローンの作業ツリーを読まないこと。** 本作業で実際に空振りしている（`ls` で計画 ADR を数えて
47 件と出た。`origin/main` では 54 件）。走査は `git ls-tree <ref>` / `git grep <ref>` で ref を明示する。

## 1. 起点となる計画書

### 何が決まっていて、何が実装へ委ねられているか

**計画側は「判定規則」を確定させたうえで、「契約の改定方針」を明示的に実装へ委ねている。**
これは 3 文書が独立に同じことを述べており、**実装に閉じた判断であると判定した**（§2）。

| 文書 | 原文（要点） |
| --- | --- |
| `07_abac-attribute-model` §選言（OR）は現在の契約では表現できない | 「**本節の判定規則は選言のままで正しく、変えない。** 直すのは契約の側である。**改定方針は実装リポジトリの IADR で決める**（`Platform.Shared.Contracts`・評価器・検索側フィルタに跨る実装設計であるため）」 |
| `ADR-0046` D-06 | 「**契約の改定方針は本 ADR では確定しない**（…実装 IADR で決める）。**計画側の判定規則は選言のままで正しく、変えない**」 |
| `ADR-0054` §結果 フォローアップ 3 | 「認可スコープ契約の選言対応…は実装リポジトリの IADR で改定方針を決める…**本 ADR の射程外である**」 |

### 計画が定める `read` の判定規則（変えてはならないもの）

`07_abac-attribute-model` の `read` 規則は **3 節の選言**である。

1. **静的属性ベース**（`confidentiality` / `department` 等の連言）
2. **所有者ベース**（`doc.owner ∈ { ${current_user} }`。`ADR-0036` の動的束縛）
3. **共有先ベース**（`${current_user} ∈ doc.shared_with`）

**この 3 節は OR で結ばれる。** 実装はこれを AND へ潰している（§3）。

## 2. 判定 — 実装に閉じた判断か

**結論: 実装に閉じている。計画側へ問う必要はない。**

| 論点 | 判定 | 根拠 |
| --- | --- | --- |
| 判定規則（3 節の選言）を変えるか | **変えない** | 計画が「選言のままで正しく、変えない」と明記。**実装が規則に追いつく話であり、規則を動かす話ではない** |
| 属性の値域・必須指定を変えるか | **変えない** | `ADR-0054` 決定 6 が既存属性の値域拡張を却下済み。本作業は属性を増やさない |
| `shared_with` を複数値にすることは計画の変更にあたるか | **あたらない** | 計画 §文書の基本属性 が `shared_with` を「ユーザー識別子／グループ識別子の**集合**」と既に定義している。**単値実装のほうが計画から外れている**ため、集合化は計画への追随である |
| 契約（`AccessScopeResponse`）の形 | **実装の裁量** | 計画に契約の形の指定は無い。3 文書が明示的に実装 IADR へ委任 |

🔴 **ただし 1 点だけ、計画へ影響が及び得る境界がある。** `shared_with` を複数値化すると
**`Document.Attributes`（`Dictionary<string, string>`）の値型を変えるか、別の持ち方をするか**という
判断が要る。**これはスキーマ変更・EF マイグレーションを伴う**（§3 の実測 5）。
**計画の属性モデルの記述とは矛盾しない**が、`07_abac-attribute-model` §必須指定と実データの乖離 が
記録する実測値に影響するため、**実装後に環流する**（本書 §7 フォローアップ）。

## 3. 実測（`origin/develop`）

### 実測 1 — 契約に選言を表す構造が無い

`src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/AccessScopeDto.cs`（原文）:

```csharp
public record AccessScopeResponse(
    string UserId,
    List<AttributeFilter> AllowedFilters,
    bool Granted = false);

public record AttributeFilter(string Key, List<string> AllowedValues);
```

同ファイルのコメントが評価規則を明記している（原文）:

> `Filters` の各要素は「文書の属性 key の値が `AllowedValues` に含まれること」を要求し、
> **フィルタ間は AND、値集合内は OR で評価する。**

**`List<AttributeFilter>` は連言 1 本しか表せない。** 選言を表す入れ子（`OR of AND`）が無い。

### 実測 2 — 評価器がキー単位 union で 1 本の連言へ潰している

`AbacEvaluator.ResolveScope`（`AuthorizationService`）の該当部（原文）:

```csharp
foreach (var (key, values) in policy.DocumentConditions ?? [])
{
    var existing = filters.FirstOrDefault(f => f.Key == key);
    if (existing is null)
        filters.Add(new AttributeFilter(key, values));
    else
        // 複数ポリシーがマッチした場合は union（ORで拡張）
        filters[filters.IndexOf(existing)] = existing with
        {
            AllowedValues = existing.AllowedValues.Union(values).Distinct().ToList()
        };
}
```

🔴 **union は「同一キー内」でしか効かない。異なるキーを持つポリシー同士は AND になる。**

**具体的な壊れ方**（計画が予告しているとおり）:

| ポリシー | `DocumentConditions` | 結果のフィルタ |
| --- | --- | --- |
| A（個人資料） | `{ owner: [${current_user}] }` | `owner ∈ {me}` |
| B（組織文書） | `{ confidentiality: [internal] }` | `confidentiality ∈ {internal}` |
| **両方マッチ** | — | **`owner ∈ {me}` AND `confidentiality ∈ {internal}`** |

**「自分の個人資料」も「組織の internal 文書」も見えず、「自分が所有する internal 文書」だけが見える。**
さらに `owner` は実データ **0 件**であるため、**実質すべての文書が消える。**

### 実測 3 — 実装側は既にこの欠陥を認識して記録している

`GraphService/Foundation/Services/GraphAccessResolver.cs`（原文コメント）:

> 🔴 **読むのは `clearance` と `department` の 2 つだけである。** これはプラットフォーム全体の
> 現状であり、本サービスが絞っているのではない。`owner` に基づく判定（`ADR-0036`）は
> **`AccessScopeResponse` に 3 分岐 OR の表現構造が無いため機能しない**（#516）。

**新規の発見ではない。** 未着手であることが問題である。

### 実測 4 — `${current_user}` の動的束縛は「実装が無い」が「テストは在る」（部品 2）

🔴 **本項は当初「0 件」と書いたが、走査したら誤りだった。訂正して残す**（`0 件` は
「無い」ではなく「その形では無い」しか意味しない、という作法が自分に当たった例である）。

| 走査語（`src/` 全域） | 結果 |
| --- | --- |
| `current_user` | **1 ファイル**（テスト） |
| `動的束縛` | **1 ファイル**（同上） |
| `currentUser` / `CurrentUser` / `DynamicBinding` | 0 ファイル |
| 陽性対照 `AttributeFilter` | **37 ファイル** |
| 陽性対照 `AccessScopeResponse` | **35 ファイル** |

**製品コードには無い。** `AbacPolicy.UserConditions` / `DocumentConditions` は
`Dictionary<string, List<string>>` の**リテラル値のみ**を持ち、束縛変数を解釈する経路が無い。

### 実測 7 — 🔴 既存の tripwire テストが在る。**赤くするのが本作業の完了条件である**

`GraphService.Api.Tests/AbacUnenforcedAxisTests.cs` が、**強制できていない認可軸を意図的に固定している**。
ファイル冒頭のコメント（原文）:

> **本テストは「まだ強制していない」ことを明示的に固定するものである。**
> 上記が是正されると本テストは赤くなる —— **それが狙いであり**、そのとき初めて
> 「所有者で見え方が変わる」実装を足してよい。**赤くなったら消すのではなく、
> 強制されるようになったことを確かめる形へ書き換えること。**

個別テストにも合図が埋め込まれている（原文）:

> `owner` に基づく判定は現時点で強制されていない（#516）。**ここが false になったら
> 3 分岐 OR が表現できるようになった合図であり、`ADR-0034` 決定 6・8・9 の実装に着手してよい**

**したがって本作業の完了条件は「テストが緑」ではない。** 次の 2 つを**対で**満たすことである。

1. `AbacUnenforcedAxisTests` の 2 件が**赤くなる**（＝強制が効くようになった）
2. **その 2 件を、強制されることを確かめる形へ書き換えて緑にする**（消さない）

**この tripwire を見落として「全テスト緑」で完了と報告すると、何も直っていないことになる。**

### 実測 8 — 🔴 計画が記録していない制約が 1 つある（`/authz/scope` の action がハードコード）

同 tripwire のコメントが 3 つ目の理由として挙げており、**独立に検算して事実だと確認した**。

| 観測点 | 実測（原文） |
| --- | --- |
| `AccessScopeRequest` | `record AccessScopeRequest(string UserId, Dictionary<string, string> UserAttributes)` —— **`Action` フィールドが無い** |
| `AuthzEndpoints.cs:21` | `AbacEvaluator.ResolveScope(req, policies, PolicyAction.Read)` —— **`Read` をサーバ側でハードコード** |

**計画 `07_abac-attribute-model` §選言 の表は 4 行（契約 / 評価器 / 検索側フィルタ / `shared_with`）で、
この 5 つ目を記録していない。** `/authz/scope` は `read` しか解決できないため、
**書き込みの認可スコープはこの経路では出せない**。FR-21 受け入れ基準 ⑧（別主体として同じ文書 ID へ
**書き込み**を試みると拒否される）に直接効くため、論点として立てる（§5 論点 7）。

**→ 計画側へ環流する**（§7 フォローアップ）。

### 実測 5 — `shared_with` は単値の器に入っている（第 3 節が表現できない）

`Document.Attributes` は `Dictionary<string, string>`（`DocumentService/Foundation/Domain/Document.cs:15`）。
**値が単一文字列であるため、共有先の集合を保持できない。**

### 実測 6 — `doc_scope` は 0 件（本作業の前提確認）

| 走査語 | 結果 |
| --- | --- |
| `doc_scope` / `document_scope` / `docScope` | **0 ファイル** |
| 陽性対照 `confidentiality` | **184 ファイル** |
| 陽性対照 `data_class` | 3 ファイル |

**0 件は「無い」ではなく「その形では無い」しか意味しないため、陽性対照を対で置いた。**

## 4. 母集合（規則 6 —— 引いた結果と、除外したものとその理由）

**誤りの側の文字列（`AttributeFilter` / `AllowedFilters` / `AccessScope`）で `origin/develop` 全域を走査した。**

| 面 | ファイル | 扱い |
| --- | --- | --- |
| 契約 | `Platform.Shared.Contracts/Dtos/AccessScopeDto.cs` | **改定対象** |
| 評価器 | `AuthorizationService/Foundation/Services/AbacEvaluator.cs` | **改定対象** |
| 検索側フィルタ | `AiAnalysisService`（`RagOrchestrator` / `DataRangeScopeResolver`） | **改定対象** |
| グラフ側フィルタ | `GraphService`（`AbacNodeFilter` / `AuthorizedNode` / `AuthorizedGraphView` / `GraphTraversal` / `GraphAccessResolver`） | **改定対象** |
| Wiki ゲートウェイ | `WikiService`（`WikiEndpoints` / `WikiAccessResolver`） | **改定対象** |
| BFF | `Platform.Bff`（`AuthzBffEndpoints`）/ `Knowledge.Bff`（`SearchBffEndpoints`） | **要確認**（契約を透過するだけか、解釈しているか） |
| `src/ai-stock-trading`（submodule） | — | **除外。** 別プロジェクトの名前空間であり、本契約を参照していないことを走査で確認する（着手時） |

**除外の理由を残す**: submodule を除いたのは名前空間が別だからであり、「関係なさそうだから」ではない。

## 5. 決めること（IADR で確定する論点）

| # | 論点 |
| --- | --- |
| 1 | **契約の形** —— `AccessScopeResponse` に選言をどう載せるか（`List<List<AttributeFilter>>` 相当の入れ子か、named branch の集合か、別 DTO か） |
| 2 | **後方互換** —— 既存の `AllowedFilters` を残して並走させるか、一度に切り替えるか。**全サービスが同時に追随できない場合の安全側の既定** |
| 3 | **`${current_user}` の束縛点** —— 評価器で解決するか、契約に束縛変数のまま載せて各サービスが解決するか（**キャッシュキーに主体を含める要件**〔`ADR-0036`・FR-21 受け入れ基準 ⑧〕と直結する） |
| 4 | **`shared_with` の複数値** —— 属性辞書の値型を変えるか、別の持ち方にするか（**EF マイグレーションの要否**） |
| 5 | **検索側・グラフ側の適用** —— 選言を索引クエリへどう落とすか（Qdrant / PostgreSQL それぞれ） |
| 6 | **段の切り方** —— 1 PR に収まらないため、どの順で・どこまでを 1 段とするか |
| 7 | 🔴 **`/authz/scope` の action** —— `AccessScopeRequest` に `Action` を足して `read` / `write` を解決し分けるか、別端点にするか（実測 8。**FR-21 受け入れ基準 ⑧ に直結**） |
| 8 | 🔴 **`AbacUnenforcedAxisTests` の書き換え方** —— 実測 7 の tripwire を、どの段でどう「強制されることを確かめる形」へ書き換えるか（**消さない**） |

## 6. 受け入れ基準（IADR まで）

- [ ] 上記 8 論点すべてに決定が書かれている（「別途決める」で残さない。残す場合はその理由と決める場所を書く）
- [ ] **計画側の判定規則（3 節の選言）を変えていない**ことを明記している
- [ ] **後方互換の判断**が書かれており、切り替え中に **deny 側へ倒れる**ことが示されている（fail-closed）
- [ ] 段の切り方が書かれ、**各段が単独でマージ可能**であることが示されている

## 7. 退行防止（実装段で必須・IADR には方針のみ）

🔴 **否定形テストには陽性対照を対で置くこと。** 「常に空スコープを返す実装」「常に 404 を返す実装」は
否定形テストだけを通す。**この実例は本プロジェクトで複数回起きている。**

| # | テスト | 種別 |
| --- | --- | --- |
| 1 | 個人資料ポリシーと組織文書ポリシーが**同時にマッチ**したとき、**両方の集合が見える**（現状は積集合になる） | **本欠陥の回帰テスト** |
| 2 | `owner` を持たない既存文書が、組織文書ポリシーだけで**見える**（現状は `owner` フィルタで全滅する） | **陽性対照。** 実データ 2,368 件がこの形 |
| 3 | 他人の個人資料が**見えない** | 否定形 |
| 4 | 3 とセットで、**自分の個人資料は見える** | **陽性対照（3 と対）** |
| 5 | 別主体として同じ文書 ID を引くとスコープが変わる（キャッシュキーに主体が含まれる。FR-21 ⑧） | 否定形＋陽性対照 |
| 6 | 🔴 `AbacUnenforcedAxisTests` の 2 件を**強制されることを確かめる形へ書き換えた**うえで緑（実測 7） | **完了条件。消さない** |

**検査を足したら、その検査が落ちることを変異試験で確かめる。** 選言の実装を AND へ戻した状態で
テスト 1・2 が赤になることを**実測してから**完了とする。**変異が当たったことを先に assert する。**

🔴 **「全テスト緑」を完了の証拠にしないこと。** 実測 7 の tripwire は**現状が緑**であり、
**本作業が効いていなければ緑のまま**である。**空振りと成功が同じ見た目になる形がここに在る。**

## 8. 射程外（明記）

- **部品 1（`owner` の付与）** —— #516 / #451 の担当。本作業は「`owner` があれば効く」ところまでを作る
- **部品 2（`${current_user}` の実装）** —— 論点 3 で**方針は決める**が、実装は段を分ける
- **FR-19 の機能実装（容量・版・トークン・画面）** —— #451
- **`doc_scope` の必須検証**（`DocumentAttributes` への追加） —— planning#465 の環流結果を待つ
- **既存 2,368 件への遡及付与** —— #457（破棄）

## 7-b. フォローアップ（環流）

1. 🔴 **実測 8（`/authz/scope` の action ハードコード）を計画へ環流する。** `07_abac-attribute-model`
   §選言（OR）は現在の契約では表現できない の表が 4 行しか記録しておらず、**5 つ目の制約が漏れている**。
   FR-21 受け入れ基準 ⑧ に効くため、計画側の記録に足す価値がある
2. **`shared_with` の複数値化がスキーマへ及ぶ場合**、`07_abac-attribute-model` §必須指定と実データの乖離
   の実測値に影響するため、実装後に環流する（§2）

## 9. 関連

- 計画: `ADR-0046` D-06 部品 3 / `ADR-0054` フォローアップ 3 / `07_abac-attribute-model` §選言（OR）は現在の契約では表現できない / `ADR-0036` D-04 / `ADR-0004`
- 実装: #451（FR-19 / FR-20）/ #516（必須属性）/ #986（`ADR-0046` D-01）/ #987・#988（ADR レンジ）
- 環流: planning#464（起案段階の遷移漏れ）/ planning#465（`ADR-0054` の費用の見立て）

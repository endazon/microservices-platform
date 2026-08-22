---
title: IADR-0253 認可スコープ契約に選言（OR）を載せる — 名前つき分岐の集合とし、既存の単一連言は算出値として据え置く
type: impl-adr
status: Proposed
related_ids: [FR-05, FR-19, FR-20, FR-21, UC-11, ADR-0004, ADR-0034, ADR-0036, ADR-0046, ADR-0054]
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0046_private-note-not-synced-to-wikijs.md
  - planning:projects/microservices-platform/07_adr/ADR-0054_doc-scope-attribute-for-private-note.md
---

# IADR-0253: 認可スコープ契約に選言（OR）を載せる

> 🔴 **番号は仮である。** `IADR-0252` が現在の最大であるため `0253` を置いたが、
> **マージ直前に `develop` の最大＋1 を実測で取り直すこと**（並行実装で採番衝突が起こり得る。
> 先着尊重）。**改番するときは間違っている側の番号で grep する。**

- 状態: Proposed
- 日付: 2026-08-22
- 決定者: claude（実装判断）

## 起点・関連

- 関連する計画書 ID: FR-05 / FR-19 / FR-20 / FR-21 / UC-11 / `ADR-0004`（ABAC）/ `ADR-0034`（ホップごと ABAC）/ `ADR-0036`（所有者ベース裁量制御）/ `ADR-0046` D-06 部品 3 / `ADR-0054` フォローアップ 3
- 関連する実装仕様書: [`20260822_adr-0046-d06-part3_authz-scope-disjunction.md`](../specs/20260822_adr-0046-d06-part3_authz-scope-disjunction.md)（母集合・実測・受け入れ基準の正本）

## コンテキストと課題

### 計画が実装へ委ねた論点である（3 文書が独立に明示）

**本 IADR は計画の判定規則を変えない。** 計画は判定規則（`read` の 3 節の選言）を確定させたうえで、
**契約の改定方針だけを実装へ委ねている**。

| 文書 | 委任の記述（要点） |
| --- | --- |
| `07_abac-attribute-model` §選言（OR）は現在の契約では表現できない | 「**本節の判定規則は選言のままで正しく、変えない。** 直すのは契約の側である。**改定方針は実装リポジトリの IADR で決める**」 |
| `ADR-0046` D-06 | 「**契約の改定方針は本 ADR では確定しない**…実装 IADR で決める」 |
| `ADR-0054` フォローアップ 3 | 「認可スコープ契約の選言対応…は実装リポジトリの IADR で改定方針を決める…**本 ADR の射程外である**」 |

**したがって本件は実装に閉じた判断であると判定した**（作業仕様書 §2 に判定の表）。

### 何が壊れているか（実測。`origin/develop` = `dd5471f4`）

計画 `read` 規則は **①静的属性ベース ∨ ②所有者ベース ∨ ③共有先ベース** の**選言**である。
実装はこれを **1 本の連言**へ潰している。

`AbacEvaluator.ResolveScope` は**キー単位でしか union しない**ため、**異なるキーを持つポリシー同士は AND になる**。

| ポリシー | `DocumentConditions` | 生成されるフィルタ |
| --- | --- | --- |
| A（個人資料） | `{ owner: [${current_user}] }` | `owner ∈ {me}` |
| B（組織文書） | `{ confidentiality: [internal] }` | `confidentiality ∈ {internal}` |
| **両方マッチ** | — | 🔴 **`owner ∈ {me}` AND `confidentiality ∈ {internal}`** |

**「自分の個人資料」も「組織の internal 文書」も見えず、積集合だけが見える。** さらに `owner` は
実データ **0 件**であるため、**実質すべての文書が消える**。

**契約側にも選言を表す構造が無い**（`AccessScopeResponse.AllowedFilters` は `List<AttributeFilter>`
＝連言 1 本）。

### 実装は既にこれを認識し、テストで固定している

`GraphService.Api.Tests/AbacUnenforcedAxisTests.cs` が**強制できていない軸を意図的に赤くならない形で固定**し、
「**ここが false になったら 3 分岐 OR が表現できるようになった合図**」と書いている。
**本 IADR の実装が効けば、このテストは赤くなる。** それが狙いである。

### 🔴 計画が記録していない制約が 1 つある

`AccessScopeRequest` に **`Action` フィールドが無く**、`AuthzEndpoints.cs:21` が
`PolicyAction.Read` を**サーバ側でハードコード**している（実測）。計画 §選言 の表は 4 行で、
**この 5 つ目を記録していない**。**書き込みの認可スコープはこの経路では出せない**ため、
FR-21 受け入れ基準 ⑧（別主体として同じ文書 ID へ**書き込み**を試みると拒否される）に直接効く。

## 検討した選択肢

| # | 案 | 評価 |
| --- | --- | --- |
| 1 | **`AccessScopeResponse` に名前つき分岐の集合を足す**（採用） | 契約の意味が明示的。**どの分岐で見えたかを言える**（計画が「どの分岐で検証したかを必ず添えること」と要求している） |
| 2 | `List<List<AttributeFilter>>`（無名の入れ子） | 表現力は同じだが**分岐に名前が無い**。監査・デバッグで「なぜ見えたか」を説明できない |
| 3 | 述語式（AST）を契約に載せる | 表現力は最大だが**各サービスに評価器を持たせることになり、認可の判断が散る**。既存テストが明示的に禁じている（「述語がプレースホルダを解釈すると認可の判断が 2 箇所へ散る」） |
| 4 | 契約を変えず、サービスごとに所有者判定を足す | `ADR-0036` D-01（単一の評価モデルへ統合する・別レイヤを作らない）に反する |

## 決定

### 決定 1: 契約へ**名前つき分岐の集合**を足す

```csharp
// 分岐内は AND、分岐間は OR。Name は監査・デバッグ用（"attribute" / "owner" / "shared"）。
public record AccessScopeBranch(string Name, List<AttributeFilter> Filters);

public record AccessScopeResponse(
    string UserId,
    List<AttributeFilter> AllowedFilters,   // 決定 2 により据え置き（算出値）
    bool Granted = false,
    List<AccessScopeBranch>? Branches = null);
```

**評価規則**: `Granted == false` なら不可視。`Granted == true` かつ `Branches` が空／`null` なら
**従来どおり `AllowedFilters` で評価**する。`Branches` が 1 件以上なら
**いずれかの分岐のフィルタをすべて満たす文書が可視**である。

**分岐に名前を持たせるのは、計画が要求しているためである**（原文）:

> **「ABAC を検証した」と書くときは、どの分岐で検証したかを必ず添えること。**

### 決定 2: `AllowedFilters` は**現在の算出アルゴリズムのまま据え置く**（後方互換の要）

**新フィールドを足すだけで、既存フィールドの値も意味も変えない。**

- **未移行のサービスは挙動が 1 ビットも変わらない**（現状のまま＝過度に絞るが、退行はしない）
- **移行済みのサービスだけが正しい選言を得る**
- **切り替え中の乖離は常に deny 側へ倒れる** —— `AllowedFilters`（分岐の積に相当）は
  `Branches`（分岐の和）の**部分集合**であるため、未移行側が余分に見せることは**構造上あり得ない**

**`AllowedFilters` の削除は全サービスの移行完了後に別 IADR で判断する。** 本 IADR では消さない。

### 決定 3: `${current_user}` の束縛は**評価器（AuthorizationService）でのみ**解決する

**述語側（`AbacNodeFilter` 等）はプレースホルダを解釈しない。** 既存テスト
`Dynamic_binding_placeholders_are_NOT_interpreted` の意図をそのまま維持する（原文）:

> 動的束縛は認可サービス側で解決されるべきものであり、**述語がプレースホルダを解釈すると
> 認可の判断が 2 箇所へ散る**

`AbacEvaluator.ResolveScope` が `request.UserId` を用いて `${current_user}` を展開してから
分岐を組み立てる。**束縛できる変数は `${current_user}` の 1 つだけとする**（増やさない。
計画が語彙を定めていないため、実装が語彙を先取りしない）。

### 決定 4: `shared_with` は**属性辞書に載せず、専用の記録として持つ**（段を分ける）

`Document.Attributes` は `Dictionary<string, string>` であり単値しか持てない。**値型は変えない。**
共有先は `DocumentShare`（文書 ID × 被共有主体）として別に持ち、分岐 ③ はそれを引く。

**理由**: 共有は属性と**ライフサイクルが違う**（付与・取り消し・監査が要る。`ADR-0036` が
**再共有不可**・**取り消し可**を定めている）。属性辞書へ多値を持ち込むと、
**属性を読むすべての面（検索・グラフ・Wiki ゲートウェイ・BFF）の契約が変わる**。

🔴 **これは EF マイグレーションを伴う。** 追加時は `dotnet ef migrations add` の生成物を
**目視で検算する**（`--no-build` はビルド済みアセンブリを読むため、**古いモデルに対する
マイグレーションが黙って生成される**）。

### 決定 5: `AccessScopeRequest` に `Action` を足す（既定 `read`）

```csharp
public record AccessScopeRequest(
    string UserId,
    Dictionary<string, string> UserAttributes,
    string Action = PolicyAction.Read);   // 既定値つき＝既存呼び出しは無改修
```

`AuthzEndpoints` のハードコードを `req.Action` へ置き換える。**既定値があるため後方互換である。**

### 決定 6: 段を 5 つに分ける（各段が単独でマージ可能）

| 段 | 内容 | 完了の判定 |
| --- | --- | --- |
| **1** | 契約へ `AccessScopeBranch` / `Branches` を追加（決定 1・2）。**評価器・消費側は未変更** | 既存テストが全緑のまま。`Branches` は常に `null` |
| **2** | `AbacEvaluator` が分岐を組み立てる（決定 1・3）。`AllowedFilters` は据え置き | 評価器の単体テストで「A と B が同時マッチ → 分岐 2 本」 |
| **3** | 消費側を分岐対応へ（`GraphService` → `WikiService` → `RetrievalService` → `AiAnalysisService`）。**1 サービス 1 PR** | 🔴 **`AbacUnenforcedAxisTests` を書き換える段。** 下記参照 |
| **4** | `DocumentShare` と分岐 ③（決定 4） | EF マイグレーションの目視検算つき |
| **5** | `Action` の解決（決定 5） | `write` スコープが `read` と別に出る |

### 決定 7: 🔴 `AbacUnenforcedAxisTests` は**消さず、書き換える**

同ファイルの指示（原文）に従う:

> **赤くなったら消すのではなく、強制されるようになったことを確かめる形へ書き換えること。**

**段 3 の該当サービスの PR で、同じテストを「強制されること」の陽性・否定形の対へ書き換える。**
**削除は許さない。**

### 決定 8: 認可スコープのキャッシュは本 IADR では**導入しない**

**現時点でキャッシュは実装されていない**（`IMemoryCache` / `IDistributedCache` を
`AuthorizationService` と各 `AccessResolver` の全域で走査して 0 件。実測）。

**したがって FR-21 受け入れ基準 ⑧「認可判定のキャッシュキーに主体が含まれる」は、
現状では検証対象が存在しない。** 分岐の導入で**スコープが主体依存になる**ため、
**キャッシュを入れるときは主体を必ずキーに含める**という制約だけを記録し、導入自体は別 IADR とする。

## 理由

- **決定 1・2 の組み合わせが「一度に切り替えない」を可能にする。** 契約を破壊的に変えると
  11 サービス＋BFF が同時に追随せねばならず、**FIFO で 1 本ずつマージする運用と両立しない**
- **乖離が deny 側へ倒れることが構造で保証される**（決定 2）。「気をつける」ではなく
  **`AllowedFilters ⊆ 各分岐の和` という包含関係**から従う
- **決定 3 は既存テストが明示的に要求している設計である。** 実装が独自に決めたのではない
- **決定 4 は「属性へ多値を持ち込まない」ことで波及を止める。** 計画は `shared_with` を
  「集合」と定義しており、**専用の記録にするほうが計画に忠実である**

## 結果

- **良い影響**:
  - `ADR-0036` の 3 分岐 OR が表現可能になり、**`ADR-0046` D-06 部品 3 が閉じる**
  - **分岐に名前が付くため「どの分岐で見えたか」を監査ログ・テストで言える**（計画の要求）
  - `owner` が付き次第（#516 / #451）、閲覧の個人スコープが**追加改修なしで効く**
- **悪い影響 / トレードオフ**:
  - **契約に「新旧 2 つの読み方」が一時的に併存する**（決定 2）。**移行完了まで `AllowedFilters` を消せない**
  - **段 4 で EF マイグレーションが要る**（決定 4）
  - 🔴 **検索側（Qdrant）で選言をどう表現するかは未実測である。** `QdrantVectorStore.BuildAttributeFilter` は
    現在 `Filter { Must = ... }` のみを使い、**`Should` は本リポジトリのどこにも使われていない**（実測）。
    **入れ子フィルタ（`Condition.Filter`）が使えるかを段 3 の着手時に実測すること。**
    使えない場合は分岐ごとにクエリを分けて和を取る案へ倒す（**候補数の上限に注意**）
- **フォローアップ**:
  1. 🔴 **計画へ環流する** —— `07_abac-attribute-model` §選言 の表に **`/authz/scope` の action ハードコード**が
     記録されていない（5 つ目の制約）。FR-21 ⑧ に効く
  2. `AllowedFilters` の削除可否は全サービス移行後に別 IADR で判断する
  3. 認可スコープのキャッシュ導入は別 IADR（決定 8）

## 関連

- Supersedes: なし
- Superseded by: なし
- 実装 issue: **#989（本 IADR を起こした issue）** / #451（FR-19 / FR-20）/ #516（必須属性）/ #986（`ADR-0046` D-01）
- 環流: planning#464 / planning#465

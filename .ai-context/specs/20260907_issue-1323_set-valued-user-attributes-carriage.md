---
title: 集合値の利用者属性（tags / projects）を realm から評価器まで通す（#1323 / ADR-0080 フォローアップ 1・2）
type: spec
status: done
related_ids: [FR-05, FR-09, FR-16, SC-09, SC-12, SC-17, ADR-0004, ADR-0036, ADR-0062, ADR-0080, ADR-0085, ADR-0086, IADR-0253, IADR-0385, IADR-0411]
author: Claude（実装）
created: 2026-09-07
updated: 2026-09-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0080_set-valued-user-attributes-and-match-semantics.md
  - planning:projects/microservices-platform/07_adr/ADR-0085_project-attribute-scope-and-non-axis.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
---

# 仕様書: 集合値の利用者属性を realm から評価器まで通す（#1323）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-05（認可・ABAC）／FR-09（属性辞書・ポリシー管理）／FR-16（無人アカウント）
- 画面: SC-09（属性辞書）／SC-12（無人アカウント）／SC-17（属性割当）
- 計画 ADR: **ADR-0080**（集合値の利用者属性と交差意味論。本件の直接の根拠）／
  ADR-0085（`project` を判定軸へ加えない）／ADR-0062 決定 2（無人アカウントの部分集合）／
  ADR-0004・ADR-0036（ABAC の基礎）／ADR-0086（利用者文脈の本文搬送）
- 実装 ADR: [[IADR-0385]]（集合値の符号化。**前提として扱い覆さない**）／[[IADR-0253]]（束縛変数は増やさない）／
  [[IADR-0411]]（本 PR で新設。抽出点の集約）
- issue: #1323。出所は #1255 / PR #1322

## 🔴 着手前に引き直して分かったこと（issue の記述を 2 点訂正する）

### 訂正 1 —— 抽出点は **4 ではなく 6** である

issue は `git grep -nE 'attrs\["clearance"\]|attrs\["department"\]'` で 4 箇所と数えていた。
**これは母集合の規則 2（あり得る形をすべて列挙してから引く）を破っている** ——
`context.UserAttributes["clearance"]` という別の受け皿の形を落としている。
引き直した走査（`\["clearance"\]|\["department"\]|\["tags"\]|\["projects"\]`・パスから引き・
`Tests` 除外）で **6 箇所**が出る:

| # | 位置 | 受け皿 | issue の 4 件に含まれたか |
| --- | --- | --- | --- |
| 1 | `Platform.Shared.Infrastructure/Foundation/Authz/BffScopeResolver.cs:69` | `attrs` | ○ |
| 2 | `AiAnalysisService/Features/Analysis/AnalysisEndpoints.cs:55` | `attrs` | ○ |
| 3 | `GraphService/Domain/Ports/IGraphAccessResolver.cs:78` | `attrs` | ○ |
| 4 | `WikiService/Infrastructure/ExternalServices/WikiAccessResolver.cs:78` | `attrs` | ○ |
| 5 | `GraphService/Infrastructure/ExternalServices/GrpcDocumentTagWriter.cs:127` | `attrs` | 🔴 **× 漏れ** |
| 6 | `RetrievalService/Infrastructure/ExternalServices/GrpcGraphNeighborExpander.cs:179` | `context.UserAttributes` | 🔴 **× 漏れ**（形が違う） |

**6 つは論理が 1 文字も違わない。** 5 つが `HttpContext` を、1 つが `ClaimsPrincipal` を受ける違いだけである。
**同じ形が 6 箇所に散っていること自体が本件の温床である**（issue 自身がそう書いている）。

### 訂正 2 —— 🔴 **`tags` / `projects` のクレームは realm に存在しない**

issue の「確かめていないこと」の 1 点目である。**実測した結果、載っていない。**

```console
$ git grep -nE '"(tags|projects)"' -- deploy/
deploy/grafana/... （ダッシュボードの tags。無関係）
```

`deploy/keycloak/microservices-platform-realm.json` の client scope `abac-attributes`
（`:226-273`）が持つ protocol mapper は **`clearance` / `department` / `groups` の 3 つだけ**である。
⇒ **6 箇所の抽出点を直しても、読むクレームが発行されていない。realm の配線が先行条件である。**

### 訂正 3（追加で判明）—— 評価器を直さないと受け入れ基準を満たせない

issue の受け入れ基準は「`tags` を利用者条件に持つポリシーで**許可が出る**」である。
`MatchesUserConditions` は `allowedValues.Contains(userValue)` の **1 キー 1 値照合**であり、
線上表現 `"sales,hr"` は許容値 `["sales"]` の**どれにも一致しない**。
⇒ **搬送だけでは基準を満たさない。** `ADR-0080` 決定 2（交差が空でない）の実装が要る。

**根拠は揃っている。** `IADR-0385` 決定 6 は「評価器の意味論は変えない。実装側で決めてよい範囲を
超えるので環流した（planning#545）」と書いていたが、**その環流の裁定が `ADR-0080`（2026-09-05）である**。
同 ADR フォローアップ 2 が「`AbacEvaluator.MatchesUserConditions` を決定 2・3 の意味論へ合わせる」と
明示的に実装へ差し戻している。**`IADR-0385` 決定 6 の保留はここで解ける。**

## 射程（どこまでやり、どこで止めるか）

**やる**: realm のクレーム発行 → 6 箇所の抽出（集約）→ 評価器の交差判定 → dev seed の属性辞書（`tags`）。
**4 層すべてが揃わないと 1 つも効かない**ため 1 PR にまとめる（`IADR-0139` の「同型な契約追加」ではなく、
**1 本の因果連鎖**である。分けると中途半端な段が develop に残る）。

**やらない（理由つき）**:

- 🔴 **dev seed の属性辞書へ `projects` を足さない。** `ADR-0080` フォローアップ 1 が挙げるのは
  **`tags` だけ**である。`projects` を利用者条件に使う統制は**文書側 `project` との静的ポリシー対**を
  要し、`ADR-0085` 決定 1・2 が**必須化と判定軸をいずれも保留**している
  （必須化しないまま載せると `project` を持たない文書が全滅する）。
  **辞書に無ければ `AbacValidation.ValidatePolicy` がそのポリシーを作らせない** ——
  保留を機械で守る形になる。
- ただし **符号化・搬送・交差判定は `UserAttributeEncoding.SetValuedKeys`（`tags` と `projects` の
  両方）に対して一般に効かせる。** 実装が鍵を絞ると**再び「運ばれないキー」が生まれる**
  ——それが本 issue の欠陥そのものである。**辞書で止めるのであって、搬送で止めない。**
- **束縛変数 `${current_projects}` は新設しない**（`ADR-0085` 決定 3 / `IADR-0253` 決定 3）。
- **`roles` は判定へ用いない**（`ADR-0080` 決定 4）。
- **`project` を文書条件の判定軸へ加えない**（`ADR-0085` 決定 2）。

## 母集合（自分で引き直した走査。規則 1〜10）

`git rev-parse --is-shallow-repository` = **`false`**。

| 軸 | 検索語 | 件数 | 用途 |
| --- | --- | --- | --- |
| 1 | `\["clearance"\]\|\["department"\]\|\["tags"\]\|\["projects"\]`（コード・`Tests` 除外） | **6**（＋文書側 2 件は別物） | 抽出点の母集合（訂正 1） |
| 2 | `ExtractUserAttributes` | 21（実装 6・呼び出し 6・注記 9） | 集約先の確認 |
| 3 | `"(tags\|projects)"` -- `deploy/` | 0（Grafana の 4 件は無関係） | realm の欠落（訂正 2） |
| 4 | `SetValuedKeys\|IsSetValued\|UserAttributeEncoding` | — | 符号化の唯一点 |

**軸 1 の除外**: `DocumentEndpoints.cs:208` と `QdrantIngestionVectorStore.cs:271` の `["tags"]` は
**文書側のタグ**であり、利用者属性ではない（語が同じで意味が違う。規則 2 の帰結として明示除外）。
`Tests` 配下の一致は**固定対象**であって抽出点ではない。

## 決定（実装方針）

### 決定 1: 抽出点を `BffScopeResolver.ExtractUserAttributes` 1 つへ集約する（→ [[IADR-0411]]）

6 箇所すべてが `Platform.Shared.Infrastructure` を**既に参照している**（実測: 5 サービスの `.csproj` で確認）。
`ClaimsPrincipal` を受ける多重定義を足し、`HttpContext` 版はそれへ委譲する。
**残り 5 箇所の private 実装は削除し、共有の 1 つを呼ぶ。**

**なぜ集約するか**: 本 issue は「6 箇所のうち 0 箇所が集合値を運ばない」欠陥だが、
**次に起きるのは「6 箇所のうち 5 箇所だけ直した」欠陥**である。issue 自身が
「1 箇所でも漏れるとその経路だけ判定が変わる」と書いている。**構造で消す。**

### 決定 2: 集合値クレームは `FindAll` で読み `UserAttributeEncoding.Join` で線上表現へ

Keycloak の多値属性マッパー（`multivalued: true`）は **JSON 配列**を発行し、
.NET は**同じ型の複数クレーム**へ写す。`FindFirst` では先頭 1 値しか取れない
（それが #1243 で実測された畳み込みそのものである）。
**単値キー（`clearance` / `department`）の読み方は 1 文字も変えない。**

### 決定 3: `MatchesUserConditions` を `ADR-0080` 決定 2・3 の意味論へ合わせる

集合値キー（`UserAttributeEncoding.IsSetValued`）のときだけ、
利用者側を `Split` して**許容値集合との交差が空でないこと**を判定する。
単値キーは従来どおり `Contains`。**「属性を持たない場合はマッチしない」は変えない**（決定 3 が追認済み）。
**利用者条件が空のとき全利用者にマッチする規則も変えない**（#1324 / PR #1325 が両方向で固定した）。

### 決定 4: realm の `abac-attributes` へ `tags` / `projects` の多値マッパーを足す

`reconcile-realm.js` は既存 scope の protocol mapper も差分適用する（`planMappers`・`:284`）ため、
稼働 realm へも届く。

### 決定 5: dev seed の属性辞書へ `tags`（`scope=user`）を足す。`projects` は足さない

射程の項に記した理由による。

## テスト（受け入れ基準）

- [x] Given `tags` を利用者条件に持つ allow ポリシー / When 当該タグを**含む集合**を持つ利用者 /
      Then **許可が出る**（交差判定・陽性）
- [x] Given 同じポリシー / When タグを持たない利用者 / Then **許可が出ない**（陰性対照）
- [x] Given 同じポリシー / When **別のタグだけ**を持つ利用者 / Then 許可が出ない
      （「集合値なら常に true」の縮退実装を落とす）
- [x] Given `projects` を利用者条件に持つポリシー / When 交差する集合を持つ利用者 / Then **許可が出る**。
      交差が空／属性そのものが無い場合は出ない（`ResolveScope_EverySetValuedKey_UsesIntersectionSemantics`）
- [x] Given 単値キー（`clearance`）のポリシー / When 従来どおりの利用者 / Then **従来どおり通る**（回帰なし）
- [x] Given 多値クレーム（同じ型のクレームが 2 つ）/ When 抽出 / Then **線上表現へ連結される**（先頭 1 値に畳まれない）
- [x] Given 単値クレーム / When 抽出 / Then **値が 1 文字も変わらない**
- [x] 6 経路すべてが共有の抽出点を呼ぶ（重複実装が 0 件であることを走査で示す）
- [x] realm の `abac-attributes` が `tags` / `projects` の多値マッパーを持つ
- [x] 変異試験: 交差判定を「常に true」/「常に false」/「先頭 1 値へ畳む」へ倒すと**新試験が赤になる**

## 変異試験（実走した。実出力を記録する）

適用は `.cjs` をスクラッチパッドへ書いて行い、**毎回 `grep` で着地を確認してから**走らせた。
🔴 **変異を戻したことは「試験が緑に戻った」で確かめた**（#1324 で mtime の罠を踏んだため
適用器は `fs.utimesSync` で mtime を明示的に進める）。

**基準（変異なし・実装後）**: platform 失敗 0 / 合格 **1644**（1631 → +13）／
knowledge 失敗 0 / 合格 **2150**（+5）。`AuthorizationService.Tests` は 204 → **213**。

| # | 変異 | 位置 | 赤くなった試験 |
| --- | --- | --- | --- |
| **N-1** | 集合値の判定を**常にマッチ**へ | `AbacEvaluator` | **3 本** — `..._DoesNotMatchWhenIntersectionIsEmpty` ＋ `..._EverySetValuedKey_UsesIntersectionSemantics` の 2 ケース（`tags` / `projects`） |
| **N-2** | 集合値の判定を**決してマッチしない**へ（改修前と同値の縮退） | `AbacEvaluator` | **6 本** — `..._MatchesWhenIntersectionIsNotEmpty` ＋ `..._UsesTheContractSplittingRule` 3 ケース ＋ `..._EverySetValuedKey_...` 2 ケース |
| **N-3** | 抽出を `FindFirst` で**先頭 1 値へ畳む**（#1243 の欠陥の再現） | `BffScopeResolver` | **4 本 / 4 アセンブリ** — `ExtractUserAttributes_MultiValuedTagClaims_AreJoinedNotCollapsed`（Bff）＋ AiAnalysis / Retrieval / Graph の各経路 1 本ずつ |
| **N-4** | 集合値を**まったく運ばない**（改修前の状態） | `BffScopeResolver` | **5 本 / 4 アセンブリ** — 上記 4 本 ＋ `ExtractUserAttributes_CarriesEverySetValuedKeyDeclaredByTheContract` |
| **N-5** | **1 経路だけ**共有点から離れ単値 2 つだけを載せる（経路ごとのドリフト） | `GrpcGraphNeighborExpander` | **1 本だけ** — `GrpcGraphNeighborExpanderTests.集合値の利用者属性も本文で運ぶ` |

**N-5 が issue の受け入れ基準 4 そのものである** —— 1 経路だけ載せ忘れると**その経路の試験だけ**が赤になる。
他経路の試験は緑のままであり、**巻き添えで拾っているのではない**ことも同時に示している。

**N-1 と N-2 は対である。** 片方だけだと縮退実装（常に true / 常に false）が通る。

## 🔴 実装中に判明したこと（予定に無かった事実）

### `ADR-0080` が引く実装 ADR の番号が違う

同 ADR は 2 箇所（§フォローアップ 2・§関連）で **`IADR-0386`** を「集合値の符号化」として引くが、
本リポジトリの `IADR-0386` は **SC-03 → SC-18 の導線**（無関係・`Proposed`）である。
符号化は **`IADR-0385`**（`Accepted`）が持つ。**決定の内容は変わらないが、
フォローアップ 2 を根拠に着手する者が前提を読み違える。** planning#568 として環流した。

### 抽出の複製は移送のたびに 1 つ増えていた

6 つ目（`GrpcDocumentTagWriter`）は **PR #1322 が足したもの**であり、
#1323 の起票時（同じ PR の作業中）の走査から漏れていた。**自分が足した複製を自分の走査が拾えていない。**
規則 2（あり得る形をすべて列挙してから引く）を破っていた —— 受け皿の変数名だけで引いていた。

## やらないこと

- `projects` を dev seed の属性辞書へ足すこと（`ADR-0085` が保留）
- 束縛変数の新設（`ADR-0085` 決定 3 / `IADR-0253` 決定 3）
- `project` を文書条件の判定軸へ加えること（`ADR-0085` 決定 2）
- `roles` を ABAC の判定へ用いること（`ADR-0080` 決定 4）
- 単値キーの読み方・分割規則を変えること（`IADR-0385` の禁則）

## ★［2026-09-07 追記 / #1326 レビュー］`projects` の直接の担保を足した

初稿の交差判定の試験は **`tags` キーでしか組み立てておらず**、issue の受け入れ基準
「Given `projects` / When 同上 / Then 同上」を**推論で満たしたことにしていた**
（「`tags` で通るから `projects` も通るはず」）。搬送側には
`ExtractUserAttributes_CarriesEverySetValuedKeyDeclaredByTheContract` を置いていたのに、
**評価器側に同じ担保が無かった**。

`ResolveScope_EverySetValuedKey_UsesIntersectionSemantics`（`[Theory]`・`tags` / `projects`）を足し、
1 ケースの中で**陽性・交差が空・属性が無い**の 3 方向を通す。
`projects` は dev seed の属性辞書へ入れていないが、**評価器は辞書と独立に動く**ため直接評価できる。

再実測: **N-1 は 1 本 → 3 本**、**N-2 は 4 本 → 6 本**が赤になる（`projects` ケースを含む）。
`AuthorizationService.Tests` 211 → **213**。


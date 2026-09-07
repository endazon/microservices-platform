---
title: IADR-0411 利用者属性の抽出点をプラットフォームに 1 つだけ置き、集合値キーの列挙を契約へ委ねる。評価器は ADR-0080 決定 2 の交差意味論へ合わせる
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-09, FR-16, SC-09, SC-12, SC-17, ADR-0004, ADR-0036, ADR-0062, ADR-0080, ADR-0085, ADR-0086, IADR-0253, IADR-0385, IADR-0410]
author: claude
created: 2026-09-07
updated: 2026-09-07
---

# IADR-0411: 利用者属性の抽出点を 1 つにし、評価器を交差意味論へ合わせる

## 状況

`ADR-0080`（2026-09-05・オーナー裁定）が **利用者属性 `tags` を定義**し、**集合値のマッチを
「交差が空でないこと」と定めた**。同 ADR のフォローアップ 1・2 は実装へ次を差し戻している。

1. 属性辞書へ `tags`（`scope=user`・集合値）を登録できるようにし、**dev seed へも追加する**
2. `AbacEvaluator.MatchesUserConditions` を決定 2・3 の意味論へ合わせる

実装側は #1323 で「利用者属性を運ぶ 4 箇所すべてが `clearance` / `department` の 2 つしか載せない」と
起票していた。**着手前に引き直したところ、issue の記述が 2 点誤っていた。**

### 🔴 実測 1 —— 抽出点は 4 ではなく **6** である

起票時の走査は `attrs["clearance"]` の形だけで引いており、**受け皿の別の形を落としていた**
（母集合の規則 2「あり得る形をすべて列挙してから引く」の破れ）。

| # | 位置 | 受け皿 | 起票時の 4 件に含まれたか |
| --- | --- | --- | --- |
| 1 | `Platform.Shared.Infrastructure/Foundation/Authz/BffScopeResolver.cs` | `attrs` | ○ |
| 2 | `AiAnalysisService/Features/Analysis/AnalysisEndpoints.cs` | `attrs` | ○ |
| 3 | `GraphService/Domain/Ports/IGraphAccessResolver.cs` | `attrs` | ○ |
| 4 | `WikiService/Infrastructure/ExternalServices/WikiAccessResolver.cs` | `attrs` | ○ |
| 5 | `GraphService/Infrastructure/ExternalServices/GrpcDocumentTagWriter.cs` | `attrs` | 🔴 **漏れ** |
| 6 | `RetrievalService/Infrastructure/ExternalServices/GrpcGraphNeighborExpander.cs` | `context.UserAttributes` | 🔴 **漏れ（形が違う）** |

**6 つは論理が 1 文字も違わない。** 5 つが `HttpContext` を、1 つが `ClaimsPrincipal` を受ける違いだけである。

### 🔴 実測 2 —— `tags` / `projects` のクレームは realm に存在しない

`deploy/keycloak/microservices-platform-realm.json` の client scope `abac-attributes` が持つ
protocol mapper は **`clearance` / `department` / `groups` の 3 つだけ**だった。
**抽出点を直しても、読むクレームが発行されていない。**

### 🔴 実測 3 —— 搬送だけでは #1323 の受け入れ基準を満たせない

`MatchesUserConditions` は `allowedValues.Contains(userValue)` の 1 キー 1 値照合であり、
線上表現 `"sales,hr"` は許容値 `["sales"]` の**どれにも一致しない**。
`IADR-0385` 決定 6 は「評価器の意味論は変えない。実装側で決めてよい範囲を超えるので環流した
（planning#545）」としていたが、**その環流の裁定が `ADR-0080` である。保留はここで解ける。**

## 検討した選択肢

1. **6 箇所それぞれに `tags` / `projects` の読み出しを足す** —— 起票時に想定していた形
2. **抽出点を共有の 1 つへ集約し、そこだけを直す**（採用）
3. 契約（`UserAttributeEncoding`）に抽出そのものを持たせる —— 契約プロジェクトが
   `HttpContext` / `ClaimsPrincipal` に依存することになり、依存規則に反する

## 決定

### 決定 1: 抽出点を `BffScopeResolver.ExtractUserAttributes` 1 つへ集約する

`ClaimsPrincipal` を受ける多重定義を足し、`HttpContext` 版はそれへ委譲する。
**残り 5 箇所の private 実装は削除し、共有の 1 つを呼ぶ。**

6 箇所すべてが `Platform.Shared.Infrastructure` を**既に参照していた**（実測: 5 サービスの `.csproj`）。
ユニット外参照は `src/README.md` の依存規則が許す 3 プロジェクトの 1 つであり、新たな依存は生じない。

🔴 **選択肢 1 を採らなかった理由**: 本件の欠陥は「6 か所とも集合値を落としていた」ことだが、
**次に起きるのは「6 か所のうち 5 か所だけ直した」欠陥**である。issue 自身が
「1 箇所でも漏れるとその経路だけ判定が変わる」と書いている。**同じ形が散っていること自体が温床であり、
構造で消す。**

### 決定 2: 集合値キーの**列挙をコード側に持たない**

読むキーは `UserAttributeEncoding.SetValuedKeys` を回して決める。
**抽出点が独自にキーを列挙し始めた瞬間に #1323 が再発する。**

- 多値クレーム（Keycloak の `multivalued: true`）は**同じ型のクレームが複数**として届くため
  `FindAll` で集め、符号化は `UserAttributeEncoding.Join`（唯一の規則）へ委ねる。
  🔴 `FindFirst` は**先頭 1 値へ畳む** —— #1243 で実測した欠陥そのものである。
- 🔴 **単値キー（`clearance` / `department`）の読み方は 1 文字も変えない。**
  一律に連結・分割すると `clearance` の値域へ区切り文字が侵食する（`IADR-0385` の禁則）。
- **集合が空ならキー自体を載せない。** 空文字を載せると「属性は持つが空」となり、
  単値キーの欠落と扱いがずれる（`ADR-0080` 決定 3 の「属性を持たない場合はマッチしない」と噛み合わない）。

### 決定 3: `MatchesUserConditions` を `ADR-0080` 決定 2・3 の意味論へ合わせる

集合値キーのときだけ利用者側を `Split` し、**許容値集合との交差が空でないこと**で判定する。
単値キーは従来どおり `Contains`。

🔴 **部分集合ではなく交差である。** `ADR-0080` 決定 2 は「タグを 1 つ足しただけで既存のアクセスが
失われる」振る舞いを明示的に退けている。`ADR-0062` 決定 2 の部分集合判定は**属性割当の統制**であり、
**向きが逆（狭める）**である。混同しない。

**変えないもの**: 「属性を持たない場合はマッチしない」（`ADR-0080` 決定 3 が実装を追認済み）／
「利用者条件が空なら全利用者にマッチ」（#1324 / PR #1325 が両方向の変異で固定した）。

### 決定 4: realm の `abac-attributes` へ `tags` / `projects` の多値マッパーを足す

`reconcile-realm.js` は既存 scope の protocol mapper も差分適用する（`planMappers`）ため、
稼働 realm へも届く。**クレームが無ければ抽出も判定も空回りする** —— これが先行条件である。

### 決定 5: dev seed の属性辞書へ `tags` を足す。**`projects` は足さない**

`ADR-0080` フォローアップ 1 が挙げるのは **`tags` だけ**である。
`projects` を利用者条件に使う統制は**文書側 `project` との静的ポリシー対**を要し、
`ADR-0085` 決定 1・2 が**必須化と判定軸をいずれも保留**している
（必須化しないまま載せると `project` を持たない文書が全滅する）。

🔴 **辞書に無ければ `AbacValidation.ValidatePolicy` がそのポリシーを作らせない** ——
**保留を機械で守る形**になる。

**ただし符号化・搬送・交差判定は `SetValuedKeys`（`tags` と `projects` の両方）へ一般に効かせる。**
実装が鍵を絞ると**再び「運ばれないキー」が生まれる**——それが本件の欠陥そのものである。
**辞書で止めるのであって、搬送で止めない。**

## 理由

- **集約したのは、複製の数が増える一方だったからである。** #1322 が 6 つ目（`GrpcDocumentTagWriter`）を
  足しており、**移送のたびに 1 つ増える**構造だった。
- **キーの列挙を契約へ寄せたのは、`IADR-0385` が符号化について同じ判断をしているからである。**
  規則を 2 か所に持つ形は、本件の欠陥の作り方そのものである。
- **`projects` を辞書へ入れなかったのは、`ADR-0085` の保留が「まだ決めていない」ではなく
  「必須化が先である」という順序の宣言だからである。** 辞書が門になる。

## 結果

- 良い影響:
  - **`tags` を利用者条件に持つポリシーが初めて機能する。** `ADR-0062` 決定 2 のタグの規則も入力を得る
  - **抽出の複製が 6 → 1 になった。** 「1 経路だけ取り残す」形が構造的に消えた
  - **経路ごとの固定を 3 本置いた**（AiAnalysis / Graph / Retrieval）。1 経路だけ独自抽出へ戻す変異は
    **その経路の試験だけが赤になる**ことを実測した
- 悪い影響・残るもの:
  - **`WikiService` / `GrpcDocumentTagWriter` の 2 経路には試験の座が無い**（private のまま委譲）。
    再インライン化は共有点の試験では捕まらない。**記録に留める**（`IADR-0141`。同型の事故は 1 回目）
  - **`projects` は運ばれるが辞書に無いため使えない。** `ADR-0085` の裁定待ちである
  - **稼働 realm での実測はしていない**（クレーム発行の確認は宣言と `reconcile-realm.js` の読みまで）

## 関連

- 計画 ADR: `ADR-0080`（決定 1・2・3 とフォローアップ 1・2）／`ADR-0085`（`project` の保留）／
  `ADR-0062` 決定 2（部分集合。**向きが逆**）／`ADR-0004`・`ADR-0036`
- 実装 ADR: [[IADR-0385]]（符号化。**前提として扱い覆さない**）／[[IADR-0253]] 決定 3（束縛変数を増やさない）／
  [[IADR-0410]]（本文搬送。6 つ目の複製を足した PR）
- issue: #1323 ／ 文脈 #1324・#1243・#1255
- 環流: planning#568（`ADR-0080` が引く実装 ADR 番号の誤り）

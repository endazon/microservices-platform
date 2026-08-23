---
title: 作業仕様書 — 計画 ADR-0056 / ADR-0058 と planning#470 への追随
type: spec
status: in-progress
related_ids:
  - FR-05
  - FR-06
  - FR-09
  - FR-19
  - FR-20
  - FR-21
  - UC-03
  - UC-11
  - SC-05
  - SC-09
  - ADR-0036
  - ADR-0054
  - ADR-0056
  - ADR-0057
  - ADR-0058
  - IADR-0009
  - IADR-0270
  - IADR-0277
  - IADR-0278
author: claude
created: 2026-08-23
updated: 2026-08-23
---

# 作業仕様書: 計画 ADR-0056 / ADR-0058 と planning#470 への追随

## 背景

本セッションで planning へ起票した環流 6 件（planning#470〜#475）に**すべて裁定が下り**、
`origin/main` へマージされた（`b6c3cc0..c0b9223`。PR planning#476 / #477 / #478）。
新設された計画 ADR は **ADR-0056 / ADR-0057 / ADR-0058** の 3 本である。

利用者裁定（2026-08-23）により、**本 PR で着地させるのは小玉のみ**とし、
**ADR-0057（削除の伝播）は起票して次波へ回す**。

## 母集合の引き方と結果

### 軸 1 —— 「403 を返す」実装箇所（`traceability.repo.md` 規則 1・3・4: 誤りの側から・パスで引く）

```
grep -rn "Status403Forbidden\|Results\.Forbid\|StatusCodes\.Status403" --include=*.cs src/ \
  | grep -v "src/ai-stock-trading" | grep -v "/tests/\|Tests\.cs\|Tests/"
```

**8 件が出た。** 内訳と除外理由:

| 箇所 | 判定 | 理由 |
| --- | --- | --- |
| `DocumentShareEndpoints.cs:34` / `:65` / `:89` | **対象** | 文書単位の判定で 403。読み取り認可を見ていない |
| `DocumentEndpoints.cs:281`（`PUT /documents/{id}/body`） | **対象** | 同上 |
| `BffSessionExtensions.cs:115` | 除外 | セッション（未確立・失効）。文書単位の ABAC ではない |
| `CsrfHeaderMiddleware.cs:26` | 除外 | CSRF ヘッダ欠落。文書単位の ABAC ではない |
| `DocumentBffEndpoints.cs:110`（`Results.Forbid()`） | 除外 | **作成**の拒否。秘匿すべき既存の文書が無い（#1010 の射程） |
| `GraphBffEndpoints.cs:69` | 除外 | `.Produces(...)`＝OpenAPI のメタデータのみ。**返していない**。後段の 403 はロール判定 |

### 軸 2 —— 「403」の文字列を含む行（規則 5: 軸を 1 本で終わらせない）

コメントを含めて走査し、**軸 1 で挙がらなかった返却箇所は 0 件**であることを確認した。
`KnowledgeHealthEndpoints.cs:33` の 403 は**ロール判定**であり、ADR-0056 が
「本 ADR の射程外」と明記した種類である。

**したがって対象は 4 経路。**

### 軸 3 —— `doc_scope` の検証箇所

```
grep -rn "doc_scope\|DocScope" --include=*.cs src/ | grep -v Tests
```

更新経路は `DocumentEndpoints.cs:148`（`PUT /documents/{id}`）と `:184`
（`PATCH /documents/{id}/metadata`）の 2 つ。どちらも `DocScopeProblemOrNull` を呼ぶだけで、
**`DocumentAttributes.ValidateDocScope` は値域（2 値）しか見ない**。

### 軸 4 —— ポリシー保存の検証点

`AbacValidation.ValidatePolicy`（`AbacValidation.cs:56`）を
`AuthzEndpoints.cs:56` / `:74` / `:208` の 3 経路が `ValidatePolicyAsync` 経由で共有している。
**足す場所は 1 か所でよい。**

## 決定と実装方針

### 1. ADR-0056 決定 1 —— 読めない文書への 403 を 404 へ倒す（4 経路）

🔴 **ADR-0056 は「実装側に一律の書き換えは生じない」と結論しているが、その根拠が成立していない。**

同 ADR は `DocumentShareEndpoints` の 403 を「**自分が読める文書**に対する書き込み拒否」＝決定 2 に
当たると整理した。実測すると、この 4 経路は次の形である。

```csharp
var doc = await db.Documents.FindAsync(id);              // 無フィルタ取得
if (doc is null) return Results.NotFound();
if (!DocumentBodyIntake.CanWrite(doc.Attributes, subject))
    return Results.StatusCode(StatusCodes.Status403Forbidden);
```

`CanWrite` は `owner == subject` **だけ**を見る（`DocumentBodyIntake.cs:41-48`）。
**読み取り認可はどこでも評価されていない。** したがって現行の 403 は決定 2 の
「読めるが書けない」ではなく、**読めない文書に対しても返る**。
任意の認証利用者が文書 ID を総当たりし、**403 と 404 の差で実在を判別できる**。

**方針: 4 経路とも 404 へ倒す（fail-closed）。**

- 決定 1（読めない → 404）は**必ず満たす**
- 決定 2 は「403 と**してよい**」であり許可であって義務ではない。**404 でも決定 2 に反しない**
- **DocumentService は ABAC の読み取り判定を持たない**（`AuthorizationService` を呼ぶ口が無い。
  走査で確認）。**判定できないものを「読める」と仮定して 403 を返すことはできない**
- 「owner か否か」を可視判定の代理にしない —— 代理にすると**被共有者が自分に共有された文書を
  見失う**（ADR-0036 D-06 の共有と矛盾する）
- **BFF へ露出する時点（ADR-0056 フォローアップ 2）で可視判定が立つため、そこで 403 を
  選び直せる。** 今それを先取りしない

論拠は **IADR-0277** に残す。

### 2. ADR-0058 決定 2 —— 更新経路で `doc_scope` の変更を拒否

🔴 **「`doc_scope` を含む更新を拒否」と実装してはならない。**
SC-05 のフォームは既存属性をスプレッドして送る（`DocumentForm.tsx:87`）ため、
**機密区分だけを変える通常の保存でも `doc_scope` が同送される**。存在で弾くと SC-05 が全部壊れる。

- **既存値と異なるときだけ拒否する**（ADR-0058 決定 2 の文言どおり）
- **既存値を持たない文書への新規付与も拒否する** —— `doc_scope` は「作成時に確定する」
  （決定 1）ため、後から生やすのは作成時確定に反する。既存文書へ遡及付与しない方針
  （ADR-0054 決定 5 / IADR-0270）とも向きが揃う
- 判定は `DocumentAttributes` へ置き、2 経路が同じ 1 つを呼ぶ

論拠は **IADR-0278** に残す。

**決定 3（SC-05 で編集不可）は実測で既に満たされている** —— フォームに `doc_scope` の入力欄が
無く、既存値をスプレッドで素通しするだけである。**新しい UI は作らない**（過剰実装になる）。
回帰テストで固定するに留める。

### 3. planning#470 —— 多キーの文書条件を持つポリシーの保存を拒否（SC-09・暫定）

`AbacValidation.ValidatePolicy` へ規則を 1 つ足す。**恒久ではなく、消費側が選言へ対応したら外す。**

## テスト

**陰性だけでは緑になる。陽性対照を必ず対で置く。**

| 対象 | 陰性 | 陽性対照 |
| --- | --- | --- |
| 404 是正 | 非所有者は 4 経路とも 404 | **所有者は 200**（「常に 404」の実装を落とす） |
| `doc_scope` 不変 | `organization → private-note` が 400 / 逆も 400 / 新規付与も 400 | **同値の同送は 200** / **`confidentiality` だけの変更は 200** / **`doc_scope` を持たない文書の更新は 200** |
| 多キー拒否 | 文書条件 2 キーは 400 | **1 キーは 201** / **利用者条件は何キーでも通る** |

**追加したテストはすべて変異試験で検出力を実測する**（1 変異ずつ独立に入れる）。

## 射程外（本 PR で実装しないもの）

- **ADR-0057（削除の伝播）** —— 利用者裁定で次波。issue として起票する
- **ADR-0058 フォローアップ 1**（台帳を持たない `private-note` の棚卸し）—— 稼働 DB が要る
- **#1011（版ごとの本文）** —— planning#473 の裁定で案 C が採れることを記録するに留める
- **planning#474 の追随** —— 実装側に SC-08 のチャートは無い（走査で 0 件）。追随不要

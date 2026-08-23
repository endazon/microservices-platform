---
title: IADR-0277 所有者ベース書き込みの拒否は 404 で返す（読み取り判定を持たない層は fail-closed 側へ倒す）
type: impl-adr
status: Proposed
related_ids: [FR-06, FR-19, FR-20, FR-21, UC-03, UC-11, SC-05, SC-19, ADR-0004, ADR-0034, ADR-0036, ADR-0056, IADR-0009, IADR-0253]
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0056_existence-hiding-boundary-404-403.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# IADR-0277: 所有者ベース書き込みの拒否は 404 で返す

- 状態: Proposed
- 日付: 2026-08-23
- 決定者: claude（planning#475 の裁定 = 計画 ADR-0056 への追随）

## 起点・関連

- 計画 `ADR-0056`（planning#475 の裁定。2026-08-23 Accepted）の**フォローアップ 1**:
  「**『読めない文書に対する 403』が残っていれば 404 へ直す**」
- 計画 `ADR-0036` D-04（404 存在秘匿）・`ADR-0034` 決定 8（リンク作成の失敗も「見つからない」）
- 先例 `IADR-0009`（Wiki 閲覧の権限外アクセスは 404）

## コンテキスト

計画 `ADR-0056` は打ち分けの軸を **「主体がその文書を読めるか」** に固定した。

- **決定 1**: 読めないなら **404**。操作が読み取りか書き込みかを問わない
- **決定 2**: 読めるが当該操作の権限が無いなら **403 としてよい**
- **決定 3**: 判定は操作の種別で行わない

同 ADR は本リポジトリの `DocumentShareEndpoints` の 403 を **決定 2 の側**（自分が読める文書に対する
書き込み拒否）と整理し、**「実装側に一律の書き換えは生じない」**と結論した。

### 🔴 実測すると、その整理は成立していなかった

対象の 4 経路はいずれも次の形である。

```csharp
var doc = await db.Documents.FindAsync(id);            // 無フィルタ取得
if (doc is null) return Results.NotFound();
if (!DocumentBodyIntake.CanWrite(doc.Attributes, subject)) return 403;
```

`DocumentBodyIntake.CanWrite` は **`owner == subject` だけ**を見る。
**読み取り認可はこの経路のどこでも評価されていない。**

したがって現行の 403 は「読めるが書けない」を意味しておらず、**読めない文書に対しても返る**。
**任意の認証利用者が文書 ID を総当たりし、403 と 404 の差だけで実在を判別できる。**

`DocumentService` は `AuthorizationService` を呼ぶ口を持たない（走査で確認）。
**この層には「読めるか」を答える手段が無い。**

## 検討した選択肢

1. **拒否を一律 404 へ倒す（採用）**
2. `DocumentService` から `AuthorizationService` の `/authz/scope` を呼び、読み取り判定を先に行う
3. 「owner か否か」を可視判定の代理として使い、owner でなければ読めないとみなす

## 決定

**決定 1: 対象 4 経路の拒否は `404 Not Found` を返す。403 を返さない。**

対象（`Status403Forbidden` を返していた全箇所＝母集合）:

| 経路 | 箇所 |
| --- | --- |
| `GET /documents/{id}/shares` | `DocumentShareEndpoints.cs` |
| `POST /documents/{id}/shares` | 同上 |
| `DELETE /documents/{id}/shares/{subjectType}/{subjectId}` | 同上 |
| `PUT /documents/{id}/body` | `DocumentEndpoints.cs` |

**決定 2: 「読めるか」を判定できない層では、決定 2（403 を返してよい）を使わない。**
計画 `ADR-0056` 決定 2 は**許可であって義務ではない**。読めることを確かめられないまま 403 を
返すのは、決定 1 の違反を「決定 2 に当たると仮定して」隠すことになる。

**決定 3: BFF へこれらを露出する時点で選び直す。**
計画 `ADR-0056` フォローアップ 2 が求めるとおり、BFF には可視判定（`BffScopeResolver`）があるため、
そこでは決定 2 の 403 を正しく選べる。**今それを先取りしない。**

## 理由

- **選択肢 2 は、この PR の射程に対して大きすぎる。** サービス間の新しい同期依存
  （`DocumentService` → `AuthorizationService`）を書き込み経路の前段に置くことになり、
  可用性・遅延・キャッシュ（計画 `ADR-0036` D-14 が主体をキャッシュキーへ含めよと課している）の
  設計が併せて要る。**BFF に口が無く外部から到達できない経路のために払う費用ではない。**
- **選択肢 3 は誤りである。** 計画 `ADR-0036` D-06 は**共有された相手も文書を読める**と定めている。
  「owner でない ⇒ 読めない」と仮定すると、**被共有者が自分に共有された文書を 404 で見失う**。
  可視性の代理に所有を使ってはならない。
- **選択肢 1 は情報を増やさない方向へ倒す。** 404 は 403 が伝える以上のことを伝えない。
  **判定できないときに、より多くを漏らす側を選ぶ理由が無い。**

## 結果

- **良い影響**: 4 経路の存在漏洩が閉じた。計画 `ADR-0056` 決定 1 を、判定できない層でも満たせる。
- **悪い影響 / トレードオフ**:
  - **読めるが所有者でない主体には「見つからない」と見える。** 計画 `ADR-0034` 決定 8 と
    UC-11 / SC-19 が「なぜ張れないのか分からない」を**受け入れ済みの副作用**としているのと同種であり、
    新しい種類の不利ではない。**現時点で BFF に口が無いため、利用者への影響は無い。**
  - **テストが「不在」と「拒否」を区別できなくなる。** これは存在秘匿の狙いそのものであり、
    失われた検出力は**陽性対照**（所有者が同じ経路で成功する）で補う。
- **フォローアップ**:
  1. **BFF へ `/body` / `/shares` を露出する際に決定 3 を実行する**（計画 `ADR-0056` フォローアップ 2）。
  2. **計画側へ実測を環流する** —— 計画 `ADR-0056` の「実装側に一律の書き換えは生じない」という
     **現状認識の記述**は実測と食い違う。**決定そのものは正しく、覆すものは無い。**

## 検証

- 追加: `DocumentExistenceConcealmentTests`（陰性 4 ＝ 実在と不在の応答一致 / 陽性対照 2）
- 更新: `DocumentShareTests`・`DocumentBodyIntakeTests` の 403 期待値
- **変異試験**（1 変異ずつ独立に実施）:
  - 共有 3 経路を 403 へ戻す → **7 件が赤**
  - `PUT /body` を 403 へ戻す → **3 件が赤**

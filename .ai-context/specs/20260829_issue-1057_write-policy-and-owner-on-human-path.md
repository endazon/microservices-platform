---
title: dev の write ポリシーを ADR-0060 の形で置き、人が作る経路で owner を作成者本人にする（#1057）
type: spec
status: done
related_ids: [FR-05, FR-21, UC-05, SC-05, ADR-0036, ADR-0060, IADR-0133, IADR-0253]
author: Claude
created: 2026-08-29
updated: 2026-08-29
---

# #1057: ABAC 正常系（段 13）が 403 で落ちるのを、計画の裁定どおりに塞ぐ

## 1. 裁定（planning#498 → ADR-0060 Accepted）

| 決定 | 内容 |
| --- | --- |
| 1 | 計画は dev シードの**中身**を定めない（`IADR-0133` の領域）。ただし **write ポリシーの「形」は計画が定める** |
| 2 | 🔴 **`userConditions` を置かない。** `clearance` でも `department` でも絞らない |
| 3 | **人が作る経路の `owner` は作成操作を行った利用者本人。** 予約値へ倒す経路を設けない |
| 4 | 段 13 を「シード投入済み」の前提条件に置き換えない |

**環流で提案した `userConditions: { clearance: [...] }` は却下された。** 理由は
`ADR-0036` D-07 の判定条件が `owner` の 1 つだけであり、**条件を足すと dev 既定が計画より狭い統制を作る**
（`clearance: public` の利用者が自分の文書すら書けなくなる）ためである。**この却下は妥当であり、
そのまま従う。**

## 2. 母集合（規則 1・2・9）—— 「owner を設定していない作成経路」で全走査

`OwnerKey`（`= "owner"`）の設定箇所を、**設定している側と設定していない側の両方**で数えた。

| 経路 | 実装 | owner |
| --- | --- | --- |
| 個人資料の作成 | `PrivateNoteEndpoints.cs:257`（`PrivateNoteDefaults`） | ✅ **設定済み** |
| システム投入（コネクタ同期） | `DataSourceSyncService.cs:59`（`item.UpdatedBy`） | ✅ **設定済み** |
| **一般作成 `POST /documents`** | `DocumentEndpoints.cs:67`（SC-05） | ❌ **未設定 ← 本作業の対象** |

🔴 **欠けているのは 1 経路だけである。** ADR-0060 決定 3 は「SC-05 の作成・個人資料の作成を含む」と
書いているが、**個人資料側は既に満たしている**。**「決定が 2 経路に掛かる」と読んで両方触ると、
既に正しい側を壊す。**

**除外**: 更新経路（`PUT /documents/{id}`）は**所有者を変えない** —— 変更できると
ADR-0060 論点② 案 B（作成画面で選ばせる）が裏口から成立する。本作業では触らない。

## 3. 変更点

### 3-1 シード（決定 1・2）

`deploy/local/abac-seed/policies.json` へ 1 件足す。**`userConditions` は書かない。**

```json
{ "name": "dev: 所有者は自分の文書を書ける", "action": "write",
  "documentConditions": { "owner": ["${current_user}"] } }
```

**`owner` を属性辞書（`attributes.json`）へ足さない。** 検証器は
**「辞書に定義済みのキーのみ許可値整合を検証（未定義キーは許容）」**であり（`AbacValidation.cs:124`）、
**利用者名は列挙できない**ため辞書に入れるべきではない。`${current_user}` も辞書外の値として素通りする。

### 3-2 作成経路（決定 3）

`DocumentEndpoints.cs` の `POST /documents` に `HttpContext` を取り、属性へ `owner` を載せる。

🔴 **クライアントが送ってきた `owner` は上書きする。** ADR-0060 は論点② 案 B（作成画面で選ばせる）を
**「自分以外を所有者にした文書を作れてしまう」ため却下**している。**受け取った値を尊重すると、
その却下が API 経由で無効になる。**

🔴 **主体が取れないとき（機械クライアント）は `owner` を載せない。** 決定 3 は
**「人が居る経路」**の既定であり、`ai-stock-trading-kb-writer` のような機械クライアントは対象外である。
**空文字を入れると「所有者が空の文書」ができ、`CanWrite` の `IsNullOrWhiteSpace` 判定を汚す。**

### 3-3 検証器のヒント（#1057 で記録した副次項目）

段 13 の失敗文「ポリシーが投入されていない疑い」は**誤りを誘導する** —— 実測ではポリシーは 5 件
投入済みで、無いのは `write` だけだった。**「write が 0 件」と「全体が 0 件」を区別する。**

## 4. 検証

- `dotnet test`（両ユニット・`Category!=Integration`）
- `dotnet format --verify-no-changes`（両ユニット）
- `node scripts/scripts.test.js` ほか文書系検査一式
- `bash -n scripts/verify-oidc-edge-flow.sh`
- **新規テスト**: 作成時に owner が載ること／クライアントの owner を上書きすること／
  主体が無ければ載せないこと

## 5. 🔴 実測できないこと

**Docker が無く統合スタックを実走できない。** ADR-0060 も
**「実走で 403 が残るなら、原因は本 ADR の想定の外にある」**と明記している。
**段 13 が通ることの確認はマージ後の Integration Stack でしか行えない。**

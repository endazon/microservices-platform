---
title: 作業仕様書 — ABAC スコープ解決の正常系を統合スタックで観測する（#972）
type: spec
status: in-progress
related_ids:
  - FR-05
  - NFR
  - ADR-0036
  - IADR-0248
  - IADR-0251
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - "ADR-0036（ABAC 属性モデル）"
issue: "#972"
---

# 作業仕様書: ABAC 正常系の観測（#972）

## 起点

- `#972`。**この経路の正常系は一度も観測されていない。** 今日 2 つの欠陥が同じ経路で見つかった。
  - `#948` / PR `#966`: BFF が `/feedback/stats` へ資格情報を渡さず、後段の 401 を中継していた（2 箇所）
  - `#958` / PR `#971`: WikiService / AiAnalysisService が `AuthorizationService` を不達ポートで呼び、
    deny-by-default へ静かに縮退していた（3 箇所）
- **両方とも直ったが、確かめたのは否定形と個別の実測だけ**である。

## 🔴 着手前の実測

### 実測 1: 穴はコード上で確定している

`BffScopeResolver.ResolveAsync` は認可サービスが不調なら **`null`** を返し（deny-by-default）、
`DocumentBffEndpoints` の一覧は

```csharp
var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, ct);
if (scope is null)
    return Results.Ok(new List<DocumentDto>());   // ← 200 ＋ 空リスト
```

を返す。**「認可が壊れている」と「文書が 1 件も無い」が、応答上まったく同じ**である。

`scripts/verify-oidc-edge-flow.sh` の段 7 は**状態コードしか見ていない**ので、
**どちらも PASS になる。** 本文は 60 バイト表示するだけで検査していない。

### 🔴 実測 2: CI の新規スタックには文書が 1 件も無い

| 項目 | 実測 |
| --- | --- |
| 文書の初期投入経路 | **無い**（`deploy/local/` にあるのは `abac-seed`＝ABAC ポリシーだけ） |
| `#948` の CI 実測（run `32554867883`） | `/bff/documents → 200 []` ——**空** |

🔴 **したがって「非空リスト」は、書き込みなしには原理的に観測できない。**
`ABACSEED=1`（`#517` / `IADR-0133`。既定オフ）が入れるのは**ポリシーだけ**で、文書は入らない。

さらに `k8s-local-up.sh:443-446` の `ABACSEED` は**失敗しても WARN で通す**（best-effort）。
**投入が失敗したまま「空」になっても、現在は誰も気付かない。**

### 実測 3: 負の対照が realm に既に居る

| 利用者 | realm ロール | 属性 | 期待 |
| --- | --- | --- | --- |
| `developer` | `platform-admin` / `platform-operator` ほか | `clearance=restricted`, `department=engineering` | ポリシーにマッチ → **許可** |
| **`poc-operator`** | **`platform-operator`**（＝一覧の RBAC は通る） | **`{}`（属性なし）** | どのポリシーにもマッチせず → **deny** |

🔴 **`poc-operator` は理想的な負の対照である。** 一覧端点は `RequireRole(Admin, Operator)` なので
**RBAC は通り、ABAC だけで落ちる。** 「RBAC しか効いていない」実装なら一覧が見えてしまうので、
**この対照は RBAC と ABAC を切り分ける。**

資格情報は realm に在る（`developer` / `Developer-2026`、`poc-operator` / `PocOperator-2026`）。

### 実測 4: 書き込み無しでも「許可された」ことは見える

`POST /bff/documents` は **deny なら `Results.Forbid()`（403）**、許可なら後段へ転送する。
**403 か否かは、データが 1 件も無くても判定できる。**
ただし本 issue の中心は「**200 ＋ 空リストが PASS にならないこと**」であり、
**それを実証するには一覧が非空になる必要がある** —— よって書き込みを行う。

### 実測 5: 統合スタックは使える。ただし nightly である

| 項目 | 実測 |
| --- | ---: |
| `Integration Stack` の直近 5 回 | **すべて success**（7〜12 分） |
| 契機 | nightly（UTC 18:30）＋ develop への push ＋ `workflow_dispatch` |
| 現在の門 | `check-stack-ready.js`（**readiness のみ**） |
| `verify-oidc-edge-flow.sh` | **走っていない** |
| `ABACSEED` | **設定されていない** |
| イメージ | **実行内でソースからビルドする**（`[2/7] build & import images`）→ **コード変異も試せる** |

🔴 **PR ゲートには載せない。** `IADR-0248` 決定 3 が上限を定義している ——
「全 PR で起動するジョブは `ci.yml` の `build-and-test` の **2 倍**を超えてはならない」。
本ジョブは 8〜10 分で明確に超える。**nightly に載せる。**

## 決定

### 決定 1: 既存スクリプトへ opt-in の書き込み段を足す（新スクリプトを作らない）

`verify-oidc-edge-flow.sh` はトークン取得（段 1〜6・約 150 行）を持つ。
**新スクリプトへ複製すると二重実装になり、必ず片方が腐る。**

**`ABAC_POSITIVE=1` のときだけ有効な段を足す。既定はオフ。**
🔴 **既定オフである限り、ヘッダの「副作用: 読み取り専用」は保たれる。**
オンのときに何が起きるか（**文書を 1 件作る**）をヘッダへ明記し、
**使い捨てのスタック専用**であることを書く。

### 決定 2: 判定は「正 ＋ 負の対」で置く

| # | 主体 | 操作 | 期待 | 何を守るか |
| --- | --- | --- | --- | --- |
| P1 | `developer` | `POST /bff/documents` | **2xx**（403 でない） | ABAC が**許可を返した**（正の対照） |
| P2 | `developer` | `GET /bff/documents` | **非空**・作成した ID を含む | 🔴 **「200 ＋ 空」を PASS にしない** |
| N1 | `poc-operator` | `GET /bff/documents` | **0 件**（200） | deny が効いている（**RBAC は通る**ので ABAC の切り分けになる） |
| P3 | `developer` | `GET /bff/dashboard/summary` | **200** | `#948` の形（後段への資格情報転送） |

🔴 **N1 に `POST` を使わない。** `POST` は `AdminOnly` で、`poc-operator` は admin を持たないため
**ABAC 以前に RBAC で 403 になる。** 対照として使うと**何が効いたのか分からなくなる。**

### 決定 3: `ABACSEED=1` を CI のこの経路でだけ有効にする

既定オフの設計（`IADR-0133` 決定 4。deny-by-default はセキュリティ上の既定値）は**変えない**。
🔴 **加えて、投入の失敗を握り潰さない** —— 現在は WARN で通るため、
**この経路では投入結果を確認してから判定へ進む**（さもないと「空だから FAIL」の原因が
認可の故障か投入漏れか分からない）。

## 受け入れ基準

1. P1・P2・N1・P3 が統合スタック上で**すべて期待どおり**になる
2. **変異 M1（`#958` の形）**: BFF の `Services__AuthorizationService` を不達ポートにすると
   **P1 が 403 になり、P2 が空で落ちる**
3. **変異 M2（`#948` の形）**: 後段への資格情報転送を外すと **P3 が 401 で落ちる**
4. 🔴 **変異 M3（本 issue の中心）**: `ABACSEED` を入れずに回すと **P2 が落ちる** ——
   **同じ応答（200 ＋ 空リスト）が現在の判定では PASS になることを、実行で対比して示す**
5. `integration-stack.yml`（**nightly**）から実行され、**PR ゲートには載せない**
6. 既定（`ABAC_POSITIVE` 未設定）では**書き込みが 1 件も発行されない**

## 🔴 検出しないこと（明示）

- **`#958` の実際の 3 箇所（WikiService / AiAnalysisService → AuthorizationService）は、
  この E2E では検出できない。**
  - **WikiService を通す BFF 端点が存在しない**（`Knowledge.Bff.Endpoints` に Wiki の口は無い）
  - **`/bff/analysis/*` は LLM を必要とする**ため、CI（鍵なし）では正の側が別の理由で落ちる
  - 🔴 **したがって M1 は「BFF 自身の `AuthorizationService` クライアント」へ当てる。**
    **故障の形は同じ（不達 → 例外 → deny-by-default → 空）だが、
    当てている経路は #958 の経路ではない。** ここを混同しない
- **「直す前に届かなかったこと」は確かめられない。** `#958` は既に直っており、
  本 issue が置くのは**回帰の門**である
- 一覧の**件数の絶対値**は固定しない（投入データに依存する）。**「非空」と「権限が無ければ 0」**で足りる

## 変異の実行計画

イメージを実行内でビルドするので、**config 変異もコード変異も 1 回の `workflow_dispatch`（約 9 分）**で試せる。

| 変異 | 種別 | 手段 |
| --- | --- | --- |
| M1 | config | helm values の BFF `Services__AuthorizationService` を不達ポートへ |
| M2 | **コード** | `DashboardBffEndpoints` の資格情報転送を外す（イメージ再ビルドが要る） |
| M3 | config | `ABACSEED` を外して回す |

🔴 **変異は本 PR のブランチへ混ぜない。** 使い捨てブランチへ載せて dispatch し、観測後に削除する。

## 未決事項

- `ADR-0032`（BFF セッション方式）へ移行するとトークン取得の段が書き換わる。
  本追加は段 7 以降に載るので影響は受けるが、**移行時に一緒に書き換える**（同スクリプトの既存注記と同じ扱い）

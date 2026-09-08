---
title: RetrievalService に east-west gRPC の受け口を開き、権限内属性値の照会を移す（#1255 スライス A）
type: spec
status: done
related_ids: [FR-04, FR-05, NFR-09, NFR-16, UC-01, SC-01, SC-08, ADR-0004, ADR-0029, ADR-0034, ADR-0043, ADR-0075, ADR-0086, ADR-0087, ADR-0088, IADR-0044, IADR-0141, IADR-0151, IADR-0152, IADR-0253, IADR-0272, IADR-0379, IADR-0401, IADR-0402, IADR-0410, IADR-0411, IADR-0412, IADR-0415, IADR-0416, IADR-0417]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 受け口として立つ最初の一枚（#1255 スライス A）

## 起点

- FR-04（RAG・属性値照会）／FR-05（ABAC）／NFR-09（認可）／NFR-16（east-west の統一）／SC-01・SC-08
- 計画 ADR: `ADR-0029`／`ADR-0075`（east-west を gRPC へ）／`ADR-0034` 決定 1（ホップごと判定）／
  `ADR-0043`（権限内属性値の照会）／`ADR-0086` 決定 1（利用者文脈は本文）／
  `ADR-0087` 決定 1（経路 1 は利用者の同一性の唯一の供給路）／`ADR-0088`（着地済み）
- 実装 ADR: [[IADR-0379]]（proto の置き場・s2s・並走）／[[IADR-0401]]・[[IADR-0402]]（面に出さない・
  宛先ごと 1 チャネル）／[[IADR-0410]]・[[IADR-0416]]（解決済み scope を運ばない・受け口が自分で解決）／
  [[IADR-0415]]（narrowing-only）／[[IADR-0417]]（本 PR で新設）
- issue: #1255（**閉じない**。残り 5 経路）

## 現状と壁（着手前に実測）

#1255 の残る east-west 経路のうち RetrievalService 宛は 2 つ。

| # | 経路 | 壁 |
| --- | --- | --- |
| ② | AiAnalysis → `POST /search` | 🔴 **近傍展開が利用者の転送トークンで動いている**（`GrpcGraphNeighborExpander` が `HttpContext.User` を読む）。s2s だけで移すと**展開が黙って空になる** |
| ③ | BFF → `POST /search/attribute-values` | 近傍展開が絡まない。**純粋な輸送の差し替え** |

**もう 1 つの壁（呼び出し元が解決した scope を本文で送る形）は #1339（[[IADR-0416]]）が取り除いた。**
受け口は既に自分で解決するので、面が運ぶのは**利用者文脈だけ**でよい。

⇒ **③ を先に移す**（[[IADR-0417]] 決定 1）。② は `Search` rpc を**別の proto**で新設する（スライス B）。

## 母集合（規則 1・2・9）

**「RetrievalService を受け口として開くために要るもの」**を、既存の受け口 3 つ
（DocumentService / GraphService / AuthorizationService）の**差分**で引いた
（誤りの側＝「片方にあって片方に無い」で引く。片側の一覧を写すと**漏れが構造的に見えない**）。

| 面 | 既存の受け口 | 本 PR | 判定 |
| --- | --- | --- | --- |
| proto | `Protos/<unit>/<service>/v1/*.proto` ＋ `GrpcServices="Both"` | 新設 | ✅ |
| baseline | `scripts/proto-contract-baseline.json` | `--update` | ✅ |
| h2c リスナ | `builder.AddPlatformGrpcListener()` | 追加 | ✅ |
| サービス登録 | `app.MapGrpcService<T>()` | 追加 | ✅ |
| compose | `expose: 8081` ＋ `Grpc__Port` | 追加 | ✅ |
| helm | `grpcPort: 8081`（テンプレートが containerPort / Service / env を組む） | 追加 | ✅ |
| realm | service account ＋ `platform-service` | **既に在る**（retrieval-service / bff とも） | ✅ 不変 |
| secret | `serviceToken.existingSecret` | **既に在る** | ✅ 不変 |
| 呼び出し元の宛先 | `Services__<X>Grpc` | bff へ追加 | ✅ |
| 呼び出し元の資格 | `ServiceToken__*` | **既に在る**（bff） | ✅ 不変 |
| 試験の器 | `Tests/Grpc/GrpcKestrelFactory.cs` ＋ collection ＋ 構成 | 新設 | ✅ |

### 除外したものと理由（規則 6）

- **`/search` の rpc**: 上の壁 2。**同じ proto へ足さない**（[[IADR-0417]] 決定 1）——
  足すと「この面に無い」という宣言が嘘になる（[[IADR-0401]] 決定 2 / [[IADR-0412]] 決定 1）。
- **`/search` への認可付与**（#1318 欠陥 B）: SPA / McpServer への影響を測る必要がある。**別件**。
- **REST の撤去**: 並走中の正は REST（[[IADR-0379]] 決定 5）。全経路が安定した段で反転の IADR を起こす。
- **AiAnalysis 側の宛先**（`Services__RetrievalServiceGrpc`）: スライス B で足す。
  **今足すと `Search` rpc が無い面へ向くだけ**で、意味が無い。

## 決定

[[IADR-0417]] 決定 1〜9 が正本。実装上の要点だけ再掲する。

1. **スライス A / B に割る**。A は `AttributeValues` rpc（呼び出し元は BFF）だけ。
2. 面は `key` / `user` / `narrow_to` の 3 項目。**`scope` を受ける口は開かない。**
3. 絞り込みは**別項目**。BFF は**送らない**（分岐をキー単位 union へ潰さないため）。
4. 面は `ServiceCaller`。**REST の口は残す。**
5. REST と gRPC は `AttributeValuesEndpoint.ListAsync` という**同じ関数**を通る。
6. 「利用者が分からない」は `INVALID_ARGUMENT`。「候補が無い／権限が無い」は**どちらも空配列**。
7. 辞書は面に出さない（BFF が添える）。
8. 試験は**実 Kestrel**で起こし、解決器は**利用者文脈を記録する**器へ差し替える。
9. 呼び出し元の縮退は REST の 2 つの枝を潰さない（不達＝空配列 ／ 後段が答えた失敗＝502）。

## 変異試験（すべて実走・着地を `grep` で確認・戻したことは緑で確認）

| # | 変異 | 赤 |
| --- | --- | --- |
| M-1 | 面から `[Authorize(ServiceCaller)]` を外す | **3** |
| M-2 | `MapGrpcService<AttributeValuesGrpcService>()` を外す | **8** |
| M-3 | `AddPlatformGrpcListener()` を外す | **10**（全滅＝ポートが実際に bind されている証拠） |
| M-4 | 受け口が自分で解決せず全許可を使う | **2** |
| M-5 | 利用者不明を deny（空配列）へ畳む | **1** |
| M-6 | `narrow_to` を読まずに捨てる | **1** |
| M-7 | BFF が gRPC 経路を選ばない（常に REST） | **8** |
| M-8 | BFF が解決済みの文脈を `narrow_to` へ写す | **1**（🔴 下記） |
| M-9 | BFF が全 `RpcException` を空配列へ畳む | **3** |
| M-10 | チャネル登録を `TryAdd` → `Add` | **1** |

🔴 **M-6 が殺したのは、この作業で足した「絞り込みが実際に絞る」試験 1 本だけである。**
既存の T-07（絞り込みは権限を広げない）は**生き残る** —— 権限を広げないことだけを測ると
**`narrow_to` を読まずに捨てる実装が合格してしまう**。陽性対照を対で置いた根拠である。

### 🔴 M-8 は最初 1 度生存した（本作業で最も学びのある実測）

初回、`narrow_to` へ**利用者属性を写す**変異が **16 本すべて緑のまま通った**。
原因は変異でも本体でもなく**器**である ——
`TestAuthHandler` は `clearance` / `department` / 集合値キーのクレームを**1 つも付けない**ので、
`BffScopeResolver.ExtractUserAttributes` は**常に空**を返す。
⇒ `NarrowTo.Should().BeEmpty()` は「写した結果が空だった」ときも成立していた。

**同時に、より重い穴が見えた** —— この器では
**「利用者属性を 1 つも運ばない実装」も緑のまま**である。
面の存在意義（判定の入力を運ぶ。`ADR-0086` 決定 1）が測れていなかった。

⇒ `TestAuthHandler` に `X-Test-Attributes` を足し、属性を持つ主体で測るようにした。
**属性が実際に届いていることを陽性対照として主張**し、そのうえで M-8 を再実行して 1 本が赤になった。

これは #1339 の M-3 / M-4・#1343 の観測点欠如と**同じ形**である ——
**「試験は在るのに、その経路を通っていない」**。器が測りたい対象を潰していた。

## 受け入れ基準

- [x] `knowledge.retrieval.v1.AttributeValues` が実 Kestrel の h2c ポートで往復する
- [x] 面が運ぶのは利用者文脈だけで、`scope` を受ける口が無い
- [x] 受け口が**自分で**スコープを解決していることを、渡された文脈の記録で観測する
- [x] 絞り込みは絞るが広げない（陽性・陰性の対）
- [x] 利用者トークンでは開かない（`PERMISSION_DENIED`）／資格情報なしは `UNAUTHENTICATED`
- [x] REST と gRPC が同じ値を返す（陽性対照つき）／REST の口と 8080 が残っている
- [x] BFF の切替が構成 1 つで決まり、未設定なら DI に 1 つも入らない
- [x] BFF の縮退が REST の 2 つの枝を保つ

## 変えていないもの

- REST の 2 端点（`/search` / `/search/attribute-values`）のコード・契約・応答形・認可の有無。
- 辞書の可視性（[[IADR-0152]]）。一般利用者には `null` のままである。
- realm / secret。**不変**（呼び出し元・受け口とも配備済みだった）。
- `/search` の経路（REST のまま）。AiAnalysis 側の構成も触っていない。

---
title: RetrievalService が ABAC スコープを自分で解決し、呼び出し元の Scope を絞り込みへ降格する
type: spec
status: done
related_ids: [FR-03, FR-04, FR-05, FR-19, NFR-09, UC-01, UC-02, SC-01, SC-08, ADR-0004, ADR-0034, ADR-0036, ADR-0043, ADR-0086, ADR-0087, ADR-0088, IADR-0012, IADR-0044, IADR-0151, IADR-0253, IADR-0335, IADR-0379, IADR-0401, IADR-0410, IADR-0411, IADR-0413, IADR-0415, IADR-0416]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 受け口が自分で解決する（#1339）

## 起点

- FR-03（検索）／FR-04（RAG）／FR-05（ABAC）／NFR-09（認可）／SC-01・SC-08
- 計画 ADR: `ADR-0004`／`ADR-0034` 決定 1（ホップごと判定）／`ADR-0086` 決定 1（利用者文脈は本文）／
  `ADR-0087` 決定 1（**経路 1 は利用者の同一性の唯一の供給路**）／`ADR-0088`（着地済み）
- 実装 ADR: [[IADR-0044]]（最終防衛線）／[[IADR-0335]] 決定 4・[[IADR-0410]]（**呼び出し元が解決した
  scope を運ぶ形は採らない**）／[[IADR-0413]]（認可サービスが属性を引き直す）／
  [[IADR-0415]]（narrowing の共有点）／[[IADR-0416]]（本 PR で新設）
- issue: #1339

## 🔴 現状（実測。`develop` `3e3350fa`）

`RetrievalService` の 2 端点は、**呼び出し元が送ってきた `Scope` をそのまま信じて**絞り込む。

| 受け口 | 利用者を読むか | 権限の根拠 |
| --- | --- | --- |
| `POST /search` | 🔴 **読まない**（`HttpContext` を引数に取らない） | 本文の `Scope` |
| `POST /search/attribute-values` | 🔴 **読まない** | 本文の `Scope` |

`HybridSearchService.cs:49` のコメント自身が危険を言い当てている ——
「**ネットワーク到達可能な相手が ABAC を全面バイパスできてしまう（呼び出し側 Scope の無検証信任）**」。
そこで採られた緩和は「`GrantsAccess=true` の明示を要る」だが、**偽の `Scope` はそう名乗るだけである。**

🔴 **[[IADR-0410]] は gRPC 面について同じ形を明示的に拒んでいる** ——
「受け取った scope をそのまま信じる口を開くと、そこへ到達できる誰もが任意の scope を主張できる」。
**REST 面には同じ統制が無い。しかも [[IADR-0379]] 決定 5 のとおり並走中の正は REST である。**

### 呼び出し元は 2 か所（非テスト）。**どちらも既に利用者トークンを転送している**

| 呼び出し元 | 送る `Scope` | 利用者トークン |
| --- | --- | --- |
| `SearchBffEndpoints.cs:71`（`/bff/search`） | BFF が解決した ABAC スコープ | **転送している**（`:64-66`） |
| `SearchBffEndpoints.cs:149`（`/bff/attribute-values`） | 同上 | 転送していない |
| `RagOrchestrator.cs:232`（AiAnalysis） | **ABAC ∩ データ範囲** | **転送している**（`:227-229`） |

🔴 **これが本 PR を小さくする鍵である。** 受け口は**既に届いている利用者**から自分で解決できる ——
契約を変えずに信頼を外せる。

### 属性値照会の側も同じ

`AttributeValuesEndpoint` は `req.Scope is { GrantsAccess: true }` だけを見る。
**#1340 で塞いだ「指定が広げる」穴は無いが、`Scope` を信じる点は同じである。**

## 決定

### 決定 1: 受け口が**自分で** ABAC スコープを解決する（`ISearchAccessResolver`）

`WikiAccessResolver` / `GraphAccessResolver` と**同型**にする ——
未認証は認可サービスへ問い合わせず deny、REST と gRPC を並走（`Services:AuthorizationServiceGrpc`）、
通信失敗も deny-by-default。

🔴 **判定の位置は動かない**（`ADR-0034` 決定 1 / [[IADR-0044]]）。**動くのは根拠の出所だけである** ——
「呼び出し元が言った」から「自分で引いた」へ。

### 決定 2: 🔴 呼び出し元の `Scope` は**絞り込みへ降格**する（権限の根拠にしない）

捨てるのではなく、[[IADR-0415]] の `ScopeNarrowing` へ**narrowing として**渡す。

**正直な呼び出し元の結果は変わらない:**

| 呼び出し元 | 従前 | 本 PR の後 |
| --- | --- | --- |
| BFF | 送った ABAC がそのまま効く | 自分で引いた ABAC ∩ 送られた ABAC = **同じ** |
| AiAnalysis | 送った ABAC ∩ 範囲が効く | 自分で引いた ABAC ∩ (ABAC ∩ 範囲) = **同じ** |

**変わるのは、偽の主張が効かなくなることだけである。**

🔴 **これにより AiAnalysis のデータ範囲を契約変更なしに保てる。** 範囲は `Scope` に畳み込まれて
届いており、それを narrowing として使えば意味がそのまま残る。

### 決定 3: 🔴 分岐は**名前で対応づけて**交差させる

呼び出し元の `Scope` に分岐があるとき、**キー単位 union へ畳んではならない**
（[[IADR-0253]] 決定 2 の反例）。分岐は**同じ許可**から導かれているので**名前が一致する**。

- 権威側の各分岐に、呼び出し元の同名分岐があれば**フィルタを交差**させる
- 呼び出し元が落とした分岐は**落とす**（narrowing として正当）
- 呼び出し元にしか無い分岐は**無視する**（広げられない）
- **全分岐が消えたときだけ全体 deny**

呼び出し元が分岐を持たず平坦な `Filters` だけを送ってきたときは、**各分岐へ平坦な narrowing を当てる**
（`BuildFilters` の分岐経路が利用者指定を選言全体と AND で重ねるのと同じ姿勢）。

### 決定 4: 未認証は **deny**（状態コードは変えない）

`/search` は `RequireAuthorization` を持たない（#1318 欠陥 B）。
**本 PR で認可は掛けない** —— 掛けると SPA / McpServer への影響を測る必要がある。
ただし**未認証では権限の根拠が無いので deny へ倒れる**（空応答。存在秘匿は現行どおり）。

🔴 **これは欠陥 B の悪用可能性を実質的に塞ぐ** —— 認証なしで `GrantsAccess=true` を主張しても
効かなくなる。**欠陥 B そのもの（認可の不在）は #1318 のまま残す。**

## 触るファイル

| 面 | ファイル | 内容 |
| --- | --- | --- |
| 共有 | `Knowledge.Contracts/Dtos/ScopeNarrowing.cs` | `AccessScope` 同士の交差（決定 3）を追加 |
| 港 | `RetrievalService/Domain/Ports/ISearchAccessResolver.cs`（新規） | 受け口が使う抽象 |
| 客体 | `RetrievalService/Infrastructure/ExternalServices/SearchAccessResolver.cs`（新規） | REST ＋ gRPC 並走 |
| 受け口 | `Features/Search/Hybrid/Endpoint.cs` ／ `AttributeValues/Endpoint.cs` | 自分で解決 → 交差 |
| 合成 | `RetrievalService/Program.cs` | 認可の客体と `IHttpContextAccessor` を**無条件で**登録 |
| 記録 | `.ai-context/adr/IADR-0416_….md` ＋ 索引行 | 決定 1〜4 |

**契約（DTO）は変えない**（決定 2）。**deploy は認可サービス宛の設定が要る**ので実測してから決める。

## テスト（受け入れ基準）

- [x] 🔴 偽の `Scope`（`GrantsAccess=true`・広い許可値）を送っても、自分で引いた許可でしか返らない
- [x] 陽性対照: 正直な `Scope` では結果が変わらない
- [x] 未認証 → 空応答（deny。404 でも 403 でもない）
- [x] 認可サービスが引けない → 空応答（fail-closed）
- [x] 分岐は名前で対応づけて交差する（呼び出し元が落とした分岐は落ちる／広げられない）
- [x] `attribute-values` にも同じ統制が入る
- [x] 既存の検索試験が緑のまま（正直な呼び出しの意味を変えない）
- [x] 変異試験で赤を実測する

## 変異試験（実出力。すべてビルドし直して実走し、戻したことは試験の緑で確認した）

| # | 変異 | 赤 |
| --- | --- | --- |
| M-1 | 権威を引かず主張をそのまま使う（元の欠陥の再現） | **2** |
| M-2 | 属性値照会だけ元へ戻す（片面だけ直す形） | **1** |
| M-3 | 未認証の短絡を外す | **1**（最初は**生存**） |
| M-4 | 呼び出し元が落とした分岐を落とさない | **1**（同上） |

🔴 **M-3 と M-4 は最初 1 度ずつ生存した。それが本作業で最も学びのある実測である。**

- **M-3**: 端点の器が `ISearchAccessResolver` をスタブするので、端点の試験では
  **解決器の短絡も縮退も 1 行も通らない**。⇒ `SearchAccessResolverTests` を新設
  （未認証で**呼び出し回数 0**・陽性対照つき）。
- **M-4**: 分岐の対応づけは `Apply(AccessScope, AccessScope)` にあり、共有点の試験は
  辞書オーバーロードしか通していなかった。⇒ `ScopeNarrowingTests` T-06〜T-09 を追加。

**どちらも「試験は在るのに、その経路を通っていない」形である。** 変異を当てるまで緑に見えていた。

## 実測（数字）

`RetrievalService.Tests` 213 → **224**／`Knowledge.Contracts.Tests` 83 → **89**。
knowledge 全ユニット失敗 0・スキップの増減なし。**契約（DTO）は 1 バイトも変えていない。**

deploy は retrieval へ `Services__AuthorizationService` / `…Grpc` を足した（compose ＋ helm）——
🔴 **`check-bff-downstreams` が実際に止めて教えた**（コード既定 :5005 と実 Service ポート :8080 の食い違い）。

## やらないこと

- **`/search` に `RequireAuthorization` を掛けること**（#1318 欠陥 B。影響範囲の測定が要る）
- **契約から `Scope` を消すこと**（絞り込みとして使い続ける。撤去は #1255 の段で判断）
- **#1255 の gRPC 化そのもの**（本 PR はその前提を作るだけ）

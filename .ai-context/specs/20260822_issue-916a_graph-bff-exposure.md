---
title: グラフ読み取りの BFF 公開 — 権限伝播の方式を選ぶ
type: spec
status: draft
related_ids: [FR-17, UC-10, ADR-0034, ADR-0043, IADR-0242]
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
---

# 仕様書: グラフ読み取りの BFF 公開（#916a / 親 #450・#916 の前半）

> 本書は**着手前**に作成した（#913 で順序を違えた是正）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-17**（探索は ABAC スコープ内に限定し判定はホップごと）
- ユースケース（UC）: **UC-10**
- 関連 ADR: **ADR-0034**（決定 1・2）/ ADR-0043（BFF 境界。読み口は 1 系統）
- 実装 ADR: **IADR-0242**（新規 IADR は起こさない見込み）

## 対象範囲

### 対象

`/bff/graph/*` を追加し、フロントエンドからグラフ読み取りへ到達できるようにする。

- `GET /bff/graph/{documentId}`（起点ノード 1 件）
- `GET /bff/graph/{documentId}/neighbors?hops=`（近傍探索）
- `docs/api/openapi.yaml` への追記 ＋ orval 生成物のコミット

### 対象外

- **RAG への統合**（#947 = #916b）
- 辺の型辞書の BFF 公開（SC-09 / SC-10 の画面が `ADR-0039` のゲート下にあり、口だけ先に置く理由が無い）
- SC-18 / SC-21 の画面（`ADR-0039` が `Proposed`）

## 🔴 設計の核心: BFF から下流への権限伝播には 2 通りある

本リポジトリの BFF には**権限伝播の方式が 2 つ併存している**。どちらを使うかの規則が明文化されていないため、ここで記録する。

| 方式 | 下流へ渡すもの | 先例 | 下流の性質 |
| --- | --- | --- | --- |
| **A: `Authorization` ヘッダの伝播** | 利用者の JWT | `AnalysisBffEndpoints`（「ABAC 権限解決のため Authorization ヘッダを伝播する（権限外文書を出さない）」）/ `AuthzBffEndpoints` | **下流が自分で ABAC を解決する型** |
| **B: 解決済み `scope` を本文で渡す** | `AccessScope`（BFF が `BffScopeResolver` で解決） | `SearchBffEndpoints` → RetrievalService（`SearchRequest(..., scope, ...)`） | 下流が渡された scope を信頼する型 |

### 本 issue は方式 A を採る

**GraphService は自分で JWT から ABAC を解決する型である**（`GraphAccessResolver.ResolveAsync(HttpContext)`。#908）。したがって:

1. **方式 B を採ると、GraphService に「本文で渡された `scope` を信頼する」口を開けることになる。** その経路へ到達できる誰もが**任意の scope を主張できる**——ホップごと ABAC の型ゲート（`IADR-0242` 決定 2）が、入力の時点で無意味になる
2. 方式 A なら **#908 / #909 が実装・試験した認可がそのまま端から端まで効く**。新しい信頼面を作らない
3. 同一リポジトリ内に先例があり（`AnalysisBffEndpoints`）、**「下流が自分で解決する型なら A」**という規則で説明が付く

**「先例があるから B に揃える」を採らない。** RetrievalService が B なのは既存の設計判断であって、GraphService へ横展開する理由にはならない。**判断の軸は「下流が自分で解決する型かどうか」である。**

> ⚠️ **方式 B そのものが持つリスクは本 issue の射程外である。** RetrievalService の `/search` は
> `req.Scope is not { GrantsAccess: true }` で本文の scope を信頼しており、そのサービスへ直接到達
> できる経路があるなら権限昇格になり得る。**指摘に留める**。必要なら別 issue とする。

### 繋ぎ方の帰結

BFF が JWT を転送しない場合、GraphService は `ctx.User.Identity?.Name` を `anonymous` として解決し、
`Granted=false` へ縮退して**すべて 404 を返す**（`GraphAccessResolver` の deny-closed）。
つまり**ヘッダ伝播を忘れると「全部 404」という形で静かに壊れる**。動くように見える壊れ方ではないが、
**「グラフには何も無い」と読める**ため、テストで固定する（§テスト方針の変異 B-1）。

## 受け入れ基準

- [ ] `/bff/graph/{id}` と `/bff/graph/{id}/neighbors` が BFF 経由で到達できる
- [ ] **BFF 経由でもホップごと ABAC が効く**（権限外ノードが応答に現れない）——否定形と**陽性対照**の対で固める
- [ ] **404 / 403 の使い分けが GraphService と一致する**（権限外・不存在はいずれも 404。403 にしない）
- [ ] `hops` 上限超過が 400 のまま BFF を透過する（BFF で握り潰さない）
- [ ] 未認証は 401
- [ ] `docs/api/openapi.yaml` と orval 生成物が整合する（CI の再生成差分検査）

## テスト方針

🔴 **「GraphService 側で効いているから BFF 経由でも効く」は測った証拠にならない。** BFF 層で改めて測る。

| ケース | 内容 |
| --- | --- |
| B-01 | 権限内の起点 → 200（**陽性対照**） |
| B-02 | 権限外の起点 → 404 |
| B-03 | 不存在の起点 → 404。**B-02 と本文まで一致**（存在秘匿が BFF でも保たれる） |
| B-04 | `A→X→B`（X が権限外）で `hops=2` → **B が現れない**（橋が BFF 経由でも成立しない） |
| B-05 | 同一トポロジで X を許可 → B が現れる（**陽性対照**） |
| B-06 | `hops=4` → 400（BFF が握り潰さない） |
| B-07 | 未認証 → 401 |

### 変異試験の設計

| 変異 | 落ちるべきテスト | 🔴 注意 |
| --- | --- | --- |
| **B-1** `Authorization` の伝播を外す | B-01（陽性対照） | **B-02〜B-04 は落ちない** —— すべて 404 になるので否定形は緑のままである。**陽性対照が無いと「常に 404 を返す実装」が変異試験を通ってしまう** |
| **B-2** 404 を 403 へ変える | B-03（区別不能性） | GraphService と使い分けが食い違う |
| **B-3** 下流の 400 を 200 や 500 へ潰す | B-06 | BFF が上限超過を握り潰す形 |

**B-1 が本 issue で最も重要な変異である。** 否定形だけ並べたテスト群は、権限伝播が壊れたときに
**全部緑のまま**になる。陽性対照を対で置くことでのみ検出できる。

## 計画書との差異

- 差異: なし（現時点）

## 未決事項

1. BFF の権限伝播に 2 方式が併存していることを、**規約として明文化するか**（本書は記録に留める。規約化は別途）
2. 方式 B（本文 `scope`）のリスク —— 射程外。指摘に留める

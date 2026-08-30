---
title: IADR-0313 埋め込みは「決定的なローカル埋め込み」をティアA に足して供給し、検索の命中を統合スタックの門にする
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-03, FR-05, FR-21, UC-01, SC-01, SC-02, NFR, ADR-0016, ADR-0017, IADR-0009, IADR-0025, IADR-0085, IADR-0252, IADR-0255, IADR-0256, IADR-0284]
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-model-routing.md
  - planning:projects/microservices-platform/07_adr/ADR-0017_embedding-model-selection.md
---

# IADR-0313: 決定的ローカル埋め込みで検索の命中を門にする

- 状態: Accepted
- 日付: 2026-08-30
- 決定者: claude（実装判断。#992 案 2 の具体形）

## 起点・関連

- #992「統合スタックで『検索が実際に効くこと』を観測可能にする」の**案 2（埋め込みを CI で供給する）**
- #466「E2E スモークセットを統合スタックで CI 実行可能にする」の未達部分（SC-01 / SC-02）
- 直前の段: [[IADR-0284]]（案 1 ＝ seed。フォローアップ 1 が本 IADR）
- 塞ぐ穴: [[IADR-0255]]（`POST /bff/search` の `200 ＋ 空` が 3 つの失敗と区別できない）
- 先例: [[IADR-0252]]（「200 ＋ 空リスト」を PASS にしない・正と負の対照を対で置く）
- 関連する実装仕様書: [`20260830_issue-992_deterministic-local-embedding.md`](../specs/20260830_issue-992_deterministic-local-embedding.md)

## コンテキスト

[[IADR-0284]] が seed（本文つき文書の投入）まで着地させ、判定の段（`SEARCH_HITS=1`）も
`verify-oidc-edge-flow.sh` に実装した。しかし **CI には載せていなかった**（同 決定 6）。理由は
**埋め込みが供給されないと原理的に落ちるから**である。

```
埋め込みの鍵が無い → /embed が Embedded=false を返す
                  → DocumentUpdatedConsumer が UpsertChunkAsync へ到達しない（fail-closed）
                  → 索引に 1 点も入らない
                  → POST /bff/search は 200 ＋ 空（＝壊れているときと同じ応答）
```

### 実測で分かった追加の事実（想定と違った）

🔴 **「埋め込みが無くても全文側で当たる」という逃げ道は、想定と別の理由で存在しない。**

[[IADR-0284]] 決定 4 は「索引側が止まっているので全文側にも点が無い」と書いた。それは正しいが、
**もっと手前で全文側は死んでいた**。

- `QdrantIngestionVectorStore.EnsureCollectionsAsync` は `CreateCollectionAsync`（VectorParams）だけを呼ぶ
- リポジトリ全体で `CreateFieldIndex` / `TextIndexParams` は **0 件**（実測）
- `QdrantVectorStore.KeywordSearchAsync` の `Match { Text = query }` は text インデックス未作成だと
  `RpcException` になり、`catch` が警告ログ 1 行を残して**空リストへ縮退**する

**ハイブリッド検索の全文側は、実配備では常に 0 件である。** これは本作業の射程外の欠陥として
別途起票する（本 IADR の門はベクトル側で成立する）。

## 検討した選択肢

| # | 案 | 評価 |
| --- | --- | --- |
| 1 | **LlmGateway にティアA の決定的プロバイダを足す**（採用） | 新しい Pod もイメージも要らない。**単体テストできる**。#992 が「決定的なローカル埋め込み」として最初に挙げた形そのもの |
| 2 | ティアA のセルフホスト（TEI / Ruri v3）を CI で立てる | モデル取得（GB 級）と CPU 推論で **CI 予算（8〜10 分）に収まらない**。しかも 768 次元・`..._ruri_v3` で、検索が見るコレクションと食い違う |
| 3 | クラスタ内 stub を立て、Voyage の `BaseUrl` をそこへ向ける | 越境マトリクスを触らずに済むが、**「外部ティアB の宛先」を偽装する**形になる。新イメージ・新 Pod も要る |
| 4 | `POST /bff/search` の応答へ縮退の別を載せる（#992 案 3） | **存在秘匿（[[IADR-0009]]）を崩す。** `docs/api/openapi.yaml`（生成物）と orval 生成物・フロントまで巻き込む |
| 5 | 判定を「全文側で当たる」へ倒す | 上記のとおり**全文側は実配備で常に 0 件**。倒す先が無い |

## 決定

### 決定 1: 穴 1（`200 ＋ 空`）は**検証の仕方**で塞ぐ。契約は変えない

**既知の文書を入れ、既知の語で ≥1 件を要求する。** 3 つの縮退経路（空クエリ／ABAC deny 縮退／
RetrievalService 不達）のどれが起きても 0 件になり、門が落ちる。[[IADR-0252]] と同じ型である。

応答へ「なぜ空か」を載せない —— 権限が無いのか該当が無いのかを利用者に区別させないことが
`SearchBffEndpoints` の設計意図であり、そこは変えない。**縮退の観測は応答の外側（ログ）に既にある**
（`WarnEmbeddingUnavailable` / `RoutingReason`）。案 3 は別 issue として残す。

### 決定 2: 埋め込みは**ティアA の決定的プロバイダ**で供給する

`DeterministicEmbeddingProvider`（keyed `deterministic-embedding`）。
文字 3-gram を FNV-1a 64bit でハッシュし、符号つきで次元へ写して L2 正規化する。

- **`string.GetHashCode` は使わない** —— プロセスごとにランダム化されるため、
  同じ本文が実行ごとに別ベクトルになり、索引と問い合わせが噛み合わなくなる
- **用途（Query / Index）でベクトルを変えない** —— Ruri v3 の 1+3 プレフィクス（#809）は
  モデルが非対称に符号化する前提の作法であり、ハッシングに持ち込むと
  **クエリ側だけ 3-gram が増えて文書から系統的に遠ざかる**
- **零ベクトルを返さない** —— Cosine 空間では比較できず Qdrant が点を拒む

### 決定 3: 🔴 越境判定（`EmbeddingEgress` / `EmbeddingRouter.Route`）は 1 バイトも触らない

本プロバイダは **HTTP を一切行わない**（プロセス内計算）。したがってティアA
（セルフホスト＝社外送信なし）の定義をそのまま満たす。confidential / restricted が
ティアB / C へ出ないという fail-closed は無傷である。

**触ったのは検証器の前提だけ**である —— `EmbeddingRoutingOptionsValidator` の
「ティアA に置けるプロバイダは 1 つ」を **1 対多**へ広げた。
🔴 **ティアB は 1 対 1 のまま**にした。**本文が外部へ出る向きの取り違えは、これまでどおり止まる。**
`EmbeddingEgress.AllowedTiers` の表そのものはテストで固定した（変えていないことの証明）。

### 決定 4: 既定は無効。有効化は `LOCALEMBED=1` の opt-in

`appsettings.json` で `Enabled: false`、チャートで `embedding.deterministicLocal.enabled: false`。
`k8s-local-up.sh` は `LOCALEMBED=1` のときだけ `--set` を 1 つ足す
（**未設定なら `helm upgrade` の引数は 1 バイトも変わらない**）。
`ABACSEED` / `SEARCHSEED` と同型である。

**有効時は起動時に警告を出す**（`Program.cs`）。設定の取り違えで本番へ紛れ込んだとき、
**無言で検索品質だけが落ちる**ことを避ける。

### 決定 5: 配線は 3 サービスを**同時に**行う

| サービス | 注入 | 無いと起きること |
| --- | --- | --- |
| llmgateway | `Embedding__Routing__Endpoints__2__Enabled=true` | 埋め込みが供給されず索引が空のまま |
| ingestion | `Embedding__Collections__2__{Name,VectorSize}` | コレクションが作られず Upsert も残存防止削除も届かない |
| retrieval | `Qdrant__CollectionName` | **索引はされるが検索が別のコレクションを見る**（静かに 0 件） |

**1 つでも欠けると 0 件になり、門は「検索が壊れた」と読める形で落ちる。**
揃っていることは `scripts/k8s-local-up.test.js` が静的に固定する。

### 決定 6: 優先度は 5（最優先）とし、有効時は索引もクエリも同じ点へ寄せる

クエリの埋め込みは機密区分に依らず public 相当へ固定される（`EmbeddingRouter`）。
priority を voyage より下（大きい値）にすると **索引だけがティアA・クエリはティアB** へ分かれ、
別空間になって必ず 0 件になる。**片側だけ寄せる構成に意味は無い。**

## 結果

- **良い影響**:
  - #992 の受け入れ観点「『検索が壊れている』と『該当が無い』が CI で区別できる」が満たされる
  - #466 の未達だった SC-01 / SC-02（検索の命中）が CI で判定される
  - `SEARCHSEED` が**実際に意味を持つ**ようになった（従来は入口条件までしか測れなかった）
- **悪い影響 / トレードオフ**:
  - 🔴 **本門は「語の関連性」を測らない。** ベクトル検索は閾値を持たない kNN であり、
    無関係な語でも最近傍が返る。測っているのは
    **「索引に入っている・ABAC を通る・埋め込みが供給される・後段へ到達できる」の 4 点**である。
  - 🔴 **決定的ハッシュ埋め込みに意味的な近さは無い。検索品質の評価に使ってはならない**
    （nDCG の実測は実モデル ＝ ADR-0017 の仕事）。
  - **コレクションを切り替えても既存文書は再索引されない。** 使い捨てスタックでは無害だが、
    既存クラスタで `LOCALEMBED` を後から立てても、既に入っている文書は旧コレクションに残る。
  - opt-in が 4 つ（`ABACSEED` / `SEARCHSEED` / `LOCALEMBED` ＋ 判定側 `SEARCH_HITS`）になった。
- **フォローアップ**:
  1. **Qdrant の全文（text）ペイロードインデックスが作られていない**（上記コンテキスト）。
     ハイブリッド検索の全文側が実配備で常に 0 件である。別 issue。
  2. #992 案 3（`POST /bff/search` の縮退を応答の外側で区別可能にする）は別 issue のまま。

## 検証

- 単体: `LlmGateway.Tests` 213 → 240 件（決定性・次元・単位長・用途不変・3-gram の重なり／
  ティア↔プロバイダの 1 対多／`AllowedTiers` の不変）
- 静的: `scripts/k8s-local-up.test.js` 107 → 114 件（既定バイト等価・`--set` の単独性・
  appsettings とチャートの一致・3 サービスの同時配線・既定 false）
- `helm template` の**既定描画が develop とバイト等価**であることを diff で実測
- **稼働クラスタ（Rancher Desktop 内蔵 k3s v1.35.4+k3s1）での実走と変異試験**は
  作業仕様書 §実測に記録する

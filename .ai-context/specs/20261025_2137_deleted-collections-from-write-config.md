---
title: 作業仕様書 — 文書の削除の対象コレクションを、取り込みと同じ構成（Embedding:Collections）から導き、決定的ローカル埋め込みの検証スタックで voyage に点が残らないようにする（#2137）
type: spec
status: done
related_ids: [FR-06, FR-19, FR-02, FR-03, ADR-0057, ADR-0016, IADR-0313, IADR-0467, IADR-0497]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0057_deletion-propagation-scope.md（決定 1: 削除は索引〔ベクトルストアのチャンクと埋め込み〕まで及ぶ）
---

# 仕様書: 削除の対象コレクションを書き込み側と同じ構成から導く（#2137）

## 起点（トレーサビリティ）

- 要求: FR-06（文書の削除）・FR-19（完全削除）。計画 ADR-0057 決定 1。親: #2047（指摘 L-8）
- 実装: `RetrievalService/Features/Search/RemoveDeleted/DocumentDeletedConsumer.cs` の「既知の限界」（#1016 の作業仕様書 §限界）。
- 関連 IADR: IADR-0313（決定的ローカル埋め込み）・IADR-0467（束ねる追加コレクション）・IADR-0497（語彙索引）

## 事象（検証スタック限定）

| | 取り込みが書く集合 | 検索が読む＝削除する集合 |
| --- | --- | --- |
| 既定の配備 | voyage（appsettings の `Embedding:Collections:0`）・語彙索引 | 主＝voyage・語彙索引 |
| ティア A 有効（`embedding.enabled`） | ＋ Ruri（helm が `Embedding__Collections__1__*`） | ＋ Ruri（helm が `Qdrant__FusedCollections__0`） |
| **決定的ローカル埋め込み（`embedding.deterministicLocal.enabled`。使い捨ての検証スタック）** | voyage ＋ 決定的（helm が `Embedding__Collections__2__*`）・語彙索引 | **主を決定的へ差し替える**（helm が `Qdrant__CollectionName`）・語彙索引 → **voyage が削除の対象から漏れる** |

## 決定

- 検索サービスの削除の対象に、**取り込みが書くが検索が読まないコレクション**（`WrittenCollections`）を足す。名前は取り込みと同じキー
  `Embedding:Collections:<i>:Name` から導き（空の名前・重複は捨てる。取り込みの `DropUnnamed` と同じ）、主・束ねる追加・語彙索引に在るものは除く。
- 検索サービスの `appsettings.json` に、取り込みと**同じ** `Embedding:Collections`（voyage/1024）を置く。サービスを跨ぐので型では束ねられず、
  一致は `Knowledge.IntegrationTests` の `EmbeddingCollectionsParityTests`（Docker 不要・PR の CI で走る）が固定する。
- helm が足す添字（Ruri＝1・決定的＝2）は検索側へ描かない。それぞれ検索の束ねる追加・主として同じ値（`embedding.collection`・`deterministicLocal.collection`）から
  既に描かれており、読む集合に入っている（描くと chart の描画の試験〔`k8s-local-up.test.js`〕が固定する形を崩すだけで、対象は増えない）。
- 削除だけの対象は、取り込みが起動時に作るコレクションなので、無いうちの削除は no-op にする（語彙索引と同じ。IADR-0497 決定 4）。
- 削除の受け口の期限の本数（`DocumentDeletedTimeouts` の `CollectionCount`）に足す（1 本ごとに期限の下で消すため）。

**採らなかった案**: 取り込みサービス自身に `DocumentDeleted` を購読させ、自分の構成（`DeleteByDocumentFromAllAsync`）で消す。最も直接だが、
イベントのトポロジ（購読者の追加・pipeline.json の段・baseline）と取り込みの版の印（IADR-0595）の扱いが変わり、検証スタック限定の欠陥に対して変更が大きい。

## 母集合（規則 9）

`Embedding__Collections` / `Embedding:Collections` / `Qdrant__CollectionName` / `Qdrant__FusedCollections` を `deploy`・`src`・`scripts`・`docs` で引いた。
- 書き込み側の構成: 取り込みの `appsettings.json`・helm `deployment.yaml`（添字 1・2）・`docker-compose.yml`（添字 1。検索の `SEARCH_FUSED_COLLECTION` と同じ変数＝検索の束ねる追加と同じ値）。
- 読み出し側の構成: 検索の `appsettings.json`・helm（`Qdrant__FusedCollections__0`・決定的モードの `Qdrant__CollectionName`）。
- 削除の受け口: `DocumentDeletedConsumer`（呼び出し点 4 か所の試験を含めて更新）。他に削除の受け口は無い（`DeleteByDocumentAsync` の製品コードの呼び出しはここだけ）。
- 文書: 本件の限界を書いた live な文書は無い（`docs/` を「既知の限界」「voyage」で引いて 0 件）。受け口の注記（コード）を更新した。

## 受け入れ基準

- [x] AC1: 決定的モード（主＝決定的・語彙索引・削除だけの対象＝voyage）で、削除した文書の点が 3 つのコレクションすべてから消え、他の文書は残る（`WrittenCollectionsDeletionTests`）。
- [x] AC2: 本サービスの実際の構成（`appsettings.json`）で、既定の配備では削除だけの対象が空、主を決定的へ差し替えると voyage になる（同上。本番のホストの構成から導く）。
- [x] AC3: 名前が空の定義・重複・読む集合に在る名前は捨てる（同上）。
- [x] AC4: 取り込みと検索の `Embedding:Collections` が同じ値である（`EmbeddingCollectionsParityTests`）。
- [x] 変異: 受け口から削除だけの対象への削除を外すと AC1 が赤。
- [x] RetrievalService 575 が緑。既存の削除の試験（4 か所）は従来どおり。

## PoC の事前手順

**不要。** 既定の配備では削除の対象は変わらない（差が空）。決定的モードは使い捨ての検証スタック専用である。

---
title: IADR-0025 埋め込みを機密区分ルーティング（Voyage 既定＋高機密セルフホスト fail-closed）とモデル別コレクションで実装する
type: impl-adr
status: Accepted
related_ids:
  - ADR-0016
  - ADR-0017
  - ADR-0013
  - ADR-0009
  - FR-02
  - FR-03
  - FR-05
  - UC-04
author: claude
created: 2026-07-07
updated: 2026-07-07
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md (Accepted)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0017_selfhosted-embedding-ruri.md (Accepted)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0013_embedding-model.md (Accepted)"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
related_specs:
  - ../specs/20260707_issue-98_embedding-implementation.md
related_adrs:
  - IADR-0007 (設定駆動の LLM 越境ルーティング)
  - IADR-0022 (既定 opus・ZDR 非対応モデル除外)
  - IADR-0014 (Qdrant 属性ペイロードのネスト構造体)
---

# IADR-0025: 埋め込みを機密区分ルーティングとモデル別コレクションで実装する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-07
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: ADR-0016（`Accepted`、voyage-3.5 既定＋高機密セルフホスト併用）、ADR-0017
  （`Accepted`、セルフホストは Ruri v3）、ADR-0013、ADR-0009、FR-02/FR-03/FR-05、UC-04、08_data-egress-policy。
- 関連する実装 ADR: IADR-0007（設定駆動 LLM ルーティング）、IADR-0022（ZDR 非対応モデル除外）、IADR-0014。
- 関連する実装仕様書: [20260707_issue-98_embedding-implementation.md](../specs/20260707_issue-98_embedding-implementation.md)（Issue #98）。

## コンテキストと課題

全プロバイダの `ILlmProvider.EmbedAsync` が空配列を返すスタブで、埋め込み生成の実体が未結線だった
（計画リポ精査 乖離3）。ADR-0016/0017 は既定を voyage-3.5（ティアB・1024 次元）、高機密
（`confidential`/`restricted`）をセルフホスト Ruri v3（ティアA・768 次元）に固定すると確定した。
埋め込みは取り込み時に**全文書本文**を送信するため、LLM 呼び出しよりデータ露出が大きい。本 IADR は、
これを既存のルーティング資産（IADR-0007 の越境マトリクス）を壊さずどう実装へ落とすかの設計判断を記録する。

## 決定

1. **埋め込みを LLM 生成と別系統に分離する**。旧 `ILlmProvider.EmbedAsync`（空配列スタブ）を削除し、
   `IEmbeddingProvider`（`EmbedAsync(text, model, dimensions)`）を新設する。実装は
   `VoyageEmbeddingProvider`（キー `voyage`、ティアB、`/v1/embeddings`）と
   `SelfHostedEmbeddingProvider`（キー `selfhosted-embedding`、ティアA、OpenAI 互換 `/v1/embeddings`）。
   モデル・次元はルーターの決定から渡し、アダプタは HTTP 契約のみを担う（ADR-0013 の抽象維持）。

2. **埋め込み専用の越境ポリシー `EmbeddingEgress` を新設し、一般 LLM 越境（`EgressMatrix`）より厳格にする**。
   `confidential`/`restricted` は**ティアA（セルフホスト）固定**とする（`EgressMatrix` は confidential に
   ティアB も許容していたが、埋め込みは本文全量送信のため許容しない）。未指定・未知は安全側
   （restricted 相当＝ティアA）。`EmbeddingRouter` がこの判定でエンドポイント・モデル・次元・コレクションを
   決定、または送信を拒否する（設定駆動 `Embedding:Routing`、IADR-0007 と同方針）。

3. **fail-closed**。高機密でティアA（セルフホスト）が無効（既定＝基盤未構築）なら候補が無く送信を拒否する。
   外部（ティアB=Voyage）は高機密区分では**決して候補にならない**ため、本文が外部埋め込み API へ送信される
   ことはない。`/embed` は拒否時に `Embedded=false`・空ベクトルを返し、取り込み側は索引をスキップする。
   次元不整合時も `Embedded=false`（誤次元をモデル別コレクションへ書かない）。

4. **モデル別コレクション分離**。異なるモデルのベクトルは同一空間で比較できないため、コレクションを
   モデル別に分離する（ADR-0016）。`knowledge_chunks_voyage_3_5`（1024 次元）/ `knowledge_chunks_ruri_v3`
   （768 次元）。暫定 1536 次元・単一コレクション（`knowledge_chunks`）からの移行（再索引）が必要。

5. **クエリ埋め込みは既定外部経路（Voyage・1024 次元）に固定する**。RetrievalService は voyage コレクションを
   検索するため、クエリ（`Purpose=Query`）を機密区分に依らず既定外部経路へ固定し、検索対象コレクションと
   次元を一致させる。高機密（ruri/768）コレクションの横断検索は FR-03 の後続課題（本 IADR の対象外）。

6. **残存防止**。取り込み冒頭で**全モデル別コレクション**から当該文書を削除する。機密区分変更
   （例 public→confidential）でモデル/コレクションが変わっても旧コレクションに残存させない（ABAC バイパス防止）。

## 理由

- 埋め込みはデータ露出が最大の経路であり、高機密のセルフホスト固定＋外部を候補から外す fail-closed は、
  「データ越境統制を最優先」とする要求（FR-05・08_data-egress-policy）に整合する。
- 設定駆動（`Embedding:Routing` / `Embedding:Collections`）により、契約認定・モデル差し替え・再索引を
  コード変更なしで運用追従できる（IADR-0007 と同じ利点）。
- `IEmbeddingProvider` 抽象により、プロバイダ追加・差し替えの影響をアダプタに閉じる（ADR-0013）。

## 結果

- 良い影響: 空配列スタブが解消し、実ベクトル（1024 次元）がモデル別コレクションへ索引される。高機密文書の
  本文を外部へ送らない構成がテストで担保される（`EmbeddingEndpointTests` / `DocumentUpdatedConsumerTests`）。
- 悪い影響 / トレードオフ: プロバイダ 2 系統・モデル別コレクションの運用が増える。次元変更（1536→1024）に
  伴う再索引が必要（手順は運用仕様書）。セルフホスト基盤（Ruri v3）は別途構築が必要で、それまで高機密文書は
  索引されない（fail-closed）。
- フォローアップ: (a) セルフホスト基盤（Ruri v3）構築＋社内文書サンプルでの nDCG@10 実測（ADR-0017 の
  事前 PoC 代替）、(b) Voyage 契約のティアB 認定（ゼロ保持・学習不使用・レジデンシー）、(c) 高機密コレクションの
  横断検索（FR-03）。

## 関連

- Supersedes: なし（埋め込みの生成実体を新規結線する。ADR-0013 の抽象を具体化する）。
- Superseded by: なし。

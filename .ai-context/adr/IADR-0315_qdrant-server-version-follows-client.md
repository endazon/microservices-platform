---
title: IADR-0315 Qdrant サーバの版はクライアントライブラリの版へ揃える（gRPC のベクトル表現が版で変わり、次元 0 として黙って拒否される）
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-03, UC-01, UC-04, NFR, ADR-0009, ADR-0016, IADR-0088]
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0009_vector-db-qdrant.md
---

# IADR-0315: Qdrant サーバの版をクライアントへ揃える

- 状態: Accepted
- 日付: 2026-08-30
- 決定者: claude（実装判断。#992 の実測で発見）

## 起点・関連

- 発見の経緯: #992 の実測（[[IADR-0314]] で発行経路を直した直後に露出した）
- 関連する実装仕様書: [`20260830_issue-992_deterministic-local-embedding.md`](../specs/20260830_issue-992_deterministic-local-embedding.md)

## コンテキスト

取り込みが実際に走るようになった直後、**すべてのチャンクが Qdrant に拒否された**。

```
Grpc.Core.RpcException: Status(StatusCode="InvalidArgument",
  Detail="Wrong input: Vector inserting error: expected dim: 1024, got 0")
```

取り込み側の実測値は `vectorLen=1024 dims=1024 embedded=True`（一時的な診断ログで確認）であり、
**アプリは 1024 次元を渡しているのに、サーバは 0 次元を受け取っていた**。

原因は**版の食い違い**である。

| | 版 |
| --- | --- |
| `Qdrant.Client`（.NET・`Directory.Packages.props`） | **1.18.1** |
| Qdrant サーバ（`deploy/local/infra/qdrant.yaml` / `docker-compose.yml`） | **v1.9.2** |

新しいクライアントは密ベクトルを新しい gRPC フィールドへ載せる。古いサーバはそのフィールドを
知らないため、**ベクトルが空だと解釈して次元 0 で拒否する**。要求は届き、エラーも返るが、
**「アプリのバグ」にしか見えない**（アプリ側のログには 1024 が出ている）。

### なぜ今まで露出しなかったのか

**取り込みが一度も走っていなかったから**である（[[IADR-0314]]）。
`UpsertChunkAsync` は実配備で一度も呼ばれておらず、統合テストは Testcontainers が
起こす Qdrant（クライアントに追随した版）を使うため、この食い違いを踏まない。

## 決定

**Qdrant サーバの版を `Qdrant.Client` の版へ揃える（`v1.18.1`）。**
適用先は `deploy/local/infra/qdrant.yaml` と `deploy/docker-compose.yml` の 2 か所。

**クライアントを下げる案は採らない。** 中央パッケージ管理（`Directory.Packages.props`）の
版はほかの理由でも上がるため、**追随の向きは「サーバがクライアントに合わせる」で固定する**。
逆にすると、パッケージを上げるたびに配備が静かに壊れる。

## 結果

- **良い影響**: 取り込みが Qdrant へ実際に書けるようになった（稼働 k3s で 3 点を実測）。
- **悪い影響 / トレードオフ**:
  - **版を 2 か所に書いている**（k8s マニフェストと compose）。単一情報源にはしていない ——
    どちらも「配備の宣言」であり、片方だけを更新すると環境差が生まれる。
    **同時に動かすこと**を本 ADR の記述で担保する。
  - **機械検査は置いていない。** クライアント版（`Directory.Packages.props`）と
    サーバ版（マニフェスト）の突合は、同型の事故がもう 1 度起きたときに足す。
- **フォローアップ**: 既存データがある環境で版を上げるときは、Qdrant のストレージ互換性を
  確認すること（本作業の環境は `emptyDir` で毎回作り直されるため影響が無い）。

## 検証

- 稼働 k3s（Rancher Desktop v1.35.4+k3s1）で `qdrant/qdrant:v1.9.2` → `v1.18.1` へ差し替え、
  同じ文書の取り込みが `Ingestion complete for ...: 3 chunks` で完了し、
  `GET /collections/knowledge_chunks_deterministic_v1` が `points_count=3` を返すことを実測した。
  **差し替え前は同じ経路で 0 点だった。**

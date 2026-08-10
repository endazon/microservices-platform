---
title: 作業仕様書 — Qdrant ABAC 属性ペイロードの格納表現 実機検証（IADR-0014 フォローアップ）
type: spec
status: done
related_ids:
  - FR-05
  - FR-11
  - ADR-0009
author: claude
created: 2026-07-05
updated: 2026-07-06
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0009_vector-store-qdrant.md"
  - "../../CLAUDE.md（トレーサビリティ規約）"
related_specs:
  - ../adr/IADR-0014_qdrant-attribute-payload-key.md
related_adrs:
  - IADR-0014 (Qdrant の ABAC 属性ペイロードはネスト構造体へ統一する（実機検証確定）)
  - IADR-0012 (Retrieval /search の fail-closed で ABAC を強制する)
  - IADR-0004 (ABAC の多値 allow-list・deny-by-default)
---

# 作業仕様書 — Qdrant ABAC 属性ペイロードの格納表現 実機検証

- 起点 ID: FR-05 / FR-11 / ADR-0009
- 関連 Issue: #71（親 #48、PR #70 Closes #58）
- 状態: done（実機検証を実行し、結果に基づき選択肢Cを実装済み）

## 背景・課題

PR #70（Closes #58）で導入した `QdrantVectorStore.ExtractAttributes` は、ABAC 属性
（`confidentiality` 等）を保守的に **2表現**から復元している（IADR-0014 選択肢B）。

- (a) フラットキー `attributes.{k}`（書き込み `UpsertAsync` と同じ表現）
- (b) ネスト構造体 `attributes -> { k: v }`（Qdrant がドットをネストパスとして格納する場合の保険）

書き込みは `QdrantVectorStore.UpsertAsync` および `QdrantIngestionVectorStore.UpsertChunkAsync` が
リテラルなフラットキー `attributes.{k}` で行い、検索フィルタ `BuildAttributeConditions` も同じ
`attributes.{k}` を用いる。**実機 Qdrant が payload キーのドットをリテラルとして格納するのか、
ネストパスとして解釈するのかが未確認**であるため、(b) が実際に必要か（不要な防御的実装か）を
確定できていない。`docs/DEFINITION_OF_DONE.md`「不要な防御的実装がない」の観点で、実機確認のうえ
(b) の要否を確定するのが本作業の目的（Issue #71）。

## 目的・スコープ

- 実機 Qdrant に対し Issue #71 の2確認事項を**再現可能**にする検証ハーネスを用意する。
- 検証結果に応じて IADR-0014 のいずれの分岐へ進むかを機械的に判定する。
- 検証結果（**過剰除外あり**）に基づき、書き込み・フィルタ・復元をネスト構造体へ統一する
  （選択肢C）実装まで本作業のスコープに含める。

## 検証の実行

先行して着手した CI Runner（GitHub Actions・非対話）では Docker デーモン操作が承認待ちで
ブロックされ実機 Qdrant を起動できなかったため、検証ハーネス `scripts/verify-qdrant-attribute-payload.sh`
の追加のみに留めていた。本作業でローカル環境（Rancher Desktop / Docker）で実機 Qdrant
（`qdrant/qdrant:latest`）を起動し、ハーネスを実行して検証を完了した。

```bash
docker run -d --name qdrant -p 6333:6333 qdrant/qdrant:latest
QDRANT_URL=http://localhost:6333 bash scripts/verify-qdrant-attribute-payload.sh
```

**実行結果**:

- (1) 格納表現: フラットキー（リテラル・ドット付き）のまま格納される。
- (2) フィルタ通過: **除外（過剰除外あり）**。
- 判定: IADR-0014 の分岐「書き込み・フィルタ・復元をネスト構造体へ統一（選択肢C）」に該当。

追加確認として、ネスト構造体 `{"attributes": {"confidentiality": "..."}}` で書き込んだ場合は
同じフィルタキー `attributes.confidentiality` が正しく通過することも確認済み（詳細は IADR-0014）。

## 受け入れ基準

- [x] `scripts/verify-qdrant-attribute-payload.sh` が Issue #71 の2確認事項を実機 Qdrant に対して
      再現し、格納表現とフィルタ通過可否を判定・表示する（依存は bash/curl のみ）。
- [x] IADR-0014 のフォローアップ節を実機検証結果・最終決定（選択肢C）・既存データ移行方針で更新する。
- [x] `QdrantVectorStore.UpsertAsync` / `QdrantIngestionVectorStore.UpsertChunkAsync` の書き込みを
      ネスト構造体 `attributes -> { k: v }` へ統一する。
- [x] `QdrantVectorStore.ExtractAttributes` の (a) フラットキー復元パスを削除し、(b) ネスト構造体
      復元のみに一本化する（`docs/DEFINITION_OF_DONE.md`「不要な防御的実装がない」）。
- [x] `QdrantVectorStoreTests` を新しい復元仕様（ネスト構造体のみ）に合わせて更新する。

## 関連

- FR-05（ABAC）、FR-11（機密区分別ルーティング）、ADR-0009（ベクトルDBポート）
- IADR-0014、Issue #71（親 #48、PR #70）

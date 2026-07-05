---
title: 作業仕様書 — Qdrant ABAC 属性ペイロードの格納表現 実機検証（IADR-0014 フォローアップ）
type: work-spec
status: review
related_ids:
  - FR-05
  - FR-11
  - ADR-0009
author: claude
created: 2026-07-05
updated: 2026-07-05
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0009_vector-store-qdrant.md"
  - "../../CLAUDE.md（トレーサビリティ規約）"
related_specs:
  - ../adr/IADR-0014_qdrant-attribute-payload-key.md
related_adrs:
  - IADR-0014 (Qdrant の ABAC 属性ペイロードは両表現で復元し、フィルタキー解釈を実機確認する)
  - IADR-0012 (Retrieval /search の fail-closed で ABAC を強制する)
  - IADR-0004 (ABAC の多値 allow-list・deny-by-default)
---

# 作業仕様書 — Qdrant ABAC 属性ペイロードの格納表現 実機検証

- 起点 ID: FR-05 / FR-11 / ADR-0009
- 関連 Issue: #71（親 #48、PR #70 Closes #58）
- 状態: review

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
- 検証結果に応じて IADR-0014 のいずれの分岐へ進むかを機械的に判定できるようにする。
- **本作業では (b) の削除・選択肢Cへの統一は行わない**。理由は「検証の実行」で後述。

### スコープ外（本 PR では実施しない）

- (b) ネスト復元パスの削除、および対応テストの除去。
- 書き込み・フィルタ・復元のネスト構造体統一（IADR-0014 選択肢C）と既存データ移行。
  → いずれも**検証実行後**に、結果に応じて別 PR で行う。

## 検証の実行（重要・透明性）

本 Issue の本質は「実機 Qdrant での確認」だが、Issue に着手した CI Runner（GitHub Actions・
非対話）では **Docker デーモン操作が承認待ちでブロックされ、実機 Qdrant を起動できない**。
外部ドキュメント取得（`WebFetch`）も未許可のため、公式ドキュメントの引用による裏取りもできない。

したがって「実機で確認した」と偽って結論を出すことはせず、代わりに **誰でも実機 Qdrant に対して
2確認事項を実行できる検証スクリプト** `scripts/verify-qdrant-attribute-payload.sh` を追加する。
検証は Qdrant を起動できる環境（ローカル / Qdrant service を持つ CI ジョブ）で実行し、その出力を
もって IADR-0014 のフォローアップ・チェックボックスを確定する。

```bash
docker run -d --name qdrant -p 6333:6333 qdrant/qdrant:latest
QDRANT_URL=http://localhost:6333 bash scripts/verify-qdrant-attribute-payload.sh
```

スクリプトは Qdrant REST API に対し、実装と同じリテラルなフラットキー
`attributes.confidentiality` で point を upsert し、
(1) 返却ペイロードのキー表現（フラットキー / ネスト構造体）と、
(2) `attributes.{k}` フィルタが書き込んだ点を通過するか（過剰除外の有無）
を判定し、IADR-0014 のどちらの分岐に進むべきかを表示する。

## 受け入れ基準

- [ ] `scripts/verify-qdrant-attribute-payload.sh` が Issue #71 の2確認事項を実機 Qdrant に対して
      再現し、格納表現とフィルタ通過可否を判定・表示する（依存は bash/curl のみ）。
- [ ] IADR-0014 のフォローアップ節に、検証ハーネスの場所と「検証実行前は (b) を保持する」旨を明記する。
- [ ] 既存の単体テスト（`QdrantVectorStoreTests`）が引き続き green（本作業では実装を変更しない）。

### 検証実行後（別作業・本 PR のスコープ外）

- **過剰除外なし & フラットキー格納**の場合: `ExtractAttributes` の (b) を削除し、ネスト表現の
  テストを除去、IADR-0014 を「フラットキー確定・(b) 削除」で更新する。
- **過剰除外あり**の場合: 書き込み・フィルタ・復元をネスト構造体へ統一（選択肢C）し、
  既存データ移行方針を IADR-0014 に追記する。

## 関連

- FR-05（ABAC）、FR-11（機密区分別ルーティング）、ADR-0009（ベクトルDBポート）
- IADR-0014、Issue #71（親 #48、PR #70）

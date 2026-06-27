---
title: 作業仕様書 — FR-03 ハイブリッド検索（ベクトル＋全文）
type: work-spec
status: in-progress
related_ids:
  - FR-03
  - UC-01
  - NFR (p95 レイテンシ)
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-03)"
  - "../../planning/projects/microservices-platform/03_usecases/ (UC-01)"
  - "../../planning/projects/microservices-platform/07_adr/ (ADR-0009 Qdrant ベクトルDB)"
related_specs:
  - ../specs/20260626_P0_infrastructure-skeleton.md
related_adrs:
  - ADR-0009 (Qdrant ベクトルDB 採用)
  - ADR-0013 (LLM ゲートウェイ経由の埋め込み)
  - ADR-0004 (Keycloak + ABAC)
---

# 作業仕様書: FR-03 ハイブリッド検索（ベクトル検索＋全文検索）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-03 「キーワードと自然文の双方で横断検索できる（ベクトル検索＋全文検索のハイブリッド）」
- ユースケース（UC）: UC-01
- 画面（SC）: （未設定）
- 関連 ADR: ADR-0009（Qdrant）, ADR-0013（埋め込み）, ADR-0004（ABAC）
- 出典: `02_requirements/01_requirements.md`

## 目的・背景

利用者が 1 つの検索窓から、**自然文（意味検索＝ベクトル）** と **キーワード（語句一致＝全文検索）** の双方で
権限内データを横断検索できるようにする。現状の `RetrievalService` はベクトル検索のみで、
キーワード完全一致（型番・固有名詞・略語など、埋め込みが苦手な語）に弱い。本作業で
**ベクトル検索結果と全文検索結果を Reciprocal Rank Fusion (RRF) で統合**し、双方の長所を併せ持つ
ハイブリッド検索を実装する。

## 対象範囲

### 含むもの
- `IVectorStore` に全文（キーワード）検索 `KeywordSearchAsync` を追加。
- Qdrant 実装: ペイロード `text` への全文 `Match` フィルタ＋ ABAC 属性フィルタで候補取得。
- インメモリ実装: 語句オーバーラップによるキーワード検索（テスト/ローカル用）。
- `HybridSearchService`（`IHybridSearchService`）: ベクトル＋全文を実行し RRF で統合。
- `POST /search` をハイブリッド検索に切り替え。
- ユニットテスト（RRF 融合ロジック）＋エンドポイントテスト（融合・権限フィルタ）。

### 含まないもの
- 画面（SC 未設定のため UI は対象外。BFF 集約は別 Issue）。
- ベクトルDB 製品の差し替え（ADR-0009 で Qdrant 確定）。
- インジェスト側のスキーマ変更（`text` ペイロードは既存。全文インデックス作成は運用/ブートストラップで実施）。

## 設計

### 処理フロー

```
POST /search { query, topK, attributeFilters }
   │
   ├─ embed.EmbedAsync(query)                → queryVector
   ├─ store.SearchAsync(queryVector, K*, filters)        … ベクトル検索（意味）
   ├─ store.KeywordSearchAsync(query, K*, filters)       … 全文検索（語句一致）
   │      （両者を並行実行。候補数 K* = max(topK*4, topK)）
   ├─ ReciprocalRankFusion(vectorHits, keywordHits)      … RRF 統合（k=60）
   └─ Take(topK) → SearchResponse{ results, totalHits, elapsedMs }
```

### Reciprocal Rank Fusion (RRF)

各リスト内の順位 `rank`（0 始まり）に対し `score += 1 / (k + rank + 1)`（k=60）を ChunkId 単位で加算し、
合算スコア降順に並べ替える。両方のリストに現れる文書ほど上位になる。順位ベースのため、
ベクトル類似度スコアと全文スコアの**スケール差を正規化なしで吸収**できる。

### 権限（ABAC）

ベクトル・全文の両検索に同一の `attributeFilters` を適用する。
これにより **権限の無い文書は両系統の候補に一切現れず**、融合後にも出現しない（受け入れ基準②を担保）。

### 性能（NFR p95）

- 2 系統を `Task.WhenAll` で**並行**実行し、直列化による遅延増を回避。
- 候補数を `topK*4` に制限し、融合コストを抑える。
- 既存どおり `Stopwatch` で `elapsedMs` を計測して返却（負荷試験の観測点）。

### 全文インデックス（運用メモ）

Qdrant の全文 `Match` はペイロード `text` への **full-text index** を要する。
インデックス未作成時は graceful degradation（全文検索 0 件→ベクトルのみ）とし、検索全体は失敗させない。
インデックス作成（`CreatePayloadIndexAsync`）はコレクション・ブートストラップ（インジェスト側）で実施する（別タスク）。

## 受け入れ基準（本作業で満たす範囲）

- [x] 利用者は 1 つの検索窓（`POST /search`）からキーワード・自然文の双方で横断検索でき、結果に出典（`MarkdownUri`/`DocumentTitle`）が付く。
- [x] 権限の無い文書は検索結果に現れない（ABAC 属性フィルタを両系統へ適用）。
- [ ] 文書更新後 15 分以内に反映（インジェスト経路の責務。本サービスは最新インデックスを参照するのみ）。
- [x] 各サービスを個別デプロイ・ロールバック可能（RetrievalService 単体の変更に閉じる）。
- [ ] p95 レイテンシ目標（負荷試験で別途確認。本作業は並行実行・候補数制限で素地を用意）。

## テスト方針

- **RRF ユニットテスト**: 両リストに出る文書が単独出現より上位になること、片方のみの文書も拾うこと、空入力で空を返すこと。
- **エンドポイントテスト（InMemory）**: キーワード一致文書とベクトル候補が融合され返ること、出典が付くこと。
- **権限フィルタテスト**: `attributeFilters` に合致しない文書が結果に現れないこと。

## 計画書との差異

- 差異: なし（ADR-0009 の Qdrant 制約内で、Qdrant の全文 Match を用いハイブリッドを構成）。
  全文の二次ストア（PostgreSQL FTS 等）は導入せず、ベクトルDB に閉じることで DB-per-Service（ADR-0002）も維持。

## 未決事項

- 全文インデックスのブートストラップ位置（インジェスト側コレクション作成時）の確定 → 別 Issue で対応。
- RRF の k 値・候補数の最終チューニングは負荷/精度試験の結果で見直す。

---
title: IADR-0085 セルフホスト埋め込み（Ruri v3）推論基盤は opt-in 配備物（Helm 専用テンプレート＋compose profile）として追加し、既定は現行 fail-closed を不変に保つ
type: impl-adr
status: Accepted
related_ids:
  - FR-02
  - FR-03
  - ADR-0016
  - ADR-0017
  - IADR-0025
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md (ティアB=Voyage voyage-3.5)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0017_selfhosted-embedding-ruri.md (ティアA=セルフホスト Ruri v3・fail-closed)"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-02 埋め込み／FR-03 検索)"
---

# IADR-0085: セルフホスト埋め込み（Ruri v3）推論基盤の opt-in 配備＋配線

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-02（取り込み・埋め込み）／FR-03（検索）／ADR-0016（ティアB=Voyage）／
  ADR-0017（ティアA=セルフホスト Ruri v3・高機密固定）
- 関連 ADR: [[IADR-0025]]（埋め込みプロバイダのルーティング／機密区分別コレクション／fail-closed の決定＝本 ADR の前提）
- 関連仕様書: `docs/specs/20260719_issue-303_selfhosted-embedding-optin.md`／`docs/operations/operations.md`（埋め込み節）
- Issue: #303（実環境構築前チェック・priority:medium。監査 2026-07-18 / `10d79e0` で検出）

## コンテキストと課題

実環境構築前監査で「**高機密区分（confidential / restricted）の文書が現状索引されない**」ことが Medium 課題として
検出された。これは [[IADR-0025]] / ADR-0017 の **fail-closed 設計として正しい**挙動だが、その前提であるセルフホスト
埋め込み推論基盤（Ruri v3・768 次元・OpenAI 互換 `/v1/embeddings` を持つ TEI / vLLM 等）の**配備物が `deploy/`
（compose / Helm）に存在しない**ため、有効化する手段自体が無い。

- アプリ側 `SelfHostedEmbeddingProvider`（ruri-v3 / 768 次元）は実装済み。`Embedding:SelfHosted:BaseUrl` へ
  `POST /v1/embeddings` するが、BaseUrl 空なら例外＝高機密は fail-closed（外部送信せず索引もしない）。
- `appsettings.json` の `selfhosted-ruri` エンドポイントは `Enabled:false`・`BaseUrl:""`。
- compose の `llm-gateway` は env スイッチ `SELFHOSTED_EMBEDDING_URL` / `SELFHOSTED_EMBEDDING_ENABLED`(既定 false) を
  持つが向き先の推論サービスが無い。Helm の `llmgateway` には埋め込み env 配線が一切無い。

#303 は 2 案（A: 基盤を配備・有効化・nDCG@10 実測／B: 高機密を索引対象外と明示承認）の確定を求める。

決めるべき実装上の論点: (1) A/B いずれを採るか、(2) 配備の有効化方式（既定変更 vs opt-in）、(3) 推論基盤の実体
（TEI vs vLLM）とイメージ／モデルの扱い、(4) llmgateway への配線方法、(5) #275 ドリフト検査との整合、
(6) 稼働環境依存（実モデル・実測）の切り分け。

## 決定

### 1. 案 A のインフラを opt-in 配備物として用意し、既定は現行 fail-closed（案 B 相当）を不変に保つ

案 A と案 B は排他ではなく段階として扱う。**リポには案 A の配備物（Helm/compose）を opt-in（既定オフ）で用意**し、
実際の有効化・実モデル投入・nDCG@10 実測は稼働環境で行う。**未有効化の間の既定挙動は案 B（高機密は索引せず
fail-closed）**であり、これは現行と byte 等価。これにより:

- 受け入れ基準 1（A/B の判断記録）＝「A の配備物を用意し、既定は fail-closed 維持」という判断として本 ADR に記録。
- 稼働環境が provisioner / GPU / モデル取得の前提を満たしたとき、値 1 つ（`embedding.enabled=true`）で案 A へ移行できる。
- 前提未整備の環境に高機密の実索引を持ち込まない **fail-safe**。

### 2. Helm は専用テンプレート `templates/embedding.yaml`（第三者 pull イメージ）で描画し、generic `services` レンジに載せない

推論基盤は自社ビルドの `microservices-platform/*` イメージではなく第三者 pull イメージ（TEI）である。generic
`services` レンジは `global.image.registry` を接頭辞に前置するため第三者イメージに使えない。`minio.yaml` /
`wikijs.yaml` と同型の**専用テンプレート**にし、`.Values.embedding.enabled`（既定 `false`）でゲートする。

- 推論基盤の既定実体は **TEI（`ghcr.io/huggingface/text-embeddings-inference`）**。OpenAI 互換 `/v1/embeddings` を
  提供し、`SelfHostedEmbeddingProvider` の呼び出し契約に一致するため（vLLM も同契約だが、埋め込み専用・軽量・
  CPU 可の TEI を既定に採り、vLLM は values の image 差し替えで代替可能とする）。
- モデル ID（Ruri v3）・イメージ tag は values でパラメータ化し、**既定値はプレースホルダ**として扱う。実値・GPU/CPU の
  選択・モデル取得は稼働環境で確定する（下記 5）。

### 3. llmgateway への配線は `deployment.yaml` の条件ブロックで行い、`embedding.enabled` に従属させる

汎用 `deployment.yaml` に `and $svc.selfHostedEmbedding $.Values.embedding.enabled` を条件とするブロックを追加し、
**有効時のみ** llmgateway へ次を注入する。

- `Embedding__SelfHosted__BaseUrl`＝embedding Service の DNS（`http://embedding-service:<port>`。provider が
  `/v1/embeddings` を後置するため base のみ）。
- `Embedding__Routing__Endpoints__1__Enabled=true`（`appsettings.json` 配列で selfhosted が index 1＝Voyage が
  index 0 の前提。Issue #98 の配列インデックス注意を踏襲。取り違えは起動時 `EmbeddingRoutingOptionsValidator` が
  fail-fast で検知）。

`services.llmgateway.selfHostedEmbedding: true` を values に付す。既定 `embedding.enabled:false` ではブロックが
描画されず、llmgateway の env は現状と byte 等価（`dataSourceSync`/`objectStorage`/`configVersion` と同じ per-service
条件ブロックの慣習）。この配線は datasource ブロック（#305）とは独立の行に置く。

### 4. compose は `profiles: ["embedding"]` の opt-in サービスで追加する

`deploy/docker-compose.yml` に TEI の `embedding` サービスを `profiles: ["embedding"]` で追加する（`--profile
embedding` 指定時のみ起動＝既定オフ）。既存 `llm-gateway` の env スイッチ（`SELFHOSTED_EMBEDDING_URL` /
`SELFHOSTED_EMBEDDING_ENABLED`）はそのままで、プロファイル有効化時に `SELFHOSTED_EMBEDDING_URL=http://embedding:80` /
`SELFHOSTED_EMBEDDING_ENABLED=true` を与えると案 A へ移行できる。build を持たない pull イメージなので **#275
（`check-image-mapping.js`）・images.yml（#268）いずれの検査対象にもならず**、両 CI は緑のまま。

### 5. 実モデル取得・実埋め込み疎通・nDCG@10 実測・Voyage ゼロ保持認定は稼働環境依存として分離する

これらはリポ内では静的検証できない（実モデル DL・GPU/CPU リソース・実データでの精度測定・第三者契約認定）。
本 PR のスコープ外＝**分離**とし、優先度ラベル付きフォローアップ issue に切る。operations.md の埋め込み節に
配備物・分離事項・Voyage ゼロ保持認定の記録枠を追記する（受け入れ基準 3）。

## 影響・トレードオフ

- **後方互換**: 既定 `embedding.enabled:false` で Helm/compose の描画・挙動は現状不変。高機密は従来どおり fail-closed。
- **#275 / images.yml 緑**: 第三者 pull・build 無しのため両検査の対象外。
- **残課題**: 実配備・実測・契約認定は稼働環境依存（分離・フォローアップ）。TEI/vLLM の選択とリソース要件（GPU 要否）は
  稼働環境で確定する。既定イメージ tag / モデル ID はプレースホルダであり、実運用前に固定する必要がある。

## 代替案（不採用）

- **案 B のみ（高機密を恒久的に索引対象外と承認）**: 配備物を用意しないと将来の有効化に手作業が要り、監査指摘の
  根本（基盤不在）が残る。段階移行できる本決定より劣る。
- **generic `services` レンジに載せる**: registry 前置により第三者イメージを描画できず、`microservices-platform/*`
  として扱うと #275 が MAPPING/compose build を要求して失敗する。専用テンプレートが適切。
- **既定オンで配備**: provisioner/GPU/モデル取得の前提を満たさない環境で Pod 起動失敗や高機密実索引の意図せぬ有効化を
  招く。opt-in・既定オフの fail-safe を採る。

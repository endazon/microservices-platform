---
title: セルフホスト埋め込み（Ruri v3）推論基盤を opt-in 配備物として追加し retrieval/ingestion を配線する（Issue #303）
type: spec
status: done
related_ids:
  - FR-02
  - FR-03
  - ADR-0016
  - ADR-0017
  - IADR-0025
  - IADR-0085
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-02 取り込み・埋め込み／FR-03 検索)
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md (ティアB=Voyage voyage-3.5)
  - planning:projects/microservices-platform/07_adr/ADR-0017_selfhosted-embedding-ruri.md (ティアA=セルフホスト Ruri v3)
related_specs:
  - "../adr/IADR-0085_selfhosted-embedding-optin-deploy.md"
  - "../adr/IADR-0025_embedding-provider-routing-and-model-collections.md"
  - "../../docs/operations/operations.md"
---

# 作業仕様書: セルフホスト埋め込み（Ruri v3）推論基盤の opt-in 配備＋配線（Issue #303）

## 目的・背景

実環境構築前監査（2026-07-18・対象 `10d79e0`）で、**高機密区分（confidential / restricted）の文書が
現状索引されない**ことが検出された（Medium）。設計上は正しい fail-closed（[IADR-0025](../adr/IADR-0025_embedding-provider-routing-and-model-collections.md)／ADR-0017）だが、
その前提であるセルフホスト埋め込み推論基盤（Ruri v3・OpenAI 互換 `/v1/embeddings` を持つ TEI / vLLM 等）の
**配備物が `deploy/`（compose / Helm）に存在しない**。

アプリ側（`SelfHostedEmbeddingProvider`・ruri-v3 / 768 次元）は実装済みだが、`Embedding:SelfHosted:BaseUrl`
未設定・`selfhosted-ruri` エンドポイント `Enabled:false` で既定無効。基盤の向き先が無いため k8s では有効化する
手段自体が無い（compose には env スイッチ `SELFHOSTED_EMBEDDING_URL/_ENABLED` だけがある）。

Issue #303 は次の 2 案の確定を求める。

- **案 A**: Ruri v3 推論基盤を配備（Helm/compose 追加）して有効化し、nDCG@10 実測を実施する。
- **案 B**: 高機密文書を索引対象外とする運用を明示承認し記録する（当面の割り切り）。

## スコープ（本 PR）

リポ内で描画・静的検証まで完結する範囲・**opt-in / 既定オフ / fail-safe** に閉じる。

- **案 A のインフラを opt-in で用意**する。既定は現行 fail-closed（案 B 相当の当面割り切り）を維持する。
- 決定は [IADR-0085](../adr/IADR-0085_selfhosted-embedding-optin-deploy.md) に記録する（受け入れ基準 1）。
- **実モデルの取得・実埋め込み疎通・nDCG@10 実測は稼働環境依存＝分離**し、フォローアップ issue に切る（受け入れ基準 2 の実測分）。
- Voyage ゼロ保持契約の認定状況を operations.md に確認・記録の枠として明記する（受け入れ基準 3）。

**境界（触れないもの）**: datasource ブロック（#305）・`k8s-local-up.sh` のクラスタ作成引数（#328）・
frontend/edge・infra 永続化・realm。`values.yaml` は埋め込み該当ブロックの追加のみ。

## 変更内容

1. **Helm 専用テンプレート** `deploy/helm/microservices-platform/templates/embedding.yaml`（新規）。
   `minio.yaml`/`wikijs.yaml` と同型の非 .NET・第三者 pull イメージ用テンプレート。`.Values.embedding.enabled`
   ゲート（既定 `false`）で Deployment/Service を描画。TEI（`ghcr.io/huggingface/text-embeddings-inference`）を
   既定イメージとし、モデル ID（Ruri v3）は values でパラメータ化する（実値は稼働環境で確定）。
2. **top-level values ブロック** `deploy/helm/microservices-platform/values.yaml` の `embedding:`（新規・既定
   `enabled: false`）。generic `services` レンジは registry を前置するため使わず、専用ブロックにする。
3. **配線**: `templates/deployment.yaml` に `and $svc.selfHostedEmbedding $.Values.embedding.enabled` 条件ブロックを
   追加。有効時のみ llmgateway へ `Embedding__SelfHosted__BaseUrl`（embedding Service DNS）と
   `Embedding__Routing__Endpoints__1__Enabled=true` を注入する。`services.llmgateway.selfHostedEmbedding: true` を
   values に追加。既定 `embedding.enabled:false` ではブロック非描画＝**現状維持（後方互換・fail-closed）**。
4. **compose**: `deploy/docker-compose.yml` に `profiles: ["embedding"]` ゲートの opt-in `embedding`（TEI）サービスを
   追加。既定 off。既存 `llm-gateway` の env スイッチ（`SELFHOSTED_EMBEDDING_URL/_ENABLED`）はそのまま。
5. **operations.md**: セルフホスト埋め込み節に「配備物（Helm/compose）の opt-in 追加」「実モデル取得・nDCG@10 実測は
   稼働環境依存＝分離」「Voyage ゼロ保持認定の記録枠」を追記。
6. **IADR-0085**・README 索引 1 行・フォローアップ起票。

## 受け入れ基準（#303 対応）

- [x] 案 A/B の判断が [IADR-0085](../adr/IADR-0085_selfhosted-embedding-optin-deploy.md) に記録される（**案 A インフラを opt-in 提供・既定は fail-closed 維持**）。
- [x] 案 A の配備物（Helm template＋compose service＋配線）が追加され、`helm template` が既定/有効化の双方で描画される。
      **実モデル取得・有効化・nDCG@10 実測は稼働環境依存＝分離**（フォローアップ #336）。
- [x] Voyage ゼロ保持契約の認定状況が operations.md に記録枠として明記される（現状=未認定・実認定は #336 で分離）。
- [x] 既定 `embedding.enabled:false` で `helm template` の llmgateway env が現状と byte 等価（後方互換。`Embedding__SelfHosted__BaseUrl` 出現数 0 を確認）。
- [x] `node scripts/check-image-mapping.js`（#275）が緑（TEI は第三者 pull・build 無しで検査対象外。ドリフト 0・自己試験 17 件 OK）。
- [x] `helm lint` / `helm template`（default・`--set embedding.enabled=true`）が成功。

## 検証手順

```
# Helm 描画（既定＝埋め込み無効）
helm template deploy/helm/microservices-platform | grep -c 'Embedding__SelfHosted__BaseUrl'   # => 0（現状維持）
# Helm 描画（埋め込み有効）
helm template deploy/helm/microservices-platform --set embedding.enabled=true | grep -E 'kind: Deployment|embedding|Embedding__'
helm lint deploy/helm/microservices-platform
node scripts/check-image-mapping.js
```

## 稼働環境依存（分離・フォローアップ）

- 実モデル（Ruri v3）の取得・TEI への load・実埋め込み疎通・nDCG@10 実測（voyage-3.5 比）。
- Voyage AI ゼロ保持契約の実認定。
- 高機密文書の再索引の実運用手順の実行（operations.md「埋め込みプロバイダ」節に既存）。

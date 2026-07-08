---
title: 作業仕様書 — 全 HTTP サービスへの introspection 横展開・ポート申告拡充
type: spec
status: done
related_ids:
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
related_specs:
  - ../adr/IADR-0029_config-info-api-placement-and-drift-granularity.md
  - ./20260708_issue-142_worker-introspection-endpoint.md
  - ../functional/FR-15_config-info-api.md
---

# 作業仕様書: 全 HTTP サービスへの introspection 横展開

Issue: #143（親: #123 ／ IADR-0029 フォローアップ 2）。#142 に続く。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-15（実効構成の取得・ドリフト検出）
- 関連 ADR: ADR-0018（宣言的構成）・IADR-0029（申告規約）・IADR-0017（ネットワーク分離）

## 目的・背景

#112 時点の自己申告は document / wiki の段ホストのみ、#142 で conversion / ingestion ワーカーを追加した。
残る HTTP サービス（retrieval / aianalysis / authorization / dashboard / datasource / feedback /
llm-gateway）へ `MapKnowledgePlatformIntrospection` を横展開し、ポート実装の申告を拡充する。

## 申告モデル（設計判断）

- **段（Step）**: 上記 7 サービスはパイプライン段（`IConsumer`＋`IPipelineStep`）をホストしないため申告なし。
- **ポート（Port）**: 合成ルート（`Program.cs`）で単一の合成可能アダプタを明確に選択している箇所のみ申告する。
  - retrieval-service: `vector-store`→`QdrantVectorStore`（qdrant）、`embedding`→`LlmGatewayEmbeddingService`（llm-gateway）
  - llm-gateway: `llm`→`LlmRouter`（claude/selfhosted/copilot）、`embedding`→`EmbeddingRouter`（voyage/selfhosted）
  - aianalysis / authorization / dashboard / datasource / feedback: 合成可能ポートなし（存在申告のみ・空）
- **コネクタ（Connector）**: データソースコネクタは実行時データ（DB 行）であり静的合成に現れないため、
  静的自己申告の対象外とする（動的コネクタ在庫の集約は別スコープ）。よって当 PR ではコネクタ申告は空。

存在申告のみのサービスも、到達可能性の確認と「段をホストしない」というトポロジ情報を実効構成へ与えるため
横展開する（ドリフト検出が全体トポロジを把握できる）。

## 対象範囲

- 対象:
  1. 上記 7 サービスの `Program.cs` へ `AddKnowledgePlatformIntrospection` 登録 + `app.MapKnowledgePlatformIntrospection()`。
  2. retrieval / llm-gateway はポートを申告。他は存在申告のみ。
  3. compose の BFF `Introspection__Services__*` と Helm `bff.extraEnv` に 7 サービスを追加。
  4. テスト: 各サービスの既存テストプロジェクトへ `/internal/introspection` の到達性検証を追加。
- 非対象: conversion / ingestion（#142）・構成バージョン注入（#144）・即時検出（#145）・動的コネクタ在庫。

## 受け入れ基準

- [ ] すべての HTTP サービスが `GET /internal/introspection` を提供（メッシュ内部限定）。
- [ ] `/bff/admin/config` の実効構成が全サービスの段・ポート選択・コネクタを網羅する（compose/Helm へ収集先追加）。
- [ ] `dotnet build` / `dotnet test` 緑。

## テスト

- 各サービス（7 件）: WebApplicationFactory で `/internal/introspection` → 200・`Service` 名を検証。
  retrieval / llm-gateway はポート（`vector-store` / `embedding` / `llm`）が含まれることも検証。

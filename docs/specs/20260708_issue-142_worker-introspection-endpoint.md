---
title: 作業仕様書 — conversion/ingestion ワーカーへの自己申告（イントロスペクション）エンドポイント追加
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
  - ./20260707_FR-15_config-info-api-introspection-drift.md
  - ../functional/FR-15_config-info-api.md
---

# 作業仕様書: ワーカーへの自己申告エンドポイント追加（Unverifiable 解消）

Issue: #142（親: #123 ／ IADR-0029 フォローアップ 1 ／ #118 監査論点 3）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-15（実効構成の取得・ドリフト検出）
- 関連 ADR: ADR-0018（宣言的構成）・IADR-0029（構成情報 API 配置・申告規約）・IADR-0017（ネットワーク分離）・IADR-0026（mTLS）

## 目的・背景

#112 では HTTP 段ホスト（document / wiki）のみ自己申告を配線した。conversion / ingestion は
バックグラウンドワーカー（`Host.CreateApplicationBuilder`・HTTP サーフェス無し）のため未配線で、
担当する宣言段（`convert` / `ingest`）が常に **Unverifiable**（検証不能）扱いとなり、適用漏れ検出の
実効性がない。IADR-0029 は「最小 HTTP サーフェス」での追加を計画済み。

## 対象範囲

- 対象:
  1. 両ワーカーの SDK を `Microsoft.NET.Sdk.Worker` → `Microsoft.NET.Sdk.Web` へ変更し、
     ホストを `WebApplication.CreateBuilder` に切替（実行時ベースイメージは既に `aspnet:10.0`）。
  2. `AddKnowledgePlatformIntrospection` で段を申告（conversion=`convert` / ingestion=`ingest`）し、
     `app.MapKnowledgePlatformIntrospection()` で `GET /internal/introspection` を公開（メッシュ内部限定・ingress 非公開）。
  3. compose: 両ワーカーに `expose: "8080"` を追加し、BFF の `Introspection:Services` に両ワーカーの内部 URL を追加。
  4. Helm values: `bff.introspection.services` に両ワーカーを追加。
  5. テスト: WebApplicationFactory で `/internal/introspection` の到達性と段名を検証。DriftDetector が
     ワーカー段の適用漏れ（MissingApply）を検出できることを検証。
- 非対象: ポート実装・コネクタ申告の拡充（#143）・構成バージョン注入（#144）・即時検出（#145）。

## 受け入れ基準

- [x] 両ワーカーが `GET /internal/introspection` を提供し、メッシュ内部からのみ到達（ingress 非公開）。
- [x] `/bff/admin/config` の実効構成に両ワーカーの段（`convert` / `ingest`）が含まれ Unverifiable でなくなる
      （compose/Helm の `Introspection:Services` へ追加）。
- [x] 適用漏れ（MissingApply）がワーカー段でも検出できるテストがある。
- [x] `dotnet build` / `dotnet test` が緑。

## テスト

- ConversionService.Worker.Tests / IngestionService.Worker.Tests: WebApplicationFactory で
  `/internal/introspection` → 200・段名（`convert` / `ingest`）を含むことを検証。
- BFF DriftDetectorTests: ワーカー段が宣言に有るが実効に無い場合に MissingApply を返すことを検証。

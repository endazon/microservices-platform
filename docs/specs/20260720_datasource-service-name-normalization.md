---
title: 作業仕様書 — 構成ビューア発行者名の表記ゆれ是正（data-source-service → datasource-service）
type: spec
status: done
related_ids:
  - FR-14
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-14 宣言的パイプライン構成 / FR-15 構成情報 API・構成ビューア)"
---

# 作業仕様書: 構成ビューア発行者名の表記ゆれ是正

## 起点・関連

- 計画書 ID: FR-14（宣言的パイプライン構成）／FR-15（構成情報 API・ドリフト検出・構成ビューア）
- 関連 ADR: ADR-0018（宣言的パイプライン構成）
- IADR: 不要（純粋な表示・論理名の表記ゆれ是正。挙動不変で新たな設計判断を伴わない）

## 背景・課題（As-Is）

構成ビューア（FR-15 / SC-11）のイベント接続表示で、`RawDocumentFetched` の発行者が
**`data-source-service`（ハイフン区切り）** と表示される。しかし k8s / compose / Helm values / 自己申告名は
すべて **`datasource-service`**（`AddPlatformIntrospection("datasource-service", …)`、
`Introspection__Services__datasource-service`、`DataSourceService.Api\Program.cs`）であり、
この 1 箇所だけが表記ゆれしている。

由来は `deploy/helm/microservices-platform/files/pipeline.json` の `sources[]` 宣言
（`{ "event": "RawDocumentFetched", "service": "data-source-service" }`）。

### 識別子影響の確認（挙動不変の根拠）

`sources[].service` は **表示・論理名専用**であり、wiring（ルーティング・宛先解決）には使われない。

- `ConfigInspectionService.BuildEventBindings`（`ConfigInspectionService.cs`）で
  `EventBindingDto.publishers` へそのまま文字列出力されるのみ。
- 到達性・ルーティング照合には未使用。`DriftDetector.IsServiceReachable` が照合するのは
  **steps の service 名**のみで、`sources` は照合対象外。datasource-service は step をホストしないため
  この publisher 名は完全に表示専用。
- `scripts/validate-pipeline-config.js` は `sources[].service` の非空チェックのみ（特定名の列挙照合なし）。
- 値 `data-source-service` を assert するテストは存在しない。

→ 表示の表記ゆれのみ。`datasource-service` へ統一しても挙動不変。

## あるべき姿（To-Be）

構成ビューアで `RawDocumentFetched` の発行者が実サービス名 `datasource-service` と表示される。

## 変更対象

1. `deploy/helm/microservices-platform/files/pipeline.json` — `sources[].service` を
   `data-source-service` → `datasource-service`。
2. `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Pipeline/PipelineOptions.cs` —
   コメント例 `（例: data-source-service）` → `（例: datasource-service）`。

### 対象外

- `docs/specs/20260708_issue-111_declarative-pipeline-config.md` のコードフェンス内 `data-source-service`：
  point-in-time の作業仕様書（当時の #111 実装を記録）であり、本リポ規約（adr/specs は point-in-time で不変・
  生き prose のみ追随）に従い据え置く。
- `FR-01_data-source-catalog` / `docs/data/data-source.md` 等の `data-source`（`-service` 無し）：
  「データソース」という**概念/トピック名**でありサービス名ではない。是正対象外。

## 受け入れ基準

- [x] `data-source-service` の実データ・生きたコードコメントが `datasource-service` へ統一されている。
- [x] `node scripts/validate-pipeline-config.js deploy/helm/microservices-platform/files/pipeline.json` が OK（self-test も OK）。
- [x] 構成ビューア関連テスト（ConfigInspectionService / DriftDetector）が緑（publisher 名を assert するテストは存在せず、CI `build-and-test` で確認）。
- [x] #275 `check-image-mapping.js` = ドリフト 0・`pipeline-config` CI 緑（本変更は画像マッピング・helm テンプレートロジック非関与）。
- [x] 挙動不変（`sources[].service` が識別子として使われないことを確認済み）。

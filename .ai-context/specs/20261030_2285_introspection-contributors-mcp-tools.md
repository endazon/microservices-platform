---
title: 作業仕様書 — 自己申告を要求ごとに組み立てる寄与の仕組みを足し、MCP の実効ツール一覧を構成情報 API と SC-11 に載せる（#2285 の (d-2)）
type: spec
status: done
related_ids: [FR-15, FR-16, SC-11, ADR-0018, ADR-0024, IADR-0029, IADR-0646, IADR-0660]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2285"
---

# 作業仕様書 — 自己申告の寄与と MCP の実効ツール一覧（#2285 の (d-2)）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `9d622322`）。**範囲は (d-2) だけ**である（コーディネータの指示）。(b)(ii)（コネクタ一覧の広い読み）は planning#850 の答えを待つので作らないが、後から足せる形にする。

## 現状（実測）

- 自己申告 `ServiceIntrospectionDto` は `AddPlatformIntrospection` が起動時に 1 回だけ作る singleton で、gRPC 面（`IntrospectionGrpcService`）はそれをそのまま返す。
- 契約（proto `ServiceIntrospectionReport`・DTO）は段・ポート・コネクタしか器を持たない。
- MCP サーバーの実効ツール一覧は `ToolCatalog`（singleton。`ToolCatalogRefresher` が周期的に申告と公開構成を突合して更新）が持つ。
  - 計画 `11_mcp-server-integration.md` §5: 「実効ツール一覧は構成情報 API（FR-15）へ申告し、SC-11 構成ビューアで可視化する」。
  - 申告の項目は `name`・`description`・`input_schema`・`required_scope`・`egress_class`（§2）。
- 構成情報 API（BFF の `/bff/admin/config`）は `ConfigInspectionService.Assemble` が自己申告を集めて `EffectiveConfigDto` を組む。SC-11 は `ConfigViewerPage` が表示する。

## 方針（IADR-0660）

1. **寄与 `IIntrospectionContributor`**（`ValueTask<IntrospectionContribution> ContributeAsync(CancellationToken)`）を共通基盤に足す。gRPC 面は要求のたびに、静的な申告へ全ての寄与を足して返す。
   - 寄与の中身はコネクタと MCP の実効ツール。コネクタは (b)(ii) が後から使う器で、本 PR では誰も寄与しない。
   - 登録は `AddIntrospectionContributor<T>()`。
   - 寄与が例外を投げたら握り潰さない。gRPC の失敗として返り、BFF の収集はそのサービスを「到達不能」にする。空の一覧を「ツールが無い」と読ませないためである。
2. **契約へ非破壊の追加**:
   - proto: `repeated McpTool mcp_tools = 5;`（`published_name`・`service`・`required_scope`・`egress_class`）。
   - DTO: `McpToolDto`。`ServiceIntrospectionDto` は末尾に既定値つきの `McpTools`。
   - 実効構成 `EffectiveConfigDto`: `McpTools`（openapi は `required` に足す）。
3. **MCP サーバーの寄与**: `ToolCatalog.PublishedTools` を、公開名の順で寄与する。収集の更新は次の要求に反映される。
4. **集約**: `ConfigInspectionService.Assemble` はサービス横断で結合し、公開名で一意化して公開名の順に並べる。ドリフト検出には使わない（申告と公開構成の突合は MCP サーバーの責務。ADR-0024 §5）。
5. **SC-11**: 実効構成の節に「MCP の実効ツール」の表（公開名・申告元サービス・必要な権限スコープ・越境分類）を足す。0 件は空の状態で出す。

## 受け入れ基準（issue の案のうち (d-2) の 2 つ）

- [x] 自己申告が要求のたびに寄与を反映する（寄与が変わると次の要求の応答が変わる）試験
- [x] 実効構成 API の応答に MCP の実効ツール一覧が含まれ、収集の更新が反映される試験。SC-11 に表示される
- （(b)(ii) は planning#850 の答えの後）

## 母集合（規則 9）

- 契約の追従:
  - `docs/api/openapi.yaml`（EffectiveConfigDto）
  - orval の生成物（`pnpm codegen`）
  - `scripts/contract-schema-baseline.json`（`check-contract-schema --update`）
  - `scripts/proto-contract-baseline.json`（`check-proto-contracts`）
- 実効構成の偽物・固定データ: `git grep -n "connectors:" -- 'src/**/*.ts' 'src/**/*.tsx'` で、SC-11 の試験・e2e の偽物の応答を引き、`mcpTools` を足す。
- 文書: テスト仕様書 SC-11 / FR-15、機能仕様書（FR-15 があれば）。

## 残余リスク

- (b)(ii) は未着手（planning#850）。寄与の器（コネクタ）は用意したが、DataSourceService の寄与は作っていない。
- 公開ツールが多いと応答が大きくなる（いまは数十件の規模）。

---
title: IADR-0660 自己申告に要求のたびの寄与（IIntrospectionContributor）を足し、MCP の実効ツール一覧を構成情報 API と SC-11 に載せる（#2285 の (d-2)。(b)(ii) は同じ仕組みに後から載せる）
type: impl-adr
status: Accepted
related_ids: [FR-15, FR-16, SC-11, ADR-0018, ADR-0024, IADR-0029, IADR-0122, IADR-0135, IADR-0462]
author: claude
created: 2026-10-30
updated: 2026-10-31
related_specs:
  - ../specs/20261030_2285_introspection-contributors-mcp-tools.md
---

# IADR-0660: 自己申告の寄与と MCP の実効ツール一覧（#2285 の (d-2)）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-30
- 決定者: claude（#2285）。範囲を (d-2) に絞り、(b)(ii) は planning#850 の答えを待つことはコーディネータの指示による。

## 起点・関連

- 起点 issue: #2285（#2118 の段 1 の残り。親 #2047 の指摘 M-21）。
- 計画: `11_mcp-server-integration.md` §5「実効ツール一覧は構成情報 API（FR-15）へ申告し、SC-11 構成ビューアで可視化する」、ADR-0018（構成情報 API）、ADR-0024（MCP の公開構成と申告の突合）。
- 前提: IADR-0029（自己申告）、IADR-0462（自己申告の gRPC 面）、IADR-0122（契約の破壊的変更の統制）、IADR-0135（SC-11 は生成された DTO を使う）。

## コンテキストと課題

- 自己申告 `ServiceIntrospectionDto` は `AddPlatformIntrospection` が起動時に 1 回だけ組み立てる singleton で、gRPC 面はそれを返すだけだった。
- MCP の実効ツール一覧は `ToolCatalog` が周期的に（申告と公開構成の突合で）作り直す。起動時の申告には載せられない。
- (b)(ii)（コネクタ一覧の広い読み。登録済みのデータソースと有効・無効）も実行時データで、同じく要求ごとに組み立てる仕組みが要る。こちらは計画の答え（planning#850）を待つ。

## 決定

1. **寄与 `IIntrospectionContributor`** を共通基盤に置く（`ValueTask<IntrospectionContribution> ContributeAsync(CancellationToken)`）。
   - 寄与の中身はコネクタと MCP の実効ツールである。
   - 自己申告の gRPC 面は要求のたびに、静的な申告の後へ全ての寄与を登録順に足して返す。
   - 寄与が 1 つも無いサービスは、従前どおり静的な申告を返す。
   - 登録は `AddIntrospectionContributor<T>()`（同じ型の二重登録は 1 つ）。
   - (b)(ii) は、DataSourceService がコネクタを寄与する形で後から足せる。本決定ではコネクタを寄与する者を作らない。
     ［2026-10-31 追記 / #2285］(b)(ii) は「コネクタを寄与する形」を採らなかった。計画（planning#850 の裁定 (ii)）が「コネクタ一覧」と「コネクタの種別」を別の項目にしたので、
     寄与の器に `DataSources` を足し、別の項目 `dataSources` で載せた（IADR-0666）。器の `Connectors` は、いまも誰も寄与しない。
2. 🔴 **寄与の例外は握り潰さない。** gRPC の失敗として返り、BFF の収集はそのサービスを「到達不能」として扱う（既存の縮退）。空の一覧を返すと「ツールが無い」と読まれ、障害と区別できないためである。
3. **契約は非破壊の追加にする**:
   - proto: `ServiceIntrospectionReport` に `repeated McpTool mcp_tools = 5;`（`published_name`・`service`・`required_scope`・`egress_class`）。
   - DTO: `McpToolDto`。`ServiceIntrospectionDto` と `EffectiveConfigDto` の末尾に、既定値つきの `McpTools` を足す。値が null なら空の配列にするので、応答は常に配列である。
   - openapi では、応答の `mcpTools` を `required` に入れる。
   - 入力スキーマと説明は載せない（構成の可視化に要らず、応答を大きくするだけ）。
4. **MCP サーバーの寄与**（`EffectiveToolsIntrospectionContributor`）は、カタログのその時点の実効ツールを公開名の順に載せる。収集の更新は次の要求に反映される。
5. **集約**（`ConfigInspectionService.Assemble`）はサービス横断で結合し、公開名で一意化して公開名の順に並べる。ドリフト検出には使わない（申告と公開構成の突合は MCP サーバーの責務。ADR-0024 §5）。
6. **SC-11** は実効構成の節に「MCP の実効ツール」の表を足す。列は公開名・申告元サービス・必要な権限スコープ・越境分類で、0 件は空の状態の文言を出し、表を消さない。

## 却下した案

- **自己申告の singleton を周期的に作り直す**: 採らない。サービスごとに更新の契機（カタログの更新・DB の変更）が違い、共通基盤に周期を持たせると古い値を返す窓ができる。要求のたびに読む方が単純で、常に最新である。
- **MCP サーバーの `/tools`（SC-12 の実効ツール一覧）を BFF が直接引く**: 採らない。構成情報 API の集約経路（自己申告の gRPC 面）が 2 本になる。計画は「構成情報 API へ申告」と書く。
- **寄与の例外を握り潰して静的な申告だけを返す**: 決定 2 の理由で採らない。

## 結果

- 良い影響:
  - 計画の「実効ツール一覧を構成情報 API へ申告し、SC-11 で可視化する」が満たされた。
  - (b)(ii) を同じ仕組みへ足す口ができた。
- トレードオフ: 自己申告の 1 回の要求で寄与の分だけ処理が増える（MCP サーバーはメモリ上のカタログを読むだけ）。
- 残余リスク:
  - (b)(ii) は未着手（planning#850）。［2026-10-31 追記 / #2285］IADR-0666 で着地した。
  - 公開ツールが多いと応答が大きくなる（いまは数十件の規模）。
- PoC 配備への影響: 新しい設定・権限は無い。BFF と MCP サーバー（と共通基盤を使う全サービス）の再配備で効く。古い BFF は新しいフィールドを読まないだけで、壊れない（非破壊の追加）。

## 検証

- `IntrospectionGrpcTests`: 寄与が変わると次の要求の応答が変わる。静的な申告は残り、寄与は後に続く。寄与の例外は呼び出しの失敗になる。
- `EffectiveToolsIntrospectionContributorTests`: カタログの更新が次の要求に反映され、公開構成から外すと消える。本番の配線が寄与を登録している。
- `ConfigInspectionServiceTests`: サービス横断で公開名で一意化して並べる。誰も申告しなければ空。
- `ConfigBffEndpointTests`: 応答の JSON に `mcpTools` が載る。
- `ConfigViewerPage.test.tsx`: 表の行（公開名・申告元・スコープ・越境分類）と 0 件の空の状態。
- 契約: `check-proto-contracts`（非破壊の追加）・`check-contract-schema`（非破壊 3 件）・`check-openapi-dto-drift` を通る。

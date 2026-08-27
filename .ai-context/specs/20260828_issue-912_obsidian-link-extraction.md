---
title: 作業仕様書 — Obsidian リンク抽出と辺の差分更新（3 層既定型・未定義型フォールバック）（#912）
type: spec
status: draft
related_ids:
  - FR-17
  - UC-10
  - ADR-0033
  - ADR-0050
  - ADR-0027
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "ADR-0033 決定 3（自動抽出の既定型 related・未定義型はフォールバックし警告）・決定 4（出所の区別）・決定 6（最新版のみ保持・差分更新）・決定 8（Obsidian 構文の 3 意味層写像）"
  - "ADR-0050 決定 3（本文指紋を再取り込み要否の判定に用いてよい）"
related_adrs:
  - IADR-0242
  - IADR-0280
  - IADR-0281
issue: "#912"
---

# 作業仕様書: Obsidian リンク抽出と辺の差分更新（#912）

## 起点と前提

- 波 2（メッセージング）の最終段 **C5**。前提の C1〜C4（E3a/E3b の Wolverine 切替・#1016 削除伝播・
  #911 ABAC デノーマライズ＝`GraphDocumentSyncConsumer` と `ContentFingerprint` 契約）は着地済み。
- issue #912 の裁定コメント（2026-08-22）: 保留は「E3b が `DocumentUpdated` に届くこと」へ変わり、
  **解消済み**。ADR-0050 決定 3 により「指紋が変わっていない `DocumentUpdated` では作り直さない」と
  書いてよい（再取り込み側の最適化を本 issue で入れるかは実装側判断 —— 本 issue では**入れない**。
  リンク抽出側のみに適用する）。
- ADR-0033 未決事項「リンク解決規則」は本 issue で決めて IADR に残す → **IADR-0281**。

## 実装（何をどこへ）

| 変更 | 内容 |
| --- | --- |
| GraphService.Domain | `ObsidianLink` / `ObsidianLinkParser`（純粋パーサ: frontmatter 明示指定＋本文の `[[...]]` / `![[...]]` / 標準 Markdown リンク。フェンス内除外）・`EdgeTypeResolver`（3 層既定の写像。IADR-0280 決定 2: ドメインモデル → Domain。**本プロジェクト初の実コード**） |
| GraphService.Application | `Foundation/Ports/IGraphContentReader`（本文取得ポート。null = 取得不能＝抽出スキップ） |
| GraphService.Infrastructure | `Composable/Adapters/StorageContentReader`（WikiService の `StorageMarkdownReader` と同型。storage:// は `IObjectStorageClient`・http(s) は HttpClient。**プレースホルダーへは縮退しない** —— 縮退本文で抽出すると実リンクの辺が全削除されるため null を返す）。csproj へ `Platform.Shared.Infrastructure` の ProjectReference（ユニット外参照の許可 3 プロジェクトの 1 つ） |
| GraphService.Api | `Edge.ExtractedFrom`（Guid?・auto 辺の抽出起点。migration `AddEdgeExtractedFrom`）・`LinkEdgeSynchronizer`（タイトル解決＋辺の差分適用）・`EdgeTypeFallbackMetrics`（未定義型フォールバックのカウンタ）・`GraphDocumentSyncConsumer` の拡張（指紋変化時のみ 本文読取 → 抽出 → 差分）・`Program.cs` の DI 配線 |
| S6 | `deploy/docker-compose.yml` graph-service へ `*objectstorage-env`、helm `values.yaml` `graph:` へ `objectStorage: true`（GraphService からの storage 読み取りは新規接続） |
| S4 / S5 / 契約 | **変更なし**（下記「母集合」の除外理由） |

### 設計判断（要点。論拠の正本は IADR-0281）

1. **差分の母集合は `Provenance == Auto && ExtractedFrom == 当該文書`。**
   対称型（related）は書き込み時に (min, max) へ正規化され（IADR-0242 決定 9）、Source 列が抽出
   起点を表さなくなる。ADR-0033 決定 6 の「**当該文書を起点とする**自動抽出の辺を作り直す」を
   列なしで実装すると、他文書の本文から抽出した related 辺まで巻き込んで消す。auto 辺は本実装が
   唯一の生成者であり（User = GraphEndpoints・AiApproved = AiSuggestionEndpoints のみ）、遡及は不要。
2. **抽出の契機は本文指紋の変化のみ**（ADR-0050 決定 3）。指紋 null（発行側が指紋化できなかった）
   では抽出しない —— 却下解除（ADR-0050 決定 2）と同じ判定材料・同じ倒し方（誤発火させない側）。
3. **取得不能（storage 未配備・URI 未指定・スキーム不明）は抽出スキップ**。辺を一切触らない。
   storage の実取得の失敗（例外）は送出し Wolverine のリトライへ委ねる（WikiService と同じ）。
4. **未定義型は `related` へ丸め、警告ログ＋カウンタ 1 件**（ADR-0033 決定 3）。カウンタの保持先は
   #910 仕様書 未決 1 が本 issue へ送っていたもので、**抽出側の OTel カウンタ**
   （`graph.edge_type_fallback.total`。`IngestTagMetrics` と同型・Grafana で観測）に決める。
   SC-10 画面への行の追加は「ナレッジ健全性」節の保留解除時（IngestTagMetrics と同じ線引き）。
5. **解決できないリンクは辺を作らない**（issue の既定を採用）。タイトル解決は graph_documents の
   複製 Title に対して行い（鮮度契約 1: 正本への同期照会をしない）、ordinal 完全一致 → 大文字小文字
   無視の一意一致。0 件・複数件（曖昧）・自己参照は作らない。
6. **受容する残余**: 2 文書が相互に `[[...]]` で related を張り合う場合、行は 1 本に正規化される
   ため、先に抽出した側が起点として記録される。その起点側がリンクを外すと、他方がまだリンクして
   いても辺は消え、他方の次回本文変更まで復元されない。全再構築（ADR-0033 決定 6 の復旧手段）で
   収束する。多起点の追跡はスキーマの複雑化に見合わない（IADR-0281 に記録）。

## 母集合（着手前に自分で引いた変更対象と除外理由）

- 変更: 上表のとおり（GraphService の Domain/Application/Infrastructure/Api・deploy 2 ファイル・
  `docs/tests/FR-17_knowledge-graph.md`・IADR-0281・本仕様書・IADR 索引 README）。
- 除外:
  - `scripts/event-topology-baseline.json`（S4） —— 購読は既存 `GraphDocumentSyncConsumer` の拡張で、
    knowledge/GraphService (wolverine) は DocumentUpdated 購読者として登録済み。増減なし。
  - `deploy/helm/microservices-platform/files/pipeline.json`（S5） —— graph-sync 段は既存・
    consumer 型完全名不変・リンク同期はイベントを発行しないため outputs は [] のまま。
  - 契約（`Knowledge.Contracts`）と契約 baseline —— `DocumentUpdated` は `MarkdownUri` / `Title` /
    `ContentFingerprint` を既に運ぶ。メンバ追加なし。
  - `AiSuggestionGenerator` の未定義型フォールバック経路 —— LLM 提案側の既存挙動（警告・カウンタ
    なし）は本 issue のスコープ（リンク抽出）外。変更しない。
  - C1〜C4 の仕様書（`20260828_edge-e3a-document-deleted` / `20260828_edge-e3b-document-updated` /
    `20260828_issue-911_abac-denormalization` / `20260828_issue-1016_delete-propagation`）—— 凍結。
  - `EdgeTypeSeed` / `EdgeType` / `EdgeTypeEndpoints` —— 辞書の値・契約は不変（読むだけ）。

## 受け入れ基準（issue #912）と写像

- [ ] 3 層それぞれの Obsidian 記法が期待した既定型に写像される →
      `EdgeTypeResolverTests`（純粋写像）＋ `LinkEdgeSyncTests`（consumer 経由の統合）
- [ ] 未定義型が `related` にフォールバックし、警告が 1 件記録される →
      `LinkEdgeSyncTests`（警告ログ＋カウンタ 1 件を MeterListener で実測）
- [ ] 差分更新で利用者付与・AI 承認済みの辺が保存される（自動抽出のみ置換） →
      `LinkEdgeSyncTests`（user / ai-approved / 他文書起点 auto の 3 種が残る）
- [ ] 解決できないリンクで辺が作られない →
      `LinkEdgeSyncTests`（不在・曖昧・自己参照。解決できた陽性対照つき）
- 追加（本仕様書）: 冪等性（同一イベント再配信・指紋不変イベントで変化なし）・対称正規化の
  安定性（GUID 逆順でも差分ゼロ）・縮退時に既存 auto 辺が消えないこと。

## 変異試験（実測して本節へ記録する。最低 2 種）

- （実装後に記録）

## 検証（コミット前）

- `dotnet build src/knowledge/backend/backend.slnx` 緑・`dotnet format --verify-no-changes` 緑
- `GraphService.Api.Tests` 全緑
- `node scripts/check-event-topology.js` / `check-backend-libraries.js` / `check-unit-dependencies.js` /
  `check-contract-schema.js` / `check-trace-blocks.js` / `validate-pipeline-config.js` 緑
- `dotnet test src/platform/backend/backend.slnx --filter "FullyQualifiedName~Pipeline"` 緑
- `node scripts/check-commit-messages.js --range e43e0a9..HEAD` 緑

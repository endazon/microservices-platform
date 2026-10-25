---
title: 作業仕様書 — アーカイブした文書を検索・RAG・グラフ・MCP の一覧から外す（計画 ADR-0141 決定 2・3。#1961）
type: spec
status: done
related_ids: [FR-06, FR-02, FR-16, FR-17, FR-18, UC-03, SC-03, SC-05, ADR-0141, ADR-0139, ADR-0061, ADR-0121, IADR-0549, IADR-0529, IADR-0396, IADR-0503, IADR-0545, IADR-0551]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0141_archive-effect-and-no-unarchive.md（決定 1〜5）
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-03・SC-05）
---

# 作業仕様書: アーカイブした文書を見つかる面から外す（#1961）

> 本仕様書は実装着手前に作成した（着手 2026-10-16）。基点は MSP `origin/develop` `3b49d911`。計画は project-planning `origin/main` `fc61ca3`。

## 起点

- 計画 ADR-0141（Accepted。planning#798 / planning#802 の利用者裁定 2026-10-16）。
  - 決定 2: アーカイブした文書は **検索・RAG・グラフ（AI 提案・クラスタ要約を含む）・Wiki・MCP の一覧** に載せない。閲覧（SC-03・ABAC の範囲）と SC-05 の一覧は残す。MCP の ID 指定の取得は定めない。
  - 決定 3: 判定の手段は実装に任せる。各面で同じ結果、SC-05 の状態と判定の根拠が食い違わないこと。選んだ手段を IADR に書く。
  - 決定 1: 解除は製品の操作として持たない。
- 実装 issue: #1961（本作業で起票。同件の既存 issue は無かった —— 直近 40 件の issue・PR を表題で確かめた）。

## 着手前の調査（コード読みの実測。`origin/develop` `3b49d911`）

| 面 | 現状 | 根拠 |
| --- | --- | --- |
| Wiki | `status == "archived"` で非公開化（済み） | `WikiService/.../DocumentSyncConsumer.cs:78` |
| 取り込み（検索・RAG の索引） | 状態を見ない。露出の門 → 全コレクション削除 → 本文を読み書き直す | `IngestionService/.../DocumentUpdatedConsumer.cs` |
| 検索・RAG の出口 | 索引（Qdrant）だけを引く。台帳の状態は読まない | `RetrievalService/Features/Search` |
| グラフ | 露出の門（`IsGraphAllowed`）だけを見る。ノードを作る・残す | `GraphService/.../GraphDocumentSyncConsumer.cs:97` |
| AI 提案・クラスタ | 母集合は `graph_documents`（ノード）。端点のノードが無い提案は一覧に出ない（deny-closed） | `TermOverlapSimilarityCandidateSource`・`ClusterDetectionJob`・`ListAiSuggestionsEndpoint` |
| MCP の一覧 | ABAC と露出（`IsMcpListable`）だけで落とす | `DocumentService/Features/McpTools/Execute/GrpcService.cs` `ListableToAgents` |
| 発行 | アーカイブ（単票・一括）は `DocumentUpdated`（status=archived）を出す。組織文書は常に門を通る | `DocumentEndpoints.PassesPublishGate` |
| 再発行の口 | 状態で絞れない（作成時刻・ID・属性だけ） | `Republish/RepublishSelection.cs` |

## 方式

1. **判定は台帳の状態（`DocumentStatus`）で行う。`lifecycle` 属性は同期しない**（IADR-0551 決定 1）。
   述語は `Knowledge.Contracts` の `DocumentArchival.IsArchived(status)`（単一情報源）。DocumentService の `DocumentStatus.Archived` はこの定数を指す。
2. **取り込み**: status=archived の `DocumentUpdated` は、露出の門と同じ位置（`MarkdownUri` の判定より前）で全索引から点を消し、本文を読まずに終える。
3. **グラフ**: status=archived はグラフの門と同じ位置でノードを撤収する（露出 OFF の撤収と同じ手順）。AI 提案の候補・クラスタはノードの母集合から外れる。
4. **MCP の一覧**: `ListableToAgents` でアーカイブ済みを外す（件数にも入れない）。ID 指定の取得は変えない（ADR-0141 決定 2 は定めない）。
5. **Wiki**: 既存の分岐の文字列リテラルを同じ述語へ寄せる（挙動は変えない）。
6. **遡及**: 再発行の口と駆動スクリプトに状態の絞り込み（`status` / `--status`）を足す。配備の後に `--status archived` で 1 回流すと、
   配備前にアーカイブした文書（#1931 の一括を含む）が全面から外れる（本文を読まず、埋め込みの費用はかからない）。クエリ時の絞り込みは採らない。
7. 計画 ADR のレンジを `0001..0141` へ引き直す（ADR-0141 を起点 ID に引くため。宣言 [1, 141] / 実物 [1, 141] を計画リポジトリの導出器で確かめた）。

## 対象外（触らない）

- 解除の口（決定 1）。本文の投入がアーカイブ済みを `normalized` へ戻す件（ADR-0141 実測 12・フォローアップ 4。利用者の判断待ち）。
- MCP の ID 指定の取得（フォローアップ 2）。SC-03 の閲覧・SC-05 の一覧（並行の作業がページングを触る —— 一覧系の口は避ける）。
- `lifecycle` 属性の語彙（計画側のフォローアップ 1）。

## 受け入れ基準と試験の写像

| 基準 | 試験 |
|---|---|
| 述語: `archived` だけが真。他の状態・空・null は偽。DocumentService の定数と一致 | `DocumentArchivalTests` |
| 取り込み: アーカイブ済みは点を書かず本文も読まない。載っていた点は全索引から消える。陽性対照: 同じ器の公開済みは載る | `ArchivedDocumentIndexExclusionTests` |
| グラフ: アーカイブ済みはノードを作らない・既存のノードを撤収する。陽性対照: 公開済みはノードになる | `GraphDocumentSyncConsumerTests`（アーカイブの節） |
| MCP: アーカイブ済みは一覧に載らず件数に入らない。ID 指定の取得と REST の一覧には残る | `GrpcMcpToolExecutionTests`（X-71） |
| 再発行: `status` で絞れる。未知の状態は 400 | `RepublishDocumentUpdatedEndpointTests` |
| 駆動器: `--status` を口へ渡し、状態ファイルの絞り込みに含める | `scripts.repo.test.js` |

## 母集合（規則 9・10）

- 「状態を見ない」「アーカイブ」「検索の見え方は変わらない」「ingestion_body_missing_total」で全文書を走査し、本変更で誤りになる記述を引いた:
  - `docs/operations/operations.md` の一括アーカイブの節（「検索の見え方は変わらない」「計数がアーカイブした件数だけ増える」「グラフは本文が無ければ辺を触らない」「戻し方」）→ 追随する。
  - `docs/functional/FR-06_document-crud-versioning.md`（アーカイブの口の行・一括の節・未決事項の解除）→ 追随する。
  - `IngestionService/.../DocumentUpdatedConsumer.cs` の NoSuchKey 分岐の注記（アーカイブの発行がこの分岐へ来る、と書いている）→ 追随する。
  - `IADR-0549`（凍結記録）→ 本文を書き換えず、日付つき追記で IADR-0551 を指す。`.ai-context/specs/` は触らない。
- `DocumentStatus.Archived` / `"archived"` の参照: DocumentService（ドメイン・一括の口）・WikiService（1 か所）・BFF（SC-05 の状態の値域。触らない）。

## 未決・計画への環流

- 計画のフォローアップ 1（SC-05 と 07_abac の語を揃える）は、本 IADR で手段が決まったので計画側の作業になる（報告で伝える）。

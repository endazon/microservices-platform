---
title: 作業仕様書 — 露出の 3 属性を 3 つとも除外にした組織文書を Wiki 同期と MCP の文書一覧から外し、組織文書への適用を各消費面の試験で固定する（#1879）
type: spec
status: done
related_ids: [FR-19, FR-13, FR-16, UC-07, UC-08, ADR-0034, ADR-0046, ADR-0061, IADR-0396, IADR-0455, IADR-0483, IADR-0512, IADR-0529]
author: claude
created: 2026-10-10
updated: 2026-10-10
issue: "#1879"
---

# 作業仕様書 — 3 つとも除外の組織文書を Wiki 同期と MCP の文書一覧から外す（#1879）

> 本仕様書は実装着手前に作成した（着手 2026-10-10）。基点は MSP `origin/develop` `d41e8641`。
> 計画は project-planning の隣接クローン（読み取り専用）と GitHub API の planning#784 を参照した。

## 起点（トレーサビリティ）

- **planning#784 の利用者裁定（2026-10-10）**: AST の承認待ちの報告書（ドラフト）を知識ユニットの組織文書として保存し、
  露出の 3 属性（`search_exposure` / `graph_exposure` / `ai_input`）を 3 つとも `excluded` にする。SC-03 で閲覧でき（ABAC・internal）、
  索引・検索・RAG・グラフ・MCP・Wiki には載せない。確定したら AST がドラフトを削除し、確定版を作る。
- **FR-19**（露出 3 トグル）・**計画 ADR-0061** 決定 1〜4（1 つでも ON なら索引・全 OFF は載せない・用途は属性で表す・ON → OFF は削除まで及ぶ）。
- **FR-13 / UC-07**（Wiki 閲覧。Wiki.js 同期）、**計画 ADR-0046 D-01**（個人資料は Wiki.js へ載せない）。
- **FR-16 / UC-08**（MCP サーバー）、**計画 ADR-0034 決定 9**（サービスアカウント実行は個人資料を返さない）。
- 実装: IADR-0396（露出の判定の単一情報源）・IADR-0455 決定 2（発行の門は個人資料だけに効かせる。組織文書への露出の適用は計画の射程として保留）・
  IADR-0483（MCP の文書ツールの実行口）・IADR-0512（検索の出口は用途の露出で落とす）。いずれも凍結記録として書き換えない。新しい判断は IADR-0529 に置く。

### 計画の裁定が要るか（確認結果: 裁定済み。計画への反映は planning#784 で進行中）

組織文書へ露出を効かせることは planning#784 で利用者が裁定した（2026-10-10）。FR-19・ADR-0061 の文言の更新は同 issue の「反映を求める箇所 2」で計画側が行う。
本件は裁定済みの内容を実装する。一部だけ除外した組織文書の扱いは裁定の外であり、索引の門と同じ粒度（1 つでも含めれば載せる）に揃える（IADR-0529 決定 1）。

## 現状の実測（`d41e8641`）

| 面 | 3 つとも除外の組織文書 | 箇所 |
| --- | --- | --- |
| 判定 | `IsAllowed` は明示値を文書種別より優先 → `IsIndexable` は偽 | `Knowledge.Contracts/Dtos/DocumentExposure.cs` |
| 発行の門 | 組織文書は常に発行（撤収・アーカイブを下流へ届けるため） | `DocumentEndpoints.PassesPublishGate` |
| 取り込み | チャンクを作らない／既存を全索引から削除 | `IngestionService/.../DocumentUpdatedConsumer.cs` |
| 検索の出口 | 用途の露出キーで落とす（REST・MCP の検索・RAG の候補） | `HybridSearchService.Finish` |
| RAG の文脈 | `AiInputExposure.IsAllowed` で落とす | `RagOrchestrator` |
| グラフ | ノードを作らない／撤収。閲覧でも落とす（MCP の `graph.*` も同じ関数） | `GraphDocumentSyncConsumer` / `AuthorizedGraphView` |
| **Wiki 同期** | **露出を見ずに Wiki.js へ載せる**（SC-04 と Wiki の検索に出る） | `WikiService/.../DocumentSyncConsumer.cs` |
| **MCP の文書一覧** | **ABAC の範囲を全部返す** | `DocumentService/Features/McpTools/Execute/GrpcService.cs` |
| 作成時の検証 | 組織文書の露出キーを拒否しない（変えない） | `Create/CreateDocumentValidator.cs` |

## 方針

1. `DocumentExposure` に `IsWikiPublishable`（個人資料は偽・組織文書は `IsIndexable`）と `IsMcpListable`（個人資料は真・組織文書は `IsIndexable`）を足す。
2. `DocumentSyncConsumer`: 個人資料の分岐の後に門を置く。偽なら Wiki.js の実体を削除し、同期メタデータを削除する（削除の伝播と同じ 2 手）。除外 → 含めるは通常の経路で作り直す。
3. MCP の `document.list_documents`: 判定点の後・件数を数える前に `IsMcpListable` で落とす。`document.get_document` は SC-03 と同じく残す（IADR-0529 決定 3）。
4. 各消費面の試験に、3 つとも除外の組織文書の否定形と陽性対照を足す。
5. 「組織文書は常に true（露出キーを持たない）」と書いたコメント・生きた文書を直す。

## 母集合（規則 9: 誤りの側の文字列で全文書を走査してから挙げる）

誤りの側の文字列は「組織文書は常に」「露出キーを持たない」「組織文書は露出」「組織文書には露出」「露出.*組織文書」「組織文書.*露出キー」。
`git grep` で `src/knowledge/backend`（`.cs`）・`docs/`・`.ai-context/adr/`（`.md`）を走査した。

| ヒット | 扱い |
| --- | --- |
| `Knowledge.Contracts/Dtos/DocumentExposure.cs`（`IsIndexable` の注記・`IsAllowed` の表） | **直す** |
| `IngestionService/.../DocumentUpdatedConsumer.cs:70` | **直す** |
| `GraphService/.../GraphDocumentSyncConsumer.cs:95` | **直す** |
| `RetrievalService/.../HybridSearchService.cs:332` | **直す** |
| `RetrievalService/.../RerankPrompt.cs:21` | **直す** |
| `Knowledge.Contracts.Tests/DocumentExposureTests.cs:78-80`（試験名「組織文書は常に索引可能である」） | **直す**（「露出キーを持たない組織文書は…」へ改名。仕様書からの参照は無いことを確かめた） |
| `docs/functional/FR-03_hybrid-search.md:171`（再順位付けの送る候補「組織文書は常に真」） | **直す** |
| `DocumentEndpoints.cs:290-293`（発行の門。「組織文書は常に通す」） | 対象外 —— 発行の門の性質として正しい（変えない） |
| 試験の陽性対照のコメント（`PrivateNoteIndexExposureTests` / `PrivateNoteAiInputPurposeTests` / `PrivateNoteIndexProductionTests` / `NodeScopeFlagTests` / `DocumentSyncConsumerTests` / `AiInputExposureTests`） | 対象外 —— いずれも「露出キーを持たない組織文書」を主語にした記述で、誤りではない |
| `.ai-context/adr/IADR-0396` / `IADR-0455` / `IADR-0483` / `IADR-0498` | 対象外 —— 凍結記録。新しい判断は IADR-0529 に置く |
| `docs/tests/FR-06_document-crud-versioning.md:118` | 対象外 —— 再発行の試験の記述で、露出キーの無い組織文書を指す |

追随する生きた文書（規則 9 の走査で決めた）: `docs/functional/FR-19_private-notes.md`（露出の判定）、`docs/functional/FR-13_wiki-browsing.md`（同期の主要コンポーネント）、
`docs/tests/FR-16_mcp-server.md`（X-70・X-59 の注記）、`docs/tests/FR-19_organization-document-exposure.md`（新規。各消費面の試験の写像）。`docs/tests/FR-19_private-note-wikijs-exclusion.md` は射程を個人資料に限ると明記しているので触らない（新しい仕様書から相互に参照する）。

規則 10（この変更で新たに誤りになる自分の記述）: X-59 の「MCP の一覧は REST と一致する」は、3 つとも除外の組織文書だけ崩れる。試験のコメントとテスト仕様書の X-59 に注記を足す。
X-70 の文書は試験の利用者が所有する restricted にし、器を共有する X-59 の件数の一致を壊さない。

## 受け入れ基準

- [x] 3 つとも除外の組織文書は Wiki.js へ載らず、同期メタデータも作られない（`published` / `normalized`）
- [x] 含める → 除外で、既存の Wiki ページ（Wiki.js の実体とメタデータ）が撤去される
- [x] 除外 → 含めるで、ページが作られる
- [x] 一部だけ除外した組織文書は従来どおり載る。個人資料のページは露出によらず撤去しない
- [x] 取り込み: 載らない／含める → 除外で削除／除外 → 含めるで再び載る
- [x] 検索の出口（横断検索・AI 入力の用途）と RAG の文脈に現れない（陽性対照つき）
- [x] グラフ: ノードを作らない／撤収／作り直す
- [x] MCP の一覧に載らず件数にも入らない。ID 指定の取得は REST（SC-03）と同じく返る
- [x] 露出キーの無い組織文書は各面で従来どおり（陽性対照）
- [x] 変異の確認: `IsWikiPublishable` を「個人資料でない」だけに、`IsMcpListable` を常に真に戻すと、Wiki の 4 件（全除外の 2 状態・撤去・戻せば載る）と MCP の X-70 が落ちる（実測。陽性対照は緑のまま）

## 検証

- `dotnet build src/knowledge/backend/backend.slnx`（警告 0）／`dotnet test`（知識ユニット全体）
- `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes`
- `node scripts/check-trace-blocks.js` ほか文書・トレーサビリティの検査（PR 本文に結果）

## 範囲外

- LlmGateway・モデル ID・`.github/workflows/`（別の PR が進行中）
- AST 側の実装（ドラフトの保存・置き換え・再取り込みの除外）
- 計画（FR-19・ADR-0061）の文言の更新（planning#784）

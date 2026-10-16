---
title: IADR-0551 アーカイブした文書は台帳の状態で判定し、取り込みは全索引から点を消し、グラフはノードを撤収し、MCP の一覧は外す。lifecycle 属性は同期しない。配備前のアーカイブ済みは状態で絞った再発行で遡及する（計画 ADR-0141。#1961）
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-02, FR-16, FR-17, FR-18, UC-03, SC-03, SC-05, ADR-0141, ADR-0139, ADR-0061, ADR-0121, IADR-0549, IADR-0529, IADR-0396, IADR-0503, IADR-0509, IADR-0540, IADR-0545]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0141_archive-effect-and-no-unarchive.md（決定 1〜5）
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-03・SC-05）
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md（`lifecycle` 属性）
related_specs:
  - ../specs/20261016_1961_archive-effect-exclusion.md
---

# IADR-0551: アーカイブした文書を見つかる面から外す（計画 ADR-0141。#1961）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-16
- 決定者: claude（計画 ADR-0141 の利用者裁定 2026-10-16〔planning#798 / planning#802〕の実装）

## 起点・関連

- 起点 issue: #1961。計画 ADR-0141 決定 2「アーカイブした文書は検索・RAG・グラフ（AI 提案・クラスタ要約を含む）・Wiki・MCP の一覧に載せない。
  閲覧（SC-03）は ABAC の範囲で残す。SC-05 の一覧は外す面に入らない。MCP の ID 指定の取得は定めない」と、決定 3「判定の手段は実装に任せる。
  各面で同じ結果、SC-05 の状態と判定の根拠が食い違わないこと。選んだ手段を IADR に書く」を実装する。
- 関連する実装 ADR: [IADR-0549](./IADR-0549_archive-bodyless-pre-cutover-documents.md)（一括アーカイブ。却下案 4 を本 IADR が改める）・
  [IADR-0529](./IADR-0529_org-doc-exposure-wiki-gate-and-mcp-list.md) / [IADR-0396](./IADR-0396_private-note-exposure-index-production.md)（露出の門。同じ型に揃える）・
  [IADR-0503](./IADR-0503_republish-document-updated-admin-endpoint-and-driver.md) / [IADR-0509](./IADR-0509_document-keyset-in-sql-and-created-at-id-index.md)（再発行の口と駆動器）・
  [IADR-0545](./IADR-0545_document-list-sort-and-filter-after-abac.md)（SC-05 の状態の絞り込み）。
- 作業仕様書: `.ai-context/specs/20261016_1961_archive-effect-exclusion.md`。

## コンテキスト（`origin/develop` `3b49d911` の実測。計画 ADR-0141 実測 2〜6 と一致）

| 面 | 実装前 | 見ていたもの |
| --- | --- | --- |
| Wiki | 非公開化する | `DocumentUpdated.Status == "archived"` |
| 取り込み（検索・RAG の索引） | 本文があれば載せ直す | 露出の門だけ |
| グラフ（SC-18・MCP のグラフ系・AI 提案・クラスタ） | ノードを作る・残す | 露出の門（`IsGraphAllowed`）だけ |
| MCP の文書一覧 | 載せる | ABAC と `IsMcpListable` だけ |
| SC-03 の閲覧・SC-05 の一覧 | 載せる | ABAC（と SC-05 の状態の絞り込み） |

アーカイブ（単票 `POST /documents/{id}/archive`・一括 `POST /documents/archive-bodyless`）は `DocumentUpdated`（status=archived）を出す。
組織文書は発行の門を常に通る（IADR-0455 決定 2）。露出 ON の個人資料も通る（露出 OFF の個人資料は元から索引・グラフに無い）。

## 決定

### 決定 1: 判定は台帳の状態（`DocumentStatus`）で行う。ABAC 属性 `lifecycle` は同期しない

- 述語は `Knowledge.Contracts` の `DocumentArchival.IsArchived(status)`（`status == "archived"`。大文字小文字を区別する）ただ 1 つである。
  取り込み・グラフ・Wiki の受け手と MCP の一覧が同じ関数を呼ぶ（`DocumentExposure` と同じ単一情報源の作法）。
  DocumentService の `DocumentStatus.Archived` はこの定数を指す。
- **SC-05 が「アーカイブ済み」と出す値（`DocumentDto.Status`。IADR-0545 の絞り込みと件数の要約）と同じ値で判定する**ので、表示と判定の根拠は食い違わない（ADR-0141 決定 3 の条件）。
- 🔴 **`lifecycle` 属性はアーカイブを表さない。** データソースの既定から入った値（指定が無ければ `active`）のまま残る。
  **ポリシーの `allowedLifecycles` でアーカイブした文書を扱うことはできない**（ADR-0141 決定 3「同期させない場合」の明記の要求）。
- 却下: アーカイブで `lifecycle` を `archived` に書き換える（下の代替案 1）。

### 決定 2: 取り込みは、status=archived なら全索引から点を消し、本文を読まずに終える（検索・RAG）

- `DocumentUpdatedConsumer` のアーカイブの門は、露出の門の直後・`MarkdownUri` の判定より前に置く。全コレクション（モデル別・語彙索引・埋め込み待ちを含む
  `DeleteByDocumentFromAllAsync`）から当該文書の点を消して ack する。**本文は読まない**（埋め込みの費用は 0。本文の無い一括の対象も同じに外れる）。
- 検索の出口（横断検索 SC-01・SC-02・MCP の `retrieval.search_documents`）と RAG の文脈は索引だけを引くので、索引から消えれば全部から外れる。
- **点を残して出口で状態を見る形（クエリ時の絞り込み）は採らない**（代替案 2）。露出の門（IADR-0396 決定 5「実体を消す」）と同じ理由である。
- アーカイブ済みの文書へのその後の `DocumentUpdated`（タグの付け替え・タグの改名・再発行）も status=archived のまま届くので、この門で外れたままになる。

### 決定 3: グラフは、status=archived ならノードを作らず、既存のノードを撤収する

- `GraphDocumentSyncConsumer` のアーカイブの門は、露出の門の直後・本文を読む前に置き、露出 OFF の撤収（`WithdrawAsync` → `GraphDocumentRemoval`）と同じ手順でノード・辺・共有タグの複製を片付ける。
- **AI 提案とクラスタ要約もこれで外れる。** 類似度候補の母集合（`TermOverlapSimilarityCandidateSource`）とクラスタの検出・要約（`ClusterDetectionJob`・`ClusterSummaryJob`）は
  `graph_documents`（ノード）を母集合にする。端点のノードが無い既存の AI 提案は一覧に出ない（`ListAiSuggestionsEndpoint` の deny-closed）。
  クラスタの所属・要約の行は次の周期の検出で作り直される（それまでの間は前の周期の要約が残る —— 残余）。
- 一括の対象（本文が無い 5,803 件）に、表題だけのノードを作らない（ADR-0141 決定 5 のグラフの行の暫定を解く）。

### 決定 4: MCP の文書一覧はアーカイブ済みを外す。ID 指定の取得は変えない

- `McpToolExecutionGrpcService.ListableToAgents` で、判定点（ABAC）の後・件数を数える前に落とす（`total_count` にも入れない。X-70 と同じ位置）。
- `document.get_document` は ADR-0141 決定 2 が「定めない」としたので変えない（内容の ABAC の門〔既定 Off〕が開くまで経路ごと拒否される。開く前に計画が決める —— ADR-0141 フォローアップ 2）。
- MCP のグラフ系ツール（GraphService）と検索ツール（RetrievalService）は、決定 2・3 で元が消えるので個別の変更は要らない。

### 決定 5: 配備前にアーカイブした文書は、状態で絞った再発行で遡及する

- 遡及は**アーカイブ時の削除（決定 2・3）を、既存のアーカイブ済みへもう一度起こす**ことで行う。再発行の口 `POST /documents/republish-updated` に
  `status`（台帳の状態の完全一致。SQL で絞る。未知の値は 400）を、駆動スクリプト `scripts/republish-document-updated.js` に `--status` を足した。
- 配備の後に `--status archived` で 1 回流すと、status=archived の `DocumentUpdated` が出て、取り込みは本文を読まずに点を消し、グラフはノードを撤収し、
  Wiki は非公開化を冪等に繰り返す。**本文を読まないので埋め込みの費用はかからない。** 件数は dry-run で確かめる（`matched` がアーカイブ済みの件数）。
- #1931 の一括アーカイブ（約 5,803 件）を**配備の後に**流すなら、一括の口が出す `DocumentUpdated` がそのまま決定 2・3 の門を通るので、遡及は要らない。
  **配備の前に**流した（または流したか不明な）ら、`--status archived` の再発行を 1 回流す（冪等なので二重でも害は無い）。

### 決定 6: Wiki は既存の挙動のまま、判定を同じ述語へ寄せる

- `DocumentSyncConsumer` の `ev.Status == "archived"` を `DocumentArchival.IsArchived(ev.Status)` に置き換えた（挙動は変えない。非公開化。ページの実体は Wiki.js に残る）。

### 変えないもの

- **解除の口は足さない**（ADR-0141 決定 1）。戻し方 (a)（再正規化で `normalized` に戻る）では、取り込み・グラフは通常の経路で載せ直す（各試験で固定した）。
- SC-03 の閲覧・SC-05 の一覧・BFF・proto・画面。本文の投入（`PUT /documents/{id}/body`）がアーカイブ済みを `normalized` へ戻す件（ADR-0141 実測 12。利用者の判断待ち）。
  戻った文書は状態が `normalized` なので、本 IADR の判定ではアーカイブではない（台帳の状態と判定が一致する）。

## 各面の試験（否定形と陽性対照）

| 面 | 試験 |
| --- | --- |
| 判定 | `DocumentArchivalTests`（archived だけが真・他の状態・空・null・大文字違いは偽・属性を受け取らない） |
| 取り込み（検索・RAG） | `ArchivedDocumentIndexExclusionTests`（載らない・本文を読まない／載っていた点が消える／本文の参照が無くても消す／正規化済みへ戻ると載る。陽性対照: 公開済みは載る） |
| グラフ（AI 提案・クラスタの母集合） | `GraphDocumentSyncConsumerTests` のアーカイブの節（ノードと語の出現数を作らない／ノードと共有タグの辺を撤収し相手は残る／戻ると作り直す） |
| MCP の一覧 | `GrpcMcpToolExecutionTests` X-71（一覧に載らず件数に入らない。ID 指定の取得と REST の閲覧・一覧には残る） |
| Wiki | 既存の `DocumentSyncConsumerTests`（status=archived で非公開化） |
| 遡及 | `RepublishDocumentUpdatedEndpointTests`（状態で絞れる・未知は 400）・`scripts.repo.test.js` の #1961 節 |

変異試験（赤になることを確かめた）: 取り込みの門を外す（3 件赤）／グラフの門を外す（2）／MCP の絞り込みを外す（1）／再発行の状態の絞り込みを外す（1）／未知の状態の 400 を外す（1）。

## 検討した代替案

| # | 案 | 採らなかった理由 |
| --- | --- | --- |
| 1 | アーカイブで `lifecycle` 属性を `archived` へ同期し、属性で判定する | ポリシーが `allowedLifecycles` を持ち `archived` を含めないと SC-03 の閲覧まで閉じる（ADR-0141 決定 3 が警告した衝突）。既存ポリシーの棚卸しが要り、戻し方 (a) で属性をどの値へ戻すか（データソースの既定か直前の値か）も新たに決めることになる。語彙も揃っていない（状態 4 値と属性 3 値） |
| 2 | 点を残し、検索の出口・グラフの閲覧で状態を見て隠す（クエリ時の絞り込み） | 点・ノードは台帳の状態を運ばない（ペイロードへ足すと全件の再索引が要る）。出口の絞り込みの実装ミス 1 つで露出に変わる（IADR-0396 決定 5 が退けた形） |
| 3 | 発行の門でアーカイブ済みを止める（イベントを出さない） | 下流へ撤収・非公開化が届かなくなる（IADR-0455 で退けた形） |
| 4 | 遡及に専用の口を足す | 再発行の口に状態の絞り込みを足せば足りる（作法・監査・駆動器を再利用できる） |
| 5 | MCP の ID 指定の取得も閉じる | ADR-0141 決定 2 が「定めない」とした。計画の判断を先取りしない |

## 結果

- アーカイブした文書は、検索・RAG・グラフ（AI 提案・クラスタ要約を含む）・Wiki・MCP の一覧のどれにも載らない。SC-03 の閲覧と SC-05 の一覧では ABAC の範囲で見える。
- 計画 ADR-0141 決定 5 の 3 点セットのうち、検索・RAG・グラフ・MCP の一覧の行は「**配備後に**外れる」が満たされる（配備前のアーカイブ済みは決定 5 の再発行の後）。
- IADR-0549 の却下案 4（「取り込みに archived なら本文を読まずに撤収の分岐を足す」—— 計画が効果を定めていなかったため退けた）は、計画 ADR-0141 で効果が定まったので本 IADR が採る。
  一括アーカイブのとき、取り込みの計数 `ingestion_body_missing_total` は増えなくなる（本文を読まない）。

## 残余リスク・未決

- **順序**: 取り込み・グラフの撤収は `UpdatedAt` の順序ガードを持たない（露出の撤収と同じ）。アーカイブの前に出た更新の配信が、アーカイブの撤収より後に処理されると、
  その文書は再び載る。アーカイブ済みへの次の `DocumentUpdated`（または決定 5 の再発行）で外れる。
- **クラスタの要約**: 次の検出の周期まで、アーカイブした文書を含んでいた前の周期の要約が残る。
- **計画の語**: ADR-0141 フォローアップ 1（SC-05 と 07_abac の「状態」と `lifecycle` 属性の語を 1 つに揃える）は、本 IADR で手段（状態で判定・属性は同期しない）が決まったので計画側の作業になる。
- **MCP の ID 指定の取得**（フォローアップ 2）と**本文の投入による状態の戻り**（フォローアップ 4）は計画の判断待ち。

---
title: 所属 1 件の単独クラスタに要約を作らず、unsummarized-clusters にも数えない（#1663）
type: spec
status: done
related_ids: [FR-17, FR-10, FR-18, SC-10, SC-18, ADR-0120, ADR-0083, ADR-0035, IADR-0425, IADR-0430]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0120_singleton-clusters-excluded-from-summary.md 決定 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0083_cluster-definition-and-unsummarized-semantics.md 決定 1〜3
issue: "#1663"
---

# 仕様書: 単独クラスタを要約と未要約の計数から外す（#1663）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`、`origin/main`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-17**（知識グラフ）、**FR-10**（ダッシュボード。ナレッジ健全性の指標）、FR-18
- 画面: SC-10（運用ダッシュボード。節は実装待ちのまま）、SC-18（表示単位は変えない）
- 関連 ADR: **ADR-0120 決定 3・4**（本件の裁定。ADR-0083 決定 2・3 の部分改定）、ADR-0083 決定 1（クラスタの定義。変えない）、
  ADR-0035 決定 3（要約は複数の文書をまとめて大域的な問いに答えるためのもの）
- 関連 IADR: **IADR-0425**（クラスタ検出と `unsummarized-clusters` の生産）、**IADR-0430**（クラスタ要約の生成バッチ）。
  いずれにも日付つき追記を置く。**新しい IADR は起こさない**（計画が決めた対象の境界を実装へ写すだけで、実装上の新しい判断は無い）。
- 起点 issue: #1663（planning#687 の裁定の実装）

## 目的・背景

PoC の実測（planning#687。`clusters=6107 nodes=6107 edges=0`）で、辺 0 本のとき全文書が単独クラスタになり、
(1) `unsummarized-clusters` と `orphan-documents` が同じ集合を二重に数え、(2) 要約バッチを有効にすると表題 1 件だけの要約を
約 6107 件作ることが分かった。ADR-0120 決定 3 は「単独クラスタには要約を作らず、`unsummarized-clusters` に数えない。検出・保存はしてよい」と裁定した。

## 設計

1. `UnsummarizedClusterRule` に**所属文書数を受け取る入口** `Evaluate(int memberCount, …)` を足す。所属文書数が
   `MinMembersToSummarize`（= 2）未満なら常に null（未要約に数えない・要約の対象でない）、以上なら従来の 3 条件の判定を返す。
2. **要約バッチ（`ClusterSummaryJob`）と指標（`KnowledgeHealthCollector.CollectUnsummarizedClustersAsync`）の両方がこの入口を呼ぶ。**
   判定を 1 か所に置く既存の建付け（IADR-0430「判定は 1 か所から引く」）を保ち、片方だけが単独クラスタを外す割れ方を作らない。
3. 所属文書数は `graph_cluster_members` の行数で数える（クラスタの構成そのもの）。文書表との突合はしない
   （構成員は検出時点の文書集合であり、文書の削除は次の検出で構成に反映される）。所属 0 件（構成員の行が無い）も対象外とする。
4. 変えないもの: 検出・保存（単独クラスタも `graph_clusters` に保存する）、未要約の 3 条件と理由の 3 語、しきい値を持たないこと、
   `ClusterSummary:Enabled` の既定（オフ）、SC-18・SC-10 の表示。既存の単独クラスタの要約行は消さない（読み手は未だ無い）。

## 受け入れ基準

- AC-1（否定）: 辺 0 本で全文書が単独クラスタのとき、クラスタは保存されるが、`unsummarized-clusters` の観測値は 0 件、
  要約バッチの候補は 0・LLM 呼び出しは 0 回・生成時刻も本文も 0 行。
- AC-2（陽性対照）: 所属 2 件以上のクラスタは従来どおり要約の対象になり、未要約として数えられる。塊と単独が混在するとき、
  塊だけが数えられ・生成され、送信本文に単独クラスタの文書は入らない。
- AC-3（判定の単体）: 所属 0・1 件は要約が無くても null。所属 2・5 件は従来の 3 条件どおり（要約無し → `no-summary`／構成変更 → `composition-changed`／要約済み → null）。
- AC-4: 既存の試験（`ClusterDetectionTests`・`ClusterSummaryTests`・`UnsummarizedClusterRuleTests`・`KnowledgeHealthProducerTests` ほか GraphService.Tests 全件、DashboardService.Tests 全件）が通る。
- AC-5: 変異（入口の除外を外す・要約バッチが所属文書数を渡さない〔常に 2 以上として扱う〕・指標が所属文書数を渡さない）で試験が赤。

## 母集合（着手前に自分で引いた。規則 9）

### 引き方

- `git grep -n -l -e unsummarized -e 未要約 -e ClusterSummaryJob -e UnsummarizedClusterRule -e 単独クラスタ`（全追跡ファイル）
- 同じ語で submodule（`src/ai-stock-trading`）: ヒット 0（`git grep` の結果に現れない）

### 結果（生産・消費の全箇所）

- **判定**: `GraphService/Domain/Clustering/UnsummarizedClusterRule.cs`（入口を足す）
- **生産（指標）**: `GraphService/Features/KnowledgeHealth/Report/KnowledgeHealthCollector.cs`（入口へ所属文書数を渡す）
- **生産（要約）**: `GraphService/Features/Clustering/Summarize/ClusterSummaryJob.cs`（同）
- **注記だけ直すもの（規則 10: 本変更で新たに不正確になる記述）**:
  `GraphService/Domain/KnowledgeHealthIndicators.cs`（指標の定義）、`GraphService/Domain/Clustering/GraphClusterSummary.cs`（「空は全クラスタが未要約」）、
  `GraphService/Features/Clustering/Summarize/ClusterSummaryOptions.cs`（「初回は全クラスタが未要約」）、
  `DashboardService/Domain/KnowledgeHealth.cs`（消費側の語彙の定義。コメントのみ）
- **文書**: `docs/functional/FR-10_dashboard.md`・`docs/observability/knowledge-health-indicators.md`（指標の分母と注記。trace ブロックへ本仕様書・#1663・ADR-0120）、
  `docs/tests/FR-17_knowledge-graph.md`（T-67〜T-69 と実装マッピング）、`scripts/test-spec-coverage-baseline.json`（`--update`）、
  `.ai-context/adr/IADR-0425`・`IADR-0430`（日付つき追記）
- **試験**: `UnsummarizedClusterRuleTests`・`ClusterSummaryTests`（足す）、`ClusterDetectionTests`（既存の試験はすべて所属 4 件の塊で組んであり、変わらない）

### 除外したもの（理由）

- `GraphService/Program.cs`（270 行目の注記「`unsummarized-clusters` の分母であり SC-18 が表示する単位」）: **並行中の #1611 段 3 が触るため触らない。**
  注記はクラスタ検出の登録の説明であり、「分母」の語は所属 2 件以上に絞られた今も「検出したクラスタが母集合」という意味では誤りにならない。
- `LeidenCommunityDetector.cs`・`ClusterDetectionJob.cs`・`GraphCluster.cs`（検出と構成変更時刻の注記）: 検出・保存は変えないため誤りにならない。
- `LlmGatewayClusterSummaryClient.cs`・`IClusterSummaryLlmClient.cs`・`ClusterSummaryHostedService.cs`: 1 クラスタぶんの呼び出し・周期の起動であり、対象選びに関わらない。
- `docs/screens/SC-10_operations-dashboard.md`・`docs/tests/SC-10_operations-dashboard.md`・フロントエンド（`OperationsDashboardPage*`）: 節を開かない理由と配線の経緯の記述であり、
  指標の分母を述べていない。節は実装待ちのまま（ADR-0120 決定 2）。
- `.ai-context/specs/` の過去の作業仕様書（#443・#1186・#1246・#1363・#1395 ほか）と `CHANGELOG.md`: point-in-time の記録・生成物で書き換えない。
- IADR-0299・IADR-0353・IADR-0389（健全性の生産者・陳腐化・観測値の軸）: 未要約の定義を持たない（語として現れるだけ）。
- 並行作業: GraphService の `Features/McpTools/`・`Program.cs`（#1611 段 3）、ABAC の seed（#1664）は触らない。

## 配備の順番

- 構成の変更は無い。配備後、`unsummarized-clusters` は所属 2 件以上のクラスタだけを返す（辺 0 本の PoC では 6107 → 0）。
- 要約バッチは既定オフのまま（ADR-0120 決定 4 の暫定手段。本変更で有効化の可否の判断は変わらないが、有効化したときに単独クラスタへ要約を作ることは無くなる）。

## 残るもの

- 辺のある環境でのクラスタ数のオーダーと要約の長さの実測（ADR-0120 決定 1・2、ADR-0083 フォローアップ 2・3。#1396）。
- リンク先 0 件の原因の確認（ADR-0120 フォローアップ 2）。
- SC-10 のナレッジ健全性の節（#1394。実装待ちを維持）。

## 検証

- `dotnet build`（knowledge・platform の slnx）、GraphService.Tests・DashboardService.Tests 全件
- `dotnet format <slnx> --verify-no-changes`（両ユニット）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` と各検査器
- 変異 2 件以上をコミット済みの状態で当てて赤を確かめ、`git show HEAD:<path> > <path>` で戻す。

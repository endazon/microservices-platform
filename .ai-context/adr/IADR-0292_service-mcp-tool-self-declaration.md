---
title: IADR-0292 MCP ツールの自己申告は「候補 → 選別」の 1 経路に閉じ、個人資料を対象に含む候補を申告しない
type: impl-adr
status: Accepted
related_ids: [FR-16, FR-17, FR-19, UC-08, SC-12, ADR-0024, ADR-0034, ADR-0054]
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0024_mcp-server-integration.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
  - planning:projects/microservices-platform/06_technical/11_mcp-server-integration.md
---

# IADR-0292: MCP ツールの自己申告は「候補 → 選別」の 1 経路に閉じる

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: claude（実装担当）

## 起点・関連

- 関連する計画書 ID: FR-16 / FR-17 / FR-19 / UC-08 / SC-12 / ADR-0024（§決定・2026-08-01 注記・2026-08-02 注記）/ ADR-0034 決定 9 / ADR-0054
- 関連する実装仕様書: `.ai-context/specs/20260828_issue-1020_internal-mcp-tools.md`
- 先行: IADR-0269（MCP ツールの公開経路を 1 本に閉じ、個人資料除外を 2 層で強制する）

## コンテキストと課題

#445 で McpServer 側の収集機構は着地したが、**`GET /internal/mcp-tools` を実装したサービスが 0 件**であり、
実効カタログは空だった。「動的ツール連携は動くが、載るツールがゼロ」であり、FR-16 の受け入れ基準は
実質満たされていない。IADR-0269 のフォローアップが指名した 3 サービスへ申告端点を実装するにあたり、
実装へ委任された範囲（ADR-0024 §結果）で決めるべきことが 4 つある。

- (a) どのサービスが申告するか（公開対象の母集合）
- (b) 個人資料の一律除外（ADR-0034 決定 9）を申告する側でどう表すか
- (c) 申告スキーマの置き場所（共有契約か、サービス内か）
- (d) 申告した `endpoint` の実体（共通エンベロープの実行口）を同時に実装するか

## 検討した選択肢

### (a) 公開対象

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A-1 | 読み取り面を持つサービスすべて（WikiService を含む） | **却下。** 既定は非公開（許可リスト方式）であり、**計画が挙げていないものは公開しない**。Wiki ページは DocumentService の文書の投影であり、公開すると `document.*` と二重の経路になる（経路が増えるほど統制の適用点が増える。IADR-0269 決定 2 が避けた形） |
| A-2 | **DocumentService / RetrievalService / GraphService の 3 件** | **採用。** ADR-0024 §決定「初期公開範囲」（`retrieval.*` / `document.*`）＋ 2026-08-01 注記（グラフ探索系の供給元は GraphService）が名指しした範囲そのもの。IADR-0269 のフォローアップが挙げた 3 件とも一致する |

### (b) 個人資料の除外の表し方（申告する側）

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | 申告するツールを直書きし、個人資料のツールは「書かない」 | **却下。** 「思い付かなかったから無い」と「規則で落としている」が区別できない。除外が効いていることを測る手段が無く、候補が増えたときの入れ忘れも検出できない |
| B-2 | **候補（ツール＋対象文書スコープ）を持ち、選別（`Publishable`）を 1 経路だけ通す** | **採用。** 除外の適用点が 1 つになり、そこにテストを置けば全ツールを覆える（IADR-0269 決定 2 と同じ論法）。DocumentService は実在する `/private-notes` 面を**候補として書き、落ちることを実物で測る** |

### (c) 申告スキーマの置き場所

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C-1 | IADR-0269 決定 6 のとおり `Platform.Shared.Contracts` へ昇格する | **今回は見送り。** `*.Contracts` への型追加は `scripts/contract-schema-baseline.json` の更新を伴い、**`scripts/**` は本 issue の領域宣言で触れない**。並行作業との衝突面でもある |
| C-2 | `Knowledge.Contracts` へ置く | **却下。** 同じ理由（`*.Contracts` はスナップショット検査の対象）。加えて McpServer は platform 側であり、可変ユニットの契約に置くと向きが逆になる |
| C-3 | **各サービスの `Features/McpTools/` に、ワイヤ形式へ合わせた写しを置く** | **採用。** 可変ユニットから platform の McpServer は参照できない（ユニット外参照は `platform/backend/Shared/` の 3 プロジェクトのみ）。GraphService の `GraphDocumentScope` が同じ制約で McpServer の `DocumentScope` を共有せず持っている先例がある（IADR-0274 §検討した選択肢） |

### (d) 実行口（`endpoint` の実体）

| 案 | 内容 | 評価 |
| --- | --- | --- |
| D-1 | 本文で渡された `ToolInvocationScope` を信じて認可する | **却下。** `GraphServiceNeighborExpander` が「**解決済み scope を本文で渡す方式 B を採ってはならない —— 採ると『本文で渡された scope を信じる』口が開き、そこへ到達できる誰もが任意の scope を主張できる**」と明記している。GraphService は JWT から自分で解決する型（方式 A）である |
| D-2 | 実行口を認証必須にする | **今は成立しない。** `HttpToolInvoker` は資格情報を下流へ運んでいない。実装しても全要求が 401 になり、「実装したのに動かない」状態を作る |
| D-3 | **実行口は本 issue で実装せず、権限伝播の方式を別 issue で裁定する** | **採用。** 半端な実装はセキュリティホールになる。申告・収集・突合の経路は D-3 のままでも成立し、ドリフト検出（ADR-0024 §5）も正しく働く |

## 決定

**決定 1**: 公開対象は **DocumentService / RetrievalService / GraphService の 3 サービス**とする。
公開するツールは `document.get_document` / `document.list_documents` / `retrieval.search_documents` /
`graph.get_backlinks` / `graph.get_links` / `graph.traverse` の 6 件である。
**WikiService は公開しない**（上記 A-1）。要約系（`get_cluster_summary`）と AI 分析系（`ai.*`）も
公開しない（11_mcp-server-integration §6 / ADR-0024 §決定。構成側では `ToolPublicationConfigValidator` が弾く）。

**決定 2**: 申告は **「候補 → 選別 → 申告」の 1 経路**に閉じる。候補は
**そのツールが対象とする文書スコープ（`doc_scope`）を明示して持ち**、選別（`Publishable`）が
`private-note` を対象に含む候補を落とす。**ツール名で分岐しない。**

**決定 3**: 個人資料の判定は **集合帰属（`doc_scope == "private-note"`）**で書く。
**否定（`!= "organization"`）で書かない。** `doc_scope` は実データ 0 件・既存文書へ遡及付与しない方針
（ADR-0054 §結果）であり、否定で書くとスコープを持たない候補がすべて個人資料に倒れて
**組織向けツールが一斉に落ちる**。判定は各サービスに既にある述語
（`DocumentAttributes.IsPrivateNote` / `GraphDocumentScope.IsPrivateNote` / `DocumentScopes.IsPrivateNote`）を使い、
**向きを 2 箇所に持たない**。

**決定 4**: 申告スキーマは各サービスの `Features/McpTools/` に置く（C-3）。
**IADR-0269 決定 6 の昇格条件（最初の生成側が実装されたとき）は満たされたが、昇格は追随 issue へ回す** ——
`scripts/contract-schema-baseline.json` の更新が要り、本 issue の領域宣言の外だからである。
昇格するまで**ワイヤ形式は McpServer 側の `McpToolContracts.cs` が正本**であり、写しを先に変えない。

**決定 5**: **共通エンベロープの実行口は本 issue で実装しない**（D-3）。
`endpoint` は申告するが、実体が入るまでは解決しても 404 である。
**この状態を受け入れる** —— `ToolCatalog` の突合は「申告の有無」で行われ、実行口の実在は見ていない。
権限伝播の方式（方式 A へ寄せて McpServer が資格情報を運ぶか、内部専用の別経路にするか）は
**裁定を別 issue へ切り出す**。

**決定 6**: 申告端点は **FR-15 の `/internal/introspection` と同じ規約系・同じ防御**に置く ——
認可を要求せず、メッシュ内部限定（ネットワーク分離と mTLS が防御）、`ExcludeFromDescription()` で
OpenAPI から外す。**BFF 契約ではないので `docs/api/openapi.yaml` には現れない。**

## 理由

- 決定 2 は「統制の適用点を数えられる状態に保つ」ためである。適用点が 1 つなら、そこにテストを置けば
  全ツールを覆え、ツールが増えても入れ忘れが起こらない。
- 決定 3 の代償は「`doc_scope` を持たない候補が漏れる」ことだが、候補はコード内の静的な宣言であり
  レビュー対象である。**既存実装と向きを揃えることを優先した**（IADR-0269 決定 4 と同じ判断）。
- 決定 5 は「動かないものを実装しない」ためではなく、**間違った形で実装しないため**である。
  本文で渡された scope を信じる口は、開いた瞬間に到達できる誰もが任意の主体を名乗れる。

## 結果

- 良い影響:
  - **実効カタログが空でなくなる。** 公開構成（`Configuration/mcp-publication.json`）と収集先
    （`appsettings.json` の `Mcp:Services`）の両方を埋め、6 ツールが載る。
  - 除外の向き（集合帰属か否定か）が**陽性対照テストで判別できる** —— スコープを持たない候補が
    落ちないことを 3 サービスで測っている。
  - 収集 → 突合の統合試験が **Docker 非依存**になった（実サービス 3 本を in-process で起こす）。
    `integration.yml` は develop への push と日次でしか走らないため、Docker を要る形にすると
    **PR では一度も実走しない**。本試験は PR の `ci` ジョブで毎回走る。
- 悪い影響 / トレードオフ:
  - 申告 DTO が 3 サービスに写しとして重複する（決定 4。昇格までの過渡状態）。
  - 申告した `endpoint` が解決できない期間が生じる（決定 5）。
  - `Mcp:Services` の既定値をコードの `appsettings.json` に持つため、配備側（`deploy/`）の
    上書きと二重になり得る。
- フォローアップ:
  - **共通エンベロープの実行口と権限伝播の方式**（決定 5）。裁定を要する。
  - **申告 DTO の `Platform.Shared.Contracts` 昇格**（決定 4 / IADR-0269 決定 6）。
  - `deploy/` の配線（`Mcp__Services__*` の注入、Istio Ingress の `/mcp` ルーティング、レート制限初期値）。
  - `notifications/tools/list_changed` の配信（IADR-0269 から継続）。

## 関連

- Supersedes: なし
- Superseded by: なし

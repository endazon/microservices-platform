---
title: IADR-0651 MCP の tools/list_changed は、無状態の Streamable HTTP のまま subscriptions/listen の流れで送る（自前の listen の処理）。セッションの方式は変えない
type: impl-adr
status: Accepted
related_ids: [FR-16, UC-08, ADR-0024, IADR-0269, IADR-0292]
author: claude
created: 2026-10-30
updated: 2026-10-30
related_specs:
  - ../specs/20261030_2142_mcp-tools-list-changed.md
---

# IADR-0651: tools/list_changed の送出（#2142）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-30
- 決定者: claude（#2142。親 #2047 の指摘 L-13）

## 起点・関連

- 起点 issue: #2142。計画 ADR-0024 §2（コア改修なしでの追従）。
- 残件の出所: [IADR-0269](./IADR-0269_mcp-tool-publication-and-service-account-exclusion.md)、[IADR-0292](./IADR-0292_service-mcp-tool-self-declaration.md)。
- 作業仕様書: [20261030_2142_mcp-tools-list-changed](../specs/20261030_2142_mcp-tools-list-changed.md)。

## 背景

- 実効ツール一覧の版は進むが、クライアントへ知らせていなかった。
- MCP 面は Streamable HTTP を**無状態**で動かしている（SDK 2.2.0 の既定）。
  - 無状態では、要求の外から送る通知は捨てられる。
  - SDK が自動で送る list_changed は、`ToolCollection` の変化をセッション全体へ流すもので、無状態では届かない（実測）。

## 決定

1. **セッションの方式は変えない（無状態のまま）。**
2. 2026-07-28 の改訂の `subscriptions/listen` の処理を自前で持ち（`WithSubscriptionsListenHandler`）、その応答の流れで list_changed を送る。
   - 受付の前に購読し、受付を 1 回送り、版が進むたびに送る。
   - tools の list_changed を求めない listen は、受付（扱う種類なし）を送ったらすぐ閉じる。送る通知が無いのに接続を保持しない（#2315 のレビュー）。
   - 各通知の `_meta` に listen の要求 ID を付ける。
   - 続けて進んだ版は 1 回にまとめる。
3. `capabilities.tools.listChanged = true` を申告する。
4. 合図の元は `ToolCatalog.Changed` とする。版が進んだときだけ呼ぶ。

### 決定 5: 長命の接続に上限を置く（#2315 の監査）

- 上限は `ToolListChangedListenLimits` が持つ。
  - **寿命**: 1 本の listen は最長 10 分で空の結果を返して閉じる。クライアントは開き直す。
  - **同時本数**: 主体ごと 4 本、プロセス全体 256 本。超えた要求は `InvalidRequest` で断り、開いている listen は切らない。
  - 主体の鍵はトークンの `sub`（JwtBearer の既定の写像で NameIdentifier になる）である（#2315 のレビュー）。表示名（preferred_username）は一意でも必須でもないので、鍵にしない。
  - **`sub` の無いトークンは listen 自体を断る**（`InvalidRequest`）。トークンの検証は `sub` を必須にしていない（実測。試験のトークンは sub 無しで `/mcp` を通る）。共有の枠を作ると、sub の無い利用者どうしが互いを締め出せる。
- 既存の上限では抑えられないので、アプリの中に置いた。確かめた事実は次のとおりである。
  - SDK の `IdleTimeout` / `MaxIdleSessionCount` は有状態のセッションの上限で、無状態の要求には効かない。
  - エッジ（`deploy/helm/microservices-platform/templates/edge.yaml` の `/mcp` の VirtualService）に timeout の設定は無い。
  - Istio の既定にも頼れない。

### 事実の整理（#2315 の監査）

1. **レプリカ間の一貫性**
   - `ToolCatalog` は singleton で、版はプロセスごとに持つ。
   - 突合（`ToolCatalogRefresher`。既定 300 秒周期）はレプリカごとに走る。各レプリカは、自分の版が進んだときに自分に付いた listen へだけ通知を出す。
   - 一部のレプリカだけが先に版を進めても、通知は取り逃がさない。別のレプリカは、自分の突合で同じ変化を見たときに版を進めて通知する。遅れは最大で 1 周期である。
   - あるレプリカで収集が失敗し続けると、そのレプリカに付いた listen への通知は、収集が戻るまで遅れる。カタログ自体もその間古いので、通知と一覧の食い違いは起きない。
   - いまの配備は 1 レプリカである（`values.yaml` の `services.mcp.replicas: 1`。HPA の対象外）。
2. **長命の接続の上限**: 上の決定 5。
3. **認可の失効**
   - listen は開いた時点で `/mcp` の認証・認可を通る。開いた後にトークンが失効したり、ロールが外れたりしても、その listen は寿命（最長 10 分）まで通知を受け続ける。
   - 通知は `notifications/tools/list_changed` の合図だけで、中身は `_meta` の listen の要求 ID しかない。ツール一覧そのものも、名前・件数も運ばない。漏れ得るのは「公開ツールの集合が変わった時刻」だけである。
   - 一覧は `tools/list` が要求ごとに認証・ABAC を通して組む。失効した利用者は一覧を引けない。
   - 寿命で閉じた後の開き直しは認証を通り直すので、失効した利用者はそこで止まる。

## 却下した案

- **有状態（`Stateful` / `StatefulForInitializeClients`）へ変えて、セッション全体へ送る**: 複数のレプリカでセッションの付着（affinity）が要り、配備の前提が変わる。2026-07-28 の改訂はセッションを廃した方向でもある。
- **中身の無い `ToolCollection` を置いて `Changed` を鳴らす**: 無状態では、どのクライアントにも届かなかった（実測）。

## 帰結・残余リスク

- 初期化の握手の利用者（2025-11-25 以前の改訂）へは届かない。その利用者は `tools/list` を引き直して知る。届けるには有状態への切り替え（上の却下案）が要る。
- 通知は 1 プロセスの中のカタログの変化だけを運ぶ（上の「事実の整理」1.）。
- 失効したトークンでも、listen の寿命（最長 10 分）の間は合図だけが届く（上の「事実の整理」3.）。

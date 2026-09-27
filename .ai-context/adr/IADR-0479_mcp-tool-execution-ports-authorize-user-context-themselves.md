---
title: IADR-0479 MCP のツールの実行口は MCP サーバー（許可集合）が運んだ利用者文脈だけを信じ、その利用者で認可サービスへ自分で判定を問う。MCP サーバーは利用者名と操作だけを運ぶ
type: impl-adr
status: Accepted
related_ids: [FR-16, UC-08, NFR-09, NFR-16, ADR-0024, ADR-0034, ADR-0086, ADR-0088, ADR-0117, IADR-0269, IADR-0292, IADR-0379, IADR-0416, IADR-0426, IADR-0462]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0117_mcp-tool-destination-and-execution-context.md 決定 1〜4
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1・4・§結果
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1
  - planning:projects/microservices-platform/06_technical/11_mcp-server-integration.md §3・§6
related_specs:
  - ../specs/20260927_issue-1611_mcp-tool-execution-ports.md
---

# IADR-0479: MCP のツールの実行口の認可（#1611）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-27
- 決定者: claude（#1611。計画 ADR-0117 決定 3 の実装側の形）

## 起点・関連

- 関連する計画書 ID: FR-16（MCP サーバー統合）/ UC-08 / NFR-09 / NFR-16
- 関連する計画 ADR: ADR-0117（決定 1: 宛先は申告したサービス＋ツール名／決定 3: 利用者文脈を本文で運び受け手が自分で認可する）、
  ADR-0086（決定 1・4。呼び出し元が `user_id` を正直に主張することへの依存）、ADR-0088（属性は認可サービスが引き直す）、
  ADR-0034 決定 9（サービスアカウント実行は個人資料を一律除外）
- 関連する実装 ADR: [[IADR-0292]] 決定 5（権限伝播の裁定の切り出し）、[[IADR-0462]]（経路 ④-b の輸送。`scope` を暫定とした）、
  [[IADR-0426]] 追記 1（`DocumentSearch` の中継者の許可集合。同じ形）、[[IADR-0416]]（検索サービスが自分で許可を引く）、[[IADR-0379]] 決定 4（s2s）

## コンテキストと課題

#1516 は MCP サーバーのツール実行を gRPC（`platform.mcp.v1.McpToolExecution/Execute`）へ移したが、本文は従前の REST の写し
（MCP サーバーが組んだ実行スコープ `scope`）のまま暫定にし、受け口はどのサービスにも無かった（fail-closed）。
ADR-0117 決定 3 は、本文を利用者文脈（`user_id`・`action`）とツールの引数へ改め、受けたサービスが自分で認可すると定めた。
実装で決める必要があったのは次の 5 点である。

1. 本文の利用者文脈を**誰が運んだときに**信じるか
2. 認可サービスへ**何を**渡して判定を問うか（属性・操作）
3. 要求のツールが本当に自分の申告かをどう確かめるか
4. サービスアカウント実行（ADR-0034 決定 9）を、属性も除外制約も運ばない本文でどう伝えるか
5. MCP サーバーが運ぶ `user_id` の綴り

## 検討した選択肢

| 論点 | 採用 | 退けた選択肢と理由 |
| --- | --- | --- |
| 1 | **許可集合（既定 `mcp-server` だけ）の機械クライアントが運んだときだけ信じる**（`TrustedUserContextRelay`） | `ServiceCaller` だけで信じる —— `platform-service` は 11 のサービスアカウント（別プロジェクトのものを含む）が持ち、どれもが任意の利用者を名乗れる（#1636 と同じ理由） |
| 2 | **利用者名だけを渡し、属性は空。操作は受け口が自分のツールから決め、本文の操作は突き合わせるだけ** | 本文の操作をそのまま渡す —— 書き込みを伴うツールが増えたとき、呼び出し元が `read` と名乗れば判定が緩む |
| 3 | **自分の申告（`McpToolDeclarationSource.Declare`。個人資料の除外を通した後）と申告名で序数一致** | 実装を持つツール名だけで引く —— 申告から落とした候補も実行できてしまう |
| 4 | **`user_id` の接頭辞 `service-account-` から受け手が導く**（要求側の 1 層目）。応答側のフィルタは MCP サーバーに残す | 本文に除外の旗を足す —— ADR-0117 決定 3 が挙げない項目を運ぶことになり、利用者名と旗の食い違いという新しい状態を作る |
| 5 | **有人は `preferred_username`、サービスアカウントは `service-account-<client>`（小文字。Keycloak の利用者名の形）。有人で利用者名が無ければ送らない** | `sub` を運ぶ —— 下流と認可サービスは利用者を利用者名で引く（BFF が運ぶ `user_id` と同じ綴り）。client の識別子をそのまま運ぶ —— 同じ綴りの利用者が居ればその人として判定される |

## 決定

1. **proto**: `ExecuteMcpToolRequest` から `scope`（番号 3）を外し `reserved 3; reserved "scope";`、`McpToolUserContext user = 4`（`user_id`・`action`）を足す。
   `McpToolInvocationScope` は削る（破壊的変更 2 件。allowlist で承認し baseline へ記録した）。
2. **受け口の判定の順番**（前段で落ちたら後段を走らせない）: `ServiceCaller` → 許可集合（`PERMISSION_DENIED`）→ 利用者文脈の有無（`INVALID_ARGUMENT`）→
   自分の申告との突合（`NOT_FOUND`）→ 操作の突合（`INVALID_ARGUMENT`）→ 引数の検証（丸めない。`INVALID_ARGUMENT`）→
   **利用者名で認可サービスへ判定を問う（属性は空）** → 既存の本体（検索なら `SearchEndpoint.ExecuteAsync`）→
   サービスアカウント実行なら個人資料を落とす → 共通エンベロープ（件数は判定と除外の後）。
3. **許可集合**は受け口ごとの節 `McpToolExecution:TrustedUserContextClients`（配列。1 つの値は起動時に止める）。既定 `mcp-server` は
   compose・helm の MCP サーバーの s2s の client と realm の機密クライアントに一致させ、配線試験で固定する。
4. **MCP サーバー**は `ToolUserContext(UserId, Action)` だけを組む（`ToolInvocationScope` を置き換えた）。公開ツールはすべて読み取りなので `Action = read`。
5. **段に分ける**: 最初の段は共通部分（proto・MCP サーバー）と RetrievalService の受け口。DocumentService・GraphService は同じ規則で受け口を足す
   （IADR-0139 決定 1 の条件 A・C・F を満たさないので束ねない。作業仕様書に表で残した）。

## 結果

- ツールの実行は、受け口を持つサービス（今は検索）について利用者の権限の範囲で動く。受け口の無いサービスは従来どおり fail-closed。
- 旧い MCP サーバーが番号 3 に scope を載せても受け口は読み飛ばし、`user` が無いので拒否する。配備の順番に依らず緩む向きは無い。
- 🔴 **残るリスク**:
  - 呼び出し元（MCP サーバー）が `user_id` を正直に主張することへの依存は残る（ADR-0086 決定 4 / ADR-0117 決定 3 が受け入れた範囲。許可集合で主体を 1 つに狭めた）。
  - **サービスアカウントの属性**: MCP サーバーの登録簿でサービスアカウントへ割り当てた属性（ADR-0024 §3）は下流の判定に使われなくなる。
    判定は認可サービスが IdP の `service-account-<client>` の利用者から引き直した属性で行う（ADR-0088）。登録簿の属性と IdP の属性の関係は計画に
    定めが無い（計画への環流の要否は利用者の判断に委ねる）。どちらに転んでも個人資料は受け手と MCP サーバーの 2 層で落ちる。
  - 検索結果の参照リンク（`reference_url`）は返さない（索引が持つのは内部の格納先であり、利用者へ見せるリンクではない）。越境不可の文書は本文だけが落ちる。

## 残るもの

- DocumentService・GraphService の受け口（#1611 の後続の段）。
- MCP サーバーと受け口を同じ器で動かす結合試験（ユニットをまたぐ器が無い）。

---

## ［2026-09-27 追記 / #1611］段 3 GraphService

> 上の本文（段 1 の決定）は書き換えない。本節は段 3（GraphService の受け口）で決めたことだけを足す。作業仕様書の同日付の段 3 の節と対になる。

- **段の並びの変更**: 段 2（DocumentService）は #1615（内容の ABAC）の後ろへ回した —— DocumentService の読み取りには内容の ABAC がまだ無く、
  実行口を今の判定点に乗せると組織文書が属性で絞られないため。段 3 を先に入れ、#1611 は段 2 で閉じる。
- **受け口の形**: 決定 2 の判定の順番・決定 3 の許可集合（`McpToolExecution:TrustedUserContextClients`。既定 `mcp-server`。GraphService の
  `Program.cs` で `ThrowIfScalar` → `Configure`）を RetrievalService と同じ形で持つ。近傍展開の面の集合（`GraphNeighbors:`。既定 `retrieval-service`）とは
  キーも集合も共有しない。3 ツール（`graph.get_backlinks` / `graph.get_links` / `graph.traverse`）とも操作は `read`。
- **第二の判定点を作らない**: 認可は既存の `ExpandNeighborsUseCase`（検証 → `IGraphAccessResolver.ResolveForUserAsync`〔利用者名・属性は空・`read`〕→
  `AuthorizedNode` の型ゲートによるホップごと ABAC → `GraphViewResponse.Seal`）をそのまま通す。被参照・参照先は 1 ホップの結果から辺の向きで選ぶ
  （`Edge` の Source → Target が意味方向。バックリンクは Target の逆引き）。応答は `Seal` 済みのノードと辺だけから作る。
- **既存の経路への変更は 2 点**（どちらも既定で従来と同じ挙動）:
  1. `ExpandNeighborsUseCase` / `GraphTraversal` の `excludePrivateNote`（既定 false）: サービスアカウント実行（決定 4 の接頭辞）では個人資料を
     **起点・中継・結果のどこにも使わない**（非許可と同じくその場で刈る）。応答から落とすだけの形は退けた —— 個人資料を橋にした先の文書が残り、
     その関係の存在をサービスアカウントへ明かす。応答の写像でも落とす（多層防御）。
  2. `GraphViewResponse` に `Seal` が通したノードの属性を内部の項目（`JsonIgnore`・internal）として持たせた。MCP サーバーの越境判定と 2 層目の除外は
     属性を読むため、属性の無い応答では 2 層目が効かない。REST の応答の形は変えない。
- **応答の限界**: 共通エンベロープは文書の並びしか持たないので、`graph.traverse` は**辺を返さない**。申告の説明（旧「到達できた文書と辺を返す」）を
  「到達できた文書（起点を除く）を返す」へ直した（返らないものを呼び出し側の LLM に約束しない）。辺を運ぶにはエンベロープ（ADR-0024 §4）の改定が要り、
  本段では行わない（緩む向きではない）。

### ［2026-09-27 追記 / #1611］段 3 監査（B-1）: エンベロープの属性は許可リストの文書属性だけ

- **エンベロープの属性は許可リストの文書属性だけ。共有先は運ばない。** GraphService の `GraphDocument.Attributes` は `shared_with` を重ねた ABAC 判定用の像であり、
  全キーを写すと共有先は所有者にだけ返す規則（ADR-0098 / IADR-0450）を MCP の経路が迂回する。許可リストは MCP サーバーが応答の統制で読むキー
  （`confidentiality`・`doc_scope`・`project`）だけとし、`owner` 等も運ばない。`Seal` の `NodeAttributes` からも `shared_with` を除く（二重に守る）。
- `graph.traverse` の `total_count` は打ち切り時に許可済みの全体件数（起点を除く）を返す。本体の失敗の `INTERNAL` は固定文言にした。

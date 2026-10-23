---
title: IADR-0594 外部 AI エージェントの入口としてエッジに /mcp（exact /mcp ＋ prefix /mcp/。rewrite 無し）を置く。認証は MCP サーバーの audience 検証に委ね、エッジでは JWT を再検証しない。エッジの主体は L7 の DENY で /mcp の面に閉じる。本番像は opt-in（クライアント単位のレート制限が未配備のため）
type: impl-adr
status: Accepted
related_ids: [FR-16, UC-08, UC-09, ADR-0024, ADR-0021, ADR-0134, ADR-0005, NFR-09, IADR-0348, IADR-0297, IADR-0516, IADR-0523, IADR-0589, IADR-0590, IADR-0533]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0024_mcp-server-integration.md（§決定「配置」・§結果「新しい攻撃面」）
  - planning:projects/microservices-platform/06_technical/11_mcp-server-integration.md（§1・§3・§4・§5 レート制限）
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md（入口・ルーティング・レート制限）
related_specs:
  - ../specs/20261023_2045_edge-mcp-entry.md
---

# IADR-0594: エッジの /mcp（外部 AI エージェントの入口）（#2045）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2045。PoC の機能面の全体監査の指摘）

## 起点・関連

- 起点 issue: #2045。
- 関連する計画書 ID: FR-16・UC-08・UC-09。計画 ADR: **ADR-0024**（入口は Istio Ingress Gateway の `/mcp`）・ADR-0021・ADR-0134・ADR-0005。
- 引き継ぐ実装判断: [IADR-0348](./IADR-0348_private-notes-sync-edge-route.md)（エッジから内部サービスへ直接通す経路の先例。opt-in の既定・L7 の DENY・NetworkPolicy の穴の型）・
  [IADR-0516](./IADR-0516_sc12-keycloak-service-account-provisioning.md) / [IADR-0523](./IADR-0523_platform-api-audience-validation.md)（`/mcp` の audience 検証）・
  [IADR-0590](./IADR-0590_mesh-mtls-strict-by-default.md)（mTLS STRICT の既定）・[IADR-0589](./IADR-0589_egress-default-deny-network-policy.md)。
- 解く記録: [IADR-0297](./IADR-0297_sc12-mcp-client-management.md) §結果「悪い（受容）: 外部 AI エージェントの入口（Ingress の `/mcp`）は公開していない」。
  本 IADR が経路を置き、受容のうち**認証**（ADR-0134 で解消済み）と**越境**（MCP サーバーの統制で既存）は前提として満たされ、**レート制御**だけが残る（決定 4）。
- 作業仕様書: [20261023_2045_edge-mcp-entry](../specs/20261023_2045_edge-mcp-entry.md)（母集合・受け入れ基準・変異）。

## コンテキストと課題

MCP サーバー本体（`/mcp` の audience 検証・許可リスト・ABAC の委譲・越境の縮退）は実装済みだが、本番像のエッジ（`templates/edge.yaml`）にも
経路 B の 2 つのエッジにも `/mcp` の経路が無く、`/mcp` 宛は catch-all で SPA へ落ちる。FR-16 の「外部 AI エージェントはツールを実行できる」は配備形で成立しない。
MCP サーバーの 1 つのポートには `/mcp` と管理 REST（`/mcp-clients*`。BFF が利用者のトークンを中継）・OpenAPI・自己申告・ヘルスが同居する。

## 検討した選択肢

1. **経路だけを置き、認証は MCP サーバーに委ね、エッジの主体を L7 の DENY で `/mcp` の面に閉じる（採用）**。
2. 経路を `prefix: /mcp` 1 本で置く。Istio の `prefix` は文字列の前方一致で `/mcp-clients` を飲み、管理 REST がエッジに出る。採らない。
3. **エッジ（メッシュ）でも JWT を再検証する（`RequestAuthentication` ＋ requestPrincipals を要する門）**。採らない。理由:
   - MCP サーバーの Pod へ当てると、`RequestAuthentication` は送り元を選べず全要求に掛かる。BFF の管理 API の中継（`aud=platform-api`）と
     MCP クライアント（`aud=mcp-server`）を 1 つの規則で両立させるには audience を規則から外すしかなく、audience の検証は結局アプリに残る。
     発行元はエッジ host と in-cluster の 2 つがあり（`global.auth.validIssuers`）、JWKS は istiod が取りに行く（到達性がアプリと別の経路になる）。
   - ゲートウェイへ当てると全経路に掛かり、同期プロトコルの**不透明な Bearer**（JWT でない）が不正な JWT として 401 に落ちる（IADR-0348 が避けた形）。
   - MCP の認可の流れは、トークン無しの要求に**リソースサーバーが 401**（`WWW-Authenticate`）を返すことを前提にする。メッシュの門は 403 を返し、クライアントの流れを壊す。
   アプリの検証（署名・発行元・期限・`aud=mcp-server` だけ。`McpAudienceAuthenticationTests`）は既にこれより強い。エッジに同じ検証を二重に置く利得は、
   既存の正当な要求を落とす危険に見合わない。
4. **クライアント単位のレート制限を同時に入れる**。計画 11 §5 は「Istio Ingress Gateway でクライアント単位」「初期値は実装時に負荷試験で確定」とする。
   負荷試験は未実施で、クライアント単位に数えるにはエッジでトークンの主体を読む器（JWT の主体のヘッダ化・グローバルのレート制限サービス等）が要り、
   リポジトリに EnvoyFilter / レート制限の先例は 0 件である。本 IADR では入れない（決定 4 で既定を倒し、段取りは計画へ環流する）。

## 決定

**決定 1（経路）**: 本番像のチャート（`edge.mcp`）・経路 B の Istio の overlay・経路 B の Traefik の Ingress に、`/mcp`（完全一致）と `/mcp/` 配下を
MCP サーバーの Service（`mcp-service:8080`）へ振る経路を置く。rewrite しない（公開パス＝`MapMcp("/mcp")`）。catch-all より前。
`/mcp-clients` はどのエッジでも catch-all（SPA）へ落ちる。
🔴 **門の層の数はエッジごとに違う。** 本番像（Istio）は経路の絞り込み＋L7 の DENY（決定 2）＋NetworkPolicy（決定 3）の 3 層。
経路 B の Istio の overlay と **Traefik の Ingress は経路の絞り込み 1 層だけ**である（L7 の DENY も ingress の NetworkPolicy も無い。経路 B は
`networkPolicy.enabled=false`）。Traefik は `Exact /mcp` ＋ `Prefix /mcp/`（末尾スラッシュ込み）なので、Prefix が文字列の前置で照合されても
パス要素の境界で照合されても `/mcp-clients` には当たらない。経路 B は配備形ではなく、認証（決定 5）は 3 つのエッジで同じである。

**決定 2（L7 の門）**: 本番像で有効のとき、`AuthorizationPolicy` `<edge.mcp.service>-edge-mcp-only`（DENY）を置く。from は gateway の Namespace
（NetworkPolicy の穴と同じ単一情報源）、to は `notPaths: ["/mcp", "/mcp/*"]`（経路と同じ字面）。ALLOW にしない（名前空間の中の BFF・プローブを巻き込む。IADR-0348 と同じ理由）。
メソッドは絞らない（Streamable HTTP は POST・GET・DELETE）。STRICT（既定）で穴が閉じ、PERMISSIVE の限界は同期の門と同じ。

**決定 3（L3/L4）**: 本番像で有効のとき、NetworkPolicy `allow-edge-ingress-to-mcp` で gateway の Namespace → MCP サーバーの当該ポートだけを開ける。

**決定 4（既定は opt-in）**: `edge.mcp.enabled` の既定は **false**。無効なら経路・門・穴のどれも描かない（稼働中の配備は変わらない）。
理由: ADR-0024 §結果は外部エージェントという新しい攻撃面を「クライアント登録制・レート制限・監査ログで対処」とし、そのうちレート制限が未配備である。
認証（登録制のクライアント・audience）と監査（ツール呼び出しの監査ログ）は効いているが、外部の新しい面を開けるかは運用者の明示の判断とする。
経路 B の overlay は overlay 自体が opt-in（ISTIO=1・LOCALEDGE=1）で FR-16 を実測する環境なので無条件に持つ（同期の経路と同じ扱い）。

**決定 5（認証）**: エッジは要求を加工せず MCP サーバーへ渡す。認証は MCP サーバーの `McpAudience` スキーム（`aud=mcp-server` だけ）であり、
トークン無し・platform の audience のトークンは 401。エッジに認証を緩める・迂回させる経路（rewrite・別の行き先・`/mcp` 以外のパス）を置かない。

**決定 6（compose には置かない）**: compose は配備形ではなくエッジを持たない。frontend の Caddy に `/mcp` の中継を足すと、同じイメージの k8s の frontend Pod が
catch-all 経由で `/mcp` を中継し、決定 4 の opt-in を迂回する経路になる。

**決定 7（門）**: 統合スタックの門（`check-mcp-client-provisioning.js` の M13）が稼働のエッジで、トークン無し 401・platform の audience 401・
無人の MCP クライアントの実トークンで initialize の結果・エッジ経由の `/mcp-clients` が管理 REST に届かない（直に当てると 200 を対に置く）、を測る。
本番像のチャートの形と評価は `helm-edge-mcp-entry.test.js`、3 つのエッジの経路の字面は `k8s-local-up.test.js` が固定する。

## 結果

- 良い: FR-16 の入口が配備形で成立する（本番像は `edge.mcp.enabled=true` で）。エッジから届くのは `/mcp` の面だけで、管理 REST は経路と L7 の 2 層で出ない。
- 悪い（受容）: **クライアント単位のレート制限が無い。** 有効化した配備では、登録済みの MCP クライアントの呼び出し頻度をエッジで抑えない
  （MCP サーバーのツールの実行期限・各サービスの負荷特性だけが上限になる）。解消は負荷試験と器の決定を伴う別作業であり、段取りを計画へ環流する。
- 悪い（受容）: エッジは JWT を再検証しない（選択肢 3）。MCP サーバーの認証の設定を誤ると、エッジの側には第 2 の検証が無い。
  その設定は `McpAudienceAuthenticationTests` と統合スタックの M12・M13 が固定する。
- 悪い（受容）: MCP サーバーは OAuth の保護リソースのメタデータ（RFC 9728）を返さない。有人のエージェントが 401 から認可サーバーを自動で発見する流れは
  エッジの経路だけでは成立しない（クライアントに認可サーバーとクライアント ID を設定して使う）。別の作業である。
- 悪い（受容）: 経路 B（Istio の overlay・Traefik の Ingress）は経路の絞り込み 1 層だけである（決定 1 の注記）。
- 計画への環流: レート制限を入口と別の段にする段取りの裁定を planning#828 で求めた。

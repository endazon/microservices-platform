---
title: 作業仕様書 — 稼働 k3s で east-west gRPC（h2c）の往復を実測する手順書を置く（#1255 残射程 2 と #1517 の保留を解く観測点）
type: spec
status: done
related_ids:
  - NFR-09
  - NFR-16
  - FR-15
  - FR-16
  - ADR-0005
  - ADR-0029
  - ADR-0075
  - ADR-0089
  - ADR-0117
  - IADR-0307
  - IADR-0377
  - IADR-0379
  - IADR-0426
  - IADR-0462
  - IADR-0487
  - IADR-0488
author: claude
created: 2026-10-04
updated: 2026-10-04
issue: "#1255"
---

# 作業仕様書 — 稼働 k3s で h2c 往復を実測する手順書

## 起点

- **#1255**（east-west 同期の残りを gRPC へ展開する）の 2026-09-27 のオーナー判断: 移行済み経路の REST 退役と
  `IADR-0379` 決定 5（並走中の正は REST）を反転する IADR は、**稼働 k3s で gRPC（h2c）経路の往復を実測するまで着手しない**。
  同 issue の §やること 7（PERMISSIVE と STRICT の両方で `authorization-service:8081` へ往復し、記録を残す）が未了。
- **#1517**（扇形 2 経路＝introspection・MCP ツール申告の REST 退役）も同じ条件で保留。解けたと分かる観測点は
  「ローカルの稼働クラスタで、MCP のツール申告の収集（④-a）と introspection（⑤）の gRPC 経路が往復したことの記録」。
- 依頼（PoC）: 測る経路と方法（経路ごとに何のログ・メトリクスで成功を示すか）、合否基準、稼働クラスタで gRPC 経路を
  有効にする構成（helm values。`extraEnv` が配列なので上書きでリストが丸ごと置換される罠を避け、`helm template` の差分で検証）、
  NetworkPolicy / Istio の考慮（サイドカーの有無・appProtocol／ポート名）、切り戻し、中止条件。

## 射程

### やること

1. `docs/operations/east-west-grpc-h2c-roundtrip-measurement-runbook.md` を新設する（`type: runbook`。実行は利用者。
   AI は稼働クラスタに触れない —— 本書の期待値はコードと宣言から導いたもので、実測ではないと明記する）。
2. 経路表を**コードから**引く（記憶・issue 本文の数えを転記しない。traceability 規則 9・10）。
3. 計測用の上書き値を**稼働リリースの値から組む生成器**（Node のワンファイル。Runbook に heredoc で埋め込む）を用意し、
   `helm template`（v3.16.4）で差分が「足した行だけ」であることを検証する。素朴な上書きが既存の env を消すことも同じ方法で示す。
4. 運用仕様書（`docs/operations/operations.md`）のメッシュの節から 1 行で辿れるようにする。

### やらないこと

- 稼働クラスタでの実測そのもの（クラウドのセッションから届かない）。
- REST の退役・`IADR-0379` 決定 5 を反転する IADR の起票（実測の後の段。本作業では IADR を採番しない ——
  判断を伴わない運用手順書であり、実装判断を新設しない）。
- 新しい検査器・スクリプトの追加（生成器は Runbook 内の使い捨てに留める。検査器の新設は同型事故 2 回からの規約）。
- チャート・コードの変更。

## 母集合の引き直し（規則 9・10。基点 `origin/develop` `b0eaeff7`）

### 呼び出し先（h2c の受け口）

- `AddPlatformGrpcListener` を呼ぶ `Program.cs`: 13 サービス（LlmGateway・Notification・Authorization・Feedback・Conversion・
  Retrieval・Document・Wiki・Ingestion・AiAnalysis・Dashboard・Graph・DataSource）。McpServer と BFF は呼ばない（呼び出し元のみ）。
- helm `services.<name>.grpcPort: 8081` を持つのも同じ 13（`helm template` の描画で containerPort 8081 を持つ Deployment を数えて一致）。
- `MapGrpcService<…>` の面: Authz（`AuthzScope`・`UserDirectory`）、LlmGateway（`LlmEmbedding`・`LlmCompletion`）、Notification
  （`NotificationIngress`）、Document（`DocumentRead`・`DocumentTagWrite`・`TagDictionary`・`McpToolDeclarations`・`McpToolExecution`）、
  Retrieval（`AttributeValues`・`DocumentSearch`・MCP 2 面）、Graph（`GraphNeighbors`・MCP 2 面）、Dashboard（`KnowledgeHealthReport`）、
  全サービス共通の `ServiceIntrospection`（`MapPlatformIntrospection` が REST と対で張る）。

### 呼び出し元と切替キー（コードの `AddressKey` と `Program.cs` の分岐から）

| 切替キー | 呼び出し元 | helm 既定（values.yaml）で入っているか |
| --- | --- | --- |
| `Services:AuthorizationServiceGrpc` | aianalysis・graph・wiki・retrieval・datasource・document・mcp・bff | bff **以外**は入っている（bff は意図的に未設定） |
| `Services:LlmGatewayGrpc` | ingestion・retrieval（埋め込み）、aianalysis・graph・conversion（生成） | 入っている |
| `Services:NotificationServiceGrpc` | document | 入っている |
| `Services:DocumentServiceGrpc` | bff（文書読み取り 4 rpc）、graph（タグ反映・タグ辞書） | 入っている |
| `Services:RetrievalServiceGrpc` | bff（属性値）、aianalysis（RAG 検索） | 入っている |
| `Services:GraphServiceGrpc` | retrieval（近傍展開・辺種別の重み） | 入っている |
| `Services:DashboardServiceGrpc` | graph（観測値の報告。1 時間周期・初回は 1 周期後） | 入っている |
| `Introspection:GrpcServices:<名>` | bff（構成情報 API。13 宛先） | 13 宛先とも入っている |
| `Mcp:GrpcServices:<名>` | mcp（申告の収集 3 宛先・ツール実行） | 3 宛先とも入っている |

⇒ **稼働クラスタで gRPC 経路を有効にするために足す構成は（BFF → 認可を除き）無い。** 現行の develop のチャートとイメージを
入れれば既に gRPC 経路で動いている。足すのは**観測のための構成**（呼び出し先の要求ログの水準）だけである。

### 除外と理由

- AST → MSP（AST#584 の受け皿）: 本リポジトリの射程外。
- BFF が利用者の資格情報を付けて中継する呼び出し（エッジ）: east-west に数えない（裁定済み）。
- MCP のツール実行（`McpToolExecution/Execute`）: REST の並走を持たない（退役の対象ではない）。測るのは任意とする。

## 設計判断（Runbook の形）

1. **成功の証拠は 3 点の積**にする: ①呼び出し先の `Microsoft.AspNetCore.Hosting.Diagnostics` の
   `Request finished HTTP/2 POST http://<宛先>:8081/<package>.<Service>/<Method> - 200`、②呼び出し先サイドカーの
   `istio_requests_total{reporter="destination",request_protocol="grpc",grpc_response_status="0",connection_security_policy="mutual_tls",source_workload="<呼び出し元>"}` の増分、
   ③呼び出し元に当該経路の gRPC 失敗ログが無いこと。理由: 収集器・クライアントは**成功時に何もログしない**
   （`GrpcServiceIntrospectionCollector` / `GrpcToolDeclarationCollector` を読んで確認）うえ、`CreatePlatformChannel` が
   `LoggerFactory` を渡さないので Grpc.Net.Client のログも出ない。HTTP の 200 は gRPC の状態を含まない（trailer）ので、
   gRPC 状態はメッシュのメトリクスで取る。
2. ①のために呼び出し先 13 サービスへ `Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics=Information` を一時的に足す
   （appsettings の `Microsoft.AspNetCore: Warning` が要求ログを抑えているため）。足し方は**チャート既存の `extraEnvAppend`**
   （#1389）を、**稼働リリースの値（`helm get values --all -o json`）に継ぎ足した形**で上書きファイルにする。
   素朴に `extraEnv` / `extraEnvAppend` / `--set …[0]` を書くと、Helm のリスト置換で既存の env が消える（下の検証）。
3. mTLS モードの切替は `scripts/lib/mesh-mtls-mode.sh` の `set_mesh_mtls_mode`（helm を唯一の書き手にする。IADR-0377）。
   `--reuse-values` なので計測用の上書き値は保たれる。
4. 切り戻しは「保存した利用者値だけで `helm upgrade`」の 1 本（env とモードを同時に戻す）。`helm get manifest` の差分 0 で確かめる。
5. STRICT の効きは、サイドカー無しの使い捨て Pod（`sidecar.istio.io/inject=false`）から `curl --http2-prior-knowledge` で
   `authorization-service:8081` を叩く陰陽の対で示す（PERMISSIVE: アプリへ届いて 401／STRICT: 接続が切られる）。

## 検証（helm v3.16.4。`values-local.yaml` ＋ `k8s-local-up.sh` の ISTIO=1 の `--set` を「稼働リリースの利用者値」として模擬）

| 上書き | 消えた行 | 足された行 | 判定 |
| --- | --- | --- | --- |
| 生成器（観測の env のみ） | 0 | 26（13 Deployment × 2 行） | 安全 |
| 生成器 `--bff-authz` | 0 | 28（＋ bff の `Services__AuthorizationServiceGrpc`） | 安全（`OpendAuth__*` 2 件は残る） |
| 生成器 `--rest mcp=Mcp__GrpcServices__document-service` | 2（その 1 件だけ） | 26 | 安全 |
| 生成器（aianalysis に既存の `extraEnvAppend` が在る模擬） | 0 | 26（既存の後ろに継ぎ足し） | 安全 |
| 素朴: `services.bff.extraEnvAppend: [宛先]` | `OpendAuth__BaseUrl`・`OpendAuth__DeviceTrustPersisted` | 1 件 | **罠** |
| 素朴: `services.document.extraEnv: [ログ水準]` | `Services__NotificationServiceGrpc`・`Services__AuthorizationServiceGrpc` | 1 件 | **罠**（gRPC 経路が黙って REST／縮退へ戻る） |
| 素朴: `--set services.bff.extraEnvAppend[0].…` | `OpendAuth__BaseUrl`（添字 0 だけ置換） | 1 件 | **罠** |

## 受け入れ基準

- [x] Runbook が経路表（コード由来）・成功の証拠・合否基準・有効化の構成・メッシュの考慮・切り戻し・中止条件を持つ。
- [x] 上書き値の作り方が既存の env を消さないことを `helm template` の差分で示している（上の表）。
- [x] `docs/` の可視本文に計画 ID・IADR・修飾付き issue 参照が無い（trace ブロックへ入れる）。
- [x] 検査: `scripts.test.js`（REQUIRE_REPO_TESTS=1）・`check-trace-blocks`・`check-doc-links`・`check-doc-updated`・
  `check-cross-repo-refs`・`check-plan-id-qualification`・`gen-knowledge-graph --check`・`check-commit-messages`・gitleaks。

## 残余リスク

- 期待値（ログの文言・メトリクスのラベル・401/接続切断）は**コードと既知の既定挙動からの導出**であり、稼働クラスタでは未確認。
  食い違ったら出た値をそのまま記録する（Runbook §結果の記録）。
- 稼働イメージが develop より古いと、面が無く `UNIMPLEMENTED` になる。前提の節でイメージの作り直しを求めている。
- LLM を呼ぶ経路（生成・埋め込み）は外部課金を伴う。Runbook では任意扱いにし、実施の可否は利用者が決める。

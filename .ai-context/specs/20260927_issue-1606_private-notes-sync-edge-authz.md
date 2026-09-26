---
title: edge.privateNotesSync を有効にしたとき、ゲートウェイの主体を document-service の /private-notes/sync/* に絞る AuthorizationPolicy を置く
type: spec
status: in-progress
related_ids: [NFR-09, FR-20, UC-11, ADR-0005, ADR-0021, ADR-0084, IADR-0017, IADR-0026, IADR-0270, IADR-0273, IADR-0348, IADR-0377, IADR-0469]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0084_nfr09-judgment-unit-and-interim-clause-release.md
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md
related_specs:
  - 20260903_issue-1154_private-notes-sync-edge-route
  - 20260926_issue-1287_helm-synthetic-monitor-optin
issue: "#1606"
---

# 作業仕様書 — 同期のエッジ公開に経路の門を置く（#1606）

## 起点

- issue: #1606（#1603 の監査が見つけた多層防御の隙間）。
  - `edge.privateNotesSync.enabled=true` のとき、NetworkPolicy `allow-edge-ingress-to-document-service` は
    istio-system の任意の Pod から document-service の 8080 番**全体**を開ける。
  - ゲートウェイの主体を `/private-notes/sync/*` に絞る `AuthorizationPolicy` が無い（チャートの AuthorizationPolicy は
    BFF のバックチャネル用の 1 つだけで、それも既定では描かれない）。
  - 経路の制限は VirtualService の振り分けだけに頼っている。共有のゲートウェイへ別の VirtualService が付けば、
    `GET /documents` へ振り分けられ得る。
- 計画: `NFR-09`（認証・認可。**判定は端点単位**＝`ADR-0084` 決定 1）／`ADR-0021`（エッジ＝Istio Ingress Gateway）／
  `ADR-0005`（メッシュ・mTLS）。隣接クローン `../project-planning` を読んだ（読み取り専用）。
- 実装 ADR: [[IADR-0348]]（`edge.privateNotesSync` を入れた IADR。決定 2 で「エッジに AuthorizationPolicy を置かない」と
  書いた）。**本 PR の判断は同 IADR への日付つき追記（決定 6）として残す**（新しい IADR は立てない —— 同じ knob の同じ条件の
  補正であり、決定 2・4 の射程に収まるため）。

## 1. 既存の姿（着手時に読んだもの）

| 何 | 所在 | 要点 |
| --- | --- | --- |
| route | `templates/edge.yaml` の VirtualService | `edge.privateNotesSync.enabled` のとき `uri.prefix: /private-notes/sync/`（**末尾スラッシュ込み・直書き**）→ `edge.privateNotesSync.service:port`。**メソッドは絞らない**。rewrite 無し |
| L3 の穴 | `templates/networkpolicy.yaml` | `networkPolicy.enabled` かつ `edge.enabled` かつ同 knob のとき、`edge.gateway.namespace` → `app: <service>` の当該ポート |
| メッシュ | `templates/istio-mtls.yaml` | 名前空間全体の PeerAuthentication（`mesh.mtlsMode`、既定 STRICT）＋ DestinationRule `*.<ns>.svc.cluster.local` ISTIO_MUTUAL。AuthorizationPolicy は `mesh.backchannelLogout.fromOutsideMesh`（既定 false）のときの BFF 用 DENY 1 枚だけ |
| 同期の端点 | `DocumentService/Features/ObsidianSync/ObsidianSyncEndpoints.cs` | `MapGroup("/private-notes/sync")` 配下に manifest / push / pull / delete / move。**`/private-notes/sync-settings/` と `/private-notes/sync-history` は別群（JWT 経路）**で、前置の境界が 1 文字しか違わない |
| ローカル overlay | `deploy/local/edge-istio/virtualservice-app.yaml` | route を無条件に持つ（[[IADR-0348]] 決定 5）。**本 PR では触らない**（§5） |
| 描画の試験の流儀 | `scripts/helm-synthetic-monitor.test.js`（#1287） | 実際に `helm template` を叩く・helm が無ければ fail-closed・`ci/*.yaml` で「有効」を 1 か所に定義・一時複製のチャートで変異。CI は `static-checks-units` |

## 2. 母集合 —— DocumentService の呼び出し元（着手前に自分で引いた）

**ALLOW の方針を 1 枚でも置くと、当たらない呼び出し元は全部 403 になる。** したがって最初に呼び出し元を全部挙げ、
どの案がどれを切るかを数える。

引いた軸（規則 1〜5・9。拡張子で絞らずパスの除外だけで取った）:

```console
# 軸 1: チャート・overlay の宛先
$ grep -rn -i "document-service\|documentservice__\|DocumentService__\|Services__Document\|DocumentService:" deploy/helm deploy/local deploy/argocd deploy/istio
# 軸 2: コードの構成鍵と既定値
$ grep -rn -E "Services:DocumentService|Services__DocumentService|\"DocumentService\"|document-service:80|DocumentServiceGrpc|GrpcServices" src
$ grep -rln "document-service" src --include=*.cs --include=*.json   # （除外: tests / bin / obj / node_modules）
# 軸 3: 別名前空間（AST）
$ grep -rn -i "document-service\|DocumentService" ../ai-stock-trading/deploy
# 軸 4: プローブ・scrape
$ grep -n -i "probe\|prometheus\|/metrics\|scrape" deploy/helm/microservices-platform/templates/deployment.yaml
$ grep -rn -i "prometheus\|servicemonitor\|podmonitor" deploy/helm/microservices-platform/templates deploy/helm/microservices-platform/values.yaml
```

| # | 呼び出し元 | 名前空間 / 主体 | 宛先（ポート・パス） | 出典 |
| --- | --- | --- | --- | --- |
| 1 | BFF（文書・個人資料・端末・競合・同期設定の中継） | microservices-platform / mTLS | REST 8080 `/documents/**`・`/private-notes/**`（`sync-settings`・`sync-history` を含む） | `Platform.Bff/Program.cs` `Services:DocumentService`、`values.yaml` bff `Services__DocumentService` |
| 2 | BFF（文書の読み取り） | 同上 | gRPC 8081 `knowledge.document.v1.DocumentRead/*` | bff `Services__DocumentServiceGrpc` |
| 3 | BFF（構成情報 API の自己申告の収集） | 同上 | REST 8080 `/internal/introspection`・gRPC 8081 `platform.introspection.v1.ServiceIntrospection/Get` | bff `Introspection__Services__document-service` / `Introspection__GrpcServices__document-service` |
| 4 | GraphService（タグの書き戻し・タグ辞書） | 同上 | REST 8080 `POST /documents/{id}/tags`（既定 `http://document-service:8080`）・gRPC 8081 `DocumentTagWrite/AddTag`・`TagDictionary/ListNames` | `GraphService/Program.cs`、graph `Services__DocumentServiceGrpc` |
| 5 | McpServer（ツール申告・ツール呼び出し） | 同上 | REST 8080 `/internal/mcp-tools`・`/internal/mcp/*`、gRPC 8081 `platform.mcp.v1.McpToolDeclarations/Declare` | `McpServer/appsettings.json` `Mcp:Services:document-service`、mcp `Mcp__GrpcServices__document-service` |
| 6 | 🔴 **AST の information-collection / report（KB 保存）** | **ai-stock-trading（別名前空間）**。**サイドカー注入は既定オフ＝平文で principal を持たない** | REST 8080 `POST /documents` | AST `values-local.yaml` `KnowledgeBase__Documents__BaseUrl=http://document-service.microservices-platform:8080`、AST `values.yaml` `mesh.sidecarInjection.enabled: false` |
| 7 | kubelet（liveness / readiness） | ノード（平文。Istio のプローブ書き換えが有効なら pilot-agent 経由で Envoy の受信を通らない） | 8080 `/health/live`・`/health/ready` | `templates/deployment.yaml` |
| 8 | エッジ（ingress gateway） | `edge.gateway.namespace`（既定 istio-system）/ mTLS | 8080 `/private-notes/sync/*`（同 knob が有効のときだけ） | `templates/edge.yaml` |

**呼び出し元ではないと確かめたもの（除外と理由。規則 6）:**

| 候補 | 除外の理由 |
| --- | --- |
| Prometheus の scrape | DocumentService は `/metrics` を持たない。指標は OTLP で otel-collector へ**送り出す**（`values.yaml` の `endpoint: http://otel-collector:4317`）。Prometheus が scrape するのは collector の自己テレメトリだけ（`ci.yml` の collector 検査の注記） |
| IngestionService | 本文はオブジェクトストレージから読み、契機は RabbitMQ の `DocumentUpdated`（`files/pipeline.json`）。DocumentService への HTTP / gRPC は無い（軸 2 で 0 件） |
| ConversionService / RetrievalService / その他 knowledge のサービス | 軸 2 で 0 件。ConversionService → DocumentService は RabbitMQ の `DocumentNormalized`（catalog 段）で、受信 HTTP ではない |
| Wolverine / RabbitMQ | DocumentService から**出ていく**接続（ブローカへ）。受信ではない |
| 合成監視（synthetic-monitor）・ドリフト検出の PostSync Job | BFF だけを叩く |
| DocumentService → NotificationService / AuthorizationService（gRPC） | DocumentService が**呼ぶ側**。受信ではない |

## 3. 設計 —— ALLOW ではなく DENY、主体ではなく Namespace

### 3.1 案の比較

| 案 | 形 | 切れる呼び出し元（§2 の番号） | 判定 |
| --- | --- | --- | --- |
| A. ALLOW 2 規則 | ①`namespaces: [<release ns>]` は全部 ②`principals: [<gateway>]` × `paths: [/private-notes/sync/*]` | **#6 AST（別名前空間・平文）**、#7 kubelet（プローブ書き換えが無い配備・平文）。PERMISSIVE の間は名前空間の中でもサイドカーの無い平文が切れる。**呼び出し元が増えるたびに列挙を直す義務** | 不採用 |
| **B. DENY 1 規則（採用）** | `from: namespaces: [<gateway ns>]` × `to: notPaths: [/private-notes/sync/*]` | **無し**（当たるのは gateway の Namespace の mTLS 主体 × sync 以外だけ） | **採用** |
| C. B の from を principal で名指し | `principals: [cluster.local/ns/istio-system/sa/istio-ingressgateway-service-account]` | 無し | 不採用: SA 名（istioctl と helm の gateway chart で違う）・trust domain が違うと**方針が黙って何も落とさない**（fail-open）。NetworkPolicy の穴は Namespace 単位なので、穴を覆うのにも足りない |
| D. B に平文の DENY を足す | `notPrincipals: ["*"]` × `notPaths` | **PERMISSIVE の間の #6 AST・#7 kubelet** | 不採用: 平文の呼び出し元を巻き込む |

**B を採る理由:**

1. **DENY は当たった要求だけを落とす。** from × to の外の呼び出しは 1 本も変わらない —— §2 の #1〜#7 は from（gateway の
   Namespace）に当たらないので、mTLS / 平文・名前空間の内外を問わず従前のままである。ALLOW はワークロードを選んだ瞬間に
   default-deny へ変わり、列挙の漏れが**黙った 403** になる。
2. **from は NetworkPolicy の穴と同じ単一情報源（`edge.gateway.namespace`）。** L3 で開けた範囲をそのまま L7 で覆える。
   gateway の Namespace に DocumentService の正当な呼び出し元は「エッジ × sync」以外に居ない（§2）。
3. **to は route の前置 + `*` を直書き**（[[IADR-0348]] 決定 1 と同じく knob にしない）。`/private-notes/sync-settings/`・
   スラッシュ無しの `/private-notes/sync`・gRPC 8081 の面も当たる＝落ちる。**メソッドは絞らない** —— route も絞っておらず、
   許すメソッドの正は端点側の契約にある（絞ると、端点を足すたびにチャートを直す二重管理になる）。
4. 選ぶ先（`app: <edge.privateNotesSync.service>`）は route の destination・NetworkPolicy の podSelector と同じ単一情報源。

### 3.2 mTLS のモードと主体の照合

- **STRICT（既定）**: 平文は PeerAuthentication が先に落とす。本方針が見るのは mTLS の要求だけで、ゲートウェイの Envoy →
  サイドカーは auto mTLS（と DestinationRule の ISTIO_MUTUAL。exportTo 既定 `*` でゲートウェイにも効く）で必ず mTLS になり、
  `source.namespace` が採れる → 本 DENY が当たる。
- **PERMISSIVE**: 平文の要求は principal も `source.namespace` も持たないので、本 DENY は当たらない。
  - 良い面: 平文の呼び出し元（#6 AST・#7 kubelet）を巻き込まない。
  - 🔴 **受け入れた限界**: gateway の Namespace に居る**サイドカー無しの Pod** からの平文は、NetworkPolicy を通り本門を素通りする。
    ゲートウェイ自身は常に mTLS で来るので当たる。閉じるのは STRICT であり（既定値）、D 案で閉じると AST を巻き込む。
- 経路の正規化: Istio の AuthorizationPolicy の `paths` はクエリを含まない正規化後のパスで照合し、VirtualService の振り分けも
  同じ正規化後のパスで行われる（[[IADR-0348]] §実測 5 が `/private-notes/sync/../devices` の正規化を確かめている）。

### 3.3 描画の条件

`edge.enabled` かつ `edge.privateNotesSync.enabled`（route と同じ）。`templates/edge.yaml` の `edge.enabled` ブロックの中に置く。
`networkPolicy.enabled` / `mesh.enabled` には依らない —— route が在る限り、振り分けに頼らない門を置く
（edge が有効＝Istio の CRD が在る前提は Gateway / VirtualService と同じ）。

## 4. 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | 既定の values と `values-local.yaml` の `helm template` は origin/develop と**バイト一致** | 両方を origin/develop 版と `cmp`（§6） |
| AC2 | 無効時は DocumentService を選ぶ AuthorizationPolicy が描かれない | 試験「既定（無効）…」「values-local…」「edge.enabled=false…」 |
| AC3 | 有効時は `document-service-edge-sync-only` がちょうど 1 枚、`action: DENY`、`selector app: document-service`、`from namespaces: [istio-system]`、`to notPaths: [/private-notes/sync/*]` | 試験「有効: … ちょうど 1 枚」 |
| AC4 | 方針の値が route の前置・NetworkPolicy の podSelector / namespaceSelector と一致し、knob を変えると追随する | 試験「有効: 方針の経路は route の前置 + "*"…」「knob の追随…」 |
| AC5 | §2 の呼び出し元の一覧へ Istio の評価規則で当てると、エッジ × sync だけが通り、エッジ × sync 以外は落ち、#1〜#7 は 1 本も切れない（PERMISSIVE でも同じ） | 試験「🔴 有効: 呼び出し元の一覧へ評価すると…」「mesh.mtlsMode=PERMISSIVE…」 |
| AC6 | 経路の制限を外す・広げる・from を内側へ向ける・ALLOW に変える変異がすべて赤になる | 試験の 4 変異 ＋ 手元での変異 1 回（§6） |
| AC7 | 有効時の描画が helm lint / kubeconform のスキーマ突合を通る | `ci/private-notes-sync-values.yaml` を `check-deploy-manifests.js` が拾う（CI） |
| AC8 | 判断が [[IADR-0348]] の日付つき追記に残り、運用手順（Obsidian プラグインの手順ガイド）とセキュリティ仕様書が追随する | diff |

## 5. 変更の範囲と、触らないもの

| 変更 | ファイル |
| --- | --- |
| 方針の描画 | `deploy/helm/microservices-platform/templates/edge.yaml` |
| values の注記 | `deploy/helm/microservices-platform/values.yaml`（注記だけ。値は不変） |
| CI 用 values | `deploy/helm/microservices-platform/ci/private-notes-sync-values.yaml`（新規。`check-deploy-manifests.js` が lint / template / kubeconform を回す） |
| 描画の試験 | `scripts/helm-private-notes-sync-authz.test.js`（新規）・`.github/workflows/ci.yml`（`static-checks-units` に 1 ステップ）・`scripts/README.md` |
| 判断の記録 | `.ai-context/adr/IADR-0348_private-notes-sync-edge-route.md`（決定 2 への追記 ＋ 決定 6）。**索引（`.ai-context/adr/README.md`）の行は変えない** —— 索引タイトルは本体 `title:` の要約であり、追記の混入と長さ超過を `scripts.repo.test.js` が止める（本体の `title:` は変わっていない） |
| 文書 | `docs/how-to/obsidian-plugin-install.md`（有効化したときに描かれる 3 資源・確かめ方・STRICT の前提）・`docs/security/security.md`（多層防御の追記） |

**触らないもの:**

- `deploy/local/edge-istio/`（ローカル overlay）: [[IADR-0348]] 決定 5 のとおり opt-in の実測環境であり、**稼働中の PoC を変えない**
  （呼び出し時の制約）。必要になれば overlay 側の別 issue で足す。
- 稼働クラスタ: `helm template` だけを使い、`kubectl` / `helm upgrade` は叩かない。
- `scripts/check-stack-ready.js` の門 G12: 宣言（helm の描画）に在るメッシュ資材を稼働と突き合わせる門で、種別
  `AuthorizationPolicy` は既に対象（`MESH_KINDS`）。有効化した配備では本方針も宣言に入るだけで、門の変更は要らない。
- ArgoCD の許可種別（`deploy/argocd/appproject.yaml`）: `AuthorizationPolicy` は #1159 で既に許可済み。

**追随する文書の母集合（規則 1・9・10。誤りの側の文言で引いた）:**

```console
$ git grep -n -E "AuthorizationPolicy.{0,40}(置かない|無い|1 つ|ひとつ|だけ|のみ)|(置かない|無い).{0,20}AuthorizationPolicy|RequestAuthentication.{0,5}/.{0,5}AuthorizationPolicy" -- . ':!src/ai-stock-trading'
.ai-context/adr/IADR-0348_private-notes-sync-edge-route.md:74:  （決定 2「置かない」→ 本 PR の追記で改めた）
.ai-context/adr/IADR-0407_mesh-gate-requires-declared-mesh.md:41:  （稼働にメッシュ資材が 0 件だった時点の実測。別の話題 → 対象外）
.ai-context/specs/20260903_issue-1154_private-notes-sync-edge-route.md:101:  （確定済みの仕様書 → 書き換えない。判断の変更は IADR 側の追記が持つ）
$ git grep -n -E "エッジ|NetworkPolicy|opt-in" -- docs/functional/FR-20_obsidian-sync.md docs/api/FR-20_obsidian-sync.md deploy/local/edge-istio/README.md
  （「外へ出るのは 1 前置だけ」「opt-in」— 本 PR の後も正しい → 変更しない）
```

## 6. 検証（証跡）

（実装後に追記する）

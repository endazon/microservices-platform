---
title: 運用 Runbook — east-west gRPC（h2c）の往復を稼働 k3s で実測し、REST 退役の前提を記録する
type: runbook
status: draft
author: claude
created: 2026-10-04
updated: 2026-10-04
---
<!-- trace:
ids: [NFR-09, NFR-16, FR-15, FR-16]
adrs: [ADR-0005, ADR-0029, ADR-0075, ADR-0089, ADR-0117]
iadrs: [IADR-0379, IADR-0462, IADR-0426, IADR-0307, IADR-0377, IADR-0487, IADR-0488]
specs: [20261004_issue-1255_h2c-roundtrip-measurement-runbook]
issues: [#1255, #1517, #1201, #1389, #1514, #1515, #1516, #1159]
-->

# 運用 Runbook: east-west gRPC（h2c）の往復を稼働 k3s で実測する

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。** 経路ごとの契約（proto・認証・縮退）は
> [east-west gRPC 通信仕様書](../api/east-west-grpc.md) が持つ。本書は **稼働クラスタで h2c の往復が成立していることを測り、記録する**ことだけを扱う。
>
> 🔴 **本書を実行するのは利用者（クラスタの持ち主）だけである。** 本書を書いた AI は稼働クラスタに 1 度も触れていない。
> **コマンドと期待値はリポジトリのコード・チャートから導いたものであって、実測ではない**（helm の描画差分だけは手元で実測した。§2.3）。
> 期待値と違う結果が出たら、**期待値に合わせて読み替えず、出た値をそのまま記録する**（§5）。

## この手順を実行する条件（いつ走らせるか）

- **#1255 の残射程 2**（移行済み経路の REST 退役と、「並走中の正は REST」とした実装判断の反転）と **#1517**（扇形 2 経路の REST 退役）は、
  オーナー判断（2026-09-27）により **稼働 k3s で h2c の往復を実測するまで着手しない**。本書はその実測の手順である。**1 回でよい。**
- #1255 の「やること 7」（PERMISSIVE と STRICT の両方で `authorization-service:8081` へ往復し、記録を残す）も本書で満たす（§3・§4）。
- クラスタを作り直した後に、REST 退役の PR を出す直前の確認として §3 の観測だけを取り直してもよい。

**実行しなくてよい場合**: 単体・結合の試験（TestServer の in-memory HTTP/2）で足りる確認。それらは h2c リスナ・サイドカー・実ネットワークを通らないので、
本書の代わりにはならない。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | `microservices-platform` 名前空間で `helm upgrade`・`exec`（`istio-proxy` コンテナを含む）・`logs`・`port-forward`・`run`（使い捨て Pod）ができること |
| 必要なツール | `kubectl`・`helm`（**クラスタを立てたものと同じ版**。描画の比較に使う）・`node` 22・`curl`・bash（Git Bash で可） |
| チェックアウトの版 | `origin/develop` の `b0eaeff7` 以降。確かめ方: `git merge-base --is-ancestor b0eaeff7 HEAD && echo OK` |
| クラスタ | **同じチェックアウトから** `bash scripts/k8s-local-images.sh --live` → `ISTIO=1 bash scripts/k8s-local-up.sh --live`（`LOCALEDGE=1` の有無・`ISTIO_MTLS_MODE` は普段どおり）で立ててあること。🔴 イメージが古いと面が無く、全経路が `UNIMPLEMENTED` で落ちる |
| 入口（STRICT の回） | **入口が Istio Ingress Gateway（`ISTIO=1 LOCALEDGE=1` で立てたもの）であるか、画面を `kubectl port-forward` で開くこと。** 既定の入口（`kube-system` の Traefik）はサイドカーを持たないので、STRICT では Traefik → BFF・フロントの平文が受け手のサイドカーに落とされ、画面が 5xx になる（起動スクリプトが STRICT を入口の移行後にしか宣言しないのはこのため）。**そうでない構成で STRICT で測れるのは I と M だけ**（画面操作を要らない）で、§1.2 の経路は PERMISSIVE だけで測り、STRICT は「未測定（入口がメッシュ外）」と書く |
| 外部課金 | LLM を呼ぶ経路（§1 の表の「課金」）は外部 API の費用が出る。**実施の可否は利用者が決める**。埋め込みだけなら `LOCALEMBED=1`（決定的ローカル埋め込み。使い捨てスタック専用）で費用なしに測れる |
| 所要時間の目安 | 約 2 時間（グラフ → ダッシュボードの報告を待つなら ＋1 時間。§3.3） |

**gRPC 経路を有効にするための構成は、BFF → 認可の 1 経路を除き、足す必要が無い。** チャートの既定（`values.yaml`）が既に
呼び出し元へ gRPC の宛先（`Services__*Grpc` / `Introspection__GrpcServices__*` / `Mcp__GrpcServices__*`）と、呼び出し先 13 サービスへ
`grpcPort: 8081` を与えており、`values-local.yaml` はそれを消していない（§2.3 の描画で確認）。本書が足すのは**観測のための構成**だけである。

---

## 0. 安全の前置き

### 0.1 触るもの・触らないもの

| 対象 | 何をするか | 節 | 戻し方 |
| --- | --- | --- | --- |
| helm リリース `msp` の利用者値 | 呼び出し先 13 サービスの `extraEnvAppend` へ要求ログの水準 1 行を**継ぎ足す**（任意で BFF へ認可の gRPC 宛先 1 行） | §2 | §6.1（保存した利用者値で `helm upgrade`） |
| `PeerAuthentication microservices-platform-mtls` のモード | `set_mesh_mtls_mode` で PERMISSIVE ⇔ STRICT を切り替える（helm 経由） | §4 | §6.1 と同じ 1 本で元のモードへ戻る |
| `deploy/mcp-service` | 再起動する（起動時のツール申告の収集を発火させる） | §3.2 | 不要 |
| 使い捨て Pod（`curlimages/curl:8.11.1`・サイドカー無し） | `authorization-service:8081` へ平文 h2c を 1 回送る | §3.5 | `--rm` で自動削除 |

**触らないもの**: `platform-infra` / `ai-stock-trading` / `istio-system` の名前空間、realm、Secret、チャートとコード。
🔴 **`kubectl patch` / `kubectl apply` でメッシュ資材や Deployment を書かない**（helm が唯一の書き手。外から書くと以後の `helm upgrade` が
conflict で恒久的に止まる —— [運用仕様書](operations.md) の「メッシュ設定のドリフトと、helm リリースが固まったときの復旧」）。
**本書の実行中は `scripts/k8s-local-up.sh` を走らせない**（`--reuse-values` を使わないので、計測用の上書きを黙って外す）。

### 0.2 中止条件（1 つでも当たったら、その場で §6.1 の切り戻しへ進む）

| # | 条件 | 理由 |
| --- | --- | --- |
| S1 | §0.3 の事前チェックで、13 サービスのどれかに `Now listening on: http://[::]:8081` が無い | h2c リスナが居ない＝イメージか env が前提を満たしていない。測っても全経路が落ちる |
| S2 | §0.3 の「描画と稼働の一致」で差分が出る | チェックアウトと稼働のチャート・値が違う。§2 の上書きが計測以外の変更まで連れて入る |
| S3 | §2.3 の差分に **消える行（`<`）が 1 行でもある** | 既存の env を消している。適用すると経路が黙って REST／縮退へ戻る |
| S4 | `helm upgrade` が `conflict … field manager` で失敗する | 所有権が奪われている。**値を変えて再試行しない**。運用仕様書の復旧手順へ |
| S5 | 上書きの適用後 10 分経っても Ready にならない Deployment がある・CrashLoopBackOff が出る | 計測以外の故障。測定を続けない |
| S6 | STRICT の区間で、利用者の操作が 5xx を返し続ける・BFF の readiness が落ちる | STRICT はサイドカーを持たない呼び出し元（別名前空間の AST・`kube-system` の Traefik の入口など）からの平文を落とす。**元が PERMISSIVE なら STRICT の区間は 30 分以内に区切る** |
| S7 | 他の誰かが同じクラスタで `helm upgrade`・起動スクリプト・ArgoCD の同期を走らせている | 測っている状態が別物になる |
| S8 | LLM の費用のアラートが鳴った・承認した回数を超えた | 課金を伴う経路だけを止める（他の経路は続けてよい） |

**中止条件ではないもの**: 呼び出し元の `rejected over gRPC (Unauthenticated|PermissionDenied)`・`could not obtain the caller's service token`
などのエラー。これは**配線不備という測定結果**であり、§5 に「不合格」として記録する（測定は続けてよい）。

### 0.3 事前チェックとスナップショット

```bash
NS=microservices-platform
W="${W:-$HOME/h2c-measure-$(date +%Y%m%d)}"; mkdir -p "$W"
git rev-parse HEAD > "$W/checkout.txt"

# (1) リリースの現状を保存する（切り戻しの正はここで保存した利用者値である）
helm history msp -n "$NS" --max 3 | tee "$W/helm-history-before.txt"
helm get values msp -n "$NS" -o yaml > "$W/current-values.yaml"         # 利用者値（values-local と --set の和）
helm get values msp -n "$NS" --all -o json > "$W/all-values.json"       # チャート既定との和（生成器が読む）
helm get manifest msp -n "$NS" > "$W/manifest-before.yaml"

# (2) 描画と稼働の一致（S2）。0 行であること
helm template msp deploy/helm/microservices-platform -n "$NS" -f "$W/current-values.yaml" \
  | diff - "$W/manifest-before.yaml" | tee "$W/render-vs-live.diff" | wc -l

# (3) 現行の mTLS モードとサイドカーの有無
. scripts/lib/mesh-mtls-mode.sh
current_mesh_mtls_mode | tee "$W/mesh-before.txt"; echo "rc=${PIPESTATUS[0]}"   # rc=0 でモード（STRICT/PERMISSIVE）が出る。1=メッシュ宣言なし・2=読めない
kubectl -n "$NS" get peerauthentication microservices-platform-mtls -o jsonpath='{.spec.mtls.mode}{"\n"}' \
  | tee "$W/peerauthentication-before.txt"                                # 稼働のモード（上と一致すること）
kubectl get ns -L istio-injection                                         # 注入ラベルは microservices-platform だけのはず
kubectl -n "$NS" get pods -o custom-columns=NAME:.metadata.name,CONTAINERS:.spec.containers[*].name \
  | tee "$W/pods-containers.txt"                                          # アプリ Pod すべてに istio-proxy が在ること

# (4) h2c リスナ（S1）。13 行 × 2（8080 と 8081）が出ること
for d in authorization llmgateway notification document retrieval graph dashboard wiki conversion ingestion aianalysis datasource feedback; do
  printf '%-14s ' "$d"; kubectl -n "$NS" logs "deploy/$d-service" -c "$d-service" | grep -oE 'Now listening on: http://[^ ]+' | tr '\n' ' '; echo
done | tee "$W/listeners.txt"

# (5) Service のポート名と appProtocol（Istio がプロトコルを推定に頼らない前提）
kubectl -n "$NS" get svc authorization-service \
  -o jsonpath='{range .spec.ports[*]}{.name}{" "}{.port}{" "}{.appProtocol}{"\n"}{end}'
# 期待: "http 8080 http" と "grpc 8081 grpc"
```

`current_mesh_mtls_mode` が `rc=1`（メッシュ宣言なし）なら、メッシュが入っていない（`grep -A3 '^mesh:'` は使わない —— キーが辞書順に並ぶので `mtlsMode` まで届かない）。§3 の観測①だけは取れるが、PERMISSIVE / STRICT の測定はできないので、
`ISTIO=1` で立て直してから始める。

---

## 1. 測る経路（コードから引いた一覧）

**引き方**（記憶・issue 本文の数えを写さない）: 呼び出し先は `builder.AddPlatformGrpcListener()` を呼ぶ `Program.cs`（13 サービス）と
`MapGrpcService<…>` の面、呼び出し元は各クライアントの `AddressKey` と `Program.cs` の「構成があれば gRPC 実装、無ければ REST 実装」の分岐、
宛先の値はチャートの `values.yaml` から引いた（基点 `b0eaeff7`）。行頭の記号は §5 の記録表で使う。

### 1.1 扇形 2 経路（#1517 の保留を解く観測点）

| # | 呼び出し元 | 宛先（:8081） | rpc のパス | 切替の env | REST 並走 | 発火 |
| --- | --- | --- | --- | --- | --- | --- |
| I-01〜I-13 | `bff-service` | 13 サービス（document / wiki / conversion / ingestion / retrieval / aianalysis / authorization / dashboard / datasource / feedback / graph / llmgateway / notification） | `/platform.introspection.v1.ServiceIntrospection/Get` | `Introspection__GrpcServices__<名>` | 有（`GET /internal/introspection`） | `POST /internal/config/drift-run`（§3.2）。定期（既定 300 秒）でも走る |
| M-1〜M-3 | `mcp-service` | document / retrieval / graph | `/platform.mcp.v1.McpToolDeclarations/Declare` | `Mcp__GrpcServices__<名>` | 有（`GET /internal/mcp-tools`） | `mcp-service` の再起動（起動時に収集。以後 300 秒周期） |

### 1.2 移行済みで REST と並走している経路（#1255 残射程 2 の母集合）

| # | 呼び出し元 | 宛先（:8081） | rpc のパス | 切替の env | 発火（操作の例） |
| --- | --- | --- | --- | --- | --- |
| A-1 | `aianalysis-service` | authorization | `/platform.authz.v1.AuthzScope/Resolve` | `Services__AuthorizationServiceGrpc` | AI 分析で質問する（課金） |
| A-2 | `graph-service` | authorization | 同上 | 同上 | グラフの閲覧（利用者の権限で動く要求） |
| A-3 | `wiki-service` | authorization | 同上 | 同上 | Wiki の閲覧 |
| A-4 | `retrieval-service` | authorization | 同上 | 同上 | 検索 |
| A-5 | `mcp-service` | authorization | `/platform.authz.v1.UserDirectory/GetUserAttributes` と `AuthzScope/Resolve` | 同上 | MCP クライアントの登録（無人アカウントの属性の検証） |
| A-6 | `datasource-service` | authorization | `/platform.authz.v1.UserDirectory/CheckUsernames` | 同上 | データソースの登録・更新（利用者の写像を含む） |
| B-1 | `bff-service` | authorization | `/platform.authz.v1.AuthzScope/Resolve` | `Services__AuthorizationServiceGrpc`（🔴 **チャート既定に無い**。§2.2 の `--bff-authz` で足す） | 権限スコープを引く画面操作（検索・文書一覧など） |
| L-1 | `ingestion-service` | llmgateway | `/platform.llmgateway.v1.LlmEmbedding/Embed` | `Services__LlmGatewayGrpc` | 文書の取り込み（`LOCALEMBED=1` なら課金なし） |
| L-2 | `retrieval-service` | llmgateway | 同上 | 同上 | 検索（クエリの埋め込み） |
| L-3 | `aianalysis-service` | llmgateway | `/platform.llmgateway.v1.LlmCompletion/Complete`・`/CompleteStream` | 同上 | AI 分析（課金） |
| L-4 | `graph-service` | llmgateway | `/platform.llmgateway.v1.LlmCompletion/Complete` | 同上 | AI 提案の生成・要約（課金） |
| L-5 | `conversion-service` | llmgateway | 同上 | 同上 | 図の変換（課金） |
| R-1 | `aianalysis-service` | retrieval | `/knowledge.retrieval.v1.DocumentSearch/Search` | `Services__RetrievalServiceGrpc` | AI 分析（RAG の文脈収集。課金） |
| R-2 | `bff-service` | retrieval | `/knowledge.retrieval.v1.AttributeValues/ListValues` | 同上 | 検索画面の属性値の選択肢 |
| G-1 | `retrieval-service` | graph | `/knowledge.graph.v1.GraphNeighbors/ExpandNeighbors`・`/ListEdgeTypeWeights` | `Services__GraphServiceGrpc` | 検索（近傍展開） |
| D-1 | `bff-service` | document | `/knowledge.document.v1.DocumentRead/ListDocuments`・`GetDocument`・`ListVersions`・`GetVersion` | `Services__DocumentServiceGrpc` | 文書の一覧・詳細・版履歴・特定版 |
| D-2 | `graph-service` | document | `/knowledge.document.v1.DocumentTagWrite/AddTag` | 同上 | タグの提案の承認 |
| D-3 | `graph-service` | document | `/knowledge.document.v1.TagDictionary/ListNames` | 同上 | AI 提案の生成（タグ辞書の読み取り） |
| N-1 | `document-service` | notification | `/platform.notification.v1.NotificationIngress/Accept` | `Services__NotificationServiceGrpc` | 個人資料の共有（通知の送出） |
| H-1 | `graph-service` | dashboard | `/knowledge.dashboard.v1.KnowledgeHealthReport/Report` | `Services__DashboardServiceGrpc` | 定期（1 時間。**graph の起動から 1 周期後**が初回） |

### 1.3 gRPC だけの経路（REST の兄弟が無い。退役の対象外・測るのは任意）

| # | 呼び出し元 | 宛先 | rpc のパス | 構成が無いと |
| --- | --- | --- | --- | --- |
| X-1 | `document-service` | authorization | `AuthzScope/Resolve`・`AuthzScope/GetOwnerReadPolicyStatus`・`UserDirectory/GetUserAttributes`・`UserDirectory/CheckRealmRole` | それぞれ縮退（判定できない・数えられない） |
| X-2 | `datasource-service` | authorization | `UserDirectory/CheckDepartmentCodes` | 照会できない扱い（明示部門の書き込みが 502） |
| X-3 | `mcp-service` | document / retrieval / graph | `/platform.mcp.v1.McpToolExecution/Execute` | 実行を拒否（fail-closed）。REST の実行経路は無い —— 旧 `HttpToolInvoker` は `GrpcToolInvoker` に置き換えられて残っておらず、置き換え前も宛先に REST の実行口が無く常に失敗していた |

**数えない経路**: 別名前空間の AST から MSP への呼び出し（AST 側の作業）、BFF が利用者の資格情報を付けて中継する呼び出し（エッジ。east-west に数えない）。

---

## 2. 計測用の構成（helm の上書き値。既存の env を消さない作り方）

### 2.1 なぜ素朴に書けないのか

成功した gRPC 呼び出しは**どちら側にもログを残さない**。収集器・クライアントは失敗だけをログし、チャネル（`CreatePlatformChannel`）は
`LoggerFactory` を渡していないので gRPC ライブラリのログも出ない。呼び出し先の ASP.NET Core は要求ごとに
`Request finished HTTP/2 POST http://<宛先>:8081/<package>.<Service>/<Method> - 200 …` を出すが、13 サービスのうち 11 は `appsettings.json` が
`Microsoft.AspNetCore` を `Warning` に絞っているので出ていない（conversion と ingestion は `Default: Information` だけを持ち、絞っていない。足しても害は無いので同じく足す）。そこで呼び出し先 13 サービスに
`Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics=Information` を一時的に足す。

🔴 **env はリストであり、Helm は上書き側のリストで丸ごと置き換える。** `extraEnv` を上書きファイルに書くと、その
サービスの gRPC 宛先（`Services__*Grpc`）がチャート既定ごと消え、経路が**黙って** REST／縮退へ戻る。継ぎ足し用の
`extraEnvAppend` も同じで、BFF には `values-local.yaml` が既に `OpendAuth__*` を入れている。`--set …extraEnvAppend[0]…` は添字 0 だけを
置き換えて残りを残す（さらに分かりにくい）。§2.3 の表がその実測である。

**作り方**: 稼働リリースの値（`helm get values --all`）から各サービスの既存の `extraEnvAppend` を読み、**その後ろへ継ぎ足した完全なリスト**を
上書きファイルに書く。下の生成器がそれを行う。

### 2.2 上書き値を作る

```bash
cat > "$W/gen-overlay.mjs" <<'EOF'
// h2c 往復の実測用の上書き値を、稼働リリースの値から組む（リストは継ぎ足す。置き換えない）。
//   node gen-overlay.mjs <all-values.json> [--bff-authz] [--rest <サービス>=<env 名>]... > overlay.json
//   <all-values.json> = `helm get values <release> -n <ns> --all -o json`
//   --bff-authz           任意: BFF → 認可（参照実装）の gRPC 宛先を足す
//   --rest svc=ENV        緊急の切り戻し: そのサービスの extraEnv から gRPC 宛先 1 行を抜く（REST へ戻す）
import { readFileSync } from 'node:fs';
const [file, ...args] = process.argv.slice(2);
const all = JSON.parse(readFileSync(file, 'utf8'));
const svcs = all.services ?? {};
const overlay = { services: {} };
const entry = (name) => (overlay.services[name] ??= {});
const append = (name, envName, value) => {
  const svc = svcs[name];
  if ((svc.extraEnv ?? []).some((e) => e.name === envName))
    throw new Error(`${name}: extraEnv に ${envName} が既に在る（同名を足すと Kubernetes が重複を持つ）`);
  const cur = entry(name).extraEnvAppend ?? svc.extraEnvAppend ?? [];
  entry(name).extraEnvAppend = cur.some((e) => e.name === envName) ? cur : [...cur, { name: envName, value }];
};
const LOG = 'Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics';
for (const [name, svc] of Object.entries(svcs))
  if (svc?.enabled && svc.grpcPort) append(name, LOG, 'Information'); // h2c の受け口を持つサービスだけ
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--bff-authz')
    append('bff', 'Services__AuthorizationServiceGrpc', `http://authorization-service:${svcs.authorization.grpcPort}`);
  else if (args[i] === '--rest') {
    const [name, envName] = (args[++i] ?? '').split('=');
    const list = svcs[name]?.extraEnv ?? [];
    if (!list.some((e) => e.name === envName)) throw new Error(`${name}: extraEnv に ${envName} が無い`);
    entry(name).extraEnv = list.filter((e) => e.name !== envName);
  } else throw new Error(`未知の引数: ${args[i]}`);
}
process.stdout.write(JSON.stringify(overlay, null, 2) + '\n');
EOF

node "$W/gen-overlay.mjs" "$W/all-values.json" > "$W/overlay.json"                     # B-1 を測らない場合
node "$W/gen-overlay.mjs" "$W/all-values.json" --bff-authz > "$W/overlay-bff.json"     # B-1 も測る場合
```

上書きファイルは JSON で書く（YAML の部分集合なので `helm -f` がそのまま読む）。B-1 を足すと **BFF の権限スコープ解決が利用者の要求ごとに gRPC を通る**
ようになる（計測の間だけ。§6.1 で外れる）。

### 2.3 適用の前に描画の差分で確かめる（S3）

```bash
C=deploy/helm/microservices-platform
helm template msp "$C" -n "$NS" -f "$W/current-values.yaml"                       > "$W/render-before.yaml"
helm template msp "$C" -n "$NS" -f "$W/current-values.yaml" -f "$W/overlay.json" > "$W/render-after.yaml"
diff "$W/render-before.yaml" "$W/render-after.yaml" > "$W/overlay.diff"
echo "removed=$(grep -c '^<' "$W/overlay.diff") added=$(grep -c '^>' "$W/overlay.diff")"
grep '^>' "$W/overlay.diff" | sort | uniq -c
```

期待（`overlay.json`）: `removed=0 added=26`（13 Deployment × 2 行。`name:` と `value: "Information"`）。`overlay-bff.json` なら `removed=0 added=28`
（BFF の `Services__AuthorizationServiceGrpc` の 2 行が増え、`OpendAuth__*` は残る）。**`removed` が 0 でなければ適用しない（S3）。**

本書を書いた時点の手元の実測（helm v3.16.4。`values-local.yaml` に `k8s-local-up.sh` の `ISTIO=1` の `--set` 4 つを足したものを稼働の利用者値とみなした）:

| 上書き | 消えた行 | 足された行 | 判定 |
| --- | --- | --- | --- |
| 生成器（観測の env だけ） | 0 | 26 | 安全 |
| 生成器 `--bff-authz` | 0 | 28 | 安全（`OpendAuth__BaseUrl`・`OpendAuth__DeviceTrustPersisted` は残る） |
| 生成器（aianalysis に既存の `extraEnvAppend` 1 件が在る模擬） | 0 | 26 | 安全（既存の後ろへ継ぎ足し） |
| 生成器 `--rest mcp=Mcp__GrpcServices__document-service` | 2（その 1 件だけ） | 26 | 安全（§6.2 の緊急切り戻し） |
| 素朴: `services.bff.extraEnvAppend` に宛先 1 件 | 4（`OpendAuth__*` 2 件） | 2 | **罠** |
| 素朴: `services.document.extraEnv` にログ水準 1 件 | 4（`Services__NotificationServiceGrpc`・`Services__AuthorizationServiceGrpc`） | 2 | **罠**（文書 → 通知・認可が黙って REST／縮退へ戻る） |
| 素朴: `--set services.bff.extraEnvAppend[0].name=…`（`.value` も） | 2（`OpendAuth__BaseUrl` だけ） | 2 | **罠**（添字 0 だけ置き換わる） |

同じ描画で、チャート既定のまま 15 の Deployment が gRPC 宛先か `Grpc__Port` を持つこと（受け口の 13 は `Grpc__Port`、mcp と bff は呼び出し元のみで `Grpc__Port` 無し）、
BFF の gRPC 宛先が introspection 13・文書・検索で、認可が無いことも確かめた。

### 2.4 適用する

```bash
helm upgrade msp deploy/helm/microservices-platform -n "$NS" \
  -f "$W/current-values.yaml" -f "$W/overlay.json"          # B-1 も測るなら overlay-bff.json
kubectl -n "$NS" rollout status deployment --timeout=10m    # 13 サービス（＋ B-1 なら bff）が作り直される（S5）
helm get manifest msp -n "$NS" | diff "$W/render-after.yaml" - | wc -l   # 0 行（overlay-bff.json なら render を作り直して比べる）
```

`--reuse-values` ではなく保存した利用者値を明示で渡す（起動スクリプトと同じ「値を全部渡す」形にし、何が入ったかを `$W` に残す）。
適用後に §0.3 (4) をもう一度流し、13 サービスとも `:8081` で待ち受けていることを確かめる。

---

## 3. 1 つのモードでの観測

§3 を **現行のモード → もう一方のモード** の順に 2 回行う（§4）。どちらの回も `MODE` に今のモードを入れてから始める。

### 3.1 観測の道具と、開始時刻・メトリクスの起点

```bash
MODE=STRICT   # または PERMISSIVE（今のモード）
T0=$(date -u +%Y-%m-%dT%H:%M:%SZ); echo "$T0" > "$W/t0-$MODE.txt"

# サイドカーの istio_requests_total を、受け手側（reporter=destination）・gRPC だけ表にする
cat > "$W/istio-grpc.mjs" <<'EOF'
let s = '';
process.stdin.on('data', (d) => (s += d)).on('end', () => {
  for (const line of s.split('\n')) {
    const m = line.match(/^istio_requests_total\{(.*)\} (\S+)/);
    if (!m) continue;
    const L = Object.fromEntries([...m[1].matchAll(/(\w+)="([^"]*)"/g)].map((x) => [x[1], x[2]]));
    if (L.reporter !== 'destination' || L.request_protocol !== 'grpc') continue;
    console.log([L.source_workload, L.destination_workload, L.response_code, L.grpc_response_status,
      L.connection_security_policy, m[2]].join('\t'));
  }
});
EOF
DESTS="authorization llmgateway notification document retrieval graph dashboard wiki conversion ingestion aianalysis datasource feedback"
snap() {  # snap <ラベル>
  for d in $DESTS; do
    kubectl -n "$NS" exec "deploy/$d-service" -c istio-proxy -- pilot-agent request GET stats/prometheus 2>/dev/null \
      | node "$W/istio-grpc.mjs"
  done | sort > "$W/istio-$MODE-$1.tsv"
}
snap before
```

`pilot-agent request` が使えない版では、`kubectl -n "$NS" port-forward deploy/<宛先>-service 15000:15000` を張り、
`curl -s http://127.0.0.1:15000/stats/prometheus | node "$W/istio-grpc.mjs"` で同じ表を取る。

### 3.2 発火させる

```bash
# I-01〜I-13: 構成情報 API の即時検出（認証なし・メッシュ内部限定の口。port-forward はサイドカーを通らずに届く）
kubectl -n "$NS" port-forward deploy/bff-service 18080:8080 >/dev/null 2>&1 & PF=$!; sleep 3
curl -sS -X POST -o /dev/null -w 'drift-run %{http_code}\n' http://127.0.0.1:18080/internal/config/drift-run   # 202
kill "$PF"

# M-1〜M-3: MCP サーバーの起動時の申告の収集（宛先 3 サービスが Ready になってから）
kubectl -n "$NS" rollout restart deploy/mcp-service && kubectl -n "$NS" rollout status deploy/mcp-service --timeout=5m
```

§1.2 の経路は、表の「発火」の操作を画面（エッジ経由のログイン）で 1 回ずつ行う。課金の経路は承認した回数だけにする（S8）。
発火できなかった経路は §5 に「未測定」と理由を書く（推測で合格にしない）。

### 3.3 待つ

- I・M は 1 分で足りる。H-1（グラフ → ダッシュボード）は **graph の Pod の起動から 1 時間後**に初回が来る（§2.4 で graph は作り直されている）。
  待てないなら H-1 は「未測定（周期 1 時間）」と書く。

### 3.4 集める（観測①〜④）

```bash
snap after
diff "$W/istio-$MODE-before.tsv" "$W/istio-$MODE-after.tsv" | grep '^>' > "$W/istio-$MODE-delta.tsv"
cat "$W/istio-$MODE-delta.tsv"   # 列: 呼び出し元 宛先 HTTP gRPC状態 mTLS 累計

for d in $DESTS; do   # ① 受け手の要求ログ（:8081 の gRPC と、:8080 の REST の退役対象）
  kubectl -n "$NS" logs "deploy/$d-service" -c "$d-service" --since-time="$T0" \
    | grep -oE 'Request finished HTTP/[0-9.]+ [A-Z]+ http://[^ ]+ - [0-9]{3}' \
    | grep -E ':8081/|/internal/(introspection|mcp-tools)' | sed "s#^#$d #"
done | sort | uniq -c | tee "$W/requests-$MODE.txt"

for c in bff mcp aianalysis graph wiki retrieval datasource document ingestion conversion; do   # ③ 呼び出し元の失敗
  kubectl -n "$NS" logs "deploy/$c-service" -c "$c-service" --since-time="$T0" \
    | grep -iE 'over gRPC|RpcException|service token|StatusCode=|Unavailable|Unimplemented' | sed "s#^#$c #"
done | tee "$W/caller-errors-$MODE.txt"
```

| 観測 | 何を見るか（合格の形） |
| --- | --- |
| ① 受け手の要求ログ | `requests-$MODE.txt` に `<宛先> … Request finished HTTP/2 POST http://<宛先>-service:8081/<rpc のパス> - 200` が経路ごとに 1 件以上 |
| ② メッシュの計数 | `istio-$MODE-delta.tsv` に `<呼び出し元>-service <宛先>-service 200 0 mutual_tls <n>` が増えている（gRPC 状態 `0` ＝ OK。`request_protocol="grpc"` で選んでいる） |
| ③ 呼び出し元の失敗 | `caller-errors-$MODE.txt` に当該経路の行が無い |
| ④ REST を通っていない（I・M のみ） | `requests-$MODE.txt` に `HTTP/1.1 GET http://…:8080/internal/introspection` / `…/internal/mcp-tools` が **0 件** |

①の `200` は HTTP の状態であり、gRPC の状態は trailer にあるので①だけでは成否が決まらない。②の `grpc_response_status` で決める。
②は宛先と呼び出し元の組の数であって rpc ごとではないので、rpc の区別は①で取る。

### 3.5 平文の陰陽の対（このモードで 8081 が何を受け付けるか）

```bash
kubectl -n "$NS" run "h2c-probe-$(date +%s)" --rm -i --restart=Never \
  --image=curlimages/curl:8.11.1 --annotations=sidecar.istio.io/inject=false --command -- \
  sh -c 'curl -sS -o /dev/null -w "%{http_version} %{http_code}\n" --max-time 5 --http2-prior-knowledge \
    -X POST -H "content-type: application/grpc" -H "te: trailers" \
    http://authorization-service:8081/platform.authz.v1.AuthzScope/Resolve; echo "curl_exit=$?"' \
  | tee "$W/plaintext-probe-$MODE.txt"
```

サイドカーの無い Pod から、資格情報を付けずに平文 h2c を送る。

| モード | 期待 | 意味 |
| --- | --- | --- |
| PERMISSIVE | `2 401`（または `2 200` で gRPC 状態 16）、`curl_exit=0` | 平文もアプリへ届き、アプリの認証が落とした（h2c はアプリまで通っている） |
| STRICT | HTTP の応答が無く `curl_exit` が 0 以外（接続を切られる） | 受け手のサイドカーが mTLS の無い接続を拒んだ |

---

## 4. 2 つのモードを測る

1. §3 を**現行のモード**で行う。
2. もう一方のモードへ切り替える。**helm 経由の唯一の口**を使う（`kubectl patch` は使わない）:

   ```bash
   . scripts/lib/mesh-mtls-mode.sh
   set_mesh_mtls_mode PERMISSIVE   # 現行が STRICT のとき。逆なら STRICT
   kubectl -n "$NS" get peerauthentication microservices-platform-mtls -o jsonpath='{.spec.mtls.mode}{"\n"}'
   ```

   `set_mesh_mtls_mode` は `--reuse-values` なので §2 の上書きは保たれる。Pod は作り直されない（モードは新しい接続から効く）。
   §3.2 の M は `mcp-service` の再起動を伴うので、新しいモードの下で接続が張り直される。
3. §3 をもう一方のモードで行う。**元が PERMISSIVE なら STRICT の区間は 30 分以内**（S6）。
   🔴 **STRICT の回で画面を操作できるのは、入口が Istio Ingress Gateway のとき（`ISTIO=1 LOCALEDGE=1`）か、画面を `port-forward` で開いたときだけ**である。
   入口が Traefik のままなら STRICT では I と M（§3.2 の 2 つのコマンドだけで発火する）だけを測り、§1.2 の経路は「未測定（入口がメッシュ外）」と書く。
4. §6.1 で戻す（元のモードもここで戻る）。

### 4.1 メッシュと NetworkPolicy について確かめたこと（チャートと起動スクリプトから）

- **サイドカーが居るのは `microservices-platform` だけ。** `ISTIO=1` の起動スクリプトがこの名前空間にだけ `istio-injection=enabled` を貼る
  （`values-local.yaml` が `namespace.create: false` なので helm はラベルを貼らない）。`platform-infra`（Keycloak・Postgres・RabbitMQ・OTel collector）と
  `ai-stock-trading` は注入されない。**測る経路はすべて同じ名前空間の中**（両端にサイドカー）なので、PERMISSIVE でも STRICT でも
  サイドカー間は mTLS になる（チャートの `DestinationRule` が `*.microservices-platform.svc.cluster.local` を `ISTIO_MUTUAL` に固定し、ポートを限定しない）。
- **ポート名と appProtocol は足さなくてよい。** `grpcPort` を持つサービスは Service に `name: grpc` ＋ `appProtocol: grpc`、HTTP 側に
  `name: http` ＋ `appProtocol: http` が描画され、containerPort も `grpc` と名付けられている。サイドカーはプロトコルを推定せず HTTP/2 として扱い、
  アプリには平文 h2c が届く（アプリは 8081 で HTTP/2 だけを受ける。平文の HTTP/1.1 は 400）。
- **起動順**: `istiod-values-local.yaml` が `holdApplicationUntilProxyStarts: true` なので、起動時の収集（M）がサイドカーより先に走ることは無い。
- **readiness は 8080 の `/health/ready` のまま**。8081 にプローブは無い（1 プロセスが両ポートを起動時に開くので、8080 が Ready なら 8081 も開いている）。
- **NetworkPolicy**: ローカルは `networkPolicy.enabled: false`（`values-local.yaml`）なので何も無い。本番像のチャートは
  `allow-intra-namespace` が同じ名前空間の Pod からの流入を**ポートを限定せず**許すので、8081 のために足すものは無い。
- **AuthorizationPolicy**: チャートが描くのは BFF のバックチャネルログアウト（平文の 1 経路）と、エッジの同期経路（ゲートウェイの名前空間から
  文書サービスへの DENY。ローカルはエッジ無効で描画されない）だけで、名前空間の中から 8081 への呼び出しには当たらない。
- **STRICT の副作用**: サイドカーを持たない呼び出し元（別名前空間の AST から LLM ゲートウェイ・文書・検索への REST など）は STRICT で落ちる。
  **既定の入口（`kube-system` の Traefik）も同じ**で、Traefik → BFF・フロントの平文が落ち、画面が 5xx になる。起動スクリプトは STRICT を
  入口を Istio Ingress Gateway へ移した後（`istio-edge-up.sh` の最後の段）でしか宣言しない。入口が Traefik の構成で STRICT にするのは本書の測定の間だけであり、
  その間に測れるのは画面操作の要らない I と M だけである。
  本書の測定とは無関係だが、その間 AST の機能は使えない（S6）。

---

## 5. 合否と記録

### 5.1 経路ごとの判定（モードごとに付ける）

| 判定 | 条件 |
| --- | --- |
| **合格** | ①（`HTTP/2 POST …:8081/<rpc> - 200`）・②（`request_protocol=grpc`・gRPC 状態 `0`・`mutual_tls`）・③（失敗ログ無し）がそろう。I・M は ④（REST 0 件）も |
| **輸送のみ** | ①と②（`request_protocol=grpc`）はあるが gRPC 状態が `0` 以外（例: LLM の鍵が無い、権限外）。**h2c の往復は成立しているが業務は失敗**。状態コードと理由を書く |
| **不合格** | ①か②が無い（受け手に gRPC が届いていない）、②が `mutual_tls` 以外、③に `rejected over gRPC` / `could not obtain the caller's service token` / `Unimplemented` などがある、I・M で ④が 1 件以上（REST を通った） |
| **未測定** | 発火できなかった（周期を待てない・課金を承認していない・操作の手段が無い）。理由を書く |

### 5.2 モードごとの判定

| 対 | PERMISSIVE の合格 | STRICT の合格 |
| --- | --- | --- |
| §3.5 平文の対 | アプリへ届く（`401` など） | 接続を切られる |
| §1 の経路 | 合格 | 合格（STRICT でサイドカー間の h2c が通る） |

### 5.3 保留を解いてよいかの目安（決めるのはオーナー）

- **#1517**: I-01〜I-13 と M-1〜M-3 が **両モードで合格**。
- **#1255 残射程 2**: §1.2 の全行（B-1 を含む）が **両モードで合格**。「輸送のみ」「未測定」が残るなら、その行と理由を並べてオーナーが判断する。
- **#1255 やること 7**: A-1〜A-6（と B-1）が両モードで合格し、§3.5 の対が期待どおり。
- **H-1（グラフ → ダッシュボード）の STRICT**: 報告は 1 時間周期（初回は graph の起動から 1 周期後）で、STRICT の区間（元が PERMISSIVE なら 30 分以内）と噛み合わない。
  次のどちらかにする。
  1. **段取りで合わせる**: §2.4 の適用（graph が作り直される）から 2 時間目の報告が STRICT の区間に入るよう、PERMISSIVE の回を先に 1 時間目の報告まで行い、
     2 時間目の少し前に §4 の 2 で STRICT へ切り替える（graph は再起動しない。モードの切り替えでは Pod は作り直されない）。
  2. **未測定のまま回す**: H-1 の STRICT を「未測定（周期 1 時間）」と書き、PERMISSIVE の合格と §3.5 の対（STRICT の効き）を根拠にオーナーの判断に回してよい。

### 5.4 記録の書式（#1255 と #1517 へコメントで貼る）

```text
## 稼働 k3s での h2c 往復の実測（<日付>・<実施者>）
- チェックアウト: <checkout.txt の SHA>  helm リリース: msp rev <適用前> → <計測> → <切り戻し後>
- 元のモード: <PERMISSIVE|STRICT>  計測した順: <…>  イメージを作り直した時刻: <…>
- 上書き: overlay.json（removed=0 added=26）／overlay-bff.json（removed=0 added=28）
| # | 呼び出し元 → 宛先 | rpc | PERMISSIVE | STRICT | 証拠（requests / istio delta の行） | 備考 |
| I-01 | bff → document | ServiceIntrospection/Get | 合格 | 合格 | … | |
| … |
- 平文の対: PERMISSIVE=<…> / STRICT=<…>
- 期待値と違ったもの: <そのまま書く>
- 切り戻し: manifest の差分 <n> 行 / check-stack-ready <結果>
```

`$W` の中身（`*.tsv`・`requests-*.txt`・`caller-errors-*.txt`・`plaintext-probe-*.txt`・`overlay*.diff`）は資格情報を含まないので、そのまま添付してよい。

---

## 6. 切り戻し

### 6.1 通常（計測の終わり・中止条件に当たったとき）

```bash
helm upgrade msp deploy/helm/microservices-platform -n "$NS" -f "$W/current-values.yaml"   # env もモードも §0.3 の時点へ
kubectl -n "$NS" rollout status deployment --timeout=10m
helm get manifest msp -n "$NS" | diff "$W/manifest-before.yaml" - | wc -l                 # 0 行
kubectl -n "$NS" get peerauthentication microservices-platform-mtls -o jsonpath='{.spec.mtls.mode}{"\n"}'   # 元のモード
node scripts/check-stack-ready.js --live                                                    # 門 G12（メッシュ資材の宣言と稼働）を含め緑
```

保存した利用者値だけを渡すので、計測用の `extraEnvAppend` も `set_mesh_mtls_mode` で変えたモードも、§0.3 の時点の宣言へ戻る。

### 6.2 経路単位の緊急切り戻し（その経路だけを REST へ戻す）

計測中にある経路が利用者の操作を壊していると分かったら、その経路の gRPC 宛先だけを抜く。**リストを手で書かない**。

- 🔴 **今適用している上書きと同じオプションを付け直す。** B-1 を測っている（`overlay-bff.json` を適用中）なら `--bff-authz` も付ける。
  付けずに組むと B-1 の宛先が**黙って外れ**、しかも `render-before.yaml` との差分には現れない（どちらにも無いため）。
- 🔴 **差分は今適用している描画と比べる**（`render-before.yaml` ではない）。下の `render-now.yaml` がそれである。
- 🔴 **今のモードを `--set` で渡す。** 保存した利用者値は §0.3 の時点のモードを持つので、§4 でモードを切り替えた後に渡さないと元のモードへ戻る。
- **B-1 だけを戻す**には `--bff-authz` を付けずに組み直す（`--rest bff=Services__AuthorizationServiceGrpc` は「extraEnv に無い」でエラーになる。B-1 の宛先は `extraEnvAppend` にあるため）。

```bash
MODE=STRICT; BFF=--bff-authz      # 今のモード／B-1 を適用中でなければ BFF=
C=deploy/helm/microservices-platform
node "$W/gen-overlay.mjs" "$W/all-values.json" $BFF > "$W/overlay-now.json"
node "$W/gen-overlay.mjs" "$W/all-values.json" $BFF --rest mcp=Mcp__GrpcServices__document-service > "$W/overlay-rest.json"
helm template msp "$C" -n "$NS" -f "$W/current-values.yaml" -f "$W/overlay-now.json"  --set "mesh.mtlsMode=$MODE" > "$W/render-now.yaml"
helm get manifest msp -n "$NS" | diff "$W/render-now.yaml" - | wc -l          # 0 行（今の稼働と同じものを組めている）
helm template msp "$C" -n "$NS" -f "$W/current-values.yaml" -f "$W/overlay-rest.json" --set "mesh.mtlsMode=$MODE" \
  | diff "$W/render-now.yaml" - | grep '^[<>]'      # 消えるのは抜いた 2 行だけ・足される行は無いこと
helm upgrade msp "$C" -n "$NS" -f "$W/current-values.yaml" -f "$W/overlay-rest.json" --set "mesh.mtlsMode=$MODE"
```

🟢 `--rest` で抜いた宛先は**上書きとしてリリースに残る**。§6.1 を飛ばして `--reuse-values` の helm（`set_mesh_mtls_mode`・`istio-edge-up.sh`）を続けると、
その固定（その宛先だけ REST）が引き継がれ続ける。計測を終えたら必ず §6.1 で保存した利用者値へ戻す。

🔴 **§1.3 の gRPC だけの経路には使わない**（REST の兄弟が無いので、宛先を抜くと縮退＝その機能が止まる）。
`--rest` で戻した経路は §5 に「不合格（切り戻し）」と書く。

### 6.3 `helm upgrade` が field manager の conflict で落ちたとき

値を変えて再試行しない。[運用仕様書](operations.md) の「メッシュ設定のドリフトと、helm リリースが固まったときの復旧」に従う
（対象を消して helm に作り直させる）。その後 §6.1 を流す。

---

## 失敗したときの分岐

| 症状 | 見るところ | 次の一手 |
| --- | --- | --- |
| §0.3 (4) で 8081 の行が無い | そのサービスの env に `Grpc__Port` が在るか（`kubectl -n "$NS" get deploy <名>-service -o yaml`） | 在るならイメージが古い → イメージを作り直して起動スクリプトから立て直す。無いならチャートの版が古い |
| ②が `connection_security_policy=none` | 呼び出し元の Pod に `istio-proxy` が在るか | 注入前に作られた Pod。起動スクリプトの注入の段（`rollout restart`）を経ていない |
| ①があり③に `rejected over gRPC (Unauthenticated)` | 呼び出し元の `ServiceToken__ClientId` と Secret、realm の service account | 配線不備（不合格として記録）。realm の追随（`reconcile-realm.sh`）と Secret の同期を確かめる |
| ③に `could not obtain the caller's service token` | 呼び出し元から Keycloak への到達・client secret の一致 | 同上 |
| ③に `Unimplemented` | 受け手のイメージ | 面が無い＝古いイメージ |
| ①に `HTTP/1.1 GET …/internal/introspection` が出る（I） | BFF の env の `Introspection__GrpcServices__<名>` | その宛先だけ gRPC 宛先が無い。§2.3 の差分で消していないか確かめる |
| §3.5 が STRICT でも `401` | `kubectl -n "$NS" get peerauthentication -o yaml` | モードが切り替わっていない。§4 の 2 をやり直す（`kubectl patch` は使わない） |

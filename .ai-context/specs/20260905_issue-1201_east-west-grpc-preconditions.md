---
title: 作業仕様書 — east-west gRPC 移行の先行条件を履行する（proto の置き場・versioning・h2c・s2s トークン）（#1201）
type: spec
status: in-progress
related_ids:
  - FR-05
  - NFR-09
  - NFR-16
  - ADR-0004
  - ADR-0029
  - ADR-0032
  - ADR-0075
  - IADR-0117
  - IADR-0122
  - IADR-0229
  - IADR-0251
  - IADR-0368
  - IADR-0379
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md (Accepted 2026-09-03) 決定 1・2・6
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md (Accepted 2026-07-25 / 2026-09-03 部分改定) 決定・フォローアップ
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md (Accepted) 決定
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09 / NFR-16
---

# 作業仕様書: east-west gRPC 移行の先行条件を履行する（#1201）

起点: 実装 issue #1201。計画 `ADR-0075` 決定 2 が「`ADR-0029` のフォローアップ（proto 契約の配置と
versioning 規約を実装ガイドへ落とす）の履行」を移行着手の**先行条件**とし、期限を 2026-11-30 に置いた。
同 決定 1 は基盤（MSP）が **proto の置き場・versioning 規約・h2c 用ポートの扱い・s2s トークンの写し方**の
現物を作り、AST がそれに追随すると定めている。

> 🔴 **本作業は「先行条件の履行」であって「gRPC への移行」ではない。** 既存の HTTP / Wolverine の
> 呼び出しは 1 本も置き換えない。作るのは基盤（規約・共通ヘルパ・検査器・manifest）と、
> **並走する参照実装 1 経路**だけである。残り（§7）は別 issue に切る。

## 1. 母集合（着手時に自分で引き直した）

基点 `origin/develop` = **`f2b82d7d`**。`git rev-parse --is-shallow-repository` = **`false`**。

### 1.1 事実（着手時点）

| 事実 | 値 | 陽性対照 |
| --- | --- | --- |
| `git ls-files "*.proto" \| wc -l` | **0** | — |
| `git grep -l "Grpc" -- "*.csproj" \| wc -l` | **0** | `git grep -l "Refit" -- "*.csproj" \| wc -l` = **1**（同じ走査が Refit を拾う） |
| `Grpc.*` / `Google.Protobuf` の CPM 宣言 | 4 行（`Grpc.AspNetCore` / `Grpc.Net.Client` / `Grpc.Tools` 2.83.0・`Google.Protobuf` 3.35.1） | いずれも参照 0 件 |
| Refit のインタフェース（`[Get(`・`RestService`） | **0 件**（BFF の `.csproj` に PackageReference が残っているだけ） | — |
| Wolverine のイベント契約（`Knowledge.Contracts/Events/*.cs`） | **6 件** | — |

### 1.2 サービス間の同期呼び出し（`AddHttpClient` の全数）

走査: `git grep -n "AddHttpClient" -- "src/platform/backend/**/*.cs" "src/knowledge/backend/**/*.cs"` から
テスト（`Tests/`・`*Tests.cs`）を除いた **47 行**。1 行ずつ宛先を読んで分類した（issue の表は転記していない）。

**east-west 同期（gRPC の候補）= 32 本**

| 呼び出し元 | 宛先（内部サービス） | 本数 | 備考 |
| --- | --- | --- | --- |
| `Platform.Bff` | AiAnalysis / Feedback / Dashboard / **Authorization** / Retrieval / Graph / McpServer / Notification / **Wiki** / Document / Conversion / DataSource / Configuration(AST) / RiskManagement(AST) / MarketMonitor(AST) | **15** | issue の 14 は `WikiService`（#1199 で追加）を数えていない |
| `Platform.Bff`（`HttpEffectiveConfigCollector`） | 全サービスの `/internal/introspection` | **1** | 構成情報 API の fan-out。基準上は east-west 同期に当たる |
| `AiAnalysisService` | Authorization / Retrieval / LlmGateway | 3 | |
| `GraphService` | Authorization / LlmGateway（typed） / Document（tag writer） / Dashboard（health reporter） | 4 | issue は 3 と数えたが `HttpDocumentTagWriter` → DocumentService が別に在る |
| `RetrievalService` | Graph / LlmGateway（typed） | 2 | |
| `WikiService` | Authorization | 1 | |
| `IngestionService` | LlmGateway（typed） | 1 | |
| `ConversionService` | LlmGateway（typed） | 1 | |
| `DocumentService` | Notification | 1 | |
| `DataSourceService` | Authorization（`AuthorizationServiceUserDirectory`。#1194） | 1 | issue の表に無い |
| `McpServer` | Authorization（registrar attributes）／ ツール公開元サービス（`HttpToolInvoker`。素の factory ＋ 絶対 URL） | 2 | issue は「素の `AddHttpClient()`」として除外していたが宛先は内部サービスである |
| **計** | | **32** | |

**候補にしないもの（除外理由つき）= 15 行**

| 行 | 理由 |
| --- | --- |
| `AuthorizationService` → Keycloak Admin REST | 外部 IdP（north-south の外側） |
| `WikiService` → Wiki.js GraphQL ×2 | 外部製品 |
| `DataSourceService` → `WikiConnector` / `SaaSConnector` | 外部コネクタ |
| `GraphService` / `IngestionService` / `WikiService` → MinIO（Storage*Reader ×3） | オブジェクトストレージ（S3 API） |
| `LlmGateway` 素の factory | 外部 LLM プロバイダ |
| `Platform.Bff` 素の factory ×2（Program / `BffSessionExtensions`） | Keycloak（OIDC）と汎用 |
| `ObservabilityExtensions.AddHttpClientInstrumentation` ×2 | クライアントではなく計装 |
| BFF readiness の `AddUrlGroup`（`/health/live` ×4） | ヘルスプローブであり業務呼び出しではない（`AddHttpClient` 行ではない） |

**候補にしないもの（形が違う）**: Wolverine の非同期イベント 6 件（`ADR-0029` は**同期**の基準であり、
非同期は `ADR-0027` の射程）、SSE（north-south、REST 側に含む）。

### 1.3 候補／非候補の基準（`ADR-0029` §決定 をそのまま写した）

**候補 = 同期 ∧ east-west（両端がメッシュ内のサービス）∧ 呼び出し側が応答を待つ。**
外部 SaaS・IdP・ストレージ・非同期メッセージ・ブラウザ／外部エージェント向け（REST + OpenAPI）は非候補。
**呼び出しプロファイル（頻度・レイテンシ要求）では判定しない**（`ADR-0075` 決定 3 が退けた）。

## 2. 対象範囲

- **対象**（本 PR）
  1. proto の置き場と所有の規約 ＋ 現物 1 件（`AuthzScope`）
  2. versioning 規約 ＋ 検査器 `scripts/check-proto-contracts.js`（baseline ／ allowlist ／ 自己試験 ／ 変異試験）
  3. h2c 用リスナ（共通ヘルパ）＋ helm / compose の追随（AuthorizationService のみ）
  4. s2s トークン（共通ヘルパ: 発行側 `IServiceTokenProvider` ／ 検証側 `ServiceCaller` ポリシー）
  5. 参照実装 1 経路: **BFF → AuthorizationService `/authz/scope`** を gRPC でも呼べるようにする（REST が正・gRPC は opt-in）
  6. 実装ガイド（通信仕様書 `docs/api/`）・技術要件書の追随・`IADR-0379`・`IADR-0122` への追記
- **対象外**（別 issue へ）: 残り 31 本の移行、AST 側の 22 本と AST→MSP 4 本（AST#584）、gRPC ヘルスプロトコル、
  Keycloak の各サービス用クライアントの一括登録、Refit の PackageReference 撤去、token exchange（RFC 8693）。

## 3. 設計（4 つの決定。論拠は `IADR-0379`）

### 決定 1: proto の置き場と所有

- **所有者は呼び出される側**（`ADR-0029`）。**置き場は所有サービスが属するユニットの共有契約プロジェクト**
  （platform 所有 → `Platform.Shared.Contracts`、knowledge 所有 → `Knowledge.Contracts`）の
  `Protos/<unit>/<service>/v<N>/<name>.proto`。`GrpcServices="Both"` でクライアント・サーバ基底の両方を生成し、
  **`*.Client` プロジェクトは作らない**（`ADR-0029` 2026-08-04 追記）。
- ユニット外参照の規則は変えない（`IADR-0117`: `Shared/` の 3 プロジェクトのみ。platform → 可変ユニットは禁止）。
  したがって **platform のサービスが knowledge の gRPC を呼ぶことは今後も無い**（現状の HTTP と同じ向き）。
- 生成物（`obj/` の `.cs`）はコミットしない。`check-contract-schema.js` は `obj/` を走査しないため無影響。

### 決定 2: versioning

- `package <unit>.<service>.v<N>;`（小文字）。`option csharp_namespace` は `<ContractsRoot>.Grpc.<Service>.V<N>`。
- **フィールド番号は不変**。削除は `reserved <番号>` と `reserved "<名前>"` を残すことが条件。型・ラベル
  （`repeated` / `map`）・番号・名前の変更、message / rpc / enum 値の削除、rpc の型変更、package 変更は**破壊的**。
  追加（field / message / rpc / enum 値）は非破壊。**破壊的変更は新しいメジャー（`v2` ディレクトリ・`v2` パッケージ）を
  並走させて行い、`v1` を in-place で壊さない。**
- 検査器 `scripts/check-proto-contracts.js`: 規約（パス⇔package⇔namespace の一致・番号の一意・reserved の再利用禁止）と
  baseline（`scripts/proto-contract-baseline.json`）との後方互換を検査する。破壊的変更は
  `scripts/proto-breaking-allowlist.json` の承認エントリで通し `--update` が消費する（`IADR-0122` 決定 3 と同型）。
- `check-contract-schema.js`（C# 構文解析）の母集合には**入れない**。構文が違い、互換規則も違う（番号の不変性）ため、
  1 構文 1 パーサとする。`IADR-0122` に日付つき追記を置く。

### 決定 3: h2c

- **専用ポート**（既定 8081・構成 `Grpc:Port`。未設定／0 なら gRPC リスナを立てない）に `HttpProtocols.Http2` **だけ**を
  bind する。HTTP/1.1 のポート（8080: REST・`/health/*`・introspection）はそのまま。ALPN 不在の平文で
  `Http1AndHttp2` に頼らない（`ADR-0075` §残るもの が挙げた懸念を、切替ではなく分離で消す）。
- Kestrel は `Listen*` を 1 つでも構成するとホスティング URL（`ASPNETCORE_URLS` / `HTTP_PORTS`）を捨てるため、
  共通ヘルパ `AddPlatformGrpcListener` は **HTTP/1.1 側のポートも同時に再宣言する**（試験で固定）。
- helm: `services.<name>.grpcPort` を宣言したサービスにだけ `containerPort`（名前 `grpc`）と Service ポート
  （`name: grpc` / `appProtocol: grpc`）を描画する。複数ポートになるため HTTP 側にも `name: http` を付ける
  （**`grpcPort` 無しのサービスは 1 バイトも変わらない**）。env `Grpc__Port` を注入する。
  compose: `expose` に 8081 を足し `Grpc__Port` を与える（host 公開はしない）。
- **readiness は HTTP の `/health/ready`（8080）のまま**。1 プロセスが両ポートを起動時に bind するので
  8080 が ready なら 8081 も bind 済みである。gRPC ヘルスプロトコルは今は足さない（射程外）。
- Istio: サイドカーがある限り PERMISSIVE / STRICT のどちらでも **mTLS は Envoy で終端され、アプリには平文 h2c が届く**。
  アプリ側の設定は両モードで同一。`appProtocol: grpc` でプロトコル推定に頼らない。既存の
  `DestinationRule`（`ISTIO_MUTUAL`・host ワイルドカード）は全ポートに掛かるので追加不要。

### 決定 4: s2s トークン

- gRPC の `authorization` メタデータには **呼び出し側サービス自身の資格情報**（platform realm から
  client credentials で得た JWT）を載せる。**利用者のトークンを載せない。** 載せると呼び出し先は
  「利用者が直接呼んだ」と「サービスが利用者のために呼んだ」を区別できず、利用者ロール（AdminOnly 等）が
  サービス間の面へ漏れる（confused deputy）。
- 呼び出し先は `AddPlatformAuth` と同じ JwtBearer で検証し、ポリシー **`ServiceCaller`**（realm ロール
  `platform-service`）を gRPC サービスに要求する。トークン無し → `UNAUTHENTICATED`、ロール無し → `PERMISSION_DENIED`。
- **利用者の文脈（`user_id` / 属性 / action）は本文で運ぶ**（REST の `AccessScopeRequest` と同じ。移行が機械的な
  トランスポート差し替えになる）。ABAC の deny-by-default は変えない: 該当ポリシーが無ければ `granted=false`、
  呼び出し側は `UNAUTHENTICATED` / `PERMISSION_DENIED` / `UNAVAILABLE` をすべて `null`（＝閲覧可能なし）へ縮退する。
- BFF セッション方式（`ADR-0032`）との分け方: **セッション Cookie ↔ 利用者トークンは north-south、
  s2s トークンは east-west**。BFF は自分の confidential client（`bff`）で client credentials を取る
  （realm の `bff` に `serviceAccountsEnabled` と `platform-service` を付ける）。
- 呼び出し先が利用者自身の権限で動く必要が出た場合は RFC 8693 token exchange（`act` claim）へ進む。今は採らない。

### 参照実装（1 経路）

`Platform.Bff` → `AuthorizationService` の `/authz/scope`（`BffScopeResolver.ResolveAsync`。BFF 内 6 箇所が呼ぶ）。
`Services:AuthorizationServiceGrpc`（h2c アドレス）が構成されたときだけ gRPC（`platform.authz.v1.AuthzScope/Resolve`）で
呼び、無ければ従来の REST。**並走中の正は REST**。gRPC 側は同じ `AbacEvaluator.ResolveScope` を呼ぶ
（評価器を 2 つにしない）。

## 4. 変更ファイル（宣言領域）

- `src/platform/backend/Shared/Platform.Shared.Contracts/{Protos/platform/authz/v1/authz_scope.proto, *.csproj}`
- `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Grpc/*`・`Foundation/Authz/AuthzScopeGrpcClient.cs`・
  `Foundation/Authz/BffScopeResolver.cs`・`Foundation/Extensions/AuthExtensions.cs`・`*.csproj`
- `src/platform/backend/Services/AuthorizationService/{Features/Authz/ResolveScope/GrpcService.cs, Program.cs, *.csproj, Tests/**}`
- `src/platform/backend/Bff/Platform.Bff/Program.cs`
- `src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Foundation/{Grpc,Authz}/*`
- `deploy/helm/microservices-platform/{values.yaml, templates/deployment.yaml, templates/service.yaml}`・
  `deploy/docker-compose.yml`・`deploy/keycloak/microservices-platform-realm.json`
- `scripts/{check-proto-contracts.js, proto-contract-baseline.json, proto-breaking-allowlist.json, scripts.test.js, README.md}`・
  `.github/workflows/ci.yml`
- `docs/api/east-west-grpc.md`（新規・通信仕様書）・`docs/tech/tech-requirements.md`・`src/README.md`
- `.ai-context/adr/{IADR-0379_*.md, README.md, IADR-0122_*.md（追記）}`

並列作業との交差: #1230 は `src/*/backend/Services/**` を触る（本作業は AuthorizationService のみ）。
#1159（`deploy/local/**`）・#1219（`deploy/helm/**`・`check-stack-ready.js`）・#1203（`deploy/prometheus/**`）とは
helm の `values.yaml` / `deployment.yaml` / `service.yaml` で交差し得る。PR 直前に `origin/develop` を merge して解く。

## 5. テスト

| # | 何を | どこで | 種別 |
| --- | --- | --- | --- |
| T-01 | 実 Kestrel の h2c ポートへ gRPC で往復し `granted=true` を得る（陽性対照） | `AuthorizationService.Tests` `GrpcResolveScopeTests` | 結合（Docker 不要。`TestKind=Integration`） |
| T-02 | 同じポートへ HTTP/1.1 で GET すると失敗する（Http2 専用であること） | 同上 | 結合 |
| T-03 | トークン無し → `Unauthenticated` | 同上 | 結合（陰性対照） |
| T-04 | `platform-service` を持たないトークン（管理者の利用者トークン）→ `PermissionDenied` | 同上 | 結合（**利用者トークンを転送しても通らない**） |
| T-05 | s2s は正しいがポリシーが無い利用者 → `granted=false`（deny-by-default） | 同上 | 結合 |
| T-06 | 不正な action → `InvalidArgument`（REST の 400 と同値） | 同上 | 結合 |
| T-07 | REST と gRPC で同じ入力が同じ `granted` / filters / branches を返す | 同上 | 結合 |
| T-08 | `AddPlatformGrpcListener` が `Grpc:Port` 未設定なら何も bind せず、設定時は HTTP/1.1 のポートも保つ | `Platform.Shared.Infrastructure.Tests` | 単体 |
| T-09 | `ClientCredentialsServiceTokenProvider` が期限内はキャッシュし、期限前 30 秒で取り直す | 同上 | 単体 |
| T-10 | `AuthzScopeGrpcClient` が `RpcException` を null（deny）へ縮退する ／ `granted=true` を写す | 同上 | 単体 |
| T-11 | `BffScopeResolver` が gRPC クライアント登録時は gRPC を、未登録時は REST を使う | 同上 | 単体 |
| T-12 | gRPC サービス型が `[Authorize(Policy=ServiceCaller)]` を持つ（リフレクション） | `AuthorizationService.Tests` | 単体 |
| C-01 | `check-proto-contracts.js --self-test`: 規約の正例・負例、baseline 互換の正例・負例、**変異試験**（番号付け替え／削除／型変更／reserved 再利用が赤になる） | `scripts/` | 検査器 |

## 6. 受け入れ基準（issue の Given-When-Then を本作業の言葉で）

- [ ] 実装ガイド（`docs/api/east-west-grpc.md`）に 4 点（置き場・versioning・h2c・s2s）と「並走中の正は REST」が書かれている
- [ ] `git ls-files "*.proto" | wc -l` ≥ 1、`git grep -l "Grpc" -- "*.csproj" | wc -l` ≥ 1
- [ ] gRPC 経路で権限外の要求が deny-by-default で落ち（T-03〜T-05）、権限内が通る（T-01）
- [ ] helm と compose の両方に h2c ポートが在り、readiness の扱いがガイドに書かれている
- [ ] `dotnet build` / `dotnet test` 両ユニット 0 警告 0 エラー・Failed=0、`dotnet format --verify-no-changes` 両ユニット成功
- [ ] `node scripts/check-contract-schema.js` 成功（proto を母集合へ入れない判断は `IADR-0379`）
- [ ] `node scripts/check-proto-contracts.js` と `--self-test` 成功
- [ ] `node scripts/check-trace-blocks.js` / `gen-knowledge-graph.js --check` / `check-doc-*.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 成功
- [ ] 残射程（§7）が issue として起票され、PR 本文に番号が載っている

## 7. 残射程（起票した）

1. **#1255** — MSP east-west の残り 31 本を gRPC へ移す展開 issue（呼び出し元ごとに Keycloak の confidential client と
   `platform-service` の付与、`grpcPort` の追随、gRPC 計装・gRPC ヘルスの要否、稼働 k3s での h2c 実測、REST 撤去の段の
   「正の反転」IADR を含む）。重複検索: `gRPC` / `east-west` / `proto` で open の展開 issue は 0 件（陽性対照 `Wiki` は 3 件以上）。
2. AST 側（AST#584）は本リポジトリからは起票しない（`ADR-0075` 決定 4）。

## 9. 実測（着手後に分かったこと）

- **h2c 専用ポートへの HTTP/1.1 要求は Kestrel が 400 Bad Request で処理しない**（426 ではない）。T-02 はこの値で固定した。
- **`WebApplicationFactory.ConfigureAppConfiguration` は builder 時点の読み取りに間に合わない**（`TestDatabaseConfiguration` の
  注記と同型。in-memory で `Grpc:Port` を与えると `IConfiguration` には載るがリスナは 1 つも立たなかった）。gRPC の器は
  `GrpcTestConfiguration`（ModuleInitializer）が環境変数 `Grpc__Port` / `ASPNETCORE_URLS` で与える。
- **Kestrel は Listen を 1 つでも構成するとホスティング URL を捨てる**。共通ヘルパが HTTP 側を再宣言しない版では
  HTTP/1.1 のポートが消えた（T-02 / T-07 の陽性対照で固定）。
- gRPC の状態写像は期待どおり: 401 → `UNAUTHENTICATED`、403 → `PERMISSION_DENIED`（Grpc.Net.Client の既定）。
- `check-proto-contracts.js --self-test` 40 件（うち変異試験 12 件）が緑。`scripts.repo.test.js` 側は baseline を
  改変した走査経路つきの変異（reserved 無しの削除 → exit 1）を固定した。

## 8. 未実測

- 稼働 k3s での h2c 往復: **未実測**。新イメージの配備＝Pod の再起動を要し、本作業では禁じられている。
  代わりに実 Kestrel（TestServer ではない）で h2c を往復する T-01 / T-02 を置く。

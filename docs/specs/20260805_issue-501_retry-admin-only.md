---
title: SC-07 再変換 API（POST /bff/conversion/jobs/{id}/retry）を管理者ロール限定へ揃える
type: spec
status: done
related_ids: [FR-12, UC-06, SC-07, IADR-0042, IADR-0128]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../adr/IADR-0042_conversion-job-read-model.md"
  - "../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md"
  - "../screens/SC-07_conversion-jobs.md"
  - "../tests/SC-07_conversion-jobs.md"
  - ./20260709_issue-133_sc07-conversion-jobs.md
---

# 仕様書: SC-07 再変換 API を管理者ロール限定へ揃える（#501）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-12**（文書正規化）
- ユースケース（UC）: **UC-06**（変換・正規化の状況確認・人手補正）
- 画面（SC）: **SC-07 変換ジョブ画面**
  （[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-07 §データソース）
- 関連 ADR:
  [IADR-0042](../adr/IADR-0042_conversion-job-read-model.md)（変換ジョブ読み取りモデル。**決定 3 が本作業の被改定側**）／
  [IADR-0029](../adr/IADR-0029_config-info-api-placement-and-drift-granularity.md)（ワーカーの最小 HTTP サーフェス。下流の認可を課さない根拠）／
  [IADR-0039](../adr/IADR-0039_datasource-management-bff-and-role-gating.md)（管理系ロール）／
  [IADR-0026](../adr/IADR-0026_mesh-mtls-supersedes-network-isolation.md)（mTLS 第一防御・ネットワーク分離は多層防御）／
  [IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md)（本作業の判断記録）
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
- 本リポジトリの起点: #501（画面側 #503 / PR #508・親 #454）

## 目的・背景

計画 [05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-07
§データソース（**2026-08-04 確定**・環流 planning#191 への裁定）は次を定める。

> **再変換の実行権限は管理者ロールに限る**（2026-08-04 確定）。**本画面のアクセス制御と API の権限を揃える**
> —— API 側だけ緩いと画面の制御が意味を持たないためである。

対象コミット `de55761`（develop）の実測は次のとおりで、**計画が名指しで否定した「API 側だけ緩い」形**である。

| 面 | 現状（`de55761`） | 根拠 |
| --- | --- | --- |
| BFF `POST /bff/conversion/jobs/{id}/retry` | **admin または operator**（グループ一括） | [`ConversionBffEndpoints.cs`](../../src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/ConversionBffEndpoints.cs) 18-22 行の `MapGroup(...).RequireAuthorization(p => p.RequireRole(AdminRole, OperatorRole))` に `retry` も含まれる |
| 画面（この develop 時点） | **admin または operator**（`RequireRole anyOf=[Admin, Operator]`） | [`features/sc07-conversions/index.tsx`](../../src/knowledge/frontend/src/features/sc07-conversions/index.tsx) 20 行 |
| 画面（#503 / PR #508・**未マージ**） | 再変換ボタンは **platform-admin のみ** | issue #501 の実測表 |

画面のボタンを消しても、**API を直接叩ける運用者は依然 retry できる**。本作業は API 側の是正を行う。
**計画側の裁定は不要**である（計画は既に「admin 限定」と確定しており、要るのは実装の追随だけ）。

## 対象範囲

### 含むもの

1. **BFF `retry` の認可を `platform-admin` のみへ絞る**（`GET` 系は据え置く。理由は下記）。
2. **下流（ConversionService `/jobs/*`）の到達性と認可の実測**、および実測に基づく判断（§下流の調査）。
3. 権限テスト（operator 拒否 / admin 成功）と回帰テスト（`processing` の 409 `not_retryable` / 照会権限不変）。
4. [IADR-0042](../adr/IADR-0042_conversion-job-read-model.md) 決定 3 への日付付き［追記］、
   [IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) の起票、索引・画面/テスト仕様書の追随。

### 含まないもの（据え置き）

| 対象 | 据え置きの理由 |
| --- | --- |
| **照会（`GET /bff/conversion/jobs`・`GET /bff/conversion/jobs/{id}`）の権限** | 2026-08-04 の確定は**再変換の実行権限**に限られる。**閲覧ロールの裁定は planning#198 提案 8 で別途仰いでいる最中**であり、グループ全体を絞ると**まだ裁定の出ていない閲覧まで巻き添えで変わる**（計画に無い制限を実装が先に作ることになる）。 |
| **画面（`ConversionJobsPage.tsx` / `index.tsx`）の再変換ボタン** | #503 / PR #508 が引き受け済み。ここで触ると同一ファイルで衝突する。 |
| **ConversionService へのアプリ層認証・認可の導入** | [IADR-0029](../adr/IADR-0029_config-info-api-placement-and-drift-granularity.md)（ワーカーは最小 HTTP サーフェス）と [IADR-0042](../adr/IADR-0042_conversion-job-read-model.md) 決定 3 が定めた構造の変更であり、認証配線（`AddPlatformAuth` ＋ 全環境への `Auth:Authority` 注入）を伴う。§下流の調査のとおり**ロールの非対称は存在しない**ため「揃える」対象ではなく、別 issue で判断すべき独立の決定である（[IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) 決定 3・フォローアップ 1）。 |
| `ingestion-service` のネットワーク分離ガード追加 | 同型の穴だが FR-12 / SC-07 の範囲外（フォローアップ 2）。 |

## 下流の調査（ConversionService へ直接到達できるか）

「BFF だけ絞っても、サービスへ直接到達できる経路があれば同じ穴が残る」。**到達できないと決めつけず**、
認可指定とデプロイ構成を実際に読んで判断した結果は次のとおり。

### 1. 下流の認可指定（実測）

[`ConversionJobEndpoints.cs`](../../src/knowledge/backend/Services/ConversionService/src/ConversionService.Worker/Foundation/Endpoints/ConversionJobEndpoints.cs) 14 行:

```csharp
var g = app.MapGroup("/jobs").WithTags("Conversion Jobs");
```

`RequireAuthorization` は**一切付いていない**。さらに
[`Program.cs`](../../src/knowledge/backend/Services/ConversionService/src/ConversionService.Worker/Program.cs) は
`AddPlatformAuth` / `UseAuthentication` / `UseAuthorization` を**呼んでいない**（認証基盤そのものが無い）。

- したがって下流は「**operator には緩く admin には厳しい**」のではなく、**ロールの区別が存在しない**
  （届いた者は誰でも実行できる）。**BFF を絞ったことで生じる新しい非対称ではなく**、IADR-0042 決定 3 が
  当初から選んだ姿勢（認可は BFF に集約・ワーカーは最小 HTTP サーフェス）そのものである。
- **付言（重要）**: この状態で `RequireAuthorization` だけを足すと、認可ミドルウェア不在のため
  `/jobs/*` は起動後に例外（`Endpoint contains authorization metadata, but a middleware was not found`）で
  **全滅する**。「1 行足せば揃う」変更ではない。

### 2. 到達性（実測）

| 面 | 実測 | 根拠 |
| --- | --- | --- |
| ローカル（compose） | host へ**非公開**（`expose: "8080"` のみ・`ports:` 無し） | [`deploy/docker-compose.yml`](../../deploy/docker-compose.yml) 256-276 |
| 本番系（Helm）: Service | ClusterIP（外部 LB 無し） | [`templates/service.yaml`](../../deploy/helm/microservices-platform/templates/service.yaml) |
| 本番系: エッジ（Istio） | VirtualService の経路は `/bff/*` → `bff-service` と catch-all → `frontend-service` のみ。**conversion-service への経路は無い** | [`templates/edge.yaml`](../../deploy/helm/microservices-platform/templates/edge.yaml) / [`values.yaml`](../../deploy/helm/microservices-platform/values.yaml) §edge |
| 本番系: NetworkPolicy | 既定 deny-ingress ＋ 同 Namespace 内のみ許可。`istio-system` からの明示許可は **bff-service と frontend-service に限定** | [`templates/networkpolicy.yaml`](../../deploy/helm/microservices-platform/templates/networkpolicy.yaml) |

**結論**: 外部（ブラウザ・エッジ）から `conversion-service:8080/jobs/{id}/retry` へ到達する経路は無い。
運用者が持つのは Keycloak のロールであってクラスタ内ネットワークではないため、**BFF を絞れば
「運用者が retry を実行できる」経路は塞がる**。残る到達手段は同 Namespace 内の別 Pod か
`kubectl port-forward` であり、それは Keycloak ロールではなく**クラスタ権限**の問題である
（[IADR-0026](../adr/IADR-0026_mesh-mtls-supersedes-network-isolation.md) の防御対象）。

### 3. ただし「compose の非公開」は機械で守られていない（実測）

[`NetworkIsolationTests.cs`](../../src/knowledge/backend/Tests/Knowledge.IntegrationTests/Deployment/NetworkIsolationTests.cs) の
`InternalAppServices` は 12 サービスを列挙するが、**`conversion-service` はその中に無い**。
すなわち上記 2 の「host 非公開」は**回帰ガードの外**にあり、誰かが `ports:` を足しても CI は沈黙する。

**下流に対する本作業の措置は、この代償統制（compensating control）を機械検査に載せること**とする
（アプリ層認可の新設ではなく。理由は §含まないもの と [IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md)）。

## 設計

### 1. BFF: retry のみ admin へ絞る

グループの認可（admin または operator）は**そのまま**残し、`retry` エンドポイントにのみ
既存の名前付きポリシー `PlatformAuthPolicies.AdminOnly`（= `RequireRole(AdminRole)`。
[`AuthExtensions.cs`](../../src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs) 82-83 行で登録済み）を重ねる。

```csharp
g.MapPost("/{id:guid}/retry", ...)
 .WithName("BffConversionJobRetry")
 .RequireAuthorization(PlatformAuthPolicies.AdminOnly);
```

**効き方**: ASP.NET Core の `AuthorizationMiddleware` はエンドポイントの `IAuthorizeData` と
`AuthorizationPolicy` メタデータを `AuthorizationPolicy.CombineAsync` で**AND 合成**する。
よって実効要件は「(admin **または** operator) **かつ** admin」＝ **admin のみ**になる
（`RolesAuthorizationRequirement` が 2 つ並び、両方を満たす必要がある）。

**この形を選ぶ理由**（代替案は [IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) §検討した選択肢）:
グループ定義を触らないため、**照会の権限が巻き添えで変わらないことがコード上で自明**になる。
`/bff/authz`（[`AuthzBffEndpoints.cs`](../../src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/AuthzBffEndpoints.cs) 18 行）・
`/bff/dashboard`（[`DashboardBffEndpoints.cs`](../../src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DashboardBffEndpoints.cs) 71 行）と
**同じ名前付きポリシーを使う**ため、管理者限定の表現がリポジトリ内で 1 種類に保たれる。

### 2. 下流: 代償統制の機械検査

`NetworkIsolationTests.InternalAppServices` に `conversion-service` を加える
（`ports:` を足した瞬間に CI が落ちる）。**認可を足すのではなく、認可を課さない前提を固定する**。

### 3. 文書

- [IADR-0042](../adr/IADR-0042_conversion-job-read-model.md) 決定 3 に日付付き［追記］を入れ、
  **retry が「管理者・運用者に限定」の例外である**ことを記す（書式は
  [IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) の［2026-08-05 追記・適用範囲の明確化］に倣う）。
- [IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) を新規起票し、
  索引（[`docs/adr/README.md`](../adr/README.md)）へ登録する（採番は既存最大 IADR-0126 ＋ PR #508 が使用中の 0127 を避けて **0128**）。
- [画面仕様書 SC-07](../screens/SC-07_conversion-jobs.md) の認可表、
  [テスト仕様書 SC-07](../tests/SC-07_conversion-jobs.md) のケース表を追随させる。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト | 場所 |
| --- | --- | --- | --- |
| 1 | operator のトークンで `retry` を呼ぶと**拒否される** | `Retry_AsOperator_IsForbidden`（403） | `BffConversionEndpointTests.cs` |
| 2 | 無認証の `retry` は 401（存在秘匿の対象外・グループ既定どおり） | `Retry_WhenAnonymous_IsUnauthorized` | 同上 |
| 3 | admin は**従来どおり成功する** | `Retry_AsAdmin_Returns202`（既存・不変） | 同上 |
| 4 | `processing` 中の 409 `not_retryable` が**変わっていない** | `Retry_ProcessingJob_Returns409NotRetryable`（新規・本文の `error` まで検証） | `ConversionJobEndpointTests.cs` |
| 4b | 同 409 が BFF を素通りする | `Retry_WhenNotRetryable_Passes409Through` | `BffConversionEndpointTests.cs` |
| 5 | 照会の権限が**変わっていない** | `GetList_AsOperator_IsAllowed`（既存）＋ `GetById_AsOperator_IsAllowed`（新規） | 同上 |
| 6 | 下流の host 非公開が回帰しない | `InternalServices_MustNotPublishHostPorts`（`conversion-service` を追加） | `NetworkIsolationTests.cs` |

**#1 が本作業の要**である。権限テストが「admin で通ること」だけだと、実は誰でも通る状態を検出できない。

## 受け入れ基準（issue #501）

- [x] operator のトークンで `retry` を呼ぶと拒否される（テストで固定）
- [x] admin は成功する／`processing` 中は 409 のまま（テストで固定）
- [x] 照会（一覧・個別取得）の権限は変わっていない（グループ定義に差分なし＋テストで固定）
- [ ] `dotnet build` / `dotnet test` が両ユニットで green
      — **本セッションでは未実行**（.NET SDK 不在・取得不可。下記）
- [x] [IADR-0042](../adr/IADR-0042_conversion-job-read-model.md) 決定 3 に日付付き［追記］
- [x] `node scripts/check-doc-links.js` / `check-commit-messages.js` / `check-test-traceability.js` /
      `check-contract-schema.js` / `check-backend-libraries.js` が成功する

## 検証結果（実測）

| コマンド | 結果 |
| --- | --- |
| `dotnet build` / `dotnet test` / `dotnet format`（両ユニット） | **未実行**（`dotnet: command not found`。SDK 取得もプロキシに拒否され不可。下記「実行可否」） |
| `node scripts/check-doc-links.js` | 後述（PR 記載） |
| `node scripts/check-commit-messages.js --base origin/develop` | 後述 |
| `node scripts/check-test-traceability.js` | 後述 |
| `node scripts/check-contract-schema.js` | 後述 |
| `node scripts/check-backend-libraries.js` | 後述 |

### ビルド・テスト実行の可否（実測・隠さず記録する）

1. **SDK 不在**: `dotnet --version` は `dotnet: command not found`。`/usr/share/dotnet` /
   `/usr/lib/dotnet` / `~/.dotnet` のいずれも存在しない。
2. **取得不可**: `curl -L https://dot.net/v1/dotnet-install.sh` は 301 で
   `builds.dotnet.microsoft.com` へ向かい、そこでエージェントプロキシが
   `CONNECT tunnel failed, response 403` を返す（先行 [#486 の仕様書](./20260804_issue-486_bff-csproj-comment-iadr0117.md)
   §ビルド検証の実行可否と同じ拒否）。SDK を入れて実走する経路が無い。
3. **したがって「壊すと落ちる」の変異試験も実走できない。** 実行していないものを実行したとは書かない。
   代替として静的な根拠（下記）を示し、**最終判定は CI（`ci.yml` の backend ジョブ）に委ねる**。

### 変異試験の代替（静的な根拠）

「認可指定を元（admin+operator）へ戻すと新テストが落ちる」ことを、実走の代わりに次で担保する。

- `Retry_AsOperator_IsForbidden` は `X-Test-Roles: platform-operator` で POST する。
  `.RequireAuthorization(AdminOnly)` を**取り除けば**、残るのはグループの
  `RequireRole(AdminRole, OperatorRole)` だけになり、operator は要件を満たして **202 が返る**
  （同じ経路・同じスタブで `GetList_AsOperator_IsAllowed` が現に 200 を得ていることが、
  「operator は当該グループの認可を通過する」ことの実測である）。期待値 403 と一致しないため必ず fail する。
- 逆に `RequireRole(OperatorRole)` を消す等でグループを絞る改変を行えば、
  `GetList_AsOperator_IsAllowed` / `GetById_AsOperator_IsAllowed` が 403 で落ちる
  （**照会の巻き添えを検出する向きのテスト**）。この 2 方向でロールの境界が両側から固定される。
- `Retry_ProcessingJob_Returns409NotRetryable` は下流の状態遷移（`StartAsync` → `processing`）に依存し、
  認可の変更とは独立に 409 と `error: "not_retryable"` を検証する（回帰の向き）。

## フォローアップ（本 issue の範囲外）

1. **ConversionService へのアプリ層認証の要否**。現状はネットワーク分離（＋mTLS）に依存する。
   同 Namespace 内の任意 Pod からは到達できるため、ゼロトラストを徹底するなら別 issue で判断する
   （[IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) フォローアップ 1）。
2. `ingestion-service` も `NetworkIsolationTests` の列挙外である（同型の穴）。
3. **閲覧ロールの裁定**（planning#198 提案 8）が出たら、照会側の権限を追随させる。
4. **PR #508 とのマージ順**: 本 PR が先にマージされると、#508 マージまでの間だけ
   「画面には operator にも再変換ボタンが見えるが API は 403」という状態になる。
   計画は「API 側だけ緩い」ことを禁じており**逆向きの一時不整合は許容範囲**だが、
   親が調停する（報告に申し送り済み）。

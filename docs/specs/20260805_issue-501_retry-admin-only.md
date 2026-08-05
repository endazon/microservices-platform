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
| **照会（`GET /bff/conversion/jobs`・`GET /bff/conversion/jobs/{id}`）の権限** | 2026-08-04 の確定が命じたのは**再変換の実行権限**の是正である。照会については計画（[§共通シェル `01_screens.md:115`](../../planning/projects/microservices-platform/05_screens/01_screens.md)「SC-05/06/07 = 管理者（管理）」・§SC-07 `:250`「アクセス制御: 管理者ロール限定。」）が **SC-07 全体を管理者ロール限定**と定めており、現状の admin/operator は [IADR-0039](../adr/IADR-0039_datasource-management-bff-and-role-gating.md) 決定 1 由来の**既知の逸脱**である（未確定なのではない）。**その是正の向き（計画改訂か実装是正か）は planning#198 提案 8 で裁定を仰いでいる最中**であり、ここで併せて絞ると**裁定を待たずに実装が先に答えを出す**ことになる。 |
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
運用者が持つのは Keycloak のロールであってクラスタ内ネットワークではないため、
**BFF を絞れば「運用者が retry を実行できる」経路は塞がる**。残る到達手段は同 Namespace 内の別 Pod か
`kubectl port-forward` であり、それは Keycloak ロールではなく**クラスタ権限**の問題である
（[IADR-0026](../adr/IADR-0026_mesh-mtls-supersedes-network-isolation.md) の防御対象）。

### 3. ただし「compose の非公開」は機械で守られていない（実測）

[`NetworkIsolationTests.cs`](../../src/knowledge/backend/Tests/Knowledge.IntegrationTests/Deployment/NetworkIsolationTests.cs) の
`InternalAppServices` は 12 サービスを列挙するが、**`conversion-service` はその中に無い**。
すなわち上記 2 の「host 非公開」は**回帰ガードの外**にあり、誰かが `ports:` を足しても CI は沈黙する。
**同じことが本番系にも当てはまる**: 上記 2 の「Service は ClusterIP」を固定する検査も無く、
`templates/service.yaml` に `type: NodePort` を 1 行足せば全内部サービスが host 公開になる
（同ファイルは `.Values.services` 全件を 1 枚の `range` で描画するため影響は conversion に留まらない）。

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
**同じ名前付きポリシー（`PlatformAuthPolicies.AdminOnly`）を使う**ため、管理者限定の表現がリポジトリ内で
1 種類に保たれる。

**ただし、再利用しているのは「名前付きポリシー」であって「重ね掛けの形」ではない**（実測）。
`grep -rn "RequireAuthorization" --include=*.cs src/` の全 19 件を確認したところ、
**グループとエンドポイントの両方に認可を課している箇所は無く、本作業が初出**である。

| 箇所 | 実際の形 |
| --- | --- |
| `AuthzBffEndpoints.cs:18` | **グループにだけ** `AdminOnly`（エンドポイント側に認可は無い） |
| `DashboardBffEndpoints.cs:21` / `:71` | **グループには認可が無く**、`/summary` エンドポイントにだけ `AdminOnly` |
| `AuthzEndpoints.cs:28`（AuthorizationService） | 入れ子 `MapGroup("")` の**内側だけ**に `AdminOnly`（外側の `g` に認可は無い） |

したがって AND 合成の実効は「先例がそうなっているから」ではなく**テストで固定する**
（`Retry_AsOperator_IsForbidden` = 403 ／ `GetById_AsOperator_IsAllowed` = 200 の対）。

### 2. 下流: 代償統制の機械検査

**認可を足すのではなく、認可を課さない前提を固定する**。到達不能の論拠は 4 本（compose の host 非公開／
Helm Service が ClusterIP ／ NetworkPolicy の既定 deny ／ Istio VirtualService に経路なし）あり、
本作業は**そのうち 2 本**を機械検査へ載せる。

1. `NetworkIsolationTests.InternalAppServices` に `conversion-service` を加える
   （compose に `ports:` を足した瞬間に CI が落ちる）。
2. `InternalServices_HelmServicesMustStayClusterIp` を新設し、Helm の
   [`templates/service.yaml`](../../deploy/helm/microservices-platform/templates/service.yaml) に
   `type:` / `nodePort:` が現れないことを固定する。同ファイルは `.Values.services` 全件を 1 枚の
   `range` で描画するため、`type:` 不在＝全内部サービスが既定 ClusterIP であることを意味する。
   **`type` の変更は最も起こりやすい公開経路**であり、Helm 側を見る先例
   （`WikiJs_HelmIngressDisabledByDefault`）が同ファイルにある。

**残る 2 本（NetworkPolicy の例外追加・Istio VirtualService へのルート追加）は機械では止まらない**。
対象が conversion に限らず内部サービス全体であるため本 issue の射程を超える（フォローアップ 5）。

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
| 5b | 個別取得の無認証が 401（一覧側にしか無かった非対称の解消・AI レビュー指摘） | `GetById_WhenAnonymous_IsUnauthorized`（新規） | 同上 |
| 6 | 下流の host 非公開が回帰しない（compose） | `InternalServices_MustNotPublishHostPorts`（`conversion-service` を追加） | `NetworkIsolationTests.cs` |
| 6b | 本番系（Helm）の Service が外部公開型にならない | `InternalServices_HelmServicesMustStayClusterIp`（新規） | 同上 |

**#1 が本作業の要**である。権限テストが「admin で通ること」だけだと、実は誰でも通る状態を検出できない。

## 受け入れ基準（issue #501）

- [x] operator のトークンで `retry` を呼ぶと拒否される（テストで固定）
- [x] admin は成功する／`processing` 中は 409 のまま（テストで固定）
- [x] 照会（一覧・個別取得）の権限は変わっていない（グループ定義に差分なし＋テストで固定）
- [x] `dotnet build` / `dotnet test` が両ユニットで green（**実走**。実行環境は下記）
- [x] [IADR-0042](../adr/IADR-0042_conversion-job-read-model.md) 決定 3 に日付付き［追記］
- [x] `node scripts/check-doc-links.js` / `check-commit-messages.js` / `check-test-traceability.js` /
      `check-contract-schema.js` / `check-backend-libraries.js` が成功する

## 検証結果（実測）

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/knowledge/backend/backend.slnx` | Build succeeded / 0 Error(s)（警告 0。`Knowledge.IntegrationTests` の CS0618 は 2 回目以降の増分ビルドで再表示されない既存警告） |
| `dotnet test src/knowledge/backend/backend.slnx` | **11 アセンブリすべて Passed / Failed 0**。`ConversionService.Worker.Tests` 54 件（+1 = 新規 409 回帰）／`Knowledge.IntegrationTests` 20 件 passed・18 skipped（Testcontainers 系はコンテナ内 docker 不在で skip。`NetworkIsolationTests` は passed 側） |
| `dotnet build src/platform/backend/backend.slnx` | Build succeeded / 0 Error(s) / 0 Warning(s) |
| `dotnet test src/platform/backend/backend.slnx` | **3 アセンブリすべて Passed / Failed 0**。`Platform.Bff.Tests` 147 passed / 1 skipped（既存のベンチマーク由来の skip。`BffConversionEndpointTests` は 14 件すべて passed = 既存 10 + 新規 4） |
| `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` | exit 0 |
| `dotnet format src/platform/backend/backend.slnx --verify-no-changes` | exit 0 |
| `node scripts/check-doc-links.js` | exit 0（415 件の Markdown。未 populate submodule 配下 2 件は対象外） |
| `node scripts/check-commit-messages.js --base origin/develop` | exit 0（2 件すべて規約適合） |
| `node scripts/check-test-traceability.js` | exit 0（仕様書のある起点 ID 28 件中 28 件が写像済み） |
| `node scripts/check-contract-schema.js` | exit 0（2 プロジェクト / 20 ファイル / 56 型が baseline と一致） |
| `node scripts/check-backend-libraries.js` | exit 0（新規混入 0 件。既知残件 42 件は baseline 済み） |
| `node scripts/check-unit-dependencies.js` | exit 0 |

### 実行環境（実走できた経路・再現条件）

本セッションのホストに .NET SDK は無く（`dotnet: command not found`。`/usr/share/dotnet` /
`/usr/lib/dotnet` / `~/.dotnet` のいずれも不在）、`dotnet-install.sh` の取得も
`builds.dotnet.microsoft.com` へのプロキシ拒否（`CONNECT tunnel failed, response 403`）で不可能だった
（先行 [#486 の仕様書](./20260804_issue-486_bff-csproj-comment-iadr0117.md) §ビルド検証の実行可否と同じ拒否）。
代わりに **`mcr.microsoft.com/dotnet/sdk:10.0` コンテナ（SDK 10.0.302）**で実走した。

- 再現条件: `docker run --rm --network host -v <worktree>:/w -w /w -v <ca-bundle>:/etc/ssl/certs/ccr-ca.crt:ro
  -e SSL_CERT_FILE=/etc/ssl/certs/ccr-ca.crt -e HTTPS_PROXY=... mcr.microsoft.com/dotnet/sdk:10.0 …`
  （NuGet 復元はプロキシ経由。`--network host` はプロキシが `127.0.0.1` で待ち受けるため必要）。
- **platform ユニットのビルドには `src/ai-stock-trading` submodule の populate が必要**である
  （`Platform.Bff` → `AiStockTrading.Bff.Endpoints` の ProjectReference）。pin（`655e2ed`）のまま
  `git submodule update --init` で取得しており、**pin は変更していない**。

### 変異試験（「壊すと落ちる」の実測）

権限テストは「admin で通ること」だけでは**誰でも通る状態を検出できない**。そこで実装を意図的に壊し、
テストが落ちることを実測した（3 種・いずれも実行後に復元して green を再確認済み）。

| # | 変異（意図的な退行） | 期待 | **実測結果** |
| --- | --- | --- | --- |
| 1 | `retry` から `.RequireAuthorization(PlatformAuthPolicies.AdminOnly)` を削除（= 是正前の admin+operator へ戻す） | `Retry_AsOperator_IsForbidden` が落ちる | **落ちた**。`Expected resp.StatusCode to be HttpStatusCode.Forbidden {value: 403}, but found HttpStatusCode.Accepted {value: 202}`（Failed 1 / Passed 13）。**運用者が実際に再変換を実行できていた**ことの直接の証拠でもある |
| 2 | グループの `RequireRole` から `OperatorRole` を削除（= 照会まで admin へ絞る巻き添え） | 照会側の 2 件が落ちる | **落ちた**。`GetList_AsOperator_IsAllowed` / `GetById_AsOperator_IsAllowed` がいずれも `Expected … OK {value: 200}, but found … Forbidden {value: 403}`（Failed 2 / Passed 12）。**裁定を仰いでいる最中の閲覧権限を巻き添えで変えたら気付ける** |
| 3 | 後段の再変換不可（409 `not_retryable`）を 202 に置換 | `Retry_ProcessingJob_Returns409NotRetryable` が落ちる | **落ちた**。同テストと既存の `Retry_NonFailedJob_Returns409` がともに `Expected … Conflict {value: 409}, but found … Accepted {value: 202}`（Failed 2 / Passed 5） |

**補足（変異 3 の途中経過も記録する）**: 最初は「エンドポイントの `if (job.Status != Failed) → 409` の
1 行だけ」を削る変異を試したが、**テストは落ちなかった**（Passed 7）。`PrepareRetryAsync` が非失敗ジョブに
`null` を返し、後続の 409 分岐が同じ応答を出すためである（**多層になっていた**）。よって
「409 を返す経路をすべて 202 に置換する」変異へ強めたのが上表 3 である。
1 回目の変異が落ちなかった事実は、テストの弱さではなく**実装側の冗長な防御**を示す。

**復元の確認**: 3 種の変異はいずれも直後に元へ戻し、最終状態で両ユニットの build / test / format が
上表のとおり green であることを再実行して確認した（`git status` はクリーン）。

## フォローアップ（本 issue の範囲外）

1. **ConversionService へのアプリ層認証の要否**。現状はネットワーク分離（＋mTLS）に依存する。
   同 Namespace 内の任意 Pod からは到達できるため、ゼロトラストを徹底するなら別 issue で判断する
   （[IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) フォローアップ 1）。
2. `ingestion-service` も `NetworkIsolationTests` の列挙外である（同型の穴）。今回入れないのは、
   HTTP サーフェスが `MapPlatformIntrospection()` 1 件のみで副作用のある操作を持たないためである。
   **「公開してよい」の意味ではない**ことを `NetworkIsolationTests` の列挙にもコメントで残した。
3. **閲覧ロールの差異の裁定**（planning#198 提案 8。**計画は SC-07 全体を管理者限定と定め、実装は
   admin/operator** という [IADR-0039](../adr/IADR-0039_datasource-management-bff-and-role-gating.md) 決定 1 由来の
   既知の逸脱）が出たら、計画改訂・実装是正のいずれであれ照会側の権限を追随させる（SC-05・SC-06 も同じ適用先）。
4. **PR #508 とのマージ順**: 本 PR が先にマージされると、#508 マージまでの間だけ
   「画面には operator にも再変換ボタンが見えるが API は 403」という状態になる。
   計画は「API 側だけ緩い」ことを禁じており**逆向きの一時不整合は許容範囲**だが、
   親が調停する（報告に申し送り済み）。
5. **代償統制の残り 2 本の機械化**（NetworkPolicy への `istio-system` 例外追加・Istio VirtualService への
   内部サービス向けルート追加）。いずれも「BFF 以外の公開エッジを作る」変更であり、対象は conversion に
   限らず内部サービス全体である（[IADR-0128](../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md) フォローアップ 4）。

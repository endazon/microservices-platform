---
title: 作業仕様書 — 統合テスト器へ IdentityAdmin:Provider を注入し、AuthorizationService の起動退行を止める（#1044）
type: spec
status: done
related_ids:
  - FR-05
  - FR-09
  - SC-17
  - ADR-0004
  - IADR-0232
  - IADR-0301
author: implementation-agent
created: 2026-08-29
updated: 2026-08-29
---

# 作業仕様書 — 統合テスト器へ `IdentityAdmin:Provider` を注入する（#1044）

## 1. 事実（実測）

`develop` の **Integration** ワークフロー run 33195657772（コミット `66f1778` ＝ PR #1043 のスカッシュ）が失敗した。

```
Total tests: 75  Passed: 71  Failed: 3  Skipped: 1
Knowledge.IntegrationTests.AuthorizationService.AbacScopeTests.*
  System.InvalidOperationException : IdentityAdmin:Provider が未設定である
    at AuthorizationService.Infrastructure.ExternalServices.IdentityAdminRegistration
         .AddIdentityAdminClient(...) IdentityAdminRegistration.cs:line 28
    at Program.<Main>$(String[] args) AuthorizationService/Program.cs:line 41
    at ... WebApplicationFactory`1.CreateClient()
    at Knowledge.IntegrationTests.AuthorizationService.AbacScopeTests.InitializeAsync()
```

**原因は自分の変更である。** PR #1043 の SC-17（IADR-0301 決定 3）で
`IdentityAdmin:Provider` を**既定なしの必須宣言**にした。配備側（compose / helm）と
`AuthorizationService.Tests`（`TestDatabaseConfiguration` の `[ModuleInitializer]` が
環境変数で注入）には手当てをしたが、**`Knowledge.IntegrationTests` の
`AuthorizationServiceFactory` にだけ手当てが無かった**。

PR の `ci` が緑だったのは設計どおりである —— 統合テストは
`--filter "Category!=Integration"` で PR から外され、`develop` への push で
Integration が回収する（IADR-0232 決定 1）。**「PR が緑でもここが赤ければ、その退行は入っている」**
という同ワークフローの前提が、そのとおりに働いた。

### 同型の事故は 3 度目である

同じファイル（`Fixtures/IntegrationTestFactory.cs`）のコメントが、既に 2 件を記録している。

| # | 鍵 | issue | 現れ方 |
| --- | --- | --- | --- |
| 1 | `Pipeline:ConfigPath` | #455 U0d | `ConfigureAppConfiguration` では読み取りに間に合わず、宣言が 1 行も読まれないまま緑 |
| 2 | `RabbitMq:ConnectionString` | ADR-0027 / #441 E1・#1022 | ホスト構築時に読まれるため間に合わず、既定値への接続失敗 → 構成未注入例外 |
| 3 | `ConnectionStrings:DefaultConnection` | #1032 | `develop` の Integration で 28 件が赤 |
| **4** | **`IdentityAdmin:Provider`** | **本件 #1044** | **`develop` の Integration で 3 件が赤** |

3・4 は「**器が鍵を与えていない**」であり、1・2 は「**与える時点が遅い**」である。
本件は 3 と同型（与えていない）で、直し方も同じ —— **`UseSetting`**（ホスト構成。
`CreateBuilder` が構成を組む時点から見える）である。`ConfigureAppConfiguration` は
トップレベル文の読み取りに間に合わない。

## 2. 母集合（自分で引いた。除外理由つき）

**問い**: 「本器が起こすサービスのうち、起動時に必須の構成鍵を与えられていないものは他に無いか」。

### 軸 1 —— 例外メッセージの規約（`… が未設定である`）で全走査

```
$ git grep -hoE '"?([A-Za-z][A-Za-z0-9]*(:[A-Za-z][A-Za-z0-9]*)+)[^"]{0,40} が未設定である' \
    -- 'src/*/backend/**/*.cs' ':!src/ai-stock-trading' | sed 's/ が未設定である//' | sort -u
"ConnectionStrings:DefaultConnection
"IdentityAdmin:Keycloak:{key}
"RabbitMq:ConnectionString
RabbitMq:ConnectionString
```

🔴 **この軸は本件を取りこぼす。** `IdentityAdminRegistration` のメッセージは
`$"{ProviderKey} が未設定である"` と**補間**しており、鍵の文字列がソースに現れない
（`IdentityAdmin:Keycloak:{key}` が同じ理由で壊れた形で出ている）。
**メッセージ本文を当てにした走査は、補間のある実装に穴を空ける。**
母集合規則 2（あり得る形をすべて列挙してから引く）の破れである。

### 軸 2 —— 「構成を読み、かつ throw する非テストコード」で全走査

```
$ for f in $(git ls-files 'src/*/backend/**/*.cs' | grep -v "/Tests/" | grep -v "^src/ai-stock-trading"); do
    if grep -q "throw new InvalidOperationException" "$f" \
       && grep -qE "configuration\[|Configuration\[|GetConnectionString|GetValue<|GetSection" "$f"; then echo "$f"; fi
  done
```

17 件。うち `Platform.Bff.Tests/ConfigVersionHistoryBindingTests.cs` はテスト（パス除外が
`/Tests/` 表記のため漏れた。**除外理由: テストコードであり起動経路ではない**）。
残る 16 件のうち、**本器が起こす 7 サービス**（下表）に属するものだけが本件の対象である。

| 本器が起こすサービス | ファクトリ | 必須鍵 | 器の手当て |
| --- | --- | --- | --- |
| DocumentService | `DocumentServiceFactory` | DefaultConnection / RabbitMq / Pipeline | ✔ |
| DataSourceService | `DataSourceServiceFactory` | DefaultConnection / RabbitMq | ✔ |
| WikiService | `WikiServiceFactory` | DefaultConnection / RabbitMq | ✔ |
| IngestionService.Worker | `IngestionServiceFactory` | RabbitMq | ✔ |
| ConversionService.Worker | `ConversionServiceFactory` | DefaultConnection / RabbitMq | ✔（死蔵。参照テスト無し） |
| GraphService | `GraphServiceFactory` | DefaultConnection / RabbitMq | ✔ |
| **AuthorizationService** | `AuthorizationServiceFactory` | DefaultConnection / **IdentityAdmin:Provider** | **✘ 本件** |

**除外**: `DashboardService` / `FeedbackService` / `RetrievalService` / `McpServer` /
`NotificationService` は**本器にファクトリが無く、起こされていない**（`git grep` で
`IntegrationTestFactoryBase` の派生を全数列挙して確認）。器の欠落は起こり得ない。
`Platform.Shared.Infrastructure` の 2 件（`ConfigInspectionExtensions` /
`PipelineExtensions`）は全サービス共通の経路で、既に `Pipeline:ConfigPath` が手当て済みである。

### 軸 3 —— `IdentityAdmin` の全走査（追随先の確認）

```
$ git grep -n "IdentityAdmin" -- . ':!src/ai-stock-trading'
```

配備側は両方とも手当て済みである（`deploy/docker-compose.yml:388` /
`deploy/helm/microservices-platform/values.yaml:351`）。単体テスト側も
`AuthorizationService/Tests/TestDatabaseConfiguration.cs:25` が環境変数で注入している。
**欠けていたのは統合テスト器の 1 箇所だけ**である。

## 3. 直し方

`AuthorizationServiceFactory` が `ConfigureWebHost` を上書きし、`UseSetting` で
`IdentityAdmin:Provider = in-memory` を与える。

- **`UseSetting` である理由**: `Program.cs` は `builder.Services.AddIdentityAdminClient(builder.Configuration)` を
  **トップレベル文で**評価する。`ConfigureAppConfiguration` で足した値が見えるのはその後であり、
  読み取りに間に合わない（同ファイルが 3 度記録している罠。#455 U0d / #1022 / #1032）。
- **値が `in-memory` である理由**: 統合テストは実 IdP を持たない。**偽物であることを明示的に宣言する**
  （既定では選ばれない。IADR-0301 決定 3）。単体テスト側の
  `TestDatabaseConfiguration` と同じ判断である。
- **基底ではなく派生に置く理由**: `IdentityAdmin:Provider` は AuthorizationService 固有の鍵であり、
  基底へ置くと「全サービス共通の配線」に見える。基底が持つのは全サービスに効く 3 鍵だけに保つ。

## 4. 検出力の証拠

🔴 **本環境では統合テストを実走できない**（`docker info` が失敗する＝ Testcontainers 不可）。
「実データで緑」は元より検出力の証拠にならないが、**ここでは緑すら測れない**。
そこで**変異試験を、Docker を要らない層で**行う。

`AbacScopeTests` は Postgres が無いと `InitializeAsync` が早期 return するため、
本環境では**この退行を再現できない**。再現できる層まで下げる:

- **`AuthorizationHostBootTests`（新規・`Category=EndpointRouting`。Docker 非依存）** —
  `AuthorizationServiceFactory` が起こすホストが**実際に構築でき**、`IIdentityAdminClient` が
  `InMemoryIdentityAdminClient` へ解決されることを検査する。
- 変異: `UseSetting` の行を消す（M1）、値を `keycloak` に変える（M2）、
  `UseSetting` を `ConfigureAppConfiguration` に変える（M3）。

> 🔴 **［2026-08-29 追記 / #1044］起草時の設計から 2 点変えた。** 当初ここは
> 「**ホストを起こさずに**構成の面だけを見る」「クラス名は `AuthorizationHostConfigurationTests`」と
> 書いていた。**ホストを起こさない案は採れなかった** —— `WebApplicationFactory` は構成だけを
> 取り出す口を持たず、`ConfigureWebHost` は `protected` なので外から呼べない。
> 代わりに **DbContext を InMemory へ差し替えて起動時 `MigrateAsync` を迂回**すれば
> （`IsRelational()` が false になる）、**実 DB もブローカも無しでホスト構築だけを通せる**ことが分かった。
> こちらのほうが検出力が高い —— 構成の有無ではなく**起動が通るかどうか**を直接測る。
> 変異も M3 を足して 3 種にした（下の実測表が正である）。

**PR CI（`Category!=Integration`）で走る**ので、次に同じ鍵が抜けたら**マージ前に**赤くなる。
Integration の実走緑は `develop` への push 後にしか得られない —— **その結果が出るまで #1044 は閉じない。**

### 実測（`dotnet test --filter FullyQualifiedName~AuthorizationHostBootTests`）

🔴 **`AuthorizationHostBootTests` は Docker を要らない。** DbContext を InMemory へ差し替えることで
`Program.cs` の起動時 `MigrateAsync` を迂回し（`IsRelational()` が false）、**実 DB もブローカも無しで
ホスト構築だけを通す**。そのため本環境でも変異試験を実走できた。

| # | 変異 | 結果 | 落ちたときの例外 |
| --- | --- | --- | --- |
| M0 | 無変異（ベースライン対照） | **Passed 2 / Failed 0** | —— |
| M1 | `UseSetting` の 1 行を消す | **KILL**（Failed 1） | `IdentityAdmin:Provider が未設定である` |
| M2 | 値を `in-memory` → `keycloak` | **KILL**（Failed 2） | `IdentityAdmin:Keycloak:BaseUrl が未設定である` |
| M3 | `UseSetting` → `ConfigureAppConfiguration` | **KILL**（Failed 1） | `IdentityAdmin:Provider が未設定である` |

🔴 **M1 の例外は `develop` の Integration が出したものと同一である** ——
本テストが**この退行そのもの**を、コンテナ無しで再現していることの証拠である。

🔴 **M3 は「読まれる時点で決まる」を測定として確かめたものである。** 同ファイルは 3 度この罠を
記録しているが、いずれも**事故として**の記録だった。M3 は
`ConfigureAppConfiguration` がトップレベル文の読み取りに**間に合わないこと**を、
主張ではなく実測で固定する。

### 併走の実測

```
$ dotnet test src/knowledge/backend/backend.slnx --filter "Category!=Integration"   # CI と同じフィルタ
Knowledge.Contracts.Tests 27 / AiAnalysisService 95 / IngestionService.Worker 28 / GraphService 275 /
DataSourceService 166 / FeedbackService 21 / DashboardService 30 / RetrievalService 137 /
ConversionService.Worker 79(+2 skip) / WikiService 64 / DocumentService 225 /
Knowledge.IntegrationTests 35   → **すべて Passed / Failed 0**
$ dotnet format src/knowledge/backend/backend.slnx --verify-no-changes   → 差分なし
```

## 5. 触るファイル

- `src/knowledge/backend/Tests/Knowledge.IntegrationTests/Fixtures/IntegrationTestFactory.cs`（`AuthorizationServiceFactory`）
- `src/knowledge/backend/Tests/Knowledge.IntegrationTests/AuthorizationService/AuthorizationHostBootTests.cs`（新規）

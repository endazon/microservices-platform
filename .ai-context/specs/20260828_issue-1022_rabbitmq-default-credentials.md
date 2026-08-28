---
title: 作業仕様書 — RabbitMQ の既定資格情報を撤去し未注入を起動失敗にする（#1022）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0002
  - ADR-0003
  - ADR-0027
related_adrs:
  - IADR-0286
  - IADR-0291
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "planning:projects/microservices-platform/02_requirements/01_requirements.md（非機能要件・セキュリティ）"
---

# 作業仕様書: RabbitMQ の既定資格情報の撤去（#1022）

## 起点

issue #1022。#1012 が DB 接続文字列で行った是正（既定資格情報の撤去＋未注入の fail-fast 化）の
**RabbitMQ 側の残件**である。`scripts/default-credentials-baseline.json` に 13 箇所が凍結されている。

```csharp
var rabbit = builder.Configuration["RabbitMq:ConnectionString"]
    ?? "amqp://guest:guest@rabbitmq:5672";
```

## 母集合の実測（2026-08-28・head 35ea0b1）

**issue 本文の「13 箇所」を母集合にしていない**（規則 1・2）。軸を 5 本引いた。

| 軸 | 走査 | 実測 |
| --- | --- | --- |
| 1 | `node scripts/check-default-credentials.js` | OK・走査 30 件・**既知 13 件**（baseline と一致） |
| 2 | `grep -rn 'amqp://' --include=*.cs --include=*.json src/`（submodule・bin/obj 除く） | `Program.cs` **7 件**／本番 `appsettings.json` **6 件** ＝ **13 件**。`appsettings.Development.json` に別途 4 件（検査器の対象外） |
| 3 | `grep -rn 'RabbitMq' src/platform/backend`（**#1025 で増えた NotificationService を含む**） | 🔴 **0 件**。platform ユニットは RabbitMQ を一切読まない。**#1025 の配備で増えていない** |
| 4 | `deploy/**` の `amqp://` / `RabbitMq` | compose の anchor `x-rabbit-env` **1 行**／helm は **0 件**（注入していない）／`deploy/local/infra/rabbitmq.yaml`（ブローカ本体） |
| 5 | 🔴 **submodule（AST）の配備物** | `src/ai-stock-trading/deploy/helm/ai-stock-trading/values.yaml` が `global.rabbitmqConnectionString: amqp://guest:guest@rabbitmq:5672` を**自前で持ち**、同 chart の `deployment.yaml` が `RabbitMq__ConnectionString` として注入する。compose でも AST 3 サービスが MSP の `*rabbit-env` を継ぐ |

**結論: 13 件のままである（増減なし）。** `Program.cs` 7（conversion / datasource / document / graph /
ingestion / retrieval / wiki）＋本番 `appsettings.json` 6（aianalysis / conversion / datasource /
document / ingestion / wiki）。`graph` と `retrieval` は `appsettings.json` を持たず**コード既定だけ**が
供給源であり、`aianalysis` は逆に `appsettings.json` を持つが **`RabbitMq:ConnectionString` を一度も
読まない**（死んだ設定。#1012 の AiAnalysis の DB と同型）。

## 🔴 着手前の実測で覆った前提（issue 記述の訂正）

### 訂正 1 — ブローカの dev 既定 `guest:guest` は変えられない（AST が握っている）

issue の手順 1 は「ブローカの資格情報を dev 既定から分離する」と書いており、素直に読めば
**dev 既定値そのものを `guest` 以外へ変える**ことになる。**軸 5 でこれは棄却された。**

共有 RabbitMQ（`platform-infra` ns の 1 実体。MSP ns / AST ns の ExternalName alias が同じ実体を指す）へは
**AST の 3 サービスも接続する**。AST chart は自分の values に `amqp://guest:guest@rabbitmq:5672` を
**ハードコードで持っており**、submodule は本リポジトリの規約の対象外（不変）である。
ブローカの利用者名／パスワードを変えると、**AST 側がローカル k8s で認証エラーになる。**

したがって #1012 が `kp/kp` を **dev 既定として残したまま**「イメージから撤去し Secret 経由の注入へ
移した」のと**同じ形**を採る —— **dev 既定の値は `guest`/`guest` のまま**、供給元を Secret と env へ移し、
**コードとイメージからは消す**。受け入れ基準「ブローカ側の資格情報が Secret 由来になる（dev 既定は
env で上書き可）」はこれで満たす。値を変えることは基準に含まれていない。

### 訂正 2 — k8s のブローカは既に半分 Secret 由来である

`deploy/local/infra/rabbitmq.yaml` は `RABBITMQ_DEFAULT_PASS` を Secret `rabbitmq`（`platform-infra` ns・
key=`password`）から取っている。**未対応は利用者名（`value: guest` のリテラル）と、compose 側（利用者名・
パスワードとも素のリテラルで env 上書きも効かない）である。** さらに **app 側（MSP ns）には
接続文字列を組むための Secret が 1 つも無い**。

### 訂正 3 — テスト器の `ConfigureAppConfiguration` は**既に効いていない**

各サービスの単体テスト器（`TestWebApplicationFactory`）は
`["RabbitMq:ConnectionString"] = "amqp://localhost"` を `ConfigureAppConfiguration` で入れているが、
`Program.cs` はトップレベル文で `builder.Configuration["RabbitMq:ConnectionString"]` を読むため
**間に合っていない**（実際に効いていたのは `appsettings.json` の `amqp://guest:guest@rabbitmq:5672`）。
実ブローカへ繋ぎに行かないのは `DisableAllExternalWolverineTransports()` が効いているからであって、
上書きが効いているからではない。**既定を外すとこの器は全滅する** —— #1012 の
`TestDatabaseConfiguration.cs` と同型の `[ModuleInitializer]` ＋ 環境変数へ移す。

**統合テスト器（`Knowledge.IntegrationTests`）は別**である。あちらは `builder.UseSetting(...)` を
使っており（ホスト構成なので `CreateBuilder` の時点から見える）**今も効いている**。触らない。

## 対象と除外

| 区分 | 対象 | 理由 |
| --- | --- | --- |
| ✅ | 本番 `appsettings.json` 6 件から `RabbitMq` セクションを撤去 | イメージへ焼かれる本番既定であり、注入漏れを隠す（IADR-0286 決定 1） |
| ✅ | `Program.cs` 7 件を **1 サービス 1 解決点 ＋ `?? throw`** へ | 撤去後に「未設定で起動成功」へ倒れないため（IADR-0286 決定 2） |
| ✅ | helm の注入（`global.messaging` ＋ per-service `messaging: true`） | k8s は**コード既定に意図的に依存**していた（values.yaml の注記）。配備側の注入が先（IADR-0286 決定 3） |
| ✅ | `k8s-local-up.sh`: MSP ns の Secret `rabbitmq-app` 作成、infra ns の `rabbitmq` へ `username` 追加 | helm の Secret 参照先を用意し、ブローカ側の資格情報を Secret 由来にする |
| ✅ | 🔴 **ESO 経路（`externalsecret-rabbitmq-app.yaml` ＋ Vault seed ＋ apply ＋ 対の試験）** | **#1012 が事後に踏んだ欠陥の再発防止**（IADR-0286 の 2026-08-28 追記）。「配備側」は手動 apply と ESO の 2 本ある |
| ✅ | compose: `x-rabbit-env` と `rabbitmq` サービスを env 上書き可能に | compose も「Secret 相当（`.env`）由来」にする |
| ✅ | テスト器 7 プロジェクトへ `TestRabbitMqConfiguration.cs`（`[ModuleInitializer]` ＋ 環境変数） | 訂正 3。**実配備と同じ経路**で注入する（IADR-0286 決定 4） |
| ✅ | baseline を `--update` で 0 件へ縮める ＋ `$comment` と `scripts/README.md` の追随 | 前方一方向のラチェット（IADR-0286 決定 5） |
| ✅ | 規則 10 の引き直し（「コード既定が in-cluster DNS で解決される」等、**この変更で偽になる自分の記述**） | 是正のたびに全走査で引き直す |
| ⛔ | `appsettings.Development.json` 4 件 | **イメージの本番既定ではない**（`dotnet run` のローカル利便）。IADR-0286 決定 1 と同じ判断。値も変えない（訂正 1 によりブローカの dev 既定は不変） |
| ⛔ | dev 既定値そのものの変更（`guest` → 別名） | 訂正 1。AST が壊れる。**申し送りへ回す** |
| ⛔ | AST（`src/ai-stock-trading`）の chart / appsettings | submodule 不変の規約。helm の該当サービス（`risk-management` / `market-monitor` / `configuration`）へ `messaging` を足さない（既定 `enabled: false`・挙動不変） |
| ⛔ | `aianalysis` への helm 注入 | `RabbitMq:ConnectionString` を読まない（軸 2）。`appsettings.json` の撤去だけで足りる |
| ⛔ | 統合テスト器（`IntegrationTestFactory` / `GraphServiceFactory`）の配線 | 訂正 3。`UseSetting` は今も効く。**注記だけ引き直す** |
| ⛔ | `rabbitmq-app` の `eso_wait` / rollout 追加 | `postgres-app`（#1012）も入っていない**既存の穴**であり、本 issue で片方だけ直すと非対称になる。申し送りへ |

## 設計

### 1. コード（fail-fast・1 サービス 1 解決点）

```csharp
// NFR, #1022: 既定資格情報を置かない。未注入は起動時に落とす（#1012 / IADR-0286 と同型）。
var rabbitConnection = builder.Configuration["RabbitMq:ConnectionString"]
    ?? throw new InvalidOperationException(
        "RabbitMq:ConnectionString が未設定である。環境変数 RabbitMq__ConnectionString で注入すること"
        + "（既定の資格情報は持たない。#1022 / IADR-0291）。");
```

`graph` / `retrieval` / `datasource` は `UseRabbitMq(new Uri(...))` の**引数の中で**読んでいたので、
`UseWolverine` より前へ巻き上げて 1 解決点にする（IADR-0286 決定 2 と同形）。

**「接続失敗」と「構成未注入」を読み分けられること**（issue の要求）: 未注入は
`InvalidOperationException`（メッセージにキー名と注入手段）、繋ぎ先はあるが届かないときは Wolverine の
`BrokerInitializationException` —— **型もメッセージも重ならない**。従来は未注入が
「`rabbitmq` という名前が引けない接続失敗」に化けていた。

### 2. 配備（**#1012 の `global.db` と同型**）

```yaml
global:
  messaging:
    host: rabbitmq
    port: 5672
    user: guest
    existingSecret: rabbitmq-app
    passwordKey: password
```

`deployment.yaml` は `$svc.messaging` が真のサービスにだけ描画する（`$svc.database` と同型）:

```yaml
- name: RABBITMQ_PASSWORD
  valueFrom: { secretKeyRef: { name: <existingSecret>, key: <passwordKey> } }
- name: RabbitMq__ConnectionString
  value: "amqp://<user>:$(RABBITMQ_PASSWORD)@<host>:<port>"
```

`$(VAR)` は**同一 container 内で先に定義した env しか参照できない** —— 順序を崩さない（IADR-0286 決定 3）。
対象は `RabbitMq:ConnectionString` を読む 7 サービス（document / datasource / conversion / ingestion /
retrieval / wiki / graph）。

### 3. 供給経路は 2 本とも塞ぐ

| 経路 | ブローカ側（`platform-infra` ns・Secret `rabbitmq`） | app 側（MSP ns・Secret `rabbitmq-app`） |
| --- | --- | --- |
| 既定（`ESO` 未設定） | `k8s-local-up.sh` step 3 の手動 apply（`username` / `password`。**ESO=1 でもスキップしない**＝bootstrap 必須） | step 5 の `ESO != 1` ブロックで手動 apply |
| `ESO=1` | 既存 `externalsecret-rabbitmq.yaml`（`creationPolicy: Merge`）＋ **`username` を追加** | **新設 `externalsecret-rabbitmq-app.yaml`（`Owner`）** |
| compose | `rabbitmq` サービスの `RABBITMQ_DEFAULT_USER/PASS` を `${RABBITMQ_USER:-guest}` / `${RABBITMQ_PASSWORD:-guest}` へ | `x-rabbit-env` を同じ変数から組む |

### 4. テスト器

`TestRabbitMqConfiguration.cs`（`[ModuleInitializer]` で `RabbitMq__ConnectionString` を
**資格情報を持たない到達不能な値** `amqp://localhost:5672` に設定）を、ホストを起こす 7 テスト
プロジェクトへ置く。既存の `ConfigureAppConfiguration` 側の `RabbitMq:ConnectionString` は
**効いていなかった**ので撤去する（2 つ置くと「どちらが効いているか」が分からなくなる）。

## 受け入れ基準

- [ ] `Program.cs` / 本番 `appsettings.json` に `amqp://user:pass@` が残らない（**baseline が 0 件**）
- [ ] 未注入で起動が落ちる（**変異試験で実測する**）
- [ ] compose・k8s・テストのいずれも壊れない
- [ ] ブローカ側の資格情報が Secret 由来になる（dev 既定は env で上書き可）
- [ ] 「接続失敗」と「構成未注入」が型とメッセージで読み分けられる
- [ ] ESO=1 でも app 側 Secret の供給元が在る（対の試験 2 本）

## テスト方針

- `scripts/k8s-local-up.test.js` へ `rabbitmq-app` の対 2 本（ESO=1 で ExternalSecret を apply し手動 apply
  はしない／既定では手動 apply する）と、ブローカ Secret の `username` 追加の 1 本。
- fail-fast そのものは**常設テストにしない** —— 環境変数はプロセス全体で共有され、xUnit のクラス並列
  実行下で一時的に消す試験は他クラスを巻き添えにする。#1012 と同じく**変異試験で実測**して記録する。

## 実施の記録（実測）

| 実行 | 結果 |
| --- | --- |
| `dotnet build`（両 slnx） | 0 Error |
| `dotnet test src/knowledge/backend/backend.slnx` | **全 12 プロジェクト緑**（AiAnalysis 95 / Dashboard 26 / DataSource 136 / Conversion 74+2skip / Feedback 21 / Ingestion 28 / Graph 250 / Document 207 / Retrieval 131 / Contracts 27 / Integration 30+40skip / Wiki 64） |
| `dotnet test src/platform/backend/backend.slnx` | **全 7 プロジェクト緑**（McpServer 66 / Authz 95 / LlmGateway 202 / Shared.Infrastructure 170 / Shared.Kernel 42 / Bff 404+1skip / Notification 53） |
| `dotnet format --verify-no-changes`（両 slnx） | 差分なし（exit 0） |
| `node scripts/check-default-credentials.js` | **OK・既知の残件 0 件**（走査 30 件）。`--self-test` 6 件緑 |
| `node scripts/k8s-local-up.test.js` | **107 件緑**（104 → 107。#1022 で 3 本追加） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **645 件緑** |
| `check-trace-blocks` / `check-doc-links` / `check-adr-numbering` / `check-doc-updated` | いずれも OK |

## 変異試験（実測）

| 変異 | 実測 |
| --- | --- |
| **M1**: `GraphService/Tests/TestRabbitMqConfiguration.cs` の注入キーを `RabbitMq__MUTATED` へ（＝未注入の再現） | **250 件中 95 件が `InvalidOperationException: RabbitMq:ConnectionString が未設定である` で失敗**。戻すと 250 件緑 —— 未注入が「起動失敗」へ倒れることと、注入すれば起動することの両方向を実測 |
| **M2**: `GraphService/Program.cs` へ既定値 `amqp://guest:guest@rabbitmq:5672` を戻す | **`check-default-credentials` が `[added] … [amqp-credentials]` で exit 1**。戻すと OK |
| **M3**: `k8s-local-up.sh` から `externalsecret-rabbitmq-app.yaml` の apply を落とす | `externalsecret-rabbitmq-app.yaml が apply されない…` で fail |
| **M4**: `rabbitmq-app` の手動 apply を `ESO != 1` ブロックの外へ出す | `ESO=1 なのに rabbitmq-app を手動 apply している（二重所有）` で fail |
| **M5**: 基盤 secret `rabbitmq` から `username=` を落とす | `rabbitmq Secret に username が無い` で fail |
| **陰性対照** | 上記いずれも変異を戻して緑への復帰を確認した |

## 🔴 併走で見つかった同型の欠陥（#1032・別コミット）

本作業中に `develop` の `integration.yml` が 28 件失敗していることが判明した（Total 70 /
Passed 41 / Failed 28 / Skipped 1）。**#1012 が本仕様書の「訂正 3」とまったく同じ罠を踏んでいた** ——
`ConnectionStrings:DefaultConnection` を `ConfigureAppConfiguration` の overrides でしか与えておらず、
`Program.cs` のトップレベル読み取りに間に合っていなかった。`UseSetting` でも与えるよう是正した
（起点が違うのでコミットは分けた）。

**本環境は Docker が無く統合テストは skip されるため、この是正を統合テストの実走では確かめられない。**
代わりに**機構そのもの**を Docker 不要の `DocumentService.Tests`（同じ `Program.cs` を
`WebApplicationFactory` で起こす）で実測した:

| 実験 | 与え方 | 実測 |
| --- | --- | --- |
| E1 | 何も与えない | `InvalidOperationException: ConnectionStrings:DefaultConnection が未設定である` で失敗 |
| E2 | `ConfigureAppConfiguration` のみ | **同じく失敗**（＝間に合わない） |
| E3 | `UseSetting` のみ | **4 件緑**（＝間に合う） |

E2 と E3 の差が、この是正が効く理由そのものである。いずれも実験後に復帰させた。

## 計画書との差異

- 差異: なし（NFR のセキュリティ・保守性。計画 ADR-0003/ADR-0027 の配線そのものは変えない）

## 申し送り

- 🔴 **dev 既定 `guest`/`guest` を実際に別の値へ変えるには AST 側の対応が要る**（訂正 1）。
  本リポジトリからは `RABBITMQ_USER` / `RABBITMQ_PASSWORD` と `global.messaging.user` で
  上書きできる形まで用意した。**AST chart の `global.rabbitmqConnectionString` を併せて
  上書きしないとローカル k8s の AST が認証エラーになる。**
- `rabbitmq-app` は ESO の `eso_wait` と供給後の `rollout restart` の対象に入れていない ——
  `postgres-app`（#1012）も入っておらず**両者に共通する既存の穴**である（IADR-0103）。
  片方だけ直すと非対称になるので 2 つまとめて別 issue で扱うこと。
- **領域宣言の外なので触っていない追随**: `scripts/README.md` の
  `check-default-credentials.js` 行と `.github/workflows/ci.yml` の同趣旨のコメントが、
  まだ「既知の残件（RabbitMQ 13 箇所。…別 issue）」と書いている。**0 件へ追随させること。**
- **「読まれる時点で決まる」罠は本件で 3 件目**（Pipeline:ConfigPath / RabbitMq:ConnectionString /
  ConnectionStrings:DefaultConnection）。同型が 2 回を超えたので機械検査の一般化は条件を満たすが、
  検査器の新設は本作業の領域宣言の外（`scripts/**` は指定の 4 ファイルのみ）である。**別途起票すること。**

## 未決事項

- 実クラスタでの helm レンダリング検証は本環境では不可（helm / kubectl 不在）。CI の
  `check-deploy-manifests` に委ねる。
- 統合テストの実走も本環境では不可（Docker 不在）。上の E1〜E3 は機構の実測であって
  統合テストの実走ではない。**CI の `integration.yml` で確かめること。**

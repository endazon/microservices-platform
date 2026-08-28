---
title: 作業仕様書 — NotificationService を配備先へ載せる（#1025）
type: spec
status: done
related_ids:
  - FR-22
  - ADR-0045
  - NFR
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "planning:projects/microservices-platform/02_requirements/01_requirements.md（利用者本人への通知）"
  - "planning:projects/microservices-platform/07_adr/ADR-0045（メール配信の SMTP リレー・送信上限）"
related_adrs:
  - IADR-0288
  - IADR-0215
  - IADR-0267
  - IADR-0270
  - IADR-0107
related_specs:
  - ./20260823_issue-600_notification-service-backend.md
  - ./20260828_issue-451b_notification-ingress.md
  - ./20260828_issue-600_notification-triggers.md
---

# 作業仕様書: NotificationService を配備先へ載せる（#1025）

> **［2026-08-28 追記 / #1034］本書の `status: done` は「配備定義の完了」を指す。issue #1025 の受け入れ基準の
> うち「Pod が Ready になる」「通知が送出側から受け口へ実際に到達する」は本書の射程外であり、**未実測のまま**である
> （本環境に Docker daemon / k8s が無い）。**したがって #1025 は本書が `done` になっても閉じない。**
> 将来 `status` の棚卸し（#1023 と同型の作業）でこのスコープ差を読み違えないこと ——
> **「仕様書が done」と「issue の受け入れ基準が満たされた」は別である。**

## 起点

issue #1025。**`NotificationService` は実装もテストも揃っているのに、配備先が 1 つも無い。**
波 3 で結線済みの個人資料通知（退職予告・容量警告・週次サマリ）の送出は、
`HttpPrivateNoteNotifier` が名前付き HttpClient `NotificationService` で叩くが、
**宛先が存在しないため届かない**。送出は fail-open であり、**不達はエラーログと計器
（`notification.dispatch.total` の結末 `unreachable`）にしか出ない** ——
「実装できている」と「届いている」が読み分けられない状態を解消するのが主題である。

先行トラックが**この作業を明示的に申し送っている**（`20260828_issue-600_notification-triggers.md`
§配備（compose / Helm）の判断）。そこが列挙した必要物を母集合の出発点にし、**自分で引き直した**。

## 母集合の実測（2026-08-28・base `b1da69e`）

規則 1〜10 に従い、**誤りの側（＝ notification の不在）から**複数軸で引いた。**issue 本文の
一覧を母集合にしていない。** 走査は生の出力に対して判断し、`head` で切っていない。

| # | 軸 | 走査 | 実測 |
| --- | --- | --- | --- |
| 1 | 配備物 | `grep -rn -i "notification" deploy/ \| wc -l` | **0 件**（compose・helm・local overlay・vault のすべてに無い） |
| 2 | ビルド供給 | `grep -rn -i "notification" scripts/` | **`k8s-local-images.sh` に 0 件**。ヒットは baseline JSON 4 件と `check-unit-service-ownership.js:45` の `'notification'` のみ |
| 3 | DB 名 | `grep -rn "notification_svc" .`（`.git`/`bin`/`obj` 除く） | **3 件**: `appsettings.Development.json` / 先行仕様書の残件記述 / `docs/data/notification.md`。**init スクリプト 2 本のどちらにも無い** |
| 4 | 宛先文字列 | `git grep -n "notification-service"` | **追跡下 30 行**。うち配備物は **0 行**。宛先を決める行は `DocumentService/Program.cs:79`（コード既定）と `Program.cs:60`（自己申告名）の 2 本 |
| 5 | 文書の主張 | `grep -rn "配備\|デプロイ" docs/functional/FR-22_*.md docs/data/notification.md` | **6 行 / 2 文書**が「まだ配備されていない」と書いている（本作業で追随が要る＝規則 10） |
| 5' | 同上・**軸を広げた再走査** | 上に `docs/api/BFF_notifications.md docs/tests/FR-22_user-notifications.md` を足す | 🔴 **11 行 / 3 文書**。**軸 5 は `docs/tests/` を対象に含めておらず 5 行を取りこぼしていた**（規則 5「軸を 1 本で終わらせない」が実際に効いた事例。追随はこの 11 行に対して行った） |

### 軸 2 が出した衝突（🔴 着手前に判明した設計上の障害）

`scripts/check-unit-service-ownership.js` の `AST_OWNED_FALLBACK` に **`'notification'` が入っている**
（AST chart も同名のサービスを所有する）。MSP の chart キーを `notification` にすると
**同検査が「重複デプロイ」として CI を落とす**。実測（改変前の values に 1 ブロック足した合成テキストで
`effectiveEnabled` → `findDuplicateOwnership` を呼んだ）:

```
notification enabled? true
duplicates: [ 'notification' ]
```

この衝突は **IADR-0107 §運用注意が明示的に予見している**（`audit` / `report` / `notification` /
`backtest` は汎用名で衝突の余地がある、と名指しされている）。同節が定めた逃げ道は 2 つ ——
**Service 名の変更**か、**到達経路を明示的に分けたうえで同節を更新し検査へ除外リストを設ける**。
判断は [IADR-0288](../adr/IADR-0288_notification-service-deployment-and-name-collision.md) に置く。

### 除外したもの（規則 6。黙って落とさない）

| 除外 | 理由 |
| --- | --- |
| BFF 端点 `/bff/notifications*` の実装 | #600 の射程。**配備とは別の欠落**であり、本作業で入れると 1 PR = 1 論理変更が崩れる |
| SMTP リレーの実体・宛先解決 | 実環境が要る（`UnconfiguredSmtpEmailTransport` / `UnresolvedEmailAddressResolver` のまま）。ADR-0045 の go-live 側 |
| `Services__NotificationService` の明示注入 | **コード既定と同値の重複**にしかならない（先行仕様書の判断を踏襲）。compose・helm とも Service 名を既定と文字列一致させる方を選ぶ |
| `NetworkIsolationTests.InternalAppServices` への追加 | `src/knowledge/**`＝**本作業の宣言領域外**。統括へ申し送る（追加しなくても検査は落ちない） |
| `check-bff-downstreams.js` の `CALLERS` へ DocumentService を足す | 宣言領域外。かつ DocumentService のコード既定は既に `:8080` のため足しても違反 0 のまま。**死角であることだけ申し送る** |
| `deploy/local/README.md` の到達手順 | 宣言領域外（`deploy/local/infra/**`・`deploy/local/vault/**` のみ許可） |
| `scaling.services` への `notification` 追加 | **入れてはならない**（後述・設計 4） |

## 対象範囲

- **対象**: compose のサービス定義 / helm values の `services.notification` / `k8s-local-images.sh` の
  `MAPPING` / DB 作成（compose 側 init と k8s 側 init の**両方**） / ESO 経路の確認 /
  名前衝突の裁定（IADR-0288）と IADR-0107 §運用注意の更新 / 文書の追随。
- **対象外**: 上の除外表のとおり。

## 設計

### 1. サービス名は `notification-service` で固定する（上書き env を作らない）

`HttpPrivateNoteNotifier.ClientName = "NotificationService"` の名前付き HttpClient は
`DocumentService/Program.cs:78-79` で

```csharp
c.BaseAddress = new Uri(builder.Configuration["Services:NotificationService"]
    ?? "http://notification-service:8080");
```

と組まれる。したがって**設定キーは `Services:NotificationService`（env では `Services__NotificationService`）**、
**コード既定は `http://notification-service:8080`**。

- compose: サービス名 `notification-service` ＋ `expose: "8080"` → コンテナ DNS `notification-service:8080`。**既定と文字列一致**。
- helm: chart キー `notification` → `deployment.yaml` / `service.yaml` が `{{ $name }}-service` を組む → Service `notification-service`、`port: 8080`。**既定と文字列一致**。

**キーを変えると上書き env が要る。** それは `llm-gateway`（compose）と `llmgateway-service`（k8s）の轍で、
#995 が 🔴 付きの警告コメントで塞いだ型である。加えて本経路は **fail-open のため不達が 502 にすらならない**
——上書きを落としても誰も気づけない。よって**上書きを作らない形（名前を揃える）を採る**。

### 2. DB は 2 本の init スクリプト**両方**へ足す

`AddDbContext` + `UseNpgsql` + 起動時 `MigrateAsync()` のため、DB `notification_svc` が無いと
**クラッシュループする**（`graph_svc` を落として同じ事故を踏んだ先例がコメントに残っている）。
init は 2 本ある: `deploy/create-multiple-dbs.sh`（compose）と
`deploy/local/infra/postgres.yaml` の ConfigMap（経路B）。**片方だけ足すと片方の環境だけ壊れる。**
所有者は両方とも `kp`（`global.db.user` と compose の `x-db-env` に一致）。

### 3. 資格情報（ESO 経路）は既存の共有 Secret に相乗りする — **新規 ExternalSecret は不要**

走査（`deploy/local/vault/eso/` の 13 本 ＋ `bootstrap.sh` の seed ＋ `policy-eso-read.hcl`）で確認した。

- helm の `deployment.yaml` は `$svc.database` があるときだけ `DB_PASSWORD` を
  **`global.db.existingSecret`（＝`postgres-app`）の `password`** から引く。**per-service の Secret は無い。**
- `postgres-app` は `externalsecret-postgres-app.yaml` が Vault `secret/msp/postgres-app` から供給し、
  `bootstrap.sh:38` が seed する。`ESO=1` の適用も配線済み。
- NotificationService が構成から読むのは `ConnectionStrings:DefaultConnection` と `Notification:*`
  （`appsettings.json` の非機密値）だけである（`Configuration[` の直接参照は 0 件）。
  SMTP は `UnconfiguredSmtpEmailTransport` のため**現時点で資格情報を要求しない**。

→ **ESO=1 経路でも DB を持つ他サービスと同じ供給元から資格情報が届く。欠けは無い。**
（`externalsecret-keycloak-smtp.yaml` は「★未配線」のまま。SMTP 実体が入るときの配線点として申し送る。）

### 4. `scaling.services` へは入れない（replicas は 1 に固定）

`NotificationMaintenanceHostedService` が**保持期限切れの削除とメール outbox の送出**を
5 分周期で回す。レプリカを増やすと同じ outbox を複数レプリカが同時に処理し、
**送信上限（テナント全体で 1 つの資源）の数え方が割れる**。`conversion` / `ingestion` / `graph` と同じく
HPA 対象外にし、`replicas: 1` を明示する。

### 5. NetworkPolicy は追加しない

`allow-intra-namespace` が `podSelector: {}`（＝同 namespace 全 Pod）で ingress/egress を許可しており、
**サービスごとの列挙を持たない**。DocumentService → NotificationService は同 namespace 内であり、
既存ポリシーで到達する。エッジ公開はしない（内部 API のため `expose` のみ・Service は既定 ClusterIP）。

### 6. 自己申告（introspection）の収集先へ足す

`NotificationService/Program.cs:60` が `AddPlatformIntrospection("notification-service", new PipelineOptions())`
を呼び、**「段は持たないが到達可能性を申告する」**と明記している。申告する側が居るのに収集側に無いと、
その申告は永久に読まれない。compose の BFF env と helm の `services.bff.extraEnv` の**両方**へ
`Introspection__Services__notification-service` を足す（片方だけだと「手元では見える・本番では見えない」）。
**`Services__*`（BFF の下流集約先・readiness の UriHealthCheck）へは足さない** ——
BFF は通知の下流を持たない（`/bff/notifications*` は未実装）。

## 受け入れ基準

- [x] `deploy/docker-compose.yml` に `notification-service` があり、**既存アプリサービスの書き方と同型**
      （`build.context: ..` / `expose` のみ・host 公開なし / `<<: [*common-env, *db-env]` / `depends_on: postgres(service_healthy)`）
- [x] `deploy/helm/microservices-platform/values.yaml` の `services.notification` が `database: notification_svc` を持ち、
      `global.db` の仕組み（`DB_PASSWORD` → `$(DB_PASSWORD)` 補間）に乗る
- [x] `scripts/k8s-local-images.sh` の `MAPPING` に入り、`node scripts/check-image-mapping.js` が緑
- [x] DB `notification_svc` が init スクリプト **2 本とも**に在り、所有者が `kp`
- [x] `ESO=1` 経路の資格情報供給に欠けが無いことを**走査で**確かめた（設計 3）
- [x] 送出側の設定キー・既定値と、compose / helm が与える宛先が**文字列レベルで一致**することを実測で示した
- [x] `node scripts/scripts.test.js` が緑（名前衝突の除外を入れた `check-unit-service-ownership.js` を含む）
- [x] 「まだ配備されていない」と書いている文書 **11 行 / 3 文書**を追随させた（規則 10・軸 5'）

## テスト方針

**本環境に Docker daemon も k8s も無い。** よって「Pod が Ready になる」「通知が実際に到達する」は
**実測できない**。代わりに静的な突合だけを受け入れ基準に置く（上記）。**実測していないことを実測したと書かない。**

- 既存の機械検査（`check-image-mapping.js` / `scripts.test.js` / `check-doc-links.js` /
  `check-trace-blocks.js`）を実走し、生の出力を PR と報告へ貼る。
- YAML は Node の解析（`js-yaml` が無いため、compose は `check-image-mapping.js` のパーサ、
  helm values は `check-unit-service-ownership.js` / `check-bff-downstreams.js` のパーサ、
  k8s マニフェストは `python3 -c "import yaml"` が使えればそれ）で構文を確かめる。
- **新規の C# テストは書かない** —— 本作業はコードを 1 行も変えない（配備定義のみ）。

## 計画書との差異

- 差異: なし。ADR-0045 が定める送信上限・「静かに落ちない」の実装側は既に入っており、
  本作業はその**配備先を作る**だけである。

## 未決事項

1. **AST の `notification` サービスが共有 Postgres に DB を持つか**は、submodule 未取得のため確認できていない。
   状況証拠として、MSP が共有 Postgres へ用意する AST 専有 DB 7 件（`audit_svc` / `configuration_svc` /
   `cost_control_svc` / `market_monitor_svc` / `order_execution_svc` / `report_svc` / `risk_management_svc`）に
   **`notification_svc` は含まれない**。submodule を取得できる環境で確認する（IADR-0288 §未決）。
2. **SMTP の実体と宛先解決**は未配線のまま。`externalsecret-keycloak-smtp.yaml` が「★未配線」で待っている。
3. **BFF 端点 `/bff/notifications*`** が入るまで、配備しても**画面には出ない**（#600）。

## 検証（実走・2026-08-28）

**実走したコマンドと生の出力を PR と報告へ貼る。** 加工していない。

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-image-mapping.js` | OK（ドリフト 0） |
| `node scripts/check-unit-service-ownership.js` | OK ＋ **除外中 `notification` の notice**（設計どおり） |
| `node scripts/check-unit-service-ownership.js --self-test` | 自己試験 16 件 OK（除外の 4 件を新設） |
| `node scripts/check-bff-downstreams.js` | OK（呼び出し元 5 件・ドリフト 0） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **645 tests passed** |
| `node scripts/check-adr-numbering.js` | OK（重複・欠番なし・索引と双方向一致） |
| `node scripts/check-doc-links.js` | OK（941 件） |
| `node scripts/check-trace-blocks.js` | OK（153 件） |
| `node scripts/check-doc-updated.js` / `check-doc-status-vocabulary.js` / `check-doc-type-vocabulary.js` | すべて OK |
| `bash -n deploy/create-multiple-dbs.sh` / `bash -n scripts/k8s-local-images.sh` | OK |
| `python3 -c "yaml.safe_load(...)"`（compose / values.yaml / postgres.yaml） | すべて解析成功。anchor のマージ結果も確認 |
| `dotnet build .../NotificationService.Tests.csproj` | Build succeeded（0 Warning / 0 Error） |
| `dotnet test .../NotificationService.Tests.csproj` | **Passed! Failed: 0, Passed: 53** |

### 🔴 実走できなかったもの（できないことを、できないと書く）

| 検証 | 状態 | 理由 |
| --- | --- | --- |
| `helm lint` / `helm template` / `kubeconform`（`check-deploy-manifests.js`） | **未実走** | `helm` / `kubectl` / `kubeconform` が本環境に無い（`command -v` で確認）。**同検査は fail-closed のためこの環境では走らせられない** |
| `docker compose config` / 実ビルド | **未実走** | `docker` CLI はあるが **daemon へ到達できない**（`docker info` が失敗） |
| **Pod が Ready になること・通知が実際に届くこと** | **未実測** | k8s クラスタが無い。**代わりに静的な文字列一致（下記）と機械検査の突合のみを行った** |
| `dotnet build src/platform/backend/backend.slnx` | **失敗（本作業と無関係）** | `Platform.Bff` が AST submodule（未取得）の `AiStockTrading.Bff.Endpoints` を参照するため `CS0246`。**C# は 1 行も触っていない**（差分は `deploy/` `scripts/` `docs/` `.ai-context/` のみ） |

### 送出側と受け口の文字列一致（実測。設定キー名まで突き合わせた）

| 面 | 実測値 | 出典 |
| --- | --- | --- |
| 設定キー | `Services:NotificationService` | `DocumentService/Program.cs:78` |
| コード既定 | `http://notification-service:8080` | `DocumentService/Program.cs:79` |
| HttpClient 名 | `NotificationService` | `HttpPrivateNoteNotifier.cs:34` |
| 送信パス | `/internal/notifications` | `HttpPrivateNoteNotifier.cs:35` |
| 受け口パス | `/internal/notifications` | `NotificationIngressEndpoints.cs:23` |
| compose のサービス名 / expose | `notification-service` / `["8080"]` | `deploy/docker-compose.yml` |
| helm の Service 名 / port | `notification-service`（キー `notification` ＋ `{{ $name }}-service`）/ `8080` | `values.yaml` ＋ `templates/service.yaml` |
| compose の `Services__NotificationService` | **未設定（＝コード既定を使う）** | 意図どおり（設計 1） |

→ **宛先の 3 面（コード既定・compose・helm）が `notification-service:8080` の 1 文字列で一致し、
パスも送受で一致している。** 上書き env は 1 つも作っていない。

### helm テンプレートの手展開（helm 未導入のため実レンダリングではない）

```
Host=postgres;Port=5432;Database=notification_svc;Username=kp;Password=$(DB_PASSWORD)
DB_PASSWORD <- secretKeyRef postgres-app / password
HPA/PDB 対象: False → replicas 1 が描かれる
worker フラグ: False → /health/live, /health/ready のプローブが付く
```

## 宣言領域の逸脱（統括へ報告）

宣言では許可されていなかったが、**受け入れ基準を満たすために触った 2 ファイル**がある。
いずれも「触ってはならない」列挙（`scripts/k8s-local-up.sh` / `scripts/k8s-local-up.test.js` /
`src/knowledge/**` / `src/platform/backend/Shared/**` / `src/*/frontend/**` / `src/package.json`）には
含まれない。**追加は加算のみで、既存行を書き換えていない。**

| ファイル | 触った理由 |
| --- | --- |
| `deploy/create-multiple-dbs.sh` | **compose 側の DB init はここにしか無い。** 足さないと compose の notification-service が DB 不在でクラッシュループする（「配備したのに動かない」を作る）。**先行仕様書 `20260828_issue-600_notification-triggers.md` §配備 が必要物として名指ししている** |
| `scripts/check-unit-service-ownership.js` | **これを直さないと CI が必ず落ちる**（実測済み）。IADR-0107 §運用注意が定めた対処そのもの。除外リストの新設と自己試験 4 件の追加のみ |

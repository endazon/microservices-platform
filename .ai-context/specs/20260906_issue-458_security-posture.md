---
title: セキュリティ暫定運用の解消 —— IADR-0026 後の第一防御を確定し、多層防御の要求をサービスごとに判定する
type: spec
status: done
related_ids:
  - FR-05
  - FR-12
  - NFR-09
  - NFR-16
  - SC-07
  - ADR-0004
  - ADR-0005
  - ADR-0029
  - ADR-0075
  - IADR-0017
  - IADR-0026
  - IADR-0029
  - IADR-0042
  - IADR-0044
  - IADR-0127
  - IADR-0128
  - IADR-0154
  - IADR-0377
  - IADR-0379
  - IADR-0403
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md 決定
  - planning:projects/microservices-platform/07_adr/ADR-0005_service-mesh-istio.md 決定
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md 決定
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 1・2・6
---

# 仕様書: #458 セキュリティ暫定運用の解消 —— 第一防御の所在を確定し、サービスごとの多層防御要求を判定する

> 本仕様書の**主たる成果物は判断（IADR-0403）である**。コード変更は判断から従属して出てくる最小の 1 点に絞る。
> issue #458 本文とその棚卸しコメントには**測り直すと成り立たない前提が 3 つ**あり、本仕様書はまずそれを訂正する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（ABAC 権限スコープ解決）／ FR-12（文書正規化・変換ジョブ）
- 非機能要求（NFR）: NFR-09（全 API で OIDC/JWT 認証）／ NFR-16（サービス間 mTLS）
- 画面（SC）: SC-07（変換ジョブ）
- 計画 ADR: ADR-0004（Keycloak OIDC/JWT・ABAC）／ ADR-0005（Service Mesh / Istio mTLS）／
  ADR-0029（gRPC / REST 使い分け）／ ADR-0075（east-west gRPC 移行順序）

## 基点と測定条件

- 基点: `origin/develop` `708eb895`
- `git rev-parse --is-shallow-repository` → **`false`**（ただし本仕様書は `git log` / `git blame` を根拠に使っていない）
- `git submodule update --init src/ai-stock-trading` を**最初に実行済み**。以下の全走査は
  **`src/ai-stock-trading` を除外**している（除外しないと `Program.cs` の母集合が膨らみ、
  `ServiceToken__` の数え方も狂う）。

## 🔴 issue の前提のうち、測り直して成り立たなかったもの

### 訂正 1: `blocked` ラベルは mTLS 起因では正当化されない

ブロッカーとされた #1159（mesh mTLS の宣言と実効の乖離）は **2026-09-04 に CLOSED**。
後継の統制は `IADR-0377`（single writer ＋ ドリフト門 G12）が持つ。上流ガイド §6 の
「blocked 判定は棚卸しごとに再検証する」に従い、**本 issue の `blocked` は外してよい**。

### 訂正 2: 🔴 「JWT は 13/15」は**測っている対象が違う**

`AddPlatformAuth` の有無を数えると 13/15 である（`ConversionService` / `IngestionService` が無い）。
**しかしこの数字は「守られているサービスの数」ではない。**

```console
$ sed -n '86,105p' src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/AuthExtensions.cs
        services.AddAuthorization(options =>
        {
            options.AddPolicy(PlatformAuthPolicies.AdminOnly, ...);
            options.AddPolicy(PlatformAuthPolicies.ConfigViewer, ...);
            options.AddPolicy(PlatformAuthPolicies.ServiceCaller, ...);
        });

$ grep -rn --include=*.cs -E 'FallbackPolicy|DefaultPolicy' src --exclude-dir=obj | grep -v ai-stock-trading
（`LlmFallbackPolicy` の 20 行のみ。認可の FallbackPolicy は 0 件）
```

**陽性対照**: 同じ走査で `AddPolicy` は 5 件、`RequireAuthorization` は 121 件当たる＝走査は生きている。

**`AddPlatformAuth` は認証（JwtBearer）とポリシー 3 本を登録するだけで、`FallbackPolicy` を置かない。**
したがって**呼んだだけでは 1 つの口も塞がらない**。実効の門は `RequireAuthorization` / `[Authorize]` の側にある。

口の側で数え直すと（構文で数える。語で数えない）:

🔴 **［2026-09-06 追記 / #458］初回の走査は語で数えており、4 サービスで実際より多い数を出していた。**
`grep 'RequireAuthorization'` はコメント行の言及も数える（Dashboard 10 / Feedback 4 / Notification 3 /
Document 15 と出ていた。正: 5 / 3 / 1 / 14）。**構文で数え直した数を正とする。** 口の数は
`Map(...)\(` の実数で、こちらは初回から変わっていない（コメントアウトされた口は 0 件）。

```console
$ for d in src/*/backend/Services/*/; do
    eps=$(grep -rn --include=*.cs -E 'Map(Get|Post|Put|Delete|Patch)\(' "$d" --exclude-dir=obj --exclude-dir=Tests | grep -vE ':[0-9]+:\s*//' | wc -l)
    ra=$(grep -rn --include=*.cs 'RequireAuthorization(' "$d" --exclude-dir=obj --exclude-dir=Tests | grep -vE ':\s*//' | wc -l)
    ao=$(grep -rn --include=*.cs 'RequireAuthorization(PlatformAuthPolicies.AdminOnly' "$d" --exclude-dir=obj --exclude-dir=Tests | wc -l)
    printf '%-22s endpoints=%-3s RA=%-3s AO=%s\n' "$(basename $d)" "$eps" "$ra" "$ao"; done
```

🔴 **`RA` は口の数ではない。** 群（`MapGroup`）へ 1 つ掛ければ配下の全口を覆い、個別の口へ重ねると
AND 合成になる（`IADR-0128` 決定 1）。**多い＝強いではない。**

| サービス | 口 | `RequireAuthorization(` | うち `AdminOnly` |
| --- | --- | --- | --- |
| DocumentService | 39 | 14 | 7 |
| AuthorizationService | 20 | 2 | 2 |
| GraphService | 13 | 7 | 1 |
| DataSourceService | 7 | 5 | 4 |
| DashboardService | 6 | 5 | 0 |
| McpServer | 6 | 2 | 1 |
| **ConversionService** | **5** | **0** | 0 |
| WikiService | 4 | 0 | 0 |
| AiAnalysisService | 3 | 0 | 0 |
| RetrievalService | 3 | 0 | 0 |
| LlmGateway | 3 | 0 | 0 |
| FeedbackService | 3 | 3 | 1 |
| NotificationService | 3 | 1 | 0 |
| **IngestionService** | **0** | **0** | 0 |

**REST の口にロール門を 1 つも持たないのは 2 サービスではなく 6 サービスである**
（Conversion / Ingestion に加え Wiki / AiAnalysis / Retrieval / LlmGateway）。
ただし後者 4 つは**別の形の保証**を持つ（IADR-0403 の表で判定する）。

### 訂正 3: 🔴 `ConversionService` の HTTP サーフェスは 3 本ではなく 5 本で、うち 2 本は副作用を持つ

issue の 2026-09-05 コメントは「`app.Map*` **3 件**（変換ジョブの読み取り・retry）」と書いている。**実測は 5 本である。**

```console
$ grep -rn --include=*.cs -E 'MapGroup|MapPost\(|MapGet\(' src/knowledge/backend/Services/ConversionService --exclude-dir=obj --exclude-dir=Tests
.../ConversionJobEndpoints.cs:19:        var g = app.MapGroup("/jobs")...
.../CorrectFigure/Endpoint.cs:13:        g.MapPost("/{id:guid}/figures/{figureId}/correction", ...
.../GetById/Endpoint.cs:12:        g.MapGet("/{id:guid}", ...
.../List/Endpoint.cs:12:        g.MapGet("/", ...
.../ListFigures/Endpoint.cs:14:        g.MapGet("/{id:guid}/figures", ...
.../Retry/Endpoint.cs:21:        g.MapPost("/{id:guid}/retry", ...
```

**陽性対照**: 同じ走査を DocumentService へ当てると 50 行返る＝走査は生きている。

3 本という数は `IADR-0042` 決定 3 が列挙した口の数であり、**その後 `IADR-0154`（人手補正 Phase 1）が
`/figures` と `/figures/{id}/correction` の 2 本を足している**。とりわけ `POST .../correction` は
**保存され、再発行されて一般利用者が読む本文を書き換える**（当該 Endpoint.cs:19-20 のコメント）。

🔴 **ただしこれは記録漏れではない。** `IADR-0154` 決定 6 が、この 2 本を足すのと同じ判断の中で
「**ワーカー自身に認可を課さない点も変えない**（`IADR-0029` / `IADR-0128` 決定 3。代償統制は
`NetworkIsolationTests`）」と明示的に据え置いている。**判断は記録されている。数字だけが古い。**

### 訂正 4: 「ネットワーク隔離の列挙に `ingestion-service` が無いのは同型の事故 2 回目」は**誤り**

`NetworkIsolationTests.cs:42-46` が除外を**意図的なものとして理由つきで記録**しており、
その理由（HTTP サーフェスが `MapPlatformIntrospection()` 1 件のみ・副作用のある操作を持たない）は
**今も実測で成り立つ**（上表 `IngestionService` の口 = 0）。`conversion-service` の方は本物の漏れだったが既に埋まっている。
→ **「同型の事故 2 回目」の条件はこの筋では満たされない。この筋を根拠に検査器を新設しない。**

## 🔴 ただし、自分で引き直したら別の筋で 2 回目・3 回目が見つかった

`NetworkIsolationTests.InternalAppServices` の母集合を**記憶ではなく compose の側から**引き直した
（`traceability.repo.md` 規則 9: 「追随する文書」を記憶で挙げない）。

compose の第一者アプリサービスは **`build:` を持つもの**で機械的に判別できる（第三者インフラは `image:`）:

```console
$ awk '/^services:/{s=1;next} /^[a-z]/{s=0} s && /^  [a-z0-9-]+:$/{name=$0} s && /^    build:/{print name}' deploy/docker-compose.yml | wc -l
19
```

19 本の内訳と、現行の列挙（14 本）との差:

| 区分 | サービス | 状態 |
| --- | --- | --- |
| 列挙済み・内部（11） | document / datasource / conversion / retrieval / aianalysis / authorization / notification / wiki-service / llm-gateway / feedback / dashboard | ✅ 回帰ガードあり |
| 列挙済み・内部（AST 後段 3） | configuration / risk-management / market-monitor | ✅ 回帰ガードあり |
| host 公開する縁（2） | bff / frontend | ✅ 意図的に公開 |
| 🔴 **列挙漏れ（3）** | **graph-service** / **mcp-service** / **ingestion-service** | ❌ **回帰ガード無し** |

`graph-service` と `mcp-service` は**実態としては正しく `expose:` のみ**であり、Helm でも
ClusterIP（`ingress:` ブロックを持つのは wikijs だけ・既定 `enabled: false`）である。
**つまり今は穴が開いていない。開いても誰も止められないだけである** ——
`conversion-service` のときと**同じ形の欠陥**であり、その 2 回目・3 回目にあたる。

🔴 **区別を明記する**: 「検査器を新設しない」と決めたのは訂正 4 の筋（ingestion の意図的除外）である。
ここで行うのは**既存の検査器の母集合を塞ぐこと**であって、新しい統制の追加ではない。
`NetworkIsolationTests.cs:46` は自ら「**追加するときはここへ 1 行足す**」と書いている。

## 決めること

1. `IADR-0026` 後の**第一防御は何か**、`IADR-0044` の多層防御は**サービスごとに何を要求するか**。
2. `ConversionService` / `IngestionService` の現状は **correct / acceptable / defect** のどれか。
3. 機械検査できるものは何か。「同型の事故 2 回目」の条件は満たされているか。
4. mTLS・Vault の 2 本について、**リポジトリ内で動かせる範囲と、クラスタ／人手を要する範囲**の境界。

→ **判断は [IADR-0403](../adr/IADR-0403_security-interim-posture-resolution.md) に記録する。**

## 実装の射程（本 PR で行うこと）

**IADR-0403 の判断から従属して出てくるのは、母集合の穴を塞ぐ 1 点だけである。**

- `NetworkIsolationTests.InternalAppServices` へ `graph-service` / `mcp-service` / `ingestion-service` を足す。
- 🔴 **同じ漏れが 4 回目を起こさないよう、列挙を compose 側から検算する `[Fact]` を足す** ——
  `build:` を持つ compose サービスは、内部（列挙）か host 公開の縁（allowlist）の**どちらかに必ず属する**。
  どちらでもないサービスが現れたら落ちる。これで**新しい内部サービスを足したときの既定が fail-closed になる**
  （現在は「列挙し忘れると黙って通る」＝ fail-open）。

**行わないこと（意図的）**:

- 🔴 **`ConversionService` / `IngestionService` へ `AddPlatformAuth` を足さない。** IADR-0403 の判定は
  「acceptable（記録された理由つき）」であり、足すと `IADR-0042` 決定 3 /`IADR-0154` 決定 6 を
  無断で覆すことになる（改定には別 IADR が要る）。**判断より先にコードを動かさない。**
- **Vault の実配備・`ISecretStore` の実装を行わない。** 抽象と移行段を IADR-0403 決定 5 に設計として記録し、
  実装は展開 issue へ送る（実配備は環境依存で、この PR では検証できない）。
- **mTLS の PERMISSIVE 掃除を行わない。** 実測の結果、**宣言側に掃除すべき PERMISSIVE は残っていない**
  （IADR-0403 決定 4）。残りは稼働クラスタでしか測れない。

## 受け入れ基準

1. `IADR-0403` が、第一防御・多層防御の所在と**15 サービス全件の判定表**（理由つき）を持つ。
2. `ConversionService` / `IngestionService` の判定が **correct / acceptable / defect** のいずれかで
   証跡つきで述べられている。
3. `NetworkIsolationTests` が 17 本の内部サービスを守り、**列挙漏れが出たら落ちる**検算 `[Fact]` を持つ。
4. 追加した `[Fact]` が変異試験で落ちる（列挙から 1 行抜く／allowlist を空にする）。
5. `dotnet build` / `dotnet test` が両ユニットで通る。
6. `node scripts/check-trace-blocks.js` / `check-adr-numbering.js` / `check-secret-injected-options.js` /
   `check-backend-libraries.js` / `check-unit-dependencies.js` / `check-realm-constraints.js` が通る。
7. `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が最後の編集の後に通る。

## 未測定（正直に残す）

- **稼働クラスタの mTLS 実効状態**は測っていない（クラスタが無い）。G12 は live object の
  `managedFields` を要するため、リポジトリ内では**原理的に**測れない。
- **`ISTIO` は `.github` に 0 件**（陽性対照: `LOCALEDGE` は 4 件）。つまり **G12 の STRICT ドリフト検出は
  CI で一度も動いていない**。これは実測された穴だが、埋めるにはメッシュ有効なクラスタを CI で立てる必要があり、
  本 PR の射程外。
- **Vault の実サーバに対する疎通・ローテーション**は行っていない。ESO マニフェスト 25 本は宣言のみを確認した。

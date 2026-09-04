---
title: 作業仕様書 — 利用イベントから UserId を落とし、保持期間 90 日の定期削除を入れる
type: spec
status: done
related_ids:
  - FR-10
  - UC-05
  - SC-10
  - ADR-0002
  - ADR-0006
  - ADR-0068
  - ADR-0071
  - ADR-0072
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - "ADR-0072 決定 1（利用イベントに利用者識別子を保持しない。UsageEvent から UserId を落とす。移送マイグレーションを伴う。受け口の RequireAuthorization() と JWT からの主体解決は維持する）"
  - "ADR-0072 決定 3（利用イベントの保持期間は 90 日。根拠は集計の上限期間。削除の基準時刻は集計の起点と一致させる）"
  - "ADR-0072 決定 4（一意カウントが必要になった場合はハッシュ化を含めて別 ADR で判断する。本作業では扱わない）"
  - "ADR-0072 §結果（SC-10 の画面表示は変わらない。BFF の発火側は変更不要。保持期間の実施と運用仕様書への手順追加が新たに要る）"
  - "ADR-0072 §残るもの（移送で既存行の UserId は失われ復元できない。Query は 90 日残る。集計の上限を変えるときは保持期間も同時に見直す）"
  - "planning#515（環流・裁定 2026-09-03） / planning#526（計画 PR）"
related_adrs:
  - IADR-0367
  - IADR-0343
  - IADR-0357
  - IADR-0353
  - IADR-0215
issue: "#1198"
---

# 作業仕様書: 利用イベントの主体と保持期間

## 起点

`ADR-0072`（Accepted・2026-09-03）が確定させたのは 4 点である。本作業が受けるのは 1・3 と
§結果・§残るものであり、2 は計画側の記述（`SC-10` Q27 の項）で完了済み、4 は将来の別 ADR へ送られている。

| # | 論点 | 裁定 | 本作業 |
| --- | --- | --- | --- |
| 1 | 識別子 | `UsageEvent` から `UserId` を落とす（移送マイグレーション）。**受け口の認証と主体解決は維持** | 実装する |
| 2 | 計画の書き足し | `SC-10` Q27 の項へ確定として書き足す | 計画側で完了（planning#526） |
| 3 | 保持期間 | **90 日**。根拠は集計の上限期間。**削除の基準時刻は集計の起点と一致させる** | 実装する |
| 4 | 一意カウント | ハッシュ化を含め将来の ADR で判断する | **扱わない** |

決め手は ADR の言葉で「**読んでいないものを持たない**」であり、認証を残すのは
「**認証と記録が別の統制だから**」である —— 認証を外すと不正投入が開くが、認証は
「誰が投げたか」を列に残す必要を生まない。

## 母集合（着手前に私が自分で引いた。issue 本文の実測は転記していない）

起点コミットは `develop` `78f9bda6`。`git rev-parse --is-shallow-repository` → **`false`**
（履歴は打ち切られていない。`git log` を出典に引ける）。

### 走査 1 — `UserId` を書く／読む箇所

```console
$ git grep -n "UserId" -- src/knowledge/backend/Services/DashboardService/
Domain/UsageEvent.cs:13                                  （宣言）
Domain/UsageEvent.cs:25                                  （Create で代入）
Infrastructure/Persistence/DashboardDbContext.cs:26      （列構成）
Infrastructure/.../20260703010000_InitialCreate.Designer.cs:45
Infrastructure/.../20260703010000_InitialCreate.cs:21
Infrastructure/.../20260822193808_AddKnowledgeHealthObservations.Designer.cs:76
Infrastructure/.../20260903001957_AddKnowledgeHealthIndicatorThresholds.Designer.cs:93
Infrastructure/.../DashboardDbContextModelSnapshot.cs:90
→ 8 件。**Features/ 配下は 0 件**（＝どの集計も読んでいない）
```

**陽性対照**（同じ走査器を同じ範囲へ向けた）:

```console
$ git grep -n "UsageEvents" -- src/knowledge/backend/Services/DashboardService/Features/
Features/Dashboard/DashboardEndpoints.cs:47   （利用状況の集計）
Features/Dashboard/DashboardEndpoints.cs:77   （検索傾向の集計）
Features/Dashboard/RecordEvent/Endpoint.cs:26 （受け口）
→ 3 件。走査は Features/ 配下に対して機能している
```

**手を入れるのは 3 件**（`Domain/UsageEvent.cs` の 2 か所・`DashboardDbContext.cs` の 1 か所）。
**過去のマイグレーションと Designer は書き換えない**（適用済みの履歴である）。
`DashboardDbContextModelSnapshot.cs` は `dotnet ef migrations add` が再生成する。

### 走査 2 — 保持期間の削除処理が「無い」こと

```console
$ git grep -nE "RemoveRange|ExecuteDelete|BackgroundService|IHostedService" \
    -- src/knowledge/backend/Services/DashboardService/
Features/KnowledgeHealth/Report/Endpoint.cs:56  db.KnowledgeHealthObservations.RemoveRange(stale);
→ 1 件。**別の表**（観測値のスナップショット置換）であり、`UsageEvents` の削除は 0 件。
  DashboardService に常駐処理は 0 件
```

**陽性対照**（同じ語を別の場所へ向けた）:

```console
$ git grep -n "BackgroundService" -- src/ | grep -v Tests
Bff/Knowledge.Bff.Endpoints/Usage/UsageEventDispatcher.cs:29        （発火側）
Services/DataSourceService/.../DataSourceSyncHostedService.cs:20
Services/DocumentService/.../PrivateNoteMaintenanceService.cs:216
Services/GraphService/.../KnowledgeHealthHostedService.cs:18
Services/IngestionService/.../QdrantCjkNgramBackfillHostedService.cs:22
Services/NotificationService/.../NotificationMaintenanceHostedService.cs:23
Shared/.../DriftDetectionHostedService.cs:14
→ 7 件。走査語そのものは機能している（DashboardService だけが 0 件である）
```

### 走査 3 — 発火側（変更不要の確認）

```console
$ sed -n '64,72p' src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Usage/UsageEventDispatcher.cs
    Content = JsonContent.Create(new UsageEventRequest(signal.EventType, signal.Query)),
    ... request.Headers.TryAddWithoutValidation("Authorization", signal.Authorization);
$ git grep -n "record UsageEventRequest" -- src/knowledge/backend/Shared/
Shared/Knowledge.Contracts/Dtos/DashboardDto.cs:22:public record UsageEventRequest(string EventType, string? Query = null);
→ 契約は 2 項目である。**利用者は本文に入っていない**（資格情報はヘッダで運ぶ）。
  `ADR-0072` §結果「BFF の発火側は変更不要である」は実測と一致する
```

### 走査 4 — 追随する文書（誤りの側の文字列で引いた。規則 9）

```console
$ git grep -rn "UserId" -- docs/ | grep -v "^docs/api/openapi.yaml"
docs/data/feedback.md:26, :40, :44, :58, :71, :76, :88          （AnswerFeedback。別エンティティ）
docs/data/usage-event.md:43, :54, :67                            （属性表・ER 図・キー欄）
docs/functional/FR-08_answer-feedback.md:34, :38, :86, :87, :89, :92  （別エンティティ）
docs/functional/FR-10_dashboard.md:35                            （データモデル表）
docs/operations/local-sso-recovery-runbook.md:83                 （Discord の AllowedUserIds。無関係）
docs/tests/FR-08_answer-feedback.md:23, :32, :33, :40            （別エンティティ）
→ **本件の対象は 4 行**（`usage-event.md` の 3 行 ＋ `FR-10_dashboard.md` の 1 行）。
  `FR-08` / `feedback.md` の `UserId` は **AnswerFeedback**（回答フィードバック）のもので、
  `ADR-0072` の射程外である（同 ADR は利用イベントだけを対象にしている）
```

```console
$ git grep -rln "利用イベント" -- docs/
docs/api/openapi.yaml                       （契約。利用者の欄は無い ＝ 変更不要）
docs/data/usage-event.md
docs/functional/FR-10_dashboard.md
docs/how-to/plan-id-range-history-annex.md  （ID レンジの別紙。無関係）
docs/screens/SC-10_operations-dashboard.md
```

**追随対象は 5 文書**: `docs/functional/FR-10_dashboard.md`（データモデル表・API 表）／
`docs/data/usage-event.md`（属性表・ER 図・キー欄・未決事項の「保持期間は未定」）／
`docs/screens/SC-10_operations-dashboard.md`（表示は変わらないが、表示されない記録の統制を注記する）／
`docs/tests/FR-10_dashboard.md`（テストケース表）／
`docs/operations/operations.md`（保持期間の節を新設）。
**除外**: `docs/api/openapi.yaml`（`UsageEventRequest` は `eventType` / `query` の 2 項目で
利用者の欄が無い ＝ 契約は変わらない）・`docs/how-to/plan-id-range-history-annex.md`（無関係）・
`FR-08` 系 4 文書（別エンティティ）。

🔴 **`docs/data/usage-event.md` は issue の宣言ファイル領域に無いが、`UserId` の属性表を持つ
唯一のデータ仕様書である**（走査 4 で判明）。**領域へ追加する**（並列の相手 3 件はいずれも
この文書を宣言していない）。

### 走査 5 — 是正で新たに誤りになる自分の記述（規則 10）

`UserId` を落とすと「実行利用者を記録する」と述べた記述が誤りになる。**是正後の語**
（`利用者` / `記録者` / `anonymous`）で引き直した:

```console
$ git grep -rn "anonymous" -- src/knowledge/backend/Services/DashboardService/ docs/
Features/Dashboard/RecordEvent/Endpoint.cs:7, :20
docs/functional/FR-10_dashboard.md:35
→ 3 件。すべて本作業で書き換える
```

## やること（実装方針）

### 1. `UserId` を落とす

- `UsageEvent.UserId` を削除し、`Create(string eventType, string? query)` の 2 引数にする。
- `DashboardDbContext` の `e.Property(u => u.UserId)` を外す。
- 受け口 `RecordEvent/Endpoint.cs` は **`RequireAuthorization()` を維持**し、主体解決
  （`http.User.Identity?.Name`）も**維持する**。ただし解決した値は**列へ書かない**。

  🔴 **「維持する」を「呼び出しを残す」で満たさない。** `ADR-0072` 決定 1 が維持すると
  述べたのは**認証の統制**であり、未使用のローカル変数を 1 行残すことではない。
  **JWT からの主体解決は認証パイプラインが行い、解決できなければ 401 になる** ——
  終端が値を受け取らなくなっても、その筋は変わらない。**終端から `HttpContext` を外し、
  統制はテスト 2 本（未認証 401 / 認証済み一般利用者 201）で機械が押さえる**（後述の判断 A）。

### 2. 移送マイグレーション

`dotnet ef migrations add DropUsageEventUserId` で 1 本足す。`Up` は `DropColumn`、
`Down` は `AddColumn`（`nullable: false` ＋ `defaultValue: ""`）。

🔴 **既存行の `UserId` は失われ、復元できない**（`Down` で戻しても空文字が入るだけである）。
`ADR-0072` §残るもの が受け入れ済みのトレードオフとして明記している。**行そのものは残る。**

### 3. 保持期間 90 日の定期削除

| 部品 | 置き場所 | 役割 |
| --- | --- | --- |
| `UsageRetentionOptions` | `Features/Dashboard/` | 掃除の有無と間隔。**保持日数は持たない**（判断 B） |
| `UsageEventRetention` | `Features/Dashboard/PurgeExpired/` | 1 周ぶんの削除（`PurgeExpiredAsync`） |
| `UsageRetentionHostedService` | `Features/Dashboard/PurgeExpired/` | `PeriodicTimer` で回す常駐処理 |

**形は `NotificationMaintenanceHostedService` ＋ `NotificationRetention`（platform 側の前例）を
なぞる** —— `BackgroundService` ＋ `PeriodicTimer` ＋ `IServiceScopeFactory` で DbContext を
毎周スコープから取り、1 周の失敗では止めない。

**削除の述語は集計の否定である**: 集計は `u.OccurredAt >= since`、削除は `u.OccurredAt < cutoff`
で、`since` と `cutoff` は**同じ式**（`DashboardEndpoints.SinceUtc(MaxDays)`）から得る。

### 4. 文書の追随

走査 4 の 5 文書 ＋ 実装ADR `IADR-0367` ＋ 索引。

## 判断（IADR-0367 へ書く）

### A. 主体解決の「維持」の形

**維持するのは認証の統制であり、コード片ではない。** `RequireAuthorization()` は維持し、
主体の解決は認証パイプライン（`AddPlatformAuth`）が従来どおり行う（**解決できなければ 401**）。
終端は `HttpContext` を受け取らなくなる —— 未使用のローカル変数を「維持の証」として残す形は
採らない（警告になるうえ、「まだ使う気がある」と読める）。**統制が残っていることは
T-71 / T-72 の 2 本が押さえる**（`RequireAuthorization()` を消せば前者が、
管理者限定にすれば後者が落ちる）。

### B. 保持日数を構成キーにしない

🔴 **`ADR-0072` §残るもの 末尾は「集計の上限を変えるときは保持期間も同時に見直す（片方だけ
動かすと、照会できるのに行が無い期間が生じる）」と定めている。** 保持日数を独立の構成キーに
すると、**その事故を運用時に起こせる形をわざわざ作る**ことになる。

したがって保持日数は**構成キーを持たず**、`DashboardEndpoints.MaxDays`（集計の上限）を
そのまま使う。**構成で変更できるのは掃除の有無と間隔だけ**である。間隔の不正値は既定へ倒し、
**ログに出す値は倒した後の値**にする（`IADR-0357` / `IADR-0353` の作法）。

**これは orchestrator の指示（保持日数も構成可）からの意図的な逸脱である**。理由は上記のとおり
計画 ADR §残るもの と衝突するためで、`CLAUDE.md`「ADR で確定した制約の無断逸脱」を避ける
判断として `IADR-0367` に残す。

### C. 削除は `RemoveRange`（`ExecuteDelete` を使わない）

テストは InMemory プロバイダで走り、`ExecuteDeleteAsync` は InMemory で動かない。
**同じサービス内の前例（`KnowledgeHealth/Report`）も `RemoveRange` である。**
無制限に読み込まないよう**1 周を上限件数で区切り、区切りに達したら次周へ送る**。

## テスト

| ID | 種別 | 内容 |
| --- | --- | --- |
| T-73 | 陽性 | 91 日前の行は掃除で消える |
| T-74 | 陰性 | 89 日前の行は残り、`GET /dashboard/summary?days=90` に出る |
| T-75 | 境界 | 削除の基準時刻ちょうど（`SinceUtc(90)`）の行は**残る**（`>=` の側） |
| T-76 | 境界 | 基準時刻の 1 ティック前の行は**消える** |
| T-70 | 構造 | `UsageEvents` に利用者識別子に相当する列（プロパティ）が無い |
| T-71 | 認可 | 未認証の `POST /dashboard/events` は **401** |
| T-72 | 認可 | 認証済み一般利用者の `POST /dashboard/events` は **201**（既存 T-09 が押さえる範囲を明示） |
| T-77 | 構成 | 掃除の間隔の不正値は既定へ倒れる（**報告する値も倒した後の値**） |
| T-78 | 構成 | 保持日数は集計の上限と同じ 1 つの定数から来る（構成キーを持たない） |
| T-79 | 結線 | 常駐処理を有効にすると起動直後の 1 周で古い行が消える（陽性） |
| T-80 | 結線 | `Enabled=false` では消えない（陰性） |

**変異試験 1 本（実施済み）**: `UsageEventRetention` の `u.OccurredAt < cutoff` を外す（全件削除にする）と
**T-74 / T-75 の 2 本が落ちた**（`失敗: 2、合格: 55`）。戻して `失敗: 0、合格: 57`・
`grep MUTATION` は 0 件（残渣なし）。

## 受け入れ基準（issue の 12 項目）

issue #1198 の受け入れ基準をそのまま採る。**契約は変わらないはず**であり、
`docs/api/openapi.yaml` の再生成差分が出たら設計を見直す。

## 実測（稼働 k3s・2026-09-04）

**DashboardService のイメージだけ差し替えた**（`kubectl set image` で `:issue1198` へ。
他の Pod は再起動していない）。クラスタは実測前に `kubectl get pods -A` で確認し、
作り直し中ではなかった（MSP の Pod は 21〜24 時間・基盤は 4〜47 日稼働）。

### 差し替え前

```console
$ kubectl -n platform-infra exec postgres-544775c474-9b2sd -- psql -U kp -d dashboard_svc     -c '\d "UsageEvents"' -c 'SELECT count(*) FROM "UsageEvents";'
   Column   |           Type           | Collation | Nullable | Default
------------+--------------------------+-----------+----------+---------
 Id         | uuid                     |           | not null |
 EventType  | character varying(16)    |           | not null |
 Query      | character varying(512)   |           |          |
 UserId     | character varying(256)   |           | not null |   ← 実在する
 OccurredAt | timestamp with time zone |           | not null |
 count
-------
    20
```

**探り行を 2 件仕込んだ**（陽性と陰性を対で置く）。

```console
$ ... INSERT ... ('search','issue1198-old-91d','probe-user', now() - interval '91 days'),
                 ('search','issue1198-new-89d','probe-user', now() - interval '89 days');
INSERT 0 2
 issue1198-old-91d | probe-user | 2026-06-05 10:30:02.110591+00
 issue1198-new-89d | probe-user | 2026-06-07 10:30:02.110591+00
 count = 22
```

### 差し替え後

```console
$ kubectl -n microservices-platform set image deployment/dashboard-service     dashboard-service=k3d-local/microservices-platform/dashboard-service:issue1198
deployment.apps/dashboard-service image updated
$ kubectl -n microservices-platform rollout status deployment/dashboard-service --timeout=300s
deployment "dashboard-service" successfully rolled out
```

```console
$ ... -c '\d "UsageEvents"' -c "SELECT ... WHERE \"Query\" LIKE 'issue1198%' ..."
   Column   |           Type           | ...
------------+--------------------------+ ...
 Id         | uuid                     |
 EventType  | character varying(16)    |
 Query      | character varying(512)   |
 OccurredAt | timestamp with time zone |     ← **UserId が無い**
Indexes:
    "PK_UsageEvents" PRIMARY KEY, btree ("Id")
    "IX_UsageEvents_OccurredAt_EventType" btree ("OccurredAt", "EventType")

       Query       |          OccurredAt
-------------------+-------------------------------
 issue1198-new-89d | 2026-06-07 10:30:02.110591+00   ← **89 日前は残る（陰性）**
(1 row)                                              ← **91 日前は消えた（陽性）**

 count = 21          ← 22 → 21。**消えたのは 1 行だけで、移送で行は失われていない**

 MigrationId
 20260904100741_DropUsageEventUserId    ← 当たっている
```

```console
$ kubectl -n microservices-platform logs deploy/dashboard-service -c dashboard-service | grep 保持
利用イベントの保持期間の削除を開始する（保持 90 日・間隔 360 分）。
保持期間（90 日）を過ぎた利用イベントを 1 件削除した。
```

```console
$ kubectl -n microservices-platform exec dashboard-service-558b9c4fc8-5c4bh -c istio-proxy --     curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json'     -d '{"eventType":"search","query":"issue1198-probe"}' http://localhost:8080/dashboard/events
401     ← **RequireAuthorization() は維持されている**
```

**探り行は実測後に削除した**（`DELETE 1` → `count = 20`。差し替え前の状態へ戻した）。

### 実測していないこと

- **認証済み 201 の稼働実測**（受け入れ基準 4 本目）。realm から利用者トークンを取る経路が
  本作業の射程外であり、**T-72（サービス層のテスト）で押さえた**。401 は上記のとおり実測済みで、
  **「誰が呼べるか」の上側だけが未実測**である。
- **画面表示の不変**（受け入れ基準 7 本目）。集計端点は管理系ロールの JWT を要するため
  クラスタでは叩いていない。**T-74 が `GET /dashboard/summary?days=90` の内容で押さえている。**

## やらないこと

- 目的外利用の禁止・開示請求への対応の明文化（`ADR-0072` §残るもの が適用対象を失うとした）
- ハッシュ化による一意カウント（決定 4 が将来の別 ADR へ送った）
- 応答契約の変更（利用者の欄はもともと無い）
- 合成トラフィックの除外（`ADR-0076` 決定 4 の追随作業。別 issue）

---
title: 作業仕様書 — 陳腐化文書数（stale-documents）を「本文更新起点・180 日」で生産し、件数としきい値を受け口まで運ぶ
type: spec
status: done
related_ids:
  - FR-10
  - FR-17
  - FR-19
  - UC-05
  - SC-10
  - ADR-0002
  - ADR-0006
  - ADR-0033
  - ADR-0050
  - ADR-0054
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - "06_technical/05_observability-ops.md §ナレッジ健全性の指標（集計範囲・2026-08-02 確定）— 7 指標と 4 規則"
  - "ADR-0050 決定 2（本文指紋。タグ・属性の更新を本文の変更と見なさない）"
  - "planning#494（2026-08-29 CLOSED / COMPLETED。しきい値 180 日・起点は本文の更新のみ・配備時構成で変更可・SC-10 に件数としきい値を併記）"
related_adrs:
  - IADR-0357
  - IADR-0299
  - IADR-0265
  - IADR-0242
  - IADR-0281
issue: "#1186"
---

# 作業仕様書: 陳腐化文書数（stale-documents）の生産

## 起点

planning#494 が 3 論点を確定させた（2026-08-29 CLOSED / COMPLETED、planning#499 `03c4bfe`）。

| # | 論点 | 裁定 |
| --- | --- | --- |
| 1 | しきい値の日数 | **180 日**。初期値であり運用開始後の実測で改める |
| 2 | 起点となる時刻 | **本文の更新のみ。タグ・属性の更新は起点にしない** |
| 3 | 運用者が変更できるか | **配備時の構成で変更できる。SC-09 の辞書運用には載せない**。**SC-10 には件数と現在のしきい値を併記する** |

論点 2 の決め手は計画の言葉で「**指標が自分の改善作業で消えるなら、それは測定ではない**」。
タグ整理で件数が減る指標は測定ではない。

受け皿は #1186。トラッカーは #443（2026-09-03 の棚卸しが「引き受け先が無い」と記録していた）。

## 母集合（着手前に私が自分で引いた。issue の本文からは転記していない）

### 走査 1 — 指標の**算出**経路

```console
$ grep -rn "BodyHash" src/knowledge/backend/Services/GraphService --include=*.cs | grep -v "/Tests/"
Domain/GraphDocument.cs:26,46,63
Features/GraphDocuments/Sync/GraphDocumentSyncConsumer.cs:69,130
Infrastructure/Persistence/GraphDbContext.cs:24
（ほかはマイグレーションの Designer / スナップショット 6 件）
```

```console
$ grep -rn "\.UpdatedAt" src/knowledge/backend/Services/GraphService --include=*.cs | grep -v "/Tests/"
Domain/GraphTraversal.cs:216            ← 探索の間引き順（GraphThinning.Updated）。本件と別用途
Features/GraphDocuments/Sync/GraphDocumentSyncConsumer.cs:64,70,75  ← 順序ガード
Infrastructure/Persistence/GraphDbContext.cs:25
```

### 走査 2 — 指標の**運搬**経路

| 段 | 実体 |
| --- | --- |
| 収集 | `GraphService/Features/KnowledgeHealth/Report/KnowledgeHealthCollector.cs` |
| 周期・排他 | 同 `KnowledgeHealthHostedService.cs`（1 時間・Postgres advisory lock） |
| 送出口 | `GraphService/Domain/Ports/IKnowledgeHealthReporter.cs` |
| 送出 | `GraphService/Infrastructure/ExternalServices/HttpKnowledgeHealthReporter.cs` → `POST /internal/knowledge-health/observations` |
| 受け口 | `DashboardService/Features/KnowledgeHealth/Report/{Command,Endpoint}.cs`（**指標 1 つ分の全量スナップショット置換**） |
| 保存 | `DashboardService/Infrastructure/Persistence/DashboardDbContext.cs`（`KnowledgeHealthObservations`） |
| 集計・閲覧 | `DashboardService/Features/KnowledgeHealth/View/{Query,Endpoint}.cs`（`GET /dashboard/knowledge-health`。運用者・管理者限定） |

### 走査 3 — 指標の**表示**経路（陰性。陽性対照つき）

```console
$ grep -n "Map" src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DashboardBffEndpoints.cs
21:        var g = app.MapGroup("/bff/dashboard").WithTags("Dashboard BFF");
31:        g.MapGet("/summary", ...
```

🔴 **`/bff/dashboard/knowledge-health` は存在しない。** BFF が持つのは `/summary` 1 本だけである。
陽性対照は同ファイルの `/summary`（在る）。したがって **SC-10 の画面から健全性指標へ到達する経路は
BFF の段で切れている**。issue の やること 6 が「節を開かない」と定めているのと整合する
（節を開く判断は planning#494 が「別の判断」と明記）。

### 走査 4 — 生産側の欠落（陰性。陽性対照つき）

```console
$ grep -rn "BodyUpdatedAt|body_updated|BodyChangedAt|StaleDocument" src --include=*.cs | grep -v /Tests/
DashboardService/Domain/KnowledgeHealth.cs:21  StaleDocuments = "stale-documents"   ← 受け口
DashboardService/Domain/KnowledgeHealth.cs:41  （All の並び）
（GraphService 側は 0 件）
```

陽性対照: 同じ走査の `BodyHash` は `GraphService/Domain/GraphDocument.cs` に 3 件当たる。
走査そのものは GraphService へ届いているので、**生産側に本文更新時刻も指標定数も無い**は「無い」である。

### 走査 5 — 🔴 issue の主張の検証（**1 点だけ言い直しが要る**）

issue は「`GraphDocument.UpdatedAt` は**タグ改名でも**進む」と書いている。**発行側を読むと、
経路によって成立と不成立に分かれる。**

| 経路 | 本文 | `Document.UpdatedAt` | `ContentFingerprint` | `GraphDocument.UpdatedAt` |
| --- | --- | --- | --- | --- |
| `Features/Documents/UpdateMetadata/Endpoint.cs` → `Document.UpdateMetadata`（`Touch()` を呼ぶ） | 不変 | **進む** | 不変 | 🔴 **進む** |
| `Features/Documents`（`Document.Update`。タイトル・属性・タグ。`Touch()` を呼ぶ） | 不変 | **進む** | 不変 | 🔴 **進む** |
| `Features/Tags/Rename/Endpoint.cs` | 不変 | **進まない**（`tag.Rename` は Tag 行だけを触り、Document 行は書き換えない。[[IADR-0153]] 決定 1・3） | 不変 | 進まない（`d.UpdatedAt` をそのまま再発行するため） |

**結論は変わらない**——`UpdatedAt` は**本文を変えないメタデータ更新で前進する**（`UpdateMetadata` /
`Update`）ので、そのまま使うと決定 2 が名指しで禁じた事故が起きる。**タグ改名だけは、たまたま
現在の実装では前進しない。** issue の理由づけの一部（タグ改名）は現行実装では当たらないが、
**同じ再発行経路（`DocumentEndpoints.PublishUpdatedAsync`）を通るため、Tag の改名が将来
Document を touch する実装に変われば即座に当たる**。したがって判定材料を `UpdatedAt` から
外す結論は保つ。**この差は IADR へ記録する**（issue の記述をそのまま写さない）。

材料が `BodyHash` であることは走査 1 で裏が取れている——`GraphDocumentSyncConsumer` は既に
**指紋の変化だけ**を契機に却下解除とリンク抽出を回している（ADR-0050 決定 2・3）。

## 決めること（実装 ADR: [[IADR-0357]]）

1. **本文が変わったときにだけ前進する時刻**を `GraphDocument` に持たせる（`BodyUpdatedAt`）。
2. 既存行の扱い（backfill か「不明」か）。
3. しきい値の構成キーと、不正値のときの倒し方。
4. しきい値を受け口へ運ぶ形（0 件でも表示できる必要がある）。

## 設計

### 1. `GraphDocument.BodyUpdatedAt`（**非 null**。マイグレーションで `UpdatedAt` を写す）

- `Create`: `BodyUpdatedAt = updatedAt`（新規行の初期値）。
- `TryApply`: **`bodyHash` が非 null かつ保持中の `BodyHash` と異なるときだけ**前進させる。
  - `null` は「指紋化できなかった＝不明」であり変更と見なさない（`GraphDocumentSyncConsumer` の
    却下解除・リンク抽出と同じ規律。ADR-0050 決定 2）。
  - 判定を**ドメインに置く**（消費側に置くと、次に消費側が増えたとき規律が割れる）。
- 順序ガード（`updatedAt < UpdatedAt` なら不適用）は従来どおり先に効く。

**backfill を採る理由**（「null は不明として数えない」を採らない理由）:

| 案 | 帰結 |
| --- | --- |
| **A. `UpdatedAt` を写す（採用）** | `UpdatedAt >= 実際の本文更新時刻` なので、既存文書は**実際より新しく見える**。**偽陽性は出ない**（新しい文書を陳腐と数えない）。真に陳腐な文書も、遅くとも移行から 180 日以内には必ず数えられる |
| B. null ＝不明として数えない | 既存文書（実データ 2,368 件）が**本文を編集されるまで恒久的に母集合から外れる**。指標はほぼ 0 を返し続ける。planning#494 が名指しした「**その 0 は『問題なし』と読める**」失敗そのものである |

### 2. しきい値

- 既定 **180 日**をコードに置く（`KnowledgeHealthOptions.DefaultStaleDocumentThresholdDays`）。
- 構成キー `KnowledgeHealth:StaleDocumentThresholdDays`（環境変数
  `KnowledgeHealth__StaleDocumentThresholdDays`）。`appsettings.json` に 180 を明記する。
- **SC-09 のタグ辞書運用には載せない**（planning#494 決定 3。辞書はドメインの語彙であり、
  観測のしきい値は運用パラメータである）。
- 🔴 **不正値（0 以下）で起動を落とさない。** `ValidateOnStart` は本サービスの
  `DocumentUpdated` / `DocumentDeleted` 購読ごと落とす。既存の
  `HttpKnowledgeHealthReporter` が fail-open を選んだ理由（「**指標の送出失敗で購読を止めない**」）と
  同じ向きで、**既定値へ倒し、警告ログを出し、報告に添えるしきい値も実際に使った値（180）にする**。
  嘘の数字を画面へ出さないことがここでの要点である。

### 3. 収集（`KnowledgeHealthCollector.CollectStaleDocumentsAsync`）

- 判定: `BodyUpdatedAt < now - threshold`（**境界は「しきい値ちょうどは陳腐でない」**。
  180 日ちょうどは含めず、181 日目から含める）。
- 現在時刻は `TimeProvider`（既に Singleton 登録済み）から採る——テストから決定的に測るため。
- **孤立文書と同じく `doc_scope` を添える**（集合帰属で判定。否定で書かない）。
- **0 件でも送る**（スナップショット置換）。

### 4. しきい値の運搬

`KnowledgeHealthObservation` は 1 件ごとの値であり、**0 件のときに消える**。しきい値は
「件数が 0 でも表示する」必要があるため、**観測値の外側（報告 1 通の属性）**として運ぶ。

- 送出: `IKnowledgeHealthReporter.ReportAsync(..., int? thresholdDays = null)`。
  **null のときは本文へ `thresholdDays` を出さない**（孤立文書の本文は 2 項目のまま。既存テスト
  T-42「2 項目ちょうど」を壊さない）。
- 受け口: `KnowledgeHealthReportRequest.ThresholdDays`（省略可・**0 以下は 400**）。
  指標ごとに 1 行だけ持つ表 `knowledge_health_indicator_thresholds` へ upsert し、
  **null の報告では当該行を消す**（スナップショット置換の姿勢を揃える）。
- 閲覧: `KnowledgeHealthIndicatorDto(Indicator, Count, ThresholdDays)`。
  しきい値を持たない指標では null。

### 5. 画面（SC-10）

🔴 **「ナレッジ健全性」節は開かない。** 生産者の無い 3 指標（`unresolved-links` /
`unsummarized-clusters` / `edge-type-usage`）が 0 件として並ぶ。
`OperationsDashboardPage.test.tsx` の否定テストは**そのまま緑で残す**。
本作業で直すのは、その否定テストと画面のコメントに残る**「しきい値の裁定待ち」という
古い理由づけ**だけである（規則 10: 是正のたびに、新たに誤りになる自分の記述を引き直す）。

## 影響ファイル（宣言領域）

| ファイル | 変更 |
| --- | --- |
| `GraphService/Domain/GraphDocument.cs` | `BodyUpdatedAt` の新設と前進規則 |
| `GraphService/Domain/KnowledgeHealthIndicators.cs` | `StaleDocuments` の追加 |
| `GraphService/Domain/Ports/IKnowledgeHealthReporter.cs` | `thresholdDays` の追加 |
| `GraphService/Features/KnowledgeHealth/Report/KnowledgeHealthOptions.cs` | 新規 |
| `GraphService/Features/KnowledgeHealth/Report/KnowledgeHealthCollector.cs` | 収集の追加・古いコメントの是正 |
| `GraphService/Infrastructure/ExternalServices/HttpKnowledgeHealthReporter.cs` | 本文へ `thresholdDays` |
| `GraphService/Infrastructure/Persistence/GraphDbContext.cs` ＋ 新規マイグレーション | 列と backfill |
| `GraphService/{Program.cs,appsettings.json}` | 構成の束縛 |
| `GraphService/Tests/Features/KnowledgeHealth/Report/*` | 受け入れ基準の写像 |
| `DashboardService/Domain/KnowledgeHealth.cs` | 古いコメントの是正・しきい値の表 |
| `DashboardService/Infrastructure/Persistence/*` ＋ 新規マイグレーション | しきい値の表 |
| `DashboardService/Features/KnowledgeHealth/{Report,View}/*` | しきい値の受け取りと返却 |
| `DashboardService/Tests/Features/KnowledgeHealth/*` | 同上 |
| `knowledge/frontend/.../OperationsDashboardPage.{tsx,test.tsx}` | **コメントのみ**（節は開かない） |
| `docs/functional/FR-10_dashboard.md` / `docs/observability/knowledge-health-indicators.md` / `docs/screens/SC-10_operations-dashboard.md` | 追随 |
| `.ai-context/adr/IADR-0357_*.md` ＋ `README.md` | 新規・索引 |

**除外した領域と理由**:

- `GraphService/Features/AiSuggestions/**` —— 並行 issue #1187 の宣言領域。触らない。
- `deploy/helm/microservices-platform/values.yaml` —— 既定 180 のままで動くため上書きは不要。
  並行 #1135（edge / Dockerfile）と衝突し得るので**触らない**。構成キーは文書に書く。
- BFF / `Knowledge.Contracts` —— 健全性指標は BFF に載っていない（走査 3）。
  **使う側が居ない契約を先に固定しない**（`View/Query.cs` 冒頭の既存の規律）。
  したがって `check-contract-schema.js` の baseline も openapi も動かない。

## 受け入れ基準（#1186 の 9 件の写像）

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | 181 日前に本文更新 → 含まれる | xUnit（境界の外側） |
| 2 | 179 日前に本文更新 → 含まれない | xUnit（境界の内側） |
| 3 | 🔴 200 日前に本文更新＋昨日タグ付け替え → **依然含まれる** | xUnit（**本裁定の中心**。`TryApply` を通した振る舞いで測る） |
| 4 | 200 日前に本文更新＋昨日本文編集 → 含まれない | xUnit（陽性対照。3 と対にする） |
| 5 | 0 件でも報告する | xUnit |
| 6 | 構成で 90 日 → 90 日で判定し、添えるしきい値も 90 | xUnit |
| 7 | 構成なし → 180 日 | xUnit |
| 8 | 個人資料に `doc_scope=private-note` が添う | xUnit（＋陽性対照: 属性なしは null） |
| 9 | SC-10 に「陳腐化文書」が現れない | Vitest（既存の否定テストが緑のまま） |

追加（planning#494 決定 3 の「併記」を画面が読める形にする分）:

| # | 基準 | 試験 |
| --- | --- | --- |
| 10 | 受け口が `thresholdDays` を保存し、`GET /dashboard/knowledge-health` が件数と併せて返す | xUnit（DashboardService） |
| 11 | しきい値を持たない指標では null（孤立文書の本文は 2 項目のまま） | xUnit（両側） |

## 実測（稼働 k3s。2026-09-03 実施）

GraphService / DashboardService のイメージだけ `kubectl set image` で差し替えた。
**他の Pod は再起動していない。**

| # | 測ったこと | 結果 |
| --- | --- | --- |
| 1 | 2 サービスのロールアウト | `successfully rolled out`（両方） |
| 2 | マイグレーションの適用と backfill | `graph_documents` 8 行すべてで `BodyUpdatedAt` = `UpdatedAt` |
| 3 | 受け口の表 | `KnowledgeHealthIndicatorThresholds`（`Indicator` が主キー）が作られた |
| 4 | 1 周期後の報告 | `stale-documents` の観測値 1 件 ＋ しきい値 180 |
| 5 | 🔴 陰性対照（下記） | `UpdatedAt` が 4 日前の文書が陳腐として数えられた |

### 🔴 陰性対照の置き方（測定の設計）

計画の決定 2 が禁じたのは「メタデータだけの更新で件数が減る」ことである。**それを測るには、
`UpdatedAt` と `BodyUpdatedAt` が食い違う文書が要る。** そこで 1 件だけ
**本文が 200 日前・`UpdatedAt` は 4 日前**の状態を作った（メタデータだけを最近更新された
古い文書と同じ状態）。残り 7 件は両方とも数日前である。

**この置き方なら、判定がどちらの列を見ているかで結果が分かれる** ——
`UpdatedAt` を見ていれば 0 件、`BodyUpdatedAt` を見ていれば 1 件。

```console
$ kubectl logs deploy/graph-service -c graph-service --since=15m | grep 健全性
ナレッジ健全性の観測値を報告した（indicator=orphan-documents count=8）。…
ナレッジ健全性の観測値を報告した（indicator=stale-documents count=1 thresholdDays=180）。…

$ psql -d dashboard_svc -c 'select * from "KnowledgeHealthIndicatorThresholds";'
 stale-documents |           180 | 2026-09-03 01:45:02.837965+00

$ psql -d dashboard_svc -c 'select "Indicator", count(*) from "KnowledgeHealthObservations" group by 1;'
 orphan-documents |     8
 stale-documents  |     1
```

**件数（1）としきい値（180）が対で受け口まで届いた**（ログ・受け口の表・観測値の 3 点で一致）。
観測値の `DocScope` は空である —— 当該文書が個人資料でないためで、集合帰属の判定が効いている。

測定後に仕掛けは戻した（`BodyUpdatedAt` <> `UpdatedAt` の行が 0 件）。

### 測れなかったこと（正直に残す）

- 🔴 **実際の `DocumentUpdated` イベントを通した陰性対照は測っていない。** メタデータ更新の
  API（`PATCH /documents/{id}/metadata`）は管理者 JWT を要し、realm は
  `directAccessGrantsEnabled: false` で直接付与を閉じているため、認可コード ＋ PKCE の
  一連を回す必要がある。**上の実測が測ったのは「収集が `BodyUpdatedAt` を見ている」ことまで**で
  あり、「`TryApply` が同一指紋で前進しない」ことは xUnit 側（変異試験つき）が押さえている。
- `/bff/dashboard/knowledge-health` は存在しないため、画面経路での確認はできない（走査 3）。

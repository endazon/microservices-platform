---
title: IADR-0407 east-west gRPC の第 5 スライス（ナレッジ健全性の報告）— 一方向の報告は s2s だけで移せるが、REST の「項目を出さない」を presence で運ばないと静かに壊れる
type: impl-adr
status: Proposed
related_ids:
  - FR-10
  - FR-17
  - FR-18
  - FR-19
  - NFR-09
  - NFR-16
  - NFR-21
  - UC-05
  - SC-10
  - ADR-0002
  - ADR-0006
  - ADR-0029
  - ADR-0030
  - ADR-0065
  - ADR-0075
  - ADR-0076
  - IADR-0011
  - IADR-0139
  - IADR-0256
  - IADR-0265
  - IADR-0299
  - IADR-0353
  - IADR-0379
  - IADR-0389
  - IADR-0394
  - IADR-0397
  - IADR-0400
  - IADR-0401
  - IADR-0402
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 3・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability.md
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-library-selection.md §決定
---

# IADR-0407: east-west gRPC の第 5 スライス — ナレッジ健全性の報告（#1255）

- 状態: Proposed
- 日付: 2026-09-06
- 決定者: claude（実装）

## 起点・関連

- 計画: `ADR-0029` §決定（east-west 同期は gRPC。proto は呼び出される側が所有）／
  `ADR-0075` 決定 3・5・6（一括移行の義務を緩めない・実装 ADR で REST 継続を自認しない・
  基盤先行は MSP 自身を含む）／`ADR-0006`・`ADR-0076` 決定 3（可観測性と `absent` 系アラート）／
  `ADR-0065` 決定 2（操作の実体は `Features/<操作>/`）／`ADR-0030` §決定（検証は FluentValidation・
  ProblemDetails 変換は API 層）／`ADR-0002`（DB-per-service。集計主体が生産できない理由）
- 実装 ADR: [[IADR-0379]]（先行条件の 4 決定。**本 IADR はこれを変えない**）／
  [[IADR-0397]]・[[IADR-0400]]・[[IADR-0401]]・[[IADR-0402]]（先行 4 スライス）／
  [[IADR-0299]] 決定 2・3・4（送出は HTTP・単一書き手・受け口の認証を外した裁定）／
  [[IADR-0265]]（個人資料の除外は**受け手**が強制する）／[[IADR-0353]] 決定 3・4（しきい値の運び方）／
  [[IADR-0389]] 決定 1・5（内訳の軸・**受理されたときだけ数える**）／
  [[IADR-0256]] 決定 3（故障を「該当なし」に化けさせない）／[[IADR-0139]] 決定 1（束ねの 6 条件）／
  [[IADR-0394]]（MeterListener の probe は Meter インスタンスで絞る）
- 実装ガイド（人が読む正）: `docs/api/east-west-grpc.md`（本 IADR で「6 つ目の面」を追記した）
- 実装仕様書: `.ai-context/specs/20260906_issue-1255_knowledge-health-grpc.md`
- issue: #1255

## コンテキストと課題

先行 4 スライスは **問い合わせ**（埋め込み・生成・スコープ解決・名簿・文書読み取り）を移した。
残っているものの性質はそこと違う —— **一方向の通知・報告**である。

着手時に母集合を引き直した（基点 `origin/develop` `2bb41b6d`。`--is-shallow-repository` = `false`）。
単位は**呼び出し箇所**であり、資格情報の有無は**送る側の行**で引いた（[[IADR-0402]] 決定 1）。

- east-west 同期呼び出しは **49 箇所**。内訳は **移行済み 16 / 利用者の資格情報を運ぶ 27 / 残 6**。
- 陽性対照として [[IADR-0402]] の BFF の実測（28／23／5／うち移行済み 4）を**同じ判定軸で再現できた**。
- 陰性対照: `DocumentService/ObsidianSyncEndpoints` は #1255 の射程外リストに名指しされているが、
  **そもそも母集合に入らない** —— `Headers.Authorization` を読むのは**自分の受け口の資格情報**
  （同期トークン）としてであり、後段への転送ではない。**ファイル名ではなく送出行で引いたから分かった。**
- **issue #1255 本文の「残 31 本」は古い。** 内訳つきの現況は上記である。

残 6 のうち本スライスが移すのは 1 つ（GraphService → DashboardService の観測値報告）である。

## 検討した選択肢

### 1. 候補 2 本（Document→Notification / Graph→Dashboard）を束ねるか

[[IADR-0139]] 決定 1 は「裁定済みの同型な契約追加」の束ねを 6 条件つきで許す。**A と F を満たさない。**

| 条件 | 判定 | 実測 |
| --- | --- | --- |
| A. 同一資源 | ❌ | 呼び出し先が別サービス・別ユニット・別 DTO 群であり、proto の置き場すら別プロジェクト（`Platform.Shared.Contracts` / `Knowledge.Contracts`）。「1 系統」と定めた計画 ADR も無い |
| F. 契約の追加に閉じる | ❌ | どちらも受け側の配備形が変わる（h2c ポート・helm・compose）。Document→Notification は**realm に新しい confidential client `document-service` と secret の注入経路**を要する（realm の `clients[]`・`users[]` のどちらにも無い ——実測） |

B・C・D・E は満たす。**A を満たさない束は理由の如何によらず作れない**ので 1 本に絞った。

### 2. どちらを先に移すか

| | A. Graph → Dashboard（採用） | B. Document → Notification |
| --- | --- | --- |
| 呼び出し元の s2s 資格情報 | **既に全部在る**（realm の `graph-service` ＋ `service-account-graph-service`／`platform-service`、compose の `ServiceToken__*`、helm の `serviceToken`） | **新設が要る**（realm の client・service account・secret の注入） |
| 契約プロジェクトの codegen | 既に在る（[[IADR-0402]] が `Knowledge.Contracts` へ入れた） | 既に在る |
| 面の広さ | rpc 1 本・呼び出し箇所 1 | rpc 1 本・呼び出し箇所 1 |

**secret を増やす判断は輸送の差し替えとは別の判断である。** #1301 が是正した「`users[]` への
service account 登録漏れ」は、**新しい主体を足すたびに再演し得る**形の事故であり、
輸送の差し替えと同じ PR に混ぜると、赤くなったときにどちらが原因か切り分けられない。

## 決定

### 決定 1: 本スライスは **GraphService → DashboardService の観測値報告 1 経路**だけを移す

proto は `knowledge/dashboard/v1/knowledge_health.proto`（service `KnowledgeHealthReport`・rpc 1 本）で、
置き場は所有者（DashboardService）が属するユニットの共有契約プロジェクト `Knowledge.Contracts` である
（[[IADR-0379]] 決定 1）。**Document → Notification は次のスライスに残す**（残 6 → 5）。

### 決定 2: **一方向の報告は、本文に利用者の文脈を持たない。** だから s2s トークンだけで移せる

先行 4 スライスは「利用者の文脈を本文で運ぶ」ことで [[IADR-0379]] 決定 4 を守った。
本経路には**運ぶべき利用者の文脈が無い** —— 生産者は利用者 JWT を持たない定期処理であり
（[[IADR-0299]] 決定 4 が受け口の認証を外した理由そのもの）、個人資料を集計から外す判定は
**受け手**が持つ（[[IADR-0265]]）。したがって proto の要求に `user_id` / `user_attributes` は現れない。

🔴 **これは「認可が要らない」ではない。** 面には `ServiceCaller`（realm ロール `platform-service`）を掛ける。
REST の受け口は無認証のままなので、**この面は現状より狭い**（[[IADR-0401]] 決定 1・[[IADR-0402]] 決定 3 と
同じ向き）。機械で守るのは「**管理者の利用者トークンでも `PERMISSION_DENIED`**」の 1 本である。

### 決定 3: 🔴 **REST の「項目そのものを出さない」は 3 箇所あり、すべて `optional`（presence）で運ぶ**

現行の送出は「持たない値では**項目を出さない**」形で書かれている（`HttpKnowledgeHealthReporter` の
匿名オブジェクトが分岐して 2 通りの形を作る）。proto3 に null は無いので、写しを落とすと壊れる。

| 契約 | REST の「未指定」の意味 | proto3 の既定 | 壊れ方（**実測。変異試験で赤にした**） |
| --- | --- | --- | --- |
| `threshold_days` | しきい値なし → 受け口は**しきい値の行を削除** | `0` | 検証器が `thresholdDays must be greater than zero` で弾き、**しきい値を持たない 3 指標（orphan-documents / unresolved-links / edge-type-usage）の報告が全部 400 になる** |
| `doc_scope` | 個人資料ではない（`null`） | `""` | 台帳で `null` と区別できなくなる |
| `dimension` | 軸を持たない指標 | `""` | 内訳に `""` という軸が 1 本生まれ、集計が割れる |

🔴 **`threshold_days` の壊れ方は先行スライスより広い。** [[IADR-0400]] の `sent` /
[[IADR-0402]] の `has_body` は**値が化ける**が、こちらは**要求そのものが拒まれる** ——
しかも呼び出し元は fail-open（ログを出して続行）なので、**エラーにならないまま報告が止まり、
受け口の数字は最後の値で凍る**（[[IADR-0299]] のスナップショット置換の性質）。

### 決定 4: 🔴 **判定器を 2 つにしない。** REST の端点と gRPC の rpc は同じ `ReportKnowledgeHealthUseCase` を通る

端点の中に在った本体（検証 → 置換 → しきい値の upsert/delete → 件数）を括り出し、
**両輸送が同じ関数を呼ぶ**（[[IADR-0397]] `EmbedUseCase` / [[IADR-0400]] `CompletionUseCase` /
[[IADR-0402]] `DocumentReadUseCase` と同じ形）。`ADR-0065` 決定 2 の適用としては「1 操作の実体」なので、
その操作のフォルダ（`Features/KnowledgeHealth/Report/`）に置く。

失敗は Kernel の `Result` で返し、**輸送への写像は 1 度だけ**行う（`ADR-0030` §決定）:
`ErrorKind.Validation` → `INVALID_ARGUMENT`（REST の 400）／それ以外 → `INTERNAL`（REST の 500）。

🔴 **`INVALID_ARGUMENT` を `INTERNAL` に畳まない。** 呼び出し元は縮退でログしか残さないので、
status を潰すと「要求の誤り」と「受け口の故障」を運用が切り分けられない。

### 決定 5: 🔴 **縮退の枝を 1 つも増やさず・1 つも減らさない。故障を「該当なし」に化けさせない**

REST 実装の枝は 3 つで、gRPC 実装はそれを写す。

| 事象 | REST | gRPC | 副作用 |
| --- | --- | --- | --- |
| 受理された | 2xx | 例外なし | `RecordDelivered(indicator)` **のみ** |
| 受理されない | 非 2xx → `LogError`（status つき） | `RpcException` → `LogError`（`StatusCode` つき） | **数えない・投げない** |
| 到達できない／s2s トークン取得失敗 | 例外 → `LogError`（到達できない） | `RpcException`(UNAVAILABLE) / `InvalidOperationException` | 同上 |
| 呼び出し元のキャンセル | **伝播させる** | 同じ | — |

🔴 **失敗時に数えないことが `absent` 系アラートの土台である**（[[IADR-0256]] 決定 3 /
[[IADR-0389]] 決定 5 / `ADR-0076` 決定 3）。試みた回数を数える形へ変えると、受け口が死んでいる間も
系列が生き続けて不在が鳴らない。**変異試験で実際に赤にした**（下記 §結果）。

★ タイムアウトは REST 側の `HttpClient.Timeout`（5 秒）を **deadline** で写す。値は
`HttpKnowledgeHealthReporter.SendTimeout` を**そのまま引く**（書き写すと片方だけ動いたとき気付けない）。
期限切れ（`DeadlineExceeded`）は上の「受理されない」枝と同じ縮退になる。

### 決定 6: 切替は `Services:DashboardServiceGrpc` の有無。**チャネルはキー付き**

未設定なら**何も登録せず** REST のまま（並走中の正は REST。戻すのは構成を外すだけ）。

🔴 **GraphService は 3 つ目の宛先を持つ最初のサービスである。** 認可サービス宛は
`AddAuthzScopeGrpcClient` が**キー無し**で、LlmGateway 宛は `AddLlmGatewayGrpcClient` が
**キー付き**で登録する。3 つ目をキー無しで足すと、観測値の報告が認可サービスへ繋がる
（[[IADR-0400]] / [[IADR-0402]] 決定 6 と同じ理由）。

🔴 **切替の試験は「登録関数」に対して置く。** `Program.cs` の選択は組み立て時
（`builder.Configuration[...]`）に行われ、`WebApplicationFactory` が差し込む構成は Build 時に載るため、
**テストホストの構成で DI の切り替わりを測ることはできない**（`SimilaritySourceWiringTests` の注記と同じ罠）。

### 決定 7: **realm は変えない。** 呼び出し元の主体は既に在る

`clients[]` の `graph-service`（`serviceAccountsEnabled: true`）と `users[]` の
`service-account-graph-service`（`realmRoles: ["platform-service"]`）を**読んで確かめた**
（`deploy/keycloak/microservices-platform-realm.json` 869-872 行）。
compose の `ServiceToken__ClientId: graph-service`・helm の `services.graph.serviceToken` も既に在る。

🔴 **「在るはず」で済ませない。** [[IADR-0379]] 決定 4 の散文は BFF について
「realm に service account と `platform-service` を付けた」と書いていたが**事実に反していた**
（#1301 が是正）。**主体を足さないスライスでも、在ることを読んで確かめる。**

配備で足すのは 2 つだけである: 受け側の h2c ポート（helm `services.dashboard.grpcPort` /
compose の `expose` ＋ `Grpc__Port`）と、呼び出し元の宛先（`Services__DashboardServiceGrpc`）。
**readiness は HTTP の `/health/ready`（8080）のまま**（[[IADR-0379]] 決定 3）。

## 理由

- **決定 2** は移行の順序そのものの根拠である。利用者の文脈を持たない経路は
  [[IADR-0379]] 決定 4 と最初から衝突しないので、token exchange の裁定を待たずに移せる。
  **残 6 のうち 3 つ（本件・Document→Notification・Graph→Document の辞書読み）がこの性質を持つ。**
- **決定 3** を独立の決定にしたのは、`threshold_days` の壊れ方が**要求の拒否**という
  先行スライスに無い形だからである。しかも呼び出し元が fail-open なので**赤くならない**。
- **決定 4** は移行の不変条件そのものである。置換の順序・しきい値の削除・件数の数え方はどれも
  「静かに違う」形で割れる。
- **決定 5** で畳み方を写したのは、**縮退の一般化が観測を壊す**からである。指標の生産者は
  fail-open であり、**届いていないことを示す唯一の手段が「数えないこと」**である。
- **決定 7** は #1301 の教訓の適用である。「変えないから見ない」は、
  「在ると書いてあるから在る」と同じ失敗の形をしている。

## 結果

- 良い影響:
  - east-west 同期呼び出しの残が **6 → 5** になり、**内訳が測定済みの形で確定**した
    （移行済み 17 / 資格情報を運ぶ 27 / 残 5）。
  - `Knowledge.Contracts` に 2 つ目の proto が入り、置き場の規約が 1 回だけの適用でないことが示された。
  - 観測値の受け口の**本体が輸送から分離**され、REST と gRPC が同じ関数を通ることが試験で固定された。
- 悪い影響・トレードオフ:
  - DashboardService が h2c ポートを持つ 4 つ目のサービスになった（Service が複数ポートになる）。
  - gRPC 面は `INVALID_ARGUMENT` を返すが、呼び出し元は fail-open で握るため
    **運用にはログでしか現れない**（REST の 400 と同じ）。これは現行の性質であり、本スライスで変えない。
  - 🔴 稼働クラスタでの h2c 往復は依然として**未実測**（[[IADR-0402]] と同じ。Pod の再起動を要する）。
  - 🔴 Istio の `appProtocol: grpc` が dashboard-service の Service で効くことは、
    **テンプレート描画までしか確かめていない**（実クラスタでは見ていない）。
- 変異試験（実走。赤になった試験名は作業仕様書 §7 と PR 本文に載せた）:
  1. `optional int32 threshold_days` → `int32`（＋ 両側の naive fix）… **DashboardService 4 件が赤**
  2. `RpcException` の枝で `RecordDelivered` を呼ぶ … **GraphService 6 件が赤**
  3. `[Authorize(Policy = ServiceCaller)]` を外す … **DashboardService 3 件が赤**
  4. `doc_scope` / `dimension` を `optional` から落とす … **DashboardService 2 件・GraphService 1 件が赤**
- フォローアップ:
  1. **DocumentService → NotificationService**（`/internal/notifications`）が次のスライス。
     **realm へ `document-service` の confidential client と service account を足す判断を含む。**
  2. **GraphService → DocumentService の `/internal/tags/names`** も資格情報を運ばないが、
     **同じ名前付きクライアントの兄弟（`HttpDocumentTagWriter`）が運ぶ**ため、切り離しの判断が要る。
  3. 検索サービスの属性値照会（BFF の 5 箇所目）と、扇形の 2 経路
     （`/internal/mcp-tools` / `/internal/introspection`）は依然として別のスライスである。
  4. 資格情報を運ぶ 27 箇所は **token exchange の候補**であり、**計画側の裁定を要する**
     （実装側では決められない。[[IADR-0402]] フォローアップ 2 と同じ）。

## 関連

- Supersedes: なし（[[IADR-0379]] の 4 決定は不変。本 IADR はその適用）
- Superseded by: なし

---
title: Document → Notification の私的資料通知を east-west gRPC へ移し、document-service に s2s の資格情報を与える（#1255 残り ②）
type: spec
status: done
related_ids: [FR-19, FR-20, FR-21, FR-22, NFR-09, NFR-16, NFR-19, UC-11, ADR-0004, ADR-0029, ADR-0037, ADR-0045, ADR-0075, IADR-0017, IADR-0026, IADR-0215, IADR-0267, IADR-0270, IADR-0299, IADR-0371, IADR-0379, IADR-0397, IADR-0398, IADR-0400, IADR-0401, IADR-0402, IADR-0408, IADR-0412, IADR-0417, IADR-0419]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: 文書 → 通知の送出を s2s の gRPC へ移す（#1255 残り ②）

## 起点

- FR-22（個人資料まわりの通知。本文は件数・閾値・期限のみ）／FR-19・FR-20・FR-21（個人資料・同期・容量）／
  NFR-09（認可）／NFR-16（east-west の統一）／NFR-19（静かに落ちない）／UC-11（受け口の入力検証）
- 計画 ADR: `ADR-0029`・`ADR-0075`（east-west は gRPC）／`ADR-0004`（サービス間の認証）／
  `ADR-0037` 決定 6・17・18（個人資料の 3 契機）／`ADR-0045` 決定 8（静かに落ちない）
- 実装 ADR: [[IADR-0379]]（proto の置き場・versioning・h2c・s2s・並走）／[[IADR-0215]] 決定 3・5（fail-open と
  発火側の計器）／[[IADR-0270]] 決定 6（検知は文書側・実体は通知側）／[[IADR-0398]]（受け口の検証は形 β）／
  [[IADR-0408]]（一方向報告の移送。縮退の枝を増やさない・減らさない）／[[IADR-0412]] 決定 5（`TryAdd`・
  同じ宛先に 2 本目のチャネルを張らない）／[[IADR-0417]] 決定 4・9（面は狭い側・status で枝を分け直す）／
  [[IADR-0419]]（本 PR で新設）
- issue: #1255（**閉じない**。残り ④⑤ は扇形）

## 現状（着手前に作業ツリーで実測）

| 位置 | 現物 |
| --- | --- |
| 送出側 | `DocumentService/Program.cs` の `AddHttpClient(HttpPrivateNoteNotifier.ClientName)`（`Timeout = SendTimeout` = 5 秒）＋ `HttpPrivateNoteNotifier`（`Infrastructure/ExternalServices`） |
| 受け口 | `NotificationService` の `POST /internal/notifications`（`NotificationIngressEndpoints.IngressPath`）。**無認証**（[[IADR-0017]] / [[IADR-0026]] の内部 API 扱い） |
| 本体 | `NotificationIngress.AcceptAsync`（検証 → 重複判定 → `NotificationPublisher.PublishAsync`）。検証は `NotificationIngressValidator`（FluentValidation・形 β） |
| realm | `document-service` クライアントは**無い**（他 9 サービスには在る） |
| helm | `services.notification` に `grpcPort` **無し**／`services.document` に `serviceToken` **無し** |
| compose | notification に `Grpc__Port` **無し**／document に `ServiceToken__*` **無し** |
| proto | `platform/notification/` は**無い**（既存 proto 10 本） |

## 母集合（規則 1・2・9）

### 1. east-west の残数を自分で数え直した

**`AddHttpClient` を全走査**（`grep -rn "AddHttpClient" src/platform/backend src/knowledge/backend --include=*.cs`。
`/Tests/` と `/obj/` を除外）した。**登録単位で 31 箇所**（`docs/api/east-west-grpc.md` が数えているのは
**呼び出し箇所単位の 49** であり、母集合の粒度が違う ——
issue #1255 本文の「31 本」は登録単位である）。登録を性質で割ると:

| 区分 | 件数 | 内訳 |
| --- | --- | --- |
| 対象外（east-west ではない） | 9 | 外部 SaaS / Wiki.js（3）・オブジェクトストレージ（3）・外部 LLM（1）・IdP（2。BFF セッションと Keycloak Admin） |
| 移行済み（gRPC 面が在る） | 8 | LlmGateway 宛（埋め込み 2・生成 3）・認可宛（共有クライアント 1・DataSource 1・McpServer 1） |
| 利用者の資格情報を運ぶ | 11 | BFF の名前付きクライアント群 ＋ AiAnalysis → Retrieval `/search` |
| **残（s2s だけで移せる）** | **3** | ② 文書 → 通知 ／ ④ McpServer → `/internal/mcp-tools` ／ ⑤ `/internal/introspection` |

⇒ **`docs/api/east-west-grpc.md` 末尾の「残 3」と一致する。** 本 PR は ② を移すので **残 2** になる。
④⑤ は宛先集合が構成で開く**扇形**であり、本リポジトリだけでは完結しない（対象サービスは
`Introspection__Services__*` / MCP の公開構成で決まる）。

🔴 **数え直しの基点は本作業ツリー（`7c9d184`）である。** 文書に残る `49 / 17 / 27 / 5` は 2026-09-06 の
実測であり**書き換えない**（基点が違う数を混ぜない。同文書の注記）。

### 2. 「受け口を開き、かつ呼び出し元に資格情報を与える」ために要るもの

既存の受け口 5 つ（Document / Graph / Authorization / Dashboard / Retrieval）と、既存の**呼び出し元**
9 つ（BFF / Retrieval / Ingestion / AiAnalysis / Graph / Conversion / Wiki / DataSource / McpServer）の**差分**で引いた（誤りの側＝「片方に在って片方に無い」で引く。片側の一覧を写すと漏れが構造的に見えない）。

| 面 | 既存 | 本 PR | 判定 |
| --- | --- | --- | --- |
| proto | `Protos/<unit>/<service>/v1/*.proto`（glob 済み・csproj は不変） | 新設 | ✅ |
| baseline | `scripts/proto-contract-baseline.json` | `--update` | ✅ |
| 受け側パッケージ | `Grpc.AspNetCore`（ホストを起こす側が持つ） | NotificationService へ追加 | ✅ |
| h2c リスナ | `builder.AddPlatformGrpcListener()` | NotificationService へ追加 | ✅ |
| サービス登録 | `app.MapGrpcService<T>()` | 同上 | ✅ |
| helm（受け口） | `services.<name>.grpcPort: 8081` | notification へ追加 | ✅ |
| compose（受け口） | `expose: "8081"` ＋ `Grpc__Port` | 同上 | ✅ |
| 呼び出し元の宛先 | `Services__<X>Grpc`（helm extraEnv / compose env） | document へ追加 | ✅ |
| 呼び出し元の資格（helm） | `services.<name>.serviceToken`（clientId / existingSecret / clientSecretKey） | document へ追加 | ✅ |
| 呼び出し元の資格（compose） | `ServiceToken__ClientId` ＋ `ServiceToken__ClientSecret: ${...}` | 同上 | ✅ |
| realm の client | confidential ＋ `serviceAccountsEnabled` ＋ secret | `document-service` を新設 | ✅ |
| 🔴 realm の `users[]` | `service-account-<clientId>` ＋ realm ロール `platform-service` | **同時に**新設 | ✅ |
| ローカル供給元（Vault / ESO） | `externalsecret-<name>-token.yaml` ＋ `bootstrap.sh` | 新設・追記 | ✅ |
| ローカル供給元（素の Secret） | `scripts/k8s-local-up.sh` の `apply_secret` ＋ 待機一覧 ＋ rollout 一覧 | 追記 | ✅ |
| 試験の器 | `Tests/Grpc/{GrpcKestrelFactory,GrpcServerCollection,GrpcTestConfiguration}.cs` | NotificationService へ新設 | ✅ |

🔴 **`users[]` を母集合へ入れたのは #1301 の穴（client は在るが `users[]` に service account が無く、
ロールが誰にも付いていない）を踏まないためである。** realm の `clients[]` だけ足すと
`platform-service` が付かず、面は**常に `PERMISSION_DENIED`** を返す —— 送出は fail-open なので
**エラーログにしか出ない**。realm を実測して確かめた（既存 9 client はすべて `users[]` に対がある）。

### 除外したものと理由（規則 6）

- **REST 受け口（`POST /internal/notifications`）の撤去・認可付与**: 並走中の正は REST（[[IADR-0379]] 決定 5）。
  無認証のまま残す（[[IADR-0419]] 決定 4）。掛けると**この PR で 2 つのことをする**ことになる。
- **④ McpServer → `/internal/mcp-tools` ／ ⑤ `/internal/introspection`**: 宛先集合が構成で開く扇形であり、
  本リポジトリだけでは完結しない。**別スライス**。
- **`Services__NotificationService`（REST の宛先）の compose / helm への追記**: コード既定
  （`http://notification-service:8080`）が compose のサービス名と文字列一致しており、**現に上書きが要らない**。
  足すと「なぜここだけ書くのか」が読めなくなる。
- **BFF → NotificationService（`/bff/notifications*`）**: 利用者の資格情報を運ぶ側（上表の 11）。
  **同じ宛先だが別の経路**であり、s2s では移せない。
- **通知の読み出し口（`GET /notifications`）の gRPC 化**: 呼び出し元が居ない。
  受け口は書き込み専用のままにする（[[IADR-0419]] 決定 3）。

## 決定

[[IADR-0419]] 決定 1〜7 が正本。実装上の要点だけ再掲する。

1. proto は **platform ユニット所有**（`Platform.Shared.Contracts/Protos/platform/notification/v1/`）。
   `package platform.notification.v1` / `csharp_namespace Platform.Shared.Contracts.Grpc.Notification.V1`。
2. 要求は REST の受け付け DTO と**同じ 6 項目**。null は proto3 の presence で運ぶ
   （`optional int32` ／ 時刻は `google.protobuf.Timestamp` の message presence）。
3. 応答は `duplicate` **だけ**。`id` は出さない（受け手の実体への handle を書き込み専用の面へ出さない）。
4. 面は `ServiceCaller`。**REST の無認証の口は残す**（狭まる向きの非対称は並走の期間だけ）。
5. REST と gRPC は `NotificationIngress.AcceptAsync` という**同じ関数**を通る。
6. 送出側の縮退は**現行の 3 結末を 1 つも増やさず・1 つも減らさない**。
   `UNAVAILABLE` / `DEADLINE_EXCEEDED` ＋ s2s トークン取得失敗 → `unreachable`、
   それ以外の status → `rejected`、成功 → `sent`。呼び出し元のキャンセルだけは伝播させる。
7. 期限は `HttpPrivateNoteNotifier.SendTimeout` を**そのまま引く**（値を書き写さない）。

## 変異試験（すべて実走・戻して緑を確認）

受け口側は 12 本（`GrpcNotificationIngressTests`）、呼び出し元側は 16 本
（`GrpcPrivateNoteNotifierTests`）を母数とする。**戻したことは緑で確認した。**

| # | 変異 | 実測 | 死んだ試験 |
| --- | --- | --- | --- |
| M-1 | 面から `[Authorize(Policy = ServiceCaller)]` を外す | **3 本赤** | 資格情報なし＝`UNAUTHENTICATED` / 管理者トークン＝`PERMISSION_DENIED` / 構造の門 |
| M-2 | 呼び出し側が s2s トークンを付けない（素のチャネルで呼ぶ） | **1 本赤** | h2c 往復の陽性対照 |
| M-3 | `MapGrpcService<NotificationIngressGrpcService>()` を外す | **9 本赤** | gRPC を通る全試験（残る 3 本は構造の門・HTTP ポート・REST の口） |
| M-4 | `AddPlatformGrpcListener()` を外す | **10 本赤** | 上記 ＋ h2c ポートの HTTP/1.1 拒否。**ポートが実際に bind されている証拠**である |
| M-5 | 呼び出し元が全 `RpcException` を `unreachable` へ畳む | **4 本赤** | `rejected` の 4 status（`INVALID_ARGUMENT` / `INTERNAL` / `UNAUTHENTICATED` / `PERMISSION_DENIED`）。🔴 **`unreachable` 側の 2 本は生き残る** —— 片方だけ測ると畳む実装が合格するので、陽性・陰性を対で置いた |
| M-6 | 呼び出し元が `count` / `thresholdPercent` の presence を立てず常に代入 | **1 本赤** | 未設定が `0` に化ける |
| M-7 | `Services:NotificationServiceGrpc` が無くても生成クライアントを登録する | **1 本赤** | 並走の切替が構成 1 つで決まらなくなる |
| M-8 | チャネル登録を `TryAdd` → `Add` | **1 本赤** | 同じ宛先へ 2 本目のチャネルが張られる |

🔴 **M-3 と M-4 が「全滅ではない」ことに意味がある。** M-3 で残る 3 本（構造の門・HTTP/1.1 ポートの生存・
REST の無認証の口）と M-4 で残る 2 本（構造の門・REST の無認証の口）は**面の有無に依存しない
不変条件**であり、これらが一緒に落ちるなら器が壊れている（＝赤が「面が消えた」ことを測れていない）。
M-4 で HTTP/1.1 の試験が落ちるのは、**h2c ポートが存在しなくなる**からであって HTTP 側が消えたのではない。

## 受け入れ基準

- [x] `platform.notification.v1.NotificationIngress` が実 Kestrel の h2c ポートで往復し、通知が永続化される
- [x] 面は `ServiceCaller` を要求する（利用者トークン＝管理者でも `PERMISSION_DENIED`／無資格＝`UNAUTHENTICATED`）
- [x] REST と gRPC が**同じ本体**を通る（同じ入力で同じ通知 1 件・重複判定も同じ）
- [x] REST の口（`POST /internal/notifications`）と HTTP/1.1 のポートが消えていない
- [x] 送出側の 3 結末（sent / rejected / unreachable）が REST 実装と同じ意味で計器に載る
- [x] 送出は fail-open のまま（受け口が拒否・不達でも例外を投げない）／呼び出し元のキャンセルだけは伝播する
- [x] 期限は REST と同じ 5 秒（定数を参照する）
- [x] 切替は `Services:NotificationServiceGrpc` の有無 1 つで決まり、未設定なら DI に 1 つも入らない
- [x] 配備 4 経路（helm / compose / realm / ローカル供給元）が揃っている

## 変えていないもの

- REST の受け口（パス・要求／応答 DTO・検証の鍵と本文・状態コード・**無認証であること**）。
- `IPrivateNoteNotifier` の形（自由文の引数を 1 つも持たない）と 3 契機の発火順（記録が送出より先）。
- 通知の重複判定（ペイロード 6 項目の完全一致）と `NotificationPublisher` の 2 段配送。
- `PrivateNoteNotificationMetrics` の計器名・属性・3 結末の値。

## 実測できていないこと

- 稼働クラスタでの h2c 往復（新イメージの配備＝Pod の再起動を要する）。`docs/api/east-west-grpc.md`
  §未決事項の既知の未実測と同じ性質である。
- realm import の実走（Keycloak を起こしていない）。`check-realm-constraints.js` の静的検査は通した。
- helm のレンダリング（`helm` / `kubeconform` が本環境に無い。`check-deploy-manifests.js` の該当段は skip）。

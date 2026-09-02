---
title: 作業仕様書 FR-22 BFF 通知中継（/bff/notifications* の 2 本。#600 最終トラック）
type: spec
status: in-progress
related_ids:
  - FR-22
  - UC-11
  - ADR-0037
  - ADR-0045
  - IADR-0009
  - IADR-0044
  - IADR-0215
  - IADR-0251
  - IADR-0267
  - IADR-0273
  - IADR-0285
  - IADR-0288
  - IADR-0346
author: Claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-22 要求文・注 4)
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md (決定 6・17・18)
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
related_specs:
  - ./20260816_issue-600_fr22-in-app-notifications.md
  - ./20260823_issue-600_notification-service-backend.md
  - ./20260828_issue-600_notification-triggers.md
  - ./20260828_issue-1025_notification-service-deployment.md
  - ../adr/IADR-0215_notification-service-and-in-app-delivery.md
  - ../adr/IADR-0267_notification-service-backend-subject-scoping-and-send-rate.md
  - ../adr/IADR-0288_notification-service-deployment-and-name-collision.md
  - ../adr/IADR-0346_bff-notification-relay.md
---

# 仕様書: FR-22 BFF 通知中継（#600 最終トラック）

> 🔴 **IADR-0346 の番号は暫定である**（起草時の `develop` の最大は `IADR-0334` だが 0335〜0346 は
> 並行 PR へ割当済み）。**マージ直前に空き番号へ付け直す。** それまで
> `node scripts/check-adr-numbering.js` は欠番で赤になる（既知・意図的）。

> **本作業で #600 を閉じる。** 前段（契約・orval 生成物・ベル UI）と後段（NotificationService・
> 発火の結線・配備）は既に develop にあり、**真ん中の中継 1 本だけが無い**（§母集合の再測定）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-22**（利用者本人への通知。優先度 Should）。**宛先は所有者本人のみ**、
  **本文は件数と期限のみ**。アプリ内通知の実体と送出主体は**実装設計に委ねる**（注 4・2026-08-07 の利用者裁定）。
- ユースケース: **UC-11**（自分の資料を作成・管理し、公開範囲を自ら設定する）の例外フロー。
- 関連 ADR: **ADR-0037** 決定 6・17・18 ／ **ADR-0045**。
- 実装 ADR: **IADR-0215**（送出主体・実体・メール経路・送信レート・発火の検知）／
  **IADR-0267**（本人限定・送出レート。決定 5 が「BFF 端点は #600 継続」と線を引いた）／
  **IADR-0288**（配備・名前衝突）／**IADR-0251・IADR-0273**（BFF セッション / Token Handler）。
- 本作業の判断は **IADR-0346** に置く。

## 母集合の再測定（棚卸しコメントの主張を自分で引き直した）

**他人の数えを転記しない**（`traceability.repo.md` §是正・追随の母集合の取り方 規則 9・10）。
基点は `d561509d`（`origin/develop` を merge した直後。#1150 の `Tests/` 鏡写し移送を含む）。**`git rev-parse --is-shallow-repository` は `false`**
（履歴は打ち切られていない）。

### 軸 1: BFF に実装が無いこと（陰性）＋ 同型の実装が在ること（陽性対照）

```
$ grep -rn "bff/notifications" src/*/backend/
src/platform/backend/Services/NotificationService/Features/Notifications/NotificationDtos.cs:3:      （コメント）
src/platform/backend/Services/NotificationService/Features/Notifications/NotificationEndpoints.cs:8: （コメント）

$ grep -rln "bff/admin/mcp-clients" src/*/backend/          # 陽性対照
src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/McpClientBffEndpoints.cs
src/platform/backend/Bff/Platform.Bff.Tests/BffEndpointCompositionTests.cs
src/platform/backend/Bff/Platform.Bff.Tests/BffMcpClientEndpointTests.cs
```

**陰性の 2 件はいずれもコメントであり、`Bff/` 配下に実装は 1 行も無い。**
同じ走査語の形で `mcp-clients` を引くと実装・合成点テスト・端点テストの 3 件が出る ——
**「当たらないのは走査が壊れているからではない」ことを対で示す**（陰性結論には陽性対照を対で置く）。

### 軸 2: 後段（NotificationService）とテスト件数

```
$ grep -rc "\[Fact\]\|\[Theory\]" --include=*.cs src/platform/backend/Services/NotificationService/Tests/
Features/Notifications/NotificationContractTests.cs:4        Features/Notifications/NotificationOwnerScopingTests.cs:7
Features/Notifications/DispatchEmails/EmailIndependenceTests.cs:5   .../EmailSendRateTests.cs:6
Features/Notifications/Accept/NotificationIngressTests.cs:10 Features/Notifications/PurgeExpired/NotificationRetentionTests.cs:2
```

（**#1150 の鏡写し移送後の経路で数え直した。** 移送前の平置き経路 `Tests/*.cs` で数えた最初の測定と
**同じ 34 件**であり、移送はテストを増減させていない。`Tests/` 直下に残るのは器
（`TestWebApplicationFactory` ほか 5 本）だけで `[Fact]` を 1 つも持たない。）

**合計 34 件**（棚卸しコメントの主張と一致した。**再測定して一致した**のであって転記していない）。
後段の面は `Features/Notifications/NotificationEndpoints.cs` が `/notifications` 群を
`RequireAuthorization()`（**ロール要件なし**）で公開し、`ListNotifications` / `MarkRead` の 2 本を持つ。

### 軸 3: 発火の結線（3 契機 / 5 種別）

```
$ grep -rn "PrivateNoteNotificationKinds\." src/knowledge/backend/Services/DocumentService/ --include=*.cs | grep -v /Tests/
Features/PrivateNotes/Maintenance/PrivateNoteMaintenanceService.cs -> PrivateNotePurgeDone / PurgeImminent / PurgeWeekly / SyncTokenExpiry
Features/PrivateNotes/PrivateNoteUsage.cs                          -> StorageQuotaWarning
```

**5 種別すべてが本番コード（Tests を除いた側）から呼ばれている。** ①削除通知（週次・7 日前・事後）
②容量警告 ③同期トークン期限 の 3 契機である。

### 軸 4: 前段（契約・生成フック・画面）

- 契約: `docs/api/openapi.yaml` の `/bff/notifications`（`x-roles: []`）・`/bff/notifications/{id}/read`。
- 生成フック: `src/platform/frontend/src/lib/api/generated/notifications/notifications.ts`
  （`useBffNotificationList` / `useBffNotificationMarkRead`。`grep` で URL 生成行を実測）。
- 画面: `src/platform/frontend/src/components/notifications/useNotifications.ts` が**その生成フックを呼んでいる**。

**したがって本作業で orval の再生成は要らない。** orval の入力は `docs/api/openapi.yaml` の
`/bff/` 配下だけであり、**本作業はその契約を 1 バイトも変えない** ——
`git status --porcelain -- src/platform/frontend docs/api/openapi.yaml` が**空**であることで確かめた
（入力が変わっていないので出力も変わりえない。`pnpm run codegen` を回して同じ結論を得るのは同語反復である）。

### 軸 5: 除外したもの（理由つき）

| 除外 | 理由 |
| --- | --- |
| `docs/api/openapi.yaml` の契約追加 | **既に載っている。** 本作業は契約を変えない（変えると生成物・画面まで波及する） |
| `deploy/` の `Services__NotificationService` 上書き | **不要。** コード既定を `http://notification-service:8080` にするため、`check-bff-downstreams.js` の不変条件（実効ポート == 8080）を上書き無しで満たす（後発サービスの規約。実行して 0 件を実測する） |
| readiness の `UriHealthCheck` への追加 | **足さない。** 通知は Should であり、後段の不調で BFF 全体を not-ready にするのは fail-safe の後退である（`ConfigurationService` / `McpServer` / `DocumentService` も入っていない＝実測） |
| SMTP・メール経路 | `blocked:env`。**AC-4 はむしろ SMTP が無い状態を前提にした試験**であり、本作業に要らない |

## 対象範囲

- **対象**
  - `src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/NotificationBffEndpoints.cs`（新規）
  - `src/platform/backend/Bff/Platform.Bff/Composition/BffEndpointComposition.cs`（1 行追加）
  - `src/platform/backend/Bff/Platform.Bff/Program.cs`（named client 1 件追加）
  - `src/platform/backend/Bff/Platform.Bff.Tests/`（スタブ 1 件＋端点テスト 1 ファイル）
  - `deploy/docker-compose.yml` / `deploy/helm/microservices-platform/values.yaml`（**「下流を持たない」注記の是正のみ**）
  - `docs/api/BFF_notifications.md` / `docs/api/BFF_bff-surface.md` / `docs/tests/FR-22_user-notifications.md`（追随）
  - `.ai-context/adr/IADR-0346_bff-notification-relay.md`（新規）
- **対象外**: 契約（`openapi.yaml`）・orval 生成物・画面・後段・SMTP・E2E（Playwright）。

### 並行作業との交差（着手時に再確認した）

| 進行中 | 領域 | 交差 |
| --- | --- | --- |
| #1063（#1150 として着地） | `src/*/backend/Services/*/Tests/**` | **無い。実測**: `git diff --stat 4d0f80e8 d561509d -- src/platform/backend/Bff/` が**空**（`Bff/` を 1 行も触っていない）。よって `Tests/Features/<集約>/<操作>/` の鏡写し規約は `Services/*/Tests/` の話であり、**BFF テストは既存の平置き規約 `Platform.Bff.Tests/*.cs` に従う** |
| #1115 / #1152 | `Bff/`（バックチャネルログアウトの宛先・realm 設定） | **面が違う**（`Foundation/Session/`）。PR 直前に `origin/develop` を merge して解く |
| #1110 / #1103 / #1126 / #1127 / #1143 | 稼働 k3s での実測 | **BFF Pod 以外を再起動しない**ことで回避する |

## 設計

### 1. 置き場所 — platform 同居（`Platform.Bff/Foundation/Endpoints/`）

後段 `NotificationService` は **platform ユニット**（`src/platform/backend/Services/NotificationService`）に在る。
`McpClientBffEndpoints` / `UserAdminBffEndpoints` と同じ切り分けで platform 同居とする
（`Knowledge.Bff.Endpoints` へ置くのはナレッジ 7 ドメインと `PrivateNote` / `TagDictionary` のように
**後段が knowledge ユニット**である場合に限る）。

### 2. 認可 — 認証必須・ロールを要求しない

契約の `x-roles: []` と後段 `NotificationEndpoints` の `RequireAuthorization()` に合わせ、
群へ `RequireAuthorization()` だけを付ける。**`AdminOnly` を足さない** —— 通知は全利用者が受け取る。
**逆に管理者へ他人の通知を見せる口も作らない**（絞りは役割ではなく主体）。

### 3. 🔴 本人絞りの実体は「資格情報を後段へ届けること」

後段は主体を **JWT の `sub` からしか採らない**（`NotificationSubject.Of(http.User)`）。
BFF は**主体をパラメータで渡さない** —— 渡す形にすると「他人の ID を入れたらどうなるか」という面ができる。
よって BFF がやるべきことは `Authorization` ヘッダの転送であり、**落とすと機能が丸ごと 401 で死ぬ**
（緩む向きではないが、テストが陽性対照を持たないと「全部 401 でも緑」になる。`PrivateNoteBffEndpoints` と同じ）。

BFF セッション方式（ADR-0032 / IADR-0251 / IADR-0273）では、SPA は Cookie で BFF を呼び、
`SessionTokenPropagationMiddleware` がセッションのアクセストークンを `Authorization` へ載せる。
**中継はその結果を読むだけでよい**（新方式を発明しない）。

### 4. ABAC の前段を置かない

`BffScopeResolver` は**文書属性**を見る。通知は文書ではなく、返すのは呼び出し者自身のものだけである。
スコープを当てると `MatchesAll` の安全側（キー欠落＝不一致）へ落ち、**自分の通知が 1 件も見られなくなる**。
`PrivateNoteBffEndpoints` の 4（読み取りに ABAC を置かない）と同じ判断である。

### 5. 応答は透過する（404 を作り替えない）

後段は「存在しない」と「本人のものでない」を区別せず **404** を返す（存在秘匿。IADR-0009）。
BFF が 403 や 200 へ変換すると**存在秘匿が BFF 層で破れる**。状態・`Content-Type`・本文をそのまま返す。
後段不達は **502** へ縮退する（空応答で「通知が 0 件になった」ように見せない）。

### 6. `limit` のクランプは後段の 1 箇所に置く

後段 `NotificationStore.ListAsync` が `Math.Clamp(limit ?? DefaultListLimit, 1, MaxListLimit)` を持つ
（既定 50 / 上限 100 は `NotificationOptions`）。**BFF に 2 つ目のクランプを置かない** ——
`NotificationOptions` を変えたときに BFF 側だけ古い上限で切る形になる（数え方を 2 つ持たない）。
BFF は `unreadOnly` / `limit` を**そのまま後段のクエリへ載せるだけ**にする。
`docs/api/BFF_notifications.md` の「BFF がクランプする」は本作業でこの実装に合わせて是正する
（規則 10: 是正のたびに新たに誤りになる自分の記述を引き直す）。

### 7. 経路対応

| BFF | 後段（NotificationService） |
| --- | --- |
| `GET /bff/notifications?unreadOnly=&limit=` | `GET /notifications?unreadOnly=&limit=` |
| `POST /bff/notifications/{id}/read` | `POST /notifications/{id}/read` |

named client 名は **`NotificationService`**、コード既定 `http://notification-service:8080`
（後発サービスの規約。`DocumentService/Program.cs` の送出側と同じホスト名で文字列一致する）。

## 受け入れ基準

棚卸しコメントが立てた 7〜9 を、実装できる形へ言い直す。

- [ ] **AC-7**: **Given** 認証済みの利用者 / **When** `GET /bff/notifications` / **Then** 200 が返り、
      後段へ渡ったパスが `/notifications` で、**`Authorization` が後段へ届いている**。
- [ ] **AC-7b**: **Given** `unreadOnly=true&limit=10` / **When** 呼ぶ / **Then** 後段のクエリに
      両方が載る（**落とすと未読フィルタが無言で効かなくなる**）。
- [ ] **AC-8**: **Given** 本人のものでない通知 ID（後段が 404 を返す） / **When** 既読化 / **Then** **BFF も 404**
      （403 や 200 へ変換しない）。
- [ ] **AC-9**: **Given** 既読の通知 / **When** もう一度既読化 / **Then** **200**（冪等。後段の 200 を透過する）。
- [ ] **AC-10**: **Given** 未認証 / **When** 2 本のいずれかを呼ぶ / **Then** **401**（エッジで認証を担保する）。
- [ ] **AC-11**: **Given** 一般利用者ロール（`viewer`）/ **When** 一覧を呼ぶ / **Then** **200**
      （**狭めすぎていない側**。ロールを要求すると全利用者が通知を読めなくなる）。
- [ ] **AC-12**: **Given** 後段が不達 / **When** 一覧を呼ぶ / **Then** **502**（空の 200 で隠さない）。
- [ ] **AC-13**: named client `NotificationService` の `BaseAddress` が `Services:NotificationService` 設定で解決される
      （#342 と同型の直書き退行を止める）。
- [ ] **AC-14**: 稼働 k3s で BFF イメージだけ差し替え、ログイン済みセッションで `/bff/notifications` が
      後段の応答を返し（**陽性**）、未認証は 401（**陰性**）。**未達（下記 §実測を参照）。**

## 実測（稼働 k3s）

### 差し替え前の基準測定 —— **「404 と 401 の差」が判定軸になる**

イメージは `nerdctl --namespace k8s.io build -f src/platform/backend/Bff/Platform.Bff/Dockerfile
-t k3d-local/microservices-platform/bff:issue600 .` で焼いた（成功）。
差し替え**前**にエッジ（`https://localhost`。TLS はローカル CA を `--cacert` で検証。`-k` は使わない）
から読み取り専用で叩いた結果:

| 経路 | 方法 | 結果 |
| --- | --- | --- |
| `GET /bff/notifications` | 無認証 | **404** |
| `POST /bff/notifications/{id}/read` | 無認証 | **404** |
| `GET /bff/documents`（**陽性対照**: 実装済み・認証必須） | 無認証 | **401** |
| `GET /bff/this-route-does-not-exist`（**陽性対照**: 実在しない経路） | 無認証 | **404** |

**稼働クラスタの BFF には通知の経路が無く、実在しない経路とまったく同じ 404 を返す。**
陽性対照 2 本が「404 は不在を、401 は在って認証が効いていることを表す」ことを示しており、
**差し替え後に `/bff/notifications` が 401 へ変わることが、経路が入ったことの判別可能な観測になる。**

### 🔴 残っている実測（差し替えが未実行）

**イメージの差し替え（`kubectl set image deployment/bff-service -n microservices-platform
bff=k3d-local/microservices-platform/bff:issue600`）が、実行環境の権限判定で拒否されたため
実行できていない。** よって AC-14 は**未達**である。イメージは焼き済みなので、
差し替えとロールアウト待ちのあと次を測ればよい。

1. **陰性**: 無認証の `GET /bff/notifications` が **401**（404 から変わること）。
2. **陽性**: ログイン済み（realm の利用者で認可コード + PKCE を通す。`scripts/verify-oidc-edge-flow.sh`
   と同じ手順）の資格情報つきで **200 ＋ `unreadCount` を含む本文**。
3. **BFF 以外の Pod を再起動しない**（同じクラスタで並行実測が走っている）。

## テスト方針

`Platform.Bff.Tests/BffNotificationEndpointTests.cs`（新規）へ AC-7〜AC-12 を写像する。
`BffTestFactory` へ `NotificationStubHandler` を足す —— **資格情報が届かないときは 401 を返すスタブ**にする
（`McpStubHandler` と同じ作法。一律 200 にすると伝播を落としても緑のままになる）。
AC-13 は `BffDownstreamResolutionTests` と同型の 1 件を同ファイルに置く。
**#1063 は `Services/*/Tests/**` が領域であり `Bff/` を含まないため、BFF テストは既存の平置き規約に従う。**

## 計画書との差異

- 差異: なし。

## 未決事項

1. **SSE 化（#788）は射程外。** 配信は 60 秒ポーリングのままである。
2. **未読件数だけを返す軽い端点は置かない**（一覧が `unreadCount` を返す。面を 2 つに増やさない）。
3. **E2E（Playwright）は置かない。** `e2e/support/bffSession.ts` は SPA 側で `GET /notifications` を
   スタブしており、実 BFF を通らない（実測）。実クラスタでの実測（AC-14）で代える。

## 残件

- **SMTP リレーの実体**（ADR-0045。`blocked:env`。自社ドメイン未定）—— #600 の外へ出す。

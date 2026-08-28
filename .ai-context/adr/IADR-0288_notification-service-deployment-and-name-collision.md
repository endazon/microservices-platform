---
title: IADR-0288 通知サービスの Service 名は送出側の既定に揃え、AST との同名衝突は除外で通す
type: impl-adr
status: Accepted
related_ids:
  - FR-22
  - ADR-0045
  - NFR
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "planning:projects/microservices-platform/07_adr/ADR-0045（メール配信の SMTP リレー）"
---

# IADR-0288: 通知サービスの Service 名は送出側の既定に揃え、AST との同名衝突は除外で通す

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: 実装エージェント（#1025）

## 起点・関連

- 関連する計画書 ID: FR-22（利用者本人への通知）／ ADR-0045（メール配信の SMTP リレー・送信上限）
- 関連する実装仕様書: [作業仕様書 #1025](../specs/20260828_issue-1025_notification-service-deployment.md)
- 前段の決定: [IADR-0215](./IADR-0215_notification-service-and-in-app-delivery.md)（送出主体の新設）／
  [IADR-0270](./IADR-0270_private-note-obsidian-sync-backend-core.md)（受け口と発火の結線）／
  [IADR-0107](./IADR-0107_ast-owned-service-single-deployment.md)（AST 所有サービスの単一デプロイ）

## コンテキストと課題

`NotificationService` は実装もテストも Dockerfile も揃っているのに、**配備先が 1 つも無かった**
（実測: `deploy/` に `notification` の出現 0 件・`k8s-local-images.sh` の `MAPPING` にも 0 件）。
波 3 で結線済みの個人資料通知は、`HttpPrivateNoteNotifier` が名前付き HttpClient で
`POST /internal/notifications` を叩くが、**宛先が存在しないため届かない**。
送出は fail-open（[IADR-0215](./IADR-0215_notification-service-and-in-app-delivery.md) 決定 5-b）であり、
**不達は例外にも 502 にもならず、エラーログと計器にしか出ない。**

配備を入れるにあたって、着手前の走査で **2 つの決めどころ**が出た。

1. **Service 名をどう決めるか。** 送出側のコード既定は `http://notification-service:8080` である。
2. 🔴 **AST chart も `notification` という名前のサービスを所有している。**
   `scripts/check-unit-service-ownership.js` の `AST_OWNED_FALLBACK` に `'notification'` が入っており、
   MSP の chart キーを `notification` にすると同検査が「重複デプロイ」として CI を落とす（実測済み）。
   これは [IADR-0107](./IADR-0107_ast-owned-service-single-deployment.md) §運用注意が
   **`audit` / `report` / `notification` / `backtest` を名指しで予見していた**衝突である。

## 検討した選択肢

### 選択肢 A — chart キーを衝突しない名前へ変える（例 `usernotification`）

Service 名が `usernotification-service` になるため、`services.document.extraEnv` に
`Services__NotificationService: http://usernotification-service:8080` の上書きが要る。

- ✅ `check-unit-service-ownership.js` に一切触らない。名前空間の曖昧さも残らない。
- ❌ **compose（`notification-service`）と k8s（`usernotification-service`）で綴りが割れる。**
  これは `llm-gateway` / `llmgateway-service` と同じ形で、#995 が 🔴 付きの警告コメントで塞いだ轍である。
- ❌ **本経路は fail-open のため、上書きを落としても 502 にすらならない。**
  「上書き漏れが静かに効かなくなる」型を、**不達が見えない経路に**新設することになる。
  #1025 が解消しようとしている「実装できている／届いている が読み分けられない」を作り直す。

### 選択肢 B — chart キーを `notification` にし、名前衝突を検査の除外として通す

- ✅ **compose・k8s・コード既定の 3 つが `notification-service` の 1 文字列で揃う。** 上書き env を作らない。
- ✅ [IADR-0107](./IADR-0107_ast-owned-service-single-deployment.md) §運用注意が用意した逃げ道そのものである
  （「回避が必要になった場合は、本節を更新したうえで検査に除外リストを設ける」）。
- ❌ #407 の再発防止の網目を 1 つ広げる。**広げた理由と、広げても安全である根拠を残す責任が生じる。**

### 選択肢 C — 除外を作らず、AST 所有一覧から `notification` を落とす

- ❌ **誤り。** AST は実際に `notification` を所有している。一覧を偽ると、AST 側の
  `notification` を MSP が有効化する**本物の重複デプロイ**が素通りする。**採らない。**

## 決定

### 決定 1 — Service 名は `notification-service`。上書き env を作らない

chart キーは `notification`（`deployment.yaml` / `service.yaml` が `{{ $name }}-service` を組む）。
compose のサービス名も `notification-service`。**送出側のコード既定と文字列一致させる**:

| 面 | 文字列 | 出典 |
| --- | --- | --- |
| 設定キー | `Services:NotificationService`（env: `Services__NotificationService`） | `DocumentService/Program.cs:78` |
| コード既定 | `http://notification-service:8080` | `DocumentService/Program.cs:79` |
| 名前付き HttpClient | `NotificationService` | `HttpPrivateNoteNotifier.ClientName` |
| 受け口パス | `/internal/notifications` | `HttpPrivateNoteNotifier.IngressPath` ／ `MapNotificationIngressEndpoints` |
| compose | サービス名 `notification-service` ＋ `expose: "8080"` | `deploy/docker-compose.yml` |
| k8s | Service `notification-service` ＋ `port: 8080` | `values.yaml` の `services.notification` |

**`Services__NotificationService` は compose にも helm にも書かない** —— 既定と同値の重複であり、
2 箇所に持つと片方が古くなる。**名前を揃えることが結線の担保である。**

### 決定 2 — 名前衝突は「到達経路の分離」を確かめたうえで、検査の除外で通す

`check-unit-service-ownership.js` に `NAME_COLLISION_EXEMPT` を設け、`notification` を入れる。
[IADR-0107](./IADR-0107_ast-owned-service-single-deployment.md) §運用注意も同時に更新する（片方だけだと条文と実装が割れる）。

**除外してよいと判断した根拠**（#407 の事故は「同一実体の二重化」であり、本件は別物の同名である）:

1. **到達経路が分離している。** `deploy/local/aliases/microservices-platform-externalnames.yaml` の
   ExternalName alias は `configuration-service` / `risk-management-service` / `market-monitor-service` の
   **3 件のみ**で、`notification-service` の alias は無い（実測）。MSP namespace で
   `notification-service` を引くと、MSP chart が描く自分の Service に解決する。
2. **将来 alias を足そうとしても静かには壊れない。** MSP ns には既に同名の Service が居るため、
   ExternalName の apply は**衝突して失敗する**（#407 のような無言の取りこぼしにならない）。
3. **共有資源を奪い合わない。** MSP の NotificationService は **RabbitMQ を使わない**
   （`Program.cs` に broker の登録が無い）。#407 の実害はキューの競合コンシューマだった。
4. **DB も分かれている見込み。** MSP は `notification_svc`。MSP が共有 Postgres へ用意する
   AST 専有 DB 7 件（`audit_svc` / `configuration_svc` / `cost_control_svc` / `market_monitor_svc` /
   `order_execution_svc` / `report_svc` / `risk_management_svc`）に `notification_svc` は含まれない。
   **ただしこれは状況証拠である**（submodule 未取得で AST chart を読めなかった。§未決 1）。

除外は**名前を列挙する形**にし、`checkTree()` は**除外が実際に効いた（両方の集合に在った）ときに
notice を出す** —— 「除外リストに書いたから静かに消えた」を避け、人が毎回気づける形にする。

### 決定 3 — DB は init スクリプト 2 本の**両方**へ足す

`deploy/create-multiple-dbs.sh`（compose）と `deploy/local/infra/postgres.yaml` の ConfigMap（経路B）。
起動時 `MigrateAsync()` が走るため、DB 不在はクラッシュループになる。**片方だけ足すと片方の環境だけ壊れる。**

### 決定 4 — replicas は 1 に固定し、HPA 対象にしない

`NotificationMaintenanceHostedService` が保持期限切れの削除とメール outbox の送出を 5 分周期で回す。
レプリカを増やすと**同じ outbox を複数レプリカが同時に処理**し、
送信上限（ADR-0045 決定 3。テナント全体で 1 つの資源）の数え方が割れる。
`scaling.services` へは入れない（`conversion` / `ingestion` / `graph` と同じ扱い）。

### 決定 5 — 自己申告の収集先へは足すが、BFF の下流集約先へは足さない

`Program.cs:60` が「段は持たないが到達可能性を申告する」と書いて `AddPlatformIntrospection` を呼ぶ。
申告する側が居るのに収集側に無いと、その申告は永久に読まれない。よって compose の BFF env と
helm の `services.bff.extraEnv` の**両方**へ `Introspection__Services__notification-service` を足す。
一方 **`Services__*`（下流集約先・readiness の UriHealthCheck）へは足さない** ——
BFF は通知の下流を持たない（`/bff/notifications*` は未実装。#600）。
**足すと readiness が未実装の端点に依存する。**

### 決定 6 — 資格情報は共有 Secret に相乗りする。新規 ExternalSecret は作らない

helm の `deployment.yaml` は `$svc.database` があるときだけ `global.db.existingSecret`（＝`postgres-app`）から
`DB_PASSWORD` を引く。`postgres-app` は `externalsecret-postgres-app.yaml` が Vault
`secret/msp/postgres-app` から供給し、`ESO=1` 経路の apply も配線済みである（走査で確認）。
NotificationService が構成から読むのは `ConnectionStrings:DefaultConnection` と `Notification:*` だけで、
**SMTP は `UnconfiguredSmtpEmailTransport` のため現時点で資格情報を要求しない**。
→ **ESO=1 でも欠けは無い。**

## 理由

決め手は **fail-open との相性**である。選択肢 A の「名前をずらして上書きで繋ぐ」形は、
上書きが落ちたときに**エラーにならない経路**へ新しい単一障害点を作る。#1025 が解消しようとしている
欠陥そのものを別の場所へ移すだけになる。対して選択肢 B の代償は
「検査の網目を 1 つ広げる」ことであり、**広げた事実は列挙とドキュメントに残り、notice で毎回見える**。
可視な代償と不可視な代償なら、可視な方を採る。

## 結果

- **良い影響**: 送出側の既定・compose・k8s が 1 つの文字列で揃い、上書きの管理点が増えない。
  実装済みだった通知が、配備定義の上では受け口へ届く形になった。
- **悪い影響・トレードオフ**: `check-unit-service-ownership.js` の網目が 1 名分広がった。
  MSP と AST が**同名の別サービス**を持つ状態そのものは残る（namespace で分離しているだけ）。
- **フォローアップ**
  1. 🔴 **実稼働の到達は未実測である。** 本環境に Docker daemon も k8s も無く、
     「Pod が Ready になる」「通知が実際に届く」は確かめていない。静的な文字列一致と
     機械検査の突合までが本 PR の射程である。live 検証は稼働環境で行う。
  2. `NetworkIsolationTests.InternalAppServices` に `notification-service` が無い
     （`src/knowledge/**` は本作業の宣言領域外）。host 公開の回帰を止められない状態が残る。
  3. `check-bff-downstreams.js` の `CALLERS` に **DocumentService が無い**。
     DocumentService → NotificationService の名前付き client は誰も見ていない
     （コード既定が既に `:8080` のため現時点の違反は 0 だが、#958 と同型の死角である）。
  4. SMTP の実体と宛先解決は未配線（`externalsecret-keycloak-smtp.yaml` が「★未配線」で待っている）。
  5. BFF 端点 `/bff/notifications*` が入るまで、配備しても**画面には出ない**（#600）。

## 未決

1. **AST の `notification` サービスが共有 Postgres に DB を持つか**を確認できていない
   （submodule 未取得）。持つ場合、DB 名が `notification_svc` と衝突しないことを確かめる必要がある。
   submodule を取得できる環境での確認を申し送る。

## 関連

- Supersedes: なし
- Superseded by: なし

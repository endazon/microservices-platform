---
title: IADR-0347 BFF 通知中継は platform 同居の透過中継とし、本人絞り・クランプ・存在秘匿を後段の 1 箇所に残す
type: impl-adr
status: Accepted
related_ids:
  - FR-22
  - UC-11
  - ADR-0004
  - ADR-0032
  - ADR-0037
  - ADR-0045
  - IADR-0009
  - IADR-0044
  - IADR-0089
  - IADR-0215
  - IADR-0251
  - IADR-0267
  - IADR-0273
  - IADR-0285
  - IADR-0288
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-22 要求文・注 4)
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md 決定 6・17・18
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
---

# IADR-0347: BFF 通知中継（`/bff/notifications*`）の形（#600）

> 🔴 **番号は暫定である。** 起草時点の `develop` の最大は `IADR-0334` だが、**0335〜0346 は
> 進行中の並行 PR へ割当済み**であるため 0347 を仮置きした。**マージ直前に実際の空き番号へ
> 付け直し**、ファイル名・本文の自称番号・索引（`.ai-context/adr/README.md`）・作業仕様書・
> コード内コメント・PR タイトルを追随させること（`scripts/check-adr-numbering.js` は
> 昇順・欠番なしを fail で見るため、付け直すまで当該検査は赤になる）。

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: claude（実装）

## コンテキストと課題

FR-22 の実装は 4 回に分けて入った —— 契約と画面（PR #825）、後段サービス（IADR-0215 / IADR-0267）、
発火の結線、配備（IADR-0288）。**残っていたのは前段と後段をつなぐ BFF 中継 1 本だけ**であり、
これが無い間、生成フックもベル UI も後段も揃っているのに**画面には何も出なかった**。

中継そのものは既存の `McpClientBffEndpoints` / `PrivateNoteBffEndpoints` と同型で足りる。
にもかかわらず記録が要るのは、**「同型で足りる」と判断した根拠のうち 4 つが、
書かないと次の担当者が逆へ倒しうるもの**だからである。

1. **置き場所**（platform 同居か `Knowledge.Bff.Endpoints` か）。
2. **ABAC の前段を置くか**。BFF の読み取り経路の多くは `BffScopeResolver` を通している。
3. **`limit` のクランプをどちらが持つか**。契約の記述（`docs/api/BFF_notifications.md`）は
   **「BFF がクランプする」と書いていたが、後段は既に自前でクランプしていた**。
4. **404 を透過するか**。

## 決定

### 決定 1: platform 同居（`Platform.Bff/Foundation/Endpoints/`）に置く

後段 `NotificationService` は **platform ユニット**のサービスである
（`src/platform/backend/Services/NotificationService`）。`Knowledge.Bff.Endpoints` へ置くのは
**後段が knowledge ユニット**である場合に限る（`PrivateNote` / `TagDictionary` がその形）。
`McpClientBffEndpoints`（後段 McpServer）・`UserAdminBffEndpoints`（後段 AuthorizationService）と
同じ切り分けである。

### 決定 2: 認証必須・ロールは要求しない。本人絞りは後段の 1 箇所に残す

契約の `x-roles: []` と後段の `RequireAuthorization()`（ロール要件なし）に合わせ、
群には `RequireAuthorization()` だけを付ける。

🔴 **BFF は本人性の判定を複製しない。** 後段は主体を `NotificationSubject.Of(http.User)` で
**トークンからしか採らない**。したがって **BFF における本人絞りの実体は「`Authorization` を
後段へ確実に渡すこと」**である。**主体をパラメータで渡す形にしてはならない** ——
その瞬間に「他人の ID を入れたらどうなるか」という面ができる。
`PrivateNoteBffEndpoints` の同じ判断（IADR-0285）と揃えた。

BFF セッション方式（ADR-0032 / IADR-0251 / IADR-0273）では
`SessionTokenPropagationMiddleware` が Cookie セッションのアクセストークンを `Authorization` へ
載せるため、中継はその結果を読むだけでよい（**新しい方式を発明しない**）。

**ロールを足さないことは狭めすぎの防止でもある。** 通知は全利用者が受け取るものであり、
`AdminOnly` を付けると一般利用者が削除予告・容量警告を受け取れなくなる。
**逆に管理者へ他人の通知を見せる口も作らない**（絞りは役割ではなく主体である）。

### 決定 3: 読み取りに ABAC の前段を置かない

`BffScopeResolver` が見るのは**文書属性**である。通知は文書ではなく、返すのは呼び出し者自身の
ものだけであって、秘匿する相手が居ない。スコープを当てると `MatchesAll` の安全側
（キー欠落＝不一致）へ落ち、**利用者が自分の通知を 1 件も見られなくなる**。
所有者スコープの実施点は後段だけである（IADR-0285 の同型の判断と揃えた）。

### 決定 4: `limit` のクランプを BFF に置かない。文書側を実装に合わせて是正する

後段 `NotificationStore.ListAsync` が既に
`Math.Clamp(limit ?? DefaultListLimit, 1, MaxListLimit)` を持つ（既定 50 / 上限 100 は
`NotificationOptions`。設定で変えられる）。BFF に 2 つ目のクランプを置くと、
**設定を変えたときに BFF 側だけが古い上限で切る**。BFF は `unreadOnly` / `limit` が
指定されたときだけ後段のクエリへ載せ替え、**既定値を自分で埋めない**。

`docs/api/BFF_notifications.md` の「`limit` の上限は 100。BFF がクランプする」は
**この実装に合わせて是正した**（実装を文書へ寄せると、値の正が 2 箇所になる）。

### 決定 5: 応答は透過する。不達は 502 へ縮退する

後段は「存在しない」と「本人のものでない」を区別せず **404** を返す
（存在秘匿。IADR-0009 / ADR-0004）。BFF がこれを 403 へ変えると**他人の通知 ID の実在が漏れ**、
200 へ変えると既読化の失敗が隠れる。状態・`Content-Type`・本文をそのまま返す。
既読化の冪等な 200 も同じ経路で透過する。

後段不達は **502**。空の 200 で隠すと「通知が 0 件になった」と読ませ、
**利用者が完全削除の期限を見落とす**。

### 決定 6: named client のコード既定を `:8080` にし、readiness には入れない

`Services:NotificationService` 未設定時の既定を `http://notification-service:8080` とする
（後発サービスの規約。IADR-0089 / #342 の「上書き漏れで不達」の面を最初から作らない）。
ホスト名は送出側 `DocumentService` のコード既定・compose のサービス名・helm の
`{{ $name }}-service` と文字列一致する（IADR-0288）。**manifest 側の上書きは足さない**
（`check-bff-downstreams.js` の不変条件を上書き無しで満たす。実行して 0 件を実測した）。

🔴 **readiness の `UriHealthCheck` には入れない。** 通知は優先度 Should であり、
後段の不調で BFF 全体を not-ready にするのは fail-safe の後退である
（`McpServer` / `DocumentService` / `ConfigurationService` も入っていない＝実測）。

## 検討した代替案

| 案 | 却下の理由 |
| --- | --- |
| `Knowledge.Bff.Endpoints` へ置く | 後段が platform ユニットである。ユニット境界の判定軸を「機能の見た目」に変えると、次に迷う人が別の答えを出す |
| BFF が主体（`sub`）をクエリで後段へ渡す | 「他人の ID を入れたらどうなるか」という面ができる。後段が守っている境界を BFF が迂回できる |
| 読み取りに `BffScopeResolver` を通す | 通知は文書属性を持たない。安全側（キー欠落＝不一致）へ落ち、**自分の通知が 1 件も見えなくなる** |
| BFF でもクランプする | 上限の正が 2 箇所になる。`NotificationOptions` を変えたときに BFF だけ古い上限で切る |
| 404 を 403 へ変換する | 他人の通知 ID の実在が漏れる（存在秘匿が BFF 層で破れる） |
| 後段不達を空の 200 にする | 「通知が 0 件になった」と読ませ、完全削除の期限を見落とさせる |
| readiness に足す | Should の機能で BFF 全体を not-ready にする（fail-safe の後退） |

## 結果

- **画面が動く。** 前段（生成フック・ベル UI）と後段（サービス・発火・配備）が
  この 1 本で結線され、#600 が閉じる。
- **BFF テスト 10 件**（`BffNotificationEndpointTests`）が、認可の両側（未認証 401 / 一般利用者 200）・
  資格情報の伝播・クエリの載せ替え・404 の透過・502 縮退・上流解決を固定する。
  **変異試験で実測**: `Authorization` の転送と `unreadOnly` の載せ替えを落とすと **5 件が fail** した
  （落としても緑のままなら、この 10 件は何も測っていないことになる）。
- **合成点は 19 モジュールになった**（`BffEndpointCompositionTests` の件数・グループ集合を更新）。
- **テストの置き場所**: `Platform.Bff.Tests/` は #1063（#1150）の `Tests/` 鏡写し移送の射程外である
  （`git diff --stat 4d0f80e8 d561509d -- src/platform/backend/Bff/` が空＝実測）。
  よって既存の平置き規約に従った。
- **残るのは SMTP リレーの実体だけ**（ADR-0045。`blocked:env`。自社ドメイン未定）。
  受け入れ基準 4 は「メールが送れなくてもアプリ内通知が届く」ことを求めており、
  **本作業はそれを満たしたうえで #600 の射程を出る**。

## 追跡

- Issue: #600
- 作業仕様書: `.ai-context/specs/20260902_issue-600_bff-notifications-relay.md`

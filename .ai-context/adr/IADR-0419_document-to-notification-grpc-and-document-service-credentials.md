---
title: IADR-0419 east-west gRPC 第 10 面: Document → Notification の私的資料通知を s2s で運び、document-service に機密クライアントを与える
type: impl-adr
status: Accepted
related_ids: [FR-19, FR-20, FR-21, FR-22, NFR-09, NFR-16, NFR-19, UC-11, ADR-0004, ADR-0026, ADR-0029, ADR-0037, ADR-0045, ADR-0075, IADR-0017, IADR-0026, IADR-0117, IADR-0215, IADR-0267, IADR-0270, IADR-0299, IADR-0316, IADR-0371, IADR-0379, IADR-0397, IADR-0398, IADR-0400, IADR-0401, IADR-0402, IADR-0408, IADR-0412, IADR-0416, IADR-0417]
author: claude
created: 2026-09-09
updated: 2026-09-09
---

# IADR-0419: 呼び出し元として立つ最初の一枚

## 状況

#1255 の残る east-west 経路は **3 つ**であった（`docs/api/east-west-grpc.md` 末尾の実測）。

| # | 経路 | 性質 |
| --- | --- | --- |
| ② | DocumentService → NotificationService `POST /internal/notifications` | 1 対 1 の一方向報告 |
| ④ | McpServer → 各サービス `GET /internal/mcp-tools` | **宛先集合が構成で開く扇形** |
| ⑤ | 各サービス → `GET /internal/introspection` | 同上 |

④⑤ は本リポジトリだけでは完結しない（宛先が公開構成で決まる）。**移せるのは ② だけである。**

### この面が持ち込むのは「輸送」ではなく「資格情報」である

先行 9 面はすべて、**呼び出し元が既に s2s の資格情報を持っていた**（BFF・Retrieval・Ingestion・
AiAnalysis・Graph・Conversion・Wiki・DataSource・McpServer）。

🔴 **DocumentService は違う。** 受け口（`DocumentRead` / `DocumentTagWrite` / `TagDictionary`）を
3 つ持ちながら、**呼び出し元としては 1 度も立っていない** —— realm に `document-service` クライアントは
無く、compose にも helm にも `ServiceToken__*` が無い。したがってこの面は
**新しい主体を 1 つ増やす**作業を伴う。#1301 が踏んだ穴（client は在るが `users[]` に service account が
無く、realm ロールが誰にも付いていない）と同型の危険がここにある。

### 経路の性質

3 契機（週次 purge 予告・完全削除の予告と事後・容量警告・同期トークン期限）はいずれも
**利用者の要求の外で走る検知**であり、**利用者の資格情報を 1 バイトも持たない**
（[[IADR-0270]] 決定 6 が「検知はデータの在る側・実体は通知側」と分けた理由そのもの）。
[[IADR-0408]]（グラフ → ダッシュボードの観測値報告）と**同じ形**である。

🔴 **そして送出は fail-open である**（[[IADR-0215]] 決定 3・5-b）。届かなくても業務処理は成功する ——
**だから配線の誤りは例外にも 502 にもならず、エラーログと計器にしか出ない。**

## 決定

### 決定 1: **② だけを移す。④⑤ は同じ PR に入れない**

④⑤ は宛先集合が構成で開く扇形であり、面の設計（宛先ごとのチャネル・s2s の主体）が
1 対 1 の経路とは別物になる。**混ぜると「面が動いたのか、扇形の解決が動いたのか」が切り分けられない。**

🔴 **同時に、この PR で `document-service` という新しい機密クライアントを 1 つ増やす。**
secret を増やす判断は #1301 の事故と同型なので、**他の変更と束ねない**
（realm・helm・compose・ローカル供給元の 4 経路が 1 つでも欠けると、Pod が起動しないか
面が常に拒否される —— 後者は fail-open で沈黙する）。

### 決定 2: 🔴 **面は REST の受け付け DTO と同じ 6 項目を運び、null は presence で運ぶ**

`AcceptRequest` は `subject` / `kind` / `occurred_at` / `count` / `threshold_percent` / `deadline`。
**自由文の項目は 1 つも無い**（FR-22「本文が件数と期限のみ」を型の形で守る。
`IPrivateNoteNotifier` の引数・REST の DTO と同じ規律）。

🔴 **`count` / `threshold_percent` は `optional`（field presence）である。** 素の `int32` にすると
未設定が `0` に化けるが、**受け口の検証は通る**（`is not < 0` は null にも 0 にも真）ので
**例外は 1 つも起きない**。割れるのは重複判定（`n.Count == request.Count`）だけであり、
**同じ事象が新規として二重に積まれる** —— FR-22 が最も禁じている「静かに落ちる」側の壊れ方である。

時刻は `google.protobuf.Timestamp`（message は既定で presence を持つ）。
`occurred_at` の欠落は検証器の「occurredAt は必須である。」へそのまま落ちる ——
**既定値（0001-01-01）を黙って採らない**（一覧の並びと 90 日保持の両方が壊れる）。

`Timestamp` は UTC へ正規化するが、**重複判定は `DateTimeOffset` の瞬間比較**なので
REST 経路と gRPC 経路は同じ事象を同じ 1 件に畳む（試験 T-11 で固定した）。

### 決定 3: 🔴 **応答は `duplicate` だけを持つ。通知の識別子は面へ出さない**

REST の `NotificationIngressResultDto` は `id` と `duplicate` の 2 項目を持つ。**面は 1 項目にする。**

- **`duplicate` は残す。** REST が **200 と 201 で区別している事実**であり、受け口の設計が
  「畳んだことが観測できないと『届いていない』と区別できない」と明記している。
  落とすと**新しい輸送のほうが情報を失う**。
- 🔴 **`id` は出さない。** この受け口は**書き込み専用**である（読み出しは認証必須の
  `GET /notifications` だけであり、s2s の面から他人の通知を覗く経路を作らない）。
  `id` は**受け手が保持する実体への handle** であり、配ると「作った通知を後から指す」経路が
  s2s 側に生まれる。`duplicate` は**この呼び出しの結末**であって handle ではない ——
  **この線が、両者を分けた根拠である**（[[IADR-0401]] 決定 2「呼び出し元が要らないものを面へ出さない」の
  適用だが、`duplicate` は「呼び出し元が要らない」ではなく「契約が既に区別している」側にある）。

呼び出し元は現に `duplicate` を読んでいない（REST 版も成否しか見ない）。**それでも残す**のは、
面の意味論を REST と一致させておくためである。

### 決定 4: 🔴 **面は `ServiceCaller` を要求する。REST の無認証の口は残す**

REST の受け口は**認証を課していない**（[[IADR-0017]] / [[IADR-0026]]。呼び出し元は利用者文脈を
持たない定期処理であり、第一防御は mesh の STRICT mTLS）。gRPC 面は [[IADR-0379]] 決定 4 に従い
realm ロール `platform-service` を要求する —— **権限が狭まる向き**である
（[[IADR-0401]] 決定 1 / [[IADR-0402]] 決定 3 / [[IADR-0408]] / [[IADR-0417]] 決定 4 と同じ非対称）。

🔴 **利用者トークンでは開かない**（confused deputy の防止）。「認証さえあれば通る」形にすると、
転送された管理者トークンで s2s の面が開く。

**REST の口は残す**（並走中の正は REST。[[IADR-0379]] 決定 5）。切替も戻しも呼び出し元の構成
（`Services:NotificationServiceGrpc`）1 つで行い、**コードは変えない**。
非対称は**並走の期間だけ**続く。

### 決定 5: 🔴 **REST と gRPC は同じ受理関数を通る**

`NotificationIngress.AcceptAsync` を両面が呼ぶ。写すと、**片方だけ検証や重複判定が変わった状態**が
作れる（[[IADR-0402]] 決定 6 / [[IADR-0408]] / [[IADR-0417]] 決定 5 が同じ理由で 1 つに寄せた）。

これは**観測できる形で固定してある** —— 面の試験は「呼び出しが返ったこと」ではなく
**台帳に 1 件積まれたこと**を見る。返り値だけを見る器では、本体を通らない実装でも緑になる。

🔴 **鍵ごとの検証本文（形 β。[[IADR-0398]] 決定 1）は輸送を跨いで再現しない。**
REST は `ValidationProblem` で鍵 5 つを返し得るが、gRPC の status に対応する構造は無い。
守るのは「不正なペイロードを 1 件も永続化しない」「成功に見せない」であって本文の形の一致ではない
（[[IADR-0417]] 決定 9 の「元の HTTP 状態番号は輸送を跨いで再現できない」と同じ）。
**鍵と本文は status の detail へ連結して載せる** —— 検証メッセージは静的な文字列だけであり
（検証器の 7 定数）、利用者の資料名も本文も混ざらない。

### 決定 6: 🔴 **呼び出し元の 3 つの結末を、status で分け直す**

`HttpPrivateNoteNotifier` は結末を **3 つ**持ち、計器（`notification.dispatch.total`）の属性に載せている。
gRPC では非 2xx も不達も同じ `RpcException` に畳まれるので、**status で分け直す**。

| 事象 | REST | gRPC | 計器 |
| --- | --- | --- | --- |
| 受理された | 2xx | 例外なし | `sent` |
| 受け口が答えた失敗 | 非 2xx | `INVALID_ARGUMENT` / `INTERNAL` / `UNAUTHENTICATED` / `PERMISSION_DENIED` ほか | `rejected` |
| 後段へ届かなかった | 例外（通信・タイムアウト・想定外） | `UNAVAILABLE` / `DEADLINE_EXCEEDED`、および s2s トークン取得失敗 | `unreachable` |
| 呼び出し元のキャンセル | **伝播させる** | 同じ | 数えない |

🔴 **全 status を「不達」へ畳まない**（[[IADR-0417]] 決定 9 と同じ理由）。畳むと `rejected` の枝が
静かに消え、**ペイロード・配備の不整合が「届かなかった」に見える** —— 計器の説明文がこの 2 つを
分けているのは**打つ手が違う**からである（前者は直す・後者は待つ／繋ぐ）。

🔴 **`UNAUTHENTICATED` / `PERMISSION_DENIED` を「不達」に入れない。** 要求は届いており、
拒んだのは受け口である。**realm の service account の配線漏れ（#1301 の穴）はまさにこの枝に出る** ——
「不達」に混ぜると、資格情報の欠落が「通知サービスが落ちている」に見えて誰も直しにいかない。

期限は `HttpPrivateNoteNotifier.SendTimeout`（5 秒）を **`deadline` としてそのまま引く**
（値を書き写さない。片方だけ動いたときに気付けなくなる）。

### 決定 7: 🔴 **`document-service` の資格情報は 4 経路すべてを同じ変更で揃える**

realm（`clients[]` **と** `users[]`）／helm（`services.document.serviceToken`）／
compose（`ServiceToken__ClientId` ＋ `ClientSecret`）／ローカル供給元（Vault seed ＋ ExternalSecret ＋
素の Secret の手動 apply）。

🔴 **`users[]` を落とさない。** `clients[]` だけ足すと service account に `platform-service` が付かず、
面は**常に `PERMISSION_DENIED`** を返す。送出は fail-open なので**エラーログと計器にしか出ない**
（#1301 が踏んだ形そのもの）。realm を実測して、既存 9 client がすべて `users[]` に対を持つことを
確かめたうえで足した。

🔴 **helm の `secretKeyRef` は非 optional のままにする。** 資格情報だけが欠けた状態で起動できると、
送出は `UNAUTHENTICATED` を食い続け、**通知が静かに 1 件も届かなくなる**。
**Pod が起動しないほうが安全側である**（fail-open の経路であるからこそ、配備側は fail-closed にする）。

## 帰結

- NotificationService の Service に `grpc` ポート（8081）が出る。**readiness は 8080 のまま**である。
  **platform ユニットで受け口を持つ 3 つ目**（認可・LLM ゲートウェイに続く）。
- DocumentService は **受け口と呼び出し元の両方**になる（本リポジトリで初めて）。
  チャネルは**キー付き ＋ `TryAdd`**（[[IADR-0402]] 決定 6 / [[IADR-0412]] 決定 5）——
  宛先は今 1 つだけだが、2 つ目が足された瞬間の事故を先に閉じる。
- 本リポジトリの s2s 主体は 10 になり、ローカルの ESO 管理 Secret は MSP ns で 18 本になる。
- REST の受け口は無認証のまま残る。**この PR はそれを変えない** ——
  掛けると既存の呼び出し元（fail-open で沈黙する側）への影響を測る必要がある。
- **#1255 の残りは 2**（④ MCP のツール申告の収集・⑤ 実効構成の収集）。
  どちらも扇形であり、**本リポジトリだけでは完結しない**。
- 稼働クラスタでの h2c 往復は**未実測**（新イメージの配備＝Pod の再起動を要する）。
  `docs/api/east-west-grpc.md` §未決事項の既知の未実測と同じ性質である。

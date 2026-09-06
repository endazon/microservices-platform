---
title: IADR-0402 east-west gRPC の第 4 スライス（BFF）— 移せるのは利用者の資格情報を運んでいない呼び出しだけであり、判定の位置は動かさない
type: impl-adr
status: Proposed
related_ids:
  - FR-05
  - FR-06
  - FR-09
  - FR-19
  - NFR-09
  - NFR-16
  - UC-03
  - SC-03
  - SC-05
  - ADR-0002
  - ADR-0004
  - ADR-0029
  - ADR-0032
  - ADR-0036
  - ADR-0054
  - ADR-0056
  - ADR-0065
  - ADR-0070
  - ADR-0075
  - IADR-0009
  - IADR-0012
  - IADR-0041
  - IADR-0044
  - IADR-0045
  - IADR-0253
  - IADR-0256
  - IADR-0290
  - IADR-0316
  - IADR-0343
  - IADR-0379
  - IADR-0388
  - IADR-0397
  - IADR-0400
  - IADR-0401
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 3・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md §決定
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-07・D-08
  - planning:projects/microservices-platform/07_adr/ADR-0056_existence-hiding-boundary-404-403.md §決定
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0070_pdf-body-extraction-and-ingest-format-set.md 決定 3
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md §決定
---

# IADR-0402: east-west gRPC の第 4 スライス — BFF の文書読み取り（#1255）

- 状態: Proposed
- 日付: 2026-09-06
- 決定者: claude（実装）

## 起点・関連

- 計画: `ADR-0029` §決定（east-west 同期は gRPC。例外は対象経路を明記した新 ADR に限る）／
  `ADR-0075` 決定 3・5・6（一括移行の義務を緩めない・実装 ADR で REST 継続を自認しない・基盤先行は MSP 自身を含む）／
  `ADR-0004`・`ADR-0036` D-07/D-08・`ADR-0054`・`ADR-0056`（文書読み取りの ABAC と存在秘匿）／
  `ADR-0065` 決定 2（操作の実体は `Features/<操作>/`。共有されるものだけを合成点に置く）／
  `ADR-0070` 決定 3（本文なしで完了した文書の区別）／`ADR-0032` §決定（BFF は confidential client）
- 実装 ADR: [[IADR-0379]]（先行条件の 4 決定。**本 IADR はこれを変えない**）／
  [[IADR-0397]]・[[IADR-0400]]・[[IADR-0401]]（先行 3 スライス）／
  [[IADR-0041]]・[[IADR-0045]]（文書 ABAC の唯一の実施点とプリフライトの保持）／
  [[IADR-0388]] 決定 2（`HasBody`）／[[IADR-0290]]（版に本文の参照を載せない）／
  [[IADR-0256]] 決定 3（故障を「該当なし」に化けさせない）／[[IADR-0343]] 決定 2（利用状況の受け口は主体をトークンから採る）
- 実装ガイド（人が読む正）: `docs/api/east-west-grpc.md`（本 IADR で「5 つ目の面」を追記した）
- issue: #1255

## コンテキストと課題

先行 3 スライスは**サービス → サービス**を移した。残るのは **BFF → サービス**である。
BFF は north-south の入口でもあり、後段の多くは**利用者の権限で判定する**（ホップごと ABAC・主体絞り・
管理系の二重ゲート）。[[IADR-0379]] 決定 4 は利用者トークンをメタデータへ載せることを禁じ、
[[IADR-0401]] 決定 2 の「読み口を狭める」は**利用者の権限を使わずに済む場合の解**である。
したがって最初に決めるべきは「**BFF のどの呼び出しが移せるか**」であって、どう移すかではない。

実測（`origin/develop` `a50403ce`。`--is-shallow-repository` = `false`）:

- BFF の名前付き HTTP クライアントは **15 本**（うち MSP 所有 12・AST 所有 3）。
- BFF のエンドポイント群の**呼び出し箇所は 28**、うち**利用者の資格情報を運ぶのは 23**。
- 残る **5 箇所**のうち **4 箇所が文書台帳の読み取り**（一覧・詳細・版履歴・特定版）で、
  1 箇所は検索サービスの属性値照会である。
- 後段の読み取り 4 口は **group に `RequireAuthorization()` を持たない**（一般利用者の文書閲覧のため）。
  すなわち現状 s2s 相当で開いている。
- realm の `users[]` に **`service-account-bff` が無い**。`bff` client は `serviceAccountsEnabled: true`
  だが、サービス用ロールを割り当てる項目が無いため**1 度も付いていなかった** ——
  [[IADR-0379]] 決定 4 の散文（同 IADR 127 行目「realm に service account と `platform-service` を付けた」）は
  **事実に反していた**。

## 決定

### 決定 1: 移すのは「利用者の資格情報を運んでいない呼び出し」だけである。判定は**送る側の行**で引く

BFF の 28 呼び出し箇所を、**後段へ資格情報を付ける行があるか**で二分した。付ける 23 箇所は移さない ——
後段が利用者の権限で判定しており、[[IADR-0379]] 決定 4 を守ったまま運ぶ手が無い（**token exchange の候補**である）。

🔴 **判定軸を「ファイル内に `http.Request.Headers.Authorization` があるか」にしてはならない。**
利用状況イベントの送出は同じ転送を**列に載せた値**（`signal.Authorization`）から行う（[[IADR-0343]] 決定 2）。
ファイル単位・受け取る側の式で引くとこの 1 件を落とし、主体で絞られる経路を「運んでいない」と誤判定する。
同じ理由で母集合は**ファイル単位では引けない** —— 文書と検索は同一ファイルの中に運ぶ箇所と運ばない箇所が同居している。

### 決定 2: 呼び出し先ごとに切り、本スライスは **DocumentService の読み取り 4 口**だけを移す

残る 5 箇所のうち検索サービスの属性値照会（`/search/attribute-values`）は**呼び出し先が違う**。
しかも**同じ名前付きクライアントの兄弟**（`/search`）は資格情報を運ぶ（二段検索のホップごと ABAC）。
検索の面は「呼び出し先が利用者の権限で動く」未決と**同じ PR で決めるべき**であり、本スライスへ混ぜない。

proto は `knowledge/document/v1/document_read.proto`（service `DocumentRead`・rpc 4 本）で、
置き場は**所有者が属するユニットの共有契約プロジェクト**（`Knowledge.Contracts`）である。
knowledge ユニットで proto を持つのはこれが最初であり、そのために当該プロジェクトが codegen を持つ
（新しいライブラリは足していない。4 パッケージとも CPM に版が既にある）。

### 決定 3: 🔴 **判定の位置を動かさない。** gRPC の面は認可を持たず、REST の 4 端点と**同じ本体**を通る

文書単位の ABAC（属性合致 ∧ 個人資料でないこと）は BFF 側のスコープ解決と `IsManageable` ただ 1 つが
実施点である（[[IADR-0041]] / `IADR-0012`）。書き込みプリフライト（[[IADR-0045]]）も残す ——
本スライスが差し替えるのはその中の**取得**だけで、往復は減らさない。

呼び出し先では `DocumentReadUseCase` を括り出し、**REST の 4 端点と gRPC の 4 rpc が同じ関数を呼ぶ**
（[[IADR-0397]] の `EmbedUseCase`・[[IADR-0400]] の `CompletionUseCase` と同じ形。**評価器を 2 つにしない**）。
`ADR-0065` 決定 2 の適用としては「4 操作が共有するもの」なので合成点と同じ階層に置き、各操作フォルダへ複写しない。

面には `ServiceCaller` を要求する。REST の読み取り群がロールで塞いでいない以上、
**この面は現状より狭い**（[[IADR-0401]] 決定 1 と同じ向き）。機械で守るのは
「**管理者の利用者トークンでも `PERMISSION_DENIED`**」の 1 本である。

### 決定 4: 🔴 proto3 に null は無い。**既定が逆向きの真偽値が 1 つある**

| 契約 | REST の既定 | proto3 の「未指定」 | サーバの写し |
| --- | --- | --- | --- |
| `has_body` | **`true`**（DTO 既定） | **`false`** | 🔴 **逆向き。** 常に明示代入する |
| `markdown_uri` | `null`（本文プレースホルダが「(未設定)」を出す） | `""` | `optional`（field presence）で運ぶ |
| `change_note` | `null`（メモ無し） | `""` | `optional` で運ぶ |
| `status` / `version` | 台帳が常に値を持つ | — | 写し不要 |
| 時刻 | `DateTimeOffset`（台帳は UTC で書く） | — | `google.protobuf.Timestamp`。tick 精度は保たれる |

`has_body` の写し漏れは **[[IADR-0400]] の `sent` と同型**の静かな壊れ方をする ——
全文書が「本文なし」に見え、文書詳細が本文の位置へ「本文なし（原本を参照）」を出す（`ADR-0070` 決定 3 /
[[IADR-0388]] 決定 2）。`null` と `""` の潰れも同型で、片方は縮退文言・片方は空文字になる。

### 決定 5: 🔴 **縮退は呼び出し箇所ごとに写す。一般化しない**

実測すると 4 箇所の縮退は**4 通りに割れている**。

| 呼び出し箇所 | 現行 REST | gRPC で写した先 |
| --- | --- | --- |
| 一覧 | 非 2xx も不達も **`[]`** | 同じ `catch` を共有し `[]`（`when` 節の入口だけ広げる） |
| 詳細 | 非 2xx も不達も **`null`**（404 秘匿） | `found=false`・`RpcException`・s2s 取得失敗を同じ `null` |
| 版履歴 | 捕捉なし（**失敗させる**） | 捕捉なし。**空の版履歴に化けさせない**（[[IADR-0256]] 決定 3） |
| 特定版 | 非 2xx は **404**、不達は捕捉なし | `found=false` は 404、`RpcException` は捕捉なし |

したがって**共有クライアントは事実（無い／引けなかった）だけを返し、畳むのは呼び出し元**である
（[[IADR-0401]] 決定 5 と同じ作法）。

🔴 **REST 側の `when` 節へ `InvalidOperationException` を足さない。** REST の JSON 読み取りは
content-type 不一致等でこれを投げ、現行はそれが失敗になる。輸送の差し替えのついでに広げると、
**後段の契約違反が「文書が 0 件」に化ける**。

### 決定 6: 切替は `Services:DocumentServiceGrpc` の有無。**チャネルは宛先ごとに分ける**

未設定なら**何も登録せず** REST のまま（並走中の正は REST。戻すのは構成を外すだけ）。

🔴 **BFF は 2 つの宛先を同時に持ち得る最初のホストである。** 認可サービス宛のチャネルは参照実装が
**キー無し**で登録する。文書サービス宛を同じキー無しで登録すると、片方のクライアントがもう片方の宛先へ繋がる。
文書側は**キー付き**にする（[[IADR-0400]] が LLM ゲートウェイ宛でキー付きにしたのと同じ理由）。

### 決定 7: 🔴 **BFF の s2s 資格情報の未配線をここで閉じる。宛先の未配線は閉じない**

realm へ `service-account-bff`（サービス用ロール付き）を足し、helm と compose へ `ServiceToken__*` を入れる。
**client は既存の `bff` を使い回す**（`ADR-0032` の confidential client がそのまま使える。
2 つ目の client を作ると secret が 2 本になり、片方だけ回されたときに静かに 401 になる）——
したがって ExternalSecret も Vault の seed も**増えない**。

🔴 **認可サービス宛の gRPC アドレス（`Services:AuthorizationServiceGrpc`）は入れない。**
参照実装の切替は本スライスの射程外であり、前 3 スライスも意図的に触っていない。
**資格情報の欠落と宛先の欠落は別の判断**である。

## 理由

- 決定 1 は「移せるか」を意見ではなく**コードの性質**で決めるためである。資格情報を運ぶ行が在ることは
  「後段が利用者の権限で判定する」ことの機械可読な現れであり、走査で数えられる。
- 決定 3 は移行の不変条件そのものである。認可の位置が 1 mm でも動くと、移行の失敗と認可の失敗が
  同じ症状（見えるものが変わる）で現れ、切り分けられなくなる。
- 決定 4 を独立の決定にしたのは、`has_body` の既定が**目に見えにくい場所**（DTO の初期化子）にあり、
  向きまで逆だからである。先行スライスが `sent` で同じ形を踏んでいる。
- 決定 5 で畳み方を呼び出し元に置いたのは、**実測した 4 通りのどれかへ一般化すると 3 つが変わる**からである。

## 結果

- 良い影響: BFF の east-west 呼び出しのうち**移せるものの内訳が測定済みの形で確定**した
  （運ぶ 23／運ばない 5／うち本スライス 4）。文書読み取りの gRPC 面が立ち、
  参照実装が配備上動けなかった原因の 1 つ（BFF の service account 欠落）が閉じた。
- 悪い影響・トレードオフ:
  - `Knowledge.Contracts` が codegen を持つプロジェクトになった（ビルド時に protoc が走る）。
  - 版履歴の rpc は `found=false` を返し得るが、呼び出し元ではプリフライトの後ろにあるため**到達し得ない**。
    到達したときは 404 にする（現行 REST の同条件は失敗になる）—— 到達し得ない枝の差分として受容する。
  - 🔴 稼働クラスタでの h2c 往復は依然として**未実測**（#1255 やること 7）。
  - 🔴 認可サービス宛の gRPC 宛先は未配線のままであり、参照実装は**まだ 1 度も走っていない**。
- フォローアップ:
  1. 検索サービスの属性値照会（BFF の 5 箇所目）を、ホップごと ABAC の未決と同じ PR で決める。
  2. 資格情報を運ぶ 23 箇所は **token exchange の候補**である。計画側の裁定を要する（実装側では決められない）。
  3. introspection の収集は「全申告元を一度に移すスライス」に属し、**本リポジトリだけでは完結しない**。
  4. [[IADR-0379]] 決定 4 の散文（realm の割当が在ると書いてある箇所）は本 PR で事実の側が追いついた。
     散文の是正は行っていない —— 凍結記録の本文を後から書き換えないため。

## 関連

- Supersedes: なし（[[IADR-0379]] の 4 決定は不変。本 IADR はその適用）
- Superseded by: なし

---
title: IADR-0516 SC-12 の無人の登録・差し替えは、検証の後に Keycloak へ機密クライアントとサービスアカウントの属性を書いてから登録簿へ書く。管理用は別の機密クライアント（manage-clients・manage-users）、失敗は補償で戻し、食い違いは定期の照合で知らせる
type: impl-adr
status: Accepted
related_ids: [FR-16, FR-09, UC-09, SC-12, ADR-0123, ADR-0134, ADR-0062, ADR-0088, ADR-0024, ADR-0034, IADR-0297, IADR-0301, IADR-0329, IADR-0366, IADR-0385, IADR-0413, IADR-0479, IADR-0481, IADR-0286]
author: claude
created: 2026-10-08
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md 決定 1〜4・§結果「悪い影響」・フォローアップ 1〜3
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1・3
  - planning:projects/microservices-platform/07_adr/ADR-0134_mcp-client-keycloak-template-and-secret-one-time-display.md 決定 1・決定 3・フォローアップ 1〜3（#1844 追記）
  - planning:projects/microservices-platform/07_adr/ADR-0134_mcp-client-keycloak-template-and-secret-one-time-display.md 決定 2・決定 3・フォローアップ 4〜6（#1845 追記）
related_specs:
  - ../specs/20261008_1786_sc12-keycloak-provisioning.md
  - ../specs/20261009_1817_sc12-provisioning-wiring.md
  - ../specs/20261009_1818_sc12-idp-drift-detection.md
  - ../specs/20261009_1829_sc12-disable-mirror-to-idp.md
  - ../specs/20261009_1844_sc12-interactive-public-client.md
  - ../specs/20261009_1845_sc12-secret-once-and-audit.md
  - ../specs/20261009_1859_keycloak-26-upgrade.md
---

# IADR-0516: SC-12 を IdP への入口にする —— Keycloak への書き込みの口・管理用の資格情報・テンプレート・順序と補償・食い違いの検知（#1786）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-08
- 決定者: claude（#1786。計画 ADR-0123 §結果「悪い影響」が、書き込みの口・管理用の資格情報・種別ごとのテンプレートを実装の IADR に委ねた）

> 🔴 **番号について**: 起草時の `origin/develop`（`5748d5d9`）の最大は IADR-0514 であり、本 IADR は当初 **0515** を採った（欠番を作らない採番規約。`check-adr-numbering.js` の `missing-number`）。並走していた PR #1815（#1781 の切替リハーサル）も IADR-0515 を持っており（起草時に「含まない」と書いたのは誤りだった。PR #1816 の監査 🟡-4）、#1815 が先にマージされた（`358a7f2d`）ため、後からマージする本 PR が規約どおり **0516 へ改番した**（2026-10-09。ファイル名・索引・参照の全数を走査して置換）。

## 起点・関連

- 起点 issue: **#1786**（第 4 回全体監査 #1772 の行「ADR-0123 決定 2・3」の切り出し）。
- 計画: **ADR-0123 決定 1**（属性の正は IdP）・**決定 2**（SC-12 の登録で Keycloak のクライアントを作り、属性をサービスアカウントの利用者属性として書く。登録簿は写し。食い違いは検知して知らせる。Keycloak で直接割り当てない）・**決定 3**（部分集合規則と個人資料の割当禁止は IdP へ書く前に掛ける）・**決定 4**（暫定手段）・**フォローアップ 1〜3**。**ADR-0062 決定 2・3**（部分集合・後段が判定する）。**ADR-0088 決定 1・3**（認可サービスが IdP から引き直す）。
- 先例: [IADR-0301](./IADR-0301_sc17-identity-admin-abstraction.md)・[IADR-0329](./IADR-0329_identity-admin-keycloak-provider-and-realm-wiring.md)（認可サービスの Keycloak Admin REST の口・`identity-admin`・偽の口は非配備ホスト限定・`PUT` は部分更新でない・unmanaged 属性が黙って捨てられる）。[IADR-0366](./IADR-0366_unattended-account-attribute-subset-judgment.md)（部分集合の判定）。[IADR-0385](./IADR-0385_set-valued-user-attribute-encoding.md)（集合値の符号化）。[IADR-0413](./IADR-0413_authz-resolves-user-attributes-and-rest-face-authorization.md)（認可サービスの名指しの照会 `exact=true`）。[IADR-0479](./IADR-0479_mcp-tool-execution-ports-authorize-user-context-themselves.md)（実行口は `service-account-<client>` を運ぶ）。[IADR-0481](./IADR-0481_owner-read-policy-loss-alert-and-content-abac-gate.md)（定期の検査 ＋ 計器 ＋ 警報）。[IADR-0286](./IADR-0286_default-credentials-fail-fast.md)（既定の資格情報を埋め込まない）。
- 基点コミット: MSP `origin/develop` `5748d5d9`。

## コンテキストと課題

SC-12 の登録・属性の差し替えは McpServer の登録簿へ書くだけで、Keycloak の管理 API を呼ぶ箇所は McpServer に無かった。判定は認可サービスが IdP の `service-account-<client>` から引き直した属性で行うので（ADR-0088・IADR-0413）、登録簿の属性は判定に使われていなかった。

### 決めることは 6 つ

| # | 論点 | 選択肢 |
| --- | --- | --- |
| 1 | 書き込みの口をどこに置くか | (a) McpServer（登録を受ける後段）／(b) 認可サービスの `identity-admin` の口を広げて呼ぶ |
| 2 | 管理用の資格情報 | (a) 別の機密クライアント／(b) `identity-admin` に `manage-clients` を足す |
| 3 | 種別ごとのテンプレート | 無人・有人それぞれの形 |
| 4 | 順序と失敗時の扱い | 検証・IdP・登録簿の順と補償 |
| 5 | 食い違いの検知 | (a) 定期の照合 ＋ 計器 ＋ 警報／(b) 書き込み時だけの検知 |
| 6 | フォローアップ 3 の確かめ方 | 認可サービスの照会がサービスアカウントの利用者を返すこと |

## 決定

### 決定 1 — 書き込みの口は McpServer の Infrastructure に置く（Domain の口 `IServiceAccountProvisioner`）

- 口は **`IServiceAccountProvisioner`**（`Domain/Ports`）。実装は **`KeycloakServiceAccountProvisioner`**（`Infrastructure/ExternalServices`）で、Keycloak Admin REST を **named `HttpClient`（`IHttpClientFactory`）＋ client_credentials のトークン**で呼ぶ。**認可サービスの `KeycloakIdentityAdminClient` と同じ作法**であり、Refit は使わない（このリポジトリの Keycloak の呼び出しは型付きの手書きクライアントだけである）。
- **置き場所は登録を受ける後段（McpServer）である。** ADR-0123 決定 3 と ADR-0062 決定 3 は、検証を掛ける場所を「MCP クライアント登録を受けるサービス」と定め、そこを IdP への入口とした。**認可サービスの口を広げる (b) は採らない** —— `identity-admin` は `manage-clients` を持たないことが最小権限の要件であり（IADR-0301 決定 2）、入口と書き込み主体が 2 サービスに分かれると「検証を掛けた値」と「書いた値」の一致を 1 か所で保てない。
- **口は検証しない。** 部分集合の判定と個人資料の割当禁止は、口を呼ぶ前に `McpClientEndpoints.RejectUnassignableAsync`（登録・差し替えの共用。IADR-0366）が掛ける。口へ 2 つ目の判定を置かない。

### 決定 2 — 管理用の資格情報は別の機密クライアント `mcp-client-admin`。realm-management の `manage-clients`・`manage-users` だけ

- 構成は `McpClientProvisioning:Provider` と `McpClientProvisioning:Keycloak:{BaseUrl,Realm,ClientId,ClientSecret}`。**既定値は持たない**（IADR-0286）。
- **与えるロールは `manage-clients`（クライアントの作成・補償の削除）と `manage-users`（サービスアカウントの属性の書き込みと名指しの照会）の 2 つだけ**である。view は manage が含むので与えない。`manage-realm`・`impersonation` は与えない。**`identity-admin` とは別のクライアントにする**（(b) を採らない理由は決定 1）。
- 🔴 **`manage-clients` は realm の全クライアントを変えられる。** 入口が作ったクライアント以外へは書かない形をコードで保つ（決定 4 の「既にあれば書かない」・補償は自分が作った内部 ID だけを消す）。権限をさらに狭める（Keycloak の fine-grained admin permissions）のは残余とする。
- 選択（`Provider`）:
  - `keycloak` … 資格情報が欠けたら**起動時に落ちる**（Program.cs が起動時に 1 度解決する）。
  - `in-memory` … **非配備ホスト（Development / Testing / Integration）限定**（IADR-0329 と同じ許可集合）。配備ホストで選べば起動時に落ちる。
  - **未設定 … 起動は止めず、無人の登録・差し替えを 503 で拒む（登録簿にも書かない）。** 起動失敗にしないのは、配備の資格情報（realm のクライアントと secret の供給）が後続の段で入るまで MCP サーバーそのもの（ツールの公開・有人の登録・無効化）を止めないためである。**「無人を登録簿だけへ書く」へは倒さない** —— それは ADR-0123 が改めた現状そのものであり、決定 4 の暫定手段（IdP へ属性を配らない）と同じ側の 503 にする。

### 決定 3 — テンプレートは無人だけを定める。有人は IdP へ書かない（既知の逸脱）

- **無人（サービスアカウント）**: `publicClient=false`・`clientAuthenticatorType=client-secret`・`serviceAccountsEnabled=true`・**人の流れは全部閉じる**（`standardFlowEnabled` / `implicitFlowEnabled` / `directAccessGrantsEnabled` = false・`redirectUris` / `webOrigins` は空）。クライアント属性 `msp.mcp-client.managed-by=mcp-server` を付ける（照合と運用者の識別のため）。**クライアントのスコープは指定しない**（realm の既定）—— 判定に使う属性はトークンからではなく認可サービスが引き直すので（ADR-0088 決定 1）、トークンの中身は判定を変えない。
- **属性は丸ごと置き換える。** サービスアカウントの利用者属性を書くのはこの入口だけである（ADR-0123 決定 2）。集合値キー（`tags` / `projects`）は多値、単一値キーは 1 要素の配列で書く（IADR-0385 と同じ符号化）。
- 🔴 **既知の逸脱: 有人は IdP へ書かない（登録簿だけ。従来どおり）。** 計画の SC-12 は種別を限らず「登録 → Keycloak クライアント作成」と定めるので、これは問いではなく**計画とのずれ**である。有人のクライアント（認可コード + PKCE）にはリダイレクト先などの入力が要るが、登録の契約にも計画の画面にも無い。作り方は計画へ環流した（planning#751）。回答が来るまで逸脱として残す。
- **client secret は応答で返さない。** Keycloak が生成し、管理者は Keycloak の管理画面で取得する（属性を直接割り当てるのではないので ADR-0123 決定 2 の禁止には当たらない）。受け渡しの経路は計画へ環流した（planning#751）。
- **`fullScopeAllowed=false`**。realm の全ロールをトークンへ載せない（サービスアカウントへ割り当てたロールだけ）。

### 決定 4 — 順序は「検証 → 登録簿の重複 → IdP → 登録簿」。失敗は補償で書く前へ戻す

```
登録:   入力の検証 → 部分集合・個人資料の判定 → 登録簿の重複 → IdP（作成 → SA の利用者 → 名指しの照会 → 属性の書き込み → 読み戻し）→ 登録簿
差し替え: 部分集合・個人資料の判定 → IdP（無ければ作成〔登録簿で無効なら無効のまま〕。在れば入口の印を確かめ → 元の属性を読む → 書き込み → 読み戻し）→ 登録簿
```

- **IdP を先に書く。** 登録簿は IdP へ書いた値の写しである（決定 2）。登録簿を先に書くと、IdP への書き込みが失敗した間、判定に効かない値が「割り当て済み」として画面に出る。
- **IdP に入口が作っていないクライアントがあれば、何も書かずに 400 で拒む。** 登録では同じ clientId があれば（Keycloak の 409）、差し替えでは `GET /clients/{id}` の表現に入口の印（`msp.mcp-client.managed-by=mcp-server`）が無ければ、である。入口を通らずに作られたクライアント（プラットフォーム自身の機密クライアントを含む）へ属性を書くと、検証の掛からない主体ができ、その主体の本来の属性も上書きする —— 本入口ができる前の SC-12 は `abac-seeder` という名の無人の行を作れたので、差し替えが `service-account-abac-seeder` の属性を書き換え得た（PR #1816 の監査 🔴-1。差し替えにも印の確かめを足した）。補償でも消さない（自分が作っていない）。
- **IdP の途中の失敗**（SA の利用者が無い・照会が合わない・書けない・読み戻せない）は、作ったクライアントを消してから 502 で返す（差し替えは元の属性を書き戻す）。**登録簿には書かない。**
- **登録簿への書き込みの失敗**は、IdP への書き込みを取り消してから元の例外を投げる（`IdpFirstWrite`）。取り消しも失敗したら Error ログを残し、元の失敗を投げる（補償の失敗で理由を上書きしない）。残った食い違いは決定 5 が拾う。
- **差し替えで IdP にクライアントが無い行**（本入口ができる前の登録簿の行）は、その場で作ってから書く。登録簿の重複検査が再登録を止めるので、これが旧い行を IdP へ載せる唯一の経路である。
- **登録簿で無効化された行**の差し替えで IdP に作るときは、**無効のまま作る**（有効なクライアントを生まない）。
- 🔴 **要求の取り消しを IdP への書き込みと補償へ伝えない**（PR #1816 の監査 🟡-1）。書きかけで止めると、作ったクライアントが孤児として残る。期限は口の HttpClient の `Timeout`（`McpClientProvisioning:Keycloak:TimeoutSeconds`。既定 10 秒・1〜120）が持ち、時間切れは `Failed`（502）へ写して補償する。登録簿への書き込みが取り消し・時間切れで止まったときも補償する。
- 🔴 **`POST /clients` そのものが時間切れ・到達不能・5xx になったとき**（作成の成否が分からない。PR #1816 の再監査 🟡-A）は、clientId で引き直し、**入口の印があれば消して**から `Failed`（502）を返す。Keycloak 側で作り終えていた場合に、印つき・属性なし・有効の孤児を残さない（残すと再登録は 409・差し替えは書けない）。**残る隙**: 同じ clientId の登録が並行し、片方の作成の要求が時間切れ・もう片方が成功した場合、時間切れの側の補償が成功した側のクライアントを消し得る（印では区別できない）。引き直しと削除そのものが失敗した場合も孤児が残る（Error ログ）。どちらも照合（決定 5。#1818）が拾う。
- 登録簿への書き込みが例外でなく**失敗の結果**（4xx / 5xx）を返したときも補償する。`Location` が無く内部 ID の引き直しにも失敗したときは、補償の側で clientId から引き直して消す。
- 管理用トークンが 401 で拒まれたら、1 度だけ取り直して送り直す。
- 🔴 **取り消しは、IdP の現在値がこの要求の書いた値のままのときだけ書き戻す**（PR #1816 の監査 🟡-2）。並行した差し替えが後から書いた値を、古い値で潰さない。**残る競合**: 2 つの差し替えが交差すると、IdP は後の要求・登録簿は先の要求の値になり得る（行の排他は入れていない）。これは照合（決定 5。#1818）が拾う。
- 状態の写し方: 503（口が無い）／502（IdP へ書けなかった）／400（検証の外れ・IdP に既にある）。境界層は状態コードを作り替えない（ただし境界層自身の不達も 502 であり、画面からは区別できない。残余）。

### 決定 4a — 無効化（Q3）

- **計画が答えている**: SC-12 の無効化は「即時に接続拒否」であり、MCP サーバーが呼び出しごとに登録簿を引いて拒否する形で既に満たしている。
- **多層の防御として、無効化を IdP のクライアントの `enabled` へ写すことを決める。** 実装は後続の段（#1817）で行う。それまでも、差し替えで IdP に作るときに無効な行から有効なクライアントを生まない（決定 4）。

### 決定 5 — 食い違いは定期の照合で検知し、計器と警報で知らせる（本 PR では実装しない）

- **形は IADR-0481（所有者の読み取りのポリシーの検査）と同じ「定期の検査 ＋ 計器 ＋ 警報」とする。** McpServer の常駐処理が周期ごとに、登録簿の無人の行ごとに IdP のサービスアカウントの属性を読み、決定 3 の符号化で突き合わせる。
  - 計器: 直近の照合で食い違った行数のゲージ（照合に失敗したとき・未照合のときは系列を出さない）と、照合の結末のカウンタ（`match` / `drift` / `failed`）。
  - 警報: 「食い違いが 1 件以上」と「系列が無い」の 2 本。置き場は既存の警報の写し 4 か所（`deploy/prometheus/alerts.yml` ほか）と同じ。
  - 食い違いの種類: IdP にクライアントが無い／属性が違う／入口の印（`managed-by`）が無い。
- **書き込み時だけの検知 (b) は採らない。** Keycloak を直接操作した変更（ADR-0123 決定 2 が禁じた操作）は、次の書き込みまで見えない。
- 🔴 **本 PR はこの照合を実装しない**（issue の受け入れ基準 3 は未達）。#1818 で入れる。

### 決定 6 — フォローアップ 3 は、書き込みのたびに「認可サービスと同じ照会」で確かめる

- 書き込みの口は、属性を書く前に **`GET /users?username=service-account-<client>&exact=true`**（認可サービスの `FindByUsernameAsync` と同じ照会）を引き、**`GET /clients/{id}/service-account-user` と同じ利用者が 1 人だけ**返ることを確かめる。返らなければ書かずに失敗する（決定 4 の補償）。**判定に使われない属性は書かない。**
- 利用者名の組み立ては `ToolUserContext.ServiceAccountUserName` の 1 か所に寄せた（実行時に下流へ運ぶ `user_id` と、属性を書く先の利用者名が同じ綴りであることを構造で保つ。Keycloak はサービスアカウントの利用者名を小文字で持つ）。
- 認可サービス側は、名指しの照会がサービスアカウントの利用者を落とさないことを単体テストで固定した（全件の列挙は機械の主体を除くが〔#1609〕、名指しの照会はそれに倣わない）。
- 🔴 **Keycloak がこの照会でサービスアカウントの利用者を返すこと自体は、稼働の Keycloak でしか確かめられない**（残余）。決定 6 の確かめは、返らない場合に「書いたつもりで判定に効かない」状態を作らないための安全側の作りである。

## 統制と現在の実現手段

| 統制 | 現在の実現手段 | 配備までの暫定手段 |
| --- | --- | --- |
| 無人の属性は検証の後に IdP へ書く（ADR-0123 決定 2・3） | **ある（コード）。** 検証 → IdP → 登録簿（決定 4）。単体・API 面の試験で固定 | 🔴 **配備では未宣言**（`McpClientProvisioning:Provider`）。無人の登録・差し替えは 503 で拒み、登録簿にも書かない（ADR-0123 決定 4 の暫定と同じ側） |
| 入口を通らない主体へ属性を書かない | **ある。** 登録は IdP に同じ clientId があれば 400、差し替えは入口の印が無ければ 400。補償は自分が作ったものだけ消す | — |
| 登録簿と IdP の食い違いを知らせる（決定 2） | 🔴 **無い**（決定 5 は未実装） | 書き込み時の補償と Error / Warning ログだけ。照合は #1818 |
| 認可サービスの照会がサービスアカウントを返す（フォローアップ 3） | **書き込みのたびに確かめる**（決定 6）。認可サービス側は単体テスト | 🔴 稼働の Keycloak での実測は無い |

## 結果

- 良い: 無人の属性の正（IdP）と検証の掛かる場所（入口）が一致する。登録簿は IdP へ書いた値の写しになる。判定に使われない属性を書かない。
- 悪い（受容）: 配備の資格情報が入るまで、無人の登録・差し替えは 503 になる（従来は登録簿へ書けていた）。
- 🔴 悪い（受容・PR #1816 の監査 🟡-3）: **`mcp-client-admin` の権限は入口の用途より広い。**
  - `manage-users` は、人の利用者を含む realm の**全利用者**の属性とロールを書き換えられる（例: `clearance`）。ADR-0062 の部分集合規則と SC-17 の利用者管理を経ずに書ける。
  - `manage-clients` は realm の**全クライアント**を作成・変更・削除でき、**全クライアントの secret を読める**。
  - **secret が漏れたときの影響範囲**: realm 内の任意の利用者の属性・ロールの改ざん（権限昇格・ABAC の判定の書き換え）、任意のクライアントの secret の取得（他サービスへのなりすまし）、クライアントの削除（認証の停止）。realm の設定（`manage-realm`）と impersonation は持たない。
  - 緩和は、コードが入口の印のあるクライアントとそのサービスアカウントにしか書かないこと、secret を配備の秘密の経路だけで配ること（#1817）。権限そのものを狭めるのは残余 5。
- 悪い（受容）: 境界層の不達（502）と IdP への書き込みの失敗（502）は画面から区別できない。

## 残余

1. **配備の配線**（#1817）: realm（`deploy/keycloak/microservices-platform-realm.json`）へ `mcp-client-admin`（manage-clients・manage-users）を足す。secret の供給（ExternalSecret `mcp-client-admin-oidc`・Vault の初期投入・`scripts/k8s-local-up.sh` の手動経路・SC-22 の秘密の項目）。helm values の `McpClientProvisioning__Provider` と `ClientSecret` を有効にする（本 PR はコメントで置いた）。compose の配線。 無効化の IdP への写し（決定 4a）も同じ段で入れる。
2. **稼働の Keycloak での実測**（#1817）: 作成・照会（フォローアップ 3）・属性の書き込みと読み戻し・補償の削除。統合試験（Keycloak の Testcontainers）は本リポジトリに無い。
3. **食い違いの検知**（決定 5。#1818）の実装と警報。交差した差し替えの残る競合（決定 4）もここで拾う。
4. 有人のクライアントの作り方（既知の逸脱。決定 3）と client secret の受け渡しは、計画の回答（planning#751）を待つ。
5. `mcp-client-admin` の権限を、入口の印のあるクライアントとそのサービスアカウントだけへ狭める。Keycloak の fine-grained admin permissions v2 は 26.2 以降であり、**配備の Keycloak は 24.0** なので、Keycloak の更新を待つ。

## ［2026-10-09 追記 / #1817］段 2: 配備の配線と稼働の Keycloak での実測

新しい決定は無い（決定 2 と §残余 1・2 が中身を決めていた）。**配線の形と、実測の取り方だけを記録する。**

- **§残余 1（配備の配線）は解消した。** realm に `mcp-client-admin`（機密・SA のみ・標準 / 暗黙 / 直接付与は閉・既定スコープ `realm-management-roles` だけ・SA に `realm-management` の `manage-clients` と `manage-users` だけ・realm ロールなし）。secret は `identity-admin`（IADR-0329）と同型に供給する: Vault の種（`vkv_create_if_absent msp/mcp-client-admin-oidc`。対になる秘密なので無いときだけ作る。ADR-0124 決定 1）→ ExternalSecret `mcp-client-admin-oidc`（キー `client-secret`）→ helm の**非 optional** な secretKeyRef。ESO を使わない起動は `k8s-local-up.sh` の手動 apply。SC-22 の項目表は `deferred[]`（画面から書かせない）。helm と compose は `McpClientProvisioning__Provider=keycloak` を宣言した。
  - 「**既定値なしで供給する**」の読み: **アプリ（McpServer）と helm は既定値を持たない**（決定 2・IADR-0286。Secret が無ければ Pod が起動しない）。dev の値（`mcp-client-admin-dev-secret-change-me`）は realm の宣言と同値で、**供給の経路（Vault の種・手動 apply・compose）だけ**が持つ。これは realm の他の全機密クライアントと同じ形であり、realm の宣言の secret は作成時だけ使われる（IADR-0485。回した後は Vault が正）。
  - 静的な固定: `scripts.repo.test.js` の #1817 節（ロールの集合が 2 つちょうど・否定形 `manage-realm` / `impersonation` / `realm-admin` / `view-realm` ほか・`manage-clients` を持つ主体はこれ 1 つ・供給の連鎖の名前とキーと dev 値の一致）、`helm-mcp-client-provisioning.test.js`（**書き込み口を持つ Deployment はすべて Provider=keycloak を持つ** ＝ 503 のまま Ready の配備を残さない・mcp-service だけ・非 optional・変異）、`k8s-local-up.test.js`（ESO の有無の対）、`check-realm-constraints.js`（スコープ・SA 利用者 1 つ）。
- **§残余 2（稼働の Keycloak での実測）は integration-stack の門に載せた**（`scripts/check-mcp-client-provisioning.js --live`。M1〜M6 は作業仕様書 20261009_1817）。取り方の判断:
  - **登録者は実行ごとに作る使い捨ての機密クライアント**（SA に `platform-admin`・既定スコープ `profile` / `roles`）。realm の `abac-seeder` は既定スコープに `profile` が無く `preferred_username` が載らないので、登録者の属性を引けず、部分集合の判定は「検証できません」の 400 になる（規則そのものを測れない）。部分集合の外れは「外れた値を名指しした 400」であることまで確かめる。
  - **照会は master の管理者**（測る側と測られる側を分ける）。**補償が使う削除の権限**は `mcp-client-admin` の資格情報そのもので M1 のクライアントを消して測る。
  - **補償の経路は稼働の構成のまま作る**: 登録簿の `DisplayName` は `varchar(200)`、Keycloak のクライアントの `name` は 255 文字まで。201〜255 文字の表示名は「IdP へ書けて登録簿で落ちる」ので、決定 4 の補償（作ったクライアントを消してから元の例外）が稼働で走る。
  - **差し替えの入口の印の確かめ**は、入口ができる前の登録簿の行（無人・`abac-seeder`）を psql で置いて測る（API では作れない形だから）。
  - 🔴 **門は PR では走らない**（integration-stack は日次・develop への push・手動）。本 PR のマージ後の最初の実行が初回の実測になる。**フォローアップ 3 が成立しなければ M2 と M4 の部分集合が赤になる**（どちらも名指しの照会でサービスアカウントを引く）。
- **決定 4a（無効化の IdP の `enabled` への写し）は本段に入れなかった。** 段 1 の作業仕様書は段 2 に含めたが、issue #1817 の受け入れ基準に無い。503 を閉じる本段を小さく保つため外す。それまでも差し替えで作るときは無効な行から有効なクライアントを生まない（決定 4）。後続の issue で入れる。
- **残余 5（権限の絞り込み）は変わらない。** 配備の Keycloak は 24.0 で、fine-grained admin permissions v2（26.2 以降）が無い。漏えい時の影響範囲（全クライアントの secret・全利用者の属性とロール）とローテーションは `docs/security/security.md` と `docs/operations/paired-secret-rotation-runbook.md` に書いた。
- 統制表の「無人の属性は検証の後に IdP へ書く」の暫定手段（配備では未宣言 → 503）は、本段で**配備でも宣言済み**になった。
- ［PR #1827 監査への対応］
  - 🟡3 **§結果の「realm の設定（`manage-realm`）と impersonation は持たない」は、直接のロールとしては正しいが影響範囲としては過小だった。** `manage-clients` は全クライアントの secret を読めるので、`reset-gate`（`manage-realm`）と `identity-admin`（`manage-users`）の secret も読め、レルムの設定まで間接的に届く。**漏えいはレルムの全権の漏えいとして扱う**（`security.md`・runbook を改めた。runbook の手順 2「群 1 の全件を回す」は変えない）。
  - 🟡4 **dev 以外のクラスタに公知の dev の値が入る。** 起動器は env が無ければ dev の値で保管先・Secret を作り、後追いも無い client を dev の値で作る（`identity-admin`・`reset-gate` と同じ型だが、権限が最も広い）。`security.md`「本番流用の禁止」と runbook に「dev 以外では起動の直後に回す」を書いた。起動器には dev かどうかを判定する文脈が無く、既存の仕組みで安く止められないので、機械の守りは #1830 へ分離した。
  - 🟡2 M6 は 5xx なら何でも通していた（IdP への書き込み自体の失敗＝502 でも「何も残らない」は自明に真）。**500 に限定**した —— 登録簿への書き込みの例外は IdpFirstWrite が補償してから投げ直し、McpServer に例外の写し替えが無いのでホストの既定の 500 になる（502 / 503 は IdpFirstWrite が ProblemDetails で返す）。併せて管理イベントで `mcp-client-admin` の「作成 → 削除」を確かめる（realm は管理イベントの詳細の記録が有効）。自己試験に 502 / 503 の陰性対照を足した。

## ［2026-10-09 追記 / #1818］段 3: 食い違いの検知（決定 5 の実装）

決定 5 が形（IADR-0481 と同じ「定期の検査 ＋ 計器 ＋ 警報」）を決めていた。**本段で決めたのは具体値と、決定 5 が書いていなかった細部だけである**（新しい IADR は起こさない）。
基点は `origin/develop` `ff20ce19`。作業仕様書 [20261009_1818_sc12-idp-drift-detection](../specs/20261009_1818_sc12-idp-drift-detection.md)。

- **読み取りの口は書き込みの口と別の型にする**（`Domain/Ports/IServiceAccountDirectory`: クライアントの一覧〔入口の印の有無〕と、サービスアカウントの属性）。
  照合が書けないことを型で保つ（決定 5 は検知して知らせるだけ）。実装は書き込みの口の 3 つ（Keycloak・プロセス内・未構成）が兼ね、
  **選択は `McpClientProvisioning:Provider` の 1 つ**（書く先と照合が読む先が別の IdP になる構成を作れない）。資格情報は `mcp-client-admin` のまま
  （列挙と照会は `manage-clients` / `manage-users` に含まれる view で足りる。権限は増やさない）。
  - Keycloak 版は `GET clients?first=&max=100` を頁で読み（上限 100 頁 ＝ 1 万件。超えたら読み切らずに失敗＝途中までの一覧で孤児を数えない）、
    属性は**認可サービスと同じ照会**（`users?username=service-account-<client>&exact=true`）で読む（決定 6。判定に効いている値と比べる）。
  - 読み取りには**要求の取り消しを伝える**（書き込みと違い、途中で止めても孤児は生まれない）。時間切れは HttpClient の `Timeout`（既定 10 秒）で `Failed`。
- **食い違いの種類は 5 つ**: 決定 5 の 3 つ（`client_missing`・`attributes_differ`・`not_managed`）に、`service_account_missing`（印つきのクライアントはあるが
  同じ照会で SA が引けない。判定ではその主体は拒否になる）と `orphan`（印つきのクライアントに登録簿の無人の行が無い。issue #1818 の受け入れ基準 2）を足した。
  比べ方は書き込みの読み戻しと同じ `SameAttributes`（集合値は集合。IADR-0385）。**有人の行と `enabled` は比べない**（決定 3 の逸脱・決定 4a の未実装。
  比べると無効化した行がすべて食い違いになる）。
- **計器**（Meter `microservices-platform.mcp-server`。McpServer に初めて足した）: ゲージ `mcp.idp_reconciliation.drifted`（`{client}`。直近の照合の件数。
  未照合・失敗は系列なし）と `mcp.idp_reconciliation.checks.total{mcp.idp_reconciliation.outcome=match|drift|failed}`（**1 回の照合に 1 つ**。行ごとではない）。
  🔴 **クライアント ID を計器の属性に載せない**（系列の数を有界に保つ）。行はログ（Warning `client=<id> kind=<種類>`。1 回に 20 件まで）で名指しする。値は出さない。
- **周期と期限**: `McpClientProvisioning:Reconciliation:Interval`（既定 `00:01:00`・下限 1 分・`hh:mm:ss`・値域外は起動時に落とす。IADR-0481 と同じ規則）。
  **1 回の照合の期限は周期と同じ長さ**（次の周期に重ねない。超えたら失敗）。行ごとの読み取りの並行は 4 まで。周期・期限は `TimeProvider` で数える。
  helm・compose には値を置かない（IADR-0481 の検査も置いていない。コードの既定で回す）。
  - **検知までの最大の遅れ**: 食い違いが起きてから `McpClientIdpDrift` が鳴るまで **およそ 7 分**（周期 1 分 ＋ 1 回の照合の期限 1 分 ＋ `for: 5m`）。
    照合の失敗が続き始めてから `McpClientIdpReconciliationSeriesAbsent` が鳴るまで **およそ 12 分**（＋ 瞬間ベクタの lookback 約 5 分。IADR-0481 の 2026-09-28 追記と同じ理由）。
  - 1 回の照合が IdP へ送る要求は「一覧 ⌊クライアント数 / 100⌋ ＋ 1（最後の頁が 100 件に満たないと分かるまで読む）＋ 印つきのクライアントの無人の行の数」。**レプリカごとに照合する**（レプリカの間で揃えない）ので、
    負荷はレプリカ数倍になる。行が数百を超えるなら周期を延ばす。
- **警報**（4 か所の写し。`scripts.repo.test.js` #1818 が C# の名前と突き合わせる）: `McpClientIdpDrift`（warning。Prometheus `mcp_idp_reconciliation_drifted >= 1`、
  Grafana は生の値を `gt 0`・`noDataState: OK`）と `McpClientIdpReconciliationSeriesAbsent`（warning。`absent(…)`）。どちらも `for: 5m`。
  - **critical にしない理由**: 判定は IdP の値で行われており（決定 1）、照合が知らせるのは「写しがずれた」ことである。IdP を直接書き換えて属性を広げる操作は
    Keycloak の管理権限を要する（それ自体が ADR-0123 決定 2 の禁止）。対応は時間単位でよく、人を即時に起こす種類ではない。
  - **一時の食い違い**: 登録・差し替えの最中（IdP へ書いた後・登録簿へ書く前）を照合が見ると、孤児・属性違いが 1 周期だけ出る。次の周期で消えるので `for: 5m` が吸収する。
- **未構成（`Provider` 未設定）は失敗として数える**（系列なし → 「見ていない」が鳴る）。照合を opt-in にしない（IADR-0481 と同じ）。ログは Error ではなく Warning。
- **決定 4 の残余**: 交差した差し替えの後勝ち（IdP は後の要求・登録簿は先の要求）は照合が `attributes_differ` として**検知する**（`IdpReconciliationRaceTests` で固定。
  本物の `IdpFirstWrite` を 2 本交差させる）。**防ぎはしない**（行の排他は入れていない）。並行登録で時間切れの側の補償が成功した側のクライアントを消した場合は
  `client_missing`、補償の失敗の残骸は `orphan` として拾う。
- **稼働の Keycloak での実測**（integration-stack の門 `check-mcp-client-provisioning.js --live` の M7）: このスタックは Prometheus を起こさないので**ゲージは読めない**。
  代わりに `kubectl logs` で照合のログを読み、master の管理者で直接書き換えた属性が `attributes_differ`、登録簿を通らずに作った印つきのクライアントが `orphan` として
  名指しされることを最大 150 秒待って確かめる。照合が失敗し続けていれば名指しは出ないので赤になる（＝一覧が印を含むこと・`mcp-client-admin` で列挙と照会が通ることの実測）。
  「M1〜M6 の後にゲージが 0」は測らない（観測の器が無い。後片付けの前は M5 の古い行などで 0 にならない）。
- **統制表の更新**: 「登録簿と IdP の食い違いを知らせる（決定 2）」の現在の実現手段は **ある**（定期の照合 ＋ 計器 ＋ 警報 2 本）。暫定手段（補償とログだけ）は不要になった。
- 🔴 **配備の直後に鳴り得る**: 書き込みの口ができる前（段 1 より前）に登録した無人の行は IdP にクライアントが無く、`client_missing` として数えられる。
  運用仕様書の手順（画面から属性を差し替える＝IdP に作られる）で解消する。これは計画（ADR-0123 決定 2）が求めた「知らせる」そのものであり、警報を弱めない。
- ［PR #1831 監査への対応］
  - 🟡1 **ログの名指しは重大度順**（`attributes_differ` → `orphan` → `not_managed` → `service_account_missing` → `client_missing`、同じ種類はクライアント ID 順）。
    クライアント ID 順だと、段 1 より前の行の `client_missing` が 20 件を超えたとき、セキュリティに関わる属性違い・孤児が上限の外へ押し出された。
    種類ごとの件数を照合ごとの Information の 1 行に出す。M7 のプローブが上限の外に落ちる脆さも同時に消えた。
  - 🟡2 1 行が読めないと照合全体を失敗にする設計は変えない（途中までの結果で数えない）。代わりに行を `IdpReconciliationRowException` で包み、Error ログでその行を名指しする。
    運用仕様書に「系列の不在が続く原因は 1 行だけのことがある」と特定の手順を書いた。
  - 🟡3 配備直後の `client_missing` と対処（画面で保存し直す）を運用仕様書に書いた。
  - AI レビュー 🟡: 認可サービスと同じ照会（利用者名の完全一致）を `FindServiceAccountUsersAsync` の 1 か所へ括り出し、書き込みの確かめと照合の読み取りが共有する（決定 6 の核）。
  - 🟢 要求回数の式を ⌊n/100⌋＋1 に直した。
- 残余: 自動の修復はしない（決定 5）。行の排他は無い（交差は検知だけ）。`enabled` の写しと比較は決定 4a とともに後続。計器と警報の発火そのものは稼働では測っていない
  （式の一致は `scripts.repo.test.js`、発火し得ることは `check-grafana-alerting.js` の検査 6 が見る）。

## ［2026-10-09 追記 / #1829］決定 4a の実装: 無効化・再有効化を IdP のクライアントの `enabled` へ写し、照合が有効・無効も比べる

決定 4a が「写す」ことを決めていた。**本段で決めたのは順序・失敗の扱い・照合の比べ方という細部だけである**（新しい IADR は起こさない）。
基点は `origin/develop` `60bca28a`（段 3 の PR #1831 のマージ）。作業仕様書 [20261009_1829_sc12-disable-mirror-to-idp](../specs/20261009_1829_sc12-disable-mirror-to-idp.md)。

- **分離した経緯**: 決定 4a は「実装は後続の段（#1817）」と書き、段 1 の作業仕様書も段 2 に含めた。段 2（#1817 / PR #1827）は issue の受け入れ基準を
  配備の配線と稼働の Keycloak での実測に絞り、配備で無人の登録・差し替えが 503 になる期間を短くすることを優先して決定 4a を外した（同追記）。
  段 3（#1818 / PR #1831）の照合も、写しが無い間は比べると無効化した行がすべて食い違いになるので `enabled` を比べなかった（同追記）。
  本段（#1829）はその 2 つの残りを合わせて入れる。SC-12 の要件「無効化は即時に接続拒否」は従前から登録簿で満たしており、本段は多層の防御である。
- **口**: `IServiceAccountProvisioner.SetEnabledAsync(clientId, enabled)`。結果は `IdpWrite` で、種類を 2 つ足した:
  `EnabledChanged`（入口の印つきのクライアントの `enabled` を書いた。同じ値なら書かない。`PreviousEnabled` / `WrittenEnabled` を持つ）と
  `Absent`（IdP に同じ clientId が無い＝段 1 より前の行。何も書かない）。印が無ければ既存の `AlreadyExists`（何も書かない）。
  - Keycloak 版はクライアントの完全一致の照会（`FindClientInternalIdAsync`。登録・差し替えと共有の 1 つ）→ `GET /clients/{id}`（印と現在値）→
    **`PUT /clients/{id}` へ `{"enabled": …}` だけ**→ 読み戻し。🔴 **表現を丸ごと送り返さない**: クライアントの表現は secret を含み、読んでから
    書くまでの間に回された secret を古い値へ戻し得る。Keycloak のクライアントの `PUT` は null・欠けた項目を変えない（利用者の `PUT` と違う。IADR-0329 の実測は利用者）。
    稼働での確かめは M8（再有効化の後にトークンが出て、テンプレートの項目が残る）。
  - 資格情報は `mcp-client-admin` のまま（`manage-clients` に含まれる）。要求の取り消しは伝えない（書き込みの口の規則）。
  - 🔴 **失敗は閉じる側へだけ倒す**: 再有効化（開く）の書き込みが途中で失敗したら無効へ戻してから `Failed`。無効化（閉じる）の失敗では戻さない
    （通っていたかもしれない無効化を取り消して開くことになる）。
  - 取り消し（`UndoAsync`）は決定 4 と同じ規則（現在値がこの要求の書いた値のときだけ前の値へ）。2 値なので結果は変わらないが、要らない書き込みを送らない。
- **順序は向きで変える**（決定 4 の「IdP を先に書く」は、登録簿に在る値が IdP にも在ることを保つためだった。閉じる操作ではそれより即時性が勝つ）:
  - **無効化は登録簿 → IdP。** SC-12 の「即時に接続拒否」が優先する。**IdP への写しが失敗（`Failed`）・未構成（`Unavailable`）・印なし・無しでも、登録簿は取り消さずに 200**。
    ログで残す（失敗は Error、未構成と印なしは Warning、無しは Information）。食い違い（登録簿は無効・IdP は有効）は照合の `enabled_differs` が拾う。
    同じ無効化をもう一度送れば写し直す（冪等）。応答を 502 にしない理由: 登録簿の無効化（要件そのもの）は成っており、画面が「失敗」と出すと運用者は
    無効化できていないと読む。
  - **再有効化は IdP → 登録簿**（接続を開く側。`IdpFirstWrite` を通す）。IdP へ書けなければ 502 / 503 で登録簿を書かない。登録簿の失敗は IdP を無効へ戻す。
    🔴 **印の無いクライアント（`abac-seeder` 等と同名の古い行）は 400 で、どちらにも書かない**（入口を通らない主体へ接続を開かない。差し替えの 400 と同じ規則。
    拒否の文言は `IdpFirstWrite` の引数で差し替えた）。**IdP に無い行（`Absent`）は登録簿だけを有効にする**（IdP にクライアントが無いのでトークンは出ず、
    開くものが無い。照合が `client_missing` を出し続ける）。
  - **未構成（`Provider` 未設定）**: 無効化は登録簿だけで 200、再有効化は 503（登録・差し替えの 503 と同じ側）。
  - 有人の行は IdP に触れない（決定 3 の逸脱のまま）。
- **照合に `enabled_differs` を足した**（登録簿の無人の行の `Enabled` と、入口の印つきのクライアントの `enabled` が違う。種類は 6 つ）。一覧の表現で読むので
  要求は増えない（`IdpClientEntry.Enabled`）。印なし・IdP に無い行は比べない（それぞれ `not_managed` / `client_missing` が既に数える）。
  - **重大度**: `attributes_differ` → `orphan` → **`enabled_differs`** → `not_managed` → `service_account_missing` → `client_missing`。
    「登録簿は無効・IdP は有効」は多層の防御が欠けた状態で、古い行の多数に押し出させない。
  - 🔴 **ゲージはクライアントの数で数える**（1 行が `attributes_differ` と `enabled_differs` を同時に持ち得るようになった）。ゲージの説明「食い違ったクライアントの件数」と一致させた。
    警報の式・閾値（`>= 1`・`gt 0`）は変えない。警報の説明（写し 4 か所）の種類の列挙に「有効と無効が違う」を足した。
  - **一時の食い違い**: 無効化（登録簿 → IdP）・再有効化（IdP → 登録簿）の最中を照合が見ると 1 周期だけ出る。`for: 5m` が吸収する（#1818 追記と同じ）。
  - **無効化と再有効化の交差**（行の排他は無い）で両者がずれ得る。照合が `enabled_differs` として検知する（防ぎはしない）。
- **稼働の Keycloak での実測（integration-stack の門の M8・M7 の追加）**: 入口で登録したクライアントの secret を master の管理者で読み、無効化の前は
  `client_credentials` のトークンが出る（陽性対照）→ SC-12 の無効化（200）で Keycloak の `enabled` が false・トークン発行が 4xx で拒否・登録簿も無効 →
  再有効化（200）で `enabled` が true・トークンが再び出る・テンプレートの項目（入口の印・人の流れの閉）が残る。否定形: M5 が置いた入口ができる前の行
  `abac-seeder` の無効化は 200 で `abac-seeder` の `enabled` は true のまま、再有効化は 400。M7 には master の管理者が直接無効にした印つきのクライアントの
  `enabled_differs` を足した（一覧の表現が `enabled` を含むことの実測）。拒否の判定は 400 / 401 だけ（5xx・到達不能は拒否と数えない）。
  🔴 **門は PR では走らない**（日次・develop への push・手動）。本 PR のマージ後の最初の実行が初回の実測になる。
- **統制表の更新**: 「無効化は即時に接続拒否（SC-12）」の現在の実現手段は従前どおり登録簿（`McpSubjectResolver`）であり、本段で IdP の `enabled` を重ねた
  （無効化した無人のクライアントは Keycloak からトークンを得られない）。写しの失敗は照合が知らせる。
- 残余: 有人のクライアントは IdP に無いので写さない（決定 3）。行の排他は無い（交差は検知だけ）。IdP への写しの失敗に自動の再試行は無い（照合が知らせ、運用者が送り直す）。

## ［2026-10-09 追記 / #1829・PR #1832 監査 🔴1］是正: Keycloak のクライアントの `PUT` は部分更新として安全ではない

直前の追記（#1829）の「Keycloak のクライアントの `PUT` は null・欠けた項目を変えない」は**誤りだった**。

- 根拠（監査が Keycloak 24.0.0 と 24.0.5 のソースで確認）: `ClientResource.updateClientFromRep`（L805〜816）は、`rep.isServiceAccountsEnabled()` が
  TRUE でなければ（**null を含む**）既存のサービスアカウントの利用者を `removeUser` する。`updateAuthorizationSettings` も TRUE でなければ authorization を
  無効にする。null の項目を飛ばす `RepresentationToModel.updateClient` は、これらの分岐より**後に**走る。
- 起きていたこと: `{"enabled": …}` だけの `PUT` は、無効化で SA の利用者を ABAC 属性ごと消す。再有効化でも戻らず、最初の `client_credentials` で Keycloak が
  空の SA の利用者を作り直し、属性なしのトークンが出る。成功を返しながら属性の正（ADR-0123 決定 1）を壊す。
- 是正: 本文を **`enabled` ＋ `serviceAccountsEnabled`・`authorizationServicesEnabled` の現在値**（同じ要求で読んだ `GET` の値）にした。表現を丸ごと
  送り返さない理由（secret）は変わらない。補償と取り消しも同じ書き込みを通る。偽の Keycloak を実物の分岐どおりに作り直し、単体試験（C-51）と
  稼働の門（M8。トークンを要求する前に SA の利用者が同じ ID で残り属性が不変であることを見る）で固定した。
- 教訓: Keycloak の管理 API の `PUT` は、利用者（IADR-0329 の B: 全置換）だけでなくクライアントでも**部分本文を安全と仮定しない**。リポジトリ全体の
  `PUT /clients/` と `PUT /users/` を走査し、ほかの書き込みはすべて GET した全表現の read-modify-write であることを確かめた（作業仕様書 20261009_1829 の監査対応の節）。

## 関連

- 作業仕様書: [20261008_1786_sc12-keycloak-provisioning](../specs/20261008_1786_sc12-keycloak-provisioning.md)
- 計画: ADR-0123・ADR-0062・ADR-0088

## ［2026-10-09 追記 / #1829・PR #1832 再監査 🟡1］`serviceAccountsEnabled` が読めない表現には書かない

上の追記（🔴1）は現在値を同送すると定めたが、現在値が読めない（GET の表現が項目を欠く）ときに false と推して送ると、同じ事故（SA の利用者の消失）になる。**この場合は何も書かずに `Failed`（502）にする**（fail-closed）。Keycloak 24 の GET は primitive で必ず出すため通常は起きないが、版の変更・応答の加工で欠けたときに黙って壊さないための防御である。`authorizationServicesEnabled` は資源サーバが無いと表現に出ない（＝無効）ので、欠落を false と読む（送っても無効のままで何も消えない）。

## ［2026-10-09 追記 / #1844］決定 3 の既知の逸脱を解く: 有人は公開クライアントとして作り、MCP サーバーの `/mcp` で audience を検証する（計画 ADR-0134 決定 1・フォローアップ 1〜3）

計画は環流（planning#751）に答え、**ADR-0134 決定 1** で有人の MCP クライアントも SC-12 で Keycloak に作ると定めた（公開クライアント・PKCE S256 必須・
リダイレクト URI の完全一致〔`https` かループバック `http://127.0.0.1` / `http://[::1]`〕・ワイルドカード不可・Web オリジン空・直接付与／暗黙／サービスアカウントは無効・
audience を MCP サーバーに限り MCP サーバーが検証する・DCR は開かない）。細目（ループバックの port の扱い・audience の付け方と検証の置き場所・DCR が閉じていることの確かめ方）は
実装の IADR へ委ねられた。**決定 3 の「有人は IdP へ書かない（既知の逸脱）」は本追記で解消する**（本文は凍結のため書き換えない）。新しい IADR は起こさない
（本件は決定 3 の逸脱を解く同じ入口の拡張であり、決定 1〜6 の骨組み〔口・資格情報・順序と補償・入口の印・照合〕をそのまま使う）。作業仕様書は 20261009_1844。

### 決めたこと

1. **口**: `IServiceAccountProvisioner.CreatePublicClientAsync(clientId, displayName, redirectUris)` を足した（型の名前は据え置く）。作成の骨組み
   （`POST /clients` → 409 なら何も書かずに `AlreadyExists` → 途中の失敗は作ったものを消して 502 → 成否不明の失敗は印つきなら消す）は無人と**同じ 1 つ**
   （`CreateClientAsync`。無人の `CreateWithAttributesAsync` をその上に載せ直した）。登録の順序・状態コード・補償（`IdpFirstWrite`）・入口の印も無人と同じ。
2. **有人のテンプレート**（`PublicClientTemplate`）: `publicClient=true`・`standardFlowEnabled=true`・`implicitFlowEnabled`／`directAccessGrantsEnabled`／`serviceAccountsEnabled=false`・
   `consentRequired=false`・`fullScopeAllowed=false`・`redirectUris`＝入力そのもの・`webOrigins=[]`・`defaultClientScopes=["profile"]`（realm は既定のスコープを宣言しないので、
   明示しないと `preferred_username` が載らず `McpSubjectResolver` が利用者名を読めない）・`optionalClientScopes=[]`・属性 `pkce.code.challenge.method=S256`・
   `oauth2.device.authorization.grant.enabled=false`・`oidc.ciba.grant.enabled=false`・入口の印・`protocolMappers` に audience の写像。
   **作成の後に `GET /clients/{id}` で読み戻し**、公開・認可コード・3 つの流れの閉（未指定を閉と読まない）・PKCE S256・リダイレクト URI の集合・Web オリジン空・audience の写像・
   入口の印を確かめる。外れていれば消してから 502（Keycloak 側の方針で黙って変えられた形を登録簿へ写さない）。
3. **audience の付け方**: クライアントごとの `oidc-audience-mapper`（`included.custom.audience=mcp-server`・`access.token.claim=true`・`id.token.claim=false`・
   `introspection.token.claim=true`）。**無人のテンプレートにも同じ写像を足した**（`/mcp` が audience を検証するので、無人のトークンも `mcp-server` を持たなければ届かない）。
   realm のクライアントスコープにしない理由: 入口が作るクライアントだけに付け、画面を通らないクライアントのトークンが `/mcp` に届かない形を保つため。
   値は `McpAudienceAuthentication.Audience`（`mcp-server`）の 1 つで、検証側と写像の側が同じ定数を使う（構成にしない＝両側の食い違いを作れない）。
4. **audience の検証の置き場所**: **`/mcp` だけ**に、既定のスキームと別の JWT スキーム `McpAudience` とポリシー（`AddAuthenticationSchemes(McpAudience)`・認証済み）を掛けた。
   🔴 **既定のスキーム（`ValidateAudience=false`）は変えない** —— 管理 API（`/mcp-clients`）は BFF が利用者のトークン（aud は MCP サーバーではない）を中継して呼ぶ。
   スキームの発行元・メタデータ・名前とロールのクレームは既定のスキームと**同じ 1 つの設定**（`AuthExtensions.PlatformJwtBearer`。今回 `AddPlatformAuth` から切り出した。
   振る舞いは不変・構成は登録の時点で読む）を当て、`ValidateAudience=true`・`ValidAudience=mcp-server` だけを足す。
   ⚠️ 初版は名前つきオプションの `Configure<IOptionsMonitor<JwtBearerOptions>>` で既定のスキームの値を写したが、同じオプション型への依存が循環し**ホストの起動が止まった**
   （試験がタイムアウトで実測）。設定の関数を共有する形へ改めた。全サービスの `ValidateAudience=false` の是正は #1846。
5. **登録の契約**: `RegisterMcpClientRequest.RedirectUris`（`string[]?`）。規則は `Domain/RedirectUriRules` の 1 つ（検証器が呼ぶ）: 有人は 1〜10 件・各 2048 文字以下・絶対 URI（`scheme://`。
   Unix の .NET は `/callback` を `file:///callback` と読むので `://` の有無も見る）・`*` 不可・フラグメント不可・利用者情報不可・前後の空白不可・重複不可。`https` は host を問わず、
   `http` は綴りが `http://127.0.0.1` / `http://[::1]` で始まり直後が `:` `/` `?` か終わりのものだけ（`Uri.IsLoopback` は `localhost` と `127.0.0.0/8` を真にするので使わない。
   `localhost` は RFC 8252 §8.3 に従い認めない）。**無人に渡すと（空配列でも）400**（受け取って黙って捨てない）。検査の順は `clientId` → `kind` → `redirectUris` → `egressTier`
   （種別が決まらなければ判定しない。[[IADR-0398]] の「宣言順が契約」）。リダイレクト URI は登録簿に持たない（正は IdP）。
6. **書き込み口が未構成なら有人も 503**（登録簿にも書かない）。従前の陽性対照「書き込み口が無くても有人の登録は通る」は、この IADR 決定 3 の逸脱そのものだったので改めた。
   ADR-0134 決定 3 の暫定手段は「有人は IdP に作らない」であり、作らずに登録簿へ書く経路を残すと逸脱が続く。
7. **無効化・再有効化**（決定 4a）: 有人の行も `SetEnabledAsync` を通す（印つきのクライアントだけを変える）。公開クライアントの表現の `serviceAccountsEnabled` は false で、
   その現在値をそのまま送るので何も消えない。本件より前の有人の行（IdP に無い）は `Absent` で登録簿だけ。
8. **照合**（決定 5）: 登録簿の**全行**を読む。有人の行は存在・入口の印（`not_managed`）・有効無効（`enabled_differs`）を比べ、属性は読まない。🔴 **有人の行が IdP に無いこと
   （`client_missing`）は数えない** —— 本件より前に登録簿だけへ書かれた有人の行は IdP へ載せる経路が無く（差し替えは無人だけ）、数えると警報が鳴り止まない。
   そのような行は対応するクライアントが IdP に無いのでトークンが出ず、接続できない（fail-closed）。全行を「登録済み」と数えるので、入口が作った有人のクライアントは `orphan` にならない。
9. **画面**: 有人のときだけリダイレクト URI の入力（1 行 1 件）を出し、`isAllowedRedirectUri`（後段と同じ事例で試験した写し）で送る前に検査する。最終の判定は後段。

### FU3 の実測（本 PR の時点の結論は Keycloak 24.0.5 のソースの読み。稼働での確かめは integration-stack の門 M9・M10）

配備の Keycloak は `quay.io/keycloak/keycloak:24.0`（digest 固定）である。タグ `24.0.5` のソースを読んだ:

| 事項 | 読んだ箇所 | 結論 | 門 |
| --- | --- | --- | --- |
| リダイレクト URI の照合 | `RedirectUtils.verifyRedirectUri`（L89〜160）・`matchesRedirects` | 登録値との文字列の完全一致（登録値が `*` で終わるときだけ前方一致。`*` 単独は全許可）。入力の `*` を拒むので、入口が作るクライアントは常に完全一致 | M9: path・host・https の port・`localhost` の違いは 400 |
| ループバックの port | 同 L121〜134・`Constants.INSTALLED_APP_URL`（`http://localhost`）/ `INSTALLED_APP_LOOPBACK`（`http://127.0.0.1`） | 一致しないとき、要求の URI が `http://localhost` か `http://127.0.0.1` で始まれば **port を落として**もう一度照合する。よって `http://127.0.0.1/cb` を（port なしで）登録すると**任意の port** を受ける（RFC 8252 §7.3 の形）。port つきで登録するとその port だけ。**`http://[::1]` にはこの扱いが無く port まで完全一致**。`localhost` は入口が登録を拒むので照合されない | M9: `127.0.0.1:49152` は進む・`127.0.0.1:49152/other` は 400・`[::1]` の登録 port は進み別 port は 400 |
| PKCE の強制 | クライアント属性 `pkce.code.challenge.method=S256`（`AuthorizationEndpointChecker.checkPKCEParams`） | PKCE なし・`plain` は `invalid_request` でリダイレクト URI へ返す（リダイレクト URI の検査が先） | M9: PKCE なし・plain は error-redirect、S256 はログイン画面 |
| DCR の既定の方針 | `RealmManager.importRealm` L626 → `setupClientRegistrations` → `DefaultClientRegistrationPolicies.addDefaultPolicies`（L56〜97）・`TrustedHostClientRegistrationPolicy.verifyHost`（L92〜122）・`ClientRegistrationAuth.requireCreate` | realm の宣言はクライアント登録ポリシーを持たないので、取り込みで既定が足される。匿名の `Trusted Hosts` は**信頼ホストが空**（`host-sending-registration-request-must-match=true`）で、匿名の DCR はどの host からも「Host not trusted.」で 403。Bearer の DCR は `manage-clients` / `create-client` が要る（＝管理 API と同じ権限で、開いていない）。初期アクセストークンは管理者が作らない限り無い | M10: 匿名（`openid-connect`・`default`）と偽の初期アクセストークンは 401 / 403 で件数が増えない・初期アクセストークン 0 個・匿名の Trusted Hosts は信頼ホストが空 |

- 🔴 **門は PR では走らない**（日次・develop への push・手動）。本 PR のマージ後の最初の実行が初回の実測になる。ソースの読みと稼働が食い違えば門が赤になり、本表を改める。
- audience は管理 API の `evaluate-scopes/generate-example-access-token`（人の利用者 `poc-user`）で測る（M9）。ブラウザでのログインとコードの交換は門に無い。

### 統制表の更新（ADR-0134 決定 3 の 1〜3 行）

| 統制 | 現在の実現手段 | 配備までの暫定手段 |
| --- | --- | --- |
| 有人のクライアントを SC-12 で Keycloak に作り、決定 1 の制約に従わせる | **ある（コード）。** 公開クライアントのテンプレートと読み戻し（上の 2）。検証器（上の 5）。単体・API 面・変異の試験。稼働は門 M9 | 書き込み口が未構成の配備では有人も 503（作らない） |
| トークンの audience を MCP サーバーに限る | **ある（`/mcp` のみ）。** 写像（上の 3）と `/mcp` の検証（上の 4）。他のサービスは audience を検証しない（#1846） | — |
| DCR を開かない | **Keycloak の既定のまま**（realm の宣言に登録ポリシーを持たない）。ソースの読みで閉と確認し、門 M10 が稼働で測る | — |

### 試験と変異

- 単体・API 面: `RegisterMcpClientValidatorTests`（C-59）・`KeycloakServiceAccountProvisionerTests`（C-61〜C-64）・`IdpProvisioningEndpointTests`（C-60）・`IdpReconciliationTests`（C-65）・
  `McpAudienceAuthenticationTests`（C-66。署名した JWT で aud の有無・発行元・トークンなし。器の既定の認証がどの要求も通す状態で測る）。McpServer の試験は 383 件すべて緑。
- 変異（すべて赤になることを確かめて戻した）: 有人のテンプレートの PKCE 属性を落とす（3 件）・機密にする（3 件）・無人の audience の写像を落とす（1 件）・読み戻しの PKCE 検査を落とす（1 件）・
  `/mcp` のポリシーのスキーム指名を外す（5 件）・`ValidateAudience=true` を外す（4 件）・`http` をループバック以外にも許す（6 件）・ワイルドカードの検査を外す（3 件）・
  照合で有人の `client_missing` を数える（1 件）・照合を無人の行だけ読む旧形へ戻す（2 件）・有人を IdP へ書かない旧形へ戻す（2 件）・有人の無効化を写さない旧形へ戻す（1 件）。
  画面: `http` を全部許す（5 件）・ワイルドカードとフラグメントを許す（4 件）・本文へ載せない（2 件）・無人にも載せる（1 件）・必須を外す（3 件）。

### 残余

1. **稼働での初回の実測**（M9・M10）は本 PR のマージ後の integration-stack の実行である（オーケストレーターが dispatch する）。
2. **ブラウザでの認可コードの交換と、そのトークンで `/mcp` を呼ぶ往復**は試験に無い（PKCE の強制とリダイレクト URI の照合は認可の要求の応答で、audience は例示のトークンで測る）。
3. **他のサービスは audience を検証しない**（#1846）。それまでは、MCP クライアントのトークン（aud=mcp-server）を他のサービスが受け得る（`fullScopeAllowed=false` で realm ロールは載らないので、
   ロールで守られた面には届かない）。
   ［2026-10-09 追記 / #1846］[IADR-0523](IADR-0523_platform-api-audience-validation.md) で解消した。全サービスの既定のスキームは `platform-api` を検証し、
   MCP クライアントのトークン（aud=mcp-server だけ）は 401 になる。`/mcp` のスキームは `mcp-server` だけを受け付ける（`ValidAudiences` も置き換える）。
4. 本件より前に登録簿だけへ書かれた有人の行は、IdP へ載せる経路が無い（差し替えは無人だけ）。トークンが出ないので接続はできない。使うには行を消して登録し直す必要があるが、
   登録簿の削除の API は無い（運用者が DB で消す）。照合はこの行を数えない。
5. 無人の secret の一度だけの表示・再発行（ADR-0134 決定 2・FU4）、管理操作の監査記録（FU5）、client secret rotation の実測（FU6）は別の issue。
6. 既存の無人のクライアント（本件より前に作ったもの）には audience の写像が無く、`/mcp` に届かない。配備では書き込み口の配線（#1817）が直前に入ったばかりで、
   稼働の無人のクライアントは門が作って消す使い捨てだけである。残っていれば Keycloak の管理画面で写像を足すか、行を消して登録し直す。


## ［2026-10-09 追記 / #1844・PR #1854 セキュリティ監査 🔴］ループバックのリダイレクト URI は port の明示を必須にする（CVE-2024-8883）

上の追記（#1844）の決定 5 は「ループバックの port は書いても書かなくてもよい」とし、FU3 の表は `http://127.0.0.1/cb` を port なしで登録すると任意の port を受けることを
RFC 8252 §7.3 の形として受け入れていた。**この受け入れは誤りだった。本追記で改める**（上の本文は凍結のため書き換えない）。

### 何が起きるか（監査が Keycloak 24.0.5 の稼働で実測）

- 配備の Keycloak は `quay.io/keycloak/keycloak:24.0`（`deploy/local/infra/keycloak.yaml`・`deploy/docker-compose.yml`）である。
- Keycloak 24 の `RedirectUtils` は、要求の URI が `http://127.0.0.1`（または `http://localhost`）で始まり完全一致しないとき、**最初の `:` から次の `/` までを落として**
  もう一度照合する。このとき**利用者情報（`user@`）を見ない**。
- よって port なしの `http://127.0.0.1/cb` を登録したクライアントは、`redirect_uri=http://127.0.0.1:49152@evil.example/cb` を一致と扱う。ブラウザはこの URI を
  「利用者情報 `127.0.0.1:49152`・host `evil.example`」と読むので、認可コードは evil.example へ送られる。公開クライアントなので、攻撃者は自分の PKCE 検証子で
  始めた要求のコードを引き換え、`aud=mcp-server` のアクセストークン（被害者の利用者名）を得る。PKCE はこれを止めない（要求を始めたのが攻撃者であるため）。
- これは **CVE-2024-8883**（Keycloak 25.0.6 で修正）である。
- port を明示した登録（`http://127.0.0.1:50000/cb`）では、`:50000@evil.example/cb`・`:1@evil.example/cb`・別の port（`:49152/cb`）のいずれも 400 になった（完全一致）。

### 決めたこと

1. **`http` のループバック（`127.0.0.1`・`[::1]`）は port の明示を必須にする。** port なし（`http://127.0.0.1/cb`・`http://127.0.0.1`・`http://127.0.0.1?x`）と、
   `:` の後に数字が無い・`0` の形は 400（「ループバックのリダイレクト URI '…' には port を明示してください」）。判定は綴りで行う（`Uri.IsDefaultPort` は `:80` を書いた形と
   書かない形を区別できない）。`RedirectUriRules`（後段・登録の検証器）と画面の写し（`isAllowedRedirectUri`。理由の識別子 `redirect-uri-loopback-port-required`）の両方に掛ける。
   `[::1]` は Keycloak 24 で port を落とす扱いの対象外だが、規則を 2 つに割らないため同じにする。既存の拒否（ワイルドカード・フラグメント・利用者情報・`localhost`・別の綴り）は残す。
2. **失う利便**: RFC 8252 §7.3 の「port なしで登録し、実行時に空いている任意の port で待ち受ける」は使えない。ネイティブアプリ・CLI は登録した固定の port で待ち受ける
   （その port が塞がっていればログインできない）。複数の port を使うなら、それぞれを登録する（上限 10 件）。
3. **門 M9** は port つき（`http://127.0.0.1:50000/cb`）で登録し、登録どおりの port で進むこと・別の port・path 違い・横取りの形（`127.0.0.1` と `[::1]` のそれぞれで
   `:<登録 port>@evil.example` と `:1@evil.example`）が 400 であること・port なしのループバックの登録が 400 で Keycloak に何も作らないことを測る。
   横取りの形を作る純関数（`loopbackHijackProbes`）と、横取りを許す応答（200 でログイン画面へ進む・evil.example へコードつきで戻す）が赤になることを自己試験に置いた。
4. **この制約を緩める条件**: Keycloak を **25.0.6 以上**へ上げ、門 M9 で port なしの登録に横取りの形が 400 になることを確かめてから。#1859 で追跡する。

### 既存のクライアント

- 本追記より前に port なしのループバックで登録された有人のクライアントは、配備の時点では門が作って消す使い捨てだけである（有人の入口は同じ PR で入る）。
  残っていれば、port を明示した URI で登録し直し、旧いクライアントを無効化する。照合はリダイレクト URI を比べないので警報は鳴らない。

### 試験

- `RegisterMcpClientValidatorTests`: port なしのループバック 9 形（`127.0.0.1`・`[::1]` × path あり・`/`・なし・クエリ・`:` だけ）が「port を明示」を名指しして 400。
  port つき（`:53123`・`:80`・path なし・クエリつき）は通る。`127.0.0.1:49152@evil.example/cb`・`[::1]:49152@evil.example/cb` は 400。
  修正を外すと port なしの 9 件が赤になることを確かめた。
- `mcpClientVocabulary.test.ts`: 同じ 9 形が `redirect-uri-loopback-port-required` だけを返す（`redirect-uri-invalid` と分ける）。修正を外すと 9 件が赤。
- `check-mcp-client-provisioning.js --self-test`: 横取りの形の生成と判定（上の 3）。

## ［2026-10-09 追記 / #1845］無人の client secret を登録・再発行の応答で一度だけ返し、SC-12 の管理操作を監査記録に残す（計画 ADR-0134 決定 2・フォローアップ 4〜6）

計画は環流（planning#751）に答え、**ADR-0134 決定 2** で無人のクライアントの secret を「登録と再発行の応答で一度だけ表示する」と定めた（統制 1〜6）。
**決定 3 の「client secret は応答で返さない」（本文は凍結）と §残余 4・#1844 追記の残余 5 は、本追記で解消する。** 新しい IADR は起こさない
（同じ入口の口に 2 つの操作を足すだけで、骨組み〔口・入口の印・`IdpFirstWrite` の補償〕はそのまま使う）。作業仕様書は 20261009_1845。

### 決めたこと

1. **口**: `IServiceAccountProvisioner` に `ReadClientSecretAsync`（`GET clients/{id}/client-secret`）と `RegenerateClientSecretAsync`（`POST` 同パス＝regenerate）を足した。
   どちらも**入口の印つき・公開でない・SA つきのクライアントだけ**に掛け、それ以外は何も書かずに種類（`Absent` / `NotManaged` / `NotConfidential`）を返す
   （プラットフォーム自身の機密クライアントの secret を SC-12 から読ませない・回させない。決定 4 の規則と同じ）。要求の取り消しは伝えない（書き込みの口の規則）。
2. **値の型** `ClientSecret` は `ToString()` が値を出さない（record の既定の `ToString` から漏らさない）。値を取り出すのは応答の本文を組み立てる 2 か所だけ（`Reveal()`）。
   応答の DTO（`McpClientRegistrationView`・`McpClientSecretView`）も `ToString` を差し替えた。
3. **登録**: 無人は `IdpFirstWrite` の「登録簿へ書く」段で、**登録簿へ書く前に** secret を読む。読めなければ失敗の結果（502）を返し、`IdpFirstWrite` が作ったクライアントを消す
   （既存の補償に乗せた。登録簿にも書かない）。201 の本文は `McpClientView` の項目 ＋ `clientSecret`（有人は null）。登録簿・一覧には持たない。
4. **再発行**: `POST /mcp-clients/{clientId}/reissue-secret`（BFF `…/reissue-secret`）。登録簿に無い 404・有人 400（IdP へ問わない）・IdP に無い／印なし／公開 400・未構成 503・失敗 502・
   成功 200。**登録簿は書かない**（値を保存しない。`updatedAt` も動かさない）。無効化された行も再発行できる（漏えいに気づいたら無効化してから回せる）。
5. **`Cache-Control: no-store`**: 登録の 201 と再発行の 200 に、後段と BFF の両方で付ける（BFF の中継は後段の見出しを運ばない）。
6. **監査**: 器は既存の `IAuditLogger`（`AuditLogger`。`Audit=true` の構造化ログ → OTel → ログ基盤。BFF の SC-22 と同じ）。McpServer に `TryAddSingleton` で登録し、
   `Features/McpClients/McpClientAudit` の 1 か所から記録する。action は `mcp-client.register` / `.replace-attributes` / `.disable` / `.enable` / `.secret.issue` / `.secret.reissue`、
   subject は利用者名（`preferred_username`）、outcome は状態コードから（2xx `granted`・400 `denied`・404 `not-found`・503 `unavailable`・他 `failed`。例外は `failed` を残して投げ直す）、
   detail は `client=`・`kind=`・`attributes=`（ABAC の属性値。秘密ではない）・`status=`。**secret の値を受け取る引数を持たない。**
   - 発行は無人の登録が 201 のときだけ、登録の行と別に残す（「誰が・いつ・どのクライアントの secret を発行したか」を action で引ける）。
   - 🔴 **削除は記録しない** —— SC-12 に削除の操作が無い（#1844 追記の残余 4）。issue #1845 の受け入れ基準は削除を挙げるが、操作が無いので記録の対象が無い。
     削除の API を足すときは同じ器で `mcp-client.delete` を残すこと。
   - 認可で弾かれた要求（非管理者の 403）は端点に届かないので記録しない。
7. **画面**: 値は応答から受け取り、画面のローカル状態（`useIssuedClientSecret`。SC-20 の `useIssuedToken` と同じ作法）にだけ持つ。表示・コピー・閉じる、
   「表示できるのは今回だけ・閉じると再表示できない」。閉じたとき・次の操作を始めたときに捨てる。再発行は無人の行だけに出し、即時失効の確認を挟む。
   - **値の写しは変更（mutation）の結果にも在る。** 表示を捨てるたびに登録・再発行の変更を `reset()` し（送信中は捨てない＝応答の表示が失われるため）、
     両方の変更に `gcTime: 0` を置く（画面を離れたら変更キャッシュから即座に消える。SC-22 の書き込みと同じ作法）。
   - **送信中は登録・再発行のボタンを押させない**（二重送信を止める。再発行が 2 本飛ぶと、先に表示した secret が後の再発行で黙って失効する）。
   - 後段の Keycloak 版の口は、値を載せた応答（`HttpResponseMessage`）を読み終えたら `using` で破棄する。

### FU6 の実測（Keycloak 24.0.5 のソースの読み。稼働での確かめは integration-stack の門 M11）

| 事項 | 読んだ箇所 | 結論 |
| --- | --- | --- |
| client secret rotation の有無 | `common/.../Profile.java` L83 `CLIENT_SECRET_ROTATION(…, Type.PREVIEW)`。配備は `--features` を宣言しない | **preview の機能で既定は無効＝配備では働かない。** 期限（有効期間）も、再発行の後に旧 secret を残す猶予（rotated secret）も無い |
| regenerate の旧 secret | `ClientResource.regenerateSecret`（rotation が無効なら `removeClientSecretRotationInfo`） | 旧 secret は**その時点で**使えなくなる（決定 2 の 5 と一致）。門 M11 が「再発行の直後に旧 secret でトークンが出ない」を測る |
| 🔴 管理イベントの詳細 | `regenerateSecret` は `adminEvent…representation(rep)`（値つきの `CredentialRepresentation`）。`AdminEventBuilder.representation` は `UserRepresentation` だけを `StripSecretsUtils` に通す。realm は `adminEventsDetailsEnabled=true`。`JBossLoggingEventListenerProvider.logAdminEvent` は表現をログへ書かない | **再発行の新しい値（管理コンソールでも SC-12 でも）は Keycloak の管理イベントの保存先（DB）に平文で残る。** 作成（`ClientsResource.createClient`）の表現は入力そのもので、テンプレートは `secret` を送らないので値は残らない。`PUT /clients/{id}` で値を書く代替も表現を剥がさないので逃げ道にならない。門 M11 は件数を観測として出す（判定しない） |

- **期限は置かない**（決定 2 のまま）。再発行は即時失効で、漏えいに気づいたときの手段として足りる。**期限を置くべきとは判断しない**（環流の対象ではない）。
- 🔴 **管理イベントに値が残ることは計画へ環流する**（決定 2 の 2「応答以外に値を出さない」の射程はプラットフォームのアプリケーションのログ・監査ログで、Keycloak の管理イベントは
  その外だが、決定 3 の表が「推論。実装の IADR で実測する」とした論点の答えである。`adminEventsDetailsEnabled` を切る・管理イベントの閲覧権限〔view-events〕を絞る・受け入れる、の選択は計画の判断）。
  計画へ **planning#771** で起票済み。`mcp-client-admin` は `view-events` を持たない（`manage-clients` / `manage-users` だけ）。

### 統制表の更新（ADR-0134 決定 3 の 4〜8 行）

| 統制 | 現在の実現手段 | 配備までの暫定手段 |
| --- | --- | --- |
| secret の表示は登録・再発行の応答の 1 回だけ。値を保存しない | **ある（コード）。** 上の 3・4。登録簿・一覧に持たない。単体・API 面の試験。稼働は門 M11 | — |
| 応答以外に値を出さない | **ある（プラットフォーム）。** 値の型・DTO の `ToString`・監査が値を受けない。ホストの全ログで不在を試験（`McpClientSecretLeakTests`）。🔴 **Keycloak の管理イベントの詳細には再発行の値が残る**（上の表。計画へ環流＝planning#771） | — |
| 監査ログに発行・再発行を残す | **ある。** `mcp-client.secret.issue` / `.secret.reissue`（値なし）。他の管理操作も同じ器 | — |
| 表示・再発行はシステム管理者に限る | **ある。** 管理 API と BFF の `AdminOnly`（再発行の端点もグループの既定に乗る）。試験は 403 | — |
| 再発行で旧 secret を失効させる | **ある。** Keycloak の regenerate（rotation は配備で無効＝猶予なし）。稼働は門 M11 | — |

### 試験と変異

- 単体・API 面: `KeycloakServiceAccountProvisionerTests`（C-70〜C-74）・`McpClientSecretEndpointTests`（C-75〜C-81・C-87）・`McpClientAuditTests`（C-82〜C-85）・
  `McpClientSecretLeakTests`（C-86。ホストの全ロガーを捕まえ、整形済みの本文・構造化の値・例外に値が無い。陽性対照つき）・`BffMcpClientEndpointTests`（中継と `no-store`）・
  画面の `McpClientManagementPage.test.tsx`（T-35〜T-37）。門 `check-mcp-client-provisioning.js`（M11 と自己試験 3 件）。
- 変異（すべて赤になることを確かめて戻した）: 登録の応答から secret を落とす（3 件）・発行の監査を落とす（1 件）・再発行で値をログへ出す（1 件）・
  Keycloak 版 / プロセス内の口で入口の印の確かめを外す（各 1 件）・監査の 400 を `failed` に潰す（2 件）・BFF の `no-store` を全経路へ／外す（1 件・2 件）・
  画面: 登録で表示しない・確認を挟まない・次の操作で捨てない・有人にも出す（各 1 件）・値を描かない（2 件）。

### 残余

1. 🔴 **再発行の値が Keycloak の管理イベントの詳細に残る**（上の FU6 の表）。計画の判断を待つ（planning#771）。それまでは管理イベントを読める主体（realm の管理者）が値を読める。
2. 門 M11 は PR では走らない（日次・develop への push・手動）。本 PR のマージ後の最初の実行が初回の実測になる。
3. 表示した値の運用者への引き渡しはシステムの外（決定 2 の 6。受け入れたリスク）。
4. 登録簿の削除の操作が無いので、削除の監査も無い（上の 6）。
5. `private_key_jwt`（共有秘密を持たない形）は計画が将来の拡張とした。
## ［2026-10-09 追記 / #1859］Keycloak を 26.7.4 へ上げた。ループバックの port 必須は残し、外すかの判断材料を門 M9 で測る

上の追記（PR #1854）の決定 4「この制約を緩める条件: Keycloak を 25.0.6 以上へ上げ、門 M9 で port なしの登録に横取りの形が 400 になることを確かめてから」の
前半を満たした。版の更新そのものの判断は [IADR-0524](./IADR-0524_keycloak-26-upgrade.md)（作業仕様書 20261009_1859）に置く。**本追記は規則を変えない。**

### 証拠（手元の docker。24.0.5 は配備していた digest、26.7.4 は採った digest）

port なしの `http://127.0.0.1/cb`・`http://[::1]/cb` を登録した公開クライアント（PKCE S256・認可コードだけ）に、認可の要求を送った（状態コードと Location で分類）。

| redirect_uri | 24.0.5 | 26.7.4 |
| --- | --- | --- |
| `http://127.0.0.1/cb`（登録どおり） | ログイン画面 | ログイン画面 |
| `http://127.0.0.1:49152/cb`・`:1/cb`（任意の port） | ログイン画面 | ログイン画面 |
| `http://127.0.0.1:49152@evil.example/cb`・`:1@`・`:@`・`:49152:1@`（横取り） | 🔴 **4 形ともログイン画面**（コードは evil.example へ） | **4 形とも 400** |
| `http://127.0.0.1:49152%40evil.example/cb`・`http://127.0.0.1:49152.evil.example/cb` | 🔴 ログイン画面 | 400 |
| `http://[::1]:49152/cb`（任意の port） | 400（`[::1]` は port を落とさない） | **ログイン画面**（26 は `[::1]` も RFC 8252 §7.3 の扱い） |
| `http://[::1]:49152@evil.example/cb` ほか 3 形 | 400 | 400 |
| `http://127.0.0.1:49152/other`・`http://localhost:49152/cb` | 400 | 400 |

- 門 M9 にこの対（陽性対照 4 段＋横取り 8 段＋ path 違い 2 段＋ `localhost` 1 段）を足した。同じ形を手元で当てると、26.7.4 は 15 段すべて期待どおり、24.0.5 は 5 段が赤（横取りの 4 形と `[::1]` の任意の port）。
  **門は版を戻すと赤になる。** 稼働での初回の確かめは本 PR のマージ後の integration-stack である。
- port つきの登録の否定（既存の M9）も 26.7.4 で同じく 400 だった。SC-12 の入口は port なしを従来どおり 400 で拒み、Keycloak に何も作らない（既存の M9 が測り続ける）。

### 推奨（判断は製品へ）

**port 必須を外し、RFC 8252 §7.3 の「port なしで登録し、実行時に空いている任意の port で戻す」を許すことを推奨する。** 理由:

1. 横取りの形を塞ぐのは本来 IdP の照合の役目で、26.7.4 はそれを満たす（上の表）。入口の port 必須は、版を上げるまでの暫定として置いたものである（PR #1854 の追記 決定 4）。
2. 失っている利便が実害である。有人の MCP クライアント（ネイティブアプリ・CLI）は起動のたびに変わる port で待ち受けるのが普通で、固定の port は塞がっていればログインできない。複数の port を登録する回避は上限 10 件で尽きる。
3. 規則を外した後の防御は「版の固定」と「門」で持てる。版は digest で固定され（`check-image-digests`）、黙って下がらない。門 M9 の本追記の対が日次で走り、版を戻す・照合が退行すれば赤になる。

外すときの条件と形（別 PR）:

- 本 PR がマージされ、integration-stack の M9（本追記の対を含む）が 26.7.4 で緑であること。
- 外すのは「ループバックは port の明示が必須」の 1 点だけ。**利用者情報・ワイルドカード・フラグメント・`localhost`・別の綴りの拒否は残す**（IdP に任せず入口でも止める。`user@` の拒否はまさに CVE の形を入口で止めている）。
- 門 M9 の本追記の対は外した後も残す（規則を外した後は、これが横取りの形を止めている唯一の機械の確かめになる）。外す PR では M9 の「port なしの登録は 400」を「port なしの登録は 201 で、横取りの形は 400」へ改める。
- 入口（`RedirectUriRules`）・画面の写し（`isAllowedRedirectUri`）・試験・通信仕様書（`docs/api/FR-16_mcp-server.md`・`docs/api/openapi.yaml` と生成物）を同時に改める。

外さない選択の理由になり得るもの: 防御が 2 段から 1 段になる（入口の規則が IdP の不具合を覆わなくなる）。IdP の照合の退行は門が日次で見つけるが、見つけるまでの窓（最大 1 日）は残る。

### #1845 追記の FU6 の残余 1（再発行の値が管理イベントに残る）への影響

同じ手元の対で、無人のクライアントの secret の再生成（`POST clients/{id}/client-secret`）を測った。realm は `adminEventsDetailsEnabled=true`。

| 事項 | 24.0.5 | 26.7.4 |
| --- | --- | --- |
| `GET clients/{id}/client-secret` が出す管理イベント | 0 件 | 0 件 |
| 再生成の管理イベント（ACTION）のうち、表現に新しい値を含むもの | 🔴 1 件 | **0 件** |
| 再生成の直後の旧 secret の client_credentials | 401 | 401 |

**26.7.4 では、再発行の値は認可サーバーの管理イベントの保存先に残らない。** #1845 追記の残余 1 は版の更新で解消する見込みである（稼働での確かめは門 M11 の観測の件数）。
計画への問い（扱い）はこの結果を添えて行う。旧 secret に猶予が無いこと（FU6）は 26.7.4 でも変わらない。

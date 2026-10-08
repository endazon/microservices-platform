---
title: IADR-0516 SC-12 の無人の登録・差し替えは、検証の後に Keycloak へ機密クライアントとサービスアカウントの属性を書いてから登録簿へ書く。管理用は別の機密クライアント（manage-clients・manage-users）、失敗は補償で戻し、食い違いは定期の照合で知らせる
type: impl-adr
status: Accepted
related_ids: [FR-16, FR-09, UC-09, SC-12, ADR-0123, ADR-0062, ADR-0088, ADR-0024, ADR-0034, IADR-0297, IADR-0301, IADR-0329, IADR-0366, IADR-0385, IADR-0413, IADR-0479, IADR-0481, IADR-0286]
author: claude
created: 2026-10-08
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md 決定 1〜4・§結果「悪い影響」・フォローアップ 1〜3
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1・3
related_specs:
  - ../specs/20261008_1786_sc12-keycloak-provisioning.md
  - ../specs/20261009_1817_sc12-provisioning-wiring.md
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

## 関連

- 作業仕様書: [20261008_1786_sc12-keycloak-provisioning](../specs/20261008_1786_sc12-keycloak-provisioning.md)
- 計画: ADR-0123・ADR-0062・ADR-0088

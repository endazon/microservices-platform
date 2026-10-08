---
title: IADR-0515 SC-12 の無人の登録・差し替えは、検証の後に Keycloak へ機密クライアントとサービスアカウントの属性を書いてから登録簿へ書く。管理用は別の機密クライアント（manage-clients・manage-users）、失敗は補償で戻し、食い違いは定期の照合で知らせる
type: impl-adr
status: Accepted
related_ids: [FR-16, FR-09, UC-09, SC-12, ADR-0123, ADR-0062, ADR-0088, ADR-0024, ADR-0034, IADR-0297, IADR-0301, IADR-0329, IADR-0366, IADR-0385, IADR-0413, IADR-0479, IADR-0481, IADR-0286]
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md 決定 1〜4・§結果「悪い影響」・フォローアップ 1〜3
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1・3
related_specs:
  - ../specs/20261008_1786_sc12-keycloak-provisioning.md
---

# IADR-0515: SC-12 を IdP への入口にする —— Keycloak への書き込みの口・管理用の資格情報・テンプレート・順序と補償・食い違いの検知（#1786）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-08
- 決定者: claude（#1786。計画 ADR-0123 §結果「悪い影響」が、書き込みの口・管理用の資格情報・種別ごとのテンプレートを実装の IADR に委ねた）

> 🔴 **番号について**: 起草時の `origin/develop`（`5748d5d9`）の最大は IADR-0514 である。依頼は「並走中の PR（#1781 の切替リハーサル）との衝突を避けて 0516 を採る」だったが、本リポジトリの採番規約は欠番を作らない（`check-adr-numbering.js` の `missing-number` が落ちる）。並走中の PR は IADR を含まない（ファイル一覧で確認した）ので、**0515** を採った。後からマージする側が衝突したら、規約どおり後発が改番する。

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

### 決定 3 — テンプレートは無人だけを定める。有人は IdP へ書かない（計画へ問う）

- **無人（サービスアカウント）**: `publicClient=false`・`clientAuthenticatorType=client-secret`・`serviceAccountsEnabled=true`・**人の流れは全部閉じる**（`standardFlowEnabled` / `implicitFlowEnabled` / `directAccessGrantsEnabled` = false・`redirectUris` / `webOrigins` は空）。クライアント属性 `msp.mcp-client.managed-by=mcp-server` を付ける（照合と運用者の識別のため）。**クライアントのスコープは指定しない**（realm の既定）—— 判定に使う属性はトークンからではなく認可サービスが引き直すので（ADR-0088 決定 1）、トークンの中身は判定を変えない。
- **属性は丸ごと置き換える。** サービスアカウントの利用者属性を書くのはこの入口だけである（ADR-0123 決定 2）。集合値キー（`tags` / `projects`）は多値、単一値キーは 1 要素の配列で書く（IADR-0385 と同じ符号化）。
- **有人は IdP へ書かない（登録簿だけ。従来どおり）。** 有人のクライアント（認可コード + PKCE）はリダイレクト先などの入力が要るが、登録の契約にその入力が無く、計画も有人のテンプレートを定めていない。**計画への問い Q1**（作業仕様書）。
- **client secret は応答で返さない。** Keycloak が生成し、管理者は Keycloak の管理画面で取得する（属性を直接割り当てるのではないので ADR-0123 決定 2 の禁止には当たらない）。**計画への問い Q2**（受け渡しの経路）。

### 決定 4 — 順序は「検証 → 登録簿の重複 → IdP → 登録簿」。失敗は補償で書く前へ戻す

```
登録:   入力の検証 → 部分集合・個人資料の判定 → 登録簿の重複 → IdP（作成 → SA の利用者 → 名指しの照会 → 属性の書き込み → 読み戻し）→ 登録簿
差し替え: 部分集合・個人資料の判定 → IdP（無ければ作成。在れば元の属性を読む → 書き込み → 読み戻し）→ 登録簿
```

- **IdP を先に書く。** 登録簿は IdP へ書いた値の写しである（決定 2）。登録簿を先に書くと、IdP への書き込みが失敗した間、判定に効かない値が「割り当て済み」として画面に出る。
- **IdP に同じ clientId のクライアントが既にあれば、何も書かずに 400 で拒む**（Keycloak の 409）。入口を通らずに作られたクライアント（プラットフォーム自身の機密クライアントを含む）へ属性を書くと、検証の掛からない主体ができる。補償でも消さない（自分が作っていない）。
- **IdP の途中の失敗**（SA の利用者が無い・照会が合わない・書けない・読み戻せない）は、作ったクライアントを消してから 502 で返す（差し替えは元の属性を書き戻す）。**登録簿には書かない。**
- **登録簿への書き込みの失敗**は、IdP への書き込みを取り消してから元の例外を投げる（`IdpFirstWrite`）。取り消しも失敗したら Error ログを残し、元の失敗を投げる（補償の失敗で理由を上書きしない）。残った食い違いは決定 5 が拾う。
- **差し替えで IdP にクライアントが無い行**（本入口ができる前の登録簿の行）は、その場で作ってから書く。登録簿の重複検査が再登録を止めるので、これが旧い行を IdP へ載せる唯一の経路である。
- 状態の写し方: 503（口が無い）／502（IdP へ書けなかった）／400（検証の外れ・IdP に既にある）。境界層は状態コードを作り替えない（ただし境界層自身の不達も 502 であり、画面からは区別できない。残余）。

### 決定 5 — 食い違いは定期の照合で検知し、計器と警報で知らせる（本 PR では実装しない）

- **形は IADR-0481（所有者の読み取りのポリシーの検査）と同じ「定期の検査 ＋ 計器 ＋ 警報」とする。** McpServer の常駐処理が周期ごとに、登録簿の無人の行ごとに IdP のサービスアカウントの属性を読み、決定 3 の符号化で突き合わせる。
  - 計器: 直近の照合で食い違った行数のゲージ（照合に失敗したとき・未照合のときは系列を出さない）と、照合の結末のカウンタ（`match` / `drift` / `failed`）。
  - 警報: 「食い違いが 1 件以上」と「系列が無い」の 2 本。置き場は既存の警報の写し 4 か所（`deploy/prometheus/alerts.yml` ほか）と同じ。
  - 食い違いの種類: IdP にクライアントが無い／属性が違う／入口の印（`managed-by`）が無い。
- **書き込み時だけの検知 (b) は採らない。** Keycloak を直接操作した変更（ADR-0123 決定 2 が禁じた操作）は、次の書き込みまで見えない。
- 🔴 **本 PR はこの照合を実装しない**（issue の受け入れ基準 3 は未達）。後続の段で入れる（作業仕様書 §残り）。

### 決定 6 — フォローアップ 3 は、書き込みのたびに「認可サービスと同じ照会」で確かめる

- 書き込みの口は、属性を書く前に **`GET /users?username=service-account-<client>&exact=true`**（認可サービスの `FindByUsernameAsync` と同じ照会）を引き、**`GET /clients/{id}/service-account-user` と同じ利用者が 1 人だけ**返ることを確かめる。返らなければ書かずに失敗する（決定 4 の補償）。**判定に使われない属性は書かない。**
- 利用者名の組み立ては `ToolUserContext.ServiceAccountUserName` の 1 か所に寄せた（実行時に下流へ運ぶ `user_id` と、属性を書く先の利用者名が同じ綴りであることを構造で保つ。Keycloak はサービスアカウントの利用者名を小文字で持つ）。
- 認可サービス側は、名指しの照会がサービスアカウントの利用者を落とさないことを単体テストで固定した（全件の列挙は機械の主体を除くが〔#1609〕、名指しの照会はそれに倣わない）。
- 🔴 **Keycloak がこの照会でサービスアカウントの利用者を返すこと自体は、稼働の Keycloak でしか確かめられない**（残余）。決定 6 の確かめは、返らない場合に「書いたつもりで判定に効かない」状態を作らないための安全側の作りである。

## 統制と現在の実現手段

| 統制 | 現在の実現手段 | 配備までの暫定手段 |
| --- | --- | --- |
| 無人の属性は検証の後に IdP へ書く（ADR-0123 決定 2・3） | **ある（コード）。** 検証 → IdP → 登録簿（決定 4）。単体・API 面の試験で固定 | 🔴 **配備では未宣言**（`McpClientProvisioning:Provider`）。無人の登録・差し替えは 503 で拒み、登録簿にも書かない（ADR-0123 決定 4 の暫定と同じ側） |
| 入口を通らない主体へ属性を書かない | **ある。** IdP に既にあれば 400・補償は自分が作ったものだけ消す | — |
| 登録簿と IdP の食い違いを知らせる（決定 2） | 🔴 **無い**（決定 5 は未実装） | 書き込み時の補償と Error ログだけ。照合は後続の段 |
| 認可サービスの照会がサービスアカウントを返す（フォローアップ 3） | **書き込みのたびに確かめる**（決定 6）。認可サービス側は単体テスト | 🔴 稼働の Keycloak での実測は無い |

## 結果

- 良い: 無人の属性の正（IdP）と検証の掛かる場所（入口）が一致する。登録簿は IdP へ書いた値の写しになる。判定に使われない属性を書かない。
- 悪い（受容）: `mcp-client-admin` は `manage-clients` を持つ（realm の全クライアントを変えられる）。配備の資格情報が入るまで、無人の登録・差し替えは 503 になる（従来は登録簿へ書けていた）。
- 悪い（受容）: 境界層の不達（502）と IdP への書き込みの失敗（502）は画面から区別できない。

## 残余

1. **配備の配線**: realm（`deploy/keycloak/microservices-platform-realm.json`）へ `mcp-client-admin`（manage-clients・manage-users）を足す。secret の供給（ExternalSecret `mcp-client-admin-oidc`・Vault の初期投入・`scripts/k8s-local-up.sh` の手動経路・SC-22 の秘密の項目）。helm values の `McpClientProvisioning__Provider` と `ClientSecret` を有効にする（本 PR はコメントで置いた）。compose の配線。
2. **稼働の Keycloak での実測**: 作成・照会（フォローアップ 3）・属性の書き込みと読み戻し・補償の削除。統合試験（Keycloak の Testcontainers）は本リポジトリに無い。
3. **食い違いの検知**（決定 5）の実装と警報。
4. 有人のテンプレート（Q1）・client secret の受け渡し（Q2）・無効化を IdP のクライアントへ写すか（Q3）は計画の回答を待つ。
5. `manage-clients` を入口が作ったクライアントだけへ狭める（fine-grained admin permissions）。

## 関連

- 作業仕様書: [20261008_1786_sc12-keycloak-provisioning](../specs/20261008_1786_sc12-keycloak-provisioning.md)
- 計画: ADR-0123・ADR-0062・ADR-0088

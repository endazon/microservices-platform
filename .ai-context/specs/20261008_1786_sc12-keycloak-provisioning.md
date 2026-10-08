---
title: 作業仕様書 — SC-12 の登録で Keycloak のクライアントとサービスアカウントの属性を作り、部分集合の判定を IdP へ書く前に掛ける（段 1。#1786）
type: spec
status: done
related_ids: [FR-16, FR-09, UC-09, SC-12, ADR-0123, ADR-0062, ADR-0088, ADR-0024, ADR-0034, IADR-0515, IADR-0297, IADR-0329, IADR-0366, IADR-0413, IADR-0479]
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md 決定 1〜4・フォローアップ 1〜3
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md 決定 2・3
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1・3
issue: "#1786"
---

# 作業仕様書 — SC-12 を IdP への入口にする（段 1: 書く前の検証と IdP への書き込み。#1786）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。判断の記録は **IADR-0515** に置く。
> 計画は project-planning `origin/main` の隣接クローン（読み取り専用）で読んだ。基点は MSP `origin/develop` `5748d5d9`。
> 🔴 **稼働中のクラスタ・Keycloak には何も実行しない。** Keycloak は試験の中の偽物（状態を持つ `HttpMessageHandler`）だけで扱う。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0123 決定 1〜4・フォローアップ 1〜3**、**ADR-0062 決定 2・3**、**ADR-0088 決定 1・3**。
- 機能要求: **FR-16**（MCP サーバー）・**FR-09**。画面: **SC-12**。UC: **UC-09**。
- 起点 issue: **#1786**（#1772 の台帳から分離）。関連 #452（SC-12）・#445（MCP サーバー）。

## 計画が決めていること・決めていないこと

| 計画が決めている | 計画が決めていない（本件で決める＝IADR-0515） | 計画が決めていない（**問う**） |
| --- | --- | --- |
| 属性の正は IdP の `service-account-<client>`（ADR-0123 決定 1） | 書き込みの口の置き場所と形 | **Q1** 有人のクライアントのテンプレート（リダイレクト先等の入力が契約に無い） |
| SC-12 の登録・差し替えで機密クライアント（無人ならサービスアカウントつき）を作り、属性を利用者属性として書く。登録簿は写し（決定 2） | 管理用の資格情報と最小ロール | **Q2** 無人のクライアントの secret を誰がどの経路で受け取るか |
| 部分集合と `private-note` の禁止は IdP へ書く前に掛け、外れたら書かずに拒否（決定 3） | 無人のテンプレート | **Q3** SC-12 の無効化を IdP のクライアントの `enabled` へ写すか |
| 食い違いは検知して知らせる（決定 2） | 順序と補償・検知の仕組み | — |
| 入口ができるまで Keycloak で直接割り当てない（決定 2・4） | フォローアップ 3 の確かめ方 | — |

### 計画への問い（推測で埋めない）

- **Q1（有人）**: ADR-0123 決定 2 は「Keycloak のクライアント（無人ならサービスアカウントを持つ機密クライアント）を作り」と書き、有人のクライアントも作る読みを許す。有人（認可コード + PKCE）のクライアントには、少なくともリダイレクト先と公開／機密の別が要るが、SC-12 の入力（計画の画面・登録の契約）に無い。**本段は有人を IdP へ書かない**（従来どおり登録簿だけ）。有人も作るなら、入力とテンプレートの裁定が要る。
- **Q2（secret の受け渡し）**: 機密クライアントの secret は Keycloak が生成する。本段は応答で返さず、管理者が Keycloak の管理画面で取得する前提にした（属性の直接割当ではないので決定 2 の禁止には当たらない、と読んだ）。SC-12 で表示・再発行させるか、別の秘密の経路（SC-22）に載せるかは計画が決めていない。
- **Q3（無効化）**: SC-12 の無効化は登録簿の `Enabled` を切り替え、MCP サーバーが呼び出しごとに拒否する（UC-09）。IdP のクライアントも無効化してトークンの発行を止めるかは決めていない。本段は写さない。

## 現状（実測。`5748d5d9`）

| 事実 | 確かめ方 |
| --- | --- |
| 登録（`POST /mcp-clients`）・差し替え（`PUT /mcp-clients/{id}/attributes`）は `RejectUnassignableAsync`（個人資料の禁止 ＋ 部分集合）の後、**登録簿（`McpDbContext`）へ書くだけ** | `Features/McpClients/{RegisterClient,ReplaceAttributes}/Endpoint.cs`・`McpClientEndpoints.cs` |
| McpServer に Keycloak Admin REST の呼び出しは無い（`admin/realms`・`realm-management`・`KeycloakAdmin` で 0 件） | `grep -rn 'admin/realms\|realm-management\|KeycloakAdmin' src/platform/backend/Services/McpServer` |
| Keycloak Admin REST の既存の呼び出しは認可サービスの `KeycloakIdentityAdminClient` だけ（named `HttpClient` ＋ client_credentials の手書きクライアント。Refit ではない）。資格情報は `identity-admin`（`view-users`・`manage-users`・`view-realm`。**`manage-clients` を持たない**） | `AuthorizationService/Infrastructure/ExternalServices/{KeycloakIdentityAdminClient,IdentityAdminRegistration}.cs`・realm の `identity-admin` |
| 認可サービスの名指しの照会は `users?username=…&exact=true&briefRepresentation=false&max=2`。**サービスアカウントを除く絞りは無い**（除くのは全件の列挙 `ListAllUsersAsync` だけ。#1609） | `KeycloakIdentityAdminClient.FindByUsernameAsync`・`ListAllUsersAsync` |
| 実行時の `user_id` は `MachinePrincipal.ServiceAccountUsernamePrefix + ClientId.ToLowerInvariant()` | `Domain/ToolInvocation.cs` `ToolUserContext.For` |
| `service-account-` の綴りの在処: `MachinePrincipal`（共有）・`DepartmentAttributeReconciliation`（認可）・`ToolUserContext`（MCP）・受け口（検索・グラフ・文書） | `grep -rn 'service-account-' --include=*.cs src` |
| Keycloak の統合試験の器（Testcontainers の Keycloak・既存の Keycloak の試験器）は**無い**。`Knowledge.IntegrationTests` は Postgres / Qdrant / RabbitMQ 等のみ | `grep -rln -i keycloak src/*/backend/Tests`・`Directory.Packages.props` |
| realm の `unmanagedAttributePolicy: ADMIN_EDIT` は入っている（IADR-0329。unmanaged 属性が黙って捨てられない） | `deploy/keycloak/microservices-platform-realm.json` |

## 母集合（規則 9・10）

### 規則 9 — 誤りの側の文字列で全文書を走査した

`grep -rn -e '#1786' -e 'クライアント作成' -e 'クライアントを作らない' -e '登録簿へ書くだけ' -e '登録簿だけ' -e 'Keycloak で直接' -e '管理コンソールで直接'`（`.md`・`.cs`・`.yaml`・`.ts`・`.tsx`。`.ai-context/specs/`・`CHANGELOG.md` を除く）:

| 箇所 | 扱い | 理由 |
| --- | --- | --- |
| `docs/screens/SC-12_mcp-client-management.md`（冒頭の注記・§計画との対応「クライアント登録」・§未決事項 1） | **直す** | 「実装は未着手」「登録先は登録簿だけ」が本段で誤りになる（無人は IdP へ書く） |
| `docs/tests/SC-12_mcp-client-management.md`（§範囲「認可サーバーのクライアント作成（実装していない）」・§未決事項 2） | **直す** | 同上 |
| `docs/api/FR-16_mcp-server.md`（管理 REST） | **直す**（追記） | 503 / 502 / 400（IdP に既にある）の応答が増える |
| `docs/tests/FR-16_mcp-server.md`（§クライアント登録管理） | **直す**（追記） | 本段の試験の写像 |
| `.ai-context/adr/IADR-0297`（§結果・§残余「クライアント作成を伴わない」） | **直さない** | 凍結記録（`.ai-context/` の本文プロズは書き換えない） |
| `.ai-context/adr/IADR-0479`（§残るリスク「#1786 で追う」） | **直さない** | 凍結記録。2026-10-08 の追記で #1786 へ委ねており、本段の結論は IADR-0515 が持つ |
| `.ai-context/adr/IADR-0329` L80・`docs/tests/SC-17` T-42（「クライアント作成」が 403） | **対象外** | `identity-admin` の陰性対照の話であり、本段の誤りの側ではない（別クライアント `mcp-client-admin` を足すので、あちらの最小権限は変わらない） |

### 規則 10 — この変更で新たに誤りになる自分の記述

- 「登録は登録簿へ書く」を前提にした試験（`McpClientEndpointTests`・`ServiceAccountAttributeSubsetEndpointTests`・`McpValidationProblemContractTests`）は、書き込み口が未設定だと 503 になる。**試験の器（`TestWebApplicationFactory`）に `in-memory` の口を宣言**して前提を保ち、未設定の挙動は別の器で固定する。
- 画面仕様書の「繰り延べ」「実装は未着手」は、**無人について**だけ誤りになる。有人は引き続き登録簿だけなので、「一部する」は残す（理由を有人と配備の配線へ差し替える）。
- 導出値: `status: completed`（画面仕様書）は「記述する範囲の実装とテストが揃った」の意であり、本段の後も有人と配備の配線が残るので据え置き、注記で範囲を書く。

## 設計（正は IADR-0515）

1. 口 `IServiceAccountProvisioner`（Domain/Ports）と実装 `KeycloakServiceAccountProvisioner`（named `HttpClient` ＋ client_credentials）。
2. 管理用は別の機密クライアント `mcp-client-admin`（`manage-clients`・`manage-users`）。構成 `McpClientProvisioning:*`。未設定は 503・`in-memory` は非配備ホスト限定・`keycloak` は資格情報が欠ければ起動時に落ちる。
3. 無人のテンプレート（機密・SA つき・人の流れを閉じる・`managed-by` の印）。属性は丸ごと置き換え、集合値は多値。
4. 順序: 検証 → 登録簿の重複 → IdP（作成 → SA の利用者 → 認可サービスと同じ照会 → 書き込み → 読み戻し）→ 登録簿。失敗は補償（`IdpFirstWrite`）。IdP に既にあれば 400。
5. 検知（定期の照合 ＋ 計器 ＋ 警報）は決めるが**本段では実装しない**。
6. フォローアップ 3 は書き込みのたびに照会で確かめる。認可サービス側は単体テストで固定。
7. 利用者名の組み立てを `ToolUserContext.ServiceAccountUserName` の 1 か所に寄せる。

## 受け入れ基準 → 試験

| issue の受け入れ基準 | 本段 | 試験（ID は試験メソッドの注記） |
| --- | --- | --- |
| AC1: 無人の登録が成功したら、Keycloak に機密クライアントとサービスアカウントがあり、割り当てた属性が利用者属性として入っている（**統合試験**） | 🔶 **一部**（偽の Keycloak と API 面。稼働の Keycloak での統合試験は無い） | T-1786-01・02・05・08（`KeycloakServiceAccountProvisionerTests`）／T-1786-31・35（`IdpProvisioningEndpointTests`） |
| AC2: 部分集合でない属性、または `private-note` を含む割当は、IdP へ何も書かずに拒否する（登録・差し替え） | ✅ | T-1786-32・33・34（否定形）／T-1786-24（IdP へ書けなければ登録簿へも書かない） |
| AC3: 登録簿と IdP が食い違うとき、照合が検知して知らせる | ❌ **未実装**（IADR-0515 決定 5。後続の段） | — |
| AC4（否定形）: 入口ができるまで、MCP のサービスアカウントへ Keycloak で直接属性を割り当てない | ✅（コードで担える範囲）: 口が未設定の配備は無人を 503 で拒み登録簿にも書かない／入口を通らずに IdP にあるクライアントへは書かない | T-1786-38（未設定は 503・登録簿に書かない）／T-1786-04・25・36（既にあれば書かない） |
| フォローアップ 3: 認可サービスの照会がサービスアカウントの利用者を返す | 🔶 **コードの経路は固定**・稼働の Keycloak での実測は無い | T-1786-11（認可サービス `KeycloakIdentityAdminClientTests`）／T-1786-02・03（書き込み口が同じ照会で確かめ、返らなければ書かない） |
| 補償（IADR-0515 決定 4） | ✅ | T-1786-03・05・06・07・21・22・23 |
| 口の選択（同 決定 2） | ✅ | T-1786-41〜45 |
| 有人は IdP へ書かない（同 決定 3・Q1） | ✅ | T-1786-37・38 の陽性対照 |

## 段の分け方（issue の受け入れ基準に沿う）

- **段 1（本 PR）**: 書く前の検証 ＋ IdP への書き込みの口（作成・差し替え・補償・フォローアップ 3 の確かめ）。配備では口を宣言しない（503）。
- **段 2（後続の issue）**: 配備の配線 ——realm へ `mcp-client-admin`（`manage-clients`・`manage-users`）、secret の供給（ExternalSecret `mcp-client-admin-oidc`・Vault の初期投入 `deploy/local/vault/eso/bootstrap.sh`・`scripts/k8s-local-up.sh` の手動経路とその試験・`deploy/bootstrap/sc22-secret-items.json`）、helm values の `McpClientProvisioning__Provider=keycloak` と `ClientSecret` の secretKeyRef、compose の配線。**稼働の Keycloak での実測**（AC1 の統合の証跡・フォローアップ 3）。
- **段 3（後続の issue）**: 食い違いの検知（IADR-0515 決定 5）—— McpServer の常駐の照合・計器（ゲージ ＋ 結末のカウンタ）・警報 2 本（写し 4 か所）・`scripts.repo.test.js` の突合。AC3。

## 検証

- `dotnet build src/platform/backend/backend.slnx`（警告 0）・`dotnet format --verify-no-changes`・`dotnet test`（McpServer・AuthorizationService）。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` と文書系の検査器一式。

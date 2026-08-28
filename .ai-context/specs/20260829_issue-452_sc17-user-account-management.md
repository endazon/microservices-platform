---
title: 作業仕様書 — SC-17 ユーザーアカウント管理（身元管理の抽象・BFF・画面・仕様書）を通す（#452）
type: spec
status: done
related_ids:
  - FR-05
  - FR-09
  - UC-05
  - SC-09
  - SC-17
  - ADR-0004
  - ADR-0026
  - ADR-0032
  - ADR-0036
author: implementation-agent
created: 2026-08-29
updated: 2026-08-29
plan_refs:
  - "05_screens §SC-17 目的: ユーザーアカウントの権限管理（ロール割当・ABAC属性割当・無効化）。Keycloak Admin API と属性ストアへ反映する"
  - "05_screens §SC-17 主要素: ユーザー一覧（部門・ロール・ABAC属性・状態。部門／ロールのフィルタ）、権限編集（ロール＝利用者／管理者／運用者／システム管理者。複数併任可。ABAC属性＝部門・機密区分上限・タグ）、アカウント無効化（表示は「無効（全セッション失効）」）、監査ログへの導線"
  - "05_screens §SC-17 入力/バリデーション: ロール割当＝必須・複数選択・定義済みロールのみ（併任可）／ABAC属性（部門・機密区分上限）＝必須・選択・SC-09 の属性体系に定義済みの値のみ／ABAC属性（タグ）＝任意・複数選択・SC-09 のタグ辞書に定義済みの値のみ"
  - "05_screens §SC-17 アクション: 保存→Keycloak・属性ストアへ反映し認可判定へ即時反映。無効化→全セッション即時失効。アカウントは人事システム連携で自動プロビジョニングし、退職者は連携により自動で無効化され全セッションが即時失効する（本画面から新規作成はしない）。操作は監査ログに記録"
  - "05_screens §SC-17 アクセス制御: システム管理者ロール限定。ルートは /admin/users"
  - "05_screens §共通シェル: SC-09・SC-12・SC-17 = システム管理者。左ナビ「管理」グループ「ユーザー管理」"
  - "ADR-0026 §決定: SC-09 は属性体系・タグ辞書・ポリシーの「定義」を担い、SC-17 は個々の利用者への「割当」と無効化を担う。SC-17 の反映先は Keycloak Admin API と属性ストアであり、認可判定へ即時反映する"
  - "06_technical/02_service-decomposition §推奨サービス構成: 認可サービス（ABAC）の保持データは「属性・タグ・ポリシー（PostgreSQL）、Keycloak連携」"
  - "06_technical/02_service-decomposition §サービス分割時の注意点: サービス数の基準は 11＋BFF とし、これを超える分割は新 ADR で判断する。認証は Keycloak に委譲し ID 管理を自作しない"
  - "06_technical/09_datasource-connectors: 取り込み経路が Keycloak 管理 API を呼ぶ主体は SC-17 の管理者操作とは別である。サービスクライアントへ与える権限の範囲を突き合わせること。本節はその権限設計を定めない"
related_adrs:
  - IADR-0301
issue: "#452"
---

# 作業仕様書: SC-17 ユーザーアカウント管理（#452）

## 1. 起点と、着手前の実測

親からの申し送りを鵜呑みにせず、すべて自分で引き直した。

| 主張 | 実測コマンド | 結果 |
| --- | --- | --- |
| `/users` を宣言するエンドポイントが backend に無い | `grep -rn 'MapGet("/users\|MapGroup("/users' --include=*.cs src/*/backend` | **0 件**（`another_users_note` 等のテスト名だけが一致した） |
| 契約に `users` の語が無い | `grep -c -i users docs/api/openapi.yaml` | **0** |
| 属性ストアに利用者の割当を持つ表が無い | `grep -rn DbSet .../AuthorizationService` | **2 件のみ**（`AttributeDefinitions` / `Policies`）。**利用者への割当を持つ表は存在しない** |
| Keycloak Admin API を呼ぶコードが無い | `grep -rn 'admin/realms\|AdminApi\|admin-cli\|KeycloakAdmin' --include=*.cs --include=*.ts src/platform src/knowledge` | **0 件** |
| 利用者 ABAC 属性の実体は Keycloak のユーザー属性である | `deploy/keycloak/microservices-platform-realm.json` の `abac-attributes` クライアントスコープを読む | `clearance` / `department` を **user attribute → claim** で写している。判定側 `BffScopeResolver.ExtractUserAttributes` はその 2 クレームだけを読む |
| BFF はセッション方式へ移行済み | `ls src/platform/backend/Bff/Platform.Bff/Foundation/Session/` | `BackchannelLogoutProcessor.cs` / `RedisTicketStore.cs` ほか 7 ファイルが在る |
| バックチャネルログアウトの宛先が realm に登録済み | realm.json の client `bff` の `attributes` | `backchannel.logout.url` ＋ `backchannel.logout.session.required: true` が在る |

**結論**: SC-17 はバックエンドが皆無であり、かつ「属性ストア」の実装上の実体は
**AuthorizationService ではなく Keycloak** である（利用者側の属性は JWT クレーム経由でしか
判定に入らない）。したがって本作業の本体は **身元管理（IdP 管理 API）の抽象を切ること**である。

## 2. 実装先の食い違い（3 者不一致）

| 出典 | SC-17 の帰属 | 実測箇所 |
| --- | --- | --- |
| 計画 `ADR-0026` §フォローアップ | 「Keycloak テーマの実装（SC-13〜16）と **Admin API 連携（SC-17）** の実装計画への反映」。**担当サービスは名指ししていない** | `07_adr/ADR-0026_...md:84` |
| #438（起票時本文） | スコープに「**管理画面バックエンド: SC-09・SC-17**」を含む＝認可認可再実装の側 | `.ai-context/specs/20260823_issue-438_keycloak-theme-and-smtp.md:39` |
| #438（2026-08-21 コメント） | **SC-09・SC-17 を残作業から除外**した（オープンに残すのは `smtpServer` と theme の 2 点のみ） | 同 `:47` / `:101` |
| planning#490 の引き継ぎ表 | 「未実装画面 **SC-12・SC-17〜SC-21 の新規実装** → **#452**」。**画面としてのみ**扱い、バックエンドに触れていない | `.ai-context/specs/20260804_issue-490_spa-router-shell.md:91` / `:456` |

**帰結**: #438 がバックエンドを手放し、#490 が SC-17 を「画面」として #452 へ送り、計画は担当を
名指ししていない。**3 者のどこにも「SC-17 のバックエンドを誰が作るか」が書かれていない。**
本作業で裁定して [[IADR-0301]] に記録し、計画側へは環流 issue の草案として親へ返す（起票は親）。

### 2.1 裁定 — `AuthorizationService` へ足す（新サービスは作らない）

根拠（いずれも計画の文言。詳細と代償は [[IADR-0301]] 決定 1）:

1. 計画 `06_technical/02_service-decomposition` §推奨サービス構成 が、**認可サービスの保持データに
   「Keycloak連携」を明記**している。SC-17 の反映先（Keycloak Admin API ＋ 属性ストア）は
   そのまま同サービスの責務欄に載っている。
2. 同 §サービス分割時の注意点 が「サービス数の基準は 11＋BFF とし、**これを超える分割は新 ADR で
   判断する**」と定める。**新サービスは計画 ADR が要る**ので、実装側の IADR では決められない。
3. 入力規則「SC-09 の属性体系・タグ辞書に定義済みの値のみ」の**値域の正は
   `AttributeDefinitions`（AuthorizationService の表）**である。別サービスへ置くと、
   保存のたびに認可サービスへ問い合わせる経路が増える（値域と検証が 2 サービスに割れる）。
4. 既に BFF の downstream（named client `AuthorizationService`）として配備 manifest に載っており、
   `scripts/check-bff-downstreams.js` の :8080 突合を通っている。新サービスは manifest・image
   マッピング・chart キーの新設を伴う（#452 の SC-12 で実測した手間であり、1 画面の追加と
   同じ PR に混ぜる変更ではない）。

**同時に境界を明示する**（計画 §注意点「ID 管理を自作せず、認可サービスは『ABAC ポリシー判定』に
責務を限定する」との緊張を、無視せず線で解く）:

- **利用者表を本サービスへ作らない。** 一覧も割当もすべて IdP へ委譲し、`DbSet` を増やさない。
- **新規作成の口を持たない**（計画 §SC-17 アクション「本画面から新規作成はしない」）。
- 認証・資格情報・パスワード・MFA は一切扱わない（SC-13〜16 の領域）。

## 3. 対象範囲

### 3.1 対象

- 身元管理の抽象 `IIdentityAdminClient`（列挙・ABAC 属性更新・ロール割当/解除・`enabled` 切替・
  セッション失効の 5 操作）と、Keycloak Admin REST 実装 ＋ in-memory fake。
- `AuthorizationService` の管理 API `/authz/users*`（AdminOnly）と入力検証。
- BFF `/bff/admin/users*`（AdminOnly・透過中継）。
- 契約 `docs/api/openapi.yaml` への**追記**と orval 生成物。
- 画面 `knowledge/frontend/src/features/sc17-users`（`/admin/users`・`group: 'admin'`・
  `requiresAnyRole: [PlatformRole.Admin]`）＋ 合成点・ナビ・`PLANNED_ROUTES`・i18n・lint 適用範囲。
- 仕様書 `docs/screens/SC-17_*.md` / `docs/tests/SC-17_*.md`、実装ADR [[IADR-0301]] と索引。
- 配備（compose / helm values）への provider 宣言の追加。

### 3.2 対象外（送り先を明記する）

| 事項 | 送り先 | 理由 |
| --- | --- | --- |
| **利用者の新規作成** | **実装しない（計画が禁じている）** | 計画 §SC-17「本画面から新規作成はしない」「人事システム連携で自動プロビジョニング」。作ると計画違反である |
| 実 Keycloak との疎通確認 | **残件**（親へ報告） | 本環境に Docker / k3s / 実 Keycloak が無い。`realm-management` ロールを持つ機密クライアントの配備も伴う |
| 人事システム連携（SCIM／バッチ）の実装 | 計画のフォローアップ（ADR-0026 §フォローアップ「人事システム連携の方式の技術選定」） | 方式が未確定である |
| ロール 4 種（利用者／管理者／運用者／システム管理者）への realm 拡張 | ADR-0026 §フォローアップ「管理ロールの権限分離（現行実装は単一 `platform-admin`）」 | realm には `platform-admin` / `platform-operator` の 2 つしか無い。**画面は値集合を焼き込まず IdP から引く**ので、realm が増えた日に画面は自動追随する |
| 監査ログ画面 | 実装しない（導線のみ） | 記録は Keycloak の admin events ＋ ログ基盤側（[[IADR-0294]] が realm 側の `adminEventsEnabled` を投入済み）。SC-12 と同じく**実行時 config の参照先が未設定なら導線を描かない** |
| タグの「1 キー複数値」 | 繰り延べ（§7 未決事項 1） | 判定側（`ExtractUserAttributes` → `AbacEvaluator`）が 1 キー 1 値しか読まない。複数値を受けても静かに切り捨てられる |

## 4. 母集合（`.claude/rules/traceability.md` §是正・追随の母集合の取り方）

規則 2（誤りの側の文字列で全文書を走査）・規則 9・10 に従い、着手前に自分で引いた。

| 引いたもの | コマンド | 結果と扱い |
| --- | --- | --- |
| SC-17 に言及する追跡下ファイル | `grep -rn 'SC-17' --include=*.md --include=*.ts --include=*.tsx .`（`src/ai-stock-trading` 除外） | 24 件。うち**追随が要るのは 3 件**（`router.test.ts` の `PLANNED_ROUTES`・`eslint.config.js` の lingui `files`・`.ai-context/adr/README.md` の索引）。残る 21 件は「SC-09・SC-12・SC-17 = システム管理者」の引用と過去の作業仕様書（凍結記録）であり**書き換えない** |
| 既存の `/admin/*` 画面の形 | `ls src/knowledge/frontend/src/features/sc12-mcp-clients` | 直近の先例（#452 同一 issue）。**同じ形に揃える**（route factory ＋ `RequireRole` ＋ 語彙モジュール ＋ orval フック） |
| 「即時失効」に言及する計画記述 | `grep -rn '即時失効' /home/user/project-planning/projects/microservices-platform` | 4 件。うち `06_technical/07_abac-attribute-model.md:162` の **1 件が実態と食い違う**（§7 環流草案 2） |
| 導出値（ロールの値集合・属性の値域） | 走査ではなく**引き直す**（規則 10） | 画面にも後段にも焼き込まない。ロールは IdP、属性値は `AttributeDefinitions` から引く |

**除外理由**: `src/ai-stock-trading/**` は別プロジェクトの submodule（[[IADR-0120]]）。
`.ai-context/specs/` の確定済み記録は凍結対象であり、本文を書き換えない。

## 5. 設計

### 5.1 抽象（`AuthorizationService/Domain/Ports/IIdentityAdminClient.cs`）

```
IReadOnlyList<IdentityUser> ListUsersAsync(ct)
IReadOnlyList<string>       ListAssignableRolesAsync(ct)
IdentityUser?               ReplaceAttributesAsync(userId, attributes, ct)
IdentityUser?               ReplaceRealmRolesAsync(userId, roles, ct)
IdentityUser?               SetEnabledAsync(userId, enabled, ct)
bool                        RevokeSessionsAsync(userId, ct)
```

- **`Create` に相当する操作を持たない。** 計画が禁じているので、**型で持てなくする**。
  これは規約ではなく構造で守る（`IdentityAdminContractTests` が反射で固定する）。
- `IdentityUser?` の `null` は「その ID の利用者が居ない」を意味し、端点は 404 へ写す。

### 5.2 実装 2 本と選択

| provider | 実体 | 用途 |
| --- | --- | --- |
| `keycloak` | `KeycloakIdentityAdminClient`（Admin REST。`realm-management` ロールを持つ機密クライアントで client_credentials） | 本番 |
| `in-memory` | `InMemoryIdentityAdminClient` | 開発・テスト |

**`IdentityAdmin:Provider` に既定を置かない。** 未設定は起動時例外にする（[[IADR-0301]] 決定 3）。
`keycloak` を選ぶと `IdentityAdmin:Keycloak:{BaseUrl,Realm,ClientId,ClientSecret}` が全部必要で、
**既定資格情報を持たない**（`DataSourceService/Program.cs` の先例と同型）。
`in-memory` は起動時に警告ログを 1 行出す（**実 IdP へ反映されないことを黙らせない**）。

### 5.3 サービスクライアントの権限範囲（計画 09_datasource-connectors が保留した点）

必要最小の `realm-management` クライアントロールは **3 つだけ**である。

| ロール | 使う操作 |
| --- | --- |
| `view-users` | 利用者の列挙・ロールマッピングの読み取り |
| `manage-users` | 属性更新・ロール割当/解除・`enabled` 切替・セッション失効（`POST users/{id}/logout`） |
| `view-realm` | 割当可能な realm ロールの列挙 |

**与えないもの**: `manage-realm` / `manage-clients` / `create-client` / `impersonation` / `manage-authorization`。
**取り込み経路のサービスクライアントとは別のクライアントにする**（計画 09 §が「別である」と明記）。

### 5.4 API（AuthorizationService。すべて AdminOnly）

| Method | Path | 意味 |
| --- | --- | --- |
| GET | `/authz/users` | 一覧（部門・ロール・ABAC 属性・状態） |
| GET | `/authz/users/assignable-roles` | 割当可能ロール（**定義済みロールのみ**の値域の正） |
| PUT | `/authz/users/{userId}/attributes` | ABAC 属性の差し替え（部分更新ではない） |
| PUT | `/authz/users/{userId}/roles` | ロール割当の差し替え |
| POST | `/authz/users/{userId}/disable` | 無効化 ＋ **全セッション失効** |
| POST | `/authz/users/{userId}/enable` | 再有効化 |

- **`POST /authz/users`（新規作成）を作らない。**
- `/authz/users/assignable-roles` は 2 セグメント、`{userId}` を含む経路は 3 セグメントなので衝突しない。
- BFF は `/bff/admin/users*` で**透過中継**する（`McpClientBffEndpoints` と同型。**状態コードを作り替えない**）。

### 5.5 入力検証（`Domain/UserAssignmentValidation.cs`）

| 規則 | 実装 |
| --- | --- |
| ロール割当は**必須**・複数選択・**定義済みロールのみ** | 空集合を 400。`ListAssignableRolesAsync` の集合外を 400。重複を 400 |
| 部門・機密区分上限は**必須** | `RequiredUserAttributeKeys = ["department", "clearance"]`。欠落・空白を 400 |
| タグは**任意** | 必須集合に入れない（**過剰拒否の否定側**。変異試験 ④ が固定する） |
| 値は **SC-09 の属性体系に定義済みのものだけ** | `scope=user` の `AttributeDefinition.AllowedValues` に無い値を 400。**未定義キーも拒否する**（文書側の「自由タグ許容」とは逆。理由は [[IADR-0301]] 決定 5） |

**必須キーを辞書の `Required` 列から引かない。** 同列は**取り込み時の必須性**として使われており
（`deploy/local/abac-seed/attributes.json` の `_comment` が「required は**すべて false**」「必須化は
実データ側が属性を備えてから」と明記）、**SC-17 の割当の必須性とは別の軸**である。
同じ列を 2 つの意味で使うと、片方を直したときにもう片方が黙って緩む。

### 5.6 画面

- ルート `/admin/users`、`group: 'admin'`、`requiresAnyRole: [PlatformRole.Admin]`、`RequireRole` で存在秘匿。
- 一覧（利用者・部門・ロール・ABAC 属性・状態）＋ **部門／ロールのフィルタ**。
- 権限編集（ロールの複数選択・部門・機密区分上限・任意属性）。
- 無効化ボタン。状態表示は **「無効（全セッション失効）」**（計画の文言そのまま）。
- 監査ログへの導線（実行時 config に参照先が無ければ**リンクを描かず所在を文言で示す**。SC-12 と同じ作法）。
- **新規作成フォームを置かない。** 不在は陽性対照つきのテストで固定する。
- 値集合（ロール・属性値）を**画面へ焼き込まない**。

## 6. 受け入れ基準

1. `dotnet build` / `dotnet test`（platform 全件）/ `dotnet format --verify-no-changes` が緑。
2. `pnpm run typecheck` / `lint` / `format:check` / `test` が緑。`pnpm run codegen && git diff --exit-code` が差分なし。
3. `check-route-manifest` / `check-bff-authz-docs` / `check-openapi-dto-drift` / `check-i18n-catalogs` /
   `check-chunk-budget` / `check-default-credentials` / `check-trace-blocks` / `check-doc-links` /
   `check-test-spec-coverage` / `check-bff-downstreams` / `scripts.test.js` が緑。
4. **変異 6 件（＋無変異のベースライン対照）を投入し、全件 KILL を実測する。**
5. 実装しなかったこと・実環境が要ることを、画面仕様書 §計画との対応 とテスト仕様書 §区分・未決事項へ
   **正直に**書く。

## 7. 検証（変異試験）

**無変異のベースライン対照を先に置いた**（変異が入っていない状態で、下の各判定器がすべて緑であること）。

| 対照 | コマンド | 結果 |
| --- | --- | --- |
| B-0 | `dotnet test src/platform/backend/backend.slnx`（全件） | **緑**（1,115 件成功 / 1 件スキップ / 失敗 0） |
| B-1 | `cd src && pnpm run test`（全件） | **緑**（99 ファイル・1,212 件成功） |
| B-2 | `node scripts/check-route-manifest.js` ほか 10 種 | **すべて OK** |

**変異 10 件・全件 KILL。** 各変異は 1 か所だけを書き換え、判定器を走らせ、**必ず元へ戻した**
（戻し忘れを防ぐため、当てる／戻すの両方で対象文字列が 1 件であることを表明する治具で回した）。

| # | 変異 | 当てた場所 | 判定器 | 結果 | 落ちた試験 |
| --- | --- | --- | --- | --- | --- |
| M1 | 境界層のロール限定（AdminOnly）を外す | `UserAdminBffEndpoints.cs` | `dotnet test Platform.Bff.Tests` | **KILL** | `Reads_AsOperator_AreForbidden`（2 例）/ `Writes_AsOperator_AreForbidden` |
| M2 | 画面のロール限定（`RequireRole`）に運用者を足す | `sc17UsersRoute.tsx` | `vitest run sc17-users` | **KILL** | `hides the screen from non-admins` |
| M3 | 定義済み以外の属性値を通す（許可値の照合を落とす） | `UserAssignmentValidation.cs` | `dotnet test AuthorizationService.Tests` | **KILL** | `ValidateAttributes_rejects_values_outside_the_dictionary` / `ReplaceAttributes_rejects_a_value_outside_the_dictionary` |
| M4 | 必須項目（部門・機密区分上限）を任意にする | `UserAssignmentValidation.cs` | 同上 | **KILL** | 5 件（必須の 2 例・辞書未整備・宣言そのもの・端点） |
| M5 | タグを必須にする（**過剰拒否の否定側**） | `userAccountVocabulary.ts` | `vitest run sc17-users` | **KILL** | 7 件（`does not require the optional tag attribute` ほか） |
| M6 | 新規作成の口を開ける（`POST /authz/users`） | `UserAdminEndpoints.cs` | `dotnet test AuthorizationService.Tests` | **KILL** | `The_route_table_has_no_user_creation_endpoint` |
| M7 | 無効化から全セッション失効を落とす | `UserAdminEndpoints.cs` | 同上 | **KILL** | `Disable_also_revokes_every_session_of_that_user` |
| M8 | 身元プロバイダの宣言に既定（偽物）を与える | `IdentityAdminRegistration.cs` | 同上 | **KILL** | `Registration_fails_when_the_provider_is_not_declared` |
| M9 | `PLANNED_ROUTES` の SC-17 行を落とす | `router.test.ts` | `node scripts/check-route-manifest.js` | **KILL** | 判定 1（マニフェストの網羅）違反 1 件 |
| M10 | 契約側だけに新規作成を宣言する（`POST /bff/admin/users`） | `docs/api/openapi.yaml` | 生成物の再生成差分 | **KILL** | 生成フック `useBffUserAdminCreateUser` が現れて差分が出る |

### M10 で測れたこと（**1 回目は生き残った**）

**`check-bff-authz-docs.js` では M10 は KILL できない**（実測）。同検査器は「実装の実効ロール」と
`x-roles` を突き合わせるもので、**契約にだけ在って実装に無い端点**を落とす向きを持たない。
1 回目の実行で `SURVIVED (exit 0)` を得てから、判定器を生成物の再生成差分へ替えて KILL した。
**「検査器が在る」ことと「その変異を捕まえる」ことは別である**ので、両方を記録に残す。

なお `check-bff-authz-docs.js` が**本作業の 6 端点を実際に見ていること**は、別の陰性対照で確かめた ——
`GET /bff/admin/users` の `x-roles` を空にすると
`実装の実効ロール: platform-admin / openapi の x-roles: (空)` で落ちる。

## 8. 未決事項・環流

1. **タグの 1 キー複数値**（繰り延べ）。判定側が 1 キー 1 値しか読まないため、契約も 1 キー 1 値にした。
   複数値を扱うには `AbacEvaluator` と `ExtractUserAttributes` の側から変える必要がある。
2. **環流草案 1（SC-17 の帰属の食い違い）** — §2 の表。起票は親が行う。
3. **環流草案 2（即時失効の記述が実態と食い違う）** — 計画 `06_technical/07_abac-attribute-model.md:162`
   が「実装は SPA がトークンを保持する方式であり、この即時失効は満たされていない…最大 10 分遅延する」
   と書いているが、**実装は BFF セッション方式へ移行済み**である（`Platform.Bff/Foundation/Session/` に
   `BackchannelLogoutProcessor` / `RedisTicketStore` が在り、realm の client `bff` に
   `backchannel.logout.url` が登録されている）。起票は親が行う。
4. **実 Keycloak 疎通は残件。** 本環境に Docker / k3s / 実 Keycloak が無く、`realm-management` ロールを
   持つ機密クライアントも realm に未登録である。Admin REST 実装は**スタブした
   `HttpMessageHandler` に対してのみ**検証した。

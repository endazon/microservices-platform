---
title: 作業仕様書 — 無人の MCP クライアントの secret を登録・再発行の応答で一度だけ返し、SC-12 の管理操作を監査記録に残す（計画 ADR-0134 決定 2・フォローアップ 4〜6。#1845）
type: spec
status: done
related_ids: [FR-16, UC-09, SC-12, ADR-0134, ADR-0123, ADR-0004, IADR-0516, IADR-0398]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0134_mcp-client-keycloak-template-and-secret-one-time-display.md 決定 2（統制 1〜6）・決定 3（3 点セットの 4〜8 行）・フォローアップ 4〜6
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-12（主要素・アクション・アクセス制御の 2026-10-09 補完）
issue: "#1845"
---

# 作業仕様書 — 無人の secret の一度だけの表示・再発行と、SC-12 の管理操作の監査記録（#1845）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。基点は MSP `origin/develop` `1e6cfa58`（PR #1854 のマージ。計画 ADR のレンジ 0001..0135）。
> 計画は project-planning の隣接クローン（`origin/main` `142c3e44`。読み取り専用）で読んだ。
> 判断の記録は **IADR-0516 への日付つき追記**に置く（本件は同 IADR の入口〔書き込みの口・入口の印・`IdpFirstWrite`〕に 2 つの操作を足すだけで、
> 計画 ADR-0134 決定 2 が統制の値を決めている。新しい IADR は起こさない）。
> 🔴 **稼働中のクラスタ・Keycloak には何も実行しない。** 稼働の Keycloak での確かめは integration-stack の門（M11）に足し、実走は dispatch に委ねる。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0134 決定 2**（統制 1: 表示は登録と再発行の応答の 1 回だけ・値を保存しない／2: 応答以外〔アプリケーションのログ・監査ログ〕に値を出さない／
  3: 監査ログに「誰が・いつ・どのクライアントの secret を発行／再発行したか」を残し値は残さない／4: 表示と再発行はシステム管理者に限る／
  5: 再発行は Keycloak の regenerate で旧 secret をその時点で失効させる／6: 運用者への引き渡しはシステムの外＝受け入れたリスク）、
  **決定 3** の 4〜8 行、**フォローアップ 4〜6**。
- 機能要求: **FR-16**。画面: **SC-12**（主要素・アクション・アクセス制御の 2026-10-09 補完）。UC: **UC-09**。監査ログの既存の器: 計画 ADR-0004（可観測性基盤への集約）。
- 起点 issue: **#1845**（環流 planning#751 の裁定）。
- 実装 ADR: **IADR-0516**（書き込みの口・入口の印・順序と補償 `IdpFirstWrite`・決定 4a の無効化の写し・#1844 追記）。

## 受け入れ基準（issue #1845）→ 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 (FU4) | 無人の登録の応答で secret を一度だけ返す（値は保存しない）。SC-12 に「再発行」（Keycloak の regenerate。旧 secret はその時点で失効）を足す。表示と再発行はシステム管理者に限る | `KeycloakServiceAccountProvisionerTests`（読み出し・regenerate・印なし・公開クライアント・失敗）・`IdpProvisioningEndpointTests`（API 面: 201 の本文に secret・有人には無い・一覧に無い・登録簿に無い・再発行で値が変わる・404 / 400 / 503・非管理者 403・取得の失敗は作ったクライアントを消して 502）・`BffMcpClientEndpointTests`（中継・`no-store`）・`--live` M11 |
| AC2 (FU5) | SC-12 の管理操作に監査記録を入れる。登録・差し替え・無効化・再有効化・（削除）・発行・再発行を記録し、secret の値は残さない | `McpClientAuditTests`（API 面: 操作ごとの action・subject・outcome・detail。拒否・不在・未構成・失敗も記録） |
| AC3 | アプリケーションのログ・監査ログに値が出ないことを試験で固定する | `McpClientSecretLeakTests`（ホストの全ロガーを捕まえ、登録と再発行の応答の secret が整形済みの本文・構造化の値・例外のどこにも無い） |
| AC4 (FU6) | Keycloak の client secret rotation の有無を実測する。期限を置くべきなら計画へ環流する | Keycloak 24.0.5 のソースの読み（IADR-0516 追記）・`--live` M11（再発行の直後に旧 secret が拒否される＝猶予期間なし） |
| AC5 | 画面（SC-12）の一度だけの表示（再表示しない・コピー導線）。再発行は確認を挟む | `McpClientManagementPage.test.tsx`（登録・再発行の後に表示・コピー・閉じると消える・次の操作で消える・有人には再発行ボタンが無い・確認を取り消すと送らない） |

## 現状（実測。`1e6cfa58`）

| 事実 | 確かめ方 |
| --- | --- |
| 登録の応答は `McpClientView`（secret を含まない）。無人の secret は Keycloak が生成し、実装は読み出さない | `RegisterClient/Endpoint.cs`・`KeycloakServiceAccountProvisioner.ServiceAccountClientTemplate`（`secret` を送らない） |
| 再発行の操作は無い | `McpClientEndpoints.MapMcpClientEndpoints`（一覧・登録・無効化・再有効化・差し替え・ツール） |
| **登録簿の削除の API は無い**（IADR-0516 #1844 追記 残余 4: 運用者が DB で消す） | 同上・`McpClientBffEndpoints` |
| 監査の既存の器は `Platform.Shared.Infrastructure.Foundation.Audit.IAuditLogger`（`Audit=true` の構造化ログ → OTel → ログ基盤。4 引数とも `LogSanitizer` を通す）。BFF の SC-22・NotificationService が使う。McpServer は登録していない | `AuditLogger.cs`・`git grep -n IAuditLogger` |
| McpServer の `Features/McpClients` にも BFF の中継にも監査の記録は無い（計画 ADR-0134 実測 5 と一致） | `git grep -n -i audit -- src/platform/backend/Services/McpServer src/platform/backend/Bff/Platform.Bff/Foundation/Endpoints/McpClientBffEndpoints.cs` |
| McpServer の利用者名は `preferred_username`（`AddPlatformAuth` の `NameClaimType`） | `AuthExtensions.cs` L157 |
| 配備の Keycloak は 24.0。realm は `adminEventsEnabled=true`・`adminEventsDetailsEnabled=true`・`eventsListeners=[jboss-logging]`。`--features` の宣言は無い | `deploy/keycloak/microservices-platform-realm.json` L45-82・`git grep -n -i features -- deploy` |

### Keycloak 24.0.5 のソースの読み（FU6）

| 事項 | 読んだ箇所 | 結論 |
| --- | --- | --- |
| client secret rotation | `common/.../Profile.java` L83 `CLIENT_SECRET_ROTATION("Client Secret Rotation", Type.PREVIEW)` | **preview の機能で既定は無効**。配備は `--features` を宣言しないので無効。期限（有効期間）も、再発行の後に旧 secret を残す猶予（rotated secret）も働かない |
| regenerate | `ClientResource.regenerateSecret`（POST `clients/{id}/client-secret`。`requireConfigure`） | 新しい値を生成して返す。rotation が無効なので `removeClientSecretRotationInfo` で旧値を残さない＝**旧 secret はその時点で使えなくなる**（決定 2 の 5 と一致） |
| 読み出し | `ClientResource.getClientSecret`（GET 同パス。`requireView`。管理イベントを出さない） | 登録の直後に値を読むのに使う。`manage-clients` は view を含む |
| 🔴 管理イベントの詳細 | `regenerateSecret` は `adminEvent…representation(rep)`（`rep` は値つきの `CredentialRepresentation`）。`AdminEventBuilder.representation` は `UserRepresentation` だけを `StripSecretsUtils` に通す。`adminEventsDetailsEnabled=true` の realm では表現を保存する | **再発行（管理コンソールでも SC-12 でも）の新しい値は、Keycloak の管理イベントの保存先（DB）に平文で残る。** `jboss-logging` のリスナーは表現をログに書かない（`logAdminEvent`）。作成（`ClientsResource.createClient`）の表現は入力そのもので、テンプレートは `secret` を送らないので値は残らない。`PUT /clients/{id}` で値を書く代替も、表現を剥がさずに残すので逃げ道にならない |

→ **FU6 の結論**: 期限の機能は配備で働かない（決定 2「期限は置かない」と食い違わない）。**期限を置くべきとは判断しない**（再発行は即時失効で、漏えいに気づいたときの手段は足りる）。
一方で、🔴 **再発行の値が Keycloak の管理イベントの詳細に残る**ことは、決定 2 の 2「応答以外に値を出さない」の射程（プラットフォームのアプリケーションのログ・監査ログ）の外だが、
決定 3 の表が「推論。実装の IADR で実測する」とした論点の答えであり、計画の判断（`adminEventsDetailsEnabled` の扱い・管理イベントの閲覧権限）が要る。**計画へ環流する**（本 PR では起票しない。PR 本文と報告で依頼する）。

## 母集合（規則 9・10）

### 規則 9 — 誤りの側の文字列で全文書を走査した

`git grep -n -i -e 'secret を返さ' -e 'secret は応答' -e 'secret を応答' -e 'secret は返さ' -e '管理画面で取得' -e 'client secret' -e 'FU4' -e 'FU5' -e 'FU6' -e '管理操作の監査' -e '再発行' -- ':!.ai-context/specs' ':!CHANGELOG.md' ':!src/ai-stock-trading'`
を MCP・SC-12・FR-16・IADR-0516 に関わる行へ絞った:

| 箇所 | 扱い | 理由 |
| --- | --- | --- |
| `docs/api/FR-16_mcp-server.md` L81「client secret は応答に載せない」 | **直す**（取り消し線＋改訂） | 無人の登録の応答に載せる |
| `docs/api/FR-16_mcp-server.md` の端点表・BFF 対応表 | **足す**（再発行） | 端点が増える |
| `docs/api/BFF_bff-surface.md` L175 付近の SC-12 の表 | **足す** | 同上 |
| `docs/api/openapi.yaml`（登録の 201・再発行の経路・スキーマ） | **直す・足す** → orval の再生成 | 契約が変わる |
| `docs/screens/SC-12_mcp-client-management.md` §未決事項 2・3 | **直す**（解消） | 本件で実装する |
| `docs/tests/SC-12_mcp-client-management.md` §未決事項 1 | **直す**（解消）＋ 試験の行を足す | 同上 |
| `docs/tests/FR-16_mcp-server.md` | **足す**（C-6x） | 試験が増える |
| `.ai-context/adr/IADR-0516` 決定 3 L77・§残余 4・#1844 追記の残余 5 | **日付つき追記だけ**（本文は凍結） | 凍結記録 |
| `.ai-context/adr/IADR-0297` L130「管理操作の監査記録（記録先・保持期間・閲覧経路）を決める」 | **据え置く**（凍結記録）。記録先は既存の監査ログ（保持・閲覧は既存の器のまま）と IADR-0516 追記に書く | 凍結記録 |
| `deploy/local/vault/eso/bootstrap.sh` L217・`scripts/k8s-local-up.sh` L449・L826 | **据え置く** | `mcp-client-admin` 自身の secret の話で、本件の対象（MCP クライアントの secret）ではない |
| `docs/how-to/plan-id-range-history-annex.md` L40 | **据え置く** | 計画 ADR の一覧の転記 |

監査について `git grep -n -i -e '監査ログ' -e 'audit' -- src/platform/backend/Services/McpServer docs/api/FR-16_mcp-server.md docs/screens/SC-12_mcp-client-management.md docs/security/security.md`
も引いた。`ToolInvocationService` の「監査」はツール呼び出しの監査（`LogInformation`）であり、本件の管理操作の監査とは別の器である（**据え置く**）。
画面の「呼び出し監査ログ」の導線（ログ基盤へのリンク）は、管理操作の記録も同じログ基盤に載るので**文言を直す**（呼び出しだけを指さない）。

### 規則 10 — この変更で新たに誤りになる自分の記述

- 登録の応答の型の説明（openapi の 201・`FR-16` 通信仕様書の状態表「201 / 200 書けた」）: 無人の 201 は secret を含む。
- 画面仕様書の冒頭の注記・§認可サーバーへの書き込み（「secret は別の作業で実装する」）。
- 門の冒頭の「M1〜M10」・終了の要約・`scripts/README.md` の門の行・`docs/tests/FR-16` の門の行: M11 を足す。
- `scripts/test-spec-coverage-baseline.json`: 新しいテストクラス（`McpClientAuditTests`・`McpClientSecretLeakTests`）を足すので、試験仕様書に行を足す。
- i18n のカタログ: 画面に文言が増える。チャンクの床（`check-chunk-budget`）は文言の増分で動くなら測って直す。
- knip: 新しい export を作らない（フックは feature 内に閉じる）。

## 設計（決定は IADR-0516 の 2026-10-09 追記 / #1845）

1. **口**（`IServiceAccountProvisioner`）に 2 つ足す:
   - `ReadClientSecretAsync(clientId)` … 入口の印つきの機密クライアントの現在の secret を読む（`GET clients/{id}/client-secret`）。
   - `RegenerateClientSecretAsync(clientId)` … 同じく regenerate する（`POST clients/{id}/client-secret`）。
   - 結果は `ClientSecretResult`（`Issued` + 値／`Absent`〔IdP に無い〕／`NotManaged`〔入口の印が無い〕／`NotConfidential`〔公開クライアント・SA なし〕）。
     失敗は既存の `IdpProvisioningException`（`Failed`＝502・`Unavailable`＝503）。
   - 🔴 **値の型は `ClientSecret`（`ToString()` が値を出さない）**。record の既定の `ToString` が値を含むので、ログの書式化・例外の文言・デバッガの表示で漏れない形にする。
   - 要求の取り消しは伝えない（書き込みの口の規則）。読み出しも登録の補償の経路にあるので同じにする。
2. **登録**（無人）: `IdpFirstWrite` の「登録簿へ書く」段で、**登録簿へ書く前に** secret を読む。読めなければ 502 を返す（`IdpFirstWrite` が失敗の結果を見て作ったクライアントを消す＝既存の補償に乗る）。
   応答は `McpClientRegistrationView`（`McpClientView` の項目 ＋ `clientSecret`。有人は null）。**登録簿・一覧・個別の応答には secret を持たない。**
3. **再発行**: `POST /mcp-clients/{clientId}/reissue-secret`（BFF `POST /bff/admin/mcp-clients/{clientId}/reissue-secret`）。AdminOnly（グループの既定）。
   登録簿に無い → 404、有人 → 400（公開クライアントは secret を持たない）、IdP に無い → 400（画面で属性を保存し直すと作られる）、印なし → 400、公開 → 400、
   未構成 → 503、失敗 → 502、成功 → 200 `McpClientSecretView(clientId, clientSecret)`。登録簿は書かない（値を保存しない。`updatedAt` も動かさない）。
   無効化された行も再発行できる（漏えいに気づいたら無効化してから回せる）。
4. **応答の `Cache-Control: no-store`**: secret を含む 2 つの応答（登録の 201・再発行の 200）は後段と BFF の両方で付ける（BFF の中継は後段の見出しを運ばない）。
5. **監査**: McpServer に既存の `IAuditLogger`（`AuditLogger`）を登録し、`Features/McpClients/McpClientAudit` の 1 か所から記録する。
   - action: `mcp-client.register` / `mcp-client.replace-attributes` / `mcp-client.disable` / `mcp-client.enable` / `mcp-client.secret.issue` / `mcp-client.secret.reissue`。
   - subject: 利用者名（`ClaimsPrincipal.Identity.Name`＝`preferred_username`。無ければ `(不明)`）。
   - outcome: 2xx `granted`・400 `denied`・404 `not-found`・503 `unavailable`・その他 `failed`（例外は `failed` を記録してから投げ直す）。
   - detail: `client=<id> kind=<種別> status=<状態>`。登録・差し替えは割り当てた属性（`attributes=k=v;…`。ABAC の値で秘密ではない）も残す。**secret の値は渡さない**（型が `string` を受けない作りにはしない＝呼び出しは 1 か所なので試験で固定する）。
   - 発行（`mcp-client.secret.issue`）は無人の登録が 201 のときだけ、登録の記録とは別の行で残す（「誰が・いつ・どのクライアントの secret を発行したか」を action で引けるように）。
   - 🔴 **削除は記録しない** —— SC-12 に削除の操作が無い（登録簿の削除の API は無い。計画の SC-12 のアクションにも無い）。足すなら同じ器で記録する（IADR 追記に残す）。
   - 認可で弾かれた要求（非管理者の 403）は端点に届かないので記録しない（既存の認可ミドルウェアの扱い）。
6. **画面**: 無人の登録・再発行の成功で、secret を**その場の状態だけ**に持つフック（`useIssuedClientSecret`。SC-20 の `useIssuedToken` と同じ作法。ストア・URL に載せない）で表示する。
   「表示できるのは今回だけ・閉じると再表示できない（再発行のみ）」の文言、値（`code`）、コピー（`navigator.clipboard` → `notify`。失敗も伝える）、閉じる。次の操作を始めたら捨てる。
   再発行は無人の行だけに出し、旧 secret が即時に失効する旨の確認を挟む。
7. **門 M11**（安ければ）: 無人の登録の 201 の `clientSecret` が Keycloak の現在値（master の管理者で読む）と一致し、それでトークンが出る。再発行が 200 で値が変わり、旧 secret は拒否・新 secret でトークンが出る。
   有人の 201 に `clientSecret` が無い。自己試験に判定器を足す。

## 検証

- `dotnet build src/platform/backend/backend.slnx`（警告 0）・McpServer / BFF の試験・`dotnet format --verify-no-changes`。
- `src/` で `pnpm run codegen`・`pnpm run i18n`（コミット後に差分なし）・`typecheck`・`lint`・`format:check`・SC-12 の vitest・ビルドと `check-chunk-budget --require`・`check-knip --require`・`check-i18n-catalogs`。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-mcp-client-provisioning.js --self-test`・文書系の検査器一式。
- 変異: 登録の応答から secret を落とす・一覧に secret を載せる・再発行で登録簿の印の確かめを外す・監査の記録を 1 つ落とす・ログに値を出す、を 1 つずつ入れて対応する試験が赤になることを確かめる。

---
title: 作業仕様書 — SC-12 の有人の登録で Keycloak に公開クライアント（PKCE S256・リダイレクト URI の完全一致・audience を MCP サーバーに限る）を作り、MCP サーバーで audience を検証する（計画 ADR-0134 決定 1・フォローアップ 1〜3。#1844）
type: spec
status: done
related_ids: [FR-16, UC-09, SC-12, ADR-0134, ADR-0123, ADR-0024, IADR-0516, IADR-0398, IADR-0086]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0134_mcp-client-keycloak-template-and-secret-one-time-display.md 決定 1・決定 3（3 点セット）・フォローアップ 1〜3
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-12（入力表のリダイレクト URI の行・アクション）
  - planning:projects/microservices-platform/06_technical/11_mcp-server-integration.md §4（クライアント登録制）
issue: "#1844"
---

# 作業仕様書 — SC-12 の有人の公開クライアントと MCP サーバーの audience の検証（#1844）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。基点は MSP `origin/develop` `afa9b917`（計画 ADR のレンジ 0001..0135）。
> 計画は project-planning の隣接クローン（`origin/main`。読み取り専用）で読んだ。
> 判断の記録は **IADR-0516 への日付つき追記**に置く（本件は同 IADR 決定 3 の既知の逸脱を解く実装であり、テンプレートの細目・audience の検証の置き場所・
> FU3 の実測の方法は計画 ADR-0134 決定 1 が実装の IADR へ委ねた範囲に収まる。新しい IADR は起こさない＝並走する PR との採番の衝突も起きない）。
> 🔴 **稼働中のクラスタ・Keycloak には何も実行しない。** 稼働の Keycloak での実測は CI の integration-stack（使い捨ての k3d クラスタ）の門に足し、
> 実走はオーケストレーターの dispatch に委ねる。本 PR の時点の FU3 の結論は Keycloak 24.0.5 のソースの読みであり、門の実走で確かめる。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0134 決定 1**（有人も SC-12 で Keycloak に作る。公開クライアント・PKCE S256 必須・リダイレクト URI の完全一致〔`https` かループバック `http://127.0.0.1` / `http://[::1]`〕・ワイルドカード不可・Web オリジンは空・直接付与 / 暗黙 / サービスアカウントは無効・audience を MCP サーバーに限り MCP サーバーが検証・DCR は開かない）、**決定 3**（統制と実現手段の表の 1〜3 行）、**フォローアップ 1〜3**。ADR-0123 決定 2（SC-12 が IdP への唯一の入口）。
- 機能要求: **FR-16**。画面: **SC-12**（入力表に「リダイレクト URI（有人時必須）」の行が足された）。UC: **UC-09**。
- 起点 issue: **#1844**（計画の環流 planning#751 の裁定。2026-10-09）。他サービスの audience の検証は #1846（本件の外）。決定 2（secret の一度だけの表示・再発行）・FU4〜6 は別 issue。
- 実装 ADR: **IADR-0516 決定 3**（有人を IdP へ書かない既知の逸脱 → 本件で解く）・決定 4（順序・補償・入口の印）・決定 4a（無効化の写し）・決定 5（照合）。

## 受け入れ基準（issue #1844）→ 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 | 有人の登録で Keycloak に公開クライアント（PKCE S256・リダイレクト URI の完全一致・Web オリジン空・直接付与 / 暗黙 / SA 無効・audience の写像）を作る。入口の印と補償は無人と同じ | `KeycloakServiceAccountProvisionerTests`（テンプレート・読み戻し・補償）・`IdpProvisioningEndpointTests`（API 面・登録簿の失敗で取り消す）・`--live` M9 |
| AC1b | 登録の契約にリダイレクト URI を足す（BFF・openapi・orval・画面の入力と検査）。不正な URI（ワイルドカード・`http` の非ループバック・`localhost`・フラグメント・利用者情報・相対）を 400 で拒む。無人へ渡したら 400 | `RegisterMcpClientValidatorTests`・`McpValidationProblemContractTests`・`mcpClientVocabulary` の単体試験・`McpClientManagementPage.test.tsx` |
| AC2 | MCP サーバー（`/mcp`）は audience `mcp-server` を検証する。管理 API（`/mcp-clients`。BFF が利用者のトークンを中継する）は従来どおり | `McpAudienceAuthenticationTests`（署名した JWT で aud の有無・既定のスキームとの分離）・`--live` M9（例示のアクセストークンの aud）・M8（無人のトークンの aud） |
| AC3 | Keycloak の既定の登録ポリシーで DCR が閉じていること・ループバックの port の扱いを実測する | `--live` M10（匿名の DCR が 403 で作られない・初期アクセストークンが無い・匿名ポリシーに Trusted Hosts〔信頼ホストは空〕が在る）・M9（ループバックの port 違い・`https` の port 違い・path 違い・host 違い）。結論は IADR-0516 追記 |
| AC4 | integration-stack の門に有人の登録を足す（作成・テンプレート・PKCE の強制・誤ったリダイレクトの拒否・audience）＋ 自己試験 | `check-mcp-client-provisioning.js --self-test`（判定器）・`--live` M9・M10 |

## 現状（実測。`afa9b917`）

| 事実 | 確かめ方 |
| --- | --- |
| 有人の登録は登録簿だけへ書く（`if (kind != McpClientKind.ServiceAccount) return await WriteRegistry(ct);`） | `Features/McpClients/RegisterClient/Endpoint.cs` L59 |
| 無効化・再有効化も有人の行は IdP に触れない | `McpClientEndpoints.SetEnabledAsync`（`client.Kind != ServiceAccount`） |
| 照合は登録簿の無人の行だけを読む（`Where(c => c.Kind == ServiceAccount)`）。登録簿に無い印つきのクライアントはすべて `orphan` | `IdpReconciliationCheck.RunAsync`・`FindDriftsAsync` |
| 全サービスの JWT は `ValidateAudience = false`。`/mcp` は `RequireAuthorization()`（既定のスキーム） | `AuthExtensions.cs` L92・`Program.cs` L168 |
| BFF は SC-12 の管理 API へ**利用者のトークン**（aud は MCP サーバーではない）を中継する | `Bff/.../McpClientBffEndpoints.cs` の `Proxy` |
| realm の宣言は `clientScopes` を明示し `defaultDefaultClientScopes` を宣言しない。入口が作るクライアントは既定スコープを持たない（無人のトークンは `preferred_username` を持たない） | `deploy/keycloak/microservices-platform-realm.json`・門の登録者が `profile` を明示で割り当てる理由（門の冒頭） |
| realm の宣言は `components` にクライアント登録ポリシーを持たない。取り込みは `RealmManager.importRealm` → `setupClientRegistrations` → `DefaultClientRegistrationPolicies.addDefaultPolicies`（ポリシーが 0 件のときだけ既定を足す） | Keycloak 24.0.5 のソース（`RealmManager.java` L626・L746、`DefaultClientRegistrationPolicies.java` L56〜97） |
| 既定の匿名ポリシーの `Trusted Hosts` は信頼ホストが空・`host-sending-registration-request-must-match=true`。匿名の DCR は `verifyHost` が「Host not trusted.」で 403。Bearer の DCR は `manage-clients` / `create-client` が要る。初期アクセストークンは管理者が作らない限り無い | 同 `DefaultClientRegistrationPolicies.java` L77〜84・`TrustedHostClientRegistrationPolicy.java` L92〜122・`ClientRegistrationAuth.requireCreate` |
| リダイレクト URI の照合（`RedirectUtils.verifyRedirectUri`）: 完全一致（登録値が `*` で終わるときだけ前方一致）。一致しないとき、要求の URI が `http://localhost` か `http://127.0.0.1` で始まれば **port を落として**もう一度照合する。`http://[::1]` にはこの扱いが無い | Keycloak 24.0.5 `RedirectUtils.java` L108〜137・`Constants.java` L46〜47 |

## 母集合（規則 9・10）

### 規則 9 — 誤りの側の文字列で全文書を走査した

`git grep -n -e '有人は登録簿' -e '有人の行' -e '有人を登録簿' -e '有人は IdP' -e 'planning#751' -e '既知の逸脱' -e 'redirect' -e 'ValidateAudience' -e 'audience' -- ':!.ai-context/specs' ':!CHANGELOG.md'`（MCP・SC-12 に関わる行に絞った）:

| 箇所 | 扱い | 理由 |
| --- | --- | --- |
| `RegisterClient/Endpoint.cs` L56-59（既知の逸脱） | **直す** | 本件で有人も IdP へ書く |
| `McpClientEndpoints.cs` L46（有人の行は IdP に触れない） | **直す** | 有人の行も印つきのクライアントの `enabled` へ写す（無い行は `Absent`） |
| `IdpReconciliationCheck.cs` L21（有人の行は比べない） | **直す** | 有人の行も存在・入口の印・有効無効を比べる（属性は比べない）。比べないと有人のクライアントが `orphan` になる |
| `IdpReconciliationTests.cs` L126・`IdpProvisioningEndpointTests.cs` L234（T-1786-37） | **直す** | 期待が変わる |
| `docs/screens/SC-12_mcp-client-management.md` L125・L206-207・L217 | **直す** | 有人も IdP へ書く。入力（リダイレクト URI）が足される |
| `docs/api/FR-16_mcp-server.md` L99（有人の行は登録簿だけを切り替える） | **直す** ＋ 登録の契約・`/mcp` の audience を足す | 契約が変わる |
| `docs/operations/operations.md` L546（有人の行は比べない） | **直す** | 照合の対象が変わる |
| `docs/tests/FR-16_mcp-server.md` C-41（有人の行を含む一致） | **直す** ＋ C-5x を足す | 有人の行の期待が変わる |
| `docs/tests/SC-12_mcp-client-management.md` | **足す** | 画面の入力が増える |
| `docs/authz/FR-16_mcp-server.md` | **足す**（`/mcp` の audience） | 認可の前段の検証が増える |
| `.ai-context/adr/IADR-0516` 決定 3 L74・§残余 4・#1818 追記 L182・#1829 追記 L252 | **日付つき追記だけ**（本文は書き換えない） | 凍結記録 |
| `.ai-context/specs/*`（確定済み） | **直さない** | point-in-time の記録 |
| `scripts/check-mcp-client-provisioning.js` L473・L669（`redirectUris: []`） | **据え置く** | 測る側（登録者・孤児の再現）のクライアントであり、有人の入口ではない |
| `AuthExtensions.cs` L92・`AuthExtensionsTests`・`PlatformAuthJwtBearerOptionsTests`（他サービスの `ValidateAudience=false`） | **据え置く** | 他サービスは #1846 |

### 規則 10 — この変更で新たに誤りになる自分の記述

- 門の冒頭・終了の「M1〜M8」・`integration-stack.yml` の門の注記・`scripts/README.md` の門の行・`docs/tests/FR-16` の門の行: M9・M10 を足す。
- 照合の母集合「無人の行」: C# のログ文言（「無人の行 {Rows} 件」）・ゲージの説明・運用仕様書の表。
- 契約の `kind` の説明（有人は属性を持たない）に「有人はリダイレクト URI が必須・無人は渡さない」を足す（openapi・FR-16 通信仕様書・C# の DTO の注記）。
- `scripts/test-spec-coverage-baseline.json`: 新しいテストクラス（`McpAudienceAuthenticationTests`）を足すので、試験仕様書に行を足す。検査で確かめる。
- i18n のカタログ: 画面に文言が増える（`pnpm run i18n` で再生成してコミット）。

## 設計（決定は IADR-0516 の 2026-10-09 追記 / #1844）

1. **契約**: `RegisterMcpClientRequest` に `RedirectUris`（`string[]?`）を足す。検査は `RegisterMcpClientValidator`（[[IADR-0398]] の作法: 鍵は `request`・先頭 1 件・宣言順が契約）に `kind` の後・`egressTier` の前で足す:
   - 有人: 1 件以上 10 件以下。各要素は絶対 URI・2048 文字以下・`*` を含まない・フラグメント無し・利用者情報無し。`https`（host は任意）か、`http` でホストが `127.0.0.1` / `[::1]` のもの（RFC 8252 §7.3。`localhost` は §8.3 に従い認めない）。重複は不可。
   - 無人: 渡せない（空配列も含めて null 以外は 400）。
   - 規則は純関数 `RedirectUriRules`（Domain）に 1 つ置き、検証器・テンプレートの読み戻しが同じものを使う。
2. **書き込み口**: `IServiceAccountProvisioner.CreatePublicClientAsync(clientId, displayName, redirectUris)` を足す。Keycloak 版のテンプレート:
   `publicClient=true`・`standardFlowEnabled=true`・`implicitFlowEnabled=false`・`directAccessGrantsEnabled=false`・`serviceAccountsEnabled=false`・`fullScopeAllowed=false`・
   `redirectUris`＝入力そのもの・`webOrigins=[]`・属性 `pkce.code.challenge.method=S256`・`oauth2.device.authorization.grant.enabled=false`・`oidc.ciba.grant.enabled=false`・入口の印・
   `defaultClientScopes=["profile"]`（`preferred_username` が要る。realm は既定スコープを宣言しない）・`protocolMappers` に `oidc-audience-mapper`（`included.custom.audience=mcp-server`・`access.token.claim=true`・`id.token.claim=false`）。
   作成後に `GET /clients/{id}` で**読み戻して**テンプレートの要件（公開・PKCE S256・リダイレクト URI の集合・Web オリジン空・3 つの流れの閉・audience の写像）を確かめ、外れたら消してから 502。
   作成の骨組み（409 → `AlreadyExists`・成否不明の補償・途中の失敗の補償）は無人と**同じ 1 つ**を通す。
3. **無人のテンプレートにも audience の写像を足す**（`/mcp` が audience を検証するので、無人のトークンも `mcp-server` を持たなければ通らない）。
4. **audience の検証の置き場所**: `/mcp` だけに、既定のスキームと別の JWT スキーム（`McpAudience`。発行元・メタデータ・署名鍵の取り方は既定のスキームの値を写し、`ValidateAudience=true`・`ValidAudience=mcp-server` だけを変える）とポリシーを掛ける。管理 API（`/mcp-clients`）は既定のスキームのまま（BFF が中継する利用者のトークンの aud は MCP サーバーではないため。全サービスの共通設定は #1846）。
5. **登録の順序と補償**は無人と同じ（検証 → 登録簿の重複 → IdP → 登録簿。`IdpFirstWrite`）。口が未構成なら 503（有人も登録簿だけへ書く経路へは倒さない＝ADR-0134 の暫定手段は「作らない」であり、作らずに登録簿へ書くのは本件が解く逸脱そのもの）。
6. **無効化・再有効化**: 有人の行も `SetEnabledAsync` を通す（印つきのクライアントだけを変える。IdP に無い〔本件より前の〕行は `Absent` で登録簿だけ）。
7. **照合**: 登録簿の全行を読む。有人の行は IdP の存在・入口の印・有効無効を比べ、属性は読まない。🔴 **有人の行が IdP に無い（`client_missing`）は数えない** —— 本件より前に登録簿だけへ書かれた有人の行は IdP に作る経路が無く（差し替えは無人だけ）、数えると警報が鳴り止まない。そのような行は対応するクライアントが IdP に無いのでトークンが出ず、接続はできない（fail-closed）。
8. **画面**: 有人のときだけリダイレクト URI の入力（1 行 1 件のテキストエリア）を出し、規則の純関数（`validateRedirectUris`。後段と同じ規則の写し。最終の判定は後段）で事前に検査する。本文の `redirectUris` は有人のときだけ送る。

## 検証

- `dotnet build src/platform/backend/backend.slnx`・McpServer / BFF の試験・`dotnet format --verify-no-changes`。
- `src/` で `pnpm install --frozen-lockfile`・`pnpm run codegen`（差分なし）・`typecheck`・`lint`・`format:check`・`i18n`（差分なし）・SC-12 の vitest。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`node scripts/check-mcp-client-provisioning.js --self-test`・文書系の検査器一式。
- 変異: テンプレートの PKCE / 公開 / audience の写像・検証器のループバック判定・`/mcp` のスキームを 1 つずつ崩し、対応する試験が赤になることを確かめる。

## ［2026-10-09 追記 / #1844・PR #1854 セキュリティ監査 🔴］ループバックの port を必須にする

監査が Keycloak 24.0.5 の稼働で、port なしで登録した `http://127.0.0.1/cb` に `redirect_uri=http://127.0.0.1:49152@evil.example/cb` が一致し、
認可コードが evil.example へ送られることを示した（CVE-2024-8883。Keycloak 25.0.6 で修正。配備は `keycloak:24.0`）。上の設計 1 の「ループバックの port は任意」を改める。
決定は IADR-0516 の同日の追記（PR #1854 セキュリティ監査 🔴）。

| 受け入れ基準 | 試験 |
| --- | --- |
| AC5: `http` のループバック（`127.0.0.1`・`[::1]`）は port の明示が必須。port なし・`:` だけは理由（port の明示）を名指しして 400。port つきは通る。既存の拒否は残す | `RegisterMcpClientValidatorTests`（`InteractiveWithPortlessLoopbackRedirectUri_FailsWithReason`・`LoopbackLookalikeWithUserInfo_Fails`・陽性対照）・`mcpClientVocabulary.test.ts` |
| AC6: 門 M9 は port つきで登録し、別の port・横取りの形（`:<port>@evil.example`・`:1@evil.example`）が 400、port なしの登録が 400 で何も作らないことを測る | `check-mcp-client-provisioning.js --self-test`（`loopbackHijackProbes`）・`--live` M9 |
| AC7: 運用仕様書に、本件より前に作られた無人のクライアントが audience の写像を欠いて `/mcp` で 401 になる移行の注意と対処を書く | 文書（運用仕様書 §MCP クライアント登録簿と認証基盤の照合） |

母集合（規則 9）: `git grep -n -e '127.0.0.1/cb' -e '127.0.0.1/callback' -e '\[::1\]/c' -e 'port を落と' -e '任意の port' -e 'port は書いても' -e 'port の有無を問わない'`
（`.ai-context/` の凍結記録を除く）で引いた。直したもの: `RedirectUriRules.cs`・`McpClientContracts.cs` の注記・検証器と IdP の試験の例（`IdpProvisioningEndpointTests`・
`KeycloakServiceAccountProvisionerTests`）・画面（語彙・文言・プレースホルダ・試験）・i18n のカタログ・openapi と orval の生成物・門（`check-mcp-client-provisioning.js`）・
`docs/api/FR-16_mcp-server.md`・`docs/screens/SC-12_mcp-client-management.md`・`docs/tests/FR-16_mcp-server.md`（C-59・C-67）・`docs/tests/SC-12_mcp-client-management.md`（T-33）・
`scripts/README.md`（門の行）。据え置いたもの: 本仕様書の上の本文と IADR-0516 の上の追記（凍結記録。日付つき追記で改める）。

規則 10（新たに誤りになる自分の記述）: 門の自己試験の件数（`scripts/README.md`）・C# の試験件数（IADR の追記に書かない＝腐る導出値）。

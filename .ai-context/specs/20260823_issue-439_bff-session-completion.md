---
title: BFF セッション移行の完了 — 3b②③④（SPA のセッション方式化・oidc-client-ts 撤去）＋失効経路の実装
type: spec
status: done
related_ids: [NFR, SC-16, ADR-0026, ADR-0031, ADR-0032, IADR-0033, IADR-0121, IADR-0251, IADR-0273]
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - ./20260822_issue-439_bff-session-token-handler.md
---

# 仕様書: BFF セッション移行の完了（#439 / 第 3 段 3b②③④ ＋ 失効経路）

> 本書は**着手前**に作成した。前段の実測と段の定義は
> `20260822_issue-439_bff-session-token-handler.md`（3a / 3b① の記録）が持つ。本書は重複させない。

## 着手前の実測 — 受け皿はどこまで出来ているか

本ブランチ（`claude/implementation-repo-all-issues-hilvbs`）で実測した。

| 対象 | 実測 | 判定 |
| --- | --- | --- |
| 3a（BFF 受け皿） | `AuthBffEndpoints`（login/logout/me）・`BffSessionExtensions`（OIDC code+PKCE / query / `/bff/` パス / SaveTokens）・`RedisTicketStore`（subject 索引 ＋ `RemoveAllForSubjectAsync`）・`CsrfHeaderMiddleware`・DataProtection→Redis が**存在する** | 着地済み |
| 3b①（既定スキーム） | `AddAuthentication(SmartScheme)`＝振り分けが既定。`DefaultSchemeRoutingTests` 9 件が固定 | 着地済み |
| 3b②（SPA のセッション化） | `foundation/auth/` 15 ファイルは**全て oidc-client-ts 方式のまま**。`apiClient` は Bearer 注入のまま | **未着手** |
| 3b③（撤去） | 宣言 2（platform / knowledge）＋ import 11 ファイル（下の母集合） | **未着手** |
| 3b④（e2e） | e2e 13 本に oidc-client-ts の import は **0 件**（`grep -rln oidc e2e/` = 0）。全て「未認証 → /login 誘導」の検証で、**ログイン実走はしていない** | 前仕様書の「無改修では済まない」見込みは**外れ**。未認証導線が保たれれば無改修で緑の見込み |

### 🔴 issue の中核「無効化 → 次リクエストで 401」は受け皿だけでは達成されていない（実測）

- `RemoveAllForSubjectAsync` を**呼ぶ製品コードが 0 件**（grep: 宣言ファイル自身のみ）。
- バックチャネルログアウトは `RemoteSignOutPath = /bff/auth/backchannel-logout` の配線だけがある。
  ASP.NET Core の `OpenIdConnectHandler` の remote-signout 既定処理は**リクエストが運ぶ Cookie の
  セッションしか消せない**。Keycloak のバックチャネルログアウトは**サーバ間 POST（Cookie 無し・
  `logout_token` JWT）**なので、**既定処理では何も失効しない**。受け口は実質未実装である。
- Cookie セッションで下流サービスへ資格情報を運ぶ経路が**無い**。BFF の各端点は
  `Request.Headers.Authorization` を下流へ透過する方式（実測: `AuthzBffEndpoints.Proxy` ほか）で、
  Cookie 認証のリクエストにはそのヘッダが**存在しない**。セッション方式で SPA を切り替えても
  下流呼び出しが全部欠落資格情報になる（#948 と同型）。
- アクセストークンの refresh 経路が**無い**。realm の `accessTokenLifespan` は 300 秒、セッションは
  「記憶」30 日。refresh が無いと**ログイン 5 分後から下流が 401 になる**。
- Cookie セッションの principal に**ロールが載る保証が無い**。realm の `roles` クライアントスコープは
  既定で realm_access を**アクセストークンにだけ**入れる（id_token / userinfo には入らない）。
  `/bff/auth/me` のロールと `RequireRole` 系ポリシーが Cookie 経路で空になる。

**したがって本作業の範囲は「SPA の切り替え」だけでなく、上の 4 経路の実装を含む。**
issue #439 のスコープ（Token Handler・全セッション即時失効の経路・SC-16 整合・SPA 置き換え）の
とおりであり、スコープの拡大ではない。

## 母集合（3b③ oidc-client-ts 撤去。規則 1〜10 で引いた）

軸を複数とり、拡張子で絞らず、行フィルタで絞らず、リポジトリ全体（`node_modules` / `.git` /
`dist` / `obj` / `bin` / `coverage` / `src/ai-stock-trading` を除外。理由は下表）を走査した。

| 軸 | 一致（実装・設定・文書） |
| --- | --- |
| `oidc-client-ts` | 37 ファイル。うち**実装・設定で撤去対象**: `platform/frontend/package.json`・`knowledge/frontend/package.json`・`src/pnpm-lock.yaml`・`foundation/auth/` 8（AuthContext / AuthProvider / CallbackPage / CallbackPage.test / RequireRole.test / authConfig / roles / roles.test）・`foundation/testing/renderUnitRoute.tsx`・`foundation/ui/Layout.test.tsx`・`foundation/routing/initialChunk.test.ts`。**追随する文書**: `CLAUDE.md`（技術スタック別ルール「認証」）・`docs/tech/tech-requirements.md`・`src/platform/frontend/README.md`・`src/platform/backend/Bff/Platform.Bff/Program.cs`（コメント）・`scripts/knip-baseline.json`。**凍結記録（触らない）**: `.ai-context/adr/` `.ai-context/specs/` の過去記録（IADR-0121 / IADR-0251 / README 索引は live なので追記可）・`docs/how-to/adding-a-unit-submodule.md`（「不採用」説明として今後も真なので変更不要） |
| `UserManager` / `userManager` / `buildUserManagerSettings` / `WebStorageStateStore` / `InMemoryWebStorage` / `automaticSilentRenew` / `setUserManagerForTest` / `signinRedirect(Callback)` / `signoutRedirect` | すべて `foundation/auth/` と上の同一集合に閉じる（新規ファイルは増えない）。ほかに**理由コメントとして** `knowledge/frontend/src/features/{adminFlow,searchFlow}.test.tsx`・`platform/frontend/e2e/{bundle-splitting,sc02,sc05,sc06,sc07,sc09}.smoke.spec.ts`・`docs/tests/SC-01_search-chat.md` が `InMemoryWebStorage` を「E2E で認証済み導線を実走できない理由」として引く → **理由の書き換えで追随**（結論「実走できない」は BFF/Keycloak 不在により変わらない） |
| `setTokenProvider` / `tokenProvider` | `foundation/api/apiClient.ts`・`apiClient.test.ts`・`orvalMutator.test.ts`・`AuthProvider.tsx` → **撤去**（SPA はトークンを扱わない） |
| `setUnauthorizedHandler` | 上に加え `orvalMutator.ts`（コメント）・IADR-0033（凍結・触らない） |
| `access_token`（frontend） | `roles.ts` / `roles.test.ts` / `RequireRole.test.tsx` / `renderUnitRoute.tsx` / `Layout.test.tsx` / `AuthProvider.tsx` → 本作業で改修。`docs/tests/SC-10_operations-dashboard.md` 141 行（ダミー User の説明）→ 追随 |
| `'/callback'` | `foundation/routing/shell.tsx`・`router.test.ts` → ルート削除と追随 |
| `oidc.clientId` / `VITE_OIDC` | `runtimeConfig.{ts,test.ts}`・`public/config.js`・`deploy/helm/.../frontend.yaml`・`docker-entrypoint.d/40-render-config.sh` → **本作業では触らない**（除外。理由: `platform-spa` public client は AST submodule が撤去完了するまで realm に残る。撤去は AST 追随と同時に別作業。`oidc.authority` は SC-16 アカウントコンソール導線で**今後も使う**） |
| `platform-spa` | deploy / docs の 12 ファイル → 同上の理由で除外（`13_frontend-stack` の完了判定は AST を含む 2 段構え。planning#450 裁定） |

**除外の理由**: `src/ai-stock-trading` は別リポジトリの submodule（IADR-0120。ここから是正できない。
AST の `oidc-client-ts` は 3b 着地後に AST 側へ起票する）。`dist` / `tsbuildinfo` / `coverage` は生成物
（再ビルドで消える）。`.ai-context/` の過去 spec / 凍結 IADR は記録であり追随対象でない
（規約「本文プロズを後から書き換えない」）。`deploy/local/*/README.md`・`scripts/verify-oidc-edge-flow.sh`
等の `access_token` は**サービス間 Bearer / 検証スクリプトの文脈**で、SPA のトークン扱いではない
（IADR-0251 決定 9 が Bearer 受理を維持しているため今後も正しい記述）。

## 設計（詳細と論拠は IADR-0273。ここは何をするかだけ）

### バックエンド（失効・伝播・refresh・ロール）

1. **バックチャネルログアウト受け口の実装**: `OnRemoteSignOut` イベントで `logout_token`（JWT）を
   検証（署名 / iss / aud / exp / events / nonce 不在 / sub 必須）し、`RemoveAllForSubjectAsync(sub)`。
   端点は新設**しない**（`/bff/*` の無認証端点を増やさない。`check-bff-authz-docs` の不変条件と整合）。
2. **セッション → 下流への資格情報伝播**: 認証後ミドルウェアで、Authorization ヘッダ不在かつ
   セッション認証成功のとき、チケット保存済みアクセストークンを `Authorization: Bearer` として
   **リクエストヘッダに昇格**する。既存の全 BFF 端点の透過転送がそのまま生きる（他ユニットの
   端点モジュールを触らない）。
3. **refresh**: Cookie 認証の `OnValidatePrincipal` で期限切れ（60 秒スキュー）を検知し、
   refresh_token グラントで更新（`ShouldRenew`）。**失敗したら RejectPrincipal ＋ SignOut**（＝
   Keycloak 側の失効・無効化が refresh 拒否として**即時に** BFF セッションを殺す第 2 経路）。
4. **`offline_access` スコープを外す**: オフライントークンは **SSO セッション終了後も生き残る**ため、
   「無効化 → 即時失効」と**逆向き**。セッション連動の通常 refresh token を使う（SSO が死ねば refresh も死ぬ）。
5. **ロール**: `OnTokenValidated` でアクセストークン（BFF が TLS 直で受けたもの）の `realm_access` を
   principal へ複写 → 既存 `KeycloakRolesClaimsTransformation` が毎リクエスト展開。
6. **ログアウトを GET ＋ `sid` 検証へ**（Duende BFF と同型）: フォーム POST はカスタムヘッダを
   付けられず、fetch POST は 302 の先（Keycloak end-session）へブラウザを運べない。トップレベル
   GET ナビゲーションにし、CSRF は**セッションの `sid` クレームと一致するクエリ**で防ぐ
   （攻撃者は sid を知り得ない）。`/bff/auth/me` が `logoutUrl` を返す。

### フロントエンド（3b②③）

- `AuthState.user` を `SessionUser`（name / subject / roles / logoutUrl）へ。**`useAuth()` /
  `RequireAuth` / `RequireRole` / `useRoles` の継ぎ目は不変**。
- `AuthProvider`: `/bff/auth/me` を TanStack Query で 1 回読む（401 は「未認証」であり
  エラーでも再ログイン誘導でもない）。`login(returnTo)` = `/bff/auth/login?returnUrl=` への
  トップレベル遷移。`logout()` = `me` が返した `logoutUrl` への遷移。
- `apiClient`: **Bearer 注入と `setTokenProvider` を撤去**。CSRF ヘッダ `X-MSP-CSRF` を付与。
  401 ハンドラは維持（セッション失効中の操作 → ログインへ）。`/auth/me` だけ 401 通知を抑止する
  オプションを持つ。
- `authConfig.ts`・`CallbackPage`（＋ `/callback` ルート）を削除。`oidc-client-ts` の宣言を
  platform / knowledge から削除し lockfile を再生成（**事前に submodule の populate を確認済み**）。
- 🔴 **AST submodule 互換**: 横断 vitest は AST のテストを実 `@foundation` で走らせ、AST は
  `user = { access_token: <jwt> }` を `AuthContext` へ流し込む（`access.test.tsx` 3 件・実測）。
  AST はここから是正できないため、`extractRealmRoles` は **roles 配列を第 1 情報源**にしつつ
  **`access_token` の JWT 復号をフォールバックとして残す**。狭める条件（AST 追随後に削除）を
  IADR-0273 に書く。

### テスト（issue の退行防止 ＋ 規約の補強）

| # | 確かめること | 置き場 |
| --- | --- | --- |
| T-1 | **失効 → 次リクエストで 401**（サインイン → 200 → `RemoveAllForSubjectAsync` → 同じ Cookie が 401）。refresh 拒否 → 401 も対で | `BffSessionFlowTests`（in-proc TestServer・実 Cookie ハンドラ） |
| T-2 | **トークンがブラウザへ出ない**（`/me` 応答のヘッダ・本文にトークン文字列が無い）＋ **陽性対照**（同じ装置で `/me` 200・Cookie 発行・身元とロールが返る） | 同上 |
| T-3 | CSRF: 既存の `RequiresHeader` 群（維持）＋ ログアウト GET の sid 一致 / 不一致 / 欠落 | `BffAuthEndpointTests` ほか |
| T-4 | 寿命: 構成既定値が realm と一致（既存 `BffSessionOptions` の既定は realm 由来。維持） | 既存 |
| T-5 | E2E: 未認証 → /login 誘導の 13 本を**無改修で維持**（ログイン実走は Keycloak 不在のため**そもそも実行しない**） | 既存 e2e |
| T-6 | バックチャネル: 正しい logout_token で対象 subject の全セッションが消える（陽性）／署名・aud・events・nonce・sub の各異常で**消えない**（陰性の対） | `BackchannelLogoutTests` |
| T-7 | 伝播: Cookie セッション → 下流ヘッダに Bearer が立つ（陽性）／無セッションでは立たない・既存 Bearer は上書きしない（陰性） | `BffSessionFlowTests` |
| T-8 | SPA: リクエストに Authorization を**付けない**（陰性）＋ CSRF ヘッダを**付ける**（陽性の対） | `apiClient.test.ts` |

**変異試験**: ①バックチャネル検証の署名検証を外す ②`RemoveAllForSubjectAsync` を no-op 化
③CSRF ヘッダ付与を外す（SPA 側）等で、対応する陰性/陽性が落ちることを実測し、**戻して残渣 0** を確認する。

## 完了条件（前仕様書から引き継ぎ、本書で判定する）

- `platform` ＋ `knowledge` から `oidc-client-ts` の**宣言と import の両方が 0**（3b の完了。
  移行全体＝★不採用の完了判定は **AST を含む**ため本作業では閉じない。planning#450 裁定）
- 「無効化 → 次リクエストで 401」が T-1 で固定されている
- トークン非露出が T-2（陽性対照つき）で固定されている
- `pnpm run typecheck / lint / format:check / test / test:coverage / build`・`dotnet build / test / format`・
  検査器一式（check-knip / check-chunk-budget / check-static-egress / check-i18n-catalogs / scripts.test.js）緑

## スコープ外（残件として報告する）

- **AST submodule の oidc-client-ts**（別リポ作業。3b 着地後に起票）と、その完了を待つ
  `platform-spa` public client・`oidc.clientId` 実行時 config の撤去
- `verify-oidc-edge-flow.sh` の Cookie 方式化（IADR-0251 決定 9 の狭める条件 1）
- 実 Keycloak / 実 Redis / 複数レプリカでの疎通（この環境では**そもそも実行していない**。
  統合スタック（CI）と #972 の再実行に委ねる）
- planning#445 のディレクトリ再編（`foundation/` 分解）は本作業の射程外（前仕様書の判断を維持）

---

## ［2026-08-23 実施結果］

### 受け入れ基準の充足

| 基準 | 結果 |
| --- | --- |
| `platform`＋`knowledge` から宣言と import の両方が 0 | **達成**（宣言 0・import 0。lockfile 差分は 2 importer の削除のみ。AST submodule の宣言は残る＝計画どおり別作業） |
| 無効化 → 次リクエストで 401（T-1） | **達成**（`BffSessionFlowTests` / `BackchannelLogoutTests`。バックチャネルと refresh 拒否の 2 経路） |
| トークン非露出（T-2・陽性対照つき） | **達成**（応答の本文・ヘッダにトークン値が現れない ＋ 同装置で /me 200・身元・ロール・logoutUrl） |
| CSRF（T-3） | **達成**（既存の RequiresHeader 群 ＋ ログアウト GET の sid 一致／不一致／欠落／未認証） |
| 寿命 = realm 由来（T-4） | 既存の構成（`SessionLifetimeSeconds` 既定）を維持。数値の散文複写なし |
| E2E 13 本（T-5） | **13/13 緑**（実走した）。ただし「無改修」の見込みは 1 ファイル外れた —— `bundle-splitting` は「4xx/5xx ゼロ」を全応答に課しており、起動時の `/bff/auth/me`（プレビューに BFF が無い）で落ちた。**検査対象をバンドル成果物に限定**（`/bff/*` を除外）して復旧 |
| ログイン・バックチャネルの実疎通 | **そもそも実行していない**（実 Keycloak・実 Redis・複数レプリカがこの環境に無い）。統合スタック（CI）と #972 再実行に委ねる |

### 変異試験（すべて戻して残渣 0 を確認）

IADR-0273 §テストと変異試験 の表が正本（6 変異・落ちた件数つき）。ここへ複写しない。

### 検証コマンド（全部実行・全部緑。例外は明記)

`pnpm install --frozen-lockfile` / `typecheck` / `lint`（0 error・9 warning は既存） / `format:check` /
`test`（1008 件・86 ファイル） / `test:coverage`（98.02 / 91.51 / 94.14。床を 93 / 93 / 89 / 87 へ引き上げ） /
`build` / `playwright test`（13/13） / `dotnet build backend.slnx` / `dotnet test Platform.Bff.Tests`（323 passed） /
`dotnet format --verify-no-changes` / `check-knip --require`（床 38 へ締め） / `check-chunk-budget --require`
（床 575,417 bytes へ締め） / `check-static-egress --require` / `check-i18n-catalogs` /
`REQUIRE_REPO_TESTS=1 scripts.test.js`（609） / check-* 全走査（**例外 2 件**: `check-deploy-manifests` と
`check-stack-ready` は helm / kubectl 不在の環境要因で、**変更前のクリーンツリーでも同じ失敗**を確認済み）。

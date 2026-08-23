---
title: IADR-0273 BFF セッション移行の完了 — バックチャネル失効・refresh 失敗即失効・トークン昇格・GET+sid ログアウト・SPA のトークン非関与
type: impl-adr
status: Accepted
related_ids: [NFR, SC-16, ADR-0026, ADR-0031, ADR-0032, IADR-0033, IADR-0121, IADR-0251]
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - ../specs/20260823_issue-439_bff-session-completion.md
  - ../specs/20260822_issue-439_bff-session-token-handler.md
---

# IADR-0273: BFF セッション移行の完了（#439 第 3 段 3b②③④ ＋ 失効経路）

> 実装リポジトリ内の意思決定記録。[IADR-0251](./IADR-0251_bff-session-token-handler.md)（3a の内部設計）を
> 引き継ぎ、**「無効化 → 次リクエストで 401」を実際に成立させるための残りの決定**を記録する。
> 運用手順は [`docs/authz/bff-session-design.md`](../../docs/authz/bff-session-design.md) が持ち、本 IADR は論拠を持つ。

- 状態: Accepted
- 日付: 2026-08-23
- 決定者: Claude（実装）

## 前提の実測（着地済みの受け皿だけでは NFR が成立しない）

3a / 3b① 着地時点で、次の 4 点が**実測で欠けていた**（詳細は作業仕様書 §着手前の実測）。

1. `RemoveAllForSubjectAsync` を呼ぶ製品コードが **0 件** —— 「全セッション即時失効」の入口が無い
2. バックチャネルログアウトは `RemoteSignOutPath` の配線のみ。**フレームワーク既定の処理は
   「リクエストが運ぶ Cookie のセッション」しか消せず、Cookie を運ばないサーバ間 POST では何も失効しない**
3. Cookie セッションのリクエストは Authorization ヘッダを持たず、**下流転送（ヘッダ透過方式）が
   全端点で資格情報欠落**になる（#948 と同型）
4. refresh 経路が無く、**アクセストークンの寿命（realm 実測 300 秒）を超えるとセッションが実質死ぬ**。
   さらに realm の `roles` スコープは realm_access を**アクセストークンにしか入れない**ため、
   Cookie 経路の principal に**ロールが載らない**

## 決定 1: バックチャネルログアウトは `OnRemoteSignOut` イベントで処理し、端点を新設しない

`/bff/*` に無認証端点を増やさない（`check-bff-authz-docs` の不変条件。IADR-0251 決定 8 は
「認証チャレンジのみ」の例外しか認めていない）。バックチャネル受け口は本質的に無認証・非チャレンジ
なので、**端点として作ると不変条件と衝突する**。OIDC ハンドラが `RemoteSignOutPath` で
インターセプトする既存の口（`/bff/auth/backchannel-logout`）に `OnRemoteSignOut` で実装を挿す ——
検査器の側を緩めず、認証ハンドラの領分で処理する。

`logout_token` は OIDC Back-Channel Logout 1.0 §2.6 のとおり検証する:
**署名（metadata の鍵）・iss・aud（クライアント ID）・exp 必須・`events` にログアウトイベントが
プロパティ名として存在・`nonce` の不在（ID トークンすり替え防止）・`sub` の存在**。
`events` の判定は文字列包含ではなく **JSON プロパティ名**で行う（値に URL が現れるだけの JSON を通さない）。

## 決定 2: 失効の単位は subject（その利用者の全セッション）。過剰失効側へ倒す

logout_token は `sid`（個別セッション）を運ぶが、チケットストアの索引（IADR-0251 決定 4）は
subject 単位である。sid 索引を増やす案は捨てた —— NFR の要求（無効化・退職時の**全**セッション失効）
には subject 単位で足り、誤る方向が「必要より多く失効させる」＝安全側だからである。
`sub` を持たない token は失効対象を解決できないので**受理しない**（fail-closed）。

## 決定 3: refresh は Cookie 認証の `OnValidatePrincipal` で行い、**拒否＝セッションの死**とする。`offline_access` は要求しない

- 毎認証時に `expires_at`（60 秒スキュー）を見て、期限内なら何もしない（毎リクエストの
  token endpoint 問い合わせにしない —— ADR-0032 が禁じた「毎リクエストのイントロスペクション」と
  同じコスト構造になるため）。
- 期限切れは refresh_token グラントで更新し `ShouldRenew`（→ Redis のチケットが更新される）。
- 🔴 **refresh の失敗は RejectPrincipal ＋ SignOut（チケット削除）で即 401 にする。**
  認可サーバの無効化・全セッション失効・パスワードリセットは refresh 拒否として現れる ——
  **バックチャネルが届かなかった場合の第 2 の即時失効経路**である。ネットワーク断など一過性の
  失敗も同じ扱い（fail-closed）。トレードオフ: 認可サーバ不調時に利用者がログアウトされる。
  「失効が遅れる」より「再ログインを求める」を採る（NFR の向きと一致）。
- 🔴 **3a が要求していた `offline_access` スコープを外した（是正）。** オフライントークンは
  **SSO セッションが終了しても生き残る** —— 管理者が Keycloak でセッションを失効させても
  refresh が成功し続け、「無効化 → 即時失効」と**逆向き**になる。コードフローのコンフィデンシャル
  クライアントには `offline_access` 無しでもセッション連動の refresh token が発行され（Keycloak の
  既定）、それは SSO セッションと同時に死ぬ。`BffSessionConfigurationTests` がスコープ集合を固定する。

## 決定 4: セッションのアクセストークンは、**受信リクエストの Authorization ヘッダへ昇格**して下流へ運ぶ

BFF の全端点（knowledge / AST のモジュール含む）は `Request.Headers.Authorization` を下流へ透過する
方式で書かれている。端点ごとにチケットからトークンを引く改修は他ユニットのモジュールへ波及するので
採らず、**ミドルウェア 1 つ**（`SessionTokenPropagationMiddleware`）で受信リクエストのヘッダに
昇格させる —— 下流転送の契約は 1 行も変えない。

- 既に Authorization を運ぶ呼び出し（サービス間 Bearer）は**上書きしない**
- CSRF 検査（`CsrfHeaderMiddleware`）の**後**に置く —— 拒否されるリクエストにトークンを付けない
- 書き換えるのは**受信リクエスト**であり、応答には決して載らない（テストが否定形＋陽性対照で固定）

## 決定 5: Cookie セッションのロールは、コード交換で受けたアクセストークンの `realm_access` を principal へ複写して得る

realm の `roles` クライアントスコープは既定で realm_access を**アクセストークンにだけ**入れる。
realm 側に「id_token へも入れる」マッパーを足す案は捨てた —— 配備構成への依存が 1 つ増え、
realm を触れない環境（既存デプロイ）で静かに壊れる。代わりに `OnTokenValidated` で、
**BFF が token endpoint から TLS 直で受け取ったアクセストークン**（改竄の余地が無い経路）の
`realm_access` クレームを principal へ複写する。展開（`ClaimTypes.Role` 化）は既存の
`KeycloakRolesClaimsTransformation` が毎リクエスト行う —— ロール写像のロジックを二重に持たない。

## 決定 6: ログアウトは GET ＋ `sid` 一致検証（Duende BFF と同型）。POST をやめた

3a の POST ログアウトは **SPA から完遂できない**（実測に基づく判断）:
フォーム POST はカスタムヘッダ（CSRF の 2 枚目の壁）を付けられず、`fetch` の POST は
302 の先（認可サーバの end-session）へ**ブラウザを運べない** —— 認可サーバ側のセッションが残り、
「ログアウトしたのに次のログインが素通し」になる。

- **GET のトップレベルナビゲーション**にする。CSRF（強制ログアウト）は
  **セッションの `sid` クレームと一致するクエリ**で防ぐ —— sid は HttpOnly セッションの中にしか無く、
  攻撃者のページは知り得ない。配り口は `/bff/auth/me` の `logoutUrl` ただ 1 つ。
- sid を持たないセッションは照合不能＝**拒否**（fail-closed。Keycloak は常に sid を発行する。
  `logoutUrl` も配らない —— 拒否される URL を配らない対称）。
- 捨てた代替案: ①「JSON で end-session URL を返し SPA が遷移する」—— OIDC ハンドラの signout
  URL 構築（id_token_hint 等）を手で複製することになり可動部が増える。② POST 維持 ＋ 302 手動追跡
  —— fetch からは Location が読めない（opaque redirect）。

## 決定 7: SPA の `AuthState.user` は `/bff/auth/me` の身元（`SessionUser`）。AST 互換の JWT 復号フォールバックを **期限つきで** 残す

- `useAuth()` / `RequireAuth` / `RequireRole` / `useRoles` の**継ぎ目は不変**（消費側 15 ファイルは無改修。
  実測どおり）。ロールの一次情報は `roles` 配列になった。
- `apiClient` から **Bearer 注入と `setTokenProvider` を撤去**した。SPA はトークンを持たないので
  供給元の口ごと消す（「付けない」を規約でなく**構造**にする）。全リクエストに CSRF ヘッダを付ける。
  `/auth/me` だけ `on401: 'silent'`（401 は「未認証」という正常な答えであり再ログイン誘導ではない）。
- 🔴 **`extractRealmRoles` は `access_token` の JWT 復号をフォールバックとして残す。**
  横断 vitest は `ai-stock-trading` submodule のテストを実 `@foundation` で走らせ、AST の
  `access.test.tsx` 3 件は旧形（`{ access_token: <jwt> }`）の値を `AuthContext` へ流し込む（実測）。
  AST は本リポジトリから是正できない（IADR-0120）。**優先順位は roles 配列が上**で、
  roles を持つ身元ではトークンを読まない（テストが固定。逆転すると「/me が空ロールでも
  JWT で権限が付く」形になる）。**狭める条件: AST 側が SessionUser 形へ追随したら、
  フォールバックと `SessionUser.access_token` フィールドごと削除する。**

## テストと変異試験（検出力の実測。すべて元へ戻して残渣 0 を確認済み）

通し（`BffSessionFlowTests`）は**本物の Cookie ハンドラ・本物のチケットストア**で測る
（差し替えたのは I/O の器のみ: Redis→メモリ・鍵リング→プロセス内・token endpoint→スタブ・
OIDC メタデータ→静的構成）。`BffTestFactory` の `Test` スキームは迂回経路なので使っていない。

| 変異 | 落ちたテスト | 戻し確認 |
| --- | --- | --- |
| logout_token の検証を素通し | **陰性 5 件**（署名・iss・aud・events・nonce）が落ち、陽性 9 件は緑のまま | 残渣 0 |
| `RemoveAllForSubjectAsync` を no-op 化 | **6 件**（ストア 4 ＋ 失効→次リクエスト 401 ＋ バックチャネル陽性） | 残渣 0 |
| ログアウトの sid 照合を外す | **陰性 2 件**（sid 不一致・欠落） | 残渣 0 |
| refresh 拒否を fail-open 化 | **1 件**（拒否→即 401） | 残渣 0 |
| SPA が Bearer を付け CSRF を外す | **3 件**（apiClient 2 ＋ orvalMutator 1） | 残渣 0 |
| ロールの優先順位を JWT 側へ逆転 | **1 件**（roles 配列優先） | 残渣 0 |

## 結果

- 良い影響: 「無効化 → **次の**リクエストで 401」が 2 経路（バックチャネル・refresh 拒否）で成立し、
  テストで固定された。`oidc-client-ts` が platform / knowledge から消え、SPA の認証コードは
  「/me を読む・遷移する」だけになった（732 行 → 約 300 行、うち認証ロジック 0）。
- トレードオフ: 認可サーバ不調時に refresh 失敗でログアウトされる（決定 3）。ログアウトが
  GET になり HTTP セマンティクスから外れる（sid 検証で代償。決定 6）。AST 互換フォールバックという
  **期限つきの負債**が roles.ts に残る（決定 7 の狭める条件）。
- フォローアップ: AST 側の `oidc-client-ts` 撤去の起票（3b 着地後。planning#450 裁定の 2 段構え）。
  その完了時に `platform-spa` public client・実行時 config の `oidc.clientId`・決定 7 の
  フォールバックを撤去する。`scripts/verify-oidc-edge-flow.sh` の Cookie 方式化は
  IADR-0251 決定 9 の狭める条件 1 のまま残る。

## 関連

- 継承: [IADR-0251](./IADR-0251_bff-session-token-handler.md)（3a の内部設計。決定 9 の振り分けスキームと
  狭める条件は**そのまま有効**）
- Supersedes: IADR-0251 の `offline_access` 要求（決定 3 で是正）
- [IADR-0033](./IADR-0033_frontend-spa-foundation.md) 決定 4 の追補欄は「置換済み」へ更新した（新規 IADR 不要の 1 行更新。前仕様書の判断どおり）

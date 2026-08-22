---
title: SPA 認証を BFF セッション方式（Token Handler）へ移行し oidc-client-ts を撤去する — 第 3 段
type: spec
status: draft
related_ids: [NFR, SC-13, SC-14, SC-15, SC-16, ADR-0026, ADR-0031, ADR-0032, IADR-0033, IADR-0121]
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# 仕様書: BFF セッション認証への移行と `oidc-client-ts` の撤去（#439 / 第 3 段）

> 本書は**着手前**に作成した。

## 起点となる計画書（トレーサビリティ）

- **非機能要件**: セッション管理（「記憶」30 日 / 無効化・退職時の**全セッション即時失効**）
- **ADR-0032**（`spa-auth-bff-session`・**Accepted**）——**追補まで本体と同じ重みで読む**
- **ADR-0031**（フロントエンドスタック・追補あり）／**ADR-0026**（認証 UX。SC-13〜16 は Keycloak テーマ側）
- **`06_technical/13_frontend-stack.md`**（**status: fixed**）§採用技術一覧・§実装への移行方針
- 実装側: **IADR-0121 決定 6**（`oidc-client-ts` は第 1 段で撤去せず**第 3 段で撤去**）／
  `#446` 仕様書 §段階分割（段の表の正本。**第 3 段＝「認証: BFF セッション方式へ移行し `oidc-client-ts` を撤去」**）

### 完了条件の正本

**`13_frontend-stack` §採用技術一覧そのもの**である。`10_feedback/20260804_frontend-migration-staging-interpretation.md` は
経緯の記録であり、自ら一次情報源の座を計画本文へ譲っている。該当行:

| 分野 | 採用技術 | 採否 |
| --- | --- | --- |
| 認証 | `oidc-client-ts` | **★不採用**（BFF セッション方式により不要） |

## 着手可否 —— ゲートは実在しない（実測で再確認した）

| 候補 | 実測 | 判定 |
| --- | --- | --- |
| バックエンドの受け皿 | `AddCookie` / `AddOpenIdConnect` / `AddSession` / `IDistributedCache` / `Response.Cookies` / `Antiforgery` / `/bff/auth*` が **ripgrep で全部 0 件** | **純 greenfield。巻き戻す対象ゼロ** |
| Redis | `deploy/docker-compose.yml` と `deploy/local/infra/redis.yaml` に配備済み | **配備済み** |
| Keycloak realm | `deploy/keycloak/microservices-platform-realm.json` が本リポジトリ内 | **編集可能** |
| #442 / #446 | #442 は #439 を塞いでいない。#446 は第 1・2 段とも着地（PR #489 / #495 / #499） | **開いている** |

### 🔴 「純 greenfield」の補正（実測）

- **`Microsoft.AspNetCore.DataProtection` 10.0.10 は既に宣言済み**である。Cookie 認証チケットの保護に効くため、
  **BFF を複数レプリカで動かすなら鍵リングの共有先が本作業の設計事項**になる。「巻き戻す対象ゼロ」ではあるが「白紙」ではない
- **Redis クライアント（`Microsoft.Extensions.Caching.StackExchangeRedis`）は未宣言**である
  （在るのは `AspNetCore.HealthChecks.Redis`＝ヘルスチェック用と `Testcontainers.Redis`＝テスト用）。**新規追加になる**
- 現行の認証は `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.9 一本である

## 直列化

| 相手 | 実測 | 扱い |
| --- | --- | --- |
| **#780**（OPEN） | `deploy/keycloak/*-realm.json` の **7 OIDC クライアント**の redirect / `post.logout.redirect.uris`（`##` 連結）/ webOrigins を編集する。**本作業も同じファイルの `bff` クライアントを編集する** | 🔴 **#780 を先行させる。** issuer host が動くと本作業の redirect 設計をやり直すことになる |
| **#948** | BFF の認可配線に触れる可能性を実測した。**根因は BFF → FeedbackService の資格情報欠落**であり、**認証スキームに依存しない**。PR #966 で先行して解消 | **解消済み。本作業へ持ち込まない** |

## planning#445（`foundation/` vs `app/`）の扱い —— **裁定を待たない。改名しない**

計画本文（`13_frontend-stack` §ディレクトリ構成・`status: fixed`）は `app/` を列挙し、実装は `foundation/` を使う。
この論点は planning#445 で裁定待ちである（実装側から材料を提出済み）。**本作業はこれを待たない。**

**待たなくてよい根拠は実測である。**

- `foundation/` 内部の相対 import **130 本**は、ディレクトリを改名しても 1 行も変わらない
- 外部の消費者は全て `@foundation/*` 別名経由である（`@foundation` を含むファイル **143**・出現 **378**）
- **ディレクトリ名そのものを束縛しているのは 10 ファイル・17 行だけ**である
- したがって計画側の「**`foundation/auth/` を二度書き換えない**」という意図は、**本作業を止めなくても満たせる**。
  「二度目」は `git mv` ＋ 宣言の数行であって、認証コードの書き換えではない
- さらに **`foundation/auth/` 732 行のうち 443 行（15 ファイル中 8 ファイル）が `oidc-client-ts` 依存**であり、
  **本作業がそこを消す。**温存される資産はもともと小さい

🔴 **本作業で `foundation/` → `app/` の改名は行わない。** 理由は 3 つ。
① 裁定の先取りになる ② 名前だけ合わせても計画ツリーへの適合にならない（`api`/`auth`/`ui`/`testing` が `app/` に入り、
計画ツリー自身の `lib/`/`components/`/`testing/` と矛盾する）③ 改名は `ai-stock-trading` submodule に波及し、
**本リポジトリからは是正できない**（IADR-0120）。

### 本作業が守る制約（これだけで案 3 の「名前に依存しない形」は満たされる）

1. **`foundation/` の外から相対パスで `foundation/` を参照しない**（`@foundation/*` 経由のみ）
2. **ディレクトリ名を束縛する設定を新たに増やさない**（現在の 10 ファイルを超えない）

## 着手前に決めること —— ADR-0032 §フォローアップの 4 点（＋1）

**これはゲートではなく作業の 1 時間目である。**新規 IADR に記録する（番号は**マージ直前に develop の最大＋1 を実測で取り直す**）。

| # | 論点 | 現時点の材料（実測） |
| --- | --- | --- |
| 1 | **Cookie 名・属性** | ADR-0032 §決定 が `HttpOnly` / `Secure` / `SameSite=Lax` を指定済み。名前とプレフィックス（`__Host-` の可否）は未決 |
| 2 | **有効期間と「記憶」30 日の対応** | 🔴 **答えは realm に在る。** `rememberMe=true` / `ssoSessionIdleTimeoutRememberMe=2592000` / `ssoSessionMaxLifespanRememberMe=2592000`＝**きっかり 30 日**。BFF セッションの寿命をこれに合わせる |
| 3 | **CSRF 方式（2 択）** | ADR-0032 は「トークン**または** SameSite ＋ カスタムヘッダ検証」と両論併記。`Antiforgery` は現在 0 件＝どちらでも新規実装 |
| 4 | **リフレッシュ戦略** | `accessTokenLifespan=300`（**5 分**）。ADR-0032 追補の暫定措置は「10 分」と書いているが、**realm の実測値は 5 分でより厳しい**。移行後は 10 分の制限を引き継がない（追補の明文） |
| 5 | **（追加）DataProtection 鍵リングの共有先** | 上記の補正による。BFF が複数レプリカなら Redis か PVC。**ADR-0032 に無い論点なので実装側で決めて IADR に残す**（実装に閉じた判断であり IADR の射程内） |

> 🔴 **5 の欠陥は単体テストでは絶対に捕まらない。** チケット保護鍵がレプリカ間で共有されないと、
> **利用者のリクエストが別レプリカへ振られた瞬間にセッション Cookie を復号できず、ログアウトしたように見える。**
> 単一プロセスで動く `WebApplicationFactory` 系のテストは鍵リングが 1 つしか無いため、**共有し忘れていても緑になる。**
> したがって検証は **(a) 2 レプリカ以上を起こす統合テスト**（#783 / IADR-0248 が CI へ入れた統合スタックの上）か、
> **(b) 鍵リングの永続化先が設定されていることを構成の側から固定する検査**のどちらかで行う。**どちらも置かずに
> 「テストが緑だから大丈夫」と読まないこと。**

## 実装範囲

### バックエンド（純増）

- BFF を Keycloak の**コンフィデンシャルクライアント**にする。🔴 **realm の `bff` クライアントは現在 `publicClient: true`**
  であり、`redirectUris` も `http://localhost:5000/*` のみ。**secret の付与と URI の実値化が要る**（#780 の後）
- `/bff/auth/login` / `/bff/auth/callback` / `/bff/auth/logout` / `/bff/auth/me`（名称は IADR で確定）
- Redis セッションストア。**全セッション即時失効**をストア側の削除で実現する（非機能要件の本丸）
- Keycloak back-channel logout の受け口
- CSRF 対策（上記 3 の決定に従う）

### フロントエンド（**正味削除**）

- `foundation/auth/` から `oidc-client-ts` 依存の 8 ファイル（443 行）を撤去し、
  `useAuth()` / `RequireAuth` / `RequireRole` の**継ぎ目を保ったまま**セッション方式へ置換する
- `knowledge/frontend/package.json` の `oidc-client-ts` 宣言は**真の未使用**である（knip の床が記録済み）。同時に落とす
- `platform/frontend/package.json` の宣言も落とす

**継ぎ目を保てば消費側 18 ファイルの多くは無改修**という見込みは、着手時に実測で確かめる。

## テスト

issue #439 が要求する退行防止に、**規約上の補強を 2 つ足す。**

| # | 確かめること | 備考 |
| --- | --- | --- |
| T-1 | 無効化 → **次リクエストで 401/403**（最大 10 分遅延へ退行させない） | 非機能要件の本丸 |
| T-2 | **トークンがブラウザ側へ露出しない**（レスポンスヘッダ・ボディの否定形） | 🔴 **陽性対照を対で置く。** 否定形だけだと「常に 404 を返す実装」が通る。**「セッション Cookie は在る」「`/bff/auth/me` は 200 で身元を返す」を同じ装置で確かめる** |
| T-3 | CSRF 方式が効く（正しいトークン/ヘッダで通り、欠落で拒否される） | 同上。**通る側と拒否される側を対で置く** |
| T-4 | 「記憶」30 日の寿命が realm の値と一致する | 数値を散文へ複写せず realm から読む |
| T-5 | E2E（Playwright）: ログイン → 利用 → 管理者による無効化 → 即時失効 | issue の要求 |

**スタブが常に成功を返す作りにしない。**#948 で「スタブが Authorization を記録するだけで検査しなかったため、
転送の欠落が緑を通った」実例が出たばかりである。**認証スタブは実体の契約を模す。**

## 規模と分割 —— 🔴 **上限を超えるので 2 つに割る**

見込みは **約 45〜55 ファイル**（バックエンド新規 10〜14 ＋ realm ＋ テスト、フロントは正味削除）。
**規模上限（目安 約 50 ファイル / +2500 行）に対して境界上**であり、**分割する（2026-08-22 承認済み）。本 PR は 3a である。**

| PR | 内容 | 単独で緑になるか |
| --- | --- | --- |
| **3a** | **BFF 側の受け皿**: OIDC コンフィデンシャルクライアント・`/bff/auth/*`・Redis セッション・CSRF・back-channel logout・realm の `bff` クライアント。**SPA は触らない**（現行 PKCE 経路を温存） | ○（後段テストで固定。SPA からは未使用） |
| **3b** | **SPA の切り替えと撤去**: `foundation/auth` をセッション方式へ置換し `oidc-client-ts` をワークスペースから削除。E2E | ○（3a が在って初めて成立） |

**この継ぎ目は #446 仕様書の理由と同じ**である ——「**先に撤去すると SPA がログインできなくなる**」。
受け皿を先に作るのは、その理由の裏返しである。

## 完了条件

- `13_frontend-stack` §採用技術一覧 と実装が一致する。とくに **`oidc-client-ts` がワークスペースから消えている**
- 非機能要件「全セッション即時失効」が T-1 で固定されている
- **`/bff/dashboard/summary` が有効なトークン（移行後はセッション）で 200 を返す**
  —— #948 の受け入れ基準を本作業へ引き継ぐ。**#948 は PR #966 で別途解消済みだが、
  認証経路を差し替える本作業が同じ症状を新経路へ持ち込んでいないことを、ここで再度固定する**
- CI 緑

## スコープ外・未解明として残すこと

- 🔴 **`ai-stock-trading` submodule の `oidc-client-ts`。** 同 submodule は `package.json` 宣言・
  `test/foundation-stub/auth/` 2 ファイル・`e2e/harness/AuthHarness.tsx`・feature の access テスト 3 ファイルで
  `oidc-client-ts` を使う。`pnpm-workspace.yaml` の `*/frontend` により **AST も同一ワークスペースの一員**である。
  **「ワークスペースから消えている」を字義どおり採ると本作業だけでは達成できない**（submodule は本リポジトリから
  是正できない。IADR-0120）。**確認を計画側へ出した ―― planning#450（`decision-needed`）。本件は 3a を止めない。効くのは 3b の完了判定時点である**
- **MCP サーバ（OAuth 2.1・ADR-0024）の認証はブラウザセッションと別系統**である旨を設計で明示する（ADR-0032 §フォローアップ）
- **planning#445 の裁定**（本作業は待たない。裁定後の移動は `git mv` ＋ 10 ファイル）

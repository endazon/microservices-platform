---
title: SPA 認証を BFF セッション方式（Token Handler）へ移行し oidc-client-ts を撤去する — 第 3 段
type: spec
status: draft
related_ids: [NFR, SC-16, ADR-0026, ADR-0031, ADR-0032, IADR-0033, IADR-0121]
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

### 3b（SPA の切り替えと `oidc-client-ts` 撤去）の実測と受け入れ基準

**3a は `6aeba042` で着地した。** 本節は 3b の着手前に実測した値である。

### 撤去対象（実測）

| 対象 | 実測 | 備考 |
| --- | --- | --- |
| `oidc-client-ts` の**宣言** | **2**（`platform/frontend/package.json` / `knowledge/frontend/package.json`） | knowledge 側は knip が「真の未使用」と記録しているもの（import 0 件） |
| `oidc-client-ts` の **import** | **10 ファイル**（すべて `platform/frontend/src/foundation/` 配下。`auth/` 8・`testing/` 1・`ui/` 1） | |

🔴 **受け入れ基準は「宣言と import の両方が 0」である。** 片方だけでは完了条件（「ワークスペースから消えている」）を満たさない。

### 🔴 見込みが外れた —— 消費側は 18 ではなく 28 ファイル

起草時の見込みは「`useAuth()` / `RequireRole` の継ぎ目を保てば**消費側 18 ファイル**の多くは無改修」だった。**実測は 28 ファイルである**（`foundation/auth/` 自身の 10 を除く）。

| 群 | 実測 | 無改修で済むか |
| --- | --- | --- |
| `platform/frontend/e2e/*.smoke.spec.ts` | **13** | 🔴 **済まない見込み。** ログイン導線そのものが変わる（`oidc-client-ts` のリダイレクト → `/bff/auth/login`）。**見込みはここを数え落としていた** |
| `knowledge/frontend` の features | 7 | 継ぎ目を保てば無改修の見込み |
| `platform/frontend/src`（`auth/` 以外） | 8 | 同上 |

**「継ぎ目を保てば多くは無改修」は `src` 配下（15）には当てはまるが、e2e（13）には当てはまらない。**

> 1 件注記: 一致に `foundation/api/generated/documents/documents.ts` が含まれるが**生成物**である。手で編集せず `pnpm run codegen` で追随させる（3a の CI 赤で学んだ形）。

### 受け入れ基準の射程 —— **AST は含めない**

🔴 `ai-stock-trading` は submodule であり、**本リポジトリからは是正できない**（`IADR-0120`）。同 submodule も `oidc-client-ts` を持つ（`package.json` 宣言・`test/foundation-stub/auth/` 2 本・`e2e/harness/AuthHarness.tsx`・feature の access テスト 3 本）。

**3b の受け入れ基準は「`platform` ＋ `knowledge` から消えている」に限定する。** 完了条件が AST を含むかは **planning#450** で裁定待ちであり、**解釈を実装側で決めない。**

### テスト装置についての注意

🔴 **`TestAuthHandler` を触るなら、変更後の装置が実体の契約より甘くなっていないかを確かめる。**

#948 で **`FeedbackStubHandler` が `Authorization` を記録するだけで検査せず、常に成功を返していた**ために、BFF の資格情報転送の欠落が緑を通った実例がある。**認証まわりのテスト装置は実体より甘くなりやすい。**

確かめ方は**変異試験**とする —— **実体が拒否する入力を装置が通してしまわないこと**を、負例で落として確認する。

### 否定形テストには陽性対照を対で置く

「**トークンがブラウザへ露出しない**」を否定形だけで書くと、**常に 404 を返す実装**が通る。同じ装置で「**セッション Cookie は在る**」「**`/bff/auth/me` は 200 で身元を返す**」を対で固定する。

## 完了条件の正本

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

### 🔴 改名しないのは横着ではない —— 計画が 3 箇所で禁じている「二度書き換え」を避ける形である

**「`foundation/auth/` を二度書き換えない」は 3 箇所に書かれている** —— `13_frontend-stack` §実装への移行方針
の認証行、`ADR-0031` 追補、`ADR-0032` §go-live の前提条件。**趣旨は「フロントのスタック移行」と
「BFF セッション認証への移行」を別々の作業にするな**という指示である（別々にやると 2 回書き換わる）。

**本作業の 3a / 3b 分割はこれと整合する** —— 3b で SPA 側を切り替えて `oidc-client-ts` を撤去するので、
**`foundation/auth/` の書き換えは 1 回である**。
🔴 **ただし 3a と 3b の間に他の作業が `foundation/auth/` を触らないこと。** 触ると分割が二度書き換えに化ける。

**そして planning#445 と正面から衝突する。** `foundation/` → `app/` の改名案が採られると
**`foundation/auth/` はそもそも消える。** 裁定が本作業より遅れると、**認証移行で 1 回・改名で 1 回の
計 2 回**書き換えることになる ―― **計画が 3 箇所で禁じているまさにその形**である。

**したがって「改名しない」は、計画の禁止を守る唯一の形として正当化される。** 横着ではない。

🔴 **本作業で `foundation/` → `app/` の改名は行わない。** 理由は 3 つ。
① 裁定の先取りになる ② 名前だけ合わせても計画ツリーへの適合にならない（`api`/`auth`/`ui`/`testing` が `app/` に入り、
計画ツリー自身の `lib/`/`components/`/`testing/` と矛盾する）③ 改名は `ai-stock-trading` submodule に波及し、
**本リポジトリからは是正できない**（IADR-0120）。

### 本作業が守る制約（これだけで案 3 の「名前に依存しない形」は満たされる）

1. **`foundation/` の外から相対パスで `foundation/` を参照しない**（`@foundation/*` 経由のみ）
2. **ディレクトリ名を束縛する設定を新たに増やさない**（現在の 10 ファイルを超えない）

## 着手前に決めること —— **計画は実装側へ委譲済みである（裁定待ちではない）**

ADR-0032 §結果 のフォローアップは**箇条書き 1 つ**で、原文はこうである。

> **BFF のセッション設計（Cookie 属性・有効期間・「記憶」30 日要件との対応・CSRF 方式）を実装ガイドへ落とす。**
> MCP サーバー（OAuth 2.1、ADR-0024）の認証はブラウザセッションと別系統であることを設計で明確化する

🔴 **「実装ガイドへ落とす」＝計画へ問う対象ではない。実装側で決めてよい。**
先例は `docs/tech/composable-component-guide.md`（実在）。よって成果物は **`docs/tech/` の実装ガイド**であり、
決定の論拠は IADR に残す（**両者で同じ値を複写しない**。ガイドは運用手順、IADR は論拠）。

**項目は 4 つである。**

| # | 論点 | 材料（実測） |
| --- | --- | --- |
| 1 | **Cookie 属性** | ADR-0032 §決定 が `HttpOnly` / `Secure` / `SameSite=Lax` を指定済み。残るのは名前・プレフィックス（`__Host-`）・`Path` |
| 2 | **有効期間** | realm の実測値に従う。散文へ複写しない |
| 3 | **「記憶」30 日要件との対応** | 🔴 **答えは realm に在る。** `rememberMe=true` / `ssoSessionIdleTimeoutRememberMe=2592000` / `ssoSessionMaxLifespanRememberMe=2592000` ＝ **きっかり 30 日** |
| 4 | **CSRF 方式** | **2 択の出典は §決定 であってフォローアップではない** ——「トークン**または** SameSite + カスタムヘッダ検証」。決定は下記 |

> **［起案時の誤り・訂正］** 本書の初稿は 5 番目に「リフレッシュ戦略」を挙げていた。**ADR-0032 に
> その項目は無い。** 全文で「リフレッシュ」が現れるのは §暫定措置 の表「リフレッシュトークンの revoke」と
> §検討した選択肢 の説明文だけで、**決めるべき項目としては挙がっていない**（実測で確認）。**削除した。**

### CSRF 方式の決定 —— **SameSite=Lax ＋ カスタムヘッダ検証を採る**

**実測 2 件で成立を確かめた。**

- **SPA と BFF は同一オリジンである。** 本番（`edge.yaml` が `/bff` を bff-service へ・catch-all を frontend へ）、
  ローカル（`platform-frontend-ingress.yaml` が同契約）、開発（`vite.config.ts` の `proxy: { '/bff': ... }`）のいずれも
- **BFF に CORS ポリシーが 1 つも無い**（`AddCors` / `UseCors` / `WithOrigins` / `AllowAnyOrigin` が **0 件**）

壁が 2 枚あり、片方が破れても残る。

| 攻撃 | 何が止めるか |
| --- | --- |
| クロスサイトの `fetch`（ヘッダ無し）・`<form>` POST | **SameSite=Lax** —— Cookie が付かない。到達しても未認証で 401 |
| クロスサイトの カスタムヘッダ付き `fetch` | **preflight** —— 非単純リクエストになり、**CORS ポリシーが無いのでブラウザが遮断** |

トークン方式を採らない理由は依存ではない（`Microsoft.AspNetCore.Antiforgery` は共有フレームワーク側）。
**可動部**である —— 発行口・セッション更新時の再取得・寿命の同期が増え、**同期する状態が 2 つ目の真実になる。**
SameSite ＋ ヘッダ方式なら SPA 側は `foundation/api/orvalMutator.ts`（**既に唯一の出口**）に 1 行で足りる。

🔴 **再検討のトリガを IADR に明記する** —— ①BFF に CORS ポリシーを入れる必要が生じたとき（壁が 1 枚に減る）
②SPA と BFF を別オリジンにする決定が出たとき。**どちらかが起きたらトークン方式へ寄せ直す。**

### 追加の設計事項（ADR-0032 に無い。実装に閉じるので IADR に残す）

**DataProtection 鍵リングの共有先。** `Microsoft.AspNetCore.DataProtection` は既に宣言済みであり、
BFF が複数レプリカならチケット保護鍵の共有先（Redis か PVC）が要る。

> 🔴 **この欠陥は単体テストでは絶対に捕まらない。** チケット保護鍵がレプリカ間で共有されないと、
> **利用者のリクエストが別レプリカへ振られた瞬間にセッション Cookie を復号できず、ログアウトしたように見える。**
> 単一プロセスで動く `WebApplicationFactory` 系のテストは鍵リングが 1 つしか無いため、**共有し忘れていても緑になる。**
> したがって検証は **(a) 2 レプリカ以上を起こす統合テスト**（#783 / IADR-0248 が CI へ入れた統合スタックの上）か、
> **(b) 鍵リングの永続化先が設定されていることを構成の側から固定する検査**のどちらかで行う。**どちらも置かずに
> 「テストが緑だから大丈夫」と読まないこと。**

## 🔴 計画が明示的に禁じていること（移行後に持ち込まない）

ADR-0032 §移行完了までの暫定措置 より。**暫定措置は移行完了をもって解消する。**

| 禁止 | 原文の趣旨 |
| --- | --- |
| **アクセストークン有効期限 10 分の制限を移行後へ引き継ぐこと** | 「本制約は本 ADR への移行完了をもって解消する。**移行後に 10 分の制限を引き継がないこと。**」サーバ側セッションになるため即時失効が可能になる |
| **リソースサーバでのリクエストごとの失効リスト検証（イントロスペクション）** | 「毎リクエストの問い合わせは BFF セッション方式と同等のコストになり、それなら移行を急ぐ方が合理的だから」 |

**セッションストアは Redis で確定である**（§決定）。選択の余地は無い。

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
- **MCP サーバ（OAuth 2.1・ADR-0024）の認証はブラウザセッションと別系統**である旨を設計で明示する
  （ADR-0032 §結果 のフォローアップ後半。**実装ガイドに書く**）
- **`IADR-0033` の後始末は「宿題」ではなく 1 行の更新である**（実測）。同 IADR は既に `status: Superseded`
  （by `IADR-0121`・2026-08-04）であり、追補の表が決定ごとの帰結を記録している。**残っているのは
  決定 4（OIDC public client + PKCE・`oidc-client-ts`）の欄を「置換予定」→「置換済み」へ改める 1 行**であり、
  **新規 IADR の起票も別 issue も要らない。3b で行う。**
- **planning#445 の裁定**（本作業は待たない。裁定後の移動は `git mv` ＋ 10 ファイル）

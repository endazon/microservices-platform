---
title: IADR-0251 BFF セッション（Token Handler）の内部設計 — CSRF は SameSite+ヘッダ、コールバックは query、失効は TicketStore の削除
type: impl-adr
status: Accepted
related_ids: [NFR, SC-16, ADR-0026, ADR-0031, ADR-0032, IADR-0033, IADR-0121, IADR-0248]
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md
related_specs:
  - ../specs/20260822_issue-439_bff-session-token-handler.md
---

# IADR-0251: BFF セッション（Token Handler）の内部設計

> 実装リポジトリ内の意思決定記録。計画 ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）で、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-08-22
- 決定者: Claude（実装）

> 🔴 **番号は仮である。** マージ直前に `develop` の最大＋1 を実測で取り直す（起草時の実測値は最大 `IADR-0248`）。
> 付け替えるときは**間違っている側の番号で grep する**（正しい側で引いても取りこぼしは見つからない）。

## 起点・関連

- 計画 ADR: **ADR-0032**（BFF セッション方式・Accepted）。§決定 と §移行完了までの暫定措置 と §go-live の前提条件
- **ADR-0032 §結果 のフォローアップは、本設計を「実装ガイドへ落とす」と定めている＝計画の裁定待ちではなく実装側へ委譲済み**である
  （先例: `docs/tech/composable-component-guide.md`）。**運用手順は [`docs/authz/bff-session-design.md`](../../docs/authz/bff-session-design.md) が持ち、本 IADR は論拠を持つ。同じ値を両方へ書かない。**
- 関連 issue: #439（第 3 段・go-live ブロッカー）／#446（段の表）／#780（realm のクライアント URI。**先行させる**）

## 前提の実測（本 IADR の決定はこの上に立つ）

**フレームワークの既定値は記憶で決めず、実際に起こして測った。**`AddOpenIdConnect` を構成し
`IOptionsMonitor<OpenIdConnectOptions>` から読み出した実測値である。

| 既定値 | 実測 | 本設計での扱い |
| --- | --- | --- |
| `ResponseType` | **`id_token`** | 🔴 **`code` を明示する。** ADR-0032 は Authorization Code + PKCE を要求する。**既定は `code` ではない** |
| `ResponseMode` | **`form_post`**（`ResponseType=code` にしても変わらない。実測） | 🔴 **`query` を明示する**（決定 2） |
| `UsePkce` | `True` | そのまま |
| `SaveTokens` | `False` | **`true`**（トークンを BFF 側に保持する） |
| `CorrelationCookie.SameSite` / `NonceCookie.SameSite` | **`None`** / `SecurePolicy=Always` | 🔴 決定 2 により **`Lax`** へ |
| `CallbackPath` | **`/signin-oidc`** | 🔴 **`/bff/` 配下へ移す**（決定 3） |
| `CookieAuthenticationOptions.Cookie.SecurePolicy` | **`SameAsRequest`** | **`Always`**（ADR-0032 §決定 が Secure を要求） |
| `Cookie.SameSite` | `Lax` | そのまま（ADR-0032 §決定 と一致） |
| `ExpireTimeSpan` | **`14.00:00:00`** | 🔴 realm の値に従う（決定 6）。**14 日は根拠が無い** |
| `SessionStore` | **`(null)`** | 🔴 **Redis 実装を入れる**（決定 4） |

## 決定 1: CSRF は SameSite=Lax ＋ カスタムヘッダ検証とする

ADR-0032 §決定 は「トークン**または** SameSite + カスタムヘッダ検証」と両論併記である。後者を採る。

**根拠は実測 2 件。**

- **SPA と BFF は同一オリジンである** —— 本番（`edge.yaml`）・ローカル（`platform-frontend-ingress.yaml`）・
  開発（`vite.config.ts` の `proxy: { '/bff': ... }`）のいずれも
- **BFF に CORS ポリシーが 1 つも無い**（`AddCors` / `UseCors` / `WithOrigins` / `AllowAnyOrigin` が 0 件）

**壁が 2 枚残る。**

| 攻撃 | 何が止めるか |
| --- | --- |
| クロスサイトの `fetch`（ヘッダ無し）・`<form>` POST | **SameSite=Lax** —— Cookie が付かない。到達しても未認証で 401 |
| クロスサイトの カスタムヘッダ付き `fetch` | **preflight** —— 非単純リクエストになり、**CORS ポリシーが無いのでブラウザが遮断** |

トークン方式を採らない理由は**依存ではない**（`Microsoft.AspNetCore.Antiforgery` は共有フレームワーク側で追加依存にならない）。
**可動部である** —— 発行口・セッション更新時の再取得・寿命の同期が増え、**同期する状態が 2 つ目の真実になる**。
SPA 側は `foundation/api/orvalMutator.ts`（**既に唯一の出口**）にヘッダ 1 行で足りる。

🔴 **再検討のトリガ（本決定は「同一オリジンかつ CORS 無し」の上に立っている）**

1. **BFF に CORS ポリシーを入れる必要が生じたとき** —— 壁が 1 枚に減る
2. **SPA と BFF を別オリジンにする決定が出たとき**

**どちらかが起きたらトークン方式へ寄せ直す。前提が動いたことに誰も気づかない状態にしないため、条件をここへ書く。**

## 決定 2: `ResponseMode` は `query` とし、correlation / nonce Cookie を `SameSite=Lax` にする

**実測**: 既定は `form_post` であり、`ResponseType=code` にしても変わらない。
`form_post` はコールバックを**クロスサイト POST** にするため、correlation / nonce Cookie は
既定で `SameSite=None; Secure=Always` になっている（実測）。

**`SameSite=None` は `Secure` を要求するため、平文 http のローカル開発でログインが壊れる。**
`localhost` を安全なコンテキストとして扱うかはブラウザ実装に依存し、**そこに賭けない。**

`query` にすればコールバックは**トップレベル GET リダイレクト**になり、correlation / nonce Cookie を
`Lax` にできる。**Authorization Code フローで `query` は標準的である**（`form_post` が要るのは
id_token をフラグメント/本文で受ける形であり、本設計では受けない）。

## 決定 3: OIDC の各パスは `/bff/` 配下に置く

**実測**: 既定は `CallbackPath=/signin-oidc` / `SignedOutCallbackPath=/signout-callback-oidc` /
`RemoteSignOutPath=/signout-oidc` である。

🔴 **エッジは `/bff` と `/bff/` しか BFF へ通さない**（`edge.yaml` は非 `/bff` を frontend へ委譲する。
`platform-frontend-ingress.yaml` も同契約）。**既定パスのままでは Keycloak からのコールバックが BFF に届かない。**

よって `/bff/auth/callback` / `/bff/auth/logout-callback` / `/bff/auth/backchannel-logout` へ移す。

## 決定 4: セッションは Redis 上の `ITicketStore` に置き、失効はその削除で行う

ADR-0032 §決定 が「**セッションストアは Redis とし、…全セッション即時失効をセッションストア側の削除で実現する**」と
定めている。**選択の余地は無い。**

**実測**: `CookieAuthenticationOptions.SessionStore` の既定は `null` である＝チケットが Cookie 本体に載る。
**この既定のままだと、サーバ側に消す対象が無いので即時失効が実現できない。**
`ITicketStore` を Redis 実装にすると Cookie はセッションキーだけを運び、**Redis から消した瞬間に失効する。**

## 決定 5: DataProtection の鍵リングを Redis へ永続化する

`Microsoft.AspNetCore.DataProtection` は既に宣言済みである（実測）。**Redis クライアント
（`Microsoft.Extensions.Caching.StackExchangeRedis`）は未宣言であり、本作業で追加する。**

> 🔴 **この欠陥は単体テストでは絶対に捕まらない。** 鍵がレプリカ間で共有されないと、
> **リクエストが別レプリカへ振られた瞬間に Cookie を復号できず、ログアウトしたように見える。**
> 単一プロセスで動く `WebApplicationFactory` 系のテストは鍵リングが 1 つしか無いため、**共有し忘れていても緑になる。**
> 検証は **(a) 2 レプリカ以上を起こす統合テスト**（`IADR-0248` が CI へ入れた統合スタックの上）か
> **(b) 永続化先が設定されていることを構成の側から固定する検査**で行う。
> **どちらも置かずに「テストが緑だから大丈夫」と読まない。**

## 決定 6: 寿命は realm から読む。散文へ複写しない

**実測**: realm は `rememberMe=true` / `ssoSessionIdleTimeoutRememberMe=2592000` /
`ssoSessionMaxLifespanRememberMe=2592000`（**きっかり 30 日**）を持つ。非「記憶」時の値は `null`＝Keycloak 既定に従う。

**フレームワーク既定の `ExpireTimeSpan=14 日` には根拠が無い。** realm の値に合わせる。
**数値を本 IADR やガイドの散文へ書き写さない** —— 複写した瞬間に 2 つ目の真実ができる。
テストも realm から読んで突き合わせる。

## 決定 8: `check-bff-authz-docs.js` の判定条件を精密にする（**免除表を足すのではない**）

`GET /bff/auth/login` は**本質的に無認証**である —— ログインするために認証は要求できない。
一方で同検査器は「`/bff/*` に無認証の端点は存在してはならない」を不変条件にしており、実測でここが止まった。

🔴 **同検査器は「事故を隠す仕組みを持たない」ことを設計判断として宣言し、その宣言自体を
メタテストで守っている**（検査器のソースに該当語が現れたら落ちる。実測で私の変更が引っかかった）。
**この設計判断は変えていない。**

**やったことは判定条件の精密化である。**

| | 免除表を足す場合（**採らなかった**） | 本決定（採った） |
| --- | --- | --- |
| 判定材料 | **端点の名前・経路**を列挙する | **ハンドラが何をしているか** |
| 新しい端点が増えたとき | 表に足せば何でも通る | **条件を満たさなければ通らない** |
| 事故を隠すか | 隠す（表に載せた瞬間に検査対象外） | 隠さない（資料を返せば違反のまま） |

条件は「**`AllowAnonymous` が明示されており、かつ文中の `Results.*` がすべて `Challenge` である**」。
`Results.Ok(...)` 等が 1 つでも混ざれば違反のままである。

**抜け道になっていないことを負例で実測した。** 条件を `every` から `some` へ緩める変異を入れると、
「チャレンジも出すがデータも返す」ハンドラの負例が
「データを返すハンドラが『チャレンジのみ』と判定された（抜け道）」で落ちる。
**守る範囲は変わっていない。判定が細かくなっただけである。**

> **［作業中に踏んだ事故の記録］** 本条件を書き込む際、シェル埋め込みの Python 文字列で
> `\b`（単語境界）が**エスケープ解釈され、生の `0x08`（backspace）としてファイルへ書かれた。**
> **JS のパースは通る**ため構文エラーにならず、**正規表現だけが静かに壊れた**（単語境界が消えた）。
> 同じ「不可視の破損」の族として、生の NUL・BOM・コマンド置換による欠落に続く **4 例目**である。
> 是正はエスケープ層を挟まない書き込み（ファイルとして書いて実行）で行い、
> **制御バイト 0 件と単語境界の復元を assert してから**保存した。

## 決定 9: ［3b］既定は振り分けスキームとし、Cookie と Bearer の**両方**を受理する

**移行期の姿勢である。恒久の姿勢として書いていない。**

### なぜ単純に Cookie を既定にしないか（実測）

素直に既定を `BffSession` 単体にすると、**スキームを指定しない `RequireAuthorization()` を持つ
端点は Bearer 呼び出しを 401 で拒む**（`DefaultSchemeRoutingTests` が実測で固定）。

`scripts/verify-oidc-edge-flow.sh` は `/bff/*` を **Bearer で 4 箇所**叩いており（実測: 220 / 291 /
319 / 345 行）、**統合スタックで実際に動いている唯一の外形確認**である。移行の副作用でこれを失わない。

🔴 **計画は Bearer 受理を許していない。禁じてもいない。** ADR-0032 が禁じているのは
**SPA がトークンを扱うこと**であって、非ブラウザの呼び出し口が `/bff/*` を Bearer で叩くことでは
ない（原文に言及が無い）。**「言及が無い」は「許可」ではない**ので、**実装側の判断として採る。**

### なぜ「既定ポリシーに 2 スキーム」ではなく振り分けスキームか

端点が `RequireAuthorization(p => p.RequireRole(...))` で作る**内側のポリシーはスキームを持たない**。
既定ポリシーだけを直しても、**ロール要求を持つ端点は `context.User` を作った既定スキームにしか
従わない**。振り分けを認証の側に置くと、**既定ポリシーの端点もロール要求の端点も同じように**
両方を受理する（条件 4 の検査で固定した）。

### 🔴 狭める条件（A へ寄せる。書かないと移行期の姿勢が恒久になる）

**次のいずれかが満たされたら、既定を `BffSession` 単体へ狭める。**

1. **`verify-oidc-edge-flow.sh` が Cookie 方式へ移った**とき（外形確認を失わずに狭められる）
2. **非ブラウザの `/bff/*` 呼び出し口がサービスアカウント方式などへ移った**とき
3. **計画側が `/bff/*` の Bearer 受理を禁じたとき**

**狭めるのは緩める方向ではないので、後から実施できる。** 逆（A から B へ緩める）は承認が要る。
**可逆性の高い側を先に採っている。**

### 検査（3 点セット ＋ 条件 4）

| # | 固定したこと |
| --- | --- |
| 1 | Cookie 呼び出しが通る |
| 2 | Bearer 呼び出しも通る |
| 3 | 🔴 **どちらも無ければ 401**（**陰性対照**。2 つだけだと「常に通す実装」が両方を通す） |
| 4 | ロール要求を持つ端点でも**両経路が同じ認可判定を通る**（＋ 資格情報が無ければ 401） |

**変異試験で検出力を確認した。** ハンドラを「常に通す」へ変異させると、**3 の陰性対照を含む
3 件が落ち、肯定側の 6 件は緑のまま**である。**陰性対照が無ければ「常に通す」が通っていた。**

## 決定 7: 計画が禁じている 2 点を実装へ持ち込まない

ADR-0032 §移行完了までの暫定措置 より。**暫定措置は移行完了をもって解消する。**

- 🔴 **アクセストークン有効期限 10 分の制限を移行後へ引き継がない**（原文「移行後に 10 分の制限を引き継がないこと」）。
  サーバ側セッションになるため即時失効が可能になる
- 🔴 **リソースサーバでリクエストごとの失効リスト検証（イントロスペクション）を行わない**
  （原文の理由: 毎リクエストの問い合わせは BFF セッション方式と同等のコストになる）

## 結果

- 良い影響: トークンがブラウザに一切出ない。Redis からの削除で即時失効できる。CSRF の可動部が増えない
- トレードオフ: BFF がステートフルになり Redis が単一障害点になる（ADR-0032 §結果 が既に記録している）。
  鍵リングとセッションストアという **2 つの Redis 依存**が増える
- フォローアップ: **MCP サーバ（OAuth 2.1・ADR-0024）の認証はブラウザセッションと別系統である**旨をガイドに明記する
  （ADR-0032 §結果 のフォローアップ後半）

## 関連

- Supersedes: `IADR-0033` 決定 4（OIDC public client + PKCE・`oidc-client-ts`）。
  **同 IADR は既に `IADR-0121` により Superseded であり**、その追補の表で決定 4 は「置換予定」と記録されている。
  **第 3 段の完了（3b）でその欄を「置換済み」へ改める。新規 IADR の起票は要らない。**
- 関連: `IADR-0121` 決定 6（撤去は第 3 段）／`IADR-0248`（統合スタックの CI ゲート。決定 5 の検証先）

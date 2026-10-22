---
title: IADR-0578 AI 回答の描画の sanitize はタグ・属性・URI を allowlist で閉じ、Wiki 本文もフォーム部品と style を落とす。SPA の配信（Caddy）に CSP を置き、その 1 行を単一情報源として E2E の配信にも同じ値を返す
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0021, ADR-0031, IADR-0362, IADR-0365, IADR-0134]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0021_edge-istio-gateway-caddy.md （SPA の静的配信は Caddy）
  - planning:projects/microservices-platform/07_adr/ADR-0031 （フロントエンドの技術スタック）
related_specs:
  - ../specs/20261022_ghsa-5h4q_answer-sanitize-allowlist-and-csp.md
---

# IADR-0578: AI 回答・Wiki 本文の sanitize を絞り、SPA の配信に CSP を置く（GHSA-5h4q-4m2j-rjhm）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: コーディネーター指示（2026-10-22。sanitize の絞り込みと CSP）・claude（許可の集合・CSP の値・単一情報源の置き方）

## 起点・関連

- 起点: GHSA-5h4q-4m2j-rjhm（多層防御の強化。描画の許可を最小にし、配信に 2 枚目の壁を置く）。本 IADR はその 1 本目
  （2 本目の RAG のプロンプトの信頼境界は別の IADR で扱う）。
- 計画: **ADR-0021**（SPA の静的配信は Caddy）・**ADR-0031**（フロントエンドの技術スタック）。矛盾しない
  （配信サーバ・ライブラリは変えない。計画書に CSP を定める・禁じる記述は無い）。
- 前提: [IADR-0362](./IADR-0362_spa-serving-caddy.md)（Caddy の配信。「CSP は入れない——実測の裏付けなしに入れると画面が静かに欠ける。
  別 issue の仕事」を本 IADR が引き取る。裏付けは決定 3 の E2E）・IADR-0365（Wiki 本文の sanitize）・IADR-0134（初期ロードの ratchet）。

## コンテキストと課題

- AI 回答（RAG の出力）は marked で HTML にし、DOMPurify を通して描く。Wiki 本文は Wiki.js が描いた HTML を DOMPurify を通して描く。
- 多層防御として、(1) 描画の許可を表示に要る最小の集合へ絞り、(2) 配信に CSP を 2 枚目の壁として加える。
  IADR-0362 が「実測の裏付けが要る」とした CSP の裏付けは、全画面の E2E を CSP の下で走らせることで得る。

## 検討した選択肢

1. **sanitize の許可**:
   - (a) 既定に `FORBID_TAGS` / `FORBID_ATTR` を足していく（denylist）—— 足し忘れがそのまま通る。
   - (b) **allowlist（採用・AI 回答）**: marked が出す要素だけを許す。足りないものは描けないだけで、余計なものは通らない。
   - Wiki 本文は Wiki.js が描いた任意の HTML（見出しの `id`・`class` 等を含む）であり、allowlist にすると描画が欠ける。
     **既定に対して、フォーム部品と `style`（要素・属性）を明示して落とす**（(a) を局所的に採る）。
2. **CSP の置き場所**: (a) index.html の `<meta>` —— `frame-ancestors` が効かず、ヘッダより弱い。
   (b) **Caddy の応答ヘッダ（採用）**: 配信の契約を持つ Caddyfile（IADR-0362）に置く。エッジ（Istio）は非 `/bff` を全部 Caddy へ流すので、SPA の文書には必ず付く。
3. **E2E での裏付け**: (a) CSP の値を `vite.config.ts` に複写 —— 2 か所で片方が古くなる。
   (b) **Caddyfile の 1 行を単一情報源にし、`vite.config.ts` の preview がその行を読む（採用）**。行が無ければ設定の読み込みで落ちる。

## 決定

1. **AI 回答の sanitize は allowlist**（`markdown.ts`）:
   - タグ: 段落・改行・区切り・見出し・強調（strong/em/b/i/del/s）・code/pre/blockquote/kbd/sub/sup・リスト・表・`a`。
   - 属性: `href` / `title` / `align` / `start` / `class`。`class` は**脚注の印 `<sup class="cite">` だけ**に残す（フック）。
     `style` / `id` / `name` / `data-*` / `aria-*` は通さない。
   - URI: `http(s)` / `mailto` の絶対 URL・`#…`・`/` で始まる同一オリジンのパス（`//` で始まるスキーム相対は通さない）。
   - DOMPurify は URI を持たない属性の値にも URI の正規表現を当てるため、`align` / `start` / `title` / `class` は `ADD_URI_SAFE_ATTR` で外す。
2. **Wiki 本文の sanitize**（`sanitizeWikiHtml.ts`）: 従前の集合に加え、`form` / `input` / `button` / `select` / `option` /
   `optgroup` / `textarea` / `fieldset` / `style` 要素と `style` 属性を落とす。
3. **CSP**（`Caddyfile`。1 行が単一情報源）:
   `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; media-src 'none'; object-src 'none'; frame-src 'none'; worker-src 'self'; manifest-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`
   - `script-src 'self'`: SPA にインラインのスクリプトは無い（`/config.js` とビルド成果物だけ）。eval も許さない。
   - `style-src` に `'unsafe-inline'` を残す: 部品（トースト等）が実行時に `<style>` を差し込む。外への取得は
     `img-src` / `font-src` / `connect-src` が `'self'` に閉じるため、CSS から外部の資産は読めない。
   - `img-src` の `data:` / `blob:`: 変換ジョブの図（BFF から読んだバイト列を `blob:` で表示）。
   - `connect-src 'self'`: BFF は同一オリジンの `/bff` だけ（SPA は `bffBaseUrl` の絶対 URL を禁じている）。
   - `form-action 'self'`: ネイティブな送信は無い（全フォームは `onSubmit` で処理）。ログイン・ログアウトは遷移であり対象外。
   - dev サーバ（`vite` の server）には付けない（HMR がインラインのスクリプトと WebSocket を使う）。
4. **E2E のガード**（`e2e/support/cspGuard.ts`）: 全 spec が `@playwright/test` の代わりにここから `test` を import する。
   ページの `securitypolicyviolation` と CSP のコンソールエラーを 1 件でも観測したら落とす（auto fixture）。
   違反は `page.exposeBinding` でその場でノード側へ送る（ページ内に溜めると、文書をまたぐ遷移で遷移前の違反が消える）。
   陰性対照（`csp.smoke.spec.ts`）はインラインのスクリプト・外部の画像・外部への通信が拒まれ、ガードが観測することを確かめる。
5. **Zod は `jitless`**（`analysisFormSchema.ts`）: Zod は既定で `new Function` の可否を試す（例外は握り潰すが CSP の違反として報告される）。
   本リポで Zod を使うのはこのファイルだけなので、ここで設定する（初期ロードへ Zod を載せない。IADR-0134）。
   この前提は ESLint が守る: `zod` / `zod/*` の import を `BANNED_IMPORT_PATTERNS`（`src/eslint.config.js`）で禁じ、
   `analysisFormSchema.ts` の 1 か所だけを理由つきの `eslint-disable-next-line` で外す（設定より前に Zod が使われる置き場所を作らせない）。

## 結果

- AI 回答と Wiki 本文の描画は、Markdown・Wiki の表示に要る要素だけを描く。
- AI 回答の**相対パスのリンク**（`docs/1`・`./x`・`../x` のように `/` で始まらないもの）は `href` を落とし、リンクの文字だけを残す
  （決定 1 の URI の許可の帰結。`/` で始まる同一オリジンのパス・`#…`・`http(s)` / `mailto` は従前どおりリンクになる）。
  回答の文脈では相対パスの基点が画面ごとに変わり、行き先が定まらないため、意図した挙動である。
- `form-action 'self'` の影響（2026-10-22 に確かめた）: ログイン・ログアウトは SPA のトップレベル遷移（`location.assign`）で
  `/bff/auth/login`・`/bff/auth/logout` の **GET** を開き（BFF は `MapGet`）、BFF の OIDC は `ResponseMode=Query`（GET の往復）である。
  SPA と AST ユニットの画面にネイティブなフォーム送信（`action` / `method` を持つ `<form>`・`submit()`）は無い
  （`git grep` で 0 件。全フォームは `onSubmit` で処理する）。よってフォーム送信後の別オリジンへのリダイレクトという経路は無く、
  `form-action` が Keycloak への往復を止めることは無い。Keycloak のログイン画面は Keycloak の文書であり、本 CSP の対象外である。
- 外向きの経路が CSP を足す・上書きすることは無い（2026-10-22 に確かめた）: Istio のエッジ（`templates/edge.yaml`・
  `deploy/local/edge-istio/`）は応答ヘッダを操作せず EnvoyFilter も無い。Traefik のエッジ（`deploy/local/edge/`）の SPA の Ingress は
  中間処理（Middleware）を持たない（Middleware は Keycloak の管理面の拒否だけ）。
- 既存の E2E 78 本（全画面）が CSP の下で違反 0 件で通る（手元で実測）。CSP を変えたときは E2E がそのまま裏付けになる。

## 残る制約

- `style-src 'unsafe-inline'` は残る（決定 3）。部品が `<style>` を差し込まなくなれば外せる（nonce の配布は Caddy の静的配信では持たない）。
- AST ユニットの画面は MSP の E2E に含まれない。AST の画面に外部の資産・インラインのスクリプトは無いことを `git grep` で確かめた
  （画像は `/bff/...` の同一オリジンだけ）。
- BFF の応答（`/bff/*`）は Caddy を通らない経路（エッジから直接）がある。BFF の応答ヘッダは本 IADR の射程外。

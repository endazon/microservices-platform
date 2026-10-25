---
title: 作業仕様書 — Keycloak の画面（SC-13〜16）を SPA と同じ Nocturne デザインへ揃える（keycloak.v2 / keycloak.v3 継承・トークン同期）
type: spec
status: done
related_ids: [SC-13, SC-14, SC-15, SC-16, ADR-0026, ADR-0031, ADR-0045, NFR-12, IADR-0261, IADR-0435, IADR-0524]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（§SC-13〜16・§共通シェル）
  - planning:projects/microservices-platform/05_screens/mockups/hi-fi/sc-13.html〜sc-16.html（Nocturne）
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
---

# 作業仕様書 — Keycloak の画面を SPA の Nocturne デザインへ揃える

> 本仕様書は実装着手前に作成した（着手 2026-10-10）。基点は MSP `origin/develop` `ef783849`。
> 起点は利用者の指摘「Keycloak の画面が既定のままでデザインが統一されていない」（issue は未起票。本 PR が起点）。

## 起点（トレーサビリティ）

- 画面: **SC-13**（ログイン）・**SC-14**（OTP）・**SC-15**（パスワードリセット）・**SC-16**（アカウント設定）。
  いずれも計画が「Keycloak のテーマとして実装する」と定める（`01_screens.md` 注記 2026-07-24）。
- 計画 ADR: **ADR-0026**（認証画面は Keycloak のテーマで提供）・**ADR-0031**（UI = Tailwind v4 / Nocturne・Lucide）・
  **ADR-0045**（リセットメール）・08_data-egress-policy（外部 CDN・Web フォント禁止）・INDEX 決定 21（色だけで意味を持たせない）。
- 実装 ADR: **IADR-0261**（テーマ方針＝`parent=keycloak` ＋ CSS のみ）・**IADR-0435**（SPA トークンの二層化）・
  **IADR-0524**（Keycloak 24 → 26.7.4）。新しい決定は **IADR-0532**（本 PR）に記録する。

## 調査結果（着手前の実測）

Keycloak 26.7.4 の配布物（GitHub release の tar.gz）を手元で `start-dev` し、実 realm（`deploy/keycloak/microservices-platform-realm.json`）と
現行テーマをそのまま載せて確かめた。

1. **ログイン（login）**: `parent=keycloak` は 26 では**旧 v1 テーマ（PatternFly 3）**を指す。画面は Keycloak の既定（多角形の背景画像・
   白カード・青ボタン）のままで、現行 `platform.css` が変えているのは見出し色と上辺の線だけである（スクリーンショット: 本 PR の記録）。
   26 の既定ログインテーマは `keycloak.v2`（PatternFly 5）。
2. **アカウント（account）**: 🔴 **26.7.4 の `keycloak` テーマは account 型を持たない**（`keycloak-themes` jar の `META-INF/keycloak-themes.json`
   は `keycloak` = login/common/email/welcome。account は `keycloak-account-ui` jar の `keycloak.v3` だけ）。
   ログに `Not found parent theme 'keycloak' of theme 'platform'. Unable to load ACCOUNT theme` / `Failed to find ACCOUNT theme platform,
   using built-in themes` が出て、**SC-16 は素の `keycloak.v3` に黙って落ちていた**（IADR-0524 の 24 → 26 で壊れた。検査 4 は parent の
   **宣言の有無**しか見ておらず、parent が**その版に実在するか**は見ていなかった）。
3. **メール（email）**: `emailTheme` 未設定＝`keycloak`（素の HTML。ブランド表示なし）。
4. **realm**: 利用者が入るのは `platform` だけ（AST 専用 realm `ai-stock-trading` は単独 E2E 用で、連結配備では platform realm で認証する。
   AST の realm-export.json の `_scope` 注記）。`master` は管理コンソール専用で利用者に見せない。
5. **配備**: compose はディレクトリをマウント、k8s ローカル（`deploy/local/infra/keycloak.yaml`）は ConfigMap `keycloak-theme-platform`
   （`scripts/k8s-local-up.sh` [3/7] が生成）を **items で 1 ファイルずつ**マウント。helm chart は Keycloak を持たない。
   既存 realm へのテーマ名の反映は realm の後追い（`reconcile-realm.js`）が非コレクションのスカラーとして当てる（`loginTheme` 等は既に宣言所有）。
6. **reset-gate / reset-floor** は text/plain のプロキシで、利用者が見る HTML を返さない（対象外）。

## 対象範囲

- 対象:
  - login テーマ: `parent=keycloak.v2` へ載せ替え、CSS（トークン＋PF5 変数の写像）と最小のメッセージ上書きで Nocturne の見た目へ揃える。
    SC-13 / SC-14（OTP・TOTP 初回登録）/ SC-15（申請・新パスワード）/ 必須アクション・エラー・ログアウト確認など login 型の全画面に効く。
  - account テーマ: `parent=keycloak.v3` へ是正（壊れていた）し、同じトークンで配色・フォント・ブランドを揃える。
  - email テーマ: `parent=keycloak`、HTML の外枠（`html/template.ftl` のマクロ 1 個）だけを上書きしてブランド見出しを付ける。
  - トークンの単一情報源: `src/packages/ui/src/styles.css` から `tokens.css` を**生成**し、`--check` で CI が差分を止める。
  - 検査: realm のテーマ宣言（`emailTheme` を追加）と **parent が Keycloak 26.7.4 に実在すること**（検査 4 の拡張）。
    テーマ配下の egress 走査（`check-static-egress.js --require deploy/keycloak/themes`）。ConfigMap items とテーマ実ファイルの集合一致。
  - 文書: 画面仕様書 SC-13〜16 の該当行、`deploy/local/README.md` の手動手順、運用手順（テーマの配備と切り戻し）。
- 対象外（理由）:
  - `.ftl` テンプレートの複製（login / account）。IADR-0261 決定 1 の理由（本体のセキュリティ修正・新フローへの追随）を維持する。
  - SPA と同じ「表示: システム / ライト / ダーク」切替ボタン。別オリジンで localStorage を共有できず、Keycloak 側に置くとテンプレートの複製が要る。
    **OS 設定（`prefers-color-scheme`）に従う**（SPA の既定 = system と同じ振る舞い）。
  - 管理コンソール（`master` realm / admin テーマ）。利用者の画面ではない。
  - 新パスワード画面の「ポリシー表示」（SC-15 の残件）と全セッション失効の確認。デザインではなく機能の作り込みである。
  - メールのテキスト版・本文の文言。ADR-0045 決定 7（本文はリンクと有効期限のみ）の検査（`check-password-reset-mail.js`）を動かさない。
  - AST リポジトリの変更（AST realm は利用者のログインに使われない）。

## 設計

### トークン

- 生成器 `scripts/gen-keycloak-theme-tokens.js` が `src/packages/ui/src/styles.css` から読む:
  - ダーク = 最初の `:root { … }`、ライト = `:root[data-theme='light'] { … }` の**意味トークン**（`--color-*` 14 個）。
  - `@theme` の `--font-sans` / `--font-mono` / `--radius-sm|md|lg` / `--color-ok|warn|err`。
- 出力は `deploy/keycloak/themes/platform/{login,account}/resources/css/tokens.css`（同一内容・生成物・コミットする）。
  既定はダーク、`@media (prefers-color-scheme: light)` でライト。`--check` は再生成との差分を exit 1、`--self-test` で抽出器を試験する。
- **メールは CSS 変数が使えない**（メールクライアント）。`email/html/template.ftl` の 16 進色は**ライトの意味トークンの値に限る**ことを `--check` が確かめる。

### login（`parent=keycloak.v2`）

- `styles=css/styles.css css/tokens.css css/platform.css`（親の `styles.css` を引き継ぎ、後段で上書き）。`darkMode=true`。
- `platform.css` は `html.login-pf` に PF5 のグローバル変数（背景・文字・リンク・主色・枠・角丸・フォント）を Nocturne の意味トークンへ写し、
  コンポーネント（カード・ボタン・入力・アラート・チェックボックス・言語選択）は**モックのクラス語彙**（`.btn-primary` = 枠と文字だけ、`.panel`、`.input`）に合わせる。
  背景画像（`keycloak-bg-darken.svg`）は外す。
- アラートのアイコンは Font Awesome から **Lucide の SVG（data URI の CSS mask）**へ差し替える（`kcFeedback*Icon` プロパティでクラスを付け替える）。
  テンプレートが `message.summary` のテキストを必ず出すので、**色＋アイコン＋テキスト**が揃う。
- ヘッダ: realm の `displayName`（汎用プラットフォーム）の前に accent の 9px 角（モック `.hf-brand i`）、下に副題（`loginTitleHtml` の上書き）。
- メッセージ（`messages_ja` / `messages_en`）: 計画が文言を定めたものだけ上書きする —— 失敗時の文言（SC-13「社員ID またはパスワードが正しくありません」）・
  識別子のラベル（社員ID または メールアドレス）・「このデバイスを記憶（30日）」・申請後の「メールを送信しました」・見出し「サインイン」・副題。
  「30日」は realm の `ssoSessionMaxLifespanRememberMe` と一致することを `--check` が確かめる。

### account（`parent=keycloak.v3`）

- `styles=css/tokens.css css/platform.css`、`darkMode=true`。PF5 変数の写像は login と同じ方針。マストヘッドのロゴを
  `img/brand.svg`（accent の角＋表示名。SVG 内の `prefers-color-scheme` で明暗）へ差し替える（`logo` プロパティ）。

### email（`parent=keycloak`）

- `html/template.ftl` の `emailLayout` マクロだけを上書きし、インライン style の外枠（ブランド見出し＋本文カード）を付ける。本文は親のメッセージのまま。

### 配備

- realm JSON: `emailTheme: "platform"` を足す（`loginTheme` / `accountTheme` は既存）。既存 realm へは後追い Job が当てる（スカラー・宣言所有）。
- compose: ディレクトリのマウントのまま（ファイルを足すだけ）。
- k8s ローカル: ConfigMap の `--from-file` と `keycloak.yaml` の `items` に新ファイルを足す。**集合の一致を試験で固定**（`k8s-local-up.test.js`）。
  ConfigMap の内容が変わるだけなら Pod の再起動は要らない（items マウントは kubelet が更新し、dev はテーマキャッシュ無効）が、
  **items を足したので Deployment が変わり rollout が起きる**。

## 母集合（規則 9・10）

- `git grep -n "themes/platform\|keycloak-theme-platform\|loginTheme\|accountTheme\|emailTheme\|parent=keycloak"`（全追跡ファイル）で引いた:
  `deploy/docker-compose.yml`・`deploy/local/infra/keycloak.yaml`・`scripts/k8s-local-up.sh`・`scripts/k8s-local-up.test.js`・
  `scripts/check-realm-constraints.js`（検査 4 と自己試験）・`deploy/local/README.md`（手動手順）・`docs/screens/SC-13〜16`・
  realm JSON。`.ai-context/` の凍結記録（IADR-0261・旧仕様書）は書き換えない（IADR-0261 は新 IADR が決定 1 を改める旨の追記だけ）。
- 規則 10: 画面仕様書の「`parent=keycloak` を継承」「CSS 上書き」の記述は本変更で誤りになる → 是正対象。

## 受け入れ基準

- [x] Keycloak 26.7.4 で `platform` の login / account テーマが**親の欠落なく**解決される（ログに `Failed to find … theme platform` が出ない）。
- [x] ログイン・エラー・リセット申請・OTP 初回登録・アカウント設定の画面が、ライト／ダーク・ja／en の各組で SPA と同じ配色・角丸・フォント・ボタン語彙で描かれる（スクリーンショットで確認）。
- [x] 外部オリジンを参照しない（`check-static-egress.js --require deploy/keycloak/themes` が 0 件）。Web フォントを読み込まない。
- [x] 状態表示（エラー・警告・成功・情報）は色＋アイコン＋テキスト。
- [x] `tokens.css` が `@platform/ui` のトークンと一致しないと CI が落ちる（`gen-keycloak-theme-tokens.js --check`）。
- [x] realm が宣言するテーマの parent が Keycloak 26.7.4 に実在しないと `check-realm-constraints.js` が落ちる。
- [x] ConfigMap の items・`--from-file`・テーマ実ファイルの集合が一致しないと `k8s-local-up.test.js` が落ちる。
- [x] 存在秘匿の 3 面（ステータス・本文・リダイレクト先）を変えない —— 文言の上書きは実在・非実在の両側に同じく効く。

## テスト方針

- 生成器: `--self-test`（抽出・出力の形・ドリフト検出の変異試験）と `--check`（実データ）。CI の static-checks に追加。
- `check-realm-constraints.js --self-test` に「parent が版に実在しない」「emailTheme の実体」の陽性・陰性を足す。
- `k8s-local-up.test.js` の既存テーマ試験を、実ファイルの列挙から期待集合を作る形へ改める。
- 目視: Keycloak 26.7.4 を手元で `start-dev` し、Playwright（Chromium）でスクリーンショットを採る。

## 計画書との差異

- 差異: 見た目は計画どおり（計画は「Keycloak のテーマとして実装」「モックアップ準拠」。モックは Inter を Google Fonts から読むが写さない —— IADR-0435 決定 6 と同じ扱い）。
- 文言（#1893 監査で洗い出した。2026-10-10）:
  - **SC-13 の送信ボタン**: 当初は親の既定「サインイン」で、モック（`sc-13.html`）は「ログイン」だった。→ **モックへ揃えた**（`messages_ja.properties` に
    `doLogIn=ログイン`）。見出しは「サインイン」のまま（モックも見出しは「サインイン」、押下の語は「ログイン」と分けている）。親の `doLogIn` は
    ワンタイムコードの確定ボタンにも使われ、SC-14 のモック「認証してログイン」と語が揃う。存在秘匿には効かない（実在・非実在の両側で同じ画面の同じ語）。
  - **SC-15 の申請画面の識別子のラベル**: 「社員ID または メールアドレス」と出る（モック `sc-15.html` は「メールアドレス」。計画 `01_screens.md` も
    「SC-15 の入力はメールアドレスのみ」と書く）。→ **意図した差異として残す**。親のテンプレート（`login-reset-password.ftl`）は realm の設定
    （`loginWithEmailAllowed=true`・`registrationEmailAsUsername` 未設定）から SC-13 と同じ鍵 `usernameOrEmail` を引き、文言ファイルだけでは
    申請画面専用の鍵へ分けられない（分けるにはテンプレートの複製が要り、IADR-0261 / IADR-0532 決定 1 に反する）。また Keycloak の申請は
    実際に社員ID でも受け付けるので、「メールアドレス」と書くとラベルが挙動と食い違う。申請後の「メールを送信しました」（存在秘匿の文言）は変えていない。
  - **SC-15 の申請画面の送信ボタン**: 「送信」と出る（モックは「リセットメールを送信」）。→ **意図した差異として残す**。親の鍵 `doSubmit` は
    パスワード更新・OTP 登録など多数の画面の確定ボタンと共有で、上書きすると他の画面の語が誤る。
  - 計画の「SC-15 の入力はメールアドレスのみ」と Keycloak の挙動（社員ID でも申請できる）の食い違いは見た目の作り込みの範囲外であり、本 PR では扱わない。

## 結果（2026-10-10）

- Keycloak 26.7.4（GitHub release の配布物・Java 21）を手元で `start-dev` し、実 realm と本テーマで確かめた。
  ログの `Failed to find … theme platform` は出なくなった。realm の後追い（`reconcile-realm.js`）を手元の Keycloak へ向けると
  `realm 設定の差分: emailTheme` を 1 件検出して当て、再計画 0 件に収束した。
- スクリーンショット（Playwright / Chromium。ライト／ダーク × ja／en）: ログイン・失敗・リセット申請・送信済み・OTP 初回登録・OTP 失敗・
  パスワード更新（ポリシー違反のエラー）・エラー画面・ログアウト確認・アカウント設定（個人情報・サインイン・デバイス）・リセットメール。
  手元で撮って利用者に見せた（PR には添付していない）。
- 🔴 **範囲外の既存不具合を見つけた**: アカウントコンソールは realm の宣言のままでは API が 401（テーマと独立。既定テーマでも同じ）。
  `roles` スコープに audience 解決・クライアントロールの写像が無い／`account-console` に `basic` スコープが付かない／利用者に `manage-account` が無い。
  見た目の確認は手元でこの 3 点を補って行った（リポジトリは変えていない）。IADR-0532 §フォローアップ・画面仕様書 SC-16 §未決事項に記録した。
  別 issue #1894 で扱う。

## 未決事項

- なし（テーマ切替ボタンを置かない判断は IADR-0532 に記録する）。アカウントコンソールの 401 は別件 #1894 として扱う。

## 監査指摘の是正（2026-10-10・#1893 のフェーズ末監査）

- 同梱テーマの表の版（`KEYCLOAK_VERSION_FOR_THEMES`）と Keycloak のイメージタグ（`deploy/docker-compose.yml`・`deploy/local/infra/keycloak.yaml`）を
  `check-realm-constraints.js` で突き合わせる（食い違い・タグが読めない・マニフェストが無いで exit 1。自己試験 4 件＝一致・片側の不一致・fail-loud・実データ）。
- `check-static-egress.js` の走査対象の拡張子に `.ftl` / `.properties` を足した（メールの外枠と `loginTitleHtml` が走査から漏れていた）。
  全体に効かせた。既存の走査先（Storybook・SPA・Obsidian プラグインの成果物）は手元でビルドして走らせ、ファイル数（22 / 58 / 3）と結果（0 件）が
  変わらないことを確かめた。テーマ配下は 6 → 12 ファイル。自己試験に「.ftl の外部 `<img src>`・.properties の HTML 値の外部画像を検出する」を足した。
- `gen-keycloak-theme-tokens.js --check` に、styles.css の明暗の対（明示ライト ⇔ system ライト、既定ダーク ⇔ 明示ダーク）の一致と、
  テーマの `platform.css` のコメント外の 16 進色の禁止を足した（自己試験 5 件）。
- `k8s-local-up.test.js` の失敗文言を是正した（ConfigMap は `optional: true` なので、キーが無いと Pod は起動したままそのファイルだけ黙って欠ける）。
- 文言の差異は §計画書との差異 に記録した（SC-13 のボタンはモックへ揃え、SC-15 の 2 点は意図した差異）。
- 是正中に見つけた: テスト仕様書 `docs/tests/SC-13_login.md` で本 PR が足したテーマの行（旧 T-09〜T-14）が、同じ文書の存在秘匿の節の既存の T-09〜T-14 と
  番号が重なっていた。本 PR の行を空き番号 T-15〜T-20 へ改め、版とイメージタグの突合を T-21 とした（既存の行は動かしていない）。

---
title: 作業仕様書 — Keycloak のテーマの画面（SC-13 の Keycloak 側・SC-14〜16）を WCAG 2.2 AA の axe で機械検査する
type: spec
status: done
related_ids: [NFR-30, SC-13, SC-14, SC-15, SC-16, ADR-0026, ADR-0031, IADR-0619, IADR-0614, IADR-0532, IADR-0569]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-30）
  - planning:projects/microservices-platform/05_screens/01_screens.md（§SC-13〜16）
issue: "#2191"
---

# 作業仕様書 — Keycloak のテーマの画面を axe で機械検査する

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。判断の記録は **IADR-0619**（新規）に置く。
> 起点は IADR-0614 §結果の残余「Keycloak のテーマの画面の機械検査（L2 以降）」（受け皿 #2015 は closed）。

## 計画の根拠（`git show origin/main:` で読んだ）

- NFR-30: 「機械検査（axe のタグ `wcag2a`・`wcag2aa`・`wcag21a`・`wcag21aa`・`wcag22aa`）が検出する違反を 0 件に保つ」。
  対象は SC-01〜SC-22 の全画面で、「Keycloak テーマで描く SC-13〜16 は、現在どちらの機械検査の対象でもない」。
- 05_screens の各画面の主要素:
  - SC-13: 社員ID／メールアドレス・パスワード・「このデバイスを記憶（30日）」・パスワードリセット導線・認証エラー表示（固定文言）・言語切替。
  - SC-14: 6 桁コード入力・初回セットアップ（QR コード・手入力キー・デバイス名・確認コード）。
  - SC-15: 申請（→ 常に「メールを送信しました」）・メール内リンクからの新パスワード設定。
  - SC-16: プロフィール・パスワード変更・OTP デバイス管理・アクティブセッション一覧（Keycloak のアカウントコンソール）。

## 受け入れ基準

- [x] 対象のテーマの画面が、ライトとダークの両方で WCAG 2.2 AA のタグの axe を通る（違反 0 件。台帳 0 件）。
- [x] 機械検査が CI で走る（新設の `keycloak-theme-a11y.yml`。テーマ・realm・compose・axe の土台・本ワークフローを変える PR と develop / main の push）。
- [x] タグ・テーマ・既知の違反の台帳・使わなくなったキーの検査が IADR-0614 と同じ作法である（タグは `e2e/support/axe.ts` から輸入）。
- [x] 方式の選定理由を IADR に残す（IADR-0619）。

## 調査結果（方式の判断材料）

- テーマの実体（`deploy/keycloak/themes/platform`）は CSS・メッセージ・ロゴ・メールの外枠だけで、`.ftl` を持たない（IADR-0532 決定 1）。
  ログインの画面は親 `keycloak.v2` の FreeMarker テンプレート（Keycloak の配布物の `keycloak-themes` jar の中）、アカウントコンソールは
  親 `keycloak.v3` の React アプリが描く。→ **Keycloak を起動せずに描く方式は成立しない**（IADR-0619 §検討した選択肢 A・B）。
- 既存の Keycloak を起こす CI は integration-stack（k3d に全基盤・nightly・約 45 分）だけで、テーマを変える PR では走らない。
- `deploy/docker-compose.yml` の keycloak サービスが、配布物と同じイメージ（タグ＋digest）で realm の取り込みとテーマのマウントを既に宣言している。
- 手元の実測（Keycloak 26.7.4 の配布物を Java 21 で起動。Docker デーモンは無い環境）: augmentation 約 28 秒＋起動 約 32 秒。

## 方式

- 新規 `.github/workflows/keycloak-theme-a11y.yml`: compose の `image:` を読み、`docker run … start-dev --import-realm` で使い捨ての Keycloak を
  起こす（realm の JSON とテーマを読み取り専用でマウント。管理者パスワードは乱数・マスク）。起動を pnpm install・ブラウザの導入と重ね、
  `/health/ready` を待ち、`Failed to find … theme` がログに無いことを確かめてから Playwright を走らせる。必須チェックにしない。
- 新規 `src/platform/frontend/playwright.keycloak.config.ts`: project `keycloak-light` / `keycloak-dark`（`colorScheme`）。spec は `e2e/keycloak/*.kc.ts`。
  `E2E_KC_URL` が無ければ読み込みで落ちる。
- 新規 `e2e/keycloak/support.ts`: `expectKeycloakAxeClean(page, surface, testInfo)`（タグは `WCAG_22_AA_TAGS` を輸入・`runOnly` と合格判定 1 件以上・
  `--color-bg` が `tokens.css` のその明暗の値であること・指紋を台帳と一致）、`deadKeycloakKnownViolationKeys`、管理 API による走査用の利用者の作成と削除、TOTP。
- 新規 `e2e/keycloak/axeKnownViolations.ts`（台帳。0 件）、`e2e/keycloak/sc13-16-theme-a11y.kc.ts`（15 面）。
- `e2e/support/axe.ts`: `WCAG_22_AA_TAGS` と `THEMES` を export する（定義は動かさない）。
- テーマ: `login/theme.properties` に `scripts=js/platform-a11y.js`、新規 `login/resources/js/platform-a11y.js`（ラベルの付け替え）。
  `deploy/local/infra/keycloak.yaml` の ConfigMap の `items` に 1 行。
- `src/platform/frontend/tsconfig.node.json`（型検査の対象に新 config）、`src/knip.jsonc`（新 config と spec を entry に）。
- `docs/tests/E2E_STANDARDS.md` §7・§11・§12 の「Keycloak のテーマの画面は対象外」を改める。

## 走査面（15 面 × 2 テーマ）

| 画面 | 面 | 状態の作り方 |
| --- | --- | --- |
| SC-13 | ログイン | アカウントコンソールを未認証で開く |
| SC-13 | 認証エラー | 存在しない利用者名で失敗（固定文言を確かめる） |
| SC-15 | 申請 | 「パスワードを忘れた」 |
| SC-15 | 申請後 | 存在しない利用者名で申請（「メールを送信しました」） |
| SC-14 | 初回登録 / 初回登録（手入力キー） | 必須アクション CONFIGURE_TOTP を持つ走査用の利用者でログイン |
| SC-15 | 新パスワードの設定 | 続く必須アクション UPDATE_PASSWORD（メールのリンクと同じ login-update-password.ftl） |
| SC-16 | 個人情報 / サインイン / デバイスの活動 | 登録後に送られるアカウントコンソール |
| SC-14 | ワンタイムコード / コードの誤り | cookie を消して再ログイン |
| 共通 | Keycloak エラー画面 | 実在しないクライアントで認可エンドポイント |

## 母集合（規則 9・10）

- 「Keycloak のテーマの画面は対象外」と書いた生きた文書を `grep -rn -E "Keycloak.{0,20}(テーマ|theme).{0,40}(対象外|走査|axe|機械検査)|axe.{0,60}Keycloak|L2 以降"` で
  `docs/`・`scripts/README.md`・`README.md`・`src/platform/frontend` から引いた → `docs/tests/E2E_STANDARDS.md` の 2 か所（§7・§12）。§11 の CI の配線の表にも行を足す。
- 凍結記録（IADR-0614 の決定 3・結果、仕様書 `20261024_2015_axe-all-screens`）は書き換えない。
- テーマにファイルを足すと `keycloak.yaml` の `items` が要る（IADR-0532 §結果）→ 足した。`scripts/k8s-local-up.test.js` が集合の一致を見る。

## 検証

- 手元で Keycloak 26.7.4 を起動し、`E2E_KC_URL=http://localhost:18080 … playwright test -c playwright.keycloak.config.ts` → **10 passed**（4 test × 2 project ＋ 台帳の整合 × 2）。
- 初回の走査の違反（直す前）: `label #totp`・`label #userLabel`（SC-14 初回登録・手入力キー）、`label #kc-attempted-username`（SC-14 ワンタイムコード）。両テーマ。
- 変異（赤になることの確認。元に戻した）:
  - `scripts=js/platform-a11y.js` を外す → `label` が SC-14 の 4 面 × 2 テーマで落ちる。
  - `tokens.css` のライトの `--color-fg-muted` を `#b8bccb` に薄める → `color-contrast` がライトの SC-13〜15・エラー画面の全面で落ちる。
- 手元で走らなかったもの（ディスクの空きが 3 GB を切っているため、workspace の pnpm install をしていない）: eslint・knip・型検査の全体。
  新しいファイルだけを最小の依存で `tsc` に掛けた（Playwright の型の版のずれによる既存と同じ 1 件を除き、エラーなし）。CI の frontend.yml が見る。

## PoC での確認手順（テーマを変えたため）

- テーマの配備は Keycloak の再配備を伴う。**市場が閉まった後**に行う（Keycloak の再起動中は全サービスのトークン検証が一時的に 401 になる）。
- テーマの ConfigMap を作り直す（`deploy/local/README.md` の「テーマ配下の全ファイルを載せる」手順。新しいキー
  `login__resources__js__platform-a11y.js` が入る。`scripts/k8s-local-up.sh` の [3/7] も同じことをする）→ `kubectl apply -k deploy/local/infra`
  （`keycloak.yaml` の `items` が変わり、Pod が作り直される）→ `kubectl -n platform-infra rollout status deploy/keycloak`。
- 確認: `curl -s https://<auth host>/realms/platform/protocol/openid-connect/auth?client_id=platform-spa&response_type=code&redirect_uri=… | grep platform-a11y.js`
  が script タグを返す。OTP の登録画面で「ワンタイムコード」のラベルを押すと入力欄にフォーカスが移る（`for` が `totp` を指す）。

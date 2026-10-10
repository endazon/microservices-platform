---
title: IADR-0532 Keycloak の画面を SPA と同じ Nocturne へ揃える —— 親テーマを keycloak.v2 / keycloak.v3 へ改め、色は @platform/ui のトークンから生成して CI で同期を守る（テンプレートは複製しない・明暗は OS 設定に従う）
type: impl-adr
status: Accepted
related_ids: [SC-13, SC-14, SC-15, SC-16, ADR-0026, ADR-0031, ADR-0045, NFR-12, IADR-0261, IADR-0435, IADR-0524]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（§SC-13〜16。「Keycloak のテーマとして実装する」）
  - planning:projects/microservices-platform/05_screens/mockups/hi-fi/sc-13.html〜sc-16.html（Nocturne）
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
  - planning:projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
related_specs:
  - ../specs/20261010_sc13-keycloak-theme-nocturne.md
---

# IADR-0532: Keycloak の画面を SPA と同じ Nocturne へ揃える

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-10-10
- 決定者: Claude（実装）／起点は利用者の指摘「Keycloak の画面が既定のままでデザインが統一されていない」

## 起点・関連

- 関連する計画書 ID: SC-13〜16（Keycloak のテーマとして実装）・ADR-0026（認証画面は Keycloak のテーマで提供）・
  ADR-0031（UI = Tailwind v4 / Nocturne・Lucide）・ADR-0045（リセットメール）・08_data-egress-policy・INDEX 決定 21（色だけで意味を持たせない）
- 関連する実装 ADR: [IADR-0261](IADR-0261_keycloak-theme-and-smtp-injection.md)（決定 1 = `parent=keycloak` ＋ CSS のみ。**本 IADR が親の指定を改める**）・
  [IADR-0435](IADR-0435_nocturne-token-layering-and-theme-switch.md)（SPA トークンの二層化・明暗）・
  [IADR-0524](IADR-0524_keycloak-26-upgrade.md)（Keycloak 24 → 26.7.4）
- 関連する実装仕様書: [20261010_sc13-keycloak-theme-nocturne](../specs/20261010_sc13-keycloak-theme-nocturne.md)

## コンテキストと課題

Keycloak 26.7.4 の配布物を手元で起動し、実 realm と現行テーマで確かめた（作業仕様書 §調査結果）。

1. **ログイン**: `parent=keycloak` は 26 では旧 v1（PatternFly 3）を指す。画面は Keycloak の既定（多角形の背景画像・白カード・青ボタン）のままで、
   追加 CSS が変えていたのは見出しの色と上辺の線だけだった。26 の既定は `keycloak.v2`（PatternFly 5）である。
2. **アカウント**: 🔴 26.7.4 の `keycloak` テーマは account 型を持たない。`parent=keycloak` の account テーマは解決できず、Keycloak は
   `Failed to find ACCOUNT theme platform, using built-in themes` を出して**素の `keycloak.v3` へ黙って落ちていた**。IADR-0524 の版上げで壊れたが、
   `check-realm-constraints.js` 検査 4 は parent の**宣言の有無**しか見ておらず、通していた。
3. **メール**: テーマ未指定（素の HTML）。
4. SPA は別オリジン・別ビルドであり、色を手で写すと SPA 側の是正（IADR-0435 決定 4 の accent の持ち上げ等）が届かない。

決めるべきこと: (a) 作り込みの方式、(b) 色の単一情報源とずれの止め方、(c) 明暗の決まり方、(d) どの画面・種別までを対象にするか。

## 検討した選択肢

| # | 案 | 評価 |
| --- | --- | --- |
| A | **親を `keycloak.v2`（login）/ `keycloak.v3`（account）/ `keycloak`（email）へ改め、CSS（PF5 変数の写像＋部品の上書き）・最小のメッセージ・ロゴ・メールの外枠マクロだけを持つ**（採用） | IADR-0261 決定 1 の理由（本体のテンプレート更新へ追随）を保ったまま、PF5 の変数経由で全画面に効く |
| B | `.ftl` を複製して SPA の DOM に寄せる | 見た目の自由度は最大だが、複製した瞬間に Keycloak のセキュリティ修正・新フロー（パスキー等）から切り離される |
| C | Keycloakify 等でテーマを React からビルドする | ビルド工程・依存（npm）が増え、Keycloak の版ごとに追随が要る。CSS で目的（同じ見た目）を満たせる以上、過剰 |
| D | 親は `keycloak` のまま CSS を足す | v1（PF3）の DOM に依存し、26 の既定（v2）と乖離していく。account はそもそも解決できない |

色の持ち方:

| # | 案 | 評価 |
| --- | --- | --- |
| a | **生成**: `src/packages/ui/src/styles.css` から `tokens.css` を作り、CI が `--check` で差分を止める（採用） | 単一情報源。SPA の是正がそのまま届く |
| b | 手で写す ＋ 目視 | ずれは静かに起きる（別オリジンで誰も並べて見ない） |
| c | SPA のビルド成果物の CSS を Keycloak へ配る | Tailwind の出力全体を持ち込むことになり、PF5 との衝突・サイズが問題になる |

## 決定

### 決定 1: 親テーマを種別ごとに改め、テンプレートは複製しない（IADR-0261 決定 1 の「親」の部分を改める）

- login = `keycloak.v2`、account = `keycloak.v3`、email = `keycloak`。持つのは CSS・計画が文言を定めたメッセージ・アカウントのロゴ／favicon（SVG）・
  メールの外枠（`email/html/template.ftl` の `emailLayout` マクロ 1 個）だけ。
- **メールの外枠は唯一のテンプレート上書きである。** 親の `template.ftl` は `<html><body><#nested></body></html>` だけのマクロで、
  追随すべき本体の変更が実質無い。本文・テキスト版・各メールのテンプレートは親のまま（ADR-0045 決定 7 の検査の前提を動かさない）。
- 🔴 **親の実在を検査する。** `check-realm-constraints.js` 検査 4 に、Keycloak の版ごとの同梱テーマ表（`KEYCLOAK_BUILTIN_THEMES`。26.7.4 の
  `META-INF/keycloak-themes.json` から引いた）を足し、realm が名指すテーマの parent がその種別で実在しなければ落とす。
  **Keycloak を上げたら表を引き直す**（表と版の値を同じ場所に置いた）。`emailTheme` も対象に加えた。
- **表の版と起動するイメージの版を結ぶ**（#1893 監査）。表を実測した版（`KEYCLOAK_VERSION_FOR_THEMES`）と、Keycloak のイメージを宣言する
  2 箇所（`deploy/docker-compose.yml`・`deploy/local/infra/keycloak.yaml`）のタグが食い違えば、同じ検査器が「表を引き直せ」と言って落ちる。
  マニフェストが無い・タグが読めない場合も黙って通さない。タグだけを上げて表が古いまま緑になる経路を塞ぐ。

### 決定 2: 色・角丸・フォントは `@platform/ui` のトークンから生成し、CI で同期を守る

- `scripts/gen-keycloak-theme-tokens.js` が `styles.css` のダーク（最初の `:root`）とライト（`:root[data-theme='light']`）の意味トークン 14 個と、
  `@theme` のフォント・角丸を `login` / `account` の `tokens.css` へ書く。CSS 本体（`platform.css`）は**意味トークンだけを引き、16 進の色を書かない**。
- 値を直書きするしかないファイル（メール外枠＝ライトのみ／アカウントのロゴ・favicon の SVG＝ダーク∪ライト）は、16 進の色が意味トークンの値に
  含まれることを `--check` が確かめる。
- **「16 進の色を書かない」も `--check` が確かめる**（#1893 監査）。`deploy/keycloak/themes` 配下のすべての `platform.css` について、コメントの外の
  宣言の値に 16 進の色があれば落とす（`#kc-header` のような ID セレクタは見ない）。
- **生成が読まない側の明暗の対も突き合わせる**（#1893 監査）。生成はライトを明示ライト（`:root[data-theme='light']`）から読むが、Keycloak が従うのは
  OS 設定＝ system ライト（`@media (prefers-color-scheme: light)` の `:root:not([data-theme='dark'])`）の値である。`--check` は明示ライトと system ライト、
  既定ダーク（`:root`）と明示ダーク（`:root[data-theme='dark']`）が同じ宣言を同じ値で持つことを確かめ、ずれを赤にする。
- CI（static-checks）に `--self-test` と `--check`、テーマ配下の egress 走査（`check-static-egress.js --require deploy/keycloak/themes`）を置いた。
  走査はメールの外枠（`.ftl`）と HTML を値に持つ文言（`messages_*.properties` の `loginTitleHtml` 等）も対象にする —— 当初は拡張子で漏れていた
  （#1893 監査）。拡張子の追加は検査器全体に効くが、SPA・カタログ・プラグインの成果物にはこの 2 種が無く、走査の結果は変わらない（実測）。

### 決定 3: 明暗は OS 設定（`prefers-color-scheme`）に従い、Keycloak 側に切替ボタンを置かない

- SPA の「表示」切替（IADR-0435 決定 3）は localStorage に保つが、Keycloak は別オリジンで読めない。ボタンを置くにはテンプレートの複製が要る（決定 1 に反する）。
- SPA の既定は system（OS に従う）であり、**切替を一度も触っていない利用者には SPA と同じ見え方になる**。明示ダーク／ライトを選んだ利用者だけ、
  OS 設定と異なれば認証画面で反対の明暗を見る。これは受け入れる。
- 明暗の切替は `tokens.css` のメディアクエリだけで行い、PF5 の `pf-v5-theme-dark`（親のスクリプトが付ける）にも同じ値を当てる
  （スクリプトが動かなくても配色は OS に従う）。

### 決定 4: 状態表示のアイコンは Lucide（SPA と同じ図柄）に差し替え、色＋アイコン＋テキストを揃える

- 親の Font Awesome を、`kcFeedback*Icon` / `kcInputErrorIconClass` のプロパティでクラスを付け替えて Lucide の SVG（data URI の CSS mask）にする。
  外部から取りに行かない。テンプレートは文言（`message.summary` / 入力エラーの本文）を必ず出すので、テキストは常に併記される。

### 決定 5: 文言の上書きは計画が文言を定めたものに限る

- SC-13: 「社員ID またはパスワードが正しくありません」（失敗時の固定文言。存在秘匿＝実在・非実在の両側に同じく効く）・「社員ID または メールアドレス」・
  「このデバイスを記憶（30日）」（日数は realm の `ssoSessionMaxLifespanRememberMe` と一致することを `--check` が確かめる）・「パスワードを忘れた」・見出し「サインイン」・
  送信ボタン「ログイン」（モックどおり。親の `doLogIn` はワンタイムコードの確定ボタンにも使われ、SC-14 のモック「認証してログイン」と語が揃う）。
- SC-15: 申請後の「メールを送信しました」。
- **モックと異なる文言を 2 つ意図して残す**（SC-15 の申請画面。#1893 監査）。いずれも親のテンプレートが語の鍵を決めており、文言だけでは分けられない
  （分けるにはテンプレートの複製が要り、決定 1 に反する）:
  - 識別子のラベルは「社員ID または メールアドレス」（モックは「メールアドレス」）。親は realm の設定（メールでのログイン可・メールを利用者名にしない）から
    SC-13 と同じ鍵 `usernameOrEmail` を引く。Keycloak の申請は実際に社員ID でも受け付けるので、ラベルは挙動と一致している。存在秘匿の文言
    （申請後は常に「メールを送信しました」）は変えていない。
  - 送信ボタンは「送信」（モックは「リセットメールを送信」）。親の鍵 `doSubmit` はパスワード更新・OTP 登録など多数の画面の確定ボタンと共有で、
    上書きすると他の画面の語が誤る。
- ヘッダ: realm の `displayName`（固有名詞。ロケールで差し替えない）＋副題（統合認証 / Single sign-on）。

### 決定 6: 対象外

- 管理コンソール（`master` realm・admin テーマ）—— 利用者の画面ではない。
- AST 専用 realm（`ai-stock-trading`）—— 単独 E2E 用で、連結配備の利用者は platform realm で認証する。AST リポジトリは変えない。
- 新パスワード画面の「ポリシー表示」・全セッション失効の確認（SC-15 の残件）—— 見た目ではなく機能の作り込み。

## 理由

- 決定 1 は IADR-0261 決定 1 の理由をそのまま保つ（複製しない）うえで、26 の既定の DOM（PF5）に乗る唯一の形である。PF5 はグローバル変数で
  色・枠・フォントを配るので、変数の写像だけで必須アクション・WebAuthn・エラー等、テーマが個別に知らない画面にも効く。
- 決定 2 は「別オリジンの画面は並べて見られないので、ずれは誰にも気付かれない」ことへの機械的な対策である。
- 決定 3 は「押せるボタンを置くために本体から切り離す」より「既定の利用者に同じ見え方」を選んだ。

## 結果

- 良い影響:
  - 認証画面・アカウント設定・リセットメールが SPA と同じ地の色・面・枠・accent・角丸・フォントで描かれる（ライト／ダーク・ja／en を手元の
    Keycloak 26.7.4 でスクリーンショット確認）。
  - **壊れていた account テーマが解決されるようになった**（`Failed to find ACCOUNT theme` が出なくなった）。同型の再発は検査 4 が止める。
  - Font Awesome の Web フォントを状態表示に使わなくなった（自己ホストではあるが、SPA と図柄が揃う）。
- 悪い影響・トレードオフ:
  - PF5 のクラス名・変数名に依存する。Keycloak の版上げで DOM が変わると、崩れるのではなく**既定の見た目へ部分的に戻る**（未使用の規則は無害）。
    版上げの際はスクリーンショットで確かめる（運用仕様書 §Keycloak テーマ）。
  - 明示的に明暗を選んだ利用者は、OS 設定と違えば認証画面で反対の明暗を見る（決定 3）。
  - k8s ローカルの ConfigMap は `items` で 1 ファイルずつマウントするため、ファイルを足すと `keycloak.yaml` の `items` も足す必要がある
    （集合の一致は `k8s-local-up.test.js` が実ファイルから固定する）。
- フォローアップ:
  - 🔴 **アカウントコンソール（SC-16）は realm の宣言のままでは API が 401 になる**（本 IADR の範囲外の既存の不具合。手元の Keycloak 26.7.4 で実測）。
    realm が独自に定義した `roles` スコープに audience 解決・クライアントロールの写像が無く、`account-console` に `basic` スコープ（`sub`）が付かず、
    利用者に `account` の `manage-account` が無い（`defaultRole` 無し）。見た目の確認は手元でこの 3 点を補って行った。別 issue（#1894）で扱う。
  - Keycloak を上げるときは `KEYCLOAK_BUILTIN_THEMES` を引き直し、ログイン・OTP・リセット・アカウントのスクリーンショットを撮り直す。

## 関連

- Supersedes: なし（IADR-0261 決定 1 の「親を `keycloak` とする」部分だけを改める。CSS で揃え、テンプレートを複製しない方針は維持する）。
- Superseded by: なし。

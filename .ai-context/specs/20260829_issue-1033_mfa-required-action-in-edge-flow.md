---
title: 作業仕様書 — MFA 必須化で止まったエッジ導線の検証を通し切る（#1033 / #438 の後始末）
type: spec
status: done
related_ids:
  - FR-05
  - NFR
  - SC-13
  - ADR-0026
  - ADR-0032
  - IADR-0294
author: implementation-agent
created: 2026-08-29
updated: 2026-08-29
---

# 作業仕様書 — MFA 必須化で止まったエッジ導線の検証を通し切る（#1033）

> **手順の逸脱を記録する。** 本仕様書は**着手後に書いた**。CLAUDE.md は着手前を求めており、
> 守れていない。原因の切り分け（CI ログの取得と読み）と実装が地続きになったためである。
> 母集合（§2）は**実装後に引き直し、その結果 2 件の追加対象が出た**（§2 軸 3）。
> **着手前に引いていれば、実装の範囲が最初から正しく決まっていた。**

## 1. 事実（実測）

`develop` の **Integration Stack** run 33200749231（`5e3b1e0`）が、段 13
「Gate — ABAC の正常系と検索 seed の入口条件が観測できる」で落ちた。

```
[4/19] 資格情報を POST し、redirect の認可コードを取る
  FAIL  認可コードを取得できない（ログイン失敗、MFA の段で止まった、または redirect_uri 不一致）:
        Location=https://keycloak.localhost/realms/platform/login-actions/required-action
                 ?execution=CONFIGURE_TOTP&client_id=platform-spa&tab_id=o9bEw7hzSzM
結果: PASS 3 / FAIL 1
```

**スタックは上がっている**（段 10 の門・ABAC seed・検索 seed はいずれも成功）。
落ちているのは**認証導線だけ**であり、原因は #1043（波 A）で入れた MFA の実効化である。

🔴 **これは「MFA が働いている」ことの証拠でもある。** `CONFIGURE_TOTP` が未設定の利用者は
パスワードだけでは通れない —— #438 が直そうとしたことがそのとおり起きている。
**直すべきは realm ではなく検証器の側である。**

### 🔴 波 A は TOTP の段を書いていた。しかし一度も実行されていなかった

`verify-oidc-edge-flow.sh` には `# ---- MFA（TOTP）の段` が既にあり、`scripts/lib/totp.js`
（RFC 6238・テストベクタ 5 件つき）まで用意されていた。**それでも通らなかった。**
欠陥は 2 つある。

| # | 欠陥 | なぜ通らないか |
| --- | --- | --- |
| 1 | **資格情報 POST の応答本文を grep していた** | その応答は **302 で本文が空**である。TOTP の画面は `Location` の先（`login-actions/required-action`）にある。**分岐条件が偽になり続け、MFA の段へ一度も入らなかった** |
| 2 | **hidden の `totpSecret` を送っていなかった** | `login-config-totp.ftl` は `totp`（コード）と **hidden `totpSecret`（生の値）** と `mode` を要求する。画面に出る `kc-totp-secret-key` は **base32 で別物**であり、計算には使えても POST の値にはならない |

**書いた分岐が死んでいることは、実行されるまで分からない。** 実 Keycloak はこの環境に無く、
波 A の時点で実走の手段が無かった（それ自体は事実だが、**「書いたので通る」と扱ったのが誤り**である）。

## 2. 母集合（引き直し。除外理由つき）

**問い**: 「MFA の必須化で通らなくなる認証経路は他に無いか」。

### 軸 1 —— `grant_type=password`（直接グラント）の全走査

```
$ git grep -n "grant_type=password\|grant_type\": \"password\|password_grant" -- . ':!src/ai-stock-trading'
```

2 件。いずれも文書・README で、実行経路ではない。

### 軸 2 —— トークン／認可エンドポイントを叩くファイルの全走査

```
$ git grep -ln "openid-connect/token\|openid-connect/auth" -- . ':!src/ai-stock-trading'
```

13 件。実行される 5 件を個別に読んだ。

| ファイル | 取得方式 | 判定 |
| --- | --- | --- |
| `scripts/verify-oidc-edge-flow.sh` | 認可コード + PKCE（対話利用者） | **本件で修正** |
| `scripts/seed-abac-policies.js` | `client_credentials`（`abac-seeder`） | 影響なし（#438 で既に人のグラントをやめている） |
| `scripts/seed-search-documents.js` | 同上 | 影響なし |
| `scripts/measure-abac-combinations.js` | `grant_type=password` / **`realms/master`** の `admin-cli` | **除外**。必須アクションを付けたのは `platform` realm の利用者であり、master realm のブートストラップ管理者は本件の対象外である |
| `perf/k6/lib/config.js` | `grant_type=password` / `realms/platform` の `poc-user` | 🔴 **影響あり**（軸 3 で捕まえた。下記） |

`KeycloakIdentityAdminClient.cs` とその試験は **`client_credentials`** であり影響しない。

### 軸 3 —— 対話利用者の資格情報を持つファイルの全走査

```
$ git grep -ln "Developer-2026\|PocOperator-2026\|PocUser-2026" -- . ':!src/ai-stock-trading'
```

🔴 **この軸だけが `perf/k6/lib/config.js` を出した**（軸 2 の一覧には居たが、既定利用者名が
`poc-user` で資格情報が env 経由のため、軸 1 の検索語では出ない）。
**軸を 1 本で終わらせない**（母集合規則 5）の実例である。

## 3. 直したもの

### (a) `verify-oidc-edge-flow.sh` —— 必須アクションの画面を追う

`code` が空で `Location` が `/login-actions/` を指すなら、**その画面を GET して本文を取り直す**。
さらに redirect されたら（＝要求が既に満たされている）そちらを見る。

### (b) 画面の解析を `scripts/lib/keycloak-login-form.js` へ出す

**この環境で検査できる形にするためである。** 実 Keycloak は無いが、**画面の HTML を固定値として
与える検査**なら書ける。固定値は Keycloak 24 の base テーマ（`login-config-totp.ftl` /
`login-otp.ftl`）の要素をそのまま写した。

🔴 **submit / button は返さない。** 同じフォームに `cancel-aia`（登録の取り消し）が居るため、
「入力を全部送り返す」と書くと**成功と応答で見分けの付かない取り消し**になる。

### (c) `perf/k6/lib/config.js` —— 診断を実態へ合わせる

`poc-user` は `CONFIGURE_TOTP` を持つため、直接グラントは `Account is not fully set up` で拒まれる。
従前の失敗文言（「direct access grants の有効化と資格情報を確認」）は**原因を取り違えさせる**。
MFA を名指しし、`TOKEN` を与える運用を案内する。**経路自体は消さない**（MFA を課さない
計測専用クライアントを用意する選択肢を閉じないため）。k6 は CI では走っていない（走査で 0 件）。

## 4. 検出力の証拠（変異試験。無変異ベースライン対照つき）

`node scripts/scripts.test.js`（**660 tests**。追加分 5 件）。

| # | 変異 | 結果 | 落ち方 |
| --- | --- | --- | --- |
| M0 | 無変異（対照） | **660 passed** | —— |
| M1 | `submit` / `button` も fields に入れる | **KILL** | `cancel-aia が混ざっている`（actual false / expected true） |
| M2 | 実体参照をデコードしない | **KILL** | action の `&amp;` が残り、`execution=…&client_id=…` を含まない |
| M3 | 表示用 base32 を hidden の `totpSecret` で代用する | **KILL** | `actual: 'rawsecretvalue' / expected: 'GEZD GNBV GY3T QOJQ'` |

🔴 **M3 が本件の欠陥 2 そのものである** —— 2 つのシークレットを取り違えると
「コードが不正」で落ち、原因が MFA の設定なのか検証器なのか切り分けられなくなる。

## 5. 🔴 実測できないこと（「統制を定めた」と「統制が働いている」の書き分け）

**この環境に Docker が無いため、k3d の統合スタックも実 Keycloak も起こせない。**

- **(a) の redirect 追従が実際に TOTP の画面へ到達すること**は測っていない。**定めただけである。**
- **初回登録の POST が Keycloak に受理されること**も測っていない。フォームの要求（`totp` /
  `totpSecret` / `mode` / `userLabel`）は Keycloak 24 base テーマの当該テンプレートから確認したが、
  **受理の実測ではない。**
- 実走の証拠は **`develop` の Integration Stack でしか得られない。**
  → **#1033 はその実走が緑になるまで閉じない。**

検証器を実行して SKIP（終了コード 2）で終わることだけは実測した
（`bash scripts/verify-oidc-edge-flow.sh` → 「エッジへ到達できません」→ `exit=2`）。
**これは構文と前提判定が生きていることの確認であり、MFA の段の確認ではない。**

## 6. 触ったファイル

- `scripts/verify-oidc-edge-flow.sh`（必須アクションの追従・TOTP の POST 本文）
- `scripts/lib/keycloak-login-form.js`（新規）
- `scripts/scripts.test.js`（固定 HTML に対する検査 5 件）
- `perf/k6/lib/config.js`（診断文言）

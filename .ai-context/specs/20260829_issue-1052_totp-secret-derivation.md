---
title: TOTP の表示 base32 に依存せず、hidden の生シークレットから導出する（#1052）
type: spec
status: done
related_ids: [NFR-09, ADR-0026, IADR-0294]
author: Claude
created: 2026-08-29
updated: 2026-08-29
---

# #1052: OTP 段でシークレットを解決できない失敗を、依存の向きを変えて塞ぐ

## 1. 事象

`develop` `b85f462` の Integration Stack（run 33233171136）が**段 4/19** で落ちた。

```
[4/19] 資格情報を POST し、redirect の認可コードを取る
  FAIL  OTP の段に入ったがシークレットを解決できない（developer）。
```

**#1033 が解決して初めて到達した失敗**であり、退行ではない（それより手前で毎回落ちていた）。

## 2. 切り分け（コードの実測）

失敗は `verify-oidc-edge-flow.sh` の**「OTP 段だと判定した後」**の分岐で起きている。
`totpField` は取れている＝**フォームの解析は成功している**。空だったのは
`totpSecretEncoded`（`<span id="kc-totp-secret-key">` の抽出。`keycloak-login-form.js:70`）だけである。

`deploy/keycloak/microservices-platform-realm.json` は `developer` に
`"requiredActions": ["CONFIGURE_TOTP"]` を与えており（`CONFIGURE_TOTP` は `defaultAction: true`）、
**クラスタは毎 run 新規**（初回 seed が `既存 0 件`、ジョブ末尾で `k3d cluster delete`）である。
したがって画面は **(a) 初回登録**のはずで、**表示用 base32 の取り方が外れている**のが最有力。

## 3. 直し方 —— 表示要素への依存をやめる

🔴 **表示用の `<span>` は「人に見せるための描画」であって契約ではない。**
Keycloak のバージョン差・テーマ差で id も要素も変わり得る。**そこに TOTP の計算を依存させたのが誤り**である。

**hidden の `totpSecret` は契約側である** —— 登録を成立させるために**送り返す必要がある**値で、
現に本スクリプトは既に POST に載せている（無ければ登録自体が通らない）。

Keycloak の対応関係（`TotpBean`）:

| 値 | 中身 |
| --- | --- |
| hidden `totpSecret` | **生のシークレット文字列**（ASCII） |
| 表示 `totpSecretEncoded` | **その bytes の base32**（4 文字ごとに空白） |

既存のテスト装置がこの関係をそのまま持っている —— フィクスチャの
`<span id="kc-totp-secret-key">GEZD GNBV GY3T QOJQ</span>` は
**ASCII `1234567890` の base32 そのもの**である（本作業で実測して確かめた）。

→ **表示が取れないときは、生の値を base32 化して代用する。**

## 4. 変更点

1. `scripts/lib/totp.js` に **`base32Encode()`** を足し、`base32Decode` と対で公開する。
2. `scripts/verify-oidc-edge-flow.sh`: `totpSecretEncoded` が空で生 `totpSecret` があれば**導出**する。
3. 同スクリプトの FAIL メッセージへ **`otp_field` と生シークレットの有無**を載せる。
   **すでに変数へ入っているのに捨てていた** —— 次の実走で (a)/(b) が必ず分かるようにする。

🔴 **生 HTML は出さない。** session ID や資格情報を CI ログへ載せる副作用があるため、
**判別に要る 2 値だけ**を出す。

## 5. 母集合（規則 1・2・9）

**誤りの側の文字列** `kc-totp-secret-key` と `totpSecretEncoded` で追跡下を全走査した。

| 走査語 | 件数 | 内訳 |
| --- | --- | --- |
| `kc-totp-secret-key` | 6 | **機能 2**（`keycloak-login-form.js:12` コメント / `:70` 抽出） ＋ 記録 3 ＋ テスト 1 |
| `totpSecretEncoded` | 10 | **機能 5**（`keycloak-login-form.js` の 4 ＋ `verify-oidc-edge-flow.sh:263`） ＋ テスト 5 |

**除外したものと理由**:

- **記録 3 件**（`IADR-0294:145` / `20260828_issue-438...:127` / `20260829_issue-1033...:53`）は
  **凍結された記録**であり、後から本文を書き換えない。なお `IADR-0294:145` は
  **「検証器が Keycloak のログイン画面の HTML 構造に依存するようになった」とリスクを記録していた**
  —— **本件はそのリスクが顕在化したものである**。
- **テスト 6 件**は現行の抽出を固定するもので、抽出を消すわけではない（表示が取れるときは今までどおり
  使う）ため、**変更しない**。本作業は**代替経路を足す**のであって置き換えではない。

→ **手を入れるのは `verify-oidc-edge-flow.sh` の 1 分岐と `totp.js` の追加分だけ**である。
`keycloak-login-form.js` は**既に生の `totpSecret` を返しており、変更不要**（実測）。

## 6. 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（base32 の往復・導出の一致・RFC ベクタ）
- `bash -n scripts/verify-oidc-edge-flow.sh`（構文）
- 文書系検査一式

## 7. 🔴 途中で踏んだ事故（記録）

**最初の実装は動かなかった。** シェル側を

```sh
node -e '...require(process.argv[1]).base32Encode(process.argv[2])' "$SCRIPT_DIR/lib/totp.js" ...
```

と書いたが、**`node -e` の `require()` は相対パスを「モジュール名」として解決する**ため
`MODULE_NOT_FOUND` になる。**ライブラリの単体テストは通っていた** —— 壊れていたのは呼び出し側である。

🔴 **`2>/dev/null || printf ''` を付けていたため、失敗しても黙って空文字になり、
「シークレットを解決できない」という元の症状に戻るだけだった。** 検査を足していなければ、
**次の実走まで「直した」と誤認したまま進んでいた**。

→ 二重に是正した: **(a)** `-e` をやめ、既存の呼び出しと同じ「スクリプトを直接実行する」形
（`--encode` サブコマンド）へ揃える。**(b)** **シェルが実際に叩く形**（`execFileSync` で CLI を起動）
の検査を `scripts.test.js` へ足す。**ライブラリだけ試して満足しない。**

## 8. 🔴 実測できないこと

**この環境に Docker が無く、実 Keycloak を起こせない。** §3 の対応関係は
**Keycloak のテンプレート／Bean の契約とリポジトリ内フィクスチャから導いたもの**であり、
**実物の HTML で確かめてはいない**。

**だから §4-3（診断の出力）を同じ変更に入れる** —— 仮に導出が外れても、
次の実走が `otp_field` と生シークレットの有無を報告し、**(a)/(b) の判別と原因の切り分けが 1 回で進む**。
**「直った」と書けるのは Integration Stack が段 4 を越えてからである。**

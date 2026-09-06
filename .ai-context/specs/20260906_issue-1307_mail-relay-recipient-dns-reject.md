---
title: 近接 MTA が宛先ドメインの DNS 検証で投函を拒み、develop の integration-stack が恒常的に赤い
type: spec
status: done
related_ids:
  - SC-15
  - FR-05
  - ADR-0045
  - ADR-0078
  - IADR-0404
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs: []
---

# 作業仕様書: 近接 MTA の `reject_unknown_recipient_domain` を外す

## 事象（実測。#1307）

`686d5934`（PR #1305 / #1245）が Keycloak → Mailpit の直結の間に **Postfix の近接 MTA** を挟んで以降、
develop の `integration-stack` が**恒常的に赤い**。失敗ステップは
`node scripts/check-password-reset-mail.js`。

```console
[check-password-reset-mail] 4 件の失敗:
  - [T-17] リセット申請の応答が 500 である（期待 200）。
  - [T-17] 捕捉用 MTA に届いたメールが 0 通である（期待 1 通）。
  - [T-10] 応答ステータスが登録の有無で分かれている（実在 500 / 非実在 200）。
  - [T-10] 応答本文が登録の有無で分かれている。
```

同じ run の診断ダンプに真因が出ている。

```
postfix/smtpd[958]: NOQUEUE: reject: RCPT from 10-42-1-8.keycloak.platform-infra.svc.cluster.local[10.42.1.8]:
  556 5.1.10 <admin@example.com>: Recipient address rejected: Domain example.com does not accept mail (nullMX);
  from=<noreply@platform.localhost> to=<admin@example.com> proto=ESMTP helo=<keycloak-79fd6d7665-9nfdj>

org.keycloak.services KC-SERVICES0029: Failed to send email: jakarta.mail.SendFailedException: Invalid Addresses;
  SMTPAddressFailedException: 556 5.1.10 <admin@example.com>: Recipient address rejected: ... (nullMX)
```

**4 件の失敗はすべてこの 1 件の帰結である。** 送出が 500 で落ちる → 捕捉箱 0 通、
かつ**実在利用者だけが 500 になる**ので SC-15 の存在秘匿（T-10）も同時に破れる。

### いつから赤か（実測）

| | run | 日時 (UTC) | コミット |
| --- | --- | --- | --- |
| 最後の緑 | 34016876414 | 2026-09-06T06:35:12Z | `45748c3e` |
| 最初の赤 | 34028253172 | 2026-09-06T10:44:25Z | `686d5934` |

`git log --oneline 45748c3e..686d5934` は **1 本だけ**（`686d5934`）。以後 `c1dfb1eb` / `107fa1ee` も同じ
ステップで赤で、**緑に戻っていない（3 連続）**。`git rev-parse --is-shallow-repository` = `false` を確認済み。

## 🔴 これは `IADR-0404` が名前を付けていた窓 W2 が、予期しない原因で発火したものである

`IADR-0404` の 4 状態表は **C3「relay 稼働だが投函を拒む」→ 実在 500 / 非実在 200 → 窓 W2** を
既に書いている。ただし想定していた原因は「452 キュー満杯・554 **差出人**拒否」であり、
**宛先ドメインの DNS 検証**は挙げていない。

同 ADR の未確認事項 **U11** は次のとおりだった。

> 🔴 `ALLOWED_SENDER_DOMAINS` が `smtpd_sender_restrictions` へ反映され、外れた差出人が 554 で拒まれること。
> マニフェストのコメントはそう断定しているが、V4〜V10 と違い**上流ソースで確認していない**。

**本 PR で上流ソースを読み、U11 に答えを出した。答えは「反映先が違う」である。**

## 実測: 上流イメージ（`boky/postfix:v5.1.0`）が何をしているか

`bokysan/docker-postfix` の tag `v5.1.0` の `scripts/functions.sh` を読んだ（`gh api` で取得）。

`postfix_setup_sender_domains()` —— **`ALLOWED_SENDER_DOMAINS` を設定すると走る**（`functions.sh:573-592`）:

```sh
do_postconf -e "smtpd_recipient_restrictions=reject_non_fqdn_recipient, reject_unknown_recipient_domain, check_sender_access lmdb:$allowed_senders, $smtpd_sasl reject"

# Since we are behind closed doors, let's just permit all relays.
do_postconf -e "smtpd_relay_restrictions=permit"
```

`postfix_reject_invalid_helos()`（`functions.sh:333-338`）:

```sh
do_postconf -e "smtpd_sender_restrictions=permit_mynetworks,reject"
```

**判明した 3 つ:**

| # | 事実 | 帰結 |
| --- | --- | --- |
| 1 | 差出人ドメインの門は `smtpd_sender_restrictions` ではなく **`smtpd_recipient_restrictions` の `check_sender_access`** である | `IADR-0404` U11 の前提（＝マニフェストのコメント）は**反映先を取り違えていた**。W2 の検知が依存していた前提なので、ここは書き直す |
| 2 | `smtpd_sender_restrictions` は `permit_mynetworks,reject` であり、**Keycloak（Pod 網 = RFC1918）は無条件に通る** | 差出人の門はこちらでは効いていない |
| 3 | 🔴 同じ 1 行に **`reject_unknown_recipient_domain`** が入る。宛先ドメインの MX / A を DNS で引き、**null MX（RFC 7505）を 556 5.1.10 で RCPT 時に拒む** | **本件の原因。** `example.com` は IANA が null MX を公開している |

**`ALLOWED_SENDER_DOMAINS` を設定したこと自体が、宛先の DNS 検証を有効にしていた。**
`permit_mynetworks` はこの並びに**入っていない**ので、クラスタ内からの投函でも DNS 検証を受ける。

## 🔴 これは試験データの問題ではない。設計の問題である

**`admin@example.com` を別のドメインへ替えても直らない**（そして直すべきでもない）。

- `.localhost` / 架空のドメインは MX も A も無いので、同じ `reject_unknown_recipient_domain` が
  「Domain not found」で拒む。**null MX を避けても DNS 検証そのものは残る。**
- 🔴 **RCPT 時の拒否は、`ADR-0078` 決定 2 の「キューを持ち、上流が停止していても投函を受け付けて後送する」に反する。**
  近接 MTA を挟んだ目的は**上流の失敗を Keycloak の応答から切り離す**ことであり、
  relay 自身が同期的に拒めば、切り離したはずの失敗が戻る（＝状態 C3・窓 W2）。
- 🔴 **production でも同じ形で起きる。** 宛先ドメインの DNS が一時的に引けない・null MX を出す利用者が
  1 人でも居れば、**その利用者だけが 500 になり存在秘匿が破れる。** これは dev 固有の事故ではない。

**したがって直す場所は relay の受け入れ規則である。**

## 母集合（規則 1〜10）

基点 `origin/develop`（`107fa1ee`）。`git rev-parse --is-shallow-repository` = `false`。

### 軸 1: 誤りの側（`@example.com`）から引く —— **替えないことの確認のために引いた**

```console
$ git grep -n "@example\.com" -- . ":!src/ai-stock-trading" | wc -l
15  （8 ファイル）
```

内訳: `deploy/keycloak/microservices-platform-realm.json`（試験利用者 4）／
`deploy/local/wikijs-setup/{README.md,bootstrap.sh}`／`scripts/{check-landed-subjects.js,check-password-reset-mail.js,scripts.repo.test.js}`／
`src/knowledge/frontend/.../DataSourceManagementPage.test.tsx`／`Platform.Shared.Infrastructure.Tests/.../LogSanitizerTests.cs`。

**除外理由: 1 件も変更しない。** 上の「設計の問題である」のとおり、宛先ドメインを替えても
DNS 検証は残り、**production の同型の事故を隠すだけ**である。走査したのは「替える案を採らない」
判断の根拠を残すためである。

### 軸 2: 近接 MTA の受け入れ規則に触っている宣言

```console
$ git grep -n "ALLOWED_SENDER_DOMAINS|smtpd_.*_restrictions|postconf" -- deploy/ scripts/
deploy/mail-relay/mail-relay.yaml   ← 本件（env 1 箇所・init スクリプト 3 段）
```

**他に無い。** 陽性対照: `platform.localhost` は `deploy/keycloak/...realm.json` 1・
`deploy/mail-relay/mail-relay.yaml` 2・`scripts/check-realm-constraints.js` 2 で当たる（走査は空振りしていない）。

## 設計

### 変更 1: init スクリプトへ段 (4) を足し、`reject_unknown_recipient_domain` だけを取り除く

**列を書き写さない。** 上流が組み立てた現在値から**当該トークンだけを取り除く**。
書き写すと、上流が並びを変えたときに差出人の門（`check_sender_access`）ごと固定してしまう。

**取り除いたあとで 2 つを検査し、どちらかが破れたら起動しない（fail-closed）:**

1. `reject_unknown_recipient_domain` が消えていること（取り除けなかったら黙って通さない）
2. `check_sender_access` が残っていること（**差出人の門まで消していないこと**）

🔴 **`reject_non_fqdn_recipient` は残す。** 宛先の**構文**検査であり DNS を引かない。
🔴 **末尾の `reject` は残す。** これが無いとオープンリレーになる。

### 変更 2: `IADR-0404` へ日付つき追記（U11 の解決 ＋ W2 の実発火）

U11 を「未確認」から**実測で解決**へ移す。あわせて **C3 の原因欄へ「宛先ドメインの DNS 検証」を足す**
——「452 キュー満杯・554 差出人拒否」だけを挙げていたのは狭い。**窓の名前は正しかったが、
入口の数を数え違えていた。**

### 変更 3: `check-realm-constraints.js` へ静的検査を 1 つ足す

**本件はローカルで実走できない**（クラスタが要る）。宣言だけが残るので、
**宣言の側で「無効化を忘れた形」を止める**。

- `deploy/mail-relay/mail-relay.yaml` が `ALLOWED_SENDER_DOMAINS` を設定しているのに、
  init スクリプトに `reject_unknown_recipient_domain` の無効化が無ければ違反とする
- **0 件走査は fail-closed**（宣言が読めないなら「違反なし」と読まない。既存の `collectMailCaptureGaps` と同じ作法）

🔴 **これは「新しい検査器」ではなく既存検査器への 1 規則の追加である**（検査器の新設は
「同型の事故 2 回目」を条件とする規約に触れない）。

## 受け入れ基準（Given-When-Then）

**ローカルで確かめられるもの（すべて実測済み）:**

- [x] Given `deploy/mail-relay/mail-relay.yaml` から無効化の段を消す / When 静的検査を走らせる /
      Then **赤になる**（変異試験。実データで **3 件検出**を実測）
- [x] Given 宣言が読めない / When 静的検査を走らせる / Then **緑を返さない**（0 件走査の fail-closed。自己試験）
- [x] Given 語だけをコメントに残して段を消す / When 静的検査を走らせる / Then **赤になる**
      （🔴 マニフェスト全文で語を探す実装だと緑になる。init スクリプトの中だけを見る）
- [x] Given 差出人の門の確認を落とす / When 静的検査を走らせる / Then **赤になる**（並びを空にする実装を通さない）
- [x] Given `exit 1` を握り潰す / When 静的検査を走らせる / Then **赤になる**
- [x] Given 上流の並びの 4 つの形 / When 取り除きの sed を当てる /
      Then **トークンだけが消え、`check_sender_access` は残る**（4 形すべてで実測）
- [x] `--self-test` が緑（**96 → 105 件**。実測）／`REQUIRE_REPO_TESTS=1 scripts.test.js` **747 件**緑

**🔴 稼働クラスタでしか確かめられないもの（マージ後に実測した）:**

- [x] Given `ALLOWED_SENDER_DOMAINS` を設定した relay / When 起動する /
      Then `smtpd_recipient_restrictions` に `reject_unknown_recipient_domain` が**無い**
- [x] Given 同上 / When 起動する / Then `check_sender_access`（差出人の門）と末尾の `reject` は**残っている**
- [x] Given 取り除きに失敗する上流の並び / When 起動する / Then **起動しない**（fail-closed）
- [x] Given develop の `integration-stack` / When 走る / Then `check-password-reset-mail.js` が緑

★［2026-09-06 追記 / #1307］**実測した。赤→緑になった。**

`109d0bbd`（PR #1310）のマージが起こした run **34034907377 が `success`**（所要 **11 分**。
健全時の実測 11〜15 分の内側で、増分は無い）。門の生の出力:

```
[check-password-reset-mail] 受信: subject="パスワードのリセット" / 宛先=admin@example.com
[check-password-reset-mail] T-10: 実在=200 / 非実在=200（非実在の利用者名 no-such-user-bxtzrgtu は realm 宣言と突き合わせて不在を確認済み）
[check-password-reset-mail] OK: 申請 → 送出 → 捕捉用 MTA での受信 → 本文（リンクと有効期限のみ）、および実在／非実在の応答同値性（T-10）が成立している。
```

**宛先の `admin@example.com`（null MX）はそのままである** ＝ 宛先を替えずに直っており、
**原因が試験データではなく relay の受け入れ規則だった**ことが裏づけられた。
直前の 4 連続失敗（`686d5934` / `c1dfb1eb` / `107fa1ee` / `ac2269ec`）はここで止まった。

🔴 **上の 4 つは run 34034907377 が実測した。** relay の起動時の `postconf` の値そのものを
読んだわけではなく、**その帰結（556 が出ず、捕捉箱に 1 通届き、T-10 が同値である）**を見ている。
値そのものの確認は `kubectl exec` が要り、本 run では行っていない。
赤→緑を確かめてから #1307 を閉じ、本仕様書を `status: done` へ移す。
**「直したはず」で閉じない。**

## テスト方針

| 試験 | 何を見るか | 赤にする変異 |
| --- | --- | --- |
| `collectRelayRecipientPolicyGaps` 陽性 | 無効化の段がある宣言 → 違反 0 | 段を消す |
| 同 陰性 | 無効化の段が無い宣言 → 違反 1 | 検査を素通しにする |
| 同 fail-closed | 宣言が読めない → 違反 1 | `if (!exists) return []` にする |
| 同 対象外 | `ALLOWED_SENDER_DOMAINS` を設定していない宣言 → 違反 0 | 無条件に違反にする |

🔴 **relay の実挙動（556 が出なくなること）はローカルでは確かめられない。**
確かめるのは develop の `integration-stack` であり、**本 PR のマージ後に赤→緑を実測して #1307 に記録する。**
「直したはず」で issue を閉じない。

## 計画書との差異

- 差異なし。`ADR-0078` 決定 2（キューを持ち後送する）を**実装が満たしていなかった**のを満たす向きの修正である。

## 未決事項

- 🔴 **`reject_non_fqdn_recipient` を残すことで、構文が壊れた宛先は依然 RCPT で拒まれる**
  （＝窓 W2 の入口が 1 つ残る）。これは DNS と違い**利用者データの問題**であり、
  Keycloak 側の登録時に閉じるべきものである。**本 PR では閉じない。**
- `POSTFIX_maximal_queue_lifetime=30m` などキュー側の値は本件と無関係。触らない。

---
title: 作業仕様書 — externalsecret-keycloak-smtp.yaml を起動器へ配線し、SMTP 資格情報の供給経路を成立させる（#1102）
type: spec
status: in-progress
related_ids:
  - SC-15
  - SC-16
  - FR-22
  - NFR
  - ADR-0026
  - ADR-0045
  - IADR-0261
  - IADR-0332
author: claude
created: 2026-08-31
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
related_specs:
  - "20260823_issue-438_keycloak-theme-and-smtp.md"
  - "20260828_issue-438_keycloak-theme-k8s-local.md"
  - "20260830_issue-1107_bff-session-deploy-config.md"
issue: "#1102"
---

# 作業仕様書 — `externalsecret-keycloak-smtp.yaml` を起動器へ配線する（#1102）

## 0. 着手前に確かめたこと（3 点）

### 0-1. **SMTP は本当に要るのか** —— 要る。ただし #600 の申し送りは別の話である

統括からの申し送り「**#600 の担当が『SMTP は不要（AC-4 はむしろ SMTP が無い状態が前提の試験）』と
言っている**」の真偽を、#600 の原文で確かめた。**転記の過程で射程が落ちている。**

#600 の 4 番目のコメント（原文）:

> - **メール送出（②③ の補助経路）** —— SMTP 実環境が要る。**`blocked:env` に相当する**
> - 🔴 **しかし受け入れ基準 4 は「メールが送れなくてもアプリ内通知が届く」ことを求めている。**
>   つまり **AC-3 / AC-4 / AC-5 はいずれも SMTP 無しで実装・検証できる**（AC-4 はむしろ SMTP が
>   無い状態が前提の試験である）。**本 issue 全体を env で止める理由が無いため `blocked:env` も付けない。**

**この文が言っているのは「#600 の AC-3/4/5 は SMTP 無しで検証できる」であって、「SMTP は不要」ではない。**
同じ段落の 1 行目が「メール送出には SMTP 実環境が要る」と明言している。

**さらに、消費側が別である。**

| 消費側 | 何を読むか | #600 の申し送りが当たるか |
| --- | --- | --- |
| **`NotificationService`（FR-22）** | **`keycloak-smtp` Secret を読まない。** `Program.cs:50` が `UnconfiguredSmtpEmailTransport` を無条件で DI する。設定値も env も一切見ない | **当たる**（SMTP 無しで AC-3/4/5 が回る） |
| **Keycloak realm（SC-15 パスワードリセット）** | `keycloak-smtp` Secret を **runbook の `kcadm` 手順が読み**、realm の `smtpServer` へ反映する（IADR-0261 決定 2） | **当たらない。別の経路である** |

```console
$ grep -rn "keycloak-smtp" src/
（0 件。.NET 側にこの Secret の消費者はいない）

$ grep -n "EmailTransport" src/platform/backend/Services/NotificationService/Program.cs
50:builder.Services.AddScoped<IEmailTransport, UnconfiguredSmtpEmailTransport>();
```

**#1102 が扱うのは後者である。** そして**計画がこの配線を確定させている** ——
`ADR-0045` 決定 6（`Accepted`・原文）:

> **SMTP の資格情報は Vault で管理し、Kubernetes Secret として供給する。** マニフェスト・リポジトリへ
> 平文で置かない

**「Kubernetes Secret として供給する」が実行されていない**のが #1102 の事象である。**要否の判断は
実装側に無い。** なお本作業で増える秘匿値は **0 個**である（実値は空のまま。§4 参照）。

### 0-2. **無い状態で何が起きているのか** —— 3 つ起きている。うち 1 つは #1102 の射程外の重大な欠陥

🔴 **稼働 k3s で測った。`-k` は使っていない**（クラスタの CA を `--cacert` に与えた。Windows の curl は
schannel なので失効確認だけ `--ssl-revoke-best-effort` で緩めた。`ssl_verify_result=0`）。

**(a) 案内が嘘になっている**（issue 記載どおり・確認）

```console
$ kubectl -n platform-infra get externalsecret,secret keycloak-smtp
Error from server (NotFound): externalsecrets.external-secrets.io "keycloak-smtp" not found
Error from server (NotFound): secrets "keycloak-smtp" not found
```

`bootstrap.sh` の最終行はこの名前を確認対象として案内する。**案内どおり打つと必ず NotFound になる。**

**(b) realm の `smtpServer` は空のまま**（設計どおり。秘匿値を非コミットにしているため）

```console
$ kcadm.sh get realms/platform --fields realm,smtpServer,resetPasswordAllowed
{ "realm": "platform", "resetPasswordAllowed": true, "smtpServer": { } }
```

**(c) 🔴 SC-15 の「存在秘匿」が稼働環境で破れている —— 利用者名を 1 リクエストで列挙できる**

**これは無言の縮退ではない。逆に「存在する利用者のときだけ大声で落ちる」ことが漏洩になっている。**

```console
### 陽性: 実在する利用者
$ curl --cacert edge-ca.pem -X POST --data-urlencode "username=poc-user" \
    "https://keycloak.localhost/realms/platform/login-actions/reset-credentials?session_code=…"
code=500
本文: 「申し訳ございません Eメールの送信に失敗しました。しばらく時間をおいてから再度お試しください。」

### 陰性対照: 実在しない利用者（同じ realm・同じフロー・同じ器）
$ curl … --data-urlencode "username=no-such-user-zzz" …
code=200
本文: 「詳細な手順を記載したEメールをすぐに受信してください。」
```

```console
$ kubectl -n platform-infra logs deploy/keycloak | grep SEND_RESET_PASSWORD_ERROR
type="SEND_RESET_PASSWORD_ERROR" … error="email_send_failed" … username="poc-user"
KC-SERVICES0026: Failed to send password reset email:
  org.keycloak.email.EmailException: Please provide a valid address
```

**`docs/screens/SC-15_password-reset.md` は「存在秘匿（常に『メールを送信しました』）| する」と書いている。
測ると偽である。** 攻撃者は 500 と 200 の差だけで利用者名を列挙できる。

🔴 **本 issue の配線を入れても直らない。** `from` が空文字のままだと Keycloak は同じ
`Please provide a valid address` を投げ、同じ 500 になる。**分割起票した（§2）。**

### 0-3. **Vault に対象のシークレットが在るのか** —— **#1108 と同型。ESO 経路が 34 日死んでいる**

```console
$ kubectl -n platform-infra get externalsecret
NAME             STATUS              READY   LAST SYNC
grafana-oidc     SecretSyncedError   False   34d
keycloak-admin   SecretSyncedError   False   34d
postgres         SecretSyncedError   False   34d
（infra ns 6 本・MSP ns 5 本とも全滅）

$ kubectl get clustersecretstore
NAME            STATUS                  READY
vault-backend   InvalidProviderConfig   False

$ kubectl -n platform-infra get pod -l app=vault
vault-8dccbd4dd-hspkf   1/1   Running   17 (141m ago)   35d
```

**dev Vault はインメモリで 17 回再起動しており、`bootstrap.sh` が入れた k8s auth backend・policy・
role・seed が全部消えている。** `secret/msp/keycloak-smtp` も当然無い。**測る前に bootstrap を
やり直す必要がある**（§5）。

**あわせて実測: `bff-oidc` / `identity-admin-oidc` の ExternalSecret がクラスタに存在しない**
（#1114 / #1101 のマージ後にクラスタで `up` が回っていない）。**「クラスタに無い＝実装が無い」ではない。**

## 1. 射程

**入れるもの**

1. `scripts/k8s-local-up.sh` の `ESO=1` ブロックへ `externalsecret-keycloak-smtp.yaml` の apply を足す
   （infra ns の並びへ・常時）。案内文（確認コマンド）の `infra_es` へ `keycloak-smtp` を加える。
2. `eso_wait` の待ち合わせ対象へ `keycloak-smtp` を加える。
3. **「起動器から参照されない ExternalSecret マニフェストが存在しない」ことを機械で固定する**
   （`scripts/k8s-local-up.test.js`・**列挙を持たない**・0 件走査は fail-closed）。
4. 是正で嘘になる／既に嘘である案内文を直す —— `deploy/local/vault/eso/README.md`（★未配線）・
   `docs/operations/keycloak-smtp-relay-setup-runbook.md`（§2「★現時点は手動」）・
   `bootstrap.sh` の確認コマンド 2 行（MSP ns 側は本件と独立に既に古い。§3 で開示）。
5. `docs/screens/SC-15_password-reset.md` の「存在秘匿 | する」を実測に合わせて直す（0-2 (c)）。
6. 実装ADR `IADR-0332`。

**入れないもの**

- **realm への `smtpServer` の実値投入**（利用者裁定 2026-08-15「設定手順の整備までが限度」。issue の補足）。
- **存在秘匿の破れの是正**（0-2 (c)）。**分割起票する**（§2）。realm・認証フロー・テーマのどれを触るかの
  設計判断が要り、`scripts/` の 1 行とは別物である。
- **開発環境の捕捉用 MTA（ADR-0045 決定 9）の配備。** **分割起票する**（§2）。
- **`scripts/check-secret-injected-options.js` の拡張。** §3 で判断を書く。

## 2. 分割起票

| # | 起票 | 内容 | 理由 |
| --- | --- | --- | --- |
| A | **#1143**（`bug` / `priority:must`） | **SC-15 の存在秘匿が稼働環境で破れている**（実在利用者 500 / 非実在 200 で列挙可能） | セキュリティ欠陥。`smtpServer` が設定済みでも SMTP が落ちれば同じ形で再発する。恒久対策の設計が要る |
| B | **#1144**（`enhancement` / `infrastructure`） | **ADR-0045 決定 9 の捕捉用 MTA（Mailpit 等）が未配備** | 決定 9 が「開発環境では実送信しない。捕捉用 MTA を置く」と確定しているのに実体が無い。`docs/tests/SC-15` の T-10/T-16 が「手動（実環境）」で止まっている原因でもある |

**重複検索**（`gh issue list --state all --search …`。起票直前に 6 語で引き直した）:
「存在秘匿」「Mailpit」「SC-15」「列挙」「捕捉用 MTA」「user enumeration」。
**A・B に当たる既存 issue は無い**（#1102 自身と、親 #438 / #600 / #452、および CLOSED の
#140（SC-11 の別画面）・#578（SC-15 の仕様書を作った側）が当たるのみ）。

**両者とも #1102 とファイル領域が交差する**（A は `docs/screens/SC-15_password-reset.md`、
B は `bootstrap.sh` / `k8s-local-up.sh` / `k8s-local-up.test.js` / runbook）。
**本 PR のマージ後に着手する**（並列不可・直列化）。この宣言は各 issue 本文にも書いた。

### B の実測を引き直したときに、自分の下書きが誤っていた（記録）

下書きの時点では「候補名で全ファイルを走査 → **0 件**」と書いていた。**実際に走らせたら 2 件出た**
（`.ai-context/specs/20260816_issue-600_…` と runbook の「捕捉用 MTA（Mailpit 等。本 runbook の
対象外）」）。**どちらも「無い」と述べている散文であって配備物ではない**ため結論は変わらないが、
**測る前に数を書いていた**のが誤りである。配備物が在るならそこに在る `deploy/` で測り直し
（0 件）、同じ走査器・同じ範囲の陽性対照（`qdrant` = 16 件）と対で置いた。

## 3. 母集合の引き直し（[[IADR-0141]] 決定 1・`traceability.repo.md` 規則 9・10）

**issue 本文の「宣言ファイル領域」は 3 ファイル。引き直したら 5 ファイルになった。**

**軸 1（正しい語ではなく対象そのもので引く。パスから引き、拡張子で絞らない）**

```console
$ git ls-files | grep -v '^src/ai-stock-trading/' > /tmp/allfiles.txt   # 2831 ファイル
$ xargs -a /tmp/allfiles.txt grep -ln 'keycloak-smtp'
.ai-context/adr/IADR-0261_…  .ai-context/adr/IADR-0288_…
.ai-context/specs/20260823_issue-438_… .ai-context/specs/20260823_issue-600_…
.ai-context/specs/20260828_issue-1025_… .ai-context/specs/20260828_issue-438_…
deploy/local/vault/eso/README.md            ← 🔴 issue の宣言領域に無い
deploy/local/vault/eso/bootstrap.sh
deploy/local/vault/eso/externalsecret-keycloak-smtp.yaml
docs/operations/keycloak-smtp-relay-setup-runbook.md
docs/screens/SC-15_password-reset.md        ← 🔴 issue の宣言領域に無い
```

**軸 2（誤りの側の語で引く）**: `未配線` / `組み込まれていない` / `まだ組み込まれ` →
`deploy/local/vault/eso/README.md`（32・76・78 行）が当たる。**軸 1 だけでは「当たったが直す必要が
あるか」を判断し損ねる形だった。**

**軸 3（マニフェストのファイル名そのもの）**: `externalsecret-keycloak-smtp` → 上の集合の部分集合。
**新規は出なかった**（3 軸目で止まったのは、1・2 軸で live な文書を取り切れたためである）。

**軸 4（是正で新たに誤りになる自分の記述を引く・規則 10）**: 起動器が apply する ExternalSecret の
**数え**を持つ記述 ——

```console
$ ls deploy/local/vault/eso/externalsecret-*.yaml | wc -l            # 16
$ grep -o 'externalsecret-[a-z-]*\.yaml' scripts/k8s-local-up.sh | sort -u | wc -l   # 15
$ comm -23 <(ls …) <(grep …)
externalsecret-keycloak-smtp.yaml     ← ちょうど 1 本だけ落ちている
```

`k8s-local-up.sh` の案内文は「MSP ns は常時 9 本」と**数えを持っている**。本件は infra ns 側なので
MSP の数えは動かない。**infra 側の案内（`infra_es`）に `keycloak-smtp` を足すのが導出の更新である。**

**除外したものと理由**

| 除外 | 理由 |
| --- | --- |
| `.ai-context/adr/IADR-0261` / `IADR-0288` | **凍結記録**。IADR-0261 フォローアップ 1 は「別 issue」と書いており、その別 issue が本件である。本文を後から書き換えない（`traceability.repo.md` §凍結の射程） |
| `.ai-context/specs/` 4 件 | 同上（確定済みの作業仕様書） |
| `src/ai-stock-trading/` | submodule。本リポジトリの担当外 |
| `docs/tests/SC-15_password-reset.md` | T-10/T-16 の「`smtpServer` 設定済」という**前提は本作業で変わらない**（実値は入らない）。§2-B の射程 |
| `docs/screens/SC-13 / SC-14 / SC-16` | `smtp` を含むが `keycloak-smtp` の配線には触れていない（OTP・アカウント設定の文脈） |
| `scripts/check-secret-injected-options.js` | 下記 |

**`check-secret-injected-options.js` は本配線を見ていない。そして見せるべきではない。**

同スクリプトの母集合は **`*Options.cs` の doc コメント宣言**であり、突合先は
**helm の `secretKeyRef` env と compose の `${…}` env** である。`keycloak-smtp` には
**C# の Options クラスが無く、env としても注入されない** —— **人間が runbook の `kcadm` で読む**
（IADR-0261 決定 2）。列挙を持たない設計に「例外的な名前」を足すと、まさにその設計が壊れる。
**代わりに、本件の不変条件（起動器が全 ExternalSecret を参照する）を別の場所で固定した**（§1-3）。

**ついでに直す（開示）**: `bootstrap.sh` 最終行の `確認(MSP)` は `postgres-app` / `rabbitmq-app` /
`bff-oidc` / `identity-admin-oidc` の 4 本を落としている（#1012 / #1022 / #1107 / #1101 で増えた分）。
**本件と独立に既に嘘である。** 同じファイルの同じ 2 行、同じ「案内が嘘」という欠陥なので併せて直す。

## 4. 受け入れ基準と、どう測るか

| # | 基準 | 測り方 |
| ---: | --- | --- |
| 1 | `k8s-local-up.sh` の `VAULT=1` 経路に apply する行がある | 差分＋ドライラン器 |
| 2 | 稼働クラスタで `externalsecret,secret keycloak-smtp` が両方存在する | `kubectl get`（§5・陽性対照） |
| 3 | `SMTP_*` 未指定なら `from`/`user`/`password` が空・`host`/`port`/`starttls` が既定値 | Secret の値を長さで測る（**値そのものを出さない**） |
| 4 | `eso_wait` に `keycloak-smtp` が含まれる | 差分＋ドライラン器 |
| 5 | `bootstrap.sh` の案内どおり打って NotFound にならない | §5 |
| 6 | runbook に手で apply する手順が残っていない | 差分 |
| 7 | リポジトリのどこにも実値が現れない | `node scripts/check-default-credentials.js` |
| 8 | **陰性対照**: ExternalSecret を消すと Secret も消える（＝この配線が唯一の供給元である） | §5 |

## 5. 稼働クラスタでの実測手順（陽性対照と陰性対照を対で置く）

**ESO 経路が 34 日死んでいる（0-3）ので、まず復旧させてから測る。** 実行するのは
`k8s-local-up.sh` の `ESO=1` ブロックが呼ぶのと**同じ手順**である:
`bootstrap.sh` → `clustersecretstore-k8s.yaml` → 各 `externalsecret-*.yaml` の apply。

**🔴 `k8s-local-up.sh` 全体は実行しない。** `[2/7]` が 19 イメージを nerdctl で焼き直し、
`[4/7]`/`[6/7]` が infra と全サービスを rollout する。**稼働クラスタは他セッションと共有している**
（本セッション中にも realm の作り直しが観測されている）ため、共有物を止める測り方は採らない。
代わりに、**起動器の当該ブロックを checked-in のバイトのまま `sed` で切り出して実行する**
（手で打ち直した別のコマンドではない）。ブロックが `VAULT=1 ESO=1` で到達されること自体は
`scripts/k8s-local-up.test.js` のドライラン器が固定する。

## 6. 実装ADR

`IADR-0332`（配置・待ち合わせ・不変条件の置き場・射程の境界）。

## 7. 実測の記録（2026-09-02・稼働 k3s）

### 7-1. クラスタで何をしたか（最小復旧。**共有物を止めていない**）

セッション再開時、ホスト再起動で **全 Pod が再起動しており**（vault は RESTARTS 19、ESO は 21、
いずれも同時刻）、dev Vault のインメモリ状態は §0-3 と同じく消えていた。

🔴 **`bootstrap.sh` を丸ごとは実行していない。** 同スクリプトは `postgres` / `rabbitmq` /
`keycloak-admin` を**既定値で seed し直す**。この 3 本の ExternalSecret は `creationPolicy: Merge`
であり、**既定と稼働中の実値がズレていれば、そのまま稼働クラスタの資格情報を上書きして壊す**。
クラスタは他セッションと共有しているため、この賭けは採らない。**必要な 1 本だけを seed した。**

実行したのは以下（すべて `bootstrap.sh` の当該行と同一のコマンド）:

1. `vault auth enable kubernetes` ／ `vault write auth/kubernetes/config kubernetes_host=…`
2. `vault policy write eso-read -` < `policy-eso-read.hcl`
3. `vault write auth/kubernetes/role/eso …`
4. **`vault kv put secret/msp/keycloak-smtp …`（この 1 本のみ。`SMTP_*` は未指定＝空既定）**
5. `kubectl apply -f deploy/local/vault/eso/clustersecretstore-k8s.yaml`
6. `kubectl apply -f deploy/local/vault/eso/externalsecret-keycloak-smtp.yaml`（**本 PR が足した行**）

**この判断が正しかったことは測れている** —— §7-3 の AC-5 で `postgres` / `rabbitmq` /
`keycloak-admin` の Secret が **AGE 36d のまま**（＝上書きされていない）ことが出ている。

**追加でやった 2 つの操作（記録）**: `clustersecretstore/vault-backend` と
`externalsecret/keycloak-smtp` へ `force-sync` annotation を打った。ESO の再照合を待たずに
起こすためだけのもので、**宣言内容は変えていない**。

### 7-2. 途中で 1 度誤診しかけた（記録）

store を apply した直後も `InvalidProviderConfig` のままで、ESO のログには
`auth/kubernetes/login … Code: 403 … permission denied` が出ていた。**Vault の TokenReview 権限
（`system:auth-delegator`）か、ESO が鋳造するトークンの audience を疑った。**

**どちらでもなかった。** ESO SA のトークンを 2 通りの audience で鋳造して login を直接叩いたら、
**両方とも成功した**（`token_policies ["default" "eso-read"]`）。ログの 403 は **bootstrap を
やり直す前の記録**で、store の status が再照合されていなかっただけである。

🔴 **ログの最新行が「今」を指しているとは限らない。** 陽性対照（実際に login を通す）を置かずに
ログだけ読んでいたら、居ない不具合を直しに行っていた。

### 7-3. 受け入れ基準の判定

| # | 基準 | 判定 | 証跡 |
| ---: | --- | :---: | --- |
| 1 | `k8s-local-up.sh` の `ESO=1` 経路に apply する行がある | ✅ | 差分／ドライラン器の新テスト（列挙を持たない不変条件） |
| 2 | 稼働クラスタで `externalsecret,secret keycloak-smtp` が両方存在する | ✅ | `SecretSynced True` ／ `secret/keycloak-smtp Opaque 6`。着手前の NotFound と対 |
| 3 | `SMTP_*` 未指定なら `from`/`user`/`password` が空・`host`/`port`/`starttls` が既定値 | ✅ | `from=0 / user=0 / password=0 bytes`、`smtp.gmail.com` / `587` / `true` |
| 4 | `eso_wait` に `keycloak-smtp` が含まれる | ✅ | ドライラン器の新テスト（`wait --for=condition=Ready externalsecret/keycloak-smtp`） |
| 5 | `bootstrap.sh` の案内どおり打って NotFound にならない | ✅ | 案内(infra) の 5 本すべてが解決（NotFound 0 件） |
| 6 | runbook に手で apply する手順が残っていない | ✅ | 差分（§2 が「同期の確認」だけになった） |
| 7 | リポジトリのどこにも実値が現れない | ✅ | `check-default-credentials` OK（30 件走査・新規 0） |
| 8 | **陰性対照**: ExternalSecret を消すと Secret も消える | ✅ | `ownerReferences = ExternalSecret/keycloak-smtp`。削除で両方 NotFound → 再 apply で復旧 |

**本 PR が増やした秘匿値は 0 個である**（`from`/`user`/`password` はいずれも 0 バイト）。

### 7-4. 直していないこと（申し送り）

- **`postgres` / `rabbitmq` / `keycloak-admin` / `vault-oidc` は `SecretSyncedError` のまま**である
  （§7-1 のとおり意図的に seed しなかった。Secret 実体は 36 日前のものが残っており、実害は無い）。
  **次に `k8s-local-up.sh` を通しで回した人が `bootstrap.sh` ごと復旧させる。** 本 PR の射程外。
- `eso_wait` の `MSP_NS` 側が `bff-oidc` / `identity-admin-oidc` / `postgres-app` / `rabbitmq-app` を
  待っていない件（IADR-0332 フォローアップ 2）は**未着手**。「同型が 2 回起きたら」の 1 回目として記録に留める。

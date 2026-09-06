---
title: 近接 MTA（キュー付き Postfix）を Keycloak の隣へ置き、realm の smtpServer を宣言所有へ移す（#1245 PR-A）
type: spec
status: draft
related_ids: [SC-15, SC-10, FR-05, FR-22, NFR-09, ADR-0026, ADR-0045, ADR-0078, IADR-0261, IADR-0329, IADR-0332, IADR-0344, IADR-0347, IADR-0369]
author: Claude（実装）
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0078_existence-hiding-response-indistinguishability-and-nearby-mta.md
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
---

# 仕様書: 近接 MTA（キュー付き Postfix）を Keycloak の隣へ置き、realm の smtpServer を宣言所有へ移す（#1245 PR-A）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（認証・アカウント管理）／FR-22（通知。同じ送出基盤を将来使う）
- 画面（SC）: SC-15（パスワードリセット）／SC-10（運用ダッシュボード。本 PR では触らない）
- 関連 ADR: **ADR-0078**（本作業が降ろす裁定。決定 1〜5）／ADR-0045（決定 2-b・8 を ADR-0078 が部分改定）／ADR-0026（認証 UX の文言固定）
- 計画書リンク: `projects/microservices-platform/07_adr/ADR-0078_existence-hiding-response-indistinguishability-and-nearby-mta.md`
  （隣接クローン既定パス `../project-planning` 配下。読み取り専用で参照した）

🔴 **引用する計画 ADR の題目を実ファイル名で確かめた**（レンジ検査は題目の正しさを見ない）:

```console
$ ls ../project-planning/projects/microservices-platform/07_adr/ | grep -E "ADR-0026|ADR-0045|ADR-0078"
ADR-0026_authentication-ux-and-account-management.md
ADR-0045_mail-delivery-smtp-relay.md
ADR-0078_existence-hiding-response-indistinguishability-and-nearby-mta.md
```

## 目的・背景

ADR-0078 は「存在秘匿の統制＝応答の区別不能性（ステータス・本文・所要時間）」と定め、**go-live の送出経路に
キュー付きの近接 MTA を挟んで状態 C（送出先へ接続できない ＋ 申請が開いている）を Keycloak の応答から消す**
（決定 2）ことと、**投函できないときは機械で `resetPasswordAllowed=false` へ倒す門**（決定 4）を確定した。

#1245 は 5 段の作業を束ねているため PR を刻む。**本 PR は PR-A**（器の配備・realm の付け替え・所有権の整理・
runbook §3 の退役・宣言の門の付け替え・実装 ADR）である。観測（PR-B）・門（PR-C）・4 状態の実測（PR-D）・
ログイン経路の実測（PR-0）は本 PR の対象外である。

## 対象範囲

- 対象:
  1. `deploy/mail-relay/`（環境非依存の base。Postfix Deployment ＋ Service ＋ NetworkPolicy ＋ init ConfigMap）
  2. `deploy/local/infra/kustomization.yaml` が `../../mail-relay` を参照する
  3. realm の `smtpServer` を relay の Service へ付け替える
  4. `reconcile-realm.js`: `smtpServer` を**宣言所有**へ（状態 B を構造で消す）／`resetPasswordAllowed` を
     **条件つき門所有**（`GATE_OWNED_REALM_KEYS`）へ
  5. `scripts/keycloak-realm-reconcile.test.js` に試験を追加（変異試験つき）
  6. `scripts/check-realm-constraints.js` の `collectMailCaptureGaps` を relay／mailpit の 2 段へ付け替え
  7. runbook §3（kcadm PATCH）の退役と §0・§確認・§限界の書き直し
  8. `scripts/k8s-local-up.sh` の rollout 待ち・`deploy/local/README.md` の経路図
  9. SC-15 画面仕様書の 4 状態表と「残る窓」の書き直し
  10. 実装 ADR（IADR-XXXX）と、IADR-0332 / IADR-0344 / IADR-0347 / IADR-0369 への日付つき追記

- 対象外（PR に明記する）:
  - **PR-B**: `postfix_exporter` サイドカー・otel-collector の `prometheus` receiver・アラート・Grafana・SC-10
  - **PR-C**: `reset-gate` の Deployment と `manage-realm` を持つ機密クライアント（**利用者は権限を承諾済みだが別 PR**）
  - **PR-D**: 4 状態の実測（稼働クラスタと利用者の手が要る）
  - **PR-0**: ログイン経路の実測
  - `check-stack-ready.js` の G8b（relay の到達判定）: 本 PR は `k8s-local-up.sh` の `rollout status` で
    「立つこと」を担保する。到達判定の門は観測（PR-B）と同じ面であり、そちらへ寄せる
  - `deploy/local/vault/eso/bootstrap.sh`（**設計から意図的に外した**。§計画書との差異 D-1）

## 母集合の再導出（自分の走査・陽性対照つき・生出力）

`git rev-parse --is-shallow-repository` → **`false`**（以後の走査・`git log` 出典はこの確認の後に引いた）。

🔴 **`src/ai-stock-trading`（submodule）は全走査から除外した。** 別プロジェクトの成果物であり、本件の
母集合ではない。`git submodule update --init src/ai-stock-trading` を先に済ませている（`Platform.Bff` の
コンパイルに要る）。

### M-1. 現在の 4 状態の挙動を記録している場所

```console
$ grep -rl -E "状態 ?[BC]" --include=*.md --include=*.js --include=*.yaml . \
    | grep -v node_modules | grep -v "^./src/ai-stock-trading" | sort
./.ai-context/adr/IADR-0347_reset-existence-concealment.md
./.ai-context/specs/20260902_issue-1143_reset-existence-concealment.md
./docs/screens/SC-15_password-reset.md
./scripts/check-password-reset-mail.js
./scripts/check-realm-constraints.js
count: 5
positive control（同じ走査器で '存在秘匿'）: 211
```

- 追随する live 文書: `docs/screens/SC-15_password-reset.md`（4 状態表・「残る窓」）／`scripts/check-realm-constraints.js`（門のコメント）
- **追随しない（凍結）**: `IADR-0347`（**日付つき追記のみ**）／`.ai-context/specs/20260902_issue-1143_*`（**書き換えない**）
- `scripts/check-password-reset-mail.js`: 本 PR では**変更しない**（Admin REST 化と gate 認識は PR-C）
- 走査に出ないが追随するもの（規則 10 で引き直した）:
  `docs/operations/keycloak-smtp-relay-setup-runbook.md`（§0・§3・§確認 3・§限界）／`docs/tests/SC-15_password-reset.md`（本 PR では応答は変えないので据え置き）

### M-2. 3 つの門（IADR-0347 決定 2）の実体

```console
$ grep -rn "function collectResetConcealmentGaps\|function evaluateRuntimeConcealment\|^### 0\. \|^### 3\. 稼働中の realm" scripts/*.js docs/operations/*.md
scripts/check-password-reset-mail.js:200:function evaluateRuntimeConcealment(cfg) {
scripts/check-realm-constraints.js:820:function collectResetConcealmentGaps(realm, { realmName = AUTH_POLICY_REALM } = {}) {
docs/operations/keycloak-smtp-relay-setup-runbook.md:74:### 0. 🔴 先に申請を閉じる（存在秘匿を割らないため。**省略しないこと**）
docs/operations/keycloak-smtp-relay-setup-runbook.md:141:### 3. 稼働中の realm へ smtpServer を反映する（kcadm。realm.json は書き換えない）
positive control（未実装の門名 collectNearbyMtaGaps・期待 0）: 0
positive control（既存の門名 collectMailCaptureGaps・期待 >0）: 5
```

**3 門はすべて維持する**（ADR-0078 決定 5）。退役するのは runbook **§3**（kcadm PATCH）だけであり、
門 3（§0 の「先に閉じる」）は残す。

### M-3. SMTP 設定の在り処（凍結記録・生成物を除く）

```console
$ grep -rl -E "smtpServer|SMTP_HOST|keycloak-smtp|mailpit" --include=*.md --include=*.js --include=*.sh \
    --include=*.yaml --include=*.yml --include=*.json . | grep -v node_modules \
    | grep -v "^./src/ai-stock-trading" | grep -v "\.ai-context/specs\|\.ai-context/superpowers\|CHANGELOG" | sort
count: 32
positive control（deploy/ 配下の 'qdrant'）: 14
```

そのうち**送出先の値そのものを literal で持つ**（＝付け替えの対象）のは、syntax（FQDN 文字列）で数えて 7 箇所:

```console
$ grep -rn "mailpit\.platform-infra\.svc\.cluster\.local" --include=*.md --include=*.js --include=*.sh \
    --include=*.yaml --include=*.json . | grep -v node_modules | grep -v "^./src/ai-stock-trading"
./.ai-context/specs/20260902_issue-1144_dev-mail-capture-mta.md:103   ← 凍結記録。書き換えない
./deploy/keycloak/microservices-platform-realm.json:31                ← 付け替える
./deploy/local/vault/eso/bootstrap.sh:103                             ← **上流（捕捉箱）の宣言。据え置く**
./docs/operations/keycloak-smtp-relay-setup-runbook.md:233            ← §3 と一緒に退役する
./scripts/check-realm-constraints.js:1307                             ← 自己試験の seed fixture。据え置く（上流側）
./scripts/check-realm-constraints.js:1323                             ← 自己試験の realm fixture。relay へ付け替える
./scripts/check-realm-constraints.js:1470                             ← 存在秘匿の fixture。host 非空だけを見るので据え置き可（値だけ揃える）
count: 7
```

### M-4. 本番像に Keycloak が無いこと（決定 2 の前提）

```console
$ grep -rln "keycloak" deploy/helm/microservices-platform/templates/ | wc -l   → 0
positive control: grep -c keycloak deploy/helm/microservices-platform/values.yaml    → 9
positive control: grep -rln "kind:" deploy/helm/microservices-platform/templates/ | wc -l → 14
```

**production の Keycloak マニフェストは本リポジトリに無い。** よって「production 構成へ配備する」の実体は
**環境非依存の base を 1 つ置くこと**であり、**唯一の稼働クラスタは利用者の k3s** である。

### M-5. 所有権の現況

```console
$ grep -rn "RUNTIME_OWNED_REALM_KEYS" deploy/local/keycloak-setup/reconcile-realm.js scripts/*.js
deploy/local/keycloak-setup/reconcile-realm.js:61:const RUNTIME_OWNED_REALM_KEYS = new Set(['smtpServer']);
deploy/local/keycloak-setup/reconcile-realm.js:152 / :155 / :572
scripts/keycloak-realm-reconcile.test.js:21 / :112
```

### M-6. 着手後の再測定（push の直前に引き直した。導出値は走査ではなく計算し直す）

```console
M-1（状態 B/C を書く追跡下ファイル）        : 着手前 5  → 着手後 11
  ＋6 の内訳（新規 3 ＝ 本仕様書 / IADR-0404 / deploy/mail-relay/mail-relay.yaml、
              既存 3 ＝ .ai-context/adr/README.md（索引行）/ reconcile-realm.js（境界の追記）/ keycloak-realm-reconcile.test.js（試験の見出し））
M-1 陽性対照（存在秘匿）                     : 着手前 211 → 着手後 213
M-3（SMTP 設定の在り処）                     : 着手前 32 → 着手後 34 （＋2 ＝ deploy/mail-relay/{kustomization,mail-relay}.yaml）
M-3 陽性対照（deploy/ 配下の qdrant）        : 14 → 14 （不変。走査器が壊れていないことの対照）
捕捉用 MTA の FQDN を literal で持つ箇所      : 着手前 7 → 着手後 7（内訳は入れ替わった）
  現在: 凍結記録 1 ／ bootstrap.sh 1（上流の宣言・据え置き）／ deploy/mail-relay/mail-relay.yaml 1（説明のコメント）
        ／ scripts/check-realm-constraints.js 3（自己試験の fixture）／ scripts/k8s-local-up.sh 1（dev 既定の導出）
  🔴 **realm.json と runbook から消えた**（付け替え・退役）。
近接 MTA の FQDN を literal で持つ箇所        : 0 → 4（realm.json 1 ／ 自己試験 fixture 1 ／ 本仕様書 1 ／ 実装 ADR 1）
```

🔴 **`deploy/local/vault/eso/bootstrap.sh` は 1 バイトも変えていない**（差異 D-1）。上流の宣言はそこが正であり、
「開発環境から外へ出ない」の検査もそこを見続ける。

## 上流の一次情報（本 PR で確かめたもの）

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| V1 | Keycloak の送出は**同期**で、`mail.smtp.timeout` / `mail.smtp.connectiontimeout` は **10000 ms のハードコード**。失敗は `EmailException` | `DefaultEmailSenderProvider.java`（`release/24.0`）L103-104・L143-150 |
| V2 | 非実在利用者は `forkWithSuccessMessage(Messages.EMAIL_SENT)` で 200。`EmailException` は `createErrorPage(Response.Status.INTERNAL_SERVER_ERROR)`＝**500**、監査は `Errors.EMAIL_SEND_FAILED` | `ResetCredentialEmail.java`（`release/24.0`） |
| V3 | **Mailpit の公式文書はキュー・再送・後送を 1 つも記していない。** `forward-smtp-errors` は「SMTP エラーを記録するか **upstream-client へ転送するか**」の選択である | Mailpit docs「SMTP relay」 |
| V4 | `boky/postfix` の最新リリースは **v5.1.0**（2026-01-04）。manifest list digest `sha256:aafc7723…82ef` | `gh api repos/bokysan/docker-postfix/releases` ＋ Docker Hub API |
| V5 | init hook は **`/docker-init.d/`**（`/docker-init.db/` は deprecated 別名）。`execute_post_init_scripts` は `exec supervisord` の**直前**に走る＝`postconf -e` が効く | `scripts/run.sh` / `scripts/functions.sh`（v5.1.0） |
| V6 | `smtp_tls_security_level` の既定は **`may`**（平文フォールバック）。`POSTFIX_smtp_tls_security_level` が空のときだけ既定が入る | `postfix_set_relay_tls_level`（v5.1.0） |
| V7 | `RELAYHOST_USERNAME`/`RELAYHOST_PASSWORD` が**空なら SASL を有効にしない**（エラーにならない）＝dev（mailpit 宛・認証なし）で成立する | `postfix_setup_relayhost`（v5.1.0） |
| V8 | `master.cf` の `submission`（587）は **`-o` がすべてコメントアウト**されており、TLS も SASL も強制しない。`Dockerfile` は `EXPOSE 587` | `configs/master.cf` / `Dockerfile`（v5.1.0） |
| V9 | `mynetworks` の既定は `127.0.0.0/8,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16`（RFC1918 全域）で、`smtpd_client_restrictions=permit_mynetworks,permit_sasl_authenticated,reject`。**Pod ネットワークから無認証で投函できる** | `postfix_setup_networks`（v5.1.0） |
| V10 | 起動スクリプトは `maillog_file` を設定せず、ログは **rsyslog 経由**である | `rsyslog_log_format`（v5.1.0） |

🔴 **V4〜V10 はイメージの**ソース**から読んだものであり、稼働コンテナで実行して確かめてはいない**
（本機に k3s クラスタが無い）。PR 本文に**未検証として明記する**。

## 設計

### D-1. 器と配置

- 器: **Postfix**（`boky/postfix:v5.1.0`）。Mailpit を production の器にしない（V3）。
- 置き場: **`deploy/mail-relay/`（環境非依存の base）**。`deploy/local/infra/kustomization.yaml` が
  `../../mail-relay` を参照する（相対ディレクトリ参照の先例あり）。**opt-in ゲートに載せない**（決定 2 は無条件）。
- namespace: `platform-infra`（Keycloak と同じ＝"近接"）。
- 上流: Secret `keycloak-smtp` の `host`/`port`/`starttls`/`from`/`user`/`password` を env で読む。
  dev = mailpit（1025・平文・無認証）、go-live = 外部リレー（587・STARTTLS・SASL）。
- キュー寿命は**リセットリンクの寿命**（realm `actionTokenGeneratedByUserLifespan=1800`）に合わせ **30m**。
  値はマニフェストが与え、コードは既定を持たない。

### D-2. STARTTLS の導出（ADR-0045 決定 5）

env の三項演算は k8s に無いので、ConfigMap `mail-relay-init` の `/docker-init.d/10-tls-level.sh` が
`SMTP_UPSTREAM_STARTTLS` を読んで `smtp_tls_security_level` を `encrypt`（平文フォールバック無し）／`none` に決める。
**true|false 以外なら起動しない**（fail-closed）。既定の `may` に落ちる経路を残さない（V6）。

### D-3. 差出人の写像

外向きのエンベロープ差出人を Secret の `from` へ写す（`smtp_generic_maps`）。
🔴 **`SMTP_FROM` が空のとき（＝dev 既定・実値未供給）は写像を張らない。** 空値で inline map を張ると壊れる。

### D-4. realm の付け替えと所有権

- `smtpServer` → `mail-relay.platform-infra.svc.cluster.local:587` / `auth=false` / `starttls=false` /
  `from=noreply@platform.localhost`。**実値は realm に一切入らない**（恒久方針をより強く満たす）。
- `RUNTIME_OWNED_REALM_KEYS` から `smtpServer` を外す → **宣言が正**になり、再起動・再インポート・後追い Job の
  どれを経ても realm は relay を指す。**状態 B が構造で作れなくなる。**
- `GATE_OWNED_REALM_KEYS = new Set(['resetPasswordAllowed'])` を**別集合**として新設し、`plan()` は
  **宣言 true・稼働 false・`attributes["reset-gate.state"] === 'closed'`** のときだけ差分から除く。
  🔴 **`RUNTIME_OWNED_REALM_KEYS` へ入れてはならない** —— 「宣言 false・稼働 true」という**危険な向きの drift**
  まで見なくなる。門が着地するのは PR-C だが、**Job が門を開き直す事故は門と同時ではなく先に塞ぐ**
  （PR-C が単独で安全に着地できるようにする）。

### D-5. 宣言の門（`check-realm-constraints.js`）の付け替え

- `parseMailCaptureEndpoint` を汎用 `parseServiceEndpoint(yamlText, portNames)` へ一般化する
  （relay の Service は `http` ポートを持たないため、port 名の必須集合を引数で受ける）。
  `parseMailCaptureEndpoint` は後方互換の薄いラッパとして残す（export・自己試験が使っている）。
- **realm の `smtpServer.host/port` の期待値の単一情報源は `deploy/mail-relay/mail-relay.yaml` の Service** へ移す。
- **「外へ出ない」は上流側で引き続き検査する** —— Vault seed の `SMTP_CAPTURE_HOST` が
  `deploy/local/infra/mailpit.yaml` の Service を指すこと・`SMTP_HOST` の既定の導出・STARTTLS 既定 true。
- **`collectResetConcealmentGaps` は変えない**（`resetPasswordAllowed=true` なら `host`・`from` 非空、という
  不変条件はそのまま真である）。

### D-6. runbook

§3（kcadm PATCH）と compose 経路の kcadm、§確認 1・3 の kcadm を退役させる。運用者に残るのは
**§1（Vault seed）と §2（ESO 同期確認）だけ**になる。§0（先に閉じる）は**残す**（PR-C が入るまで機械の門は無い）。

## 受け入れ基準

- [x] `deploy/mail-relay/` の base が kustomize で描画でき、`deploy/local/infra` から参照される（`kubectl kustomize deploy/local/infra` / `deploy/local/infra-persistence` の両方が成功）
- [x] realm の `smtpServer` が relay の Service を指し、`node scripts/check-realm-constraints.js` が緑
- [x] `node scripts/check-realm-constraints.js --self-test` が **96 件 OK**（着手前 92 件 → ＋4）。変異 1-b（relay を捕捉用 MTA 直結へ戻す）・6-b（relay の宣言が消える）・6-c（port 名が読めない）・上流側の Service 名変更 が対で落ちる
- [x] `reconcile-realm.js` の `smtpServer` が宣言所有になり（`RUNTIME_OWNED_REALM_KEYS` は空集合）、`resetPasswordAllowed` が条件つき門所有になる（`GATE_OWNED_REALM_KEYS`）
- [x] `node scripts/keycloak-realm-reconcile.test.js` が **32 件 OK**（着手前 20 件 → ＋12）。変異 3 件を対で確かめた（下記「変異試験の記録」）
- [x] runbook から送出先の `kcadm` PATCH（旧 §3 と compose 経路）が消え、送出先は宣言が正だと書かれている。**§0 の「先に閉じる」だけは `kcadm` のまま残る**（機械の門は PR-C。**その 1 箇所を人手に残していること自体が窓である**と本文に書いた）
- [x] SC-15 画面仕様書に**変更後の 4 状態表**を足し、**残る窓 W1 / W1' / W2 を名前つきで残した**（「消えた」と書いていない）。実測していないことも明記した
- [x] 実装 ADR（IADR-0404）を起こし、索引行を足した（**題目セル 183 文字 / 上限 200**）。`check-adr-numbering.js` 緑
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑（最後の編集の後に実行）

### 変異試験の記録（実装を壊して、対の試験が赤くなることを確かめた）

| # | 壊し方 | 落ちた試験 | 復元 |
| --- | --- | --- | --- |
| 1 | `GATE_OWNED_REALM_KEYS` を空集合にする | 「宣言は resetPasswordAllowed=true である（前提・陽性対照）」 | 済 |
| 2 | `plan()` から `heldByGate.has(k)` の除外を外す | 🎯 **「門が閉じている間は drift 0 件 —— 開き直さない」**（`門が閉じた realm を開き直す計画が出た: ["realm.update"]`） | 済 |
| 3 | `gateHoldsClosed` から向き・属性の条件を外す（＝無条件の実行時所有と同じ） | 🎯 **「属性が無い false（人が手で閉じた）は従来どおり drift 1 件で宣言へ戻す」** | 済 |

復元は `diff` でバイト一致を確かめた（`RESTORED byte-identical`）。

## テスト方針

| 何を | どこで | 陽性対照 |
| --- | --- | --- |
| 宣言の門の付け替え | `check-realm-constraints.js --self-test` | realm を mailpit 直結へ戻す変異／relay の Service port を変える変異／relay の宣言を消す変異 |
| 所有権 | `keycloak-realm-reconcile.test.js` | 門所有の条件を外すと「閉じた realm を開き直す」が赤 |
| 危険な向きの drift | 同上 | 宣言 false・稼働 true は drift 1 件のまま |
| 宣言所有化 | 同上 | `smtpServer` の差分が `realm.update` の body に載ること |

## 計画書との差異

- 差異: **あり**
  - **D-1**: 設計書は Vault seed の dev 既定 `from` を合成値へ変えることで `smtp_generic_maps` の破れを避けていた。
    **採らない。** runbook §2 の確認手順は「`from` の長さが 0 より大きければ同期が成立している」という判定であり、
    dev 既定を非空にするとこの判定が**実値の投入前から真**になって壊れる。代わりに **init スクリプト側で
    `SMTP_FROM` が空なら写像を張らない**（D-3）。`bootstrap.sh` は 1 行も触らない（母集合も狭くなる）。
  - **D-2**: 設計書のマニフェストは `/docker-init.db` を hook ディレクトリと書いていたが、**現行は `/docker-init.d/`**
    である（`/docker-init.db/` は deprecated 別名。V5）。`/docker-init.d/` を使う。
  - **D-3**: 設計書は `POSTFIX_maillog_file` を Deployment に置いていた。**本 PR では置かない** ——
    読み手（`postfix_exporter`）が PR-B で入るまで意味を持たず、起動スクリプトはログを rsyslog へ流す（V10）ため
    両立の可否は稼働で確かめる必要がある。**PR-B の課題として送る。**
  - **D-4**: 設計書は exporter サイドカーと `metrics` ポートを `mail-relay.yaml` に含めていたが、
    **PR-B の射程**（設計書 §6 のファイル領域も PR-B へ割り当てている）。本 PR は relay 単体で置く。
  - **D-5**: 採番は設計書の `IADR-0400` が既に別 PR に取られていた（`.ai-context/adr/` は `IADR-0000..0402`）。
    **マージ時点で空いている次番**を取る。
- **計画（ADR-0078）との差異は無い。** 決定 2・3・5 の一部（観測点の移動・門）は**後続 PR で実装する**という
  刻み方の差であり、決定そのものへの逸脱ではない。

## 未決事項（PR 本文へ「未検証」として書く）

- 🔴 **稼働コンテナでの実測が本機ではできない**（k3s クラスタが無い・`kubeconform` も未導入。`helm`/`kubectl`/`docker` は在る）。
  イメージのタグと digest・`/docker-init.d/` の作法・`mynetworks` の既定が k3s の Pod CIDR を覆うこと・
  submission(587) が無認証平文を受けること —— **いずれも上流ソースから読んだ値であり、実行して確かめていない。**
- 🔴 **残る窓 W1（relay Pod が居ない）・W1'（SYN が落ちる＝所要時間が 10 秒になる）・W2（relay が投函を拒む 452/554）は
  本 PR では閉じない。** 門（PR-C）が入って初めてプローブ周期＋PUT 往復へ縮む。
- 🔴 **所要時間の閾値は定めない**（ADR-0078 決定 1 §残るもの。「実測してから」）。
- Keycloak → relay の 1 ホップは平文である（Pod ネットワークに閉じる）。**production でも同じ**である点は
  受容として実装 ADR に記録し、環流する。

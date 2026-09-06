---
title: IADR-0404 Keycloak の送出先はクラスタ内の近接 MTA（キュー付き Postfix）に固定し、smtpServer を宣言所有・resetPasswordAllowed を条件つき門所有へ移す
type: impl-adr
status: Proposed
related_ids: [SC-15, SC-10, FR-05, FR-22, NFR-09, ADR-0026, ADR-0045, ADR-0078, IADR-0261, IADR-0301, IADR-0329, IADR-0332, IADR-0344, IADR-0347, IADR-0369]
author: Claude（実装）
created: 2026-09-06
updated: 2026-09-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0078_existence-hiding-response-indistinguishability-and-nearby-mta.md
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
---

# IADR-0404: Keycloak の送出先はクラスタ内の近接 MTA（キュー付き Postfix）に固定し、`smtpServer` を宣言所有・`resetPasswordAllowed` を条件つき門所有へ移す

- 状態: Proposed
- 日付: 2026-09-06
- 決定者: Claude（実装）。**権限の拡張（門の `manage-realm`）は利用者が 2026-09-05 に承諾済みだが、着地は後続 PR である**

## 起点・関連

- 関連する計画書 ID: **ADR-0078**（本 ADR が降ろす裁定）／ADR-0045（決定 2-b・8 を ADR-0078 が部分改定）／
  ADR-0026／SC-15・SC-10・FR-05・FR-22・NFR-09
- 起点 issue: **#1245**（出所 #1143 / PR #1169 のフェーズ末監査。計画側の裁定は planning#536 → ADR-0078）
- 先行する実装 ADR:
  [IADR-0347](./IADR-0347_reset-existence-concealment.md)（状態 B を作らない 3 つの門。**維持する**）／
  [IADR-0344](./IADR-0344_dev-mail-capture-mta.md)（開発環境の捕捉用 MTA。本 ADR で**上流へ 1 ホップ後退**）／
  [IADR-0332](./IADR-0332_keycloak-smtp-externalsecret-wiring.md)（Secret 配線。**env で読む Pod ができた**）／
  [IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md)（宣言／実行時の境界。**所有を改める**）／
  [IADR-0261](./IADR-0261_keycloak-theme-and-smtp-injection.md)（決定 2 の実行時注入を本 ADR が反転させる）／
  [IADR-0329](./IADR-0329_identity-admin-keycloak-provider-and-realm-wiring.md)（最小権限。門が部分的に後退させる）
- 関連する実装仕様書: `.ai-context/specs/20260906_issue-1245_nearby-mta-relay.md`（PR-A）／
  `.ai-context/specs/20260907_issue-1245_reset-gate.md`（PR-C。2026-09-07 の追記が対応する）

## コンテキストと課題

IADR-0347 は SC-15 のパスワードリセット申請を 4 状態で実測し、**状態 B（送出先未設定 ＋ 申請が開いている）を
宣言・稼働・運用の 3 門で作らない**ようにしたが、**状態 C（送出先へ接続できない ＋ 申請が開いている）は塞げず**、
残余を計画へ環流した。ADR-0078 が裁定した:

1. 存在秘匿の統制は**応答の区別不能性**である（ステータス・本文・所要時間）
2. go-live の送出経路に**キュー付きの近接 MTA** を挟む（ADR-0045 決定 2-b の部分改定）
3. 上流停止の観測点を**近接 MTA のキュー**へ移す（同決定 8 の部分改定）
4. 投函できないときは**機械で** `resetPasswordAllowed=false` へ倒す
5. #1143 の受け入れ基準 1・2 は 2 と 4 の両方が配備されて満たされる

**本 ADR は #1245 を刻んだ 5 段のうち PR-A（器の配備・realm の付け替え・所有権の整理・runbook §3 の退役・
宣言の門の付け替え）を記録する。** 観測（決定 3）と門（決定 4）は後続の PR が着地させる。

### 着手前に自分で確かめたこと（上流ソース）

| # | 事実 | 出典 |
| --- | --- | --- |
| V1 | Keycloak の送出は**同期**で、`mail.smtp.timeout` / `mail.smtp.connectiontimeout` は **10000 ms のハードコード**。失敗は `EmailException` | `DefaultEmailSenderProvider.java`（`release/24.0`）L103-104・L143-150 |
| V2 | 非実在利用者は `forkWithSuccessMessage(Messages.EMAIL_SENT)` で 200。`EmailException` は `createErrorPage(INTERNAL_SERVER_ERROR)`＝**500**、監査は `Errors.EMAIL_SEND_FAILED` | `ResetCredentialEmail.java`（同） |
| V3 | **Mailpit の公式文書はキュー・再送・後送を 1 つも記していない。** `forward-smtp-errors` は「SMTP エラーを記録するか **upstream-client へ転送するか**」の選択である | Mailpit docs「SMTP relay」 |
| V4 | `boky/postfix` の最新リリースは **v5.1.0**（2026-01-04）。manifest list digest `sha256:aafc7723…82ef` | GitHub releases API ＋ Docker Hub API |
| V5 | init hook は **`/docker-init.d/`**（`/docker-init.db/` は deprecated 別名）。`execute_post_init_scripts` は `exec supervisord` の**直前**に走る | `scripts/run.sh` / `scripts/functions.sh`（v5.1.0） |
| V6 | `smtp_tls_security_level` の既定は **`may`**（平文フォールバック）。`POSTFIX_smtp_tls_security_level` が空のときだけ既定が入る | `postfix_set_relay_tls_level`（同） |
| V7 | `RELAYHOST_USERNAME` / `RELAYHOST_PASSWORD` が**空なら SASL を有効にしない**（エラーにならない） | `postfix_setup_relayhost`（同） |
| V8 | `master.cf` の `submission`（587）は `-o` がすべてコメントアウトされており、**TLS も SASL も強制しない**。`Dockerfile` は `EXPOSE 587` | `configs/master.cf` / `Dockerfile`（同） |
| V9 | `mynetworks` の既定は RFC1918 全域で、`smtpd_client_restrictions=permit_mynetworks,permit_sasl_authenticated,reject` | `postfix_setup_networks`（同） |
| V10 | 起動スクリプトは `maillog_file` を設定せず、ログは **rsyslog 経由**である | `rsyslog_log_format`（同） |
| U11 | 🔴 **`ALLOWED_SENDER_DOMAINS` が `smtpd_sender_restrictions` へ反映され、外れた差出人が 554 で拒まれること**。マニフェストのコメントはそう断定しているが、V4〜V10 と違い**上流ソースで確認していない**。🔴 **W2（relay が投函を拒む）の検知はこの前提に依存する** | 未確認（v5.1.0 のソース未読） |

🔴 **V4〜V10 はイメージの**ソース**から読んだものであり、稼働コンテナで実行して確かめていない**
（作業機に稼働クラスタが無い）。PR 本文に未検証として明記した。

### 本リポジトリ側で確かめたこと

- **production の Keycloak マニフェストは本リポジトリに無い**（helm テンプレートの `keycloak` ヒット 0 ファイル。
  陽性対照: `values.yaml` 9 行・テンプレート 14 ファイル）。「production 構成へ配備する」の実体は
  **環境非依存の base を 1 つ置くこと**であり、**唯一の稼働クラスタは利用者の k3s** である。
- 後追いの realm 差分適用（`reconcile-realm.js`）は `smtpServer` **だけ**を実行時所有とみなし、
  `resetPasswordAllowed` は宣言所有として PUT で戻す。**門を置くと Job が開き直す。**
- Secret `keycloak-smtp` は `ESO=1` のときにしか作られなかった（`env` で読む Pod が 1 つも無かったため）。
  **近接 MTA が読み手になる以上、ESO の有無によらず存在しなければならない。**

## 検討した選択肢

### 器（決定 1）

| 案 | キュー・再送 | 上流の STARTTLS 必須 ＋ SASL | 出自・重さ | 評価 |
| --- | --- | --- | --- | --- |
| A. Mailpit の relay 機能を production でも使う | **無い**（V3）。`forward-smtp-errors` を立てると**上流の失敗が Keycloak へ返り状態 C が戻る** | 対応 | 既に pin 済み | **不採用**。ADR-0078 決定 2 の「キューを持ち、上流が停止していても投函を受け付けて後送する」を満たさない |
| **B. Postfix（`boky/postfix`）** | **有る**（deferred キュー・指数バックオフ・寿命） | `RELAYHOST` ＋ 資格情報、`POSTFIX_smtp_tls_security_level` | 1 コンテナ・env 駆動 | **採用** |
| C. maddy / OpenSMTPD | 有る | 有る | 設定 DSL が要る・先例が無い | 不採用（B より学ぶ面が広く、得るものが同等以下） |
| D. Keycloak SPI で非同期送出 | — | — | Java のビルド基盤が無い | 不採用（IADR-0347 決定 3-A と同じ理由。加えて公式イメージの pin を自前ビルドへ変えると本体の修正への追随を失う） |

### 配置（決定 2）

| 案 | 評価 |
| --- | --- |
| A. helm chart へ足す | **不採用**。Keycloak が chart に無く、別 namespace になって既定拒否のポリシーに穴を開ける形になる。**Keycloak に最も近い場所**という "近接" の趣旨に反する |
| B. `deploy/local/infra/` に dev 専用として置く | 不採用（単独では）。dev 専用の器に production 構成を置くと、production が同じ宣言を使う根拠が文書上にしか残らない |
| **C. 環境非依存の base `deploy/mail-relay/` を新設し、dev の overlay が参照する** | **採用**。1 つの宣言を両環境が使う。相対ディレクトリ参照の先例あり |

### 接続条件（決定 3）

| 案 | 評価 |
| --- | --- |
| A. runbook §3 の kcadm PATCH を続け、relay は素通し | **不採用**。ADR-0078 決定 2 は接続条件を relay → 上流の区間へ移すと定めた。加えて**状態 B の温床**（実行時所有の `smtpServer` が再起動で消える）が残る |
| **B. realm の `smtpServer` は近接 MTA を指す宣言固定。relay が Secret を env で読む** | **採用**。実値は realm に一切入らず、恒久方針をより強く満たす |

### 所有権（決定 4・5）

| 案 | 評価 |
| --- | --- |
| A. `resetPasswordAllowed` を `RUNTIME_OWNED_REALM_KEYS` へ入れる | **不採用**。「**宣言 false なのに稼働 true**」（閉じたはずの申請が開いている）という**危険な向きの drift** まで見なくなる |
| **B. `GATE_OWNED_REALM_KEYS` を別集合にし、条件つきで除外する** | **採用**。除外は「宣言 true・稼働 false・`attributes["reset-gate.state"]==closed`」の 1 組だけ |

## 決定

1. **近接 MTA は Postfix（`boky/postfix:v5.1.0` を pin）。** Mailpit は **dev の上流（捕捉箱）** に残る。
   キュー寿命は**リセットリンクの寿命**（realm の `actionTokenGeneratedByUserLifespan=1800`）に合わせ 30 分にする
   —— 既定の 5 日では**期限切れのリンクを 5 日間配り続ける**。値は**マニフェストが与え**、コードは既定を持たない。
2. **配置は Keycloak と同じ namespace（`platform-infra`）。dev と go-live は同じ宣言（`deploy/mail-relay/`）を使い、
   違うのは上流だけ**である。**opt-in ゲートに載せない**（決定 2 は無条件。IADR-0344 決定 1 と同じ理由）。
   🔴 **production の Keycloak マニフェストは本リポジトリに無い。** ここに置くのは
   「go-live で Keycloak の隣に適用されるべき唯一の宣言」であり、ADR-0078 の実測は利用者の k3s で行う。
3. **接続条件（host / port / STARTTLS / 資格情報 / 送信元）は relay → 上流の区間へ移り、relay が Secret
   `keycloak-smtp` を env で読む。** realm の `smtpServer` は
   `mail-relay.platform-infra.svc.cluster.local:587` / `auth=false` / `starttls=false` /
   `from=noreply@platform.localhost` の**宣言固定**である。
   - STARTTLS は**宛先に結び付けて導出する**: `starttls=true` → `smtp_tls_security_level=encrypt`
     （**平文フォールバック無し**。ADR-0045 決定 5）、`false` → `none`（Vault seed が捕捉箱宛にしか出さない値）。
     イメージの既定 `may`（V6）へは決して落とさない。**true / false 以外なら起動しない**（fail-closed）。
   - **上流が空なら起動しない。** `RELAYHOST` が空だと Postfix は宛先の MX へ**直接配送する**＝クラスタ外へ出る。
   - 外向きのエンベロープ差出人は Secret の `from` へ写す。🔴 **実値が空（dev）のときは写像を張らない。**
   - 🔴 **Keycloak → relay の 1 ホップは平文**（Pod ネットワークに閉じる）。**production でも同じ**である点は
     新しく、**受容として記録し環流する**。
4. **`smtpServer` を実行時所有から宣言所有へ移す**（IADR-0261 決定 2 / IADR-0369 の境界表の反転）。
   宣言が正になるので、再起動・再インポート・後追い Job のどれを経ても realm は近接 MTA を指す ——
   **状態 B は構造で作れなくなる。** 宣言に無いキー（runbook が入れた `user` / `password`）は消さない。
   **IADR-0347 の 3 門は維持する**（別の事故＝宣言の混入を捕まえる）。
5. **`resetPasswordAllowed` は条件つき門所有にする。** `GATE_OWNED_REALM_KEYS` を
   `RUNTIME_OWNED_REALM_KEYS` とは**別集合**として持ち、差分から除くのは
   「**宣言 true・稼働 false・`attributes["reset-gate.state"]==="closed"`**」の 1 組だけである。
   逆向き（宣言 false・稼働 true）と、属性が無い false（人が手で閉じた）は**従来どおり drift** である。
   🔴 **門（#1245 PR-C）より先にこれを入れる** —— 門を後から置いたときに Job が開き直す事故を、
   門と同じ PR で初めて気付く形にしない。
   🔴 **`attributes["reset-gate.state"]` を realm 宣言へ書かない。** 書くと `attributes` が宣言所有になり、
   **後追い Job が `closed` を `open` へ戻す**（除外は `resetPasswordAllowed` にしか効かない）。
   realm 宣言にトップレベルの `attributes` が無いので、この属性は**実行時にだけ存在する**。
   門が書き、Job は見るだけである。
6. **宣言の門（`check-realm-constraints.js`）の期待値を 2 区間に分ける。**
   realm の `smtpServer` は**近接 MTA の Service** と、Vault seed の `SMTP_CAPTURE_HOST` は
   **捕捉用 MTA の Service** と突き合わせる。どちらも**宣言を走査して期待値を組み立てる**（値を書き写さない）。
   🔴 **「開発環境から外へ出ない」の検査は上流側へ移して残す** —— 移した先で消すと、`SMTP_HOST` を
   指定した瞬間に dev から実送信できる状態へ静かに戻る。
   **`collectResetConcealmentGaps` は変えない**（`resetPasswordAllowed=true` なら `host`・`from` 非空、という
   不変条件はそのまま真である）。
7. **runbook §3（kcadm PATCH）と compose 経路の同手順を退役させる。** 運用者に残るのは
   §1（Vault seed）・§2（同期確認）・§4（relay の再起動）である。§0「先に申請を閉じる」は**残す** ——
   機械の門はまだ無い。**この 1 箇所を人手に残していること自体が窓である**と runbook に書く。
8. **Secret `keycloak-smtp` は ESO の有無によらず必ず存在させる。** 起動器が dev 既定（Vault seed と同じ導出）で
   作り、ESO は `creationPolicy: Merge` で Vault の値をマージする。**ESO 供給後は relay を rollout し直す**
   （`secretKeyRef` の env は Pod 起動時に一度だけ解決される）。
9. **しないこと**: Keycloak SPI ／ エッジでの応答書き換え ／ Mailpit を production の器にする ／
   `resetPasswordAllowed` の無条件な実行時所有化 ／ **所要時間の閾値を決めること**（ADR-0078 決定 1 は
   「実測してから」と言い、実測がまだ無い）。

### 変更後の 4 状態と、残る窓の名前

Keycloak の送出は同期（V1）で、relay が `250` を返した時点で成功になる。**以後の上流失敗は Keycloak に一切戻らない。**

| # | 状態 | 実在 | 非実在 | 判定 |
| --- | --- | --- | --- | --- |
| A | relay 稼働・上流稼働・申請が開いている | 200 | 200 | **一致**（所要時間の差は測って環流する。本 PR では測っていない） |
| B | `smtpServer` 未設定・開 | — | — | **構造で作れなくなる**（決定 4。宣言所有 ＋ 後追い Job が復元） |
| C1 | **上流停止**・relay 稼働・開 | **200** | **200** | **一致。ADR-0078 決定 2 の目標。** メールは deferred キューへ入る |
| C2 | **relay 停止**（Pod 無し）・開 | **500**（即時） | 200 | 🔴 **窓 W1**: 停止から申請が閉じるまで。**本 PR では人手（runbook §0）** |
| C2' | relay へ **SYN が落ちる** | **500**（**10 秒後**。V1） | 200 | 🔴 **窓 W1'**: 同上。加えて**所要時間の差が 10 秒**になり、ステータスを見ずとも判別できる |
| C3 | relay 稼働だが**投函を拒む**（452 キュー満杯・554 差出人拒否） | **500** | 200 | 🔴 **窓 W2**: 同上 |
| D | 申請が閉じている | 400 | 400 | **一致**（IADR-0347 実測。本文はバイト一致） |

🔴 **W1 / W1' / W2 は本 PR では閉じない。** 門（#1245 PR-C）が入って初めて、人手の分〜時間から
**プローブ周期 ＋ PUT 往復**へ縮む。**「窓は無い」と書かない**（IADR-0347 決定 5 と同じ規律）。
🔴 **上の表のうち本 PR で実測したものは 1 つも無い**（稼働クラスタが要る。#1245 PR-D）。
A・B・D は #1143 / IADR-0347 の実測、C1 は V1〜V3 からの**導出**である。

## 理由

- **器を替えたのは、ADR-0078 決定 2 の「キューを持ち後送する」が Mailpit に無いからである。**
  「決定 9 の器を production へ拡張する」という計画の言い方は topology としては正しいが、
  **構成要素としては別物**になる。書き分けないと、次の人が Mailpit を production に置く。
- **接続条件を relay へ移すと、状態 B の温床（実行時所有の `smtpServer` が再起動で消える）も同時に消える。**
  これは ADR-0078 が直接求めたものではない副産物だが、**IADR-0347 の 3 門を外す理由にはしない** ——
  門は別の事故（宣言そのものの混入）を捕まえている。
- **門所有を条件つきにしたのは、無条件だと逆向きの drift まで見えなくなるからである。**
  「閉じたはずの申請が開いている」は**利用者名が漏れる向き**であり、これこそ検知したい drift である。
- **門より先に所有権を入れたのは、順序が逆だと事故に気付く場所が増えるからである。**
  門を先に置くと、閉じた realm を Job が開き直す —— しかもそれは**静かに**起こる（門は閉じたと記録し、
  Job は drift を直したと記録する）。
- **fail-closed を 2 箇所（上流が空・STARTTLS の値が不正）で選んだのは、代替が「黙って外へ出る」だからである。**
  起動しない Pod は気付ける。平文で外へ出た 1 通は気付けない。

## 結果

- **良い影響**: 状態 C1（上流停止）が Keycloak の応答から消える。状態 B が構造で作れなくなる。
  実値が**稼働 realm にも入らなくなる**（恒久方針をより強く満たす）。runbook から手順が 2 つ消え、
  認証基盤の Pod で `kcadm` を打つ場面が §0 の 1 箇所だけになる。dev と go-live の topology が同じになり、
  go-live 前に dev で経路を丸ごと試せる。
- **悪い影響 / トレードオフ**: Pod が 1 つ増える。**Keycloak → relay が平文**（クラスタ内）で、
  production でも同じである。**メールの到達が非同期になり、応答時点では上流への送達が保証されない**
  （ADR-0078 §結果 が受け入れたもの）。キューはコンテナの書き込み層にあり **Pod の再作成で失われる**
  （載るのは寿命 30 分のメールだけなので受容する）。`k8s-local-up.sh` の [4/7] に待ち合わせが 1 つ増える。
- **測っていて、直していないこと**:
  🔴 **W1 / W1' / W2**（門が入るまで人手）。
  🔴 **状態 A の所要時間の差**（実在側だけ SMTP 取引 1 往復。閾値は計画が実測後に定める）。
  🔴 **観測点の移動が未了**（ADR-0078 決定 3）—— 近接 MTA を挟んだ結果、**Keycloak の監査ログだけを見ると
  上流停止を見逃す**。キューの観測が入るまで、この見逃しは実在する。
  🔴 **Keycloak → relay の STARTTLS の要否**（cert-manager で張る余地はあるが本 PR では入れない）。
  🔴 **egress allowlist の実装**（外へ出るのが relay の 1 経路に狭まったが、統制の実装は go-live の課題）。
- **フォローアップ**:
  (1) 観測（exporter → collector → 運用ダッシュボードの導線・アラート）を入れる（ADR-0078 決定 3。#1245 PR-B）
  (2) 門 `reset-gate` を入れる（同決定 4。#1245 PR-C。**権限は利用者が承諾済み**）
  (3) 4 状態と所要時間を稼働クラスタで実測し planning#536 へ環流する（同フォローアップ 4。**着手可否の注記の解除の契機**。#1245 PR-D）
  (4) ログイン経路の実測（同フォローアップ 5。#1245 PR-0）
  (5) キューの容量設計（寿命・永続化）を環流する（同 §残るもの）
  (6) Keycloak → relay の平文 1 ホップの受容を環流する
  (7) イメージのタグ / digest・init hook の作法・`mynetworks` の既定が Pod CIDR を覆うこと・
      submission(587) が無認証平文を受けることを**稼働クラスタで確かめる**（本 PR は上流ソースからの読み取りのみ）

## ★［2026-09-06 追記 / #1307］U11 に答えが出た。反映先は違っており、**窓 W2 が実際に開いた**

本 ADR を実装した `686d5934`（PR #1305）のマージ直後から、develop の `integration-stack` が
**3 回連続で赤い**（`34028253172` / `34028289558` / `34031165560`）。失敗しているのは
`node scripts/check-password-reset-mail.js` で、実測した一次症状は次のとおりである。

```
postfix/smtpd: NOQUEUE: reject: RCPT from ...keycloak...:
  556 5.1.10 <admin@example.com>: Recipient address rejected: Domain example.com does not accept mail (nullMX)
Keycloak KC-SERVICES0029: Failed to send email: SendFailedException: Invalid Addresses
```

**これは本 ADR が名前を付けていた状態 C3・窓 W2（relay 稼働だが投函を拒む）そのものである。**
表は「実在 500 / 非実在 200」と書いており、実測はそのとおりになった（T-10 が破れた）。
**窓の名前は正しかったが、入口の数を数え違えていた** —— C3 の原因として挙げていたのは
「452 キュー満杯・554 差出人拒否」の 2 つだけで、**宛先ドメインの DNS 検証**が抜けていた。

### U11 の答え（上流ソースで実測。`bokysan/docker-postfix` tag `v5.1.0`）

| # | 実測 | 出典 |
| --- | --- | --- |
| V12 | 差出人ドメインの門は `smtpd_sender_restrictions` **ではなく** `smtpd_recipient_restrictions` の `check_sender_access lmdb:/etc/postfix/allowed_senders` である | `scripts/functions.sh:589`（`postfix_setup_sender_domains`） |
| V13 | `smtpd_sender_restrictions` は `permit_mynetworks,reject` であり、**Pod 網（RFC1918）からの投函を無条件に通す** | 同 `:337`（`postfix_reject_invalid_helos`） |
| V14 | 🔴 同じ 1 行に **`reject_unknown_recipient_domain`** が入る。**`ALLOWED_SENDER_DOMAINS` を設定したときだけ**この並びが組まれる。`permit_mynetworks` は**この並びに入っていない** | 同 `:573-592` |
| V15 | あわせて `smtpd_relay_restrictions=permit` が入る（「behind closed doors」なので中継は全許可） | 同 `:592` |

**U11 は「未確認」から「実測で解決。ただし前提が誤りだった」へ移る。**
マニフェストのコメントが断定していた反映先（`smtpd_sender_restrictions`）は誤りであり、
**W2 の検知が依存していた前提が 1 つ崩れた。**

### なぜ本 ADR の設計と両立しないか

`ADR-0078` 決定 2 は近接 MTA に「**キューを持ち、上流が停止していても投函を受け付けて後送する**」ことを
求めている。`reject_unknown_recipient_domain` は **RCPT 時に同期的に拒む**ので、
切り離したはずの失敗が Keycloak の応答へ戻る。**近接 MTA を挟んだ目的そのものを打ち消していた。**

🔴 **これは dev 固有ではない。** 宛先ドメインの DNS が一時的に引けない利用者・null MX を出す利用者が
1 人でも居れば、**その利用者だけが 500 になり存在秘匿が破れる**。試験利用者の `@example.com` を
別のドメインへ替えても、DNS 検証そのものは残るので直らない。

### 是正（#1307 の PR）

- init スクリプトに段 (4) を足し、上流が組み立てた `smtpd_recipient_restrictions` から
  **`reject_unknown_recipient_domain` だけを取り除く**（列を書き写さない）。
  `reject_non_fqdn_recipient`（構文検査。DNS を引かない）と末尾の `reject`（オープンリレー防止）は残す。
- 取り除けたか・**差出人の門を消していないか**を両方向で確かめ、破れたら**起動しない**（fail-closed）。
- `scripts/check-realm-constraints.js` に検査 5-b を足し、宣言側で「無効化を置き忘れた形」を止める。
  🔴 **実挙動はクラスタでしか測れない。** 検査が見るのは宣言だけである。

### 残る窓（狭めたが、閉じてはいない）

🔴 **C3 の入口は 1 つ減っただけである。** `reject_non_fqdn_recipient`（構文が壊れた宛先）と
キュー満杯（452）は残る。前者は**利用者データの問題**であり Keycloak 側の登録時に閉じるべきもの、
後者はキュー容量の設計（フォローアップ (5)）に属する。**「W2 は閉じた」と書かない。**

## ★［2026-09-07 追記 / #1245 PR-C］門 `reset-gate` が着地した。窓 W1 / W1' / W2 は**縮んだ**

本 ADR のフォローアップ (2)（ADR-0078 決定 4）を実装した。**決定 5 で先に入れた条件つき門所有の
「書き手」がこれで揃う。** 権限の拡張は利用者が 2026-09-05 に承諾済みであり、本追記はその着地の記録と、
**新たに要った 3 つの裁定**（検知の方式・判定の非対称性・権限の面積）を残す。
関連する作業仕様書: `.ai-context/specs/20260907_issue-1245_reset-gate.md`

### 決定 10: 検知は **relay へ本物の SMTP 取引を打つ能動プローブ**である

| 案 | 評価 |
| --- | --- |
| A. 監査イベント `SEND_RESET_PASSWORD` の error を Admin API で監視する | **不採用**。**反応的である** —— 最初の 1 件は既に 500 を返しており、その 1 件で利用者名が 1 つ漏れる。`view-events` の権限も増える |
| B. k8s API で relay Pod の Ready を見る | **不採用**。**Ready でも投函は拒まれる** —— #1307 が「relay は生きていて投函だけを拒む」状態を develop の CI で 3 回連続の赤として実測している。RBAC も増える |
| **C. 門自身が relay へ SMTP 取引を打つ** | **採用**。Keycloak が通るのと**同じ経路・同じ判定**を、利用者の要求より先に受ける |

🔴 **プローブは `MAIL FROM` → `RCPT TO` → `RSET` → `QUIT` で終える。DATA を送らない。**
設計時は「`DATA` まで送り relay 側の `transport_maps` で捨てる」形を想定していたが、
**着地した近接 MTA に `transport_maps` は入っていない**（本 ADR の実装 `686d5934` にも #1307 の是正にも無い）。
relay は `smtpd_relay_restrictions=permit` で**宛先によらず上流へ中継する**（V15）ので、
`DATA` まで送ると**プローブのメールが捕捉箱へ流れ込み**、`check-password-reset-mail.js` の
「ちょうど 1 通」（T-17）が壊れる。周期 10 秒なら寿命 30 分の間に 180 通である。

**RCPT で終えても検知能力は落ちない**（むしろ #1307 の実測に照らすと上がる）:

| 捕まえたい状態 | Postfix が返す段 | RCPT 止まりで見えるか |
| --- | --- | --- |
| relay が居ない（W1） | TCP 接続拒否 | ○ |
| SYN が落ちる（W1'） | タイムアウト（Keycloak と同じ 10 000 ms で待つ。V1） | ○ |
| キュー満杯・容量不足（W2） | `452 4.3.1` は **MAIL FROM** で返る | ○ |
| 差出人の拒否（W2） | `check_sender_access` は `smtpd_recipient_restrictions` の中＝**RCPT**（V12） | ○ |
| 宛先 DNS 検証の再混入（#1307 の再発） | `reject_unknown_recipient_domain` は **RCPT**（V14） | ○ |
| 宛先構文の拒否（W2 の残る入口） | `reject_non_fqdn_recipient` は **RCPT** | ○ |

🔴 **上表は上流ソース（V1〜V15）と Postfix の仕様からの導出であり、稼働クラスタで打っていない**
（本作業機にクラスタが無い。#1245 PR-D で測る）。

### 決定 11: 判定は非対称である（**失敗 1 回で閉じ、連続 N 回の成功で宣言値へ戻す**）

閉じるのを遅らせた分がそのまま「実在する利用者だけ 500 が返る窓」になる。逆に開けるのを急ぐと、
復旧の揺らぎで開閉を繰り返し admin event が溢れる。**門が開けないのは次の 2 つ**である。

- **宣言（realm JSON）が `resetPasswordAllowed: false`** —— 門は「開ける主体」ではなく
  「**宣言どおりに戻す**主体」である。
- **稼働の属性 `reset-gate.state` が `closed` でない**（＝**人が手で閉じた**）—— 他人の意思を上書きしない。
  🔴 **これは決定 5 の後追い Job 側の除外条件と対になっている** —— 属性が無い `false` は Job が宣言へ戻し、
  門は触らない。**役割が重ならないので、両方が「直した」と記録する形にならない。**

**PUT 本文で差し替えるのは 4 つだけ**（`resetPasswordAllowed` と `attributes` の
`reset-gate.{state,reason,since}`）。`manage-realm` は realm 設定を丸ごと書ける権限なので、
**本文に入るものを機械で狭めておく** —— `scripts/reset-gate.test.js` が固定する。

### 決定 12: 権限は専用の機密クライアント `reset-gate` の SA に `view-realm` ＋ `manage-realm` **だけ**

| 案 | 権限の面積 | 評価 |
| --- | --- | --- |
| A. 後追い Job と同じ master 管理者資格情報 | **全 realm の全操作** | **不採用**。Job は数秒で終わるが門は常駐する。漏れたときの半径が最大になる |
| B. 合成ロール `realm-admin` | realm 内の全管理操作（利用者・クライアント・認証フロー含む） | **不採用**。`manage-users` を含み、IADR-0301 決定 2 / IADR-0329 決定 1 が分けた「取り込み経路と管理経路は別主体」の区切りを壊す |
| **C. `view-realm` ＋ `manage-realm`** | realm 設定の読み書きだけ | **採用**。`PUT /admin/realms/{realm}` は `requireManageRealm()` であり、**`resetPasswordAllowed` 1 項目にだけ効く細粒度権限は Keycloak 24 に無い**（fine-grained admin permissions v1 の対象は利用者・クライアント・グループ・ロールであって realm 設定ではない）。**これが下限である** |
| D. Keycloak の権限ゼロ（エッジで端点を遮断する） | 0 | **不採用**。dev（Traefik）と production（Istio）でエッジが違い**既定の経路が守られない**（IADR-0347 決定 3-B）。ADR-0078 決定 4 が名指したレバーは `resetPasswordAllowed` である |

🔴 **`manage-realm` の危険を薄めない。** 同じ権限で `smtpServer` を外へ向けることも
`bruteForceProtected` を切ることもできる。緩和は 4 つある。

1. 門のコードが差し替えるのは 4 キーだけ（決定 11）
2. **宣言の門**（`check-realm-constraints.js` の `collectServiceAccountRoleGaps`）が天井を固定する ——
   `realm-management` のロールを持つなら `realm-management-roles` スコープを持つこと／
   **`manage-realm` を持つ SA は 1 つだけ**であり `manage-users` を併せ持たず対話ログインの経路も開いていないこと／
   `realm-management-roles` を宣言したクライアントには**ロールを担う SA 利用者が `users[]` に居る**こと
3. `standardFlowEnabled` / `directAccessGrantsEnabled` はどちらも false（MFA 迂回禁止）
4. secret はリポジトリに置かない（Secret `reset-gate-oidc`。dev 既定は起動器、go-live は Vault → ESO）

**2 は [IADR-0329](./IADR-0329_identity-admin-keycloak-provider-and-realm-wiring.md) §記録に留める が
「同型の事故の 2 回目が起きたら足せ」と申し送った検査である。本件が 2 つ目の主体であり、条件が満たされた。**
🔴 **実データへ当てたところ、`synthetic-monitor` が「`serviceAccountsEnabled` なのに `users[]` に居ない」
形で引っかかった。** 精査すると**あれは意図（ロールを 1 つも持たない主体）であり事故ではない**ため、
不変条件を「**`realm-management-roles` を宣言したクライアント**にはロールを担う利用者が居ること」へ狭めた
—— #1301 の形（宣言だけあってロールが誰にも付いていない）はこれで捕まる。

### 変わったこと（窓の上限）

| 窓 | 本 ADR 着地時（PR-A） | 本追記時（PR-C） |
| --- | --- | --- |
| W1（relay 停止） | 運用者が気付いて runbook §0 を打つまで（分〜時間） | **プローブ周期 ＋ プローブのタイムアウト ＋ PUT 往復** |
| W1'（SYN 落ち） | 同上 | 同上（実在側だけ 10 秒かかる性質は変わらない） |
| W2（投函を拒む） | 同上 | 同上（門は利用者と同じ取引を打つので同じ拒否を受ける） |

🔴 **「窓は閉じた」と書かない。** 加えて**門自身が落ちている間は W1 が開いたまま**である
（門を監視する門は作らない。不在は観測側＝ PR-B で見せる）。

### 測っていないこと（本追記の時点）

- 🔴 **上の表の値を 1 つも実測していない**（稼働クラスタが要る。#1245 PR-D）。
  プローブ周期 10 秒・タイムアウト 10 000 ms・連続成功 3 回は**マニフェストが与える初期値**であり、
  正しさは実測で決めて環流する（ADR-0078 決定 1 §残るもの）。
- 🔴 **プローブが relay の応答を実際に受けること**を確かめていない（決定 10 の表は導出である）。
- 🔴 **`check-password-reset-mail.js` の稼働 realm 読み出しは依然 Keycloak pod 内の `kcadm.sh` である。**
  [IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md) の禁則（pod 内で kcadm を exec しない）
  に対する**残債**であり、門と同じ `view-realm` で Admin REST から読むのが正しい。
  **経路の付け替えは稼働クラスタでしか確かめられない**ため本 PR では行わず、環流に含める。
- 🔴 **`probe@probe.invalid` 宛のプローブは、宛先 DNS 検証が再混入したとき go-live では偽陽性になり得る**
  （実在ドメインは引けるため）。fail-closed 側なので受容するが、環流して判断を仰ぐ。

### フォローアップの更新

- (2)（門を入れる）は**本追記で完了**した。(1) 観測・(3) 4 状態の稼働実測・(4) ログイン経路の実測・
  (5) キューの容量設計・(6) 平文 1 ホップの受容・(7) イメージの稼働確認は**そのまま残る**。
- **新規 (8)**: 稼働 realm の読み出しを Admin REST へ移す（IADR-0369 の禁則の残債）。
- **新規 (9)**: 門の不在（`ResetGateProbeAbsent` 相当）を観測へ載せる（#1245 PR-B と同時が自然）。

## 関連

- Supersedes: なし（[IADR-0261](./IADR-0261_keycloak-theme-and-smtp-injection.md) 決定 2 の「`smtpServer` は
  runbook が入れる実行時状態」と [IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md) の
  境界表の該当行を**本 ADR が反転させる**。両 ADR には日付つき追記を入れた）
- Superseded by: なし

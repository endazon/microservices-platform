---
title: IADR-0403 Keycloak の送出先はクラスタ内の近接 MTA（キュー付き Postfix）に固定し、smtpServer を宣言所有・resetPasswordAllowed を条件つき門所有へ移す
type: impl-adr
status: Proposed
related_ids: [SC-15, SC-10, FR-05, FR-22, NFR-09, ADR-0026, ADR-0045, ADR-0078, IADR-0261, IADR-0329, IADR-0332, IADR-0344, IADR-0347, IADR-0369]
author: Claude（実装）
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0078_existence-hiding-response-indistinguishability-and-nearby-mta.md
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
---

# IADR-0403: Keycloak の送出先はクラスタ内の近接 MTA（キュー付き Postfix）に固定し、`smtpServer` を宣言所有・`resetPasswordAllowed` を条件つき門所有へ移す

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
- 関連する実装仕様書: `.ai-context/specs/20260906_issue-1245_nearby-mta-relay.md`

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

## 関連

- Supersedes: なし（[IADR-0261](./IADR-0261_keycloak-theme-and-smtp-injection.md) 決定 2 の「`smtpServer` は
  runbook が入れる実行時状態」と [IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md) の
  境界表の該当行を**本 ADR が反転させる**。両 ADR には日付つき追記を入れた）
- Superseded by: なし

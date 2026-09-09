---
title: IADR-0421 近接 MTA の観測は relay の指標を otel-collector 経由で既存経路へ流し、SC-10 のアラートは実測待ちの暫定閾値で置く
type: impl-adr
status: Accepted
related_ids: [SC-10, SC-15, FR-05, FR-22, NFR-09, NFR-21, ADR-0006, ADR-0026, ADR-0045, ADR-0078, IADR-0130, IADR-0164, IADR-0165, IADR-0168, IADR-0304, IADR-0344, IADR-0347, IADR-0370, IADR-0383, IADR-0404]
author: Claude（実装）
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0078_existence-hiding-response-indistinguishability-and-nearby-mta.md
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability.md
---

# IADR-0421: 近接 MTA の観測は relay の指標を otel-collector 経由で既存経路へ流し、SC-10 のアラートは実測待ちの暫定閾値で置く

- 状態: Accepted
- 日付: 2026-09-09
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: **ADR-0078 決定 3**（上流停止の観測点を近接 MTA のキューへ移す）／
  ADR-0045 決定 8（同決定が部分改定した旧観測点）／ADR-0006（可観測性）／
  SC-10・SC-15・FR-05・FR-22・NFR-09・NFR-21
- 起点 issue: **#1245**（PR-B。PR-A ＝ [IADR-0404](./IADR-0404_nearby-mta-relay-and-realm-ownership.md) 本体、
  PR-C ＝ 同 2026-09-07 追記が着地済み）
- 先行する実装 ADR:
  [IADR-0404](./IADR-0404_nearby-mta-relay-and-realm-ownership.md)（**本 ADR はそのフォローアップ (1) を着地させる**）／
  [IADR-0344](./IADR-0344_dev-mail-capture-mta.md)（捕捉用 MTA。近接 MTA の上流）／
  [IADR-0347](./IADR-0347_reset-existence-concealment.md)（存在秘匿の 3 門）／
  [IADR-0165](./IADR-0165_grafana-interim-alerting.md)（Grafana の暫定アラート。3 系統目の置き場）／
  [IADR-0168](./IADR-0168_grafana-provisioning-parity.md)（経路 A/B のパリティ）／
  [IADR-0304](./IADR-0304_alertmanager-deployment-and-null-receiver.md)（唯一の scrape 対象という不変条件）／
  [IADR-0370](./IADR-0370_slo-evaluation-target-absent-rules.md)（**評価対象の不在**を鳴らす形）
- 関連する実装仕様書: `.ai-context/specs/20260909_issue-1245_mail-relay-observation.md`

## コンテキストと課題

ADR-0078 決定 2 で近接 MTA（キュー付き Postfix）を挟んだ結果、**Keycloak から見た送出は上流が
止まっていても成功する**（relay が 250 を返した以後の失敗は一切戻らない）。同 決定 3 はこれを見越して
**観測点をキューへ移せ**と定めている。IADR-0404 は自ら「**観測点の移動が未了 —— キューの観測が
入るまで、この見逃しは実在する**」と記録し、フォローアップ (1) として本作業を指名した。

**本 ADR が答えるのは 3 つである。** ①何で測るか（既製 exporter か、自前か）
②どう運ぶか（Prometheus の直 scrape か、collector 経由か）③何をいつ鳴らすか（閾値をどう決めるか）。

### 着手前に自分で確かめたこと（上流ソース）

`bokysan/docker-postfix` tag `v5.1.0`（IADR-0404 が pin したもの）の起動経路を読んだ。
🔴 **稼働コンテナで実行して確かめてはいない**（作業機にクラスタが無い。#1245 PR-D で確かめる）。

| # | 事実 | 出典 |
| --- | --- | --- |
| V16 | 起動 `run.sh` は **`reown_folders`** を走らせ、`/var/spool/postfix/{pid,dev,private,public}` を `mkdir -p` し、`postfix set-permissions` を打つ | `scripts/run.sh` L14 ／ `scripts/functions.sh:137-166` |
| V17 | 続く **`postfix_enable_chroot`** が `spool/etc/` と zoneinfo を作り、`localtime` / `nsswitch.conf` / `resolv.conf` / `services` / `host.conf` / `hosts` / `passwd` を**毎回コピーする**（存在すれば無条件に `cp -f`） | 同 `:168-210` |
| V18 | supervisord が起動する postfix は **`/usr/sbin/postfix -c /etc/postfix start-fg`** である（`start` 系は `postfix-script` の `check-fatal` を経由し、**欠けたキューディレクトリを作る**） | `configs/supervisord.conf` ／ `scripts/postfix.sh` |
| V19 | V16〜V18 は**すべて `execute_post_init_scripts`（＝ `/docker-init.d/`）より前**に走る。init hook の時点で spool は整っている | `scripts/run.sh` の並び |
| V20 | イメージは `enable_long_queue_ids` / `hash_queue_names` / `hash_queue_depth` / `maillog_file` を**設定しない**（`functions.sh` に出現 0 件）。よって Postfix の既定が効く | 同 `functions.sh` 全文検索 |
| V21 | 長形式キュー ID は `<秒 base52・最小 6 桁><マイクロ秒 base52・4 桁>'z'<inode base51>`。inode 部は base51 なので **`z` を含まず**、区切りは**最後の `z`**（本家も `strrchr`） | Postfix `src/global/mail_queue.h` の `MQID_*` |
| V22 | 52 進の安全アルファベットは `0123456789BCDFGHJKLMNPQRSTVWXYZbcdfghjklmnpqrstvwxyz`（母音等を除いた 52 字） | Postfix `src/global/safe_ultostr.c` の `safe_chars` |
| V23 | 🔴 **qmgr は deferred のキューファイルの更新時刻を「次回配送予定時刻」＝未来へ書き換える。** 更新時刻から齢は採れない | Postfix のキュー運用（V21 の ID 復号を採った理由） |

### 本リポジトリ側で確かめたこと

- 近接 MTA の宣言は **`deploy/mail-relay/mail-relay.yaml` が spool に volume を張っていない**と明記し、
  「観測が showq を読む exporter を同居させるときに共有の emptyDir を張る —— **稼働で確かめてから**入れる」
  と申し送っていた。**V16〜V19 はその確認の代わりに置いた上流ソースの読みである**（稼働の確認は残る）。
- collector 設定は **3 つ**ある（compose ／ k8s 既定 ／ k8s 転送）。`scripts/check-collector-self-telemetry.js` が
  **自己テレメトリの待受だけ**を突合しており、receiver の増減は見ていない。
- **compose スタックに `mail-relay` は無い**（`deploy/docker-compose.yml` に `mail` / `postfix` のヒット 0 件。
  陽性対照: `keycloak` は 6 行）。運用 Runbook も compose 経路の SMTP 手順を退役させている。
- アラートの置き場は **3 系統**である（compose の `alerts.yml` ／ k8s の inline ／ Grafana の provisioning。
  一致は `check-prometheus-alerts-parity.js` と `check-grafana-alerting.js` が守る）。

## 検討した選択肢

### ① 何で測るか

| 案 | キュー長 | 滞留時間 | 後送失敗 | 供給網 | 評価 |
| --- | --- | --- | --- | --- | --- |
| A. `kumina/postfix_exporter` 系を pin して同居させる | showq の UNIX ソケット（**spool の共有が要る**） | 同左 | **ログファイルを読む** | **イメージが 1 つ増える** | **不採用**。後送失敗はこの image では**そもそも読めない**（V20：`maillog_file` を設定せず rsyslog 経由で stdout。読めるログファイルが無い）。キュー長は結局 spool 共有が要るので、**得る面は同じで供給網だけが増える**（08_data-egress-policy が統制する面） |
| B. relay の中で `postqueue -j` を回し、静的ファイルを HTTP で配る | 正確 | 正確 | 同上（読めない） | 増えない | 不採用。supervisord の設定へ program を差し込む必要があり、**上流イメージの内部構造への依存が増える**（V16〜V19 で確かめた面より深い）。失敗したときに**メール中継そのものを巻き込む** |
| **C. spool を共有し、Node のサイドカーがディレクトリを読む** | ファイル数 | **キュー ID から到着時刻を復号**（V21・V22） | 読めない（代理値で置く） | **増えない**（`node:22-alpine` は門が既に使っている） | **採用** |

🔴 **A を落とした決め手は「増える面が無いのにイメージが増える」ことである。** 供給網の面積は
**得られるものと引き換えにのみ広げる**。ログが読めない以上、A と C の観測できる範囲は同じである。

### ② どう運ぶか

| 案 | 評価 |
| --- | --- |
| A. Prometheus の `scrape_configs` へ `mail-relay:9154` を足す | **不採用**。**「otel-collector が唯一の scrape 対象」という不変条件が壊れる**（IADR-0304 / #546 / #1090）。`up{job="otel-collector"}` はこの唯一性を前提に「パイプライン断」を判定しており、対象が増えると `OtelCollectorDown` の意味が「パイプラインが死んだ」から「collector だけが死んだ」へ静かにずれる |
| **B. otel-collector の `prometheus` receiver で取り、既存の経路（remote write）へ流す** | **採用**。Prometheus 側は 1 行も変わらない。転送が無効な既定構成でも debug エクスポータで受けられる |
| C. relay に OTLP を喋らせる | 不採用。Postfix は OTLP を喋らず、サイドカーに喋らせるなら SDK を足すことになる（外部依存ゼロを崩す） |

### ③ 何をいつ鳴らすか

**閾値は決められない。** ADR-0078 決定 1 §残るもの は所要時間の閾値を「**実測してから定める**」とし、
同 §残るもの はキューの容量設計も未了としている。**実測は稼働クラスタでしか採れない**（#1245 PR-D）。
選択肢は「実測まで置かない」か「暫定値で置き、暫定であることを明示する」の 2 つである。

**後者を採る。** 置かなければ**観測点の移動は完了しない**（ADR-0078 決定 3 は「見える」ことを求めている）。
置いたうえで、**しきい値が測っていない値であることを 3 系統すべてに書く。**

## 決定

### 決定 1: exporter は**自前の最小サイドカー**とし、外部イメージを増やさない

`deploy/mail-relay/mail-queue-exporter.js`（Node 標準のみ・`node:22-alpine`）を
**mail-relay Pod のサイドカー**として置き、`:9154/metrics` を Prometheus のテキスト形式で出す。
スクリプト本体は ConfigMap（起動器が `--from-file` で作る。門と同型）。

- 出す計器は 3 つ: `postfix_up` ／ `postfix_queue_size{queue}` ／
  `postfix_queue_oldest_message_age_seconds{queue}`。**名前は Postfix exporter の慣行に寄せる**
  （将来 kumina 系へ乗り換えるときに、ダッシュボードとアラートの書き換えを最小にする）。
- 対象キューは `incoming` / `active` / `deferred` / `hold` / `maildrop`。
  🔴 **`defer` と `bounce` は数えない** —— あれはメッセージではなく**理由のログ**である。
- 値の既定を**コードが持たない**（spool の位置・ポート・キュー名はマニフェストが与える。門と同じ作法）。

### 決定 2: spool は **emptyDir で共有する。永続性は変わらない**

`/var/spool/postfix` に `emptyDir` を張り、postfix は読み書き・exporter は**読み取り専用**で見る。

- 🔴 **emptyDir の寿命は Pod の寿命であり、従前のコンテナ書き込み層と同じである。**
  「Pod の再作成でキューが失われる」という IADR-0404 の受容は**変わらない**。
  変わるのは**サイドカーから同じ spool を読めること**だけである。
- 🔴 **空の spool を被せても image が起動時に作り直す**（V16〜V19）。IADR-0404 が「稼働で確かめてから入れる」と
  申し送った点について、**代わりに上流ソースで確かめた**。**稼働での確認は残っており、PR-D に入れる。**
- 案として `showq` ソケット越しの読み取りもあり得たが、**同じ spool 共有が要るうえに
  プロトコル実装が増える**ので採らない。
- 🔴 **サイドカーに probe を置かない。観測がメールを止めてはならない。** Pod の Ready は
  **全コンテナ**の Ready を要求するので、exporter に readiness を置くと**その不調が Service の
  endpoint を落とし、Keycloak が投函できなくなる** —— 実在する利用者名だけ 500 になる窓（W1）を、
  **観測の都合で開ける**ことになる。exporter が答えないことは系列の不在で拾う（決定 5・6）。
- 🔴 **それでも同居の宿命は残る。** スクリプトの ConfigMap が欠けるなどで**コンテナが起動できない**
  場合は CrashLoopBackOff になり、Pod ごと NotReady になる（relay も止まる）。
  **これは受容である** —— **静かには壊れず**、門（`reset-gate`）が投函不能を検知して申請を閉じるため
  **存在秘匿は保たれる**（失われるのはリセットメールの送出であり、その代替は ADR-0045 決定 9-b が持つ）。

### 決定 3: 滞留時間は**キュー ID から復号する。ファイルの更新時刻は使わない**

🔴 **qmgr は deferred のキューファイルの更新時刻を「次回配送予定時刻」＝未来へ書き換える**（V23）。
更新時刻から齢を採ると **deferred だけ負の値**になる —— **しかも構文としては正当なので誰も気付かない**
（#1110 が踏んだ「式は正しいが評価対象が違う」と同じ形である）。

長形式キュー ID の時刻部（V21・V22）を復号して到着時刻を得る。
🔴 **復号できない ID が 1 件でも混ざったら、そのキューの系列を出さない**（`postfix_up` も 0 になる）——
最古でないものを最古として出すより、**測らないほうがよい。**

### 決定 4: 経路は **otel-collector の `prometheus` receiver**。Prometheus の scrape 対象を増やさない

k8s の collector 設定 **2 つ（既定・転送）の両方**に `prometheus/mail-relay` を置く。
🔴 **片方だけに置くと、opt-in の apply で collector を差し替えた瞬間に観測が消える**（#1090 の形）。

🔴 **compose の collector 設定には置かない。乖離は意図である** —— compose スタックに近接 MTA は居らず、
**宛先の無い scrape は恒常的に失敗し続ける**。**同ファイルへ理由を書き、compose へ近接 MTA を載せるときの
追随先として名指しした**（黙って違えない）。

### 決定 5: 「測っていない」を **0 で出さない**

読めなかったキューの系列は**出さない**（`postfix_up` だけが 0 になる）。
0 を出すと画面上「滞留なし」に見え、**沈黙が正常と読める** —— 本リポジトリはこの形を 2 度踏んでいる
（#1110 の存在しない計器名、#1246 の生産者の居ない指標）。
**系列の不在は専用のアラート（`MailRelayQueueSeriesAbsent`）が拾う**（IADR-0370 と同じ形）。

### 決定 6: アラートは 3 件 ×3 系統。**しきい値は暫定であることを 3 系統すべてに書く**

| 名前 | 式 | for | severity | 由来 |
| --- | --- | --- | --- | --- |
| `MailRelayDeferredBacklog` | `postfix_queue_size{queue="deferred"} > 0` | 5m | warning | 上流停止。`for` は再送の最小間隔 60s の 5 周期 |
| `MailRelayDeferredMessageNearExpiry` | `postfix_queue_oldest_message_age_seconds{queue="deferred"} > 1200` | 5m | critical | キュー寿命 1800s の内側で、**破棄の約 5 分前**に鳴る |
| `MailRelayQueueSeriesAbsent` | `absent(postfix_queue_size{queue="deferred"})` | 5m | warning | 観測点そのものの不在（`platform-slo-evaluation-target` 群へ入れる） |

🔴 **上の数字はどれも実測ではない。** キュー寿命 30 分（＝ realm の `actionTokenGeneratedByUserLifespan`）と
再送間隔から**導いた**初期値である。ADR-0078 決定 1 は閾値を「実測してから」と定めており、
**確定は #1245 PR-D の実測を待つ。**

### 決定 7: **後送の失敗率は「率」として持たない**

**キュー寿命を超えたメッセージは spool から消える**（バウンスも溜めない設定）。
**届いたものも捨てられたものも同じく「消えている」ので、spool を読むだけでは事後に数えられない。**
正しく数えるにはログの取り込みが要るが、この image は読めるログファイルを持たない（V20）。

⇒ **捨てられる前に鳴らす**（決定 6 の 2 件目）。**「失敗率 N%」という数字は持たない。**
持っているのは「**失敗し続けている状態**」と「**その齢**」である。**この差は繰り延べであり、
解消していない**（フォローアップ (3)）。

### 決定 8: しないこと

- **Prometheus の scrape 対象を増やすこと**（決定 4 の理由）。
- **SC-10 の画面へキューの数字を出すこと** —— 画面は専用ツールへの入口であり、時系列は Grafana で見る
  （ナレッジ健全性の 2 指標と同じ扱い。SC-10 の画面仕様書は変えない）。
- **門（`reset-gate`）の不在を観測へ載せること** —— IADR-0404 フォローアップ (9) が求めているが、
  門に計器を持たせる変更であり本 PR の射程ではない。**残件として明示する**（フォローアップ (2)）。
- **閾値を確定させること**（決定 6）。
- **`check-collector-self-telemetry.js` の射程を receiver まで広げること** —— 「同型の事故が 2 回」の
  条件を満たしていない（本件が 1 回目である。**記録に留める**）。

## 理由

- **exporter を自前にしたのは、既製品で増える面が無かったからである。** ログが読めない以上、
  kumina 系が出せるのもキュー長と齢だけであり、**同じものを得るためにイメージを 1 つ増やす**ことになる。
  供給網の面積は 08_data-egress-policy が統制する対象であり、**等価交換でないなら広げない。**
- **collector 経由にしたのは、不変条件を壊さないためである。** 「唯一の scrape 対象」は
  `OtelCollectorDown` が「パイプライン断」を名乗れる根拠そのものであり、対象が増えると
  **アラートの意味が静かにずれる**（#1110 と同型の、誰も気付かない壊れ方である）。
- **キュー ID から齢を採ったのは、更新時刻が別のものを指しているからである。** 更新時刻でも
  値は出る —— **出るが、意味が違う。** 「数字が出ていること」と「正しいものを測っていること」は
  別である（本リポジトリが #1110 で学んだ区別）。
- **暫定値で置いたのは、置かないと観測点の移動が完了しないからである。** ADR-0078 決定 3 は
  「見える」ことを求めており、閾値の確定はその後の話である。**暫定であることを書けば、
  実測した人が上書きできる。** 書かなければ、暫定値が確定値として固まる。

## 結果

- **良い影響**: ADR-0078 決定 3 が満たされ、**近接 MTA を挟んだことで失われていた観測が戻る**。
  上流の停止・認証失敗・宛先拒否が、利用者に見えないまま 30 分で捨てられる前に鳴る。
  外部イメージも Prometheus の scrape 対象も増えない。
- **悪い影響 / トレードオフ**: mail-relay Pod にコンテナが 1 つ増える（要求 10m CPU / 32Mi）。
  🔴 **同居ゆえに、exporter が起動できないと relay も止まる**（決定 2 の最後）。
  spool に emptyDir が張られる（永続性は不変だが、**イメージの初期化と両立することを稼働で
  確かめていない**）。collector が 30 秒ごとに 1 本 scrape する。
- **測っていて、直していないこと**:
  🔴 **後送の失敗率は「率」として測れていない**（決定 7）。
  🔴 **門の不在は観測に載っていない** —— 門が落ちている間は存在秘匿の窓 W1 が開いたままである。
  🔴 **しきい値・`for`・scrape 間隔のいずれも実測していない**（決定 6）。
  🔴 **本 PR の配備物は 1 つも稼働で動かしていない**（作業機にクラスタが無い）。
  V16〜V23 は**上流ソースからの読み取り**であり、稼働の確認ではない。
  🔴 **compose 経路との乖離**（決定 4）。受容として記録した。
- **フォローアップ**:
  (1) 稼働クラスタで配備し、上記「測っていないこと」を実測して閾値を確定する（#1245 PR-D）。
  🔴 **空の spool を被せても relay が起動すること**を最初に確かめる（V16〜V19 の検証）。
  (2) 門（`reset-gate`）の不在を観測へ載せる（IADR-0404 フォローアップ (9)。**本 PR では入れない**）。
  (3) 後送の失敗率を「率」として測る（ログの取り込み経路が要る。決定 7）。
  (4) キューの容量設計（寿命・永続化）を計画へ環流する（IADR-0404 フォローアップ (5) のまま）。
  (5) 実測後、`docs/observability/mail-relay-queue-metrics.md` の「暫定」表記を落とす。

## 関連

- Supersedes: なし（[IADR-0404](./IADR-0404_nearby-mta-relay-and-realm-ownership.md) のフォローアップ (1) を
  着地させるものであり、同 ADR の決定は 1 つも覆さない）
- Superseded by: なし

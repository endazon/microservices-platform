---
title: IADR-0675 クラウドの最小構成は、バックアップを S3 互換のオブジェクトストレージ（Object Lock・lifecycle）へ書き、警報を Discord へ送り、常に鳴る警報をクラスタの中から外の監視（Healthchecks.io）へ heartbeat として送る（#2402）
type: impl-adr
status: Accepted
related_ids: [NFR-21, NFR-03, NFR-05, ADR-0002, ADR-0006, ADR-0008, IADR-0471, IADR-0548, IADR-0571, IADR-0669, IADR-0670]
author: claude
created: 2026-11-05
updated: 2026-11-06
related_specs:
  - ../specs/20261105_2402_cloud-backup-alerts-heartbeat.md
---

# IADR-0675: クラウドのバックアップ・警報・heartbeat（#2402）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: claude（#2402。コーディネーターの指示 2026-11-05）

## 起点・関連

- 起点 issue: #2402（PoC セッションの監査・2026-11-05）。対の issue は #2401（クラウドの最小構成）。
- 前提:
  - IADR-0471（age で暗号化した日次のバックアップ）・IADR-0669（保管先のロックと改名の後の検算）・IADR-0670（依存先の待ち）。
  - IADR-0571（Alertmanager の受信先を Secret から initContainer で描画する形）。

## コンテキストと課題

- PoC を単一ノードの VPS へ移す。今の運用の部品は、自宅の PC に結び付いている。
  - バックアップの保管先は、PC の 2 つのドライブ（hostPath）だけである。
  - Alertmanager は、どこへも送らない。
  - 外からの死活監視は、PC の PowerShell が行う。
- ノードのファイアウォールは SSH だけを通す（#2401）。外から届く口は増やせない。
- インフラ費の上限は月 5,000 円である。ノードは 8 GB で、Prometheus の他に AST と MSP の最小構成が載る。

## 決定

1. **バックアップは、既存の本体（`backup.sh`）に S3 互換の保管先を足して書く。** 別の実装を作らない。
   - 受取人の検査・age の暗号化・依存先の待ち・staging・`SHA256SUMS` は、hostPath の回と同じ道を通る。
   - 送るのは rclone（Alpine の community パッケージ。版・sha256・署名で固定し、クラウドのイメージに同梱する）。
     資格情報は env（secretKeyRef）で rclone に渡し、引数に載せない。
   - 書く順: 同じ名前の回が無いことを確かめる → 成果物と `SHA256SUMS` を送る → 送った回を作業場所へ読み戻し、
     `SHA256SUMS` の一致と `sha256sum -c` を確かめる → 最後に `COMPLETE`（`SHA256SUMS` の sha256）を置く。
   - 🔴 **オブジェクトストレージには改名と `mkdir` の原子性が無い。** IADR-0669 のロックは使わず、
     **`COMPLETE` の無い回を完了と見なさない**ことで同じ約束（欠けた回を完了と記録しない）を守る。
     リストア試験も `COMPLETE` と `SHA256SUMS` の一致を確かめてから戻す。
   - hostPath の既定（`BACKUP_TARGETS` の既定値）は変えない。`BACKUP_TARGETS=none` で hostPath を使わない。
   - ［2026-11-05 / #2406 のレビュー 🔴2］**一覧（`rclone lsf`）の終了コード 3（directory not found）だけを「空」と見なす。**
     実測（rclone v1.75.2）: bucket 型の `:memory:` では無い接頭辞が 3。S3 の API（`rclone serve s3`）では無い接頭辞が 0 と空、
     **バケットが無いと 3**。3 を空と見なしても、バケットが無いときは続く送信が `NoSuchBucket` で失敗する（実測）。
     3 以外の失敗（認証の誤り・接続・時間切れ）は失敗のまま返し、何も送らない（認証の誤りで実測。試験 T-2402-13）。
     同じ実測で、初回の回・月の最初の回・同じ名前の回の拒否・バケットの不在・鍵の誤りが、`rclone serve s3` の実物の上で期待どおりに動いた。
2. **保持は、日次 30 日・月次 12 か月とし、削除はバケットの lifecycle が行う。** スクリプトは S3 の回を消さない。
   - 月次は、その月の最初の完全な回を `monthly/` へも書く（`monthly/` に同じ月の回が無いときだけ）。
     ［2026-11-06 追記 / #2406 のレビュー 🟡］**同じ月の回として数えるのは、`COMPLETE` を持つ回だけ**にした。月次の回が途中で失敗すると `COMPLETE` の無い回が残り、
     それを数えるとその月の月次が取り直されない。取りこぼしの回は消さずに残し（Object Lock の下では消せない）、翌日の完全な回で取り直す。
     回の中身を確かめられないときは、取得済みとも未取得とも決めずに失敗を返す（daily は書けている）。試験は `backup.test.sh` の T-2402-14。
     ［2026-11-06 追記 / #2406 の再レビュー 🟡］確かめる回は**新しい順に `BACKUP_S3_MONTHLY_CHECK_MAX` 件（既定 2）まで**とし、`COMPLETE` を見つけたら打ち切る。
     1 件の確かめが rclone の呼び出し 1 回なので、最悪の合計は「依存先の待ち＋確かめ＋間隔＋（9 ＋ K）× 240 秒＋余裕 300 秒」＝ 3555 秒で、`activeDeadlineSeconds` 3600 秒に収まる（T-2402-12。K = 3 では超えることも確かめた）。
     上限までに決められず、まだ確かめていない回が残るときは、取得済みとも未取得とも決めずに失敗で返す（daily は書けている。T-2402-15）。
     溜まったときの手当ては、上限を上げた 1 回（K = 31）を手で起こし、同じパイプでその Job の `activeDeadlineSeconds` を同じ式の最悪（10,515 秒）より大きい 10,800 秒にする（runbook。T-2402-16 が手順の値を式で突き合わせる）。
     確かめは新しい順なので、K を小さくして何回かに分けても古い回へは届かない。分けない。
   - 消せない形: バケットを Object Lock（COMPLIANCE・既定の保持 **28 日**）で作る。保持の期間の内は、資格情報を持つ者でも消せない。
   - ［2026-11-05 / #2406 のレビュー 🟡4］**Object Lock は日次の保持（lifecycle の 30 日）より真に短くする。** 同じ 30 日だと、
     期限の日に lifecycle が版を消そうとしたとき、まだ Object Lock の内側に在って拒まれ得る（作成の時刻と lifecycle の評価の時刻の
     ずれで、どちらが先かが決まらない）。版付きのバケットでは、期限で現行の版に削除の印が付き、古い版は NoncurrentDays（1 日）の後に
     消える。Object Lock を 28 日にすれば、現行の版の期限（30 日）にも、古い版の削除（31 日）にも、2 日以上の余りが残る。
     月次（365 日）は Object Lock より十分に長い。試験 T-2402-36 が「Object Lock ＜ 日次の保持」を固定する。
   - 資格情報は、バックアップのバケットだけを持つ専用のプロジェクトのものにする。スクリプトは存在の確かめと読み戻しをするので、
     読み・一覧・書きが要る。**書き込みだけには絞らない**（消せないことは Object Lock が持つ）。
   - 費用: Hetzner Object Storage の基本料（1 TB の保管・転送を含む）の範囲に収まる（暗号化したダンプは数十 MB/回）。
3. **警報は Discord の webhook へ送る。URL は Secret から initContainer が描画する。**
   - Alertmanager は v0.27.0 のまま（経路 B と同じ digest）。v0.27 の `discord_configs` は URL のファイル読みを持たない（amtool で実測。
     「no discord webhook URL provided」）ので、IADR-0571 と同じく initContainer が Secret の値を emptyDir の設定へ書く。
   - 🔴 URL が空・形が違う（`https://discord.com/api/webhooks/<数字>/<トークン>` でない）なら、Alertmanager を起動しない。
     黙って `default-null` へ落とさない —— 落ちた分は次の決定 4 の heartbeat が止まって見える。
   - Prometheus は保持 7 日・1GB、メモリの上限 768Mi（8 GB のノード）。Grafana・Loki・Tempo は置かない。
   - AST の警報（`PrometheusRule`）は、ConfigMap `ast-prometheus-rules` に入った YAML の `spec` を initContainer が取り出し、
     `promtool check rules` を通してから読む（Prometheus Operator を持ち込まない）。読めなければ起動しない。
     ConfigMap が無いときは起動し、警報 `AstAlertRulesNotLoaded` を Discord へ出す。
4. **死活は、常に鳴る警報（`Watchdog`）を Alertmanager から外の監視（Healthchecks.io の無料の枠）へ 5 分ごとに送る。**
   - クラスタの中から外へ送るだけで、受け口を増やさない。
   - Prometheus・Alertmanager・ノード・外への通信のどれが止まっても ping が止まり、外の監視がオーナーへ知らせる
     （監視の側が止まっても黙らない）。Alertmanager の設定の誤り（決定 3）も、同じく ping の停止として見える。
   - ［2026-11-05 / #2406 のレビュー 🟡3］**Discord への到達は、死活（check A）と別の check B で見る。** Discord への送信に失敗が無い間だけ
     鳴る `DiscordDeliveryHealthy` を B へ 5 分ごとに送り、失敗（`AlertmanagerDiscordFailing`）は B の `/fail` へ送る。
     失敗の間は B へ ping が来ないので B は下がったままになる。同じ check の `/fail` にすると、次の Watchdog の ping で
     すぐに「up」へ戻り、失敗が続いているのに知らせが 1 度で止まる。Healthchecks.io の無料の枠で check を 1 つ足すだけで済む。
   - ping の URL も Secret から読む（Alertmanager の `webhook_configs` の `url_file`）。

## 実測（2026-11-05 / #2406 の再レビュー）

CI と同じ版のバイナリを手元で起動して、警報の式の前提を確かめた。

1. **`AstAlertRulesNotLoaded` の `rule_group` の形**（Prometheus v2.52.0）:
   - `prometheus_rule_group_rules` の `rule_group` は `<規則ファイルの絶対パス>;<群名>` の形である。
     例: `…/ast-rules/ai-stock-trading-alerts.yml;ai-stock-trading.trade-cycle`。
   - AST の実ファイル（`deploy/observability/alerts/ai-stock-trading-alerts.yaml`）から initContainer の script で取り出した群名は、
     `ai-stock-trading.order-execution`・`ai-stock-trading.stop-loss`・`ai-stock-trading.trade-cycle` の 3 つで、
     どれも `ai-stock-trading.` で始まる。
   - 自分を収集する設定（`job_name: prometheus`）の下で、AST の規則がある場合とない場合の評価を見た。
     ある場合、`absent(prometheus_rule_group_rules{rule_group=~".*;ai-stock-trading\\..*"})` は空で、群は 3 つ数えられる。
     ない場合、同じ式は 1 になり、警報は pending になる。式は直さない。
   - 🔴 この警報は、Prometheus が自分を収集していることを前提にする（`/metrics` に出ていても、収集しなければ TSDB に系列が無く、
     規則があっても鳴る）。設定は `job_name: prometheus` で自分を収集している。
2. **`DiscordDeliveryHealthy` の前提「失敗のカウンタが起動時に 0 で現れる」**（Alertmanager v0.27.0）:
   - Discord の受信先を置いて起動すると、`/metrics` に `alertmanager_notifications_failed_total{integration="discord",reason=…}` が
     `clientError`・`contextCanceled`・`contextDeadlineExceeded`・`other`・`serverError` の 5 本、どれも 0 で現れた
     （`alertmanager_notifications_total{integration="discord"}` も 0）。
   - Prometheus v2.52.0 でこの Alertmanager を収集すると、`sum(increase(…{integration="discord"}[15m])) == 0` は 15 秒で値を持った
     （＝警報が鳴り、check B へ ping が届く）。`reason` が複数本あるので、式は `sum` で束ねたまま変えない。
   - Alertmanager を収集できないときは式が空になり、警報が鳴らず、B が下がる。これは意図どおりで、黙らない。

## #2401 との結線（2026-11-05）

#2401 の実物（`deploy/cloud/infra`・`bootstrap-secrets.sh`）に合わせて、#2402 の側を次のように揃えた。名前は試験 T-2402-37・39・50・51 で突き合わせる。

1. **全体の kustomization:** `deploy/cloud/kustomization.yaml` を置き、`infra`・`backup`・`observability` を束ねる。`infra` は単独でも描画できる形のまま残す。
2. **Postgres の資格情報:** 超利用者は乱数の名前で作られる。`PGUSER` と `PGPASSWORD` は、Secret `postgres` のキー `username` と `password` から読む（Postgres 本体と同じ参照）。
3. **TLS:** Postgres は `hostssl` しか受けない。そのため `PGSSLMODE=verify-full` とし、`PGSSLROOTCERT` に ConfigMap `platform-infra-ca` の `ca.crt` だけをマウントする（非 optional）。
   - 名前の照合に使う `PGHOST` は Service `postgres` である。この名前は #2401 の証明書の SAN（`DNS:postgres,…`）に入っている。
   - 形は #2405（ローカル）と同じだが、CA の置き場所が違う。#2401 の Secret `postgres-tls` は `ca.crt` を持たないので、ConfigMap から読む。
4. **入りの許可:** #2401 は platform-infra を default-deny にしている。その上に、次の 2 つの別宣言を足した（NetworkPolicy は加算的なので、#2401 の宣言は書き換えない）。
   - `postgres-ingress-backup`: バックアップの Pod から Postgres の 5432 だけを許す。
   - `otel-collector-ingress-prometheus`: Prometheus から otel-collector の 8888 だけを許す。
5. **otel-collector:** #2401 の設定は `debug` の exporter だけである。そこで kustomize の patch（`observability/patches/otel-collector.yaml`）で、メトリクスの経路に `prometheusremotewrite`（`http://prometheus:9090/api/v1/write`）を足し、Service に 8888 の口を足した。
   - traces と logs は変えない。
   - ConfigMap の設定は 1 つの文字列なので、patch は全体を置き換える。T-2402-51 が、remote write の他は #2401 の設定と同じであることを木で突き合わせる。

［2026-11-06 追記 / #2402］#2401 のマージ（#2409）の後に取り込んで、次も揃えた。

6. **適用の対象:** 手順書（`deploy/cloud/README.md` §3）の適用を `deploy/cloud`（最上位）にした。`infra` だけでは patch・バックアップ・観測が入らない。
7. **Keycloak のプローブ:** startupProbe（`/health/started`。最長 10 分）と livenessProbe（`/health/live`、9000）を足し、本 IADR の範囲に含めた（#2409 のレビューの 🟢）。
   単一ノードの無人の運用で、起動の後のハングを作り直す。起動の間（realm の取り込み）は startupProbe が liveness を止める。描画の試験が固定する。

## 却下した代替案

- **Alertmanager を v0.28 へ上げて Discord の URL をファイルで読む**: 経路 B と版が分かれ、digest の固定を 2 つ持つ。描画の形には先例（IADR-0571）がある。
- **外から HTTP で見る監視（uptime の SaaS がクラスタを叩く）**: 受け口を開ける必要がある。ファイアウォールの方針（SSH だけ）に反する。
- **バックアップ専用の別のスクリプト**: 暗号化・検算・依存先の待ちの約束が二重になる（片方だけ直る）。
- **スクリプトが保持の削除をする（hostPath と同じ）**: 削除の権限が要り、Object Lock と両立しない。

## 結果・残余

- 良い: オフサイトの暗号化したバックアップ・Discord への警報・外からの死活監視が、受け口を増やさずに揃う。
- 残余:
  - バックアップの Job の失敗を Prometheus は見ない（kube-state-metrics を持ち込まない）。週次のリストア試験と Job の一覧で見る。
  - Healthchecks.io そのものが止まると、知らせは届かない（外の監視を 2 重にはしない）。
  - Object Lock が使えないプロバイダでは、資格情報を持つ者が回を消せる（runbook の確かめの手順で検出する）。
  - ［2026-11-06 追記 / #2402］`deploy/cloud/` の infra（#2401）との結線は、この PR で済ませた（上の「#2401 との結線」）。
  - ［2026-11-06 追記 / #2406 のレビュー 🟡］`AstAlertRulesNotLoaded` は、AST の群名が `ai-stock-trading.` で始まることに依る（実測は上の「実測」の 1）。この依存は受容する。
    AST の側で群名を変えるときは、式を直して `rule_group` の実際の値を確かめる（runbook §4 の 3）。

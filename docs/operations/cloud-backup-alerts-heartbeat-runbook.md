---
title: 運用 Runbook — クラウドの最小構成のバックアップ（オブジェクトストレージ）・警報（Discord）・外からの死活監視（heartbeat）
type: runbook
status: draft
author: claude
created: 2026-11-05
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-21, NFR-03, NFR-05, NFR-18]
adrs: [ADR-0002, ADR-0006, ADR-0008]
iadrs: [IADR-0698, IADR-0689, IADR-0675, IADR-0471, IADR-0571, IADR-0669, IADR-0670, IADR-0697]
specs: [20261107_llm-cost-cap-persistence, 20261107_cloud-infra-pod-hardening, 20261107_2471_cloud-external-secrets-stage2, 20261106_2471_cloud-openbao-eso, 20261105_2402_cloud-backup-alerts-heartbeat]
issues: [#2471, #2402, #2401]
-->

# 運用 Runbook: クラウドのバックアップ・警報・heartbeat

> **運用仕様書（[`operations.md`](operations.md)）の「バックアップ」と「監視・アラート」の下位にあたる手順書である。**
> 対象はクラウドの最小構成（単一ノードの k3s・`deploy/cloud/`）の `deploy/cloud/backup/` と `deploy/cloud/observability/`。
> ローカルの PoC（hostPath の 2 か所）の手順は [platform-infra-backup-runbook.md](platform-infra-backup-runbook.md) のままである。
>
> **状態を `draft` に留めている理由**: クラウドのノードで §5 の止め試験と §6 のリストア試験をまだ記録していない。

## 何が起きるか

| 部品 | 動き | 止まったら |
| --- | --- | --- |
| バックアップ（CronJob `platform-backup-postgres`・JST 12:00） | Postgres の全 DB と globals を age で暗号化し、オブジェクトストレージの `daily/<回>/` へ送る。月の最初の完全な回は `monthly/<回>/` にも送る。送った回を読み戻して検算し、最後に `COMPLETE` を置く | Job が失敗として残る（`kubectl -n platform-infra get jobs`）。`COMPLETE` の無い回は完了ではない |
| 警報（Prometheus → Alertmanager → Discord） | AST の警報と、基盤の警報（collector の停止・受け取りの拒否・メトリクスの途絶え・AST の規則の不在）を Discord のチャンネルへ送る | Discord へ届いているかは外の監視の **check B** が見る（届いている間だけ ping が来る。失敗は B の `/fail` へ送る） |
| 死活（常に鳴る警報 `Watchdog` → Healthchecks.io） | Alertmanager が 5 分ごとに外の監視へ ping を送る（クラスタの中から外へ。受け口は開けない） | **外の監視が、ping が途絶えたことをオーナーへ知らせる**（メール・Discord） |

- 保持: 日次 30 日・月次 12 か月。**削除はバケットの lifecycle が行う**（スクリプトは消さない）。
- バケットは Object Lock（COMPLIANCE・28 日）で作る。保持の期間の内は、資格情報を持つ者でも回を消せない。
  Object Lock は日次の保持（30 日）より短くしてある —— 期限の日に lifecycle の削除が Object Lock に拒まれないように。
- 費用: オブジェクトストレージの基本料（1 TB の保管と転送を含む）と、外の監視・Discord の無料の枠に収まる。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | クラウドのノードへの SSH（kubectl は SSH のトンネル越し）、Hetzner Console、Discord のサーバーの「ウェブフックの管理」、Healthchecks.io のアカウント |
| 必要なツール | `kubectl`、`docker`（イメージのビルド）、`rclone` か `aws` CLI（バケットの設定とリストア試験）、`age`（リストア試験） |
| 所要時間の目安 | 初回の準備 60 分、止め試験 20 分、リストア試験 30 分 |

🔴 **秘密の値（S3 の鍵・webhook の URL・ping の URL）をコマンドの引数・シェルの履歴・リポジトリに書かない。**
下の手順はどれも、値を**手元のファイル**（`umask 077` で作り、使ったら消す）から読ませる。

## 1. オブジェクトストレージ（バケット・Object Lock・lifecycle）

1. Hetzner Console で、**バックアップ専用のプロジェクト**を作る（資格情報が届く範囲をこのバケットだけに閉じる）。
2. そのプロジェクトで S3 の資格情報を作る。表示された鍵は手元のファイルへ写す（`~/.secrets/backup-s3.env`。`chmod 600`）:
   ```text
   AWS_ACCESS_KEY_ID=...
   AWS_SECRET_ACCESS_KEY=...
   ```
3. バケットを **Object Lock を有効にして**作る（Object Lock は作成時にしか有効にできない）。リージョンはノードと同じにする。
4. 既定の保持と lifecycle を当てる（`deploy/cloud/backup/` の JSON。接頭辞 `ast-poc/` は下の `dest` と揃える）:
   ```bash
   set -a; . ~/.secrets/backup-s3.env; set +a
   EP=https://fsn1.your-objectstorage.com   # リージョンのエンドポイント
   aws --endpoint-url "$EP" s3api put-object-lock-configuration --bucket <バケット> \
     --object-lock-configuration file://deploy/cloud/backup/object-lock.json
   aws --endpoint-url "$EP" s3api put-bucket-lifecycle-configuration --bucket <バケット> \
     --lifecycle-configuration file://deploy/cloud/backup/bucket-lifecycle.json
   ```
5. **確かめる**（消せないこと）: 試しの小さなオブジェクトを置き、版を指定して消せないこと（`AccessDenied`）を見る。
   ```bash
   aws --endpoint-url "$EP" s3api put-object --bucket <バケット> --key lock-check.txt --body /etc/hostname
   aws --endpoint-url "$EP" s3api list-object-versions --bucket <バケット> --prefix lock-check.txt
   aws --endpoint-url "$EP" s3api delete-object --bucket <バケット> --key lock-check.txt --version-id <上の VersionId>   # 拒否されること
   ```
   拒否されないなら、Object Lock が効いていない。バケットを作り直す（効かないプロバイダなら、その旨を issue に記録する）。

## 2. クラスタへの投入（Secret・ConfigMap・イメージ）

1. S3 の鍵と宛先。鍵は秘匿管理（OpenBao）の `cloud/platform/platform-backup-s3` に書き、ESO が Secret `platform-backup-s3` に写す
   （手で Secret を作らない。手順の前提は[秘匿管理の手順書](cloud-openbao-init-unseal-runbook.md)の「外から持ち込む値」）。
   宛先（ConfigMap `platform-backup-s3-target`）は秘密ではないので、そのまま作る:
   ```bash
   P="bash deploy/cloud/vault/put-external-secret.sh --live --operator-file <operator>.age --identity <識別子のファイル>"
   read -rs v; printf '%s' "$v" | $P cloud/platform/platform-backup-s3 access-key-id; unset v       # アクセスキーの ID
   read -rs v; printf '%s' "$v" | $P cloud/platform/platform-backup-s3 secret-access-key; unset v   # シークレットキー
   kubectl -n platform-infra create configmap platform-backup-s3-target \
     --from-literal=endpoint=https://fsn1.your-objectstorage.com \
     --from-literal=dest=objstore:<バケット>/ast-poc
   ```
2. Postgres への接続は TLS（verify-full）である。CA の ConfigMap `platform-infra-ca` と、超利用者の Secret `postgres`（`username` / `password`）は、
   クラウドの最小構成の秘匿管理（OpenBao）の手順で作る（[手順書](cloud-openbao-init-unseal-runbook.md)）。手で作らない。
3. age の公開鍵（ConfigMap `platform-backup-age-recipients`）はローカルと同じ手順で作る（[platform-infra-backup-runbook.md](platform-infra-backup-runbook.md) の準備）。秘密鍵はクラスタに置かない。
4. バックアップのイメージ（レジストリへ置かない。手元でビルドしてノードへ取り込む）:
   ```bash
   T=microservices-platform/platform-backup-cloud:pg16.15-age1.3.2-r1-rclone1.75.2-r0
   docker build --platform linux/amd64 -t "$T" deploy/cloud/backup/image
   docker save "$T" | ssh <ノード> 'sudo k3s ctr images import -'
   ssh <ノード> "sudo k3s ctr images ls | grep platform-backup-cloud"   # digest を記録する
   ```
   CronJob は `imagePullPolicy: Never` である。取り込んでいなければ Pod は起動しない（外のレジストリから同じ名前を取らない）。
5. 初回の手動の回で、書けることを確かめる:
   ```bash
   kubectl -n platform-infra create job --from=cronjob/platform-backup-postgres backup-first
   kubectl -n platform-infra logs -f job/backup-first    # 「書きました: objstore:…/postgres/daily/<回>」と「完了」
   ```
   送信が拒否される（Object Lock のバケットが要求の検算の見出しを求める等）なら、ログを付けて issue に記録する。

## 3. Discord の受信先

1. Discord のサーバーで、警報のチャンネルの「連携サービス → ウェブフック」を作り、URL を手元のファイルへ写す。
2. Secret `alertmanager-discord`（キー `webhook-url`）は、秘匿管理の `cloud/platform/alertmanager-discord` に書き、ESO が写す:
   ```bash
   P="bash deploy/cloud/vault/put-external-secret.sh --live --operator-file <operator>.age --identity <識別子のファイル>"
   read -rs v; printf '%s' "$v" | $P cloud/platform/alertmanager-discord webhook-url; unset v   # URL を 1 行（引数・履歴に出さない）
   ```
3. Alertmanager は起動時に 1 度だけ描画する。差し替えたら（`--overwrite` で書き直し、ESO が写した後に）`kubectl -n platform-infra rollout restart deploy/alertmanager`。
   URL が空・形が違うと Alertmanager は起動しない（`Init:Error`。ログは `kubectl -n platform-infra logs deploy/alertmanager -c render-config`。値は表示しない）。

## 4. 外の監視（Healthchecks.io）と heartbeat

1. Healthchecks.io（無料の枠）にプロジェクトを作り、check を **2 つ**作る。
   - **check A（死活）**: **Period: 5 分、Grace: 5 分**。常に鳴る警報 Watchdog が 5 分ごとに ping する。途絶えてから最大 10 分で知らせる。
   - **check B（Discord への到達）**: **Period: 5 分、Grace: 10 分**。Discord への送信に失敗が無い間だけ、警報 DiscordDeliveryHealthy が
     5 分ごとに ping する。失敗が起きると、警報 AlertmanagerDiscordFailing が B の `/fail` を叩いてすぐに下げる。
     失敗の間は B へ ping が来ないので、**B は下がったまま**である（A の ping では戻らない。B が戻るのは、失敗が 15 分の窓から消えたとき）。
   - どちらも「Integrations」で、オーナーへ届く宛先を**2 つ以上**つなぐ（メールと Discord。Discord の連携は、警報とは別のチャンネルにする —— 警報の webhook が壊れたときも届くように）。
2. 2 つの check の ping の URL を、秘匿管理の `cloud/platform/alertmanager-heartbeat`（キー `ping-url`・`discord-ping-url`・`discord-fail-url`）に書く。
   ESO が Secret `alertmanager-heartbeat` に写す:
   ```bash
   P="bash deploy/cloud/vault/put-external-secret.sh --live --operator-file <operator>.age --identity <識別子のファイル>"
   read -rs a; printf '%s' "$a" | $P cloud/platform/alertmanager-heartbeat ping-url; unset a          # check A の ping の URL
   read -rs b; printf '%s' "$b" | $P cloud/platform/alertmanager-heartbeat discord-ping-url
   printf '%s/fail' "$b" | $P cloud/platform/alertmanager-heartbeat discord-fail-url; unset b          # check B の URL の後ろに /fail
   ```
   2 つの URL が同じ（check を分けていない）・`discord-fail-url` が「`discord-ping-url`/fail」でないときは、Alertmanager は起動しない。
   この Secret が無いと Alertmanager の Pod は起動しない（ping が始まらず、外の監視が「一度も来ていない」と見せる）。
3. AST の警報の規則（ConfigMap `ast-prometheus-rules`。AST の `PrometheusRule` の YAML をそのまま入れる）:
   ```bash
   kubectl -n platform-infra create configmap ast-prometheus-rules \
     --from-file=<AST の作業ツリー>/deploy/observability/alerts/ai-stock-trading-alerts.yaml
   kubectl -n platform-infra rollout restart deploy/prometheus
   ```
   読めない規則なら Prometheus は起動しない（`kubectl -n platform-infra logs deploy/prometheus -c load-ast-rules`）。
   ConfigMap が無いときは起動し、警報 `AstAlertRulesNotLoaded` が 15 分後に Discord へ出る。
   取り込んだ直後に、AST の群が読み込まれたことを確かめる（`rule_group` が `<パス>;ai-stock-trading.<群名>` の形で並ぶこと。0 件なら取り込めていない）:
   `kubectl -n platform-infra exec deploy/prometheus -- wget -qO- 'http://localhost:9090/api/v1/query?query=prometheus_rule_group_rules' | grep -o '"rule_group":"[^"]*ai-stock-trading\.[^"]*"'`
   この警報の式は、`prometheus_rule_group_rules` の `rule_group` が「`<パス>;<群名>`」の形で、AST の群名がすべて `ai-stock-trading.` で始まることを前提にする。**AST の側で群名を変えるときは、`deploy/cloud/observability/prometheus.yaml` の `AstAlertRulesNotLoaded` の式も直し、取り込んだ後に `rule_group` の実際の値を確かめる**（外れると、常に鳴る誤警報か、読み込めていないのに鳴らない、のどちらかになる）。

## 5. 試験（初回と、構成を変えたとき）

1. **Discord へ届く**: 試しの警報を流す（クラスタの中から。受け口は開けない）:
   ```bash
   kubectl -n platform-infra exec deploy/alertmanager -- amtool alert add RunbookTest severity=warning \
     --annotation=summary="試験の警報" --alertmanager.url=http://localhost:9093
   ```
   Discord のチャンネルに届くことを見る。
2. **heartbeat が届く**: Healthchecks.io の check A と check B が「up」で、最後の ping が 5 分以内であることを見る。
3. **heartbeat が途絶えたとき、オーナーへ届く**（止め試験。米国の場の時間外に行う）:
   ```bash
   kubectl -n platform-infra scale deploy/alertmanager --replicas=0
   # 10 分以内に Healthchecks.io から「down」の知らせが、つないだ宛先（メール・Discord）へ届くことを見る
   kubectl -n platform-infra scale deploy/alertmanager --replicas=1
   # 次の ping で「up」へ戻ることを見る
   ```
   ノードごと止まった・外への通信が切れた・Prometheus が止まったときも、同じく ping が止まって知らせが届く。
4. 結果（日時・届いた宛先・かかった時間）を運用の記録に残す。

## 6. リストア試験（オブジェクトストレージの回から）

手元の PC で行う（秘密鍵はクラスタに置かない）。資格情報は rclone の env で渡す（引数に載せない）:

```bash
set -a; . ~/.secrets/backup-s3.env; set +a
export RCLONE_CONFIG_OBJSTORE_TYPE=s3 RCLONE_CONFIG_OBJSTORE_PROVIDER=Other \
  RCLONE_CONFIG_OBJSTORE_ENDPOINT=https://fsn1.your-objectstorage.com \
  RCLONE_CONFIG_OBJSTORE_ACCESS_KEY_ID="$AWS_ACCESS_KEY_ID" RCLONE_CONFIG_OBJSTORE_SECRET_ACCESS_KEY="$AWS_SECRET_ACCESS_KEY"
rclone lsf objstore:<バケット>/ast-poc/postgres/daily/          # 回の一覧
bash scripts/backup-restore-drill.sh --from-s3 objstore:<バケット>/ast-poc/postgres/daily/<回> \
  --identity <age の秘密鍵ファイル> --live-counts <稼働側の行数の TSV>
```

- `COMPLETE` の無い回・`COMPLETE` が `SHA256SUMS` と合わない回は、書きかけとして戻さない（失敗で終わる）。
- 稼働側の行数は、ノードで読み取り専用の psql を流して TSV にする（手順は [platform-infra-backup-runbook.md](platform-infra-backup-runbook.md) の「リストア試験」と同じ）。

### 回を手動でやり直すとき（中途半端な回の扱い）

失敗した回のオブジェクト（`COMPLETE` の無い `daily/<回>/`・`monthly/<回>/`）は**消さない**。Object Lock の期間の内は消せないうえ、
`COMPLETE` が無いので、リストア試験もほかの回も完了と見なさない。lifecycle が期限で片付ける。
原因を直したら `kubectl -n platform-infra create job --from=cronjob/platform-backup-postgres backup-retry-<日付>` で新しい回を起こす。
回の名前は起動の秒で決まるので、前の回とは重ならない。「同じ名前の回が既にあります」と出たら、同じ秒に 2 本起こしている。
1 本が終わってから起こし直す。`-partial` の回（一部の DB が欠けた回）は `COMPLETE` を持つが、月次へは書かれない。
原因を直してから、完全な回をもう 1 つ取る（その月の月次が未だなら、そこで月次にも書かれる）。
月次の回が途中で失敗して `COMPLETE` の無い `monthly/<回>/` が残っても、その月は「取得済み」と数えない。次の完全な回（翌日の定時の回か、手動で起こした回）が月次に書かれる。
確かめるのは、その月の回の新しい順に 2 件まで（`BACKUP_S3_MONTHLY_CHECK_MAX`）。それを超えて取りこぼしが溜まると、月次を決めずに失敗で止まる（下の分岐）。
上限を超えて溜まったときは、原因を直してから、上限を上げた 1 回だけを手で起こす（CronJob の既定は変えない）。
この 1 回は確かめの数だけ長くかかり得るので、**同じパイプでその Job の時間切れ（`activeDeadlineSeconds`）も上げる**:
```bash
kubectl -n platform-infra create job --from=cronjob/platform-backup-postgres backup-monthly-<日付> --dry-run=client -o yaml \
  | kubectl set env --local -f - BACKUP_S3_MONTHLY_CHECK_MAX=31 -o yaml \
  | kubectl patch --local -f - --type merge -p '{"spec":{"activeDeadlineSeconds":10800}}' -o yaml \
  | kubectl apply -f -
```
- K = 31 は、1 か月の定時の回の数の上限（1 日 1 回）。取りこぼしの回は 1 回に 1 つまでしか増えない。
- 時間切れの値は、定時の回と同じ式（依存先の待ち 600 ＋ 確かめ 10 ＋ 間隔 5 ＋（9 ＋ K）× rclone の 1 回の上限 240 ＋ 余裕 300）で決めた。
  K = 31 で 615 ＋ 40 × 240 ＋ 300 ＝ 10,515 秒なので、切り上げて 10,800 秒（3 時間）にする。K を変えたら、この式で値を引き直す（試験 T-2402-16 が突き合わせる）。
- 中身を確かめる回は新しい順なので、分けて何回か起こしても古い回へは届かない。1 回で上げる。

## 失敗したときの分岐

| 事象 | 見るところ | 対処 |
| --- | --- | --- |
| Healthchecks.io から「down」が届いた | `kubectl -n platform-infra get pods`（alertmanager・prometheus）、ノードへの SSH | Pod が落ちていれば `logs` で原因を見る。ノードに届かなければ Hetzner Console でノードを確かめる |
| check B が「down」（`/fail` か、ping の途絶え）になった | Alertmanager のログ（`notify` の失敗）、Discord の webhook | webhook が消された・URL が変わったなら §3 で Secret を作り直す。直れば 15 分ほどで B は「up」へ戻る |
| `AstAlertRulesNotLoaded` が Discord に出た | ConfigMap `ast-prometheus-rules` | §4 の 3 を行う |
| `OtelCollectorRefusingData` が出た | `kubectl -n platform-infra logs deploy/otel-collector`（`refused`・`memory`）、`otelcol_receiver_refused_*` を送り手（`transport`）で見る | 受け取りの量が上限（メモリ・1 回の大きさ）を超えている。どの送り手が増えたかを取引ユニットの側で確かめ、送り手を直す。拒まれた系列は Prometheus に無いので、その間の費用・取引の警報は鳴っていない |
| `OtelCollectorExportFailing` が出た | collector のログ（`prometheusremotewrite`）、`kubectl -n platform-infra get pods -l app=prometheus` | Prometheus が落ちているか詰まっている。Prometheus を立て直す（メモリの上限で作り直されていれば `PrometheusHeadSeriesHigh` も見る） |
| `OtlpMetricsPipelineSilent` か `OtlpSendersMissing` が出た | `count by (job) (last_over_time(target_info[10m]))`（届いている送り手）、取引ユニットの Pod の状態 | 届いていない送り手を探す。Pod が動いているのに届かないなら、collector の入り（`infra/networkpolicy.yaml` の送り元の一覧）にその Pod が入っているかを見る（送り手を足したら一覧と警報の数を対で直す） |
| `PrometheusHeadSeriesHigh` が出た | `topk(10, count by (__name__, job) ({__name__=~".+"}))` | 系列を増やしている送り手と名前を探し、送り手を直す。放っておくとメモリの上限で Prometheus が作り直され、保持の値が消える |
| `LlmGatewayRestartLoop` が Discord に出た（LLM ゲートウェイが直近 30 分に 3 回以上起動し直した） | `kubectl -n microservices-platform describe pod -l app=llmgateway-service`、`logs --previous` | 起動で止まる理由（設定の検査・鍵の欠落・メモリの上限）を読んで直す。当日の費用の枠は PVC に残るので、再起動で枠は 0 に戻らない |
| `LlmCostStateUnavailable` が Discord に出た（当日の費用の枠を読めない・書けないため、LLM の呼び出しを送っていない） | ゲートウェイのログ「当日の費用の枠を置き場で扱えない」（`operation`・`error`）、`kubectl -n microservices-platform get pvc llmgateway-service-state` | PVC が付いていない・容量が尽きた・ファイルが壊れた・2 つ目の Pod が同じ置き場を開いた、のどれかである。壊れたファイルは退避して残す。退避するとその日の枠は 0 から数え直すので、その日の消費は提供元のコンソールの使用量で確かめ、要れば当日の上限を一時的に下げる。直れば次の呼び出しから送る |
| バックアップの Job が失敗した | `kubectl -n platform-infra logs job/<名前>` | 「保管先が 1 つもありません」→ §2 の ConfigMap。「検算が合いません」→ 再実行（`COMPLETE` は置かれていない）。「同じ名前の回が既にあります」→ 手動の回が同じ秒に重なった。「monthly の回の確かめが上限に達しました」→ その月の `monthly/` に `COMPLETE` の無い回が溜まっている。送れない原因（鍵・接続・容量）を直し、取りこぼしの回は消さずに（lifecycle が片付ける）、完全な回を 1 つ取る。上限を超えて溜まっている間は定時の回も手動の回も同じところで止まるので、「回を手動でやり直すとき」の、上限を上げた 1 回で取る（daily は毎回書けている） |

---
title: 運用 Runbook — クラウドの基盤の外向きの中継（名前の許可表・稼働中の構成へ入れる段・宛先を足す・切り戻し）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-08
---
<!-- trace:
ids: [NFR-18, NFR-21]
iadrs: [IADR-0705, IADR-0709, IADR-0674, IADR-0675, IADR-0689]
specs: [20261107_cloud-egress-audit-cert-expiry, 20261108_egress-per-source-otlp-identity]
-->

# 運用 Runbook: クラウドの基盤の外向きの中継

> 対象はクラウドの最小構成（単一ノードの k3s・`deploy/cloud/`）の外向きの中継（`deploy/cloud/egress/`）。
> 🔴 **本書の手順はすべてオーナーが行う。** 稼働中のクラスタへの適用は、米国の場の時間外に行う。
>
> **状態を `draft` に留めている理由**: クラウドのノードで流していない。

## 何が変わるか

| 項目 | 旧 | 新 |
| --- | --- | --- |
| クラスタの外の 443 へ宛先を名指して出る Pod | LLM ゲートウェイ・バックアップ・Alertmanager（宛先は絞らない） | 外向きの中継（`egress-proxy`）だけ |
| 3 つの Pod の外への通信 | 直に外の 443 | 中継の**自分の受け口**（LLM ゲートウェイ 3131・バックアップ 3132・Alertmanager 3133）を HTTPS_PROXY として使う（TLS は端から端まで。中継は中身を読まない） |
| 宛先 | 絞らない | 中継の許可表のうち、**その送り元の行**に在る名前の 443 だけ（§6） |

OpenBao・ESO・collector の出（API サーバー）は中継を通らない。番地（`10.43.0.1:443` と `127.0.0.1:6443`）を名指す。
🔴 ESO と OpenBao には、宛先を問わない 443・6443 の別の宣言（名前の末尾 `-ports-only-stage1`）が段 1 として残る。外し方は §7。

## 1. 許可表

許可表は次の 3 つの和で、中継は最後に読んでから 30 秒を過ぎた後の最初の要求のときに読み直す（Pod を作り直さなくてよい）。
ConfigMap の変更が Pod のファイルへ届くまでに kubelet の同期の間（通常 1 分前後）がかかるので、足した宛先が効くまでの目安は 1〜2 分である。

| 表 | 置き場 | 持つ宛先 | 誰が埋めるか |
| --- | --- | --- | --- |
| 宣言 | ConfigMap `egress-proxy-hosts`（`deploy/cloud/egress/egress-proxy.yaml`） | 送り元ごとの行（`<送り元> <名前>`）。LLM の提供元（`llmgateway api.anthropic.com`）・Discord（`alertmanager discord.com` ほか）・取引ユニットの 6 つのサービスの宛先 | リポジトリ（設定に在る宛先） |
| バックアップの保管先 | ConfigMap `platform-backup-s3-target` の `endpoint`（と `dest`・`force_path_style`） | 送り元 `backup` の行。パス形式なら `endpoint` の URL の名前（例 `fsn1.your-objectstorage.com`）、仮想ホスト形式なら `<バケット>.<その名前>` だけ（§6-2） | オーナー（[バックアップの手順書](cloud-backup-alerts-heartbeat-runbook.md)の準備で作る ConfigMap） |
| オーナーが埋める表 | ConfigMap `egress-proxy-extra-hosts` の `hosts`（kustomize の外） | 設定に無い宛先。heartbeat の宛先（Healthchecks.io の ping の URL の名前） | オーナー |

### 1-1. オーナーが埋める表を作る

heartbeat の宛先の名前は、Healthchecks.io の画面の ping の URL の名前の部分（`https://` の後から最初の `/` の前まで）を読む。
URL の残り（check の識別子）は表に書かない。

```sh
kubectl -n platform-infra create configmap egress-proxy-extra-hosts \
  --from-literal=hosts="$(printf '%s\n' 'alertmanager <heartbeat の宛先の名前>' 'backup <heartbeat の宛先の名前>')" --dry-run=client -o yaml | kubectl apply -f -
```

- 1 行に 1 つ: `<送り元> <名前>`（名前は完全一致）。heartbeat の宛先へ送るのは Alertmanager（check A・B）とバックアップ（回の生存の確かめ）の 2 つなので、
  同じ名前を送り元 `alertmanager` と `backup` の 2 行に書く。片方だけだと、もう一方は送り元ごとの段が `enforce` で拒まれる。IP の字面は入れても無視される。
- 送り元を書かない古い形の行（`<名前>` だけ）は、どの送り元の行にもならない（送り元ごとの段が report の間は通し、`EgressProxySourceMismatch` が鳴る）。
  §6-1 の前に、送り元つきの形へ書き直す。
- 宛先を足すときは、その宛先を使う設定（values・受信先）と対で足す。要らなくなった宛先は外す。

## 2. 新しく作る構成

`deploy/cloud/README.md` §3 の順のまま。`kubectl apply -k deploy/cloud` の前に、1-1 の表とバックアップの保管先の ConfigMap を作る。

## 3. 稼働中の構成へ入れる（移す段）

前の構成は、3 つの Pod が直に外の 443 へ出ている。中継を入れる間は、移す段の重ね（`deploy/cloud-transition/egress-proxy-report`）で、
中継を **report**（表に無い宛先も通し、`report:` の行を出す）にし、3 つの直の 443 も残す。

1. **最初に、report の段に入る日時を書く**（警報 `EgressProxyReportMode` はこの日時から 14 日で鳴る。Prometheus や中継を作り直しても数え直さない）。
   **在れば書き直さない**（流し直しで日時を先へ延ばさない）。日時は `date -u +%Y-%m-%dT%H:%M:%SZ` の形（`Z` 付きの RFC 3339 の UTC。例の形は
   `2026-11-07T09:30:00Z`）で書く。中継が受けるのはこの形だけで、オフセット付き（`+09:00` など）・日付だけ・UNIX 秒は読まずに 0 とみなす
   （＝警報がすぐ鳴る側）:

   ```sh
   kubectl -n platform-infra get configmap egress-proxy-report-since >/dev/null 2>&1 \
     || kubectl -n platform-infra create configmap egress-proxy-report-since --from-literal=since="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
   kubectl -n platform-infra get configmap egress-proxy-report-since -o jsonpath='{.data.since}'; echo   # 読み返す。書いた日時を運用の記録に残す
   ```

2. 1-1 の表を作る。

**当てる前のチェックリスト**（全部に印が付くまで 3 へ進まない）:

- [ ] 1 で ConfigMap `egress-proxy-report-since` に日時を書いた（または前の回のものが在る）。
- [ ] `kubectl -n platform-infra get configmap egress-proxy-report-since -o jsonpath='{.data.since}'` で読み返し、`YYYY-MM-DDTHH:MM:SSZ` の形で、
      今より先の日時でないことを確かめた。
- [ ] 2 で表（`egress-proxy-extra-hosts`）を作った。
- [ ] その後に、3 の `kubectl apply -k deploy/cloud-transition/egress-proxy-report` を流す。

3. 移す段を当てる（中継が起き、3 つの Pod の出に中継の 3128 が足される。直の 443 は残る）:

   ```sh
   kubectl apply -k deploy/cloud-transition/egress-proxy-report
   kubectl -n platform-infra rollout status deploy/egress-proxy deploy/alertmanager --timeout=300s
   kubectl -n platform-infra rollout status deploy/keycloak --timeout=660s
   ```

   🔴 この重ねは `deploy/cloud` 全体を含むので、中継のほかに次も同時に起きる。米国の場の時間外に流す。
   - **Keycloak の作り直し**（宣言から一時管理者の env が消える。作り直しは Recreate）: 古い Pod が止まってから新しい Pod が Ready になるまで、
     **トークンの発行と検証の鍵の取得が止まる**。目安は通常数分（起動と realm の取り込み）、上限は startupProbe の 10 分。止まっている間、
     取引ユニットのサービス間の呼び出しとログインは失敗する。作り直しの後に、残っている一時管理者を
     [一時管理者の手順書](cloud-keycloak-admin-runbook.md) §3 で消す。
   - **証明書の期限の測り（`tls-cert-probe`）の作成**: 新しい Pod が 1 つ起き、4 つの受け口へ 10 分ごとに繋ぐ（受け口は止まらない）。
     起きるまでに 30 分を越えると警報 `TlsServerCertProbeMissing` が出る（`for: 30m`）。

4. LLM ゲートウェイを当て直す（HTTPS_PROXY が足される。`deploy/cloud/README.md` §3 の `helm upgrade`）。
5. 少なくとも 1 回のバックアップ（日次）と、Watchdog の heartbeat（5 分ごと）と、LLM の呼び出しが流れるまで待ち、report の行を読む:

   ```sh
   kubectl -n platform-infra logs deploy/egress-proxy | grep -E '^(allow|report|deny):' | sort | uniq -c
   ```

   - `allow:` に、LLM の提供元・バックアップの保管先・Discord・heartbeat の宛先が並ぶ（3 つの Pod が中継を経ている）。
   - `report:` の行は表に無い宛先。要る宛先なら 1-1 の表に足す。要らない宛先なら、呼び手を調べる（足さない）。
6. `report:` の行が出なくなったら、最終の段を当てて閉じる（中継は enforce・3 つの直の 443 は宣言から消える）:

   ```sh
   kubectl apply -k deploy/cloud
   kubectl -n microservices-platform delete networkpolicy llmgateway-egress-direct-https-transition
   kubectl -n platform-infra delete networkpolicy platform-backup-egress-direct-https-transition alertmanager-egress-direct-https-transition
   kubectl -n platform-infra delete configmap egress-proxy-report-since      # 次に移す段へ入るときに、新しい日時を書くため
   ```

   `kubectl apply` は宣言から外した資源を消さないので、移す段の 3 つは名指しで消す。日時の ConfigMap も消す（enforce の段では読まない）。
7. 確かめ: 警報 `EgressProxyDenied` が出ない・`EgressProxyReportMode` が出ない・heartbeat が届く・次のバックアップが成功する。

## 4. 運用

- 警報 `EgressProxyDenied`（直近 15 分に拒んだ）: `kubectl -n platform-infra logs deploy/egress-proxy | grep '^deny:'` で宛先を読む。
  要る宛先なら 1-1 の表に足す（効くまでの目安は 1〜2 分。§1）。
- 警報 `EgressProxyDown`: 中継が止まると、LLM の提供元・バックアップの保管先・Discord・heartbeat へ届かない（heartbeat が止まり、外の監視も知らせる）。
  `kubectl -n platform-infra describe pod -l app=egress-proxy` と `logs` で理由を読む。
- 警報 `EgressProxyReportMode`: 移す段が、入った日時（ConfigMap `egress-proxy-report-since`）から 14 日を過ぎて残っている。3 の 5・6 を行う。
  日時が無い・形が違う（`Z` 付きの RFC 3339 の UTC でない）ときも鳴る（3 の 1 で書いていない）。そのときは 3 の 1 の手順で書く（移す段に入った
  実際の日時が分かれば、それを同じ形で書く）。

- 警報 `EgressProxySourceMismatch`: 送り元が、自分の行に無い宛先（他の送り元の行に在る宛先）へ出ようとした。
  `kubectl -n platform-infra logs deploy/egress-proxy | grep 'の表に無い'` で送り元（`[送り元]`）と宛先を読む。その送り元が使う宛先なら、送り元の行として足す。

## 5. 切り戻し

- 宛先の不足で止まったときは、まず 1-1 の表に足す（切り戻すより早い）。
- 中継そのものを外すときは、前の版の `deploy/cloud` を当て、LLM ゲートウェイを前の版の values で当て直し、`egress-proxy` の Deployment・Service・
  NetworkPolicy・ConfigMap を名指しで消す。

## 6. 送り元ごとの表

中継は「どの受け口に来たか」で送り元を決める。各受け口へ届く Pod は、中継の NetworkPolicy が名前空間とラベル `app` で 1 つに絞る。

| 送り元 | 受け口 | Pod（名前空間 / `app`） |
| --- | --- | --- |
| `llmgateway` | 3131 | `microservices-platform` / `llmgateway-service` |
| `backup` | 3132 | `platform-infra` / `platform-backup` |
| `alertmanager` | 3133 | `platform-infra` / `alertmanager` |
| `ast-information-collection` | 3141 | `ai-stock-trading` / `information-collection-service` |
| `ast-market-monitor` | 3142 | `ai-stock-trading` / `market-monitor-service` |
| `ast-notification` | 3143 | `ai-stock-trading` / `notification-service` |
| `ast-report` | 3144 | `ai-stock-trading` / `report-service` |
| `ast-risk-management` | 3145 | `ai-stock-trading` / `risk-management-service` |
| `ast-trade-decision` | 3146 | `ai-stock-trading` / `trade-decision-service` |
| `ast-opend` | 3147 | `ai-stock-trading` / `opend` |

- 取引ユニットの Pod は `HTTPS_PROXY=http://egress-proxy.platform-infra.svc.cluster.local:<受け口>` を使う（クラスタの中の宛先は `NO_PROXY`）。
- 従来の口（3128）は、移す間と切り戻しの Pod のために残る。全体の表で判定し、送り元ごとの段が `enforce` なら何も通さない。
- 名前が応答で決まる宛先（Discord の Bot の再接続先）だけ、`*.discord.gg`（`discord.gg` の 1 段下だけ）の形で書く。

### 6-1. 送り元ごとの段を進める（report → enforce）

既定は `report`（他の送り元の行に在る宛先も通し、`report:` の行と警報 `EgressProxySourceMismatch` を出す）。全体の表に無い宛先は、全体の段（`EGRESS_PROXY_MODE`）で判定する。

1. この変更を当てる前に、入った日時を書く（警報 `EgressProxyReportMode` は送り元ごとの段が `report` のときも、この日時から 14 日で鳴る）:
   `kubectl -n platform-infra create configmap egress-proxy-report-since --from-literal=since="$(date -u +%Y-%m-%dT%H:%M:%SZ)" --dry-run=client -o yaml | kubectl apply -f -`
   （§3 で書いた日時が残っていれば書き直さない）。
2. 1-1 の表を送り元つきの形へ書き直す。
3. `kubectl apply -k deploy/cloud` と、LLM ゲートウェイの values の当て直し。3 つの Pod が自分の受け口を使っていることを
   `kubectl -n platform-infra logs deploy/egress-proxy | grep -E '^(allow|report): \[(llmgateway|backup|alertmanager)\]'` で確かめる。
4. 7 日続けて、`egress_proxy_source_mismatch_total` が増えないことと、**`[legacy]` の行が 1 行も出ないこと**を確かめる。
   🔴 **後者は enforce へ移る前の必須の確認である。** 従来の口（3128）は、report の間は送り元の判定を受けない（全体の表で通る）ので、
   `[legacy]` の行が出ている送り元は、まだ送り元ごとの表で確かめられていない:
   `kubectl -n platform-infra logs deploy/egress-proxy --since=168h | grep -c '\[legacy\]'` が 0 であること（Pod を作り直した後は、作り直した時刻から数え直す）。
5. `EGRESS_PROXY_SOURCE_MODE` を `enforce` にする変更をリポジトリへ入れて当てる（従来の口は何も通さなくなる）。その後、3 つの Pod の出と中継の入りから 3128 を外す。

### 6-2. バックアップの保管先を自分のバケットの名前だけにする

同じ事業者の共有の名前（`fsn1.your-objectstorage.com`）を許すと、その上の別のバケットへも出られる。仮想ホスト形式に切り替えると、表は `<バケット>.<名前>` だけになる。

1. 手元で、そのバケットが仮想ホスト形式で読めることを確かめる（`https://<バケット>.fsn1.your-objectstorage.com/` へ鍵つきで一覧を取る）。
2. `kubectl -n platform-infra patch configmap platform-backup-s3-target --type merge -p '{"data":{"force_path_style":"false"}}'`
3. 1〜2 分後（§1）に、バックアップの Job を手で流し（[バックアップの手順書](cloud-backup-alerts-heartbeat-runbook.md) §5）、成功と、中継の `allow: [backup] <バケット>.…` の行を確かめる。
4. 失敗したら `force_path_style` を消して戻す（`kubectl -n platform-infra patch configmap platform-backup-s3-target --type json -p '[{"op":"remove","path":"/data/force_path_style"}]'`）。

## 7. API サーバーへの出（ESO・OpenBao・collector）

API サーバーへ届く宛先は、k3s の起動の旗（`--advertise-address 127.0.0.1`・`--service-cidr` を渡さない）で決まる。
宣言は Pod が出す宛先（Service `kubernetes` の `10.43.0.1:443`）と、ノードで書き換わった先（`127.0.0.1:6443`）の両方を名指す。

1. 番地を確かめる（読むだけ）: `bash deploy/cloud/apiserver-egress-check.sh --live`。`OK` でなければ先へ進まない（違いを報告する）。
2. ESO の段 1 の宣言を名指しで消し、ESO の同期が続くことを確かめる:
   ```sh
   kubectl -n external-secrets delete networkpolicy eso-egress-apiserver-ports-only-stage1
   kubectl annotate externalsecret -A --all force-sync="$(date +%s)" --overwrite
   kubectl get externalsecret -A    # どれも SecretSynced のまま（1〜2 分）
   ```
   止まったら `kubectl apply -k deploy/cloud/external-secrets` で戻す。
3. OpenBao の段 1 の宣言を名指しで消し、ESO から OpenBao への認証が続くことを確かめる（2 と同じ確かめ）:
   `kubectl -n openbao delete networkpolicy openbao-egress-apiserver-ports-only-stage1`。止まったら `kubectl apply -k deploy/cloud/vault` で戻す。
4. 2・3 が通ったら、2 つの段 1 の宣言をリポジトリから外す変更を入れる（外すまでは、`kubectl apply -k` のたびに段 1 の宣言が作り直される）。
5. collector は段 1 を持たない。届かなければ送り元の印が付かず、警報 `OtlpSenderUnverified` が鳴る（系列は止まらない）。

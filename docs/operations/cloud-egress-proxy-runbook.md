---
title: 運用 Runbook — クラウドの基盤の外向きの中継（名前の許可表・稼働中の構成へ入れる段・宛先を足す・切り戻し）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-18, NFR-21]
iadrs: [IADR-0703, IADR-0674, IADR-0675, IADR-0689]
specs: [20261107_cloud-egress-audit-cert-expiry]
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
| 3 つの Pod の外への通信 | 直に外の 443 | 中継の 3128 を HTTPS_PROXY として使う（TLS は端から端まで。中継は中身を読まない） |
| 宛先 | 絞らない | 中継の名前の許可表に在る名前の 443 だけ |

OpenBao と ESO の出（API サーバー）は中継を通らない。🔴 この 2 つはポートだけで許すので、宛先を問わず 443・6443 へ出られる（宛先で絞れるかは移行の門で測る）。

## 1. 許可表

許可表は次の 3 つの和で、中継は最後に読んでから 30 秒を過ぎた後の最初の要求のときに読み直す（Pod を作り直さなくてよい）。
ConfigMap の変更が Pod のファイルへ届くまでに kubelet の同期の間（通常 1 分前後）がかかるので、足した宛先が効くまでの目安は 1〜2 分である。

| 表 | 置き場 | 持つ宛先 | 誰が埋めるか |
| --- | --- | --- | --- |
| 宣言 | ConfigMap `egress-proxy-hosts`（`deploy/cloud/egress/egress-proxy.yaml`） | LLM の提供元（`api.anthropic.com`・`api.voyageai.com`）・Discord（`discord.com`・`discordapp.com`） | リポジトリ（設定に在る宛先） |
| バックアップの保管先 | ConfigMap `platform-backup-s3-target` の `endpoint` | `endpoint` の URL の名前（例 `fsn1.your-objectstorage.com`） | オーナー（[バックアップの手順書](cloud-backup-alerts-heartbeat-runbook.md)の準備で作る ConfigMap） |
| オーナーが埋める表 | ConfigMap `egress-proxy-extra-hosts` の `hosts`（kustomize の外） | 設定に無い宛先。heartbeat の宛先（Healthchecks.io の ping の URL の名前） | オーナー |

### 1-1. オーナーが埋める表を作る

heartbeat の宛先の名前は、Healthchecks.io の画面の ping の URL の名前の部分（`https://` の後から最初の `/` の前まで）を読む。
URL の残り（check の識別子）は表に書かない。

```sh
kubectl -n platform-infra create configmap egress-proxy-extra-hosts \
  --from-literal=hosts="$(printf '%s\n' '<heartbeat の宛先の名前>')" --dry-run=client -o yaml | kubectl apply -f -
```

- 1 行に 1 つの名前（完全一致）。`*` は使えない。IP の字面は入れても無視される。
- 宛先を足すときは、その宛先を使う設定（values・受信先）と対で足す。要らなくなった宛先は外す。

## 2. 新しく作る構成

`deploy/cloud/README.md` §3 の順のまま。`kubectl apply -k deploy/cloud` の前に、1-1 の表とバックアップの保管先の ConfigMap を作る。

## 3. 稼働中の構成へ入れる（移す段）

前の構成は、3 つの Pod が直に外の 443 へ出ている。中継を入れる間は、移す段の重ね（`deploy/cloud-transition/egress-proxy-report`）で、
中継を **report**（表に無い宛先も通し、`report:` の行を出す）にし、3 つの直の 443 も残す。

1. 1-1 の表を作る。
2. 移す段を当てる（中継が起き、3 つの Pod の出に中継の 3128 が足される。直の 443 は残る）:

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

3. LLM ゲートウェイを当て直す（HTTPS_PROXY が足される。`deploy/cloud/README.md` §3 の `helm upgrade`）。
4. 少なくとも 1 回のバックアップ（日次）と、Watchdog の heartbeat（5 分ごと）と、LLM の呼び出しが流れるまで待ち、report の行を読む:

   ```sh
   kubectl -n platform-infra logs deploy/egress-proxy | grep -E '^(allow|report|deny):' | sort | uniq -c
   ```

   - `allow:` に、LLM の提供元・バックアップの保管先・Discord・heartbeat の宛先が並ぶ（3 つの Pod が中継を経ている）。
   - `report:` の行は表に無い宛先。要る宛先なら 1-1 の表に足す。要らない宛先なら、呼び手を調べる（足さない）。
5. `report:` の行が出なくなったら、最終の段を当てて閉じる（中継は enforce・3 つの直の 443 は宣言から消える）:

   ```sh
   kubectl apply -k deploy/cloud
   kubectl -n microservices-platform delete networkpolicy llmgateway-egress-direct-https-transition
   kubectl -n platform-infra delete networkpolicy platform-backup-egress-direct-https-transition alertmanager-egress-direct-https-transition
   ```

   `kubectl apply` は宣言から外した資源を消さないので、移す段の 3 つは名指しで消す。
6. 確かめ: 警報 `EgressProxyDenied` が出ない・`EgressProxyReportMode` が出ない・heartbeat が届く・次のバックアップが成功する。

## 4. 運用

- 警報 `EgressProxyDenied`（直近 15 分に拒んだ）: `kubectl -n platform-infra logs deploy/egress-proxy | grep '^deny:'` で宛先を読む。
  要る宛先なら 1-1 の表に足す（効くまでの目安は 1〜2 分。§1）。
- 警報 `EgressProxyDown`: 中継が止まると、LLM の提供元・バックアップの保管先・Discord・heartbeat へ届かない（heartbeat が止まり、外の監視も知らせる）。
  `kubectl -n platform-infra describe pod -l app=egress-proxy` と `logs` で理由を読む。
- 警報 `EgressProxyReportMode`: 移す段が 14 日を過ぎて残っている。3 の 4・5 を行う。

## 5. 切り戻し

- 宛先の不足で止まったときは、まず 1-1 の表に足す（切り戻すより早い）。
- 中継そのものを外すときは、前の版の `deploy/cloud` を当て、LLM ゲートウェイを前の版の values で当て直し、`egress-proxy` の Deployment・Service・
  NetworkPolicy・ConfigMap を名指しで消す。

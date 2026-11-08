---
title: 運用 Runbook — クラウドの k3s の API サーバーの監査の記録（稼働中のノードへ足す・確かめる・読む）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-08
---
<!-- trace:
ids: [NFR-18]
iadrs: [IADR-0710, IADR-0705, IADR-0674]
specs: [20261108_backup-alerts-audit-offnode-approle-openapi, 20261107_cloud-egress-audit-cert-expiry]
-->

# 運用 Runbook: k3s の API サーバーの監査の記録

> 対象はクラウドの単一ノードの k3s。新しく作るノードは `deploy/cloud/README.md` §1-4 の手順が、導入の前に監査の方針と設定の追加を置く。
> 本書は**稼働中のノードへ足す**手順である。
> 🔴 **本書の手順はすべてオーナーが行う**（ノードへの SSH は管理する端末から）。k3s を作り直すので、米国の場の時間外に行う。
>
> **状態を `draft` に留めている理由**: クラウドのノードで流していない。

## 何を足すか

| ファイル（リポジトリ） | ノードの置き場 | 中身 |
| --- | --- | --- |
| `deploy/cloud/k3s/audit-policy.yaml` | `/var/lib/rancher/k3s/server/audit-policy.yaml`（暗号化した領域） | 監査の方針。Metadata まで（要求・応答の本文を残さない）。Secret・トークン・exec・RBAC の操作は必ず残す |
| `deploy/cloud/k3s/config.yaml.d/20-apiserver-audit.yaml` | `/etc/rancher/k3s/config.yaml.d/20-apiserver-audit.yaml` | API サーバーの引数（方針・記録の置き場 `/var/lib/rancher/k3s/server/logs/audit.log`・保持 30 日・10 本・100MB） |

## 1. 稼働中のノードへ足す

手元（リポジトリの根）から運ぶ:

```sh
scp deploy/cloud/k3s/audit-policy.yaml deploy/cloud/k3s/config.yaml.d/20-apiserver-audit.yaml <ノード>:/root/
```

ノードで:

```sh
mountpoint /var/lib/rancher                                          # 暗号化した領域がマウントされていること
install -d -m 0700 /var/lib/rancher/k3s/server/logs
install -m 0600 -o root -g root /root/audit-policy.yaml /var/lib/rancher/k3s/server/audit-policy.yaml
install -d -m 0755 /etc/rancher/k3s/config.yaml.d
install -m 0600 -o root -g root /root/20-apiserver-audit.yaml /etc/rancher/k3s/config.yaml.d/20-apiserver-audit.yaml
systemctl restart k3s
systemctl is-active k3s                                              # active
kubectl -n platform-infra get pods                                   # すべてが Ready に戻るまで見る
test -s /var/lib/rancher/k3s/server/logs/audit.log && echo audit-ok
```

- 方針のファイルが読めないと API サーバーは起動しない（k3s が `active` にならない）。そのときは `journalctl -u k3s -n 100` で理由を読み、
  `/etc/rancher/k3s/config.yaml.d/20-apiserver-audit.yaml` を外して `systemctl restart k3s` すれば前の形に戻る。

## 2. 読む

記録そのものはノードの上にだけ在る。値（Secret の中身）は載らない。記録の**目録**（ファイルごとの大きさと sha256）は §4 で日次に外へ送る。

```sh
# 誰が Secret を読んだか（直近）
grep '"resource":"secrets"' /var/lib/rancher/k3s/server/logs/audit.log | tail -n 20 | cut -c1-400
# exec・port-forward
grep -E '"subresource":"(exec|portforward|attach)"' /var/lib/rancher/k3s/server/logs/audit.log | tail -n 20 | cut -c1-400
```

## 3. 切り戻し

`/etc/rancher/k3s/config.yaml.d/20-apiserver-audit.yaml` を消して `systemctl restart k3s`。記録のファイルは残る（要らなければ消す）。

## 4. 記録の目録をノードの外へ送る（日次）・外の写しと突き合わせる

API サーバーの監査と OpenBao の監査（Pod のログ）は、ノードの上にしか無い。ノードの root を取った者は書き換え・消去ができるので、
**目録（ファイルごとの大きさと、先頭から大きさまでの sha256）を日次で外の check へ送り**、外に残った写しと後から突き合わせる。
送るのは名前・大きさ・sha256 だけで、記録の中身は送らない。目録は前の回の目録の sha256 を持つ（鎖）。

1. Healthchecks.io に **check D（監査の目録）** を作る。**Period: 1 日、Grace: 6 時間**。Integrations は check A〜C と同じ宛先（2 つ以上）。
   ping の本文が check の履歴に残る（目録の外の写しになる）。
2. 手元から運ぶ（リポジトリの根）:
   ```sh
   scp deploy/cloud/node/audit-offsite.sh deploy/cloud/node/msp-audit-offsite.service deploy/cloud/node/msp-audit-offsite.timer <ノード>:/root/
   ```
3. ノードで置く（URL は対話で入れ、引数・履歴に残さない）:
   ```sh
   install -d -m 0755 /usr/local/lib/msp && install -m 0755 -o root -g root /root/audit-offsite.sh /usr/local/lib/msp/audit-offsite.sh
   install -d -m 0700 /etc/msp-audit-offsite
   ( umask 077; read -rs u; printf '%s' "$u" > /etc/msp-audit-offsite/ping-url; unset u )   # check D の ping の URL
   install -m 0644 /root/msp-audit-offsite.service /root/msp-audit-offsite.timer /etc/systemd/system/
   systemctl daemon-reload && systemctl enable --now msp-audit-offsite.timer
   systemctl start msp-audit-offsite.service && journalctl -u msp-audit-offsite -n 5   # 「OK: 監査の記録の目録（N 件）を外の check へ送った」
   ```
   check D が「up」になり、最後の ping の本文に `msp-audit-manifest v1` で始まる目録が在ることを見る。
4. 突き合わせ（侵害を疑ったとき・月に 1 度）: Healthchecks.io の check D の履歴から、過去の回の本文を手元へ写し、ノードへ運んで確かめる:
   ```sh
   bash /usr/local/lib/msp/audit-offsite.sh verify /root/manifest-<日付>.txt   # 全部の行が ok なら 0
   ```
   `NG` の行は、その時点の記録の中身がどのファイルの先頭にも無い（書き換えられたか消された）。保持（30 日・10 世代。Pod のログは kubelet の世代交代）を
   過ぎた記録なら `NG` が正しい。保持の内側の `NG` は侵害を疑い、ノードの外の写し（check D の履歴）を残したまま調べる。
   外の写しの鎖（各回の `prev` が 1 つ前の回の本文の sha256）が切れていれば、その間の回が送られていないか、送り先が違う。

- check D が「down」なら、timer が止まった・ノードが止まった・送れない（URL・外への通信）のどれかである。`systemctl status msp-audit-offsite.timer`・
  `journalctl -u msp-audit-offsite` を見る。暗号化した領域がマウントされていない間は、service は流れない（条件で止まる）。
- 外す: `systemctl disable --now msp-audit-offsite.timer` と、置いた 3 つのファイル・`/etc/msp-audit-offsite`・`/var/lib/msp-audit-offsite` を消す。

---
title: 運用 Runbook — クラウドの k3s の API サーバーの監査の記録（稼働中のノードへ足す・確かめる・読む）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-18]
iadrs: [IADR-0704, IADR-0674]
specs: [20261107_cloud-egress-audit-cert-expiry]
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

記録はノードの上にだけ在る（外へ送らない）。値（Secret の中身）は載らない。

```sh
# 誰が Secret を読んだか（直近）
grep '"resource":"secrets"' /var/lib/rancher/k3s/server/logs/audit.log | tail -n 20 | cut -c1-400
# exec・port-forward
grep -E '"subresource":"(exec|portforward|attach)"' /var/lib/rancher/k3s/server/logs/audit.log | tail -n 20 | cut -c1-400
```

## 3. 切り戻し

`/etc/rancher/k3s/config.yaml.d/20-apiserver-audit.yaml` を消して `systemctl restart k3s`。記録のファイルは残る（要らなければ消す）。

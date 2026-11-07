---
title: 運用 Runbook — クラウドのノードの防御（暗号化した領域へ寄せる・ノードの中の 2 枚目の防御・SSH）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-18, NFR-05]
iadrs: [IADR-0704, IADR-0674]
specs: [20261107_backup-owner-ops-hardening]
-->

# 運用 Runbook: クラウドのノードの防御

> **運用仕様書（[`operations.md`](operations.md)）の「セキュリティ」の下位にあたる手順書である。**
> 対象はクラウドの最小構成（単一ノードの k3s）のノードそのもの。新しくノードを作るときの順序は `deploy/cloud/README.md` §1 にあり、
> ここは**既に動いているノードへ足す手順**と、足したことの確かめを持つ。
>
> **状態を `draft` に留めている理由**: クラウドのノードで §1〜§4 をまだ記録していない。

## 何を硬くするか

| 項目 | 前 | 後 |
| --- | --- | --- |
| 暗号化した領域（LUKS）に置くもの | k3s のデータ（`/var/lib/rancher`）だけ | 加えて、管理者の kubeconfig（`/etc/rancher`）と Pod のログ（`/var/log/pods`）。どれかがマウントされていなければ k3s は起動しない |
| ノードの入りの防御 | 提供元のファイアウォールの 1 枚 | 加えて、ノードの中の nftables（表 `inet node_guard`）。22 と、ノードの中・Pod からの通信のほかを落とす |
| SSH | root の鍵ログインを許す・送り元の絞りは任意 | root では入らない（管理の利用者と sudo）・送り元を固定 IP に絞る（必須） |

- 規則と確かめのスクリプトは `deploy/cloud/node/`（`node-guard.nft`・`node-guard-enforce.nft`・`check-node.sh`）。手元からノードへ `scp` で運ぶ。
- どの段も、**ノードの上で人が行う**（スクリプトはノードを変えない。`check-node.sh` は読むだけ）。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | ノードへの SSH（root か、sudo を持つ管理の利用者）、Hetzner Console（ファイアウォールとコンソールの画面） |
| 必要なツール | ノードの `nftables`（`apt-get install -y nftables`）、`cryptsetup`、手元の `scp` |
| 時間帯 | §1 は k3s を止める。米国の場の時間外（JST 07:00〜21:00）に行う。§2・§3 は止めないが、締め出しの備え（下）を必ず置く |
| 締め出しの備え | Hetzner Console の画面（コンソール）から root で入れることを先に確かめる（SSH を誤って閉じたときの戻り道） |
| 所要時間の目安 | §1 は 30 分（止める時間を含む）、§2 は 2 日（報告の段で数える期間）＋ 15 分、§3 は 15 分 |

## 1. 管理者の kubeconfig と Pod のログを暗号化した領域へ寄せる（既に動いているノード）

1. k3s を止める（Pod も止まる）: `systemctl stop k3s && /usr/local/bin/k3s-killall.sh`。
2. 暗号化した領域へ写し、bind mount する:
   ```sh
   mountpoint /var/lib/rancher                                   # 暗号化した領域がマウントされていること
   mkdir -p /var/lib/rancher/etc-rancher /var/lib/rancher/pod-logs
   cp -a /etc/rancher/. /var/lib/rancher/etc-rancher/
   cp -a /var/log/pods/. /var/lib/rancher/pod-logs/
   mount --bind /var/lib/rancher/etc-rancher /etc/rancher
   mount --bind /var/lib/rancher/pod-logs /var/log/pods
   findmnt -T /etc/rancher; findmnt -T /var/log/pods             # どちらも SOURCE が /dev/mapper/rancher[…] であること
   ```
3. 下に隠れたルートのディスクの上の写しを消す（bind mount を一度外して消し、付け直す）:
   ```sh
   umount /etc/rancher /var/log/pods
   find /etc/rancher /var/log/pods -mindepth 1 -delete
   mount --bind /var/lib/rancher/etc-rancher /etc/rancher
   mount --bind /var/lib/rancher/pod-logs /var/log/pods
   ```
   消してもディスクの上の領域は上書きされるまで残り得る。**管理者の資格は平文で置かれていた前提で作り直す**（次の 4）。
4. k3s の drop-in（`/etc/systemd/system/k3s.service.d/10-require-encrypted-data.conf`）を `deploy/cloud/README.md` §1-4 の形にする
   （3 つのマウントを起動の条件にする）。`systemctl daemon-reload`。
5. 管理者の資格を作り直してから起こす:
   ```sh
   k3s certificate rotate                                        # 止めたまま流す（クライアントの証明書を作り直す）
   systemctl start k3s && systemctl is-active k3s
   kubectl -n platform-infra get pods                            # すべてが Ready に戻るまで見る
   ```
   手元の kubeconfig を、新しい `/etc/rancher/k3s/k3s.yaml` から作り直す（`scp` で運び、`server:` を `https://127.0.0.1:6443` にする）。
   古い kubeconfig で入れないこと（`kubectl get nodes` が拒まれること）を確かめる。
6. 再起動の後の手順は `deploy/cloud/README.md` §1-5（2 つの bind mount を足した形）になる。

## 2. ノードの中の 2 枚目の防御（nftables。段 1 で報告、段 2 で落とす）

🔴 規則は自分の表（`inet node_guard`）だけを作り直す。**`flush ruleset` を使わない**（k3s・flannel・kube-router の表が消え、Pod の通信が止まる）。
Debian・Ubuntu の既定の `/etc/nftables.conf` は先頭に `flush ruleset` を持つので、**中身を置き換えてから** nftables.service を有効にする。

1. 規則を運ぶ:
   ```sh
   scp deploy/cloud/node/node-guard.nft deploy/cloud/node/node-guard-enforce.nft deploy/cloud/node/check-node.sh <ノード>:/root/
   ssh <ノード> 'install -d -m 0755 /etc/nftables.d && install -m 0644 /root/node-guard.nft /root/node-guard-enforce.nft /etc/nftables.d/'
   ```
2. **段 1（報告）**: 当てて数える。落とさない:
   ```sh
   nft -c -f /etc/nftables.d/node-guard.nft && nft -f /etc/nftables.d/node-guard.nft
   printf '#!/usr/sbin/nft -f\ninclude "/etc/nftables.d/node-guard.nft"\n' > /etc/nftables.conf
   systemctl enable nftables
   ```
   2 日ほど置き、落とすはずだった通信を見る:
   ```sh
   nft list chain inet node_guard guard_tail                    # counter の数
   journalctl -k --since -2d | grep 'node-guard would-drop' | awk '{for(i=1;i<=NF;i++) if($i ~ /^(IN|SRC|PROTO|DPT)=/) printf "%s ", $i; print ""}' | sort | uniq -c | sort -rn | head
   ```
   出てよいのは外からの走査（送り元が管理の端末でない）だけである。ノードの中・Pod からの通信（`IN=cni0`・`IN=flannel.1`）や、
   提供元の DHCP・監視が並ぶなら、落とす前に規則へ足す（足す変更はリポジトリの `node-guard.nft` へ入れ、試験を通してから運び直す）。
3. **段 2（落とす）**: 締め出しの備えを置いてから替える:
   ```sh
   systemd-run --on-active=10min --unit=node-guard-rollback nft -f /etc/nftables.d/node-guard.nft   # 10 分の後に報告へ戻す（備え）
   nft -f /etc/nftables.d/node-guard-enforce.nft
   ```
   **開いている SSH の接続を閉じずに**、別の端末から SSH で入れることを確かめる。入れたら備えを外して、再起動の後も落とす段にする:
   ```sh
   systemctl stop node-guard-rollback.timer
   printf '#!/usr/sbin/nft -f\ninclude "/etc/nftables.d/node-guard.nft"\ninclude "/etc/nftables.d/node-guard-enforce.nft"\n' > /etc/nftables.conf
   kubectl -n platform-infra get pods                            # Pod が Ready のままであること
   ```
   入れなければ、10 分待つと報告の段へ戻る（それでも入れなければ Hetzner Console の画面から `nft delete table inet node_guard`）。
4. 戻すとき: 報告へ戻すのは `nft -f /etc/nftables.d/node-guard.nft`、外すのは `nft delete table inet node_guard`（`/etc/nftables.conf` の include も外す）。

## 3. SSH（root で入らない・送り元を絞る）

1. 管理の利用者を作り、鍵と sudo を確かめる（root の接続は開いたままにする）:
   ```sh
   adduser --disabled-password --gecos '' <管理の利用者> && usermod -aG sudo <管理の利用者>
   install -d -m 0700 -o <管理の利用者> -g <管理の利用者> /home/<管理の利用者>/.ssh
   install -m 0600 -o <管理の利用者> -g <管理の利用者> /root/.ssh/authorized_keys /home/<管理の利用者>/.ssh/authorized_keys
   passwd <管理の利用者>                                          # sudo のためのパスワード（対話で入れる。SSH はパスワードを受けない）
   ```
   別の端末から `ssh <管理の利用者>@<ノード>` で入り、`sudo -v` が通ることを見る。
2. `/etc/ssh/sshd_config.d/10-hardening.conf` を `deploy/cloud/README.md` §1-1 の形にし（`PermitRootLogin no`・`AllowUsers <管理の利用者>`）、
   `sshd -t && systemctl reload ssh`。開いた接続を閉じずに、別の端末から管理の利用者で入れること・root で入れないことを見る。
3. Hetzner Cloud Firewall の SSH の規則の送り元を、管理する端末の固定 IP（`/32`）に絞る。絞った後に、もう一度入れることを見る。
4. 手元の手順（`ssh <ノード>`・トンネル・`scp`）は管理の利用者で行い、ノードの上の操作は `sudo` を付ける。

## 4. 確かめる（足した後・再起動の後・月に 1 度）

```sh
sudo bash /root/check-node.sh                    # 段 2 の後（「ノードの防御は手順書どおり」と出ること）
sudo bash /root/check-node.sh --expect report    # 段 1 で数えている間
```

暗号化した 3 つの領域（マウント点で、元が dm-crypt）・k3s の drop-in の条件・管理者の kubeconfig の権限・2 枚目の防御の段と再起動の後の読み込み・
SSH の実効値（root・パスワード・キーボード対話を受けない）を見る。ループバックの外で受けている口（kubelet の 10250 など）は参考に名指す
（2 枚目の防御が落とす。提供元のファイアウォールでも閉じていること）。結果は運用の記録に残す。

## 失敗したときの分岐

| 事象 | 見るところ | 対処 |
| --- | --- | --- |
| 段 2 の後に SSH で入れない | 開いたままの接続、Hetzner Console の画面 | 10 分待つ（備えが報告へ戻す）。それでも入れなければ画面から `nft delete table inet node_guard` |
| 段 2 の後に Pod の通信が止まった | `kubectl -n platform-infra get pods`・`journalctl -k \| grep 'node-guard drop'` | `nft -f /etc/nftables.d/node-guard.nft` で報告へ戻し、落ちた通信（`IN=`・`DPT=`）を規則へ足してから段 2 をやり直す |
| 再起動の後に k3s が起動しない | `systemctl status k3s`（条件が満たされない） | `deploy/cloud/README.md` §1-5 の 2 つの bind mount を忘れている。マウントしてから `systemctl start k3s` |
| `check-node.sh` が `flush ruleset` を名指した | `/etc/nftables.conf` | §2 の 2 の形（include だけ）へ置き換える。nftables.service を再起動する前に直す |

---
title: 運用 Runbook — オーナーの手元の手順が宛先のクラスタを確かめる（クラスタの印の記録）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-18, NFR-05]
iadrs: [IADR-0703, IADR-0689, IADR-0674, IADR-0705]
specs: [20261107_backup-owner-ops-hardening, 20261107_cloud-egress-audit-cert-expiry]
-->

# 運用 Runbook: 宛先のクラスタの確かめ（クラスタの印）

> **運用仕様書（[`operations.md`](operations.md)）の「秘匿管理」の下位にあたる手順書である。**
> 対象は、オーナーが手元から流すクラウドの最小構成の手順（`deploy/cloud/vault/*.sh`・`deploy/cloud/keycloak-clients/keycloak-clients.sh`・
> `deploy/cloud/keycloak-admin/keycloak-admin.sh`・`deploy/cloud/db-users/ast-db-users.sh`）。秘匿管理そのものの手順は[秘匿管理の手順書](cloud-openbao-init-unseal-runbook.md)。
>
> **状態を `draft` に留めている理由**: クラウドのノードで印の記録と照らし合わせをまだ記録していない。

## 何が起きるか

手順は、kubectl を使う前に次の 3 つを確かめる。どれかが違えば**何もせずに終了コード 4 で終わる**。

| 確かめること | 合う形 |
| --- | --- |
| kubectl の現在の context が在る | — |
| その context の API の宛先 | クラウドのノードへの SSH のトンネル（既定 `https://127.0.0.1:6443`。環境変数 `CLOUD_API_SERVER` で変える） |
| その宛先のクラスタの印（名前空間 `kube-system` の UID） | 手元に記録した印（既定 `~/.config/microservices-platform/cloud-cluster-mark`。環境変数 `CLOUD_CLUSTER_MARK_FILE` で変える） |

- 確かめた後の kubectl は、確かめた context に固定される。流している途中で別の端末が現在の context を替えても、別のクラスタへは流れない。
- 試験用のクラスタ（k3d など）を同じ `127.0.0.1:6443` へ向けていても、印が違うので止まる。
- 印は秘密ではない（クラスタの識別子）。手順は印のほかに何も記録しない。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | クラウドのノードへの SSH（管理の利用者と sudo） |
| 必要なツール | `kubectl`（SSH のトンネル越し） |
| 所要時間の目安 | 5 分 |

## 1. 印を記録する（一度だけ。クラスタを作り直したときも）

1. ノードへのトンネルを張る（`ssh -N -L 6443:127.0.0.1:6443 <ノード>`）。`<ノード>` は Hetzner Console に表示されたノードの IP と、SSH のホスト鍵の指紋で確かめる。
2. 手元で記録する:
   ```sh
   bash deploy/cloud/cluster-mark.sh --live record
   ```
   API の宛先がトンネルでなければ書かずに 4 で終わる。記録が在れば上書きしない（2 で終わる）。
3. **ノードの上で照らす**（トンネルの先が本当にそのノードであることの確かめ）:
   ```sh
   ssh <ノード> 'sudo k3s kubectl get namespace kube-system -o jsonpath={.metadata.uid}'; echo
   cat ~/.config/microservices-platform/cloud-cluster-mark
   ```
   2 つが同じであること。違えば記録を消し、トンネルの張り先を確かめてから 2 をやり直す。
4. 記録のファイルは、秘匿管理のファイル（`<init>.age` など）と一緒に保管する（手元の端末を替えたときに写す）。

## 2. 流す前に確かめる（いつでも）

```sh
bash deploy/cloud/cluster-mark.sh --live check     # 「記録したクラウドのクラスタを向いている」と出ること
```

各手順は流すたびに同じ確かめをするので、これは任意である（`kubectl apply -k` のように手で流すコマンドの前に使う）。

## 失敗したときの分岐

| 事象 | 見るところ | 対処 |
| --- | --- | --- |
| 「API の宛先が、クラウドのノードへのトンネルでない」 | `kubectl config current-context`・`kubectl config view --minify` | 正しい context へ切り替える。トンネルの手元の口を変えているなら `CLOUD_API_SERVER` を与える |
| 「クラスタの印を読めない」 | トンネル（`ssh -N -L …`）が張られているか | トンネルを張り直す |
| 「記録したクラスタの印が無い」 | 記録のファイル | §1 を行う（手元の端末を替えたなら、保管した記録を写す） |
| 「記録したクラウドのクラスタでない」 | 現在の context の向き先 | **流さない。** 別のクラスタ（試験用など）を向いている。正しい context へ切り替える。クラウドのクラスタを作り直した後なら、古い記録を退避してから §1 をやり直す |

---
title: IADR-0704 クラウドのバックアップの鍵を管理と書き込みの 2 組に分け、保持の設定を外から確かめる。手元の S3 の鍵は age のファイルから 1 つのコマンドの環境にだけ渡す。オーナーの手順は宛先のクラスタ（トンネルと kube-system の UID）を確かめて context を固定し、平文の age の識別子を warn → enforce で扱う。ノードは kubeconfig と Pod のログも LUKS の領域へ寄せ、nftables の 2 枚目の防御（報告 → 落とす）と SSH の root の禁止を置く
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-05, NFR-21, IADR-0675, IADR-0682, IADR-0689, IADR-0674]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_backup-owner-ops-hardening.md
---

# IADR-0704: バックアップとオーナーの運用の硬化（クラウドの最小構成）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「全部直してから稼働」・unseal の鍵の保持はオーナー）・claude（方式）

## 起点・関連

- 要求 **NFR-18**（統制）・**NFR-05**（秘匿）・**NFR-21**（バックアップ）。
- 前提: IADR-0675（オフサイトのバックアップ・Object Lock COMPLIANCE 28 日）・IADR-0682（手元に平文と秘密を残さない形）・IADR-0689（OpenBao とオーナーの手順）・IADR-0674（クラウドの最小構成のノード）。
- 作業仕様書: `.ai-context/specs/20261107_backup-owner-ops-hardening.md`。

## コンテキストと課題

- バックアップの S3 の鍵は 1 組で、バケットの設定とクラスタの Job の両方に使っていた。保持の設定が宣言どおりかを、クラスタの外から確かめる手段が無かった。
- 手元のリストア試験とバケットの設定の手順は、S3 の鍵を平文のファイルに置き、シェルの環境へ読み込ませていた（IADR-0682 の形の追随漏れ）。
- オーナーの手順（`deploy/cloud/` の 9 本）は kubectl の現在の context をそのまま使い、宛先がクラウドのクラスタかを確かめなかった。
  unseal の鍵とオーナー用の資格は同じ age の識別子で開くが、識別子そのものを守る前提が手順書に無かった。
- ノードは k3s のデータだけを LUKS の領域に置き、管理者の kubeconfig と Pod のログはルートのディスクにあった。ノードの入りの防御は提供元のファイアウォールの 1 枚で、
  SSH の送り元の絞りは任意、root の鍵ログインを許していた。

## 決定

### 決定 1 — バックアップの鍵を 2 組に分け、保持の設定を外から確かめる。手元の鍵は age のファイルから 1 つのコマンドにだけ渡す

- 鍵は「管理」（バケットの設定・確かめ。手元だけ）と「書き込み」（クラスタの Job とリストア試験。OpenBao へ）の 2 組にする。
- 書き込みの鍵は、提供元がバケットのポリシーで絞れるなら、保持・版の管理・ポリシーを変える操作を拒む。絞れたかは
  `deploy/cloud/backup/check-bucket.sh probe-writer` が、宣言の JSON を書き込もうとして確かめる（拒まれれば合格）。書けてしまう鍵では
  その時の設定が宣言で上書きされ、ずれがあれば跡が消えるので、`probe-writer` は書く前に verify と同じ突き合わせを行い、ずれていれば（読めなければ）書かずに止まる。
  手順書も verify を先に流す順にする。
- 保持の設定（版の管理・Object Lock の既定の保持・lifecycle）は、`check-bucket.sh verify` が宣言の JSON と突き合わせる（読むだけ）。オーナーが週に 1 度と、警報・障害・鍵の作り直しの後に流す。
- 手元の鍵は `deploy/cloud/backup/s3-creds.sh` が扱う: `store` は端末から伏せて読んで age で暗号化したファイルだけを書き、`run` は復号した値をスクリプトの変数へ読み、
  `exec` するコマンドの環境（AWS_*・rclone の objstore）にだけ載せる。引数・平文のファイル・呼んだシェルには残らない。
- `AWS_ENDPOINT_URL` を読まない古い aws CLI（v2 は 2.13.0 より前・v1 は 1.29.0 より前。aws-cli の CHANGELOG で確かめた）は、既定の AWS の宛先へ鍵で署名した要求を送る。
  `run` は PATH の aws の版を確かめ、古い・判定できなければ鍵を渡さずに止まる。`AWS_ENDPOINT_URL_S3` も同じ宛先にし、`AWS_IGNORE_CONFIGURED_ENDPOINT_URLS=false` を置く。
  `check-bucket.sh` は宛先を `--endpoint-url` で明示する。
- 手順書の検査（`scripts/runbook-secret-handling.test.js`）に 2 つの規則を足す: `set -a` の下の env ファイルの読み込みと、手元のツールの S3 の鍵の `export`（変数を渡す形でも）。
  行末の `\` で続く行はつないで読む。

### 決定 2 — オーナーの手順は宛先のクラスタを確かめ、確かめた context に固定する

- `deploy/cloud/lib/target-cluster.sh` の `ct_require_target` を、9 本の手順が最初の kubectl より前に呼ぶ。確かめるのは、現在の context が在ること・
  その API の宛先がノードへの SSH のトンネル（既定 `https://127.0.0.1:6443`）であること・クラスタの印（名前空間 `kube-system` の UID）が手元の記録と同じであること。
  どれかが違えば kubectl を使わずに終了コード 4 で終わる。
- 印の記録は `deploy/cloud/cluster-mark.sh record`（トンネルの宛先のときだけ書く・上書きしない）。記録の後に、ノードの上で同じ UID であることをオーナーが照らす。
- 確かめた後は、手順の中の kubectl をシェル関数で確かめた context に固定する（`--context`）。
- 印は秘密ではないので、手元の設定のファイル（0600）に置く。

### 決定 3 — 平文の age の識別子は warn → enforce の 2 段で扱う（保持はオーナーのまま）

- unseal の鍵の保持は「オーナーが持つ」（裁定）を変えない。2 つのファイルを別の識別子に分けることはしない（root の作り直しが両方を要るので、秘匿の分離にならない）。
- 識別子そのものを、パスフレーズで包んだファイル（`age -p`）か鍵の装置（age のプラグイン）で持つことを手順書の前提にする。
- `vault/lib.sh` の `ob_identity_guard` は、平文の識別子（`AGE-SECRET-KEY-1` の行を持つファイル）を、既定（`OB_IDENTITY_POLICY=warn`）で警告して進め、
  `enforce` で止める。既定を warn にしたのは、識別子を包む前の再起動の後の unseal を止めないため。包んだ後にオーナーが enforce へ移る。

### 決定 4 — ノード: kubeconfig と Pod のログも LUKS の領域へ。nftables の 2 枚目の防御（報告 → 落とす）。SSH は root の禁止と送り元の絞り

- `/etc/rancher` と `/var/log/pods` を LUKS の領域の中のディレクトリへ bind mount し、k3s の drop-in の起動の条件に 3 つのマウントを並べる。
  既に動いているノードは止めて写し、平文で置かれていた管理者の資格を、クラスタの CA ごと作り直して無効にする（`k3s certificate rotate-ca --force`。
  古い CA と関係のない CA を `generate-custom-ca-certs.sh` で作る）。`k3s certificate rotate` は葉の証明書を作り直すだけで CA を変えず、
  Kubernetes は CRL を使わないので、古い client 証明書が期限まで通る。k3s の公式の文書（https://docs.k3s.io/cli/certificate 「Certificate Authority (CA) Certificates」）は、
  漏れた管理者の kubeconfig を失効させるには CA を丸ごと置き換えるしかないとする。既定の `rotate-default-ca-certs.sh` は古い root を CA の束に残すので使わない
  （k3s の `contrib/util/rotate-default-ca-certs.sh`。main `dca306c81c44` で確かめた）。`rotate-ca` は動いている k3s へ送る（`pkg/cli/cert/cert.go` が `/v1-k3s/cert/cacerts` へ PUT する）。
  拒まれることは、新しい server CA でサーバを確かめて古い client 証明書だけを出し、`/api` が 200 から 401 に替わることで確かめる。
- ノードの中の防御は nftables の自分の表（`inet node_guard`）だけを作り直す（`flush ruleset` を使わない）。入りだけを絞り、受けるのはループバック・確立済みの戻り・ICMP・
  22・DHCP の応答・Pod からの口（cni0・flannel.1）。既定の規則は「報告」（数えて記録する）で、`node-guard-enforce.nft` で「落とす」へ替える。替えるときは 10 分の後に報告へ戻す備えを置く。
- SSH は `PermitRootLogin no`・`AllowUsers`・送り元の固定 IP への絞りを必須にする。
- ノードの確かめ `deploy/cloud/node/check-node.sh` は読むだけ。オーナーがノードの上で流す。

## 却下した案

- **`/etc/rancher` を k3s の `--data-dir` の下へ移す**: k3s の kubeconfig の既定の置き場を変えるとインストーラと手順の前提が広く変わる。bind mount は既存の配置のまま領域だけを替えられる。
- **ノードの防御を `flush ruleset` を持つ既定の nftables.conf に書く**: nftables.service の再起動で k3s の表が消え、Pod の通信が止まる。
- **平文の識別子を最初から拒む**: 包む前の再起動の後に unseal を止めてしまう（稼働を止める変更は段を踏む）。

## 残余リスク

- 提供元が鍵ごとにバケットの操作を絞れないときは、書き込みの鍵で 28 日より古い回の保持を縮められ得る。直近 28 日の回は Object Lock が守る。週に 1 度の verify で変化に気付き、管理の鍵で戻す。
- bind mount の前にルートのディスクへ書かれた kubeconfig とログは、消しても領域が上書きされるまで残り得る。管理者の client 証明書は CA の入れ替えで無効にするが、ログの中身（記録された値）は無効にできない。CA の入れ替えは API とすべての Pod を 10 分ほど止め、OpenBao の unseal を要する。サービスアカウントのトークンは署名の鍵を残すので無効にならない（トークンが漏れていた場合は別に鍵を替える）。Pod の emptyDir（`/var/lib/kubelet`）はこの決定の対象外で、ルートのディスクに残る。
- 端末そのものが侵された場合は、包んだ識別子も復号の時に読まれ得る。鍵の装置はこれを狭める。

## 影響

- 新規: `deploy/cloud/backup/{s3-creds.sh,check-bucket.sh,backup-owner-scripts.test.sh}`・`deploy/cloud/lib/target-cluster.sh`・`deploy/cloud/cluster-mark.sh`・`deploy/cloud/cluster-target.test.sh`・
  `deploy/cloud/node/{node-guard.nft,node-guard-enforce.nft,check-node.sh,check-node.test.sh}`・`docs/operations/cloud-owner-target-cluster-runbook.md`・`docs/operations/cloud-node-hardening-runbook.md`。
- 変更: `deploy/cloud/vault/*.sh`（宛先の確かめ・識別子の確かめ）・`deploy/cloud/keycloak-clients/keycloak-clients.sh`・`deploy/cloud/db-users/ast-db-users.sh`・`deploy/cloud/README.md` §1・§3・
  `docs/operations/cloud-backup-alerts-heartbeat-runbook.md`・`docs/operations/cloud-openbao-init-unseal-runbook.md`・`scripts/runbook-secret-handling.test.js`・`.github/workflows/ci.yml`（既存のジョブへ段を足す。起動条件・必須の check は不変）。
- 手順の終了コードに 4（宛先が違う）が加わる。`put-external-secret.sh` を呼ぶ側は、0 と 5 のほかを失敗として扱う（従来どおり）。

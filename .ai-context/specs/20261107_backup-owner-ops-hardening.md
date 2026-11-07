---
title: 作業仕様書 — クラウドのバックアップの鍵の分離と保持の設定の確かめ・手元の S3 の鍵の扱い・オーナーの手順の宛先の確かめと識別子の保護・ノードの防御
type: spec
status: in-progress
related_ids: [NFR-18, NFR-05, NFR-21, IADR-0704, IADR-0675, IADR-0682, IADR-0689, IADR-0674]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - 20261105_2402_cloud-backup-alerts-heartbeat.md
  - 20261106_2471_cloud-openbao-eso.md
---

# 作業仕様書 — バックアップとオーナーの運用の硬化（クラウドの最小構成）

> 着手 2026-11-07（develop `cc8f316f`）。判断の記録は IADR-0704（仮の番号。マージの順で改番があり得る）。
> 稼働中のクラスタ・ノード・提供元のコンソールには何もしない。秘密の値は扱わない。オーナーの端末・提供元のコンソール・ノードの上の作業は、
> 手順書と確かめのスクリプトとして用意し、オーナーが流す。

## 起点

- 要求 **NFR-18**（統制）・**NFR-05**（秘匿）・**NFR-21**（バックアップ）。
- 前提: IADR-0675（オフサイトのバックアップ・Object Lock）・IADR-0682（手元に平文と秘密を残さない）・IADR-0689（OpenBao・オーナーの手順）・IADR-0674（クラウドの最小構成）。
- 利用者の裁定（2026-11-07）: 稼働の前に直す。unseal の鍵の保持は「オーナーが持つ」（変えない）。

## 着手時点の確かめ（develop `cc8f316f` で残っているか）

| 項目 | 残っているか | 根拠 |
| --- | --- | --- |
| A. バックアップの鍵が 1 組で、クラスタの鍵が保持の設定も変えられ得る。保持の設定を外から確かめる手段が無い | 残る | 手順書 `cloud-backup-alerts-heartbeat-runbook.md` §1 の 2（鍵は 1 組）・§2 の 1（その鍵を OpenBao へ）。`deploy/cloud/backup/` に確かめの手段が無い |
| B. 手元の S3 の鍵が平文のファイルにあり、シェルの環境へ読み込ませている | 残る | 同手順書 §1 の 2・4、§6 |
| C. オーナーの手順が kubectl の現在の context をそのまま使う。識別子の保護の前提が手順書に無い | 残る | `deploy/cloud/vault/lib.sh`（context を確かめない）、`cloud-openbao-init-unseal-runbook.md` |
| D. 管理者の kubeconfig・Pod のログが暗号化した領域の外。ノードの中に防御が無い。SSH の root のログインと送り元の絞りが任意 | 残る | `deploy/cloud/README.md` §1-1・§1-2・§1-4 |

## 母集合（規則 9）

- B: `git grep -n -E "set -a|\.secrets/|RCLONE_CONFIG_[A-Z]+_SECRET|AWS_SECRET_ACCESS_KEY" -- '*.md' '*.sh'`（`.ai-context/specs` を除く）。
  - 直す: `docs/operations/cloud-backup-alerts-heartbeat-runbook.md` の 3 か所（§1 の 2・4、§6）。
  - 除外: `deploy/local/platform-backup/script/backup.test.sh`・`scripts/backup-restore-drill.sh`（試験のスタブの中の使い捨ての値）、
    `docs/how-to/run-integration-tests-without-docker.md`・`docs/operations/object-storage-seaweedfs-cutover-runbook.md`（ローカルの PoC の開発用の値。クラウドの鍵ではない）、
    `.ai-context/adr/IADR-0461_…`（記録）。
  - 拡げた検査器（`scripts/runbook-secret-handling.test.js`）を全文書へ当てて、新たに当たるのは上の 3 か所だけであることを確かめた（行末の `\` でつなぐ読み方を足しても、ほかに当たらない）。
- C: `git grep -l kubectl -- 'deploy/cloud/*.sh'`（試験を除く）。`deploy/cloud/vault/` の 7 本（init・unseal・configure・seed-secrets・root-token・put-external-secret・tls-cert）と、
  `deploy/cloud/keycloak-clients/keycloak-clients.sh`・`deploy/cloud/db-users/ast-db-users.sh` の計 9 本。全部に宛先の確かめを入れる。
  `scripts/` の kubectl を使う手順はローカルの PoC（k3d）向けで、クラウドのクラスタを宛先にしないので対象外。
- D: `deploy/cloud/README.md` §1 の全項（1-1〜1-5）。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| A1 | バックアップの鍵を 2 組（管理・書き込み）に分ける手順がある。書き込みの鍵で保持の設定を書けないことを、宣言と同じ設定だけを書き込もうとして確かめる手段がある（書けたら失敗） | `deploy/cloud/backup/backup-owner-scripts.test.sh`（probe-writer） |
| A2 | 版の管理・Object Lock の既定の保持・lifecycle を、宣言（`object-lock.json`・`bucket-lifecycle.json`）と突き合わせる読むだけの確かめがあり、日数・接頭辞・規則の増減・保持の型と期間・版の管理の変化を名指して落ちる | 同（verify。変異 8 つ） |
| B1 | 手元の S3 の鍵は age で暗号化したファイルだけに書かれ、使うときは 1 つのコマンドの環境にだけ載る（引数・平文のファイル・呼んだシェルに残らない） | 同（store・run） |
| B2 | 手順書に、平文の env ファイルを `set -a` の下で読み込む形と、手元のツールの S3 の鍵の `export` が無い（行末の `\` でつないだ行も読む） | `scripts/runbook-secret-handling.test.js`（直す前の字面を変異に足す） |
| C1 | オーナーの手順 9 本は、kubectl を使う前に、現在の context・API の宛先（トンネル）・クラスタの印（kube-system の UID と手元の記録）を確かめ、違えば kubectl を使わずに 4 で終わる。記録が無ければ記録の手順を案内して止まる | `deploy/cloud/cluster-target.test.sh` |
| C2 | 確かめた後の kubectl は、確かめた context に固定される（途中で現在の context が替わっても） | 同、と `deploy/cloud/vault/openbao-scripts.test.sh`（スタブが固定の無い呼び出しを拒む） |
| C3 | 平文の age の識別子は、既定（warn）で警告して進み、`OB_IDENTITY_POLICY=enforce` で止まる。包んだ識別子は通す。中身を表示しない。手順書に識別子を守る前提がある | `deploy/cloud/cluster-target.test.sh` |
| D1 | ノードの中の 2 枚目の防御（nftables）が、自分の表だけを作り直し（`flush ruleset` なし）、入りだけを絞り、既定は報告・別のファイルで落とす | `deploy/cloud/node/check-node.test.sh`（実物の nft を使い捨てのネットワークの名前空間へ当てる） |
| D2 | ノードの確かめ（読むだけ）が、暗号化した 3 つの領域・drop-in・kubeconfig の権限・防御の段と再起動の後の読み込み・SSH の実効値を見て、欠けを名指して落ちる | 同（変異 12 の形） |
| D3 | README と手順書が、kubeconfig と Pod のログを暗号化した領域へ寄せ、SSH の送り元の絞りを必須に、root のログインを外す。既に動いているノードの移し方（管理者の資格の作り直しを含む）がある | 文書（既存の README の検査が通ること） |

## 方式

- `deploy/cloud/backup/s3-creds.sh`（store・run）・`check-bucket.sh`（verify・probe-writer）。鍵はスクリプトの変数へ読み、`exec` するコマンドの環境にだけ載せる。
- `deploy/cloud/lib/target-cluster.sh`（`ct_require_target`）・`deploy/cloud/cluster-mark.sh`（record・check）。`vault/lib.sh` が source し、9 本の手順が呼ぶ。
- `vault/lib.sh` の `ob_identity_guard`（warn → enforce の 2 段）。
- `deploy/cloud/node/`（`node-guard.nft`・`node-guard-enforce.nft`・`check-node.sh`）。ノードへは手順書に従ってオーナーが運ぶ。
- 手順書: `cloud-backup-alerts-heartbeat-runbook.md`（§1・§2・§6）・`cloud-openbao-init-unseal-runbook.md`（識別子を守る）・
  新規 `cloud-owner-target-cluster-runbook.md`・`cloud-node-hardening-runbook.md`。README §1・§3。
- CI: `ci.yml` の既存のジョブ（OpenBao の試験と同じジョブ）に 3 つの試験の段を足す。起動条件・必須の check は変えない。

## 利用者（オーナー）の作業

| 作業 | 手順書 |
| --- | --- |
| S3 の鍵を 2 組にし、暗号化したファイルへ入れる。書き込みの鍵を絞り、probe-writer で確かめる。保持の設定を週に 1 度 verify する。平文の鍵のファイルを消し、その鍵を作り直す | `cloud-backup-alerts-heartbeat-runbook.md` §1・§6 |
| クラスタの印を記録し、ノードで照らす | `cloud-owner-target-cluster-runbook.md` §1 |
| age の識別子をパスフレーズか鍵の装置で守り、`OB_IDENTITY_POLICY=enforce` へ移る | `cloud-openbao-init-unseal-runbook.md`「識別子を守る」 |
| kubeconfig と Pod のログを暗号化した領域へ移し、管理者の資格を作り直す。nftables を報告 → 落とすの順に入れる。SSH の root のログインを外し、送り元を絞る。check-node.sh で確かめる | `cloud-node-hardening-runbook.md` §1〜§4 |

## 確かめ（陰性対照）

- 各試験は、直した箇所を外す変異で赤になることを確かめた（宛先の確かめの行の削除・context の固定の削除・印の比較の無効化・識別子の確かめの削除・
  保持の日数と lifecycle の比較の削除・書けたときの判定の無効化・鍵の export の削除・平文での書き出し・nftables.conf の `flush ruleset` の確かめの削除・
  sshd の確かめの削除・規則の 10250 の受け入れ・既定の段の drop）。手順書の検査は、手順書を直す前に 3 件で赤になった。

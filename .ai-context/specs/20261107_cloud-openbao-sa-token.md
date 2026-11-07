---
title: 作業仕様書 — クラウドの OpenBao の Pod の ServiceAccount のトークンを、自動のマウントから期限を限った明示のトークンへ替える
type: spec
status: in-progress
related_ids: [NFR-18, NFR-05, IADR-0697, IADR-0696, IADR-0689]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - 20261107_cloud-infra-pod-hardening.md
---

# 作業仕様書 — OpenBao の ServiceAccount のトークン

> 着手 2026-11-07（develop `30588d467`）。判断の記録は IADR-0697 決定 7（追記）。稼働中のクラスタには何もしない。

## 起点

- 要求 **NFR-18**（統制）・**NFR-05**（秘匿は OpenBao）。#2496 の追随: 基盤の Pod の判定（H1）を OpenBao にも当てると、OpenBao の Deployment に
  `automountServiceAccountToken: false` が無い。

## 現状と母集合

- `deploy/cloud/vault/openbao.yaml`: ServiceAccount `openbao`（ClusterRoleBinding `system:auth-delegator`）。Pod は自動のマウントのまま。
- `deploy/cloud/vault/configure.sh`: クラスタの中では `auth/kubernetes/config kubernetes_host=…` だけ（`token_reviewer_jwt` なし・`disable_local_ca_jwt` は既定の false）＝
  OpenBao 自身のトークンで TokenReview を呼ぶ。分岐は `/var/run/secrets/kubernetes.io/serviceaccount/ca.crt` の有無。
- 走査: `token_reviewer_jwt`・`disable_local_ca_jwt`・`auth-delegator`・`serviceaccount` を `deploy/cloud`・`deploy/cloud-transition`・`scripts`・`docs/operations` で引いた。
  OpenBao の併存の重ね（`deploy/cloud-transition/openbao-tls-coexist`）は Deployment の宛先を替えるだけで、ボリュームを変えない。

## 方式

1. 自動のマウントを止め、同じ置き場へ projected（トークン 1 時間・`kube-root-ca.crt` の `ca.crt`・名前空間）を読み取り専用で載せる。
2. H1〜H3 の対象に `deploy/cloud/vault` と併存の重ねを入れ、明示のトークンを理由つきの許可の Pod・期限 1 時間以内・読み取り専用に限る。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | OpenBao の Pod は自動のマウントをせず、トークンは期限 1 時間以内・読み取り専用。許可の外の Pod は明示のトークンを持たない | `deploy-cloud.test.js` H1・陰性対照 |
| AC2 | 名前空間 `openbao` の PSA は restricted、OpenBao の Pod は NET_RAW を持たない | 同 H2・H3 |
| AC3 | OpenBao の手順（init・unseal・configure・seed ほか）の実物の試験が通る | `openbao-scripts.test.sh` |
| AC4 | 明示のトークンで kubernetes 認証の設定とログインが通り、トークンが無いと通らない | 実の k3s での実測（下） |

## 実測・記録

- docker の中の k3s v1.35.10（restricted の名前空間・auth-delegator の ServiceAccount）で、宣言と同じ securityContext・projected のボリュームの Pod に
  OpenBao 2.7.1 を dev で起こした（試験の足場。値はその場の乱数）。`auth/kubernetes/config` を `kubernetes_host` だけで書け、ESO 役の ServiceAccount の
  トークン（audience vault）でログインでき（policy default）、audience の違うトークンは `invalid audience` で拒まれた。設定は `disable_local_ca_jwt=false`・
  `token_reviewer_jwt_set=false`。projected のボリュームを外した Pod では、`auth/kubernetes/config` の書き込みが `ca.crt: no such file` で通らなかった。
- 測っていないこと: トークンの期限（1 時間）の切り替わりの後の動き（IADR-0697 残余 6）。

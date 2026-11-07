---
title: 運用 Runbook — クラウドの Keycloak の master realm の一時管理者（開く・閉じる・確かめる・稼働中の構成の一時管理者を消す）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-18]
iadrs: [IADR-0703, IADR-0674, IADR-0689, IADR-0693, IADR-0700]
specs: [20261107_cloud-egress-audit-cert-expiry]
-->

# 運用 Runbook: Keycloak の master realm の一時管理者

> 対象はクラウドの最小構成の Keycloak（`deploy/cloud/infra/keycloak.yaml`）。入口は `deploy/cloud/keycloak-admin/keycloak-admin.sh`（`--live` が無ければ何もしない）。
> 🔴 **本書の手順はすべてオーナーが行う。** 管理者のパスワードは手元へ出さない（Job の Pod が Secret `keycloak-admin` から読む。
> `kubectl set env --from=secret` は Secret への参照を足すだけで、値を読まない）。
>
> **状態を `draft` に留めている理由**: クラウドのノードで流していない。

## 何が変わるか

| 項目 | 旧 | 新 |
| --- | --- | --- |
| master realm の管理者 | 初回の起動で作られ、そのまま残る（Deployment が常に一時管理者の env を持つ） | 常には在らない。稼働中の realm へ手を入れる間だけ開いて、作業の後に消す |
| master realm の総当たりの検知 | 無効（既定） | 有効（platform realm と同じ値。開くたびに確かめる） |

realm の取り込み（`--import-realm`）に管理者は要らない。管理者が要るのは、稼働中の realm へ手を入れる Job（機密クライアントを足す Job など）だけである。

## 1. 開く・閉じる

```sh
bash deploy/cloud/keycloak-admin/keycloak-admin.sh --live open     # 一時管理者の env を足して作り直す → 総当たりの検知を有効にする
# … 管理者の要る作業（例: keycloak-clients.sh --live check / apply）…
bash deploy/cloud/keycloak-admin/keycloak-admin.sh --live close    # 今の状態を見て、在る分だけ消す（一時管理者 → env）→ 在らないことを確かめる
```

- `open`・`close` は Keycloak を作り直す（Recreate。起動の間はトークンの発行が止まる）。米国の場の時間外に流す。
- 作業が途中で失敗しても、`close` は必ず流す。閉じ忘れは `check` が 3 で知らせる。
- `close` は流し直せる。始めに Deployment の env と一時管理者の有無を読み、在る分だけ消す（一時管理者が在れば総当たりの検知を確かめてから消し、
  env が在れば外して作り直す）。前の回が途中（一時管理者を消した後の作り直しの失敗など）で落ちていても、理由を直してもう一度流せば閉じる。
  一時管理者が既に無いことでは止まらない。
- `close` が止まるのは、有無を確かめられないとき（Keycloak に繋がらない・起動していない等。ログに「確かめられない」と出て、何も変えない）と、
  消す・外す操作そのものが落ちたときだけである。Keycloak の状態を直してから流し直す。
- `open` は、master に管理者が居なければ Keycloak が起動のときに作る（Secret `keycloak-admin` の利用者名とパスワード）。居ればそのまま使う。

## 2. 確かめる

```sh
bash deploy/cloud/keycloak-admin/keycloak-admin.sh --live check    # 0: 一時管理者が在らず Deployment も env を持たない / 3: 閉じ忘れ / 1: 確かめられない
```

棚卸しのたびに流す。1 は Keycloak に繋がらないなどで確かめられなかったことを表し、閉じているとはみなさない。

一時管理者の有無は、その資格（Secret `keycloak-admin`）でトークンを取れるかで読む。資格が通らなければ「在らない」と読むので、Secret の値を
OpenBao で替えた後は、前の値で作られた一時管理者を在らないと読む。値を替えるのは閉じた状態で行う。

## 3. 稼働中の構成の一時管理者を消す（一度だけ）

前の構成は、初回の起動で作られた一時管理者が残り、Deployment が env を持つ。

1. 新しい `deploy/cloud` を当てる（Keycloak の宣言から env が消え、作り直される。一時管理者は DB に残る）。
2. `bash deploy/cloud/keycloak-admin/keycloak-admin.sh --live close`（一時管理者が在るので、総当たりの検知を有効にしてから消し、在らないことを確かめる。
   env は 1 で消えているので外す段は飛ぶ）。

`kubectl apply` は、`open` が足した env を消さない（宣言の外の追加を残す）。env を外すのは `close` である。

## 4. 切り戻し

- 管理者が要るのに開けない（Secret `keycloak-admin` が無い等）ときは、ESO の同期を確かめる（値は OpenBao の `cloud/platform/keycloak-admin` のまま）。
- 前の形（常に一時管理者を置く）へ戻すときは、前の版の `deploy/cloud` を当てる。その後の起動で一時管理者が作られる。

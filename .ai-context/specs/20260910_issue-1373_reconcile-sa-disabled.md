---
title: realm reconcile が「宣言 SA 有効・稼働 SA 無効」の既存クライアントで止まるのを、SA 利用者を未存在として扱って収束させる
issue: "#1373"
plan_refs:
  - NFR-09
  - ADR-0005
adr_refs:
  - IADR-0369
status: in-progress
created: 2026-09-10
---

# 作業仕様書: reconcile の service-account-user 400 停止（#1373）

## 起点

- issue #1373。#1372 の realm 変更を稼働クラスタへ当てるため `reconcile-realm.sh` を走らせたところ、
  `platform` realm の読み取りで止まった（2026-09-10 実測）:

```
==> realm 'platform' (microservices-platform-realm.json) mode=apply
ERROR: GET /admin/realms/platform/clients/<bff>/service-account-user -> 400 {"error":"unknown_error", …}
```

- 稼働側の `bff` クライアントは `serviceAccountsEnabled=false`、宣言は `true`。Keycloak は SA 無効クライアントの
  `service-account-user` に **400** を返し（404 ではない）、`kc.get` は 404 以外を例外にする。

## 何が起きているか

`collectLive` は宣言の `users[].serviceAccountClientId` ごとに `service-account-user` を無条件に GET する。
稼働側のクライアントが存在して SA が無効なとき、**計画に入る前に落ちる**。計画側は「クライアントが無い →
SA ロール割当を `deferred`」の経路を既に持つ（`service-account-identity-admin` の試験）が、この場合はそこへ辿り着けない。

実害: この 1 件で realm 全体の追随が止まる。同じ稼働クラスタでは宣言済み 14 クライアント（east-west の s2s 客体・
`reset-gate`・`synthetic-monitor`・`ai-stock-trading-llm-caller` ほか）が live に無く、Keycloak が `reset-gate` の
`client_not_found` を 10 秒ごとに出し続けている。`check-stack-ready.js` G9（`--check`）も同じ経路で「測れない」になる。

## 設計

`collectLive` で、**稼働側のクライアントが `serviceAccountsEnabled` でないときは SA 利用者を「未存在」として扱う**
（`continue`）。その結果 `live.serviceAccounts[clientId]` が無く、計画は既存の経路で
1 周目「client.update（SA 有効化）」＋「SA ロールは deferred」、2 周目「SA 利用者へロール割当」と収束する（`MAX_PASSES = 3`）。

| 経路 | 変更前 | 変更後 |
| --- | --- | --- |
| 稼働に無いクライアント | SA 未存在 → deferred | 変更なし |
| 稼働にあり SA 有効 | SA 利用者を読む | 変更なし |
| **稼働にあり SA 無効** | **GET が 400 → 例外で停止** | **SA 未存在 → deferred** |

### 採らなかった案

- **`kc.get` が 400 を null にする**: 400 は「無い」ではなく「要求が不正」であり、他の端点の 400 まで黙って null にする。
- **先に client.update を当ててから読む**: 読み取りと適用の分離（計画 → 適用 → 再計画）を崩す。

## 走査した母集合（規則 2・9）

`service-account-user` で全走査（除外: `node_modules` / `.git`）: `reconcile-realm.js:536` の 1 箇所のみ。
`serviceAccountsEnabled` を読む他の箇所は `plan` のクライアント差分（宣言側の値を PUT する）で、本件の対象ではない。

## 受け入れ基準

- [ ] 試験: 宣言 SA=true・稼働 SA=false の既存クライアントで `collectLive` が落ちず `serviceAccounts` にその client を持たない
- [ ] 試験: その live に対する `plan` が `client.update` と `deferred`（`service-account-<clientId>`）を含む
- [ ] 陰性対照: 稼働 SA=true のときは従来どおり SA 利用者を読む（既存試験が通る）
- [ ] 稼働クラスタで `reconcile-realm.sh` が収束する

## 計画書との差異

- 差異なし（IADR-0369 の「計画 → 適用 → 再計画」の型のまま、読み取りの欠陥を直すだけ）。

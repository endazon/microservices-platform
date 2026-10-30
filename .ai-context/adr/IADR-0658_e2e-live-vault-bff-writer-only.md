---
title: IADR-0658 e2e-live は Vault（-dev）と BFF の書き込みの役だけを足して SC-22 の一覧の 200 の形を流し、ESO は入れない（新しい opt-in VAULT_BFF_WRITER）
type: impl-adr
status: Accepted
related_ids: [SC-22, NFR-18, NFR-10, ADR-0095, ADR-0124, IADR-0453, IADR-0625, IADR-0585, IADR-0096, IADR-0627]
author: claude
created: 2026-10-30
updated: 2026-10-30
related_specs:
  - ../specs/20261030_2230_e2e-live-vault-bff-writer.md
---

# IADR-0658: e2e-live の Vault と BFF の書き込みの役（#2230）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-30
- 決定者: claude（#2230）。案 B はコーディネータの承認（2026-10-30）による。

## 起点・関連

- 起点 issue: #2230（E-42 / PR #2229 の M-113 が、CI では 503 の形だけで通っていた）。
- 前提: IADR-0453 決定 5（保管先に届かない構成は 503）、IADR-0625（試験アカウントの値はジョブの中で作る）、IADR-0585（role の audience）、IADR-0627（`E2E_SEARCH_HITS` の門の作法）。

## コンテキストと課題

- M-113（SC-22 の一覧）は、200（値の鍵が無い一覧）か 503（保管先に届かない）のどちらでも通る。e2e-live の使い捨てのスタックは Vault を立てないので、CI では 503 だけが通っていた。**200 の応答に値の鍵が無いことを CI で一度も確かめていない。**
- issue は `VAULT=1`（必要なら `ESO=1`）を足す案を挙げた。着手前の見積もり（issue のコメント 2026-10-30）で次が分かった。
  - `ESO=1` は ESO の chart（3 Deployment）・Reloader・ExternalSecret 約 35 本を足し、起動の段を +4〜8 分と見積もった。
  - さらに `ESO=1` は Secret の供給元を手動の作成から Vault → ESO へ切り替える（`postgres-app`・ブローカのユーザー・OIDC 群など）。
  - `ESO=1` の `e2e-accounts` の ExternalSecret（Owner）は、ジョブが作る試験アカウントの Secret（IADR-0625）と所有がぶつかる。
- 一覧が 200 になるのに要るのは、BFF が k8s auth でログインし、項目の metadata を読めることだけである（KV が無い項目は「未設定」の 200、供給元の ExternalSecret が読めなくても「不明」の 200。`SecretItemBffEndpoints`・`VaultKvClient`）。

## 決定

1. **新しい opt-in `VAULT_BFF_WRITER=1`**（`scripts/k8s-local-up.sh`。`VAULT=1` が前提で、無ければクラスタを作る前に止まる。値域は `1` / `0`・未指定）。
   - VAULT ブロックの rollout の待ちの後に、`vault-auth-rbac.yaml`（Vault の SA に `system:auth-delegator`）を当て、`deploy/local/vault/bff-writer/bootstrap.sh` を走らせる。
   - bootstrap は k8s auth の有効化と設定、policy `bff-secret-write` / `bff-secret-group-write`、role `bff-secret-writer`（audience=vault）だけを書く。
     - **ESO の bootstrap の部分集合**である。policy は同じ HCL ファイルを読み、role の字面は同じにする（一致は `k8s-local-up.test.js` が固定）。
     - KV の種・ESO の role・ExternalSecret は入れない。
   - `ESO=1` と一緒なら何もしない（ESO の bootstrap が同じものを入れる）。
   - 🔴 **既存の `VAULT=1` の手順は変えない**（変数を立てなければ起動器の出すコマンドは従前と同じ。`VAULT_BFF_WRITER=0` との一致を試験が固定する）。
   - role の audience は chart（名乗る側）の後に書く順序を、`vault-k8s-auth-audience.test.js` の検査 5 に足した。
2. **e2e-live.yml** は起動に `VAULT=1 VAULT_BFF_WRITER=1` を足す。新規クラスタには ESO の CRD が無いので、Vault は -dev（インメモリ）で立つ。使い捨てのスタックにはこれで足りる。
   - 起動の段の前後に `df -h` / `free -m` を出し、ランナーの余裕を実測する。
   - 起動の段の上限（30 分）は、実測の余裕が 10 分を切るときだけ上げる。
3. **M-113 の門 `E2E_SECRETS_LISTED`**（`E2E_SEARCH_HITS` と同じ作法。値域は `1` か未設定・空で、`E2E_LIVE=1` が要る）。
   - `1` のときは 200（`listed`）だけを通す。503 を許したままだと、Vault が落ちた退行を見逃すためである。
   - 未設定（常設の PoC など）は従前どおり 2 つの形を許す。
   - e2e-live は `1` を渡す。

## 却下した案

- **`VAULT=1 ESO=1`（issue の案 A）**: 採らない。
  - 起動の段が +4〜8 分（段の上限 30 分に対して最悪で余裕 5 分）になる。
  - Secret の供給経路が integration-stack.yml と分かれる。
  - 試験アカウントの Secret（IADR-0625）と ESO の所有がぶつかり、別の門か KV への投入の仕組みが要る。
  - M-113 の 200 に要るのは BFF の役だけで、それに比べて大きい。
- **ESO の bootstrap に「BFF の分だけ」の切り替えを足す**: 採らない。ESO の経路の手順に分岐を入れると、既存の `VAULT=1 ESO=1` の手順に触れる。別の小さな bootstrap にし、字面の一致を試験で固定する方が、既存の手順を変えない。

## 結果

- 良い影響:
  - CI で「200 の応答に値の鍵が無い」を確かめるようになる。
  - Vault が落ちた退行は `E2E_SECRETS_LISTED=1` で赤になる。
- 悪い影響 / トレードオフ: 起動器に opt-in が 1 つ増える。bootstrap の BFF の分が 2 か所にある（字面の一致は試験で固定）。
- 🔴 **残余リスク**:
  - **CI は本番相当の ESO 経路（Vault → ExternalSecret → Secret）を通らない。** ESO の供給・Reloader・ExternalSecret の同期の退行は e2e-live では見つからない。この経路を見るのは、`VAULT=1 ESO=1` で立てた常設の PoC とローカルの起動である。
  - Vault は -dev（インメモリ）であり、永続化（raft・unseal・バックアップの役）の経路も CI では通らない。
  - 一覧の 200 が確かめるのは metadata の読み取りまでである。書き込み（PUT）は live-rw の範囲で、本決定では足していない。
- PoC 配備への影響: 無い（使い捨てのスタックと起動器の opt-in だけ。常設の PoC は `VAULT=1 ESO=1` のまま）。

## 検証

- `scripts/k8s-local-up.test.js`:
  - 既定（`VAULT=1` だけ）は `VAULT_BFF_WRITER=0` とコマンドが同じで、役の bootstrap も rbac も出さない。
  - `VAULT_BFF_WRITER=1` は rollout の後に rbac、その後に role（audience=vault）と policy を書き、ESO を入れない。
  - `VAULT=1` が無いと止まり、値域外も止まる。`ESO=1` と一緒なら二重に書かない。
  - role と policy の字面が ESO の bootstrap と同じである。
- `scripts/vault-k8s-auth-audience.test.js` 検査 5: chart の後に BFF の役の bootstrap を当てる（逆転の変異で赤）。
- `e2e/live-opt-in.smoke.spec.ts`: `E2E_SECRETS_LISTED` の値域と `E2E_LIVE=1` の要求。
- e2e-live（PR の実行）: M-113 の annotation が `listed`。起動の段の前後の `df` / `free` を PR 本文に記録する。

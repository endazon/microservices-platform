---
title: 作業仕様書 — e2e-live に Vault と BFF の書き込みの役だけを足し、M-113 の 200 の形を CI で流す（#2230。案 B）
type: spec
status: done
related_ids: [SC-22, NFR-18, NFR-10, ADR-0095, ADR-0124, IADR-0453, IADR-0625, IADR-0585, IADR-0096, IADR-0658]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2230"
---

# 作業仕様書 — e2e-live の Vault と BFF の書き込みの役（#2230）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `e0cf7a86`）。見積もり（issue のコメント 2026-10-30）を経て、コーディネータが**案 B**を承認した。

## 承認の条件（コーディネータ 2026-10-30）

1. CI が本番相当の ESO 経路を通らないことを IADR の残余リスクに書き、issue にも記録する。
2. 最初に `df` / `free` を起動の段の前後で出し、実測値を PR 本文に載せる。
3. 起動の段の 30 分の上限は、実測で余裕が 10 分を切る場合だけ上げる。
4. 新しい opt-in は、既存の `VAULT=1` の起動手順（ローカル）を変えない形にし、`k8s-local-up.test` で固定する。

## 現状（実測）

- M-113（`e2e/live/admin.ro.spec.ts`）は 200（`listed`）か 503（`vault-unavailable` 等）のどちらでも通る。e2e-live は Vault を立てないので、CI では 503 だけが通る。
- 一覧（`GET /bff/secrets`）が 200 になる条件（`SecretItemBffEndpoints.cs`・`VaultKvClient.cs`）:
  - BFF が k8s auth（role `bff-secret-writer`、audience `vault`）でログインできる。
  - 項目の metadata が読める。KV が無ければ `Absent`（未設定として 200）。供給元の ExternalSecret が読めなくても `Unknown`（200 のまま）。
- 役と policy は今は `deploy/local/vault/eso/bootstrap.sh`（`ESO=1` のときだけ起動器が呼ぶ）にしか無い。k8s auth の TokenReview に要る `vault-auth-rbac.yaml`（Vault の SA に `system:auth-delegator`）も ESO ブロックだけが当てる。
- 新規クラスタの `VAULT=1` 単独は、ESO の CRD が無いので `vault-dev.yaml`（-dev・インメモリ）で立つ。使い捨ての CI にはこれで足りる。

## 方針（IADR-0658）

1. **新しい opt-in `VAULT_BFF_WRITER=1`**（`VAULT=1` が前提。無ければ起動器が止まる）。VAULT ブロックの rollout の待ちの後で、次を当てる。
   - `kubectl apply -f deploy/local/vault/eso/vault-auth-rbac.yaml`
   - `bash deploy/local/vault/bff-writer/bootstrap.sh`: k8s auth の有効化と設定、policy `bff-secret-write` / `bff-secret-group-write`（HCL は ESO の bootstrap と同じファイル）、role `bff-secret-writer`（`vault_role_write_cmd` 経由で audience=vault）だけを書く。KV の種は入れない。
   - `ESO=1` と一緒なら何もしない（ESO の bootstrap が同じものを入れる）。
2. **既存の `VAULT=1` の起動手順は変えない**（変数を立てなければ、起動器の出すコマンドは従前と同じ）。`k8s-local-up.test.js` で次を固定する。
   - 既定（`VAULT=1` だけ）で新しい手順が出ないこと。
   - 新しい opt-in で rbac と bootstrap が rollout の後に出ること。
   - `VAULT=1` が無いと止まること。
   - `ESO=1` と一緒なら二重に当てないこと。
3. 名乗る側（chart）より後に role の audience を当てる順序を `vault-k8s-auth-audience.test.js` の検査 5 に足す。
4. **e2e-live.yml**:
   - 起動に `VAULT=1 VAULT_BFF_WRITER=1` を足す。
   - 起動の段の前後に `df -h` / `free -m` を出す（条件 2）。
   - E2E の段に `E2E_SECRETS_LISTED=1` を渡す。
5. **M-113 の門 `E2E_SECRETS_LISTED`**（`E2E_SEARCH_HITS` と同じ作法: 値域は `1` か未設定・空、`E2E_LIVE=1` が要る）。
   - `1` のときは 200（`listed`）だけを通す。Vault が落ちた退行を 503 の許容で見逃さないためである。
   - 未設定（常設の PoC など）は従前どおり 2 つの形を許す。
6. 起動の段の上限（30 分）は、実測の余裕が 10 分を切るときだけ上げる（条件 3）。

## 受け入れ基準

- [x] e2e-live（PR の実行）で M-113 の annotation が `listed`、`E2E_SECRETS_LISTED=1` で 200 だけが通る
- [x] 起動の段の前後の `df` / `free` の実測値を PR 本文に載せ、上限の扱いを条件 3 で決めた
- [x] `k8s-local-up.test.js`・`vault-k8s-auth-audience.test.js`・`live-opt-in.smoke.spec.ts` が緑
- [x] IADR-0658 の残余リスクに「CI は本番相当の ESO 経路を通らない」を書き、issue にも記録した

## 結果（PR #2366 の e2e-live。run 41035294127）

- M-113: passed、annotation「SC-22 の一覧」= `listed`（`E2E_SECRETS_LISTED=1` で 200 だけを通した）。
- 起動の段: 15 分 26 秒（09:42:08 → 09:57:34）。上限 30 分に対して残り 14 分 34 秒 ≥ 10 分なので、上限は上げない（条件 3）。
  - Vault の段（`[opt-in] Vault dev` → BFF の役の bootstrap の完了）は約 24 秒。直近 5 回の起動の段は 12.6〜16.8 分で、その範囲に収まる。
- ランナー（前 → 後）: ディスク `/` 145G 中 使用 60G・空き 85G → 使用 91G・空き 54G。メモリ 15,983 MiB 中 used 1,396・available 14,587 → used 5,241・available 10,742。swap は未使用。

## 母集合（規則 9）

- `git grep -nE "VAULT を立てない|VAULT=1 ESO=1 でなければ|M-113"`:
  - `deploy/local/values-local.yaml` 202 行（「VAULT=1 ESO=1 でなければ … 503」）→ 新しい opt-in を併記する。
  - `admin.ro.spec.ts` の注記 → 門の説明に直す。
- 起動器の opt-in の一覧（`deploy/local/README.md`）に `VAULT_BFF_WRITER` を足す。

## 残余リスク

- CI は本番相当の ESO 経路（Vault → ExternalSecret → Secret）を通らない（条件 1）。
- -dev の Vault（インメモリ）であり、永続化の経路も通らない。

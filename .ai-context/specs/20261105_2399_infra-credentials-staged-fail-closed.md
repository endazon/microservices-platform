---
title: 作業仕様書 — 基盤の資格情報の生成を fail-closed に揃える（IADR-0574 の残余 1 の続き。段階で入れる。#2399）
type: spec
status: done
related_ids: [NFR-18, ADR-0124, IADR-0574, IADR-0572, IADR-0517, IADR-0485, IADR-0672]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2399"
---

# 作業仕様書 — 基盤の資格情報の生成を fail-closed に揃える（#2399）

> 本仕様書は実装着手前に作成した（着手 2026-11-05。develop `b05b19b9`）。判断の記録は **IADR-0672**（新規）に置く。
> 🔴 稼働中のクラスタには何も実行しない。試験はすべて PATH のスタブの下で行う。値は表示しない・リポジトリに置かない。

## 起点（トレーサビリティ）

- 非機能要求: **NFR-18**。計画 ADR: **ADR-0124 決定 1**（対になる秘密・初期投入は無いときだけ作る形を変えない）。
- 前提の IADR:
  - **IADR-0574**（同じ守りの 1 本目の対象。本仕様書はその §残余 1 を引き継ぐ）
  - **IADR-0572 / IADR-0517**（kube context の許可集合・検査の形）
  - **IADR-0485**（無いときだけ作る）

## 要件（コーディネーターの指示 2026-11-05）

IADR-0574 §残余 1 と、その作業仕様書 §残余で「今回の範囲外」とした基盤の資格情報に、IADR-0574 と同じ守りを広げる。

1. dev ではないクラスタで、宣言の開発用の値のまま作らない。
2. 稼働の値が開発用の値のままかを検査できるようにする。
3. 稼働の通信や起動を止め得るので、段階で入れる。まず検査・報告だけを入れ、強制は後から入れる。
4. 作り直しは、利用者が実行するスクリプトとして用意する。値は表示しない。
5. 対象外: `deploy/docker-compose.yml` の手元の経路（IADR-0574 §残余 2）。

## 母集合（規則 9。走査で引いた）

### 走査語

追跡ファイルのうち `scripts/*.sh`・`scripts/lib/*.sh`・`deploy/**/*.sh`・`deploy/**/*.yaml`・`.github/workflows/*.yml` を、次の 2 つの語で走査した。

- `(PASSWORD|PASS|SECRET|TOKEN|SECRET_KEY|APIKEY|API_KEY)…:-<空でない既定>}`
- マニフェストの `WITH PASSWORD` / `stringData:`

### 対象

作る口は起動器 `scripts/k8s-local-up.sh` と Vault の種 `deploy/local/vault/eso/bootstrap.sh` である。

| env | Secret（キー） | Vault の種 | 備考 |
| --- | --- | --- | --- |
| `PG_PASSWORD` | `platform-infra/postgres`（`password`） | `msp/postgres` | DB の管理者。読み手は DB の初期化とバックアップの Job |
| `APP_DB_PASSWORD` | `microservices-platform/postgres-app`（`password`） | `msp/postgres-app` | DB 利用者 `kp` |
| `WIKIJS_DB_PASSWORD` | `microservices-platform/wikijs-db`（`password`） | `msp/wikijs-db` | 同じ DB 利用者 `kp`（上と同値） |
| `OBJECT_STORAGE_SECRET_KEY` | `microservices-platform/object-storage-credentials`（`secretKey`） | `msp/object-storage-credentials` | オブジェクトストレージの資格情報 |
| `VAULT_DEV_ROOT_TOKEN` | `platform-infra/vault-dev-token`（`token`） | 無し | 秘匿管理の固定の管理者トークン。`VAULT=1` のときだけ作る |

### ストア側（同じ表に載せる。作り直しと検査で扱う）

- `deploy/local/infra/postgres.yaml` の初期化スクリプトは、DB 利用者 `kp`・`ai` を固定の値で作る。
  - 作るのは空の PVC の初回の起動だけで、Secret の値を読まない。
  - 検査（DB へのログインで判定）と作り直し（`ALTER ROLE`）で扱う。
  - 初期化が Secret の値を読む形への変更は、段 2（強制）の後続に送る（残余 1）。

### 除外とその理由

| 走査で当たったもの | 除外の理由 |
| --- | --- |
| `KEYCLOAK_ADMIN_PASSWORD` / `RABBITMQ_PASSWORD` | IADR-0574 が守っている。検査の対象には入れる（同じ検査で一覧できるようにする） |
| `*_CLIENT_SECRET`（`*_OIDC_*` を含む） | IADR-0517 / IADR-0572 が context の許可集合で守り、`reconcile-realm.sh --check-dev-secrets` で検査できる |
| `SESSION_STORE_PASSWORD`・観測の前段のトークン・ブローカのサービスごとのユーザー | 既存の値か、初回だけ乱数で作る（開発用の値を持たない） |
| `ANTHROPIC_API_KEY` 等・SMTP | 既定は空（fail-safe） |
| `OBJECT_STORAGE_ACCESS_KEY`・`RABBITMQ_USER`・`KEYCLOAK_ADMIN_USER` | 識別子である。IADR-0574 決定 1 と同じ扱い |
| `RABBITMQ_USERS_SECRET` / `WIKIJS_API_KEY_*` / `MIGRATION_TOKEN_*` | 名前・期限・回数で、資格情報ではない |
| `scripts/verify-oidc-edge-flow.sh` の利用者のパスワード | realm の試験利用者を使う確認のスクリプトで、基盤の資格情報を作らない |
| `deploy/docker-compose.yml`・`deploy/create-multiple-dbs.sh` | 手元の compose の経路（IADR-0574 §残余 2） |
| `deploy/bootstrap/secret-templates.example.yaml` | 例のファイルで、配備しない |
| DB 利用者 `ai`（取引ユニットの DB） | 消費側の Secret は取引ユニットが持つ。検査は名指す（読むだけ）。作り直しは取引ユニットと対で行うので、本スクリプトの対象外（残余 2） |

## 方針（IADR-0672）

1. 判定は `scripts/lib/infra-credentials.sh` に足す。IADR-0574 の 2 つと同じ口に置き、複写しない。
   - 新しい対象を「段階の対象」（`INFRA_CRED_STAGED_TARGETS`）とする。
   - 選び方は IADR-0574 と同じ **明示 ＞ 既存の値（開発用でない）**。
   - どちらも無いときの扱いは、kube context と段で変わる（下表）。

   | context | 段 | 扱い |
   | --- | --- | --- |
   | dev（IADR-0517 の許可集合） | どれでも | 従前どおり開発用の値で作る |
   | dev ではない | 報告（段 1。既定） | 開発用の値で作り、名指して警告する（値は出さない） |
   | dev ではない | 強制（`INFRA_CREDENTIALS_ENFORCE=1`） | 止める。`ALLOW_DEV_INFRA_CREDENTIALS=1` があれば、警告して開発用の値で作る |

2. 起動器は IADR-0574 と同じ位置（[1/7] の直後・Secret を 1 つも書く前）で決め、選んだ値を export する。
   - Secret の作成の `:-<開発用の値>` は `:?` にする（値は判定器が決める）。
   - 既存の値を使うので、作り直した値が次の起動で戻らない。
3. Vault の種は、まだ無い KV だけを、明示の値と context で判定する（在る KV は触らない）。
4. 検査: `scripts/check-infra-credentials.sh --live`（読むだけ）。
   - IADR-0574 の 2 つと段階の対象の、Secret の値を開発用の値と比べる。
   - ストア側も確かめる: DB 利用者 `postgres`・`kp`・`ai` のログインと、秘匿管理の固定の管理者トークン。
   - 名前だけを出し、値は出さない。終了コードは 0＝全部済み、1＝開発用の値が残る、2＝確かめられないものがある。
5. 作り直し: `scripts/rotate-infra-credentials.sh --live <対象>…`（利用者が実行する）。
   - 対象は `postgres-admin`・`app-db`・`object-storage`・`vault-root-token`。
   - 新しい値は乱数で作り、表示しない。値はファイル（0600）か stdin でだけ渡し、どのプロセスの引数にも載せない。
   - 順序は手順書（secret-rotation-runbook の B-1）と同じ: 保管先 → ストア側 → 同期 → 消費側の作り直し。

## 受け入れ基準と試験の写像

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | 判定の真理値表: 明示 × 既存 × context（dev / 非 dev）× 段（報告 / 強制）× opt-in | `k8s-local-up.test.js`「IADR-0672: 判定の真理値表」 |
| AC2 | dev の context では、指定が無ければ従前と同じ開発用の値で作る（Secret の行が変わらない） | 同「dev の context では従前と同じ」 |
| AC3 | dev ではない context・報告の段では、作るが名指して警告する。値は出力・引数に出ない | 同「報告の段」 |
| AC4 | dev ではない context・強制の段では、[1/7] の直後・何も書かずに名指して止まる。opt-in で通る | 同「強制の段」 |
| AC5 | 既存の Secret が開発用でない値なら使う（作り直した値を戻さない）。明示は既存に勝つ | 同「既存の値」 |
| AC6 | 起動器の Secret の作成に、段階の対象の `:-<開発用の値>` が残らない | 同「既定値の代わりが残っていない」 |
| AC7 | Vault の種は、無い KV だけを判定し、強制の段で止めるときは Vault へ何も書かない | `scripts.repo.test.js` の IADR-0672 節 |
| AC8 | 検査は、開発用の値の Secret・ストア側の残りを名指して 1 を返す。値は出さない。何も書かない | `scripts.repo.test.js` の IADR-0672 節 |
| AC9 | 検査は、判定できない（誤った値でもログインできる）ときは「確かめられない」として 2 を返す | 同 |
| AC12 | 検査・作り直しは `--live` が無ければ何もせずに 3 で終わる（`live-scripts.json` に分類する） | 同・#1550 の閉包 |
| AC10 | 作り直しは、保管先 → ストア側 → 消費側の順に動く。値は引数・出力に出ない。`--live` が無ければ何もしない | 同 |
| AC11 | 作り直しは、ESO の所有する Secret なら Vault の KV へ書いて同期を促し、そうでなければ Secret を直接書く | 同 |

## 赤の確認と変異（実測）

- 赤の確認（実装前）:
  - `k8s-local-up.test.js` の 4 つの区画が、新しい試験で赤になった（真理値表・名指し・既存の値・既定値の残り）。
  - 検査と作り直しの試験 8 件は、スクリプトの無い状態で全件赤になった。
- 実装後: `k8s-local-up.test.js` 427 件が緑。`scripts.repo.test.js` の IADR-0672 の試験 10 件が緑。

| 変異 | 赤になった試験 |
| --- | --- |
| M2: 強制を `1` 以外の値でも効かせる | AC1（真理値表） |
| 種の判定を外す（`bootstrap.sh`） | AC7 |
| M3: 検査の比較を外す | AC8 |
| M4: 作り直しが値を引数に載せる（`--patch "<JSON>"`） | AC10（2 件） |
| M1: 起動器の段階の判定の呼び出しを外す | AC6 が固定する（`infra_cred_staged_resolve` の呼び出しの在処と、`:?` の形） |

## 配備前の手順（PoC。値は書かない）

PR 本文に順序つきで書く。

- 段 1 は報告だけで、配備しても止まらない。
- まず `check-infra-credentials.sh --live` を流し、名指されたものを `rotate-infra-credentials.sh --live` で作り直す。
- 全部が済んだら、段 2（`INFRA_CREDENTIALS_ENFORCE=1` を既定にする）へ進める。段 2 は別の PR で入れる。

## 残余

1. DB の初期化スクリプトは、空の PVC の初回に、DB 利用者を固定の値で作る（Secret を読まない）。
   - 検査が名指し、作り直しが `ALTER ROLE` で直す。
   - 初期化が Secret の値を読む形は、段 2 と合わせて入れる。
2. DB 利用者 `ai`（取引ユニット）の作り直しは、取引ユニットの Secret と対で行う（本スクリプトの対象外。検査は名指す）。
3. 判定は context の名前だけを見る（IADR-0517 残余 3 と同じ）。

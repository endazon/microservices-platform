---
title: IADR-0672 基盤の資格情報の判定を、DB・オブジェクトストレージ・秘匿管理の管理者トークンへ広げる。dev ではない context で開発用の値になるとき、段 1 は報告だけ・段 2 は明示の切り替えで止める。検査と作り直しは利用者が実行するスクリプトにする（#2399）
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0124, IADR-0574, IADR-0572, IADR-0517, IADR-0485]
author: claude
created: 2026-11-05
updated: 2026-11-05
related_specs:
  - ../specs/20261105_2399_infra-credentials-staged-fail-closed.md
---

# IADR-0672: 基盤の資格情報の生成を fail-closed に揃える（段階で入れる。#2399）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: コーディネーターの指示（2026-11-05）・claude（段の分け方・名前・スクリプトの形）

## 起点・関連

- 起点 issue: #2399。IADR-0574 §残余 1 を引き継ぐ。
- 計画: **ADR-0124 決定 1**。要求 **NFR-18**。
- 前提:
  - [IADR-0574](./IADR-0574_infra-credentials-fail-closed.md)（判定の口 `scripts/lib/infra-credentials.sh`・明示 ＞ 既存 ＞ 止める）
  - [IADR-0572](./IADR-0572_dev-secret-guard-all-service-account-clients.md) / [IADR-0517](./IADR-0517_dev-client-secret-guard-kube-context-allowlist.md)（kube context の許可集合）
  - [IADR-0485](./IADR-0485_paired-secrets-create-only-declarations.md)（種は無いときだけ作る）

## コンテキストと課題

- IADR-0574 は、Keycloak の管理者とブローカの資格情報だけを判定の対象にした。§残余 1 は、ほかの基盤の資格情報を範囲外として残した。
- 走査で引いた対象（作業仕様書 §母集合）は 5 つ。いずれも env が無ければ宣言の開発用の値で作る。
  - DB の管理者（`PG_PASSWORD`）
  - DB 利用者 `kp` の 2 つの Secret（`APP_DB_PASSWORD`・`WIKIJS_DB_PASSWORD`）
  - オブジェクトストレージ（`OBJECT_STORAGE_SECRET_KEY`）
  - 秘匿管理の固定の管理者トークン（`VAULT_DEV_ROOT_TOKEN`）
- 起動器は再実行のたびに、これらの Secret を env か開発用の値で書き直す。作り直した値も、次の起動で戻る。
- 強制をいきなり入れると、稼働中のクラスタで起動器の再実行が止まる。止まらないようにするには、利用者が作り直してから入れる必要がある。

## 検討した選択肢

1. **IADR-0574 と同じく、すぐに止める（どの context でも opt-in が要る）**
   - 稼働のクラスタで起動器が止まる。CI・手元の起動にも opt-in が要る。
   - 段階で入れる指示に反する。
2. **報告だけ（強制を持たない）**
   - 検知はできるが、作らない守りが永久に入らない。
3. **段階の切り替えを持つ（採用）**
   - 段 1（既定）は、dev ではない context で警告して作る。
   - 段 2 は `INFRA_CREDENTIALS_ENFORCE=1` で止める。
   - 段 2 の既定化は、利用者の作り直しの後に別の PR で入れる。

## 決定

### 決定 1 — 判定は IADR-0574 の口に足す（段階の対象）

- `scripts/lib/infra-credentials.sh` に `INFRA_CRED_STAGED_TARGETS`（上の 5 つ）を足す。判定は `infra_cred_staged_decide`（純関数）。
- 選び方は **明示 ＞ 既存の値**（どちらも開発用の値と同じなら「無い」と同じ）。どちらも無いとき:

  | context | 段 | 判定 |
  | --- | --- | --- |
  | dev の許可集合（`dev_client_secret_context_is_dev`） | どれでも | `dev`（従前どおり開発用の値） |
  | dev ではない | 報告（既定） | `report`（開発用の値で作り、名指して警告） |
  | dev ではない | 強制（`INFRA_CREDENTIALS_ENFORCE=1`） | `deny`（止める） |
  | dev ではない | 強制 ＋ `ALLOW_DEV_INFRA_CREDENTIALS=1` | `override`（警告して開発用の値） |

- 切り替えの値は `1` だけを有効にする（`true` 等は無効。IADR-0574 決定 3 と同じ）。
- 利用者名・アクセスキーの ID は識別子なので対象外（IADR-0574 決定 1 と同じ）。
- IADR-0574 の 2 つの判定（どの context でも opt-in が要る）は変えない。

### 決定 2 — 起動器は [1/7] の直後で決め、既存の値を引き継ぐ

- IADR-0574 の判定の直後に `infra_cred_staged_resolve` を呼ぶ。既存の Secret を名前空間ごとに読み、選んだ値を export する。
  - `VAULT_DEV_ROOT_TOKEN` は `VAULT=1` のときだけ対象にする。
- Secret の作成の `${…:-<開発用の値>}` は `${…:?}` にする。
- **既存の値を使うので、作り直した値が次の起動で戻らない。** これは作り直しのスクリプトが効き続けるための前提である。

### 決定 3 — Vault の種は無い KV だけを判定する

- `bootstrap.sh` は、Vault へ何か書く前に、まだ無い KV（`msp/postgres`・`msp/postgres-app`・`msp/wikijs-db`・`msp/object-storage-credentials`）に対応する env だけを、明示の値と context で判定する（`infra_cred_staged_guard`）。
- 在る KV は触らない（IADR-0485）。

### 決定 4 — 検査は読むだけのスクリプトにする

- `scripts/check-infra-credentials.sh --live` を足す。`--live` が無ければ何もせずに終わる（起動器の作法。#1550）。
- 見るのは 2 つ。
  1. Secret の値: IADR-0574 の 2 つと段階の対象を、開発用の値と比べる。
  2. ストア側:
     - DB 利用者 `postgres`・`kp`・`ai` が開発用の値でログインできるか。
       - Pod の IP へ繋ぐ（`127.0.0.1` は認証しない設定があり得るので使わない）。
       - 先に誤った値でも試す。誤った値でもログインできるときは、判定できない（2）とする。
     - 秘匿管理の固定の管理者トークンが開発用の値で通るか。
- 名前だけを出し、値は出さない。比べる値は Pod の中へ stdin で渡し、引数に載せない。
- 終了コード: 0＝開発用の値が無い、1＝残る、2＝確かめられないものがある。

### 決定 5 — 作り直しは利用者が実行するスクリプトにする

- `scripts/rotate-infra-credentials.sh --live <postgres-admin|app-db|object-storage|vault-root-token>…` を足す。
- 新しい値は乱数（16 進 48 字）で作り、表示しない。値はファイル（0600・終了時に消す）か stdin でだけ渡す。
- 順序は `docs/operations/secret-rotation-runbook.md` B-1 と同じ: 保管先 → ストア側 → 同期 → 消費側の作り直し。
- 保管先は 2 通り。
  - Secret を ExternalSecret が持つとき（ESO）: Vault の KV へ書き、同期を促す。
  - そうでないとき: Secret へ直接書く。
- 対象ごとのストア側と消費側:

  | 対象 | ストア側 | 消費側の作り直し |
  | --- | --- | --- |
  | `postgres-admin` | `ALTER ROLE postgres` | 無し（DB の初期化とバックアップの Job が Secret を読む） |
  | `app-db` | `ALTER ROLE kp` | 2 つの Secret（`postgres-app`・`wikijs-db`）へ同じ値を書き、`microservices-platform` の Deployment を作り直す |
  | `object-storage` | 無し（起動時の環境変数で決まる） | 本体を先に作り直し、次に `microservices-platform` の Deployment を作り直す |
  | `vault-root-token` | 今のトークンで新しい固定のトークンを作る | Secret を書き、秘匿管理を作り直し、新しいトークンで通ることを確かめてから古いトークンを失効させる |

### 決定 6 — 段 2 は別の PR

- 段 2 では `INFRA_CREDENTIALS_ENFORCE` の既定を `1` にする。
- 入れる条件は、PoC で `check-infra-credentials.sh --live` が 0 を返すこと。
- DB の初期化スクリプトが Secret の値を読む形（残余 1）も、段 2 で入れる。

## 結果

- 良い影響:
  - dev ではない context で開発用の値の基盤の資格情報を作ると、名指して警告が出る（段 1）。
  - 稼働の値は検査で一覧でき、作り直しはスクリプト 1 本で済む。
  - 作り直した値が起動器の再実行で戻らない。
- 悪い影響:
  - 段 1 のうちは作るのを止めない（報告だけ）。
  - 作り直しは、DB 利用者 `kp` の切り替えの間、消費側を断続的に止める（手順書 B-1 と同じ。業務の少ない時間に行う）。

## 残余

1. DB の初期化スクリプト（空の PVC の初回）は、DB 利用者を固定の値で作る。検査が名指し、作り直しが直す。Secret を読む形は段 2 で入れる。
2. DB 利用者 `ai` は取引ユニットの Secret と対で回すので、作り直しの対象外である（検査は名指す）。
3. 判定は context の名前だけを見る（IADR-0517 残余 3）。
4. `deploy/docker-compose.yml` の手元の経路は対象外（IADR-0574 §残余 2）。

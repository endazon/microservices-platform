---
title: IADR-0574 起動器と Vault の種は、Keycloak の管理者・RabbitMQ のブローカのパスワードを公知の既定値で作らない。明示 ＞ 既存の値 ＞ 止める、開発用の値は明示の opt-in（使い捨ての環境だけ）でだけ許す
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0124, IADR-0572, IADR-0485, IADR-0286, IADR-0517]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密・初期投入は無いときだけ作る）
related_specs:
  - ../specs/20261022_infra-credentials-fail-closed.md
---

# IADR-0574: 基盤の資格情報を公知の既定値で作らない（fail-closed。GHSA-rh44-px44-p48q）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: オーナー指示（2026-10-22）・claude（選び方の順序と opt-in の名前）

## 起点・関連

- 起点: GHSA-rh44-px44-p48q（多層防御の強化。既定を fail-closed にする一連の 2 本目）。
- 計画: **ADR-0124 決定 1**。要求 **NFR-18**。
- 前提: [IADR-0485](./IADR-0485_paired-secrets-create-only-declarations.md)（無いときだけ作る）・[IADR-0286](./IADR-0286_default-credentials-fail-fast.md)（既定の資格情報を埋め込まない）・[IADR-0572](./IADR-0572_dev-secret-guard-all-service-account-clients.md)（1 本目）。

## コンテキストと課題

起動器（`scripts/k8s-local-up.sh`）は Secret `platform-infra/keycloak-admin`・`rabbitmq`（と MSP の `rabbitmq-app`）を、env が無ければ公知の既定値で作り、再実行のたびに書き直していた。Vault の種（`bootstrap.sh`）も、無い KV を同じ既定値で作る。既定を fail-closed にし、開発用の値は明示したときだけにする。

## 検討した選択肢

1. **env を必須にする（既存の値は見ない）** — 再実行のたびに稼働中の値を env で与え直す必要があり、与え忘れ・打ち間違いが稼働中の値の上書き（食い違い）になる。
2. **初回だけ乱数で作る（セッションストアと同じ形）** — Keycloak の管理者・ブローカの値は既存の利用者（AST のチャート・運用の手順）が読むので、知らない値が生まれると別の箇所が壊れる。指示（未指定なら止まる）とも違う。
3. **明示 ＞ 既存の値（開発用でないもの）＞ 止める、開発用の値は opt-in（採用）**。

## 決定

1. 判定は `scripts/lib/infra-credentials.sh` 1 本に置く。対象は `KEYCLOAK_ADMIN_PASSWORD`（Secret `keycloak-admin/password`）と `RABBITMQ_PASSWORD`（Secret `rabbitmq/password`。`rabbitmq-app` も同じ値）。利用者名は対象外。
2. 選び方: **明示（env）＞ 既存の値 ＞ 止める**。明示・既存の値が開発用の値と同じなら「指定なし」と同じに扱う。
3. 明示の opt-in **`ALLOW_DEV_INFRA_CREDENTIALS=1`**（`1` だけ）でだけ開発用の値を許す。用途は使い捨ての開発環境（手元の検証・CI の使い捨てスタック）に限り、通すときは大きく警告する。CI の `integration-stack` / `cutover-rehearsal` の起動器の呼び出しはこれを付ける。
4. 適用点: 起動器は **[1/7] の直後**（既存の Secret を読むのでクラスタが要る・Secret を 1 つも書く前・[2/7] の長いビルドの前）で決め、選んだ値を export する（[3/7]・[5/7]・ESO の段の `bootstrap.sh` が同じ値を使う）。`bootstrap.sh` は **Vault へ何か書く前**に、まだ無い KV（`msp/keycloak-admin`・`msp/rabbitmq`・`msp/rabbitmq-app`）に対応する env だけを、明示の値で判定する（在る KV は触らない）。
5. 値は比較と変数にしか使わず、出力しない。

## 結果

- 良い影響: 既定の実行で公知の値の管理者・ブローカの資格情報が作られない。再実行は既存の Secret の値を使うので、稼働中の値を戻さない。
- 悪い影響: 手元の初回の起動に opt-in か env が要る（止めたときのメッセージが対処を告げる）。既存の Secret が開発用の値のままのクラスタでは、次の起動器の実行が止まる（入れ替えの後に配備する）。

## 残余

1. `postgres` 等のほかの基盤の資格情報は従来どおり（今回の範囲外）。
2. `deploy/docker-compose.yml` の手元の経路は対象外（kube context を持たない）。

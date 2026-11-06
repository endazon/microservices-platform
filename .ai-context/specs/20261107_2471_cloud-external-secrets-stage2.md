---
title: 作業仕様書 — クラウドの外から持ち込む値を OpenBao から写し（put-external-secret.sh）、クラウドの段 2 の切り替えを強制にする（#2471 の PR ②）
type: spec
status: in-progress
related_ids: [NFR-05, NFR-18, IADR-0689, IADR-0681, IADR-0677, IADR-0678, IADR-0684]
author: claude
created: 2026-11-07
updated: 2026-11-07
issue: "#2471"
related_specs:
  - 20261106_2471_cloud-openbao-eso.md
---

# 作業仕様書 — 外から持ち込む値と段 2 の強制（#2471 の PR ②）

> 着手 2026-11-07（develop `e9e01524`。PR ①〔#2474〕のマージの後）。判断の記録は IADR-0689 への日付つきの追記（決定 11・決定 12）。稼働中のクラスタには何もしない。秘密の値は扱わない。

## 起点

- 要求 **NFR-05**・**NFR-18**。AST の計画 ADR-0080 決定 1（区分 1: 外から持ち込む値も OpenBao から供給する）。
- 前の段: PR ①（#2474）。作業仕様書 `20261106_2471_cloud-openbao-eso`。IADR-0689。
- 統括の決定（2026-11-06）: クラウドでは段 1 の直しを段 2 にする（移行の門の条件）。InternalGate・BFF の流量制限・ローカルだけの切り替えは入れない。

## 母集合（規則 9）

### 外から持ち込む値（区分 1）

`secret-inventory.js` の区分 1 と、取引ユニットの values-cloud（AST#1564 の `deploy/helm/ai-stock-trading/values-cloud.yaml` と `templates/external-secrets.yaml`）から引いた。

| 値 | OpenBao のパス | キー | 写す先 | ExternalSecret の持ち主 |
| --- | --- | --- | --- | --- |
| LLM の提供元の鍵 | `cloud/msp/llm-provider-credentials` | `anthropic-api-key`（任意 `voyage-api-key`） | `microservices-platform/llm-provider-credentials` | MSP（本 PR） |
| バックアップの保管先の鍵 | `cloud/platform/platform-backup-s3` | `access-key-id`・`secret-access-key` | `platform-infra/platform-backup-s3` | MSP（本 PR） |
| 警報の Discord の webhook | `cloud/platform/alertmanager-discord` | `webhook-url` | `platform-infra/alertmanager-discord` | MSP（本 PR） |
| 外の監視の ping の URL | `cloud/platform/alertmanager-heartbeat` | `ping-url`・`discord-ping-url`・`discord-fail-url` | `platform-infra/alertmanager-heartbeat` | MSP（本 PR） |
| moomoo のログインの資格 | `cloud/ast/moomoo-credentials` | `login-account`・`login-pwd-md5` | `ai-stock-trading/moomoo-credentials` | AST |
| OpenD の RSA の鍵（区分 2。OpenBao の中で作れないので手元から流す） | `cloud/ast/moomoo-rsa` | `opend_rsa.pem`（PKCS#1 の PEM・1024 ビット） | `ai-stock-trading` の OpenD の RSA の Secret | AST |
| オーナーの TOTP | `cloud/ast/ast-owner-totp` | `secret` | `ai-stock-trading/ast-owner-totp` | AST |
| Discord の 3 つ | `cloud/ast/ast-secrets` | `discord-bot-token`・`discord-webhook-url`・`discord-bot-killswitch-phrase` | `ai-stock-trading/ast-secrets` | AST |
| 情報源の 5 つ | `cloud/ast/ast-secrets` | `finnhub-api-key`・`marketdata-finnhub-api-key`・`fred-api-key`・`edinet-subscription-key`・`sec-edgar-user-agent` | 同上 | AST |

- `cloud/ast/ast-secrets` の `order-approval-signing-key` は区分 2（PR ① の `gen`）。
- 除外: `cloud/shared/*`（区分 2。`seed-secrets.sh` が作る）。

### 段 2 の切り替え（deploy/cloud に乗るもの）

PR ① の作業仕様書の表を引き直した（develop `e9e01524`）。乗るのは 4 つで、IADR-0689 決定 12 に表で書いた。

| 切り替え | クラウドの値 | 既定・ローカル |
| --- | --- | --- |
| ブローカの権限（IADR-0681） | narrow（生成物 `deploy/cloud/infra/rabbitmq-permissions.yaml`） | 変えない（ローカルの起動器の段のまま） |
| `Llm__RouteValidation__Mode`（IADR-0677） | `Fail` | 変えない（段 1） |
| `Llm__CallerPolicy__Mode`（IADR-0684） | `Enforce` | 変えない |
| `Llm__CallLimits__Mode`（IADR-0678） | `Enforce` | 変えない |

- `Fail` で起動が止まらないことの確かめ: 配備の `appsettings.json` を検査の規則（未知のモデル・空の宛先・単価の欠落）に当て、0 件だった（2026-11-07）。

## 方式

1. `deploy/cloud/vault/put-external-secret.sh`（新規）: 呼び方と終了コードは IADR-0689 決定 11。取引ユニットの仮の呼び方（`AST_PUT_EXTERNAL_SECRET` の前置き＋「パス」「キー」、値は標準入力）にそのまま合わせる。
2. ExternalSecret 4 つと policy の名指しのパス（IADR-0689 決定 11）。
3. 段 2: `values-cloud.yaml` の `extraEnvAppend`（LLM の 3 つ）。ブローカは起動の直後の復元を narrow だけにし、表を非 optional でマウントする。表は `scripts/gen-rabbitmq-permissions.js --write` が作り、既定の照合が食い違いを止める。
4. 試験: 実物の OpenBao の試験に put の経路（値をそのまま書く・PEM の改行・在るキーで 5・`--overwrite`・`cloud/shared` を拒む・空を拒む・取引ユニットの呼び方・policy の境界・値が引数と作業場に残らない）。描画の試験に段 2（クラウドは段 2・既定とローカルは段 1・陰性対照）。`secret-inventory.js --strict` が全件で通る。
5. 手順書: `docs/operations/cloud-openbao-init-unseal-runbook.md` に「外から持ち込む値」、`docs/operations/cloud-backup-alerts-heartbeat-runbook.md` の Secret の手作りを put へ。`deploy/cloud/README.md` §4 に段 2 の表。値の例は書かない。

## 受け入れ基準

- AC-1: `put-external-secret.sh` は値を標準入力からだけ受け、在るキーは 5 で止まり、`--overwrite` でだけ上書きし、`cloud/shared` へは書かない。値は kubectl の引数・作業場に残らない（実物の OpenBao の試験）。
- AC-2: 取引ユニットの呼び方（前置き＋パス＋キー、値は標準入力）で通る（同上）。
- AC-3: `secret-inventory.js --strict` が全件で通る（試験）。
- AC-4: クラウドの描画は段 2 の 4 つを持ち、既定とローカルの描画は段 1 のまま（描画の試験・陰性対照）。
- AC-5: ブローカの権限の表は生成物と一致し、復元は vhost 全体を当てない（同上）。

## 範囲の外

- 取引ユニットの名前空間の ExternalSecret と手順書（AST）。
- 稼働中のクラスタでの確かめ（移行の門の条件 5）。

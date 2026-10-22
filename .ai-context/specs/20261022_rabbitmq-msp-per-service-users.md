---
title: 作業仕様書 — 基盤の 7 サービスをブローカのサービスごとのユーザーへ切り替え、既定のユーザーを無効化する（IADR-0573 の段 3・GHSA-pvmh-cvmf-pvcp / GHSA-rh44-px44-p48q）
type: spec
status: done
related_ids: [NFR-18, ADR-0027, ADR-0124, IADR-0577, IADR-0573, IADR-0574, IADR-0291, IADR-0286, IADR-0485]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027 （メッセージングは Wolverine ＋ RabbitMQ）
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1
issue: "GHSA-pvmh-cvmf-pvcp / GHSA-rh44-px44-p48q"
---

# 作業仕様書 — 基盤のサービスをブローカのサービスごとのユーザーへ切り替える（段 3）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0577**（新規）に置く。
> 多層防御の強化（共有の資格情報を残さない・最小権限）。参照は GHSA の ID だけとする。
> 🔴 稼働中のクラスタには何も実行しない。試験は PATH のスタブ・状態を持つ模型の kubectl / rabbitmqctl と `helm template` で行う。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR-18**。計画 ADR: **ADR-0027**（メッセージング）・**ADR-0124 決定 1**（対になる秘密）。矛盾しない
  （ブローカの製品・トポロジ・vhost は変えない。KV `msp/rabbitmq-users` の扱いは IADR-0573 のまま）。
- 実装 ADR: **IADR-0577**（新設）。前提 **IADR-0573**（段 1・決定 4 の移行の段）・**IADR-0574**・**IADR-0291**・**IADR-0286**・**IADR-0485**。

## 要件（オーナー指示 2026-10-22・コーディネーター経由）

1. 基盤の 7 サービス（helm の `messaging: true`）を `msp-<名前>` のユーザーへ切り替える。
2. 要るもの: MSP の名前空間の ExternalSecret・chart の `global.messaging` のサービスごとの形・ESO を使わない経路の扱い・既定のユーザーの無効化の順序。
3. 1 PR。ワークフローを変えるときは起動条件・必須チェックを変えない。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| A1 | 既定・values-local の描画で、ブローカへ繋ぐ Deployment の集合が users.txt の基盤の行と一致し、各ユーザーは `msp-<名前>`、パスワードは Secret `msp-rabbitmq-users` の同名のキー（非 optional・リテラルなし） | `helm-broker-per-service-users.test.js` |
| A2 | 旧キー（`user` / `existingSecret` / `passwordKey`）・新キーの欠落は描画で止まる。描画物に `rabbitmq-app` / `guest` の接続が無い | 同上 |
| A3 | ESO=1: ExternalSecret `msp-rabbitmq-users`（Owner・KV `msp/rabbitmq-users`）を apply し、手動では作らない。旧 `rabbitmq-app` はどちらの経路でも作らない | `k8s-local-up.test.js`（IADR-0577） |
| A4 | ESO なし: 起動器が `rabbitmq-users`（全ユーザー）と `msp-rabbitmq-users`（基盤の 7）を同じ値で作り、helm の前にブローカへ合わせる。既存の値を保ち、無いユーザーには乱数（48 桁の 16 進・ユーザーごとに別）。値は出力・引数に出ない | 同上 |
| A5 | 旧 `rabbitmq-app` は helm の後に ExternalSecret → Secret の順で片付ける | 同上 |
| A6 | 無効化: 全ユーザーが認証でき、既定のユーザーの接続が無いときだけ、印を先に置いてから消す。接続が在れば exit 3・欠けがあれば exit 1（どちらも印を置かない）。`--check` は読むだけ | 同上（状態を持つ模型） |
| A7 | 起動器は最後に無効化を試み、できなければ WARN で通す | 同上 |
| A8 | ブローカの再起動の直後に Secret の全ユーザーを標準入力の値で作り直す（権限・空のタグ）。印があり全員揃えば既定のユーザーを消す。揃わない・Secret が空・印が無いなら消さない。起動を待てなくても 0 で終わる | 同上（manifest の postStart の本文を抜き出して実行） |
| A9 | Vault の種は旧 `msp/rabbitmq-app` を作らない。SC-22 の `excluded[]` から外す（`deferred ∪ excluded` = 無いときだけ作るパスの集合、は C# 試験が固定） | `scripts.repo.test.js`・`SecretItemBootstrapSeedTests`（CI） |

## 母集合（規則 9・10。誤りの側の文字列で全文書を走査）

- `rabbitmq-app`: `git grep -n rabbitmq-app -- ':!.ai-context' ':!src/ai-stock-trading' ':!CHANGELOG.md'` → chart の values・ExternalSecret・
  起動器（[5/7]・ESO の段・msp_es・コメント）・Vault の種（判定・種・確認の表示）・SC-22 の `excluded[]`・`deploy/local/README.md` の表・
  `deploy/local/vault/eso/README.md` の表・runbook 2 本（群 2 の列挙と件数・同値の組・ブローカの行と注意）・`scripts/lib/infra-credentials.sh` のコメント・
  試験（k8s-local-up.test.js・scripts.repo.test.js）。全件を直した。残るのは「旧〜の後継」「撤去した」と書く行と、撤去を固定する試験だけ。
- `global.messaging.user` / `existingSecret`: chart・起動器のコメント・README の表 → 直した。サービスのコード（`Program.cs` の
  「helm の global.messaging が注入する」）は今も正しいので変えない。
- 件数の導出（規則 10）: SC-22 の `excluded[]` は 7 → 6（runbook 2 本の件数を直した）。起動器の MSP ns の ExternalSecret は 18 のまま（置き換え）。
- 除外: `.ai-context/`（凍結記録）・`CHANGELOG.md`（生成物）・AST の submodule（段 2 は AST 側で済み）・`deploy/docker-compose.yml`（k8s の経路だけを変える。IADR-0577 残る制約 4）。

## 実装

- chart: `values.yaml` の `global.messaging`、`templates/deployment.yaml` のブローカ接続（旧キーと欠落は fail）。
- 供給: `externalsecret-msp-rabbitmq-users.yaml`（新規）・`externalsecret-rabbitmq-app.yaml`（削除）・`bootstrap.sh`・`sc22-secret-items.json`。
- 起動器: ESO なしの Secret の作成（関数 `rabbitmq_users_secrets_without_eso`）と合わせ・旧 rabbitmq-app の片付け・ESO の段・最後の無効化。
- ブローカ: `rabbitmq.yaml` に postStart と ConfigMap `rabbitmq-boot`（復元）・マウント（`rabbitmq-users` と印は optional）。
- 新規スクリプト: `deploy/local/rabbitmq-setup/disable-default-user.sh`。
- 文書: `deploy/local/README.md`・`deploy/local/vault/eso/README.md`・`docs/operations/secret-rotation-runbook.md`・`docs/operations/paired-secret-rotation-runbook.md`。
- CI: `ci.yml` の static-checks-units に helm の試験の段を 1 つ足す（起動条件・必須チェックは不変）。

## 配備前の手順（PoC。値は書かない）

PR 本文に順序つきで書く。要点: KV `msp/rabbitmq-users` に基盤の 7 ユーザーが在ること → ExternalSecret `msp-rabbitmq-users` の同期 →
reconcile-users.sh で合わせる → chart を配備（7 サービスが切り替わる）→ 取引ユニットが段 2 以降の版であることを確かめる →
ブローカの manifest を配備（再起動を伴う）→ disable-default-user.sh。配備は閉場後。

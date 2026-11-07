---
title: 運用 Runbook — クラウドの取引ユニットの DB の利用者をサービスごとに分ける（新しく作る・稼働中の構成を 3 段で移す・切り戻し）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-18, NFR-05]
iadrs: [IADR-0695, IADR-0689, IADR-0674, IADR-0693]
specs: [20261107_cloud-db-per-service-users]
-->

# 運用 Runbook: クラウドの DB の利用者をサービスごとに分ける

> 対象はクラウドの最小構成（単一ノードの k3s・`deploy/cloud/`）の Postgres と、それを使う取引ユニットの 7 つのサービス
> （audit・configuration・cost-control・market-monitor・order-execution・report・risk-management）。
> 秘匿管理の手順（unseal・root の作り直し・値の作成）は[秘匿管理の手順書](cloud-openbao-init-unseal-runbook.md)にある。
>
> **状態を `draft` に留めている理由**: クラウドのノードで移行をまだ流していない（移行の門の稼働の確かめで記録する）。

## 形（何がどう変わるか）

| 項目 | 共有の構成（旧） | サービスごとの構成（最終の段） |
| --- | --- | --- |
| 利用者 | 1 人（OpenBao `cloud/shared/ast-db`）が 7 つの DB を OWNER で持つ | 7 人（`cloud/shared/ast-db-<サービス>`）。各人が自分の DB と中の物だけを OWNER で持つ |
| CONNECT | 既定（PUBLIC。どの利用者もどの DB にも接続を試みられる） | 各 DB の OWNER だけ（PUBLIC から外す。Keycloak・postgres・template1 の DB も） |
| 取引ユニットの資格 | 7 つのサービスが同じ Secret を読む | 各サービスが自分の Secret を読む |
| 利用者を作る口 | Postgres の初期化（initdb） | Job `postgres-ast-db-users`（`deploy/cloud/db-users/ast-db-users.sh`）。手順は ConfigMap `postgres-ast-db-users` |

- スクリプトはどれも `--live` が無ければ何もしない。**値（利用者名・パスワード）は手元へ来ない**（Job の Pod が Secret から読む）。
- Job のログはサービスの名前と確かめの結果だけを出す。最後に「7 つのサービスの利用者は自分の DB と中の物だけの OWNER で、自分の DB にだけ CONNECT できる」が出れば成功。

## 1. 新しく作る構成

`deploy/cloud/README.md` §3 の順のままでよい（`seed-secrets.sh` が 7 組も作り、`kubectl apply -k deploy/cloud` の後に `ast-db-users.sh --live apply` を流す）。
取引ユニットを入れた後（取引ユニットの全サービスが自分の資格で Ready になった後）に §2 の段 ③ を流して共有の利用者を消す。

## 2. 稼働中の構成を移す（3 段）

🔴 **順を守る。** 段 ② の前に段 ③ を流すと、まだ共有の資格で動く取引ユニットの Pod が DB に入れなくなる（段 ③ は接続が残っていれば止まるが、
接続していない Pod の再起動で落ちる）。各段の後に「確かめ」を見てから次へ進む。取引の時間帯の外（JST 07:00〜21:00）に流す（段 ① は表ごとに短いロックを取る）。

### 段 ①: MSP（policy・値・ESO・利用者の作成）

```sh
bash deploy/cloud/vault/root-token.sh --live --init-file <init>.age --operator-file <operator>.age --identity <age の識別子> apply-policies
bash deploy/cloud/vault/seed-secrets.sh --live --operator-file <operator>.age --identity <age の識別子>      # 在る値は触らない。7 組だけが増える
kubectl apply -k deploy/cloud/vault/eso
kubectl -n platform-infra wait --for=condition=Ready externalsecret --all --timeout=300s
node deploy/cloud/secret-inventory.js --strict
kubectl apply -k deploy/cloud                                          # ConfigMap postgres-ast-db-users と Postgres への入り（Job の Pod）
bash deploy/cloud/db-users/ast-db-users.sh --live apply
```

- 確かめ: seed の出力に「作った: cloud/shared/ast-db-<サービス>」が 7 つ（流し直しなら「在る」）。`ast-db-users.sh` の出力の最後に「確かめた: …」と「OK: apply を終えた」。
  取引ユニットのサービスが今までどおり動く（移行の間は共有の利用者が 7 つの利用者の役割の一員で、旧の Pod は読み書き・マイグレーションを続けられる）。
- `kubectl apply -k deploy/cloud` は Postgres の Deployment を変えない（作り直されない）。

### 段 ②: 取引ユニット（各サービスが自分の資格を読む）

取引ユニットのリポジトリの手順で chart を当てる（values-cloud で、7 つのサービスがそれぞれの Secret を読む。ExternalSecret が 7 つ増える）。

- 確かめ: 取引ユニットの 7 つのサービスが Ready で、旧の ReplicaSet の Pod が残っていない（`kubectl -n ai-stock-trading get pods`）。
  共有の資格で繋ぐ Pod が残っているかは、段 ③ が接続を数えて確かめる（残っていれば止まる）。

### 段 ③: 共有の利用者を消す（最終）

```sh
bash deploy/cloud/db-users/ast-db-users.sh --live drop-shared
```

- 共有の利用者の接続が 1 本でも残っていれば、何も消さずに止まる（終了 4。段 ② を確かめ直す）。新しい接続を止めてから数え直し、残っていれば元に戻して止まる。
- 確かめ: 出力に「消した: 共有の利用者」と「OK: drop-shared を終えた」。流し直すと「共有の利用者は既に無い」。
- この後に、宣言から共有の利用者（seed・ESO・policy・inventory の `ast-db`、取引ユニットの `ast-db-credentials`）を外す PR を入れる。OpenBao の
  `cloud/shared/ast-db` と Secret `ast-db`（Retain）は、その PR の後に残っていても使われない。

### 切り戻し

| いつ | 戻し方 | 戻した後の形 |
| --- | --- | --- |
| 段 ① の後 | 戻さなくてよい（共有の利用者は 7 つの役割の一員で、旧の Pod は動く） | 共有の資格で動いたまま、OWNER だけがサービスの利用者 |
| 段 ② の途中・後 | 取引ユニットの chart を前の版で当て直す（取引ユニットの手順） | 共有の資格へ戻る（段 ③ の前なら共有の利用者は在る） |
| 段 ③ の後 | 共有の利用者へは戻さない。サービスの利用者の資格に問題があれば、段 ① の `apply` を流し直す（パスワードを OpenBao の値へ合わせ直す） | サービスごとのまま |

- 🔴 **段 ③ の後に、取引ユニットの chart を共有の資格の版へ戻さない。** 共有の利用者が無いので、戻した Pod は DB に入れない。

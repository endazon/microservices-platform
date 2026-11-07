---
title: 運用 Runbook — クラウドの基盤の資格の作り直し（OpenBao の値と消費側の反映を対で流す）・Postgres の超利用者の受け付けの絞り込み・ブローカの口の絞り込み
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-18, NFR-05]
iadrs: [IADR-0706, IADR-0689, IADR-0695, IADR-0700, IADR-0674, IADR-0577]
specs: [20261107_cloud-data-broker-hardening]
-->

# 運用 Runbook: クラウドの基盤の資格の作り直し

> 対象はクラウドの最小構成（単一ノードの k3s・`deploy/cloud/`）の Postgres・RabbitMQ・Keycloak と、OpenBao の `cloud/platform/*`・`cloud/shared/*`。
> 秘匿管理の手順（unseal・オーナー用の資格・policy の当て直し）は[秘匿管理の手順書](cloud-openbao-init-unseal-runbook.md)にある。
> 取引ユニットの DB の利用者をサービスごとに分ける手順は[DB の利用者の手順書](cloud-db-per-service-users-runbook.md)にある。
>
> **状態を `draft` に留めている理由**: クラウドのノードで §1 の当て方と §3 の作り直しをまだ流していない（移行の門の稼働の確かめで記録する）。

- スクリプトはどれも `--live` が無ければ何もしない。**値は手元へ来ない**（OpenBao の Pod・Job の Pod・Postgres の Pod の中で読む）。出力に出るのは名前と真偽と件数だけ。
- 以下の `S` はオーナー用の資格つきの値の作り直しの入口である:

  ```sh
  S="bash deploy/cloud/vault/seed-secrets.sh --live --operator-file <operator>.age --identity <age の識別子>"
  sync() { kubectl -n "$1" annotate externalsecret "$2" force-sync="$(date +%s)" --overwrite; }   # ESO にすぐ写させる
  ```

## 形（どの値を、どこで作り直し、どこへ反映するか）

| 値 | OpenBao（作り直し） | 写す先（ESO） | 消費側への反映 |
| --- | --- | --- | --- |
| Keycloak の機密クライアントの secret | `$S rotate cloud/shared/ast-client-secrets <clientId>` | `keycloak-realm-import`（基盤）と取引ユニットの側の Secret | `keycloak-clients.sh --live sync-secrets`（稼働の realm の secret を合わせる）→ 取引ユニットのそのクライアントを使うサービスを作り直す |
| Keycloak の DB の利用者 | `$S rotate cloud/platform/keycloak-db password` | `keycloak-db` | `ast-db-users.sh --live platform`（DB の中のパスワードを合わせる）→ Keycloak を作り直す |
| バックアップの利用者 | `$S rotate cloud/platform/postgres-backup password` | `postgres-backup` | `ast-db-users.sh --live platform`（次の回の CronJob は新しい値を読む） |
| Postgres の超利用者 | `$S rotate cloud/platform/postgres password` | `postgres` | `rotate-superuser.sh --live check` → `… apply`（Pod の中のソケットで合わせる） |
| 取引ユニットの DB のサービスごとの利用者 | `$S rotate cloud/shared/ast-db-<サービス> password` | `ast-db-<サービス>` | `ast-db-users.sh --live apply`（従来どおり）→ そのサービスを作り直す |
| ブローカのサービスごとのユーザー | `$S rotate cloud/shared/ast-rabbitmq-users <利用者名>` | `rabbitmq-users` | ブローカを作り直す（起動の直後の復元がパスワードを合わせる）→ そのサービスを作り直す |

- `rotate` は在るキーだけを Pod の中の乱数で作り直し、ほかのキー（利用者名・ほかのクライアント）は触らない。キーが無ければ止まる（作るのは `seed`）。
- **OpenBao の値を作り直しただけでは、Keycloak・Postgres の中の値は変わらない。** 表の右端の反映まで流して初めて作り直しが終わる。
  反映の前に消費側（取引ユニット・Keycloak）を作り直すと、新しい値で名乗って拒まれる。**順は表の左から右**である。
- 作り直しは米国の場の時間外に行う。1 回に 1 つの値だけを作り直し、反映を確かめてから次へ進む。

## 1. この版を稼働中の構成へ当てる（一度だけ）

この版は次の 3 つを変える。どれも、取引ユニットとの間で届いている通信は変えない。

- Postgres の超利用者は TCP で名乗れない（既定で拒む）。超利用者で TCP から入るのは、利用者を作る Job だけで、その入口（`ast-db-users.sh --live`）が
  Job の間だけ受け付けを開ける（Pod の中のゲート。Pod が作り直されれば閉じる）。バックアップは専用の利用者（全データを読めるだけ）で入る。
- ブローカは平文（5672）を受けず、管理（15672）と計測（15692）の受け口は Pod の中（ループバック）だけ。
  入りの制限（NetworkPolicy）は以前から TLS（5671）だけを通している。管理の画面を開くときは `kubectl -n platform-infra port-forward deploy/rabbitmq 15672`。
- ブローカの既定のユーザーの「揃わなかったときの扱い」を段にした（§4）。この版の既定は従来どおり（残して警告の行を出す）。

順:

```sh
$S                                                                  # 1. バックアップの利用者の値を作る（在る値は触らない。cloud/platform/postgres-backup を足す）
bash deploy/cloud/vault/root-token.sh --live --init-file <init>.age --operator-file <operator>.age --identity <age の識別子> apply-policies
                                                                    # 2. 基盤の側の ESO が新しいパスを読めるよう policy を当て直す
kubectl apply -k deploy/cloud/vault/eso                             # 3. ExternalSecret postgres-backup を足す
kubectl -n platform-infra wait --for=condition=Ready externalsecret/postgres-backup --timeout=300s
kubectl apply -k deploy/cloud                                       # 4. Postgres・RabbitMQ は作り直される（Recreate。数十秒、取引ユニットは繋ぎ直すまで待つ）
kubectl -n platform-infra rollout status deploy/postgres deploy/rabbitmq --timeout=600s
bash deploy/cloud/db-users/ast-db-users.sh --live platform          # 5. バックアップの利用者を DB の中に作る（ゲートを開け、終われば閉じる）
kubectl -n platform-infra create job --from=cronjob/platform-backup-postgres platform-backup-postgres-check
                                                                    # 6. バックアップの回を 1 つ流し、完了（COMPLETE）を確かめる（バックアップの手順書 §4）
```

- 4 と 5 の間にバックアップの時刻（JST 12:00）を挟まない（挟むと、その回は利用者が無くて失敗する。5 の後に 6 で流し直せばよい）。
- 確かめ:
  - `kubectl -n platform-infra exec deploy/postgres -c postgres -- sh /etc/postgres-hba/superuser-tcp-gate.sh status` が `superuser-tcp: closed`。
  - `kubectl -n platform-infra logs deploy/rabbitmq | grep rabbitmq-boot` の「サービスごとのユーザーを復元した: 11 件（失敗 0 件…）」。
  - 取引ユニットの全サービスが Ready（ブローカ・DB へ繋ぎ直している）。
- **ゲートの開閉の確かめと、閉じられないとき**:
  - ゲートは開けてから上限（既定 900 秒＝利用者を作る Job の上限 600 秒＋余裕）を過ぎると、Pod の中の閉じ手が閉じる（入口との通信が切れても閉じる）。
  - 入口（`ast-db-users.sh --live`）は終わりに閉じて `status` で確かめる。確かめられなかったときは `ERROR: ゲートを閉じたことを確かめられない` で 1 を返す。
    そのときは、届くようになってから次を流す:

    ```sh
    kubectl -n platform-infra exec deploy/postgres -c postgres -- sh /etc/postgres-hba/superuser-tcp-gate.sh close
    kubectl -n platform-infra exec deploy/postgres -c postgres -- sh /etc/postgres-hba/superuser-tcp-gate.sh status   # superuser-tcp: closed
    ```

  - `kubectl exec` が通らないまま上限を待てないときは、Postgres の Pod を作り直す（ゲートのファイルはメモリ〔emptyDir〕に在り、作り直すと消えて閉じる。
    場の時間外に。取引ユニットは繋ぎ直すまで待つ）:

    ```sh
    kubectl -n platform-infra rollout restart deploy/postgres
    kubectl -n platform-infra rollout status deploy/postgres --timeout=600s
    kubectl -n platform-infra exec deploy/postgres -c postgres -- sh /etc/postgres-hba/superuser-tcp-gate.sh status   # superuser-tcp: closed
    ```

  - 開いている間の `status` は `superuser-tcp: open（残り N 秒）` を返す。作り直し（§3）の作業の後と、日々の点検で `closed` であることを確かめる。
- 戻すときは前の版の `deploy/cloud` を当てる（Postgres・RabbitMQ は作り直される）。前の版のバックアップは超利用者で入る。

## 2. 新しく作る構成

`deploy/cloud/README.md` §3 の順のままでよい（`seed-secrets.sh` が `cloud/platform/postgres-backup` も作り、`ast-db-users.sh --live apply` の後に
`… --live platform` を流す）。

## 3. 作り直し

### Keycloak の機密クライアントの secret

```sh
$S rotate cloud/shared/ast-client-secrets <clientId>
sync platform-infra keycloak-realm-import                           # 取り込み元（Keycloak の Pod と Job が読む）
bash deploy/cloud/keycloak-clients/keycloak-clients.sh --live sync-secrets   # 稼働の realm の secret を取り込み元へ合わせる（違うものだけ書く）
```

- ログの「合わせた: クライアント <clientId> の secret」が 1 件で、ほかは「同じ」であることを確かめる。
- その後に、取引ユニットの側の ExternalSecret を同期し、そのクライアントを使うサービスを作り直す（取引ユニットの手順書）。
  合わせてから作り直すまでの間、そのサービスの新しいトークンの取得は拒まれる（取得済みのトークンは期限まで使える）。
- Keycloak の `--import-realm` は在る realm を取り込み直さないので、Keycloak を作り直しても secret は変わらない。反映は `sync-secrets` だけが行う。

### Keycloak の DB の利用者

```sh
$S rotate cloud/platform/keycloak-db password
sync platform-infra keycloak-db
bash deploy/cloud/db-users/ast-db-users.sh --live platform          # DB の中のパスワードを Secret の値へ合わせる
kubectl -n platform-infra rollout restart deploy/keycloak            # Keycloak は起動の時に読む
kubectl -n platform-infra wait --for=condition=Ready pod -l app=keycloak --timeout=600s
```

- 合わせてから Keycloak が作り直されるまで、Keycloak の新しい DB の接続は拒まれる（張ってある接続は使える）。間を空けない。

### バックアップの利用者

```sh
$S rotate cloud/platform/postgres-backup password
sync platform-infra postgres-backup
bash deploy/cloud/db-users/ast-db-users.sh --live platform
```

- 次の回の CronJob は新しい値を読む。すぐに確かめるときは §1 の 6 と同じく 1 回流す。

### Postgres の超利用者

```sh
$S rotate cloud/platform/postgres password
sync platform-infra postgres
bash deploy/cloud/db-users/rotate-superuser.sh --live check          # 3（合わせる前）になるまで待つ。kubelet が Pod のファイルを書き換えるまで 1〜2 分
bash deploy/cloud/db-users/rotate-superuser.sh --live apply          # Pod の中のソケットで、Secret の値へ合わせる（Postgres は作り直さない）
bash deploy/cloud/db-users/rotate-superuser.sh --live check          # 0（合っている）
```

- 超利用者を使うのは、利用者を作る Job（実行のたびに Secret を読む）と Postgres の Pod の中の手順だけである。合わせた後に作り直す消費側は無い。
- Postgres の Pod の env は起動の時の値のままである。**次の作り直しの前に** Postgres を作り直す（`kubectl -n platform-infra rollout restart deploy/postgres`。
  場の時間外に。取引ユニットは繋ぎ直すまで待つ）。作り直さずに 2 回目を流すと、`check` が 4（どちらの値でも入れない）で止まり、何も変えない。
- **戻し方（`check` が 4 のとき）**: OpenBao の KV は版を残す。`cloud/platform/postgres` を前の版へ戻し（オーナー用の資格で OpenBao の Pod の中から
  `bao kv rollback -mount=cloud -version=<前の版> platform/postgres`。版は `bao kv metadata get -mount=cloud platform/postgres` で読む）、
  `sync platform-infra postgres` の後に `check` が 0 か 3 になることを確かめる。0 なら Postgres を作り直してから作り直しを始めからやり直す。
- `rotate` の後、`apply` の前に Postgres を作り直さない（作り直すと env が新しい値になり、DB の中は古い値のままになる＝`check` が 4）。
  そうなったときも上の戻し方で戻せる。

### 取引ユニットの DB の利用者・ブローカのユーザー

従来どおり（Job の `apply` がパスワードを合わせ、ブローカは起動のたびに合わせる）。作り直した値を読むよう、合わせた後にそのサービスを作り直す。

## 4. ブローカの既定のユーザー（段）

- 起動の直後の復元は、サービスごとのユーザーが揃えば既定のユーザーを消す。揃わなかったときの扱いは ConfigMap `rabbitmq-default-user` の
  `on-incomplete` で決まる:

  | 値 | 揃わなかったとき |
  | --- | --- |
  | `retain`（この版の既定・キーが無い・知らない値） | 既定のユーザーを残し、`rabbitmq-boot: WARN: 既定のユーザーを残した` の行を出す |
  | `delete` | 揃わなくても消す（ブローカの操作は `rabbitmqctl` で行い、既定のユーザーを要らない） |

- 切り替えの順: §1 の後に、ブローカの起動の行が「失敗 0 件」であることを 2 回の作り直しにわたって確かめ、`deploy/cloud/infra/rabbitmq.yaml` の
  `on-incomplete` を `delete` へ替える変更をリポジトリに入れてから当てる（`kubectl apply -k deploy/cloud` はリポジトリの値へ戻すので、クラスタで直に書き換えない）。
- 既定のユーザーが残っているかは、値を出さずに数で読む:

  ```sh
  kubectl -n platform-infra exec deploy/rabbitmq -- sh -c 'rabbitmqctl -q list_users | awk -v u="$RABBITMQ_DEFAULT_USER" "\$1 == u" | wc -l'
  ```

  `0` なら消えている。`1` なら残っている（起動の行の理由を読み、サービスごとのユーザーを揃えてからブローカを作り直す）。

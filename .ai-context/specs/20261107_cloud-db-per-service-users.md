---
title: 作業仕様書 — クラウドの取引ユニットの DB の利用者をサービスごとに分け、各利用者が自分の DB だけを持つようにする（最小権限）
type: spec
status: in-progress
related_ids: [NFR-18, NFR-05, IADR-0695, IADR-0689, IADR-0674, IADR-0693]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - 20261107_cloud-keycloak-tls.md
---

# 作業仕様書 — クラウドの DB の利用者をサービスごとに分ける

> 着手 2026-11-07（develop `32c8cda5a`）。判断の記録は IADR-0695。稼働中のクラスタには何もしない。秘密の値は扱わない（値を作るのはスクリプトで、オーナーが流す）。

## 起点

- 要求 **NFR-18**（統制）・**NFR-05**（秘匿は OpenBao）。
- 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」）: DB の利用者をサービスごとに分け、最小権限にする。ブローカは既にサービスごとのユーザー（取引ユニットの IADR-0568）で、その DB 版。
- 前提: IADR-0689（OpenBao・ESO・値の置き場と読み手の境界）、IADR-0674（クラウドの最小構成）、IADR-0693（稼働中の構成を段で移す型）。

## 現状（develop `32c8cda5a` で読んだ）

| 場所 | 現状 |
| --- | --- |
| `deploy/cloud/infra/postgres.yaml` の `01-create-databases.sh` | 1 人の利用者（`AST_DB_USER`。Secret `ast-db`）が取引ユニットの 7 つの DB を OWNER で持つ。DB の CONNECT は既定（PUBLIC）のまま |
| `deploy/cloud/vault/seed-secrets.sh` | `cloud/shared/ast-db` の 1 組（`ast_<乱数>`・24 バイトのパスワード） |
| `deploy/cloud/vault/eso/platform-infra.yaml` | ExternalSecret `ast-db`（`shared/ast-db`） |
| `policies/platform-infra-eso.hcl`・`ast-eso.hcl` | 両方が `cloud/data/shared/ast-db` を名指しで読む |
| `deploy/cloud/secret-inventory.js` | `platform-infra/ast-db` を区分 2 |
| 取引ユニットの chart（develop `7b237f8fe`） | `global.db.usernameSecret` / `passwordSecret` を DB を持つ 7 つのサービスへ同じ値で描く。ExternalSecret `ast-db-credentials`（`shared/ast-db`） |
| 取引ユニットのマイグレーション | 7 つのサービスとも起動の時に同じ接続で `MigrateAsync`（Npgsql の EF Core 10.0.2。履歴の表の `CREATE TABLE IF NOT EXISTS` と `LOCK TABLE … IN ACCESS EXCLUSIVE MODE` を使う。配布物の字面で確かめた） |

## 母集合（規則 9: 誤りの側の字面で走査した）

- `ast-db` / `AST_DB` / `ast_user` / `shared/ast-db` を `git grep`（`src/`・`.ai-context/specs` を除く）で走査した。
  - 変える: `infra/postgres.yaml`（触らない理由は方式 3）・`vault/seed-secrets.sh`・`vault/eso/platform-infra.yaml`・`policies/platform-infra-eso.hcl`・`policies/ast-eso.hcl`・
    `secret-inventory.js`・`vault/openbao-scripts.test.sh`・`scripts/scripts.repo.test.js`（policy の名指しの集合）・`deploy/cloud/README.md`（§3・§4）・`docs/operations/cloud-openbao-init-unseal-runbook.md`（seed の 7 種）。
  - 変えない: `IADR-0689` の表（凍結記録。IADR-0695 が後継の表を持つ）・`.github/workflows/cutover-rehearsal.yml`（`neg-ast-db` はローカルの棚卸しの陰性の対照で、クラウドの利用者とは別）・
    `scripts/backup-restore-drill.sh`（DB の名前の一覧。利用者は超利用者）・`scripts/deploy-cloud-postgres.test.js` の初期化の env（共有の利用者は移行の間は残すので、初期化はそのまま）。
- DB を持つ取引ユニットのサービス: `infra/networkpolicy.yaml` の `postgres-ingress-clients` の 7 つ（audit・configuration・cost-control・market-monitor・order-execution・report・risk-management）と、
  初期化の 7 つの DB が 1 対 1（`<サービス>_svc` の `-` を `_` にしたもの）。

## 方式

1. 値: `seed-secrets.sh`（既定の seed）が `cloud/shared/ast-db-<サービス>`（7 つ。`username` は `ast_<サービス>_<乱数>`・`password` は 24 バイト）を作る。在れば触らない。
   `cloud/ast/` ではなく `cloud/shared/` に置くのは、Postgres の側（基盤の ESO）がパスワードを読んで利用者を作るから（IADR-0689 決定 6 の境界）。`gen` は 1 つのキーしか作れないので使わない。
2. policy: `platform-infra-eso` と `ast-eso` に 7 つのパスを名指しで足す（ワイルドカードにしない）。取引ユニットの SecretStore は 1 つなので、OpenBao の側ではサービスごとに絞れない
   （絞るには名前空間に 7 つの ServiceAccount・role・SecretStore が要る。Pod は ExternalSecret が書いた Secret を secretKeyRef で読むだけなので、ExternalSecret の単位で十分。IADR-0695 決定 3）。
3. 利用者の作成と OWNER の移し替え: `deploy/cloud/db-users/`。SQL は ConfigMap `postgres-ast-db-users` の 1 つに持ち、Job（`ast-db-users.sh --live apply|drop-shared` が作って待って消す）が
   超利用者で TLS（verify-full）で流す。新しく作る構成も同じ Job を流す（README §3）。`01-create-databases.sh`（initdb）は変えない: 変えると Postgres の Deployment の env が変わり、
   稼働中の Postgres が作り直される（Recreate）。値は initdb の env ではなく Job の env からだけ読む。
   - `apply`（冪等）: 7 人を作る（在ればパスワードを OpenBao の値に合わせる。LOGIN・NOSUPERUSER・NOCREATEDB・NOCREATEROLE・NOREPLICATION・NOBYPASSRLS）。7 つの DB の OWNER を移す（DB を先に全部移し、
     その後に DB ごとに `REASSIGN OWNED BY <共有> TO <サービス>`。順を逆にすると、DB は共有の対象なので 1 回目の REASSIGN が 7 つの DB を全部 1 人へ移す）。接続を許す全 DB（template1・postgres・keycloak を含む）の
     CONNECT・TEMPORARY を PUBLIC から外す。移行の間は共有の利用者を各サービスの利用者の役割の一員にする（旧の Pod が動き続ける）。最後に「どの利用者がどの DB に CONNECT できるか」の表を出し、自分の DB 以外に出れば失敗。
   - `drop-shared`（最終の段・冪等）: 共有の利用者の接続が残っていれば止める。DB ごとに残りを REASSIGN し、DROP OWNED、役割の一員から外し、DROP ROLE。
   - 値は SQL の引数・ファイルに書かない（psql の `\getenv` で env から読む）。セッションで `log_statement=none`・`log_min_error_statement=panic`・psql の `VERBOSITY terse`（パスワードを含む文がサーバのログ・Job のログに出ない）。
4. 段（手順書 `docs/operations/cloud-db-per-service-users-runbook.md`）: ① MSP（policy・seed・ESO・`apply`）→ ② 取引ユニット（chart。各サービスが自分の Secret を読む）→ ③ `drop-shared`。
   共有の利用者の宣言（seed・ESO・policy・inventory・取引ユニットの `ast-db-credentials`）を外すのは ③ の後の別の PR。
5. 監査の DB の追記だけの権限: 起動の時のマイグレーションが同じ接続で履歴の表を作りロックを取る（INSERT・SELECT だけの利用者ではロックが取れない。実測）。マイグレーション用の OWNER とアプリ用の追記だけの利用者に分ける案は、
   取引ユニットのコード（マイグレーションだけを流す口）と chart（initContainer）と基盤（8 人目・既定の権限）に跨るので、本件では入れず別の作業に残す（IADR-0695 決定 5）。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | seed が 7 つの `cloud/shared/ast-db-<サービス>` を作り（利用者名は `ast_<サービス>_<乱数>`）、2 回目は変えない。値は kubectl の引数・作業場に残らない | `vault/openbao-scripts.test.sh` |
| AC2 | `platform-infra-eso` と `ast-eso` は 7 つを名指しで読める（ワイルドカードなし）。名指しの外の `cloud/shared` は読めない | 同上・`scripts.repo.test.js` |
| AC3 | 基盤の ESO は 7 つの Secret `ast-db-<サービス>` を `shared/ast-db-<サービス>` から写す（Retain）。inventory の区分 2 に供給元がある | `scripts.repo.test.js`（`secret-inventory.js --strict`） |
| AC4 | 実物の Postgres で `apply` が: 7 人を作り、各 DB の OWNER と表の OWNER をその利用者へ移し、各利用者は自分の DB にだけ CONNECT でき、他の DB には入れない。共有の利用者は移行の間も読み書きできる。2 回目も通る（冪等） | `deploy-cloud-postgres.test.js` |
| AC5 | 実物の Postgres で `drop-shared` が: 共有の利用者の接続が残っていれば止め、無ければ消す。消した後もサービスの利用者は自分の DB を読み書きできる。2 回目も通る | 同上 |
| AC6 | 変異: REVOKE を外すと AC4 の CONNECT の確かめが赤になる・共有の利用者を役割の一員にしないと移行の間に旧の利用者が読めなくなる | 同上 |
| AC7 | Job・ConfigMap・NetworkPolicy の宣言: イメージは Postgres と同じ digest・TLS は verify-full・値は secretKeyRef だけ・Postgres への入りは Job の Pod の 5432 だけ | `deploy-cloud.test.js` |
| AC8 | `ast-db-users.sh` は `--live` が無ければ何もせずに 3 で終わり、値を引数に置かない | `scripts.repo.test.js` |

## 実測

- Npgsql の EF Core 10.0.2 の配布物の字面に `LOCK TABLE` と ` IN ACCESS EXCLUSIVE MODE`・`CREATE TABLE IF NOT EXISTS` がある（履歴の表）。
- 実物の Postgres（宣言の digest）で、INSERT・SELECT だけを持つ利用者は `LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE` を
  `permission denied for table __EFMigrationsHistory`、`CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory"` を `permission denied for schema public` で拒まれた。
- 同じ Postgres で、`REASSIGN OWNED` は DB を先に移した後に DB ごとに流すと、その DB の中の物だけを移す（表・連番・スキーマ）。共有の利用者を役割の一員にすると、
  OWNER を移した後も共有の利用者は表を読み書きでき、表も作れる（作った表は共有の利用者の物になる。drop-shared が移す）。
- `has_database_privilege(<サービスの利用者>, <他の DB>, 'CONNECT')` は PUBLIC から外した後に false、接続は `User does not have CONNECT privilege` で拒まれる。Keycloak の利用者（OWNER）は自分の DB に入れる。

## 記録

- 方式 3 の順の注記: DB の OWNER の移し替えを REASSIGN の後に回しても最後の形は同じ（ALTER DATABASE が直す）。先に移すのは途中で 1 人が 7 つの DB を持つ間を作らないためで、
  変異の試験には使わない（検出できない）。変異は「CONNECT を外さない」「共有の利用者を役割の一員にしない」の 2 つで固定した（AC6 を改めた）。
- `drop-shared` の前の確かめは DB の OWNER と CONNECT だけにした（中の物の OWNER まで見ると、移行の間に共有の利用者が作った物で止まり、それを移すはずの drop-shared が流れない。実測で見つけた）。
- 採番: IADR-0695（develop の最大 0693・開いている MSP#2493 が 0694）。MSP#2493 が先にマージされるまで `check-adr-numbering.js` は 0694 の欠番で落ちる。
- 実行の結果は PR の本文に書く。

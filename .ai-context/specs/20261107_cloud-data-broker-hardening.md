---
title: 作業仕様書 — クラウドの Postgres の超利用者の TCP の名乗りを既定で閉じ、ブローカの平文・管理の口を閉じ、資格の作り直しを消費側へ届ける
type: spec
status: done
related_ids: [NFR-18, NFR-05, IADR-0706, IADR-0674, IADR-0689, IADR-0695, IADR-0697, IADR-0700, IADR-0591, IADR-0577]
author: claude
created: 2026-11-07
updated: 2026-11-08
related_specs:
  - 20261105_2401_deploy-cloud-minimal.md
---

# 作業仕様書 — クラウドのデータとブローカの基盤の硬化

> 着手 2026-11-07（develop `cc8f316f1`）。判断の記録は IADR-0706。稼働中のクラスタには何もしない。秘密の値は扱わない
> （作り直しは、オーナーが手元から流すスクリプトで行う）。

## 起点

- 利用者の裁定（2026-11-07）「全部直してから稼働」。クラウドの最小構成（`deploy/cloud/`）の基盤（Postgres・RabbitMQ・OpenBao の値）について、
  次の 4 点を硬くする（どれも重大度は低い）。
  1. Postgres の超利用者が、届く Pod からネットワーク越し（TLS）に名乗れる。
  2. RabbitMQ の Pod が ServiceAccount のトークンを持つ。
  3. RabbitMQ に平文の受け口（5672）と管理の受け口（15672）が残り、既定のユーザーが消えずに残る経路がある。
  4. OpenBao で値を作り直しても、Keycloak のクライアントの secret・Keycloak の DB の利用者・Postgres の超利用者には届かない。

## 現状（着手前に develop で確かめたもの）

| 項目 | develop の状態 | 根拠 |
| --- | --- | --- |
| 2. ブローカの ServiceAccount のトークン | **既に解消**（#2496） | `deploy/cloud/infra/rabbitmq.yaml` の Pod の `automountServiceAccountToken: false`。`scripts/deploy-cloud.test.js` の `podHardeningProblems` が全 Pod を固定し、変異（この行を消す）で赤になる |
| 1. 超利用者の TCP | 残る | `infra/postgres.yaml` の pg_hba は `hostssl all all` だけ。超利用者で TCP から入るのは、バックアップの CronJob と利用者を作る Job（`db-users/job.yaml`）の 2 つ |
| 3. 平文・管理の口 | 残る | `rabbitmq.yaml` の `listeners.tcp.default = 5672`・containerPort と Service の 5672 / 15672。NetworkPolicy は 5671 だけを通す |
| 3. 既定のユーザー | 残る経路がある | 起動の直後の復元は、サービスごとのユーザーが 1 件でも揃わなければ既定のユーザーを残す |
| 4. 作り直しの反映 | 残る | Keycloak の `--import-realm` は在る realm を取り込み直さない。Postgres の初期化は初回だけ。OpenBao の値を作り直す手段（`seed-secrets.sh`）も、在る値は触らない |

## 母集合（規則 9・10。誤りの側の文字列で走査した）

- 超利用者を TCP で使う宣言: `git grep 'secretKeyRef: { name: postgres,\|name: postgres$' -- deploy/cloud` →
  `infra/postgres.yaml`（Pod 自身の env。ソケットで使う）・`backup/cronjob-postgres.yaml`・`db-users/job.yaml`・ESO の 2 つ（写すだけ）。
  取引ユニット・Keycloak は自分の利用者で入る（超利用者を読まない）。
- ブローカの 5672 / 15672: `git grep -n '15672\|5672'` → クラウドでは `infra/rabbitmq.yaml` だけ。試験の既存の判定（`scripts/*.test.js`）は
  「NetworkPolicy が 5672 を通さない」を見るもので、受け口が閉じても成り立つ。`docs/operations/operations.md` の port-forward の記述はローカルの構成。
- 資格の作り直しの記述: `git grep -n '作り直'` の `deploy/cloud/README.md`・各手順書。値の作り直しの手順は無い（証明書の作り直しだけ）。

## 方針（決定は IADR-0706）

1. **超利用者の TCP は既定で拒む。開けるのはオーナーの手元のスクリプトが Job を流す間だけ**（ゲート）。
   - pg_hba の先頭近くに「超利用者の名前（Secret `postgres` の `username` を読み取り専用で置いたファイル）の TCP は reject」を置き、
     その手前で Pod の中のメモリのファイル（emptyDir）を `include_if_exists` で読む。ファイルがあるときだけ超利用者の TCP（TLS・scram）を受ける。
   - ファイルを置いて読み直させる（`pg_ctl reload`）のは `ast-db-users.sh --live` だけで、Job が終われば（失敗でも）消して読み直させる。
     Pod が作り直されれば必ず閉じる（emptyDir）。
   - バックアップは専用の利用者（`pg_read_all_data` の一員・全 DB の CONNECT だけ。超利用者でない）へ替える。値は OpenBao の中で作り
     （`cloud/platform/postgres-backup`）、ESO が Secret `postgres-backup` へ写す。作るのは Job の新しい段 `platform`。
2. **ブローカの平文の受け口を閉じ（`listeners.tcp = none`）、管理と計測の受け口はループバックだけにする**。Service・containerPort から 5672・15672 を外す。
   NetworkPolicy は既に 5671 だけを通すので、届いている通信は変わらない（段の切り替えは既に済んでいる）。
3. **既定のユーザーの扱いを段にする**。ConfigMap `rabbitmq-default-user` の新しいキー `on-incomplete` を `retain`（既定。従来どおり残し、
   残したことを警告の行で出す）／`delete`（揃わなくても消す）とする。オーナーが確かめてから `delete` へ替える。
4. **作り直しを消費側へ届ける手段と手順を対で持つ**。
   - OpenBao: `seed-secrets.sh rotate <cloud/パス> <キー>`（在るキーだけを Pod の中の乱数で作り直す）。
   - Keycloak のクライアントの secret: `keycloak-clients.sh --live sync-secrets`（取り込み元の値と違うクライアントだけを書き換える。値は出さない）。
   - Keycloak の DB の利用者・バックアップの利用者: `ast-db-users.sh --live platform`（Secret の値へパスワードを合わせる）。
   - 超利用者: `deploy/cloud/db-users/rotate-superuser.sh --live`（Pod の中でソケットから合わせる。前もって確かめ、確かめられなければ何も変えない）。
   - 手順書 `docs/operations/cloud-credential-rotation-runbook.md`。

## 受け入れ基準

- AC1（R2-L-04）: develop で解消済みであることを根拠（ファイル・試験）で示す。コードは変えない。
- AC2（R2-L-03）: 描画の pg_hba は、超利用者の TCP を reject し、その手前にゲートの `include_if_exists` だけを置く。ゲートのファイルは emptyDir の中。
  超利用者の名前のファイルは Secret `postgres` の `username` だけ（`password` を置かない）。バックアップは超利用者を読まない。
- AC3（R2-L-03・実物）: 実物の Postgres で、ゲートが閉じていれば超利用者は TCP（TLS）で入れず、ほかの利用者は入れる。ゲートを開けると入れ、閉じると
  また入れない。バックアップの利用者は全 DB を `pg_dump` でき、globals も取れ、書き込み・超利用者の操作はできない。変異（reject の行を外す）で赤になる。
- AC4（R2-L-03）: `ast-db-users.sh --live` は Job の前にゲートを開け、終われば（失敗でも）閉じる。
- AC4b（R2-L-03・レビューの指摘）: ゲートは開けてから上限（既定 900 秒）を過ぎると Pod の中の閉じ手が閉じる（exec の後も）。開け直した回は前の回の閉じ手が閉じない。
  Pod（コンテナ）の再起動でゲートは消えて閉じる。手順書に、閉じられないときに Pod を作り直す手順と `status` の確かめを置く。
- AC5（R2-L-05）: ブローカの設定は平文を受けず（`listeners.tcp = none`）、管理・計測はループバックだけ。containerPort・Service に 5672・15672 が無い。
  実物のブローカで、5671 だけが Pod の外の宛先で受け、5672 は受けない。
- AC6（R2-L-05）: 既定のユーザーは `on-incomplete: retain` で従来どおり残して警告を出し、`delete` なら揃わなくても消す。キーが無い・知らない値は `retain`。
- AC7（R2-L-07）: 4 つの消費側（クライアントの secret・Keycloak の DB・バックアップ・超利用者）について、作り直しの手段がスクリプトにあり、
  値を出力・引数に載せない。手順書は OpenBao の値の作り直しと消費側への反映を対で書く。
- AC8: 既存の試験・検査が通る（`deploy-cloud.test.js`・`deploy-cloud-postgres.test.js`・`rabbitmq-permissions.test.js`・`openbao-scripts.test.sh`・
  `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・文書の検査）。

## 残余リスク・対象外

- 管理 API の口を Pod の外へ出さないだけで、管理のプラグイン自体は残す（`rabbitmqctl` と port-forward での確かめに使う）。
- 超利用者のソケットの名乗りはパスワードのまま（Pod に入れる者は既にデータの置き場を読める）。
- 稼働中の構成への当て方（順番）は手順書。クラスタへの適用はオーナーが行う。

## 結果（手元の実行。2026-11-07）

| 受け入れ基準 | 確かめたもの |
| --- | --- |
| AC1 | develop の `rabbitmq.yaml` の `automountServiceAccountToken: false`（#2496）と、`deploy-cloud.test.js` の変異（この行を消すと赤）。変えていない |
| AC2 | `deploy-cloud.test.js` の `superuserTcpProblems`（描画）と陰性対照 6 つ |
| AC3 | `deploy-cloud-postgres.test.js`（実物の Postgres 16）: ゲートの開け閉め・reject を外す変異・段 platform・バックアップの利用者のダンプと拒み |
| AC4 | `scripts.repo.test.js`（入口は Job の前に開け、成功・失敗とも閉じて確かめる）。trap を外す変異で閉じないことを手元で確かめた |
| AC4b | `deploy-cloud-postgres.test.js` の「ゲートは上限を過ぎるとその回の閉じ手が閉じ…」「開いたゲートは再起動で消え、閉じた側へ戻る…」と、`deploy-cloud.test.js` の DB2（上限・閉じ手の変異）。閉じ手を外す変異で赤 |
| AC5 | `deploy-cloud.test.js` の `brokerListenerProblems` と変異 5 つ。実物の RabbitMQ 3.13（宣言の digest）で、5671 だけが Pod の外の宛先で受け、15672・15692 はループバック、5672 は受けないことを確かめた |
| AC6 | `rabbitmq-permissions.test.js`（retain・キーなし・知らない値・delete・印なし）。delete の分岐を外す変異で赤 |
| AC7 | `openbao-scripts.test.sh`（実物の OpenBao 2.7.1 で rotate）・`add-clients.test.js`（sync-secrets）・`deploy-cloud-postgres.test.js`（段 platform の作り直し・超利用者の check/apply/4 で止まる） |
| AC8 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（1290 件）・`deploy-cloud.test.js`・文書の検査（trace ブロック・リンク・参照の表記・計画 ID の修飾・必読の予算・知識グラフ・type の値域） |

- 採番: 着手の時は仮の番号で起こし、develop（0701〜0705 が先に入った）を取り込んだ後に最大＋1 の IADR-0706 へ改番した（2026-11-08。ファイル名・索引・本書・コード内コメント・trace ブロック・PR タイトル）。

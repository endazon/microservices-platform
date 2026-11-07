---
title: IADR-0702 クラウドの Postgres の超利用者の TCP は既定で拒み、利用者を作る Job の間だけ Pod の中のゲートで開ける。バックアップは全データを読めるだけの専用の利用者で入る。ブローカは平文と管理の口を Pod の外へ出さず、既定のユーザーの扱いを段にする。OpenBao の値の作り直しを消費側へ反映する手段を持つ
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-05, IADR-0674, IADR-0689, IADR-0695, IADR-0697, IADR-0700, IADR-0591, IADR-0577]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_cloud-data-broker-hardening.md
---

# IADR-0702: クラウドのデータとブローカの基盤の硬化

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「全部直してから稼働」）・claude（方式）

## 起点・関連

- 要求 **NFR-18**（統制）・**NFR-05**（秘匿）。前提: IADR-0674（クラウドの最小構成）・IADR-0689（OpenBao・ESO）・IADR-0695（DB の利用者をサービスごとに分ける Job）・
  IADR-0697（基盤の Pod の最小権限）・IADR-0700（稼働の realm へ足す Job）・IADR-0591（ブローカの TLS の段）・IADR-0577（既定のユーザーの無効化の印）。
- 作業仕様書: `.ai-context/specs/20261107_cloud-data-broker-hardening.md`。

## コンテキストと課題

- Postgres の pg_hba は TLS の TCP をどの利用者にも受けていた。超利用者で TCP から入るのは、バックアップと利用者を作る Job だけである。
- ブローカは平文（5672）と管理（15672）の受け口を Pod の外に持っていた（入りの制限は 5671 だけを通す）。既定のユーザーは、サービスごとのユーザーが
  1 件でも揃わなければ残る。
- OpenBao の値を作り直す手段が無く、作り直しても Keycloak のクライアントの secret（`--import-realm` は在る realm を取り込み直さない）・Keycloak の DB の利用者・
  Postgres の超利用者（初期化は初回だけ）には届かない。
- Pod の ServiceAccount のトークン（ブローカ）は IADR-0697 で外れている（本 IADR は扱わない）。

## 決定

### 決定 1 — 超利用者の TCP は既定で拒み、Job の間だけ Pod の中のゲートで開ける

- pg_hba を「ソケット → ゲートの `include_if_exists` → 超利用者の `reject`（v4・v6）→ ほかの `hostssl`」の順にする。超利用者の名前は、Secret `postgres` の
  `username` だけを読み取り専用で置いたファイルを `@` で読む（描画に名前を載せない。password は置かない）。
- ゲートは Pod の中のメモリ（emptyDir）のファイルで、在る間だけ超利用者の TLS・scram の行を足す。開け閉めは ConfigMap `postgres-hba` の
  `superuser-tcp-gate.sh`（uid 70 が `pg_ctl reload`。Postgres を作り直さない）。呼ぶのはオーナーの手元の `ast-db-users.sh --live` だけで、
  Job の前に開け、終われば（成功・失敗・時間切れ・中断）閉じて、閉じたことを確かめる。Pod が作り直されれば閉じる。
- 採らなかった案: (a) Job を `kubectl exec` のソケット経由にする — Job が読むサービスごとの資格（Secret）を Postgres の Pod へ持ち込むか、手元を通すことになる。
  (b) 超利用者にクライアント証明書を求める — 発行・配布の仕組みが増える割に、届く Pod の範囲は変わらない。

### 決定 2 — バックアップは全データを読めるだけの専用の利用者で入る

- 値は OpenBao の中で作る（`cloud/platform/postgres-backup`。`userpass`）。ESO が Secret `postgres-backup` へ写す（policy `platform-infra-eso` に名指しで足す）。
- DB の中の利用者は Job の新しい段 `platform` が作る: 超利用者でなく、作れず（利用者・DB）、複製できず、RLS を越えられず、一員は `pg_read_all_data` だけ、
  テンプレートでない接続できる全 DB に CONNECT。冪等で、在ればパスワードを OpenBao の値に合わせる。最後に属性・一員・CONNECT を確かめる。
- バックアップの本体（`backup.sh`）は変えない。`pg_read_all_data` は globals の検証子も読めるので、写しの形とリストアの手順は従来どおり。

### 決定 3 — ブローカは平文と管理の口を Pod の外へ出さない

- `listeners.tcp = none`。管理（15672）と計測（15692）の受け口は `127.0.0.1` だけ。containerPort と Service は 5671 だけ。確かめは `kubectl port-forward deploy/rabbitmq`。
- 段を置かない理由: 入りの制限は #2401 から 5671 だけを通しており、平文の口へ届いている通信は無い（段 1 の併存が要るクライアントはクラウドに無い）。

### 決定 4 — 既定のユーザーの「揃わなかったときの扱い」を段にする

- ConfigMap `rabbitmq-default-user` の `on-incomplete`: `retain`（既定。キーが無い・知らない値も）は従来どおり残し、`WARN` の行を出す。`delete` は揃わなくても消す
  （ブローカの操作は `rabbitmqctl`＝Erlang の cookie で行い、既定のユーザーを要らない）。稼働の確かめの後に、リポジトリの値を `delete` へ替える（手順書 §4）。

### 決定 5 — OpenBao の値の作り直しを消費側へ反映する手段を持つ

- `seed-secrets.sh rotate <cloud/パス> <キー>`: seed が作る値だけ（userpass は password だけ）の在るキーを Pod の中の乱数で作り直す。無いキーは作らない。
- Keycloak のクライアントの secret: `add-clients.js` の `MODE=sync-secrets`（`keycloak-clients.sh --live sync-secrets`）。取り込み元と違う secret だけを、
  読んだ表現に secret を差して書く（ほかの設定は変えない）。無いクライアントは作らない（`apply` の役目）。値は比べるだけで出さない。
- Keycloak の DB の利用者・バックアップの利用者: Job の段 `platform`（決定 2）が Secret の値へパスワードを合わせる。
- 超利用者: `rotate-superuser.sh --live check|apply`。Pod の中（ソケット）で、Secret の今の値（Pod に置いたファイル。0444＝同じコンテナの利用者は既に env に同じ値を持つ）で
  入れれば何もしない。Pod の env（起動の時の値）でだけ入れれば合わせる。どちらでも入れなければ何も変えずに 4 で止まる（戻し方は手順書）。
- 手順書 `docs/operations/cloud-credential-rotation-runbook.md` に、値の作り直しと反映を対で書く。

## 結果

- 試験: `deploy-cloud-postgres.test.js`（実物の Postgres で、ゲートの開け閉め・reject の変異・段 platform・バックアップの利用者のダンプと拒み・超利用者の作り直し）、
  `deploy-cloud.test.js`（描画の判定と変異。ブローカの口・pg_hba の順・名前のファイル・ゲートの行・バックアップの資格）、`rabbitmq-permissions.test.js`（既定のユーザーの段）、
  `add-clients.test.js`（sync-secrets）、`openbao-scripts.test.sh`（実物の OpenBao で rotate）、`scripts.repo.test.js`（入口がゲートを開けて閉じる）。

## 残る制約

1. 超利用者のソケットの名乗りはパスワードのまま。Pod に入れる者は既にデータの置き場を読める。
2. Erlang の分散の口（4369・25672）は Pod の外で受ける（cookie で守る。入りの制限が止める）。本 IADR は扱わない。
3. 超利用者の作り直しの後、Postgres を作り直すまで Pod の env は古い値のまま（次の作り直しは作り直しの後に）。
4. 取引ユニットの側の反映（そのクライアント・利用者を使うサービスの作り直し）は取引ユニットの手順書が持つ。

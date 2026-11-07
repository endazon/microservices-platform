---
title: IADR-0695 クラウドの取引ユニットの DB の利用者をサービスごとに分け、各利用者が自分の DB だけを OWNER で持ち、他の DB へは CONNECT できないようにする。利用者は OpenBao の中で作った値で Job が作り、稼働中の構成は共有の利用者を残す段を経て移す
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-05, IADR-0689, IADR-0674, IADR-0693]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_cloud-db-per-service-users.md
---

# IADR-0695: クラウドの DB の利用者をサービスごとに分ける

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」）・claude（方式）

## 起点・関連

- 要求 **NFR-18**（統制）・**NFR-05**（秘匿は OpenBao）。
- 前提: IADR-0689（OpenBao・ESO・値の置き場と読み手の境界。決定 6・決定 7）、IADR-0674（クラウドの最小構成）、IADR-0693（稼働中の構成を段で移す型）。
  ブローカは既にサービスごとのユーザー（取引ユニットの IADR-0568）で、本件はその DB 版。
- 作業仕様書: `.ai-context/specs/20261107_cloud-db-per-service-users.md`（現状・母集合・実測）。

## コンテキストと課題

- クラウドの構成では、取引ユニットの 7 つの DB（audit_svc 〜 risk_management_svc）を 1 人の利用者が OWNER で持つ。7 つのサービスが同じ資格で接続する。
  1 つのサービスの資格が漏れると、他のサービスの DB（監査の記録を含む）まで読み書き・スキーマの変更ができる。DB の CONNECT も既定（PUBLIC）のままで、
  どの利用者もどの DB にも接続を試みられる。
- 取引ユニットの 7 つのサービスは、起動の時に同じ接続で EF Core のマイグレーションを流す（DDL を要る）。
- 稼働中の構成は止めずに移したい。Postgres の Deployment は Recreate で、Pod の宣言を変えると作り直される。

## 決定

### 決定 1 — 値はサービスごとに `cloud/shared/ast-db-<サービス>`（7 つ）。`seed-secrets.sh` の既定の seed が OpenBao の中で作る

- `username` は `ast_<サービス>_<乱数 6 桁>`（サービスの `-` は `_`）、`password` は 24 バイトの乱数（hex）。在れば触らない（既存の 7 種と同じ）。
- 置き場は `cloud/ast/` ではなく `cloud/shared/`: Postgres の側（基盤の ESO。policy `platform-infra-eso`）がパスワードを読んで利用者を作るから（IADR-0689 決定 6 の境界）。
- `gen` は使わない（1 つのキーの base64 しか作れず、利用者名とパスワードの組を作れない）。既定の seed を流し直せば、在る値を変えずに 7 組だけが増える。
- 基盤の ESO が Secret `platform-infra/ast-db-<サービス>` へ、取引ユニットの ESO が取引ユニットの名前空間の Secret へ写す（Retain）。

### 決定 2 — 利用者の作成・OWNER の移し替え・CONNECT の剥奪は、ConfigMap の 1 つの手順を Job が流す（initdb の手順は変えない）

- 手順は ConfigMap `postgres-ast-db-users`（`deploy/cloud/infra/postgres-ast-db-users.yaml`）。Job（`deploy/cloud/db-users/job.yaml`。kustomize に入れない）を
  `deploy/cloud/db-users/ast-db-users.sh --live <段>` が作り、待ち、ログを出し、成功したら消す。新しく作る構成も同じ Job を流す（README §3）。
- Job は超利用者で TLS（verify-full。CA は ConfigMap `platform-infra-ca`）で接続する。到達は Postgres の 5432 だけ（別の NetworkPolicy）。
- 値は Job の env（Secret の `secretKeyRef`）から psql の `\getenv` で読み、SQL・引数・ConfigMap に書かない。パスワードを含む文がサーバのログに出ないよう、
  セッションで `log_statement`・`log_min_error_statement`・`log_min_duration_statement` を止め、psql は `VERBOSITY terse`（失敗した文の字面を返さない）で走らせる。
  出力にはサービスの名前と真偽だけを出す。
- 手順（冪等。実物の Postgres で確かめた）:
  1. 7 人を作る（在ればパスワードを OpenBao の値に合わせる）。`LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS`。
  2. 各 DB の OWNER をそのサービスの利用者へ移す（1 つのトランザクションで、移行の間の役割の付与・CONNECT の剥奪と一緒に）。
  3. 接続を許す全 DB（template1・postgres・keycloak を含む）の `CONNECT`・`TEMPORARY` を PUBLIC から外す。各 DB の OWNER（Keycloak の利用者を含む）は自分の DB に入れる。
  4. DB ごとに `REASSIGN OWNED BY <共有の利用者> TO <サービスの利用者>`（表・連番・スキーマ・型）。DB の OWNER を先に移すのは、DB が共有の対象で、
     共有の利用者が DB を持ったまま REASSIGN すると、その間は 7 つの DB が 1 人へ移るから。
  5. 確かめ: 各サービスの利用者が自分の DB と中の物だけの OWNER で、自分の DB にだけ CONNECT できること。外れれば Job を失敗にする。
- **initdb（`01-create-databases.sh`）を変えない理由**: 変えると Postgres の Deployment の env が変わり、稼働中の Postgres が作り直される（Recreate）。
  initdb の env に入れても稼働中の構成には効かない（initdb は空のボリュームでしか走らない）ので、手順は 2 か所に分かれる。Job の 1 か所に寄せた。
- 採らなかった: `kubectl exec` で Postgres の Pod の中で流す — パスワードを Pod の中で読む口が無い（Secret を env・ボリュームで持たない）。持たせると Pod が作り直される。
  手元で Secret を読んでパイプで流す — 値が手元を通る（IADR-0689 決定 7 は値を手元へ返さない）。

### 決定 3 — 読み手の境界は ExternalSecret の単位で十分とし、OpenBao の policy は 7 つを名指しで足す

- `platform-infra-eso` と `ast-eso` に `cloud/data/shared/ast-db-<サービス>` を 7 つ名指しで足す（ワイルドカードにしない。MSP の側は読めない。試験で固定）。
- 取引ユニットの名前空間の SecretStore は 1 つ（role `ast-eso`）なので、OpenBao の側でサービスごとに読み手を分けることはできない。分けるには、名前空間に
  7 つの ServiceAccount・role・SecretStore が要る。分けても、写した Secret は同じ名前空間に並び、OpenBao を読むのは ESO の controller だけである。
  サービスの Pod は自分の Secret だけを `secretKeyRef` で読み、API への資格（ServiceAccount のトークン）を持たない。漏れの単位は Pod の資格で決まるので、
  ExternalSecret の単位（サービスごとの Secret）で十分とした。

### 決定 4 — 稼働中の構成は 3 段で移す。共有の利用者は最終の段で消し、宣言から外すのはその後

- 段 ①（MSP）: `apply-policies` → `seed-secrets.sh` → `kubectl apply -k deploy/cloud/vault/eso` → `kubectl apply -k deploy/cloud`（ConfigMap・NetworkPolicy）→ `ast-db-users.sh --live apply`。
  移行の間、共有の利用者を 7 つの利用者の役割の一員にする（旧の Pod は共有の資格のまま読み書き・マイグレーションを続けられる）。
- 段 ②（取引ユニット）: chart で各サービスが自分の Secret を読む（取引ユニットのリポジトリ）。Pod は 1 つずつ入れ替わる。
- 段 ③（最終）: `ast-db-users.sh --live drop-shared`。共有の利用者の接続が残っていれば止める（新しい接続を止めて数え直し、残れば戻す）。無ければ、移行の間に
  共有の利用者が作った物をサービスの利用者へ移し、役割を外し、消す。
- 宣言（seed・ESO・policy・inventory の `ast-db`、取引ユニットの `ast-db-credentials`）を外すのは段 ③ の後の別の PR。外すと、新しく作る構成でも共有の
  利用者が initdb で作られなくなる（`01-create-databases.sh` も同じ PR で 7 人に寄せる）。
- 手順書は `docs/operations/cloud-db-per-service-users-runbook.md`。

### 決定 5 — 監査の DB を追記だけ（INSERT・SELECT）の利用者にするのは、本件では入れず別の作業に残す

- 調べたこと: 監査のサービスのアプリの書き込みは追記（EF の Add と SaveChanges）と照会だけで、UPDATE・DELETE を出さない。追記だけの権限はアプリとしては足りる。
- 両立しない点: 起動の時のマイグレーションが同じ接続で流れる。Npgsql の EF Core（10.0.2）は履歴の表に `CREATE TABLE IF NOT EXISTS` と
  `LOCK TABLE … IN ACCESS EXCLUSIVE MODE` を使う。実物の Postgres で、INSERT・SELECT だけの利用者はどちらも `permission denied` で拒まれた。
- 案の比較:

  | 案 | 守り | 要るもの | 判断 |
  | --- | --- | --- | --- |
  | A. マイグレーション用の OWNER とアプリ用の追記だけの利用者に分ける（マイグレーションは initContainer か Job で OWNER の資格で流し、アプリの容器には追記だけの資格だけを渡す） | アプリの資格が漏れても、監査の記録を書き換え・消せない | 取引ユニットのコード（マイグレーションだけを流して終わる口・アプリの起動でマイグレーションを流さない切り替え）、chart（initContainer と 2 組の Secret）、基盤（8 人目・表と連番の権限・既定の権限〔`ALTER DEFAULT PRIVILEGES`〕） | 採る方向。ただし 3 つに跨るので別の作業 |
  | B. OWNER のまま UPDATE・DELETE を REVOKE する | 無い（OWNER は自分で権限を戻せる） | — | 採らない |
  | C. 超利用者が持つトリガで UPDATE・DELETE を拒む | 弱い（表の OWNER はトリガを外せる・`ALTER TABLE … DISABLE TRIGGER`） | 基盤の手順 | 採らない |
  | D. アプリの資格に OWNER と追記だけの 2 組を渡し、マイグレーションだけ OWNER で流す | 無い（同じ容器が OWNER の資格を持つ） | 取引ユニットのコード | 採らない |

- 本件（サービスごとの OWNER）で、監査の DB を書き換えられるのは監査のサービスの資格だけになった（他のサービスの資格からは入れない）。案 A は、その資格からも
  書き換えられないようにする次の段である。

## 結果

- 良い影響: 1 つのサービスの資格が漏れても、他のサービスの DB と監査の記録には入れない（CONNECT で拒まれる）。どの利用者も Keycloak・postgres・template1 の DB に入れない。
- 悪い影響: 資格が 7 組増える（OpenBao・ESO・inventory）。DB を持つサービスを足すときは、手順の `SERVICES`・ESO・policy・Job の env・seed・取引ユニットの values-cloud を
  対で足す（`scripts/deploy-cloud.test.js` が 7 つの集合の一致を固定する）。移行の間（段 ①〜③）は共有の利用者が全 DB に入れる（従来と同じ）。

## 残余

1. 段 ① の REASSIGN は表ごとに ACCESS EXCLUSIVE のロックを取る（OWNER の変更）。長いトランザクションがあれば待つ（`lock_timeout` 30 秒で Job が失敗する。冪等なので流し直す）。
   取引の時間帯（JST 22:30〜05:00）の外で流す（手順書）。
2. 監査の DB の追記だけの権限（決定 5 の案 A）は別の作業。
3. クラスタでしか確かめられないこと（移行の門の稼働の確かめ）: ESO が 7 つの Secret を書くこと・Job が TLS で入れること・取引ユニットの Pod が自分の資格で起動すること。

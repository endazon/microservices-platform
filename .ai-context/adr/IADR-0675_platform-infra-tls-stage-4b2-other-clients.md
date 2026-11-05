---
title: IADR-0675 基盤との通信を TLS にする 段 4b-2 の MSP の分（compose の取引ユニットの 3 サービス・Keycloak の DB・Wiki.js・バックアップの CronJob も CA と名前を検証して繋ぐ。helm の Wiki.js は値で opt-in。AST の chart は AST リポジトリ）
type: impl-adr
status: Accepted
related_ids: [NFR-16, NFR-18, ADR-0005, ADR-0023, IADR-0591, IADR-0471]
author: claude
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-16 通信暗号化）
  - planning:projects/microservices-platform/07_adr/ADR-0005（サービス間通信の暗号化を徹底する）
  - planning:projects/microservices-platform/07_adr/ADR-0023（証明書の配布層は cert-manager）
related_specs:
  - ../specs/20261105_2403_infra-tls-stage-4b2.md
---

# IADR-0675: 基盤との通信を TLS にする 段 4b-2 の MSP の分（#2403）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-05
- 決定者: claude（形の選択。多層防御の強化の一環）

## 起点・関連

- 起点 issue: #2403。計画: **NFR-16**・**ADR-0005**・**ADR-0023**。要求 **NFR-18**（同じ強化の一連の変更と同じ起点）。
- 前提: [IADR-0591](./IADR-0591_platform-infra-tls-stage1.md)（段 1。postgres・rabbitmq は平文と TLS を併存させ、基盤の CA を MSP と AST の名前空間へ ConfigMap `platform-infra-ca` として写す）。本 IADR は同 IADR の段の表の **4b-2** のうち MSP リポジトリにある部分である。[IADR-0471](./IADR-0471_platform-infra-encrypted-daily-backup.md)（バックアップの CronJob）。
- 作業仕様書: `.ai-context/specs/20261105_2403_infra-tls-stage-4b2.md`。

## コンテキストと課題

段 1 の後も、基盤の DB・ブローカへ平文で繋ぐクライアントが MSP リポジトリに残っている。compose の取引ユニットの 3 サービス、compose の Keycloak（共有 Postgres を外部 DB に使う）、Wiki.js（compose と helm）、経路B のバックアップの CronJob（pg_dump）である。段 4b-3（平文の受け口を閉じる）の前に、これらを検証つきの TLS へ切り替える必要がある。

制約:

- 検証を緩める形（`sslmode=require`・`rejectUnauthorized: false`・`NODE_TLS_REJECT_UNAUTHORIZED=0`・名前の照合を外す）は使わない。
- 稼働の経路を止めずに段階的に入れられる（値の切り替えか opt-in）。
- 製品ごとに TLS の受け口が違う（Npgsql・pgjdbc・node-postgres・libpq）。各々で CA と名前の両方が検証されることを実測で確かめる。

## 検討した選択肢

1. **Wiki.js へ `DB_SSL_CA`（証明書の本文を env で渡す）** — 本文を env に載せることになり、Pod の定義に証明書の中身が出る。ConfigMap の値を env へ写す形も、Wiki.js がヘッダなしの base64 の本文を要求するので CA の ConfigMap をそのまま使えない。採らない。
2. **Wiki.js へ `DB_SSL=true` ＋ `NODE_EXTRA_CA_CERTS`（採用）** — Wiki.js 2.5 は `DB_SSL=true` で `ssl: { rejectUnauthorized: true }` を pg へ渡す（イメージの `server/core/db.js` で確認）。Node は既定の信頼ストアを残したまま CA を足し、`DB_HOST` の名前を照合する。
3. **バックアップの CA を ConfigMap `platform-infra-ca` として platform-infra にも写す** — 起動器の写しの対象を増やすことになり、バックアップの kustomization の当て順とも結ぶ。同じ名前空間には cert-manager の Secret `postgres-tls`（キー `ca.crt` を持つ。発行元の CA＝基盤のルート CA）が段 1 から在る（postgres が非 optional でマウントしている）。採らない。
4. **バックアップは Secret `postgres-tls` からキー `ca.crt` だけを写す（採用）** — 新しい配布物が要らない。`items` で `ca.crt` だけを写すので、鍵（`tls.key`）はコンテナに現れない。
5. **helm の Wiki.js を `global.db.tls.enabled`（既定 true）に従わせる** — 本番像の配備で、値を変えないまま Wiki.js が CA の ConfigMap を要求するようになる（無ければ Pod が立たず、Recreate なので Wiki.js が止まる）。採らない。

## 決定

1. **compose の取引ユニットの 3 サービスも検証つきで繋ぐ。** DB の個別の接続文字列を `SSL Mode=VerifyFull;Root Certificate=/etc/platform-infra-ca/ca.crt` で終え、ブローカは `*rabbit-env`（amqps:5671・`SSL_CERT_DIR=/etc/ssl/certs:/etc/platform-infra-ca`）へ寄せ、`x-rabbit-env-ast-plain` を消す。CA を `*infra-ca-volumes` でマウントする。ブローカの名前の照合（IADR-0591 決定 5 に相当する呼び出し）は AST リポジトリが入れる。それまでは連鎖の検証だけが効く（名前の照合は AST の変更で揃う）。
2. **compose の Keycloak の DB は pgjdbc の `sslmode=verify-full`。** `KC_DB_URL_PROPERTIES=?sslmode=verify-full&sslrootcert=/etc/platform-infra-ca/ca.crt` を足し、CA を volumes へ足す。経路B の Keycloak は start-dev の H2 で postgres へ繋がず、helm の chart は Keycloak を描かないので、対象は compose だけである。
3. **Wiki.js は `DB_SSL=true` ＋ `NODE_EXTRA_CA_CERTS=/etc/platform-infra-ca/ca.crt`。** compose は常に有効。helm は `wikijs.db.tls.enabled`（**既定 false＝opt-in**）で描き分け、有効のときだけ `global.infraCa` の ConfigMap（キー `caKey` の 1 つ）を `/etc/platform-infra-ca` へ非 optional でマウントする。経路B（`values-local.yaml`）は有効にする。起動器は CA の ConfigMap を helm の前に写し、写せなければ止まる（IADR-0591 決定 6）ので、CA の無いまま描かれることは無い。
4. **バックアップの CronJob は libpq の `PGSSLMODE=verify-full` ＋ `PGSSLROOTCERT=/etc/platform-infra-ca/ca.crt`。** CA は同じ名前空間の Secret `postgres-tls` からキー `ca.crt` だけを非 optional で写す（選択肢 4）。`PGHOST=postgres` は証明書の名前（素の名前）に入っている。
5. **稼働の効きを統合スタックの門で測る。** 基盤の TLS の門（IADR-0591 決定 9）に、Wiki.js（利用者 kp・DB `wikijs`）の接続がすべて TLS で 1 本以上あることを足す。Keycloak（経路B は H2）・バックアップ（統合スタックでは走らない）・取引ユニット（統合スタックに無い）は門では測らない。

## 段の表（IADR-0591 の 4b-2 の内訳）

| 内訳 | 状態 |
| --- | --- |
| compose の取引ユニットの 3 サービス | 本 IADR（決定 1） |
| Keycloak の DB | 本 IADR（決定 2。compose だけ） |
| Wiki.js | 本 IADR（決定 3。helm は opt-in、経路B は有効） |
| バックアップの CronJob | 本 IADR（決定 4） |
| AST の chart の接続・AST の Wolverine の名前の照合 | 後続（AST リポジトリ） |

4b-3（平文を閉じる）の前提は「4b-2 の切り替えを門で確かめた後」のままである。AST の chart の切り替えが済むまで 4b-3 には進めない。

## 実測（2026-11-05）

手元の docker で compose の `infra-tls-init`・postgres（段 1 と同じ digest）を起こし、各製品を compose と同じ digest のイメージで繋いだ（`pg_stat_ssl` で観測）:

- Wiki.js 2.5（pg 8.16.3・Node 24）: `DB_SSL=true` ＋ `NODE_EXTRA_CA_CERTS` → 接続・`TLSv1.3`。CA を足さない → `UNABLE_TO_VERIFY_LEAF_SIGNATURE` で拒否。別の名前 → `ERR_TLS_CERT_ALTNAME_INVALID` で拒否。
- Keycloak 26.7.4（pgjdbc）: `KC_DB_URL_PROPERTIES=?sslmode=verify-full&sslrootcert=…` → 起動・`TLSv1.3` の接続 3 本。別の名前 → `PgjdbcHostnameVerifier` で拒否。`sslrootcert` 無し → ルート証明書のファイルが無いとして拒否（既定の置き場へ倒れない）。
- pg_dump / psql 16（libpq）: `PGSSLMODE=verify-full` ＋ `PGSSLROOTCERT` → `pg_dump --schema-only` 成功・`TLSv1.3`。別の名前 → 名前の不一致で拒否。`PGSSLROOTCERT` 無し → ルート証明書が無いとして拒否。
- 統合スタック（k3d・`ISTIO=1`）の門の結果は PR の報告に記す。

## 結果

- 良い影響: MSP リポジトリにある基盤の DB・ブローカのクライアントは、AST の chart を除いてすべて検証つきの TLS になる。4b-3 の前に残るのは AST の chart だけになる。
- 悪い影響・トレードオフ:
  - 本番像の helm の Wiki.js は値を有効にするまで平文のまま（opt-in。既定を true にするのは CA の供給元を決めるとき＝IADR-0591 の未決と同じ時）。
  - compose の取引ユニットのブローカの接続は、AST の変更が入るまで名前の照合が効かない（連鎖の検証は効く）。
  - 経路B の再実行で Wiki.js（Recreate）が 1 回作り直される。
  - バックアップの CronJob は Secret `postgres-tls` を名指しするので、証明書の Secret 名を変えるときは CronJob も追随する（試験が突き合わせる）。
- フォローアップ: AST の chart の切り替え（AST リポジトリ）。その後に 4b-3。
